using System.Security.Cryptography;
using PulseTerm.Core.Data;

namespace PulseTerm.Infrastructure.Persistence;

/// <summary>
/// AES-256-GCM 敏感字段加密。密钥保存在本地密钥文件(首次使用时生成 32 字节随机密钥),
/// 密文格式 <c>enc1:base64(nonce ‖ tag ‖ ciphertext)</c>;无前缀的输入视为历史明文。
/// </summary>
public sealed class AesSecretProtector : ISecretProtector
{
    private const string Prefix = "enc1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly Lazy<byte[]> _key;

    public AesSecretProtector(PulseTermStoragePaths paths)
        : this((paths ?? throw new ArgumentNullException(nameof(paths))).SecretKeyFile)
    {
    }

    public AesSecretProtector(string keyFilePath)
    {
        if (string.IsNullOrWhiteSpace(keyFilePath))
        {
            throw new ArgumentException("Key file path is required.", nameof(keyFilePath));
        }

        _key = new Lazy<byte[]>(() => LoadOrCreateKey(keyFilePath), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext) || plaintext.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return plaintext;
        }

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plainBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key.Value, TagSize);
        aes.Encrypt(nonce, plainBytes, cipher, tag);

        var blob = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, NonceSize);
        cipher.CopyTo(blob, NonceSize + TagSize);
        return Prefix + Convert.ToBase64String(blob);
    }

    public string? Unprotect(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext) || !ciphertext.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return ciphertext;
        }

        try
        {
            var blob = Convert.FromBase64String(ciphertext[Prefix.Length..]);
            if (blob.Length < NonceSize + TagSize)
            {
                return ciphertext;
            }

            var nonce = blob.AsSpan(0, NonceSize);
            var tag = blob.AsSpan(NonceSize, TagSize);
            var cipher = blob.AsSpan(NonceSize + TagSize);
            var plain = new byte[cipher.Length];

            using var aes = new AesGcm(_key.Value, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
            return System.Text.Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            // 密钥不匹配或数据损坏:返回原文以避免连锁失败,连接时会以认证失败暴露。
            return ciphertext;
        }
    }

    private static byte[] LoadOrCreateKey(string keyFilePath)
    {
        if (File.Exists(keyFilePath))
        {
            var existing = File.ReadAllBytes(keyFilePath);
            if (existing.Length == 32)
            {
                return existing;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(keyFilePath)!);
        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(keyFilePath, key);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(keyFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return key;
    }
}
