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

    /// <summary>本端主动中止（Dispose 或取消）。</summary>
    Aborted,

    /// <summary>请求的能力对端不支持。</summary>
    Unsupported,
}
