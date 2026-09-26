// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/08-failures.md §1(原则)、§2(层级)、§5(带上下文的三个异常)

namespace VelaShell.Ssh.Diagnostics;

/// <summary>链路上一跳的结果。</summary>
/// <param name="Kind">这一跳是怎么走的。</param>
/// <param name="Target">这一跳的目标。</param>
/// <param name="Succeeded">是否成功。</param>
/// <param name="Elapsed">耗时。</param>
/// <param name="Detail">失败时的补充说明。</param>
public readonly record struct SshHopInfo(
    Transport.SshDialKind Kind,
    string Target,
    bool Succeeded,
    TimeSpan Elapsed,
    string? Detail = null);
