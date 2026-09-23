// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §3（七种 KEX）、§4（交换哈希）、§7（密钥派生）
//
// 这一套**自己扮演服务端**：用 BC/BCL 直接做对侧的运算，验证两边算出同一个共享密钥。
// 只测「客户端不抛异常」是没有意义的 —— 共享密钥算错了照样不抛，
// 只会在后面的签名验证里表现为一句看不出原因的失败。

using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Kems;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Pqc.Crypto.NtruPrime;
using Org.BouncyCastle.Security;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Crypto.Kex;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Tests.Crypto;

[TestClass]
[TestCategory("Crypto")]
public sealed class KeyExchangeTests
{
    // ------------------------------------------------------- 椭圆曲线 / 有限域

    [TestMethod]
    public void Curve25519两端算出同一个共享密钥()
    {
        using Curve25519KeyExchange client = new();
        byte[] clientPublic = client.CreateClientPublicValue();
        Assert.HasCount(32, clientPublic);

        // 服务端那一侧
        X25519PrivateKeyParameters serverPrivate = new(new SecureRandom());
        byte[] serverPublic = serverPrivate.GeneratePublicKey().GetEncoded();

        byte[] serverSecret = new byte[32];
        X25519Agreement agreement = new();
        agreement.Init(serverPrivate);
        agreement.CalculateAgreement(new X25519PublicKeyParameters(clientPublic), serverSecret, 0);

        byte[] clientSecret = client.ComputeSharedSecret(serverPublic);
        Assert.AreSequenceEqual(serverSecret, clientSecret);
    }

    [TestMethod]
    public void Curve25519拒绝全零结果()
    {
        // RFC 7748 §6.1 的 contributory behaviour：低阶点会让共享密钥与我方私钥无关，
        // 对端可以单方面决定它。X25519 的低阶点之一是全零公钥。
        using Curve25519KeyExchange client = new();
        _ = client.CreateClientPublicValue();

        Assert.ThrowsExactly<SshKeyExchangeException>(
            () => client.ComputeSharedSecret(new byte[32]));
    }

    [TestMethod]
    public void Curve25519拒绝长度不对的公钥()
    {
        using Curve25519KeyExchange client = new();
        _ = client.CreateClientPublicValue();

        Assert.ThrowsExactly<SshKeyExchangeException>(() => client.ComputeSharedSecret(new byte[31]));
        Assert.ThrowsExactly<SshKeyExchangeException>(() => client.ComputeSharedSecret(new byte[33]));
    }

    [TestMethod]
    public void Ecdh三条曲线都能算出同一个共享密钥()
    {
        (string Name, ECCurve Curve, int Coord)[] cases =
        [
            (SshAlgorithmNames.EcdhSha2Nistp256, ECCurve.NamedCurves.nistP256, 32),
            (SshAlgorithmNames.EcdhSha2Nistp384, ECCurve.NamedCurves.nistP384, 48),
            // 521 位 → 66 字节。按 64/65 写死的实现在这条上崩，而它最少被测到。
            (SshAlgorithmNames.EcdhSha2Nistp521, ECCurve.NamedCurves.nistP521, 66),
        ];

        foreach ((string name, ECCurve curve, int coord) in cases)
        {
            using EcdhKeyExchange client = new(name);
            byte[] clientPublic = client.CreateClientPublicValue();

            Assert.HasCount(1 + (coord * 2), clientPublic, $"{name}：未压缩点长度");
            Assert.AreEqual(0x04, clientPublic[0], $"{name}：必须是未压缩点编码");

            using ECDiffieHellman server = ECDiffieHellman.Create(curve);
            ECParameters serverParams = server.ExportParameters(false);

            byte[] serverPublic = new byte[1 + (coord * 2)];
            serverPublic[0] = 0x04;
            CopyRightAligned(serverParams.Q.X!, serverPublic.AsSpan(1, coord));
            CopyRightAligned(serverParams.Q.Y!, serverPublic.AsSpan(1 + coord, coord));

            using ECDiffieHellman clientPeer = ECDiffieHellman.Create(new ECParameters
            {
                Curve = curve,
                Q = new ECPoint
                {
                    X = clientPublic[1..(1 + coord)],
                    Y = clientPublic[(1 + coord)..],
                },
            });

            byte[] serverSecret = server.DeriveRawSecretAgreement(clientPeer.PublicKey);
            byte[] clientSecret = client.ComputeSharedSecret(serverPublic);

            Assert.AreSequenceEqual(serverSecret, clientSecret, $"{name}：共享密钥不一致");
        }
    }

    [TestMethod]
    public void Ecdh拒绝不在曲线上的点()
    {
        // 不校验就等于接受任意点，那是一条可以泄漏私钥的路（无效曲线攻击）。
        using EcdhKeyExchange client = new(SshAlgorithmNames.EcdhSha2Nistp256);
        _ = client.CreateClientPublicValue();

        byte[] bogus = new byte[65];
        bogus[0] = 0x04;
        bogus[1] = 0x01;   // 几乎肯定不在曲线上

        Assert.ThrowsExactly<SshKeyExchangeException>(() => client.ComputeSharedSecret(bogus));
    }

    [TestMethod]
    public void Ecdh拒绝压缩点编码()
    {
        using EcdhKeyExchange client = new(SshAlgorithmNames.EcdhSha2Nistp256);
        _ = client.CreateClientPublicValue();

        byte[] compressed = new byte[65];
        compressed[0] = 0x02;   // 压缩点标记
        Assert.ThrowsExactly<SshKeyExchangeException>(() => client.ComputeSharedSecret(compressed));
    }

    [TestMethod]
    public void 有限域DH两端算出同一个共享密钥()
    {
        foreach (string name in new[]
                 {
                     SshAlgorithmNames.DiffieHellmanGroup14Sha256,
                     SshAlgorithmNames.DiffieHellmanGroup16Sha512,
                 })
        {
            using DiffieHellmanGroupKeyExchange client = new(name);
            byte[] clientPublic = client.CreateClientPublicValue();

            // 服务端用同一个群做一次 DH。
            DHParameters group = name == SshAlgorithmNames.DiffieHellmanGroup14Sha256
                ? DHStandardGroups.rfc3526_2048
                : DHStandardGroups.rfc3526_4096;
            DHParameters parameters = new(group.P, group.G, null, 512);

            DHKeyPairGenerator generator = new();
            generator.Init(new DHKeyGenerationParameters(new SecureRandom(), parameters));
            AsymmetricCipherKeyPair serverPair = generator.GenerateKeyPair();

            byte[] serverPublic = ((DHPublicKeyParameters)serverPair.Public).Y.ToByteArrayUnsigned();

            DHBasicAgreement serverAgreement = new();
            serverAgreement.Init(serverPair.Private);
            byte[] serverSecret = serverAgreement
                .CalculateAgreement(new DHPublicKeyParameters(
                    new Org.BouncyCastle.Math.BigInteger(1, clientPublic), parameters))
                .ToByteArrayUnsigned();

            byte[] clientSecret = client.ComputeSharedSecret(serverPublic);
            Assert.AreSequenceEqual(serverSecret, clientSecret, $"{name}：共享密钥不一致");
        }
    }

    [TestMethod]
    public void 有限域DH拒绝越界的公钥()
    {
        // 0、1、p-1 会让共享密钥落进一个极小的集合（小子群攻击）。
        using DiffieHellmanGroupKeyExchange client = new(SshAlgorithmNames.DiffieHellmanGroup14Sha256);
        _ = client.CreateClientPublicValue();

        Assert.ThrowsExactly<SshKeyExchangeException>(() => client.ComputeSharedSecret([0]));
        Assert.ThrowsExactly<SshKeyExchangeException>(() => client.ComputeSharedSecret([1]));

        byte[] pMinusOne = DHStandardGroups.rfc3526_2048.P
            .Subtract(Org.BouncyCastle.Math.BigInteger.One).ToByteArrayUnsigned();
        Assert.ThrowsExactly<SshKeyExchangeException>(() => client.ComputeSharedSecret(pMinusOne));
    }

    // ------------------------------------------------------------ 后量子混合

    [TestMethod]
    public void MlKem混合两端算出同一个共享密钥()
    {
        using HybridKeyExchange client = new(SshAlgorithmNames.MlKem768X25519Sha256);
        byte[] clientPublic = client.CreateClientPublicValue();

        // ML-KEM-768 公钥 1184 字节 + X25519 公钥 32 字节
        Assert.HasCount(1184 + 32, clientPublic);

        // 服务端：对 KEM 公钥做封装，再做一次 X25519。
        MLKemEncapsulator encapsulator = new(MLKemParameters.ml_kem_768);
        encapsulator.Init(MLKemPublicKeyParameters.FromEncoding(MLKemParameters.ml_kem_768, clientPublic[..1184]));
        byte[] ciphertext = new byte[encapsulator.EncapsulationLength];
        byte[] kemSecret = new byte[encapsulator.SecretLength];
        encapsulator.Encapsulate(ciphertext, 0, ciphertext.Length, kemSecret, 0, kemSecret.Length);
        Assert.HasCount(1088, ciphertext, "ML-KEM-768 密文长度");

        X25519PrivateKeyParameters serverX25519 = new(new SecureRandom());
        byte[] serverX25519Public = serverX25519.GeneratePublicKey().GetEncoded();
        byte[] classicalSecret = new byte[32];
        X25519Agreement agreement = new();
        agreement.Init(serverX25519);
        agreement.CalculateAgreement(new X25519PublicKeyParameters(clientPublic[1184..]), classicalSecret, 0);

        byte[] serverReply = [.. ciphertext, .. serverX25519Public];
        byte[] clientSecret = client.ComputeSharedSecret(serverReply);

        // K = SHA-256(K_kem ‖ K_x25519)
        byte[] expected = SHA256.HashData([.. kemSecret, .. classicalSecret]);
        Assert.AreSequenceEqual(expected, clientSecret);
        Assert.HasCount(32, clientSecret, "SHA-256 输出");
    }

    [TestMethod]
    public void SNtruPrime混合两端算出同一个共享密钥()
    {
        using HybridKeyExchange client = new(SshAlgorithmNames.SNtruP761X25519Sha512);
        byte[] clientPublic = client.CreateClientPublicValue();
        Assert.HasCount(1158 + 32, clientPublic, "sntrup761 公钥 1158 + X25519 32");

        SNtruPrimeKemGenerator generator = new(new SecureRandom());
        ISecretWithEncapsulation encapsulated = generator.GenerateEncapsulated(
            new SNtruPrimePublicKeyParameters(SNtruPrimeParameters.sntrup761, clientPublic[..1158]));
        byte[] ciphertext = encapsulated.GetEncapsulation();
        byte[] kemSecret = encapsulated.GetSecret();
        Assert.HasCount(1039, ciphertext, "sntrup761 密文长度");

        X25519PrivateKeyParameters serverX25519 = new(new SecureRandom());
        byte[] serverX25519Public = serverX25519.GeneratePublicKey().GetEncoded();
        byte[] classicalSecret = new byte[32];
        X25519Agreement agreement = new();
        agreement.Init(serverX25519);
        agreement.CalculateAgreement(new X25519PublicKeyParameters(clientPublic[1158..]), classicalSecret, 0);

        byte[] clientSecret = client.ComputeSharedSecret([.. ciphertext, .. serverX25519Public]);

        byte[] expected = SHA512.HashData([.. kemSecret, .. classicalSecret]);
        Assert.AreSequenceEqual(expected, clientSecret);
        Assert.HasCount(64, clientSecret, "SHA-512 输出");
    }

    [TestMethod]
    public void 混合方法的共享密钥按定长串编码()
    {
        // 这是与 curve25519 / ECDH 的**根本差别**（velashell-docs/zh/ssh/spec/03 §3.6）。
        // 写成 mpint 会在最高位为 1 时多补一个 0x00，表现为概率性签名失败。
        using HybridKeyExchange mlkem = new(SshAlgorithmNames.MlKem768X25519Sha256);
        Assert.AreEqual(SshKexValueEncoding.ByteString, mlkem.SharedSecretEncoding);

        using Curve25519KeyExchange c25519 = new();
        Assert.AreEqual(SshKexValueEncoding.Mpint, c25519.SharedSecretEncoding);

        using DiffieHellmanGroupKeyExchange dh = new(SshAlgorithmNames.DiffieHellmanGroup14Sha256);
        Assert.AreEqual(SshKexValueEncoding.Mpint, dh.SharedSecretEncoding);
        Assert.AreEqual(SshKexValueEncoding.Mpint, dh.PublicValueEncoding, "DH 的公钥也是 mpint");
    }

    // ------------------------------------------------------------ 工厂

    [TestMethod]
    public void 默认清单里的每个KEX算法都造得出实例()
    {
        // 算法清单与工厂注册表不一致是一个编程错误 —— 它会在协商成功之后才炸，
        // 那时用户已经等了一个往返。这条用例把它挪到构建期。
        foreach (string name in SshAlgorithmSet.Default.KeyExchange)
        {
            Assert.IsTrue(SshKeyExchangeFactory.IsSupported(name), $"{name} 未注册");
            using ISshKeyExchange kex = SshKeyExchangeFactory.Create(name);
            Assert.AreEqual(name, kex.Name);
        }
    }

    [TestMethod]
    public void 放开老算法后的清单也都造得出实例()
    {
        foreach (string name in SshAlgorithmSet.Default.WithLegacyInterop().KeyExchange)
        {
            using ISshKeyExchange kex = SshKeyExchangeFactory.Create(name);
            Assert.AreEqual(name, kex.Name);
        }
    }

    [TestMethod]
    public void 未注册的算法名抛出()
    {
        Assert.ThrowsExactly<SshKeyExchangeException>(
            () => SshKeyExchangeFactory.Create("kex-that-does-not-exist"));
    }

    [TestMethod]
    public void 可以注册自定义算法()
    {
        // 架构 §8 第 3 项的扩展点：后量子的下一代方案照此接入，不必改库。
        const string Name = "test-kex@velashell.invalid";
        SshKeyExchangeFactory.Register(Name, static _ => new Curve25519KeyExchange(Name));

        Assert.IsTrue(SshKeyExchangeFactory.IsSupported(Name));
        using ISshKeyExchange kex = SshKeyExchangeFactory.Create(Name);
        Assert.AreEqual(Name, kex.Name);
    }

    private static void CopyRightAligned(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        destination.Clear();
        source.CopyTo(destination[^source.Length..]);
    }
}
