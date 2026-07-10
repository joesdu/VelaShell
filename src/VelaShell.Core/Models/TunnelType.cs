namespace VelaShell.Core.Models;

/// <summary>
/// Specifies the type of SSH port forwarding tunnel.
/// </summary>
public enum TunnelType
{
    /// <summary>
    /// Local forward: localhost:localPort → remoteHost:remotePort (via SSH server)
    /// </summary>
    LocalForward,

    /// <summary>
    /// Remote forward: sshServer:remotePort → localhost:localPort
    /// </summary>
    RemoteForward,

    /// <summary>
    /// Dynamic forward: localhost:localPort 作为 SOCKS 代理,目标由客户端协议协商(ssh -D)。
    /// </summary>
    DynamicForward
}
