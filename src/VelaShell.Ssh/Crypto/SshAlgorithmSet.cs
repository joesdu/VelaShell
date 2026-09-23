// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §7.1  协商规则:取客户端列表中第一个双方都支持的
//   行为规格:      velashell-docs/zh/ssh/spec/00-overview.md §6(总表)、§7(优先级);velashell-docs/zh/ssh/spec/03 §2.2

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
    /// ⚠️ 放开的是 <c>diffie-hellman-group14-sha1</c>、<c>hmac-sha1</c> 与 CBC。
    /// 这些算法今天都不该用 —— 但「连不上那台交换机」对运维是一个真实的、
    /// 每天都在发生的问题，而把它做成一个显式开关，好过让人去别处找一个更差的工具。
    /// </para>
    /// </remarks>
    public SshAlgorithmSet WithLegacyInterop() => this with
    {
        KeyExchange = [.. KeyExchange, SshAlgorithmNames.DiffieHellmanGroup14Sha1],
        HostKey = [.. HostKey, SshAlgorithmNames.SshRsa],
        EncryptionClientToServer = [.. EncryptionClientToServer, SshAlgorithmNames.Aes256Cbc, SshAlgorithmNames.Aes128Cbc],
        EncryptionServerToClient = [.. EncryptionServerToClient, SshAlgorithmNames.Aes256Cbc, SshAlgorithmNames.Aes128Cbc],
        MacClientToServer = [.. MacClientToServer, SshAlgorithmNames.HmacSha1Etm, SshAlgorithmNames.HmacSha1],
        MacServerToClient = [.. MacServerToClient, SshAlgorithmNames.HmacSha1Etm, SshAlgorithmNames.HmacSha1],
    };

    /// <summary>启用压缩（<c>zlib@openssh.com</c>，认证后才开始压缩）。</summary>
    public SshAlgorithmSet WithCompression() => this with
    {
        CompressionClientToServer = [SshAlgorithmNames.ZlibOpenSsh, SshAlgorithmNames.None],
        CompressionServerToClient = [SshAlgorithmNames.ZlibOpenSsh, SshAlgorithmNames.None],
    };
}
