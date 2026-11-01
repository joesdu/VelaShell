using System.Security.Cryptography;
using System.Text;
using VelaShell.Core.Data;

namespace VelaShell.Infrastructure.Persistence;

/// <summary>
/// AES-256-GCM 敏感字段加密。本机加密密钥由 <see cref="MasterKeyResolver" /> 解析:macOS / Linux 上交给系统密钥链保管
/// (钥匙串 / Secret Service),用不了时回落到本地密钥文件(明文 + 0600);Windows 上本地密钥文件以 DPAPI(CurrentUser)包裹。
/// 开了主密码时密钥另经主密码包裹,解锁前加不了密、解不开(<see cref="MasterKeyVault" />)。
/// 密文格式 <c>enc1:base64(nonce ‖ tag ‖ ciphertext)</c>;无前缀的输入视为历史明文。
/// </summary>
/// <remarks>
/// 解密时依次尝试解析出的每一把密钥(GCM 认证标签会如实说明是不是这一把),加密只用第一把 ——
/// 密钥链暂时不可用的那几轮另起的本地密钥、旧版本另生成的密钥,都靠这一点在之后照样解得开。
/// 密文格式因此不必带密钥编号,存量密文一字不改;开启、关闭、修改主密码也一字不改(包的是密钥,不是每条密文)。
/// </remarks>
public sealed class AesSecretProtector : ISecretProtector, IMasterKeyStatusSource
{
    private const string Prefix = StoredSecret.ProtectedPrefix;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly Lazy<MasterKeyVault> _vault;

    /// <summary>
    /// 应用实际使用的保护器:数据根里的密钥文件 + 这个平台的系统密钥链(见 <see cref="MasterKeyStores.ForCurrentPlatform" />)。
    /// </summary>
    public AesSecretProtector(VelaShellStoragePaths paths)
        : this(new MasterKeyResolver((paths ?? throw new ArgumentNullException(nameof(paths))).SecretKeyFile,
            MasterKeyStores.ForCurrentPlatform(paths.RootDirectory)))
    { }

    /// <summary>
    /// 只用指定的本地密钥文件、不碰系统密钥链的保护器(测试与工具用 —— 跑测试不该往开发机的钥匙串里写条目)。
    /// 密钥在首次使用时惰性加载或生成。
    /// </summary>
    public AesSecretProtector(string keyFilePath)
        : this(new MasterKeyResolver(keyFilePath, store: null)) { }

    /// <summary>用给定的解析器构造;密钥在首次使用(或首次查询 <see cref="GetKeyStatus" />)时解析一次。</summary>
    public AesSecretProtector(MasterKeyResolver resolver)
        : this(resolver, time: null) { }

    /// <summary>用给定的解析器与时钟构造(测试用:输错的退避按这个时钟算)。</summary>
    internal AesSecretProtector(MasterKeyResolver resolver, TimeProvider? time)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _vault = new(() => new MasterKeyVault(resolver, time), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>密钥与主密码那一层(首次访问时解析)。</summary>
    internal MasterKeyVault Vault => _vault.Value;

    /// <inheritdoc />
    public MasterKeyStatus GetKeyStatus() => Vault.Status;

    /// <inheritdoc />
    /// <remarks>解析失败(数据目录写不进去)时也一直是 false:那种情况第一次真用到密钥时照样抛出来。</remarks>
    public bool IsReady => _vault.IsValueCreated;

    /// <inheritdoc />
    public bool IsLocked => Vault.IsLocked;

    /// <summary>加密明文并返回带 <c>enc1:</c> 前缀的密文;空值或已加密的输入原样返回。</summary>
    /// <exception cref="SecretsLockedException">主密码开着、此刻锁着,而输入是一段新的明文。</exception>
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
        Vault.UseKeys(ring =>
        {
            // 锁着时不另起新密钥:那会生出一把主密码管不到的密钥。
            if (ring is null)
            {
                throw new SecretsLockedException();
            }
            using var aes = new AesGcm(ring.Current, TagSize);
            aes.Encrypt(nonce, plainBytes, cipher, tag);
            return true;
        });
        byte[] blob = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, NonceSize);
        cipher.CopyTo(blob, NonceSize + TagSize);
        return Prefix + Convert.ToBase64String(blob);
    }

    /// <summary>解密带 <c>enc1:</c> 前缀的密文;无前缀的历史明文原样返回,解密失败(含锁着)时返回原始输入。</summary>
    public string? Unprotect(string? ciphertext) =>
        // 密钥不匹配、数据损坏或主密码锁着:返回原文以避免连锁失败,连接时会以「解不开」暴露;存回去时密文也不会丢。
        TryUnprotect(ciphertext, out string? plaintext) ? plaintext : ciphertext;

    /// <inheritdoc />
    public bool TryUnprotect(string? ciphertext, out string? plaintext)
    {
        plaintext = ciphertext;
        if (string.IsNullOrEmpty(ciphertext) || !ciphertext.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return true;
        }
        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(ciphertext[Prefix.Length..]);
        }
        catch (FormatException)
        {
            plaintext = null;
            return false;
        }
        if (blob.Length < NonceSize + TagSize)
        {
            plaintext = null;
            return false;
        }
        byte[] plain = new byte[blob.Length - NonceSize - TagSize];
        bool decrypted = Vault.UseKeys(ring =>
        {
            if (ring is null)
            {
                return false;
            }
            ReadOnlySpan<byte> nonce = blob.AsSpan(0, NonceSize);
            ReadOnlySpan<byte> tag = blob.AsSpan(NonceSize, TagSize);
            ReadOnlySpan<byte> cipher = blob.AsSpan(NonceSize + TagSize);
            foreach (byte[] key in ring.Keys)
            {
                try
                {
                    using var aes = new AesGcm(key, TagSize);
                    aes.Decrypt(nonce, cipher, tag, plain);
                    return true;
                }
                catch (CryptographicException)
                {
                    // 认证标签对不上:不是这一把,试下一把。
                }
            }
            return false;
        });
        plaintext = decrypted ? Encoding.UTF8.GetString(plain) : null;
        return decrypted;
    }
}
