// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH ssh_config(5)  Match / Include 两个关键字
//   行为规格:              velashell-docs/zh/ssh/spec/00-overview.md(ssh_config 子集)

namespace VelaShell.Ssh.Config;

/// <summary><c>Match</c> 块里的一个条件。</summary>
/// <param name="Keyword">条件名，小写（<c>host</c>、<c>user</c>、<c>exec</c>…）。</param>
/// <param name="Patterns">逗号分隔的模式列表；<c>all</c> / <c>canonical</c> 之类没有模式。</param>
/// <param name="Negated">条件名前带了 <c>!</c>。</param>
public sealed record SshConfigMatchCondition(
    string Keyword,
    IReadOnlyList<string> Patterns,
    bool Negated = false);

