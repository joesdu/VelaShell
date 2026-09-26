// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH ssh_config(5)  各项的语义(只取行为描述)
//   行为规格:              velashell-docs/zh/ssh/spec/09-dialing.md §7

namespace VelaShell.Ssh.Config;

/// <summary><c>ssh_config</c> 里的一个 <c>Host</c> 块。</summary>
/// <param name="Patterns">这个块管哪些主机（支持 <c>*</c>、<c>?</c>、<c>!</c>）。</param>
/// <param name="Settings">块里的设置，键不区分大小写。</param>
/// <param name="Match">
/// <c>Match</c> 块的条件；<see langword="null"/> 表示这是普通的 <c>Host</c> 块。
/// </param>
/// <param name="Enclosing">
/// 这个块来自一个写在 <c>Host</c> / <c>Match</c> 块里的 <c>Include</c> 时，Include 所在的那个块
/// （只用它的条件，不用它的设置）；<see langword="null"/> 表示没有外层条件。
/// 两边的条件都满足，这个块才生效 —— 那就是「带条件的包含」。
/// </param>
public sealed record SshConfigBlock(
    IReadOnlyList<string> Patterns,
    IReadOnlyDictionary<string, List<string>> Settings,
    SshConfigMatchCriteria? Match = null,
    SshConfigBlock? Enclosing = null);
