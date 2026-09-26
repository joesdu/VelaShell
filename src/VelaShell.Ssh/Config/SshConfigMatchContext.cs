// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH ssh_config(5)  Match 的语义(只取行为描述)
//   行为规格:              velashell-docs/zh/ssh/spec/09-dialing.md §7

namespace VelaShell.Ssh.Config;

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
