// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH ssh_config(5)  各项的语义(只取行为描述)
//   行为规格:              velashell-docs/zh/ssh/spec/09-dialing.md §7

using VelaShell.Ssh.Auth;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Session;

namespace VelaShell.Ssh.Config;

/// <summary>把 <c>ssh_config</c> 变成连接参数时，配置文件里没有、要由调用方给的那些东西。</summary>
public sealed record SshConfigConnectOptions
{
    /// <summary>配置里没写 <c>User</c> 时用谁；<see langword="null"/> 取本机当前用户名。</summary>
    public string? DefaultUserName { get; init; }

    /// <summary>
    /// 排在配置里的 <c>IdentityFile</c> <b>之后</b>的凭据（口令、键盘交互、agent 里的钥……）。
    /// </summary>
    public IReadOnlyList<SshCredential> Credentials { get; init; } = [];

    /// <summary>
    /// 加密的 <c>IdentityFile</c> 向谁要口令；参数是文件路径。
    /// 返回 <see langword="null"/> 表示跳过这把钥。<see langword="null"/>（不给回调）时加密的钥一律跳过。
    /// </summary>
    public Func<string, CancellationToken, ValueTask<string?>>? PassphraseProvider { get; init; }

    /// <summary>
    /// 配置里没有 <c>StrictHostKeyChecking</c> / <c>UserKnownHostsFile</c> 时用的主机密钥策略；
    /// <see langword="null"/> 时用 <see cref="SshConnectionOptions"/> 的默认（按 known_hosts，没见过就拒绝）。
    /// </summary>
    public IHostKeyPolicy? HostKeyPolicy { get; init; }

    /// <summary>
    /// <c>StrictHostKeyChecking ask</c>（或缺省）且配了 <c>UserKnownHostsFile</c> 时，没见过的主机问谁。
    /// </summary>
    public Func<SshHostKeyContext, CancellationToken, ValueTask<bool>>? AskUnknownHost { get; init; }

    /// <summary>最后再改一遍 —— 对<b>每一跳</b>（含跳板）的连接参数都会调用。</summary>
    public Func<SshConnectionOptions, SshConnectionOptions>? Configure { get; init; }

    /// <summary>某个 <c>IdentityFile</c> 读不出来、被跳过时通知一声（参数：路径、原因）。</summary>
    /// <remarks>
    /// 一把钥读不出来（格式不认识、口令不对、没权限读）只跳过它自己，连接照常用别的凭据去试 ——
    /// 曾经是整个连接直接失败。跳过总得让人知道，否则最后的「认证失败」就无从查起。
    /// </remarks>
    public Action<string, Exception>? IdentityFileSkipped { get; init; }
}
