using DynamicData;
using VelaShell.Core.Models;

namespace VelaShell.Core.Ssh;

/// <summary>
/// SSH 连接服务:管理活动会话的建立、断开与查询,并以可观察列表对外暴露会话集合。
/// </summary>
public interface ISshConnectionService : IAsyncDisposable
{
    /// <summary>当前所有活动 SSH 会话的可观察列表。</summary>
    IObservableList<SshSession> Sessions { get; }

    /// <summary>按给定连接信息建立一个新的 SSH 会话并返回。</summary>
    Task<SshSession> ConnectAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken = default);

    /// <summary>断开并移除指定 Id 的会话。</summary>
    Task DisconnectAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>按 Id 获取会话;不存在时返回 <c>null</c>。</summary>
    SshSession? GetSession(Guid sessionId);

    /// <summary>按会话 Id 获取其底层 SSH 客户端包装器;不存在时返回 <c>null</c>。</summary>
    ISshClientWrapper? GetClient(Guid sessionId);
}
