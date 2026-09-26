// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/09-dialing.md §1

namespace VelaShell.Ssh.Transport;

/// <summary>
/// 一次拨号要到达的端点。
/// </summary>
/// <param name="Host">主机名或 IP。<b>不在本地解析</b> —— 见 <see cref="SshDialTarget"/> 的说明。</param>
/// <param name="Port">端口。</param>
/// <remarks>
/// <para>
/// <b>主机名不在本地解析。</b>原样交给拨号器，由它决定怎么处理：
/// <see cref="SshDialKind.Tcp"/> 会解析它，而 <see cref="SshDialKind.Socks5"/>
/// 与 <see cref="SshDialKind.SshJump"/> 把名字交给对端去解析。
/// </para>
/// <para>
/// 这个区别不是细节：内网域名在本地根本解析不出来，本地解析会让「DNS 走本地、
/// 连接走隧道」的分裂彻底失效；而且它泄漏了访问目标。
/// </para>
/// </remarks>
public readonly record struct SshEndPoint(string Host, int Port)
{
    /// <summary>形如 <c>host:port</c>。IPv6 地址加方括号。</summary>
    public override string ToString() =>
        Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
}
