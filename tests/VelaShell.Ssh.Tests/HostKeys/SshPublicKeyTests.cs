// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §5（主机密钥与签名）
//
// 这一套**自己造密钥、自己签名**，再用被测代码去验 —— 验签这件事只有端到端跑过才算数。
// 尤其 ECDSA 的「双层 string 嵌套」与 RSA 的「类型名 ≠ 签名算法名」两个坑，
// 光看代码是看不出对错的。

using System.Buffers;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Tests.HostKeys;

[TestClass]
[TestCategory("HostKeys")]
public sealed class SshPublicKeyTests
{
    private static byte[] Blob(Action<ArrayBufferWriter<byte>> build)
    {
        ArrayBufferWriter<byte> w = new();
        build(w);
        return w.WrittenSpan.ToArray();
    }

    private static void WriteString(ArrayBufferWriter<byte> w, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, (uint)value.Length);
        w.Write(length);
        w.Write(value);
    }

    private static void WriteString(ArrayBufferWriter<byte> w, string value) =>
        WriteString(w, System.Text.Encoding.ASCII.GetBytes(value));

    private static void WriteMpint(ArrayBufferWriter<byte> w, ReadOnlySpan<byte> magnitude)
    {
        int start = 0;
        while (start < magnitude.Length && magnitude[start] == 0)
        {
            start++;
        }
        ReadOnlySpan<byte> t = magnitude[start..];
        if (t.IsEmpty)
        {
            WriteString(w, ReadOnlySpan<byte>.Empty);
            return;
        }
        if ((t[0] & 0x80) != 0)
        {
            byte[] padded = new byte[t.Length + 1];
            t.CopyTo(padded.AsSpan(1));
            WriteString(w, padded);
        }
        else
        {
            WriteString(w, t);
        }
    }

    // ------------------------------------------------------------ Ed25519

    private static (SshPublicKey Key, Ed25519PrivateKeyParameters Private) CreateEd25519()
    {
        Ed25519PrivateKeyParameters priv = new(new SecureRandom());
        byte[] pub = priv.GeneratePublicKey().GetEncoded();

        byte[] blob = Blob(w =>
        {
            WriteString(w, SshAlgorithmNames.SshEd25519);
            WriteString(w, pub);
        });

        return (SshPublicKey.Parse(blob), priv);
    }

    [TestMethod]
    public void Ed25519公钥能解析并验签()
    {
        (SshPublicKey key, Ed25519PrivateKeyParameters priv) = CreateEd25519();

        Assert.AreEqual(SshAlgorithmNames.SshEd25519, key.KeyType);
        Assert.AreEqual(256, key.KeyBits);

        byte[] data = RandomNumberGenerator.GetBytes(100);
        Ed25519Signer signer = new();
        signer.Init(forSigning: true, priv);
        signer.BlockUpdate(data);
        byte[] signature = signer.GenerateSignature();

        byte[] signatureBlob = Blob(w =>
        {
            WriteString(w, SshAlgorithmNames.SshEd25519);
            WriteString(w, signature);
        });

        Assert.IsTrue(key.VerifySignature(signatureBlob, data, SshAlgorithmNames.SshEd25519));
    }

    [TestMethod]
    public void Ed25519改一个比特就验不过()
    {
        (SshPublicKey key, Ed25519PrivateKeyParameters priv) = CreateEd25519();
        byte[] data = RandomNumberGenerator.GetBytes(100);

        Ed25519Signer signer = new();
        signer.Init(true, priv);
        signer.BlockUpdate(data);
        byte[] signature = signer.GenerateSignature();
        byte[] signatureBlob = Blob(w =>
        {
            WriteString(w, SshAlgorithmNames.SshEd25519);
            WriteString(w, signature);
        });

        data[50] ^= 0x01;
        Assert.IsFalse(key.VerifySignature(signatureBlob, data, SshAlgorithmNames.SshEd25519));
    }

    // ------------------------------------------------------------ ECDSA

    [TestMethod]
    public void Ecdsa三条曲线都能解析并验签()
    {
        (string Name, string Curve, ECCurve Ec, int Coord)[] cases =
        [
            (SshAlgorithmNames.EcdsaSha2Nistp256, "nistp256", ECCurve.NamedCurves.nistP256, 32),
            (SshAlgorithmNames.EcdsaSha2Nistp384, "nistp384", ECCurve.NamedCurves.nistP384, 48),
            (SshAlgorithmNames.EcdsaSha2Nistp521, "nistp521", ECCurve.NamedCurves.nistP521, 66),
        ];

        foreach ((string name, string curveName, ECCurve curve, int coord) in cases)
        {
            using var ecdsa = ECDsa.Create(curve);
            ECParameters p = ecdsa.ExportParameters(false);

            byte[] point = new byte[1 + (coord * 2)];
            point[0] = 0x04;
            p.Q.X!.CopyTo(point.AsSpan(1 + coord - p.Q.X!.Length));
            p.Q.Y!.CopyTo(point.AsSpan(1 + (coord * 2) - p.Q.Y!.Length));

            byte[] blob = Blob(w =>
            {
                WriteString(w, name);
                WriteString(w, curveName);
                WriteString(w, point);
            });

            var key = SshPublicKey.Parse(blob);
            Assert.AreEqual(name, key.KeyType, name);

            byte[] data = RandomNumberGenerator.GetBytes(100);
            HashAlgorithmName hash = coord switch
            {
                32 => HashAlgorithmName.SHA256,
                48 => HashAlgorithmName.SHA384,
                _ => HashAlgorithmName.SHA512,
            };
            byte[] ieee = ecdsa.SignData(data, hash);   // r ‖ s，各 coord 字节

            // ⚠️ SSH 的 ECDSA 签名是**双层嵌套**：外层 string 的内容是「mpint r ‖ mpint s」。
            //    直接把 IEEE P1363 的 r‖s 当成签名 blob 是错的。
            byte[] inner = Blob(w =>
            {
                WriteMpint(w, ieee.AsSpan(0, coord));
                WriteMpint(w, ieee.AsSpan(coord, coord));
            });
            byte[] signatureBlob = Blob(w =>
            {
                WriteString(w, name);
                WriteString(w, inner);
            });

            Assert.IsTrue(key.VerifySignature(signatureBlob, data, name), $"{name}：验签应当通过");
        }
    }

    [TestMethod]
    public void Ecdsa的曲线名不符会被拒绝()
    {
        // blob 里重复了一次曲线名。不一致说明它被拼错或被改过。
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECParameters p = ecdsa.ExportParameters(false);
        byte[] point = new byte[65];
        point[0] = 0x04;
        p.Q.X!.CopyTo(point.AsSpan(33 - p.Q.X!.Length));
        p.Q.Y!.CopyTo(point.AsSpan(65 - p.Q.Y!.Length));

        byte[] blob = Blob(w =>
        {
            WriteString(w, SshAlgorithmNames.EcdsaSha2Nistp256);
            WriteString(w, "nistp384");   // 与类型名不符
            WriteString(w, point);
        });

        Assert.ThrowsExactly<SshPublicKeyException>(() => SshPublicKey.Parse(blob));
    }

    // ------------------------------------------------------------ RSA

    [TestMethod]
    public void Rsa公钥能解析并按三种签名算法验签()
    {
        using var rsa = RSA.Create(2048);
        RSAParameters p = rsa.ExportParameters(false);

        // ⚠️ RSA 的 blob 里类型串**永远是 ssh-rsa**，即使签名算法是 rsa-sha2-512。
        byte[] blob = Blob(w =>
        {
            WriteString(w, SshAlgorithmNames.SshRsa);
            WriteMpint(w, p.Exponent!);
            WriteMpint(w, p.Modulus!);
        });

        var key = SshPublicKey.Parse(blob);
        Assert.AreEqual(SshAlgorithmNames.SshRsa, key.KeyType, "密钥类型名恒为 ssh-rsa");
        Assert.AreEqual(2048, key.KeyBits);

        // 一把密钥能用三种签名算法 —— 这是 RFC 8332 引入的那处不对称。
        Assert.IsTrue(key.SupportsSignatureAlgorithm(SshAlgorithmNames.SshRsa));
        Assert.IsTrue(key.SupportsSignatureAlgorithm(SshAlgorithmNames.RsaSha256));
        Assert.IsTrue(key.SupportsSignatureAlgorithm(SshAlgorithmNames.RsaSha512));
        Assert.IsFalse(key.SupportsSignatureAlgorithm(SshAlgorithmNames.SshEd25519));

        byte[] data = RandomNumberGenerator.GetBytes(100);

        foreach ((string algorithm, HashAlgorithmName hash) in new[]
                 {
                     (SshAlgorithmNames.RsaSha512, HashAlgorithmName.SHA512),
                     (SshAlgorithmNames.RsaSha256, HashAlgorithmName.SHA256),
                 })
        {
            // PKCS#1 v1.5，不是 PSS。
            byte[] signature = rsa.SignData(data, hash, RSASignaturePadding.Pkcs1);
            byte[] signatureBlob = Blob(w =>
            {
                WriteString(w, algorithm);
                WriteString(w, signature);
            });

            Assert.IsTrue(key.VerifySignature(signatureBlob, data, algorithm), algorithm);
        }
    }

    [TestMethod]
    public void 签名算法名与协商结果不符时拒绝()
    {
        // 放过它 = 允许对端把 rsa-sha2-512 降级成 ssh-rsa（SHA-1）。
        using var rsa = RSA.Create(2048);
        RSAParameters p = rsa.ExportParameters(false);
        byte[] blob = Blob(w =>
        {
            WriteString(w, SshAlgorithmNames.SshRsa);
            WriteMpint(w, p.Exponent!);
            WriteMpint(w, p.Modulus!);
        });
        var key = SshPublicKey.Parse(blob);

        byte[] data = RandomNumberGenerator.GetBytes(50);
        byte[] signature = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        // 签名 blob 里写 rsa-sha2-256，但协商出的是 rsa-sha2-512 —— 必须拒。
        byte[] signatureBlob = Blob(w =>
        {
            WriteString(w, SshAlgorithmNames.RsaSha256);
            WriteString(w, signature);
        });

        Assert.IsFalse(key.VerifySignature(signatureBlob, data, SshAlgorithmNames.RsaSha512));
    }

    // ------------------------------------------------------------ 指纹

    [TestMethod]
    public void SHA256指纹是OpenSSH格式()
    {
        (SshPublicKey key, _) = CreateEd25519();
        string fingerprint = key.Sha256Fingerprint;

        Assert.StartsWith("SHA256:", fingerprint);
        Assert.DoesNotContain("=", fingerprint,
            "OpenSSH 显示的指纹不带 base64 填充 —— 带上用户就没法与 ssh-keygen -lf 的输出对照");
        // SHA-256 是 32 字节 → base64 无填充是 43 个字符
        Assert.AreEqual("SHA256:".Length + 43, fingerprint.Length);
    }

    [TestMethod]
    public void 相同的密钥指纹相同不同的密钥指纹不同()
    {
        (SshPublicKey a, _) = CreateEd25519();
        (SshPublicKey b, _) = CreateEd25519();

        Assert.AreEqual(a.Sha256Fingerprint, SshPublicKey.Parse(a.Blob).Sha256Fingerprint);
        Assert.AreNotEqual(a.Sha256Fingerprint, b.Sha256Fingerprint);
    }

    // ------------------------------------------------------------ 非法输入

    [TestMethod]
    public void 拒绝空blob与超长blob()
    {
        Assert.ThrowsExactly<SshPublicKeyException>(() => SshPublicKey.Parse(ReadOnlyMemory<byte>.Empty));
        Assert.ThrowsExactly<SshPublicKeyException>(() => SshPublicKey.Parse(new byte[70000]));
    }

    [TestMethod]
    public void 拒绝未知的密钥类型()
    {
        byte[] blob = Blob(w =>
        {
            WriteString(w, "ssh-dss");    // 我们刻意不实现它：1024 位定长，已不可接受
            WriteString(w, new byte[32]);
        });
        Assert.ThrowsExactly<SshPublicKeyException>(() => SshPublicKey.Parse(blob));
    }

    [TestMethod]
    public void 拒绝长度不对的Ed25519公钥()
    {
        byte[] blob = Blob(w =>
        {
            WriteString(w, SshAlgorithmNames.SshEd25519);
            WriteString(w, new byte[31]);
        });
        Assert.ThrowsExactly<SshPublicKeyException>(() => SshPublicKey.Parse(blob));
    }

    [TestMethod]
    public void 拒绝blob末尾有多余字节()
    {
        // 多出来的字节意味着我们对这个 blob 的理解有误。沉默忽略会让真错误跑得更远。
        (SshPublicKey key, _) = CreateEd25519();
        byte[] extended = [.. key.Blob.ToArray(), 0xFF];
        Assert.ThrowsExactly<SshPublicKeyException>(() => SshPublicKey.Parse(extended));
    }

    [TestMethod]
    public void 畸形签名blob只返回false不抛异常()
    {
        // 验签失败与「签名格式非法」对调用方是同一件事：这个签名不作数。
        // 抛异常会让调用点多一条与安全判定无关的分支。
        (SshPublicKey key, _) = CreateEd25519();
        byte[] data = RandomNumberGenerator.GetBytes(10);

        Assert.IsFalse(key.VerifySignature([], data, SshAlgorithmNames.SshEd25519));
        Assert.IsFalse(key.VerifySignature([0, 0, 0, 99], data, SshAlgorithmNames.SshEd25519));
        Assert.IsFalse(key.VerifySignature(new byte[64], data, SshAlgorithmNames.SshEd25519));
    }
}
