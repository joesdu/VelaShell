// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-miller-ssh-agent
//   行为规格: velashell-docs/zh/ssh/spec/04-authentication.md §4;velashell-docs/zh/ssh/spec/08-failures.md

using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Ssh.Keys;

/// <summary>连不上 agent、agent 拒绝了请求，或者 agent 的应答不合协议。</summary>
/// <remarks>
/// 具体是哪一种看 <see cref="SshException.Reason"/>：<see cref="SshFailureReason.AgentUnavailable"/>、
/// <see cref="SshFailureReason.AgentRefused"/>、<see cref="SshFailureReason.ProtocolError"/>、
/// <see cref="SshFailureReason.LimitExceeded"/>。
/// </remarks>
public sealed class SshAgentException : SshException
{
    /// <summary>创建一个 agent 异常。</summary>
    /// <param name="reason">失败的分类。</param>
    /// <param name="message">诊断消息。</param>
    /// <param name="innerException">内部异常。</param>
    public SshAgentException(SshFailureReason reason, string message, Exception? innerException = null)
        : base(reason, SshPhase.Authenticating, message, innerException)
    {
    }
}
