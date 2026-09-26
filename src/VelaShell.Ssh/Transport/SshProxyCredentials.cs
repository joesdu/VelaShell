// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/09-dialing.md §1

namespace VelaShell.Ssh.Transport;

/// <summary>代理的用户名与口令。</summary>
/// <param name="UserName">用户名。</param>
/// <param name="Password">口令。</param>
/// <remarks>
/// 〔安全〕<see cref="ToString"/> 不输出口令 —— 这个对象很容易被顺手写进日志。
/// </remarks>
public sealed record SshProxyCredentials(string UserName, string Password)
{
    /// <summary>只输出用户名。</summary>
    public override string ToString() => $"{UserName}:***";
}
