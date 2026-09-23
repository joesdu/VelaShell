namespace VelaShell.Core.Ssh;

/// <summary>远端经 agent 转发请求签名时,交给用户裁决的那一次请求。</summary>
/// <param name="Target">哪条会话在要:<c>用户@主机:端口</c>。</param>
/// <param name="KeyType">密钥类型,如 <c>ssh-ed25519</c>。</param>
/// <param name="Fingerprint">密钥指纹(<c>SHA256:…</c>)。</param>
/// <param name="Comment">这把钥在 agent 里的注释,通常是私钥文件路径;agent 没给时为空串。</param>
/// <param name="Timeout">多久没人应答就按拒绝处理;弹窗据此告诉用户还剩多久。</param>
public sealed record AgentSignRequest(string Target, string KeyType, string Fingerprint, string Comment, TimeSpan Timeout);

/// <summary>用户对一次 agent 签名请求的裁决。</summary>
public enum AgentSignDecision
{
    /// <summary>拒签。关窗、超时、出错一律落在这里。</summary>
    Deny,

    /// <summary>只允许这一次。</summary>
    AllowOnce,

    /// <summary>这条会话里这把钥之后的签名都不再问。</summary>
    AllowForSession,
}

/// <summary>
/// agent 转发的「逐次确认」:远端每次要用本机 agent 签名时问用户一次。
/// </summary>
/// <remarks>
/// 与 <see cref="IHostKeyPrompt" /> 同一个模式:基础设施层在后台线程上等,界面层负责弹窗。
/// 实现方必须 <b>fail-closed</b> —— 拿不到主窗口、弹窗出错、
/// 取消令牌触发(通道关了或超时)一律返回 <see cref="AgentSignDecision.Deny" />,并收起还开着的窗口。
/// </remarks>
public interface IAgentSignPrompt
{
    /// <summary>问用户是否允许这次签名。</summary>
    /// <param name="request">这次请求。</param>
    /// <param name="cancellationToken">取消时收起窗口并按拒绝处理。</param>
    Task<AgentSignDecision> ConfirmAsync(AgentSignRequest request, CancellationToken cancellationToken);
}
