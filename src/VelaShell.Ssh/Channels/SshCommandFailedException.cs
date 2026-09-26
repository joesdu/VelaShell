// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/08-failures.md

using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Ssh.Channels;

/// <summary>远端命令没有成功结束（<see cref="SshCommandResult.EnsureSuccess"/>）。</summary>
public sealed class SshCommandFailedException : SshException
{
    /// <summary>创建一个命令失败异常。</summary>
    /// <param name="message">诊断消息。</param>
    /// <param name="result">完整结果。</param>
    public SshCommandFailedException(string message, SshCommandResult result)
        : base(SshFailureReason.CommandFailed, SshPhase.Open, message) => Result = result;

    /// <summary>完整结果，含 stdout 与 stderr。</summary>
    public SshCommandResult Result { get; }
}
