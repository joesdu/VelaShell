namespace VelaShell.Core.Models;

/// <summary>
/// 记录一个已确认信任的 SSH 主机及其公钥指纹,用于后续连接时校验主机身份、防范中间人攻击。
/// </summary>
public class KnownHost
{
    /// <summary>目标主机的地址(主机名或 IP)。</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>目标主机的 SSH 端口,默认 22。</summary>
    public int Port { get; set; } = 22;

    /// <summary>主机公钥的原始内容(通常为 Base64 编码)。</summary>
    public string HostKey { get; set; } = string.Empty;

    /// <summary>主机公钥的类型,如 ssh-rsa、ssh-ed25519 等。</summary>
    public string KeyType { get; set; } = string.Empty;

    /// <summary>主机公钥的指纹,用于人工核对与快速比对。</summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>生成指纹所使用的散列算法,如 SHA256、MD5 等。</summary>
    public string Algorithm { get; set; } = string.Empty;

    /// <summary>首次记录并信任该主机的时间(UTC)。</summary>
    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;

    /// <summary>最近一次成功匹配到该主机的时间(UTC)。</summary>
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}
