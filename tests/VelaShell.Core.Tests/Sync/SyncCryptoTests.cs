using System.Security.Cryptography;
using VelaShell.Core.Sync;

namespace VelaShell.Core.Tests.Sync;

[TestClass]
public class SyncCryptoTests
{
    [TestMethod]
    [TestCategory("Sync")]
    public void EncryptDecrypt_RoundTrips()
    {
        const string plaintext = """{"settings":{"language":"zh-CN"},"值":"中文与 emoji 🚀"}""";

        string blob = SyncCrypto.Encrypt(plaintext, "correct horse battery staple");

        Assert.AreNotEqual(plaintext, blob);
        Assert.AreEqual(plaintext, SyncCrypto.Decrypt(blob, "correct horse battery staple"));
    }

    [TestMethod]
    [TestCategory("Sync")]
    public void Decrypt_WrongPassphrase_Throws()
    {
        string blob = SyncCrypto.Encrypt("secret payload", "right-passphrase");

        Assert.ThrowsExactly<AuthenticationTagMismatchException>(() => SyncCrypto.Decrypt(blob, "wrong-passphrase"));
    }

    [TestMethod]
    [TestCategory("Sync")]
    public void Encrypt_SameInput_ProducesDifferentBlobs()
    {
        // 随机盐与随机 nonce:相同明文每次密文都不同,云端无法据此推断内容是否变化。
        string first = SyncCrypto.Encrypt("same input", "pass");
        string second = SyncCrypto.Encrypt("same input", "pass");

        Assert.AreNotEqual(first, second);
    }
}
