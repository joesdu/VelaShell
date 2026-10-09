// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §6.2    压缩:对**载荷**压缩,每个方向一个独立的、跨报文保留的 zlib 流
//   RFC 1950/1951    zlib 与 deflate 的容器与块格式(flush 语义出自这里)
//   OpenSSH PROTOCOL zlib@openssh.com —— 认证成功之后才开始压缩
//   行为规格:        velashell-docs/zh/ssh/spec/01-transport-framing.md §压缩

using System.Buffers;
using System.IO.Compression;
using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Ssh.Crypto;

/// <summary>zlib 压缩（RFC 4253 §6.2）。</summary>
/// <remarks>
/// <para>
/// 每个报文之后做一次 flush：把当前报文的数据全部吐出来，但<b>保留字典</b>。
/// 不 flush 的话数据会留在压缩器里出不来，对端收不到完整报文；
/// 而重置字典（<c>Z_FULL_FLUSH</c>）会把压缩率一起扔掉。
/// </para>
/// <para>
/// .NET 11 的 <see cref="ZLibEncoder"/> 和 <see cref="ZLibDecoder"/> 是运行时自带的原生
/// zlib 的 span API。它们保留跨调用的字典，同时不需要 <see cref="Stream"/> 适配器，
/// 所以压缩结果直接写入 SSH 的 <see cref="IBufferWriter{T}"/>。
/// </para>
/// <para>
/// ⚠️ <b>压缩器的生命周期绑在密钥上，中途不能 Dispose。</b>
/// Dispose 会给 zlib 流收尾（写出结束标记），而那个标记不是给对端的报文数据。
/// 重新协商密钥时要整个换掉（见 <c>velashell-docs/zh/ssh/spec/01-transport-framing.md</c> §六）。
/// </para>
/// </remarks>
internal sealed class ZlibCompressor : ISshCompressor
{
    /// <summary>单次向调用方 writer 请求的输出大小。</summary>
    private const int OutputChunkSize = 16 * 1024;
    private const int LargeOutputChunkSize = 64 * 1024;

    private readonly int _level;
    private ZLibEncoder? _encoder;
    private ZLibDecoder? _decoder;
    private bool _disposed;

    /// <summary>建一个 zlib 压缩器。</summary>
    /// <param name="level">压缩级别，1–9。OpenSSH 用 6。</param>
    public ZlibCompressor(int level = 6)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 9);
        _level = level;
    }

    /// <inheritdoc />
    public bool IsActive => true;

    /// <inheritdoc />
    public void Compress(ReadOnlySpan<byte> payload, IBufferWriter<byte> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(output);

        _encoder ??= new ZLibEncoder(_level);

        // isFinalBlock:false 保留跨报文的 zlib 字典。DestinationTooSmall 时，
        // 编码器报告已经消耗的前缀，下一轮继续喂剩余输入即可。
        int consumedTotal = 0;
        bool called = false;
        while (!called || consumedTotal < payload.Length)
        {
            called = true;
            Span<byte> destination = output.GetSpan(OutputChunkSize);
            OperationStatus status = _encoder.Compress(payload[consumedTotal..], destination, out int consumed, out int written, isFinalBlock: false);

            if ((uint)written > (uint)destination.Length || (uint)consumed > (uint)(payload.Length - consumedTotal))
            {
                throw new SshProtocolException(SshPhase.Open, "zlib 编码器返回了无效的字节计数。");
            }

            output.Advance(written);
            consumedTotal += consumed;

            if (status == OperationStatus.InvalidData || (status == OperationStatus.DestinationTooSmall && consumed == 0 && written == 0))
            {
                throw new SshProtocolException(SshPhase.Open, "zlib 压缩失败。");
            }

            if (status != OperationStatus.DestinationTooSmall)
            {
                if (status != OperationStatus.Done || consumedTotal != payload.Length)
                {
                    throw new SshProtocolException(SshPhase.Open, "zlib 压缩未能处理完整载荷。");
                }

                break;
            }
        }

        // ZLibEncoder.Flush 等价于同步 flush：输出当前报文的尾部，
        // 但不结束流，也不清掉后续报文要复用的字典。
        while (true)
        {
            Span<byte> destination = output.GetSpan(OutputChunkSize);
            OperationStatus status = _encoder.Flush(destination, out int written);
            if ((uint)written > (uint)destination.Length)
            {
                throw new SshProtocolException(SshPhase.Open, "zlib flush 返回了无效的字节计数。");
            }

            output.Advance(written);
            if (status == OperationStatus.DestinationTooSmall)
            {
                continue;
            }

            if (status != OperationStatus.Done)
            {
                throw new SshProtocolException(SshPhase.Open, "zlib flush 失败。");
            }

            break;
        }
    }

    /// <inheritdoc />
    public void Decompress(ReadOnlySequence<byte> payload, IBufferWriter<byte> output, int maxOutputLength)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfNegative(maxOutputLength);

        _decoder ??= new ZLibDecoder();
        long total = 0;

        foreach (ReadOnlyMemory<byte> segment in payload)
        {
            ReadOnlySpan<byte> source = segment.Span;
            int consumedInSegment = 0;

            while (consumedInSegment < source.Length)
            {
                long remaining = maxOutputLength - total;
                bool directOutput = remaining > 0;

                OperationStatus status;
                int consumed;
                int written;
                try
                {
                    if (directOutput)
                    {
                        int chunkSize = source.Length >= OutputChunkSize ? LargeOutputChunkSize : OutputChunkSize;
                        int sizeHint = (int)Math.Min(chunkSize, remaining);
                        Span<byte> destination = output.GetSpan(sizeHint)[..sizeHint];
                        status = _decoder.Decompress(source[consumedInSegment..], destination, out consumed, out written);
                    }
                    else
                    {
                        // 允许解码器把没有输出的 flush 尾部消费掉；如果它产生了
                        // 一个字节，则在写入调用方 writer 之前立刻拦截压缩炸弹。
                        status = DecompressWithLimitProbe(source[consumedInSegment..], out consumed, out written);
                    }

                    if ((uint)consumed > (uint)(source.Length - consumedInSegment) || (uint)written > (uint)(directOutput ? Math.Min(LargeOutputChunkSize, remaining) : 1))
                    {
                        throw new SshProtocolException(SshPhase.Open, "zlib 解码器返回了无效的字节计数。");
                    }

                    if (written != 0)
                    {
                        if (!directOutput || total + written > maxOutputLength)
                        {
                            throw BombLimit(maxOutputLength);
                        }

                        output.Advance(written);
                        total += written;
                    }

                    consumedInSegment += consumed;
                }
                catch (SshProtocolException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException)
                {
                    throw DecompressionFailure(ex);
                }

                if (status == OperationStatus.InvalidData)
                {
                    throw DecompressionFailure();
                }

                if (status == OperationStatus.DestinationTooSmall && consumed == 0 && written == 0)
                {
                    throw BombLimit(maxOutputLength);
                }

                if (consumed == 0 && written == 0 && status != OperationStatus.NeedMoreData)
                {
                    throw DecompressionFailure();
                }
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _encoder?.Dispose();
        _decoder?.Dispose();
    }

    private static SshProtocolException DecompressionFailure(Exception? inner = null) =>
        new(SshPhase.Open, inner is null ? "解压失败。" : $"解压失败：{inner.Message}", inner);

    private static SshProtocolException BombLimit(int maxOutputLength) =>
        new(SshPhase.Open, $"解压后的载荷超过上限 {maxOutputLength} 字节 —— 对端可能在用压缩炸弹撑爆我们的内存。");

    private OperationStatus DecompressWithLimitProbe(ReadOnlySpan<byte> source, out int consumed, out int written)
    {
        Span<byte> destination = stackalloc byte[1];
        return _decoder!.Decompress(source, destination, out consumed, out written);
    }
}
