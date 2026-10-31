using System.Text;
using VelaShell.Core.Models;
using VelaShell.Core.Net;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Persistence;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.HostKeys;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>导入一份 <c>known_hosts</c> 的结果(各项是条数)。</summary>
/// <param name="Imported">新记进信任库的主机密钥。</param>
/// <param name="AlreadyTrusted">信任库里已经有同一把钥(只补上公钥原文)。</param>
/// <param name="Conflicts">这台主机已经信任了同一类型(或类型说不清的旧记录)的另一把钥:不覆盖、不并排加。</param>
/// <param name="Authorities">新加的受信主机 CA(<c>@cert-authority</c>)。</param>
/// <param name="Revocations">新加的吊销记录(<c>@revoked</c>)。</param>
/// <param name="MarkersAlreadyPresent">信任库里已经有的 CA / 吊销记录。</param>
/// <param name="RevokedSkipped">普通行的钥被同一份文件(或信任库)里的 <c>@revoked</c> 吊销了:不导入。</param>
/// <param name="HashedUnmatched">散列过的主机名对不上任何已保存的连接、已信任的主机:认不出是哪台。</param>
/// <param name="Wildcards">带通配的普通行:落不到某一台主机上。</param>
/// <param name="Unsupported">本版认不得的密钥类型、写坏的行,或主机名不能原样记下。</param>
public sealed record KnownHostsImportResult(
    int Imported,
    int AlreadyTrusted,
    int Conflicts,
    int Authorities,
    int Revocations,
    int MarkersAlreadyPresent,
    int RevokedSkipped,
    int HashedUnmatched,
    int Wildcards,
    int Unsupported);

/// <summary>导出 <c>known_hosts</c> 的结果。</summary>
/// <param name="Content">OpenSSH 格式的文本(每行以 <c>\n</c> 结尾)。</param>
/// <param name="HostKeys">写出的主机密钥行。</param>
/// <param name="Markers">写出的 <c>@cert-authority</c> / <c>@revoked</c> 行。</param>
/// <param name="MissingPublicKey">只记了指纹、没有公钥原文的记录(下次连上那台主机时会补上):写不出来。</param>
/// <param name="Unrecordable">主机名里有 <c>known_hosts</c> 另有含义的字符:写不出来。</param>
public sealed record KnownHostsExport(string Content, int HostKeys, int Markers, int MissingPublicKey, int Unrecordable);

/// <summary>
/// 信任库与 OpenSSH <c>known_hosts</c> 之间的导入、导出:导入之后与命令行 ssh 共用一份信任基线。
/// </summary>
/// <remarks>
/// <para>
/// 解析、主机名匹配(取反、散列、<c>[host]:port</c>)、行格式都用 SSH 库的 <see cref="KnownHostsFile" />,宿主不另写一份。
/// 信任库按 host:port 逐台记(<see cref="KnownHost" />),<c>known_hosts</c> 的一行却是一栏模式,于是普通行只落得下
/// 字面量的主机名:散列行拿已保存的连接与已信任的主机逐个去对,带通配的普通行如实跳过并计数。
/// </para>
/// <para>
/// <b>导入只加不改</b>:这台主机已经信任了同一类型的另一把钥时不覆盖 —— 一份文件里写着的钥不比用户亲手确认过的更可信,
/// 悄悄换掉等于把「密钥变了」的检查绕过去。换库之前存下、类型只记了 <c>PublicKey</c> 这类占位词的旧记录说不清是哪种钥,
/// 同样按冲突处理,不把文件里的钥并排加进去(否则两把都受信)。<c>@revoked</c> 照收(连接时先看它,吊销压过信任),
/// 同一份文件里被它吊销的普通行也不导入。
/// </para>
/// </remarks>
public static class KnownHostsInterop
{
    /// <summary>导入一份 <c>known_hosts</c> 文本。</summary>
    /// <param name="store">信任库。</param>
    /// <param name="content">文件内容。</param>
    /// <param name="candidates">用来对散列主机名的 host:port(已保存的连接、已信任的主机)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<KnownHostsImportResult> ImportAsync(
        IHostKeyService store,
        string content,
        IEnumerable<(string Host, int Port)> candidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(candidates);

        string[] lines = content.Split('\n');
        IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(content);
        // 认不出的行(注释与空行之外):写坏的、认不出的标记,库都整行跳过,这里只数一下。
        int unsupported = lines.Count(static raw => raw.Trim('\r', ' ', '\t') is { Length: > 0 } line && line[0] != '#')
                          - entries.Count;

        int authorities = 0, revocations = 0, markersPresent = 0;
        foreach (KnownHostEntry entry in entries.Where(static e => e.IsCertificateAuthority || e.IsRevoked))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string comment = entry.LineNumber - 1 < lines.Length
                ? KnownHostMarkers.CommentOf(lines[entry.LineNumber - 1], fieldsBefore: 4)
                : string.Empty;
            KnownHostMarker marker = KnownHostMarkers.FromEntry(entry, comment)!;
            if (!await store.AddMarkerAsync(marker, cancellationToken).ConfigureAwait(false))
            {
                markersPresent++;
            }
            else if (marker.Kind == KnownHostMarkerKind.CertificateAuthority)
            {
                authorities++;
            }
            else
            {
                revocations++;
            }
        }

        // 吊销看两处:这份文件里的,和信任库里已有的(含刚导入的)。
        IReadOnlyList<KnownHostEntry> revoked = KnownHostMarkers.ToEntries(
            (await store.GetMarkersAsync(cancellationToken).ConfigureAwait(false)).Where(static m => m.Kind == KnownHostMarkerKind.Revoked));

        List<(string Host, int Port)> candidateList = [.. candidates.Distinct()];
        int imported = 0, already = 0, conflicts = 0, revokedSkipped = 0, hashedUnmatched = 0, wildcards = 0;
        foreach (KnownHostEntry entry in entries.Where(static e => !e.IsCertificateAuthority && !e.IsRevoked))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SshPublicKey key;
            try
            {
                key = SshPublicKey.Decode(entry.KeyBlob);
            }
            catch (SshPublicKeyException)
            {
                unsupported++;
                continue;
            }
            if (key.IsCertificate)
            {
                // 普通行里的证书永远对不上任何主机(库按证书里那把钥比),OpenSSH 也不这么写。
                unsupported++;
                continue;
            }

            List<(string Host, int Port)> targets = TargetsOf(entry, key, candidateList, out bool hasWildcard);
            if (targets.Count == 0)
            {
                if (entry.IsHashed)
                {
                    hashedUnmatched++;
                }
                else if (hasWildcard)
                {
                    wildcards++;
                }
                else
                {
                    unsupported++;
                }
                continue;
            }

            foreach ((string host, int port) in targets)
            {
                if (revoked.Count > 0 && KnownHostsFile.Lookup(revoked, host, port, key).Status == KnownHostStatus.Revoked)
                {
                    revokedSkipped++;
                    continue;
                }
                switch (await TrustAsync(store, host, port, key, cancellationToken).ConfigureAwait(false))
                {
                    case TrustOutcome.Imported:
                        imported++;
                        break;
                    case TrustOutcome.AlreadyTrusted:
                        already++;
                        break;
                    default:
                        conflicts++;
                        break;
                }
            }
        }

        return new KnownHostsImportResult(imported, already, conflicts, authorities, revocations, markersPresent,
            revokedSkipped, hashedUnmatched, wildcards, Math.Max(0, unsupported));
    }

    /// <summary>把信任库写成一份 OpenSSH 格式的 <c>known_hosts</c>。</summary>
    /// <param name="store">信任库。</param>
    /// <param name="hashHostNames">主机名散列成 <c>|1|…</c>(OpenSSH 的 <c>HashKnownHosts yes</c>)。只作用于主机密钥行:
    /// <c>@cert-authority</c> / <c>@revoked</c> 的那一栏是模式,带通配的模式散列不了,原样写出。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<KnownHostsExport> ExportAsync(
        IHostKeyService store, bool hashHostNames, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        List<KnownHost> hosts = await store.GetKnownHostsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<KnownHostMarker> markers = await store.GetMarkersAsync(cancellationToken).ConfigureAwait(false);

        StringBuilder text = new();
        int written = 0, missing = 0, unrecordable = 0;
        foreach (KnownHost host in hosts
                     .OrderBy(static h => h.Host, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static h => h.Port)
                     .ThenBy(static h => h.KeyType, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryDecodeRecorded(host, out SshPublicKey? key))
            {
                missing++;
                continue;
            }
            if (!KnownHostsFile.IsRecordableHost(host.Host))
            {
                unrecordable++;
                continue;
            }
            text.Append(KnownHostsFile.FormatEntry(host.Host, host.Port, key, hashHostNames)).Append('\n');
            written++;
        }

        foreach (KnownHostMarker marker in markers)
        {
            text.Append(KnownHostMarkers.FormatLine(marker)).Append('\n');
        }

        return new KnownHostsExport(text.ToString(), written, markers.Count, missing, unrecordable);
    }

    /// <summary>这一行落到哪几台 host:port 上。</summary>
    /// <remarks>
    /// 字面量的名字逐个拿库的 <see cref="KnownHostsFile.Lookup(IReadOnlyList{KnownHostEntry}, string, int, SshPublicKey)" />
    /// 再对一遍 —— 同一行里的取反模式(<c>a,b,!b</c>)就按库的规则一票否决,不另写一套。散列行没有名字可取,拿候选逐个去对。
    /// </remarks>
    private static List<(string Host, int Port)> TargetsOf(
        KnownHostEntry entry, SshPublicKey key, List<(string Host, int Port)> candidates, out bool hasWildcard)
    {
        hasWildcard = false;
        IEnumerable<(string Host, int Port)> named;
        if (entry.IsHashed)
        {
            named = candidates;
        }
        else
        {
            List<(string Host, int Port)> literal = [];
            foreach (string pattern in entry.Patterns)
            {
                if (pattern.StartsWith('!'))
                {
                    continue;
                }
                if (pattern.AsSpan().IndexOfAny('*', '?') >= 0)
                {
                    hasWildcard = true;
                    continue;
                }
                if (TryParseHostPattern(pattern, out string host, out int port))
                {
                    literal.Add((host, port));
                }
            }
            named = literal;
        }

        KnownHostEntry[] single = [entry];
        return
        [
            .. named
                .Where(target => KnownHostsFile.IsRecordableHost(target.Host)
                                 && KnownHostsFile.Lookup(single, target.Host, target.Port, key).Status == KnownHostStatus.Known)
                .Distinct(),
        ];
    }

    /// <summary><c>host</c>(端口 22)或 <c>[host]:port</c>。</summary>
    internal static bool TryParseHostPattern(string pattern, out string host, out int port)
    {
        host = pattern;
        port = 22;
        if (!pattern.StartsWith('['))
        {
            return pattern.Length > 0 && !pattern.Contains(']', StringComparison.Ordinal);
        }
        int close = pattern.IndexOf("]:", StringComparison.Ordinal);
        if (close <= 1 || !int.TryParse(pattern.AsSpan(close + 2), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)
        {
            return false;
        }
        host = HostAddress.Normalize(pattern[1..close]);
        return host.Length > 0;
    }

    private enum TrustOutcome
    {
        Conflict,
        Imported,
        AlreadyTrusted,
    }

    /// <summary>记一把钥:同一把已经有了只补公钥原文;同一类型另有一把不覆盖。</summary>
    private static async Task<TrustOutcome> TrustAsync(
        IHostKeyService store, string host, int port, SshPublicKey key, CancellationToken cancellationToken)
    {
        string blob = Convert.ToBase64String(key.Blob.Span);
        IReadOnlyList<KnownHost> known = await store.FindKnownHostKeysAsync(host, port, cancellationToken).ConfigureAwait(false);
        if (known.FirstOrDefault(k => SonnetDbHostKeyService.SameFingerprint(k.Fingerprint, key.Sha256Fingerprint)) is { } same)
        {
            if (string.IsNullOrEmpty(same.HostKey))
            {
                await store.TrustHostKeyAsync(host, port, key.PlainKeyType, key.Sha256Fingerprint, blob, cancellationToken)
                    .ConfigureAwait(false);
            }
            return TrustOutcome.AlreadyTrusted;
        }
        // 记录的真实类型说不清(换库之前存的 PublicKey 这类占位词)时同样算冲突:那条可能正是这个类型的另一把钥,
        // 把文件里的这把并排记进去,两把就都受信了(校验只比指纹)。
        if (known.Any(k => RecordedKeyType(k) is not { } type || string.Equals(type, key.PlainKeyType, StringComparison.Ordinal)))
        {
            return TrustOutcome.Conflict;
        }
        await store.TrustHostKeyAsync(host, port, key.PlainKeyType, key.Sha256Fingerprint, blob, cancellationToken).ConfigureAwait(false);
        return TrustOutcome.Imported;
    }

    /// <summary>
    /// 一条记录记的是哪种钥:公钥原文解得出来就按原文(<see cref="SshPublicKey.PlainKeyType" />);否则记着的类型是认得的主机密钥算法名
    /// 就用它;都不是(<c>PublicKey</c> 这类占位词、空的)时为 <see langword="null" />。
    /// </summary>
    private static string? RecordedKeyType(KnownHost host)
    {
        if (TryDecodeRecorded(host, out SshPublicKey? decoded))
        {
            return decoded.PlainKeyType;
        }
        return host.KeyType is { Length: > 0 } type
               && (SshAlgorithmCatalog.Implemented(SshAlgorithmCategory.HostKey).Contains(type, StringComparer.Ordinal)
                   || SshAlgorithmCatalog.KnownUnimplemented(SshAlgorithmCategory.HostKey).Contains(type, StringComparer.Ordinal))
            ? type
            : null;
    }

    /// <summary>记录里的公钥原文解得出来、而且就是记着的那把指纹。</summary>
    private static bool TryDecodeRecorded(KnownHost host, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SshPublicKey? key)
    {
        key = null;
        if (string.IsNullOrEmpty(host.HostKey))
        {
            return false;
        }
        try
        {
            var decoded = SshPublicKey.Decode(Convert.FromBase64String(host.HostKey));
            if (!SonnetDbHostKeyService.SameFingerprint(host.Fingerprint, decoded.Sha256Fingerprint))
            {
                return false;
            }
            key = decoded;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or SshPublicKeyException)
        {
            return false;
        }
    }
}
