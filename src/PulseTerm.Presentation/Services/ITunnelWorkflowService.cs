using PulseTerm.Core.Models;

namespace PulseTerm.Presentation.Services;

public interface ITunnelWorkflowService
{
    IReadOnlyList<TunnelInfo> GetActiveTunnels(Guid sessionId);
    Task<TunnelInfo> CreateTunnelAsync(Guid sessionId, TunnelConfig config, CancellationToken cancellationToken = default);
    Task StopTunnelAsync(Guid tunnelId, CancellationToken cancellationToken = default);
}
