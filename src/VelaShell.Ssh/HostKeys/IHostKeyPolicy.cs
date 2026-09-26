// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §5.3、§5.4
//           velashell-docs/zh/ssh/design/architecture.md §8 第 7 项

namespace VelaShell.Ssh.HostKeys;

/// <summary>
/// 决定要不要信任服务端出示的主机密钥。
/// </summary>
/// <remarks>
/// <para>
/// <b>这是一个接口而不是委托</b>，因为策略要带状态：known_hosts 的句柄、CA 信任链、
/// 本次运行里「只信这一次」的临时集合。委托装不下这些。
/// </para>
/// <para>
/// <b>裁决独立计时。</b>本方法的耗时**不计入连接超时**，由
/// <c>HostKeyDecisionTimeout</c>（默认无限）单独约束。
/// </para>
/// <para>
/// 这一条是冲着一个具体缺陷去的：交互式客户端在这里要弹窗问用户，
/// 而弹窗摆着的时间如果算进连接超时，用户点完「信任」这一轮已经被判死，
/// 只能原地补连一次 —— 那种「补连一次」的代码与它的解释性注释，
/// 在两个计时分开之后就不必存在了（velashell-docs/zh/ssh/spec/03 §5.3）。
/// </para>
/// </remarks>
public interface IHostKeyPolicy
{
    /// <summary>裁决一把主机密钥。</summary>
    /// <param name="context">裁决材料。</param>
    /// <param name="cancellationToken">取消令牌（连接被中止时触发）。</param>
    ValueTask<SshHostKeyVerdict> EvaluateAsync(SshHostKeyContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// 裁决为 <see cref="SshHostKeyDecision.AcceptAndPersist"/> 时把密钥记下来。
    /// </summary>
    /// <remarks>默认什么都不做 —— 不持久化的策略（如「只信这一次」）不必实现它。</remarks>
    ValueTask PersistAsync(SshHostKeyContext context, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}
