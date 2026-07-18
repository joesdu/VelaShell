using VelaShell.Core.Models;

namespace VelaShell.Presentation.Services;

/// <summary>
/// 隧道工作流服务:对 <see cref="Core.Tunnels.ITunnelService" /> 的编排层抽象,
/// 封装类型分派(CreateTunnelAsync 按 TunnelConfig.Type 路由到本地/远程/动态转发)
/// 与快照读取(GetActiveTunnels 返回列表副本,避免 UI 层直接持有 IObservableList)。
/// </summary>
public interface ITunnelWorkflowService
{
    /// <summary>获取指定会话当前活动隧道的列表快照。</summary>
    IReadOnlyList<TunnelInfo> GetActiveTunnels(Guid sessionId);

    /// <summary>
    /// 按 <see cref="TunnelConfig.Type" /> 自动路由到本地/远程/动态转发,
    /// 返回创建结果;配置中的 <see cref="TunnelConfig.Type" /> 不受支持时抛出
    /// <see cref="ArgumentOutOfRangeException" />。
    /// </summary>
    Task<TunnelInfo> CreateTunnelAsync(Guid sessionId, TunnelConfig config, CancellationToken cancellationToken = default);

    /// <summary>停止指定隧道(保留隧道记录)。</summary>
    Task StopTunnelAsync(Guid tunnelId, CancellationToken cancellationToken = default);

    /// <summary>彻底移除一条隧道记录(活动中的先停止)。</summary>
    Task RemoveTunnelAsync(Guid tunnelId, CancellationToken cancellationToken = default);
}
