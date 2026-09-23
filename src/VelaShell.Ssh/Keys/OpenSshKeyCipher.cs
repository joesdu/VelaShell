// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH PROTOCOL.key            —— openssh-key-v1 的 ciphername / kdfname / kdfoptions
//   OpenSSH PROTOCOL.chacha20poly1305 —— aadlen 为 0 时的单消息用法
//   NIST SP 800-38A §6.5            —— CTR 模式
//   NIST SP 800-38D                 —— GCM
//   RFC 8439 §2.5                   —— Poly1305
//   行为规格: velashell-docs/zh/ssh/design/architecture.md §11.2.6

using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Parameters;

namespace VelaShell.Ssh.Keys;

/// <summary>
/// 加密的 <c>openssh-key-v1</c> 私钥区怎么解开。
/// </summary>
/// <remarks>
/// <para>
/// 这一层**只做解密**,不做加密 —— 本库从不写出私钥文件。
/// </para>
/// <para>
/// <b>认证类算法(GCM / ChaCha20-Poly1305)的标签在私钥区那个 <c>string</c> 的
/// 外面</b>,紧跟在它后面,是容器里最后的裸字节 —— 不带长度前缀,也不在密文里。
/// 把它当成密文的尾部会得到一个能"解出"正确开头、却永远验不过的结果
/// (流密码解前缀照样是对的),排查起来极其费劲。
/// </para>
/// <para>
/// **标签必须先验再解**,否则等于给攻击者提供一个解密预言机 ——
/// 哪怕这里的"攻击者"只是一个损坏的文件。
/// </para>
/// </remarks>
internal static class OpenSshKeyCipher
{
    /// <summary>一种算法的参数。</summary>
    /// <param name="KeyBytes">密钥字节数。</param>
    /// <param name="IvBytes">IV / nonce 字节数。</param>
    /// <param name="BlockBytes">分组字节数(密文长度必须是它的整数倍)。</param>
    /// <param name="TagBytes">认证标签字节数;非 AEAD 为 0。</param>
    internal readonly record struct CipherShape(int KeyBytes, int IvBytes, int BlockBytes, int TagBytes);

    /// <summary>本层认得的算法名。</summary>
    /// <remarks>
    /// 没有 <c>3des-cbc</c>:它是 <c>ssh-keygen -Z</c> 才选得到的过时算法,
    /// 而为了读一种没人用的格式在一个安全库里带上 3DES 不划算。遇到时如实报错。
    /// </remarks>
    public static IReadOnlyCollection<string> SupportedCipherNames { get; } =
    [
        "aes128-ctr", "aes192-ctr", "aes256-ctr",
        "aes128-cbc", "aes192-cbc", "aes256-cbc",
        "aes128-gcm@openssh.com", "aes256-gcm@openssh.com",
        "chacha20-poly1305@openssh.com",
    ];

    /// <summary>查一种算法的参数;不认识就返回 <see langword="null" />。</summary>
    public static CipherShape? Describe(string cipherName) => cipherName switch
    {
        "aes128-ctr" => new CipherShape(16, 16, 16, 0),
        "aes192-ctr" => new CipherShape(24, 16, 16, 0),
        "aes256-ctr" => new CipherShape(32, 16, 16, 0),
        "aes128-cbc" => new CipherShape(16, 16, 16, 0),
        "aes192-cbc" => new CipherShape(24, 16, 16, 0),
        "aes256-cbc" => new CipherShape(32, 16, 16, 0),
        "aes128-gcm@openssh.com" => new CipherShape(16, 12, 16, 16),
        "aes256-gcm@openssh.com" => new CipherShape(32, 12, 16, 16),
        "chacha20-poly1305@openssh.com" => new CipherShape(64, 0, 8, 16),
        _ => null,
    };

    /// <summary>解开私钥区。</summary>
    /// <param name="cipherName">算法名。</param>
    /// <param name="ciphertext">私钥区那个 <c>string</c> 的内容 —— **只有密文**。</param>
    /// <param name="tag">紧跟在那个 <c>string</c> 后面的认证标签;非 AEAD 时为空。</param>
    /// <param name="key">密钥 ‖ IV 的派生材料。</param>
    /// <returns>明文私钥区。</returns>
    /// <exception cref="CryptographicException">标签校验不过(口令不对或文件损坏)。</exception>
    public static byte[] Decrypt(
        string cipherName, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag, ReadOnlySpan<byte> key)
    {
        CipherShape shape = Describe(cipherName)
            ?? throw new ArgumentException($"不支持的算法 {cipherName}。", nameof(cipherName));

        ReadOnlySpan<byte> keyBytes = key[..shape.KeyBytes];
        ReadOnlySpan<byte> iv = key.Slice(shape.KeyBytes, shape.IvBytes);

        byte[] plaintext = new byte[ciphertext.Length];
        try
        {
            if (cipherName == "chacha20-poly1305@openssh.com")
            {
                DecryptChaCha20Poly1305(ciphertext, tag, keyBytes, plaintext);
            }
            else if (shape.TagBytes != 0)
            {
                using AesGcm aes = new(keyBytes, shape.TagBytes);
                aes.Decrypt(iv, ciphertext, tag, plaintext);
            }
            else if (cipherName.EndsWith("-ctr", StringComparison.Ordinal))
            {
                DecryptCounterMode(ciphertext, keyBytes, iv, plaintext);
            }
            else
            {
                DecryptCipherBlockChaining(ciphertext, keyBytes, iv, plaintext);
            }
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
        return plaintext;
    }

    /// <summary>CTR:把 IV 当成初始计数块,逐块加密计数器再与密文异或。</summary>
    /// <remarks>
    /// 计数器按**整个分组**当作大端整数递增(不是只动低 32 位)——
    /// 私钥区不长,两种做法在这里不会分岔,但写对的那种才与对端一致。
    /// </remarks>
    private static void DecryptCounterMode(
        ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, Span<byte> plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = key.ToArray();

        Span<byte> counter = stackalloc byte[16];
        Span<byte> keyStream = stackalloc byte[16];
        iv.CopyTo(counter);

        for (int offset = 0; offset < ciphertext.Length; offset += 16)
        {
            aes.EncryptEcb(counter, keyStream, PaddingMode.None);
            int length = Math.Min(16, ciphertext.Length - offset);
            for (int i = 0; i < length; i++)
            {
                plaintext[offset + i] = (byte)(ciphertext[offset + i] ^ keyStream[i]);
            }
            IncrementCounter(counter);
        }

        CryptographicOperations.ZeroMemory(keyStream);
        CryptographicOperations.ZeroMemory(counter);
    }

    private static void DecryptCipherBlockChaining(
        ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, Span<byte> plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = key.ToArray();
        aes.DecryptCbc(ciphertext, iv, plaintext, PaddingMode.None);
    }

    /// <summary>
    /// <c>chacha20-poly1305@openssh.com</c> 在私钥文件里的用法:
    /// aad 为空,所以只用得到前 32 字节那把钥,序号恒为 0。
    /// </summary>
    private static void DecryptChaCha20Poly1305(
        ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag, ReadOnlySpan<byte> key, Span<byte> plaintext)
    {
        // 64 字节密钥材料的后 32 字节是「只加密长度字段」的那把钥 ——
        // 私钥文件没有长度字段(aad 为空),所以它在这里用不上。
        // nonce 是 8 字节大端的报文序号;私钥文件里没有序号这回事,恒取 0。
        ChaChaEngine engine = new(20);
        engine.Init(forEncryption: true, new ParametersWithIV(new KeyParameter(key[..32]), new byte[8]));

        // counter 0 的那一整块只用来出 Poly1305 的钥,后 32 字节丢弃;
        // 载荷从 counter 1 开始 —— 引擎读完这一块就停在那里了。
        Span<byte> firstBlock = stackalloc byte[64];
        firstBlock.Clear();
        engine.ProcessBytes(firstBlock, firstBlock);

        Span<byte> computed = stackalloc byte[16];
        Poly1305 mac = new();
        mac.Init(new KeyParameter(firstBlock[..32]));
        mac.BlockUpdate(ciphertext);
        mac.DoFinal(computed);
        CryptographicOperations.ZeroMemory(firstBlock);

        // 先验后解。验不过就到此为止,一个字节的明文都不产出。
        if (!CryptographicOperations.FixedTimeEquals(computed, tag))
        {
            throw new CryptographicException("私钥的认证标签不匹配 —— 口令不对,或者文件损坏了。");
        }

        engine.ProcessBytes(ciphertext, plaintext);
    }

    private static void IncrementCounter(Span<byte> counter)
    {
        for (int i = counter.Length - 1; i >= 0; i--)
        {
            if (++counter[i] != 0)
            {
                break;
            }
        }
    }
}
