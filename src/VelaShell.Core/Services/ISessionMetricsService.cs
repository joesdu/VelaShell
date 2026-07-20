namespace VelaShell.Core.Services;

/// <summary>获取已连接会话的实时资源快照(资源面板 §11)。</summary>
public interface ISessionMetricsService
{
    /// <summary>
    /// 返回当前指标;当会话未连接或远端主机未暴露预期的探测项时返回 null。
    /// </summary>
    Task<SessionMetrics?> GetMetricsAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
