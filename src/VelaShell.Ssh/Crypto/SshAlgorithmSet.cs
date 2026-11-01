// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §7.1  协商规则:取客户端列表中第一个双方都支持的
//   行为规格:      velashell-docs/zh/ssh/spec/00-overview.md §6(总表)、§7(优先级);velashell-docs/zh/ssh/spec/03 §2.2

using System.Collections.ObjectModel;
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
/// <para>
/// 〔velashell-docs/zh/ssh/spec/03 §2.2〕<b>每条清单在设值时抄一份只读的存下来</b>，之后调用方改自己手里那份不影响这里。
/// 曾经原样存下调用方给的集合：传一个 <c>List</c> 进来、<c>Validate()</c> 之后再改，连接把这份清单存进重协商的上下文，
/// 下一次重协商用的就是改过的、没校验过的清单；<see cref="Default"/> 里的数组下转型就能改，加密与 MAC 两个方向还共用同一个数组。
/// </para>
/// </remarks>
public sealed record SshAlgorithmSet
{
    /// <summary>密钥交换算法，按偏好排序。</summary>
    /// <exception cref="ArgumentNullException">设成 <see langword="null"/>。</exception>
    public required IReadOnlyList<string> KeyExchange { get; init => field = Freeze(value, nameof(KeyExchange)); }

    /// <summary>主机密钥算法，按偏好排序。</summary>
    /// <exception cref="ArgumentNullException">设成 <see langword="null"/>。</exception>
    public required IReadOnlyList<string> HostKey { get; init => field = Freeze(value, nameof(HostKey)); }

    /// <summary>加密算法（客户端 → 服务端）。</summary>
    /// <exception cref="ArgumentNullException">设成 <see langword="null"/>。</exception>
    public required IReadOnlyList<string> EncryptionClientToServer { get; init => field = Freeze(value, nameof(EncryptionClientToServer)); }

    /// <summary>加密算法（服务端 → 客户端）。</summary>
    /// <exception cref="ArgumentNullException">设成 <see langword="null"/>。</exception>
    public required IReadOnlyList<string> EncryptionServerToClient { get; init => field = Freeze(value, nameof(EncryptionServerToClient)); }

    /// <summary>MAC 算法（客户端 → 服务端）。AEAD 加密下协商结果被忽略。</summary>
    /// <exception cref="ArgumentNullException">设成 <see langword="null"/>。</exception>
    public required IReadOnlyList<string> MacClientToServer { get; init => field = Freeze(value, nameof(MacClientToServer)); }

    /// <summary>MAC 算法（服务端 → 客户端）。</summary>
    /// <exception cref="ArgumentNullException">设成 <see langword="null"/>。</exception>
    public required IReadOnlyList<string> MacServerToClient { get; init => field = Freeze(value, nameof(MacServerToClient)); }

    /// <summary>压缩算法（客户端 → 服务端）。</summary>
    /// <exception cref="ArgumentNullException">设成 <see langword="null"/>。</exception>
    public required IReadOnlyList<string> CompressionClientToServer { get; init => field = Freeze(value, nameof(CompressionClientToServer)); }

    /// <summary>压缩算法（服务端 → 客户端）。</summary>
    /// <exception cref="ArgumentNullException">设成 <see langword="null"/>。</exception>
    public required IReadOnlyList<string> CompressionServerToClient { get; init => field = Freeze(value, nameof(CompressionServerToClient)); }

    /// <summary>抄一份只读的：之后调用方改它手里那份，这里不跟着变。</summary>
    private static ReadOnlyCollection<string> Freeze(IReadOnlyList<string> value, string name) =>
        value is null ? throw new ArgumentNullException(name) : Array.AsReadOnly([.. value]);

    /// <summary>逐类、按顺序比较清单的内容。</summary>
    /// <remarks>
    /// 记录默认按成员的引用比较，而清单设值时会抄一份 —— 内容一样的两个清单引用不同，按引用比较就说不通了。
    /// 顺序也算在内：顺序就是偏好。
    /// </remarks>
    public bool Equals(SshAlgorithmSet? other) =>
        ReferenceEquals(this, other)
        || (other is not null
            && KeyExchange.SequenceEqual(other.KeyExchange)
            && HostKey.SequenceEqual(other.HostKey)
            && EncryptionClientToServer.SequenceEqual(other.EncryptionClientToServer)
            && EncryptionServerToClient.SequenceEqual(other.EncryptionServerToClient)
            && MacClientToServer.SequenceEqual(other.MacClientToServer)
            && MacServerToClient.SequenceEqual(other.MacServerToClient)
            && CompressionClientToServer.SequenceEqual(other.CompressionClientToServer)
            && CompressionServerToClient.SequenceEqual(other.CompressionServerToClient));

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach (IReadOnlyList<string> list in (IReadOnlyList<string>[])[
            KeyExchange, HostKey, EncryptionClientToServer, EncryptionServerToClient,
            MacClientToServer, MacServerToClient, CompressionClientToServer, CompressionServerToClient])
        {
            hash.Add(list.Count);
            foreach (string name in list)
            {
                hash.Add(name, StringComparer.Ordinal);
            }
        }
        return hash.ToHashCode();
    }

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

                // 〔velashell-docs/zh/ssh/spec/00 §6.1〕面向 FIPS 的两种排在 X25519 那种之后（三种都给的服务端照旧谈成它）、
                // sntrup761 与不带后量子的椭圆曲线之前：开了 FIPS 策略的服务端不给 X25519，没有它们就只剩 ecdh-sha2-nistp256。
                SshAlgorithmNames.MlKem768Nistp256Sha256,
                SshAlgorithmNames.MlKem1024Nistp384Sha384,
                SshAlgorithmNames.Sntrup761X25519Sha512,
                SshAlgorithmNames.Sntrup761X25519Sha512OpenSsh,
                SshAlgorithmNames.Curve25519Sha256,
                SshAlgorithmNames.Curve25519Sha256LibSsh,
                SshAlgorithmNames.EcdhSha2Nistp256,
                SshAlgorithmNames.EcdhSha2Nistp384,
                SshAlgorithmNames.EcdhSha2Nistp521,

                // 群交换排在 DH 标准群之前、椭圆曲线之后：别的都谈不成时它才会被选中（客户端的顺序为准），
                // 而只开它的服务端（老设备、加固过的）原来直接连不上。
                SshAlgorithmNames.DiffieHellmanGroupExchangeSha256,
                SshAlgorithmNames.DiffieHellmanGroup16Sha512,

                // 8192 位排在 4096 位之后：两种都给的服务端谈成 group16 —— 4096 位已经够强，8192 位的模幂要慢上好几倍；
                // 服务端与我们共有的只剩它时才会选中它。
                SshAlgorithmNames.DiffieHellmanGroup18Sha512,
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
    /// 只含 FIPS 认可算法的清单：密钥交换只用命名的曲线与群，主机密钥与 MAC 和 RHEL 10.2 的 FIPS 加密策略给 OpenSSH 放行的是同一组。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 〔velashell-docs/zh/ssh/spec/00 §6.6〕合规环境要求客户端也只谈认可的算法时用它：密钥交换先是两种面向 FIPS 的后量子混合
    /// （<c>mlkem768nistp256-sha256</c>、<c>mlkem1024nistp384-sha384</c>），再是 NIST 曲线的 ECDH 与 DH 标准群
    /// （group 16 / 18 / 14，RFC 3526 的 MODP 群）；主机密钥只用 ECDSA 与 SHA-2 的 RSA（含对应的证书），加密只用 AES（GCM 与 CTR），MAC 只用 HMAC-SHA2。
    /// 不含 X25519、Ed25519、ChaCha20-Poly1305，也不含带 X25519 或 sntrup761 的后量子混合。
    /// </para>
    /// <para>
    /// 〔决策〕<b>不含群交换</b>（<c>diffie-hellman-group-exchange-sha256</c>，RFC 4419）：群是服务端现给的素数，
    /// 本库只核它的位数、<c>g</c> 的范围与素性（spec/03 §3.5），认不出它是不是某个命名的群 —— 严格说它不在 FIPS 认可的命名群之内。
    /// 只给群交换的服务端用这份清单连不上，报协商失败；要连就自己组清单把它加回去。维护者 2026-10-31 定。
    /// 〔历史〕§251 曾为与 RHEL 一致把它补进来（RHEL 10.2 的 FIPS 策略开着它）。
    /// </para>
    /// <para>
    /// 与 RHEL 10.2 的 FIPS 策略（crypto-policies 给 OpenSSH 客户端、服务端的两份清单）比：密钥交换少一个群交换（上一条），
    /// 加密多一个 <c>aes192-ctr</c>（AES-192 同样是 FIPS 197 认可的，RHEL 的策略没开它），其余是同一组；
    /// 顺序按本库的偏好排（与默认清单同一套规则：GCM 在 CTR 前、group16 在 group18 前、group14 最后），
    /// 协商以客户端的顺序为准，所以不照抄策略的顺序。〔历史〕曾经少了 group18，注释却说「与 RHEL 一致」。
    /// </para>
    /// <para>
    /// 两种混合排最前：它们只用 FIPS 认可的原语（ML-KEM、P-256 / P-384 上的 ECDH、SHA-2），不给 X25519 的服务端上，
    /// 没有它们就只剩不带后量子的 ECDH。不支持它们的服务端照旧谈成 <c>ecdh-sha2-nistp256</c>。
    /// 〔历史〕最初不含这两种：那时还没有能对照验证线上格式的服务端（velashell-docs/zh/ssh/spec/03 §3.7）。
    /// </para>
    /// <para>
    /// ⚠️ <b>这只限定了算法，不等于本库通过了 FIPS 140 验证。</b>AES、SHA-2、ECDH、ECDSA、RSA 走 BCL
    /// （Windows 上是经过验证的 CNG，Linux / macOS 上取决于系统的 OpenSSL 与是否开了 FIPS 模式），
    /// ML-KEM 在平台支持时走 BCL、否则与 DH 标准群的模幂一样走 BouncyCastle。
    /// </para>
    /// </remarks>
    public static SshAlgorithmSet FipsApprovedOnly { get; } = CreateFipsApprovedOnly();

    private static SshAlgorithmSet CreateFipsApprovedOnly()
    {
        string[] encryption =
        [
            SshAlgorithmNames.Aes256Gcm,
            SshAlgorithmNames.Aes128Gcm,
            SshAlgorithmNames.Aes256Ctr,
            SshAlgorithmNames.Aes192Ctr,
            SshAlgorithmNames.Aes128Ctr,
        ];
        string[] mac =
        [
            SshAlgorithmNames.HmacSha256Etm,
            SshAlgorithmNames.HmacSha512Etm,
            SshAlgorithmNames.HmacSha256,
            SshAlgorithmNames.HmacSha512,
        ];
        return new SshAlgorithmSet
        {
            KeyExchange =
            [
                SshAlgorithmNames.MlKem768Nistp256Sha256,
                SshAlgorithmNames.MlKem1024Nistp384Sha384,
                SshAlgorithmNames.EcdhSha2Nistp256,
                SshAlgorithmNames.EcdhSha2Nistp384,
                SshAlgorithmNames.EcdhSha2Nistp521,

                // 不含群交换：见属性上的说明。
                SshAlgorithmNames.DiffieHellmanGroup16Sha512,
                SshAlgorithmNames.DiffieHellmanGroup18Sha512,
                SshAlgorithmNames.DiffieHellmanGroup14Sha256,
            ],
            HostKey =
            [
                SshAlgorithmNames.EcdsaSha2Nistp256,
                SshAlgorithmNames.EcdsaSha2Nistp384,
                SshAlgorithmNames.EcdsaSha2Nistp521,
                SshAlgorithmNames.RsaSha512,
                SshAlgorithmNames.RsaSha256,
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

    /// <summary>校验清单里的密钥交换、加密、MAC 与压缩算法都是本库实现了的。</summary>
    /// <exception cref="ArgumentException">某个类别为空，或含有本库不实现的算法名。</exception>
    /// <remarks>
    /// 连接开始前就查：清单里混进一个没实现的名字，只有对端恰好也只剩它时才会被谈成，
    /// 那时失败在密钥派生里，报出来的是一句看不出缘由的「尚未实现」，
    /// 而且只在连某一台设备时出现。提前在这里报，错误指向的是配置本身。
    /// 主机密钥算法不在这里查：它由主机密钥的解析与验签把关，未知类型在那里有明确的错误。
    /// <c>internal</c>：清单由几条 <c>init</c> 属性与 <c>With*</c> 链拼成，做不到构造时校验；
    /// 唯一消费它的入口 <c>SshConnection.ConnectAsync</c> 在拨号前代为调用，调用方不必记得。
    /// </remarks>
    internal void Validate()
    {
        Check(KeyExchange, nameof(KeyExchange),
            static n => SshKeyExchangeFactory.IsSupported(n) || SshAlgorithmNegotiator.IsIndicator(n));

        // 〔velashell-docs/zh/ssh/spec/03 §2.3〕指示符（ext-info-c、kex-strict-c-v00@openssh.com 之类）不是算法，
        // 由编码 KEXINIT 时按需加在首次交换里。写进清单的话它会出现在每一个 KEXINIT 里，包括重协商 ——
        // ext-info-c 在重协商里再出现是协议违规（RFC 8308 §2.2）。曾经这里放行。
        if (KeyExchange.FirstOrDefault(SshAlgorithmNegotiator.IsIndicator) is { } indicator)
        {
            throw new ArgumentException(
                $"算法清单的 KeyExchange 里不要写指示符 {indicator}：它不是算法，库会在首次 KEXINIT 里自己加上。",
                nameof(KeyExchange));
        }
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
