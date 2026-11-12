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
/// <para>
/// 实现上连接本身管生命周期（判死、释放）、发送路径与重协商；接收循环与收包分发（<see cref="InboundDispatcher"/>）、
/// 通道表（<see cref="ChannelTable"/>）、全局请求账本（<see cref="GlobalRequestLedger"/>）、保活循环（<see cref="KeepAliveMonitor"/>）
/// 与失败的归类（<see cref="ConnectionFaults"/>）是它的协作者。
/// </para>
/// </remarks>
public sealed partial class SshConnection : ISshChannelHost, IAsyncDisposable
{
    /// <summary>对端给的变长字段（请求类型、描述…）最多读这么长。</summary>
    internal const int MaxFieldBytes = 64 * 1024;

    private readonly SshPacketTransport _transport;
    private readonly SshConnectionLimits _limits;
    private readonly Lock _stateLock = new();

    /// <summary>通道表：占着号的通道（含正在打开的）、号的分配回收、窗口总预算、入站处理器。共用 <see cref="_stateLock"/>。</summary>
    private readonly ChannelTable _channelTable;

    /// <summary>全局请求与它们的应答账本（<b>同样没有 id，靠 FIFO 对齐</b>）。</summary>
    private readonly GlobalRequestLedger _globalRequests;

    /// <summary>接收循环与收包分发。</summary>
    private readonly InboundDispatcher _dispatcher;

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

    private Exception? _fault;
    private bool _disposed;

    /// <summary>在一条已完成认证的传输上建立会话。</summary>
    /// <param name="transport">传输。</param>
    /// <param name="kex">首次密钥交换的结果：会话标识、协商出的算法、主机公钥。</param>
    /// <param name="limits">通道限额。</param>
    /// <remarks>
    /// <c>internal</c>：公开的入口只有 <see cref="ConnectAsync"/>。从外面拿一条裸传输拼出来的连接
    /// 没有重协商所需的上下文，<see cref="StartRekeyAsync"/> 一调就抛。
    /// </remarks>
    internal SshConnection(SshPacketTransport transport, SshKeyExchangeResult kex, SshConnectionLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(kex);
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _sessionId = kex.SessionId;
        SessionProof = kex.CreateSessionProof();
        Algorithms = kex.Algorithms;
        HostKey = kex.HostKey;
        // 只有 OpenSSH 才发 eow@openssh.com：有的实现收到不认识的通道请求会直接断开（RFC 4254 §5.4 不许，但确实有）。
        PeerAcceptsEndOfWrite = kex.ServerVersion.StartsWith("SSH-2.0-OpenSSH_", StringComparison.Ordinal);
        _limits = limits ?? SshConnectionLimits.Default;
        Disconnected = _disconnected.Token;

        _channelTable = new ChannelTable(this, _transport, _stateLock, _limits);
        _globalRequests = new GlobalRequestLedger(this);
        _dispatcher = new InboundDispatcher(this, _transport, _channelTable, _globalRequests);

        // 发送泵从一开始就要在 —— 打开通道之前、重协商之前，任何出站都经过它。
        // 直接调用而不是 Task.Run：它同步跑到第一个 await（等队列）就让出。
        _sendPump = SendPumpAsync(_lifetime.Token);
    }

    private readonly byte[] _sessionId;

    /// <inheritdoc />
    bool ISshChannelHost.PeerAcceptsEndOfWrite => PeerAcceptsEndOfWrite;

    /// <summary>对端是 OpenSSH（按版本标识串判断），可以发 <c>eow@openssh.com</c>。</summary>
    internal bool PeerAcceptsEndOfWrite { get; }

    /// <summary>会话标识（首次密钥交换的交换哈希 H，RFC 4253 §7.2），整条连接不变。</summary>
    /// <remarks>
    /// 只交出只读视图：重协商拿的就是这份字节去算哈希，交出可写的数组等于让调用方能改坏下一次重协商。
    /// </remarks>
    public ReadOnlyMemory<byte> SessionId => _sessionId;

    /// <summary>首次密钥交换里服务端的身份证明 —— agent 转发给本机 agent 发会话声明时用（spec/07 §7.4）。</summary>
    internal Keys.SshSessionProof SessionProof { get; }

    /// <summary>这条会话实际协商出来的算法。</summary>
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
    /// <para>
    /// 重协商会把它整个换掉（算法可以谈出不一样的结果），所以读写都走
    /// <see cref="_stateLock"/> —— 它不是一个装完就不动的值。
    /// </para>
    /// </remarks>
    public Crypto.SshNegotiatedAlgorithms Algorithms
    {
        get
        {
            lock (_stateLock)
            {
                return field;
            }
        }

        private set;
    }

    /// <summary>服务端出示的主机公钥。</summary>
    public HostKeys.SshPublicKey HostKey { get; }

    /// <summary>对端的标识串（<c>SSH-2.0-OpenSSH_10.3</c> 这样），已按对端文本清洗。</summary>
    /// <remarks>曾经只出现在异常与主机密钥裁决的材料里；连接信息、排障要回答「对面是什么服务端、什么版本」。</remarks>
    public string PeerVersion { get; internal init; } = "";

    /// <summary>认证最终用哪种方法成功的（<c>publickey</c> / <c>password</c> / <c>keyboard-interactive</c> / <c>none</c>）。</summary>
    public string AuthenticationMethod { get; internal init; } = "";

    /// <summary>服务端在 <c>server-sig-algs</c> 里宣告的签名算法（RFC 8308 §3.1）；没宣告时为空。</summary>
    /// <remarks>公钥认证挑签名算法看的就是它 —— 「为什么这把 RSA 钥用的是 SHA-1」要从这里回答。</remarks>
    public IReadOnlyList<string> ServerSignatureAlgorithms { get; internal init; } = [];

    /// <summary>这条连接建立时各阶段用了多久。</summary>
    public SshConnectTimings ConnectTimings { get; internal init; }

    /// <summary>
    /// 主机密钥策略裁决「信任并记住」、记的时候却失败了的原因；记下了（或者没让记）时为 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/03 §5.4〕信任已经给了，这次连接照常进行 —— 与 OpenSSH 一样只是提醒，
    /// 下次连接还会再问。<see cref="HostKeys.KnownHostsPolicy"/> 写不进 known_hosts 时它是
    /// <see cref="SshFailureReason.HostKeyStoreFailed"/> 的 <see cref="SshConnectException"/>；
    /// 调用方自己的策略抛的异常原样放在这里。经跳板时只记在那一跳自己的连接上。
    /// </remarks>
    public Exception? HostKeyPersistFailure { get; internal init; }

    /// <summary>保活策略。</summary>
    internal SshKeepAlivePolicy KeepAlive { get; init; } = SshKeepAlivePolicy.Disabled;

    /// <summary>测试用：对端开过来的通道，确认入队之后、起泵之前调用（在决定开通道的后台任务上）。</summary>
    /// <remarks>
    /// 用例在这里停住，让接收循环先处理对端紧跟着确认发来的 <c>CLOSE</c> —— 把「后台任务恰好在两步之间被抢占」这一刻摆出来
    /// （velashell-docs/zh/ssh/spec/05 §8.1）。
    /// </remarks>
    internal Action<SshChannel>? AfterIncomingOpenConfirmationQueued { get; set; }

    /// <summary>保活、重协商（时间阈值、期限）与通道号复用延迟用的时钟；建连时取 <see cref="SshConnectionOptions.TimeProvider"/>。</summary>
    /// <remarks>
    /// 只有测试会换成手动拨的时钟 —— 「复用延迟刚好到点」「闲了刚好一个保活间隔」这种时刻靠真实时钟摆不出来。
    /// 曾经这几处直接用 <c>Environment.TickCount64</c> / <c>Task.Delay</c>，只能拿真实时间去等、去碰运气。
    /// </remarks>
    internal TimeProvider Time
    {
        get;
        init
        {
            field = value ?? throw new ArgumentNullException(nameof(Time));

            // 两个基准时刻跟着换成这个时钟的口径（字段初值是按系统时钟取的）。
            long now = value.GetTimestamp();
            _lastInboundAt = now;
            _lastKexAt = now;
        }
    } = TimeProvider.System;

    /// <summary>给人看的描述（<c>user@host:port</c>），进日志与异常。</summary>
    internal string Description { get; init; } = "";

    /// <summary>标着 <c>always_display</c> 的 <c>SSH_MSG_DEBUG</c> 的回调（<see cref="SshConnectionOptions.DebugMessageHandler"/>；由 <see cref="InboundDispatcher"/> 在线程池上交给它）。</summary>
    internal Func<string, CancellationToken, ValueTask>? DebugMessageHandler { get; init; }

    /// <summary>度量的 <c>host</c> 标签（逻辑目标）；<see langword="null"/>（测试里直接建的连接）时不记度量。</summary>
    internal string? MetricsHost { get; init; }

    /// <inheritdoc />
    string? ISshChannelHost.MetricsHost => MetricsHost;

    /// <summary>活着的连接数记没记上：0 还没记，1 记了 +1，2 收尾了（之后不再记）。</summary>
    private int _activeMetered;

    /// <summary>上一次收到任何入站报文的时刻（<see cref="Time"/> 的时间戳）。</summary>
    private long _lastInboundAt = TimeProvider.System.GetTimestamp();
    private int _rekeyCount;
    private int _sendGateOpensPosted;

    /// <summary>我们发出、还没被一次交换用掉的 <c>KEXINIT</c>（<see cref="_kexInProgress"/> 为假时才可能非空）。</summary>
    private byte[]? _ourPendingKexInit;

    /// <summary>一次重协商正在接收循环上跑（从对端的 <c>KEXINIT</c> 到开闸）。</summary>
    /// <remarks>与 <see cref="_ourPendingKexInit"/> 一起在 <c>_stateLock</c> 里读写。</remarks>
    private bool _kexInProgress;
    private long _bytesAtLastKex;
    private long _bytesReceivedAtLastKex;
    private long _packetsAtLastKex;
    private long _packetsReceivedAtLastKex;
    /// <summary>上一次密钥交换完成的时刻（<see cref="Time"/> 的时间戳）。</summary>
    private long _lastKexAt = TimeProvider.System.GetTimestamp();
    private SshRekeyCause? _lastRekey;

    /// <summary>当前占着通道号的通道数。</summary>
    /// <remarks>
    /// <b>正在打开、还没等到对端确认的那条也算在内</b> —— 通道号从发出
    /// <c>CHANNEL_OPEN</c> 起就已经占用，限额也按这个数算。
    /// </remarks>
    public int ChannelCount => _channelTable.Count;

    /// <summary>这条连接上此刻开着的通道（快照：类型、状态、开通时刻、字节数），按通道号排。</summary>
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/05 §一〕连接信息里列「这条连接上开着哪些 shell、隧道、SFTP」用它。交出的是快照而不是通道本身 ——
    /// 通道归开它的那一方，别人拿到手就能关掉它。正在打开的那条也在里面（与 <see cref="ChannelCount"/> 一致）。
    /// 曾经只有一个数。
    /// </remarks>
    public IReadOnlyList<SshChannelSnapshot> Channels => _channelTable.Snapshot();

    /// <summary>会话是否还活着。</summary>
    public bool IsAlive => Volatile.Read(ref _fault) is null && !_disposed;

    /// <summary>
    /// 在敏感通道打开之前取得连接级诊断载荷抑制；最后一个租约结束后仅恢复原诊断配置。
    /// </summary>
    /// <remarks>
    /// 依据 velashell-docs/zh/ssh/spec/08-failures.md §9.1。调用方须持有至通道在途收发及双向关闭完成；
    /// 取消、进程退出或通道 DisposeAsync 返回不等于此边界。租约重复释放安全。
    /// </remarks>
    public IAsyncDisposable SuppressSensitivePayload()
    {
        ObjectDisposedException.ThrowIf(!IsAlive, this);
        return _transport.AcquireSensitivePayloadLease();
    }

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
    /// <para>
    /// 令牌在构造时就取好并留住，而不是每次读 <c>_disconnected.Token</c> ——
    /// 那个属性在源被释放之后会抛 <see cref="ObjectDisposedException"/>，
    /// 而「连接已经释放了」恰恰是最常去读这个令牌的时刻。
    /// </para>
    /// </remarks>
    public CancellationToken Disconnected { get; }

    /// <summary>连接结束时完成，结果是结束的原因。</summary>
    /// <remarks>
    /// <para>
    /// 〔velashell-docs/zh/ssh/spec/08 §2.1〕断线时是那次故障本身：保活超时（<c>KeepAliveTimeout</c>）、对端关闭（<c>ClosedByPeer</c>）、
    /// 收到 <c>DISCONNECT</c>（<see cref="SshConnectionClosedException"/> 带着原因码与对端原话）、协议错误……；
    /// 本端释放时是一个 <see cref="SshFailureReason.Aborted"/> 的 <see cref="SshConnectionClosedException"/>。
    /// </para>
    /// <para>
    /// <b>以结果完成，从不以异常完成</b>：没人等它时也不会变成未观察的任务异常。
    /// <see cref="Disconnected"/> 只说「到此为止」，原因在这里 —— 曾经没有这个入口，使用者挂 <see cref="Disconnected"/>
    /// 只能一律报「对端关闭」，或者从读管道的结束方式去反推。
    /// </para>
    /// </remarks>
    public Task<SshException> Completion => _completion.Task;

    /// <summary>连接是怎么结束的；还没结束时为 <see langword="null"/>。与 <see cref="Completion"/> 的结果相同。</summary>
    public SshException? CloseReason => _completion.Task.IsCompleted ? _completion.Task.Result : null;

    private readonly TaskCompletionSource<SshException> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>记下结束原因（第一个胜出）。故障不是 <see cref="SshException"/>（取消、释放）时换成一个说得清的。</summary>
    private void Complete(Exception reason)
    {
        _completion.TrySetResult(reason as SshException ?? new SshConnectionClosedException(
            SshFailureReason.Aborted, SshPhase.Open, $"连接中止了：{reason.Message}", reason));

        if (Interlocked.Exchange(ref _activeMetered, 2) == 1 && MetricsHost is { } host)
        {
            SshMetrics.ConnectionsActive.Add(-1, SshMetrics.HostTag(host));
        }
    }

    /// <summary>开始收包。</summary>
    /// <remarks>必须在打开任何通道之前调用一次。</remarks>
    internal void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _receiveLoop ??= Task.Run(() => _dispatcher.RunAsync(_lifetime.Token));

        if (MetricsHost is { } host && Interlocked.CompareExchange(ref _activeMetered, 1, 0) == 0)
        {
            SshMetrics.ConnectionsActive.Add(1, SshMetrics.HostTag(host));
        }

        if (KeepAlive.IsEnabled)
        {
            _keepAliveLoop ??= Task.Run(() => new KeepAliveMonitor(this, _transport, _globalRequests).RunAsync(_lifetime.Token));
        }

        // 只有工厂建的连接才谈得上主动发起重协商（要用 RekeyContext）。
        // 不看策略开没开：报文数的硬线（见 RekeyHardPacketLimit）关不掉，监视循环总要在。
        if (RekeyContext is not null)
        {
            _rekeyMonitorLoop ??= Task.Run(() => RekeyMonitorLoopAsync(_lifetime.Token));
        }
    }

    /// <summary>上一次收到任何入站报文的时刻（<see cref="Time"/> 的时间戳）：保活的计时基准（见 <see cref="KeepAliveMonitor"/>）。</summary>
    internal long LastInboundAt => Volatile.Read(ref _lastInboundAt);

    /// <summary>最近一次测到的往返时间（ticks）；0 表示还没测过。</summary>
    private long _lastRoundTripTicks;

    /// <summary>
    /// 最近一次测到的往返时间：保活探测（链路闲下来时自动发）或 <see cref="MeasureRoundTripAsync"/> 从入队到收到应答；
    /// 还没测过时为 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/05 §6.3〕量的是整条路径（经代理、跳板也一样），含服务端处理一个全局请求的时间 ——
    /// 比 ICMP ping 更接近用户感到的「卡」，也不怕目标禁 ICMP。通道正在大量收发时，探测排在数据后面，量到的会偏大。
    /// </remarks>
    public TimeSpan? LastRoundTrip => Volatile.Read(ref _lastRoundTripTicks) is var ticks and > 0
        ? TimeSpan.FromTicks(ticks)
        : null;

    internal void RecordRoundTrip(TimeSpan elapsed) =>
        Volatile.Write(ref _lastRoundTripTicks, Math.Max(1, elapsed.Ticks));

    /// <summary>量一次到服务端的往返时间：发一个要应答的保活全局请求，从入队到收到应答。</summary>
    /// <returns>往返时间（同时记进 <see cref="LastRoundTrip"/>）。</returns>
    /// <exception cref="SshException">连接已经断了。</exception>
    /// <exception cref="OperationCanceledException">取消。</exception>
    /// <remarks>服务端回成功还是失败都算（它多半不认这个请求类型），有应答就是到过一个来回。</remarks>
    public async ValueTask<TimeSpan> MeasureRoundTripAsync(CancellationToken cancellationToken = default)
    {
        // 对端认 PING 就用它：传输层的回声，不经服务端的全局请求处理，量得更准。
        if (PeerSupportsPing)
        {
            return await PingAsync(cancellationToken).ConfigureAwait(false);
        }

        long startedAt = Time.GetTimestamp();
        await SendGlobalRequestAsync(
            SshProtocolNames.KeepAliveOpenSsh, default, wantReply: true, cancellationToken).ConfigureAwait(false);
        TimeSpan elapsed = Time.GetElapsedTime(startedAt);
        RecordRoundTrip(elapsed);
        return elapsed;
    }

    // ------------------------------------------------------------ 通道

    /// <summary>打开一条 <c>session</c> 通道。</summary>
    public ValueTask<SshChannel> OpenSessionChannelAsync(
        SshChannelOptions? options = null, CancellationToken cancellationToken = default) =>
        OpenChannelAsync(SshProtocolNames.ChannelSession, default, options, cancellationToken);

    /// <summary>打开一条通道。</summary>
    /// <param name="channelType">通道类型。</param>
    /// <param name="typeSpecificPayload">类型相关的附加字段。</param>
    /// <param name="options">通道参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="SshChannelException">对端拒绝，或本端限额已满。</exception>
    internal async ValueTask<SshChannel> OpenChannelAsync(
        string channelType,
        ReadOnlyMemory<byte> typeSpecificPayload = default,
        SshChannelOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(channelType);

        // 限额、预算、「发 CHANNEL_OPEN 之前先登记」与取消后的善后，见 ChannelTable.OpenAsync。
        return await _channelTable.OpenAsync(channelType, typeSpecificPayload, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>这条连接上所有 X11 转发共用的入口（按假 cookie 分通道）。</summary>
    internal Forwarding.X11ChannelRouter X11Router
    {
        get
        {
            lock (_stateLock)
            {
                return field ??= new Forwarding.X11ChannelRouter(this);
            }
        }
    }

    /// <summary>给某一类型<b>再挂</b>一个处理器，不影响已有的。</summary>
    /// <remarks>
    /// 通道到达时<b>后登记的先问</b>：它的 <see cref="IIncomingChannelHandler.GetOptionsAsync"/>
    /// 抛异常（「这条不归我」）就问下一个，第一个同意的接走这条通道。
    /// 全都不要才拒绝，拒绝理由取最后一个问到的处理器给的。
    /// </remarks>
    internal void AddIncomingChannelHandler(string channelType, IIncomingChannelHandler handler) =>
        _channelTable.AddIncomingChannelHandler(channelType, handler);

    /// <summary>摘掉<b>这一个</b>处理器；别人登记的同类型处理器不受影响。</summary>
    internal void RemoveIncomingChannelHandler(string channelType, IIncomingChannelHandler handler) =>
        _channelTable.RemoveIncomingChannelHandler(channelType, handler);

    // ------------------------------------------------------------ 全局请求

    /// <summary>发一个全局请求，把应答（含载荷）带回来。</summary>
    /// <returns><paramref name="wantReply"/> 为假时立刻返回一个成功的空应答。</returns>
    /// <remarks>
    /// 应答载荷要带回来：<c>tcpip-forward</c> 请求端口 0 时，服务端分配的实际端口就在
    /// <c>REQUEST_SUCCESS</c> 的载荷里（一个 <c>uint32</c>）—— 只回成不成的话那个端口号就永远拿不到了。
    /// 登记与入队同一时刻做、应答按 FIFO 对齐，见 <see cref="GlobalRequestLedger"/>。
    /// </remarks>
    internal async ValueTask<SshGlobalRequestReply> SendGlobalRequestAsync(
        string requestType,
        ReadOnlyMemory<byte> payload = default,
        bool wantReply = true,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await _globalRequests.SendAsync(requestType, payload, wantReply, cancellationToken).ConfigureAwait(false);
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
        return _globalRequests.SendAsync(requestType, payload, onReply, cancellationToken);
    }

    /// <summary>发一次保活探测。</summary>
    /// <remarks>
    /// <b>应答内容不重要，有应答就说明链路活着</b> —— 服务端回
    /// <c>REQUEST_FAILURE</c> 也算数（它本来就不认识这个请求类型）。
    /// </remarks>
    internal async ValueTask<bool> SendKeepAliveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await SendGlobalRequestAsync(
                SshProtocolNames.KeepAliveOpenSsh, default, wantReply: true, cancellationToken)
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
        ReadOnlyMemory<byte> packet, Func<bool> admit, bool isInteractive, CancellationToken cancellationToken) =>
        EnqueueAsync(packet, applyBackpressure: true, admit, cancellationToken, isInteractive: isInteractive);

    /// <inheritdoc />
    ValueTask ISshChannelHost.SendBorrowedIfAsync(
        ReadOnlyMemory<byte> packet, Func<bool> admit, bool isInteractive, CancellationToken cancellationToken) =>
        EnqueueAsync(packet, applyBackpressure: true, admit, cancellationToken, borrowed: true, isInteractive: isInteractive);

    /// <inheritdoc />
    bool ISshChannelHost.TryReserveWindowBudget(int bytes) => _channelTable.TryReserveWindowBudget(bytes);

    /// <inheritdoc />
    void ISshChannelHost.ReleaseWindowBudget(int bytes) => _channelTable.ReleaseWindowBudget(bytes);

    /// <inheritdoc />
    void ISshChannelHost.OnChannelClosed(uint localId, int windowBytes) => _channelTable.OnChannelClosed(localId, windowBytes);

    // ------------------------------------------------------------ 接收

    /// <summary>重协商期间读到的会话层报文就地派发（<see cref="RekeyKexTransport"/>）；接收循环与分发本身在 <see cref="InboundDispatcher"/>。</summary>
    private ValueTask DispatchAsync(SshInboundPacket packet, CancellationToken cancellationToken) =>
        _dispatcher.DispatchAsync(packet, cancellationToken);

    /// <summary>收到了一个入站报文：记下时刻（保活的计时基准是「上次收到任何报文」）。</summary>
    internal void NoteInbound() => Volatile.Write(ref _lastInboundAt, Time.GetTimestamp());

    // ------------------------------------------------------------ 内部

    /// <summary>对端违反了协议：先把 <c>DISCONNECT</c> 送出去，再把会话判死。</summary>
    /// <remarks>
    /// ⚠️ <b>顺序不能反。</b>先判死的话，发送路径第一步就会因为「已经故障」而拒绝，
    /// <c>DISCONNECT</c> 永远到不了对端 —— 对端只看到连接莫名其妙地断了，
    /// 它的日志里也就没有那句指得到原因的话。
    /// </remarks>
    internal async ValueTask FaultProtocolAsync(string message, CancellationToken cancellationToken)
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
    internal async ValueTask TrySendDisconnectAsync(
        SshDisconnectReason reason, string description, CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteMessageNumber(SshMessageNumber.Disconnect);
        writer.WriteUInt32((uint)reason);
        writer.WriteUtf8String(description);
        writer.WriteUtf8String("");

        Task sending = SendControlAsync(buffer.WrittenMemory, cancellationToken).AsTask();
        try
        {
            await sending.WaitAsync(DisconnectFlushTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 已经在断开的路上了。
            // 等不及（超时、被取消）时这一帧还在发送队列里：链路堵着（对端不读）正是会超时的那种情形，
            // 随后释放停下发送泵，它就以「连接已经关闭」失败 —— 那时已经没人等它了。
            // 把异常看掉：曾经它成了未观察的任务异常，每关一个堵住的连接，宿主的崩溃日志里就多一条。
            _ = sending.ContinueWith(
                static t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>发 <c>DISCONNECT</c> 时最多等多久。</summary>
    private static readonly TimeSpan DisconnectFlushTimeout = TimeSpan.FromSeconds(2);

    /// <summary>释放时等收发循环收工最多等多久（到点先释放传输再等一次，见 <see cref="DisposeAsync"/>）。</summary>
    private static readonly TimeSpan LoopShutdownTimeout = TimeSpan.FromSeconds(2);

    /// <summary>等 <paramref name="task"/> 结束，最多 <paramref name="timeout"/>；它自己失败了也算结束。不抛。</summary>
    private static async ValueTask<bool> CompletesWithinAsync(Task task, TimeSpan timeout)
    {
        try
        {
            await task.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (Exception)
        {
            return true;   // 循环自己的失败早已经由 Fault 交代过了
        }
    }

    /// <summary>把会话判死：记下原因、放出信号、叫醒所有在等的人、停下收发。</summary>
    /// <remarks>
    /// <para>
    /// <b>判死就要真的停下来。</b>只记一个「已故障」而让接收循环接着读、
    /// 通道接着开着，会让会话挂在一个「<see cref="IsAlive"/> 是假、却还在收数据」的
    /// 半死状态里 —— 通道的读者等不到结尾，保活判死之后也一样。
    /// </para>
    /// <para>幂等：第一个原因胜出，后面的都是它的连带结果。</para>
    /// <para>
    /// 原因先归一成公开的 <see cref="SshException"/>（见 <see cref="ConnectionFaults.NormalizeFault"/>）再记下 ——
    /// 它会被原样抛给这条连接上的每一个调用者、每一条通道的读者。
    /// </para>
    /// </remarks>
    internal void Fault(Exception exception)
    {
        exception = ConnectionFaults.NormalizeFault(exception);

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
        // 原因先记下：被唤醒的人可能马上就去看 CloseReason。
        _transport.StopPacketTap();
        Complete(exception);
        SignalDisconnected();

        // 还在等开通道的人不会等到应答了。让它们拿到原因，而不是永远挂着。
        foreach (TaskCompletionSource<SshChannel> pending in _channelTable.TakePendingOpens())
        {
            pending.TrySetException(exception);
        }

        _globalRequests.Close();

        // 停下接收循环、发送泵、保活与重协商监视 —— 它们都挂在这一个令牌上。
        // 回调放到线程池上执行：Fault 常常跑在接收循环或发送泵上，同步执行回调会把
        // 使用者的续体拉到这两个线程上来（见 SshChannel.FinishClose 的说明）。
        Lifecycle.CancelInBackground(_lifetime);

        // 会话没了：每条通道跟着收尾，还没读完的读端拿到这个原因。
        _channelTable.CloseAll(exception);
    }

    /// <summary>会话已经判死（<see cref="CurrentFault"/> 不为空）。</summary>
    internal bool IsFaulted => Volatile.Read(ref _fault) is not null;

    /// <summary>判死的原因（已经归一成公开的异常）；还没判死时为 <see langword="null"/>。</summary>
    internal Exception? CurrentFault => Volatile.Read(ref _fault);

    /// <summary>连接的生命周期令牌（释放或判死时取消）：交给入站通道的处理器。</summary>
    /// <remarks>与读 <c>_lifetime.Token</c> 一样：连接释放之后再读会抛 <see cref="ObjectDisposedException"/>。</remarks>
    internal CancellationToken LifetimeToken => _lifetime.Token;

    internal static ObjectDisposedException DisposedReason() =>
        new(nameof(SshConnection), "连接已释放，通道随之关闭。");

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

    /// <summary>连接已经释放或判死时抛 —— 与开通道、发全局请求时同一种异常（释放了是 <see cref="ObjectDisposedException"/>，判死了是那次故障）。</summary>
    internal void ThrowIfUnusable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfFaulted();
    }

    internal void ThrowIfFaulted()
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
        _transport.StopPacketTap();
        Complete(new SshConnectionClosedException(SshFailureReason.Aborted, SshPhase.Open, "本端释放了连接。"));
        SignalDisconnected();

        // 〔velashell-docs/zh/ssh/spec/08 §六〕正常收工：先限时冲刷已入队的帧、发 DISCONNECT(BY_APPLICATION)，再停收发。
        // DISCONNECT 排在发送队列的末尾，它出去了就说明前面的帧都出去了 —— 关标签页之前的最后一次输入不会丢；
        // 服务端日志里也有了原因。曾经直接取消：已入队的帧作废，对端只看到连接没了。
        if (Volatile.Read(ref _fault) is null)
        {
            await TrySendDisconnectAsync(
                SshDisconnectReason.ByApplication, ConnectionFaults.DisconnectDescription(SshDisconnectReason.ByApplication), CancellationToken.None)
                .ConfigureAwait(false);
        }

        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 释放路径不抛。
        }

        _globalRequests.Close();

        // 还在等开通道的人等不到应答了。Fault 那条路径会结算它们，而释放不走 Fault ——
        // 曾经就漏在这里：关标签页时正在开的 SFTP / 隧道，不带令牌的话就永远挂着。
        foreach (TaskCompletionSource<SshChannel> pending in _channelTable.TakePendingOpens())
        {
            pending.TrySetException(new SshConnectionClosedException(
                SshFailureReason.Aborted, SshPhase.Open, "连接已释放，通道没有打开。"));
        }

        // 本端释放的：读端拿到 ObjectDisposedException —— 使用者据此分得清「自己拆的」与「断线」。
        _channelTable.CloseAll(DisposedReason());

        // 等收发循环收工要有时限：底层流的读写不一定响应取消（Windows 上 ProxyCommand 的匿名管道
        // 在线程池上阻塞完成，architecture.md §11.2.19），曾经那时释放就一直挂着。
        // 到点先释放传输（关掉底层流，卡着的读写随之结束），再等一次。
        var loops = Task.WhenAll(
            new[] { _receiveLoop, _keepAliveLoop, _rekeyMonitorLoop, _sendPump }.OfType<Task>());
        if (!await CompletesWithinAsync(loops, LoopShutdownTimeout).ConfigureAwait(false))
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
            _ = await CompletesWithinAsync(loops, LoopShutdownTimeout).ConfigureAwait(false);
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
