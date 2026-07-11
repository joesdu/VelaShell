using System.Security.Cryptography;
using System.Text;

namespace VelaShell.Core.Sync;

/// <summary>
/// 同步载荷的端到端加密:PBKDF2-SHA256(200k 迭代)派生 AES-256-GCM 密钥。
/// 密文格式 Base64(salt16 | nonce12 | tag16 | cipher);口令只在各端本地保存,
/// GitHub 侧只见密文,即使 Gist 链接泄露也无法还原内容。
/// </summary>
public static class SyncCrypto
{
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int Iterations = 200_000;

    public static string Encrypt(string plaintext, string passphrase)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        byte[] plain = Encoding.UTF8.GetBytes(plaintext);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);
        byte[] blob = new byte[SaltSize + NonceSize + TagSize + cipher.Length];
        salt.CopyTo(blob, 0);
        nonce.CopyTo(blob, SaltSize);
        tag.CopyTo(blob, SaltSize + NonceSize);
        cipher.CopyTo(blob, SaltSize + NonceSize + TagSize);
        return Convert.ToBase64String(blob);
    }

    /// <summary>解密;口令错误或数据损坏时抛 <see cref="CryptographicException" />。</summary>
    public static string Decrypt(string blobBase64, string passphrase)
    {
        ArgumentException.ThrowIfNullOrEmpty(blobBase64);
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        byte[] blob = Convert.FromBase64String(blobBase64);
        if (blob.Length < SaltSize + NonceSize + TagSize)
        {
            throw new CryptographicException("密文长度非法。");
        }
        ReadOnlySpan<byte> salt = blob.AsSpan(0, SaltSize);
        ReadOnlySpan<byte> nonce = blob.AsSpan(SaltSize, NonceSize);
        ReadOnlySpan<byte> tag = blob.AsSpan(SaltSize + NonceSize, TagSize);
        ReadOnlySpan<byte> cipher = blob.AsSpan(SaltSize + NonceSize + TagSize);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        byte[] plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
