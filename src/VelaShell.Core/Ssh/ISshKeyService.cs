namespace VelaShell.Core.Ssh;

/// <summary>~/.ssh 下的一个密钥对。</summary>
/// <param name="Name">密钥名称(私钥文件名)。</param>
/// <param name="Type">密钥类型(如 RSA、ED25519)。</param>
/// <param name="Fingerprint">密钥指纹。</param>
/// <param name="PrivateKeyPath">私钥文件的完整路径。</param>
/// <param name="PublicKeyLine">OpenSSH 格式的公钥行;无公钥时为 null。</param>
public sealed record SshKeyInfo(
    string Name,
    string Type,
    string Fingerprint,
    string PrivateKeyPath,
    string? PublicKeyLine);

/// <summary>SSH 密钥管理(设置 - 密钥管理页):枚举、导入、生成、删除 ~/.ssh 下的密钥对。</summary>
public interface ISshKeyService
{
    /// <summary>枚举 ~/.ssh 下已有的密钥对。</summary>
    Task<List<SshKeyInfo>> ListKeysAsync(CancellationToken cancellationToken = default);

    /// <summary>把外部私钥(及同名 .pub)复制进 ~/.ssh。返回 null 表示同名文件已存在。</summary>
    Task<SshKeyInfo?> ImportKeyAsync(string sourcePrivateKeyPath, CancellationToken cancellationToken = default);

    /// <summary>生成 RSA 密钥对(PEM 私钥 + OpenSSH 公钥行)。名称已存在时抛 IOException。</summary>
    Task<SshKeyInfo> GenerateRsaKeyAsync(string name, int bits = 4096, CancellationToken cancellationToken = default);

    /// <summary>删除指定名称的密钥对(私钥及同名 .pub)。</summary>
    Task DeleteKeyAsync(string name, CancellationToken cancellationToken = default);
}
