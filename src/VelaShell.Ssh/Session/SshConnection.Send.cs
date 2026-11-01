// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §6    报文逐个封装,序号逐个推进 —— 所以出站必须单写者
//   RFC 4253 §7.1  发出 KEXINIT 之后到 NEWKEYS 之前只许发传输层消息
//   RFC 4253 §7.3  发出 NEWKEYS 之后的下一个报文就用新密钥
//   行为规格:      velashell-docs/zh/ssh/spec/03-key-exchange.md §8.2;velashell-docs/zh/ssh/design/architecture.md §5.4

using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Threading.Tasks.Sources;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Session;

public sealed partial class SshConnection
{
    /// <summary>
    /// 排在发送泵前面、还没封装上线的字节上限。超过它，数据面的发送方就要等。
    /// </summary>
    /// <remarks>
    /// 重协商期间被闸门暂存的帧也算在里面 —— 对端迟迟不完成重协商时，
    /// 我们宁可让发送方等着，也不能无界地攒（原则 2）。
    /// </remarks>
    internal const long MaxPendingSendBytes = SendGate<ReadOnlyMemory<byte>>.DefaultMaxStashBytes;

    /// <summary>一轮最多合并多少字节再刷出。</summary>
    private const int BatchBytes = 64 * 1024;

    /// <summary>一轮最多合并多少个出站项再刷出。</summary>
    private const int BatchItems = 32;

    private readonly Channel<OutboundItem> _outbound = Channel.CreateUnbounded<OutboundItem>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

    /// <summary>
    /// 插队的那一条队：本端的 <c>CHANNEL_WINDOW_ADJUST</c>。发送泵每取一项都先看这里。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 同一条连接上在大量上传时，普通队列里可能积着 <see cref="MaxPendingSendBytes"/> 的数据；
    /// 另一条通道的窗口回补排在它们后面（曾经还要先在背压上等空位），对端就一直等窗口 ——
    /// 下载被上传拖到几乎停住。回补只有 9 字节，每条通道同一时刻至多一条在途，所以不受背压、直接插队。
    /// </para>
    /// <para>
    /// 插队只会让它<b>提前</b>，不会让它越过它之前的东西：<c>CHANNEL_CLOSE</c> 发过之后，
    /// 入队锁里的「还发不发」就把回补拦下了；重协商期间它照样被闸门暂存，暂存保序。
    /// 回补本身也没有和我们发出的数据之间的顺序约束 —— 它说的是我们的收，不是我们的发。
    /// </para>
    /// </remarks>
    private readonly Channel<OutboundItem> _priority = Channel.CreateUnbounded<OutboundItem>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

    /// <summary>
    /// 交互道：交互式通道（<see cref="Channels.SshChannelOptions.IsInteractive"/>，终端）发的报文。发送泵让它与普通队列轮流出队。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 〔Q7，velashell-docs/zh/ssh/spec/05 §3.2〕每条通道同一时刻至多一帧数据在队里（stdin 泵等上一帧刷出去才发下一帧），
    /// 可一堆隧道连接、SFTP 一起在传时，普通队列里就排着每条一帧；按键排在它们后面，要等它们都上线 ——
    /// 积压 2 MiB、上行 5 Mbit/s 时就是 3 秒以上。走交互道，至多等普通队列的一帧。
    /// </para>
    /// <para>
    /// <b>轮流，不是优先</b>：往终端里粘贴一大段、或者开着很多个终端一起刷屏时，普通队列照样每隔一项轮到一次，不会被饿死。
    /// </para>
    /// <para>
    /// 一条通道的报文<b>全都</b>走同一条道 —— 它自己发的，以及接收循环替它回的（<c>CHANNEL_CLOSE</c>、请求应答）——
    /// 所以它自己的报文之间顺序不变，<c>CLOSE</c> 照样在它的数据之后。道在开通道时定下，之后不换。
    /// 与别的通道之间、与连接级的报文之间本来就没有顺序约束；重协商期间它照样被闸门暂存，暂存保序。
    /// 回补（<see cref="_priority"/>）仍在它前面。
    /// </para>
    /// </remarks>
    private readonly Channel<OutboundItem> _interactive = Channel.CreateUnbounded<OutboundItem>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

    /// <summary>这一次轮到交互道先取（只有发送泵碰它，见 <see cref="TryReadNext"/>）。</summary>
    private bool _isInteractiveTurn;

    /// <summary>一帧排进哪条队。</summary>
    private enum OutboundLane : byte
    {
        /// <summary>普通队列。</summary>
        Normal,

        /// <summary>交互道（见 <see cref="_interactive"/>）。</summary>
        Interactive,

        /// <summary>插队的那一条（见 <see cref="_priority"/>）。</summary>
        Priority,
    }

    /// <summary>
    /// 闸门本身不再限量：限量挪到了入队这一步（<see cref="_sendBudget"/>），
    /// 因为只有在那里才分得清「数据面的发送方」与「接收循环」—— 后者绝不能等。
    /// </summary>
    private readonly SendGate<ReadOnlyMemory<byte>> _sendGate = new(long.MaxValue);

    /// <summary>排在发送泵前面、还没上线的字节数，以及排队等额度的数据面发送方。</summary>
    private readonly SendBudget _sendBudget = new(MaxPendingSendBytes);

    /// <summary>让「登记账本 + 入队」成为一个动作。</summary>
    private readonly Lock _enqueueLock = new();

    /// <summary>排在发送泵前面、还没上线的字节数（测试用来确认积压已经顶满）。</summary>
    internal long PendingSendBytes => _sendBudget.Pending;
    private readonly Task _sendPump;

    /// <summary>出站项的种类。</summary>
    private enum OutboundKind : byte
    {
        /// <summary>一个报文。</summary>
        Frame,

        /// <summary>关闸：进入重协商。</summary>
        CloseGate,

        /// <summary>发 NEWKEYS 并换上新的发送侧状态。</summary>
        NewKeys,

        /// <summary>开闸并按原顺序排空暂存区。</summary>
        OpenGate,

        /// <summary>什么都不做：叫醒发送泵去看插队的那一条队（见 <see cref="_priority"/>）。</summary>
        Wake,
    }

    /// <summary>排队等发送泵处理的一项。</summary>
    /// <param name="Kind">种类。</param>
    /// <param name="Packet">报文（<see cref="OutboundKind.Frame"/> 时）。</param>
    /// <param name="AccountedBytes">计入 <see cref="_sendBudget"/> 的字节数；控制帧为 0。</param>
    /// <param name="Completion">上线之后要通知的人；<see langword="null"/> 表示没人等。</param>
    /// <param name="Suite">新的发送密码套件（<see cref="OutboundKind.NewKeys"/> 时）。</param>
    /// <param name="Compressor">新的发送压缩器；<see langword="null"/> 表示不动压缩。</param>
    /// <param name="StrictKex">是否启用了严格 KEX。</param>
    /// <param name="IsReply">接收循环投递的应答（计在 <see cref="_queuedReplyBytes"/> 上）。</param>
    /// <param name="Borrowed">
    /// <see cref="Packet"/> 是发送方借来的（池里租的），<see cref="Completion"/> 一完成它就被回收 ——
    /// 要暂存就得先复制一份（见 <see cref="Process"/>）。
    /// </param>
    private readonly record struct OutboundItem(
        OutboundKind Kind,
        ReadOnlyMemory<byte> Packet,
        int AccountedBytes,
        SendCompletion? Completion,
        ISshCipherSuite? Suite = null,
        ISshCompressor? Compressor = null,
        bool StrictKex = false,
        bool IsReply = false,
        bool Borrowed = false);

    // ------------------------------------------------------------ 入队

    /// <summary>发一个报文，等它真正刷上线。</summary>
    /// <remarks>
    /// <para>
    /// <b>报文的内存交给发送泵</b>，直到本方法返回之前都不能改动它 ——
    /// 重协商期间它可能被暂存到开闸之后才发出。
    /// </para>
    /// <para>
    /// 取消只在入队之前（含背压等待）生效。入队之后这一帧一定会按顺序发出去，
    /// 那时再取消只会让调用方误以为它没发。
    /// </para>
    /// </remarks>
    internal ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken) =>
        EnqueueAsync(packet, applyBackpressure: true, admit: null, cancellationToken);

    /// <summary>发一个报文；<paramref name="onEnqueued"/> 与入队<b>原子地</b>执行。</summary>
    /// <remarks>
    /// 给「应答靠 FIFO 对齐」的请求用：登记账本与入队必须是同一个动作。
    /// 分开做的话，两个并发的请求可能登记顺序与上线顺序相反，
    /// 应答就会被安到对方头上 —— 而那是静默的错误答案；
    /// 登记之后在背压上被取消，账本里又会留下一个永远等不到应答的空位。
    /// </remarks>
    internal ValueTask SendAsync(
        ReadOnlyMemory<byte> packet, Action onEnqueued, CancellationToken cancellationToken) =>
        EnqueueAsync(
            packet,
            applyBackpressure: true,
            () =>
            {
                onEnqueued();
                return true;
            },
            cancellationToken);

    /// <summary>投递一个报文、不等它上线、不受背压限制；与入队同一时刻问 <paramref name="admit"/>。</summary>
    /// <remarks>
    /// 接收循环回复通道报文用（它不能在背压上等）。理由同 <see cref="Channels.ISshChannelHost.SendIfAsync"/>。
    /// <paramref name="isInteractive"/> 是那条通道的道（<see cref="Channels.SshChannel.IsInteractive"/>）：回给它的 <c>CLOSE</c> 要排在它自己的数据后面。
    /// </remarks>
    internal void PostIf(ReadOnlyMemory<byte> packet, Func<bool> admit, bool isInteractive)
    {
        lock (_enqueueLock)
        {
            if (admit())
            {
                Post(packet, isInteractive);
            }
        }
    }

    /// <summary>发一个不受背压限制的报文（接收循环与密钥交换用）。</summary>
    /// <remarks>
    /// ⚠️ <b>接收循环绝不能在背压上等。</b>背压要靠接收循环读到
    /// <c>WINDOW_ADJUST</c>、读到对端的 <c>NEWKEYS</c> 才能解除 ——
    /// 让它等背压，就是让它等它自己。
    /// </remarks>
    private ValueTask SendControlAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken) =>
        EnqueueAsync(packet, applyBackpressure: false, admit: null, cancellationToken);

    private async ValueTask EnqueueAsync(
        ReadOnlyMemory<byte> packet,
        bool applyBackpressure,
        Func<bool>? admit,
        CancellationToken cancellationToken,
        bool borrowed = false,
        bool isInteractive = false)
    {
        // 空包不该走到这里 —— 消息编号是第一个字节。
        if (packet.IsEmpty)
        {
            throw new ArgumentException("待发的报文是空的。", nameof(packet));
        }

        ThrowIfFaulted();
        cancellationToken.ThrowIfCancellationRequested();

        // 本端的窗口回补插队、不在背压上等（见 _priority）；交互式通道的其余报文走交互道（见 _interactive）。
        bool jumpQueue = packet.Span[0] == (byte)SshMessageNumber.ChannelWindowAdjust;
        OutboundLane lane = jumpQueue ? OutboundLane.Priority : isInteractive ? OutboundLane.Interactive : OutboundLane.Normal;

        int accounted = 0;
        if (applyBackpressure)
        {
            accounted = packet.Length;
            if (jumpQueue)
            {
                _sendBudget.Charge(accounted);
            }
            else
            {
                // 额度不够就按到达顺序排队，放行的那一刻已经替这一帧记上了账（见 SendBudget）。
                await _sendBudget.ReserveAsync(accounted, cancellationToken).ConfigureAwait(false);
            }
        }

        var completion = SendCompletion.Rent();
        ValueTask done = completion.AsValueTask();
        OutboundItem frame = new(OutboundKind.Frame, packet, accounted, completion, Borrowed: borrowed);

        bool admitted = true;
        bool enqueued;
        if (admit is null)
        {
            enqueued = TryWriteFrame(frame, lane);
        }
        else
        {
            // ⚠️ **先登记，再入队。**这把锁只让入队者之间互斥，挡不住另一个线程上的发送泵：
            //    入队的那一刻泵就可能把这一帧取走发出去，对端的应答随即到达接收循环 ——
            //    要是这时账本还没登记，接收循环看到的就是「没有对应请求的应答」，
            //    按 FIFO 失步把整条连接判死（症状是一串「服务端拒绝…」与连接无故断开，
            //    泵线程能立刻抢到核的 Linux / macOS 上尤其常见）。
            //    登记与入队仍在同一把锁里，所以登记顺序与上线顺序照样一致。
            //    入队失败只发生在连接已经收尾时：调用方拿到的是下面的关闭异常，
            //    不会去等那个登记项，而账本随连接一起被结算。
            //    「通道关没关」的判定也在这把锁里做（见 ISshChannelHost.SendIfAsync）。
            lock (_enqueueLock)
            {
                admitted = admit();
                enqueued = admitted && TryWriteFrame(frame, lane);
            }
        }

        if (!admitted)
        {
            // 调用方自己说不发了：不是错误，照常还回背压额度、放它走。
            ReleasePendingBytes(accounted);
            completion.SetResult();
        }
        else if (!enqueued)
        {
            ReleasePendingBytes(accounted);
            completion.SetException(ClosedException());
        }

        await done.ConfigureAwait(false);
    }

    /// <summary>把一帧放进普通队列、交互道（见 <see cref="_interactive"/>），或者插队（见 <see cref="_priority"/>）。</summary>
    /// <returns>放进去了为 <see langword="true"/>；连接已经收尾时为 <see langword="false"/>。</returns>
    private bool TryWriteFrame(in OutboundItem frame, OutboundLane lane)
    {
        if (lane == OutboundLane.Normal)
        {
            return _outbound.Writer.TryWrite(frame);
        }

        if (!(lane == OutboundLane.Priority ? _priority : _interactive).Writer.TryWrite(frame))
        {
            return false;
        }

        // 泵只在普通队列上等 —— 放一个空项叫醒它。泵正忙着的话，它取下一项之前就会先看插队的那一条、轮到时看交互道。
        // 叫醒失败只发生在收尾时，那时这一帧会随收尾一起结算。
        _outbound.Writer.TryWrite(new OutboundItem(OutboundKind.Wake, default, 0, null));
        return true;
    }

    /// <summary>取下一项：插队的先；交互道与普通队列轮流（一边空着就取另一边）。</summary>
    private bool TryReadNext(ChannelReader<OutboundItem> reader, out OutboundItem item)
    {
        if (_priority.Reader.TryRead(out item))
        {
            return true;
        }

        _isInteractiveTurn = !_isInteractiveTurn;
        return _isInteractiveTurn
            ? _interactive.Reader.TryRead(out item) || reader.TryRead(out item)
            : reader.TryRead(out item) || _interactive.Reader.TryRead(out item);
    }

    /// <summary>投递一个报文，不等它上线（接收循环用）。</summary>
    /// <remarks>
    /// 接收循环里要回的那些小报文（<c>CHANNEL_CLOSE</c>、请求应答、
    /// <c>OPEN_CONFIRMATION</c>…）走这里：发送泵保证它们按投递顺序上线，
    /// 发送失败则由发送泵把整条会话判死 —— 接收循环不需要、也不应该等。
    /// </remarks>
    internal void Post(ReadOnlyMemory<byte> packet, bool isInteractive = false)
    {
        if (Volatile.Read(ref _fault) is not null)
        {
            return;
        }

        // 接收循环不能等背压，但**也不能无界地攒**：对端不读我们发的东西、同时不停地发要应答的报文
        // （全局请求、畸形的开通道、未知报文号），每一个都换来一个排在发送队列里的应答。
        // 超过上限就判对端违规断开 —— 那时它显然已经不在读了，DISCONNECT 也送不到。
        // （重协商期间被闸门暂存的那一部分由重协商超时兜底。）
        long queued = Interlocked.Add(ref _queuedReplyBytes, packet.Length);
        if (queued > _limits.MaxQueuedReplyBytes)
        {
            Interlocked.Add(ref _queuedReplyBytes, -packet.Length);
            Fault(new SshProtocolException(
                SshPhase.Open,
                $"待发的应答积压超过了 {_limits.MaxQueuedReplyBytes} 字节 —— " +
                "对端一直在发要应答的报文，却不读我们发回去的东西。"));
            return;
        }

        // 投递的字节照样计入背压：应答攒多了，数据面的发送方就得等。
        _sendBudget.Charge(packet.Length);
        if (!TryWriteFrame(
                new OutboundItem(OutboundKind.Frame, packet, packet.Length, null, IsReply: true),
                isInteractive ? OutboundLane.Interactive : OutboundLane.Normal))
        {
            ReleasePendingBytes(packet.Length);
            Interlocked.Add(ref _queuedReplyBytes, -packet.Length);
        }
    }

    /// <summary>投递一个报文，不等背压、不等上线；<paramref name="onEnqueued"/> 与入队<b>原子地</b>执行。</summary>
    /// <returns>入队了为 <see langword="true"/>；连接已经判死或收尾时为 <see langword="false"/>。</returns>
    /// <remarks>
    /// <para>
    /// 给保活探测用（见 <c>GlobalRequestLedger.ProbeAsync</c>）：探测要对付的正是「发送泵卡在一次写上」的死链，
    /// 它自己不能也卡在那里。
    /// </para>
    /// <para>
    /// 不受背压也不会无界：每个保活周期至多一帧，连着 <c>MaxMissed</c> 帧没有应答就判死了。
    /// 字节照样计入背压，数据面的发送方看得到它。
    /// </para>
    /// </remarks>
    internal bool PostRegistered(ReadOnlyMemory<byte> packet, Action onEnqueued)
    {
        if (Volatile.Read(ref _fault) is not null)
        {
            return false;
        }

        _sendBudget.Charge(packet.Length);
        bool enqueued;
        lock (_enqueueLock)
        {
            // 先登记、再入队，理由同 EnqueueAsync。入队失败只发生在连接收尾时，账本随连接一起结算。
            onEnqueued();
            enqueued = _outbound.Writer.TryWrite(
                new OutboundItem(OutboundKind.Frame, packet, packet.Length, null));
        }

        if (!enqueued)
        {
            ReleasePendingBytes(packet.Length);
        }
        return enqueued;
    }

    /// <summary>接收循环投递、还没被发送泵取走的应答字节数（见 <see cref="SshConnectionLimits.MaxQueuedReplyBytes"/>）。</summary>
    private long _queuedReplyBytes;

    /// <summary>投递一个控制项（关闸 / 开闸），不等它生效。</summary>
    /// <remarks>
    /// 控制项与报文在同一条队列里，所以「关闸之前入队的帧照常发出、之后的被暂存」
    /// 是由队列顺序保证的，不需要任何锁。
    /// </remarks>
    private void PostControl(OutboundKind kind) =>
        _outbound.Writer.TryWrite(new OutboundItem(kind, default, 0, null));

    /// <summary>发 NEWKEYS 并换上新的发送侧状态，等它刷上线。</summary>
    private async ValueTask SendNewKeysAsync(
        ISshCipherSuite suite, ISshCompressor? compressor, bool strictKex, CancellationToken cancellationToken)
    {
        ThrowIfFaulted();
        cancellationToken.ThrowIfCancellationRequested();

        var completion = SendCompletion.Rent();
        ValueTask done = completion.AsValueTask();

        if (!_outbound.Writer.TryWrite(
                new OutboundItem(OutboundKind.NewKeys, default, 0, completion, suite, compressor, strictKex)))
        {
            completion.SetException(ClosedException());
        }

        await done.ConfigureAwait(false);
    }

    /// <summary>还回背压额度；有人在排队就按顺序放行。</summary>
    private void ReleasePendingBytes(int bytes) => _sendBudget.Refund(bytes);

    private Exception ClosedException() =>
        Volatile.Read(ref _fault) ?? new SshConnectionClosedException(
            SshFailureReason.Aborted, SshPhase.Open, "连接已经关闭，报文没有发出去。");

    // ------------------------------------------------------------ 发送泵

    /// <summary>
    /// 唯一的写者：把出站项逐个封装进传输，一轮合并若干项再刷出一次。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 单写者是 RFC 4253 §6 逼出来的：序号逐包推进、密钥在 NEWKEYS 处切换，
    /// 这两件事都要求封装严格按一个顺序进行。与其让 N 个写者抢一把锁、
    /// 每人各刷一次，不如只有一个写者，一轮把攒下的帧一起刷出去。
    /// </para>
    /// <para>
    /// 重协商的闸门只有这里会碰 —— 关闸、开闸、换密钥都是队列里的一项，
    /// 它们与报文之间的先后就是入队的先后，不需要另外的同步。
    /// </para>
    /// </remarks>
    private async Task SendPumpAsync(CancellationToken cancellationToken)
    {
        ChannelReader<OutboundItem> reader = _outbound.Reader;
        List<SendCompletion> flushed = [];
        Exception? failure = null;

        // 正在处理的那一项：Process 抛异常时它已经出队、又还没登记进 flushed —— 收尾要单独结算它。
        OutboundItem? inFlight = null;

        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                int bytes = 0;
                int items = 0;

                while (bytes < BatchBytes && items < BatchItems && TryReadNext(reader, out OutboundItem item))
                {
                    items++;
                    inFlight = item;
                    bytes += Process(item, flushed);
                    inFlight = null;
                }

                await _transport.FlushAsync(cancellationToken).ConfigureAwait(false);

                foreach (SendCompletion completion in flushed)
                {
                    completion.SetResult();
                }
                flushed.Clear();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 我们自己要收工。
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (failure is not null)
        {
            Fault(failure);
        }

        // 收尾：还在等的人一律拿到同一个原因，而不是永远挂着。
        // 两条队都先关上再排空 —— 之后的写入一律失败（调用方拿到关闭异常），不会有漏在队里没人结算的。
        _outbound.Writer.TryComplete();
        _priority.Writer.TryComplete();
        _interactive.Writer.TryComplete();
        Exception reason = ClosedException();

        foreach (SendCompletion completion in flushed)
        {
            completion.SetException(reason);
        }

        // 〔CH-E7〕处理到一半抛了异常的那一项：曾经它的完成通知永远不结算 —— 如果是 NEWKEYS，
        // 挂住的是等它的接收循环，DisposeAsync 跟着一直等。
        if (inFlight is { } stuck)
        {
            ReleasePendingBytes(stuck.AccountedBytes);
            stuck.Completion?.SetException(reason);
            stuck.Suite?.Dispose();
            stuck.Compressor?.Dispose();
        }

        while (TryReadNext(reader, out OutboundItem item))
        {
            ReleasePendingBytes(item.AccountedBytes);
            item.Completion?.SetException(reason);
            item.Suite?.Dispose();
            item.Compressor?.Dispose();
        }

        _sendGate.DrainForAbort();
        Interlocked.Exchange(ref _queuedReplyBytes, 0);

        // 还在排队等额度的发送方拿到同一个原因；之后再来的也是。
        _sendBudget.Close(reason);
    }

    /// <summary>处理一个出站项。</summary>
    /// <returns>这一项写进传输的字节数。</returns>
    private int Process(in OutboundItem item, List<SendCompletion> flushed)
    {
        switch (item.Kind)
        {
            case OutboundKind.Frame:
                {
                    if (item.IsReply)
                    {
                        Interlocked.Add(ref _queuedReplyBytes, -item.Packet.Length);
                    }

                    var number = (SshMessageNumber)item.Packet.Span[0];
                    ReadOnlyMemory<byte> packet = item.Packet;

                    // ⚠️ 借来的内存要被暂存的话先复制一份：发送方在我们完成它之后就把那块缓冲还回池里了，
                    //    暂存区却要一直引用到开闸 —— 不复制，开闸后发出去的是别人后来写进那块缓冲的东西。
                    //    直接发走的那条（绝大多数时候）不必复制：WritePacket 加密时已经拷走了。
                    if (item.Borrowed && !_sendGate.WouldSendNow(number))
                    {
                        packet = packet.ToArray();
                    }

                    // 暂存时登记的是「计入背压的字节数」，排空时照原数归还。
                    switch (_sendGate.Admit(packet, number, item.AccountedBytes))
                    {
                        case SendGateAdmission.Send:
                            _transport.WritePacket(packet.Span);
                            ReleasePendingBytes(item.AccountedBytes);
                            if (item.Completion is not null)
                            {
                                flushed.Add(item.Completion);
                            }
                            return item.Packet.Length;

                        case SendGateAdmission.Stashed:
                            // 已暂存：开闸后按原顺序流出。发送方不必再等 ——
                            // 它等的是「收下了」，不是「上线了」，而暂存的字节仍然计在背压里。
                            item.Completion?.SetResult();
                            return 0;

                        default:
                            // StashFull：这道闸不限量（_sendGate 的注释），走不到这里。真走到了就是那条前提被改坏了 ——
                            // 帧**没有**被收下，照「已暂存」结算等于静默丢帧（流被截断、对端无从察觉）。抛出来让连接照实判死。
                            // 曾经 StashFull 落进的正是「已暂存」的分支。
                            throw new System.Diagnostics.UnreachableException(
                                $"发送闸门拒收了一帧（{number}）：它在连接上不该有暂存上限。");
                    }
                }

            case OutboundKind.CloseGate:
                _sendGate.Close();
                if (item.Completion is not null)
                {
                    flushed.Add(item.Completion);
                }
                return 0;

            case OutboundKind.NewKeys:
                // ⚠️ NEWKEYS 与换发送侧状态之间不能插进任何一帧 ——
                // 单写者 + 同一项里做完，这件事是结构上保证的。
                _transport.WritePacket([(byte)SshMessageNumber.NewKeys]);
                _transport.SetSendCipherSuite(item.Suite!, item.StrictKex);
                if (item.Compressor is not null)
                {
                    _transport.SetSendCompressor(item.Compressor);
                }
                if (item.Completion is not null)
                {
                    flushed.Add(item.Completion);
                }
                return 1;

            case OutboundKind.OpenGate:
                {
                    _sendGate.Open();
                    int written = 0;
                    while (_sendGate.TryTakeStashed(out ReadOnlyMemory<byte> frame, out int accounted))
                    {
                        _transport.WritePacket(frame.Span);
                        ReleasePendingBytes(accounted);
                        written += frame.Length;
                    }
                    if (item.Completion is not null)
                    {
                        flushed.Add(item.Completion);
                    }
                    return written;
                }

            case OutboundKind.Wake:
                return 0;   // 只是来叫醒泵的；插队或交互道的那一帧在它前面处理过了，或者轮到时再处理

            default:
                throw new InvalidOperationException($"未知的出站项 {item.Kind}。");
        }
    }

    /// <summary>
    /// 一次发送的完成通知。池化，免得每个报文都分配一个 <see cref="TaskCompletionSource"/>
    /// （原则 1：热路径用 <see cref="ValueTask"/> + 池化的 <see cref="IValueTaskSource"/>）。
    /// </summary>
    private sealed class SendCompletion : IValueTaskSource
    {
        private const int MaxPooled = 256;
        private static readonly ConcurrentQueue<SendCompletion> Pool = new();

        // 续体一律异步执行：完成它的是发送泵，不能让等待方的代码跑在泵的线程上。
        private ManualResetValueTaskSourceCore<bool> _core = new() { RunContinuationsAsynchronously = true };

        public static SendCompletion Rent() =>
            Pool.TryDequeue(out SendCompletion? pooled) ? pooled : new SendCompletion();

        public ValueTask AsValueTask() => new(this, _core.Version);

        public void SetResult() => _core.SetResult(true);

        public void SetException(Exception exception) => _core.SetException(exception);

        void IValueTaskSource.GetResult(short token)
        {
            try
            {
                _core.GetResult(token);
            }
            finally
            {
                _core.Reset();
                if (Pool.Count < MaxPooled)
                {
                    Pool.Enqueue(this);
                }
            }
        }

        ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _core.GetStatus(token);

        void IValueTaskSource.OnCompleted(
            Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            _core.OnCompleted(continuation, state, token, flags);
    }
}
