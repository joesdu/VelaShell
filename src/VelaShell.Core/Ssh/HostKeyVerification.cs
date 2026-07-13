namespace VelaShell.Core.Ssh;

/// <summary>主机密钥(指纹)在本地已知主机记录中的校验结果。</summary>
public enum HostKeyVerification
{
    /// <summary>指纹已存在于已知主机记录且匹配,主机可信。</summary>
    Trusted,

    /// <summary>该主机首次连接,尚无已知指纹记录。</summary>
    Unknown,

    /// <summary>指纹与已知记录不一致,可能存在中间人风险。</summary>
    Changed
}
