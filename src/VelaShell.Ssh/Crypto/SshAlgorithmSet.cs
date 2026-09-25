// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §7.1  协商规则:取客户端列表中第一个双方都支持的
//   行为规格:      velashell-docs/zh/ssh/spec/00-overview.md §6(总表)、§7(优先级);velashell-docs/zh/ssh/spec/03 §2.2

using VelaShell.Ssh.Crypto.Kex;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Crypto;

/// <summary>
/// 本端支持的算法清单。**列表顺序就是偏好顺序**。
/// </summary>
/// <remarks>
/// <para>
/// 协商规则（RFC 4253 §7.1）是「取**客户端列表**中第一个、且同时出现在服务端列表里的名字」。
/// 因此作为客户端，我们的顺序完全决定了结果 —— 服务端的顺序不起作用。
/// </para>
/// <para>
/// 排序原则（velashell-docs/zh/ssh/spec/00 §7）：
/// </para>
/// <list type="number">
///   <item>安全性优先于性能：后量子混合 &gt; 椭圆曲线 &gt; 有限域 DH。</item>
///   <item>AEAD 优先于「加密 + MAC」：少一次数据遍历，且没有 MAC 与加密顺序的历史坑。</item>
///   <item>同等安全性下选有硬件加速的 —— 见 <see cref="Default"/> 对 AES 与 ChaCha 的运行期排序。</item>
///   <item>默认关闭的老算法不进默认列表，只在使用者显式配置时加入。</item>
/// </list>
/// </remarks>
public sealed record SshAlgorithmSet
{
    /// <summary>密钥交换算法，按偏好排序。</summary>
    public required IReadOnlyList<string> KeyExchange { get; init; }

    /// <summary>主机密钥算法，按偏好排序。</summary>
    public required IReadOnlyList<string> HostKey { get; init; }

    /// <summary>加密算法（客户端 → 服务端）。</summary>
    public required IReadOnlyList<string> EncryptionClientToServer { get; init; }

    /// <summary>加密算法（服务端 → 客户端）。</summary>
    public required IReadOnlyList<string> EncryptionServerToClient { get; init; }

    /// <summary>MAC 算法（客户端 → 服务端）。AEAD 加密下协商结果被忽略。</summary>
    public required IReadOnlyList<string> MacClientToServer { get; init; }

    /// <summary>MAC 算法（服务端 → 客户端）。</summary>
    public required IReadOnlyList<string> MacServerToClient { get; init; }

    /// <summary>压缩算法（客户端 → 服务端）。</summary>
    public required IReadOnlyList<string> CompressionClientToServer { get; init; }

    /// <summary>压缩算法（服务端 → 客户端）。</summary>
    public required IReadOnlyList<string> CompressionServerToClient { get; init; }

    /// <summary>
    /// 默认清单：安全、现代、不含任何已被弃用的算法。
    /// </summary>
    /// <remarks>
    /// 要连老网络设备（Cisco IOS、华为 VRP、CentOS 7）的，用
    /// <see cref="WithLegacyInterop"/> 显式放开 —— 那是使用者的决定，不是默认。
    /// </remarks>
    public static SshAlgorithmSet Default { get; } = CreateDefault();

    private static SshAlgorithmSet CreateDefault()
    {
        // AES 与 ChaCha20 的相对顺序按**本机是否有 AES 硬件加速**决定。
        //
        // 没有 AES-NI / ARM Crypto 扩展时，软件 AES 比 ChaCha20 慢一个数量级；
        // 有硬件加速时 AES-GCM 又明显快于 ChaCha20。写死任何一个顺序，
        // 都会在一半的机器上选错。
        bool aesAccelerated =
            (System.Runtime.Intrinsics.X86.Aes.IsSupported && System.Runtime.Intrinsics.X86.Pclmulqdq.IsSupported)
            || System.Runtime.Intrinsics.Arm.Aes.IsSupported;

        string[] encryption = aesAccelerated
            ?
            [
                SshAlgorithmNames.Aes256Gcm,
                SshAlgorithmNames.Aes128Gcm,
                SshAlgorithmNames.ChaCha20Poly1305,
                SshAlgorithmNames.Aes256Ctr,
                SshAlgorithmNames.Aes192Ctr,
                SshAlgorithmNames.Aes128Ctr,
            ]
            :
            [
                SshAlgorithmNames.ChaCha20Poly1305,
                SshAlgorithmNames.Aes256Gcm,
                SshAlgorithmNames.Aes128Gcm,
                SshAlgorithmNames.Aes256Ctr,
                SshAlgorithmNames.Aes192Ctr,
                SshAlgorithmNames.Aes128Ctr,
            ];

        string[] mac =
        [
            // EtM 排在前面：MtE 要求先解密不可信数据才能验证，本质上是一个解密预言机。
            SshAlgorithmNames.HmacSha256Etm,
            SshAlgorithmNames.HmacSha512Etm,
            SshAlgorithmNames.HmacSha256,
            SshAlgorithmNames.HmacSha512,
        ];

        return new SshAlgorithmSet
        {
            KeyExchange =
            [
                // 后量子混合排最前：「先截获、以后再解」的攻击今天就在发生。
                SshAlgorithmNames.MlKem768X25519Sha256,
                SshAlgorithmNames.SNtruP761X25519Sha512,
                SshAlgorithmNames.SNtruP761X25519Sha512OpenSsh,
                SshAlgorithmNames.Curve25519Sha256,
                SshAlgorithmNames.Curve25519Sha256LibSsh,
                SshAlgorithmNames.EcdhSha2Nistp256,
                SshAlgorithmNames.EcdhSha2Nistp384,
                SshAlgorithmNames.EcdhSha2Nistp521,
                SshAlgorithmNames.DiffieHellmanGroup16Sha512,
                SshAlgorithmNames.DiffieHellmanGroup14Sha256,
            ],
            HostKey =
            [
                SshAlgorithmNames.SshEd25519,
                SshAlgorithmNames.EcdsaSha2Nistp256,
                SshAlgorithmNames.EcdsaSha2Nistp384,
                SshAlgorithmNames.EcdsaSha2Nistp521,
                SshAlgorithmNames.RsaSha512,
                SshAlgorithmNames.RsaSha256,

                // 主机证书排在普通算法之后（velashell-docs/zh/ssh/spec/03 §5.5）：没给这台主机配 CA 时，
                // 谈成证书得不到任何额外的保证，排在后面就保证这类连接与以前完全一样。
                // known_hosts 里有对上的 @cert-authority 时，IHostKeyTypePreference 会把它们提到前面。
                // 不含 ssh-rsa-cert-v01（SHA-1）。
                SshAlgorithmNames.SshEd25519CertV01,
                SshAlgorithmNames.EcdsaSha2Nistp256CertV01,
                SshAlgorithmNames.EcdsaSha2Nistp384CertV01,
                SshAlgorithmNames.EcdsaSha2Nistp521CertV01,
                SshAlgorithmNames.RsaSha512CertV01,
                SshAlgorithmNames.RsaSha256CertV01,
            ],
            EncryptionClientToServer = encryption,
            EncryptionServerToClient = encryption,
            MacClientToServer = mac,
            MacServerToClient = mac,
            // 默认不开压缩，与 OpenSSH 一致（velashell-docs/zh/ssh/spec/00 §6.5）。
            CompressionClientToServer = [SshAlgorithmNames.None],
            CompressionServerToClient = [SshAlgorithmNames.None],
        };
    }

    /// <summary>
    /// 在现有清单之后**追加**老算法，用于连接不支持现代算法的设备。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 追加而不是前置：只有在对端一个现代算法都不支持时才会落到这些上。
    /// </para>
    /// <para>
    /// ⚠️ 放开的是 <c>diffie-hellman-group14-sha1</c>、SHA-1 的 <c>ssh-rsa</c> 主机密钥与 <c>hmac-sha1</c>。
    /// 这些算法今天都不该用 —— 但「连不上那台交换机」对运维是一个真实的、
    /// 每天都在发生的问题，而把它做成一个显式开关，好过让人去别处找一个更差的工具。
    /// </para>
    /// <para>
    /// <b>不含 CBC</b>：本库没有实现 CBC 模式。把没实现的名字报给对端，只会在一台
    /// 只剩 CBC 的设备上「谈成」它，然后在派生密钥时才失败 —— 那比一句
    /// 「没有共同的加密算法」难懂得多。
    /// </para>
    /// </remarks>
    public SshAlgorithmSet WithLegacyInterop() => this with
    {
        KeyExchange = [.. KeyExchange, SshAlgorithmNames.DiffieHellmanGroup14Sha1],
        HostKey = [.. HostKey, SshAlgorithmNames.SshRsa],
        MacClientToServer = [.. MacClientToServer, SshAlgorithmNames.HmacSha1Etm, SshAlgorithmNames.HmacSha1],
        MacServerToClient = [.. MacServerToClient, SshAlgorithmNames.HmacSha1Etm, SshAlgorithmNames.HmacSha1],
    };

    /// <summary>启用压缩（<c>zlib@openssh.com</c>，认证后才开始压缩）。</summary>
    public SshAlgorithmSet WithCompression() => this with
    {
        CompressionClientToServer = [SshAlgorithmNames.ZlibOpenSsh, SshAlgorithmNames.None],
        CompressionServerToClient = [SshAlgorithmNames.ZlibOpenSsh, SshAlgorithmNames.None],
    };

    /// <summary>把这些密钥类型的主机密钥算法排到前面，其余保持原有的相对顺序。</summary>
    /// <param name="keyTypes">已经记下的密钥类型（<c>ssh-ed25519</c>、<c>ssh-rsa</c>…）。</param>
    /// <remarks>
    /// 协商以客户端的顺序为准，所以这就决定了能谈成已知类型时一定谈成它（见 <c>IHostKeyTypePreference</c>）。
    /// 只调顺序，不增删 —— 清单里没有的算法不会因此被加进来。
    /// </remarks>
    public SshAlgorithmSet PreferHostKeyTypes(IReadOnlyCollection<string> keyTypes)
    {
        ArgumentNullException.ThrowIfNull(keyTypes);
        if (keyTypes.Count == 0)
        {
            return this;
        }

        bool IsKnown(string algorithm) => keyTypes.Contains(KeyTypeOf(algorithm), StringComparer.Ordinal);
        return this with { HostKey = [.. HostKey.Where(IsKnown), .. HostKey.Where(a => !IsKnown(a))] };
    }

    /// <summary>校验清单里的密钥交换、加密、MAC 与压缩算法都是本库实现（或注册）了的。</summary>
    /// <exception cref="ArgumentException">某个类别为空，或含有本库不实现的算法名。</exception>
    /// <remarks>
    /// 连接开始前就查：清单里混进一个没实现的名字，只有对端恰好也只剩它时才会被谈成，
    /// 那时失败在密钥派生里，报出来的是一句看不出缘由的「尚未实现」，
    /// 而且只在连某一台设备时出现。提前在这里报，错误指向的是配置本身。
    /// 主机密钥算法不在这里查：它由主机密钥的解析与验签把关，未知类型在那里有明确的错误。
    /// </remarks>
    public void Validate()
    {
        Check(KeyExchange, nameof(KeyExchange),
            static n => SshKeyExchangeFactory.IsSupported(n) || SshAlgorithmNegotiator.IsIndicator(n));
        Check(HostKey, nameof(HostKey), static _ => true);
        Check(EncryptionClientToServer, nameof(EncryptionClientToServer), SshSessionKeys.IsSupportedEncryption);
        Check(EncryptionServerToClient, nameof(EncryptionServerToClient), SshSessionKeys.IsSupportedEncryption);
        Check(MacClientToServer, nameof(MacClientToServer), SshSessionKeys.IsSupportedMac);
        Check(MacServerToClient, nameof(MacServerToClient), SshSessionKeys.IsSupportedMac);
        Check(CompressionClientToServer, nameof(CompressionClientToServer), IsSupportedCompression);
        Check(CompressionServerToClient, nameof(CompressionServerToClient), IsSupportedCompression);

        static bool IsSupportedCompression(string name) =>
            name is SshAlgorithmNames.None or SshAlgorithmNames.ZlibOpenSsh;

        static void Check(IReadOnlyList<string> names, string category, Func<string, bool> isSupported)
        {
            if (names is null || names.Count == 0)
            {
                throw new ArgumentException($"算法清单的 {category} 不能为空 —— 空清单与任何对端都谈不成。", category);
            }

            foreach (string name in names)
            {
                if (!isSupported(name))
                {
                    throw new ArgumentException($"算法清单的 {category} 含有本库未实现的算法：{name}。", category);
                }
            }
        }
    }

    /// <summary>主机密钥算法对应的密钥类型：三个 RSA 签名算法都是 <c>ssh-rsa</c>，其余同名。</summary>
    internal static string KeyTypeOf(string hostKeyAlgorithm) => hostKeyAlgorithm switch
    {
        SshAlgorithmNames.RsaSha256 or SshAlgorithmNames.RsaSha512 => SshAlgorithmNames.SshRsa,

        // 证书同理：rsa-sha2-512-cert-v01 的 blob 里写的是 ssh-rsa-cert-v01。
        SshAlgorithmNames.RsaSha256CertV01 or SshAlgorithmNames.RsaSha512CertV01 => SshAlgorithmNames.SshRsaCertV01,
        _ => hostKeyAlgorithm,
    };
}
