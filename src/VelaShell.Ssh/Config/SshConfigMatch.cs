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

/// <summary>求解 <c>ssh_config</c> 时的上下文 —— <c>Match</c> 要拿它来判断。</summary>
/// <remarks>
/// <para>
/// 只给一个主机名也能用（<see cref="ForHost"/>），那时
/// <c>Match user</c> 一类的条件一律不匹配 —— <b>信息不足就不匹配</b>，
/// 而不是猜一个。猜错的后果是「用了一份我没想到会生效的配置」。
/// </para>
/// </remarks>
public sealed record SshConfigMatchContext
{
    /// <summary>要连的主机名（<c>HostName</c> 改写之后的那个）。</summary>
    public required string Host { get; init; }

    /// <summary>命令行上原始写的那个主机名；<see langword="null"/> 时取 <see cref="Host"/>。</summary>
    public string? OriginalHost { get; init; }

    /// <summary>要登录的远端用户名。</summary>
    public string? User { get; init; }

    /// <summary>本机当前用户名。</summary>
    public string? LocalUser { get; init; }

    /// <summary>求值 <c>Match exec</c>。</summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>默认是 <see langword="null"/>，也就是不执行任何命令。</b>
    /// <c>Match exec</c> 意味着<b>解析一份配置文件就能在本机跑任意程序</b> ——
    /// 而配置文件常常是从别处拷来的、同步过来的、或者别人给的。
    /// </para>
    /// <para>
    /// 这里不提供「内置的执行器」，只留一个口子：真需要的调用方自己传一个进来，
    /// 于是<b>「要不要执行外部命令」这个决定明确地落在调用方身上</b>，
    /// 而不是藏在库的默认行为里。
    /// </para>
    /// <para>
    /// 为 <see langword="null"/> 时带 <c>exec</c> 条件的块<b>一律不匹配</b>。
    /// </para>
    /// </remarks>
    public Func<string, bool>? ExecEvaluator { get; init; }

    /// <summary>只知道主机名时的上下文。</summary>
    public static SshConfigMatchContext ForHost(string host) => new() { Host = host };
}
