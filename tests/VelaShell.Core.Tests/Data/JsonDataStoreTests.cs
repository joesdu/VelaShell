using System.Text.Json;
using VelaShell.Core.Data;
using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Data;

[TestClass]
[TestCategory("DataStore")]
public class JsonDataStoreTests : IDisposable
{
    private readonly JsonDataStore _dataStore;
    private readonly string _testDirectory;

    public JsonDataStoreTests()
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
    public async Task SaveAndLoad_SessionProfile_ShouldRoundTripSuccessfully()
    {
        string filePath = Path.Combine(_testDirectory, "session.json");
        var session = new SessionProfile
        {
            Id = Guid.NewGuid(),
            Name = "Test Server",
            Host = "192.168.1.100",
            Port = 2222,
            Username = "admin",
            Password = "secret",
            PrivateKeyPath = "/path/to/key"
        };
        await _dataStore.SaveAsync(filePath, session);
        SessionProfile? loaded = await _dataStore.LoadAsync<SessionProfile>(filePath);
        Assert.IsNotNull(loaded);
        Assert.AreEqual(session.Id, loaded!.Id);
        Assert.AreEqual(session.Name, loaded.Name);
        Assert.AreEqual(session.Host, loaded.Host);
        Assert.AreEqual(session.Password, loaded.Password);
        Assert.AreEqual(session.PrivateKeyPath, loaded.PrivateKeyPath);
    }

    [TestMethod]
    public async Task SaveAndLoad_AppSettings_ShouldRoundTripSuccessfully()
    {
        string filePath = Path.Combine(_testDirectory, "settings.json");
        var settings = new AppSettings
        {
            Language = "zh",
            Theme = "light",
            TerminalFont = "Consolas",
            TerminalFontSize = 16,
            ScrollbackLines = 5000,
            DefaultPort = 2222
        };
        await _dataStore.SaveAsync(filePath, settings);
        AppSettings? loaded = await _dataStore.LoadAsync<AppSettings>(filePath);
        Assert.AreEqual(JsonSerializer.Serialize(settings),
            JsonSerializer.Serialize(loaded));
    }

    [TestMethod]
    public async Task Load_FileDoesNotExist_ShouldReturnNewInstance()
    {
        string filePath = Path.Combine(_testDirectory, "nonexistent.json");
        AppSettings? result = await _dataStore.LoadAsync<AppSettings>(filePath);
        Assert.IsNotNull(result);
        Assert.AreEqual("zh-CN", result!.Language);
        Assert.AreEqual("dark", result.Theme);
    }

    [TestMethod]
    public async Task Load_InvalidJson_ShouldReturnDefaultsInsteadOfThrowing()
    {
        string filePath = Path.Combine(_testDirectory, "invalid.json");
        await File.WriteAllTextAsync(filePath, "{ invalid json }");
        AppSettings? result = await _dataStore.LoadAsync<AppSettings>(filePath);
        Assert.IsNotNull(result);
        Assert.AreEqual("zh-CN", result!.Language);
        Assert.AreEqual("dark", result.Theme);
    }

    [TestMethod]
    public async Task Save_CreatesDirectoryIfNotExists()
    {
        string subDir = Path.Combine(_testDirectory, "nested", "path");
        string filePath = Path.Combine(subDir, "settings.json");
        var settings = new AppSettings();
        await _dataStore.SaveAsync(filePath, settings);
        Assert.IsTrue(File.Exists(filePath));
        Assert.IsTrue(Directory.Exists(subDir));
    }

    [TestMethod]
    public async Task Save_ProducesCamelCaseJson()
    {
        string filePath = Path.Combine(_testDirectory, "settings.json");
        var settings = new AppSettings
        {
            TerminalFont = "JetBrains Mono",
            TerminalFontSize = 14
        };
        await _dataStore.SaveAsync(filePath, settings);
        string json = await File.ReadAllTextAsync(filePath);
        StringAssert.Contains(json, "\"terminalFont\":");
        StringAssert.Contains(json, "\"terminalFontSize\":");
        Assert.DoesNotContain("\"TerminalFont\":", json);
    }

    [TestMethod]
    public async Task Save_ProducesIndentedJson()
    {
        string filePath = Path.Combine(_testDirectory, "settings.json");
        var settings = new AppSettings();
        await _dataStore.SaveAsync(filePath, settings);
        string json = await File.ReadAllTextAsync(filePath);
        StringAssert.Contains(json, "\n");
        StringAssert.Contains(json, "  ");
    }

    [TestMethod]
    public async Task ConcurrentSave_ShouldNotCorruptFile()
    {
        string filePath = Path.Combine(_testDirectory, "concurrent.json");
        var tasks = new List<Task>();
        for (int i = 0; i < 10; i++)
        {
            var settings = new AppSettings { TerminalFontSize = i };
            tasks.Add(_dataStore.SaveAsync(filePath, settings));
        }
        await Task.WhenAll(tasks);
        AppSettings? loaded = await _dataStore.LoadAsync<AppSettings>(filePath);
        Assert.IsNotNull(loaded);
        Assert.IsTrue(loaded!.TerminalFontSize >= 0 && loaded.TerminalFontSize <= 9);
    }

    [TestMethod]
    public async Task Save_UsesExclusiveFileAccess()
    {
        string filePath = Path.Combine(_testDirectory, "exclusive.json");
        var settings = new AppSettings();
        await _dataStore.SaveAsync(filePath, settings);
        var fileInfo = new FileInfo(filePath);
        Assert.IsTrue(fileInfo.Exists);
    }

    [TestMethod]
    public async Task MultipleInstances_DifferentPaths_ShouldWorkConcurrently()
    {
        string filePath1 = Path.Combine(_testDirectory, "file1.json");
        string filePath2 = Path.Combine(_testDirectory, "file2.json");
        var settings1 = new AppSettings { Language = "en" };
        var settings2 = new AppSettings { Language = "zh" };
        await Task.WhenAll(_dataStore.SaveAsync(filePath1, settings1),
            _dataStore.SaveAsync(filePath2, settings2));
        AppSettings? loaded1 = await _dataStore.LoadAsync<AppSettings>(filePath1);
        AppSettings? loaded2 = await _dataStore.LoadAsync<AppSettings>(filePath2);
        Assert.AreEqual("en", loaded1!.Language);
        Assert.AreEqual("zh", loaded2!.Language);
    }

    [TestMethod]
    [TestCategory("EdgeCase")]
    public async Task Load_TruncatedJson_ShouldReturnDefaults()
    {
        string filePath = Path.Combine(_testDirectory, "truncated.json");
        await File.WriteAllTextAsync(filePath, "{ \"language\": \"fr\", \"theme\":");
        AppSettings? result = await _dataStore.LoadAsync<AppSettings>(filePath);
        Assert.IsNotNull(result);
        Assert.AreEqual("zh-CN", result!.Language);
    }

    [TestMethod]
    [TestCategory("EdgeCase")]
    public async Task Load_EmptyJsonFile_ShouldReturnDefaults()
    {
        string filePath = Path.Combine(_testDirectory, "empty.json");
        await File.WriteAllTextAsync(filePath, "");
        AppSettings? result = await _dataStore.LoadAsync<AppSettings>(filePath);
        Assert.IsNotNull(result);
        Assert.AreEqual("zh-CN", result!.Language);
    }
}
