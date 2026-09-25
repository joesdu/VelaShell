// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH PROTOCOL.certkeys —— 认证请求出示整张证书,签名仍由被签发的那把私钥出,
//                                且签名 blob 里写的是**普通**算法名
//   RFC 4252 §7               —— publickey 请求里「公钥算法名 + 公钥 blob」两个字段
//   行为规格: velashell-docs/zh/ssh/design/architecture.md §8 第 5 项

using VelaShell.Ssh.Auth;
using VelaShell.Ssh.HostKeys;

namespace VelaShell.Ssh.Keys;

/// <summary>
/// 拿证书去认证:<b>出示整张证书,签名仍由私钥出。</b>
/// </summary>
/// <remarks>
/// <para>
/// 证书认证没有单独的认证方法 —— 它走的还是 <c>publickey</c>,只是请求里那个
/// 「公钥 blob」字段装的是整张证书,「公钥算法名」字段是
/// <c>*-cert-v01@openssh.com</c>。而签名 blob 里写的仍然是普通算法名
/// (<c>ssh-ed25519</c> / <c>rsa-sha2-512</c>),因为签名本来就是那把普通私钥出的。
/// </para>
/// <para>
/// 这层不对称整个落在本类型里,认证器一行都不用改 —— 它只管
/// 「用 <c>Signer.PublicKey.Blob</c> 去出示、用挑出来的算法名去签」。
/// </para>
/// <para>
/// 私钥从哪来仍由内层的 <see cref="ISshSigner" /> 决定:文件、ssh-agent、
/// PKCS#11、HSM 都行,证书不改变这一点。
/// </para>
/// </remarks>
public sealed class SshCertificateSigner : ISshSigner
{
    private readonly ISshSigner _inner;

    private SshCertificateSigner(ISshSigner inner, OpenSshCertificate certificate, SshPublicKey publicKey)
    {
        _inner = inner;
        Certificate = certificate;
        PublicKey = publicKey;
    }

    /// <summary>被出示的那张证书。</summary>
    public OpenSshCertificate Certificate { get; }

    /// <summary>
    /// 认证时出示的「公钥」—— 它的 <see cref="SshPublicKey.Blob" /> 是整张证书。
    /// </summary>
    public SshPublicKey PublicKey { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> SignatureAlgorithms => PublicKey.SignatureAlgorithms;

    /// <inheritdoc />
    public bool IsLocalAndCheap => _inner.IsLocalAndCheap;

    /// <summary>
    /// 把一张证书与它对应的私钥配成一个签名器。
    /// </summary>
    /// <param name="certificate">证书。</param>
    /// <param name="signer">被签发的那把私钥的签名器。</param>
    /// <returns>可直接交给 <see cref="PublicKeyCredential" /> 的签名器。</returns>
    /// <exception cref="SshCertificateException">
    /// 证书不是用户证书,或者它里面的公钥与 <paramref name="signer" /> 的不是同一把。
    /// </exception>
    /// <remarks>
    /// <b>这里当场核对「证书与私钥是不是一对」。</b>不核对的话,配错了的表现是
    /// 服务端一句 <c>Permission denied (publickey)</c> —— 而那句话与「CA 不被信任」
    /// 「主体不匹配」「证书过期」长得一模一样,用户根本无从下手。
    /// 核对是本地一次字节比较,代价为零。
    /// </remarks>
    public static SshCertificateSigner Create(OpenSshCertificate certificate, ISshSigner signer)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(signer);

        if (certificate.CertificateType != SshCertificateType.User)
        {
            throw new SshCertificateException(
                $"这是一张**主机**证书({certificate.KeyId}),不能拿来登录。" +
                "用户证书是 ssh-keygen 签发时不带 -h 的那一种。");
        }

        if (!certificate.Key.Blob.Span.SequenceEqual(signer.PublicKey.Blob.Span))
        {
            throw new SshCertificateException(
                $"证书({certificate.KeyId})里的公钥与这把私钥不是一对:" + Environment.NewLine +
                $"  证书里的是 {certificate.Key.KeyType} {certificate.Key.Sha256Fingerprint}" + Environment.NewLine +
                $"  私钥这边是 {signer.PublicKey.KeyType} {signer.PublicKey.Sha256Fingerprint}" + Environment.NewLine +
                "证书要与**签发时用的那把**私钥一起用。");
        }

        var presented = SshPublicKey.ForCertificate(
            certificate.Key, certificate.Algorithm, certificate.Blob.ToArray(), certificate);

        return new SshCertificateSigner(signer, certificate, presented);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="algorithm" /> 进来时带着 <c>-cert-v01@openssh.com</c> 后缀
    /// (认证器是从 <see cref="SignatureAlgorithms" /> 里挑的),这里把它去掉再交给内层 ——
    /// 签名本身与证书无关。
    /// </remarks>
    public ValueTask<byte[]> SignAsync(
        ReadOnlyMemory<byte> data, string algorithm, CancellationToken cancellationToken = default) =>
        _inner.SignAsync(data, SshPublicKey.StripCertificateSuffix(algorithm), cancellationToken);
}
