namespace VelaShell.Core.Ssh;

/// <summary>
/// 主机指纹的人工确认入口(设置 → 安全审计 → 主机信任策略)。由 UI 层实现
/// (App 弹 HostKeyPromptView);在 SSH 握手线程上被同步等待。
/// </summary>
public interface IHostKeyPrompt
{
    /// <summary>询问用户是否信任该指纹;true = 信任并记录。</summary>
    Task<bool> ConfirmAsync(string host, int port, string keyType, string fingerprint, HostKeyVerification verification);
}
