using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Ssh.HostKeys;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 把宿主的主机信任策略(<see cref="IHostKeyService" /> + <see cref="IHostKeyPrompt" /> +
/// 安全设置)接到 SSH 库的 <see cref="IHostKeyPolicy" /> 上。
/// </summary>
/// <remarks>
/// <para>
/// 链上**每一跳共用同一个实例** —— 跳板与终点走同一套策略。
/// 这一条以前是另写的一段:未知 / 变更一律返回 false,既不弹窗也不说原因,
/// 于是「只当跳板、从没单独连过的机器」第一次用就连不上。
/// </para>
/// <para>
/// <b>拒绝原因直接随裁决返回</b>(<see cref="SshHostKeyVerdict.Reject" />),
/// 不再需要 <c>HostKeyPromptOutcome</c> 那块「原因牌」—— 上一版底层库的回调只能返回
/// true/false,原因得靠一个旁路对象递出去,再由包装器在失败时读回来。
/// </para>
/// <para>
/// <b>弹窗的时间不计入连接超时</b>(库的 <c>HostKeyDecisionTimeout</c> 默认无限),
/// 所以那段「用户点了信任、这一轮却已被判超时,于是原地补连一次」的补偿逻辑
/// 也一并不存在了。
/// </para>
/// </remarks>
/// <param name="hostKey">已知主机记录。</param>
/// <param name="settings">安全设置(首次是否确认、变更是否阻断)。</param>
/// <param name="prompt">指纹确认弹窗;为 <see langword="null" /> 时走不问的两条默认。</param>
/// <param name="alerts">安全告警。</param>
internal sealed class VelaHostKeyPolicy(
    IHostKeyService hostKey,
    ISettingsService? settings,
    IHostKeyPrompt? prompt,
    ISecurityAlertService? alerts) : IHostKeyPolicy
{
    /// <inheritdoc />
    /// <remarks>
    /// 三条出路:已信任直接放行、按设置弹窗裁决、fail-closed 拒绝。
    /// <para>
    /// **指纹变更默认走弹窗而不是直接拒**(#476)。「换了台服务器、IP 没变」是运维里
    /// 最常见的正当变更,旧行为把它当成一条连接错误抛出来,用户看到的只有一句英文的
    /// <c>UntrustedPeer</c>,唯一的出路是去 <c>.velashell</c> 里手动删记录 ——
    /// 而其它 SSH 客户端在这里弹的是一个「接受并覆盖 / 拒绝」的框。
    /// 要严格 fail-closed 的,把「指纹变更时阻断并告警」打开即可。
    /// </para>
    /// </remarks>
    public async ValueTask<SshHostKeyVerdict> EvaluateAsync(
        SshHostKeyContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        SecurityOptions security = GetSecurityOptions(settings);

        string host = context.Host;
        int port = context.Port;
        // 主机证书按证书里那把钥记、按那把钥比:证书每次重签 blob 都会变,钥不变
        // (指纹本身就是那把钥的,见 SshPublicKey.Sha256Fingerprint)。
        string fingerprint = context.Key.Sha256Fingerprint;
        string keyType = context.Key.PlainKeyType;
        string target = context.Target;

        HostKeyVerification verification = await hostKey
            .VerifyHostKeyAsync(host, port, keyType, fingerprint, cancellationToken).ConfigureAwait(false);

        if (verification == HostKeyVerification.Trusted
            || HostTrustOnceCache.IsTrusted(host, port, fingerprint))
        {
            return SshHostKeyVerdict.Accept;
        }

        // 变更时把旧指纹取出来,弹窗与错误文案都要摆它 —— 用户靠两把对照才判断得了
        // 这是自己刚重装的那台,还是路上有人。读不到就当没有,绝不因此阻断判定。
        string? knownFingerprint = null;
        if (verification == HostKeyVerification.Changed)
        {
            try
            {
                knownFingerprint = (await hostKey.FindKnownHostAsync(host, port, cancellationToken)
                    .ConfigureAwait(false))?.Fingerprint;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                System.Diagnostics.Trace.WriteLine($"[VelaShell] known_hosts lookup failed: {ex}");
            }
        }

        bool firstSeen = verification == HostKeyVerification.Unknown;
        bool askUser = prompt is not null
                       && (firstSeen ? security.ConfirmFirstFingerprint : !security.BlockOnFingerprintChange);

        HostKeyDecision decision = askUser
            ? await prompt!.DecideAsync(host, port, keyType, fingerprint, verification, knownFingerprint)
                .ConfigureAwait(false)
            // 不问的两条默认:首次连接按 TOFU 记下,指纹变更 fail-closed 拒掉。
            : firstSeen
                ? HostKeyDecision.TrustPermanently
                : HostKeyDecision.Reject;

        switch (decision)
        {
            case HostKeyDecision.Reject:
                alerts?.RaiseAsync("hostkey-rejected",
                    Strings.Format("KeySvc_AlertFirstRejected", target, fingerprint));
                return SshHostKeyVerdict.Reject(
                    DescribeRejection(target, fingerprint, knownFingerprint, verification, askUser));

            case HostKeyDecision.TrustOnce:
                HostTrustOnceCache.Remember(host, port, fingerprint);
                if (alerts is not null)
                {
                    await alerts.RaiseAsync("hostkey-trusted-once",
                        verification == HostKeyVerification.Changed
                            ? Strings.Format("KeySvc_AlertChangedTrustOnce", target, fingerprint)
                            : Strings.Format("KeySvc_AlertFirstTrustOnce", target, fingerprint))
                        .ConfigureAwait(false);
                }
                return SshHostKeyVerdict.Accept;

            default:
                if (verification == HostKeyVerification.Changed && alerts is not null)
                {
                    await alerts.RaiseAsync("hostkey-changed-accepted",
                        Strings.Format("KeySvc_AlertChangedAccepted", target, fingerprint)).ConfigureAwait(false);
                }
                // 真正落盘交给 PersistAsync —— 库只在握手确实走到这一步之后才调它。
                return SshHostKeyVerdict.AcceptAndPersist;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 裁决与落盘分成两步是库的设计:裁决那一步跑在独立的计时里(可能要等用户),
    /// 落盘则跟着连接本身的取消令牌走。两者混在一个回调里时,
    /// 写 known_hosts 这件事会被裁决的超时口径一起管住 —— 那是不对的。
    /// <para>
    /// 落盘发生在密钥交换阶段、认证**之前**,与 OpenSSH 的时机一致:
    /// 主机身份在 KEX 就已经证明完了,认证成不成功是另一回事。
    /// </para>
    /// </remarks>
    public async ValueTask PersistAsync(
        SshHostKeyContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        await hostKey.TrustHostKeyAsync(
            context.Host, context.Port, context.Key.PlainKeyType, context.Key.Sha256Fingerprint, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>被拒时给用户看的那句话:说清是哪台、哪把指纹、以及下一步去哪操作。</summary>
    private static string DescribeRejection(
        string target, string fingerprint, string? knownFingerprint,
        HostKeyVerification verification, bool askedUser) =>
        verification == HostKeyVerification.Changed && !askedUser
            // 阻断开关开着:用户根本没被问过,得告诉他这是策略拦的,以及两条出路。
            ? Strings.Format("Msg_HostKeyChangedBlocked", target, knownFingerprint ?? "-", fingerprint)
            : Strings.Format("Msg_HostKeyRejected", target, fingerprint);

    private static SecurityOptions GetSecurityOptions(ISettingsService? settings)
    {
        try
        {
            return settings.GetSnapshotBlocking().Security;
        }
        catch
        {
            return new();
        }
    }
}
