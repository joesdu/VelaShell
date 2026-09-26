// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §7.2  六把密钥与字母 A–F 的用途
//   RFC 5647 §7.1  AES-GCM 的 IV 为 12 字节
//   OpenSSH PROTOCOL.chacha20poly1305  64 字节密钥材料
//   行为规格:      velashell-docs/zh/ssh/spec/03-key-exchange.md §7

using System.Security.Cryptography;
using VelaShell.Ssh.Crypto.Kex;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Crypto;

/// <summary>
/// 从一次密钥交换派生出双向的密码套件。
/// </summary>
/// <remarks>
/// RFC 4253 §7.2 的六把密钥，字母与用途的对应关系**是固定的**，
/// 而且**方向不能弄反** —— 作为客户端，我们用 C/A/E 加密发出去的，用 D/B/F 解收到的。
/// 弄反的症状是握手完成后第一个报文就解不开。
/// </remarks>
internal static class SshSessionKeys
{
    /// <summary>为一次交换造出收发两侧的密码套件。</summary>
    /// <param name="algorithms">协商结果。</param>
    /// <param name="hashAlgorithm">KEX 方法配套的哈希。</param>
    /// <param name="sharedSecret">共享密钥 <c>K</c>。</param>
    /// <param name="secretEncoding"><c>K</c> 的编码方式 —— 必须与算 <c>H</c> 时一致。</param>
    /// <param name="exchangeHash">本次交换的 <c>H</c>。</param>
    /// <param name="sessionId">会话标识（首次 KEX 的 <c>H</c>）。</param>
    /// <returns>发送用与接收用的两个套件。调用方负责释放。</returns>
    public static (ISshCipherSuite Send, ISshCipherSuite Receive) Derive(
        in SshNegotiatedAlgorithms algorithms,
        HashAlgorithmName hashAlgorithm,
        ReadOnlySpan<byte> sharedSecret,
        SshKexValueEncoding secretEncoding,
        ReadOnlySpan<byte> exchangeHash,
        ReadOnlySpan<byte> sessionId)
    {
        // 作为客户端：
        //   A/C/E → 客户端→服务端方向 → 我们**发送**用
        //   B/D/F → 服务端→客户端方向 → 我们**接收**用
        ISshCipherSuite send = Build(
            algorithms.EncryptionClientToServer, algorithms.MacClientToServer,
            hashAlgorithm, sharedSecret, secretEncoding, exchangeHash, sessionId,
            ivLetter: 'A', keyLetter: 'C', macLetter: 'E');

        try
        {
            ISshCipherSuite receive = Build(
                algorithms.EncryptionServerToClient, algorithms.MacServerToClient,
                hashAlgorithm, sharedSecret, secretEncoding, exchangeHash, sessionId,
                ivLetter: 'B', keyLetter: 'D', macLetter: 'F');
            return (send, receive);
        }
        catch
        {
            send.Dispose();
            throw;
        }
    }

    /// <summary>该加密算法本库是否实现了（<see cref="Derive"/> 造得出它的套件）。</summary>
    /// <param name="encryption">算法名。</param>
    public static bool IsSupportedEncryption(string encryption) => encryption is
        SshAlgorithmNames.ChaCha20Poly1305 or
        SshAlgorithmNames.Aes128Gcm or SshAlgorithmNames.Aes256Gcm or
        SshAlgorithmNames.Aes128Ctr or SshAlgorithmNames.Aes192Ctr or SshAlgorithmNames.Aes256Ctr;

    /// <summary>该 MAC 算法本库是否实现了。</summary>
    /// <param name="mac">算法名。</param>
    public static bool IsSupportedMac(string mac) => mac is
        SshAlgorithmNames.HmacSha256Etm or SshAlgorithmNames.HmacSha512Etm or
        SshAlgorithmNames.HmacSha256 or SshAlgorithmNames.HmacSha512 or
        SshAlgorithmNames.HmacSha1Etm or SshAlgorithmNames.HmacSha1;

    private static ISshCipherSuite Build(
        string encryption,
        string? mac,
        HashAlgorithmName hashAlgorithm,
        ReadOnlySpan<byte> sharedSecret,
        SshKexValueEncoding secretEncoding,
        ReadOnlySpan<byte> exchangeHash,
        ReadOnlySpan<byte> sessionId,
        char ivLetter,
        char keyLetter,
        char macLetter)
    {
        byte[]? iv = null;
        byte[]? key = null;
        byte[]? macKey = null;

        try
        {
            switch (encryption)
            {
                case SshAlgorithmNames.ChaCha20Poly1305:
                    // 两把 32 字节的钥，从同一个字母派生出 64 字节。不需要 IV。
                    key = DeriveKey(hashAlgorithm, sharedSecret, secretEncoding, exchangeHash, sessionId,
                        keyLetter, ChaCha20Poly1305CipherSuite.KeyMaterialBytes);
                    return new ChaCha20Poly1305CipherSuite(key);

                case SshAlgorithmNames.Aes128Gcm:
                case SshAlgorithmNames.Aes256Gcm:
                    {
                        int keyBytes = encryption == SshAlgorithmNames.Aes128Gcm ? 16 : 32;
                        // RFC 5647 §7.1：GCM 的 IV 是 12 字节（4 固定 + 8 计数器）。
                        iv = DeriveKey(hashAlgorithm, sharedSecret, secretEncoding, exchangeHash, sessionId, ivLetter, 12);
                        key = DeriveKey(hashAlgorithm, sharedSecret, secretEncoding, exchangeHash, sessionId, keyLetter, keyBytes);
                        return new AesGcmCipherSuite(key, iv);
                    }

                case SshAlgorithmNames.Aes128Ctr:
                case SshAlgorithmNames.Aes192Ctr:
                case SshAlgorithmNames.Aes256Ctr:
                    {
                        int keyBytes = encryption switch
                        {
                            SshAlgorithmNames.Aes128Ctr => 16,
                            SshAlgorithmNames.Aes192Ctr => 24,
                            _ => 32,
                        };
                        if (mac is null)
                        {
                            throw new SshKeyExchangeException($"{encryption} 不是 AEAD，必须协商出 MAC 算法。");
                        }

                        (SshMacAlgorithm macAlgorithm, int macBytes, bool etm) = ParseMac(mac);
                        iv = DeriveKey(hashAlgorithm, sharedSecret, secretEncoding, exchangeHash, sessionId, ivLetter, 16);
                        key = DeriveKey(hashAlgorithm, sharedSecret, secretEncoding, exchangeHash, sessionId, keyLetter, keyBytes);
                        macKey = DeriveKey(hashAlgorithm, sharedSecret, secretEncoding, exchangeHash, sessionId, macLetter, macBytes);
                        return new AesCtrHmacCipherSuite(key, iv, macAlgorithm, macKey, etm);
                    }

                default:
                    throw new SshKeyExchangeException($"尚未实现的加密算法：{encryption}。");
            }
        }
        finally
        {
            // 密钥材料已经交给套件复制走了，这里的副本立刻抹掉。
            if (iv is not null)
            {
                CryptographicOperations.ZeroMemory(iv);
            }
            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }
            if (macKey is not null)
            {
                CryptographicOperations.ZeroMemory(macKey);
            }
        }
    }

    private static (SshMacAlgorithm Algorithm, int KeyBytes, bool EncryptThenMac) ParseMac(string mac) => mac switch
    {
        SshAlgorithmNames.HmacSha256Etm => (SshMacAlgorithm.HmacSha256, 32, true),
        SshAlgorithmNames.HmacSha512Etm => (SshMacAlgorithm.HmacSha512, 64, true),
        SshAlgorithmNames.HmacSha256 => (SshMacAlgorithm.HmacSha256, 32, false),
        SshAlgorithmNames.HmacSha512 => (SshMacAlgorithm.HmacSha512, 64, false),
        SshAlgorithmNames.HmacSha1Etm => (SshMacAlgorithm.HmacSha1, 20, true),
        SshAlgorithmNames.HmacSha1 => (SshMacAlgorithm.HmacSha1, 20, false),
        _ => throw new SshKeyExchangeException($"尚未实现的 MAC 算法：{mac}。"),
    };

    private static byte[] DeriveKey(
        HashAlgorithmName hashAlgorithm,
        ReadOnlySpan<byte> sharedSecret,
        SshKexValueEncoding secretEncoding,
        ReadOnlySpan<byte> exchangeHash,
        ReadOnlySpan<byte> sessionId,
        char letter,
        int length) =>
        SshExchangeHash.DeriveKey(
            hashAlgorithm, sharedSecret, secretEncoding, exchangeHash, sessionId, letter, length);
}
