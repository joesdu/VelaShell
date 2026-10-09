// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §6.3.1  x11-req
//   行为规格:        velashell-docs/zh/ssh/spec/07-forwarding.md §7.5

namespace VelaShell.Ssh.Forwarding;

/// <summary>X11 转发的选项。</summary>
/// <remarks>
/// <para>
/// <b>默认是非受信模式</b>（对应 <c>ssh -X</c>）。受信模式（<c>ssh -Y</c>）
/// 把本机显示的完全控制权交给远端 —— X11 没有客户端隔离，
/// 连上同一个显示的任何客户端都能<b>读别人的按键、抓别人的窗口</b>。
/// </para>
/// </remarks>
public sealed record X11ForwardOptions
{
    /// <summary>用哪个显示；<see langword="null"/> 取 <c>DISPLAY</c>。</summary>
    public X11Display? Display { get; init; }

    /// <summary>受信模式（<c>ssh -Y</c>）。</summary>
    /// <remarks>
    /// <b>默认 <see langword="false"/>。</b> 打开它等于把本机所有图形会话
    /// 的输入输出交给远端，要有明确的理由。
    /// <para>
    /// 非受信模式需要本机有 <c>xauth</c>、且 X server 支持 SECURITY 扩展；
    /// Windows 上通常两者都没有，那里只能用受信模式。经 <see cref="LocalConnector"/> 接本机显示时不用 <c>xauth</c>：
    /// 连接器那一端自己按非受信处理这条连接。
    /// </para>
    /// </remarks>
    public bool IsTrusted { get; init; }

    /// <summary>转发的有效期。默认 20 分钟；<see cref="System.Threading.Timeout.InfiniteTimeSpan"/> 表示不过期。</summary>
    /// <remarks>
    /// <para>
    /// 过期之后新的 <c>x11</c> 通道一律拒绝（已经建好的不受影响）。
    /// </para>
    /// <para>
    /// 〔与 OpenSSH 的一处**有意差异**〕OpenSSH 的 <c>ForwardX11Timeout</c>
    /// 只管非受信模式。我们<b>两种模式都管</b> —— 因为「有效期只在某一种模式下
    /// 起作用」是一个会让人栽跟头的 API：受信模式恰恰是危险得多的那个，
    /// 却反而没有期限，说不通。
    /// </para>
    /// <para>
    /// 长会话要一直用的话，显式设成 <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>（对应 <c>ForwardX11Timeout 0</c>：整条连接期间都有效）。
    /// 非受信模式下它还决定 <c>xauth generate ... timeout</c>：有效期再加 60 秒，不过期时传 0（永不过期）——
    /// 见 <see cref="X11Forwarder.XAuthTimeoutSeconds"/>。
    /// </para>
    /// <para>
    /// 〔AGENTS 4.2〕「不限时」全库只有一种写法：<see cref="System.Threading.Timeout.InfiniteTimeSpan"/>。
    /// 曾经这里用 <see cref="TimeSpan.Zero"/> 表示不过期，而连接、认证与主机密钥裁决的时限都用 <c>InfiniteTimeSpan</c>。
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">不为正，也不是 <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>。</exception>
    public TimeSpan Timeout
    {
        get;
        init => field = value > TimeSpan.Zero || value == System.Threading.Timeout.InfiniteTimeSpan
            ? value
            : throw new ArgumentOutOfRangeException(nameof(Timeout), value, "有效期必须为正；不过期写 Timeout.InfiniteTimeSpan。");
    } = TimeSpan.FromMinutes(20);

    /// <summary><c>.Xauthority</c> 的路径；<see langword="null"/> 走默认。</summary>
    /// <remarks>
    /// 受信模式从这里读真 cookie；非受信模式下它是 <c>xauth</c> 连本机显示时用的授权
    /// （通过 <c>XAUTHORITY</c> 传给它）—— <b>这个文件本身永远不会被写</b>。
    /// </remarks>
    public string? XAuthorityPath { get; init; }

    /// <summary><c>xauth</c> 可执行文件的位置；<see langword="null"/> 用 <c>xauth</c>。</summary>
    public string? XAuthLocation { get; init; }

    /// <summary>只允许一条 X11 连接。</summary>
    /// <remarks>
    /// 默认 <see langword="false"/>：一个远端会话常常开多个 X 客户端，
    /// 设成 <see langword="true"/> 的话第二个就连不上了。
    /// 这一条同时发给服务端（<c>x11-req</c> 的 single connection 字段）
    /// <b>并在本端强制</b> —— 不把安全约束寄托在对端身上。
    /// </remarks>
    public bool IsSingleConnection { get; init; }

    /// <summary>同时允许的 X11 通道数上限。</summary>
    public int MaxConnections { get; init; } = 16;

    /// <summary>开会话时 X11 没开成，这次启动怎么办。</summary>
    /// <remarks>
    /// <para>
    /// <b>默认 <see cref="ForwardFailureMode.Fail"/>。</b>〔<c>velashell-docs/zh/ssh/spec/07</c> §7.5.8〕
    /// 调用方在这一次执行上显式要求的 X11，失败就抛 —— 他明确要 X11，静默降级等于骗他。
    /// </para>
    /// <para>
    /// 只有 X11 是由<b>连接级开关</b>打开的时候（比如 <c>ssh_config</c> 里的 <c>ForwardX11 yes</c>，
    /// 见 <see cref="Config.SshHostConfig.ApplyToShell"/>）才用 <see cref="ForwardFailureMode.Continue"/>：
    /// 否则一份存量配置会让这台主机上的所有会话都起不来。
    /// </para>
    /// <para>
    /// 在 <see cref="Session.SshConnectionExtensions.OpenShellAsync"/> /
    /// <see cref="Session.SshConnectionExtensions.ExecuteAsync"/> 里起作用：失败的原因放在
    /// <see cref="Channels.SshShell.X11SetupFailure"/> / <see cref="Channels.SshCommand.X11SetupFailure"/> 上，
    /// 并计入转发的错误计数（<see cref="ForwardMetrics.MeterName"/>）。取消照常抛出。
    /// </para>
    /// </remarks>
    public ForwardFailureMode FailureMode { get; init; }

    /// <summary>
    /// 本机显示的连接器:设了它,<c>x11</c> 通道不再去连 <see cref="Display" /> 的套接字,而是调它拿一条双工流
    /// (比如直接接进进程内嵌的 X server)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 〔<c>velashell-docs/zh/ssh/spec/07</c> §7.5.9〕假 cookie 的核对照旧 —— 那一层防的是远端,与本机这一端怎么接无关。
    /// 核对通过之后,建立报文里的 cookie 换成 <see cref="LocalCookie" />(没给就是空的),再写进这条流。
    /// 连接器那一端自己负责访问控制:受信模式下给出的流就等于一条已被信任的本机连接。
    /// </para>
    /// <para>
    /// 非受信模式(<see cref="IsTrusted" /> 为 <see langword="false" />)下不跑 <c>xauth</c> 去签受限 cookie ——
    /// 连接器后面未必有可供 <c>xauth</c> 去连的显示 —— 由连接器那一端把这条连接当成非受信的处理
    /// (比如进程内嵌的 X server 直接按 SECURITY 扩展的非受信语义限制它)。原先两者同时设是配置矛盾、开通道之前就抛。
    /// <see cref="Display" /> 仍然要给(或取 <c>DISPLAY</c>):屏幕号与诊断信息用它。
    /// </para>
    /// <para>
    /// 连接器返回的流归转发所有,用完释放;它抛出 <see cref="IOException" /> 或
    /// <see cref="InvalidOperationException" />(比如 X server 已经停了)时,这条通道按「本机显示连不上」处理。
    /// </para>
    /// </remarks>
    public Func<CancellationToken, ValueTask<Stream>>? LocalConnector { get; init; }

    /// <summary>
    /// 经 <see cref="LocalConnector" /> 连本机显示时建立报文里带的 cookie;空 = 不带 cookie(连接器那一端不查 cookie)。
    /// 不设 <see cref="LocalConnector" /> 时不起作用 —— 那时真 cookie 取自 <c>.Xauthority</c> 或 <c>xauth</c>。
    /// </summary>
    public ReadOnlyMemory<byte> LocalCookie { get; init; }

    /// <summary>默认选项。</summary>
    public static X11ForwardOptions Default { get; } = new();
}
