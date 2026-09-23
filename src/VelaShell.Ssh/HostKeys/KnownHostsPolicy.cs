// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §5.3、§5.4

namespace VelaShell.Ssh.HostKeys;

/// <summary>没见过这台主机时怎么办。</summary>
public enum UnknownHostBehavior
{
    /// <summary>
    /// 问使用者（TOFU）。
    /// </summary>
    /// <remarks>
    /// 交互式客户端用这个。<b>裁决耗时不计入连接超时</b>，
    /// 所以弹窗可以一直摆着等用户看指纹。
    /// </remarks>
    Ask,

    /// <summary>直接接受并记下来。</summary>
    /// <remarks>⚠️ 等于 <c>StrictHostKeyChecking no</c> 的第一次，首连是盲信的。</remarks>
    AcceptAndPersist,

    /// <summary>拒绝。对应 <c>StrictHostKeyChecking yes</c>。</summary>
    Reject,
}

/// <summary>按 <c>known_hosts</c> 裁决主机密钥。</summary>
/// <remarks>
/// <para>
/// <b>「密钥变了」与「没见过」是两件完全不同的事，所以走两条不同的路。</b>
/// </para>
/// <para>
/// 没见过 → 可以问、可以记（TOFU）。
/// 变了 → <b>默认直接拒绝，并且不提供「记住新的」这条捷径</b>：
/// 它可能是中间人，也可能只是服务器重装了，而这两者在协议层无法区分。
/// 要接受新密钥，使用者得先把旧的那一行删掉 —— 那一下手动操作正是让人
/// 停下来想一想的地方。异常消息里会指出是文件的第几行。
/// </para>
/// </remarks>
public sealed class KnownHostsPolicy : IHostKeyPolicy
{
    private readonly string _path;
    private readonly Func<SshHostKeyContext, CancellationToken, ValueTask<bool>>? _askUser;
    private IReadOnlyList<KnownHostEntry>? _cache;

    /// <summary>用给定的 <c>known_hosts</c> 路径构造。</summary>
    /// <param name="path"><c>known_hosts</c> 路径；<see langword="null"/> 表示用默认路径。</param>
    /// <param name="askUser">
    /// 没见过这台主机时问使用者。返回 <see langword="true"/> 表示信任并记下来。
    /// </param>
    public KnownHostsPolicy(
        string? path = null,
        Func<SshHostKeyContext, CancellationToken, ValueTask<bool>>? askUser = null)
    {
        _path = path ?? KnownHostsFile.DefaultPath;
        _askUser = askUser;
    }

    /// <summary>没见过这台主机时的行为。</summary>
    public UnknownHostBehavior UnknownHost { get; init; } = UnknownHostBehavior.Ask;

    /// <summary>
    /// 允许在密钥变化时也接受。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>默认关闭，而且不该打开。</b>
    /// 这个开关存在只是为了「我知道我刚重装了那台机器」这一种情形，
    /// 而那种情形下更好的做法是去把 <c>known_hosts</c> 里那一行删掉。
    /// </remarks>
    public bool DangerouslyAcceptChangedKeys { get; init; }

    /// <summary>写入时是否把主机名散列掉（对应 <c>HashKnownHosts yes</c>）。</summary>
    public bool HashHostNames { get; init; }

    /// <inheritdoc />
    public async ValueTask<SshHostKeyVerdict> EvaluateAsync(
        SshHostKeyContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyList<KnownHostEntry> entries =
            _cache ??= await KnownHostsFile.LoadAsync(_path, cancellationToken).ConfigureAwait(false);

        KnownHostLookup lookup = KnownHostsFile.Lookup(entries, context.Host, context.Port, context.Key);

        switch (lookup.Status)
        {
            case KnownHostStatus.Known:
                return SshHostKeyVerdict.Accept;

            case KnownHostStatus.Revoked:
                return SshHostKeyVerdict.Reject(
                    $"{context.Target} 出示的主机密钥在 {_path} 里被标记为 @revoked" +
                    $"（第 {lookup.MatchedEntry?.LineNumber} 行）。" +
                    "这把密钥已经作废 —— 不要连。");

            case KnownHostStatus.Changed:
                return DangerouslyAcceptChangedKeys
                    ? SshHostKeyVerdict.Accept
                    : SshHostKeyVerdict.Reject(BuildChangedMessage(context, lookup));

            default:
                return await HandleUnknownAsync(context, cancellationToken).ConfigureAwait(false);
        }
    }

    private string BuildChangedMessage(SshHostKeyContext context, KnownHostLookup lookup)
    {
        string lines = string.Join(
            "、", lookup.ConflictingEntries.Select(e => $"第 {e.LineNumber} 行"));

        return
            $"⚠️ {context.Target} 的主机密钥**变了**。" + Environment.NewLine +
            $"对端现在出示：{context.Key.Sha256Fingerprint}" +
            $"（{context.Key.KeyType}，{context.Key.KeyBits} 位）" + Environment.NewLine +
            $"而 {_path} 里记的是另一把（{lines}）。" + Environment.NewLine +
            "这可能是中间人攻击，也可能只是那台服务器重装了 —— " +
            "**协议层分不出这两者**，所以得由你来判断。" + Environment.NewLine +
            $"确认是重装之后，把 {_path} 里上面那一行删掉再连。";
    }

    private async ValueTask<SshHostKeyVerdict> HandleUnknownAsync(
        SshHostKeyContext context, CancellationToken cancellationToken)
    {
        switch (UnknownHost)
        {
            case UnknownHostBehavior.AcceptAndPersist:
                return SshHostKeyVerdict.AcceptAndPersist;

            case UnknownHostBehavior.Reject:
                return SshHostKeyVerdict.Reject(
                    $"没见过 {context.Target} 这台主机，而策略是不接受新主机。" +
                    $"对端出示：{context.Key.Sha256Fingerprint}（{context.Key.KeyType}）。" +
                    $"确认无误后可以手动加进 {_path}。");

            default:
                if (_askUser is null)
                {
                    return SshHostKeyVerdict.Reject(
                        $"没见过 {context.Target} 这台主机，而这个策略没有配询问回调。" +
                        $"对端出示：{context.Key.Sha256Fingerprint}（{context.Key.KeyType}）。");
                }

                bool trusted = await _askUser(context, cancellationToken).ConfigureAwait(false);
                return trusted
                    ? SshHostKeyVerdict.AcceptAndPersist
                    : SshHostKeyVerdict.Reject($"使用者拒绝信任 {context.Target} 的主机密钥。");
        }
    }

    /// <inheritdoc />
    public async ValueTask PersistAsync(SshHostKeyContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        await KnownHostsFile.AppendAsync(
            context.Host, context.Port, context.Key, _path, HashHostNames, cancellationToken)
            .ConfigureAwait(false);

        // 缓存作废 —— 下一次裁决要看到刚写进去的这一条。
        _cache = null;
    }
}
