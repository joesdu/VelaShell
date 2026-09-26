// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 测试用的服务端主机密钥：生成密钥对、产出 SSH 格式的公钥 blob、对交换哈希签名。
//
// ⚠️ **只为测试存在。** 它没有任何私钥保护措施，密钥就在托管内存里裸着。

using System.Buffers;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Tests.TestKit;

/// <summary>测试服务端用的主机密钥。</summary>
internal abstract class TestHostKey : IDisposable
{
    /// <summary>密钥类型名（blob 里那个）。</summary>
    public abstract string KeyType { get; }

    /// <summary>这把密钥能用的签名算法名，按偏好排序。</summary>
    public abstract IReadOnlyList<string> SignatureAlgorithms { get; }

    /// <summary>SSH 格式的公钥 blob。</summary>
    public abstract byte[] PublicKeyBlob { get; }

    /// <summary>用指定算法对数据签名，产出 SSH 格式的签名 blob。</summary>
    public abstract byte[] Sign(ReadOnlySpan<byte> data, string algorithm);

    /// <inheritdoc />
    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    /// <summary>按算法名造一把主机密钥。</summary>
    public static TestHostKey Create(string keyTypeOrAlgorithm) => keyTypeOrAlgorithm switch
    {
        SshAlgorithmNames.SshEd25519 => new Ed25519HostKey(),
        SshAlgorithmNames.EcdsaSha2Nistp256 => new EcdsaHostKey(SshAlgorithmNames.EcdsaSha2Nistp256, ECCurve.NamedCurves.nistP256, 32, HashAlgorithmName.SHA256),
        SshAlgorithmNames.EcdsaSha2Nistp384 => new EcdsaHostKey(SshAlgorithmNames.EcdsaSha2Nistp384, ECCurve.NamedCurves.nistP384, 48, HashAlgorithmName.SHA384),
        SshAlgorithmNames.EcdsaSha2Nistp521 => new EcdsaHostKey(SshAlgorithmNames.EcdsaSha2Nistp521, ECCurve.NamedCurves.nistP521, 66, HashAlgorithmName.SHA512),
        SshAlgorithmNames.SshRsa or SshAlgorithmNames.RsaSha256 or SshAlgorithmNames.RsaSha512 => new RsaHostKey(),
        _ => throw new ArgumentException($"测试桩不支持的主机密钥类型：{keyTypeOrAlgorithm}", nameof(keyTypeOrAlgorithm)),
    };

    /// <summary>拿库里的签名器当主机密钥，出示给定的 <c>K_S</c>（比如一张 ssh-keygen 签的主机证书）。</summary>
    /// <param name="signer">持有私钥的签名器。</param>
    /// <param name="presentedBlob">出示的 <c>K_S</c>。出示证书时签名仍由证书里那把钥来做。</param>
    /// <param name="algorithms">宣告的主机密钥算法名。</param>
    public static TestHostKey FromSigner(
        VelaShell.Ssh.Auth.ISshSigner signer, byte[] presentedBlob, IReadOnlyList<string> algorithms) =>
        new SignerHostKey(signer, presentedBlob, algorithms);

    private sealed class SignerHostKey(
        VelaShell.Ssh.Auth.ISshSigner signer, byte[] blob, IReadOnlyList<string> algorithms) : TestHostKey
    {
        public override string KeyType => signer.PublicKey.KeyType;

        public override IReadOnlyList<string> SignatureAlgorithms => algorithms;

        public override byte[] PublicKeyBlob => blob;

        // 证书的签名 blob 里写的是普通算法名（OpenSSH PROTOCOL.certkeys）。
        public override byte[] Sign(ReadOnlySpan<byte> data, string algorithm) =>
            signer.SignAsync(data.ToArray(), VelaShell.Ssh.HostKeys.SshPublicKey.StripCertificateSuffix(algorithm))
                .AsTask().GetAwaiter().GetResult();
    }

    private protected static byte[] Build(Action<SshDataWriterHelper> write)
    {
        ArrayBufferWriter<byte> buffer = new();
        write(new SshDataWriterHelper(buffer));
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>测试里手工拼 SSH wire 格式的小工具。</summary>
    private protected readonly struct SshDataWriterHelper(ArrayBufferWriter<byte> output)
    {
        public void String(ReadOnlySpan<byte> value)
        {
            Span<byte> length = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, (uint)value.Length);
            output.Write(length);
            output.Write(value);
        }

        public void String(string value) => String(System.Text.Encoding.ASCII.GetBytes(value));

        public void Mpint(ReadOnlySpan<byte> magnitude)
        {
            int start = 0;
            while (start < magnitude.Length && magnitude[start] == 0)
            {
                start++;
            }
            ReadOnlySpan<byte> t = magnitude[start..];
            if (t.IsEmpty)
            {
                String(ReadOnlySpan<byte>.Empty);
                return;
            }
            if ((t[0] & 0x80) != 0)
            {
                byte[] padded = new byte[t.Length + 1];
                t.CopyTo(padded.AsSpan(1));
                String(padded);
            }
            else
            {
                String(t);
            }
        }
    }

    // ------------------------------------------------------------ Ed25519

    private sealed class Ed25519HostKey : TestHostKey
    {
        private readonly Ed25519PrivateKeyParameters _private = new(new SecureRandom());

        public override string KeyType => SshAlgorithmNames.SshEd25519;

        public override IReadOnlyList<string> SignatureAlgorithms => [SshAlgorithmNames.SshEd25519];

        public override byte[] PublicKeyBlob => Build(w =>
        {
            w.String(SshAlgorithmNames.SshEd25519);
            w.String(_private.GeneratePublicKey().GetEncoded());
        });

        public override byte[] Sign(ReadOnlySpan<byte> data, string algorithm)
        {
            Ed25519Signer signer = new();
            signer.Init(forSigning: true, _private);
            signer.BlockUpdate(data);
            byte[] signature = signer.GenerateSignature();

            return Build(w =>
            {
                w.String(SshAlgorithmNames.SshEd25519);
                w.String(signature);
            });
        }
    }

    // ------------------------------------------------------------ ECDSA

    private sealed class EcdsaHostKey(string name, ECCurve curve, int coordinate, HashAlgorithmName hash) : TestHostKey
    {
        private readonly ECDsa _ecdsa = ECDsa.Create(curve);

        public override string KeyType => name;

        public override IReadOnlyList<string> SignatureAlgorithms => [name];

        public override byte[] PublicKeyBlob
        {
            get
            {
                ECParameters p = _ecdsa.ExportParameters(false);
                byte[] point = new byte[1 + (coordinate * 2)];
                point[0] = 0x04;
                p.Q.X!.CopyTo(point.AsSpan(1 + coordinate - p.Q.X!.Length));
                p.Q.Y!.CopyTo(point.AsSpan(1 + (coordinate * 2) - p.Q.Y!.Length));

                return Build(w =>
                {
                    w.String(name);
                    w.String(name["ecdsa-sha2-".Length..]);   // 曲线名在 blob 里重复一次
                    w.String(point);
                });
            }
        }

        public override byte[] Sign(ReadOnlySpan<byte> data, string algorithm)
        {
            byte[] ieee = _ecdsa.SignData(data, hash);   // r ‖ s，各 coordinate 字节

            // SSH 的 ECDSA 签名是**双层嵌套**：外层 string 装「mpint r ‖ mpint s」。
            byte[] inner = Build(w =>
            {
                w.Mpint(ieee.AsSpan(0, coordinate));
                w.Mpint(ieee.AsSpan(coordinate, coordinate));
            });

            return Build(w =>
            {
                w.String(name);
                w.String(inner);
            });
        }

        public override void Dispose()
        {
            _ecdsa.Dispose();
            base.Dispose();
        }
    }

    // ------------------------------------------------------------ RSA

    private sealed class RsaHostKey : TestHostKey
    {
        private readonly RSA _rsa = RSA.Create(2048);

        public override string KeyType => SshAlgorithmNames.SshRsa;

        // 一把 RSA 密钥能用三种签名算法 —— RFC 8332 的那处不对称。
        public override IReadOnlyList<string> SignatureAlgorithms =>
            [SshAlgorithmNames.RsaSha512, SshAlgorithmNames.RsaSha256, SshAlgorithmNames.SshRsa];

        public override byte[] PublicKeyBlob
        {
            get
            {
                RSAParameters p = _rsa.ExportParameters(false);
                return Build(w =>
                {
                    // blob 里的类型串**永远是 ssh-rsa**，与签名算法无关。
                    w.String(SshAlgorithmNames.SshRsa);
                    w.Mpint(p.Exponent!);
                    w.Mpint(p.Modulus!);
                });
            }
        }

        public override byte[] Sign(ReadOnlySpan<byte> data, string algorithm)
        {
            HashAlgorithmName hash = algorithm switch
            {
                SshAlgorithmNames.RsaSha512 => HashAlgorithmName.SHA512,
                SshAlgorithmNames.RsaSha256 => HashAlgorithmName.SHA256,
                _ => HashAlgorithmName.SHA1,
            };

            byte[] signature = _rsa.SignData(data, hash, RSASignaturePadding.Pkcs1);
            return Build(w =>
            {
                w.String(algorithm);   // 签名 blob 里是**签名算法**名
                w.String(signature);
            });
        }

        public override void Dispose()
        {
            _rsa.Dispose();
            base.Dispose();
        }
    }
}
