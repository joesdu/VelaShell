using DynamicData;
using VelaShell.Core.Models;

namespace VelaShell.Core.Tunnels;

public interface ITunnelService : IAsyncDisposable
{
    IObservableList<TunnelInfo> GetActiveTunnels(Guid sessionId);
    Task<TunnelInfo> CreateLocalForwardAsync(Guid sessionId, TunnelConfig config, CancellationToken cancellationToken = default);
    Task<TunnelInfo> CreateRemoteForwardAsync(Guid sessionId, TunnelConfig config, CancellationToken cancellationToken = default);

    /// <summary>动态转发(SOCKS 代理,等价 ssh -D):监听本地端口,目标由客户端协商。</summary>
    Task<TunnelInfo> CreateDynamicForwardAsync(Guid sessionId, TunnelConfig config, CancellationToken cancellationToken = default);

    Task StopTunnelAsync(Guid tunnelId, CancellationToken cancellationToken = default);

    /// <summary>彻底移除一条隧道记录(活动中的先停止),用于删除或"停止后重建"前的清理。</summary>
    Task RemoveTunnelAsync(Guid tunnelId, CancellationToken cancellationToken = default);

    /// <summary>会话断开时停止其全部隧道并标记为已停止(尽力而为,不抛出)。</summary>
    Task StopAllForSessionAsync(Guid sessionId);
}
