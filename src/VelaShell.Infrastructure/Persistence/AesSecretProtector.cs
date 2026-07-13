using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using VelaShell.Core.Data;

namespace VelaShell.Infrastructure.Persistence;

/// <summary>
/// AES-256-GCM 敏感字段加密。密钥保存在本地密钥文件(首次使用时生成 32 字节随机密钥):
/// Windows 上以 DPAPI(CurrentUser)密文落盘,其余平台明文 + 0600 权限;历史明文密钥会自动升级。
/// 密文格式 <c>enc1:base64(nonce ‖ tag ‖ ciphertext)</c>;无前缀的输入视为历史明文。
/// </summary>
public sealed class AesSecretProtector : ISecretProtector
{
    private const string Prefix = "enc1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly Lazy<byte[]> _key;

    /// <summary>使用存储路径配置中的密钥文件位置构造保护器。</summary>
    public AesSecretProtector(VelaShellStoragePaths paths)
        : this((paths ?? throw new ArgumentNullException(nameof(paths))).SecretKeyFile) { }

    /// <summary>使用指定密钥文件路径构造保护器;密钥在首次使用时惰性加载或生成。</summary>
    public AesSecretProtector(string keyFilePath)
    {
        if (string.IsNullOrWhiteSpace(keyFilePath))
        {
            throw new ArgumentException(@"Key file path is required.", nameof(keyFilePath));
        }
        _key = new(() => LoadOrCreateKey(keyFilePath), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>加密明文并返回带 <c>enc1:</c> 前缀的密文;空值或已加密的输入原样返回。</summary>
    public string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext) || plaintext.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return plaintext;
        }
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] plainBytes = Encoding.UTF8.GetBytes(plaintext);
        byte[] cipher = new byte[plainBytes.Length];
        byte[] tag = new byte[TagSize];
        using var aes = new AesGcm(_key.Value, TagSize);
        aes.Encrypt(nonce, plainBytes, cipher, tag);
        byte[] blob = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, NonceSize);
        cipher.CopyTo(blob, NonceSize + TagSize);
        return Prefix + Convert.ToBase64String(blob);
    }

    /// <summary>解密带 <c>enc1:</c> 前缀的密文;无前缀的历史明文原样返回,解密失败时返回原始输入。</summary>
    public string? Unprotect(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext) || !ciphertext.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return ciphertext;
        }
        try
        {
            byte[] blob = Convert.FromBase64String(ciphertext[Prefix.Length..]);
            if (blob.Length < NonceSize + TagSize)
            {
                return ciphertext;
            }
            Span<byte> nonce = blob.AsSpan(0, NonceSize);
            Span<byte> tag = blob.AsSpan(NonceSize, TagSize);
            Span<byte> cipher = blob.AsSpan(NonceSize + TagSize);
            byte[] plain = new byte[cipher.Length];
            using var aes = new AesGcm(_key.Value, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
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
            byte[] stored = File.ReadAllBytes(keyFilePath);

            // Windows:密钥以 DPAPI(CurrentUser)密文落盘。历史版本可能是 32 字节明文,
            // 解包失败时回退按明文处理,并顺带升级为 DPAPI 密文。
            if (OperatingSystem.IsWindows())
            {
                if (TryDpapiUnprotect(stored, out byte[] unwrapped) && unwrapped.Length == 32)
                {
                    return unwrapped;
                }
                if (stored.Length == 32)
                {
                    WriteKey(keyFilePath, stored); // 明文 → DPAPI 密文迁移
                    return stored;
                }
            }
            else if (stored.Length == 32)
            {
                return stored;
            }
        }
        byte[] key = RandomNumberGenerator.GetBytes(32);
        WriteKey(keyFilePath, key);
        return key;
    }

    /// <summary>写入密钥:Windows 用 DPAPI(CurrentUser)包裹,其余平台明文 + 0600 权限。</summary>
    private static void WriteKey(string keyFilePath, byte[] key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(keyFilePath)!);
        if (OperatingSystem.IsWindows())
        {
            byte[] wrapped = ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(keyFilePath, wrapped);
            return;
        }
        File.WriteAllBytes(keyFilePath, key);
        File.SetUnixFileMode(keyFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [SupportedOSPlatform("windows")]
    private static bool TryDpapiUnprotect(byte[] wrapped, out byte[] key)
    {
        try
        {
            key = ProtectedData.Unprotect(wrapped, null, DataProtectionScope.CurrentUser);
            return true;
        }
        catch (CryptographicException)
        {
            key = [];
            return false;
        }
    }
}
