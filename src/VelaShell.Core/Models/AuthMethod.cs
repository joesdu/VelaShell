namespace VelaShell.Core.Models;

/// <summary>
/// SSH 认证方式类型
/// </summary>
public enum AuthMethod
{
    /// <summary>
    /// 基于密码的认证
    /// </summary>
    Password,

    /// <summary>
    /// 私钥认证(RSA、ED25519、ECDSA)
    /// </summary>
    PrivateKey
}
