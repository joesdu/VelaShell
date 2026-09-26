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
using System.Net.Sockets;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Session;

namespace VelaShell.Ssh.Forwarding;

/// <summary>远程转发（<c>-R</c>）的参数。</summary>
public sealed record RemotePortForwardOptions
{
    /// <summary>
    /// 请服务端绑哪个地址。
    /// </summary>
    /// <remarks>
    /// <c>""</c>、<c>"*"</c>、<c>"0.0.0.0"</c>、<c>"localhost"</c> 在服务端是
    /// <b>不同的语义</b>，所以这里原样传，不做任何规范化 —— 也因此是字符串而不是
    /// <see cref="System.Net.IPAddress"/>（本地转发绑的是本机套接字，那边才是地址）。
    /// 默认 <c>"localhost"</c>：只有服务端本机能连，与 OpenSSH 的
    /// <c>GatewayPorts no</c> 一致。
    /// </remarks>
    public string BindAddress { get; init; } = "localhost";

    /// <summary>请服务端绑哪个端口。<c>0</c> 表示由服务端分配。</summary>
    public int BindPort { get; init; }

    /// <summary>并发连接数上限。</summary>
    public int MaxConnections { get; init; } = 1024;

    /// <summary>每条隧道通道的参数。</summary>
    public SshChannelOptions Channel { get; init; } = SshChannelOptions.Default with
    {
        StderrMode = SshStderrMode.Discard,
    };

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

    /// <summary>这个转发器的一切连接都挂在它上面：释放时取消，搬运随之中止。</summary>
    /// <remarks>不释放它：回连的处理可能在释放之后才开始读它的令牌，而它没有要还的资源。</remarks>
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>取消之后、处理器摘掉之前的宽限期（velashell-docs/zh/ssh/spec/07 §4.3）。</summary>
    internal static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(2);

    private int _boundPort;

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
        string? targetSocketPath = null)
        : base(ForwardKind.Remote)
    {
        _connection = connection;
        _options = options;
        _targetHost = targetHost;
        _targetPort = targetPort;
        RemoteSocketPath = remoteSocketPath;
        _targetSocketPath = targetSocketPath;
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
    }

    /// <summary>这是不是 Unix 套接字变体。</summary>
    private bool IsStreamLocal => RemoteSocketPath is not null;

    /// <summary>本机目标的名字（进日志与事件）。</summary>
    private string TargetName => _targetSocketPath ?? $"{_targetHost}:{_targetPort}";

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
                (effective.BindPort is > 0 and < 1024 ? "又或者那是个特权端口。" : "又或者端口已被占用。"));
        }

        if (forwarder.BoundPort == 0)
        {
            // 端口给 0 时必须从应答载荷里取实际端口。
            // 取不到就按 (bind_addr, 0) 去路由回连 —— 一条都对不上，
            // 而症状是「转发看起来建好了，但连过来的全被拒」。
            forwarder.Unregister();
            throw new SshForwardException(SshFailureReason.ProtocolError,
                "请求了动态端口，但服务端的 REQUEST_SUCCESS 里没有带回实际端口号。");
        }

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
                "那个路径已经存在，或者所在目录不可写。");
        }

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
    ValueTask<SshChannelOptions> IIncomingChannelHandler.GetOptionsAsync(
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
                    SshFailureReason.ForwardRejected, $"没有匹配 {socketPath} 的远程套接字转发。");
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
                    SshFailureReason.ForwardRejected, $"没有匹配 {bindAddress}:{bindPort} 的远程转发。");
            }
        }

        if (!_connectionSlots.Wait(0, CancellationToken.None))
        {
            throw new SshForwardException(
                SshFailureReason.LimitExceeded, $"并发连接数已达上限 {_options.MaxConnections}。");
        }

        return ValueTask.FromResult(_options.Channel);
    }

    /// <inheritdoc />
    /// <remarks>还回 <c>GetOptionsAsync</c> 占的连接槽位。</remarks>
    void IIncomingChannelHandler.OnOpenAborted(string channelType, ReadOnlyMemory<byte> typeSpecificPayload) =>
        _connectionSlots.Release();

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

        Socket? outbound = null;
        try
        {
            outbound = _targetSocketPath is null
                ? new Socket(SocketType.Stream, ProtocolType.Tcp)
                : new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

            try
            {
                if (_targetSocketPath is null)
                {
                    await outbound.ConnectAsync(_targetHost, _targetPort, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await outbound.ConnectAsync(
                        new UnixDomainSocketEndPoint(_targetSocketPath), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (SocketException ex)
            {
                // 连不上本机目标：关掉这一条，转发器继续跑。
                Report(ForwardErrorReason.TargetConnect, $"连不上本机目标 {target}：{ex.SocketErrorCode}。", ex);
                return;
            }

            NetworkStream stream = new(outbound, ownsSocket: false);
            Socket connected = outbound;
            await using StreamRelayEndpoint local = new(
                stream, () => StreamRelayEndpoint.ShutdownSend(connected), ownsStream: true,
                abort: () => StreamRelayEndpoint.Reset(connected));

            await RelayAsync(connectionId, null, target, local, channel, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 转发器在收工。
        }
        catch (Exception ex)
        {
            Report(ForwardErrorReason.Relay, $"到 {target} 的连接出错：{ex.Message}", ex);
        }
        finally
        {
            outbound?.Dispose();
            _connectionSlots.Release();
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

        ArrayBufferWriter<byte> payload = new();
        SshDataWriter writer = new(payload);
        if (IsStreamLocal)
        {
            writer.WriteUtf8String(RemoteSocketPath!);
        }
        else
        {
            writer.WriteUtf8String(_options.BindAddress);
            writer.WriteUInt32((uint)BoundPort);
        }

        string cancelRequest = IsStreamLocal
            ? SshProtocolNames.RequestCancelStreamLocalForward
            : SshProtocolNames.RequestCancelTcpIpForward;

        bool connectionAlive = true;
        try
        {
            await _connection.SendGlobalRequestAsync(cancelRequest, payload.WrittenMemory).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 会话可能已经没了 —— 那样服务端的监听自然也没了。
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

        // 不 Dispose 槽位信号量与 _lifetime：还在收尾的回连要 Release 前者、读后者的令牌，
        // 而两者都没有用到需要归还的资源（没有等待句柄、没有定时器）。
    }
}
