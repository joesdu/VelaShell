// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/08-failures.md §1(原则)、§2(层级)、§5(带上下文的三个异常)

namespace VelaShell.Ssh.Diagnostics;

/// <summary>对端违反了协议。</summary>
/// <remarks>收到它必须断开连接，不得重试或继续读。</remarks>
public sealed class SshProtocolException : SshException
{
    /// <summary>创建一个协议错误异常。</summary>
    public SshProtocolException(SshPhase phase, string message, Exception? innerException = null)
        : base(SshFailureReason.ProtocolError, phase, message, innerException)
    {
    }
}
