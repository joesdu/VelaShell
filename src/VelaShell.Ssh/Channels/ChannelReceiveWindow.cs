// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §5.2  CHANNEL_WINDOW_ADJUST —— 接收窗口在数据到达时扣减、被消费之后回补
//   行为规格:      velashell-docs/zh/ssh/spec/05-connection.md §3

using System.Diagnostics;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Channels;

/// <summary>一条通道的接收窗口：到达时扣减、消费后攒着回补、按链路情况伸缩，以及发 <c>WINDOW_ADJUST</c> 的回补泵。</summary>
/// <remarks>
/// <para>
/// 从 <see cref="SshChannel"/> 拆出来的协作者，只管「窗口这个数」。数据进不进管道是 <see cref="ChannelDataReceiver"/> 的事，
/// 通道的生命周期（泵什么时候起、什么时候停）仍归通道。
/// </para>
/// <para>
/// ⚠️ <b>扩窗、缩窗动的是两本账</b>：会话的窗口总预算归连接的锁，通道这一笔计了多少、号还了没有归通道的状态锁。
/// 「先向会话申请 → 通道锁里确认 → 失败当场还回去」这三步仍由通道做（<see cref="SshChannel.TryChargeBudget"/> /
/// <see cref="SshChannel.RefundBudget"/>）。这里不持有任何一把锁去调它们，也不持锁入队。
/// </para>
/// </remarks>
internal sealed class ChannelReceiveWindow
{
    private readonly SshChannel _channel;

    /// <summary>接收窗口的伸缩策略。</summary>
    private readonly SshWindowPolicy _policy;

    private readonly SshWindow _window;

    /// <summary>消费者已消费、但还没回补给对端的字节数。跨轮累加，攒够回补阈值时由回补泵整笔取走。</summary>
    private long _consumedPendingAdjust;
    private readonly AsyncGate _consumedGate = new();
    private int _pumpWakeups;

    /// <summary>连续几轮没有把窗口用尽。</summary>
    private int _idleRounds;

    /// <summary>上一轮回补以来，接收窗口有没有被对端吃到见底。</summary>
    /// <remarks>
    /// <b>这才是「窗口是瓶颈」的真信号。</b>
    /// 用「两次回补的间隔很短」当信号是不对的：局域网上间隔本来就短，
    /// 那会让窗口一路涨到上限，白占几十 MiB 内存 —— 而那条链路上
    /// 窗口根本不是瓶颈。
    /// </remarks>
    private volatile bool _windowWasExhausted;

    /// <summary>交给了读的一方、还没被读走的字节数（伸缩判据用，见 <see cref="NoteArrival"/>）。</summary>
    private long _unreadBytes;

    /// <summary>读的一方上一次读空了在等数据，是几次回补之前的事（见 <see cref="NoteArrival"/>）。</summary>
    /// <remarks>接收循环写 0，回补泵每次回补加一。起始值很大：还没收到数据时不算「等过」。</remarks>
    private int _adjustsSinceStarved = int.MaxValue / 2;

    /// <summary>收到过数据了（第一包到来之前读的一方本来就空着，那不算「等」）。</summary>
    private bool _receivedAnyData;

    /// <summary>「空等」判据（见 <see cref="CreditIdleDetector"/>）：接收循环报到达，回补泵每轮判一次。</summary>
    private readonly CreditIdleDetector _creditIdle = new();

    internal ChannelReceiveWindow(SshChannel channel, SshWindowPolicy policy)
    {
        _channel = channel;
        _policy = policy;
        _window = new SshWindow(policy.InitialBytes);
    }

    /// <summary>当前的剩余（诊断用）。</summary>
    public uint Remaining => _window.Remaining;

    /// <summary>当前的额定大小。自适应策略下它会随链路情况变。</summary>
    public int Size => _window.Size;

    /// <summary>回补泵被叫醒过几次（测试用：离阈值还远时不该叫醒它）。</summary>
    public int PumpWakeups => Volatile.Read(ref _pumpWakeups);

    /// <summary>数据到达：扣窗口。</summary>
    /// <returns>窗口够不够。<see langword="false"/> 是对端的协议违规，<b>一个字节都没扣</b>。</returns>
    public bool TryConsume(int length) => _window.TryConsume(length);

    /// <summary>这么多字节交给了读的一方、还没被读走（伸缩判据用）。</summary>
    public void NoteDelivered(int length) => Interlocked.Add(ref _unreadBytes, length);

    /// <summary>读的一方读走了这么多（经 <see cref="WindowedPipeReader"/>）。</summary>
    public void NoteReaderConsumed(long bytes)
    {
        Interlocked.Add(ref _unreadBytes, -bytes);
        NoteConsumed(bytes);
    }

    /// <summary>叫醒回补泵去看结局（通道收尾时用）。</summary>
    public void Wake() => _consumedGate.Signal();

    /// <summary>消费者消费掉了一些字节 —— 该给对端补窗口了。</summary>
    /// <remarks>
    /// 〔决策 velashell-docs/zh/ssh/spec/05 §3.2〕<b>窗口挂在消费上，不挂在接收上。</b>
    /// 消费者不读，窗口就不补，对端自然停下来 —— 背压是结构性的，
    /// 不需要额外的限流器，也不会出现「内部队列无限涨」。
    /// </remarks>
    public void NoteConsumed(long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        // 〔CH-P2〕攒够回补阈值才叫醒回补泵。曾经每消费一次都叫：消费者读得碎（逐行读几十字节）时，
        // 每一次都是新建票、注册令牌、一次线程池调度，而绝大多数都是白醒 —— 离阈值还远。
        // 不会漏醒：阈值只随窗口大小变，而窗口只由回补泵自己改，改完它接着重查。
        if (Interlocked.Add(ref _consumedPendingAdjust, bytes) >= AdjustThreshold)
        {
            _consumedGate.Signal();
        }
    }

    /// <summary>
    /// 攒够这么多已消费的字节再发 <c>WINDOW_ADJUST</c>：半个窗口。
    /// </summary>
    /// <remarks>
    /// 每消费几十字节就发一个是在拿控制报文淹没链路。攒着不发也不会卡住对端：
    /// 本地未消费的数据接近 0 时，对端手上至少还剩半个窗口。
    /// </remarks>
    private long AdjustThreshold => _window.Size / 2;

    /// <summary>回补泵：攒够阈值就回补（顺带伸缩窗口），直到通道的生命周期结束。</summary>
    public async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            // ⚠️ 已消费、但还没告诉对端的字节数必须**跨轮累加**（_consumedPendingAdjust 本身就是那个累加器，
            //    够了阈值才整笔取走）。攒不够阈值就把这一轮的 consumed 丢掉的话，
            //    对端视角的窗口会一点一点缩小，最后归零 —— 症状是传了一阵子之后
            //    通道永久停住，而本地账面上看窗口明明是满的。
            //    消费者每次只读几十字节（逐行读）时最容易撞上。
            while (!cancellationToken.IsCancellationRequested)
            {
                Task ticket = _consumedGate.NextChange();

                if (Volatile.Read(ref _consumedPendingAdjust) >= AdjustThreshold)
                {
                    long unreported = Interlocked.Exchange(ref _consumedPendingAdjust, 0);

                    // 扩窗要**当场把多出来的额度授予对端**。
                    //
                    // 只改本地的 Size 而不多授一点，结果是：阈值（Size/2）涨了，
                    // 而对端手上的窗口没涨 —— 它发不出更多数据，我们也就攒不够
                    // 下一次回补的量。两边一起停住，谁都不动。
                    long extra = ResizeWindowIfNeeded();

                    long grant = unreported + extra;
                    if (grant > 0)
                    {
                        uint delta = (uint)Math.Min(grant, uint.MaxValue);
                        _window.Add(delta);
                        await SendWindowAdjustAsync(delta, cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                await ticket.WaitAsync(cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _pumpWakeups);
            }
        }
        catch (OperationCanceledException)
        {
            // 通道关了，正常收场。
        }
        catch (Exception)
        {
            // 会话没了。窗口回补没有补救动作可做 —— 通道本来也用不下去了。
        }
    }

    /// <summary>数据到达：对端把窗口吃到见底了的话记下来，给伸缩用。</summary>
    /// <remarks>
    /// <para>
    /// 「见底」不要求恰好为 0：剩下不到 1/8 的时候，对端已经在拿「还能发多少」当限制了。
    /// </para>
    /// <para>
    /// ⚠️ <b>但要分清是谁让它见底的。</b>读的一方慢，数据堆在管道里没人读，回补就不发，窗口一样见底 ——
    /// 那时窗口不是瓶颈，扩窗换不来吞吐，只是让这条通道多缓着几十 MiB 没读的数据。
    /// 曾经不分：消费者一慢，窗口就一路翻到上限。
    /// </para>
    /// <para>
    /// 分得清的信号是「读的一方有没有读空了在等」：窗口不够（BDP 大于窗口）时，读得快的一方把数据读空，
    /// 要空等一个往返下一轮才到；读得慢时管道里总有没读完的，永远读不空。
    /// 所以额度见了底、而且最近读空过，才算窗口太小（在 <see cref="ResizeWindowIfNeeded"/> 里合起来判）。
    /// 通道的第一包不算读空 —— 那时本来就空着。
    /// 按比例划一条「未读少于几分之几」的线不行：那条线会被时序偶然跨过，窗口就时涨时落。
    /// </para>
    /// </remarks>
    public void NoteArrival()
    {
        bool drained = _receivedAnyData && Interlocked.Read(ref _unreadBytes) <= 0;
        if (drained)
        {
            Volatile.Write(ref _adjustsSinceStarved, 0);
        }
        _creditIdle.NoteArrival(Stopwatch.GetTimestamp(), drained);
        _receivedAnyData = true;

        if (_window.Remaining <= (uint)(_window.Size / 8))
        {
            _windowWasExhausted = true;
        }
    }

    /// <summary>按链路情况调整窗口。</summary>
    /// <returns>因为扩窗而<b>额外要授予对端</b>的字节数。</returns>
    /// <remarks>
    /// <para>
    /// 固定窗口的问题在于它<b>同时决定了吞吐上限</b>（<c>窗口 / RTT</c>）：
    /// 2 MiB / 200 ms ≈ 10 MB/s，跨洋链路上怎么也跑不过这个数。
    /// 所以窗口要能长到带宽时延积那么大。
    /// </para>
    /// <para>
    /// <b>判据是「窗口有没有吃紧」</b>（被吃到见底，或者读的一方空等额度，见 <see cref="CreditIdleDetector"/>），
    /// 不是「两次回补隔了多久」。后者在局域网上永远成立，会让窗口一路涨到上限白占内存 ——
    /// 而那条链路上窗口根本不是瓶颈。
    /// </para>
    /// <para>
    /// 反向也要有：连续几轮都没吃紧，说明窗口开大了，缩回去。
    /// 缩窗的做法是<b>少授一点</b> —— 已经给出去的额度收不回来，
    /// 只能靠「这一轮少给」让它慢慢回落。
    /// </para>
    /// </remarks>
    private long ResizeWindowIfNeeded()
    {
        if (!_policy.IsAdaptive)
        {
            return 0;
        }

        int current = _window.Size;

        // 「最近读空过」看的是这一轮与上一轮：回补常常发生在一轮数据还没收完的时候，
        // 读空（在一轮的开头）与见底（在一轮的末尾）就被那次回补隔在了两边。
        int sinceStarved = Volatile.Read(ref _adjustsSinceStarved);
        Interlocked.CompareExchange(ref _adjustsSinceStarved, Math.Min(sinceStarved + 1, int.MaxValue / 2), sinceStarved);
        bool starvedLately = sinceStarved <= 1;

        // 吃紧：见底，或者读的一方空等了额度（见 CreditIdleDetector）。它每轮都得调（顺带把这一轮的记录挪成「上一轮」），
        // 所以先单独调，不放进会短路的条件里。
        bool idled = _creditIdle.EndRound(Stopwatch.GetTimestamp(), eligible: current >= 4 * _channel.ReceiveMaxPacketBytes);
        if (idled || _windowWasExhausted)
        {
            _windowWasExhausted = false;
            _idleRounds = 0;

            if (!starvedLately)
            {
                return 0;   // 见底是读的一方跟不上造成的：扩窗只会多缓一堆没读的数据
            }

            int grown = (int)Math.Min((long)current * 2, _policy.MaximumBytes);
            if (grown <= current)
            {
                return 0;
            }

            // 扩出来的每一个字节都是对端可以塞进来、我们得缓着的内存 —— 先向会话的窗口总预算申请。
            // 曾经扩窗从不计预算：开通道时只计初始窗口，之后每条通道都能长到上限（默认 64 MiB），
            // 256 MiB 的会话总上限就只管得住「开通道那一刻」。
            if (!_channel.TryChargeBudget(grown - current))
            {
                return 0;
            }

            _window.Resize(grown);
            RecordWindow(grown);
            _creditIdle.Discard();   // 再扩要有新一轮的空等作证：一段空档不连扩两回
            return grown - current;
        }

        if (++_idleRounds < 3)
        {
            return 0;
        }

        _idleRounds = 0;
        int shrunk = (int)Math.Max((long)(current * 0.75), _policy.MinimumBytes);

        if (shrunk >= current)
        {
            return 0;
        }

        _window.Resize(shrunk);
        _channel.RefundBudget(current - shrunk);
        RecordWindow(shrunk);

        // 负数：这一轮少授这么多，窗口就此回落。
        return shrunk - current;
    }

    /// <summary>窗口额定大小变了：记进 <c>velashell.ssh.channel.window</c>。</summary>
    private void RecordWindow(int size)
    {
        if (_channel.MetricsHost is { } host && Diagnostics.SshMetrics.ChannelWindow.Enabled)
        {
            Diagnostics.SshMetrics.ChannelWindow.Record(
                size, Diagnostics.SshMetrics.HostTag(host), Diagnostics.SshMetrics.ChannelTypeTag(_channel.ChannelType));
        }
    }

    private async ValueTask SendWindowAdjustAsync(uint bytes, CancellationToken cancellationToken)
    {
        if (bytes == 0 || _channel.IsClosedOrClosing())
        {
            return;
        }

        byte[] packet = new byte[9];
        packet[0] = (byte)SshMessageNumber.ChannelWindowAdjust;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(1), _channel.RemoteId);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(5), bytes);
        await _channel.SendIfStillOpenAsync(packet, cancellationToken).ConfigureAwait(false);
    }
}
