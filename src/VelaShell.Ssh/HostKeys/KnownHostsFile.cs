// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH sshd(8) 的 AUTHORIZED_KEYS / SSH_KNOWN_HOSTS 章节
//   行为规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §主机密钥策略

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace VelaShell.Ssh.HostKeys;

/// <summary><c>known_hosts</c> 里的一条。</summary>
/// <param name="Patterns">主机模式（逗号分隔的原文已经拆开）。</param>
/// <param name="IsHashed">主机名是不是 <c>|1|salt|hash</c> 形式。</param>
/// <param name="Marker"><c>@cert-authority</c> 或 <c>@revoked</c>；没有则为空。</param>
/// <param name="KeyType">密钥类型。</param>
/// <param name="KeyBlob">公钥 blob。</param>
/// <param name="LineNumber">在文件里的行号（1 起）。</param>
public sealed record KnownHostEntry(
    IReadOnlyList<string> Patterns,
    bool IsHashed,
    string Marker,
    string KeyType,
    byte[] KeyBlob,
    int LineNumber)
{
    /// <summary>这一条是不是「此密钥已吊销」。</summary>
    public bool IsRevoked => Marker == "@revoked";

    /// <summary>这一条是不是证书颁发者。</summary>
    public bool IsCertificateAuthority => Marker == "@cert-authority";
}

/// <summary>查 <c>known_hosts</c> 的结果。</summary>
public enum KnownHostStatus
{
    /// <summary>这台主机 + 这把密钥都对得上。</summary>
    Known,

    /// <summary>没见过这台主机。</summary>
    Unknown,

    /// <summary>
    /// 见过这台主机，但<b>密钥变了</b>。
    /// </summary>
    /// <remarks>
    /// 这是最要紧的一种：它可能是中间人，也可能只是服务器重装了。
    /// <b>两者在协议层无法区分</b>，所以库不替使用者决定 —— 如实报出来。
    /// </remarks>
    Changed,

    /// <summary>这把密钥被 <c>@revoked</c> 标记过。</summary>
    Revoked,
}

/// <summary>查 <c>known_hosts</c> 的结果详情。</summary>
/// <param name="Status">结论。</param>
/// <param name="MatchedEntry">对上的那一条（<see cref="KnownHostStatus.Unknown"/> 时为空）。</param>
/// <param name="ConflictingEntries">
/// 主机对上但密钥不对的那些条目 —— <b>报「密钥变了」时要把行号指给用户</b>，
/// 否则他不知道该去删哪一行。
/// </param>
public readonly record struct KnownHostLookup(
    KnownHostStatus Status,
    KnownHostEntry? MatchedEntry,
    IReadOnlyList<KnownHostEntry> ConflictingEntries);

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

    /// <summary>查一台主机的一把密钥。</summary>
    /// <param name="entries">已解析的条目。</param>
    /// <param name="host">主机名或地址。</param>
    /// <param name="port">端口。<b>非 22 时要按 <c>[host]:port</c> 匹配。</b></param>
    /// <param name="key">服务端出示的公钥。</param>
    public static KnownHostLookup Lookup(
        IReadOnlyList<KnownHostEntry> entries, string host, int port, SshPublicKey key)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(key);

        string plain = port == 22 ? host : $"[{host}]:{port}";

        List<KnownHostEntry> conflicts = [];
        KnownHostEntry? trusted = null;

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

            bool sameKey = entry.KeyBlob.AsSpan().SequenceEqual(key.Blob.Span);

            if (entry.IsRevoked)
            {
                if (sameKey)
                {
                    // 吊销赢，立刻返回 —— 后面再有什么都不重要了。
                    return new KnownHostLookup(KnownHostStatus.Revoked, entry, []);
                }
                continue;
            }

            if (entry.IsCertificateAuthority)
            {
                // 证书主机密钥是另一套机制（还没实现）。这里不把它当成普通密钥来比，
                // 否则会把「我们不认识证书」误报成「密钥变了」。
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
            if (entry.KeyType == key.KeyType)
            {
                conflicts.Add(entry);
            }
        }

        if (trusted is not null)
        {
            return new KnownHostLookup(KnownHostStatus.Known, trusted, []);
        }

        return conflicts.Count > 0
            ? new KnownHostLookup(KnownHostStatus.Changed, null, conflicts)
            : new KnownHostLookup(KnownHostStatus.Unknown, null, []);
    }

    private static bool MatchesHost(KnownHostEntry entry, string host, int port, string plain)
    {
        if (entry.IsHashed)
        {
            return MatchesHashed(entry.Patterns[0], plain);
        }

        foreach (string pattern in entry.Patterns)
        {
            if (MatchesPattern(pattern, plain) || (port == 22 && MatchesPattern(pattern, host)))
            {
                return true;
            }
        }

        return false;
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

    /// <summary>匹配主机模式，支持 <c>*</c> 与 <c>?</c> 通配以及 <c>!</c> 取反。</summary>
    private static bool MatchesPattern(string pattern, string host)
    {
        if (pattern.StartsWith('!'))
        {
            // 取反模式：对上了反而是「明确不匹配」。
            return false;
        }

        if (!pattern.Contains('*', StringComparison.Ordinal)
            && !pattern.Contains('?', StringComparison.Ordinal))
        {
            return string.Equals(pattern, host, StringComparison.OrdinalIgnoreCase);
        }

        return WildcardMatch(pattern, host);
    }

    private static bool WildcardMatch(string pattern, string text)
    {
        int p = 0;
        int t = 0;
        int starPattern = -1;
        int starText = 0;

        while (t < text.Length)
        {
            if (p < pattern.Length
                && (pattern[p] == '?'
                    || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(text[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starPattern = p++;
                starText = t;
            }
            else if (starPattern >= 0)
            {
                p = starPattern + 1;
                t = ++starText;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    /// <summary>拼一条可以直接追加进 <c>known_hosts</c> 的行。</summary>
    /// <param name="host">主机。</param>
    /// <param name="port">端口。</param>
    /// <param name="key">公钥。</param>
    /// <param name="hashHostName">要不要把主机名散列掉（对应 <c>HashKnownHosts yes</c>）。</param>
    public static string FormatEntry(string host, int port, SshPublicKey key, bool hashHostName = false)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(key);

        string name = port == 22 ? host : $"[{host}]:{port}";

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

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{name} {key.KeyType} {Convert.ToBase64String(key.Blob.Span)}");
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
        await File.AppendAllTextAsync(actual, line, cancellationToken).ConfigureAwait(false);
    }
}
