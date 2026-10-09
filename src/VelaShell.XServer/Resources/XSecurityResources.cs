// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   Security Extension Specification, Version 7.1 —— 「SecurityGenerateAuthorization」(授权的 timeout、trust-level、group、event-mask)、
//   「SecurityRevokeAuthorization」

using VelaShell.XServer.Server;

namespace VelaShell.XServer.Resources;

/// <summary>
/// SecurityGenerateAuthorization 签出的一个授权:MIT-MAGIC-COOKIE-1 的 cookie 与它的属性。AUTHID 是自己的一套编号,不是资源 ID,
/// 不在资源表里(规范:AUTHID 的取值空间不必与资源 ID、原子等分开)。只在执行线程上读写。
/// </summary>
/// <param name="id">AUTHID(非 0)。</param>
/// <param name="cookie">建立连接时要带的 cookie(16 字节)。</param>
/// <param name="untrusted">用它连进来的客户端是不是非受信的(trust-level)。</param>
/// <param name="timeout">没有连接之后过多少秒自动作废;0 = 永不作废。</param>
internal sealed class SecurityAuthorization(uint id, byte[] cookie, bool untrusted, uint timeout)
{
    public uint Id { get; } = id;

    public byte[] Cookie { get; } = cookie;

    public bool Untrusted { get; } = untrusted;

    public uint Timeout { get; } = timeout;

    /// <summary>选了 SecurityAuthorizationRevoked 的那个客户端(签它的客户端;event-mask 为 None 或它已断开时为 null)。</summary>
    public XClient? RevokedListener { get; set; }

    /// <summary>此刻用它连着的客户端数。</summary>
    public int Connections { get; set; }

    /// <summary>
    /// 每回落到「没有连接」就加一:到期的计时器核对它,期间有新连接用过(又断开)就不算到期 ——
    /// 规范:最后一次进入「没有连接」之后满 timeout 秒、并且这段时间里没有新连接用过它,才作废。
    /// </summary>
    public int IdleEpoch { get; set; }

    /// <summary>已经作废(撤销或到期)。</summary>
    public bool Revoked { get; set; }
}
