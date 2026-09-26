// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §5.3、§5.4
//           velashell-docs/zh/ssh/design/architecture.md §8 第 7 项

using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.HostKeys;

/// <summary>裁决时能拿到的全部材料。</summary>
public sealed class SshHostKeyContext
{
    /// <summary>
    /// 被连的**逻辑**主机名。
    /// </summary>
    /// <remarks>
    /// 跳板链上这是**这一跳的目标**，不是 TCP 对端 —— 信任是针对逻辑主机的，
    /// 不是针对某条 TCP 连接。走代理时尤其重要：TCP 对端可能是本机的一个中继。
    /// </remarks>
    public required string Host { get; init; }

    /// <summary>端口。</summary>
    public required int Port { get; init; }

    /// <summary>服务端出示的主机公钥。</summary>
    public required SshPublicKey Key { get; init; }

    /// <summary>协商出的主机密钥算法名（可能与 <see cref="SshPublicKey.KeyType"/> 不同，见 RSA）。</summary>
    public required string NegotiatedAlgorithm { get; init; }

    /// <summary>这一跳在链路上的位置。用于把「跳板机的密钥」与「目标机的密钥」区分开。</summary>
    public SshDialKind HopKind { get; init; } = SshDialKind.Tcp;

    /// <summary>对端的版本标识串。</summary>
    public string PeerVersion { get; init; } = "";

    /// <summary><c>host:port</c> 形式的展示名。IPv6 加方括号。</summary>
    public string Target => new SshEndPoint(Host, Port).ToString();
}
