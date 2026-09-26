using System.Security.Cryptography;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Keys;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 用户导入的**传统 PEM 私钥**必须直接能用。
/// </summary>
/// <remarks>
/// <para>
/// 这条用例守的是一个有用户反馈的缺陷:<b>用 PKCS#1 的 <c>id_rsa</c> 登不上</b>。
/// 上一版底层库的私钥解析器只认 OpenSSH 格式,其余一律判 "Unsupported format" 而
/// <b>静默跳过</b>,认证以一句 <c>skipped: publickey</c> 失败 ——
/// 用户会去反复检查服务端的 <c>authorized_keys</c>,而问题在本地。
/// </para>
/// <para>
/// 当时的对策是在连接前把它们转成 OpenSSH 格式(<c>OpenSshPrivateKey.TryConvertToOpenSsh</c>,
/// 约 170 行 + 两个 BCL 加载器)。换到 VelaShell.Ssh 之后库原生认这些格式,那段转换删掉了 ——
/// <b>于是这条用例也从「转换器把格式转对了没有」改成「库直接读得出来没有」</b>,
/// 盯的是同一个用户可见的行为。
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Ssh")]
public sealed class LegacyPrivateKeyFormatTests
{
    /// <summary>用户反馈的确切格式:<c>-----BEGIN RSA PRIVATE KEY-----</c>。</summary>
    [TestMethod]
    public void Rsa_Pkcs1_IsReadDirectly()
    {
        using var rsa = RSA.Create(2048);

        using InMemorySshSigner signer = SshPrivateKeyFile.Parse(rsa.ExportRSAPrivateKeyPem());

        Assert.AreEqual("ssh-rsa", signer.PublicKey.KeyType);
        Assert.AreEqual(2048, signer.PublicKey.KeyBits);
    }

    [TestMethod]
    public void Rsa_Pkcs8_IsReadDirectly()
    {
        using var rsa = RSA.Create(2048);

        using InMemorySshSigner signer = SshPrivateKeyFile.Parse(rsa.ExportPkcs8PrivateKeyPem());

        Assert.AreEqual("ssh-rsa", signer.PublicKey.KeyType);
    }

    [TestMethod]
    public void Rsa_EncryptedPkcs8_IsReadWithPassphrase()
    {
        using var rsa = RSA.Create(2048);
        string pem = rsa.ExportEncryptedPkcs8PrivateKeyPem(
            "s3cret",
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000));

        using InMemorySshSigner signer = SshPrivateKeyFile.Parse(pem, "s3cret");

        Assert.AreEqual("ssh-rsa", signer.PublicKey.KeyType);
    }

    /// <summary><c>-----BEGIN EC PRIVATE KEY-----</c>(SEC1)。</summary>
    [TestMethod]
    [DataRow(256, "ecdsa-sha2-nistp256")]
    [DataRow(384, "ecdsa-sha2-nistp384")]
    [DataRow(521, "ecdsa-sha2-nistp521")]
    public void Ecdsa_Sec1_IsReadDirectly(int bits, string expectedKeyType)
    {
        using var ecdsa = ECDsa.Create(bits switch
        {
            256 => ECCurve.NamedCurves.nistP256,
            384 => ECCurve.NamedCurves.nistP384,
            _ => ECCurve.NamedCurves.nistP521,
        });

        using InMemorySshSigner signer = SshPrivateKeyFile.Parse(ecdsa.ExportECPrivateKeyPem());

        Assert.AreEqual(expectedKeyType, signer.PublicKey.KeyType);
    }

    /// <summary>
    /// 口令不对时要按「口令不对」报,并把 <c>NeedsPassphrase</c> 置上。
    /// </summary>
    /// <remarks>
    /// 界面靠这个标志决定要不要再弹一次输入框。报成别的(或者干脆静默跳过)
    /// 就会把用户引向一条永远改不对的路。
    /// </remarks>
    [TestMethod]
    public void EncryptedKey_WrongPassphrase_AsksAgain()
    {
        using var rsa = RSA.Create(2048);
        string pem = rsa.ExportEncryptedPkcs8PrivateKeyPem(
            "s3cret",
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000));

        SshPrivateKeyException error =
            Assert.ThrowsExactly<SshPrivateKeyException>(() => SshPrivateKeyFile.Parse(pem, "wrong"));

        Assert.IsTrue(error.NeedsPassphrase);
    }
}
