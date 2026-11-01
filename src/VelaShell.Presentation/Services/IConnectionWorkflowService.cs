using VelaShell.Core.Models;
using VelaShell.Core.Ssh;

namespace VelaShell.Presentation.Services;

/// <summary>
/// 插件连接类型的连通性探针:按配置真的开一次插件会话,随即关掉。
/// <para>
/// 插件连接(S3、Redis 之类)握手完全不是 SSH,拿 SSH 去试只会连出一个超时。
/// 但真正开一次插件会话要用到界面层才有的东西(SSH 隧道链路、凭据解密),
/// 所以这里只留一个可挂的委托,由界面层在启动时接线 —— 与
/// <c>PluginProtocolRegistry.ConnectionProposalHandler</c> 同一个套路。
/// </para>
/// </summary>
/// <param name="profile">要测的连接配置(凭据已解密)。</param>
/// <param name="cancellationToken">取消令牌。</param>
/// <returns>成功即正常返回;失败抛异常,由调用方翻成界面上的原因。</returns>
public delegate Task PluginConnectionProbe(SessionProfile profile, CancellationToken cancellationToken);

/// <summary>连接工作流服务:统一管理会话配置的读取/保存、连接测试、建立与断开。</summary>
public interface IConnectionWorkflowService
{
    /// <summary>
    /// 插件连接类型的探针。界面层接线;没接线时插件配置的「测试」会明确说"测不了",
    /// 而不是拿 SSH 去撞插件端口撞出一个超时。
    /// </summary>
    PluginConnectionProbe? PluginProbe { get; set; }

    /// <summary>获取已保存的全部会话配置。</summary>
    Task<IReadOnlyList<SessionProfile>> GetSavedProfilesAsync(CancellationToken cancellationToken = default);

    /// <summary>保存会话配置,返回持久化后的结果。</summary>
    Task<SessionProfile> SaveProfileAsync(SessionProfile profile, CancellationToken cancellationToken = default);

    /// <summary>对指定配置执行连接测试,返回测试结果。</summary>
    Task<ConnectionTestResult> TestConnectionAsync(SessionProfile profile, CancellationToken cancellationToken = default);

    /// <summary>
    /// 同 <see cref="TestConnectionAsync(SessionProfile, CancellationToken)" />,并把这次连接(连同跳板链上每一跳)收发的每个报文的
    /// 元信息记进 <paramref name="packetTrace" />(连接诊断的协议轨迹;不记内容)。
    /// </summary>
    /// <param name="profile">要测的配置。</param>
    /// <param name="packetTrace">协议轨迹。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<ConnectionTestResult> TestConnectionAsync(
        SessionProfile profile, SshPacketTrace packetTrace, CancellationToken cancellationToken = default);

    /// <summary>
    /// 没人看着的连接测试(会话健康巡检):同 <see cref="TestConnectionAsync(SessionProfile, CancellationToken)" />,但要人回答的一律不问 ——
    /// 主机指纹、动态码、Tailscale 复核照拒绝 / 失败报回(<see cref="ConnectionInfo.IsUnattended" />);
    /// 插件配置不测。调用方的取消照原样抛出,不当成「连不上」。
    /// </summary>
    Task<ConnectionTestResult> TestConnectionUnattendedAsync(SessionProfile profile, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按指定配置建立 SSH 连接,返回已连接的会话。连上之后只把库里那份的最近连接时间改掉(库里没有的临时配置不落库),
    /// 不把 <paramref name="profile" /> 整份写回 —— 调用方手里的副本可能早就过时了。
    /// </summary>
    Task<SshSession> ConnectProfileAsync(SessionProfile profile, CancellationToken cancellationToken = default);

    /// <summary>
    /// 登录框交回的那份连上之后调用:把登录框改得到的几项(用户名、认证方式、记住密码与认证材料,见
    /// <see cref="SessionProfileMerge.CopyLoginFields" />)写进库里那份,其余字段以库里为准。密码落不落盘照「记住密码」与常规页的总开关;
    /// 库里没有这条配置(临时配置)时什么都不写。
    /// </summary>
    /// <param name="profile">登录框交回、刚用它连上的那份。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task SaveLoginChangesAsync(SessionProfile profile, CancellationToken cancellationToken = default);

    /// <summary>
    /// 断线后的自动重连:同 <see cref="ConnectProfileAsync" />,但主机指纹要人裁决时直接拒绝、不弹窗
    /// (<see cref="ConnectionInfo.IsAutomaticReconnect" />)。被拒时抛 <see cref="VelaSshHostKeyRejectedException" />。
    /// </summary>
    Task<SshSession> ReconnectProfileAutomaticallyAsync(SessionProfile profile, CancellationToken cancellationToken = default);

    /// <summary>断开指定会话。</summary>
    Task DisconnectAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
