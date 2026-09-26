// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/07-forwarding.md、velashell-docs/zh/ssh/design/architecture.md §5.1

namespace VelaShell.Ssh.Transport;

/// <summary>拨号请求：这一跳要连到哪里。</summary>
/// <remarks>
/// 经跳板时，跳板这一跳与目标这一跳各自拿到自己的 <see cref="SshDialTarget"/>；
/// 失败时要能说清「连不上跳板机 jump.example.com」而不是笼统的「连不上 10.0.0.9」——
/// 那一层信息在异常的 <c>Hops</c> 里（velashell-docs/zh/ssh/spec/08-failures.md §5.2）。
/// </remarks>
public sealed record SshDialTarget
{
    /// <summary>这一跳要连的端点。</summary>
    public required SshEndPoint EndPoint { get; init; }

    /// <summary>
    /// 发起这次拨号的那条连接的计时器。跳板拨号器把它交给里面那一跳当外层计时器 ——
    /// 里面在等用户裁决主机密钥时，外面也要停表。
    /// </summary>
    internal Session.SshConnectDeadline? Deadline { get; init; }

    /// <summary>直连到给定端点的请求。</summary>
    public static SshDialTarget Direct(string host, int port)
    {
        SshEndPoint ep = new(host, port);
        return new SshDialTarget { EndPoint = ep };
    }
}
