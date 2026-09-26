// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH PROTOCOL.certkeys
//   行为规格: velashell-docs/zh/ssh/spec/04-authentication.md §4;velashell-docs/zh/ssh/spec/08-failures.md

using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Ssh.Keys;

/// <summary>证书读不出来，或者用不了。</summary>
/// <remarks>
/// 具体是哪一种看 <see cref="SshException.Reason"/>：<see cref="SshFailureReason.KeyFileUnreadable"/>、
/// <see cref="SshFailureReason.KeyFormatInvalid"/>、<see cref="SshFailureReason.KeyMismatch"/>（与私钥不是一对、
/// 拿主机证书去登录）、<see cref="SshFailureReason.Unsupported"/>。
/// </remarks>
public sealed class SshCertificateException : SshException
{
    /// <summary>创建一个证书异常。</summary>
    /// <param name="reason">失败的分类。</param>
    /// <param name="message">诊断消息。</param>
    /// <param name="innerException">内部异常。</param>
    public SshCertificateException(SshFailureReason reason, string message, Exception? innerException = null)
        : base(reason, SshPhase.None, message, innerException)
    {
    }
}
