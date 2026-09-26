// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH PROTOCOL        auth-agent-req@openssh.com / auth-agent@openssh.com
//   行为规格:               velashell-docs/zh/ssh/spec/07-forwarding.md §七

using VelaShell.Ssh.HostKeys;

namespace VelaShell.Ssh.Forwarding;

/// <summary>远端请求用某把密钥签名时，交给使用者定夺。</summary>
/// <param name="Key">远端想用哪把钥。</param>
/// <param name="Comment">这把钥在 agent 里的注释（通常是私钥文件路径）。</param>
public readonly record struct AgentSignatureRequest(SshPublicKey Key, string Comment);
