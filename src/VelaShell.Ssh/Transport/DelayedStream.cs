// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/09-dialing.md §1

using System.Diagnostics;
using System.Threading.Channels;

namespace VelaShell.Ssh.Transport;

/// <summary>给一条流套上时延与带宽。</summary>
/// <remarks>
/// <para>
/// <b>时延加在写这一侧，而且是流水线式的</b>：写入按带宽计时之后立刻返回，
/// 数据要等 <c>OneWayLatency</c> 之后才对另一端可读。这与真实链路一致 ——
/// 字节在光纤里飞的那段时间，发送方已经返回、接着发下一段，接收方还没看见。
/// </para>
/// <para>
/// ⚠️ 曾经每一次写都在写锁里整段睡掉 <c>OneWayLatency</c> 再交给下层：
/// 写与写被时延串了起来，链路上任一时刻最多只有一次写在飞，
/// 吞吐被压在「单次写的大小 / 单向时延」—— 那是模拟器自己造出来的瓶颈，
/// 窗口与管线深度的测试量到的一部分其实是它。
/// </para>
/// <para>
/// 在途的数据放在一个有上限的队列里，由一个后台任务按顺序、到点交给下层。
/// 下层写不动（另一端不读）时队列会满，写入方照样会等 —— 背压不因为流水线而消失。
/// </para>
/// <para>
/// 这是一个<b>测试设施</b>，但它放在 <c>src</c> 而不是 <c>tests</c>：
/// 它同时是一个可用的特性（做限速、做故障演练），
/// 而且 <c>ISshTransportDialer</c> 的实现者可以直接拿它包自己的流。
/// </para>
/// </remarks>
internal sealed class DelayedStream : Stream
{
    /// <summary>最多有几次写同时在途。</summary>
    /// <remarks>
    /// 只是内存上限，不是链路特征：带宽受限时在途量本来就被带宽 × 时延限住了；
    /// 这一条管的是「下层写不动」时写入方最多能往前多垫多少。
    /// </remarks>
    private const int MaxWritesInFlight = 1024;

    /// <summary>关流时等在途数据送达的余量（在单向时延之外）。</summary>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(1);

    private readonly Stream _inner;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Channel<InFlight> _inFlight;
    private readonly CancellationTokenSource _abort = new();
    private readonly Task _delivery;
    private long _nextWriteAllowedTicks;
    private Exception? _fault;
    private int _disposed;

    /// <summary>一段在途数据，以及它该到达另一端的时刻。</summary>
    private readonly record struct InFlight(long DeliverAtTicks, byte[] Data);

    /// <summary>给一条流套上链路特征。</summary>
    public DelayedStream(Stream inner, LinkCharacteristics link)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Link = link;
        _nextWriteAllowedTicks = Stopwatch.GetTimestamp();
        _inFlight = Channel.CreateBounded<InFlight>(new BoundedChannelOptions(MaxWritesInFlight)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _delivery = link.IsIdeal ? Task.CompletedTask : Task.Run(DeliverAsync);
    }

    /// <summary>链路特征。</summary>
    public LinkCharacteristics Link { get; }

    /// <inheritdoc />
    public override bool CanRead => _inner.CanRead;

    /// <inheritdoc />
    public override bool CanWrite => _inner.CanWrite;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (Link.IsIdeal)
        {
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            return;
        }

        ThrowIfFaulted();

        // 串起来：带宽是一条链路的共享资源，两个并发的写不能各自独立计时；
        // 而且到达时刻要与入队顺序一致，另一端才不会看到乱序的字节。
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ThrottleAsync(buffer.Length, cancellationToken).ConfigureAwait(false);

            // 调用方一返回就可能复用它的缓冲区，而这段数据还要在「线上」飞一会儿 —— 必须拷一份。
            InFlight item = new(
                Stopwatch.GetTimestamp() + (long)(Link.OneWayLatency.TotalSeconds * Stopwatch.Frequency),
                buffer.ToArray());

            try
            {
                await _inFlight.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                // 队列被关了：要么流已关闭，要么下层出过错。按那个原因报，不报「队列已关闭」。
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                ThrowIfFaulted();
                throw;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>后台送达：按入队顺序，每段到点交给下层。</summary>
    private async Task DeliverAsync()
    {
        CancellationToken abort = _abort.Token;
        ChannelReader<InFlight> reader = _inFlight.Reader;

        try
        {
            while (await reader.WaitToReadAsync(abort).ConfigureAwait(false))
            {
                while (reader.TryRead(out InFlight item))
                {
                    TimeSpan wait = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), item.DeliverAtTicks);
                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait, abort).ConfigureAwait(false);
                    }

                    await _inner.WriteAsync(item.Data, abort).ConfigureAwait(false);

                    // 同一时刻到期的几段一起写完再 flush —— 与真实网卡一样，一个时钟节拍里到的包一次递上去。
                    if (!reader.TryPeek(out InFlight next)
                        || next.DeliverAtTicks > Stopwatch.GetTimestamp())
                    {
                        await _inner.FlushAsync(abort).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (abort.IsCancellationRequested)
        {
            // 关流时放弃还没送到的数据 —— 那相当于链路被切断。
        }
        catch (Exception ex)
        {
            // 下层出错：记下来，后续的写照这个原因失败；排在队里等着的写也立刻放出来。
            Volatile.Write(ref _fault, ex);
            _inFlight.Writer.TryComplete(ex);
        }
    }

    private void ThrowIfFaulted()
    {
        if (Volatile.Read(ref _fault) is { } fault)
        {
            throw new IOException("模拟链路的下层流写入失败，链路已断。", fault);
        }
    }

    /// <summary>按带宽限速。</summary>
    /// <remarks>
    /// 用「下一次允许发送的时刻」而不是「每次都睡固定时间」——
    /// 后者会把调度抖动累加进去，几百次之后模拟出来的带宽会明显偏低。
    /// </remarks>
    private async ValueTask ThrottleAsync(int byteCount, CancellationToken cancellationToken)
    {
        if (Link.BytesPerSecond <= 0)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        long allowedAt = Interlocked.Read(ref _nextWriteAllowedTicks);

        if (allowedAt > now)
        {
            TimeSpan wait = Stopwatch.GetElapsedTime(now, allowedAt);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
            now = Math.Max(now, allowedAt);
        }

        double seconds = (double)byteCount / Link.BytesPerSecond;
        long cost = (long)(seconds * Stopwatch.Frequency);
        Interlocked.Exchange(ref _nextWriteAllowedTicks, now + cost);
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override Task WriteAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    /// <remarks>
    /// <b>不等在途数据送达。</b>字节已经「发出去了」，flush 管的是发送方自己的缓冲；
    /// 等送达就又把时延串回到每一次 flush 上。下层的 flush 由送达任务在交付之后做。
    /// </remarks>
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        if (Link.IsIdeal)
        {
            return _inner.FlushAsync(cancellationToken);
        }

        ThrowIfFaulted();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override void Flush()
    {
        if (Link.IsIdeal)
        {
            _inner.Flush();
            return;
        }

        ThrowIfFaulted();
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("这条流只支持异步读写 —— 同步读会把时延变成线程阻塞。");

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("这条流只支持异步读写。");

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    /// <remarks>
    /// 先让在途的数据照常送达、再关下层 —— 与真实连接一样，FIN 排在已发出的字节后面。
    /// 写返回了就意味着「已发出」，关流时把它们丢掉，另一端就收不到最后那几条消息（比如 DISCONNECT）。
    /// 下层写不动时不会一直等：单向时延加一点余量之后就当链路被切断。
    /// </remarks>
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _inFlight.Writer.TryComplete();
            try
            {
                await _delivery.WaitAsync(Link.OneWayLatency + DrainGrace).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // 下层卡住了 —— 下面切断。
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>同步关流不等送达：在途的数据丢弃，相当于链路被切断。要让它们送到，用 <see cref="DisposeAsync"/>。</remarks>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Volatile.Write(ref _disposed, 1);
            _inFlight.Writer.TryComplete();
            _abort.Cancel();
            _inner.Dispose();

            // ⚠️ **不要 Dispose 写锁。**
            //
            // `SemaphoreSlim.Dispose` 不会唤醒任何正等在 `WaitAsync` 上的人 ——
            // 它们的 Task **永远不会完成**。收尾时有人正排在写锁后面是常态，不是意外
            // （持锁的那个可能正等着在途队列腾位置；队列关掉之后它会出来、放锁，后面的人看到已关闭就退出）。
            // 于是 Dispose 把「关流」变成了「那个写入者挂死到用例超时」。
            //
            // 不 Dispose 没有代价：我们从不取它的 `AvailableWaitHandle`，
            // 没有那个句柄时 SemaphoreSlim 不持有任何非托管资源。_abort 同理（没有计时器、没取句柄）。
        }
        base.Dispose(disposing);
    }
}
