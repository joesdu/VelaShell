// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §7.1  tcpip-forward / cancel-tcpip-forward
//   RFC 4254 §7.2  forwarded-tcpip
//   OpenSSH PROTOCOL §2.4  streamlocal-forward@openssh.com / forwarded-streamlocal@openssh.com
//   行为规格:      velashell-docs/zh/ssh/spec/07-forwarding.md §四、§五、§八

using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Session;

namespace VelaShell.Ssh.Forwarding;

/// <summary>远程转发（<c>-R</c>）的参数。</summary>
/// <remarks>〔FW-E16〕非法值在设值时就抛（AGENTS 4.3）。</remarks>
public sealed record RemotePortForwardOptions
{
    /// <summary>
    /// 请服务端绑哪个地址。
    /// </summary>
    /// <remarks>
    /// <c>""</c>、<c>"*"</c>、<c>"0.0.0.0"</c>、<c>"localhost"</c> 在服务端是
    /// <b>不同的语义</b>，所以这里原样传，不做任何规范化 —— 也因此是字符串而不是
    /// <see cref="IPAddress"/>（本地转发绑的是本机套接字，那边才是地址）。
    /// 默认 <c>"localhost"</c>：只有服务端本机能连，与 OpenSSH 的
    /// <c>GatewayPorts no</c> 一致。
    /// </remarks>
    public string BindAddress
    {
        get;
        init => field = value ?? throw new ArgumentNullException(nameof(BindAddress));
    } = "localhost";

    /// <summary>请服务端绑哪个端口。<c>0</c> 表示由服务端分配。</summary>
    /// <exception cref="ArgumentOutOfRangeException">不在 0–65535 之间。</exception>
    public int BindPort
    {
        get;
        init => field = value is >= 0 and <= 65535 ? value : throw new ArgumentOutOfRangeException(nameof(BindPort), value, "监听端口要在 0–65535 之间（0 由服务端分配）。");
    }

    /// <summary>并发连接数上限。</summary>
    /// <exception cref="ArgumentOutOfRangeException">小于 1。</exception>
    public int MaxConnections
    {
        get;
        init => field = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(MaxConnections), value, "并发连接数上限至少为 1。");
    } = 1024;

    /// <summary>每个方向每秒最多搬多少应用字节；<see langword="null"/>（默认）不限。这个转发器的全部回连共用这个额度。</summary>
    /// <exception cref="ArgumentOutOfRangeException">不为正。</exception>
    public long? MaxBytesPerSecond
    {
        get;
        init => field = value is null or > 0 ? value : throw new ArgumentOutOfRangeException(nameof(MaxBytesPerSecond), value, "限速必须为正；不限就给 null。");
    }

    /// <summary>每条隧道通道的参数。</summary>
    public SshChannelOptions Channel { get; init; } = SshChannelOptions.Default with
    {
        StderrMode = SshStderrMode.Discard,
    };

    /// <summary>远程动态转发的 SOCKS 握手时限：从回连确认起，到读完 <c>CONNECT</c> 请求为止（与本地动态转发同一个口径）。</summary>
    /// <exception cref="ArgumentOutOfRangeException">不为正。</exception>
    public TimeSpan SocksHandshakeTimeout
    {
        get;
        init => field = value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(SocksHandshakeTimeout), value, "SOCKS 握手时限必须为正。");
    } = TimeSpan.FromSeconds(30);

    /// <summary>释放时等服务端回「取消监听」的应答最多多久。</summary>
    /// <remarks>只有测试会调短它（见 <see cref="RemotePortForwarder.DisposeAsync"/>）。</remarks>
    internal TimeSpan CancelReplyTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>默认参数。</summary>
    public static RemotePortForwardOptions Default { get; } = new();
}

/// <summary>服务端监听、回连到本机目标的转发器（<c>-R</c>）。</summary>
/// <remarks>
/// 方向与 <see cref="LocalPortForwarder"/> 正好相反：<b>入站是服务端给的通道，
/// 出站是本机 TCP</b>。但搬运循环一模一样 —— 那正是把它放进基类的理由。
/// </remarks>
public sealed class RemotePortForwarder : PortForwarder, IIncomingChannelHandler
{
    private const int MaxFieldBytes = 64 * 1024;

    private readonly SshConnection _connection;
    private readonly RemotePortForwardOptions _options;
    private readonly string _targetHost;
    private readonly int _targetPort;

    /// <summary>Unix 套接字变体：本机要连过去的那个套接字路径。</summary>
    private readonly string? _targetSocketPath;
    private readonly SemaphoreSlim _connectionSlots;

    /// <summary>
    /// <c>GetOptionsAsync</c> 里连好的本机目标，等 <c>HandleAsync</c> 取走（或 <c>OnOpenAborted</c> 丢掉）。
    /// </summary>
    /// <remarks>
    /// 连的都是同一个目标，彼此可以互换 —— 不必与某一条通道对上号，只要「每放进一个，就恰好有一次
    /// <c>HandleAsync</c> 或 <c>OnOpenAborted</c> 来取」：连接对每一次成功的 <c>GetOptionsAsync</c> 恰好调用其中之一。
    /// </remarks>
    private readonly ConcurrentQueue<Socket> _readyTargets = new();

    /// <summary>这个转发器的一切连接都挂在它上面：释放时取消，搬运随之中止。</summary>
    /// <remarks>不释放它：回连的处理可能在释放之后才开始读它的令牌，而它没有要还的资源。</remarks>
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>取消之后、处理器摘掉之前的宽限期（velashell-docs/zh/ssh/spec/07 §4.3）。</summary>
    internal static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(2);

    private int _boundPort;

    // 监听请求的应答与「调用方不要了」谁先到，用一个原子状态排定（见 RequestAsync）。
    private const int RequestPending = 0;
    private const int RequestAnswered = 1;
    private const int RequestAbandoned = 2;
    private int _requestState;
    private bool _requestGranted;

    /// <summary>已经开始释放：服务端的监听已请求取消，宽限期里照常接在途的回连。</summary>
    private int _draining;

    /// <summary>宽限期过了，处理器已摘掉。</summary>
    private bool _disposed;

    private RemotePortForwarder(
        SshConnection connection,
        RemotePortForwardOptions options,
        string targetHost,
        int targetPort,
        int boundPort,
        string? remoteSocketPath = null,
        string? targetSocketPath = null,
        RemoteOpenPolicy? permitRemoteOpen = null)
        : base(permitRemoteOpen is null ? ForwardKind.Remote : ForwardKind.RemoteDynamic)
    {
        _connection = connection;
        _options = options;
        _targetHost = targetHost;
        _targetPort = targetPort;
        RemoteSocketPath = remoteSocketPath;
        _targetSocketPath = targetSocketPath;
        PermitRemoteOpen = permitRemoteOpen;
        ConfigureRateLimit(options.MaxBytesPerSecond);
        _boundPort = boundPort;
        _connectionSlots = new SemaphoreSlim(options.MaxConnections, options.MaxConnections);
    }

    /// <summary><c>tcpip-forward</c> 的应答到了（在接收循环上，见 <see cref="StartAsync"/>）。</summary>
    private void OnForwardReply(SshGlobalRequestReply reply)
    {
        // 端口给 0 时，实际端口在应答载荷里。取不到就留着 0 —— StartAsync 会据此报错。
        if (reply.Success && _options.BindPort == 0 && reply.Payload.Length >= 4)
        {
            Volatile.Write(ref _boundPort, (int)BinaryPrimitives.ReadUInt32BigEndian(reply.Payload.Span));
        }

        _requestGranted = reply.Success;
        if (Interlocked.CompareExchange(ref _requestState, RequestAnswered, RequestPending) == RequestAbandoned
            && reply.Success)
        {
            // 调用方已经不要了（StartAsync 被取消），服务端却开好了监听：撤掉它。
            CancelListenerInBackground();
        }
    }

    /// <summary>调用方不要这个转发了（监听请求已上线之后被取消或失败）。</summary>
    private void AbandonRequest()
    {
        if (Interlocked.CompareExchange(ref _requestState, RequestAbandoned, RequestPending) == RequestAnswered
            && _requestGranted)
        {
            // 应答已经到了、监听已经开着（取消与应答同时到达）：撤掉它。
            CancelListenerInBackground();
        }
    }

    /// <summary>
    /// 在后台请服务端取消监听，<c>want_reply = false</c>：不登记应答，也就不会挂在一个没人等的应答上。
    /// </summary>
    /// <remarks>
    /// 曾经监听请求上线之后被取消，只摘掉了本端的处理器：服务端回了 SUCCESS，它的监听就一直开到连接断开，
    /// 连进来的全被拒；固定端口紧接着重试，必报「端口已被占用」。
    /// </remarks>
    private void CancelListenerInBackground()
    {
        (string request, ReadOnlyMemory<byte> payload) = EncodeCancelRequest();
        _ = Task.Run(async () =>
        {
            try
            {
                await _connection.SendGlobalRequestAsync(request, payload, wantReply: false).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 连接没了，服务端的监听也随之没了。
            }
        });
    }

    /// <summary>取消监听的全局请求：名字与载荷（RFC 4254 §7.1；OpenSSH PROTOCOL 的 streamlocal 一节）。</summary>
    private (string Request, ReadOnlyMemory<byte> Payload) EncodeCancelRequest()
    {
        ArrayBufferWriter<byte> payload = new();
        SshDataWriter writer = new(payload);
        if (IsStreamLocal)
        {
            writer.WriteUtf8String(RemoteSocketPath!);
            return (SshProtocolNames.RequestCancelStreamLocalForward, payload.WrittenMemory);
        }

        writer.WriteUtf8String(_options.BindAddress);
        writer.WriteUInt32((uint)BoundPort);
        return (SshProtocolNames.RequestCancelTcpIpForward, payload.WrittenMemory);
    }

    /// <summary>这是不是 Unix 套接字变体。</summary>
    private bool IsStreamLocal => RemoteSocketPath is not null;

    /// <summary>本机目标的名字（进日志与事件）。动态转发的目标要等 SOCKS 握手才知道。</summary>
    private string TargetName => _targetSocketPath ?? (PermitRemoteOpen is null ? $"{_targetHost}:{_targetPort}" : "SOCKS");

    /// <summary>远程动态转发的放行名单；不是动态转发时为 <see langword="null"/>。</summary>
    public RemoteOpenPolicy? PermitRemoteOpen { get; }

    /// <summary>
    /// Unix 套接字变体里，服务端监听的那个套接字路径；TCP 变体下是
    /// <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/07 §4.4〕<c>streamlocal-forward@openssh.com</c> 与
    /// <c>tcpip-forward</c> 的差别只有三处：全局请求的名字、
    /// 把 <c>addr ‖ port</c> 换成一个 <c>string socket_path</c>、
    /// 以及回连走 <c>forwarded-streamlocal@openssh.com</c>。
    /// <b>计量、并发槽、搬运、事件、收尾全都一模一样</b> ——
    /// 所以这里用「这个属性是不是 null」分流，而不是再抄一份三百行。
    /// </remarks>
    public string? RemoteSocketPath { get; }

    /// <summary>这条转发在服务端那头的位置，给人看的。</summary>
    /// <remarks>
    /// TCP 变体是 <c>bind:port</c>，Unix 套接字变体是路径 ——
    /// 隧道面板要显示「这条转发开在哪」，两种形态得有一个统一的说法，
    /// 否则调用方就得自己写这个三目运算（架构原则 4）。
    /// </remarks>
    public string RemoteEndpointName => RemoteSocketPath ?? $"{_options.BindAddress}:{BoundPort}";

    /// <summary>服务端实际绑的端口。<b>请求端口 0 时，实际端口在这里。</b></summary>
    public int BoundPort => Volatile.Read(ref _boundPort);

    /// <summary>服务端绑的地址，原样。</summary>
    public string BindAddress => _options.BindAddress;

    /// <inheritdoc />
    /// <remarks>开始释放之后、或者 SSH 连接断了之后是 <see langword="false"/>。</remarks>
    public override bool IsActive =>
        Volatile.Read(ref _draining) == 0 && !_disposed && !_connection.Disconnected.IsCancellationRequested;

    /// <summary>请服务端开一个监听，把回连送到本机的某个目标。</summary>
    /// <param name="connection">会话。</param>
    /// <param name="targetHost">本机目标主机。</param>
    /// <param name="targetPort">本机目标端口。</param>
    /// <param name="options">参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="SshForwardException">服务端拒绝了这个请求。</exception>
    public static async ValueTask<RemotePortForwarder> StartAsync(
        SshConnection connection,
        string targetHost,
        int targetPort,
        RemotePortForwardOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrEmpty(targetHost);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetPort, 65535);

        RemotePortForwardOptions effective = options ?? RemotePortForwardOptions.Default;

        ArrayBufferWriter<byte> payload = new();
        SshDataWriter writer = new(payload);
        writer.WriteUtf8String(effective.BindAddress);
        writer.WriteUInt32((uint)effective.BindPort);

        // 端口给 0 时，实际端口由 OnForwardReply 在应答到达的当场记下。
        RemotePortForwarder forwarder = new(connection, effective, targetHost, targetPort, effective.BindPort);

        // want_reply 必须为 true。端口给 0 时，服务端分配的实际端口
        // 就在 REQUEST_SUCCESS 的载荷里 —— 不要应答就永远拿不到它。
        SshGlobalRequestReply reply = await forwarder
            .RequestAsync(SshProtocolNames.RequestTcpIpForward, payload.WrittenMemory, cancellationToken).ConfigureAwait(false);

        if (!reply.Success)
        {
            // 不留半挂的转发器。
            throw new SshForwardException(SshFailureReason.ForwardRejected,
                $"服务端拒绝在 {effective.BindAddress}:{effective.BindPort} 上开监听。" +
                "常见原因是 sshd_config 里 AllowTcpForwarding no，" +
                "或者要绑非环回地址而 GatewayPorts 没打开，" +
                (effective.BindPort is > 0 and < 1024 ? "又或者那是个特权端口。" : "又或者端口已被占用。"))
            {
                FailureKind = SshForwardFailureKind.RemoteListenRejected,
                Endpoint = $"{effective.BindAddress}:{effective.BindPort}",
                Port = effective.BindPort,
            };
        }

        if (forwarder.BoundPort == 0)
        {
            // 端口给 0 时必须从应答载荷里取实际端口。
            // 取不到就按 (bind_addr, 0) 去路由回连 —— 一条都对不上，
            // 而症状是「转发看起来建好了，但连过来的全被拒」。
            forwarder.Unregister();
            throw new SshForwardException(SshFailureReason.ProtocolError,
                "请求了动态端口，但服务端的 REQUEST_SUCCESS 里没有带回实际端口号。")
            {
                FailureKind = SshForwardFailureKind.NoBoundPortInReply,
            };
        }

        forwarder.TrackConnection(connection);
        return forwarder;
    }

    /// <summary>
    /// 远程动态转发（<c>ssh -R [bind:]port</c>，不给目标）：服务端监听，远端程序把它当 SOCKS5 代理用，
    /// 本机按放行名单替它去连 —— 远端借本机的网络到达只有本机能到的地方（内网的包镜像、内部 API）。
    /// </summary>
    /// <param name="connection">会话。</param>
    /// <param name="permitRemoteOpen">
    /// 放行名单（<c>PermitRemoteOpen</c>）。<b>必须给</b>：本机能到的内网远端都能到，放哪些出去由调用方明说；
    /// 全放就显式给 <see cref="RemoteOpenPolicy.Any"/>。
    /// </param>
    /// <param name="options">参数（绑定地址与端口、并发上限、SOCKS 握手时限）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="SshForwardException">服务端拒绝了监听请求。</exception>
    /// <remarks>
    /// <para>
    /// 〔velashell-docs/zh/ssh/spec/07 §4.6〕回连先确认，再在通道上跑 SOCKS5（与本地动态转发同一个子集：只有 <c>CONNECT</c>、
    /// 不认证）；目标不在名单里回「规则不允许」（0x02），连不上按原因回码，连上了才回成功、开始搬运。
    /// 域名在<b>本机</b>解析 —— 这正是它的用途。
    /// </para>
    /// <para>服务端那头只是一个普通的 <c>tcpip-forward</c>，所以它要 OpenSSH 7.6 之前也有的那几样：<c>AllowTcpForwarding</c> 开着。</para>
    /// </remarks>
    public static async ValueTask<RemotePortForwarder> StartDynamicAsync(
        SshConnection connection,
        RemoteOpenPolicy permitRemoteOpen,
        RemotePortForwardOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(permitRemoteOpen);

        RemotePortForwardOptions effective = options ?? RemotePortForwardOptions.Default;

        ArrayBufferWriter<byte> payload = new();
        SshDataWriter writer = new(payload);
        writer.WriteUtf8String(effective.BindAddress);
        writer.WriteUInt32((uint)effective.BindPort);

        RemotePortForwarder forwarder = new(
            connection, effective, targetHost: "", targetPort: 0, effective.BindPort, permitRemoteOpen: permitRemoteOpen);

        SshGlobalRequestReply reply = await forwarder
            .RequestAsync(SshProtocolNames.RequestTcpIpForward, payload.WrittenMemory, cancellationToken).ConfigureAwait(false);

        if (!reply.Success)
        {
            throw new SshForwardException(SshFailureReason.ForwardRejected,
                $"服务端拒绝在 {effective.BindAddress}:{effective.BindPort} 上开监听（远程动态转发）。" +
                "常见原因是 sshd_config 里 AllowTcpForwarding no，或者要绑非环回地址而 GatewayPorts 没打开，又或者端口已被占用。")
            {
                FailureKind = SshForwardFailureKind.RemoteListenRejected,
                Endpoint = $"{effective.BindAddress}:{effective.BindPort}",
                Port = effective.BindPort,
            };
        }

        if (forwarder.BoundPort == 0)
        {
            forwarder.Unregister();
            throw new SshForwardException(SshFailureReason.ProtocolError,
                "请求了动态端口，但服务端的 REQUEST_SUCCESS 里没有带回实际端口号。")
            {
                FailureKind = SshForwardFailureKind.NoBoundPortInReply,
            };
        }

        forwarder.TrackConnection(connection);
        return forwarder;
    }

    /// <summary>在服务端开一个 <b>Unix 套接字</b>监听，回连到本机的另一个套接字。</summary>
    /// <param name="connection">会话。</param>
    /// <param name="targetSocketPath">本机要连过去的套接字路径。</param>
    /// <param name="remoteSocketPath">服务端要监听的套接字路径。</param>
    /// <param name="options">选项；<see cref="RemotePortForwardOptions.BindAddress"/>
    /// 与 <see cref="RemotePortForwardOptions.BindPort"/> 在这里用不上。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="SshForwardException">服务端拒绝了监听请求。</exception>
    /// <remarks>
    /// <para>
    /// 对应 <c>ssh -R /远端/路径:/本机/路径</c>。典型用途是把本机的
    /// <c>docker.sock</c>、数据库套接字之类交到远端 —— 走套接字而不是端口，
    /// 远端机器上的其它用户就<b>看不到也连不上</b>（文件权限说了算）。
    /// </para>
    /// <para>
    /// <b>服务端会在自己的文件系统上创建那个套接字文件。</b>
    /// 路径已存在时 OpenSSH 会拒绝，所以这里的失败多半是「上一次没清干净」。
    /// </para>
    /// </remarks>
    public static async ValueTask<RemotePortForwarder> StartUnixSocketAsync(
        SshConnection connection,
        string targetSocketPath,
        string remoteSocketPath,
        RemotePortForwardOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrEmpty(targetSocketPath);
        ArgumentException.ThrowIfNullOrEmpty(remoteSocketPath);

        RemotePortForwardOptions effective = options ?? RemotePortForwardOptions.Default;

        ArrayBufferWriter<byte> payload = new();
        SshDataWriter writer = new(payload);
        writer.WriteUtf8String(remoteSocketPath);

        RemotePortForwarder forwarder = new(
            connection, effective, targetHost: "", targetPort: 0, boundPort: 0,
            remoteSocketPath: remoteSocketPath, targetSocketPath: targetSocketPath);

        SshGlobalRequestReply reply = await forwarder
            .RequestAsync(SshProtocolNames.RequestStreamLocalForward, payload.WrittenMemory, cancellationToken)
            .ConfigureAwait(false);

        if (!reply.Success)
        {
            // 不留半挂的转发器。
            throw new SshForwardException(SshFailureReason.ForwardRejected,
                $"服务端拒绝在 {remoteSocketPath} 上开套接字监听。" +
                "常见原因是 sshd_config 里 AllowStreamLocalForwarding no、" +
                "那个路径已经存在，或者所在目录不可写。")
            {
                FailureKind = SshForwardFailureKind.RemoteSocketListenRejected,
                Endpoint = remoteSocketPath,
            };
        }

        forwarder.TrackConnection(connection);
        return forwarder;
    }

    /// <summary>这个转发器的回连走哪种通道类型。</summary>
    private string ChannelType => IsStreamLocal
        ? SshProtocolNames.ChannelForwardedStreamLocal
        : SshProtocolNames.ChannelForwardedTcpIp;

    /// <summary>登记处理器、发出监听请求；服务端拒绝或发送失败时把处理器摘掉。</summary>
    /// <remarks>
    /// <para>
    /// <b>先登记处理器，再发请求。</b>服务端回完 <c>REQUEST_SUCCESS</c> 立刻就可能开回连
    /// （有人正等着连那个端口），而那条 <c>CHANNEL_OPEN</c> 由接收循环紧接着处理 ——
    /// 拿到应答再登记的话，它已经被当成没人认领拒掉了。端口给 0 时的实际端口
    /// 也在接收循环上当场记下（<see cref="OnForwardReply"/>），理由相同。
    /// </para>
    /// <para>
    /// 用 Add 而不是替换：同一条连接上的多个远程转发都要接同一种回连，
    /// 各自按「绑定地址 + 端口」认领（不归自己的就在 GetOptionsAsync 里拒，
    /// 连接会去问下一个）。
    /// </para>
    /// </remarks>
    private async ValueTask<SshGlobalRequestReply> RequestAsync(
        string requestType, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        _connection.AddIncomingChannelHandler(ChannelType, this);

        SshGlobalRequestReply reply;
        try
        {
            reply = await _connection
                .SendGlobalRequestAsync(requestType, payload, OnForwardReply, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 请求可能已经上线：服务端随后批准的话，它的监听得撤掉（见 AbandonRequest）。
            AbandonRequest();
            Unregister();
            throw;
        }

        if (!reply.Success)
        {
            Unregister();
        }
        return reply;
    }

    private void Unregister() => _connection.RemoveIncomingChannelHandler(ChannelType, this);

    // ------------------------------------------------------------ 入站通道

    /// <inheritdoc />
    /// <remarks>
    /// 〔FW-D2，velashell-docs/zh/ssh/spec/07 §4.1〕<b>本机目标在确认通道之前连好</b>：连不上就回 <c>CHANNEL_OPEN_FAILURE</c>
    /// （原因码 2，connect failed），远端看到的是「连不上」，服务端日志里也有这一句。曾经先确认、再去连：
    /// 目标连不上时远端看到的是「接受之后立刻关闭」。与 agent 转发同一个时序。这里不在接收循环上（连接把「问处理器」放在后台做）。
    /// </remarks>
    async ValueTask<SshChannelOptions> IIncomingChannelHandler.GetOptionsAsync(
        string channelType, ReadOnlyMemory<byte> typeSpecificPayload, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsStreamLocal)
        {
            // forwarded-streamlocal 的载荷是 socket_path ‖ reserved。
            SshDataReader reader = new(new ReadOnlySequence<byte>(typeSpecificPayload));
            string socketPath = reader.ReadUtf8String(MaxFieldBytes);

            if (!string.Equals(socketPath, RemoteSocketPath, StringComparison.Ordinal))
            {
                throw new SshForwardException(
                    SshFailureReason.ForwardRejected, $"没有匹配 {socketPath} 的远程套接字转发。")
                {
                    FailureKind = SshForwardFailureKind.UnmatchedForwardedChannel,
                    Endpoint = socketPath,
                };
            }
        }
        else
        {
            (string bindAddress, int bindPort) = ReadForwardedHeader(typeSpecificPayload);

            // 〔决策 velashell-docs/zh/ssh/spec/07 §4.2〕按「绑定地址 + 端口」路由，
            // 地址原样比较，不做规范化 —— 服务端回给我们的就是我们请求时用的那个串，
            // 而 ""、"*"、"0.0.0.0"、"localhost" 在服务端是不同的语义。
            if (!string.Equals(bindAddress, _options.BindAddress, StringComparison.Ordinal)
                || bindPort != BoundPort)
            {
                throw new SshForwardException(
                    SshFailureReason.ForwardRejected, $"没有匹配 {bindAddress}:{bindPort} 的远程转发。")
                {
                    FailureKind = SshForwardFailureKind.UnmatchedForwardedChannel,
                    Endpoint = $"{bindAddress}:{bindPort}",
                };
            }
        }

        if (!_connectionSlots.Wait(0, CancellationToken.None))
        {
            // 〔spec 07 §八〕与本地转发同一条：计入 errors、发 ConnectionLimit 事件（每秒至多一次）。曾经只拒掉那条通道，不发事件也不计数。
            ReportConnectionLimit(_options.MaxConnections);
            throw new SshForwardException(
                SshFailureReason.LimitExceeded, $"并发连接数已达上限 {_options.MaxConnections}。")
            {
                FailureKind = SshForwardFailureKind.ChannelLimitReached,
                Limit = _options.MaxConnections,
            };
        }

        // 动态转发的目标要等 SOCKS 握手才知道：先确认，握手在 HandleAsync 里跑。
        if (PermitRemoteOpen is not null)
        {
            return _options.Channel;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            _readyTargets.Enqueue(await ConnectTargetAsync(linked.Token).ConfigureAwait(false));
        }
        catch (Exception)
        {
            _connectionSlots.Release();
            throw;
        }

        return _options.Channel;
    }

    /// <summary>连本机目标。连不上时记一笔错误、抛出带「连不上」原因码的异常 —— 拒绝理由发给服务端，只说连不上，本机的地址不往外送。</summary>
    private async Task<Socket> ConnectTargetAsync(CancellationToken cancellationToken)
    {
        string target = TargetName;
        Socket outbound = _targetSocketPath is null
            ? new Socket(SocketType.Stream, ProtocolType.Tcp)
            : new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            if (_targetSocketPath is null)
            {
                await outbound.ConnectAsync(_targetHost, _targetPort, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await outbound.ConnectAsync(new UnixDomainSocketEndPoint(_targetSocketPath), cancellationToken).ConfigureAwait(false);
            }
            return outbound;
        }
        catch (SocketException ex)
        {
            outbound.Dispose();

            // 连不上本机目标：拒掉这一条，转发器继续跑。
            Report(ForwardErrorReason.TargetConnect, $"连不上本机目标 {target}：{ex.SocketErrorCode}。", ex, target);
            throw new SshForwardException(ReasonFor(ex.SocketErrorCode), "连不上转发的本机目标。", ex)
            {
                FailureKind = SshForwardFailureKind.LocalTargetUnreachable,
                Endpoint = target,
            };
        }
        catch
        {
            outbound.Dispose();
            throw;
        }
    }

    /// <summary>连本机目标失败的原因码（会被译成 <c>CHANNEL_OPEN_FAILURE</c> 的 connect failed）。</summary>
    private static SshFailureReason ReasonFor(SocketError error) => error switch
    {
        SocketError.ConnectionRefused => SshFailureReason.TcpRefused,
        SocketError.TimedOut => SshFailureReason.TcpTimeout,
        SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData or SocketError.NoRecovery => SshFailureReason.DnsFailure,
        _ => SshFailureReason.TcpUnreachable,
    };

    /// <inheritdoc />
    /// <remarks>还回 <c>GetOptionsAsync</c> 占的连接槽位，关掉为它连好的那个本机目标。</remarks>
    void IIncomingChannelHandler.OnOpenAborted(string channelType, ReadOnlyMemory<byte> typeSpecificPayload)
    {
        if (PermitRemoteOpen is null && _readyTargets.TryDequeue(out Socket? ready))
        {
            ready.Dispose();
        }
        _connectionSlots.Release();
    }

    /// <inheritdoc />
    async Task IIncomingChannelHandler.HandleAsync(
        SshChannel channel, ReadOnlyMemory<byte> typeSpecificPayload, CancellationToken cancellationToken)
    {
        long connectionId = NextConnectionId();
        string target = TargetName;

        // 连上转发器自己的生命周期，不只是连接的：只看连接的令牌的话，
        // 释放转发器之后它的连接照样一直搬下去，直到整条 SSH 连接断开。
        using var linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = linked.Token;

        if (PermitRemoteOpen is { } permit)
        {
            try
            {
                await HandleDynamicAsync(channel, connectionId, OriginatorOf(typeSpecificPayload), permit, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _connectionSlots.Release();
            }
            return;
        }

        // 本机目标在确认之前已经连好（见 GetOptionsAsync）。取不到只会发生在转发器刚被释放、把队列清空了的时候 ——
        // 那这条通道本来也不该再转发了。
        _readyTargets.TryDequeue(out Socket? outbound);
        try
        {
            if (outbound is null)
            {
                return;
            }

            NetworkStream stream = new(outbound, ownsSocket: false);
            Socket connected = outbound;
            await using StreamRelayEndpoint local = new(
                stream, () => StreamRelayEndpoint.ShutdownSend(connected), ownsStream: true,
                abort: () => StreamRelayEndpoint.Reset(connected));

            await RelayAsync(connectionId, OriginatorOf(typeSpecificPayload), target, local, channel, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 转发器在收工。
        }
        catch (Exception ex)
        {
            Report(ForwardErrorReason.Relay, $"到 {target} 的连接出错：{ex.Message}", ex, target);
        }
        finally
        {
            outbound?.Dispose();
            _connectionSlots.Release();
        }
    }

    /// <summary>远程动态转发的一条回连：在通道上跑 SOCKS5，按名单放行，本机去连，连上了再搬运。</summary>
    private async Task HandleDynamicAsync(
        SshChannel channel, long connectionId, EndPoint? source, RemoteOpenPolicy permit, CancellationToken cancellationToken)
    {
        string target = "SOCKS";
        try
        {
            // 握手有时限：连上来一句不说的客户端，每个都白占一个并发名额。
            SocksTarget? socks;
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                handshake.CancelAfter(_options.SocksHandshakeTimeout);
                try
                {
                    socks = await SocksHandshake
                        .ReadRequestAsync(channel.StandardOutput, channel.StandardInput, handshake.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    Report(ForwardErrorReason.SocksHandshake,
                        $"远端的 SOCKS 握手在 {_options.SocksHandshakeTimeout.TotalSeconds:0.#} 秒内没有完成，这一条被关掉。", null,
                        timeout: _options.SocksHandshakeTimeout);
                    return;
                }
            }

            if (socks is not { } parsed)
            {
                Report(ForwardErrorReason.SocksHandshake, "远端的 SOCKS 握手非法或命令不支持，这一条被关掉。", null);
                await channel.SendEofAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            // 远端给的名字是对端的文本：进事件与日志之前清一下。
            target = $"{PeerText.Sanitize(parsed.Host, 255)}:{parsed.Port}";

            // 先记、再回：远端一收到应答就可能再来一条，事件不该落在它后面。
            if (!permit.Permits(parsed.Host, parsed.Port))
            {
                Report(ForwardErrorReason.TargetNotPermitted, $"远端要连 {target}，不在放行名单（{permit}）里。", null, target);
                await RefuseAsync(channel, SocksReply.NotAllowed, parsed.AddressType, cancellationToken).ConfigureAwait(false);
                return;
            }

            Socket outbound = new(SocketType.Stream, ProtocolType.Tcp);
            try
            {
                // 域名在本机解析 —— 远端要的正是本机能到的地方。
                // 拨的就是上面 Permits 比过的那个字符串，原样：中间不许再改写（去末尾的点、把简写的 IP 换成规范写法、转 Punycode……），
                // 否则名单比的和本机连的就不是同一个东西。
                await outbound.ConnectAsync(parsed.Host, parsed.Port, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                outbound.Dispose();
                Report(ForwardErrorReason.TargetConnect, $"替远端连 {target} 没连上：{ex.SocketErrorCode}。", ex, target);
                await RefuseAsync(channel, ReplyFor(ex.SocketErrorCode), parsed.AddressType, cancellationToken).ConfigureAwait(false);
                return;
            }

            try
            {
                await SocksHandshake.WriteReplyAsync(channel.StandardInput, SocksReply.Succeeded, parsed.AddressType, cancellationToken)
                    .ConfigureAwait(false);

                NetworkStream stream = new(outbound, ownsSocket: false);
                Socket connected = outbound;
                await using StreamRelayEndpoint local = new(
                    stream, () => StreamRelayEndpoint.ShutdownSend(connected), ownsStream: true,
                    abort: () => StreamRelayEndpoint.Reset(connected));

                await RelayAsync(connectionId, source, target, local, channel, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                outbound.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            // 转发器在收工。
        }
        catch (Exception ex)
        {
            Report(ForwardErrorReason.Relay, $"到 {target} 的连接出错：{ex.Message}", ex, target);
        }
    }

    /// <summary>回一个失败的 SOCKS 应答，并让它确实发出去（冲干净 stdin 再 EOF），之后由连接关掉通道。</summary>
    private static async Task RefuseAsync(SshChannel channel, SocksReply reply, byte addressType, CancellationToken cancellationToken)
    {
        await SocksHandshake.WriteReplyAsync(channel.StandardInput, reply, addressType, cancellationToken).ConfigureAwait(false);
        await channel.SendEofAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>本机连不上目标时回哪个 SOCKS 码（RFC 1928 §6）：curl 与浏览器据此决定怎么报。</summary>
    private static SocksReply ReplyFor(SocketError error) => error switch
    {
        SocketError.ConnectionRefused => SocksReply.ConnectionRefused,
        SocketError.NetworkUnreachable or SocketError.NetworkDown => SocksReply.NetworkUnreachable,
        SocketError.HostUnreachable or SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData => SocksReply.HostUnreachable,
        SocketError.TimedOut => SocksReply.TtlExpired,
        _ => SocksReply.GeneralFailure,
    };

    /// <summary>
    /// 是谁连上了服务端那个暴露出来的端口：<c>forwarded-tcpip</c> 载荷里的 originator 地址与端口（RFC 4254 §7.2）。
    /// </summary>
    /// <returns>Unix 套接字转发（没有这一段）或者读不出来时为 <see langword="null"/>。</returns>
    /// <remarks>
    /// 〔FW-D3〕曾经整个丢掉，<see cref="ForwardConnectionEventArgs.Source"/> 永远是 null —— 面板上本可以显示是谁连进来的。
    /// 这是对端给的文本：认得出是 IP 地址就给 <see cref="IPEndPoint"/>，否则按主机名给 <see cref="DnsEndPoint"/>（不去解析）。
    /// </remarks>
    private EndPoint? OriginatorOf(ReadOnlyMemory<byte> payload)
    {
        if (IsStreamLocal)
        {
            return null;
        }

        try
        {
            SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
            _ = reader.ReadUtf8String(MaxFieldBytes);   // 绑定地址
            _ = reader.ReadUInt32();                    // 绑定端口
            string address = reader.ReadUtf8String(MaxFieldBytes);
            uint port = reader.ReadUInt32();
            if (port > 65535 || address.Length == 0)
            {
                return null;
            }

            return IPAddress.TryParse(address, out IPAddress? ip)
                ? new IPEndPoint(ip, (int)port)
                : new DnsEndPoint(PeerText.Sanitize(address, 255), (int)port);
        }
        catch (SshWireFormatException)
        {
            return null;
        }
    }

    private static (string BindAddress, int BindPort) ReadForwardedHeader(ReadOnlyMemory<byte> payload)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        string bindAddress = reader.ReadUtf8String(MaxFieldBytes);
        uint bindPort = reader.ReadUInt32();
        return (bindAddress, (int)bindPort);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 顺序是：请服务端取消监听 → 宽限期里照常接在途的回连 → 摘掉处理器 → 结束这个转发器的全部连接。
    /// 连接已经断了就不等宽限期 —— 那时不会再有回连。
    /// </remarks>
    public override async ValueTask DisposeAsync()
    {
        // 这里只设「在释放」，不设 _disposed：GetOptionsAsync 看到 _disposed 就拒，
        // 曾经一进来就设上，宽限期于是形同虚设 —— 在途的回连照样被莫名拒绝。
        if (Interlocked.Exchange(ref _draining, 1) != 0)
        {
            return;
        }
        CompleteAsStopped();

        (string cancelRequest, ReadOnlyMemory<byte> payload) = EncodeCancelRequest();

        // ⚠️ 等应答有时限。半死的链路上（保活没开、或者周期很长）应答可能永远不来，
        //    不设时限的话释放一直卡到 TCP 重传放弃（Linux 默认约 15 分钟），调用方的「停止隧道」跟着卡住。
        //    到点就当链路已经不可用，照常收尾、不再等宽限期 —— 与通道的释放时限是同一个思路。
        bool connectionAlive = true;
        using CancellationTokenSource deadline = new(_options.CancelReplyTimeout);
        try
        {
            await _connection.SendGlobalRequestAsync(cancelRequest, payload, cancellationToken: deadline.Token)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 会话可能已经没了 —— 那样服务端的监听自然也没了；或者等应答超时了，链路多半已经半死。
            connectionAlive = false;
        }

        // 〔决策 velashell-docs/zh/ssh/spec/07 §4.3〕先不摘处理器。
        // 取消之后仍会有在途的回连到来，立刻摘掉会让正在建立的连接被莫名拒绝。
        if (connectionAlive && !_connection.Disconnected.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(DrainGrace, _connection.Disconnected).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 宽限期里连接断了：不会再有回连，不必再等。
            }
        }

        _disposed = true;
        Unregister();

        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 释放路径不抛。
        }

        // 连好了、还没被取走的本机目标（对应的通道还没确认）：关掉。
        while (_readyTargets.TryDequeue(out Socket? ready))
        {
            ready.Dispose();
        }

        // 不 Dispose 槽位信号量与 _lifetime：还在收尾的回连要 Release 前者、读后者的令牌，
        // 而两者都没有用到需要归还的资源（没有等待句柄、没有定时器）。
    }
}
