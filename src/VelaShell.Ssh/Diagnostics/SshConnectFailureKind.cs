// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/08-failures.md §一、§二;velashell-docs/zh/ssh/spec/03-key-exchange.md §5.3;
//           velashell-docs/zh/ssh/spec/09-dialing.md §2.2、§3、§4、§5、§6

namespace VelaShell.Ssh.Diagnostics;

/// <summary>建连失败的具体情形（<see cref="SshConnectException.FailureKind"/>）—— 比 <see cref="SshFailureReason"/> 更细，界面据此说清是哪一步、哪一跳。</summary>
/// <remarks>
/// <para>
/// 异常消息是写给开发者的中文；界面按这个种类与异常上的参数（<see cref="SshConnectException.Via"/>、<see cref="SshConnectException.Target"/>、
/// 主机密钥的类型与位数、代理的应答……）自己说，不去解析那句话。
/// </para>
/// <para>
/// 零值 <see cref="Unknown"/> 是「没归类」：拨号、版本交换、超时这些 <see cref="SshFailureReason"/> 本身就说得清的失败不归类；
/// 使用者自己构造的异常也是它。
/// </para>
/// </remarks>
public enum SshConnectFailureKind
{
    /// <summary>没归类。</summary>
    Unknown = 0,

    // ---- 直连 TCP ----

    /// <summary>
    /// 连 <see cref="SshConnectException.Target"/> 失败，套接字错误不属于解析 / 拒绝 / 超时 / 不可达里的任何一类（本机防火墙拦了出站、本机地址不可用……），
    /// 原因码因此是 <see cref="SshFailureReason.Unknown"/>；错误码在内层的 <c>SocketException</c>。
    /// </summary>
    TcpConnectFailed,

    // ---- 主机密钥：库自己验不过（还没轮到主机密钥策略）----

    /// <summary>服务端出示的主机密钥解析不了（内层是 <c>SshPublicKeyException</c>）。</summary>
    HostKeyMalformed,

    /// <summary>
    /// 服务端出示的密钥类型（<see cref="SshConnectException.HostKeyType"/>）与协商出的主机密钥算法
    /// （<see cref="SshConnectException.HostKeyAlgorithm"/>）对不上 —— 含谈成证书算法却出示普通钥、或者反过来。
    /// </summary>
    HostKeyAlgorithmMismatch,

    /// <summary>
    /// RSA 主机密钥太短：<see cref="SshConnectException.HostKeyBits"/> 位，低于要求的 <see cref="SshConnectException.MinimumHostKeyBits"/> 位。
    /// </summary>
    HostKeyTooShort,

    /// <summary>
    /// 服务端对交换哈希的签名验不过（<see cref="SshConnectException.HostKeyAlgorithm"/>）：对端没有它声称的那把主机私钥，可能有中间人。
    /// </summary>
    HostKeySignatureInvalid,

    // ---- 主机密钥：策略拒的 ----

    /// <summary>
    /// 主机密钥策略拒绝了这把钥（<see cref="SshFailureReason.HostKeyRejected"/> 或 <see cref="SshFailureReason.HostKeyChanged"/>）。
    /// <see cref="Exception.Message"/> 就是策略随裁决给的原因，库原样转交、不改写 —— 界面语言由策略自己决定。
    /// </summary>
    HostKeyPolicyRejected,

    /// <summary>主机密钥策略拒绝了这把钥，却没给原因（<c>default(SshHostKeyVerdict)</c>）；<see cref="Exception.Message"/> 是库补的一句。</summary>
    HostKeyPolicyRejectedWithoutReason,

    // ---- 代理（SOCKS5 / HTTP CONNECT / ProxyCommand）----

    /// <summary>
    /// 连不上代理 <see cref="SshConnectException.Via"/> 本身（<see cref="SshFailureReason.ProxyUnreachable"/>；经嵌套的代理或跳板去连代理时沿用那一跳的原因码）。
    /// 具体原因在 <see cref="Exception.InnerException"/>。
    /// </summary>
    ProxyConnectFailed,

    /// <summary>代理 <see cref="SshConnectException.Via"/> 接下了连接，却一直没有回应握手 —— 连接的计时器到点了。</summary>
    ProxyHandshakeTimeout,

    /// <summary>代理 <see cref="SshConnectException.Via"/> 在握手中途断开了连接（内层是 <see cref="IOException"/>）：多半是拒绝了我们，只是没说原因。</summary>
    ProxyClosedDuringHandshake,

    /// <summary>
    /// 目标主机名 <see cref="SshConnectException.Target"/> 放不进代理的请求（不是合法域名、超长、含有不能出现在请求里的字符）。
    /// <see cref="SshFailureReason.InvalidConfiguration"/>：代理根本没见到请求。<b>未清洗</b>，展示前先过 <see cref="PeerText.Sanitize"/>。
    /// </summary>
    ProxyTargetNameInvalid,

    /// <summary>SOCKS5 的用户名或口令编码后不在 1–255 字节之间（RFC 1929），本地就拒了（<see cref="SshFailureReason.InvalidConfiguration"/>）。</summary>
    Socks5CredentialsTooLong,

    /// <summary><see cref="SshConnectException.Via"/> 回的不是 SOCKS5：版本字节是 <see cref="SshConnectException.ProxyReplyCode"/>。它可能是 HTTP 代理，或者根本不是代理。</summary>
    Socks5VersionMismatch,

    /// <summary>SOCKS5 代理不接受我们提供的任何认证方法（配了凭据时；没配凭据是 <see cref="SshFailureReason.ProxyAuthRequired"/>）。</summary>
    Socks5NoAcceptableMethod,

    /// <summary>SOCKS5 代理选了一个我们没提供的认证方法（方法字节是 <see cref="SshConnectException.ProxyReplyCode"/>）—— 协议错误。</summary>
    Socks5UnexpectedMethod,

    /// <summary>SOCKS5 代理的应答里地址类型非法（地址类型字节是 <see cref="SshConnectException.ProxyReplyCode"/>）。</summary>
    Socks5InvalidAddressType,

    /// <summary>
    /// SOCKS5 代理拒绝连接 <see cref="SshConnectException.Target"/>：应答码（RFC 1928 §6 的 <c>REP</c>，1–8 各有含义）是 <see cref="SshConnectException.ProxyReplyCode"/>。
    /// </summary>
    Socks5ConnectRefused,

    /// <summary><see cref="SshConnectException.Via"/> 回的不是 HTTP 响应（第一行原样在 <see cref="SshConnectException.ProxyReply"/>）。它可能是 SOCKS 代理，或者根本不是代理。</summary>
    HttpProxyNotHttp,

    /// <summary>HTTP 代理的响应头超过了上限 —— 它可能不是一个 HTTP 代理。</summary>
    HttpProxyResponseTooLarge,

    /// <summary>
    /// HTTP 代理拒绝连接 <see cref="SshConnectException.Target"/>：状态码是 <see cref="SshConnectException.ProxyReplyCode"/>，
    /// 状态行原样在 <see cref="SshConnectException.ProxyReply"/>。目标端口是 22、状态码是 403 / 405 / 501 时，多半是代理只放行 80/443。
    /// </summary>
    HttpConnectRefused,

    /// <summary>
    /// <c>ProxyCommand</c>（命令行在 <see cref="SshConnectException.Via"/>）在连上 <see cref="SshConnectException.Target"/> 之前就退出了：
    /// 退出码是 <see cref="SshConnectException.ProxyReplyCode"/>，stderr 的末尾（已清洗）在 <see cref="SshConnectException.ProxyReply"/>。
    /// </summary>
    ProxyCommandExited,

    // ---- 跳板（ProxyJump）----

    /// <summary>连不上跳板 <see cref="SshConnectException.Via"/>：原因码沿用那一跳的，具体原因在 <see cref="Exception.InnerException"/>（那一跳自己的异常）。</summary>
    JumpConnectFailed,

    /// <summary>经跳板 <see cref="SshConnectException.Via"/> 建连时外层连接的计时器到点了（等人输入的时间不算在内）。</summary>
    JumpConnectTimeout,

    /// <summary>
    /// 跳板 <see cref="SshConnectException.Via"/> 不肯转发到 <see cref="SshConnectException.Target"/>（它回了 <c>CHANNEL_OPEN_FAILURE</c>，
    /// 内层是带原因码与种类的 <c>SshChannelException</c>）。
    /// </summary>
    JumpForwardRefused,

    /// <summary>跳板 <see cref="SshConnectException.Via"/> 在转发到 <see cref="SshConnectException.Target"/> 时出了别的问题（断开、违反协议），原因在 <see cref="Exception.InnerException"/>。</summary>
    JumpForwardFailed,

    /// <summary>跳板 <see cref="SshConnectException.Via"/> 转发到 <see cref="SshConnectException.Target"/> 时外层连接的计时器到点了。</summary>
    JumpForwardTimeout,

    /// <summary>至少一个方向选中了要求严格密钥交换的加密算法，但首次握手未协商此扩展。</summary>
    StrictKexRequired,
}
