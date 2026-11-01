// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH ssh_config(5)  关键字与值的分隔(只取行为描述)
//   行为规格:              velashell-docs/zh/ssh/spec/09-dialing.md §7.4

namespace VelaShell.Ssh.Config;

/// <summary><c>ssh_config</c> 的一行拆成的关键字与值（<see cref="SshConfigSyntax.ReadLine"/>）。</summary>
/// <param name="Key">关键字；空行与注释行为空串。</param>
/// <param name="Value">
/// 关键字与分隔之后的原文：注释已去掉，引号与反斜杠转义还在（分词见 <see cref="SshConfigSyntax.TrySplit"/>）；
/// <c>ProxyCommand</c> 这类命令键是到行尾的原文（<see cref="SshConfigSyntax.TakesRestOfLine"/>）。
/// </param>
/// <param name="QuotesBalanced">引号配上了没有。没配上的那一行不算（spec/09 §7.4）。</param>
internal readonly record struct SshConfigLine(string Key, string Value, bool QuotesBalanced);
