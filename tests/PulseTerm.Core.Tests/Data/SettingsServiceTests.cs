using PulseTerm.Core.Data;
using PulseTerm.Core.Models;

namespace PulseTerm.Core.Tests.Data;

[TestClass]
[TestCategory("DataStore")]
public class SettingsServiceTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly JsonDataStore _dataStore;

    public SettingsServiceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"pulseterm_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
        _dataStore = new JsonDataStore();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    [TestMethod]
    public async Task GetSettingsAsync_FileDoesNotExist_ShouldReturnDefaultSettings()
    {
        var service = new SettingsService(_dataStore, _testDirectory);

        var settings = await service.GetSettingsAsync();

        Assert.IsNotNull(settings);
        Assert.AreEqual("en", settings.Language);
        Assert.AreEqual("dark", settings.Theme);
        Assert.AreEqual("JetBrains Mono", settings.TerminalFont);
        Assert.AreEqual(14, settings.TerminalFontSize);
        Assert.AreEqual(10000, settings.ScrollbackLines);
        Assert.AreEqual(22, settings.DefaultPort);
    }

    [TestMethod]
    public async Task SaveAndGetSettings_ShouldPersist()
    {
        var service = new SettingsService(_dataStore, _testDirectory);
        var settings = new AppSettings
        {
            Language = "zh",
            Theme = "light",
            TerminalFont = "Consolas",
            TerminalFontSize = 16,
            ScrollbackLines = 5000,
            DefaultPort = 2222
        };

        await service.SaveSettingsAsync(settings);
        var retrieved = await service.GetSettingsAsync();

        Assert.AreEqual(
            System.Text.Json.JsonSerializer.Serialize(settings),
            System.Text.Json.JsonSerializer.Serialize(retrieved));
    }

    [TestMethod]
    public async Task GetStateAsync_FileDoesNotExist_ShouldReturnDefaultState()
    {
        var service = new SettingsService(_dataStore, _testDirectory);

        var state = await service.GetStateAsync();

        Assert.IsNotNull(state);
        Assert.AreEqual(0, state.RecentConnections.Count());
        Assert.IsNull(state.WindowPosition);
        Assert.IsNull(state.WindowSize);
        Assert.IsNull(state.LastActiveTab);
    }

    [TestMethod]
    public async Task SaveAndGetState_ShouldPersist()
    {
        var service = new SettingsService(_dataStore, _testDirectory);
        var state = new AppState
        {
            RecentConnections = new List<string> { "session1", "session2", "session3" },
            WindowPosition = new WindowPosition { X = 100, Y = 200 },
            WindowSize = new WindowSize { Width = 1024, Height = 768 },
            LastActiveTab = "tab1"
        };

        await service.SaveStateAsync(state);
        var retrieved = await service.GetStateAsync();

        Assert.AreEqual(
            System.Text.Json.JsonSerializer.Serialize(state),
            System.Text.Json.JsonSerializer.Serialize(retrieved));
    }

    [TestMethod]
    public async Task SaveSettings_UpdatesExisting_ShouldOverwrite()
    {
        var service = new SettingsService(_dataStore, _testDirectory);
        var settings1 = new AppSettings { Language = "en", Theme = "dark" };
        var settings2 = new AppSettings { Language = "zh", Theme = "light" };

        await service.SaveSettingsAsync(settings1);
        await service.SaveSettingsAsync(settings2);
        var retrieved = await service.GetSettingsAsync();

        Assert.AreEqual("zh", retrieved.Language);
        Assert.AreEqual("light", retrieved.Theme);
    }

    [TestMethod]
    public async Task SaveState_UpdatesRecentConnections_ShouldPersist()
    {
        var service = new SettingsService(_dataStore, _testDirectory);
        var state = new AppState
        {
            RecentConnections = new List<string> { "session1" }
        };

        await service.SaveStateAsync(state);
        state.RecentConnections.Add("session2");
        await service.SaveStateAsync(state);
        var retrieved = await service.GetStateAsync();

        Assert.AreEqual(2, retrieved.RecentConnections.Count());
        Assert.IsTrue(retrieved.RecentConnections.Contains("session1"));
        Assert.IsTrue(retrieved.RecentConnections.Contains("session2"));
    }

    [TestMethod]
    public async Task SettingsAndState_ShouldBeStoredSeparately()
    {
        var service = new SettingsService(_dataStore, _testDirectory);
        var settings = new AppSettings { Language = "fr" };
        var state = new AppState { LastActiveTab = "tab1" };

        await service.SaveSettingsAsync(settings);
        await service.SaveStateAsync(state);

        var retrievedSettings = await service.GetSettingsAsync();
        var retrievedState = await service.GetStateAsync();

        Assert.AreEqual("fr", retrievedSettings.Language);
        Assert.AreEqual("tab1", retrievedState.LastActiveTab);
    }

    [TestMethod]
    [TestCategory("EdgeCase")]
    public async Task WindowState_PersistsPositionAndSize_AcrossReloads()
    {
        var service1 = new SettingsService(_dataStore, _testDirectory);
        var state = new AppState
        {
            WindowPosition = new WindowPosition { X = 150, Y = 250 },
            WindowSize = new WindowSize { Width = 1280, Height = 720 }
        };

        await service1.SaveStateAsync(state);

        var service2 = new SettingsService(_dataStore, _testDirectory);
        var retrieved = await service2.GetStateAsync();

        Assert.IsNotNull(retrieved.WindowPosition);
        Assert.AreEqual(150, retrieved.WindowPosition!.X);
        Assert.AreEqual(250, retrieved.WindowPosition.Y);
        Assert.IsNotNull(retrieved.WindowSize);
        Assert.AreEqual(1280, retrieved.WindowSize!.Width);
        Assert.AreEqual(720, retrieved.WindowSize.Height);
    }
}
