// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-miller-ssh-agent  SSH Agent Protocol
//   OpenSSH PROTOCOL.agent  实现口径
//   行为规格:               velashell-docs/zh/ssh/design/architecture.md §8 第 5 项;velashell-docs/zh/ssh/spec/07-forwarding.md §七(加钥见 §7.3)

namespace VelaShell.Ssh.Keys;

/// <summary>往 agent 里加钥时附带的约束（<c>ssh-add -t</c> / <c>ssh-add -c</c>）。</summary>
/// <remarks>
/// 并非每个 agent 都支持约束 —— 不支持的会整条请求拒绝，而不是忽略约束。
/// </remarks>
public sealed record SshAgentKeyConstraints
{
    /// <summary>多久之后由 agent 自己删掉这把钥；<see langword="null"/> 表示不限。</summary>
    /// <remarks>按整秒发送，至少 1 秒。</remarks>
    public TimeSpan? Lifetime { get; init; }

    /// <summary>每次用这把钥签名时，由 agent 向使用者确认。</summary>
    public bool ConfirmEachUse { get; init; }

    internal bool IsEmpty => Lifetime is null && !ConfirmEachUse;
}
