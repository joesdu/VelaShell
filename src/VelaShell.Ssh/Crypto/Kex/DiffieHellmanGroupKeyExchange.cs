// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §8    diffie-hellman-group14-sha1:e = g^x mod p,公钥与 K 都按 mpint
//   RFC 8268 §3    diffie-hellman-group14-sha256 / group16-sha512
//   RFC 3526 §3/§5 MODP 2048 位(group 14)与 4096 位(group 16)的群参数
//   行为规格:      velashell-docs/zh/ssh/spec/03-key-exchange.md §3.4
//
// 群参数取自 BouncyCastle 的 DHStandardGroups —— 不手工抄写那两个大素数。
// 4096 位素数抄错一个十六进制位不会报错,只会让协商静默失败,而且极难发现。

using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using VelaShell.Ssh.Protocol;
using BcBigInteger = Org.BouncyCastle.Math.BigInteger;

namespace VelaShell.Ssh.Crypto.Kex;

/// <summary>
/// <c>diffie-hellman-group14-sha256</c> / <c>group16-sha512</c> / <c>group14-sha1</c>。
/// </summary>
/// <remarks>
/// <para>
/// 有限域 DH。相对椭圆曲线，它的公钥与模幂都大一个数量级（2048 / 4096 位），
/// 因此排在默认清单的最后 —— 但很多老设备只有它。
/// </para>
/// <para>
/// 〔决策，velashell-docs/zh/ssh/spec/03 §3.4〕私指数取 <b>2 × 哈希输出长度</b>，
/// 而不是群的完整位宽。RFC 4253 §8 只建议「至少两倍」；取完整位宽没有额外安全收益，
/// 只会让模幂慢一大截（4096 位群上是 4096 位指数 vs 512 位指数的差别）。
/// </para>
/// </remarks>
public sealed class DiffieHellmanGroupKeyExchange : ISshKeyExchange
{
    private readonly DHParameters _parameters;
    private readonly AsymmetricCipherKeyPair _keyPair;
    private bool _disposed;

    /// <summary>按算法名创建一次有限域 DH 交换。</summary>
    public DiffieHellmanGroupKeyExchange(string name)
    {
        DHParameters group;
        int hashBits;

        switch (name)
        {
            case SshAlgorithmNames.DiffieHellmanGroup14Sha256:
                group = DHStandardGroups.rfc3526_2048;
                HashAlgorithm = HashAlgorithmName.SHA256;
                hashBits = 256;
                break;
            case SshAlgorithmNames.DiffieHellmanGroup16Sha512:
                group = DHStandardGroups.rfc3526_4096;
                HashAlgorithm = HashAlgorithmName.SHA512;
                hashBits = 512;
                break;
            case SshAlgorithmNames.DiffieHellmanGroup14Sha1:
                // 只为老网络设备保留，默认不在算法清单里。
                group = DHStandardGroups.rfc3526_2048;
                HashAlgorithm = HashAlgorithmName.SHA1;
                hashBits = 160;
                break;
            default:
                throw new ArgumentException($"不是已知的有限域 DH 算法名：{name}", nameof(name));
        }

        Name = name;

        // 第三个参数是私指数的位数。2 × 哈希长度，见 <remarks>。
        _parameters = new DHParameters(group.P, group.G, null, hashBits * 2);

        DHKeyPairGenerator generator = new();
        generator.Init(new DHKeyGenerationParameters(new SecureRandom(), _parameters));
        _keyPair = generator.GenerateKeyPair();
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public HashAlgorithmName HashAlgorithm { get; }

    /// <inheritdoc />
    /// <remarks><b>公钥也是 <c>mpint</c></b> —— 这是它与椭圆曲线方法的关键差别。</remarks>
    public SshKexValueEncoding PublicValueEncoding => SshKexValueEncoding.Mpint;

    /// <inheritdoc />
    public SshKexValueEncoding SharedSecretEncoding => SshKexValueEncoding.Mpint;

    /// <inheritdoc />
    public byte[] CreateClientPublicValue()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // 无符号大端。mpint 的前导零处理由 SshDataWriter.WriteMpint 负责。
        return ((DHPublicKeyParameters)_keyPair.Public).Y.ToByteArrayUnsigned();
    }

    /// <inheritdoc />
    public byte[] ComputeSharedSecret(ReadOnlySpan<byte> serverPublicValue)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        BcBigInteger f = new(1, serverPublicValue.ToArray());

        // **必须校验 1 < f < p-1。**
        // 不校验等于接受 0、1、p-1 这几个值 —— 它们会让共享密钥落进一个极小的集合，
        // 对端可以单方面决定它（小子群攻击）。
        BcBigInteger one = BcBigInteger.One;
        BcBigInteger pMinusOne = _parameters.P.Subtract(one);
        if (f.CompareTo(one) <= 0 || f.CompareTo(pMinusOne) >= 0)
        {
            throw new SshKeyExchangeException(
                $"{Name} 的服务端公钥越界：必须满足 1 < f < p-1。");
        }

        try
        {
            DHBasicAgreement agreement = new();
            agreement.Init(_keyPair.Private);
            BcBigInteger secret = agreement.CalculateAgreement(new DHPublicKeyParameters(f, _parameters));
            return secret.ToByteArrayUnsigned();
        }
        catch (Exception ex) when (ex is not SshKeyExchangeException)
        {
            throw new SshKeyExchangeException($"{Name} 协商失败。", ex);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _disposed = true;
}
