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

    /// <summary>
    /// 连不上代理本身（代理的地址解析不了、端口没人听、超时、网络不可达）。具体是哪一种在 <see cref="Exception.InnerException"/> 与 <c>Hops</c> 里。
    /// </summary>
    /// <remarks>
    /// 与直连的 <see cref="TcpRefused"/> 等分开：曾经沿用同一组码，调用方分不出没开的是目标还是代理 ——
    /// 宿主于是把代理的失败一律改写成 <see cref="ProxyRefused"/>，又丢了「连不上」与「拒绝转发」的区别。
    /// </remarks>
    ProxyUnreachable,

    /// <summary>代理拒绝转发。消息里会带上代理类型、地址与目标。</summary>
    ProxyRefused,

    /// <summary>代理要求认证，但未配置代理凭据。</summary>
    ProxyAuthRequired,

    /// <summary>代理拒绝了配置的凭据（用户名或口令不对）。</summary>
    /// <remarks>
    /// 与 <see cref="ProxyAuthRequired"/> 分开：一个是「去配」，一个是「改对」。曾经两者共用一个码，
    /// 宿主只好自己靠「有没有配凭据」来分。
    /// </remarks>
    ProxyAuthFailed,

    /// <summary>TLS 握手失败（<c>DialerChain.Tls</c>）：服务端证书不可信或名字对不上，或者对端说的不是 TLS。</summary>
    TlsFailed,

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

    /// <summary>
    /// 主机密钥的记录读不出来或写不进去：<c>known_hosts</c> 没有权限、被别的进程占着、磁盘满。
    /// </summary>
    /// <remarks>
    /// 读不出来时没法判断这台主机认不认识，连接不放行；「信任并记住」时写不进去不影响这次连接，
    /// 失败记在 <c>SshConnection.HostKeyPersistFailure</c> 上（velashell-docs/zh/ssh/spec/03 §5.4）。
    /// </remarks>
    HostKeyStoreFailed,

    // ---- 认证 ----
    // 曾经还有一个 AuthenticationFailed（「一次认证尝试失败」）：全库没有产生者，却列在 IsRetryable 里。
    // 认证失败报的是 AuthenticationMethodExhausted（带逐条尝试记录），那不该重试。

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

    /// <summary>
    /// 本机没有在跑 ssh-agent：<c>SSH_AUTH_SOCK</c> 没设、套接字不存在或没人监听、Windows 的命名管道在时限内没有出现。
    /// </summary>
    /// <remarks>
    /// 从 <see cref="AgentUnavailable"/> 里单独分出来，因为它是最常见的那一种，而且下一步很明确（把 agent 起来）——
    /// 宿主据此给出本地化的提示，不必去解析消息句子。
    /// </remarks>
    AgentNotRunning,

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

    /// <summary>本机一侧准备转发失败：拿不到 X 显示、<c>xauth</c> 跑不起来或失败，远程转发回连的本机套接字路径太长。</summary>
    ForwardSetupFailed,

    /// <summary>本端的某个上限到了（并发通道数、会话接收窗口总预算、转发连接数、agent / X11 通道数）。</summary>
    LimitExceeded,

    // ---- 远端命令 ----

    /// <summary>
    /// 远端命令没有以退出码 0 结束（<c>SshCommandResult.EnsureSuccess</c>）；
    /// 或者 sftp-server 没等 SFTP 建立就退出了（<c>SftpUnavailableException</c>，带退出码与 stderr 的末尾）。
    /// </summary>
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
