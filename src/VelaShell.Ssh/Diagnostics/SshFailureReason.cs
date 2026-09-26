// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/08-failures.md §3

namespace VelaShell.Ssh.Diagnostics;

/// <summary>
/// 失败的分类。<b>这是 API 的一部分</b> —— 使用者据此写 <c>catch</c> 分支与 UI 文案。
/// </summary>
/// <remarks>
/// <para>
/// 取值一旦发布就不能改其含义。新增取值是兼容的，但使用者的 <c>switch</c>
/// 应当有 <c>default</c> 分支。
/// </para>
/// <para>
/// <b>为什么要有这个枚举</b>：异常的 <c>Message</c> 是给人看的，不是 API。
/// 让调用方为了区分「认证失败 / 超时 / 协商失败」去切消息字符串，
/// 会在库改动一个字的措辞时静默失效 —— 那种代码没有任何办法察觉自己已经坏了。
/// </para>
/// </remarks>
public enum SshFailureReason
{
    /// <summary>未分类。</summary>
    Unknown = 0,

    // ---- 拨号阶段 ----

    /// <summary>主机名解析失败。</summary>
    DnsFailure,

    /// <summary>TCP 连接被拒绝（对端没有服务在监听）。</summary>
    TcpRefused,

    /// <summary>TCP 连接超时。</summary>
    TcpTimeout,

    /// <summary>网络不可达。</summary>
    TcpUnreachable,

    /// <summary>代理拒绝转发。消息里会带上代理类型、地址与目标。</summary>
    ProxyRefused,

    /// <summary>代理要求认证，但未配置代理凭据。</summary>
    ProxyAuthRequired,

    // ---- 版本交换 ----

    /// <summary>对端不是 SSH 服务（没有发出合法的协议标识串）。</summary>
    NotAnSshServer,

    /// <summary>对端的协议版本不是 2.0 / 1.99。</summary>
    VersionMismatch,

    // ---- 密钥交换 ----

    /// <summary>
    /// 算法协商失败：某一类算法双方没有交集。
    /// </summary>
    /// <remarks>
    /// 对应的异常带**双方的完整算法名单**与对端版本串 ——
    /// 不需要再开一条连接去探对端支持什么。
    /// </remarks>
    NegotiationFailed,

    /// <summary>主机密钥被策略拒绝（含首次连接时用户拒绝信任）。</summary>
    HostKeyRejected,

    /// <summary>主机密钥与已记录的不符。</summary>
    HostKeyChanged,

    // ---- 认证 ----

    /// <summary>一次认证尝试失败。</summary>
    AuthenticationFailed,

    /// <summary>
    /// 所有可用的认证方法都试完了。
    /// </summary>
    /// <remarks>对应的异常带**逐方法的尝试记录**，含「因服务端不接受而跳过」的那些。</remarks>
    AuthenticationMethodExhausted,

    /// <summary>
    /// 服务端要求 <c>keyboard-interactive</c>（动态码 / OTP），但未配置相应凭据。
    /// </summary>
    /// <remarks>
    /// 单独一个取值，是为了让上层能说「这台机器需要动态码」
    /// 而不是笼统的「用户名或密码不正确」—— 后者会把用户引向一条永远改不对的路。
    /// </remarks>
    TwoFactorRequired,

    /// <summary>服务端要求先修改密码。</summary>
    PasswordExpired,

    // ---- 本地凭据（私钥、证书、ssh-agent）----

    /// <summary>私钥 / 证书 / 公钥文件读不出来（不存在、没有权限、IO 错误）。</summary>
    KeyFileUnreadable,

    /// <summary>私钥 / 证书 / 公钥的内容格式不对（损坏、截断、参数不成立）。</summary>
    KeyFormatInvalid,

    /// <summary>加密的私钥需要口令，而没有给。</summary>
    KeyPassphraseRequired,

    /// <summary>给了口令，但解不开这把私钥 —— 口令多半不对。</summary>
    KeyPassphraseIncorrect,

    /// <summary>
    /// 凭据材料彼此对不上，或者用错了地方：证书里的公钥与私钥不是一对、拿主机证书去登录。
    /// </summary>
    KeyMismatch,

    /// <summary>找不到或连不上 ssh-agent，或者它的端点不可信。</summary>
    AgentUnavailable,

    /// <summary>ssh-agent 拒绝了请求（拒签、拒绝加钥 —— 常见于 <c>ssh-add -c</c> 的确认被拒、agent 被锁）。</summary>
    AgentRefused,

    // ---- 通用 ----

    /// <summary>某个阶段超时。具体哪一步看 <see cref="SshPhase"/>。</summary>
    Timeout,

    /// <summary>
    /// 保活探测连续无应答，判定连接已死。
    /// </summary>
    /// <remarks><b>自动重连策略应当只对这一类生效</b>，而不是对所有断开。</remarks>
    KeepAliveTimeout,

    /// <summary>对端关闭了连接。</summary>
    ClosedByPeer,

    /// <summary>收到 <c>SSH_MSG_DISCONNECT</c>。异常里带原因码与对端给的描述。</summary>
    Disconnected,

    /// <summary>对端违反了协议。</summary>
    ProtocolError,

    /// <summary>通道打开失败。异常里带 <c>CHANNEL_OPEN_FAILURE</c> 的原因码。</summary>
    ChannelOpenFailed,

    /// <summary>
    /// 通道开成了，但服务端拒绝了通道上的请求（<c>exec</c>、<c>pty-req</c>、<c>shell</c>、<c>subsystem</c>）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="ChannelOpenFailed"/> 分开：那是连通道都没开成（带 <c>CHANNEL_OPEN_FAILURE</c> 的原因码），
    /// 这是通道开了、要它做的事被拒 —— 常见原因是 <c>ForceCommand</c>、<c>PermitTTY no</c>、没配子系统。
    /// </remarks>
    ChannelRequestRejected,

    // ---- 转发 ----

    /// <summary>
    /// 转发被拒：服务端不接受转发请求（<c>AllowTcpForwarding no</c> 之类），
    /// 或者本端拒绝了对端发来的、对不上任何转发的通道。
    /// </summary>
    ForwardRejected,

    /// <summary>本机的监听端口开不了（被占用、没有权限）。</summary>
    ForwardBindFailed,

    /// <summary>本机一侧准备转发失败：拿不到 X 显示、<c>xauth</c> 跑不起来或失败。</summary>
    ForwardSetupFailed,

    /// <summary>本端的某个并发上限到了（转发连接数、agent / X11 通道数）。</summary>
    LimitExceeded,

    // ---- 远端命令 ----

    /// <summary>远端命令没有以退出码 0 结束（<c>SshCommandResult.EnsureSuccess</c>）。</summary>
    CommandFailed,

    // ---- 配置 ----

    /// <summary>
    /// 配置本身不成立：<c>ProxyJump</c> 成环、跳数超限、<c>ProxyCommand</c> 模板非法之类。
    /// </summary>
    /// <remarks>重试没有意义 —— 不改配置，下一次还是一样。</remarks>
    InvalidConfiguration,

    // ---- 其它 ----

    /// <summary>本端主动中止（Dispose 或取消）。</summary>
    Aborted,

    /// <summary>请求的能力（算法、密钥类型、格式版本）对端或本库不支持。</summary>
    Unsupported,
}
