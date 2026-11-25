namespace VelaShell.Core.Models;

/// <summary>交互式 SSH 终端连接在认证后允许协商压缩的方向。枚举值随配置保存。</summary>
public enum SshTerminalCompressionMode
{
    /// <summary>终端连接的两个方向均不压缩。</summary>
    Off = 0,

    /// <summary>仅允许压缩发往服务器的终端输入。</summary>
    UploadOnly = 1,

    /// <summary>允许压缩终端输入与服务器发来的输出，需用户确认服务器受其管理。</summary>
    Bidirectional = 2,
}

/// <summary>
/// 一条 SSH 连接配置在协议层的可选能力:终端压缩、agent 转发、X11 转发、算法清单。
/// </summary>
/// <remarks>
/// <para>
/// 默认全关 / 全用默认,与 OpenSSH 的出厂行为一致。它们都是「按机器开」的东西:
/// 两个转发都等于把本机的一部分能力借给远端 ——
/// 没有理由对所有机器一刀切地打开。
/// </para>
/// <para>
/// 与 <see cref="TerminalOverrides" /> 一样单开一个对象:一项都没开时整个存 <c>null</c>,
/// 老配置零迁移,也不会给每条配置的 JSON 平白多一段。
/// </para>
/// </remarks>
public sealed class SshSessionOptions
{
    /// <summary>旧版配置字段。保留以兼容已有配置，连接时忽略。</summary>
    public bool Compression { get; set; }

    /// <summary>仅用于交互式终端。双向表示用户已确认信任这台服务器发来的压缩数据。</summary>
    public SshTerminalCompressionMode TerminalCompression
    {
        get;
        set => field = Enum.IsDefined(value) ? value : SshTerminalCompressionMode.Off;
    }

    /// <summary>
    /// 按键时序混淆(OpenSSH 的 <c>ObscureKeystrokeTiming</c>):交互式 shell 的输入按固定节拍(20 毫秒)发出,
    /// 打字期间没有输入的节拍上发等长的掩护报文,按键之间的间隔在网上就看不出来了。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 每次按键一个报文,按键间隔是公认的侧信道 —— 输口令、敲命令的节奏能推测出内容。代价是带宽:
    /// 打字时每秒约 50 个报文(服务端还回同样多),每次按键最多晚一个节拍发出;闲着时一个报文都不发,粘贴不受影响。
    /// 掩护要服务端认 <c>ping@openssh.com</c>(OpenSSH 9.5 起),不认时只攒批、不发掩护。
    /// </para>
    /// <para>默认关(与 OpenSSH 不同:它默认开),理由是带宽代价。只对终点那一跳的交互式 shell 生效,SFTP 配置不存。</para>
    /// </remarks>
    public bool ObscureKeystrokeTiming { get; set; }

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

    /// <summary>
    /// 这条连接用哪个 ssh-agent(ssh_config 的 <c>IdentityAgent</c>):Windows 命名管道 <c>\\.\pipe\名字</c>,或 Unix 域套接字的路径
    /// (开头的 <c>~</c> 连接时展开)。<see langword="null" /> = 系统默认(<c>SSH_AUTH_SOCK</c> / Windows 的 OpenSSH agent 服务、Pageant)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「SSH Agent」认证、agent 转发与「自动加载密钥到 Agent」都用它 —— 与 OpenSSH 一致:<c>IdentityAgent</c> 指了别的 agent 时,
    /// <c>ForwardAgent yes</c> 转发的也是那一个。跳板链上每一跳各用各的。写法与检查见 <see cref="Ssh.SshAgentEndpoints" />。
    /// </para>
    /// <para>SSH 与 SFTP 都存(SFTP 也能用 agent 认证);只填了它也不算「空」,否则配置页存回 null 就丢了。</para>
    /// </remarks>
    public string? AgentEndpoint { get; set; }

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

    /// <summary>
    /// 放开老算法:<c>diffie-hellman-group14-sha1</c>、SHA-1 的 <c>ssh-rsa</c> 主机密钥、<c>hmac-sha1</c>。
    /// </summary>
    /// <remarks>
    /// 追加在默认清单<b>之后</b>:只有对端一个现代算法都不支持时才会落到它们上(老交换机、CentOS 6 一类)。
    /// 不含 CBC —— 本版没有实现,报给对端只会在只剩 CBC 的设备上「谈成」再在派生密钥时失败。
    /// </remarks>
    public bool LegacyAlgorithms { get; set; }

    /// <summary>密钥交换算法的自定义清单(OpenSSH 的 <c>KexAlgorithms</c> 写法);<see langword="null" /> = 默认。</summary>
    /// <remarks>
    /// 四个清单同一套写法:<c>+a,b</c> 追加到默认之后、<c>-a,b</c> 从默认里删掉(可带 <c>*</c> / <c>?</c> 通配)、
    /// <c>^a,b</c> 提到最前,不带前缀则整个替换。「默认」指放开老算法之后的那一份。
    /// </remarks>
    public string? KexAlgorithms { get; set; }

    /// <summary>主机密钥算法的自定义清单(<c>HostKeyAlgorithms</c> 写法);<see langword="null" /> = 默认。</summary>
    public string? HostKeyAlgorithms { get; set; }

    /// <summary>加密算法的自定义清单(<c>Ciphers</c> 写法),两个方向共用;<see langword="null" /> = 默认。</summary>
    public string? Ciphers { get; set; }

    /// <summary>MAC 算法的自定义清单(<c>MACs</c> 写法),两个方向共用;<see langword="null" /> = 默认。</summary>
    public string? Macs { get; set; }

    /// <summary>四个自定义清单是否至少写了一个。</summary>
    public bool HasCustomAlgorithms =>
        !string.IsNullOrWhiteSpace(KexAlgorithms) || !string.IsNullOrWhiteSpace(HostKeyAlgorithms)
        || !string.IsNullOrWhiteSpace(Ciphers) || !string.IsNullOrWhiteSpace(Macs);

    /// <summary>一项都没开(X11 的两个附属字段在没开 X11 时不算数)。</summary>
    public bool IsEmpty => TerminalCompression == SshTerminalCompressionMode.Off && !Compression
                           && !ObscureKeystrokeTiming && !AgentForwarding && !X11Forwarding && !LegacyAlgorithms
                           && !HasCustomAlgorithms && Ssh.SshAgentEndpoints.IsDefault(AgentEndpoint);

    /// <summary>返回本对象的副本。</summary>
    /// <returns>与本实例等值的新实例。</returns>
    public SshSessionOptions Clone() =>
        new()
        {
            Compression = Compression,
            TerminalCompression = TerminalCompression,
            ObscureKeystrokeTiming = ObscureKeystrokeTiming,
            AgentForwarding = AgentForwarding,
            AgentForwardKeys = AgentForwardKeys is null ? null : [.. AgentForwardKeys],
            AgentForwardConfirm = AgentForwardConfirm,
            AgentEndpoint = AgentEndpoint,
            X11Forwarding = X11Forwarding,
            X11Display = X11Display,
            X11Trusted = X11Trusted,
            LegacyAlgorithms = LegacyAlgorithms,
            KexAlgorithms = KexAlgorithms,
            HostKeyAlgorithms = HostKeyAlgorithms,
            Ciphers = Ciphers,
            Macs = Macs
        };
}
