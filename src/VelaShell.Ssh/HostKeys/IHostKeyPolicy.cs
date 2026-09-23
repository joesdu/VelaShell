// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §5.3、§5.4
//           velashell-docs/zh/ssh/design/architecture.md §8 第 7 项

using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.HostKeys;

/// <summary>主机密钥策略的裁决结果。</summary>
public enum SshHostKeyDecision
{
    /// <summary>接受这一次连接，但不持久化信任。</summary>
    Accept,

    /// <summary>接受并请求调用方把这把密钥记下来（TOFU 的「记住」）。</summary>
    AcceptAndPersist,

    /// <summary>拒绝。<b>必须</b>带上给用户看的原因。</summary>
    Reject,
}

/// <summary>一次主机密钥裁决的结果。</summary>
/// <param name="Decision">裁决。</param>
/// <param name="Reason">
/// 拒绝时给用户看的**人话**。说清是哪台、哪把指纹、以及下一步去哪操作。
/// </param>
public readonly record struct SshHostKeyVerdict(SshHostKeyDecision Decision, string? Reason = null)
{
    /// <summary>接受这一次。</summary>
    public static SshHostKeyVerdict Accept { get; } = new(SshHostKeyDecision.Accept);

    /// <summary>接受并持久化。</summary>
    public static SshHostKeyVerdict AcceptAndPersist { get; } = new(SshHostKeyDecision.AcceptAndPersist);

    /// <summary>拒绝，并给出原因。</summary>
    public static SshHostKeyVerdict Reject(string reason) =>
        new(SshHostKeyDecision.Reject, reason ?? throw new ArgumentNullException(nameof(reason)));
}

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

/// <summary>只接受指纹在给定集合里的主机密钥。</summary>
/// <remarks>
/// 适合「已知目标、指纹写死在配置里」的自动化场景 ——
/// 比 TOFU 强，因为它连第一次都不盲信。
/// </remarks>
public sealed class PinnedFingerprintHostKeyPolicy : IHostKeyPolicy
{
    private readonly HashSet<string> _fingerprints;

    /// <summary>用给定的 SHA-256 指纹集合构造。</summary>
    /// <param name="sha256Fingerprints">
    /// 形如 <c>SHA256:abc...</c>，与 <see cref="SshPublicKey.Sha256Fingerprint"/> 同格式。
    /// 为方便起见，不带 <c>SHA256:</c> 前缀的也接受。
    /// </param>
    public PinnedFingerprintHostKeyPolicy(IEnumerable<string> sha256Fingerprints)
    {
        ArgumentNullException.ThrowIfNull(sha256Fingerprints);
        _fingerprints = new HashSet<string>(
            sha256Fingerprints.Select(Normalize), StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public ValueTask<SshHostKeyVerdict> EvaluateAsync(
        SshHostKeyContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        string actual = context.Key.Sha256Fingerprint;
        return ValueTask.FromResult(_fingerprints.Contains(Normalize(actual))
            ? SshHostKeyVerdict.Accept
            : SshHostKeyVerdict.Reject(
                $"{context.Target} 的主机密钥指纹不在允许列表里。" +
                $"对端出示：{actual}（{context.Key.KeyType}, {context.Key.KeyBits} 位）。"));
    }

    private static string Normalize(string fingerprint) =>
        fingerprint.StartsWith("SHA256:", StringComparison.Ordinal) ? fingerprint : "SHA256:" + fingerprint;
}
