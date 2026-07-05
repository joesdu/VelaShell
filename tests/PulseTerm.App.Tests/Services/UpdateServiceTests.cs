using PulseTerm.App.Services;
using Velopack.Locators;

namespace PulseTerm.App.Tests.Services;

[TestClass]
public class UpdateServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TestVelopackLocator _locator;

    public UpdateServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"pulseterm_update_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        _locator = new TestVelopackLocator("com.pulseterm.test", "1.0.0", _tempDir, null);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [TestMethod]
    [TestCategory("Update")]
    public void CurrentVersion_WhenNotInstalled_ReturnsAssemblyVersion()
    {
        var service = new UpdateService("https://example.com/updates", _locator);

        Assert.IsFalse(string.IsNullOrEmpty(service.CurrentVersion));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void AvailableVersion_Initially_IsNull()
    {
        var service = new UpdateService("https://example.com/updates", _locator);

        Assert.IsNull(service.AvailableVersion);
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task CheckForUpdateAsync_WhenNetworkUnavailable_ReturnsFalse()
    {
        var service = new UpdateService("https://invalid.test.example.com/updates", _locator);

        var result = await service.CheckForUpdateAsync();

        Assert.IsFalse(result);
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadUpdateAsync_WhenNoUpdateAvailable_CompletesWithoutError()
    {
        var service = new UpdateService("https://example.com/updates", _locator);

        await service.DownloadUpdateAsync();
    }

    [TestMethod]
    [TestCategory("Update")]
    public void ApplyUpdateAndRestart_WhenNoUpdateAvailable_ThrowsInvalidOperation()
    {
        var service = new UpdateService("https://example.com/updates", _locator);

        var act = () => service.ApplyUpdateAndRestart();

        Assert.ThrowsExactly<InvalidOperationException>(act);
    }

    [TestMethod]
    [TestCategory("Update")]
    public void Constructor_WithNullUrl_ThrowsArgumentNullException()
    {
        var act = () => new UpdateService(null!, _locator);

        Assert.ThrowsExactly<ArgumentNullException>(act);
    }

    [TestMethod]
    [TestCategory("Update")]
    public void ImplementsIUpdateService()
    {
        var service = new UpdateService("https://example.com/updates", _locator);

        Assert.IsInstanceOfType(service, typeof(IUpdateService));
    }
}
