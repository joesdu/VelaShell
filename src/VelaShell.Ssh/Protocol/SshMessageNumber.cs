// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4250 §4.1.2  "Message Numbers"                    —— 编号分区与本表全部取值
//   RFC 4253 §12     "Summary of Message Numbers"         —— 传输层 1..49
//   RFC 4252 §6      "Authentication Protocol Message Numbers" —— 认证层 50..79
//   RFC 4254 §9      "Summary of Message Numbers"         —— 连接层 80..127
//   RFC 8308 §2.3    "ext-info" 的 SSH_MSG_EXT_INFO = 7
//
// 说明:本文件里的数值是协议规定的事实,任何正确的 SSH 实现都必然相同 ——
// 它们落在 NOTICE.md 所说的「表达方式唯一、不受版权保护」那一类里。

namespace VelaShell.Ssh.Protocol;

/// <summary>
/// SSH 二进制报文的消息编号（每个报文载荷的第一个字节）。
/// </summary>
/// <remarks>
/// <para>
/// 取值按 RFC 4250 §4.1.2 分区。**分区本身是有行为含义的，不只是编号习惯**：
/// </para>
/// <list type="table">
///   <item>
///     <term>1–19</term>
///     <description>传输层通用。其中 <see cref="Disconnect"/> / <see cref="Ignore"/> /
///     <see cref="Debug"/> / <see cref="Unimplemented"/> 在**任何**状态下都可能到达。</description>
///   </item>
///   <item>
///     <term>20–29</term>
///     <description>算法协商。重协商期间只有这一区与 30–49 区允许发送
///     （RFC 4253 §7.1），这正是发送闸门（<c>SendGate</c>）要放行的那一类。</description>
///   </item>
///   <item>
///     <term>30–49</term>
///     <description>密钥交换方法**专用**，含义随协商出的 KEX 方法而变 ——
///     同一个 30 在 curve25519-sha256 下是 ECDH_INIT，在别的方法下是别的东西。
///     <b>因此这一区不进本枚举</b>，由各 KEX 实现自己定义，见 <c>velashell-docs/zh/ssh/spec/03-key-exchange.md</c>。</description>
///   </item>
///   <item>
///     <term>50–59</term>
///     <description>认证层通用。</description>
///   </item>
///   <item>
///     <term>60–79</term>
///     <description>认证**方法**专用，含义随当前进行中的认证方法而变
///     （60 在 publickey 下是 PK_OK，在 password 下是 PASSWD_CHANGEREQ，
///     在 keyboard-interactive 下是 INFO_REQUEST）。<b>同样不进本枚举</b>，
///     由各认证方法自己定义，见 <c>velashell-docs/zh/ssh/spec/04-authentication.md</c>。</description>
///   </item>
///   <item>
///     <term>80–89</term>
///     <description>连接层全局请求。</description>
///   </item>
///   <item>
///     <term>90–127</term>
///     <description>连接层通道消息。</description>
///   </item>
///   <item>
///     <term>128–191</term>
///     <description>保留给将来的扩展。</description>
///   </item>
///   <item>
///     <term>192–255</term>
///     <description>本地扩展/私有使用。</description>
///   </item>
/// </list>
/// <para>
/// 把 30–49 与 60–79 两个**上下文相关**的区段排除在本枚举之外，是刻意的设计：
/// 它们的取值只有在「当前协商出的 KEX 方法」或「当前正在跑的认证方法」这个上下文里
/// 才有意义。把它们混进一个全局枚举，等于鼓励调用方在没有上下文的地方去 switch 它们 ——
/// 而那正是 SSH 实现里最容易出的一类错。
/// </para>
/// </remarks>
public enum SshMessageNumber : byte
{
    // ---- 传输层：通用（RFC 4253 §12） ----

    /// <summary>断开连接并说明原因；发送方随后必须立即关闭连接。RFC 4253 §11.1。</summary>
    Disconnect = 1,

    /// <summary>必须被接收方忽略。可用于对抗流量分析。RFC 4253 §11.2。</summary>
    Ignore = 2,

    /// <summary>对一个无法识别的消息编号的应答，携带触发它的报文序号。RFC 4253 §11.4。</summary>
    Unimplemented = 3,

    /// <summary>调试信息；是否展示给用户由 <c>always_display</c> 字段决定。RFC 4253 §11.3。</summary>
    Debug = 4,

    /// <summary>请求启动一个服务（<c>ssh-userauth</c> / <c>ssh-connection</c>）。RFC 4253 §10。</summary>
    ServiceRequest = 5,

    /// <summary>服务已接受。RFC 4253 §10。</summary>
    ServiceAccept = 6,

    /// <summary>
    /// 扩展信息协商。RFC 8308 §2.3。
    /// </summary>
    /// <remarks>
    /// 它可以出现在**两个**位置：首次 NEWKEYS 之后立刻，或 <see cref="UserAuthSuccess"/> 之前。
    /// 我们靠它拿到 <c>server-sig-algs</c> —— 没有它就无法安全地用 rsa-sha2-256/512 签名
    /// （RFC 8332 §3.1），只能退回已被弃用的 ssh-rsa。
    /// </remarks>
    ExtInfo = 7,

    // ---- 传输层：算法协商（RFC 4253 §12） ----

    /// <summary>
    /// 算法协商。双方各发一次，取「客户端列表中第一个双方都支持的」。RFC 4253 §7.1。
    /// </summary>
    /// <remarks>
    /// 收到它意味着对端发起了（重）协商。此后直到 <see cref="NewKeys"/>，
    /// 只允许收发 20–49 区的消息 —— 发送闸门据此工作。
    /// </remarks>
    KexInit = 20,

    /// <summary>
    /// 密钥交换完成，此后的报文改用新密钥。RFC 4253 §7.3。
    /// </summary>
    /// <remarks>
    /// 启用严格 KEX（<c>kex-strict-*-v00@openssh.com</c>，Terrapin 缓解）时，
    /// 收发此消息后报文序号**归零**；且在首次 KEX 期间收到任何非预期消息都必须断连。
    /// 见 <c>velashell-docs/zh/ssh/spec/03-key-exchange.md</c>。
    /// </remarks>
    NewKeys = 21,

    // 30–49：密钥交换方法专用，含义随方法而变 —— 刻意不在此枚举中，见 <remarks>。

    // ---- 认证层：通用（RFC 4252 §6） ----

    /// <summary>发起一次认证尝试。RFC 4252 §5。</summary>
    UserAuthRequest = 50,

    /// <summary>
    /// 本次尝试失败，并列出仍可继续的认证方法。RFC 4252 §5.1。
    /// </summary>
    /// <remarks>
    /// <c>partial success</c> 为 <see langword="true"/> 时表示这一步其实**成功了**，
    /// 但服务器还要求继续下一种方法 —— 多因素认证（公钥 + OTP）正是这么表达的。
    /// 把它当成失败处理，是 2FA 支持最常见的实现错误。
    /// </remarks>
    UserAuthFailure = 51,

    /// <summary>认证成功；此后进入连接协议。RFC 4252 §5.1。</summary>
    UserAuthSuccess = 52,

    /// <summary>服务器下发的横幅文本，应在认证完成前展示给用户。RFC 4252 §5.4。</summary>
    UserAuthBanner = 53,

    // 60–79：认证方法专用，含义随方法而变 —— 刻意不在此枚举中，见 <remarks>。

    // ---- 连接层：全局请求（RFC 4254 §9） ----

    /// <summary>与具体通道无关的请求，如 <c>tcpip-forward</c>、keepalive。RFC 4254 §4。</summary>
    GlobalRequest = 80,

    /// <summary>全局请求成功。RFC 4254 §4。</summary>
    RequestSuccess = 81,

    /// <summary>全局请求失败。RFC 4254 §4。</summary>
    RequestFailure = 82,

    // ---- 连接层：通道（RFC 4254 §9） ----

    /// <summary>请求打开一条通道。RFC 4254 §5.1。</summary>
    ChannelOpen = 90,

    /// <summary>通道已打开，并告知对端的通道号、初始窗口与最大报文长度。RFC 4254 §5.1。</summary>
    ChannelOpenConfirmation = 91,

    /// <summary>通道打开被拒，携带 <c>reason code</c>。RFC 4254 §5.1。</summary>
    ChannelOpenFailure = 92,

    /// <summary>增加对端可发送的字节数（流控窗口）。RFC 4254 §5.2。</summary>
    ChannelWindowAdjust = 93,

    /// <summary>通道数据（标准输出方向）。RFC 4254 §5.2。</summary>
    ChannelData = 94,

    /// <summary>带类型码的通道数据；类型码 1 为标准错误。RFC 4254 §5.2。</summary>
    ChannelExtendedData = 95,

    /// <summary>本端不再发送数据，但仍可接收（半关闭）。RFC 4254 §5.3。</summary>
    ChannelEof = 96,

    /// <summary>通道关闭；双方各发一次后通道号方可回收。RFC 4254 §5.3。</summary>
    ChannelClose = 97,

    /// <summary>通道内请求，如 <c>pty-req</c>、<c>shell</c>、<c>exec</c>、<c>window-change</c>。RFC 4254 §5.4。</summary>
    ChannelRequest = 98,

    /// <summary>通道请求成功（仅当请求方要求回复时发送）。RFC 4254 §5.4。</summary>
    ChannelSuccess = 99,

    /// <summary>通道请求失败（仅当请求方要求回复时发送）。RFC 4254 §5.4。</summary>
    ChannelFailure = 100,
}
