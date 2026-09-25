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
using System.Net.Sockets;

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

    /// <summary>这一端<b>整个</b>结束时被取消：不但不再发，也不再收了。</summary>
    /// <remarks>
    /// 搬运循环据此停下<b>往这一端写</b>的那个方向。SSH 通道收到 <c>CHANNEL_CLOSE</c> 之后
    /// 再往里写已经没有意义，而那个方向这时多半正卡在本机 socket 的读上 —— 本机程序在等响应，
    /// 不会先关；不停下它，socket 与转发名额就一直占着。
    /// 从这一端<b>读</b>的方向不受影响：已经收到的数据照常排空。
    /// 默认永不取消。
    /// </remarks>
    CancellationToken Closed => CancellationToken.None;

    /// <summary>异常收尾：让这一端知道连接是<b>出错</b>断的，而不是正常发完了。</summary>
    /// <remarks>
    /// TCP 是 RST（linger 0 再关），SSH 通道是不先发 EOF 的 <c>CHANNEL_CLOSE</c>。
    /// 拿 <see cref="CompleteSendAsync"/> 顶替它是错的：对面看到一个干净的结尾，
    /// 传到一半的文件就被当成了完整的。
    /// 会与另一个方向上正在进行的读写并发调用；不许抛。默认什么也不做。
    /// </remarks>
    ValueTask AbortAsync() => ValueTask.CompletedTask;
}

/// <summary>一次搬运的统计。</summary>
/// <param name="BytesFromLeft">左端读到、搬给右端的字节数。</param>
/// <param name="BytesFromRight">右端读到、搬给左端的字节数。</param>
/// <param name="Duration">持续时间。</param>
/// <param name="Error">导致收尾的错误（最先出的那个）；正常结束为 <see langword="null"/>。</param>
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
    /// <param name="cancellationToken">取消令牌。取消按出错处理：两端一起中止。</param>
    /// <remarks>
    /// <para>
    /// 一个方向<b>正常</b>读完只关那一个方向（半关闭），另一个方向照常搬。
    /// </para>
    /// <para>
    /// <b>任何一个方向出错（含取消），两端一起中止</b>：先让两端都知道是出错断的
    /// （<see cref="IRelayEndpoint.AbortAsync"/>），再停下另一个方向。
    /// 出错时照正常结束去关（发 EOF / FIN）是错的 —— 对面会把截断的数据当成完整的；
    /// 只停一个方向也是错的 —— 另一个方向会一直挂着，直到对面自己想起来关。
    /// </para>
    /// </remarks>
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
        using RelayAbort abort = new(left, right, cancellationToken);

        // 两个方向**同时**跑。串行搬是死锁的经典写法：
        // 先搬完一个方向再搬另一个，而对面正等着我们读它才肯继续。
        Task<long> leftToRight = PumpAsync(left, right, onBytesFromLeft, abort);
        Task<long> rightToLeft = PumpAsync(right, left, onBytesFromRight, abort);

        try
        {
            await Task.WhenAll(leftToRight, rightToLeft).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 原因记在 abort 里。WhenAll 抛的是排在前面那个任务的异常，
            // 而那往往只是被中止的另一个方向（一个取消），不是真正出错的那一边。
        }

        long fromLeft = leftToRight.IsCompletedSuccessfully ? await leftToRight.ConfigureAwait(false) : 0;
        long fromRight = rightToLeft.IsCompletedSuccessfully ? await rightToLeft.ConfigureAwait(false) : 0;

        return new RelayResult(
            fromLeft, fromRight, TimeSpan.FromMilliseconds(Environment.TickCount64 - start), abort.Failure);
    }

    private static async Task<long> PumpAsync(
        IRelayEndpoint source, IRelayEndpoint destination, Action<int>? onBytes, RelayAbort abort)
    {
        long total = 0;
        bool finished = false;

        // 往 destination 写的这个方向，在 destination 整个结束时停下（见 IRelayEndpoint.Closed）。
        CancellationToken destinationClosed = destination.Closed;
        using CancellationTokenSource? linked = destinationClosed.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(abort.Token, destinationClosed)
            : null;
        CancellationToken cancellationToken = linked?.Token ?? abort.Token;

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

            finished = true;
        }
        catch (OperationCanceledException)
            when (destinationClosed.IsCancellationRequested && !abort.Token.IsCancellationRequested)
        {
            // 目的端整个结束了：这个方向已经没有地方可送，就此收尾 —— 不算出错。
            // 本机那头之后再发什么，套接字关掉时操作系统会回它 RST，与真正的对端关闭一样。
            finished = true;
        }
        catch (Exception ex)
        {
            await abort.AbortAsync(ex).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (finished)
            {
                // 这个方向**正常**到头了 —— 只关这一个方向。
                // **另一个方向仍然在跑**，那正是半关闭的意义。
                try
                {
                    await destination.CompleteSendAsync(abort.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 对面可能已经走了。收尾路径上不抛。
                }
            }

            await source.Input.CompleteAsync().ConfigureAwait(false);
        }

        return total;
    }

    /// <summary>一次搬运的中止开关：两个方向共用，谁先出错谁拉下。</summary>
    private sealed class RelayAbort(IRelayEndpoint left, IRelayEndpoint right, CancellationToken cancellationToken)
        : IDisposable
    {
        private readonly CancellationTokenSource _source =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        private Exception? _failure;
        private int _aborted;

        /// <summary>中止之后（或调用方取消之后）被取消。</summary>
        public CancellationToken Token => _source.Token;

        /// <summary>最先出的那个错；正常结束为 <see langword="null"/>。</summary>
        public Exception? Failure => Volatile.Read(ref _failure);

        /// <summary>记下原因，中止两端，停下另一个方向。只有第一次调用生效。</summary>
        public async ValueTask AbortAsync(Exception reason)
        {
            Interlocked.CompareExchange(ref _failure, reason, null);
            if (Interlocked.Exchange(ref _aborted, 1) != 0)
            {
                return;
            }

            // 先中止两端再停另一个方向：中止本身（RST、关通道）往往就把另一个方向从读里放了出来。
            await AbortQuietlyAsync(left).ConfigureAwait(false);
            await AbortQuietlyAsync(right).ConfigureAwait(false);
            await _source.CancelAsync().ConfigureAwait(false);
        }

        private static async ValueTask AbortQuietlyAsync(IRelayEndpoint endpoint)
        {
            try
            {
                await endpoint.AbortAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 契约说不许抛；万一抛了，也不能让另一端因此漏掉中止。
            }
        }

        public void Dispose() => _source.Dispose();
    }
}

/// <summary>把一条 <see cref="Stream"/>（通常是 TCP）接进搬运循环。</summary>
public sealed class StreamRelayEndpoint : IRelayEndpoint
{
    private readonly Stream _stream;
    private readonly Action? _shutdownSend;
    private readonly Action? _abort;
    private readonly bool _ownsStream;

    /// <summary>包一条流。</summary>
    /// <param name="stream">底层流。</param>
    /// <param name="shutdownSend">
    /// 单向关写端的动作。<b>给不出来就传 <see langword="null"/></b> ——
    /// 那样半关闭会退化成「什么都不做」，对面要等到整条连接关掉才知道我们发完了。
    /// </param>
    /// <param name="ownsStream">释放时是否连底层流一起释放。</param>
    /// <param name="abort">
    /// 异常收尾的动作（见 <see cref="IRelayEndpoint.AbortAsync"/>）。TCP 上用 <see cref="Reset"/>，对面收到 RST。
    /// 给不出来就传 <see langword="null"/>：那样中止退化成释放底层流（<paramref name="ownsStream"/> 时）——
    /// 对面知道连接没了，只是分不出是出错还是正常结束。
    /// </param>
    public StreamRelayEndpoint(Stream stream, Action? shutdownSend = null, bool ownsStream = true, Action? abort = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _shutdownSend = shutdownSend;
        _ownsStream = ownsStream;
        _abort = abort;

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
    public async ValueTask AbortAsync()
    {
        try
        {
            if (_abort is not null)
            {
                _abort();
            }
            else if (_ownsStream)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // 中止路径不抛（契约）。
        }
    }

    /// <summary>异常收尾一个套接字：linger 0 再关 —— 对面收到 RST，而不是一个像正常结束的 FIN。</summary>
    /// <param name="socket">要中止的套接字。</param>
    public static void Reset(Socket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        try
        {
            // Unix 套接字上不一定支持这个选项 —— 不支持也照样关。
            socket.LingerState = new LingerOption(enable: true, seconds: 0);
        }
        catch (Exception)
        {
            // 同上。
        }
        socket.Dispose();
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
