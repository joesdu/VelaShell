using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using VelaShell.Core.Models;
using VelaShell.Core.Net;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Core.Tunnels;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Infrastructure.Tunnels;

/// <summary>
/// 端口转发通道管理服务:在指定 SSH 会话上创建本地/远程/动态转发,跟踪各通道的活动状态,
/// 并在停止或会话拆除时释放底层监听端口。以 <see cref="Guid" /> 会话为单位维护可观察的通道列表。
/// </summary>
/// <param name="connectionService">会话查询,用于确认目标会话仍然连着。</param>
/// <param name="clientFactory">按会话取 SSH 客户端。</param>
/// <param name="logger">可选日志。</param>
/// <param name="isLocalPortInUse">
/// 本地端口占用探测,默认查询系统的 TCP 监听表。做成可注入是为了让测试不必看
/// 运行机器上恰好有没有 PostgreSQL 在监听 5432 —— 那种依赖会让测试时灵时不灵。
/// </param>
public class TunnelService(
    ISshConnectionService connectionService,
    Func<Guid, ISshClientWrapper> clientFactory,
    ILogger<TunnelService>? logger = null,
    Func<string, uint, bool>? isLocalPortInUse = null) : ITunnelService
{
    private readonly Func<Guid, ISshClientWrapper> _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    private readonly Func<string, uint, bool> _isLocalPortInUse = isLocalPortInUse ?? IsLocalPortInUse;
    private readonly ISshConnectionService _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
    /// <summary>
    /// 会话 → 该会话的通道列表。每个 <see cref="List{T}" /> 实例自身即为其内容的锁对象:
    /// 增删与快照读取都必须 <c>lock</c> 住它,字典本身的并发由 ConcurrentDictionary 保证。
    /// </summary>
    private readonly ConcurrentDictionary<Guid, List<TunnelInfo>> _sessionTunnels = new();

    private readonly ConcurrentDictionary<Guid, (IPortForwardHandle Handle, TunnelInfo Info)> _tunnelPorts = new();

    /// <summary>获取指定会话当前所有转发通道的列表快照(锁内复制,遍历期间不受并发增删影响)。</summary>
    public IReadOnlyList<TunnelInfo> GetActiveTunnels(Guid sessionId)
    {
        RefreshStatistics();
        List<TunnelInfo> tunnels = _sessionTunnels.GetOrAdd(sessionId, _ => []);
        lock (tunnels)
        {
            return [.. tunnels];
        }
    }

    /// <summary>把各活动转发句柄的连接数、流量、实时速率与连接快照同步到对应的 <see cref="TunnelInfo" />。</summary>
    public void RefreshStatistics()
    {
        foreach ((IPortForwardHandle handle, TunnelInfo info) in _tunnelPorts.Values)
        {
            // 停止的隧道其句柄已被移出 _tunnelPorts,累计读数就停在最后一次同步的值上(速率与连接列表在停止时清掉)。
            info.BytesTransferred = handle.BytesTransferred;
            info.TotalConnections = handle.TotalConnections;
            info.ActiveConnections = handle.ActiveConnections;
            info.SentBytesPerSecond = handle.SentBytesPerSecond;
            info.ReceivedBytesPerSecond = handle.ReceivedBytesPerSecond;
            info.Connections = handle.Connections ?? [];
        }
    }

    /// <summary>在指定会话上创建本地端口转发通道(本地监听端口 → 远端目标)。</summary>
    public async Task<TunnelInfo> CreateLocalForwardAsync(Guid sessionId, TunnelConfig config, CancellationToken cancellationToken = default)
    {
        if (config.Type != TunnelType.LocalForward)
        {
            throw new ArgumentException(@"Config type must be LocalForward", nameof(config));
        }
        // 本机这头在套接字文件上监听时,预检的是那个路径而不是端口。
        if (config.LocalSocketPath is { } listenPath)
        {
            EnsureLocalSocketPathAvailable(listenPath);
        }
        else
        {
            EnsureLocalPortAvailable(config.LocalHost, config.LocalPort);
        }
        return await CreateForwardAsync(sessionId,
                   config,
                   new(PortForwardKind.Local, config.LocalHost, config.LocalPort, config.RemoteHost, config.RemotePort)
                   {
                       MaxBytesPerSecond = config.MaxBytesPerSecond,
                       BoundSocketPath = config.LocalSocketPath,
                       TargetSocketPath = config.RemoteSocketPath
                   },
                   "local",
                   cancellationToken).ConfigureAwait(false);
    }

    /// <summary>在指定会话上创建远程端口转发通道(远端监听端口 → 本地目标)。</summary>
    public async Task<TunnelInfo> CreateRemoteForwardAsync(Guid sessionId, TunnelConfig config, CancellationToken cancellationToken = default)
    {
        if (config.Type != TunnelType.RemoteForward)
        {
            throw new ArgumentException(@"Config type must be RemoteForward", nameof(config));
        }
        // 两头各自可以是端口或套接字(SSH 库的四种组合都有,velashell-docs spec/07 §4.4):
        // 服务器那头的路径进监听端(库发 streamlocal-forward),本机那头的路径进目的端(回连到本机的套接字)。
        return await CreateForwardAsync(sessionId,
                   config,
                   new(PortForwardKind.Remote, config.RemoteHost, config.RemotePort, config.LocalHost, config.LocalPort)
                   {
                       MaxBytesPerSecond = config.MaxBytesPerSecond,
                       BoundSocketPath = config.RemoteSocketPath,
                       TargetSocketPath = config.LocalSocketPath
                   },
                   "remote",
                   cancellationToken).ConfigureAwait(false);
    }

    /// <summary>在指定会话上创建动态转发通道(本地 SOCKS 代理端口),按连接动态选择远端目标。</summary>
    public async Task<TunnelInfo> CreateDynamicForwardAsync(Guid sessionId, TunnelConfig config, CancellationToken cancellationToken = default)
    {
        if (config.Type != TunnelType.DynamicForward)
        {
            throw new ArgumentException(@"Config type must be DynamicForward", nameof(config));
        }
        EnsureLocalPortAvailable(config.LocalHost, config.LocalPort);
        return await CreateForwardAsync(sessionId,
                   config,
                   new(PortForwardKind.Dynamic, config.LocalHost, config.LocalPort)
                   {
                       MaxBytesPerSecond = config.MaxBytesPerSecond
                   },
                   "dynamic",
                   cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 在指定会话上创建远程动态转发通道:服务器上的端口当 SOCKS 代理,本机按放行名单替远端去连。
    /// 名单留空(或有写错的规则)时不建,报一句说得清的话 —— 不替用户把它当成「全放」或「全拒」。
    /// </summary>
    public async Task<TunnelInfo> CreateRemoteDynamicForwardAsync(Guid sessionId, TunnelConfig config, CancellationToken cancellationToken = default)
    {
        if (config.Type != TunnelType.RemoteDynamicForward)
        {
            throw new ArgumentException(@"Config type must be RemoteDynamicForward", nameof(config));
        }
        if (!RemoteOpenRules.TryParse(RemoteOpenRules.Format(config.PermitRemoteOpen), out IReadOnlyList<string> rules, out string? invalid))
        {
            throw new ArgumentException(invalid is null
                ? Strings.Get("TunnelSvc_PermitRemoteOpenRequired")
                : Strings.Format("Tunnel_PermitRemoteOpenInvalid", invalid));
        }
        return await CreateForwardAsync(sessionId,
                   config,
                   new(PortForwardKind.RemoteDynamic, config.RemoteHost, config.RemotePort)
                   {
                       MaxBytesPerSecond = config.MaxBytesPerSecond,
                       PermitRemoteOpen = rules
                   },
                   "remote-dynamic",
                   cancellationToken).ConfigureAwait(false);
    }

    /// <summary>移除指定转发通道:若仍在活动则先停止,随后将其从会话列表中删除。</summary>
    public async Task RemoveTunnelAsync(Guid tunnelId, CancellationToken cancellationToken = default)
    {
        // 活动中的先停止(同时把记录状态置为 Stopped)。
        if (_tunnelPorts.ContainsKey(tunnelId))
        {
            await StopTunnelAsync(tunnelId, cancellationToken).ConfigureAwait(false);
        }
        foreach ((Guid _, List<TunnelInfo> tunnels) in _sessionTunnels)
        {
            lock (tunnels)
            {
                TunnelInfo? existing = tunnels.Find(t => t.Id == tunnelId);
                if (existing is null)
                {
                    continue;
                }
                tunnels.Remove(existing);
            }
            if (logger is not null && logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Removed tunnel {TunnelId}", tunnelId);
            }
            return;
        }
    }

    /// <summary>停止指定会话下的所有活动转发通道,常用于会话断开/拆除时的批量清理。</summary>
    public async Task StopAllForSessionAsync(Guid sessionId)
    {
        foreach ((Guid tunnelId, (IPortForwardHandle _, TunnelInfo info)) in _tunnelPorts)
        {
            if (info.SessionId != sessionId)
            {
                continue;
            }
            try
            {
                await StopTunnelAsync(tunnelId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 会话断开时底层端口可能已经随客户端一起失效;记录后继续。
                logger?.LogWarning(ex, "Failed to stop tunnel {TunnelId} on session teardown", tunnelId);
                _tunnelPorts.TryRemove(tunnelId, out _);
                info.Status = TunnelStatus.Stopped;
                info.SentBytesPerSecond = 0;
                info.ReceivedBytesPerSecond = 0;
                info.Connections = [];
            }
        }
    }

    /// <summary>停止指定转发通道:释放底层监听端口并将其状态置为 <see cref="TunnelStatus.Stopped" />;找不到通道时抛出异常。</summary>
    public async Task StopTunnelAsync(Guid tunnelId, CancellationToken cancellationToken = default)
    {
        if (!_tunnelPorts.TryRemove(tunnelId, out (IPortForwardHandle Handle, TunnelInfo Info) tunnelData))
        {
            throw new InvalidOperationException($"Tunnel {tunnelId} not found");
        }
        (IPortForwardHandle handle, TunnelInfo info) = tunnelData;
        try
        {
            // 先把最后一次读数取下来:句柄一释放,这条隧道这辈子搬了多少字节就再也问不到了,
            // 而界面在"已停止"状态下仍要显示它跑过的总量。
            info.BytesTransferred = handle.BytesTransferred;
            info.TotalConnections = handle.TotalConnections;
            info.ActiveConnections = 0;
            info.SentBytesPerSecond = 0;
            info.ReceivedBytesPerSecond = 0;
            info.Connections = [];

            // 释放幂等,且自带"客户端已随会话释放"的容错(见 IPortForwardHandle 契约)。
            // **等它真的收完**再把状态置为已停止 —— 否则界面上显示「已停止」的那一刻
            // 监听端口可能还没放开,用户紧接着重开同一条隧道会撞上「端口被占用」。
            await handle.DisposeAsync().ConfigureAwait(false);
            info.Status = TunnelStatus.Stopped;
            if (_sessionTunnels.TryGetValue(info.SessionId, out List<TunnelInfo>? tunnels))
            {
                lock (tunnels)
                {
                    TunnelInfo? existingTunnel = tunnels.Find(t => t.Id == tunnelId);
                    if (existingTunnel != null)
                    {
                        tunnels.Remove(existingTunnel);
                        tunnels.Add(info);
                    }
                }
            }
            if (logger is not null && logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Stopped tunnel {TunnelId} for session {SessionId}", tunnelId, info.SessionId);
            }
        }
        catch (Exception ex)
        {
            info.Status = TunnelStatus.Error;
            logger?.LogError(ex, "Failed to stop tunnel {TunnelId}", tunnelId);
            throw;
        }
    }

    /// <summary>释放服务:停止并释放所有会话下的转发通道与可观察列表资源。</summary>
    public async ValueTask DisposeAsync()
    {
        foreach ((Guid tunnelId, (IPortForwardHandle handle, TunnelInfo info)) in _tunnelPorts)
        {
            try
            {
                // 原先是 Task.Run —— 那时停一条隧道是同步阻塞的,在退出路径上会卡住。
                // 现在释放本身是异步的,直接 await。
                await handle.DisposeAsync().ConfigureAwait(false);
                info.Status = TunnelStatus.Stopped;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to dispose tunnel {TunnelId}", tunnelId);
            }
        }
        _tunnelPorts.Clear();
        _sessionTunnels.Clear();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 创建本机监听前的端口占用预检:命中就抛 <see cref="TunnelPortInUseException" />,
    /// 让用户看到"27017 被占用"而不是底层套接字的错误码。
    /// <para>
    /// 预检与真正的绑定之间存在竞态(检查完到绑定前端口可能刚被抢走),这不是问题:
    /// 预检负责把最常见的情形讲清楚,真绑不上时底层异常仍会照常抛出。
    /// </para>
    /// </summary>
    private void EnsureLocalPortAvailable(string host, uint port)
    {
        if (_isLocalPortInUse(host, port))
        {
            throw new TunnelPortInUseException(Strings.Format("TunnelSvc_LocalPortInUse", port), port);
        }
    }

    /// <summary>
    /// 本机套接字监听的预检:那个路径上已经有文件就不建 —— 多半是上一次没收拾干净,也可能是别的程序的套接字,
    /// 不替用户删(SSH 库默认同样不删),报一句说得清的话。
    /// </summary>
    private static void EnsureLocalSocketPathAvailable(string path)
    {
        if (File.Exists(path))
        {
            throw new TunnelSocketPathInUseException(Strings.Format("TunnelSvc_LocalSocketExists", path), path);
        }
    }

    /// <summary>默认的占用探测:比对系统 TCP 监听表。</summary>
    private static bool IsLocalPortInUse(string host, uint port)
    {
        IPAddress[] requested;
        try
        {
            requested = ParseBindAddresses(host);
        }
        catch (FormatException)
        {
            // 监听地址本身不合法,交给真正的绑定去报错,预检不越俎代庖。
            return false;
        }
        IPEndPoint[] listeners;
        try
        {
            listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        }
        catch (NetworkInformationException)
        {
            // 平台不给监听表(部分容器/受限环境)时跳过预检,退回底层异常。
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
        return listeners.Any(listener => listener.Port == port && requested.Any(address => Overlaps(listener.Address, address)));
    }

    /// <summary>
    /// 两个监听地址是否会撞车:任一方绑在"所有接口"上就与同端口的一切监听冲突,
    /// 否则只有地址完全相同才算冲突(不同网卡的同一端口可以并存)。
    /// </summary>
    private static bool Overlaps(IPAddress existing, IPAddress requested) =>
        IsAnyAddress(existing) || IsAnyAddress(requested) || existing.Equals(requested);

    private static bool IsAnyAddress(IPAddress address) =>
        address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);

    /// <summary>把配置里的监听主机翻译成要绑的地址(与转发句柄的解析保持一致:<c>localhost</c> 是两个环回)。</summary>
    private static IPAddress[] ParseBindAddresses(string host) =>
        host is "0.0.0.0" or "*" ? [IPAddress.Any] :
        host == "::" ? [IPAddress.IPv6Any] :
        host == "localhost" ? [IPAddress.Loopback, IPAddress.IPv6Loopback] :
        host == "127.0.0.1" ? [IPAddress.Loopback] :
        [IPAddress.Parse(host)];

    /// <summary>
    /// 把转发通道异常翻译成用户可理解的提示;最常见的是把目标填成了服务器的
    /// 公网地址,而服务只监听 127.0.0.1。
    /// </summary>
    /// <param name="ex">转发通道上报的异常。</param>
    /// <param name="type">
    /// 隧道类型:远程 / 远程动态转发的目标是<b>本机</b>去连的(连不上时是本机这头的套接字报的错),
    /// 提示要说「本机这头」;本地 / 动态转发的目标由服务器去连,提示说「从服务器视角」。
    /// 曾经不分类型,远程转发的本机服务没起来时,行上写的却是「目标地址是从服务器视角解析的」。
    /// </param>
    internal static string DescribeForwardError(Exception ex, TunnelType type)
    {
        bool targetIsLocal = type is TunnelType.RemoteForward or TunnelType.RemoteDynamicForward;
        // 库把底层异常包在 SshForwardException / SshChannelException 里,
        // 宿主的 SshInterop 又会再包一层 —— 所以沿整条 InnerException 链找,而不是只看一层。
        SocketException? socket = null;
        for (Exception? e = ex; e is not null && socket is null; e = e.InnerException)
        {
            socket = e as SocketException;
        }
        switch (socket?.SocketErrorCode)
        {
            case SocketError.ConnectionRefused:
                return Strings.Get(targetIsLocal ? "TunnelSvc_LocalTargetRefused" : "TunnelSvc_TargetRefused");
            case SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.HostNotFound:
                return Strings.Get(targetIsLocal ? "TunnelSvc_LocalTargetUnreachable" : "TunnelSvc_TargetUnreachable");
        }
        // 认原因码而不是认文案:库的消息是中文,「administratively prohibited」这串字只在
        // 服务端原文里才有。原因码是协议定死的(RFC 4254 §5.1),不随谁的措辞变。
        // 曾经匹配不到原因码时还去找消息里的那串英文 —— 宿主唯一一处解析库的句子,删掉;
        // ConnectFailed(服务端连不上 -L 的目标)也曾没有映射,直接显示原文。
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case SshChannelException { OpenFailureReason: SshChannelOpenFailureReason.AdministrativelyProhibited }:
                    return Strings.Get("TunnelSvc_ForwardProhibited");
                case SshChannelException { OpenFailureReason: SshChannelOpenFailureReason.ConnectFailed }:
                    return Strings.Get("TunnelSvc_TargetRefused");
            }
        }

        // 其余的按原因码本地化(库的消息是写给开发者看的中文),不认识的才显示原文。
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is SshException ssh)
            {
                return Ssh.SshInterop.Localize(ssh);
            }
        }
        return ex.Message;
    }

    private async Task<TunnelInfo> CreateForwardAsync(
        Guid sessionId,
        TunnelConfig config,
        PortForwardRequest request,
        string direction,
        CancellationToken cancellationToken)
    {
        SshSession? session = _connectionService.GetSession(sessionId) ?? throw new InvalidOperationException($"Session {sessionId} not found");
        if (session.Status != SessionStatus.Connected)
        {
            throw new InvalidOperationException($"Session {sessionId} is not connected");
        }
        ISshClientWrapper client = _clientFactory(sessionId);
        if (!client.IsConnected)
        {
            throw new InvalidOperationException($"SSH client for session {sessionId} is not connected");
        }
        var tunnelInfo = new TunnelInfo
        {
            Id = Guid.NewGuid(),
            Config = config,
            Status = TunnelStatus.Active,
            SessionId = sessionId,
            CreatedAt = DateTime.UtcNow,
            BytesTransferred = 0
        };
        try
        {
            // StartPortForwardAsync 建立并启动监听,失败时不留下半挂的端口(见接口契约)。
            IPortForwardHandle handle = await client.StartPortForwardAsync(request, cancellationToken).ConfigureAwait(false);

            // 转发通道错误(目标拒绝连接等)不会让监听端口停摆,但每个经过的连接都会失败;
            // 记到 LastError 供界面展示,否则用户只看到"运行中"却连不上。
            handle.ChannelError += ex =>
            {
                tunnelInfo.LastError = DescribeForwardError(ex, config.Type);
                logger?.LogWarning(ex, "Tunnel {TunnelId} channel error", tunnelInfo.Id);
            };
            List<TunnelInfo> tunnels = _sessionTunnels.GetOrAdd(sessionId, _ => []);
            _tunnelPorts[tunnelInfo.Id] = (handle, tunnelInfo);
            lock (tunnels)
            {
                tunnels.Add(tunnelInfo);
            }
            if (logger is not null && logger.IsEnabled(LogLevel.Information))
            {
                logger?.LogInformation("Created {Direction} forward tunnel {TunnelId} for session {SessionId}: {LocalHost}:{LocalPort} <-> {RemoteHost}:{RemotePort}",
                    direction, tunnelInfo.Id, sessionId, config.LocalHost, config.LocalPort, config.RemoteHost, config.RemotePort);
            }
            return tunnelInfo;
        }
        catch (Exception ex)
        {
            tunnelInfo.Status = TunnelStatus.Error;
            logger?.LogError(ex, "Failed to create {Direction} forward tunnel for session {SessionId}", direction, sessionId);
            if (DescribeLocalBindFailure(ex, request) is { } message)
            {
                throw new TunnelBindFailedException(message, ex);
            }
            throw;
        }
    }

    /// <summary>
    /// 本机监听起不来(SSH 库报 <see cref="SshFailureReason.ForwardBindFailed" />)时给一句本地化的话:
    /// 在哪儿(配置里的地址与端口、或套接字路径)、为什么(套接字错误码)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 库的消息是写给开发者的中文,宿主的 <c>SshInterop.Localize</c> 对这个原因码刻意不翻 —— 换成一句通用文案会丢掉消息里的路径与端口。
    /// 隧道这里配置在手,路径与端口不必从库的句子里抠,所以在这一层说清楚。
    /// </para>
    /// <para>
    /// TCP 端口被占(<see cref="SocketError.AddressAlreadyInUse" />)不在这里说:面板按「本地端口 N 已被其它程序占用」报,那句已经够具体。
    /// 远程转发的监听在服务器上,失败是 <see cref="SshFailureReason.ForwardRejected" />,不归这里。
    /// </para>
    /// </remarks>
    internal static string? DescribeLocalBindFailure(Exception ex, PortForwardRequest request)
    {
        if (request.Kind is not (PortForwardKind.Local or PortForwardKind.Dynamic))
        {
            return null;
        }
        bool bindFailed = false;
        SocketException? socket = null;
        Exception? innermost = null;
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            bindFailed |= e is SshException { Reason: SshFailureReason.ForwardBindFailed };
            socket ??= e as SocketException;
            innermost = e;
        }
        if (!bindFailed)
        {
            return null;
        }
        if (request.BoundSocketPath is { } path)
        {
            string detail = socket is not null
                ? socket.SocketErrorCode.ToString()
                : innermost is not null and not SshException ? innermost.Message : string.Empty;
            return detail.Length == 0
                ? Strings.Format("TunnelSvc_LocalSocketBindFailedNoDetail", path)
                : Strings.Format("TunnelSvc_LocalSocketBindFailed", path, detail);
        }
        if (socket?.SocketErrorCode is SocketError.AddressAlreadyInUse)
        {
            return null;
        }
        string endpoint = $"{HostAddress.Bracket(HostAddress.Normalize(request.BoundHost))}:{request.BoundPort}";
        return socket is null
            ? Strings.Format("TunnelSvc_LocalBindFailedNoDetail", endpoint)
            : Strings.Format("TunnelSvc_LocalBindFailed", endpoint, socket.SocketErrorCode.ToString());
    }
}
