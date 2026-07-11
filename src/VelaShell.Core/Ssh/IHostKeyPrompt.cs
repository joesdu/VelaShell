namespace VelaShell.Core.Ssh;

/// <summary>主机指纹人工确认的用户裁决(与主流 SSH 客户端一致的三选项)。</summary>
public enum HostKeyDecision
{
    /// <summary>取消:拒绝该指纹并中止连接。</summary>
    Reject = 0,

    /// <summary>仅本次信任:放行连接但不写入已信任主机,本次应用运行内有效。</summary>
    TrustOnce = 1,

    /// <summary>永久信任:放行连接并写入已信任主机(known_hosts)。</summary>
    TrustPermanently = 2
}

/// <summary>
/// 主机指纹的人工确认入口(设置 → 安全审计 → 主机信任策略)。由 UI 层实现
/// (App 弹 HostKeyPromptView);在 SSH 握手线程上被同步等待。
/// </summary>
public interface IHostKeyPrompt
{
    /// <summary>询问用户如何处置该指纹;实现方失败时必须返回 <see cref="HostKeyDecision.Reject" />(fail-closed)。</summary>
    Task<HostKeyDecision> DecideAsync(string host, int port, string keyType, string fingerprint, HostKeyVerification verification);
}
