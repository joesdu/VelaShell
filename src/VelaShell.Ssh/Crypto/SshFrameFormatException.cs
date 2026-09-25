// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

namespace VelaShell.Ssh.Crypto;

/// <summary>
/// 帧格式非法：长度越界、填充不合规、完整性校验失败。
/// </summary>
/// <remarks>
/// <para>
/// <b>收到它必须断开连接</b>，不得重试、不得「跳过这一帧继续读」
/// （velashell-docs/zh/ssh/spec/01-transport-framing.md §5）。
/// SSH 的帧是定长拼接的，一处错位之后的所有字节都不可信；
/// 而完整性校验失败要么是链路损坏，要么是有人在改数据 —— 两种都不该容忍。
/// </para>
/// <para>
/// 这是内部异常，在会话边界上被包成公开的 <c>SshProtocolException</c>
/// （带 <c>Reason</c> 与 <c>Phase</c>）。
/// </para>
/// </remarks>
public sealed class SshFrameFormatException : Exception
{
    /// <summary>用给定消息创建异常。</summary>
    public SshFrameFormatException(string message) : base(message)
    {
    }

    /// <summary>用给定消息与内部异常创建异常。</summary>
    public SshFrameFormatException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>对端在一个报文的中途关闭了连接 —— 那是断线，不是对端乱发（会话层据此归类）。</summary>
    internal bool PeerClosedMidPacket { get; init; }

    /// <summary>完整性校验失败。</summary>
    /// <remarks>
    /// 消息刻意不区分「MAC 不符」与「tag 不符」，也不带任何计算出来的中间值 ——
    /// 把校验细节回送给对端是一个侧信道。
    /// </remarks>
    public static SshFrameFormatException IntegrityCheckFailed() =>
        new("报文完整性校验失败。");
}
