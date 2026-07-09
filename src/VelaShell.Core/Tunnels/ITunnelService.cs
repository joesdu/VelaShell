using DynamicData;
using VelaShell.Core.Models;

namespace VelaShell.Core.Tunnels;

public interface ITunnelService : IAsyncDisposable
{
    IObservableList<TunnelInfo> GetActiveTunnels(Guid sessionId);
    Task<TunnelInfo> CreateLocalForwardAsync(Guid sessionId, TunnelConfig config, CancellationToken cancellationToken = default);
    Task<TunnelInfo> CreateRemoteForwardAsync(Guid sessionId, TunnelConfig config, CancellationToken cancellationToken = default);
    Task StopTunnelAsync(Guid tunnelId, CancellationToken cancellationToken = default);
}
