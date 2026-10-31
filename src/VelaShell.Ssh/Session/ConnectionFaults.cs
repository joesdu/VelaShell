// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §11.1  DISCONNECT 的原因码
//   行为规格:       velashell-docs/zh/ssh/spec/08-failures.md §二、§六;velashell-docs/zh/ssh/spec/03-key-exchange.md §3.7.4

using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Session;

/// <summary>连接上的失败怎么归类：判死时归成哪种公开异常，断开时告诉对端哪个原因码、哪句话。</summary>
/// <remarks>
/// 从 <see cref="SshConnection"/> 拆出来的纯函数（没有状态）。建连期间（<c>SshConnection.ConnectAsync</c>）与会话期间
/// （接收循环、<c>SshConnection.Fault</c>、释放）用的是同一套口径。
/// </remarks>
internal static class ConnectionFaults
{
    /// <summary>因这个失败断开时，告诉对端的原因码；不必（或者发了也没人收）时为 <see langword="null"/>。</summary>
    /// <remarks>
    /// <para>
    /// 帧层的失败一律报 <see cref="SshDisconnectReason.ProtocolError"/>，不区分「完整性校验失败」与「格式不对」——
    /// 把校验细节回送给对端是侧信道（见 <c>SshFrameFormatException.IntegrityCheckFailed</c>）。
    /// </para>
    /// <para>
    /// 〔velashell-docs/zh/ssh/spec/03 §3.7.4〕密钥交换里对端的公开值不合格（<see cref="Crypto.Kex.SshKeyExchangeException"/>：长度、编码、
    /// 不在曲线上、X25519 全零、DH 越界…）报 <see cref="SshDisconnectReason.KeyExchangeFailed"/>，所有方法一律这样 ——
    /// RFC 10042 §2.1 对混合方法是「必须」，RFC 8731 对 curve25519 是「应当」。本端的原因照旧是 <c>ProtocolError</c>。曾经发 2。
    /// </para>
    /// </remarks>
    internal static SshDisconnectReason? DisconnectReasonFor(Exception failure) => failure switch
    {
        Crypto.SshFrameFormatException { PeerClosedMidPacket: true } => null,
        Crypto.SshFrameFormatException or SshWireFormatException or SshProtocolException => SshDisconnectReason.ProtocolError,
        Crypto.Kex.SshKeyExchangeException or SshNegotiationException => SshDisconnectReason.KeyExchangeFailed,
        SshException { Reason: SshFailureReason.HostKeyRejected or SshFailureReason.HostKeyChanged } =>
            SshDisconnectReason.HostKeyNotVerifiable,
        Auth.SshAuthenticationException => SshDisconnectReason.NoMoreAuthMethodsAvailable,
        _ => null,
    };

    /// <summary><c>DISCONNECT</c> 里给对端日志看的那句话（英文：对端的日志是给运维看的，不按本机界面语言）。</summary>
    /// <param name="reason">原因码。</param>
    /// <param name="failure">断开的原因：同是 <see cref="SshDisconnectReason.KeyExchangeFailed"/>，协商不上与公开值不合格说的话不一样。</param>
    internal static string DisconnectDescription(SshDisconnectReason reason, Exception? failure = null) => reason switch
    {
        SshDisconnectReason.ProtocolError => "protocol error",
        SshDisconnectReason.KeyExchangeFailed when failure is Crypto.Kex.SshKeyExchangeException => "key exchange failed: invalid public value",
        SshDisconnectReason.KeyExchangeFailed => "no matching algorithms",
        SshDisconnectReason.HostKeyNotVerifiable => "host key not verifiable",
        SshDisconnectReason.NoMoreAuthMethodsAvailable => "no more authentication methods available",
        SshDisconnectReason.AuthCancelledByUser => "authentication cancelled by user",
        SshDisconnectReason.ByApplication => "disconnected by application",
        _ => reason.ToString(),
    };

    /// <summary>把会话的故障原因归成公开的异常类型。</summary>
    /// <remarks>
    /// <para>
    /// 接收循环与发送泵接到的异常五花八门：套接字的 <see cref="IOException"/>、帧格式与完整性校验的
    /// <see cref="Crypto.SshFrameFormatException"/>、解析报文时的内部格式异常。它们会被原样抛给使用者
    /// （连接上的每个调用、每条通道的读者），而使用者按 <see cref="SshException"/> 与它的
    /// <see cref="SshException.Reason"/> 分流（重连只该对「断了」生效）。原样抛出的话，
    /// 一个内部类型使用者连 catch 都 catch 不到，宿主的异常翻译也认不出它。
    /// </para>
    /// <para>
    /// 归法：I/O 失败与「报文中途断开」→ <see cref="SshFailureReason.ClosedByPeer"/>；
    /// 帧格式、完整性校验、解析失败 → 协议错误；本来就是 <see cref="SshException"/> 的原样；
    /// 取消与释放原样（那是本端在收工，不是故障）；其它意外包一层，原异常留在 InnerException 里。
    /// </para>
    /// </remarks>
    internal static Exception NormalizeFault(Exception exception) => exception switch
    {
        SshException or OperationCanceledException or ObjectDisposedException => exception,
        Crypto.SshFrameFormatException { PeerClosedMidPacket: true } =>
            new SshConnectionClosedException(
                SshFailureReason.ClosedByPeer, SshPhase.Open, $"连接断了：{exception.Message}", exception),
        IOException or System.Net.Sockets.SocketException =>
            new SshConnectionClosedException(
                SshFailureReason.ClosedByPeer, SshPhase.Open, $"连接断了：{exception.Message}", exception),
        Crypto.SshFrameFormatException or SshWireFormatException =>
            new SshProtocolException(SshPhase.Open, $"对端违反了协议：{exception.Message}", exception),
        _ => new SshConnectionClosedException(
            SshFailureReason.Unknown, SshPhase.Open, $"连接因意外错误中断：{exception.Message}", exception),
    };
}
