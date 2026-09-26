// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4252 §7   publickey 的两段式与签名输入
//   RFC 8332 §3   rsa-sha2-256 / rsa-sha2-512 的选择
//   draft-miller-ssh-agent  加钥报文的私钥格式（WriteAgentPrivateKey）
//   行为规格:     velashell-docs/zh/ssh/spec/04-authentication.md §4;velashell-docs/zh/ssh/design/architecture.md §8 第 5 项;velashell-docs/zh/ssh/spec/07-forwarding.md §7.3

using System.Buffers;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Auth;

/// <summary>在进程内内存里持有私钥的签名器。</summary>
/// <remarks>
/// ⚠️ 私钥就在托管堆上，可能被交换到磁盘、被内存转储带走。
/// 对高价值密钥应当用 <see cref="ISshSigner"/> 的其它实现
/// （ssh-agent / PKCS#11 / HSM），让私钥从不进入本进程。
/// </remarks>
public sealed class InMemorySshSigner : ISshSigner, IDisposable
{
    private readonly Ed25519PrivateKeyParameters? _ed25519;
    private readonly ECDsa? _ecdsa;
    private readonly RSA? _rsa;
    private readonly int _coordinateBytes;
    private bool _disposed;

    private InMemorySshSigner(
        SshPublicKey publicKey,
        IReadOnlyList<string> algorithms,
        Ed25519PrivateKeyParameters? ed25519,
        ECDsa? ecdsa,
        RSA? rsa,
        int coordinateBytes)
    {
        PublicKey = publicKey;
        SignatureAlgorithms = algorithms;
        _ed25519 = ed25519;
        _ecdsa = ecdsa;
        _rsa = rsa;
        _coordinateBytes = coordinateBytes;
    }

    /// <inheritdoc />
    public SshPublicKey PublicKey { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> SignatureAlgorithms { get; }

    /// <inheritdoc />
    public bool IsLocalAndCheap => true;

    /// <summary>用一把 Ed25519 私钥构造。</summary>
    public static InMemorySshSigner FromEd25519(ReadOnlySpan<byte> privateKeySeed)
    {
        if (privateKeySeed.Length != 32)
        {
            throw new ArgumentException("Ed25519 私钥种子必须是 32 字节。", nameof(privateKeySeed));
        }

        Ed25519PrivateKeyParameters key = new(privateKeySeed.ToArray());
        byte[] blob = BuildBlob(w =>
        {
            w.WriteUtf8String(SshAlgorithmNames.SshEd25519);
            w.WriteString(key.GeneratePublicKey().GetEncoded());
        });

        return new InMemorySshSigner(
            SshPublicKey.Decode(blob), [SshAlgorithmNames.SshEd25519], key, null, null, 0);
    }

    /// <summary>生成一把新的 Ed25519 密钥并构造签名器（测试与临时密钥用）。</summary>
    public static InMemorySshSigner GenerateEd25519()
    {
        Ed25519PrivateKeyParameters key = new(new SecureRandom());
        return FromEd25519(key.GetEncoded());
    }

    /// <summary>用一把 RSA 私钥构造。</summary>
    public static InMemorySshSigner FromRsa(RSA rsa)
    {
        ArgumentNullException.ThrowIfNull(rsa);
        RSAParameters p = rsa.ExportParameters(false);

        byte[] blob = BuildBlob(w =>
        {
            // blob 里的类型串**永远是 ssh-rsa**，与签名算法无关（RFC 8332 的不对称）。
            w.WriteUtf8String(SshAlgorithmNames.SshRsa);
            w.WriteMpint(p.Exponent!);
            w.WriteMpint(p.Modulus!);
        });

        // 三个都列出来,按偏好排序。SHA-1 的 ssh-rsa 排在最后,并且
        // **默认会被认证器过滤掉** —— 只有显式打开 AllowSha1RsaSignatures 才会用到它
        // (velashell-docs/zh/ssh/spec/04 §4.4)。在这里就删掉它的话,那个开关就永远没有效果了。
        return new InMemorySshSigner(
            SshPublicKey.Decode(blob),
            [SshAlgorithmNames.RsaSha512, SshAlgorithmNames.RsaSha256, SshAlgorithmNames.SshRsa],
            null, null, rsa, 0);
    }

    /// <summary>用一把 ECDSA 私钥构造。</summary>
    public static InMemorySshSigner FromEcdsa(ECDsa ecdsa)
    {
        ArgumentNullException.ThrowIfNull(ecdsa);
        ECParameters p = ecdsa.ExportParameters(false);

        (string name, string curveName, int coordinate) = ecdsa.KeySize switch
        {
            256 => (SshAlgorithmNames.EcdsaSha2Nistp256, "nistp256", 32),
            384 => (SshAlgorithmNames.EcdsaSha2Nistp384, "nistp384", 48),
            521 => (SshAlgorithmNames.EcdsaSha2Nistp521, "nistp521", 66),
            _ => throw new ArgumentException($"SSH 不支持 {ecdsa.KeySize} 位的 ECDSA 曲线。", nameof(ecdsa)),
        };

        byte[] point = new byte[1 + (coordinate * 2)];
        point[0] = 0x04;
        p.Q.X!.CopyTo(point.AsSpan(1 + coordinate - p.Q.X!.Length));
        p.Q.Y!.CopyTo(point.AsSpan(1 + (coordinate * 2) - p.Q.Y!.Length));

        byte[] blob = BuildBlob(w =>
        {
            w.WriteUtf8String(name);
            w.WriteUtf8String(curveName);
            w.WriteString(point);
        });

        return new InMemorySshSigner(SshPublicKey.Decode(blob), [name], null, ecdsa, null, coordinate);
    }

    /// <inheritdoc />
    public ValueTask<byte[]> SignAsync(
        ReadOnlyMemory<byte> data, string algorithm, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!SignatureAlgorithms.Contains(algorithm, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"这把 {PublicKey.KeyType} 密钥不支持签名算法 {algorithm}。", nameof(algorithm));
        }

        byte[] signature = algorithm switch
        {
            SshAlgorithmNames.SshEd25519 => SignEd25519(data.Span),
            SshAlgorithmNames.EcdsaSha2Nistp256 => SignEcdsa(data.Span, HashAlgorithmName.SHA256, algorithm),
            SshAlgorithmNames.EcdsaSha2Nistp384 => SignEcdsa(data.Span, HashAlgorithmName.SHA384, algorithm),
            SshAlgorithmNames.EcdsaSha2Nistp521 => SignEcdsa(data.Span, HashAlgorithmName.SHA512, algorithm),
            SshAlgorithmNames.RsaSha512 => SignRsa(data.Span, HashAlgorithmName.SHA512, algorithm),
            SshAlgorithmNames.RsaSha256 => SignRsa(data.Span, HashAlgorithmName.SHA256, algorithm),
            // SHA-1。只有使用者显式打开 SshAuthenticator.AllowSha1RsaSignatures 时
            // 才会走到这里 —— 为的是还能连上那些停在 OpenSSH 7.x 的老机器。
#pragma warning disable CA5350 // ssh-rsa 的签名摘要由 RFC 4253 规定就是 SHA-1,换不得
            SshAlgorithmNames.SshRsa => SignRsa(data.Span, HashAlgorithmName.SHA1, algorithm),
#pragma warning restore CA5350
            _ => throw new ArgumentException($"尚未实现的签名算法：{algorithm}", nameof(algorithm)),
        };

        return ValueTask.FromResult(signature);
    }

    /// <summary>
    /// 按 agent 加钥报文的格式写出「密钥类型 + 私钥内容」（velashell-docs/zh/ssh/spec/07 §7.3）。
    /// </summary>
    /// <remarks>
    /// 写进 <paramref name="output"/> 的是明文私钥 —— 用完由调用方清零。
    /// </remarks>
    internal void WriteAgentPrivateKey(IBufferWriter<byte> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SshDataWriter writer = new(output);

        if (_ed25519 is not null)
        {
            byte[] seed = _ed25519.GetEncoded();
            byte[] publicKey = _ed25519.GeneratePublicKey().GetEncoded();
            byte[] secret = new byte[seed.Length + publicKey.Length];
            try
            {
                // 种子在前、公钥在后，共 64 字节。
                seed.CopyTo(secret, 0);
                publicKey.CopyTo(secret, seed.Length);

                writer.WriteUtf8String(SshAlgorithmNames.SshEd25519);
                writer.WriteString(publicKey);
                writer.WriteString(secret);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(seed);
                CryptographicOperations.ZeroMemory(secret);
            }
            return;
        }

        if (_rsa is not null)
        {
            RSAParameters p = _rsa.ExportParameters(true);
            try
            {
                // ⚠️ n 在前、e 在后 —— 与公钥 blob 的顺序相反。
                writer.WriteUtf8String(SshAlgorithmNames.SshRsa);
                writer.WriteMpint(p.Modulus!);
                writer.WriteMpint(p.Exponent!);
                writer.WriteMpint(p.D!);
                writer.WriteMpint(p.InverseQ!);   // q⁻¹ mod p
                writer.WriteMpint(p.P!);
                writer.WriteMpint(p.Q!);
            }
            finally
            {
                ClearRsa(p);
            }
            return;
        }

        ECParameters e = _ecdsa!.ExportParameters(true);
        try
        {
            // 类型串、曲线名、公钥点三样与公钥 blob 完全相同，直接照搬。
            SshDataReader blob = new(new ReadOnlySequence<byte>(PublicKey.Blob));
            string keyType = blob.ReadUtf8String(64);
            string curveName = blob.ReadUtf8String(64);
            byte[] point = blob.ReadStringAsArray(1 + (_coordinateBytes * 2));

            writer.WriteUtf8String(keyType);
            writer.WriteUtf8String(curveName);
            writer.WriteString(point);
            writer.WriteMpint(e.D!);
        }
        finally
        {
            if (e.D is not null)
            {
                CryptographicOperations.ZeroMemory(e.D);
            }
        }
    }

    private static void ClearRsa(RSAParameters p)
    {
        foreach (byte[]? secret in new[] { p.D, p.P, p.Q, p.DP, p.DQ, p.InverseQ })
        {
            if (secret is not null)
            {
                CryptographicOperations.ZeroMemory(secret);
            }
        }
    }

    private byte[] SignEd25519(ReadOnlySpan<byte> data)
    {
        Ed25519Signer signer = new();
        signer.Init(forSigning: true, _ed25519!);
        signer.BlockUpdate(data);
        byte[] raw = signer.GenerateSignature();

        return BuildBlob(w =>
        {
            w.WriteUtf8String(SshAlgorithmNames.SshEd25519);
            w.WriteString(raw);
        });
    }

    private byte[] SignEcdsa(ReadOnlySpan<byte> data, HashAlgorithmName hash, string algorithm)
    {
        byte[] ieee = _ecdsa!.SignData(data, hash);   // r ‖ s，各 _coordinateBytes 字节

        // SSH 的 ECDSA 签名是**双层嵌套**：外层 string 装「mpint r ‖ mpint s」
        // （velashell-docs/zh/ssh/spec/03 §5.2）。直接用 IEEE P1363 的 r‖s 或 DER 都是错的。
        byte[] inner = BuildBlob(w =>
        {
            w.WriteMpint(ieee.AsSpan(0, _coordinateBytes));
            w.WriteMpint(ieee.AsSpan(_coordinateBytes, _coordinateBytes));
        });

        return BuildBlob(w =>
        {
            w.WriteUtf8String(algorithm);
            w.WriteString(inner);
        });
    }

    private byte[] SignRsa(ReadOnlySpan<byte> data, HashAlgorithmName hash, string algorithm)
    {
        // PKCS#1 v1.5，不是 PSS（RFC 8332 §3）。
        byte[] raw = _rsa!.SignData(data, hash, RSASignaturePadding.Pkcs1);

        return BuildBlob(w =>
        {
            // 签名 blob 里是**签名算法**名，不是密钥类型名。
            w.WriteUtf8String(algorithm);
            w.WriteString(raw);
        });
    }

    private static byte[] BuildBlob(Action<SshDataWriterBox> write)
    {
        ArrayBufferWriter<byte> buffer = new();
        write(new SshDataWriterBox(buffer));
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>把 ref struct 的写入器包一层，好在 lambda 里用。</summary>
    private sealed class SshDataWriterBox(ArrayBufferWriter<byte> output)
    {
        public void WriteUtf8String(string value)
        {
            SshDataWriter w = new(output);
            w.WriteUtf8String(value);
        }

        public void WriteString(ReadOnlySpan<byte> value)
        {
            SshDataWriter w = new(output);
            w.WriteString(value);
        }

        public void WriteMpint(ReadOnlySpan<byte> magnitude)
        {
            SshDataWriter w = new(output);
            w.WriteMpint(magnitude);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _ecdsa?.Dispose();
        _rsa?.Dispose();
    }
}
