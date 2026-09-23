namespace VelaShell.Core.Models;

/// <summary>
/// 一条 SSH 连接配置在协议层的可选能力:压缩、agent 转发、X11 转发。
/// </summary>
/// <remarks>
/// <para>
/// 三项默认全关,与 OpenSSH 的出厂行为一致。它们都是「按机器开」的东西:
/// 压缩只在慢链路上划算,两个转发都等于把本机的一部分能力借给远端 ——
/// 没有理由对所有机器一刀切地打开。
/// </para>
/// <para>
/// 与 <see cref="TerminalOverrides" /> 一样单开一个对象:一项都没开时整个存 <c>null</c>,
/// 老配置零迁移,也不会给每条配置的 JSON 平白多一段。
/// </para>
/// </remarks>
public sealed class SshSessionOptions
{
    /// <summary>
    /// 启用 <c>zlib@openssh.com</c> 压缩(服务端不支持时自动退回不压缩)。
    /// </summary>
    /// <remarks>
    /// 高延迟、低带宽的链路上,终端输出与文本文件能省下可观的流量;
    /// 局域网或传输已压缩的数据(图片、压缩包)时只会白白多耗 CPU。
    /// 跳板链上只作用于这一跳。
    /// </remarks>
    public bool Compression { get; set; }

    /// <summary>
    /// 把本机 ssh-agent 转发给远端(<c>ssh -A</c>):在这台机器上继续 ssh 到下一跳时,
    /// 用的是本机 agent 里的密钥,私钥不必拷到服务器上。
    /// </summary>
    /// <remarks>
    /// 转发期间,远端的 root 可以借你的 agent 签名 —— 只对信得过的机器打开。
    /// </remarks>
    public bool AgentForwarding { get; set; }

    /// <summary>
    /// 只转发这些密钥(OpenSSH 公钥行,<c>类型 base64 [注释]</c>);<see langword="null" /> = agent 里的钥全部可见。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 一台跳板机通常只需要下一跳那一把钥,没有理由看得见 agent 里的全部。
    /// 存公钥本身而不是指纹:开 shell 时直接就能交给库,不必先连 agent 去按指纹找钥。
    /// </para>
    /// <para>
    /// ⚠️ <b>非 null 就是「限定」,哪怕一把都解析不出来也不会退回「全部」</b> ——
    /// 那种情况下干脆不转发(见 <c>SshForwardingOptions.Agent</c>)。空列表在界面上保存不了。
    /// </para>
    /// </remarks>
    public List<string>? AgentForwardKeys { get; set; }

    /// <summary>
    /// 远端每次请求用 agent 签名时,先弹窗问一次(类似 <c>ssh-add -c</c>)。
    /// </summary>
    /// <remarks>
    /// 对跳板场景这是唯一能看见「那台机器拿你的身份做了什么、做了几次」的办法。
    /// 没人应答时按拒绝处理。
    /// </remarks>
    public bool AgentForwardConfirm { get; set; }

    /// <summary>把远端图形程序的窗口转发到本机的 X 服务器上显示(<c>ssh -X</c> / <c>-Y</c>)。</summary>
    public bool X11Forwarding { get; set; }

    /// <summary>
    /// 本机 X 显示,如 <c>localhost:0.0</c>;null / 空 = 取 <c>DISPLAY</c> 环境变量,
    /// 再没有就用 <see cref="DefaultX11Display" />。
    /// </summary>
    public string? X11Display { get; set; }

    /// <summary>
    /// 受信任的 X11 转发(<c>ssh -Y</c>)。默认开。
    /// </summary>
    /// <remarks>
    /// 非受信模式要本机有 <c>xauth</c> 且 X 服务器支持 SECURITY 扩展,Windows 上的 X 服务器
    /// (VcXsrv / Xming / X410)两样通常都没有 —— 那里只有受信模式能用,默认值照顾的是这一边。
    /// 代价是远端 X 客户端对本机显示有完全的访问权。
    /// </remarks>
    public bool X11Trusted { get; set; } = true;

    /// <summary>没有 <c>DISPLAY</c> 时退回的显示:Windows 上的 X 服务器默认监听 TCP 6000。</summary>
    public const string DefaultX11Display = "localhost:0.0";

    /// <summary>三项都没开(X11 的两个附属字段在没开 X11 时不算数)。</summary>
    public bool IsEmpty => !Compression && !AgentForwarding && !X11Forwarding;

    /// <summary>返回本对象的副本。</summary>
    /// <returns>与本实例等值的新实例。</returns>
    public SshSessionOptions Clone() =>
        new()
        {
            Compression = Compression,
            AgentForwarding = AgentForwarding,
            AgentForwardKeys = AgentForwardKeys is null ? null : [.. AgentForwardKeys],
            AgentForwardConfirm = AgentForwardConfirm,
            X11Forwarding = X11Forwarding,
            X11Display = X11Display,
            X11Trusted = X11Trusted
        };
}
