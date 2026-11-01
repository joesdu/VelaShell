// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   行为规格: velashell-docs/zh/ssh/spec/09-dialing.md §7.4(引号没配对的那一行不算)

namespace VelaShell.Ssh.Config;

/// <summary><see cref="SshConfigFile.LoadAsync"/> 读配置时跳过的一行。</summary>
/// <param name="Path">那一行所在的文件（完整路径；可能是 <c>Include</c> 进来的）。</param>
/// <param name="LineNumber">行号，从 1 起。</param>
/// <param name="Text">那一行的原文（去掉首尾空白）。</param>
/// <remarks>
/// 目前只有一种：<b>引号没配对</b>（spec/09 §7.4）。OpenSSH 读到这样的行整份配置都不读（<c>invalid quotes</c>），
/// 本库只跳过这一行 —— 所以要告诉调用方：同一份配置在 <c>ssh</c> 里根本用不了。
/// </remarks>
public sealed record SshConfigSkippedLine(string Path, int LineNumber, string Text);
