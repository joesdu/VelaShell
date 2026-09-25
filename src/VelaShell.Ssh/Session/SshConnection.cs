// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §4    GLOBAL_REQUEST / REQUEST_SUCCESS / REQUEST_FAILURE
//   RFC 4254 §5    通道的开、关、数据、请求
//   行为规格:      velashell-docs/zh/ssh/spec/05-connection.md §1、§2、§6、§8

using System.Buffers;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Session;

/// <summary>已认证的会话：通道的多路复用器。</summary>
/// <remarks>
/// <para>
/// 一条接收循环把入站报文分发到各通道；一个发送泵是唯一的写者 ——
/// 多条通道并发地往外写，由它按入队顺序逐帧封装、一轮合并刷出（architecture.md §5.4）。
/// </para>
/// <para>
/// <b>接收循环绝不因为某一条通道而停下。</b>通道的管道水位定得比窗口高，
/// 写进去不会阻塞；真正的背压由 SSH 窗口表达，而窗口挂在消费上。
/// 在接收循环里等某条通道，就是把其它所有通道一起饿死。
/// </para>
/// </remarks>
public sealed partial class SshConnection : ISshChannelHost, IAsyncDisposable
{
    private const int MaxFieldBytes = 64 * 1024;

    /// <summary>
    /// 一个 <c>CHANNEL_EXTENDED_DATA</c> 报文里，数据之外占 <c>packet_length</c> 的最大字节数：
    /// <c>padding_length</c>(1) + 编号(1) + 通道号(4) + 类型码(4) + 长度(4) + 最大填充(255)。
    /// </summary>
    private const int ChannelPacketOverhead = 1 + 1 + 4 + 4 + 4 + 255;

    private readonly SshPacketTransport _transport;
    private readonly SshConnectionLimits _limits;
    private readonly Lock _stateLock = new();

    private readonly Dictionary<uint, SshChannel> _channels = [];
    private readonly Dictionary<uint, TaskCompletionSource<SshChannel>> _pendingOpens = [];

    /// <summary>全局请求的应答账本。<b>同样没有 id，靠 FIFO 对齐。</b></summary>
    private readonly FifoRequestLedger<SshGlobalRequestReply> _globalRequests = new();

    /// <summary>已回收、但还不到复用时间的通道号。</summary>
    private readonly Queue<(uint Id, long ReusableAtTicks)> _recycledIds = new();

    /// <summary>按通道类型登记的入站通道处理器（服务端发起的通道）。</summary>
    /// <remarks>
    /// 同一类型可以有<b>多个</b>处理器 —— 两个远程转发（<c>-R 8080</c> 与 <c>-R 9090</c>）
    /// 都要接 <c>forwarded-tcpip</c>。后登记的先问，第一个同意的接走。
    /// </remarks>
    private readonly Dictionary<string, List<IIncomingChannelHandler>> _incomingHandlers = [];

    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>
    /// <see cref="Disconnected"/> 的源。与 <see cref="_lifetime"/> 分开是因为两者
    /// 职责不同：<c>_lifetime</c> 用来把内部的收发泵停下来，而这一个是<b>对外</b>的信号，
    /// 会在 <see cref="Fault"/> 时就触发 —— 那时收发泵可能还在做收尾。
    /// </summary>
    private readonly CancellationTokenSource _disconnected = new();
    private Task? _receiveLoop;
    private Task? _keepAliveLoop;
    private Task? _rekeyMonitorLoop;

    private uint _nextChannelId;
    private long _windowBudgetUsed;
    private Exception? _fault;
    private bool _disposed;

    /// <summary>在一条已完成认证的传输上建立会话。</summary>
    /// <param name="transport">传输。</param>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="limits">通道限额。</param>
    public SshConnection(SshPacketTransport transport, byte[] sessionId, SshConnectionLimits? limits = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        SessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        _limits = limits ?? SshConnectionLimits.Default;
        Disconnected = _disconnected.Token;

        // 发送泵从一开始就要在 —— 打开通道之前、重协商之前，任何出站都经过它。
        // 直接调用而不是 Task.Run：它同步跑到第一个 await（等队列）就让出。
        _sendPump = SendPumpAsync(_lifetime.Token);
    }

    /// <summary>会话标识。</summary>
    public byte[] SessionId { get; }

    /// <summary>这条会话实际协商出来的算法（从工厂建的连接才有）。</summary>
    /// <remarks>
    /// <para>
    /// <b>协商成功的结果同样要交出去，不只是失败时的名单。</b>
    /// 终端产品要在状态栏上写「aes256-gcm@openssh.com · zlib@openssh.com」，
    /// 排障时第一句话也是「你们这条连接到底谈成了什么」——
    /// 库知道而不说，使用者就只能自己再探一次（架构原则 4）。
    /// </para>
    /// <para>
    /// 也是唯一能确认<b>压缩到底有没有谈成</b>的地方：服务端不支持时会
    /// 静默落到 <c>none</c>，<b>不会报错</b>。
    /// </para>
    /// </remarks>
    /// <remarks>
    /// 重协商会把它整个换掉（算法可以谈出不一样的结果），所以读写都走
    /// <see cref="_stateLock"/> —— 它不是一个装完就不动的值。
    /// </remarks>
    public Crypto.SshNegotiatedAlgorithms? Algorithms
    {
        get
        {
            lock (_stateLock)
            {
                return _negotiated;
            }
        }
        init => _negotiated = value;
    }

    /// <summary>服务端出示的主机公钥（从工厂建的连接才有）。</summary>
    public HostKeys.SshPublicKey? HostKey { get; init; }

    /// <summary>保活策略。</summary>
    public KeepAlivePolicy KeepAlive { get; init; } = KeepAlivePolicy.Disabled;

    /// <summary>给人看的描述（<c>user@host:port</c>），进日志与异常。</summary>
    public string Description { get; init; } = "";

    /// <summary>上一次收到任何入站报文的时刻（<c>Environment.TickCount64</c> 口径）。</summary>
    private long _lastInboundTicks = Environment.TickCount64;
    private int _rekeyCount;

    /// <summary>我们发出、还没被一次交换用掉的 <c>KEXINIT</c>（<see cref="_kexInProgress"/> 为假时才可能非空）。</summary>
    private byte[]? _ourPendingKexInit;

    /// <summary>一次重协商正在接收循环上跑（从对端的 <c>KEXINIT</c> 到开闸）。</summary>
    /// <remarks>与 <see cref="_ourPendingKexInit"/> 一起在 <c>_stateLock</c> 里读写。</remarks>
    private bool _kexInProgress;
    private long _bytesAtLastKex;
    private long _bytesReceivedAtLastKex;
    private long _packetsAtLastKex;
    private long _packetsReceivedAtLastKex;
    private long _lastKexTicks = Environment.TickCount64;
    private string? _lastRekeyReason;
    private Crypto.SshNegotiatedAlgorithms? _negotiated;

    /// <summary>当前占着通道号的通道数。</summary>
    /// <remarks>
    /// <b>正在打开、还没等到对端确认的那条也算在内</b> —— 通道号从发出
    /// <c>CHANNEL_OPEN</c> 起就已经占用，限额也按这个数算。
    /// </remarks>
    public int ChannelCount
    {
        get
        {
            lock (_stateLock)
            {
                return _channels.Count;
            }
        }
    }

    /// <summary>会话是否还活着。</summary>
    public bool IsAlive => Volatile.Read(ref _fault) is null && !_disposed;

    /// <summary>
    /// 会话结束时被取消的令牌 —— 掉线、被对端断开、协议出错，或者自己释放。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这是给「掉线要立刻有反应」的调用方准备的。</b>只有 <see cref="IsAlive"/>
    /// 的话，上层只能定时去问一句「还活着吗」——
    /// 而终端的读循环、自动重连、状态栏的连接指示灯都要的是「一断就知道」，
    /// 不是「最多一秒之后知道」。
    /// </para>
    /// <para>
    /// 它<b>只表示会话到此为止</b>，不表示为什么：原因在各个操作抛出的异常里
    /// （<c>SshConnectionClosedException</c> 带着 <c>DisconnectReason</c>）。
    /// 一个 <see cref="CancellationToken"/> 装不下原因，硬塞只会让调用方
    /// 从取消异常里去猜。
    /// </para>
    /// <para>
    /// 正常释放同样会取消它 —— 「连接没了」对读循环来说是同一件事，
    /// 区分「是我关的还是掉线」得看调用方自己的状态。
    /// </para>
    /// </remarks>
    /// <remarks>
    /// 令牌在构造时就取好并留住，而不是每次读 <c>_disconnected.Token</c> ——
    /// 那个属性在源被释放之后会抛 <see cref="ObjectDisposedException"/>，
    /// 而「连接已经释放了」恰恰是最常去读这个令牌的时刻。
    /// </remarks>
    public CancellationToken Disconnected { get; }

    /// <summary>开始收包。</summary>
    /// <remarks>必须在打开任何通道之前调用一次。</remarks>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _receiveLoop ??= Task.Run(() => ReceiveLoopAsync(_lifetime.Token));

        if (KeepAlive.IsEnabled)
        {
            _keepAliveLoop ??= Task.Run(() => KeepAliveLoopAsync(_lifetime.Token));
        }

        // 只有工厂建的连接才谈得上主动发起重协商（要用 RekeyContext）。
        if (RekeyPolicy.IsEnabled && RekeyContext is not null)
        {
            _rekeyMonitorLoop ??= Task.Run(() => RekeyMonitorLoopAsync(_lifetime.Token));
        }
    }


    /// <summary>保活循环。</summary>
    /// <remarks>
    /// <para>
    /// <b>计时基准是「上次收到任何报文」，不是固定周期。</b>
    /// 连接正忙的时候根本不需要发保活 —— 数据本身就证明了链路活着，
    /// 按固定周期发只是在给一条已经证明活着的链路添噪音。
    /// </para>
    /// <para>
    /// <b>任何入站报文都重置计数</b>，不只是保活应答。
    /// </para>
    /// </remarks>
    private async Task KeepAliveLoopAsync(CancellationToken cancellationToken)
    {
        int missed = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                long idle = Environment.TickCount64 - Volatile.Read(ref _lastInboundTicks);
                long interval = (long)KeepAlive.Interval.TotalMilliseconds;

                if (idle < interval)
                {
                    // 还没闲够。睡到「刚好闲够」的那一刻再看。
                    await Task.Delay((int)Math.Max(interval - idle, 50), cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (Volatile.Read(ref _fault) is not null)
                {
                    return;   // 会话已经判死了，没什么可探的
                }

                long before = Volatile.Read(ref _lastInboundTicks);

                // ⚠️ **每一次探测都要有自己的期限。**
                //    保活要对付的正是半开连接：报文写得进本机的发送缓冲，
                //    应答却永远不会来。不设期限的话这一次 await 永远不返回，
                //    missed 永远不会加一 —— 判死的逻辑就成了摆设。
                //
                //    超时的那一项**仍然留在应答账本里**：应答只是迟到，
                //    它来的时候必须落在这一项上，后面的应答才对得上号。
                bool alive = await ProbeAsync(TimeSpan.FromMilliseconds(interval), cancellationToken)
                    .ConfigureAwait(false);

                // 有应答，或者期间收到了别的报文 —— 都算活着。
                if (alive || Volatile.Read(ref _lastInboundTicks) != before)
                {
                    missed = 0;
                    continue;
                }

                if (++missed >= KeepAlive.MaxMissed)
                {
                    // 判死时用 KeepAliveTimeout 而不是笼统的 Timeout ——
                    // 上层的自动重连策略只应该对这一类生效。
                    Fault(new SshConnectionClosedException(
                        SshFailureReason.KeepAliveTimeout, SshPhase.Open,
                        $"连续 {missed} 次保活探测没有得到应答，判定连接已死" +
                        (Description.Length > 0 ? $"（{Description}）" : "") + "。"));
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 我们自己要收工。
        }
        catch (Exception)
        {
            // 会话已经出问题了，接收循环会自己发现。
        }
    }

    /// <summary>发一次保活探测，在 <paramref name="timeout"/> 之内等应答。</summary>
    /// <returns>期限内有应答（成功或失败都算）为 <see langword="true"/>。</returns>
    /// <remarks>
    /// ⚠️ <b>期限从入队那一刻算起，罩住「发出去」与「等应答」两段。</b>
    /// 死链的典型样子是对端不再读、发送泵卡在一次写上、出站积压顶满背压：
    /// 这时探测若在背压上等、或等自己刷上线之后才开始计时，它就跟着一起卡住，
    /// <c>missed</c> 永远不加一 —— 恰恰在最需要判死的时候判不了。
    /// </remarks>
    private async ValueTask<bool> ProbeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> packet = EncodeGlobalRequest(
            SshAlgorithmNames.KeepAliveOpenSsh, default, wantReply: true);

        // 登记与入队是同一个动作：应答靠 FIFO 对齐（见 SendAsync 的重载说明）。
        Task<SshGlobalRequestReply>? reply = null;
        if (!PostRegistered(packet, () => reply = _globalRequests.Register()))
        {
            return false;
        }

        try
        {
            await reply!.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return Volatile.Read(ref _fault) is null;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------ 通道

    /// <summary>打开一条 <c>session</c> 通道。</summary>
    public ValueTask<SshChannel> OpenSessionChannelAsync(
        SshChannelOptions? options = null, CancellationToken cancellationToken = default) =>
        OpenChannelAsync("session", default, options, cancellationToken);

    /// <summary>打开一条通道。</summary>
    /// <param name="channelType">通道类型。</param>
    /// <param name="typeSpecificPayload">类型相关的附加字段。</param>
    /// <param name="options">通道参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="SshChannelException">对端拒绝，或本端限额已满。</exception>
    public async ValueTask<SshChannel> OpenChannelAsync(
        string channelType,
        ReadOnlyMemory<byte> typeSpecificPayload = default,
        SshChannelOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(channelType);

        SshChannelOptions effective = options ?? SshChannelOptions.Default;
        int window = effective.WindowPolicy.InitialBytes;

        // 我们宣告的单包上限必须装得进传输层允许的报文 —— 否则对端照着宣告发来的
        // 报文会被传输层当成超长报文而判为协议错误，错误还指不到这里。
        int maxPayload = _transport.MaxPacketLength - ChannelPacketOverhead;
        if (effective.ReceiveMaxPacketBytes > maxPayload)
        {
            throw new ArgumentException(
                $"通道的 ReceiveMaxPacketBytes（{effective.ReceiveMaxPacketBytes}）超过了传输层允许的上限 " +
                $"{maxPayload}（报文上限 {_transport.MaxPacketLength} 减去报文头与填充）。",
                nameof(options));
        }

        SshChannel channel;
        TaskCompletionSource<SshChannel> completion;

        lock (_stateLock)
        {
            ThrowIfFaulted();

            // 限额撞满**不断开会话** —— 通道是独立的失败域。
            //
            // 只数 `_channels`：正在打开的那条**也在里面**（见下面的登记），
            // 两个字典加起来会把它数两遍，等于把上限砍了一半。
            if (_channels.Count >= _limits.MaxChannels)
            {
                throw new SshChannelException(
                    SshFailureReason.ChannelOpenFailed,
                    $"本端的并发通道数已达上限 {_limits.MaxChannels}。");
            }

            if (_windowBudgetUsed + window > _limits.SessionWindowBudgetBytes)
            {
                throw new SshChannelException(
                    SshFailureReason.ChannelOpenFailed,
                    $"会话的接收窗口总预算已用尽（{_limits.SessionWindowBudgetBytes / (1024 * 1024)} MiB）。" +
                    "把窗口策略调小，或者少开几条并发通道。");
            }

            uint localId = AllocateChannelId();
            _windowBudgetUsed += window;

            channel = new SshChannel(this, localId, channelType, effective);
            completion = new TaskCompletionSource<SshChannel>(TaskCreationOptions.RunContinuationsAsynchronously);

            // ⚠️ **通道要在发 CHANNEL_OPEN 之前就登记进 `_channels`。**
            //
            // 登记晚了会丢应答，而且是随机丢：收包循环和这里是两个线程，
            // 内存传输上服务端可以在 `SendAsync` 的 await 还没恢复时就把
            // OPEN_CONFIRMATION 送到。那一刻 `OnChannelOpenConfirmation`
            // 会把 `_pendingOpens` 里的项**取走**，再去 `_channels` 里找通道 ——
            // 找不到，于是 `return`，应答就被丢在地上，TCS 永远不完成。
            //
            // 表现是「OpenChannelAsync 挂到用例超时」，而且每次挂的用例都不一样，
            // 因为它只取决于那两个线程谁先跑。满跑约十轮复现一次。
            // 两条 dump 把它钉死：客户端收到的报文号最后一个正是 91
            // （OPEN_CONFIRMATION），而 `_pendingOpens` 已经空了。
            _pendingOpens[localId] = completion;
            _channels[localId] = channel;
        }

        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteMessageNumber(SshMessageNumber.ChannelOpen);
        writer.WriteUtf8String(channelType);
        writer.WriteUInt32(channel.LocalId);
        writer.WriteUInt32((uint)window);
        writer.WriteUInt32((uint)effective.ReceiveMaxPacketBytes);
        writer.WriteRaw(typeSpecificPayload.Span);

        bool enqueued = false;
        try
        {
            // 取消只在入队之前生效（见 EnqueueAsync）：这一步正常返回，CHANNEL_OPEN 就一定会上线。
            await SendAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
            enqueued = true;

            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (enqueued)
        {
            // CHANNEL_OPEN 已经发出去了，调用方不等了 —— 但对端迟早会应答。
            //
            // ⚠️ **不能把它从账本里摘掉。**摘了的话，迟到的 OPEN_CONFIRMATION 找不到主、被丢在地上：
            //    服务端那条通道永远不关，每取消一次就占掉它一个 MaxSessions 名额（OpenSSH 默认 10），
            //    之后再开通道全都失败。所以让应答照常走完：确认下来的通道立刻关掉；
            //    对端拒绝、连接断开或释放时，通道已经由那几条路径收尾了。
            //    「确认刚到、取消紧跟着到」的竞态也落在这里 —— 那时 completion 已经带着结果。
            _ = completion.Task.ContinueWith(
                static opened => opened.Result.DisposeAsync().AsTask(),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
        catch (Exception)
        {
            // 没发出去（入队前取消、连接已经没了），或者对端拒绝了。
            lock (_stateLock)
            {
                _pendingOpens.Remove(channel.LocalId);

                // 对端拒绝时 OnOpenFailed 已经把通道摘掉、预算退回 —— 这里再退一次，
                // 预算就越拒越多，会话窗口总上限形同虚设。
                if (_channels.Remove(channel.LocalId))
                {
                    _windowBudgetUsed -= window;
                }
            }
            throw;
        }
    }

    /// <summary>把某一类型的处理器<b>整个换成</b>这一个（<see langword="null"/> 表示全部摘掉）。</summary>
    /// <param name="channelType">通道类型，如 <c>forwarded-tcpip</c>。</param>
    /// <param name="handler">处理器；传 <see langword="null"/> 取消该类型的全部登记。</param>
    /// <remarks>
    /// <para>
    /// <b>没有登记的类型一律明确拒绝</b>，不沉默 —— 沉默会让对端一直等着。
    /// </para>
    /// <para>
    /// 同一类型要挂多个处理器（多个远程转发、多个会话的 agent 转发）时用
    /// <see cref="AddIncomingChannelHandler"/> / <see cref="RemoveIncomingChannelHandler"/>：
    /// 这个方法会把别人登记的一起换掉。
    /// </para>
    /// </remarks>
    public void SetIncomingChannelHandler(string channelType, IIncomingChannelHandler? handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelType);

        lock (_stateLock)
        {
            if (handler is null)
            {
                _incomingHandlers.Remove(channelType);
            }
            else
            {
                _incomingHandlers[channelType] = [handler];
            }
        }
    }

    private Forwarding.X11ChannelRouter? _x11Router;

    /// <summary>这条连接上所有 X11 转发共用的入口（按假 cookie 分通道）。</summary>
    internal Forwarding.X11ChannelRouter X11Router
    {
        get
        {
            lock (_stateLock)
            {
                return _x11Router ??= new Forwarding.X11ChannelRouter(this);
            }
        }
    }

    /// <summary>给某一类型<b>再挂</b>一个处理器，不影响已有的。</summary>
    /// <remarks>
    /// 通道到达时<b>后登记的先问</b>：它的 <see cref="IIncomingChannelHandler.GetOptionsAsync"/>
    /// 抛异常（「这条不归我」）就问下一个，第一个同意的接走这条通道。
    /// 全都不要才拒绝，拒绝理由取最后一个问到的处理器给的。
    /// </remarks>
    public void AddIncomingChannelHandler(string channelType, IIncomingChannelHandler handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelType);
        ArgumentNullException.ThrowIfNull(handler);

        lock (_stateLock)
        {
            if (!_incomingHandlers.TryGetValue(channelType, out List<IIncomingChannelHandler>? list))
            {
                list = [];
                _incomingHandlers[channelType] = list;
            }
            list.Add(handler);
        }
    }

    /// <summary>摘掉<b>这一个</b>处理器；别人登记的同类型处理器不受影响。</summary>
    public void RemoveIncomingChannelHandler(string channelType, IIncomingChannelHandler handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelType);
        ArgumentNullException.ThrowIfNull(handler);

        lock (_stateLock)
        {
            if (_incomingHandlers.TryGetValue(channelType, out List<IIncomingChannelHandler>? list)
                && list.Remove(handler)
                && list.Count == 0)
            {
                _incomingHandlers.Remove(channelType);
            }
        }
    }

    // ------------------------------------------------------------ 全局请求

    /// <summary>发一个全局请求，只关心成不成。</summary>
    /// <returns><paramref name="wantReply"/> 为假时恒为 <see langword="true"/>。</returns>
    public async ValueTask<bool> SendGlobalRequestAsync(
        string requestType,
        ReadOnlyMemory<byte> payload = default,
        bool wantReply = true,
        CancellationToken cancellationToken = default)
    {
        SshGlobalRequestReply reply = await SendGlobalRequestWithReplyAsync(
            requestType, payload, wantReply, cancellationToken).ConfigureAwait(false);
        return reply.Success;
    }

    /// <summary>发一个全局请求，<b>并把应答载荷带回来</b>。</summary>
    /// <remarks>
    /// <c>tcpip-forward</c> 请求端口 0 时，服务端分配的实际端口就在
    /// <c>REQUEST_SUCCESS</c> 的载荷里（一个 <c>uint32</c>）——
    /// 只回成不成的话那个端口号就永远拿不到了。
    /// </remarks>
    public async ValueTask<SshGlobalRequestReply> SendGlobalRequestWithReplyAsync(
        string requestType,
        ReadOnlyMemory<byte> payload = default,
        bool wantReply = true,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!wantReply)
        {
            await SendAsync(EncodeGlobalRequest(requestType, payload, wantReply: false), cancellationToken)
                .ConfigureAwait(false);
            return new SshGlobalRequestReply(true, ReadOnlyMemory<byte>.Empty);
        }

        return await SendGlobalRequestCoreAsync(requestType, payload, onReply: null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>发一个要应答的全局请求；应答一到，先在接收循环上同步调用 <paramref name="onReply"/>。</summary>
    /// <remarks>
    /// 给「应答后面紧跟着的报文要用到应答内容」的请求用：远程转发请求端口 0 时，服务端回完应答
    /// 立刻就可能开回连通道，而给回连认路要用应答里的端口。等返回的任务完成再去记的话，
    /// 续体跑在线程池上，接收循环那时可能已经把那条回连当成「没人认领」拒掉了。
    /// <paramref name="onReply"/> 必须又短又不阻塞；连接断了时它拿到的是一个失败的应答。
    /// </remarks>
    internal ValueTask<SshGlobalRequestReply> SendGlobalRequestAsync(
        string requestType,
        ReadOnlyMemory<byte> payload,
        Action<SshGlobalRequestReply> onReply,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return SendGlobalRequestCoreAsync(requestType, payload, onReply, cancellationToken);
    }

    private async ValueTask<SshGlobalRequestReply> SendGlobalRequestCoreAsync(
        string requestType,
        ReadOnlyMemory<byte> payload,
        Action<SshGlobalRequestReply>? onReply,
        CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> packet = EncodeGlobalRequest(requestType, payload, wantReply: true);

        // 登记与入队是同一个动作：应答靠 FIFO 对齐（见 SendAsync 的重载说明）。
        Task<SshGlobalRequestReply>? reply = null;
        await SendAsync(packet, () => reply = _globalRequests.Register(onReply), cancellationToken)
            .ConfigureAwait(false);
        return await reply!.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ReadOnlyMemory<byte> EncodeGlobalRequest(
        string requestType, ReadOnlyMemory<byte> payload, bool wantReply)
    {
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteMessageNumber(SshMessageNumber.GlobalRequest);
        writer.WriteUtf8String(requestType);
        writer.WriteBoolean(wantReply);
        writer.WriteRaw(payload.Span);
        return buffer.WrittenMemory;
    }

    /// <summary>发一次保活探测。</summary>
    /// <remarks>
    /// <b>应答内容不重要，有应答就说明链路活着</b> —— 服务端回
    /// <c>REQUEST_FAILURE</c> 也算数（它本来就不认识这个请求类型）。
    /// </remarks>
    public async ValueTask<bool> SendKeepAliveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await SendGlobalRequestAsync(
                SshAlgorithmNames.KeepAliveOpenSsh, default, wantReply: true, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (SshException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------ ISshChannelHost

    /// <inheritdoc />
    ValueTask ISshChannelHost.SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken) =>
        SendAsync(packet, cancellationToken);

    /// <inheritdoc />
    ValueTask ISshChannelHost.SendAsync(
        ReadOnlyMemory<byte> packet, Action onEnqueued, CancellationToken cancellationToken) =>
        SendAsync(packet, onEnqueued, cancellationToken);

    /// <inheritdoc />
    ValueTask ISshChannelHost.SendIfAsync(
        ReadOnlyMemory<byte> packet, Func<bool> admit, CancellationToken cancellationToken) =>
        SendIfAsync(packet, admit, cancellationToken);

    /// <inheritdoc />
    ValueTask ISshChannelHost.SendBorrowedIfAsync(
        ReadOnlyMemory<byte> packet, Func<bool> admit, CancellationToken cancellationToken) =>
        EnqueueAsync(packet, applyBackpressure: true, admit, cancellationToken, borrowed: true);

    /// <inheritdoc />
    bool ISshChannelHost.TryReserveWindowBudget(int bytes)
    {
        lock (_stateLock)
        {
            if (_windowBudgetUsed + bytes > _limits.SessionWindowBudgetBytes)
            {
                return false;
            }
            _windowBudgetUsed += bytes;
            return true;
        }
    }

    /// <inheritdoc />
    void ISshChannelHost.ReleaseWindowBudget(int bytes)
    {
        lock (_stateLock)
        {
            _windowBudgetUsed -= bytes;
        }
    }

    /// <inheritdoc />
    void ISshChannelHost.OnChannelClosed(uint localId, int windowBytes)
    {
        lock (_stateLock)
        {
            if (!_channels.Remove(localId))
            {
                return;
            }
            _windowBudgetUsed -= windowBytes;
            RecycleChannelId(localId);
        }
    }

    // ------------------------------------------------------------ 接收循环

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                SshInboundPacket packet = await _transport.ReadPacketAsync(cancellationToken).ConfigureAwait(false);

                // 任何入站报文都证明链路活着 —— 不只是保活应答。
                Volatile.Write(ref _lastInboundTicks, Environment.TickCount64);

                if (packet.IsEndOfStream)
                {
                    Fault(new SshConnectionClosedException(
                        SshFailureReason.ClosedByPeer, SshPhase.Open, "对端关闭了连接。"));
                    return;
                }

                await DispatchAsync(packet, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 我们自己要收工（释放，或者 Fault 已经把会话判死）。
        }
        catch (Exception ex)
        {
            Fault(ex);
        }
        finally
        {
            // _lifetime 只有 Fault 与释放会取消，而 Fault 在取消之前就记下了原因 ——
            // 没有故障就只能是本端释放的。
            CloseAllChannels(Volatile.Read(ref _fault) ?? DisposedReason());
        }
    }

    /// <remarks>
    /// ⚠️ <b>这里的每一条路径都不许等发送。</b>要回的报文一律 <see cref="Post"/>：
    /// 发送泵按投递顺序把它们送出去，而接收循环接着读。
    /// 在这里等发送，TCP 发送缓冲一满，接收循环就停了 —— 而对端正等着我们读，
    /// 才肯去读我们发的东西。
    /// </remarks>
    private async ValueTask DispatchAsync(SshInboundPacket packet, CancellationToken cancellationToken)
    {
        switch (packet.MessageNumber)
        {
            // 对端要重协商。必须应答 —— 不应答的表现是「会话忽然断掉」。
            case SshMessageNumber.KexInit:
                await OnPeerKexInitAsync(packet.Payload, cancellationToken).ConfigureAwait(false);
                return;

            case SshMessageNumber.ChannelData:
                await OnChannelDataAsync(packet.Payload, cancellationToken).ConfigureAwait(false);
                return;

            case SshMessageNumber.ChannelExtendedData:
                await OnChannelExtendedDataAsync(packet.Payload, cancellationToken).ConfigureAwait(false);
                return;

            case SshMessageNumber.ChannelWindowAdjust:
                await OnChannelWindowAdjustAsync(packet.Payload, cancellationToken).ConfigureAwait(false);
                return;

            case SshMessageNumber.ChannelOpenConfirmation:
                OnChannelOpenConfirmation(packet.Payload);
                return;

            case SshMessageNumber.ChannelOpenFailure:
                OnChannelOpenFailure(packet.Payload);
                return;

            case SshMessageNumber.ChannelEof:
                Lookup(ReadRecipient(packet.Payload))?.OnEof();
                return;

            case SshMessageNumber.ChannelClose:
                OnChannelClose(packet.Payload);
                return;

            case SshMessageNumber.ChannelRequest:
                OnChannelRequest(packet.Payload);
                return;

            case SshMessageNumber.ChannelSuccess:
            case SshMessageNumber.ChannelFailure:
                await OnChannelRequestReplyAsync(
                    packet.Payload, packet.MessageNumber == SshMessageNumber.ChannelSuccess, cancellationToken)
                    .ConfigureAwait(false);
                return;

            case SshMessageNumber.GlobalRequest:
                OnGlobalRequest(packet.Payload);
                return;

            case SshMessageNumber.RequestSuccess:
            case SshMessageNumber.RequestFailure:
                await OnGlobalRequestReplyAsync(
                    packet.MessageNumber == SshMessageNumber.RequestSuccess,
                    packet.Payload.Length > 1 ? packet.Payload[1..].ToArray() : ReadOnlyMemory<byte>.Empty,
                    cancellationToken).ConfigureAwait(false);
                return;

            case SshMessageNumber.ChannelOpen:
                await OnPeerChannelOpenAsync(packet.Payload, cancellationToken).ConfigureAwait(false);
                return;

            // 这几种在任何阶段都可能出现，而且都不需要回应：
            //   · IGNORE / DEBUG —— RFC 4253 §11.2、§11.3；
            //   · UNIMPLEMENTED —— 对 UNIMPLEMENTED 再回 UNIMPLEMENTED 只会让两边互相回声；
            //   · EXT_INFO —— RFC 8308 §2.4 允许服务端在认证成功后再发一次。
            case SshMessageNumber.Ignore:
            case SshMessageNumber.Debug:
            case SshMessageNumber.Unimplemented:
            case SshMessageNumber.ExtInfo:
                return;

            case SshMessageNumber.Disconnect:
                Fault(ReadDisconnect(packet.Payload));
                return;

            default:
                // 未知报文编号：回 UNIMPLEMENTED，不断开（RFC 4253 §11.4）。
                PostUnimplemented();
                return;
        }
    }

    /// <summary>把对端的 <c>DISCONNECT</c> 解成带原因码与原话的异常。</summary>
    /// <remarks>
    /// 对端给的描述常常是唯一有用的信息（<c>Too many authentication failures</c>），
    /// 拼进一句「服务端断开了」就等于把它扔了（原则 3）。
    /// </remarks>
    private static SshConnectionClosedException ReadDisconnect(ReadOnlyMemory<byte> payload)
    {
        SshDisconnectReason? reason = null;
        string? description = null;
        try
        {
            SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
            reader.ReadMessageNumber(SshMessageNumber.Disconnect);
            reason = (SshDisconnectReason)reader.ReadUInt32();
            description = reader.ReadUtf8String(MaxFieldBytes);
        }
        catch (Exception)
        {
            // 残缺的 DISCONNECT 仍然是 DISCONNECT —— 能读出多少算多少。
        }

        string message = "服务端发来了 DISCONNECT" +
            (reason is { } code ? $"（{code}，{(uint)code}）" : "") +
            (string.IsNullOrEmpty(description) ? "。" : $"：{PeerText.Sanitize(description)}");

        return new SshConnectionClosedException(SshFailureReason.Disconnected, SshPhase.Open, message)
        {
            DisconnectReason = reason,
            PeerDescription = description,
        };
    }

    private async ValueTask OnChannelDataAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.ChannelData);
        uint recipient = reader.ReadUInt32();
        ReadOnlySequence<byte> data = reader.ReadString(int.MaxValue);

        SshChannel? channel = Lookup(recipient);
        if (channel is null)
        {
            return;   // 刚回收的通道的在途数据 —— 忽略，不断开
        }

        if (!channel.OnData(data))
        {
            await FaultProtocolAsync(
                $"通道 {recipient} 的对端发来了超出我们宣告窗口的数据 —— 这是明确的协议违规。",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask OnChannelExtendedDataAsync(
        ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.ChannelExtendedData);
        uint recipient = reader.ReadUInt32();
        uint dataTypeCode = reader.ReadUInt32();
        ReadOnlySequence<byte> data = reader.ReadString(int.MaxValue);

        SshChannel? channel = Lookup(recipient);
        if (channel is null)
        {
            return;
        }

        if (!channel.OnExtendedData(dataTypeCode, data))
        {
            await FaultProtocolAsync(
                $"通道 {recipient} 的对端发来了超出我们宣告窗口的扩展数据。",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask OnChannelWindowAdjustAsync(
        ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.ChannelWindowAdjust);
        uint recipient = reader.ReadUInt32();
        uint bytes = reader.ReadUInt32();

        SshChannel? channel = Lookup(recipient);
        if (channel is not null && !channel.OnWindowAdjust(bytes))
        {
            await FaultProtocolAsync(
                $"通道 {recipient} 的窗口回补让计数溢出了 uint32 —— 对端在拿它撑爆我们的账。",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private void OnChannelOpenConfirmation(ReadOnlyMemory<byte> payload)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.ChannelOpenConfirmation);
        uint recipient = reader.ReadUInt32();
        uint senderChannel = reader.ReadUInt32();
        uint initialWindow = reader.ReadUInt32();
        uint maxPacket = reader.ReadUInt32();

        TaskCompletionSource<SshChannel>? completion;
        SshChannel? channel;
        lock (_stateLock)
        {
            _pendingOpens.Remove(recipient, out completion);
            _channels.TryGetValue(recipient, out channel);
        }

        if (completion is null || channel is null)
        {
            return;
        }

        channel.OnOpenConfirmed(senderChannel, initialWindow, maxPacket);
        completion.TrySetResult(channel);
    }

    private void OnChannelOpenFailure(ReadOnlyMemory<byte> payload)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.ChannelOpenFailure);
        uint recipient = reader.ReadUInt32();
        uint reasonCode = reader.ReadUInt32();
        string description = reader.ReadUtf8String(MaxFieldBytes);

        TaskCompletionSource<SshChannel>? completion;
        SshChannel? channel;
        string channelType = "session";
        lock (_stateLock)
        {
            _pendingOpens.Remove(recipient, out completion);
            if (_channels.TryGetValue(recipient, out channel))
            {
                channelType = channel.ChannelType;
            }
        }

        channel?.OnOpenFailed();
        completion?.TrySetException(
            SshChannelException.FromOpenFailure(channelType, reasonCode, description));
    }

    private void OnChannelClose(ReadOnlyMemory<byte> payload)
    {
        uint recipient = ReadRecipient(payload);
        SshChannel? channel = Lookup(recipient);
        if (channel is null)
        {
            return;
        }

        // CLOSE **必须双向**：收到对端的就得回一个（除非我们已经发过）。
        // 「要不要回」与入队在同一把锁里判定 —— 泵正要发的数据要么排在这个 CLOSE 前面，
        // 要么看到「已发」而不再发（见 SendIfAsync）。
        byte[] reply = new byte[5];
        reply[0] = (byte)SshMessageNumber.ChannelClose;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(1), channel.RemoteId);
        PostIf(reply, channel.OnClose);

        channel.OnCloseCompleted(SshChannelCloseReason.ClosedByPeer);
    }

    private void OnChannelRequest(ReadOnlyMemory<byte> payload)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.ChannelRequest);
        uint recipient = reader.ReadUInt32();
        string requestType = reader.ReadUtf8String(MaxFieldBytes);
        bool wantReply = reader.ReadBoolean();

        // 只把**类型相关的那一段**交给通道 —— 报文头(编号、通道号、类型、want_reply)
        // 的长度是变的(类型是个变长 string)，让下游自己去跳过它，
        // 迟早会有人按固定长度跳，然后把 exit-status 读成别的数。
        ReadOnlyMemory<byte> typeSpecific = payload[(int)reader.Consumed..];

        SshChannel? channel = Lookup(recipient);
        if (channel is null)
        {
            // 不存在的通道：不回。通道号要等双向 CLOSE 走完才回收，所以这里只剩对端违规一种可能；
            // 而回应答需要**对端的**通道号，我们手上只有自己的 —— 曾经就拿它去填，
            // FAILURE 落在对端一条不相干的通道上（velashell-docs/zh/ssh/spec/05 §8：忽略）。
            return;
        }

        bool handled = channel.OnPeerRequest(requestType, typeSpecific);

        if (!wantReply)
        {
            return;
        }

        // 认得就回 SUCCESS，不认得回 FAILURE —— 但**必须回**。
        // 沉默会让对端的 FIFO 队列永远错位。
        // 例外是我们已经发过 CLOSE：之后这条通道上不许再有任何报文（RFC 4254 §5.3），
        // 对端收到 CLOSE 也就不再等这个应答了。
        byte[] reply = new byte[5];
        reply[0] = (byte)(handled ? SshMessageNumber.ChannelSuccess : SshMessageNumber.ChannelFailure);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(1), channel.RemoteId);
        PostIf(reply, () => !channel.CloseSent);
    }

    private async ValueTask OnChannelRequestReplyAsync(
        ReadOnlyMemory<byte> payload, bool success, CancellationToken cancellationToken)
    {
        uint recipient = ReadRecipient(payload);
        SshChannel? channel = Lookup(recipient);

        if (channel is not null && !channel.OnRequestReply(success))
        {
            // 队列空时收到应答 = FIFO 失步。再往下走，后面每一个应答
            // 都会被安到错误的请求上，而那是**静默的**错误答案。
            await FaultProtocolAsync(
                $"通道 {recipient} 收到了没有对应请求的应答 —— 请求/应答的 FIFO 已经失步。",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private void OnGlobalRequest(ReadOnlyMemory<byte> payload)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.GlobalRequest);
        _ = reader.ReadUtf8String(MaxFieldBytes);
        bool wantReply = reader.ReadBoolean();

        if (!wantReply)
        {
            return;
        }

        // 〔重要 velashell-docs/zh/ssh/spec/05 §6.4〕对未知全局请求回 REQUEST_FAILURE 是**必须**的，
        // 不能沉默。沉默会让对端的 FIFO 队列永远错位 ——
        // 它下一个请求的应答会被认成这一个的。
        byte[] failure = [(byte)SshMessageNumber.RequestFailure];
        Post(failure);
    }

    private async ValueTask OnGlobalRequestReplyAsync(
        bool success, ReadOnlyMemory<byte> replyPayload, CancellationToken cancellationToken)
    {
        if (!_globalRequests.TryComplete(new SshGlobalRequestReply(success, replyPayload)))
        {
            await FaultProtocolAsync(
                "收到了没有对应全局请求的应答 —— FIFO 已经失步。", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>同时在「问处理器要不要接」的对端开通道请求上限，超出的直接以资源不足拒绝。</summary>
    internal const int MaxPeerOpensDeciding = 64;

    /// <summary>正在后台问处理器的对端开通道请求数。</summary>
    private int _peerOpensDeciding;

    /// <remarks>
    /// <para>
    /// ⚠️ <b>问处理器这一步不在接收循环上等。</b>处理器是使用者写的，它的 <c>GetOptionsAsync</c>
    /// 可能要弹窗问人、查配置、连别的东西 —— 曾经就地 await，它一慢，整条连接上所有通道的收包都停住。
    /// 现在解析与查处理器在这里做完，问与接受交给后台：接收循环立刻回去收包。
    /// </para>
    /// <para>
    /// 对端在我们确认之前不能往这条通道上发任何东西，所以后台完成的确认与接收循环之间没有顺序问题；
    /// 两条开通道请求的确认谁先谁后也不要紧 —— 各自带着对端的通道号。
    /// 同时在问的请求有上限（<see cref="MaxPeerOpensDeciding"/>）：没有的话，对端刷一堆开通道请求、
    /// 处理器又慢，就是一堆挂着的任务。
    /// </para>
    /// </remarks>
    private ValueTask OnPeerChannelOpenAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.ChannelOpen);
        string channelType = reader.ReadUtf8String(MaxFieldBytes);
        uint senderChannel = reader.ReadUInt32();
        uint initialWindow = reader.ReadUInt32();
        uint maxPacket = reader.ReadUInt32();

        // ⚠️ **必须复制。**载荷背后是传输的接收缓冲，下一次读包就会被覆盖；
        //    而处理器是在另一个任务上、在接收循环读了别的报文之后才用它。
        ReadOnlyMemory<byte> typeSpecific = payload[(int)reader.Consumed..].ToArray();

        IIncomingChannelHandler[] candidates;
        lock (_stateLock)
        {
            candidates = _incomingHandlers.TryGetValue(channelType, out List<IIncomingChannelHandler>? list)
                ? [.. Enumerable.Reverse(list)]
                : [];
        }

        if (candidates.Length == 0)
        {
            // 没人处理这种类型。**必须明确拒绝**，不能沉默 —— 沉默会让对端一直等着。
            PostOpenFailure(
                senderChannel,
                SshChannelOpenFailureReason.UnknownChannelType,
                $"本端没有登记 {channelType} 的处理器。");
            return default;
        }

        if (Interlocked.Increment(ref _peerOpensDeciding) > MaxPeerOpensDeciding)
        {
            Interlocked.Decrement(ref _peerOpensDeciding);
            PostOpenFailure(
                senderChannel,
                SshChannelOpenFailureReason.ResourceShortage,
                $"同时在决定的开通道请求已达上限 {MaxPeerOpensDeciding}。");
            return default;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await AcceptPeerChannelAsync(
                            channelType, senderChannel, initialWindow, maxPacket, typeSpecific, candidates,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 会话在收工（取消），或者会话已经坏了（Post 不再发）。单条开通道请求没有别的补救。
                }
                finally
                {
                    Interlocked.Decrement(ref _peerOpensDeciding);
                }
            },
            CancellationToken.None);
        return default;
    }

    /// <summary>问处理器要不要接，接的话建通道、回确认、交给处理器（后台执行，见 <see cref="OnPeerChannelOpenAsync"/>）。</summary>
    private async Task AcceptPeerChannelAsync(
        string channelType,
        uint senderChannel,
        uint initialWindow,
        uint maxPacket,
        ReadOnlyMemory<byte> typeSpecific,
        IIncomingChannelHandler[] candidates,
        CancellationToken cancellationToken)
    {
        // 后登记的先问；第一个同意的接走。「不归我」用异常表达（见接口说明）。
        IIncomingChannelHandler? handler = null;
        SshChannelOptions options = SshChannelOptions.Default;
        string refusal = "";
        foreach (IIncomingChannelHandler candidate in candidates)
        {
            try
            {
                options = await candidate.GetOptionsAsync(channelType, typeSpecific, cancellationToken)
                    .ConfigureAwait(false);
                handler = candidate;
                break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 处理器拒绝（例如远程转发的路由表里找不到这个绑定地址）。
                refusal = ex.Message;
            }
        }

        if (handler is null)
        {
            PostOpenFailure(senderChannel, SshChannelOpenFailureReason.AdministrativelyProhibited, refusal);
            return;
        }

        int window = options.WindowPolicy.InitialBytes;
        SshChannel? channel = null;

        lock (_stateLock)
        {
            if (_fault is null
                && _channels.Count < _limits.MaxChannels
                && _windowBudgetUsed + window <= _limits.SessionWindowBudgetBytes)
            {
                uint localId = AllocateChannelId();
                _windowBudgetUsed += window;
                channel = new SshChannel(this, localId, channelType, options);
                _channels[localId] = channel;
            }
        }

        if (channel is null)
        {
            // ⚠️ 处理器在 GetOptionsAsync 里可能已经占了资源（并发槽位）。
            //    这条通道不会走到 HandleAsync 了，得告诉它还回去 —— 否则每拒一次漏一个，
            //    漏满之后这一类通道就再也开不出来了。
            NotifyOpenAborted(handler, channelType, typeSpecific);
            PostOpenFailure(
                senderChannel,
                SshChannelOpenFailureReason.ResourceShortage,
                "本端的通道数或窗口预算已用尽。");
            return;
        }

        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteMessageNumber(SshMessageNumber.ChannelOpenConfirmation);
        writer.WriteUInt32(senderChannel);
        writer.WriteUInt32(channel.LocalId);
        writer.WriteUInt32((uint)window);
        writer.WriteUInt32((uint)options.ReceiveMaxPacketBytes);

        // 确认先入队，泵后启动 —— 泵发出的数据在队列里排在确认之后，
        // 对端一定先认得这条通道，再收到它的数据。
        Post(buffer.WrittenMemory);
        channel.OnOpenConfirmed(senderChannel, initialWindow, maxPacket);

        // 交给处理器时**不等它** —— 它多半要去连一个本地目标，
        // 而接收循环不能停在任何一条通道上。
        SshChannel accepted = channel;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await handler.HandleAsync(accepted, typeSpecific, _lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 单条通道的失败不影响会话 —— 通道是独立的失败域。
                }
                finally
                {
                    await accepted.DisposeAsync().ConfigureAwait(false);
                }
            },
            CancellationToken.None);
    }

    private static void NotifyOpenAborted(
        IIncomingChannelHandler handler, string channelType, ReadOnlyMemory<byte> typeSpecific)
    {
        try
        {
            handler.OnOpenAborted(channelType, typeSpecific);
        }
        catch (Exception)
        {
            // 处理器自己的善后出错，不该变成会话的问题。
        }
    }

    private void PostOpenFailure(uint senderChannel, SshChannelOpenFailureReason reason, string description)
    {
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteMessageNumber(SshMessageNumber.ChannelOpenFailure);
        writer.WriteUInt32(senderChannel);
        writer.WriteUInt32((uint)reason);

        // 描述里常常带着对端给的通道类型名（最长 64 KiB）—— 原样回显的话，
        // 每一个畸形的 CHANNEL_OPEN 都换来一个同样大的应答，对端拿它来撑爆我们的发送队列。
        writer.WriteUtf8String(description.Length <= MaxOpenFailureDescription
            ? description
            : description[..MaxOpenFailureDescription] + "…");
        writer.WriteUtf8String("");
        Post(buffer.WrittenMemory);
    }

    /// <summary>回 <c>CHANNEL_OPEN_FAILURE</c> 时描述的最大字符数。</summary>
    private const int MaxOpenFailureDescription = 256;

    /// <summary>回一个 <c>UNIMPLEMENTED</c>。</summary>
    /// <remarks>
    /// 里面要带<b>被拒那个报文的序号</b>（RFC 4253 §11.4），不是 0 ——
    /// 对端靠它知道是哪一个报文没被认出来。刚读完的那个报文用的是
    /// 「当前接收序号 − 1」（序号在取出一帧之后才推进）。
    /// </remarks>
    private void PostUnimplemented()
    {
        byte[] packet = new byte[5];
        packet[0] = (byte)SshMessageNumber.Unimplemented;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
            packet.AsSpan(1), unchecked(_transport.ReceiveSequenceNumber - 1));
        Post(packet);
    }

    // ------------------------------------------------------------ 内部

    private static uint ReadRecipient(ReadOnlyMemory<byte> payload)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadByte();
        return reader.ReadUInt32();
    }

    private SshChannel? Lookup(uint localId)
    {
        lock (_stateLock)
        {
            return _channels.GetValueOrDefault(localId);
        }
    }

    /// <summary>分配一个通道号，优先用「已经放够时间」的回收号。</summary>
    /// <remarks>调用时必须持有 <see cref="_stateLock"/>。</remarks>
    private uint AllocateChannelId()
    {
        long now = Environment.TickCount64;

        // 回收号要放够 ChannelIdReuseDelay 才能再用 —— 对端可能还在路上
        // 发这个号的数据，复用得太早会让那些数据投递到新通道上（串话）。
        if (_recycledIds.TryPeek(out (uint Id, long ReusableAtTicks) head) && head.ReusableAtTicks <= now)
        {
            _recycledIds.Dequeue();
            return head.Id;
        }

        return _nextChannelId++;
    }

    /// <remarks>调用时必须持有 <see cref="_stateLock"/>。</remarks>
    private void RecycleChannelId(uint localId) =>
        _recycledIds.Enqueue((localId, Environment.TickCount64 + (long)_limits.ChannelIdReuseDelay.TotalMilliseconds));

    /// <summary>对端违反了协议：先把 <c>DISCONNECT</c> 送出去，再把会话判死。</summary>
    /// <remarks>
    /// ⚠️ <b>顺序不能反。</b>先判死的话，发送路径第一步就会因为「已经故障」而拒绝，
    /// <c>DISCONNECT</c> 永远到不了对端 —— 对端只看到连接莫名其妙地断了，
    /// 它的日志里也就没有那句指得到原因的话。
    /// </remarks>
    private async ValueTask FaultProtocolAsync(string message, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _fault) is not null)
        {
            return;
        }

        await TrySendDisconnectAsync(SshDisconnectReason.ProtocolError, message, cancellationToken)
            .ConfigureAwait(false);
        Fault(new SshProtocolException(SshPhase.Open, message));
    }

    /// <summary>尽力发一个 <c>DISCONNECT</c>，最多等 <see cref="DisconnectFlushTimeout"/>。</summary>
    /// <remarks>
    /// 有期限是因为这时链路多半已经不太好了：发不出去不是新问题，
    /// 为它把接收循环挂住才是。
    /// </remarks>
    private async ValueTask TrySendDisconnectAsync(
        SshDisconnectReason reason, string description, CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteMessageNumber(SshMessageNumber.Disconnect);
        writer.WriteUInt32((uint)reason);
        writer.WriteUtf8String(description);
        writer.WriteUtf8String("");

        try
        {
            await SendControlAsync(buffer.WrittenMemory, cancellationToken).AsTask()
                .WaitAsync(DisconnectFlushTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 已经在断开的路上了。
        }
    }

    /// <summary>发 <c>DISCONNECT</c> 时最多等多久。</summary>
    private static readonly TimeSpan DisconnectFlushTimeout = TimeSpan.FromSeconds(2);

    /// <summary>把会话的故障原因归成公开的异常类型。</summary>
    /// <remarks>
    /// <para>
    /// 接收循环与发送泵接到的异常五花八门：套接字的 <see cref="IOException"/>、帧格式与完整性校验的
    /// <see cref="Crypto.SshFrameFormatException"/>、解析报文时的内部格式异常。它们会被原样抛给使用者
    /// （连接上的每个调用、每条通道的读者），而使用者按 <see cref="SshException"/> 与它的
    /// <see cref="SshException.Reason"/> 分流（重连只该对「断了」生效）。原样抛出的话，
    /// 一个内部类型使用者连 catch 都 catch 不到，宿主的异常翻译也认不出它。
    /// </para>
    /// <para>
    /// 归法：I/O 失败与「报文中途断开」→ <see cref="SshFailureReason.ClosedByPeer"/>；
    /// 帧格式、完整性校验、解析失败 → 协议错误；本来就是 <see cref="SshException"/> 的原样；
    /// 取消与释放原样（那是本端在收工，不是故障）；其它意外包一层，原异常留在 InnerException 里。
    /// </para>
    /// </remarks>
    private static Exception NormalizeFault(Exception exception) => exception switch
    {
        SshException or OperationCanceledException or ObjectDisposedException => exception,
        Crypto.SshFrameFormatException { PeerClosedMidPacket: true } =>
            new SshConnectionClosedException(
                SshFailureReason.ClosedByPeer, SshPhase.Open, $"连接断了：{exception.Message}", exception),
        IOException or System.Net.Sockets.SocketException =>
            new SshConnectionClosedException(
                SshFailureReason.ClosedByPeer, SshPhase.Open, $"连接断了：{exception.Message}", exception),
        Crypto.SshFrameFormatException or SshWireFormatException =>
            new SshProtocolException(SshPhase.Open, $"对端违反了协议：{exception.Message}", exception),
        _ => new SshConnectionClosedException(
            SshFailureReason.Unknown, SshPhase.Open, $"连接因意外错误中断：{exception.Message}", exception),
    };

    /// <summary>把会话判死：记下原因、放出信号、叫醒所有在等的人、停下收发。</summary>
    /// <remarks>
    /// <para>
    /// <b>判死就要真的停下来。</b>只记一个「已故障」而让接收循环接着读、
    /// 通道接着开着，会让会话挂在一个「<see cref="IsAlive"/> 是假、却还在收数据」的
    /// 半死状态里 —— 通道的读者等不到结尾，保活判死之后也一样。
    /// </para>
    /// <para>幂等：第一个原因胜出，后面的都是它的连带结果。</para>
    /// <para>
    /// 原因先归一成公开的 <see cref="SshException"/>（见 <see cref="NormalizeFault"/>）再记下 ——
    /// 它会被原样抛给这条连接上的每一个调用者、每一条通道的读者。
    /// </para>
    /// </remarks>
    private void Fault(Exception exception)
    {
        exception = NormalizeFault(exception);

        lock (_stateLock)
        {
            if (_fault is not null)
            {
                return;
            }
            Volatile.Write(ref _fault, exception);
        }

        // 先放出「断了」这个信号，再去收拾等在里面的人 ——
        // 顺序反过来的话，被唤醒的调用方回头去看 Disconnected，会看到它还没取消。
        SignalDisconnected();

        // 还在等开通道的人不会等到应答了。让它们拿到原因，而不是永远挂着。
        foreach (TaskCompletionSource<SshChannel> pending in TakePendingOpens())
        {
            pending.TrySetException(exception);
        }

        _globalRequests.Close(new SshGlobalRequestReply(false, ReadOnlyMemory<byte>.Empty));

        // 停下接收循环、发送泵、保活与重协商监视 —— 它们都挂在这一个令牌上。
        // 回调放到线程池上执行：Fault 常常跑在接收循环或发送泵上，同步执行回调会把
        // 使用者的续体拉到这两个线程上来（见 SshChannel.FinishClose 的说明）。
        Lifecycle.CancelInBackground(_lifetime);

        CloseAllChannels(exception);
    }

    private List<TaskCompletionSource<SshChannel>> TakePendingOpens()
    {
        lock (_stateLock)
        {
            List<TaskCompletionSource<SshChannel>> pending = [.. _pendingOpens.Values];
            _pendingOpens.Clear();
            return pending;
        }
    }

    private static ObjectDisposedException DisposedReason() =>
        new(nameof(SshConnection), "连接已释放，通道随之关闭。");

    /// <summary>会话没了：每条通道跟着收尾，还没读完的读端拿到 <paramref name="reason"/>。</summary>
    private void CloseAllChannels(Exception reason)
    {
        SshChannel[] channels;
        lock (_stateLock)
        {
            channels = [.. _channels.Values];
        }

        foreach (SshChannel channel in channels)
        {
            channel.OnSessionClosed(reason);
        }
    }

    /// <summary>
    /// 放出 <see cref="Disconnected"/>。幂等，而且<b>绝不让回调里的异常冒出来</b> ——
    /// 这条路径上正在处理的是「连接已经没了」，再被一个订阅者的异常打断，
    /// 剩下的清理就做不完了。
    /// </summary>
    /// <remarks>
    /// <see cref="Disconnected"/> 上登记的是<b>使用者的</b>回调（重连、改状态栏）——
    /// 它们在线程池上执行，而不是在调用这里的接收循环 / 发送泵 / 保活线程上。
    /// <see cref="CancellationToken.IsCancellationRequested"/> 仍然是当场变真的。
    /// </remarks>
    private void SignalDisconnected()
    {
        Task? signal = Lifecycle.CancelInBackground(_disconnected);
        if (signal is not null)
        {
            Interlocked.CompareExchange(ref _disconnectedSignal, signal, null);
        }
    }

    /// <summary><see cref="Disconnected"/> 的回调还在线程池上跑着的那个任务；释放前要等它。</summary>
    private Task? _disconnectedSignal;

    private void ThrowIfFaulted()
    {
        Exception? fault = Volatile.Read(ref _fault);
        if (fault is not null)
        {
            throw fault;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        SignalDisconnected();

        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 释放路径不抛。
        }

        _globalRequests.Close(new SshGlobalRequestReply(false, ReadOnlyMemory<byte>.Empty));

        // 还在等开通道的人等不到应答了。Fault 那条路径会结算它们，而释放不走 Fault ——
        // 曾经就漏在这里：关标签页时正在开的 SFTP / 隧道，不带令牌的话就永远挂着。
        foreach (TaskCompletionSource<SshChannel> pending in TakePendingOpens())
        {
            pending.TrySetException(new SshConnectionClosedException(
                SshFailureReason.Aborted, SshPhase.Open, "连接已释放，通道没有打开。"));
        }

        // 本端释放的：读端拿到 ObjectDisposedException —— 使用者据此分得清「自己拆的」与「断线」。
        CloseAllChannels(DisposedReason());

        foreach (Task? loop in new[] { _receiveLoop, _keepAliveLoop, _rekeyMonitorLoop, _sendPump })
        {
            if (loop is null)
            {
                continue;
            }

            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 同上。
            }
        }

        await _transport.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();

        // 放在最后：上面那些收尾里还可能有人去读 Disconnected。
        // 它的回调在线程池上跑（SignalDisconnected），先等它们跑完再释放。
        if (Volatile.Read(ref _disconnectedSignal) is { } signal)
        {
            try
            {
                await signal.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 使用者的回调出了错或者太慢 —— 不是释放路径该管的事。
            }
        }

        _disconnected.Dispose();
    }
}
