using VelaShell.Core.Models;

namespace VelaShell.Presentation.Services;

/// <summary>连接工作流服务:统一管理会话配置的读取/保存、连接测试、建立与断开。</summary>
public interface IConnectionWorkflowService
{
    /// <summary>获取已保存的全部会话配置。</summary>
    Task<IReadOnlyList<SessionProfile>> GetSavedProfilesAsync(CancellationToken cancellationToken = default);

    /// <summary>保存会话配置,返回持久化后的结果。</summary>
    Task<SessionProfile> SaveProfileAsync(SessionProfile profile, CancellationToken cancellationToken = default);

    /// <summary>对指定配置执行连接测试,返回测试结果。</summary>
    Task<ConnectionTestResult> TestConnectionAsync(SessionProfile profile, CancellationToken cancellationToken = default);

    /// <summary>按指定配置建立 SSH 连接,返回已连接的会话。</summary>
    Task<SshSession> ConnectProfileAsync(SessionProfile profile, CancellationToken cancellationToken = default);

    /// <summary>断开指定会话。</summary>
    Task DisconnectAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
