// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH sshd(8) 的 SSH_KNOWN_HOSTS 章节（含 @cert-authority / @revoked）
//   行为规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §5.4、§5.5

namespace VelaShell.Ssh.HostKeys;

/// <summary>查 <c>known_hosts</c> 的结果详情。</summary>
/// <param name="Status">结论。</param>
/// <param name="MatchedEntry">对上的那一条（<see cref="KnownHostStatus.Unknown"/> 时为空）。</param>
/// <param name="ConflictingEntries">
/// 主机对上但密钥不对的那些条目 —— <b>报「密钥变了」时要把行号指给用户</b>，
/// 否则他不知道该去删哪一行。
/// </param>
public readonly record struct KnownHostLookup(
    KnownHostStatus Status,
    KnownHostEntry? MatchedEntry,
    IReadOnlyList<KnownHostEntry> ConflictingEntries)
{
    /// <summary><see cref="KnownHostStatus.CertificateInvalid"/> 时：证书哪里不合格（诊断文本，进日志与拒绝理由，不是界面文案）。</summary>
    public string? CertificateProblem { get; init; }
}
