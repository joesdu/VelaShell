using System.Collections.Concurrent;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Core.Tunnels;

namespace VelaShell.Infrastructure.Tunnels;

/// <summary>
/// 端口转发通道管理服务:在指定 SSH 会话上创建本地/远程/动态转发,跟踪各通道的活动状态,
/// 并在停止或会话拆除时释放底层监听端口。以 <see cref="Guid" /> 会话为单位维护可观察的通道列表。
/// </summary>
public class TunnelService(
    ISshConnectionService connectionService,
    Func<Guid, ISshClientWrapper> clientFactory,
    ILogger<TunnelService>? logger = null) : ITunnelService
{
    private readonly Func<Guid, ISshClientWrapper> _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
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
        List<TunnelInfo> tunnels = _sessionTunnels.GetOrAdd(sessionId, _ => []);
        lock (tunnels)
        {
            return [.. tunnels];
        }
    }

    /// <summary>在指定会话上创建本地端口转发通道(本地监听端口 → 远端目标)。</summary>
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

    /// <summary>在指定会话上创建远程端口转发通道(远端监听端口 → 本地目标)。</summary>
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

    /// <summary>在指定会话上创建动态转发通道(本地 SOCKS 代理端口),按连接动态选择远端目标。</summary>
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
            // Stop 幂等且自带"客户端已随会话释放"的容错(见 IPortForwardHandle 契约)。
            // handle.Dispose 是纯内存操作(标记 + 字典移除),直接调用即可。
            handle.Dispose();
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
                await Task.Run(handle.Dispose).ConfigureAwait(false);
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
    /// 把转发通道异常翻译成用户可理解的提示;最常见的是把目标填成了服务器的
    /// 公网地址,而服务只监听 127.0.0.1。
    /// </summary>
    private static string DescribeForwardError(Exception ex)
    {
        SocketException? socket = ex as SocketException ?? ex.InnerException as SocketException;
        switch (socket?.SocketErrorCode)
        {
            case SocketError.ConnectionRefused:
                return Strings.Get("TunnelSvc_TargetRefused");
            case SocketError.TimedOut or SocketError.HostUnreachable:
                return Strings.Get("TunnelSvc_TargetUnreachable");
        }
        if (ex.Message.Contains("administratively prohibited", StringComparison.OrdinalIgnoreCase))
        {
            return Strings.Get("TunnelSvc_ForwardProhibited");
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
                tunnelInfo.LastError = DescribeForwardError(ex);
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
            throw;
        }
    }
}
