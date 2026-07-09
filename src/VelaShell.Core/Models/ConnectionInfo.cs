namespace VelaShell.Core.Models;

/// <summary>
/// SSH connection information and credentials
/// </summary>
public class ConnectionInfo
{
    /// <summary>
    /// Gets or sets the hostname or IP address
    /// </summary>
    public required string Host { get; init; }

    /// <summary>
    /// Gets or sets the SSH port (default: 22)
    /// </summary>
    public int Port { get; init; } = 22;

    /// <summary>
    /// Gets or sets the username
    /// </summary>
    public required string Username { get; init; }

    /// <summary>
    /// Gets or sets the authentication method
    /// </summary>
    public required AuthMethod AuthMethod { get; init; }

    /// <summary>
    /// Gets or sets the password (for Password auth)
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Gets or sets the private key file path (for PrivateKey auth)
    /// </summary>
    public string? PrivateKeyPath { get; init; }

    /// <summary>
    /// Gets or sets the private key passphrase (optional)
    /// </summary>
    public string? PrivateKeyPassphrase { get; init; }

    /// <summary>跳板主机(ProxyJump):先连它,再经其本地转发端口连本机。递归嵌套即多段跳;
    /// 由工作流按 <c>SessionProfile.JumpHostProfileId</c> 链解析(带环检测)。null = 直连。</summary>
    public ConnectionInfo? JumpHost { get; init; }
}
