using VelaShell.Core.Models;

namespace VelaShell.Presentation.Services;

public interface ITunnelWorkflowService
{
    IReadOnlyList<TunnelInfo> GetActiveTunnels(Guid sessionId);
    Task<TunnelInfo> CreateTunnelAsync(Guid sessionId, TunnelConfig config, CancellationToken cancellationToken = default);
    Task StopTunnelAsync(Guid tunnelId, CancellationToken cancellationToken = default);
}
