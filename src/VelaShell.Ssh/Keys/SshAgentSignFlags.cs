// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-miller-ssh-agent  SSH Agent Protocol
//   OpenSSH PROTOCOL.agent  实现口径
//   行为规格:               velashell-docs/zh/ssh/design/architecture.md §8 第 5 项;velashell-docs/zh/ssh/spec/07-forwarding.md §七(加钥见 §7.3)

namespace VelaShell.Ssh.Keys;

/// <summary>签名请求的标志位（OpenSSH PROTOCOL.agent）。</summary>
[Flags]
internal enum SshAgentSignFlags : uint
{
    None = 0,

    /// <summary>用 <c>rsa-sha2-256</c> 而不是 SHA-1 的 <c>ssh-rsa</c>。</summary>
    RsaSha2_256 = 0x02,

    /// <summary>用 <c>rsa-sha2-512</c>。</summary>
    RsaSha2_512 = 0x04,
}
