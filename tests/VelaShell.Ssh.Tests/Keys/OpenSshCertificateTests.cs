// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: OpenSSH PROTOCOL.certkeys
//
// 证书样本由真 ssh-keygen 签发（见 Keys/Fixtures/README.md）——
// 自己按字段表拼一张出来，只能证明「我们的写法和我们的读法一致」。

using VelaShell.Ssh.Auth;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Tests.Keys;

[TestClass]
[TestCategory("Keys")]
public sealed class OpenSshCertificateTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Keys", "Fixtures", name);

    /// <summary>MSTest 注入的测试上下文。</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// 逐个字段对着 <c>ssh-keygen -L</c> 的输出核。
    /// </summary>
    /// <remarks>
    /// 这些字段不是摆设：<c>Serial</c> 用于吊销名单，<c>ValidBefore</c> 决定
    /// 「连不上是不是因为证书过期」，<c>CriticalOptions</c> 非空时服务端
    /// 不认识其中任何一条就会整张拒掉 —— 界面要能把这些说出来。
    /// </remarks>
    [TestMethod]
    public async Task 解析出的字段与ssh_keygen显示的一致()
    {
        OpenSshCertificate cert = await OpenSshCertificate.LoadAsync(
            FixturePath("cert-ed25519-cert.pub"), TestContext.CancellationToken);

        Assert.AreEqual("ssh-ed25519-cert-v01@openssh.com", cert.Algorithm);
        Assert.AreEqual(SshCertificateType.User, cert.CertificateType);
        Assert.AreEqual("joe@velashell", cert.KeyId);
        Assert.AreEqual(4242UL, cert.Serial);
        Assert.AreSequenceEqual(new[] { "joe", "deploy" }, [.. cert.ValidPrincipals]);
        Assert.IsEmpty(cert.CriticalOptions);
        Assert.Contains("permit-pty", [.. cert.Extensions]);
        Assert.Contains("permit-agent-forwarding", [.. cert.Extensions]);

        Assert.AreEqual(2026, cert.ValidAfterTime?.Year);
        Assert.AreEqual(2027, cert.ValidBeforeTime?.Year);

        // 被签发的那把钥就是 cert-ed25519.pub。
        Assert.AreEqual(SshAlgorithmNames.SshEd25519, cert.Key.KeyType);
        Assert.AreSequenceEqual(ReadPublicBlob("cert-ed25519.pub"), cert.Key.Blob.ToArray());

        // 签发它的 CA 就是 ca.pub。
        Assert.IsNotNull(cert.SignatureKey);
        Assert.AreSequenceEqual(ReadPublicBlob("ca.pub"), cert.SignatureKey.Blob.ToArray());
    }

    [TestMethod]
    [DataRow("cert-ed25519", SshAlgorithmNames.SshEd25519)]
    [DataRow("cert-rsa", SshAlgorithmNames.SshRsa)]
    [DataRow("cert-ecdsa", SshAlgorithmNames.EcdsaSha2Nistp256)]
    public async Task 三种密钥类型的证书都解得出被签发的那把钥(string name, string expectedKeyType)
    {
        OpenSshCertificate cert = await OpenSshCertificate.LoadAsync(
            FixturePath(name + "-cert.pub"), TestContext.CancellationToken);

        Assert.AreEqual(expectedKeyType + "-cert-v01@openssh.com", cert.Algorithm);
        Assert.AreEqual(expectedKeyType, cert.Key.KeyType);
        Assert.AreSequenceEqual(ReadPublicBlob(name + ".pub"), cert.Key.Blob.ToArray());
    }

    /// <summary>
    /// 认证时出示的是整张证书，而 <c>*-cert-v01@openssh.com</c> 才是请求里那个算法名。
    /// </summary>
    [TestMethod]
    public async Task 签名器出示整张证书而算法名带证书后缀()
    {
        SshCertificateSigner signer = await LoadCertificateSignerAsync("cert-ed25519");

        Assert.AreSequenceEqual(signer.Certificate.Blob.ToArray(), signer.PublicKey.Blob.ToArray());
        Assert.IsTrue(signer.PublicKey.IsCertificate);
        Assert.AreSequenceEqual(
            new[] { "ssh-ed25519-cert-v01@openssh.com" }, [.. signer.SignatureAlgorithms]);
    }

    /// <summary>RSA 证书要把三个签名算法各带一次后缀，顺序仍是 SHA-512 优先。</summary>
    [TestMethod]
    public async Task RSA证书的签名算法名逐个带后缀()
    {
        SshCertificateSigner signer = await LoadCertificateSignerAsync("cert-rsa");

        Assert.AreSequenceEqual(
            new[]
            {
                "rsa-sha2-512-cert-v01@openssh.com",
                "rsa-sha2-256-cert-v01@openssh.com",
                "ssh-rsa-cert-v01@openssh.com",
            }, [.. signer.SignatureAlgorithms]);
    }

    /// <summary>
    /// 签名 blob 里写的是**普通**算法名，而请求里那个字段带证书后缀 ——
    /// 验签必须容得下这处不对称，否则证书认证永远过不去。
    /// </summary>
    [TestMethod]
    public async Task 证书签名用普通算法名而验签仍然认得()
    {
        SshCertificateSigner signer = await LoadCertificateSignerAsync("cert-ed25519");

        byte[] data = "velashell-ssh 证书签名自检"u8.ToArray();
        string certAlgorithm = signer.SignatureAlgorithms[0];
        byte[] signature = await signer.SignAsync(data, certAlgorithm, TestContext.CancellationToken);

        // 签名 blob 的头一个字段是普通算法名。
        Assert.Contains("ssh-ed25519", System.Text.Encoding.ASCII.GetString(signature[4..15]));

        // 拿带后缀的名字去验也要过（后缀在比对前被去掉）。
        Assert.IsTrue(signer.PublicKey.VerifySignature(signature, data, certAlgorithm));
        Assert.IsTrue(signer.Certificate.Key.VerifySignature(signature, data, certAlgorithm));
    }

    /// <summary>主机证书不能拿来登录，而且要在本地当场说清楚。</summary>
    [TestMethod]
    public async Task 主机证书不能当用户证书用()
    {
        OpenSshCertificate cert = await OpenSshCertificate.LoadAsync(
            FixturePath("cert-host-cert.pub"), TestContext.CancellationToken);
        ISshSigner inner = await SshPrivateKeyFile.LoadAsync(
            FixturePath("cert-host"), null, TestContext.CancellationToken);

        Assert.AreEqual(SshCertificateType.Host, cert.CertificateType);

        SshCertificateException ex = Assert.ThrowsExactly<SshCertificateException>(
            () => SshCertificateSigner.Create(cert, inner));
        Assert.Contains("主机", ex.Message);
    }

    /// <summary>
    /// 证书与私钥配错了要当场报，而不是留给服务端一句 <c>Permission denied</c>。
    /// </summary>
    [TestMethod]
    public async Task 证书与私钥不配对时当场报错()
    {
        OpenSshCertificate cert = await OpenSshCertificate.LoadAsync(
            FixturePath("cert-ed25519-cert.pub"), TestContext.CancellationToken);
        InMemorySshSigner other = await SshPrivateKeyFile.LoadAsync(
            FixturePath("cert-rsa"), null, TestContext.CancellationToken);

        SshCertificateException ex = Assert.ThrowsExactly<SshCertificateException>(
            () => SshCertificateSigner.Create(cert, other));

        Assert.Contains("不是一对", ex.Message);
        // 两把钥的指纹都要摆出来 —— 不摆的话用户无从判断是证书选错了还是私钥选错了。
        Assert.Contains(cert.Key.Sha256Fingerprint, ex.Message);
        Assert.Contains(other.PublicKey.Sha256Fingerprint, ex.Message);
    }

    /// <summary>过期的证书仍然解得出来 —— 它是事实，由上层决定怎么说。</summary>
    [TestMethod]
    public async Task 过期证书解得出且有效期判定为false()
    {
        OpenSshCertificate cert = await OpenSshCertificate.LoadAsync(
            FixturePath("cert-expired-cert.pub"), TestContext.CancellationToken);

        Assert.AreEqual("expired@velashell", cert.KeyId);
        Assert.IsFalse(cert.IsTimeValid(DateTimeOffset.UtcNow), "这张证书 2020 年就过期了");
        Assert.IsTrue(cert.IsTimeValid(new DateTimeOffset(2020, 1, 1, 12, 0, 0, TimeSpan.Zero)));
    }

    /// <summary>普通公钥不是证书 —— 把它当证书解要给出说得清的错。</summary>
    [TestMethod]
    public void 普通公钥不是证书()
    {
        byte[] blob = ReadPublicBlob("cert-ed25519.pub");

        SshCertificateException ex = Assert.ThrowsExactly<SshCertificateException>(
            () => OpenSshCertificate.Decode(blob));
        Assert.Contains("-cert-v01@openssh.com", ex.Message);
    }

    /// <summary>
    /// 反过来：证书 blob 走公钥的解析口，得到的是一把完整的、带证书身份的钥，而不是解出个半成品。
    /// </summary>
    /// <remarks>
    /// 主机证书要经这个口进来（KEX 应答里的 <c>K_S</c>）。曾经这里是直接拒绝 —— 那时主机证书还没实现。
    /// 只认普通公钥的那个口（CA 公钥走它）照样拒绝证书。
    /// </remarks>
    [TestMethod]
    public async Task 证书blob走公钥解析口得到带证书身份的钥()
    {
        OpenSshCertificate cert = await OpenSshCertificate.LoadAsync(
            FixturePath("cert-ed25519-cert.pub"), TestContext.CancellationToken);

        var key = SshPublicKey.Decode(cert.Blob);

        Assert.IsTrue(key.IsCertificate);
        Assert.AreEqual(cert.Algorithm, key.KeyType);
        Assert.AreEqual(SshAlgorithmNames.SshEd25519, key.PlainKeyType);
        Assert.AreSequenceEqual(cert.Blob.ToArray(), key.Blob.ToArray(), "出示的仍是整张证书");
        Assert.AreSequenceEqual(ReadPublicBlob("cert-ed25519.pub"), key.PlainKey.Blob.ToArray(), "证书里那把钥");
        Assert.AreEqual(cert.KeyId, key.Certificate?.KeyId);
        Assert.ThrowsExactly<SshPublicKeyException>(() => SshPublicKey.DecodePlain(cert.Blob));
    }

    private static async Task<SshCertificateSigner> LoadCertificateSignerAsync(string name)
    {
        ISshSigner inner = await SshPrivateKeyFile.LoadAsync(FixturePath(name));
        OpenSshCertificate cert = await OpenSshCertificate.LoadAsync(FixturePath(name + "-cert.pub"));
        return SshCertificateSigner.Create(cert, inner);
    }

    private static byte[] ReadPublicBlob(string fileName)
    {
        string line = File.ReadAllText(FixturePath(fileName));
        return Convert.FromBase64String(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
    }
}
