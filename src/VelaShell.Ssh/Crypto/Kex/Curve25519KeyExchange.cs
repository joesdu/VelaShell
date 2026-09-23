// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 8731 §3    curve25519-sha256:公钥按 string,共享密钥按 mpint
//   RFC 7748 §6.1  contributory behaviour —— 结果全零必须中止
//   行为规格:      velashell-docs/zh/ssh/spec/03-key-exchange.md §3.2

using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Crypto.Kex;

/// <summary><c>curve25519-sha256</c>（以及等价的 <c>curve25519-sha256@libssh.org</c>）。</summary>
/// <remarks>
/// 两个名字指的是**完全相同**的算法，只是历史原因留下了两个注册名。
/// </remarks>
public sealed class Curve25519KeyExchange : ISshKeyExchange
{
    /// <summary>X25519 公钥与共享密钥的字节数。</summary>
    public const int KeyBytes = 32;

    private readonly X25519PrivateKeyParameters _privateKey;
    private bool _disposed;

    /// <summary>创建一次 curve25519 交换。</summary>
    /// <param name="name">算法名（两个等价注册名之一）。</param>
    public Curve25519KeyExchange(string name = SshAlgorithmNames.Curve25519Sha256)
    {
        Name = name;
        _privateKey = new X25519PrivateKeyParameters(new SecureRandom());
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public HashAlgorithmName HashAlgorithm => HashAlgorithmName.SHA256;

    /// <inheritdoc />
    public SshKexValueEncoding PublicValueEncoding => SshKexValueEncoding.ByteString;

    /// <inheritdoc />
    /// <remarks>
    /// <b>共享密钥按 <c>mpint</c>。</b>它是 32 字节随机数据，最高位为 1 的概率是 1/2，
    /// 而 <c>mpint</c> 此时要补一个 <c>0x00</c>。按 <c>string</c> 写会在**大约一半**的连接上
    /// 让签名验证失败 —— 那种概率性失败极难排查（velashell-docs/zh/ssh/spec/00 §3.1）。
    /// </remarks>
    public SshKexValueEncoding SharedSecretEncoding => SshKexValueEncoding.Mpint;

    /// <inheritdoc />
    public byte[] CreateClientPublicValue()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _privateKey.GeneratePublicKey().GetEncoded();
    }

    /// <inheritdoc />
    public byte[] ComputeSharedSecret(ReadOnlySpan<byte> serverPublicValue)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (serverPublicValue.Length != KeyBytes)
        {
            throw new SshKeyExchangeException(
                $"curve25519 的服务端公钥必须是 {KeyBytes} 字节，收到 {serverPublicValue.Length} 字节。");
        }

        byte[] secret = new byte[KeyBytes];
        try
        {
            X25519Agreement agreement = new();
            agreement.Init(_privateKey);
            agreement.CalculateAgreement(
                new X25519PublicKeyParameters(serverPublicValue.ToArray()), secret, 0);
        }
        catch (Exception ex) when (ex is not SshKeyExchangeException)
        {
            throw new SshKeyExchangeException("curve25519 协商失败：服务端公钥非法。", ex);
        }

        // RFC 7748 §6.1 的 contributory behaviour：全零意味着对端给了一个低阶点，
        // 此时共享密钥与我方私钥无关 —— 对端可以单方面决定它。必须中止。
        Span<byte> zero = stackalloc byte[KeyBytes];
        if (CryptographicOperations.FixedTimeEquals(secret, zero))
        {
            CryptographicOperations.ZeroMemory(secret);
            throw new SshKeyExchangeException(
                "curve25519 协商结果为全零：服务端提供了低阶点。");
        }

        return secret;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        // BC 的私钥参数没有显式清零接口；它会随对象一起被回收。
    }
}
