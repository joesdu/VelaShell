using System.Text.Json;
using VelaShell.Core.Data;
using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Data;

[TestClass]
[TestCategory("DataStore")]
public class SettingsServiceTests : IDisposable
{
    private readonly JsonDataStore _dataStore;
    private readonly string _testDirectory;

    public SettingsServiceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"velashell_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
        _dataStore = new();
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
        AppSettings settings = await service.GetSettingsAsync();
        Assert.IsNotNull(settings);
        Assert.AreEqual("zh-CN", settings.Language);
        Assert.AreEqual("dark", settings.Theme);
        Assert.AreEqual("JetBrains Mono", settings.TerminalFont);
        Assert.AreEqual(14, settings.TerminalFontSize);
        Assert.AreEqual(50000, settings.ScrollbackLines);
        Assert.AreEqual(22, settings.DefaultPort);
        Assert.IsFalse(settings.Appearance.ShowQuickCommandsPanel);
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
            DefaultPort = 2222,
            Appearance = new() { ShowQuickCommandsPanel = true },
        };
        await service.SaveSettingsAsync(settings);
        AppSettings retrieved = await service.GetSettingsAsync();
        Assert.AreEqual(JsonSerializer.Serialize(settings), JsonSerializer.Serialize(retrieved));
        Assert.IsTrue(retrieved.Appearance.ShowQuickCommandsPanel);
    }

    [TestMethod]
    public async Task GetStateAsync_FileDoesNotExist_ShouldReturnDefaultState()
    {
        var service = new SettingsService(_dataStore, _testDirectory);
        AppState state = await service.GetStateAsync();
        Assert.IsNotNull(state);
        Assert.IsEmpty(state.RecentConnections);
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
            RecentConnections = ["session1", "session2", "session3"],
            WindowPosition = new() { X = 100, Y = 200 },
            WindowSize = new() { Width = 1024, Height = 768 },
            LastActiveTab = "tab1",
        };
        await service.SaveStateAsync(state);
        AppState retrieved = await service.GetStateAsync();
        Assert.AreEqual(JsonSerializer.Serialize(state), JsonSerializer.Serialize(retrieved));
    }

    [TestMethod]
    public async Task SaveSettings_UpdatesExisting_ShouldOverwrite()
    {
        var service = new SettingsService(_dataStore, _testDirectory);
        var settings1 = new AppSettings { Language = "en", Theme = "dark" };
        var settings2 = new AppSettings { Language = "zh", Theme = "light" };
        await service.SaveSettingsAsync(settings1);
        await service.SaveSettingsAsync(settings2);
        AppSettings retrieved = await service.GetSettingsAsync();
        Assert.AreEqual("zh", retrieved.Language);
        Assert.AreEqual("light", retrieved.Theme);
    }

    [TestMethod]
    public async Task SaveState_UpdatesRecentConnections_ShouldPersist()
    {
        var service = new SettingsService(_dataStore, _testDirectory);
        var state = new AppState { RecentConnections = ["session1"] };
        await service.SaveStateAsync(state);
        state.RecentConnections.Add("session2");
        await service.SaveStateAsync(state);
        AppState retrieved = await service.GetStateAsync();
        Assert.HasCount(2, retrieved.RecentConnections);
        Assert.Contains("session1", retrieved.RecentConnections);
        Assert.Contains("session2", retrieved.RecentConnections);
    }

    [TestMethod]
    public async Task SettingsAndState_ShouldBeStoredSeparately()
    {
        var service = new SettingsService(_dataStore, _testDirectory);
        var settings = new AppSettings { Language = "fr" };
        var state = new AppState { LastActiveTab = "tab1" };
        await service.SaveSettingsAsync(settings);
        await service.SaveStateAsync(state);
        AppSettings retrievedSettings = await service.GetSettingsAsync();
        AppState retrievedState = await service.GetStateAsync();
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
            WindowPosition = new() { X = 150, Y = 250 },
            WindowSize = new() { Width = 1280, Height = 720 },
        };
        await service1.SaveStateAsync(state);
        var service2 = new SettingsService(_dataStore, _testDirectory);
        AppState retrieved = await service2.GetStateAsync();
        Assert.IsNotNull(retrieved.WindowPosition);
        Assert.AreEqual(150, retrieved.WindowPosition!.X);
        Assert.AreEqual(250, retrieved.WindowPosition.Y);
        Assert.IsNotNull(retrieved.WindowSize);
        Assert.AreEqual(1280, retrieved.WindowSize!.Width);
        Assert.AreEqual(720, retrieved.WindowSize.Height);
    }
}
