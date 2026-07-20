namespace VelaShell.Core.Models;

/// <summary>
/// SSH 连接信息与凭据
/// </summary>
public class ConnectionInfo
{
    /// <summary>
    /// 获取或设置主机名或 IP 地址
    /// </summary>
    public required string Host { get; init; }

    /// <summary>
    /// 获取或设置 SSH 端口(默认:22)
    /// </summary>
    public int Port { get; init; } = 22;

    /// <summary>
    /// 获取或设置用户名
    /// </summary>
    public required string Username { get; init; }

    /// <summary>
    /// 获取或设置认证方式
    /// </summary>
    public required AuthMethod AuthMethod { get; init; }

    /// <summary>
    /// 获取或设置密码(用于密码认证)
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// 获取或设置私钥文件路径(用于私钥认证)
    /// </summary>
    public string? PrivateKeyPath { get; init; }

    /// <summary>
    /// 获取或设置私钥口令(可选)
    /// </summary>
    public string? PrivateKeyPassphrase { get; init; }

    /// <summary>
    /// 跳板主机(ProxyJump):先连它,再经其本地转发端口连本机。递归嵌套即多段跳;
    /// 由工作流按 <c>SessionProfile.JumpHostProfileId</c> 链解析(带环检测)。null = 直连。
    /// </summary>
    public ConnectionInfo? JumpHost { get; init; }
}
