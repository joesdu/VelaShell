// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §5.3、§5.4
//           velashell-docs/zh/ssh/spec/08-failures.md

using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Ssh.HostKeys;

/// <summary>一次主机密钥裁决的结果。</summary>
/// <remarks>
/// <para>
/// 只能经由 <see cref="Accept"/>、<see cref="AcceptAndPersist"/>、<see cref="Reject"/>、
/// <see cref="RejectChanged"/> 得到。<c>default(SshHostKeyVerdict)</c> 是一次没有说明的拒绝 ——
/// 忘了填裁决的策略会让连接失败，而不是放行。
/// </para>
/// <para>
/// 拒绝要分清<b>「不信任」</b>与<b>「变了」</b>（<see cref="Reason"/>）：后者可能是中间人，
/// 界面上要给出完全不同的提示。
/// </para>
/// </remarks>
public readonly record struct SshHostKeyVerdict
{
    private SshHostKeyVerdict(SshHostKeyDecision decision, SshFailureReason reason, string? message)
    {
        Decision = decision;
        Reason = reason;
        Message = message;
    }

    /// <summary>裁决。</summary>
    public SshHostKeyDecision Decision { get; }

    /// <summary>
    /// 拒绝的分类：<see cref="SshFailureReason.HostKeyRejected"/> 或 <see cref="SshFailureReason.HostKeyChanged"/>；
    /// 接受时为 <see cref="SshFailureReason.Unknown"/>。
    /// </summary>
    public SshFailureReason Reason { get; }

    /// <summary>
    /// 拒绝时给用户看的<b>人话</b>：说清是哪台、哪把指纹、以及下一步去哪操作。接受时为 <see langword="null"/>。
    /// </summary>
    public string? Message { get; }

    /// <summary>是否放行这次连接。</summary>
    public bool IsAccepted => Decision is SshHostKeyDecision.Accept or SshHostKeyDecision.AcceptAndPersist;

    /// <summary>接受这一次。</summary>
    public static SshHostKeyVerdict Accept { get; } =
        new(SshHostKeyDecision.Accept, SshFailureReason.Unknown, null);

    /// <summary>接受并持久化。</summary>
    public static SshHostKeyVerdict AcceptAndPersist { get; } =
        new(SshHostKeyDecision.AcceptAndPersist, SshFailureReason.Unknown, null);

    /// <summary>拒绝：不信任这把密钥（含首次连接时用户拒绝信任）。</summary>
    /// <param name="message">给用户看的原因。</param>
    public static SshHostKeyVerdict Reject(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new(SshHostKeyDecision.Reject, SshFailureReason.HostKeyRejected, message);
    }

    /// <summary>拒绝：这台主机出示的密钥与已记录的不符。</summary>
    /// <param name="message">给用户看的原因，应当同时列出新旧指纹。</param>
    public static SshHostKeyVerdict RejectChanged(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new(SshHostKeyDecision.Reject, SshFailureReason.HostKeyChanged, message);
    }
}
