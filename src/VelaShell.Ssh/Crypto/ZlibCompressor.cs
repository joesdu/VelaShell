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
/// 每个报文之后 flush，保留 zlib 流。发送端只编码字面量，不使用报文内或跨报文的字符串匹配；
/// 收方在显式启用后仍须接受服务端选择的标准 deflate 编码。
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

        _encoder ??= new ZLibEncoder(new ZLibCompressionOptions
        {
            CompressionLevel = _level,
            CompressionStrategy = ZLibCompressionStrategy.HuffmanOnly,
        });

        // isFinalBlock:false 保留跨报文的 zlib 流。DestinationTooSmall 时，
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
        // 但不结束流。
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
        OperationStatus status = OperationStatus.NeedMoreData;

        foreach (ReadOnlyMemory<byte> segment in payload)
        {
            ReadOnlySpan<byte> source = segment.Span;
            int consumedInSegment = 0;

            while (consumedInSegment < source.Length)
            {
                status = DecompressStep(source[consumedInSegment..], output, maxOutputLength, ref total, out int consumed);
                consumedInSegment += consumed;
            }
        }

        // 输入吃完了，最后一次却是因为输出缓冲用满才停下的：解码器手里可能还压着输出，接着取，直到它不再说「缓冲不够」。
        // OpenSSH 每个报文之后做的是 partial flush，报文末尾只有几个位的空块（不像同步 flush 以字节对齐的 00 00 FF FF 收尾）：
        // 最后一个匹配的编码一读完，整段输入就可能都进了解码器，而那个匹配还没复制完。曾经「输入吃完」就收工 ——
        // 压着的几个字节留到下一个报文开头才吐出来，这一报文短了、下一报文全部错位，整条连接以协议错误断开
        // （X11 转发的 GTK 程序一画大窗口就断）。
        while (status == OperationStatus.DestinationTooSmall)
        {
            status = DecompressStep([], output, maxOutputLength, ref total, out _);
        }
    }

    /// <summary>喂一次解码器：<paramref name="source"/> 可以为空（只取解码器压着的输出）。</summary>
    /// <returns>解码器报的状态；流结束、数据坏了、超过上限都在这里抛。</returns>
    private OperationStatus DecompressStep(
        ReadOnlySpan<byte> source, IBufferWriter<byte> output, int maxOutputLength, ref long total, out int consumed)
    {
        long remaining = maxOutputLength - total;
        bool directOutput = remaining > 0;

        OperationStatus status;
        int written;
        try
        {
            if (directOutput)
            {
                int chunkSize = source.Length >= OutputChunkSize ? LargeOutputChunkSize : OutputChunkSize;
                int sizeHint = (int)Math.Min(chunkSize, remaining);
                Span<byte> destination = output.GetSpan(sizeHint)[..sizeHint];
                status = _decoder!.Decompress(source, destination, out consumed, out written);
            }
            else
            {
                // 允许解码器把没有输出的 flush 尾部消费掉；如果它产生了
                // 一个字节，则在写入调用方 writer 之前立刻拦截压缩炸弹。
                status = DecompressWithLimitProbe(source, out consumed, out written);
            }

            if ((uint)consumed > (uint)source.Length || (uint)written > (uint)(directOutput ? Math.Min(LargeOutputChunkSize, remaining) : 1))
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
        }
        catch (SshProtocolException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            throw DecompressionFailure(ex);
        }

        // 〔velashell-docs/zh/ssh/spec/01 §六〕SSH 的压缩流永远不结束（RFC 4253 §6.2：上下文从一个报文传到下一个，
        // 每个报文末尾只 flush）。解码器报告流结束，说明对端在载荷里写了 zlib 的结束标记 —— 当场断开。
        // 曾经照常收下这一报文：之后的报文解码器一个字节都不肯再收，错到下一帧才报一句看不出缘由的「解压失败」。
        if (status == OperationStatus.Done)
        {
            throw new SshProtocolException(SshPhase.Open, "对端结束了 zlib 流（载荷里出现了流结束标记）—— SSH 的压缩流不会结束。");
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

        return status;
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
