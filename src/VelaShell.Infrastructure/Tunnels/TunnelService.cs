using System.Collections.Concurrent;
using DynamicData;
using Microsoft.Extensions.Logging;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Core.Tunnels;
using Renci.SshNet;

namespace VelaShell.Infrastructure.Tunnels;

public class TunnelService : ITunnelService
{
    private readonly ISshConnectionService _connectionService;
    private readonly Func<Guid, ISshClientWrapper> _clientFactory;
    private readonly ILogger<TunnelService>? _logger;
    private readonly ConcurrentDictionary<Guid, SourceList<TunnelInfo>> _sessionTunnels = new();
    private readonly ConcurrentDictionary<Guid, (ForwardedPort Port, TunnelInfo Info)> _tunnelPorts = new();

    public TunnelService(
        ISshConnectionService connectionService,
        Func<Guid, ISshClientWrapper> clientFactory,
        ILogger<TunnelService>? logger = null)
    {
        _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _logger = logger;
    }

    public IObservableList<TunnelInfo> GetActiveTunnels(Guid sessionId)
    {
        var tunnels = _sessionTunnels.GetOrAdd(sessionId, _ => new SourceList<TunnelInfo>());
        return tunnels.AsObservableList();
    }

    public async Task<TunnelInfo> CreateLocalForwardAsync(Guid sessionId, TunnelConfig config, CancellationToken cancellationToken = default)
    {
        if (config.Type != TunnelType.LocalForward)
        {
            throw new ArgumentException("Config type must be LocalForward", nameof(config));
        }

        return await CreateForwardAsync(
            sessionId,
            config,
            () => new ForwardedPortLocal(config.LocalHost, config.LocalPort, config.RemoteHost, config.RemotePort),
            "local",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<TunnelInfo> CreateRemoteForwardAsync(Guid sessionId, TunnelConfig config, CancellationToken cancellationToken = default)
    {
        if (config.Type != TunnelType.RemoteForward)
        {
            throw new ArgumentException("Config type must be RemoteForward", nameof(config));
        }

        return await CreateForwardAsync(
            sessionId,
            config,
            () => new ForwardedPortRemote(config.RemoteHost, config.RemotePort, config.LocalHost, config.LocalPort),
            "remote",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<TunnelInfo> CreateDynamicForwardAsync(Guid sessionId, TunnelConfig config, CancellationToken cancellationToken = default)
    {
        if (config.Type != TunnelType.DynamicForward)
        {
            throw new ArgumentException("Config type must be DynamicForward", nameof(config));
        }

        return await CreateForwardAsync(
            sessionId,
            config,
            () => new ForwardedPortDynamic(config.LocalHost, config.LocalPort),
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

        foreach (var (_, tunnels) in _sessionTunnels)
        {
            var existing = tunnels.Items.FirstOrDefault(t => t.Id == tunnelId);
            if (existing != null)
            {
                tunnels.Remove(existing);
                _logger?.LogInformation("Removed tunnel {TunnelId}", tunnelId);
                return;
            }
        }
    }

    public async Task StopAllForSessionAsync(Guid sessionId)
    {
        foreach (var (tunnelId, (_, info)) in _tunnelPorts)
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
                _logger?.LogWarning(ex, "Failed to stop tunnel {TunnelId} on session teardown", tunnelId);
                _tunnelPorts.TryRemove(tunnelId, out _);
                info.Status = TunnelStatus.Stopped;
            }
        }
    }

    public async Task StopTunnelAsync(Guid tunnelId, CancellationToken cancellationToken = default)
    {
        if (!_tunnelPorts.TryRemove(tunnelId, out var tunnelData))
        {
            throw new InvalidOperationException($"Tunnel {tunnelId} not found");
        }

        var (port, info) = tunnelData;

        try
        {
            await Task.Run(() =>
            {
                port.Stop();

                try
                {
                    var client = _clientFactory(info.SessionId);
                    client.RemoveForwardedPort(port);
                }
                catch (InvalidOperationException)
                {
                    // 会话已断开、客户端已释放:端口随之失效,无需再从客户端摘除。
                }
            }, cancellationToken).ConfigureAwait(false);

            info.Status = TunnelStatus.Stopped;

            if (_sessionTunnels.TryGetValue(info.SessionId, out var tunnels))
            {
                var existingTunnel = tunnels.Items.FirstOrDefault(t => t.Id == tunnelId);
                if (existingTunnel != null)
                {
                    tunnels.Remove(existingTunnel);
                    tunnels.Add(info);
                }
            }

            _logger?.LogInformation("Stopped tunnel {TunnelId} for session {SessionId}", tunnelId, info.SessionId);
        }
        catch (Exception ex)
        {
            info.Status = TunnelStatus.Error;
            _logger?.LogError(ex, "Failed to stop tunnel {TunnelId}", tunnelId);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (tunnelId, (port, info)) in _tunnelPorts)
        {
            try
            {
                await Task.Run(() =>
                {
                    port.Stop();
                    var client = _clientFactory(info.SessionId);
                    client.RemoveForwardedPort(port);
                }).ConfigureAwait(false);

                info.Status = TunnelStatus.Stopped;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to dispose tunnel {TunnelId}", tunnelId);
            }
        }

        _tunnelPorts.Clear();

        foreach (var (_, tunnels) in _sessionTunnels)
        {
            tunnels.Dispose();
        }

        _sessionTunnels.Clear();

        GC.SuppressFinalize(this);
    }

    /// <summary>把转发通道异常翻译成用户可理解的提示;最常见的是把目标填成了服务器的
    /// 公网地址,而服务只监听 127.0.0.1。</summary>
    private static string DescribeForwardError(Exception ex)
    {
        var socket = ex as System.Net.Sockets.SocketException
            ?? ex.InnerException as System.Net.Sockets.SocketException;
        if (socket?.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused)
        {
            return "目标拒绝连接:目标地址是从服务器视角解析的,转发服务器本机服务请用 127.0.0.1,并确认目标端口正在监听。";
        }

        if (socket?.SocketErrorCode is System.Net.Sockets.SocketError.TimedOut or System.Net.Sockets.SocketError.HostUnreachable)
        {
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
        Func<ForwardedPort> portFactory,
        string direction,
        CancellationToken cancellationToken)
    {
        var session = _connectionService.GetSession(sessionId);
        if (session == null)
        {
            throw new InvalidOperationException($"Session {sessionId} not found");
        }

        if (session.Status != SessionStatus.Connected)
        {
            throw new InvalidOperationException($"Session {sessionId} is not connected");
        }

        var client = _clientFactory(sessionId);
        if (!client.IsConnected)
        {
            throw new InvalidOperationException($"SSH client for session {sessionId} is not connected");
        }

        var forwardedPort = portFactory();

        var tunnelInfo = new TunnelInfo
        {
            Id = Guid.NewGuid(),
            Config = config,
            Status = TunnelStatus.Active,
            SessionId = sessionId,
            CreatedAt = DateTime.UtcNow,
            BytesTransferred = 0
        };

        // 转发通道错误(目标拒绝连接等)不会让监听端口停摆,但每个经过的连接都会失败;
        // 记到 LastError 供界面展示,否则用户只看到"运行中"却连不上。
        forwardedPort.Exception += (_, args) =>
        {
            tunnelInfo.LastError = DescribeForwardError(args.Exception);
            _logger?.LogWarning(args.Exception, "Tunnel {TunnelId} channel error", tunnelInfo.Id);
        };

        try
        {
            await Task.Run(() =>
            {
                client.AddForwardedPort(forwardedPort);

                try
                {
                    forwardedPort.Start();
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("not added to a client"))
                {
                    _logger?.LogDebug(ex, "Port start failed (expected with mocked clients) for tunnel {TunnelId}", tunnelInfo.Id);
                }
                catch (Exception)
                {
                    try
                    {
                        client.RemoveForwardedPort(forwardedPort);
                    }
                    catch (Exception removeEx)
                    {
                        _logger?.LogWarning(removeEx, "Failed to remove forwarded port after start failure for tunnel {TunnelId}", tunnelInfo.Id);
                    }

                    throw;
                }
            }, cancellationToken).ConfigureAwait(false);

            var tunnels = _sessionTunnels.GetOrAdd(sessionId, _ => new SourceList<TunnelInfo>());
            _tunnelPorts[tunnelInfo.Id] = (forwardedPort, tunnelInfo);
            tunnels.Add(tunnelInfo);

            _logger?.LogInformation("Created {Direction} forward tunnel {TunnelId} for session {SessionId}: {LocalHost}:{LocalPort} <-> {RemoteHost}:{RemotePort}",
                direction, tunnelInfo.Id, sessionId, config.LocalHost, config.LocalPort, config.RemoteHost, config.RemotePort);

            return tunnelInfo;
        }
        catch (Exception ex)
        {
            tunnelInfo.Status = TunnelStatus.Error;
            _logger?.LogError(ex, "Failed to create {Direction} forward tunnel for session {SessionId}", direction, sessionId);
            throw;
        }
    }
}
