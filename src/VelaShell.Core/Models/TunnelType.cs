namespace VelaShell.Core.Models;

/// <summary>
/// 指定 SSH 端口转发隧道的类型。
/// </summary>
public enum TunnelType
{
    /// <summary>
    /// 本地转发:localhost:localPort → remoteHost:remotePort(经由 SSH 服务器)
    /// </summary>
    LocalForward,

    /// <summary>
    /// 远程转发:sshServer:remotePort → localhost:localPort
    /// </summary>
    RemoteForward,

    /// <summary>
    /// Dynamic forward: localhost:localPort 作为 SOCKS 代理,目标由客户端协议协商(ssh -D)。
    /// </summary>
    DynamicForward
}
