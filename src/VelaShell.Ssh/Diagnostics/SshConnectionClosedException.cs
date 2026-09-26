// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/08-failures.md §1(原则)、§2(层级)、§5(带上下文的三个异常)

namespace VelaShell.Ssh.Diagnostics;

/// <summary>连接已断开。</summary>
public sealed class SshConnectionClosedException : SshException
{
    /// <summary>创建一个连接已断异常。</summary>
    public SshConnectionClosedException(SshFailureReason reason, SshPhase phase, string message, Exception? innerException = null)
        : base(reason, phase, message, innerException)
    {
    }

    /// <summary>收到 <c>SSH_MSG_DISCONNECT</c> 时对端给出的原因码。</summary>
    public SshDisconnectReason? DisconnectReason { get; init; }

    /// <summary>
    /// 对端给出的描述文本，原样保留。
    /// </summary>
    /// <remarks>
    /// 它常常是唯一有用的信息（<c>Too many authentication failures</c>、
    /// <c>No supported authentication methods available</c>）。
    /// <b>同时它是不可信文本</b>，展示时按不可信内容处理：不解释控制字符、不当作富文本。
    /// </remarks>
    public string? PeerDescription { get; init; }
}
