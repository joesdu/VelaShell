// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH PROTOCOL.certkeys —— *-cert-v01@openssh.com 的字段表、
//                                证书类型(1 用户 / 2 主机)、有效期与扩展
//   RFC 4251 §5               —— string / uint64 / name-list 的 wire 表示
//   行为规格: velashell-docs/zh/ssh/design/architecture.md §8 第 5 项

using System.Buffers;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Keys;

/// <summary>证书是发给用户的还是发给主机的。</summary>
public enum SshCertificateType
{
    /// <summary>用户证书:拿它登录服务器。</summary>
    User = 1,

    /// <summary>主机证书:服务器拿它向客户端证明身份。</summary>
    Host = 2,
}

/// <summary>证书读不出来。</summary>
public sealed class SshCertificateException : SshException
{
    /// <summary>创建一个证书解析异常。</summary>
    public SshCertificateException(string message, Exception? innerException = null)
        : base(SshFailureReason.Unsupported, SshPhase.Authenticating, message, innerException)
    {
    }
}

/// <summary>
/// 一张 OpenSSH 证书(<c>*-cert-v01@openssh.com</c>)。
/// </summary>
/// <remarks>
/// <para>
/// 证书把「谁签发的」与「这把钥能干什么」绑在一起:一张由 CA 签过名的证书,
/// 让服务端不必逐个记住用户的公钥 —— <c>TrustedUserCAKeys</c> 里放一把 CA 公钥即可。
/// </para>
/// <para>
/// <b>本库只解析与出示证书,不验证 CA 签名。</b>验证是**服务端**的事:
/// 客户端验了也不改变任何结果 —— 服务端照样要自己验一遍,
/// 而客户端这边根本没有「哪些 CA 可信」这份名单。
/// 我们把 <see cref="SignatureKey" /> 与 <see cref="ValidBefore" /> 这些事实交出去,
/// 让使用者能在界面上显示、能在证书过期时给出一句人话。
/// </para>
/// </remarks>
public sealed class OpenSshCertificate
{
    /// <summary>证书 blob 的解析上限。</summary>
    private const int MaxBlobBytes = 256 * 1024;

    private const int MaxFieldBytes = 32 * 1024;

    private readonly byte[] _blob;

    private OpenSshCertificate(
        string algorithm, byte[] blob, SshPublicKey key, ulong serial, SshCertificateType certificateType,
        string keyId, IReadOnlyList<string> validPrincipals, ulong validAfter, ulong validBefore,
        IReadOnlyList<string> criticalOptions, IReadOnlyList<string> extensions, SshPublicKey? signatureKey)
    {
        Algorithm = algorithm;
        _blob = blob;
        Key = key;
        Serial = serial;
        CertificateType = certificateType;
        KeyId = keyId;
        ValidPrincipals = validPrincipals;
        ValidAfter = validAfter;
        ValidBefore = validBefore;
        CriticalOptions = criticalOptions;
        Extensions = extensions;
        SignatureKey = signatureKey;
    }

    /// <summary>证书的算法名,例如 <c>ssh-ed25519-cert-v01@openssh.com</c>。</summary>
    public string Algorithm { get; }

    /// <summary>证书 blob 的原始字节 —— 认证时**原样**出示的就是它。</summary>
    public ReadOnlyMemory<byte> Blob => _blob;

    /// <summary>证书里那把普通公钥(被签发的那一把)。</summary>
    public SshPublicKey Key { get; }

    /// <summary>序列号,由 CA 指定;没指定时是 0。</summary>
    public ulong Serial { get; }

    /// <summary>用户证书还是主机证书。</summary>
    public SshCertificateType CertificateType { get; }

    /// <summary>CA 写的标识串(<c>ssh-keygen -I</c>),通常是人名或主机名,出现在服务端日志里。</summary>
    public string KeyId { get; }

    /// <summary>
    /// 这张证书能用于哪些主体(用户证书是登录名,主机证书是主机名)。
    /// <b>空表示「对所有主体有效」</b> —— 这是规范定的,不是解析失败。
    /// </summary>
    public IReadOnlyList<string> ValidPrincipals { get; }

    /// <summary>生效时间(Unix 秒)。0 表示不限。</summary>
    public ulong ValidAfter { get; }

    /// <summary>失效时间(Unix 秒)。<c>ulong.MaxValue</c> 表示不限。</summary>
    public ulong ValidBefore { get; }

    /// <summary>
    /// 关键选项的名字(如 <c>force-command</c>、<c>source-address</c>)。
    /// <b>服务端不认识其中任何一个就必须拒绝这张证书</b> —— 这是「关键」二字的含义。
    /// </summary>
    public IReadOnlyList<string> CriticalOptions { get; }

    /// <summary>扩展的名字(如 <c>permit-pty</c>、<c>permit-agent-forwarding</c>)。不认识的会被忽略。</summary>
    public IReadOnlyList<string> Extensions { get; }

    /// <summary>签发它的 CA 公钥;解析不出来时为 <see langword="null" />。</summary>
    public SshPublicKey? SignatureKey { get; }

    /// <summary>生效时间,已换算成本地可读的时刻;不限时为 <see langword="null" />。</summary>
    public DateTimeOffset? ValidAfterTime =>
        ValidAfter is 0 ? null : DateTimeOffset.FromUnixTimeSeconds((long)ValidAfter);

    /// <summary>失效时间,已换算成本地可读的时刻;不限时为 <see langword="null" />。</summary>
    public DateTimeOffset? ValidBeforeTime =>
        ValidBefore >= long.MaxValue ? null : DateTimeOffset.FromUnixTimeSeconds((long)ValidBefore);

    /// <summary>
    /// 在给定时刻是不是还在有效期内。
    /// </summary>
    /// <remarks>
    /// 这里**只看有效期**,不代表这张证书可用 —— CA 是否可信、主体对不对、
    /// 关键选项服务端认不认,都是服务端的判断。它的用途是在连接失败时
    /// 能说一句「你的证书 3 天前就过期了」,而不是让用户对着 <c>Permission denied</c> 猜。
    /// </remarks>
    /// <param name="moment">要判断的时刻。</param>
    public bool IsTimeValid(DateTimeOffset moment)
    {
        ulong seconds = (ulong)Math.Max(0, moment.ToUnixTimeSeconds());
        return seconds >= ValidAfter && seconds < ValidBefore;
    }

    /// <summary>
    /// 从证书文件读一张证书(<c>ssh-keygen -s</c> 产出的 <c>*-cert.pub</c>)。
    /// </summary>
    /// <param name="path">文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async ValueTask<OpenSshCertificate> LoadAsync(
        string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new SshCertificateException($"读不了证书文件 {path}:{ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new SshCertificateException($"没有权限读证书文件 {path}。", ex);
        }

        return ParseText(text, path);
    }

    /// <summary>
    /// 解析一行证书文本:<c>&lt;算法&gt; &lt;base64&gt; [注释]</c>。
    /// </summary>
    /// <param name="text">证书文本(允许有多行,取第一行非空的)。</param>
    /// <param name="origin">出错消息里用来标明来源。</param>
    public static OpenSshCertificate ParseText(string text, string? origin = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        string where = origin is null ? "" : $"({origin})";

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                throw new SshCertificateException(
                    $"证书文本的格式不对{where}:应当是「算法 base64 [注释]」。");
            }

            byte[] blob;
            try
            {
                blob = Convert.FromBase64String(parts[1]);
            }
            catch (FormatException ex)
            {
                throw new SshCertificateException($"证书的 base64 解不开{where}。", ex);
            }

            OpenSshCertificate certificate = Parse(blob);
            if (!string.Equals(certificate.Algorithm, parts[0], StringComparison.Ordinal))
            {
                // 行首写的算法与 blob 里的类型串不符 —— 文件被手工拼接过的典型症状。
                throw new SshCertificateException(
                    $"证书文件里标的算法是 {parts[0]},而 blob 里是 {certificate.Algorithm}{where}。");
            }
            return certificate;
        }

        throw new SshCertificateException($"证书文件是空的{where}。");
    }

    /// <summary>解析一个证书 blob。</summary>
    /// <param name="blob">证书 blob。</param>
    /// <exception cref="SshCertificateException">格式非法或类型不支持。</exception>
    public static OpenSshCertificate Parse(ReadOnlyMemory<byte> blob)
    {
        if (blob.Length is 0 or > MaxBlobBytes)
        {
            throw new SshCertificateException($"证书 blob 长度非法:{blob.Length} 字节。");
        }

        byte[] copy = blob.ToArray();
        SshDataReader reader = new(new ReadOnlySequence<byte>(copy));

        try
        {
            string algorithm = reader.ReadUtf8String(MaxFieldBytes, strict: true);
            if (!algorithm.EndsWith(SshAlgorithmNames.CertificateSuffix, StringComparison.Ordinal))
            {
                throw new SshCertificateException(
                    $"{algorithm} 不是证书类型 —— 证书的类型串以 {SshAlgorithmNames.CertificateSuffix} 结尾。");
            }

            string plainType = algorithm[..^SshAlgorithmNames.CertificateSuffix.Length];

            _ = reader.ReadString(MaxFieldBytes);                    // nonce:防碰撞用,解析时不关心
            SshPublicKey key = ReadEmbeddedKey(ref reader, plainType);

            ulong serial = reader.ReadUInt64();
            uint type = reader.ReadUInt32();
            if (type is not (1 or 2))
            {
                throw new SshCertificateException($"证书类型 {type} 不合法(只有 1=用户、2=主机)。");
            }

            string keyId = reader.ReadUtf8String(MaxFieldBytes);
            IReadOnlyList<string> principals = ReadStringSequence(ref reader);
            ulong validAfter = reader.ReadUInt64();
            ulong validBefore = reader.ReadUInt64();
            IReadOnlyList<string> criticalOptions = ReadNamedPairs(ref reader);
            IReadOnlyList<string> extensions = ReadNamedPairs(ref reader);
            _ = reader.ReadString(MaxFieldBytes);                    // reserved:规范要求为空,不做他用

            SshPublicKey? signatureKey = TryReadKey(ref reader);
            _ = reader.ReadString(MaxFieldBytes);                    // CA 的签名 —— 由服务端验,见类型说明

            return new OpenSshCertificate(
                algorithm, copy, key, serial, (SshCertificateType)type, keyId, principals,
                validAfter, validBefore, criticalOptions, extensions, signatureKey);
        }
        catch (SshWireFormatException ex)
        {
            throw new SshCertificateException("证书 blob 的格式非法。", ex);
        }
    }

    /// <summary>
    /// 把证书里的密钥字段重新拼成一个**普通公钥 blob**,再交给
    /// <see cref="SshPublicKey.Parse" /> 去解。
    /// </summary>
    /// <remarks>
    /// 重拼而不是另写一套解析:证书里的密钥字段与普通公钥 blob 里的**完全一样**,
    /// 差的只是前面那个类型串。另写一套等于把同一份格式理解维护两遍,
    /// 而两份迟早会分岔。
    /// </remarks>
    private static SshPublicKey ReadEmbeddedKey(scoped ref SshDataReader reader, string plainType)
    {
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteUtf8String(plainType);

        switch (plainType)
        {
            case SshAlgorithmNames.SshEd25519:
                writer.WriteString(reader.ReadString(MaxFieldBytes).ToArray());
                break;

            case SshAlgorithmNames.SshRsa:
                // 顺序是 e 在前、n 在后 —— 与普通 ssh-rsa blob 一致。
                writer.WriteMpint(reader.ReadMpint(MaxFieldBytes).ToArray());
                writer.WriteMpint(reader.ReadMpint(MaxFieldBytes).ToArray());
                break;

            case SshAlgorithmNames.EcdsaSha2Nistp256:
            case SshAlgorithmNames.EcdsaSha2Nistp384:
            case SshAlgorithmNames.EcdsaSha2Nistp521:
                writer.WriteString(reader.ReadString(MaxFieldBytes).ToArray());   // 曲线名
                writer.WriteString(reader.ReadString(MaxFieldBytes).ToArray());   // 公开点
                break;

            default:
                throw new SshCertificateException($"不支持的证书密钥类型:{plainType}。");
        }

        try
        {
            return SshPublicKey.Parse(buffer.WrittenMemory);
        }
        catch (SshPublicKeyException ex)
        {
            throw new SshCertificateException($"证书里的 {plainType} 公钥不合法。", ex);
        }
    }

    /// <summary>CA 公钥解不出来不该让整张证书读不了 —— 它只用于显示。</summary>
    private static SshPublicKey? TryReadKey(scoped ref SshDataReader reader)
    {
        byte[] raw = reader.ReadStringAsArray(MaxFieldBytes);
        try
        {
            return raw.Length == 0 ? null : SshPublicKey.Parse(raw);
        }
        catch (SshPublicKeyException)
        {
            // CA 用的可能是本库还不认识的类型(比如它自己也是一张证书)。
            return null;
        }
    }

    /// <summary>读一个「里面装着若干 string」的 string。</summary>
    private static List<string> ReadStringSequence(scoped ref SshDataReader reader)
    {
        byte[] section = reader.ReadStringAsArray(MaxFieldBytes);
        if (section.Length == 0)
        {
            return [];
        }

        List<string> values = [];
        SshDataReader inner = new(new ReadOnlySequence<byte>(section));
        while (!inner.IsEmpty)
        {
            values.Add(inner.ReadUtf8String(MaxFieldBytes));
        }
        return values;
    }

    /// <summary>读一段「name / data 成对」的区域,只取名字。</summary>
    /// <remarks>
    /// 只取名字是有意的:<c>force-command</c> 的数据是要在服务端执行的命令,
    /// <c>source-address</c> 的数据是网段 —— 客户端拿它们做不了任何判断,
    /// 而把它们摆进公开面只会让人误以为可以据此决定要不要连。
    /// 名字则足以在界面上说清「这张证书带了哪些限制」。
    /// </remarks>
    private static List<string> ReadNamedPairs(scoped ref SshDataReader reader)
    {
        byte[] section = reader.ReadStringAsArray(MaxFieldBytes);
        if (section.Length == 0)
        {
            return [];
        }

        List<string> names = [];
        SshDataReader inner = new(new ReadOnlySequence<byte>(section));
        while (!inner.IsEmpty)
        {
            names.Add(inner.ReadUtf8String(MaxFieldBytes));
            _ = inner.ReadString(MaxFieldBytes);          // 对应的数据
        }
        return names;
    }
}
