using PulseTerm.Core.Models;
using PulseTerm.Core.Tunnels;

namespace PulseTerm.Presentation.Services;

public sealed class TunnelWorkflowService : ITunnelWorkflowService
{
    private readonly ITunnelService _tunnelService;

    public TunnelWorkflowService(ITunnelService tunnelService)
    {
        _tunnelService = tunnelService ?? throw new ArgumentNullException(nameof(tunnelService));
    }

    public IReadOnlyList<TunnelInfo> GetActiveTunnels(Guid sessionId)
        => _tunnelService.GetActiveTunnels(sessionId).Items.ToList();

    public Task<TunnelInfo> CreateTunnelAsync(Guid sessionId, TunnelConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.Type switch
        {
            TunnelType.LocalForward => _tunnelService.CreateLocalForwardAsync(sessionId, config, cancellationToken),
            TunnelType.RemoteForward => _tunnelService.CreateRemoteForwardAsync(sessionId, config, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(config), config.Type, "Unsupported tunnel type.")
        };
    }

    public Task StopTunnelAsync(Guid tunnelId, CancellationToken cancellationToken = default)
        => _tunnelService.StopTunnelAsync(tunnelId, cancellationToken);
}
