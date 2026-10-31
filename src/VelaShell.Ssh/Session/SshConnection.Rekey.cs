// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §7.1  发出 KEXINIT 之后到 NEWKEYS 之前只许发传输层消息
//   RFC 4253 §9    建议每 1 GiB 或每小时重协商一次
//   行为规格:      velashell-docs/zh/ssh/spec/03-key-exchange.md §八

using System.Buffers;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Diagnostics;

using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Session;

/// <summary>重协商需要的上下文（只有工厂建起来的连接才有）。</summary>
/// <remarks>
/// 重协商要把整个密钥交换再跑一遍，所以它需要首次交换时用过的那一套东西：
/// 算法清单、主机密钥策略、版本串（交换哈希的前两个输入）、以及被连的主机与端口
/// （交给主机密钥策略做裁决）。
/// <para>
/// 直接 <c>new SshConnection(...)</c> 建出来的连接没有这些 ——
/// 那种用法只在测试里出现，对端不会向它发起重协商。
/// </para>
/// </remarks>
/// <param name="Algorithms">本端的算法清单。</param>
/// <param name="HostKeyPolicy">主机密钥策略。</param>
/// <param name="Versions">版本交换的结果。</param>
/// <param name="Host">被连的逻辑主机名。</param>
/// <param name="Port">端口。</param>
/// <param name="MinimumRsaKeyBits">接受的最小 RSA 模数位数。</param>
/// <param name="HostKeyDecisionTimeout">主机密钥裁决的超时。</param>
internal sealed record SshRekeyContext(
    SshAlgorithmSet Algorithms,
    IHostKeyPolicy HostKeyPolicy,
    SshVersionExchangeResult Versions,
    string Host,
    int Port,
    int MinimumRsaKeyBits,
    TimeSpan HostKeyDecisionTimeout);

public sealed partial class SshConnection
{
    /// <summary>重协商用的上下文；<see langword="null"/> 表示这条连接不支持重协商。</summary>
    internal SshRekeyContext? RekeyContext { get; init; }

    /// <summary>我们主动发起重协商的阈值。</summary>
    internal SshRekeyPolicy RekeyPolicy { get; init; } = SshRekeyPolicy.Disabled;

    /// <summary>阈值多久看一眼。见 <c>SshConnectionOptions.RekeyCheckInterval</c>。</summary>
    internal TimeSpan RekeyCheckInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 报文数的硬线：任一方向在同一套密钥下到这么多个报文，<b>不论 <see cref="RekeyPolicy"/> 如何</b>都主动重协商。
    /// </summary>
    /// <remarks>默认 <see cref="SshRekeyPolicy.MaximumPackets"/>（2³¹），离序号回绕（2³²）留出一半的余量。只有测试会调小。</remarks>
    internal long RekeyHardPacketLimit { get; init; } = SshRekeyPolicy.MaximumPackets;

    /// <summary>一次重协商最多等多久：我们的 <c>KEXINIT</c> 等不到回应，或者交换卡在半路。</summary>
    /// <remarks>
    /// 重协商期间闸门关着，通道数据一律暂存 —— 对端永远不完成的话，发送就永远停着，
    /// 保活探测也被暂存、根本发不出去（velashell-docs/zh/ssh/spec/03 §9 的「KEX 超时」）。
    /// </remarks>
    internal TimeSpan RekeyTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>已经完成过几次重协商（诊断与测试用）。</summary>
    public int RekeyCount => Volatile.Read(ref _rekeyCount);

    /// <summary>重协商之后投递过几次开闸（测试用：失败的交换一次都不该开）。</summary>
    internal int SendGateOpensPosted => Volatile.Read(ref _sendGateOpensPosted);

    /// <summary>这条连接一共发出/收到了多少个报文（诊断用）。</summary>
    /// <remarks>
    /// 重协商的报文数阈值盯的就是它 —— 交出来，排障时才能回答
    /// 「离下一次换密钥还有多远」。
    /// </remarks>
    public long PacketsSent => _transport.PacketsSent;

    /// <inheritdoc cref="PacketsSent" />
    public long PacketsReceived => _transport.PacketsReceived;

    /// <summary>这条连接在线上一共发出/收到了多少字节（含版本标识串、报文头、填充与 MAC；压缩之后的）。</summary>
    /// <remarks>
    /// 状态栏、连接信息里的「这条连接用了多少流量」说的就是它。应用数据量要看各通道的 <c>SshChannel.BytesSent</c>。
    /// </remarks>
    public long BytesSent => _transport.BytesSent;

    /// <inheritdoc cref="BytesSent" />
    public long BytesReceived => _transport.BytesReceived;

    /// <summary>最近一次重协商是怎么来的；还没重协商过是 <see langword="null"/>。</summary>
    /// <remarks>
    /// <para>
    /// 它存在的理由和 <c>Algorithms</c> 一样：排障时要能回答
    /// 「这条连接刚才为什么换了密钥」，而库知道而不说，
    /// 使用者就只能去猜（架构原则 4）。
    /// </para>
    /// <para>
    /// 〔velashell-docs/zh/ssh/spec/03 §8〕结构化的：触发方式是枚举，到阈值时的观测值与阈值是数字。
    /// 曾经是一句自由书写的中文（<c>LastRekeyReason</c>，「单向报文数达到 1024（阈值 1024）」）—— 程序判断不了，
    /// 界面也翻译不了；对端发起的、调用方显式请求的也不记，只记我们按阈值发起的那一种。
    /// </para>
    /// </remarks>
    public SshRekeyCause? LastRekey => Volatile.Read(ref _lastRekey);

    /// <summary>最近一次重协商从收到对端的 <c>KEXINIT</c> 到新密钥装好用了多久；还没重协商过是 <see langword="null"/>。</summary>
    /// <remarks>这段时间里通道数据暂存、发不出去 —— 「终端偶尔卡一下」要从这里对得上。</remarks>
    public TimeSpan? LastRekeyDuration => Volatile.Read(ref _lastRekeyDurationTicks) is var ticks and > 0
        ? TimeSpan.FromTicks(ticks)
        : null;

    private long _lastRekeyDurationTicks;

    /// <summary>一次重协商做完了（新密钥已装好、暂存的通道数据放出去之后）。</summary>
    /// <remarks>
    /// <para>
    /// 〔velashell-docs/zh/ssh/spec/03 §8〕对端发起的、按阈值发起的、显式请求的都报。在接收循环上同步调用 ——
    /// <b>订阅者不要阻塞</b>；订阅者抛的异常吞掉，不影响连接。失败的重协商不报（连接随之判死，看 <see cref="Completion"/>）。
    /// </para>
    /// <para>曾经没有事件：重协商只能事后翻 <see cref="RekeyCount"/> 与 <see cref="LastRekey"/>，也没有耗时。</para>
    /// </remarks>
    public event EventHandler<SshRekeyEventArgs>? Rekeyed;

    private void RaiseRekeyed(SshRekeyEventArgs args)
    {
        if (Rekeyed is not { } handlers)
        {
            return;
        }

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<SshRekeyEventArgs>)handler)(this, args);
            }
            catch (Exception)
            {
                // 订阅者的异常不影响连接（同转发器的事件）。
            }
        }
    }

    /// <summary>重协商时只留下与钉住的主机密钥同类型的主机密钥算法；一个都不剩就原样返回。</summary>
    /// <remarks>
    /// 谈出另一种类型，服务端出示的必然是另一把钥，只会被当成「换了主机密钥」断开（见 <c>PinnedHostKey</c>）。
    /// 证书与否也要一致：<see cref="SshPublicKey.SupportsSignatureAlgorithm"/> 比的是去掉证书后缀的名字，
    /// 只看它的话，钉住的是证书时普通算法也会留下；谈成普通算法，服务端出示的是那把钥而不是证书，同样被当成换了钥。
    /// </remarks>
    internal static SshAlgorithmSet RestrictToPinnedHostKey(SshAlgorithmSet algorithms, SshPublicKey pinned)
    {
        string[] sameType =
        [
            .. algorithms.HostKey.Where(a => pinned.SupportsSignatureAlgorithm(a)
                && a.EndsWith(SshAlgorithmNames.CertificateSuffix, StringComparison.Ordinal) == pinned.IsCertificate),
        ];

        return sameType.Length > 0 ? algorithms with { HostKey = sameType } : algorithms;
    }

    /// <summary>重协商用的算法清单：主机密钥算法收窄到钉住的那把钥。</summary>
    /// <remarks>
    /// 发出去的 KEXINIT 与本地协商<b>必须用同一份</b>。曾经本端发起时 KEXINIT 用的是没收窄的清单、本地协商却用收窄后的 ——
    /// 服务端清单不变时两边结果一样，只是一个隐患：协商出来的未必是对端按我们发的清单算出来的那一个。
    /// </remarks>
    private SshAlgorithmSet RekeyAlgorithms(SshRekeyContext context) =>
        HostKey is { } pinned ? RestrictToPinnedHostKey(context.Algorithms, pinned) : context.Algorithms;

    /// <summary>主动发起一次密钥重协商。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// <para>
    /// 发出我们的 <c>KEXINIT</c> 就返回，<b>不等重协商完成</b>：
    /// 剩下的由接收循环在对端的 <c>KEXINIT</c> 到达时接着做
    /// （<see cref="OnPeerKexInitAsync"/>）。
    /// 想等完成，轮询 <see cref="RekeyCount"/>。
    /// </para>
    /// <para>
    /// <b>为什么不在这里把整件事做完</b>：密钥交换要读对端的报文，
    /// 而这条传输唯一的读者是接收循环。在这里读就是两个读者抢同一条流。
    /// </para>
    /// <para>
    /// 已经在重协商中时这是一个空操作 —— 重复发 <c>KEXINIT</c> 是协议违规。
    /// </para>
    /// </remarks>
    public ValueTask StartRekeyAsync(CancellationToken cancellationToken = default) =>
        StartRekeyCoreAsync(new SshRekeyCause(SshRekeyTrigger.Requested), cancellationToken);

    /// <summary>发起一次重协商，真发出了 <c>KEXINIT</c> 才把 <paramref name="cause"/> 记进 <see cref="LastRekey"/>。</summary>
    private async ValueTask StartRekeyCoreAsync(SshRekeyCause cause, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (RekeyContext is null)
        {
            throw new InvalidOperationException(
                "这条连接不是由 SshConnection.ConnectAsync 建的，拿不到重协商需要的算法清单与主机密钥策略。");
        }

        // 取消只在「发起」之前生效：状态一旦记下，KEXINIT 就必须真的入队 ——
        // 曾经是先记状态、再在入队时被取消，闸门已经关上而 KEXINIT 没发，
        // 这条连接的发送从此永远暂存，对端下一次发起的重协商还会拿一份没发过的 KEXINIT 去算交换哈希。
        cancellationToken.ThrowIfCancellationRequested();

        ValueTask sent;
        lock (_stateLock)
        {
            // 已经发过 KEXINIT、或者一次交换正在跑：都是「已经在谈了」。
            // 曾经只看前者 —— 交换一开始它就被清掉，而阈值要等交换完成才归零，
            // 于是监视循环在交换进行中又发了一个 KEXINIT（协议违规，对端断连）。
            if (_ourPendingKexInit is not null || _kexInProgress)
            {
                return;
            }

            ArrayBufferWriter<byte> buffer = new();
            SshKexInitMessage.Encode(
                RekeyAlgorithms(RekeyContext), includeIndicators: false, buffer);
            byte[] ourKexInit = buffer.WrittenSpan.ToArray();
            _ourPendingKexInit = ourKexInit;
            Volatile.Write(ref _lastRekey, cause);

            // 关闸要在发 KEXINIT **之前** —— 反过来的话，两者之间发出去的
            // 通道数据就违反了 RFC 4253 §7.1。两者都走发送泵的队列，先后就是入队的先后。
            //
            // **两者都在锁里入队**（入队是同步的，等的只是刷出）：放到锁外的话，
            // 对端同时发起的那一次交换可能已经在接收循环上把它的第一帧排进队列，
            // 我们的 KEXINIT 反倒落在它后面。
            PostControl(OutboundKind.CloseGate);
            sent = SendControlAsync(ourKexInit, CancellationToken.None);
            _ = WatchUnansweredKexInitAsync(ourKexInit);
        }

        await sent.ConfigureAwait(false);
    }

    /// <summary>我们发出的 <c>KEXINIT</c> 超时还没被对端回应，就把连接判死。</summary>
    /// <remarks>
    /// 对端回了之后的那一段由 <see cref="OnPeerKexInitAsync"/> 自己计时；这里只管「一直没回」——
    /// 那时接收循环上什么都没发生，没有别人会发现闸门一直关着。
    /// </remarks>
    private async Task WatchUnansweredKexInitAsync(byte[] ourKexInit)
    {
        try
        {
            await Task.Delay(RekeyTimeout, Time, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;   // 连接先收工了
        }

        bool unanswered;
        lock (_stateLock)
        {
            unanswered = ReferenceEquals(_ourPendingKexInit, ourKexInit);
        }

        if (unanswered)
        {
            Fault(new SshConnectionClosedException(
                SshFailureReason.Timeout, SshPhase.Rekeying,
                $"发起密钥重协商之后 {RekeyTimeout.TotalSeconds:0} 秒，对端都没有回 KEXINIT。"));
        }
    }

    /// <summary>监视阈值，到点就主动发起重协商。</summary>
    /// <remarks>
    /// 只做「发起」这一件事，不参与密钥交换本身 —— 那是接收循环的活。
    /// </remarks>
    private async Task RekeyMonitorLoopAsync(CancellationToken cancellationToken)
    {
        // 阈值最小是 1 分钟 / 64 MiB / 1024 个报文，默认 5 秒的粒度足够，
        // 又不至于让一条闲着的连接每秒都醒一次。
        TimeSpan tick = RekeyCheckInterval;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(tick, Time, cancellationToken).ConfigureAwait(false);

                if (ShouldRekey() is { } cause)
                {
                    await StartRekeyCoreAsync(cause, cancellationToken).ConfigureAwait(false);

                    // 发起之后先歇一拍：等接收循环把这一轮谈完，
                    // 不然下一次 tick 会看到同一组还没归零的计数。
                    await Task.Delay(tick, Time, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 连接收工了。
        }
        catch (Exception)
        {
            // 发起失败说明连接已经出问题了，接收循环会把它判死 ——
            // 这里没有别的补救动作，也不该把这条异常再抛一遍。
        }
    }

    /// <summary>到阈值了吗：到了的话交回是哪一条（进 <see cref="LastRekey"/>），没到是 <see langword="null"/>。</summary>
    private SshRekeyCause? ShouldRekey()
    {
        SshRekeyPolicy policy = RekeyPolicy;

        long bytes = Math.Max(
            _transport.BytesSent - Volatile.Read(ref _bytesAtLastKex),
            _transport.BytesReceived - Volatile.Read(ref _bytesReceivedAtLastKex));
        long packets = Math.Max(
            _transport.PacketsSent - Volatile.Read(ref _packetsAtLastKex),
            _transport.PacketsReceived - Volatile.Read(ref _packetsReceivedAtLastKex));

        // ⚠️ 报文数这一条**最要紧**：序号是 32 位的，chacha20-poly1305 的 nonce 就是序号 ——
        //    同一套密钥下回绕会重用 nonce，可以伪造报文；HMAC 套件则可以被重放（RFC 4344 §3.1）。
        //    字节数与时长只是 RFC 4253 §9 的建议，这一条是硬约束：先看与策略无关的那条硬线。
        //    曾经它只是策略的一项，SshRekeyPolicy.Disabled 会把它一并关掉。
        long underKey = Math.Max(_transport.SendPacketsUnderKey, _transport.ReceivePacketsUnderKey);
        if (underKey >= RekeyHardPacketLimit)
        {
            return new SshRekeyCause(SshRekeyTrigger.PacketHardLimit, underKey, RekeyHardPacketLimit);
        }

        if (policy.MaxPackets > 0 && packets >= policy.MaxPackets)
        {
            return new SshRekeyCause(SshRekeyTrigger.Packets, packets, policy.MaxPackets);
        }

        if (policy.MaxBytes > 0 && bytes >= policy.MaxBytes)
        {
            return new SshRekeyCause(SshRekeyTrigger.Bytes, bytes, policy.MaxBytes);
        }

        TimeSpan interval = policy.MaxInterval;
        if (interval > TimeSpan.Zero)
        {
            TimeSpan elapsed = Time.GetElapsedTime(Volatile.Read(ref _lastKexAt));
            if (elapsed >= interval)
            {
                return new SshRekeyCause(
                    SshRekeyTrigger.Interval, (long)elapsed.TotalMilliseconds, (long)interval.TotalMilliseconds);
            }
        }

        return null;
    }

    /// <summary>记下这一刻的计数，作为下一轮阈值的基准。</summary>
    private void SnapshotRekeyBaseline()
    {
        Volatile.Write(ref _bytesAtLastKex, _transport.BytesSent);
        Volatile.Write(ref _bytesReceivedAtLastKex, _transport.BytesReceived);
        Volatile.Write(ref _packetsAtLastKex, _transport.PacketsSent);
        Volatile.Write(ref _packetsReceivedAtLastKex, _transport.PacketsReceived);
        Volatile.Write(ref _lastKexAt, Time.GetTimestamp());
    }

    /// <summary>收到对端的 <c>KEXINIT</c> —— 对端要重协商。</summary>
    /// <remarks>
    /// <para>
    /// <b>这件事必须应答，不能忽略。</b> OpenSSH 的 <c>RekeyLimit</c> 默认是
    /// 1 GiB 或 1 小时，到点它自己发 <c>KEXINIT</c>。不应答的表现不是「功能缺失」，
    /// 而是<b>开着的会话在某个时刻忽然断掉</b> —— 长时间挂着的 shell、
    /// 传到一半的大文件，都栽在这里。
    /// </para>
    /// <para>
    /// <b>整个密钥交换就在接收循环上原地跑完</b>，不另起任务。这是刻意的：
    /// 接收循环是这条传输唯一的读者，而「读到对端的 NEWKEYS」与
    /// 「换上新的接收密钥」之间<b>一个报文都不能插进来</b>。
    /// 放到别的任务上去做，这个顺序就要靠额外的同步来保证，而那是白找麻烦。
    /// </para>
    /// </remarks>
    internal async ValueTask OnPeerKexInitAsync(
        ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (RekeyContext is not { } context)
        {
            // 没有上下文就真的做不了。**明确失败，不要沉默** ——
            // 沉默的话对端会一直等我们的 KEXINIT，最后以超时收场，
            // 而那个超时指不到这里。
            await FaultProtocolAsync(
                "对端发起了密钥重协商，但这条连接不是由 SshConnection.ConnectAsync 建的，" +
                "拿不到重协商需要的算法清单与主机密钥策略。",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        // 载荷要复制一份：它背后是接收缓冲，密钥交换过程中会被回收重用。
        byte[] peerKexInit = payload.ToArray();
        long startedAt = Time.GetTimestamp();

        // 我们自己发起过吗？
        //
        // 三种情况在这里汇成一条路径：
        //   · 对端发起 —— _ourPendingKexInit 是 null，下面由 runner 去发我们的 KEXINIT；
        //   · 我们发起 —— 已经发过了，把那一份交给 runner，别再发第二个；
        //   · 两边同时发起 —— RFC 4253 §7.1 说这合法，且**只做一次**密钥交换。
        //     它长得和「我们发起」一模一样，所以不需要额外的代码。
        byte[]? ourKexInit;
        lock (_stateLock)
        {
            ourKexInit = _ourPendingKexInit;
            _ourPendingKexInit = null;

            // 我们没发过 KEXINIT：这一次是对端发起的。
            if (ourKexInit is null)
            {
                Volatile.Write(ref _lastRekey, new SshRekeyCause(SshRekeyTrigger.Peer));
            }

            // 从这里到开闸都算「在谈」—— StartRekeyAsync 看到它就不会再发一个 KEXINIT。
            _kexInProgress = true;
        }

        // 关闸：从现在到 NEWKEYS，只许发传输层消息（RFC 4253 §7.1）。
        // 通道数据会被暂存，开闸后按原顺序流出。
        // （我们自己发起时已经关过了，Close 是幂等的。）
        //
        // 只投递、不等：闸门由发送泵在队列里的这个位置上关，
        // 接收循环不需要等它 —— 接收循环在这里等任何发送都有自锁的风险。
        PostControl(OutboundKind.CloseGate);

        SshRekeyEventArgs? completed = null;
        try
        {
            SshAlgorithmSet algorithms = RekeyAlgorithms(context);

            SshKeyExchangeRunner runner = new(
                new RekeyKexTransport(this),
                algorithms,
                context.HostKeyPolicy,
                context.MinimumRsaKeyBits)
            {
                HostKeyDecisionTimeout = context.HostKeyDecisionTimeout,

                // 首次交换定下的；每次重协商的结果都把它原样带回来，所以 Algorithms 里一直是它。
                InitialStrictKeyExchange = Algorithms.StrictKeyExchange,

                // 钉住首次交换的主机密钥，不再走策略（spec/03 §8.4）。
                PinnedHostKey = HostKey,
            };

            // 交换卡在半路（对端不发 31、不发 NEWKEYS）的话，闸门一直关着、发送一直暂存 ——
            // 给它一个期限，到点就把连接判死，而不是无声地停住。
            using CancellationTokenSource timer = new(RekeyTimeout, Time);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);

            // 〔velashell-docs/zh/ssh/spec/03 §8.2〕到点**直接判死**，不只是取消令牌：交换的报文走发送泵，
            // 本端发送卡住（对端不读、链路半断）时「等这一帧发出去」不响应取消 —— 只取消令牌的话这次交换永远等下去，
            // 闸门永远关着。判死会停下发送泵，卡着的那次写随之放出来。曾经只有本端发起、对端一直不回 KEXINIT 那一种
            // 有兜底（WatchUnansweredKexInitAsync），对端发起的没有。
            int outcome = 0;   // 0 进行中、1 做完了、2 到点判死了
            using CancellationTokenRegistration onDeadline = deadline.Token.Register(() =>
            {
                if (!cancellationToken.IsCancellationRequested && Interlocked.CompareExchange(ref outcome, 2, 0) == 0)
                {
                    Fault(new SshConnectionClosedException(
                        SshFailureReason.Timeout, SshPhase.Rekeying,
                        $"密钥重协商在 {RekeyTimeout.TotalSeconds:0} 秒内没有完成。"));
                }
            });

            SshKeyExchangeResult result;
            try
            {
                result = await runner.RunAsync(
                    context.Versions,
                    context.Host,
                    context.Port,
                    sessionId: _sessionId,
                    peerKexInit: peerKexInit,
                    ourKexInitAlreadySent: ourKexInit,
                    resetCompression: true,
                    deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new SshConnectionClosedException(
                    SshFailureReason.Timeout, SshPhase.Rekeying,
                    $"密钥重协商在 {RekeyTimeout.TotalSeconds:0} 秒内没有完成。");
            }
            catch (SshException ex) when (ex.Phase != SshPhase.Rekeying)
            {
                throw AsRekeyFailure(ex);
            }

            if (Interlocked.CompareExchange(ref outcome, 1, 0) == 2)
            {
                // 交换恰好在判死的同时做完：连接已经判死了，新密钥不再装。
                throw new SshConnectionClosedException(
                    SshFailureReason.Timeout, SshPhase.Rekeying,
                    $"密钥重协商在 {RekeyTimeout.TotalSeconds:0} 秒内没有完成。");
            }

            lock (_stateLock)
            {
                Algorithms = result.Algorithms;
            }

            Interlocked.Increment(ref _rekeyCount);
            if (MetricsHost is { } host)
            {
                SshMetrics.Rekeys.Add(1, SshMetrics.HostTag(host));
            }
            SnapshotRekeyBaseline();

            // 〔velashell-docs/zh/ssh/spec/03 §8.2〕**只在交换成功时开闸。**暂存的帧随之按原顺序流出。
            Interlocked.Increment(ref _sendGateOpensPosted);
            PostControl(OutboundKind.OpenGate);

            TimeSpan duration = Time.GetElapsedTime(startedAt);
            Volatile.Write(ref _lastRekeyDurationTicks, Math.Max(duration.Ticks, 1));
            completed = new SshRekeyEventArgs(
                LastRekey ?? new SshRekeyCause(SshRekeyTrigger.Peer), duration, RekeyCount, result.Algorithms);
        }
        finally
        {
            // 失败时**不开闸**：异常一路抛到接收循环，连接随即判死。开闸的话，发送泵可能抢在判死之前
            // 把暂存的通道数据写出去 —— 在 KEXINIT 之后、NEWKEYS 之前发应用数据违反 RFC 4253 §7.1，
            // 刚判定「主机密钥变了」之后更不该再往外发东西。暂存区由发送泵的收尾丢掉，
            // 等着背压的发送方也由那里放出来（拿到连接关闭的异常），不会永远挂着。
            //
            // 成功时开闸之后才允许下一次发起：它的关闸排在这个开闸后面。
            lock (_stateLock)
            {
                _kexInProgress = false;
            }
        }

        // 在「可以再发起」之后才报：订阅者收到事件就发起下一次时，不会因为还记着「在谈」而被当成空操作悄悄吞掉。
        // 曾经在清掉之前就报 —— 一个「每次重协商完就再来一次」的订阅者，第二次就停了。
        if (completed is not null)
        {
            RaiseRekeyed(completed);
        }
    }

    /// <summary>把密钥交换按首次交换的口径报出的失败，改成「这条已经建好的连接在重协商时断了」。</summary>
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/08 §2.1〕交换器不分首次与重协商：验签失败、协商不上报的是 <see cref="SshConnectException"/>，
    /// 阶段一律是 <see cref="SshPhase.KeyExchange"/>。重协商时连接早就建好了 —— 按类型分流的调用方会把它当成「没连上」。
    /// 原因码不变，阶段改成 <see cref="SshPhase.Rekeying"/>；协议错误仍是 <see cref="SshProtocolException"/>，
    /// 其余是 <see cref="SshConnectionClosedException"/>。原来的异常挂在内层（协商失败时的双方名单还在它上面）。
    /// </remarks>
    private static SshException AsRekeyFailure(SshException ex)
    {
        string message = $"密钥重协商失败：{ex.Message}";
        return ex is SshProtocolException or Crypto.Kex.SshKeyExchangeException
            ? new SshProtocolException(SshPhase.Rekeying, message, ex)
            : new SshConnectionClosedException(ex.Reason, SshPhase.Rekeying, message, ex)
            {
                DisconnectReason = (ex as SshConnectionClosedException)?.DisconnectReason,
                PeerDescription = (ex as SshConnectionClosedException)?.PeerDescription,
            };
    }

    /// <summary>重协商期间的密钥交换收发通道。</summary>
    /// <remarks>
    /// 它把密钥交换接到<b>会话已有的收发路径</b>上：
    /// 读走接收循环（唯一的读者），写走发送锁（N 条通道的泵都在写）。
    /// 为什么不能直接动传输，见 <see cref="ISshKexTransport"/>。
    /// </remarks>
    private sealed class RekeyKexTransport(SshConnection connection) : ISshKexTransport
    {
        /// <inheritdoc />
        /// <remarks>
        /// 不受背压限制：密钥交换跑在接收循环上，而背压要等重协商完成才会解除
        /// （暂存的帧也计在里面）—— 在这里等背压就是接收循环等它自己。
        /// </remarks>
        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken) =>
            connection.SendControlAsync(packet, cancellationToken);

        /// <inheritdoc />
        /// <remarks>
        /// 重协商期间<b>仍然会收到通道数据</b>（闸门只管发送方向，
        /// <c>velashell-docs/zh/ssh/spec/03</c> §8.3）。那些报文在这里就地派发掉，
        /// 只有密钥交换自己的报文才返回出去 —— 否则一条正在跑的 SFTP
        /// 会在重协商的那一两个 RTT 里整个停住。
        /// </remarks>
        public async ValueTask<SshInboundPacket> ReadPacketAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                SshInboundPacket packet =
                    await connection._transport.ReadPacketAsync(cancellationToken).ConfigureAwait(false);

                if (packet.IsEndOfStream)
                {
                    return packet;
                }

                Volatile.Write(ref connection._lastInboundAt, connection.Time.GetTimestamp());

                // 1–49 是传输层消息，密钥交换就是靠它们完成的 —— 交回去。
                // 其余的是会话层报文，就地派发。
                if (SendGate<ReadOnlyMemory<byte>>.IsTransportMessage(packet.MessageNumber))
                {
                    return packet;
                }

                await connection.DispatchAsync(packet, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public async ValueTask SendNewKeysAndSwitchSendAsync(
            ISshCipherSuite send,
            ISshCompressor? sendCompressor,
            bool strictKeyExchange,
            CancellationToken cancellationToken)
        {
            // ⚠️ 发 NEWKEYS 与换发送侧状态之间不能插进任何一帧。
            // 两件事是发送泵里同一个出站项做完的 —— 单写者，结构上就插不进来。
            await connection.SendNewKeysAsync(send, sendCompressor, strictKeyExchange, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        /// <remarks>
        /// 在接收循环上跑，而且正好在「刚读完对端的 NEWKEYS」与
        /// 「读下一个报文」之间 —— 这个位置是它唯一正确的位置。
        /// </remarks>
        public void SwitchReceive(
            ISshCipherSuite receive, ISshCompressor? receiveCompressor, bool strictKeyExchange)
        {
            connection._transport.SetReceiveCipherSuite(receive, strictKeyExchange);

            if (receiveCompressor is not null)
            {
                connection._transport.SetReceiveCompressor(receiveCompressor);
            }
        }
    }
}
