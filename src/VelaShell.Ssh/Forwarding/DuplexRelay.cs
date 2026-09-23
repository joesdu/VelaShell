// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §5.3  CHANNEL_EOF / CHANNEL_CLOSE 的半关闭语义
//   行为规格:      velashell-docs/zh/ssh/spec/07-forwarding.md §2.2、§五、§六
//
// 〔决策 §六〕**这一层与 SSH 无关。**它只见到两个端点,各有一读一写。
// 所以半关闭、计量、错误收尾这条主路径不必架一台真服务器就能验证。

using System.Buffers;
using System.IO.Pipelines;

namespace VelaShell.Ssh.Forwarding;

/// <summary>搬运循环的一端。</summary>
/// <remarks>
/// 实现只需要三件事：读、写、以及<b>单向</b>地告诉对面「我不再发了」。
/// 第三件是半关闭的全部内容，也是最容易被漏掉的那一件。
/// </remarks>
public interface IRelayEndpoint : IAsyncDisposable
{
    /// <summary>从这一端读进来的数据。</summary>
    PipeReader Input { get; }

    /// <summary>写到这一端去的数据。</summary>
    PipeWriter Output { get; }

    /// <summary>
    /// 告诉这一端「我不会再发数据了」。
    /// </summary>
    /// <remarks>
    /// TCP 这边是 <c>shutdown(SEND)</c>，SSH 通道那边是 <c>CHANNEL_EOF</c>。
    /// <para>
    /// <b>它不是关闭。</b>做完之后<b>仍然要继续收</b>对面的数据 ——
    /// 把它当成「连接结束」会截断数据：典型症状是 <c>curl</c> 通过隧道 POST 完
    /// 请求体后等响应，而我们在它关写端时把整条通道关了，响应永远收不到。
    /// </para>
    /// </remarks>
    ValueTask CompleteSendAsync(CancellationToken cancellationToken);
}

/// <summary>一次搬运的统计。</summary>
/// <param name="BytesFromLeft">左端读到、搬给右端的字节数。</param>
/// <param name="BytesFromRight">右端读到、搬给左端的字节数。</param>
/// <param name="Duration">持续时间。</param>
/// <param name="Error">导致收尾的错误；正常结束为 <see langword="null"/>。</param>
public readonly record struct RelayResult(
    long BytesFromLeft,
    long BytesFromRight,
    TimeSpan Duration,
    Exception? Error);

/// <summary>双向搬运。</summary>
/// <remarks>
/// 三种转发（<c>-L</c> / <c>-D</c> / <c>-R</c>）都收敛到这一个循环，
/// <b>这是刻意的</b> —— 它们的差别只在「出站怎么建」，搬运本身没有区别。
/// </remarks>
public static class DuplexRelay
{
    /// <summary>每方向的缓冲大小。</summary>
    /// <remarks>
    /// 与 SSH 通道的 max packet 同量级，又不至于让每条连接都占住大块内存：
    /// 1000 条并发连接 × 2 方向 × 32 KiB = 64 MiB，可接受。
    /// </remarks>
    public const int BufferSize = 32 * 1024;

    /// <summary>把两端接起来，双向搬到底。</summary>
    /// <param name="left">一端（通常是本机 socket）。</param>
    /// <param name="right">另一端（通常是 SSH 通道）。</param>
    /// <param name="onBytesFromLeft">左 → 右每搬一块的回调（计量用，可为空）。</param>
    /// <param name="onBytesFromRight">右 → 左每搬一块的回调。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<RelayResult> RunAsync(
        IRelayEndpoint left,
        IRelayEndpoint right,
        Action<int>? onBytesFromLeft = null,
        Action<int>? onBytesFromRight = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        long start = Environment.TickCount64;

        // 两个方向**同时**跑。串行搬是死锁的经典写法：
        // 先搬完一个方向再搬另一个，而对面正等着我们读它才肯继续。
        Task<long> leftToRight = PumpAsync(left, right, onBytesFromLeft, cancellationToken);
        Task<long> rightToLeft = PumpAsync(right, left, onBytesFromRight, cancellationToken);

        Exception? error = null;
        try
        {
            await Task.WhenAll(leftToRight, rightToLeft).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        long fromLeft = leftToRight.IsCompletedSuccessfully ? await leftToRight.ConfigureAwait(false) : 0;
        long fromRight = rightToLeft.IsCompletedSuccessfully ? await rightToLeft.ConfigureAwait(false) : 0;

        return new RelayResult(
            fromLeft, fromRight, TimeSpan.FromMilliseconds(Environment.TickCount64 - start), error);
    }

    private static async Task<long> PumpAsync(
        IRelayEndpoint source, IRelayEndpoint destination, Action<int>? onBytes, CancellationToken cancellationToken)
    {
        long total = 0;

        try
        {
            while (true)
            {
                ReadResult read = await source.Input.ReadAsync(cancellationToken).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = read.Buffer;

                if (!buffer.IsEmpty)
                {
                    foreach (ReadOnlyMemory<byte> segment in buffer)
                    {
                        destination.Output.Write(segment.Span);
                    }

                    int length = (int)buffer.Length;
                    total += length;

                    // 计量在**搬运循环里**累加，不在通道层：
                    // 通道层的字节数含协议开销，而面板上要显示的是应用数据量。
                    onBytes?.Invoke(length);

                    FlushResult flush = await destination.Output.FlushAsync(cancellationToken)
                        .ConfigureAwait(false);

                    if (flush.IsCompleted)
                    {
                        break;   // 对面不要了
                    }
                }

                source.Input.AdvanceTo(buffer.End);

                if (read.IsCompleted)
                {
                    break;
                }
            }
        }
        finally
        {
            // 这个方向到头了 —— 只关这一个方向。
            // **另一个方向仍然在跑**，那正是半关闭的意义。
            try
            {
                await destination.CompleteSendAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 对面可能已经走了。收尾路径上不抛。
            }

            await source.Input.CompleteAsync().ConfigureAwait(false);
        }

        return total;
    }
}

/// <summary>把一条 <see cref="Stream"/>（通常是 TCP）接进搬运循环。</summary>
public sealed class StreamRelayEndpoint : IRelayEndpoint
{
    private readonly Stream _stream;
    private readonly Action? _shutdownSend;
    private readonly bool _ownsStream;

    /// <summary>包一条流。</summary>
    /// <param name="stream">底层流。</param>
    /// <param name="shutdownSend">
    /// 单向关写端的动作。<b>给不出来就传 <see langword="null"/></b> ——
    /// 那样半关闭会退化成「什么都不做」，对面要等到整条连接关掉才知道我们发完了。
    /// </param>
    /// <param name="ownsStream">释放时是否连底层流一起释放。</param>
    public StreamRelayEndpoint(Stream stream, Action? shutdownSend = null, bool ownsStream = true)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _shutdownSend = shutdownSend;
        _ownsStream = ownsStream;

        Input = PipeReader.Create(stream, new StreamPipeReaderOptions(
            bufferSize: DuplexRelay.BufferSize, leaveOpen: true));
        Output = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
    }

    /// <inheritdoc />
    public PipeReader Input { get; }

    /// <inheritdoc />
    public PipeWriter Output { get; }

    /// <inheritdoc />
    public async ValueTask CompleteSendAsync(CancellationToken cancellationToken)
    {
        await Output.FlushAsync(cancellationToken).ConfigureAwait(false);
        await Output.CompleteAsync().ConfigureAwait(false);
        _shutdownSend?.Invoke();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            await Input.CompleteAsync().ConfigureAwait(false);
            await Output.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 释放路径不抛。
        }

        if (_ownsStream)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
