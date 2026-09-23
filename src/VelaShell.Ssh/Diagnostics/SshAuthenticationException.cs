// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/04-authentication.md §9;velashell-docs/zh/ssh/spec/08-failures.md §5.3

using VelaShell.Ssh.Auth;

namespace VelaShell.Ssh.Diagnostics;

/// <summary>认证失败。</summary>
/// <remarks>
/// <para>
/// <b>异常带着逐方法的尝试记录。</b>没有它，「私钥文件读不出来」与
/// 「服务端不接受公钥认证」在界面上是同一句「用户名或密码不正确」——
/// 而那两件事的下一步完全不同。
/// </para>
/// </remarks>
public sealed class SshAuthenticationException : SshException
{
    /// <summary>创建一个认证失败异常。</summary>
    public SshAuthenticationException(
        SshFailureReason reason,
        string message,
        IReadOnlyList<SshAuthAttempt> attempts,
        IReadOnlyList<string> serverOffered,
        bool partialSuccessAchieved = false,
        Exception? innerException = null)
        : base(reason, SshPhase.Authenticating, message, innerException)
    {
        Attempts = attempts;
        ServerOffered = serverOffered;
        PartialSuccessAchieved = partialSuccessAchieved;
    }

    /// <summary>逐条尝试记录，含「因服务端不接受而跳过」的那些。</summary>
    public IReadOnlyList<SshAuthAttempt> Attempts { get; }

    /// <summary>服务端最后给出的可继续方法列表。</summary>
    public IReadOnlyList<string> ServerOffered { get; }

    /// <summary>
    /// 过程中是否有过部分成功。
    /// </summary>
    /// <remarks>
    /// 为真说明用户**至少过了一关**，卡在后面某一步 ——
    /// 这与「一关都没过」对用户是完全不同的信息，
    /// 值得在界面上说成「第一步通过了，但还需要 X」。
    /// </remarks>
    public bool PartialSuccessAchieved { get; }

    /// <summary>把尝试记录拼成多行人话，用于诊断面板。</summary>
    public string DescribeAttempts() =>
        Attempts.Count == 0
            ? "（没有任何认证尝试）"
            : string.Join(Environment.NewLine, Attempts.Select(static a => "· " + a));
}
