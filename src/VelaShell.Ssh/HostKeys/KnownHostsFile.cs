// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH sshd(8) 的 AUTHORIZED_KEYS / SSH_KNOWN_HOSTS 章节（含 @cert-authority / @revoked）
//   OpenSSH PROTOCOL.certkeys（主机证书）
//   行为规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §5.4、§5.5

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.HostKeys;

/// <summary>读写 <c>known_hosts</c>。</summary>
/// <remarks>
/// <para>
/// 支持三种主机名写法：明文（<c>example.com</c>）、带端口（<c>[example.com]:2222</c>）、
/// 以及 <b>散列过的</b>（<c>|1|base64(salt)|base64(hmac-sha1)</c>）。
/// </para>
/// <para>
/// 散列形式是 OpenSSH 的 <c>HashKnownHosts yes</c> 产物，在很多发行版上是默认 ——
/// 不支持它等于在那些机器上完全读不到已知主机。
/// </para>
/// </remarks>
public static class KnownHostsFile
{
    /// <summary>用户默认的 <c>known_hosts</c> 路径。</summary>
    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "known_hosts");

    /// <summary>解析一份 <c>known_hosts</c> 文本。</summary>
    /// <remarks>
    /// <b>解析不出来的行一律跳过，不抛异常。</b>
    /// 真实的 <c>known_hosts</c> 里什么都有：手写的注释、被别的工具写坏的行、
    /// 未来才定义的密钥类型。为其中一行报错，等于让整个文件不可用。
    /// </remarks>
    public static IReadOnlyList<KnownHostEntry> Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        List<KnownHostEntry> entries = [];
        string[] lines = content.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            KnownHostEntry? entry = ParseLine(lines[i].Trim('\r', ' ', '\t'), i + 1);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary>读一份 <c>known_hosts</c> 文件；文件不存在时返回空列表。</summary>
    public static async ValueTask<IReadOnlyList<KnownHostEntry>> LoadAsync(
        string? path = null, CancellationToken cancellationToken = default)
    {
        string actual = path ?? DefaultPath;

        if (!File.Exists(actual))
        {
            // 第一次用的机器上它本来就不存在。那不是错误。
            return [];
        }

        string content = await File.ReadAllTextAsync(actual, cancellationToken).ConfigureAwait(false);
        return Parse(content);
    }

    private static KnownHostEntry? ParseLine(string line, int lineNumber)
    {
        if (line.Length == 0 || line[0] == '#')
        {
            return null;
        }

        string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int index = 0;

        string marker = "";
        if (fields.Length > 0 && fields[0].StartsWith('@'))
        {
            marker = fields[0];
            index = 1;
        }

        // 主机 ‖ 密钥类型 ‖ base64 —— 少一样就不是一条有效记录。
        if (fields.Length < index + 3)
        {
            return null;
        }

        string hostField = fields[index];
        string keyType = fields[index + 1];
        string base64 = fields[index + 2];

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;   // 被写坏的行，跳过
        }

        bool hashed = hostField.StartsWith("|1|", StringComparison.Ordinal);

        return new KnownHostEntry(
            hashed ? [hostField] : [.. hostField.Split(',', StringSplitOptions.RemoveEmptyEntries)],
            hashed,
            marker,
            keyType,
            blob,
            lineNumber);
    }

    /// <summary>查一台主机的一把密钥（主机证书按此刻的时间验有效期）。</summary>
    /// <param name="entries">已解析的条目。</param>
    /// <param name="host">主机名或地址。</param>
    /// <param name="port">端口。<b>非 22 时要按 <c>[host]:port</c> 匹配。</b></param>
    /// <param name="key">服务端出示的公钥（普通公钥或主机证书）。</param>
    public static KnownHostLookup Lookup(
        IReadOnlyList<KnownHostEntry> entries, string host, int port, SshPublicKey key) =>
        Lookup(entries, host, port, key, DateTimeOffset.UtcNow);

    /// <summary>查一台主机的一把密钥。</summary>
    /// <param name="entries">已解析的条目。</param>
    /// <param name="host">主机名或地址。</param>
    /// <param name="port">端口。<b>非 22 时要按 <c>[host]:port</c> 匹配。</b></param>
    /// <param name="key">服务端出示的公钥（普通公钥或主机证书）。</param>
    /// <param name="now">验主机证书有效期用的时刻。</param>
    /// <remarks>
    /// 主机证书的裁决顺序见 velashell-docs/zh/ssh/spec/03 §5.5：吊销 → 证书里那把钥单独记着 →
    /// 有对上的 CA 就验证书 → 否则把证书里那把钥当普通钥。<b>比对与记录用的都是证书里那把钥</b>
    /// （<see cref="SshPublicKey.PlainKey"/>），不是证书 blob —— 证书每次重签 blob 都会变。
    /// </remarks>
    public static KnownHostLookup Lookup(
        IReadOnlyList<KnownHostEntry> entries, string host, int port, SshPublicKey key, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(key);

        string plain = FormatHostPattern(host, port);
        SshPublicKey presented = key.PlainKey;
        OpenSshCertificate? certificate = key.Certificate;
        SshPublicKey? authorityKey = certificate?.SignatureKey;

        List<KnownHostEntry> conflicts = [];
        List<KnownHostEntry> otherTypes = [];
        KnownHostEntry? trusted = null;
        KnownHostEntry? authority = null;

        // ⚠️ **必须把整份表扫完才能下结论。**
        //
        // 吊销要压过信任，而 @revoked 那一行通常是**追加**在后面的
        // （撤销一把密钥最自然的动作就是往文件末尾加一行）。
        // 一碰到「已知」就 return 的话，吊销会被前面那条旧的信任行盖掉 ——
        // 那是一个实打实的安全漏洞：撤销了等于没撤销。
        foreach (KnownHostEntry entry in entries)
        {
            if (!MatchesHost(entry, host, port, plain))
            {
                continue;
            }

            bool sameKey = entry.KeyBlob.Span.SequenceEqual(presented.Blob.Span);

            if (entry.IsRevoked)
            {
                // 吊销的可以是那把钥、整张证书，或者签发它的 CA（吊销一个 CA 就作废它签过的全部证书）。
                if (sameKey
                    || (certificate is not null && entry.KeyBlob.Span.SequenceEqual(key.Blob.Span))
                    || (authorityKey is not null && entry.KeyBlob.Span.SequenceEqual(authorityKey.Blob.Span)))
                {
                    // 吊销赢，立刻返回 —— 后面再有什么都不重要了。
                    return new KnownHostLookup(KnownHostStatus.Revoked, entry, []);
                }
                continue;
            }

            if (entry.IsCertificateAuthority)
            {
                // CA 只为它签的证书担保；不把 CA 公钥当成这台主机的普通密钥来比
                // （否则会把「出示了一张证书」误报成「密钥变了」）。
                if (authorityKey is not null && entry.KeyBlob.Span.SequenceEqual(authorityKey.Blob.Span))
                {
                    authority ??= entry;
                }
                else
                {
                    // 这台主机由（别的）CA 管：出示的钥若没有单独记着，不能当成没见过（见 OtherKeyTypesKnown）。
                    otherTypes.Add(entry);
                }
                continue;
            }

            if (sameKey)
            {
                trusted ??= entry;
                continue;
            }

            // 主机对上、密钥不对。**先记下来继续找** ——
            // 同一台主机可以有多把不同类型的密钥（ed25519 与 rsa 各一条），
            // 只有当没有任何一条对上时，「变了」才成立。
            if (entry.KeyType == presented.KeyType)
            {
                conflicts.Add(entry);
            }
            else
            {
                otherTypes.Add(entry);
            }
        }

        // 明确记下的钥优先：它已经被信任，证书不必再看。
        if (trusted is not null)
        {
            return new KnownHostLookup(KnownHostStatus.Known, trusted, []);
        }

        if (authority is not null)
        {
            string? problem = certificate!.CheckHostCertificate(host, now);
            return problem is null
                ? new KnownHostLookup(KnownHostStatus.Known, authority, [])
                : new KnownHostLookup(KnownHostStatus.CertificateInvalid, authority, []) { CertificateProblem = problem };
        }

        if (conflicts.Count > 0)
        {
            return new KnownHostLookup(KnownHostStatus.Changed, null, conflicts);
        }

        // 只记着别的类型：**不是「没见过」**（见 OtherKeyTypesKnown）。
        return otherTypes.Count > 0
            ? new KnownHostLookup(KnownHostStatus.OtherKeyTypesKnown, null, otherTypes)
            : new KnownHostLookup(KnownHostStatus.Unknown, null, []);
    }

    /// <summary>这台主机在 <c>known_hosts</c> 里记着哪些类型的密钥（不含吊销行）。</summary>
    /// <remarks>
    /// 对上这台主机的 <c>@cert-authority</c> 行报出全部证书类型 —— CA 能为任何类型的主机密钥签证书，
    /// 这样证书算法会被排到前面，服务端才会出示证书（velashell-docs/zh/ssh/spec/03 §5.5）。
    /// </remarks>
    public static IReadOnlyList<string> KnownKeyTypes(IReadOnlyList<KnownHostEntry> entries, string host, int port)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(host);

        string plain = FormatHostPattern(host, port);
        List<string> types = [];
        foreach (KnownHostEntry entry in entries)
        {
            if (entry.IsRevoked || !MatchesHost(entry, host, port, plain))
            {
                continue;
            }

            foreach (string type in entry.IsCertificateAuthority ? CertificateKeyTypes : [entry.KeyType])
            {
                if (!types.Contains(type, StringComparer.Ordinal))
                {
                    types.Add(type);
                }
            }
        }
        return types;
    }

    /// <summary>本库验得了的主机证书类型（blob 里的类型串）。</summary>
    private static readonly string[] CertificateKeyTypes =
    [
        SshAlgorithmNames.SshEd25519CertV01,
        SshAlgorithmNames.EcdsaSha2Nistp256CertV01,
        SshAlgorithmNames.EcdsaSha2Nistp384CertV01,
        SshAlgorithmNames.EcdsaSha2Nistp521CertV01,
        SshAlgorithmNames.SshRsaCertV01,
    ];

    /// <summary>非 22 端口要按 <c>[host]:port</c> 匹配。</summary>
    private static string FormatHostPattern(string host, int port) => port == 22 ? host : $"[{host}]:{port}";

    private static bool MatchesHost(KnownHostEntry entry, string host, int port, string plain)
    {
        if (entry.IsHashed)
        {
            return MatchesHashed(entry.Patterns[0], plain);
        }

        // 取反模式（!pattern）对上了，这一整行就不算这台主机 —— 哪怕别的模式也对上了。
        // 曾经把它当成「这一个模式不匹配」：`*.corp,!untrusted.corp` 那一行照样经 *.corp
        // 把密钥信给了 untrusted.corp，与写配置的人的本意正好相反。
        bool matched = false;
        foreach (string pattern in entry.Patterns)
        {
            bool negated = pattern.StartsWith('!');
            string body = negated ? pattern[1..] : pattern;

            if (MatchesPattern(body, plain) || (port == 22 && MatchesPattern(body, host)))
            {
                if (negated)
                {
                    return false;
                }
                matched = true;
            }
        }

        return matched;
    }

    /// <summary>匹配 <c>|1|salt|hash</c> 形式。</summary>
    /// <remarks>
    /// HMAC-SHA1(key = salt, data = 主机名)。
    /// SHA-1 在这里不是当安全散列用的 —— 它只是 OpenSSH 定下的、
    /// 用来避免明文列出主机名的混淆手段，换算法就与 OpenSSH 的文件不兼容了。
    /// </remarks>
    private static bool MatchesHashed(string pattern, string host)
    {
        string[] parts = pattern.Split('|');
        if (parts.Length != 4 || parts[1] != "1")
        {
            return false;
        }

        try
        {
            byte[] salt = Convert.FromBase64String(parts[2]);
            byte[] expected = Convert.FromBase64String(parts[3]);

#pragma warning disable CA5350 // known_hosts 的散列形式由 OpenSSH 规定就是 HMAC-SHA1，换不得
            byte[] actual = HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes(host));
#pragma warning restore CA5350

            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>匹配主机模式，支持 <c>*</c> 与 <c>?</c> 通配（<c>!</c> 取反由调用方处理）。</summary>
    private static bool MatchesPattern(string pattern, string host)
    {
        if (!pattern.Contains('*', StringComparison.Ordinal)
            && !pattern.Contains('?', StringComparison.Ordinal))
        {
            return string.Equals(pattern, host, StringComparison.OrdinalIgnoreCase);
        }

        return HostPatterns.Matches(pattern, host);
    }

    /// <summary>拼一条可以直接追加进 <c>known_hosts</c> 的行。</summary>
    /// <param name="host">主机。</param>
    /// <param name="port">端口。</param>
    /// <param name="key">公钥。是证书时记下的是<b>证书里那把钥</b>（证书每次重签 blob 都会变）。</param>
    /// <param name="hashHostName">要不要把主机名散列掉（对应 <c>HashKnownHosts yes</c>）。</param>
    public static string FormatEntry(string host, int port, SshPublicKey key, bool hashHostName = false)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(key);

        string name = FormatHostPattern(host, port);

        if (hashHostName)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(20);

#pragma warning disable CA5350 // 同上：格式由 OpenSSH 规定
            byte[] hash = HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes(name));
#pragma warning restore CA5350

            name = string.Create(
                CultureInfo.InvariantCulture,
                $"|1|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(hash)}");
        }

        SshPublicKey recorded = key.PlainKey;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{name} {recorded.KeyType} {Convert.ToBase64String(recorded.Blob.Span)}");
    }

    /// <summary>把一台主机追加进 <c>known_hosts</c>。</summary>
    /// <remarks>
    /// <b>只追加，不改写已有的行。</b>改写意味着要把整个文件读进来再写回去，
    /// 而那会在并发写时丢掉别的进程刚加的记录 —— OpenSSH 自己也是追加。
    /// </remarks>
    public static async ValueTask AppendAsync(
        string host,
        int port,
        SshPublicKey key,
        string? path = null,
        bool hashHostName = false,
        CancellationToken cancellationToken = default)
    {
        string actual = path ?? DefaultPath;
        string? directory = Path.GetDirectoryName(actual);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string line = FormatEntry(host, port, key, hashHostName) + Environment.NewLine;

        // 文件最后一行没有换行（手工编辑过的文件很常见）的话，直接追加会把新记录接在那一行后面，
        // 两条一起坏掉 —— 那台主机从此每次都按「没见过」处理。先补一个换行。
        if (!await EndsWithNewlineAsync(actual, cancellationToken).ConfigureAwait(false))
        {
            line = Environment.NewLine + line;
        }

        await File.AppendAllTextAsync(actual, line, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>文件不存在、为空，或者最后一个字节是 <c>\n</c>。</summary>
    private static async ValueTask<bool> EndsWithNewlineAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return true;
        }

        await using FileStream stream = new(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, useAsync: true);
        if (stream.Length == 0)
        {
            return true;
        }

        stream.Seek(-1, SeekOrigin.End);
        byte[] last = new byte[1];
        return await stream.ReadAsync(last, cancellationToken).ConfigureAwait(false) == 1 && last[0] == (byte)'\n';
    }
}
