// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §6.3/§6.4/§6.5  加密、MAC、压缩算法名
//   RFC 4253 §8              diffie-hellman-group*
//   RFC 5656 §6.2/§10.1      ecdh-sha2-* / ecdsa-sha2-*
//   RFC 8268 §3              diffie-hellman-group14-sha256 / group16-sha512
//   RFC 8308 §2.1/§3.1       ext-info-c / server-sig-algs
//   RFC 8332 §3              rsa-sha2-256 / rsa-sha2-512
//   RFC 8709 §4              ssh-ed25519
//   RFC 8731 §3              curve25519-sha256
//   RFC 5647                 AEAD_AES_*_GCM
//   OpenSSH PROTOCOL         *-etm@openssh.com、chacha20-poly1305@openssh.com、
//                            aes*-gcm@openssh.com、kex-strict-*-v00@openssh.com、
//                            sntrup761x25519-sha512
//   行为规格:                velashell-docs/zh/ssh/spec/00-overview.md §6
//
// 说明:这些字符串是协议规定的事实,任何正确的实现都必然相同 ——
// 它们落在 NOTICE.md 所说的「表达方式唯一、不受版权保护」那一类里。

namespace VelaShell.Ssh.Protocol;

/// <summary>
/// SSH 协议中各类算法的注册名。
/// </summary>
/// <remarks>
/// <para>
/// 名字**区分大小写**，协商时按字节比对，任何规范化都会导致与对端的理解产生分歧
/// （velashell-docs/zh/ssh/spec/00-overview.md §3.2）。
/// </para>
/// <para>
/// 含 <c>@</c> 的是厂商扩展（<c>name@domain</c>）；不含 <c>@</c> 的必须是 IANA 注册过的。
/// 我们不自造无域名后缀的算法名。
/// </para>
/// </remarks>
internal static class SshAlgorithmNames
{
    // ---------------------------------------------------------------- 密钥交换

    /// <summary>ML-KEM-768 与 X25519 的混合，SHA-256。后量子。</summary>
    public const string MlKem768X25519Sha256 = "mlkem768x25519-sha256";

    /// <summary>sntrup761 与 X25519 的混合，SHA-512。后量子。</summary>
    public const string Sntrup761X25519Sha512 = "sntrup761x25519-sha512";

    /// <summary><see cref="Sntrup761X25519Sha512"/> 的旧名，OpenSSH &lt; 9.9 用它。</summary>
    public const string Sntrup761X25519Sha512OpenSsh = "sntrup761x25519-sha512@openssh.com";

    /// <summary>X25519 + SHA-256。RFC 8731。</summary>
    public const string Curve25519Sha256 = "curve25519-sha256";

    /// <summary><see cref="Curve25519Sha256"/> 的旧名，两者完全相同。</summary>
    public const string Curve25519Sha256LibSsh = "curve25519-sha256@libssh.org";

    /// <summary>NIST P-256 上的 ECDH + SHA-256。</summary>
    public const string EcdhSha2Nistp256 = "ecdh-sha2-nistp256";

    /// <summary>NIST P-384 上的 ECDH + SHA-384。</summary>
    public const string EcdhSha2Nistp384 = "ecdh-sha2-nistp384";

    /// <summary>NIST P-521 上的 ECDH + SHA-512。</summary>
    public const string EcdhSha2Nistp521 = "ecdh-sha2-nistp521";

    /// <summary>MODP 2048 位群 + SHA-256。RFC 8268。</summary>
    public const string DiffieHellmanGroup14Sha256 = "diffie-hellman-group14-sha256";

    /// <summary>MODP 4096 位群 + SHA-512。RFC 8268。</summary>
    public const string DiffieHellmanGroup16Sha512 = "diffie-hellman-group16-sha512";

    /// <summary>MODP 2048 位群 + SHA-1。<b>默认关闭</b>，只为老网络设备保留。</summary>
    public const string DiffieHellmanGroup14Sha1 = "diffie-hellman-group14-sha1";

    // ------------------------------------------------- 藏在 kex 列表里的指示符

    /// <summary>
    /// 客户端宣告支持 RFC 8308 的扩展协商。
    /// </summary>
    /// <remarks>
    /// <b>不是密钥交换方法</b>，是塞在同一个列表里的标志位。
    /// RFC 8308 §2.2 要求它**只出现在第一次 KEXINIT 里** —— 重协商时再发是协议违规。
    /// </remarks>
    public const string ExtInfoClient = "ext-info-c";

    /// <summary>服务端宣告支持扩展协商。我们只读不发。</summary>
    public const string ExtInfoServer = "ext-info-s";

    /// <summary>客户端宣告支持严格 KEX（Terrapin 缓解，CVE-2023-48795）。</summary>
    public const string StrictKexClient = "kex-strict-c-v00@openssh.com";

    /// <summary>服务端宣告支持严格 KEX。</summary>
    public const string StrictKexServer = "kex-strict-s-v00@openssh.com";

    // ---------------------------------------------------------------- 主机密钥

    /// <summary>Ed25519。RFC 8709。</summary>
    public const string SshEd25519 = "ssh-ed25519";

    /// <summary>NIST P-256 上的 ECDSA。</summary>
    public const string EcdsaSha2Nistp256 = "ecdsa-sha2-nistp256";

    /// <summary>NIST P-384 上的 ECDSA。</summary>
    public const string EcdsaSha2Nistp384 = "ecdsa-sha2-nistp384";

    /// <summary>NIST P-521 上的 ECDSA。</summary>
    public const string EcdsaSha2Nistp521 = "ecdsa-sha2-nistp521";

    /// <summary>
    /// RSA 密钥**类型**名。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它同时是一个签名算法名（SHA-1 签名，已弃用）。
    /// <b>公钥 blob 里的类型串永远是它</b>，即使协商出的签名算法是
    /// <see cref="RsaSha256"/> 或 <see cref="RsaSha512"/>
    /// —— 这是 RFC 8332 引入的一处不对称（velashell-docs/zh/ssh/spec/03-key-exchange.md §5.1）。
    /// </remarks>
    public const string SshRsa = "ssh-rsa";

    /// <summary>RSA + SHA-256 签名。RFC 8332。</summary>
    public const string RsaSha256 = "rsa-sha2-256";

    /// <summary>RSA + SHA-512 签名。RFC 8332。</summary>
    public const string RsaSha512 = "rsa-sha2-512";

    /// <summary>OpenSSH 证书算法名的后缀。</summary>
    public const string CertificateSuffix = "-cert-v01@openssh.com";

    /// <summary>Ed25519 主机证书 / 用户证书（OpenSSH PROTOCOL.certkeys）。</summary>
    public const string SshEd25519CertV01 = SshEd25519 + CertificateSuffix;

    /// <summary>ECDSA P-256 证书。</summary>
    public const string EcdsaSha2Nistp256CertV01 = EcdsaSha2Nistp256 + CertificateSuffix;

    /// <summary>ECDSA P-384 证书。</summary>
    public const string EcdsaSha2Nistp384CertV01 = EcdsaSha2Nistp384 + CertificateSuffix;

    /// <summary>ECDSA P-521 证书。</summary>
    public const string EcdsaSha2Nistp521CertV01 = EcdsaSha2Nistp521 + CertificateSuffix;

    /// <summary>RSA 证书的密钥类型串（blob 里写的就是它）。</summary>
    public const string SshRsaCertV01 = SshRsa + CertificateSuffix;

    /// <summary>RSA 证书 + SHA-512 签名（RFC 8332 的证书变体）。</summary>
    public const string RsaSha512CertV01 = RsaSha512 + CertificateSuffix;

    /// <summary>RSA 证书 + SHA-256 签名。</summary>
    public const string RsaSha256CertV01 = RsaSha256 + CertificateSuffix;

    // ------------------------------------------------------------------ 加密

    /// <summary>ChaCha20-Poly1305，OpenSSH 的自定义构造（两把密钥，长度字段单独加密）。</summary>
    public const string ChaCha20Poly1305 = "chacha20-poly1305@openssh.com";

    /// <summary>AES-256-GCM。</summary>
    public const string Aes256Gcm = "aes256-gcm@openssh.com";

    /// <summary>AES-128-GCM。</summary>
    public const string Aes128Gcm = "aes128-gcm@openssh.com";

    /// <summary>AES-256-CTR。</summary>
    public const string Aes256Ctr = "aes256-ctr";

    /// <summary>AES-192-CTR。</summary>
    public const string Aes192Ctr = "aes192-ctr";

    /// <summary>AES-128-CTR。</summary>
    public const string Aes128Ctr = "aes128-ctr";

    /// <summary>AES-256-CBC。<b>本库未实现</b>：只用来辨认对端清单里的名字，不能放进 <see cref="VelaShell.Ssh.Crypto.SshAlgorithmSet"/>。</summary>
    public const string Aes256Cbc = "aes256-cbc";

    /// <summary>AES-128-CBC。<b>本库未实现</b>：同 <see cref="Aes256Cbc"/>。</summary>
    public const string Aes128Cbc = "aes128-cbc";

    // ------------------------------------------------------------------ MAC

    /// <summary>HMAC-SHA-256，Encrypt-then-MAC。</summary>
    public const string HmacSha256Etm = "hmac-sha2-256-etm@openssh.com";

    /// <summary>HMAC-SHA-512，Encrypt-then-MAC。</summary>
    public const string HmacSha512Etm = "hmac-sha2-512-etm@openssh.com";

    /// <summary>HMAC-SHA-256，MAC-then-Encrypt。</summary>
    public const string HmacSha256 = "hmac-sha2-256";

    /// <summary>HMAC-SHA-512，MAC-then-Encrypt。</summary>
    public const string HmacSha512 = "hmac-sha2-512";

    /// <summary>HMAC-SHA-1，Encrypt-then-MAC。<b>默认关闭</b>。</summary>
    public const string HmacSha1Etm = "hmac-sha1-etm@openssh.com";

    /// <summary>HMAC-SHA-1。<b>默认关闭</b>。</summary>
    public const string HmacSha1 = "hmac-sha1";

    // ------------------------------------------------------------------ 压缩

    /// <summary>不压缩。</summary>
    public const string None = "none";

    /// <summary>zlib，认证**之后**才开始压缩。本库唯一支持的压缩算法。</summary>
    /// <remarks>
    /// RFC 4253 的裸 <c>zlib</c> 不实现：它从首次 NEWKEYS 起就压，认证报文也在压缩流里，
    /// 口令的可压缩性会从密文长度漏出去（见 <see cref="Crypto.SshCompressorFactory.IsDelayed"/>）。
    /// </remarks>
    public const string ZlibOpenSsh = "zlib@openssh.com";
}
