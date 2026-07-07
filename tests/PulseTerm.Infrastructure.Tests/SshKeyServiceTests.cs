using PulseTerm.Infrastructure.Ssh;

namespace PulseTerm.Infrastructure.Tests;

[TestClass]
public sealed class SshKeyServiceTests : IDisposable
{
    private readonly string _sshDir;
    private readonly SshKeyService _service;

    public SshKeyServiceTests()
    {
        _sshDir = Path.Combine(Path.GetTempPath(), $"pulseterm_sshkeys_{Guid.NewGuid():N}");
        _service = new SshKeyService(_sshDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_sshDir))
        {
            Directory.Delete(_sshDir, true);
        }
    }

    [TestMethod]
    public async Task Generate_List_Delete_RoundTrips()
    {
        var generated = await _service.GenerateRsaKeyAsync("test_key", bits: 2048);

        Assert.AreEqual("test_key", generated.Name);
        Assert.AreEqual("RSA 2048", generated.Type);
        StringAssert.StartsWith(generated.Fingerprint, "SHA256:");
        Assert.IsTrue(File.Exists(Path.Combine(_sshDir, "test_key")));
        Assert.IsTrue(File.Exists(Path.Combine(_sshDir, "test_key.pub")));

        var listed = await _service.ListKeysAsync();
        Assert.AreEqual(1, listed.Count);
        Assert.AreEqual(generated.Fingerprint, listed[0].Fingerprint, "列举解析的指纹应与生成时一致");
        Assert.AreEqual("RSA 2048", listed[0].Type, "公钥 blob 解析出的位数应一致");
        StringAssert.StartsWith(listed[0].PublicKeyLine, "ssh-rsa ");

        await _service.DeleteKeyAsync("test_key");
        Assert.AreEqual(0, (await _service.ListKeysAsync()).Count);
    }

    [TestMethod]
    public async Task Generate_DuplicateName_Throws()
    {
        await _service.GenerateRsaKeyAsync("dup", bits: 2048);
        await Assert.ThrowsExactlyAsync<IOException>(() => _service.GenerateRsaKeyAsync("dup", bits: 2048));
    }

    [TestMethod]
    public async Task List_EmptyDirectory_ReturnsEmpty()
    {
        Assert.AreEqual(0, (await _service.ListKeysAsync()).Count);
    }
}
