// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §5.3、§5.4

namespace VelaShell.Ssh.HostKeys;

/// <summary>主机密钥策略的裁决结果。</summary>
/// <remarks>
/// <b>零值是 <see cref="Reject"/>。</b>一个没填好的裁决（<c>default</c>）必须让连接失败，
/// 而不是悄悄放行 —— 主机密钥是整条连接安全性的地基，默认值只能站在拒绝那一边。
/// </remarks>
public enum SshHostKeyDecision
{
    /// <summary>拒绝。</summary>
    Reject = 0,

    /// <summary>接受这一次连接，但不持久化信任。</summary>
    Accept,

    /// <summary>接受并请求调用方把这把密钥记下来（TOFU 的「记住」）。</summary>
    AcceptAndPersist,
}
