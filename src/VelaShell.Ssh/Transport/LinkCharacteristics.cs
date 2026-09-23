// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/design/architecture.md §10.2、§11.2.1
//
// 这一层存在的**唯一理由**是：自适应窗口与 SFTP 管线深度
// 在一条零延迟、无限带宽的内存链路上没有任何东西可验证 ——
// 无论窗口是 32 KiB 还是 64 MiB,跑出来都一样快。
// 没有链路特征模拟,那两处「自适应」的代码就是在空转。

using System.Diagnostics;

namespace VelaShell.Ssh.Transport;

/// <summary>一条链路长什么样。</summary>
/// <param name="OneWayLatency">单向时延。往返时延（RTT）是它的两倍。</param>
/// <param name="BytesPerSecond">带宽上限；<c>0</c> 表示不限。</param>
/// <remarks>
/// <para>
/// <b>带宽时延积（BDP）= 带宽 × RTT</b> 就是「一条链路在任一瞬间能装多少在途数据」。
/// 窗口小于 BDP 时，发送方会在等确认上空转，吞吐被死死压在 <c>窗口 / RTT</c>：
/// </para>
/// <code>
/// 2 MiB / 200 ms ≈ 10 MB/s      ← 跨洋链路上怎么也跑不过这个数
/// </code>
/// </remarks>
public readonly record struct LinkCharacteristics(TimeSpan OneWayLatency, long BytesPerSecond = 0)
{
    /// <summary>理想链路：零时延、无限带宽。</summary>
    public static LinkCharacteristics Ideal => new(TimeSpan.Zero);

    /// <summary>局域网：0.25 ms 单向，1 Gbps。</summary>
    public static LinkCharacteristics LocalNetwork =>
        new(TimeSpan.FromMilliseconds(0.25), 125_000_000);

    /// <summary>跨洋：100 ms 单向（RTT 200 ms），100 Mbps。</summary>
    /// <remarks>BDP = 12.5 MB/s × 0.2 s = 2.5 MiB —— 正好卡在「固定 2 MiB 窗口」上面。</remarks>
    public static LinkCharacteristics Intercontinental =>
        new(TimeSpan.FromMilliseconds(100), 12_500_000);

    /// <summary>往返时延。</summary>
    public TimeSpan RoundTrip => OneWayLatency * 2;

    /// <summary>带宽时延积（字节）。<c>0</c> 表示带宽不限。</summary>
    public long BandwidthDelayProduct =>
        BytesPerSecond == 0 ? 0 : (long)(BytesPerSecond * RoundTrip.TotalSeconds);

    /// <summary>有没有需要模拟的东西。</summary>
    public bool IsIdeal => OneWayLatency <= TimeSpan.Zero && BytesPerSecond == 0;
}

/// <summary>给一条流套上时延与带宽。</summary>
/// <remarks>
/// <para>
/// <b>时延加在写这一侧</b>：写进来的数据要等 <c>OneWayLatency</c> 之后
/// 才真正可读。这与真实链路一致 —— 字节在光纤里飞的那段时间，
/// 发送方已经返回，接收方还没看见。
/// </para>
/// <para>
/// 这是一个<b>测试设施</b>，但它放在 <c>src</c> 而不是 <c>tests</c>：
/// 它同时是一个可用的特性（做限速、做故障演练），
/// 而且 <c>ISshTransportDialer</c> 的实现者可以直接拿它包自己的流。
/// </para>
/// </remarks>
public sealed class DelayedStream : Stream
{
    private readonly Stream _inner;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private long _nextWriteAllowedTicks;

    /// <summary>给一条流套上链路特征。</summary>
    public DelayedStream(Stream inner, LinkCharacteristics link)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Link = link;
        _nextWriteAllowedTicks = Stopwatch.GetTimestamp();
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
        if (Link.IsIdeal)
        {
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            return;
        }

        // 串起来：带宽是一条链路的共享资源，两个并发的写不能各自独立计时。
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ThrottleAsync(buffer.Length, cancellationToken).ConfigureAwait(false);

            if (Link.OneWayLatency > TimeSpan.Zero)
            {
                // 时延：这段数据要在「线上」飞一会儿才对读者可见。
                await Task.Delay(Link.OneWayLatency, cancellationToken).ConfigureAwait(false);
            }

            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
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
    public override Task FlushAsync(CancellationToken cancellationToken) =>
        _inner.FlushAsync(cancellationToken);

    /// <inheritdoc />
    public override void Flush() => _inner.Flush();

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
    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);

        // ⚠️ **不要 Dispose 这个信号量。**
        //
        // `SemaphoreSlim.Dispose` 不会唤醒任何正等在 `WaitAsync` 上的人 ——
        // 它们的 Task **永远不会完成**。而这条流正是「写要等时延」的地方：
        // 收尾时有人正排在写锁后面是常态，不是意外。
        // 于是 Dispose 把「关流」变成了「那个写入者挂死到用例超时」。
        //
        // 不 Dispose 没有代价：我们从不取它的 `AvailableWaitHandle`，
        // 没有那个句柄时 SemaphoreSlim 不持有任何非托管资源。
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // 同上：不 Dispose 写锁，理由见 DisposeAsync。
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
