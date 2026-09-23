// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §6.6  ssh-rsa 的公钥与签名 blob 格式
//   RFC 5656 §3.1  ecdsa-sha2-* 的公钥格式(含曲线名)与签名格式(两个 mpint 嵌在一个 string 里)
//   RFC 8332 §3    rsa-sha2-256 / rsa-sha2-512 —— 签名算法名与**密钥类型名**不是一回事
//   RFC 8709 §4    ssh-ed25519
//   OpenSSH PROTOCOL.certkeys  *-cert-v01@openssh.com
//   行为规格:      velashell-docs/zh/ssh/spec/03-key-exchange.md §5

using System.Buffers;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.HostKeys;

/// <summary>一把 SSH 公钥（主机密钥或用户公钥）。</summary>
/// <remarks>
/// <para>
/// ⚠️ <b>「密钥类型」与「签名算法」不是一回事</b>（RFC 8332）：
/// 一把 RSA 密钥的 blob 里类型串<b>永远</b>是 <c>ssh-rsa</c>，
/// 而签名算法可以是 <c>ssh-rsa</c>（SHA-1）、<c>rsa-sha2-256</c> 或 <c>rsa-sha2-512</c>。
/// 拿协商出的算法名去比对 blob 里的类型串会失败 —— 这是 RFC 8332 引入的一处不对称，
/// 也是 RSA 互操作最常见的一个坑（velashell-docs/zh/ssh/spec/03 §5.1）。
/// </para>
/// </remarks>
public sealed class SshPublicKey
{
    /// <summary>公钥 blob 的解析上限。一把公钥不该有这么大。</summary>
    private const int MaxBlobBytes = 64 * 1024;

    private const int MaxFieldBytes = 8 * 1024;

    private readonly byte[] _blob;
    private readonly RSA? _rsa;
    private readonly ECDsa? _ecdsa;
    private readonly byte[]? _ed25519;

    private SshPublicKey(string keyType, byte[] blob, RSA? rsa, ECDsa? ecdsa, byte[]? ed25519, int keyBits)
        : this(keyType, keyType, blob, rsa, ecdsa, ed25519, keyBits)
    {
    }

    private SshPublicKey(
        string keyType, string plainKeyType, byte[] blob,
        RSA? rsa, ECDsa? ecdsa, byte[]? ed25519, int keyBits)
    {
        KeyType = keyType;
        PlainKeyType = plainKeyType;
        _blob = blob;
        _rsa = rsa;
        _ecdsa = ecdsa;
        _ed25519 = ed25519;
        KeyBits = keyBits;
    }

    /// <summary>
    /// 把一把普通公钥「换上证书的身份」：<see cref="KeyType"/> 与 <see cref="Blob"/> 变成证书的，
    /// 而验签仍然由原来那把钥来做。
    /// </summary>
    /// <remarks>
    /// 证书与普通公钥在密码学上是<b>同一把钥</b>，区别只在「出示什么」：
    /// 认证请求里出示的是整张证书，签名却仍是那把普通钥签的，
    /// 签名 blob 里写的也仍是普通算法名（<c>ssh-ed25519</c> 而不是
    /// <c>ssh-ed25519-cert-v01@openssh.com</c>）。这层不对称就落在这个类型里，
    /// 不扩散到认证器 —— 认证器只管「拿 Signer.PublicKey.Blob 去出示」。
    /// </remarks>
    internal static SshPublicKey ForCertificate(SshPublicKey key, string algorithm, byte[] certificateBlob) =>
        new(algorithm, key.PlainKeyType, certificateBlob, key._rsa, key._ecdsa, key._ed25519, key.KeyBits);

    /// <summary>
    /// 去掉证书身份之后的密钥类型名 —— 证书用它来选签名算法与验签，
    /// 普通公钥则与 <see cref="KeyType"/> 相同。
    /// </summary>
    public string PlainKeyType { get; }

    /// <summary>这是不是一张证书（<c>*-cert-v01@openssh.com</c>）。</summary>
    public bool IsCertificate => !string.Equals(KeyType, PlainKeyType, StringComparison.Ordinal);

    /// <summary>密钥类型名（<c>ssh-ed25519</c> / <c>ecdsa-sha2-nistp256</c> / <c>ssh-rsa</c>）。</summary>
    public string KeyType { get; }

    /// <summary>密钥强度（位）。RSA 是模数位数，EC 是曲线位数，Ed25519 恒为 256。</summary>
    public int KeyBits { get; }

    /// <summary>公钥 blob 的原始字节。</summary>
    public ReadOnlyMemory<byte> Blob => _blob;

    /// <summary>
    /// OpenSSH 风格的 SHA-256 指纹：<c>SHA256:</c> 前缀 + base64（<b>去掉末尾的 <c>=</c> 填充</b>）。
    /// </summary>
    /// <remarks>
    /// 去填充不是风格问题 —— OpenSSH 就是这么显示的，带上 <c>=</c> 会让用户没法把
    /// 我们的指纹与 <c>ssh-keygen -lf</c> 的输出对照，而**对照**恰恰是指纹唯一的用途。
    /// </remarks>
    public string Sha256Fingerprint => "SHA256:" + Convert.ToBase64String(SHA256.HashData(_blob)).TrimEnd('=');

    /// <summary>
    /// OpenSSH 风格的 MD5 指纹：<c>MD5:</c> 前缀 + 冒号分隔的十六进制。
    /// </summary>
    /// <remarks>
    /// MD5 早已不安全，这里**只用于显示**，而且只因为老文档与老设备的面板上还在用它。
    /// 任何信任判定都必须走 <see cref="Sha256Fingerprint"/>。
    /// </remarks>
    public string Md5Fingerprint
    {
        get
        {
            // CA5351「损坏的加密算法 MD5」—— **这里不是在用它做安全判定。**
            // 它只产出一个给人眼看的字符串，因为老设备的 Web 面板与老文档里
            // 记的还是 MD5 指纹，用户需要能把两边对上。
            // 任何信任判定都走 Sha256Fingerprint —— 那是代码里唯一被比较的那个。
#pragma warning disable CA5351 // Do Not Use Broken Cryptographic Algorithms
            byte[] digest = MD5.HashData(_blob);
#pragma warning restore CA5351
            return "MD5:" + Convert.ToHexString(digest).ToLowerInvariant()
                .Chunk(2).Select(static c => new string(c)).Aggregate(static (a, b) => a + ":" + b);
        }
    }

    /// <summary>解析一个公钥 blob。</summary>
    /// <exception cref="SshPublicKeyException">格式非法或类型不支持。</exception>
    public static SshPublicKey Parse(ReadOnlyMemory<byte> blob)
    {
        if (blob.Length is 0 or > MaxBlobBytes)
        {
            throw new SshPublicKeyException($"公钥 blob 长度非法：{blob.Length} 字节。");
        }

        byte[] copy = blob.ToArray();
        SshDataReader reader = new(new ReadOnlySequence<byte>(copy));

        string keyType;
        try
        {
            keyType = reader.ReadUtf8String(MaxFieldBytes, strict: true);
        }
        catch (SshWireFormatException ex)
        {
            throw new SshPublicKeyException("公钥 blob 的类型串非法。", ex);
        }

        try
        {
            return keyType switch
            {
                SshAlgorithmNames.SshEd25519 => ParseEd25519(ref reader, keyType, copy),
                SshAlgorithmNames.EcdsaSha2Nistp256 => ParseEcdsa(ref reader, keyType, copy, ECCurve.NamedCurves.nistP256, "nistp256", 256),
                SshAlgorithmNames.EcdsaSha2Nistp384 => ParseEcdsa(ref reader, keyType, copy, ECCurve.NamedCurves.nistP384, "nistp384", 384),
                SshAlgorithmNames.EcdsaSha2Nistp521 => ParseEcdsa(ref reader, keyType, copy, ECCurve.NamedCurves.nistP521, "nistp521", 521),
                SshAlgorithmNames.SshRsa => ParseRsa(ref reader, keyType, copy),
                _ => throw new SshPublicKeyException($"不支持的公钥类型：{keyType}。"),
            };
        }
        catch (SshWireFormatException ex)
        {
            throw new SshPublicKeyException($"公钥 blob（{keyType}）格式非法。", ex);
        }
    }

    /// <summary>
    /// 验证一个 SSH 签名 blob。
    /// </summary>
    /// <param name="signatureBlob">签名 blob（<c>string 算法名 ‖ string 签名</c>）。</param>
    /// <param name="signedData">被签名的数据。</param>
    /// <param name="expectedAlgorithm">
    /// 协商出的签名算法名。签名 blob 里的算法名**必须**与它一致 ——
    /// 不比对就等于让对端自选签名算法，那是一条降级攻击路径。
    /// </param>
    /// <returns>验证是否通过。</returns>
    public bool VerifySignature(
        ReadOnlySpan<byte> signatureBlob, ReadOnlySpan<byte> signedData, string expectedAlgorithm)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(signatureBlob.ToArray()));

        string algorithm;
        ReadOnlySequence<byte> signature;
        try
        {
            algorithm = reader.ReadUtf8String(MaxFieldBytes, strict: true);
            signature = reader.ReadString(MaxFieldBytes);
        }
        catch (SshWireFormatException)
        {
            return false;
        }

        // 证书出示的是 *-cert-v01@openssh.com，而签名 blob 里写的是**普通**算法名。
        // 比对前先把后缀去掉，否则证书的签名永远验不过（OpenSSH PROTOCOL.certkeys）。
        string expectedPlain = StripCertificateSuffix(expectedAlgorithm);

        if (!string.Equals(algorithm, expectedPlain, StringComparison.Ordinal))
        {
            // 签名算法名与协商结果不符。放过它 = 允许对端把 rsa-sha2-512 降级成 ssh-rsa。
            return false;
        }

        byte[] signatureBytes = signature.ToArray();

        return algorithm switch
        {
            SshAlgorithmNames.SshEd25519 => VerifyEd25519(signatureBytes, signedData),
            SshAlgorithmNames.EcdsaSha2Nistp256 => VerifyEcdsa(signatureBytes, signedData, HashAlgorithmName.SHA256, 32),
            SshAlgorithmNames.EcdsaSha2Nistp384 => VerifyEcdsa(signatureBytes, signedData, HashAlgorithmName.SHA384, 48),
            SshAlgorithmNames.EcdsaSha2Nistp521 => VerifyEcdsa(signatureBytes, signedData, HashAlgorithmName.SHA512, 66),
            SshAlgorithmNames.RsaSha512 => VerifyRsa(signatureBytes, signedData, HashAlgorithmName.SHA512),
            SshAlgorithmNames.RsaSha256 => VerifyRsa(signatureBytes, signedData, HashAlgorithmName.SHA256),
            SshAlgorithmNames.SshRsa => VerifyRsaSha1(signatureBytes, signedData),
            _ => false,
        };
    }

    /// <summary>
    /// 该签名算法名是否能用这把密钥来验。
    /// </summary>
    /// <remarks>
    /// RSA 的三个签名算法名都对应同一个密钥类型 <c>ssh-rsa</c>（见类型说明）。
    /// </remarks>
    public bool SupportsSignatureAlgorithm(string algorithm) => PlainKeyType switch
    {
        SshAlgorithmNames.SshRsa => StripCertificateSuffix(algorithm) is SshAlgorithmNames.SshRsa
            or SshAlgorithmNames.RsaSha256 or SshAlgorithmNames.RsaSha512,
        _ => string.Equals(StripCertificateSuffix(algorithm), PlainKeyType, StringComparison.Ordinal),
    };

    /// <summary>去掉证书算法名的 <c>-cert-v01@openssh.com</c> 后缀；不是证书名就原样返回。</summary>
    internal static string StripCertificateSuffix(string algorithm) =>
        algorithm.EndsWith(SshAlgorithmNames.CertificateSuffix, StringComparison.Ordinal)
            ? algorithm[..^SshAlgorithmNames.CertificateSuffix.Length]
            : algorithm;

    /// <summary>这把密钥能用的签名算法名，按偏好排序。</summary>
    /// <remarks>
    /// RSA 排三个：SHA-512、SHA-256，最后才是 SHA-1 的 <c>ssh-rsa</c>。
    /// 最后那个默认会被认证器过滤掉（见 <c>SshAuthenticator.AllowSha1RsaSignatures</c>）。
    /// </remarks>
    public IReadOnlyList<string> SignatureAlgorithms
    {
        get
        {
            string[] plain = PlainKeyType switch
            {
                SshAlgorithmNames.SshRsa =>
                    [SshAlgorithmNames.RsaSha512, SshAlgorithmNames.RsaSha256, SshAlgorithmNames.SshRsa],
                _ => [PlainKeyType],
            };

            // 证书出示时用的是带后缀的名字 —— 它才是认证请求里那个「公钥算法名」字段。
            return IsCertificate
                ? [.. plain.Select(static a => a + SshAlgorithmNames.CertificateSuffix)]
                : plain;
        }
    }

    // ------------------------------------------------------------------ 解析

    private static SshPublicKey ParseEd25519(ref SshDataReader reader, string keyType, byte[] blob)
    {
        byte[] key = reader.ReadStringAsArray(MaxFieldBytes);
        if (key.Length != 32)
        {
            throw new SshPublicKeyException($"ssh-ed25519 公钥必须是 32 字节，收到 {key.Length} 字节。");
        }
        reader.ExpectEnd("ssh-ed25519 公钥");
        return new SshPublicKey(keyType, blob, null, null, key, 256);
    }

    private static SshPublicKey ParseEcdsa(
        ref SshDataReader reader, string keyType, byte[] blob, ECCurve curve, string expectedCurveName, int bits)
    {
        // ECDSA 的 blob 里**重复**了一次曲线名。它必须与类型名蕴含的曲线一致 ——
        // 不一致说明 blob 被拼错或被改过。
        string curveName = reader.ReadUtf8String(MaxFieldBytes, strict: true);
        if (!string.Equals(curveName, expectedCurveName, StringComparison.Ordinal))
        {
            throw new SshPublicKeyException(
                $"{keyType} 的曲线名不符：blob 里是 {curveName}，应为 {expectedCurveName}。");
        }

        byte[] point = reader.ReadStringAsArray(MaxFieldBytes);
        reader.ExpectEnd($"{keyType} 公钥");

        int coordinate = (bits + 7) / 8;
        if (point.Length != 1 + (coordinate * 2) || point[0] != 0x04)
        {
            throw new SshPublicKeyException(
                $"{keyType} 的公钥点必须是 {1 + (coordinate * 2)} 字节的未压缩点。");
        }

        try
        {
            var ecdsa = ECDsa.Create(new ECParameters
            {
                Curve = curve,
                Q = new ECPoint
                {
                    X = point[1..(1 + coordinate)],
                    Y = point[(1 + coordinate)..],
                },
            });
            return new SshPublicKey(keyType, blob, null, ecdsa, null, bits);
        }
        catch (Exception ex) when (ex is not SshPublicKeyException)
        {
            // 各平台抛的类型不同（OpenSSL vs CNG），含义都是「这个点用不了」。
            throw new SshPublicKeyException($"{keyType} 的公钥点不在曲线上。", ex);
        }
    }

    private static SshPublicKey ParseRsa(ref SshDataReader reader, string keyType, byte[] blob)
    {
        byte[] exponent = reader.ReadMpint(MaxFieldBytes).ToArray();
        byte[] modulus = reader.ReadMpint(MaxFieldBytes).ToArray();
        reader.ExpectEnd("ssh-rsa 公钥");

        if (modulus.Length == 0 || exponent.Length == 0)
        {
            throw new SshPublicKeyException("ssh-rsa 公钥的模数或指数为空。");
        }

        try
        {
            var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters { Modulus = modulus, Exponent = exponent });
            return new SshPublicKey(keyType, blob, rsa, null, null, modulus.Length * 8);
        }
        catch (Exception ex) when (ex is not SshPublicKeyException)
        {
            throw new SshPublicKeyException("ssh-rsa 公钥参数非法。", ex);
        }
    }

    // ------------------------------------------------------------------ 验签

    private bool VerifyEd25519(byte[] signature, ReadOnlySpan<byte> data)
    {
        if (_ed25519 is null || signature.Length != 64)
        {
            return false;
        }

        try
        {
            Ed25519Signer signer = new();
            signer.Init(forSigning: false, new Ed25519PublicKeyParameters(_ed25519));
            signer.BlockUpdate(data);
            return signer.VerifySignature(signature);
        }
        catch (Exception)
        {
            // 验签失败与「签名格式非法」对调用方是同一件事：这个签名不作数。
            return false;
        }
    }

    private bool VerifyEcdsa(byte[] signature, ReadOnlySpan<byte> data, HashAlgorithmName hash, int coordinate)
    {
        if (_ecdsa is null)
        {
            return false;
        }

        // ⚠️ ECDSA 签名是**双层嵌套**：外层 string 的内容是「mpint r ‖ mpint s」的拼接。
        //    直接把 r、s 拼成定长字节是错的；DER 编码也是错的。
        //    这是 SSH 里 ECDSA 最经典的一个坑（velashell-docs/zh/ssh/spec/03 §5.2）。
        byte[] r, s;
        try
        {
            SshDataReader inner = new(new ReadOnlySequence<byte>(signature));
            r = inner.ReadMpint(MaxFieldBytes).ToArray();
            s = inner.ReadMpint(MaxFieldBytes).ToArray();
            inner.ExpectEnd("ECDSA 签名");
        }
        catch (SshWireFormatException)
        {
            return false;
        }

        // BCL 的 VerifyHash 要的是定长、右对齐的 r ‖ s。
        byte[] ieee = new byte[coordinate * 2];
        if (!TryCopyRightAligned(r, ieee.AsSpan(0, coordinate))
            || !TryCopyRightAligned(s, ieee.AsSpan(coordinate, coordinate)))
        {
            return false;
        }

        try
        {
            byte[] digest = HashData(hash, data);
            return _ecdsa.VerifyHash(digest, ieee);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool VerifyRsa(byte[] signature, ReadOnlySpan<byte> data, HashAlgorithmName hash)
    {
        if (_rsa is null)
        {
            return false;
        }

        try
        {
            // PKCS#1 v1.5，**不是 PSS**（RFC 8332 §3）。
            return _rsa.VerifyData(data, signature, hash, RSASignaturePadding.Pkcs1);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool VerifyRsaSha1(byte[] signature, ReadOnlySpan<byte> data)
    {
        if (_rsa is null)
        {
            return false;
        }

        try
        {
            // CA5350「弱加密算法」—— 这是 ssh-rsa 的定义（RFC 4253 §6.6），
            // 而 ssh-rsa 只在使用者显式放开老算法时才会被协商出来。
#pragma warning disable CA5350 // Do Not Use Weak Cryptographic Algorithms
            return _rsa.VerifyData(data, signature, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
#pragma warning restore CA5350
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static byte[] HashData(HashAlgorithmName algorithm, ReadOnlySpan<byte> data)
    {
        if (algorithm == HashAlgorithmName.SHA256)
        {
            return SHA256.HashData(data);
        }
        if (algorithm == HashAlgorithmName.SHA384)
        {
            return SHA384.HashData(data);
        }
        return SHA512.HashData(data);
    }

    private static bool TryCopyRightAligned(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.Length > destination.Length)
        {
            return false;
        }
        destination.Clear();
        source.CopyTo(destination[^source.Length..]);
        return true;
    }
}

/// <summary>公钥解析失败。</summary>
public sealed class SshPublicKeyException : Exception
{
    /// <summary>用给定消息创建异常。</summary>
    public SshPublicKeyException(string message) : base(message)
    {
    }

    /// <summary>用给定消息与内部异常创建异常。</summary>
    public SshPublicKeyException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
