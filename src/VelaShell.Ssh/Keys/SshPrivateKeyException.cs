// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/04-authentication.md §4;velashell-docs/zh/ssh/spec/08-failures.md

using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Ssh.Keys;

/// <summary>私钥读不出来：文件、格式、口令、算法。</summary>
/// <remarks>
/// 具体是哪一种看 <see cref="SshException.Reason"/>：<see cref="SshFailureReason.KeyFileUnreadable"/>、
/// <see cref="SshFailureReason.KeyFormatInvalid"/>、<see cref="SshFailureReason.KeyPassphraseRequired"/>、
/// <see cref="SshFailureReason.KeyPassphraseIncorrect"/>、<see cref="SshFailureReason.Unsupported"/>。
/// 读私钥发生在连接之外，所以阶段是 <see cref="SshPhase.None"/>。
/// </remarks>
public sealed class SshPrivateKeyException : SshException
{
    /// <summary>创建一个私钥读取异常。</summary>
    /// <param name="reason">失败的分类。</param>
    /// <param name="message">诊断消息。</param>
    /// <param name="innerException">内部异常。</param>
    public SshPrivateKeyException(SshFailureReason reason, string message, Exception? innerException = null)
        : base(reason, SshPhase.None, message, innerException)
    {
    }

    /// <summary>是不是口令的问题（没给，或者给错了）—— 调用方据此再问一次口令。</summary>
    public bool NeedsPassphrase =>
        Reason is SshFailureReason.KeyPassphraseRequired or SshFailureReason.KeyPassphraseIncorrect;
}
