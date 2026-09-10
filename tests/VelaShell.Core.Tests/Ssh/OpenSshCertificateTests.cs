using VelaShell.Core.Ssh;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// <see cref="OpenSshCertificate.InferPrivateKeyPath" />:由证书路径反推配对私钥的那条 OpenSSH 命名约定。
/// </summary>
/// <remarks>
/// 推断只是个填表便利,但推错的代价不小:填进去一把不匹配的私钥,连接会以一句笼统的
/// publickey 被拒收场,用户看不出是路径的问题。所以宁可推不出来,也不能推出个错的。
/// </remarks>
[TestClass]
[TestCategory("Ssh")]
public sealed class OpenSshCertificateTests
{
    /// <summary>只认这一个存在的私钥,别的一律当不存在 —— 免得用例真去碰磁盘。</summary>
    private static Func<string, bool> Only(string existing) =>
        path => string.Equals(path, existing, StringComparison.Ordinal);

    [TestMethod]
    public void InferPrivateKeyPath_OnConventionalPair_ReturnsKeyPath()
    {
        const string key = "/home/user/.ssh/id_ed25519";
        Assert.AreEqual(key, OpenSshCertificate.InferPrivateKeyPath($"{key}-cert.pub", Only(key)));
    }

    [TestMethod]
    public void InferPrivateKeyPath_OnWindowsPath_ReturnsKeyPath()
    {
        const string key = @"C:\Users\ops\.ssh\id_rsa";
        Assert.AreEqual(key, OpenSshCertificate.InferPrivateKeyPath($"{key}-cert.pub", Only(key)));
    }

    [TestMethod]
    public void InferPrivateKeyPath_WhenKeyFileAbsent_ReturnsNull()
    {
        // 证书旁边没有同名私钥:多半是用户只拷了证书过来。填一个不存在的路径进去
        // 只会把"没选私钥"变成"选了个坏路径",后者更难查。
        Assert.IsNull(OpenSshCertificate.InferPrivateKeyPath("/home/user/.ssh/id_ed25519-cert.pub", _ => false));
    }

    [TestMethod]
    public void InferPrivateKeyPath_OnCustomName_ReturnsNull()
    {
        // 不按 ssh-keygen 的后缀命名就推不出来:去掉 ".pub" 得到的不是私钥,而是公钥。
        Assert.IsNull(OpenSshCertificate.InferPrivateKeyPath("/home/user/.ssh/id_ed25519.pub", _ => true));
        Assert.IsNull(OpenSshCertificate.InferPrivateKeyPath("/home/user/.ssh/prod-cert", _ => true));
    }

    [TestMethod]
    public void InferPrivateKeyPath_OnBareSuffix_ReturnsNull()
    {
        // 文件名整个就是后缀:剥掉之后剩空串,那不是路径。
        Assert.IsNull(OpenSshCertificate.InferPrivateKeyPath("-cert.pub", _ => true));
    }

    [TestMethod]
    public void InferPrivateKeyPath_OnEmptyInput_ReturnsNull()
    {
        Assert.IsNull(OpenSshCertificate.InferPrivateKeyPath(null, _ => true));
        Assert.IsNull(OpenSshCertificate.InferPrivateKeyPath("   ", _ => true));
    }

    [TestMethod]
    public void InferPrivateKeyPath_IsCaseInsensitiveOnSuffix()
    {
        // Windows 上文件名大小写不敏感,用户手敲出 -CERT.PUB 完全可能。
        const string key = @"C:\keys\id_ecdsa";
        Assert.AreEqual(key, OpenSshCertificate.InferPrivateKeyPath($"{key}-CERT.PUB", Only(key)));
    }
}
