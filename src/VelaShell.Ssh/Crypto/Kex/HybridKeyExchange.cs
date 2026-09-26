// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-kampanakis-curdle-ssh-pq-ke  mlkem768x25519-sha256
//   OpenSSH PROTOCOL                   sntrup761x25519-sha512
//   FIPS 203                           ML-KEM-768
//   RFC 7748                           X25519
//   行为规格:                          velashell-docs/zh/ssh/spec/03-key-exchange.md §3.6

using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Kems;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Pqc.Crypto.NtruPrime;
using Org.BouncyCastle.Security;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Crypto.Kex;

/// <summary>
/// 后量子混合密钥交换：把一个 KEM 与 X25519 **并联**。
/// </summary>
/// <remarks>
/// <para>
/// 支持 <c>mlkem768x25519-sha256</c> 与 <c>sntrup761x25519-sha512</c>
/// （含其旧名 <c>sntrup761x25519-sha512@openssh.com</c>）。
/// </para>
/// <para>
/// <b>为什么是「混合」而不是纯后量子</b>：后量子算法还年轻，
/// 万一某个被攻破，X25519 仍然撑着；反过来量子计算机来了，KEM 撑着。
/// 两者都被攻破才失效 —— 而这正是「先截获、以后再解」这类攻击今天就需要的防护。
/// </para>
/// <para>
/// 形状（两端的公开值都是**两段拼接后整体按一个 <c>string</c>**，不是两个 string）：
/// </para>
/// <list type="bullet">
///   <item>客户端 → 服务端：<c>KEM 公钥 ‖ X25519 公钥</c></item>
///   <item>服务端 → 客户端：<c>KEM 密文 ‖ X25519 公钥</c></item>
///   <item>共享密钥：<c>HASH(K_kem ‖ K_x25519)</c>，<b>按 <c>string</c> 编码</b></item>
/// </list>
/// <para>
/// ⚠️ 最后那条是与其它所有方法的**根本差别**：curve25519 与 ECDH 的 <c>K</c> 是
/// <c>mpint</c>，这里是定长 <c>string</c>。混合方法刻意改用定长，正是为了避开
/// <c>mpint</c> 的前导零问题（velashell-docs/zh/ssh/spec/03 §3.6）。写错同样表现为概率性签名失败。
/// </para>
/// </remarks>
internal sealed class HybridKeyExchange : ISshKeyExchange
{
    private const int X25519KeyBytes = 32;

    private readonly IHybridKem _kem;
    private readonly X25519PrivateKeyParameters _x25519Private;
    private bool _disposed;

    /// <summary>按算法名创建一次混合交换。</summary>
    public HybridKeyExchange(string name)
    {
        switch (name)
        {
            case SshAlgorithmNames.MlKem768X25519Sha256:
                _kem = new MlKem768Kem();
                HashAlgorithm = HashAlgorithmName.SHA256;
                break;
            case SshAlgorithmNames.Sntrup761X25519Sha512:
            case SshAlgorithmNames.Sntrup761X25519Sha512OpenSsh:
                _kem = new SNtruPrime761Kem();
                HashAlgorithm = HashAlgorithmName.SHA512;
                break;
            default:
                throw new ArgumentException($"不是已知的混合 KEX 算法名：{name}", nameof(name));
        }

        Name = name;
        _x25519Private = new X25519PrivateKeyParameters(new SecureRandom());
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public HashAlgorithmName HashAlgorithm { get; }

    /// <inheritdoc />
    public SshKexValueEncoding PublicValueEncoding => SshKexValueEncoding.ByteString;

    /// <inheritdoc />
    /// <remarks><b>定长 <c>string</c>，不是 <c>mpint</c>。</b>见类型说明。</remarks>
    public SshKexValueEncoding SharedSecretEncoding => SshKexValueEncoding.ByteString;

    /// <inheritdoc />
    public byte[] CreateClientPublicValue()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        byte[] kemPublic = _kem.GenerateKeyPairAndGetPublicKey();
        byte[] x25519Public = _x25519Private.GeneratePublicKey().GetEncoded();

        byte[] combined = new byte[kemPublic.Length + x25519Public.Length];
        kemPublic.CopyTo(combined, 0);
        x25519Public.CopyTo(combined, kemPublic.Length);
        return combined;
    }

    /// <inheritdoc />
    public byte[] ComputeSharedSecret(ReadOnlySpan<byte> serverPublicValue)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int expected = _kem.CiphertextBytes + X25519KeyBytes;
        if (serverPublicValue.Length != expected)
        {
            throw new SshKeyExchangeException(
                $"{Name} 的服务端公开值必须是 {expected} 字节" +
                $"（{_kem.CiphertextBytes} 字节 KEM 密文 + {X25519KeyBytes} 字节 X25519 公钥），" +
                $"收到 {serverPublicValue.Length} 字节。");
        }

        byte[] kemSecret = _kem.Decapsulate(serverPublicValue[.._kem.CiphertextBytes]);
        byte[] classicalSecret = new byte[X25519KeyBytes];

        try
        {
            X25519Agreement agreement = new();
            agreement.Init(_x25519Private);
            agreement.CalculateAgreement(
                new X25519PublicKeyParameters(serverPublicValue[_kem.CiphertextBytes..].ToArray()),
                classicalSecret, 0);
        }
        catch (Exception ex) when (ex is not SshKeyExchangeException)
        {
            CryptographicOperations.ZeroMemory(kemSecret);
            throw new SshKeyExchangeException($"{Name} 的 X25519 部分协商失败。", ex);
        }

        // RFC 7748 §6.1：X25519 结果全零意味着对端给了低阶点。
        // 混合方案下这一条仍然要查 —— 不能因为「反正还有 KEM 撑着」就放过它。
        Span<byte> zero = stackalloc byte[X25519KeyBytes];
        if (CryptographicOperations.FixedTimeEquals(classicalSecret, zero))
        {
            CryptographicOperations.ZeroMemory(kemSecret);
            CryptographicOperations.ZeroMemory(classicalSecret);
            throw new SshKeyExchangeException($"{Name}：X25519 协商结果为全零，服务端提供了低阶点。");
        }

        try
        {
            // K = HASH(K_kem ‖ K_x25519)。顺序不能颠倒。
            byte[] combined = new byte[kemSecret.Length + classicalSecret.Length];
            kemSecret.CopyTo(combined, 0);
            classicalSecret.CopyTo(combined, kemSecret.Length);
            try
            {
                return HashAlgorithm == HashAlgorithmName.SHA256
                    ? SHA256.HashData(combined)
                    : SHA512.HashData(combined);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(combined);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kemSecret);
            CryptographicOperations.ZeroMemory(classicalSecret);
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
        _kem.Dispose();
    }

    /// <summary>把两种 KEM 的差异收进一个接口，让上面的混合逻辑只写一遍。</summary>
    private interface IHybridKem : IDisposable
    {
        int CiphertextBytes { get; }

        byte[] GenerateKeyPairAndGetPublicKey();

        byte[] Decapsulate(ReadOnlySpan<byte> ciphertext);
    }

    private sealed class MlKem768Kem : IHybridKem
    {
        private MLKemPrivateKeyParameters? _private;

        /// <summary>ML-KEM-768 的密文长度（FIPS 203 表 3）。</summary>
        public int CiphertextBytes => 1088;

        public byte[] GenerateKeyPairAndGetPublicKey()
        {
            MLKemKeyPairGenerator generator = new();
            generator.Init(new MLKemKeyGenerationParameters(new SecureRandom(), MLKemParameters.ml_kem_768));
            AsymmetricCipherKeyPair pair = generator.GenerateKeyPair();
            _private = (MLKemPrivateKeyParameters)pair.Private;
            return ((MLKemPublicKeyParameters)pair.Public).GetEncoded();
        }

        public byte[] Decapsulate(ReadOnlySpan<byte> ciphertext)
        {
            if (_private is null)
            {
                throw new InvalidOperationException("必须先生成密钥对。");
            }

            MLKemDecapsulator decapsulator = new(MLKemParameters.ml_kem_768);
            decapsulator.Init(_private);
            byte[] secret = new byte[decapsulator.SecretLength];
            decapsulator.Decapsulate(ciphertext.ToArray(), 0, ciphertext.Length, secret, 0, secret.Length);
            return secret;
        }

        public void Dispose() => _private = null;
    }

    private sealed class SNtruPrime761Kem : IHybridKem
    {
        private SNtruPrimePrivateKeyParameters? _private;

        /// <summary>sntrup761 的密文长度。</summary>
        public int CiphertextBytes => 1039;

        public byte[] GenerateKeyPairAndGetPublicKey()
        {
            SNtruPrimeKeyPairGenerator generator = new();
            generator.Init(new SNtruPrimeKeyGenerationParameters(
                new SecureRandom(), SNtruPrimeParameters.sntrup761));
            AsymmetricCipherKeyPair pair = generator.GenerateKeyPair();
            _private = (SNtruPrimePrivateKeyParameters)pair.Private;
            return ((SNtruPrimePublicKeyParameters)pair.Public).GetEncoded();
        }

        public byte[] Decapsulate(ReadOnlySpan<byte> ciphertext)
        {
            if (_private is null)
            {
                throw new InvalidOperationException("必须先生成密钥对。");
            }

            SNtruPrimeKemExtractor extractor = new(_private);
            return extractor.ExtractSecret(ciphertext.ToArray());
        }

        public void Dispose() => _private = null;
    }
}
