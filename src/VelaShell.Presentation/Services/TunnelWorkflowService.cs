using VelaShell.Core.Models;
using VelaShell.Core.Tunnels;

namespace VelaShell.Presentation.Services;

/// <summary>隧道工作流服务:按配置类型分发本地/远程/动态转发的创建与停止,封装底层 <see cref="ITunnelService" />。</summary>
public sealed class TunnelWorkflowService(ITunnelService tunnelService) : ITunnelWorkflowService
{
    private readonly ITunnelService _tunnelService = tunnelService ?? throw new ArgumentNullException(nameof(tunnelService));

    /// <summary>获取指定会话当前活动的隧道列表。</summary>
    public IReadOnlyList<TunnelInfo> GetActiveTunnels(Guid sessionId) => [.. _tunnelService.GetActiveTunnels(sessionId).Items];

    /// <summary>按隧道配置的类型创建对应的本地/远程/动态转发隧道。</summary>
    public Task<TunnelInfo> CreateTunnelAsync(Guid sessionId, TunnelConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Type switch
        {
            TunnelType.LocalForward => _tunnelService.CreateLocalForwardAsync(sessionId, config, cancellationToken),
            TunnelType.RemoteForward => _tunnelService.CreateRemoteForwardAsync(sessionId, config, cancellationToken),
            TunnelType.DynamicForward => _tunnelService.CreateDynamicForwardAsync(sessionId, config, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(config), config.Type, @"Unsupported tunnel type.")
        };
    }

    /// <summary>停止指定标识的隧道。</summary>
    public Task StopTunnelAsync(Guid tunnelId, CancellationToken cancellationToken = default) => _tunnelService.StopTunnelAsync(tunnelId, cancellationToken);
}
