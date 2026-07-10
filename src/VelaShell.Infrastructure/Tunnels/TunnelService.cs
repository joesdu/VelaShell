using System.Collections.Concurrent;
using System.Net.Sockets;
using DynamicData;
using Microsoft.Extensions.Logging;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Core.Tunnels;

namespace VelaShell.Infrastructure.Tunnels;

public class TunnelService(
    ISshConnectionService connectionService,
    Func<Guid, ISshClientWrapper> clientFactory,
    ILogger<TunnelService>? logger = null) : ITunnelService
{
    private readonly Func<Guid, ISshClientWrapper> _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    private readonly ISshConnectionService _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
    private readonly ConcurrentDictionary<Guid, SourceList<TunnelInfo>> _sessionTunnels = new();
    private readonly ConcurrentDictionary<Guid, (IPortForwardHandle Handle, TunnelInfo Info)> _tunnelPorts = new();

    public IObservableList<TunnelInfo> GetActiveTunnels(Guid sessionId)
    {
        SourceList<TunnelInfo> tunnels = _sessionTunnels.GetOrAdd(sessionId, _ => new());
        return tunnels.AsObservableList();
    }

    public async Task<TunnelInfo> CreateLocalForwardAsync(Guid sessionId, TunnelConfig config, CancellationToken cancellationToken = default)
    {
        if (config.Type != TunnelType.LocalForward)
        {
            throw new ArgumentException(@"Config type must be LocalForward", nameof(config));
        }
        return await CreateForwardAsync(sessionId,
                   config,
                   new(PortForwardKind.Local, config.LocalHost, config.LocalPort, config.RemoteHost, config.RemotePort),
                   "local",
                   cancellationToken).ConfigureAwait(false);
    }

    public async Task<TunnelInfo> CreateRemoteForwardAsync(Guid sessionId, TunnelConfig config, CancellationToken cancellationToken = default)
    {
        if (config.Type != TunnelType.RemoteForward)
        {
            throw new ArgumentException(@"Config type must be RemoteForward", nameof(config));
        }
        return await CreateForwardAsync(sessionId,
                   config,
                   new(PortForwardKind.Remote, config.RemoteHost, config.RemotePort, config.LocalHost, config.LocalPort),
                   "remote",
                   cancellationToken).ConfigureAwait(false);
    }

    public async Task<TunnelInfo> CreateDynamicForwardAsync(Guid sessionId, TunnelConfig config, CancellationToken cancellationToken = default)
    {
        if (config.Type != TunnelType.DynamicForward)
        {
            throw new ArgumentException(@"Config type must be DynamicForward", nameof(config));
        }
        return await CreateForwardAsync(sessionId,
                   config,
                   new(PortForwardKind.Dynamic, config.LocalHost, config.LocalPort),
                   "dynamic",
                   cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveTunnelAsync(Guid tunnelId, CancellationToken cancellationToken = default)
    {
        // 活动中的先停止(同时把记录状态置为 Stopped)。
        if (_tunnelPorts.ContainsKey(tunnelId))
        {
            await StopTunnelAsync(tunnelId, cancellationToken).ConfigureAwait(false);
        }
        foreach ((Guid _, SourceList<TunnelInfo> tunnels) in _sessionTunnels)
        {
            TunnelInfo? existing = tunnels.Items.FirstOrDefault(t => t.Id == tunnelId);
            if (existing is null)
            {
                continue;
            }
            tunnels.Remove(existing);
            logger?.LogInformation("Removed tunnel {TunnelId}", tunnelId);
            return;
        }
    }

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
            }
        }
    }

    public async Task StopTunnelAsync(Guid tunnelId, CancellationToken cancellationToken = default)
    {
        if (!_tunnelPorts.TryRemove(tunnelId, out (IPortForwardHandle Handle, TunnelInfo Info) tunnelData))
        {
            throw new InvalidOperationException($"Tunnel {tunnelId} not found");
        }
        (IPortForwardHandle handle, TunnelInfo info) = tunnelData;
        try
        {
            // Stop 幂等且自带"客户端已随会话释放"的容错(见 IPortForwardHandle 契约)。
            await Task.Run(handle.Dispose, cancellationToken).ConfigureAwait(false);
            info.Status = TunnelStatus.Stopped;
            if (_sessionTunnels.TryGetValue(info.SessionId, out SourceList<TunnelInfo>? tunnels))
            {
                TunnelInfo? existingTunnel = tunnels.Items.FirstOrDefault(t => t.Id == tunnelId);
                if (existingTunnel != null)
                {
                    tunnels.Remove(existingTunnel);
                    tunnels.Add(info);
                }
            }
            logger?.LogInformation("Stopped tunnel {TunnelId} for session {SessionId}", tunnelId, info.SessionId);
        }
        catch (Exception ex)
        {
            info.Status = TunnelStatus.Error;
            logger?.LogError(ex, "Failed to stop tunnel {TunnelId}", tunnelId);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach ((Guid tunnelId, (IPortForwardHandle handle, TunnelInfo info)) in _tunnelPorts)
        {
            try
            {
                await Task.Run(handle.Dispose).ConfigureAwait(false);
                info.Status = TunnelStatus.Stopped;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to dispose tunnel {TunnelId}", tunnelId);
            }
        }
        _tunnelPorts.Clear();
        foreach ((Guid _, SourceList<TunnelInfo> tunnels) in _sessionTunnels)
        {
            tunnels.Dispose();
        }
        _sessionTunnels.Clear();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 把转发通道异常翻译成用户可理解的提示;最常见的是把目标填成了服务器的
    /// 公网地址,而服务只监听 127.0.0.1。
    /// </summary>
    private static string DescribeForwardError(Exception ex)
    {
        SocketException? socket = ex as SocketException ?? ex.InnerException as SocketException;
        switch (socket?.SocketErrorCode)
        {
            case SocketError.ConnectionRefused:
                return "目标拒绝连接:目标地址是从服务器视角解析的,转发服务器本机服务请用 127.0.0.1,并确认目标端口正在监听。";
            case SocketError.TimedOut or SocketError.HostUnreachable:
                return "目标不可达:请确认目标主机从服务器可以访问(内网地址/防火墙)。";
        }
        if (ex.Message.Contains("administratively prohibited", StringComparison.OrdinalIgnoreCase))
        {
            return "服务器禁止了端口转发(sshd 配置 AllowTcpForwarding no)。";
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
            // StartPortForward 建立并启动监听,失败时不留下半挂的端口(见接口契约)。
            IPortForwardHandle handle = await Task.Run(() => client.StartPortForward(request), cancellationToken).ConfigureAwait(false);

            // 转发通道错误(目标拒绝连接等)不会让监听端口停摆,但每个经过的连接都会失败;
            // 记到 LastError 供界面展示,否则用户只看到"运行中"却连不上。
            handle.ChannelError += ex =>
            {
                tunnelInfo.LastError = DescribeForwardError(ex);
                logger?.LogWarning(ex, "Tunnel {TunnelId} channel error", tunnelInfo.Id);
            };
            SourceList<TunnelInfo> tunnels = _sessionTunnels.GetOrAdd(sessionId, _ => new());
            _tunnelPorts[tunnelInfo.Id] = (handle, tunnelInfo);
            tunnels.Add(tunnelInfo);
            logger?.LogInformation("Created {Direction} forward tunnel {TunnelId} for session {SessionId}: {LocalHost}:{LocalPort} <-> {RemoteHost}:{RemotePort}",
                direction, tunnelInfo.Id, sessionId, config.LocalHost, config.LocalPort, config.RemoteHost, config.RemotePort);
            return tunnelInfo;
        }
        catch (Exception ex)
        {
            tunnelInfo.Status = TunnelStatus.Error;
            logger?.LogError(ex, "Failed to create {Direction} forward tunnel for session {SessionId}", direction, sessionId);
            throw;
        }
    }
}
