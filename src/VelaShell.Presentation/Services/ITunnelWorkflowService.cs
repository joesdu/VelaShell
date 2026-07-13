using VelaShell.Core.Models;

namespace VelaShell.Presentation.Services;

/// <summary>面向 UI 的隧道工作流服务:按会话创建、查询与停止端口转发隧道。</summary>
public interface ITunnelWorkflowService
{
    /// <summary>获取指定会话当前处于活动状态的隧道列表。</summary>
    IReadOnlyList<TunnelInfo> GetActiveTunnels(Guid sessionId);

    /// <summary>在指定会话上按配置创建一条隧道,并返回其信息。</summary>
    Task<TunnelInfo> CreateTunnelAsync(Guid sessionId, TunnelConfig config, CancellationToken cancellationToken = default);

    /// <summary>停止指定标识的隧道。</summary>
    Task StopTunnelAsync(Guid tunnelId, CancellationToken cancellationToken = default);
}
