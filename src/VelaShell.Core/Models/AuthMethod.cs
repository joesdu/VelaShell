namespace VelaShell.Core.Models;

/// <summary>
/// SSH 认证方式类型
/// </summary>
/// <remarks>
/// <b>新值只能加在末尾。</b>这个枚举没挂 <c>JsonStringEnumConverter</c>(同仓的
/// <c>QuickCommandGroupKind</c> 挂了),落盘的是序号而不是名字 —— 往中间插一个值,
/// 已存档配置里的 <c>1</c> 就会从"私钥"变成别的东西,用户的连接会静默改用另一套凭据。
/// </remarks>
public enum AuthMethod
{
    /// <summary>
    /// 基于密码的认证
    /// </summary>
    Password,

    /// <summary>
    /// 私钥认证(RSA、ED25519、ECDSA)
    /// </summary>
    PrivateKey,

    /// <summary>
    /// OpenSSH 用户证书认证:由 CA 签发的证书(<c>*-cert.pub</c>)配上与之匹配的私钥。
    /// </summary>
    /// <remarks>
    /// 证书本身不是凭据,签名仍由私钥出 —— 所以这一路复用
    /// <c>PrivateKeyPath</c> / <c>PrivateKeyPassphrase</c>,只额外多一个证书文件路径。
    /// 服务器认的是 CA 的签名而非某把具体公钥,因此换机器不必再往 authorized_keys 里加东西。
    /// </remarks>
    Certificate,

    /// <summary>
    /// 用本机 ssh-agent 里的密钥认证(Windows 的 OpenSSH Authentication Agent 服务,
    /// 或 <c>SSH_AUTH_SOCK</c> 指向的 agent)。
    /// </summary>
    /// <remarks>
    /// 私钥从不进入本进程,配置里也不存任何凭据 —— 签名由 agent 代做。
    /// agent 里有几把钥就逐把试,直到服务端接受。
    /// </remarks>
    Agent
}
