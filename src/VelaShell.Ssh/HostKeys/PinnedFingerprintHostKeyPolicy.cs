// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §5.3、§5.4
//           velashell-docs/zh/ssh/design/architecture.md §8 第 7 项

namespace VelaShell.Ssh.HostKeys;

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
