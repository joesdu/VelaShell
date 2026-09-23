// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/07-forwarding.md、velashell-docs/zh/ssh/design/architecture.md §5.1

namespace VelaShell.Ssh.Transport;

/// <summary>一跳拨号的种类。</summary>
public enum SshDialKind
{
    /// <summary>直接 TCP 连接。</summary>
    Tcp,

    /// <summary>经 SOCKS5 代理。</summary>
    Socks5,

    /// <summary>经 HTTP CONNECT 代理。</summary>
    HttpConnect,

    /// <summary>经另一台 SSH 主机（跳板 / ProxyJump）。</summary>
    SshJump,

    /// <summary>内存传输（测试用，不经过网络）。</summary>
    InMemory,

    /// <summary>使用者自定义的拨号方式。</summary>
    Custom,

    /// <summary>经一个外部程序的标准输入输出（<c>ProxyCommand</c>）。</summary>
    ProxyCommand,
}

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

/// <summary>
/// 拨号请求：要到哪里去，以及这一跳在整条链路上处于什么位置。
/// </summary>
/// <remarks>
/// <para>
/// 链路上的每一跳都拿到一个 <see cref="SshDialTarget"/>。
/// <see cref="FinalDestination"/> 始终是用户真正想连的那台机器，
/// 而 <see cref="EndPoint"/> 是**这一跳**的目标 —— 两者只有在直连时才相同。
/// </para>
/// <para>
/// 分开给的理由很具体：失败时要能说清「连不上跳板机 jump.example.com」
/// 而不是笼统的「连不上 10.0.0.9」。这两句话对用户是完全不同的两个问题
/// （velashell-docs/zh/ssh/spec/08-failures.md §5.2）。
/// </para>
/// </remarks>
public sealed record SshDialTarget
{
    /// <summary>这一跳要连的端点。</summary>
    public required SshEndPoint EndPoint { get; init; }

    /// <summary>整条链路最终要到达的端点。直连时与 <see cref="EndPoint"/> 相同。</summary>
    public required SshEndPoint FinalDestination { get; init; }

    /// <summary>这一跳在链路上的序号，从 0 开始。</summary>
    public int HopIndex { get; init; }

    /// <summary>是否启用 TCP keepalive（由操作系统做的那一层，与 SSH 的保活无关）。</summary>
    public bool TcpKeepAlive { get; init; } = true;

    /// <summary>
    /// 发起这次拨号的那条连接的计时器。跳板拨号器把它交给里面那一跳当外层计时器 ——
    /// 里面在等用户裁决主机密钥时，外面也要停表。
    /// </summary>
    internal Session.SshConnectDeadline? Deadline { get; init; }

    /// <summary>直连到给定端点的请求。</summary>
    public static SshDialTarget Direct(string host, int port)
    {
        SshEndPoint ep = new(host, port);
        return new SshDialTarget { EndPoint = ep, FinalDestination = ep };
    }
}
