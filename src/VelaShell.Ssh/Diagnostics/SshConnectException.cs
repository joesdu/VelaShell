// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/08-failures.md §1(原则)、§2(层级)、§5(带上下文的三个异常)

namespace VelaShell.Ssh.Diagnostics;

/// <summary>建连阶段的失败（拨号、版本交换、协商、主机密钥）。</summary>
public class SshConnectException : SshException
{
    /// <summary>用给定原因、阶段与消息创建异常。</summary>
    public SshConnectException(SshFailureReason reason, SshPhase phase, string message, Exception? innerException = null)
        : base(reason, phase, message, innerException)
    {
    }

    /// <summary>
    /// 链路上每一跳的结果（代理、跳板、最终目标）。
    /// </summary>
    /// <remarks>
    /// 失败在哪一跳**必须能看出来**：「连不上跳板机 jump.example.com」与
    /// 「连不上 10.0.0.9」对用户是两个完全不同的问题，而没有这张表它们长得一模一样。
    /// </remarks>
    public IReadOnlyList<SshHopInfo> Hops { get; init; } = [];
}
