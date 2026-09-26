// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-miller-ssh-agent  SSH Agent Protocol
//   OpenSSH PROTOCOL.agent  实现口径
//   行为规格:               velashell-docs/zh/ssh/design/architecture.md §8 第 5 项;velashell-docs/zh/ssh/spec/07-forwarding.md §七(加钥见 §7.3)

using VelaShell.Ssh.HostKeys;

namespace VelaShell.Ssh.Keys;

/// <summary>agent 里的一把密钥。</summary>
/// <param name="PublicKey">公钥。</param>
/// <param name="Comment">agent 给的注释，通常是私钥文件路径。</param>
public sealed record SshAgentIdentity(SshPublicKey PublicKey, string Comment);
