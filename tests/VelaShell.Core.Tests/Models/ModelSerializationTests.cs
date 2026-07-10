using System.Text.Json;
using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Models;

[TestClass]
[TestCategory("DataStore")]
public class ModelSerializationTests
{
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [TestMethod]
    public void SessionProfile_ShouldSerializeWithCamelCase()
    {
        var session = new SessionProfile
        {
            Id = Guid.NewGuid(),
            Name = "Test Server",
            Host = "192.168.1.100",
            Port = 2222,
            Username = "admin",
            AuthMethod = AuthMethod.Password,
            Password = "secret123",
            PrivateKeyPath = "/path/to/key",
            PrivateKeyPassphrase = "passphrase",
            GroupId = Guid.NewGuid(),
            LastConnectedAt = new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc),
            Tags = ["production", "critical"]
        };
        string json = JsonSerializer.Serialize(session, _options);
        StringAssert.Contains(json, "\"name\":");
        StringAssert.Contains(json, "\"host\":");
        StringAssert.Contains(json, "\"username\":");
        StringAssert.Contains(json, "\"password\":");
        StringAssert.Contains(json, "\"privateKeyPath\":");
        StringAssert.Contains(json, "\"privateKeyPassphrase\":");
        StringAssert.Contains(json, "\"groupId\":");
        StringAssert.Contains(json, "\"lastConnectedAt\":");
        Assert.IsFalse(json.Contains("\"Name\":"));
        Assert.IsFalse(json.Contains("\"Host\":"));
    }

    [TestMethod]
    public void SessionProfile_ShouldDeserializeCorrectly()
    {
        var id = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        string json = $$"""
                        {
                          "id": "{{id}}",
                          "name": "Test Server",
                          "host": "192.168.1.100",
                          "port": 2222,
                          "username": "admin",
                          "authMethod": 0,
                          "password": "secret123",
                          "privateKeyPath": "/path/to/key",
                          "privateKeyPassphrase": "passphrase",
                          "groupId": "{{groupId}}",
                          "lastConnectedAt": "2026-03-05T12:00:00Z",
                          "tags": ["production", "critical"]
                        }
                        """;
        SessionProfile? session = JsonSerializer.Deserialize<SessionProfile>(json, _options);
        Assert.IsNotNull(session);
        Assert.AreEqual(id, session!.Id);
        Assert.AreEqual("Test Server", session.Name);
        Assert.AreEqual("192.168.1.100", session.Host);
        Assert.AreEqual(2222, session.Port);
        Assert.AreEqual("admin", session.Username);
        Assert.AreEqual(AuthMethod.Password, session.AuthMethod);
        Assert.AreEqual("secret123", session.Password);
        Assert.AreEqual("/path/to/key", session.PrivateKeyPath);
        Assert.AreEqual("passphrase", session.PrivateKeyPassphrase);
        Assert.AreEqual(groupId, session.GroupId);
        Assert.AreEqual(new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc), session.LastConnectedAt);
        CollectionAssert.AreEquivalent(new[] { "production", "critical" }.ToList(), session.Tags.ToList());
    }

    [TestMethod]
    public void AppSettings_ShouldSerializeWithCamelCase()
    {
        var settings = new AppSettings
        {
            Language = "en",
            Theme = "dark",
            TerminalFont = "JetBrains Mono",
            TerminalFontSize = 14,
            ScrollbackLines = 10000,
            DefaultPort = 22
        };
        string json = JsonSerializer.Serialize(settings, _options);
        StringAssert.Contains(json, "\"language\":");
        StringAssert.Contains(json, "\"theme\":");
        StringAssert.Contains(json, "\"terminalFont\":");
        StringAssert.Contains(json, "\"terminalFontSize\":");
        StringAssert.Contains(json, "\"scrollbackLines\":");
        StringAssert.Contains(json, "\"defaultPort\":");
    }

    [TestMethod]
    public void AppSettings_ShouldDeserializeCorrectly()
    {
        string json = """
                      {
                        "language": "zh",
                        "theme": "light",
                        "terminalFont": "Consolas",
                        "terminalFontSize": 16,
                        "scrollbackLines": 5000,
                        "defaultPort": 2222
                      }
                      """;
        AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json, _options);
        Assert.IsNotNull(settings);
        Assert.AreEqual("zh", settings!.Language);
        Assert.AreEqual("light", settings.Theme);
        Assert.AreEqual("Consolas", settings.TerminalFont);
        Assert.AreEqual(16, settings.TerminalFontSize);
        Assert.AreEqual(5000, settings.ScrollbackLines);
        Assert.AreEqual(2222, settings.DefaultPort);
    }

    [TestMethod]
    public void AppState_ShouldSerializeWithNestedObjects()
    {
        var state = new AppState
        {
            RecentConnections = ["session1", "session2"],
            WindowPosition = new() { X = 100, Y = 200 },
            WindowSize = new() { Width = 1024, Height = 768 },
            LastActiveTab = "tab1"
        };
        string json = JsonSerializer.Serialize(state, _options);
        StringAssert.Contains(json, "\"recentConnections\":");
        StringAssert.Contains(json, "\"windowPosition\":");
        StringAssert.Contains(json, "\"windowSize\":");
        StringAssert.Contains(json, "\"lastActiveTab\":");
        StringAssert.Contains(json, "\"x\":");
        StringAssert.Contains(json, "\"y\":");
        StringAssert.Contains(json, "\"width\":");
        StringAssert.Contains(json, "\"height\":");
    }

    [TestMethod]
    public void ServerGroup_ShouldSerializeWithSessionsList()
    {
        var group = new ServerGroup
        {
            Id = Guid.NewGuid(),
            Name = "Production",
            Icon = "server",
            SortOrder = 1,
            Sessions = [Guid.NewGuid(), Guid.NewGuid()]
        };
        string json = JsonSerializer.Serialize(group, _options);
        StringAssert.Contains(json, "\"id\":");
        StringAssert.Contains(json, "\"name\":");
        StringAssert.Contains(json, "\"icon\":");
        StringAssert.Contains(json, "\"sortOrder\":");
        StringAssert.Contains(json, "\"sessions\":");
    }

    [TestMethod]
    public void KnownHost_ShouldSerializeWithDates()
    {
        var host = new KnownHost
        {
            HostKey = "AAAAB3NzaC1...",
            Fingerprint = "SHA256:abc123...",
            Algorithm = "ssh-rsa",
            FirstSeenAt = new(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc),
            LastSeenAt = new(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc)
        };
        string json = JsonSerializer.Serialize(host, _options);
        StringAssert.Contains(json, "\"hostKey\":");
        StringAssert.Contains(json, "\"fingerprint\":");
        StringAssert.Contains(json, "\"algorithm\":");
        StringAssert.Contains(json, "\"firstSeenAt\":");
        StringAssert.Contains(json, "\"lastSeenAt\":");
    }

    [TestMethod]
    public void SessionProfile_RoundTripSerialization_ShouldPreserveAllProperties()
    {
        var original = new SessionProfile
        {
            Id = Guid.NewGuid(),
            Name = "Test",
            Host = "example.com",
            Port = 2222,
            Username = "user",
            AuthMethod = AuthMethod.PrivateKey,
            Password = "pass",
            PrivateKeyPath = "/key",
            PrivateKeyPassphrase = "phrase",
            GroupId = Guid.NewGuid(),
            LastConnectedAt = DateTime.UtcNow,
            Tags = ["tag1", "tag2"]
        };
        string json = JsonSerializer.Serialize(original, _options);
        SessionProfile? deserialized = JsonSerializer.Deserialize<SessionProfile>(json, _options);
        Assert.AreEqual(JsonSerializer.Serialize(original, _options),
            JsonSerializer.Serialize(deserialized, _options));
    }

    [TestMethod]
    public void AppSettings_RoundTripSerialization_ShouldPreserveAllProperties()
    {
        var original = new AppSettings
        {
            Language = "fr",
            Theme = "dark",
            TerminalFont = "Fira Code",
            TerminalFontSize = 18,
            ScrollbackLines = 20000,
            DefaultPort = 2222
        };
        string json = JsonSerializer.Serialize(original, _options);
        AppSettings? deserialized = JsonSerializer.Deserialize<AppSettings>(json, _options);
        Assert.AreEqual(JsonSerializer.Serialize(original, _options),
            JsonSerializer.Serialize(deserialized, _options));
    }
}
