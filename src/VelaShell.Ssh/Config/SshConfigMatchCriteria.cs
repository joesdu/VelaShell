// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH ssh_config(5)  Match 的语义(只取行为描述)
//   行为规格:              velashell-docs/zh/ssh/spec/09-dialing.md §7

namespace VelaShell.Ssh.Config;

/// <summary>一个 <c>Match</c> 块的全部条件。</summary>
/// <param name="Conditions">**全部满足**才算这个块适用（条件之间是与）。</param>
/// <remarks>
/// <para>
/// <c>Match</c> 与 <c>Host</c> 的区别：<c>Host</c> 只看主机名，
/// <c>Match</c> 还能看用户、本机用户、原始主机名，以及 —— 危险的那一个 ——
/// 执行一条外部命令。
/// </para>
/// </remarks>
public sealed record SshConfigMatchCriteria(IReadOnlyList<SshConfigMatchCondition> Conditions)
{
    /// <summary>这个块里有没有 <c>exec</c> 条件。</summary>
    /// <remarks>
    /// 调用方可以据此提示用户「这份配置想执行外部命令」——
    /// 默认我们是不执行的，见 <see cref="SshConfigMatchContext.ExecEvaluator"/>。
    /// </remarks>
    public bool UsesExec =>
        Conditions.Any(c => string.Equals(c.Keyword, "exec", StringComparison.Ordinal));
}
