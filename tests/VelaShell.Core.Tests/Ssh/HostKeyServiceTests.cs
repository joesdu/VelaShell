using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;

namespace VelaShell.Core.Tests.Ssh;

[TestClass]
[TestCategory("HostKey")]
public class HostKeyServiceTests : IDisposable
{
    private readonly JsonDataStore _dataStore;
    private readonly string _knownHostsPath;
    private readonly HostKeyService _sut;
    private readonly string _testDirectory;

    public HostKeyServiceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"velashell_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
        _knownHostsPath = Path.Combine(_testDirectory, "known_hosts.json");
        _dataStore = new();
        _sut = new(_dataStore, _knownHostsPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    [TestMethod]
    [TestCategory("HostKey")]
    public async Task VerifyHostKeyAsync_UnknownHost_ReturnsUnknown()
    {
        HostKeyVerification result = await _sut.VerifyHostKeyAsync("example.com", 22, "ssh-rsa", "SHA256:abc123");
        Assert.AreEqual(HostKeyVerification.Unknown, result);
    }

    [TestMethod]
    [TestCategory("HostKey")]
    public async Task TrustHostKey_ThenVerify_ReturnsTrusted()
    {
        await _sut.TrustHostKeyAsync("example.com", 22, "ssh-rsa", "SHA256:abc123");
        HostKeyVerification result = await _sut.VerifyHostKeyAsync("example.com", 22, "ssh-rsa", "SHA256:abc123");
        Assert.AreEqual(HostKeyVerification.Trusted, result);
    }

    [TestMethod]
    [TestCategory("HostKey")]
    public async Task VerifyHostKeyAsync_FingerprintChanged_ReturnsChanged()
    {
        await _sut.TrustHostKeyAsync("example.com", 22, "ssh-rsa", "SHA256:original");
        HostKeyVerification result = await _sut.VerifyHostKeyAsync("example.com", 22, "ssh-rsa", "SHA256:different");
        Assert.AreEqual(HostKeyVerification.Changed, result);
    }

    [TestMethod]
    [TestCategory("HostKey")]
    public async Task RemoveKnownHostAsync_RemovesTrustedHost()
    {
        await _sut.TrustHostKeyAsync("example.com", 22, "ssh-rsa", "SHA256:abc123");
        await _sut.RemoveKnownHostAsync("example.com", 22);
        HostKeyVerification result = await _sut.VerifyHostKeyAsync("example.com", 22, "ssh-rsa", "SHA256:abc123");
        Assert.AreEqual(HostKeyVerification.Unknown, result);
    }

    [TestMethod]
    [TestCategory("HostKey")]
    public async Task GetKnownHostsAsync_ReturnsAllTrustedHosts()
    {
        await _sut.TrustHostKeyAsync("host1.com", 22, "ssh-rsa", "SHA256:aaa");
        await _sut.TrustHostKeyAsync("host2.com", 2222, "ssh-ed25519", "SHA256:bbb");
        List<KnownHost> hosts = await _sut.GetKnownHostsAsync();
        Assert.HasCount(2, hosts);
        Assert.Contains(h => h is { Host: "host1.com", Port: 22 }, hosts);
        Assert.Contains(h => h is { Host: "host2.com", Port: 2222 }, hosts);
    }

    [TestMethod]
    [TestCategory("HostKey")]
    public async Task TrustHostKeyAsync_SameHostDifferentPort_StoresBoth()
    {
        await _sut.TrustHostKeyAsync("example.com", 22, "ssh-rsa", "SHA256:aaa");
        await _sut.TrustHostKeyAsync("example.com", 2222, "ssh-rsa", "SHA256:bbb");
        List<KnownHost> hosts = await _sut.GetKnownHostsAsync();
        Assert.HasCount(2, hosts);
    }

    [TestMethod]
    [TestCategory("HostKey")]
    public async Task TrustHostKeyAsync_UpdatesLastSeenAt_OnRepeatedTrust()
    {
        await _sut.TrustHostKeyAsync("example.com", 22, "ssh-rsa", "SHA256:abc123");
        List<KnownHost> hostsBefore = await _sut.GetKnownHostsAsync();
        DateTime firstSeenBefore = hostsBefore.Single().FirstSeenAt;
        await Task.Delay(50);
        await _sut.TrustHostKeyAsync("example.com", 22, "ssh-rsa", "SHA256:abc123");
        List<KnownHost> hostsAfter = await _sut.GetKnownHostsAsync();
        Assert.HasCount(1, hostsAfter);
        Assert.AreEqual(firstSeenBefore, hostsAfter.Single().FirstSeenAt);
        Assert.IsGreaterThanOrEqualTo(firstSeenBefore, hostsAfter.Single().LastSeenAt);
    }

    [TestMethod]
    [TestCategory("HostKey")]
    public async Task Persistence_DataSurvivesNewServiceInstance()
    {
        await _sut.TrustHostKeyAsync("example.com", 22, "ssh-rsa", "SHA256:abc123");
        var newService = new HostKeyService(_dataStore, _knownHostsPath);
        HostKeyVerification result = await newService.VerifyHostKeyAsync("example.com", 22, "ssh-rsa", "SHA256:abc123");
        Assert.AreEqual(HostKeyVerification.Trusted, result);
    }

    [TestMethod]
    [TestCategory("HostKey")]
    public async Task RemoveKnownHostAsync_NonExistentHost_DoesNotThrow() => await _sut.RemoveKnownHostAsync("nonexistent.com", 22);
}
