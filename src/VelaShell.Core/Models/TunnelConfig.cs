namespace VelaShell.Core.Models;

/// <summary>端口转发隧道的配置:类型、名称及本地/远端监听与目标地址。</summary>
public sealed class TunnelConfig
{
    /// <summary>转发类型:本地、远程或动态(SOCKS)。</summary>
    public required TunnelType Type { get; init; }

    /// <summary>隧道显示名称。</summary>
    public required string Name { get; init; }

    /// <summary>本地监听主机(通常为 127.0.0.1 或 0.0.0.0)。</summary>
    public required string LocalHost { get; init; }

    /// <summary>本地监听端口。</summary>
    public required uint LocalPort { get; init; }

    /// <summary>转发目标主机;动态转发(SOCKS)无固定目标,允许留空。</summary>
    public string RemoteHost { get; init; } = string.Empty;

    /// <summary>转发目标端口;动态转发(SOCKS)无固定目标,允许为 0。</summary>
    public uint RemotePort { get; init; }

    /// <summary>
    /// 承载会话掉线后是否自动重连并重建这条隧道。默认关闭:自动重连会替用户
    /// 重新拨号(可能触发凭据提示、也可能反复撞上同一个不可达的服务器),
    /// 该由用户按隧道自己决定。旧的持久化配置缺这个字段时按 false 反序列化。
    /// </summary>
    public bool AutoReconnect { get; init; }

    /// <summary>
    /// 程序启动时是否自动连上服务器并建立这条隧道。默认关闭:隧道的启停状态从不落盘,
    /// 重启后一律是已停止、等用户手动启动;只有勾了这一项的才在启动时替用户建好。
    /// 旧的持久化配置缺这个字段时按 false 反序列化。
    /// </summary>
    public bool AutoStart { get; init; }

    /// <summary>
    /// 限速:每个方向每秒最多搬多少应用字节,这条隧道的全部连接共用这个额度;<see langword="null" />(默认)不限。
    /// </summary>
    /// <remarks>
    /// 慢链路上开着好几条隧道时,不让一条下载把交互终端挤满。在隧道建立时交给 SSH 库,改了要重启隧道才生效
    /// (运行中的隧道本来就不能编辑)。旧的持久化配置缺这个字段时按不限反序列化。
    /// </remarks>
    public long? MaxBytesPerSecond { get; init; }

    /// <summary>
    /// 远程动态转发的放行名单(ssh_config 的 <c>PermitRemoteOpen</c>):远端经这条隧道能让本机连的 <c>主机:端口</c>,
    /// 主机可带 <c>*</c> / <c>?</c> 通配、端口可以是 <c>*</c>;单独一条 <c>any</c> 是全放。别的类型用不上,为 <see langword="null" />。
    /// </summary>
    /// <remarks>
    /// 没有默认值、不许留空:远程动态转发等于把本机变成远端的 SOCKS 代理,本机能到的内网远端都能到,
    /// 放哪些出去必须由用户明说(写法与校验见 <see cref="Tunnels.RemoteOpenRules" />)。
    /// </remarks>
    public IReadOnlyList<string>? PermitRemoteOpen { get; init; }

    /// <summary>
    /// 本机这头改用 Unix 域套接字:本地转发是在这个套接字文件上监听(代替 <see cref="LocalHost" />:<see cref="LocalPort" />),
    /// 远程转发是回连到本机的这个套接字。<see langword="null" />(默认)= 用主机与端口。
    /// </summary>
    /// <remarks>
    /// 套接字文件按文件权限隔离(非 Windows 上建好即设 0600),多用户机器上别人借不走这条隧道 —— 环回端口则对所有用户开放。
    /// Windows 10 1803 起支持 AF_UNIX,文件沿用所在目录的 ACL。两头各自可以是套接字:远程转发也可以只有本机这头是
    /// (服务器的端口回连到本机的套接字,<c>-R 端口:/本机/路径</c>)。旧的持久化配置缺这个字段时按 <see langword="null" /> 反序列化。
    /// </remarks>
    public string? LocalSocketPath { get; init; }

    /// <summary>
    /// 服务器那头改用 Unix 域套接字:本地转发是转到服务器上的这个套接字(<c>direct-streamlocal@openssh.com</c>,
    /// 代替 <see cref="RemoteHost" />:<see cref="RemotePort" />,如 <c>/var/run/docker.sock</c>),
    /// 远程转发是请服务器在这个路径上监听(<c>streamlocal-forward@openssh.com</c>)。<see langword="null" />(默认)= 用主机与端口。
    /// </summary>
    /// <remarks>
    /// 服务端要开着 <c>AllowStreamLocalForwarding</c>(OpenSSH 默认开)。两头各自可以是套接字:远程转发也可以只有服务器这头是
    /// (服务器上的套接字回连到本机的端口,<c>-R /服务器/路径:主机:端口</c>)。
    /// </remarks>
    public string? RemoteSocketPath { get; init; }
}
