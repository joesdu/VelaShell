// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §5.3、§5.4
//           velashell-docs/zh/ssh/design/architecture.md §8 第 7 项

namespace VelaShell.Ssh.HostKeys;

/// <summary>接受任何主机密钥。</summary>
/// <remarks>
/// ⚠️ <b>只用于测试。</b>它等于关掉中间人防护 —— 而 SSH 的全部安全性
/// 都建立在「你确实连到了你以为的那台机器」之上。
/// <para>
/// 它刻意没有一个友好的名字，也刻意在文档里说得这么重：
/// 让任何在生产代码里看到它的人当场停下来。
/// </para>
/// </remarks>
public sealed class DangerousAcceptAnyHostKeyPolicy : IHostKeyPolicy
{
    /// <inheritdoc />
    public ValueTask<SshHostKeyVerdict> EvaluateAsync(
        SshHostKeyContext context, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(SshHostKeyVerdict.Accept);
}
