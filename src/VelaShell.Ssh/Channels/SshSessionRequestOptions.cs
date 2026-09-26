// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §6.3  x11-req
//   RFC 4254 §6.4  env
//   行为规格:      velashell-docs/zh/ssh/spec/05-connection.md §5.2;velashell-docs/zh/ssh/spec/07-forwarding.md §7.5.3、§7.5.8

using VelaShell.Ssh.Forwarding;

namespace VelaShell.Ssh.Channels;

/// <summary>
/// 在 session 通道上启动命令或 shell 时共用的请求参数 ——
/// <see cref="SshCommandOptions"/> 与 <see cref="SshShellOptions"/> 的基类。
/// </summary>
/// <remarks>
/// 两者在 <c>exec</c> / <c>shell</c> 之前发的请求是同一组（<c>x11-req</c> → <c>auth-agent-req</c> → <c>env</c>），
/// 参数写在一处，语义与默认值就不会两边各漂各的。
/// </remarks>
public abstract record SshSessionRequestOptions
{
    /// <summary>只给本库的两个派生类型用。</summary>
    private protected SshSessionRequestOptions()
    {
    }

    /// <summary>通道参数。</summary>
    public SshChannelOptions Channel { get; init; } = SshChannelOptions.Default;

    /// <summary>
    /// 要设的环境变量。
    /// </summary>
    /// <remarks>
    /// 〔决策 velashell-docs/zh/ssh/spec/05 §5.2〕<b><c>env</c> 请求不要求回复。</b>
    /// 绝大多数服务端的 <c>AcceptEnv</c> 只放行少数变量，被拒是常态而不是错误；
    /// 要求回复只会让每设一个变量多一个 RTT，并且把一个正常情况报成失败。
    /// 设失败的后果由使用者在远端自行观察。
    /// </remarks>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>请求 X11 转发（<c>ssh -X</c> / <c>-Y</c>）；<see langword="null"/> 表示不请求。</summary>
    /// <remarks>
    /// <para>
    /// <b>默认不请求。</b> X11 没有客户端隔离 —— 把本机显示交给远端，
    /// 等于把本机所有图形会话的输入输出交给远端（<c>velashell-docs/zh/ssh/spec/07</c> §7.5.1）。
    /// </para>
    /// <para>
    /// 〔<c>velashell-docs/zh/ssh/spec/07</c> §7.5.8〕在这里显式要求的，<b>失败就抛</b> ——
    /// 调用方明确要 X11，静默降级等于骗他。例外是
    /// <see cref="X11ForwardOptions.BestEffort"/> 为 <see langword="true"/> 的选项
    /// （连接级开关打开的，比如 <c>ssh_config</c> 的 <c>ForwardX11 yes</c>）：失败时不开 X11、
    /// 命令 / shell 照常启动，原因放在结果对象的 <c>X11SetupFailure</c> 上。
    /// </para>
    /// </remarks>
    public X11ForwardOptions? X11Forwarding { get; init; }

    /// <summary>请求 agent 转发（<c>ssh -A</c>）；<see langword="null"/> 表示不请求。</summary>
    /// <remarks>
    /// <b>默认不请求。</b> agent 转发让远端能用本机 agent 里的钥签名 ——
    /// 远端的 root 同样能用。<see cref="AgentForwardOptions.AllowedKeys"/> /
    /// <see cref="AgentForwardOptions.ConfirmEachSignature"/> 就是为这个存在的。显式要求而服务端拒绝时抛出。
    /// </remarks>
    public AgentForwardOptions? AgentForwarding { get; init; }

    /// <summary>
    /// 在 <c>exec</c> / <c>shell</c> 请求发出之前、其余请求都发完之后调用 —— 给库没有内置的通道请求留的位置。
    /// </summary>
    /// <remarks>
    /// 时序是（<c>pty-req</c>）→ <c>x11-req</c> → <c>auth-agent-req</c> → <c>env</c> → <b>这里</b> → <c>exec</c> / <c>shell</c>。
    /// 抛异常等于放弃这次启动（通道会被关掉）。
    /// </remarks>
    public Func<SshChannel, CancellationToken, ValueTask>? BeforeStart { get; init; }
}
