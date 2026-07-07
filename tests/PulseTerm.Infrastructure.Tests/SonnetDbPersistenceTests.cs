using PulseTerm.Core.Models;
using PulseTerm.Infrastructure.Persistence;

namespace PulseTerm.Infrastructure.Tests;

[TestClass]
public sealed class SonnetDbPersistenceTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly SonnetDbEngine _engine;
    private readonly AesSecretProtector _protector;

    public SonnetDbPersistenceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"pulseterm_sndbtest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _engine = new SonnetDbEngine(Path.Combine(_testDirectory, "sonnetdb"));
        _protector = new AesSecretProtector(Path.Combine(_testDirectory, "secret.key"));
    }

    public void Dispose()
    {
        _engine.Dispose();
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    [TestMethod]
    public async Task SessionRepository_SaveAndLoad_RoundTrips()
    {
        var repo = new SonnetDbSessionRepository(_engine, _protector);
        var group = new ServerGroup { Name = "生产环境", SortOrder = 1 };
        var profile = new SessionProfile
        {
            Name = "web-prod-01",
            Host = "192.168.1.100",
            Port = 22,
            Username = "root",
            Password = "s3cret",
            GroupId = group.Id,
        };

        await repo.SaveGroupAsync(group);
        await repo.SaveSessionAsync(profile);

        var groups = await repo.GetAllGroupsAsync();
        var sessions = await repo.GetAllSessionsAsync();

        Assert.AreEqual(1, groups.Count);
        Assert.AreEqual("生产环境", groups[0].Name);
        Assert.AreEqual(1, sessions.Count);
        Assert.AreEqual("web-prod-01", sessions[0].Name);
        Assert.AreEqual("s3cret", sessions[0].Password, "读出的密码应已解密");

        var single = await repo.GetSessionAsync(profile.Id);
        Assert.IsNotNull(single);
        Assert.AreEqual(profile.Host, single.Host);
    }

    [TestMethod]
    public async Task SessionRepository_Password_IsEncryptedAtRest()
    {
        var repo = new SonnetDbSessionRepository(_engine, _protector);
        var profile = new SessionProfile { Name = "n", Host = "h", Username = "u", Password = "plaintext-pass" };
        await repo.SaveSessionAsync(profile);

        var rawJson = await _engine.WithCollectionAsync(SonnetDbEngine.ProfilesCollection,
            store => store.Get(profile.Id.ToString("D"))!.Json);

        Assert.IsFalse(rawJson.Contains("plaintext-pass"), "落盘 JSON 不应包含明文密码");
        Assert.IsTrue(rawJson.Contains("enc1:"), "落盘密码应带加密前缀");
    }

    [TestMethod]
    public async Task SessionRepository_DeleteGroup_DetachesSessions()
    {
        var repo = new SonnetDbSessionRepository(_engine, _protector);
        var group = new ServerGroup { Name = "g" };
        var profile = new SessionProfile { Name = "n", Host = "h", Username = "u", GroupId = group.Id };
        await repo.SaveGroupAsync(group);
        await repo.SaveSessionAsync(profile);

        await repo.DeleteGroupAsync(group.Id);

        var sessions = await repo.GetAllSessionsAsync();
        Assert.IsNull(sessions.Single().GroupId);
        Assert.AreEqual(0, (await repo.GetAllGroupsAsync()).Count);
    }

    [TestMethod]
    public async Task SessionRepository_ImportsLegacyJson_Once()
    {
        var legacyFile = Path.Combine(_testDirectory, "sessions.json");
        var legacyId = Guid.NewGuid();
        await File.WriteAllTextAsync(legacyFile,
            $$"""{"groups":[{"id":"{{Guid.NewGuid()}}","name":"旧分组","sortOrder":0,"sessions":[]}],"sessions":[{"id":"{{legacyId}}","name":"legacy","host":"1.2.3.4","port":22,"username":"ops","authMethod":0,"password":"oldpass","tags":[]}]}""");

        var repo = new SonnetDbSessionRepository(_engine, _protector, legacyFile);
        var sessions = await repo.GetAllSessionsAsync();

        Assert.AreEqual(1, sessions.Count);
        Assert.AreEqual("legacy", sessions[0].Name);
        Assert.AreEqual("oldpass", sessions[0].Password);
        Assert.IsFalse(File.Exists(legacyFile), "导入成功后旧文件应被改名");
        Assert.IsTrue(File.Exists(legacyFile + ".migrated.bak"));
    }

    [TestMethod]
    public async Task SettingsService_SaveAndLoad_RoundTrips_AndRaisesEvent()
    {
        var service = new SonnetDbSettingsService(_engine);
        AppSettings? observed = null;
        service.SettingsSaved += s => observed = s;

        var settings = await service.GetSettingsAsync();
        settings.Theme = "light";
        settings.TerminalFontSize = 16;
        await service.SaveSettingsAsync(settings);

        var reloaded = await service.GetSettingsAsync();
        Assert.AreEqual("light", reloaded.Theme);
        Assert.AreEqual(16, reloaded.TerminalFontSize);
        Assert.IsNotNull(observed);

        var state = await service.GetStateAsync();
        state.LastActiveTab = "tab-1";
        await service.SaveStateAsync(state);
        Assert.AreEqual("tab-1", (await service.GetStateAsync()).LastActiveTab);
    }

    [TestMethod]
    public async Task HostKeyService_TrustAndVerify_Works()
    {
        var service = new SonnetDbHostKeyService(_engine);

        Assert.AreEqual(Core.Ssh.HostKeyVerification.Unknown,
            await service.VerifyHostKeyAsync("host1", 22, "ssh-ed25519", "SHA256:abc"));

        await service.TrustHostKeyAsync("host1", 22, "ssh-ed25519", "SHA256:abc");
        Assert.AreEqual(Core.Ssh.HostKeyVerification.Trusted,
            await service.VerifyHostKeyAsync("host1", 22, "ssh-ed25519", "SHA256:abc"));
        Assert.AreEqual(Core.Ssh.HostKeyVerification.Changed,
            await service.VerifyHostKeyAsync("host1", 22, "ssh-ed25519", "SHA256:different"));

        await service.RemoveKnownHostAsync("host1", 22);
        Assert.AreEqual(0, (await service.GetKnownHostsAsync()).Count);
    }

    [TestMethod]
    public async Task RecentConnections_RecordAndQuery_DedupesAndOrdersByTimeDesc()
    {
        var service = new SonnetDbRecentConnectionService(_engine);
        var profileId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await service.RecordAsync(new RecentConnectionEntry
        {
            ProfileId = profileId, Name = "web-prod-01", GroupName = "生产环境",
            Host = "192.168.1.100", Port = 22, Username = "root",
            ConnectedAt = now.AddHours(-2), Success = true, DurationMs = 120,
        });
        await service.RecordAsync(new RecentConnectionEntry
        {
            ProfileId = profileId, Name = "web-prod-01", GroupName = "生产环境",
            Host = "192.168.1.100", Port = 22, Username = "root",
            ConnectedAt = now.AddMinutes(-5), Success = true, DurationMs = 80,
        });
        await service.RecordAsync(new RecentConnectionEntry
        {
            Name = "deploy@staging.example.com", Host = "staging.example.com", Port = 22,
            Username = "deploy", ConnectedAt = now.AddDays(-1), Success = true, DurationMs = 300,
        });
        await service.RecordAsync(new RecentConnectionEntry
        {
            Name = "failed@10.0.0.9", Host = "10.0.0.9", Port = 22, Username = "x",
            ConnectedAt = now, Success = false, DurationMs = 5000,
        });

        var recent = await service.GetRecentAsync(10);

        Assert.AreEqual(2, recent.Count, "同一配置去重、失败连接不出现");
        Assert.AreEqual("web-prod-01", recent[0].Name);
        Assert.AreEqual("生产环境", recent[0].GroupName);
        Assert.AreEqual(profileId, recent[0].ProfileId);
        Assert.AreEqual("deploy@staging.example.com", recent[1].Name);
        Assert.IsTrue(recent[0].ConnectedAt > recent[1].ConnectedAt);

        await service.ClearAsync();
        Assert.AreEqual(0, (await service.GetRecentAsync(10)).Count);
    }

    [TestMethod]
    public async Task AuditLog_WriteAndQuery_FiltersByCategory()
    {
        var service = new SonnetDbAuditLogService(_engine);
        await service.WriteAsync(new AuditEntry { Category = "connection", Action = "connect", Detail = "root@h1" });
        await service.WriteAsync(new AuditEntry { Category = "settings", Action = "save", Detail = "theme" });

        var all = await service.QueryAsync(10);
        Assert.AreEqual(2, all.Count);

        var connections = await service.QueryAsync(10, "connection");
        Assert.AreEqual(1, connections.Count);
        Assert.AreEqual("connect", connections[0].Action);
    }

    [TestMethod]
    public void SecretProtector_RoundTrips_And_PassesThroughLegacyPlaintext()
    {
        var cipher = _protector.Protect("你好 password!");
        Assert.IsNotNull(cipher);
        Assert.IsTrue(cipher.StartsWith("enc1:"));
        Assert.AreEqual("你好 password!", _protector.Unprotect(cipher));

        Assert.AreEqual("legacy-plain", _protector.Unprotect("legacy-plain"));
        Assert.AreEqual(cipher, _protector.Protect(cipher), "已加密的值不应二次加密");
        Assert.IsNull(_protector.Protect(null));
    }

    [TestMethod]
    public async Task AppDataStore_UpsertGetDelete_Works()
    {
        var store = new SonnetDbAppDataStore(_engine);
        await store.UpsertAsync("ui_config", "layout", new TestDoc { Value = "docked" });

        var loaded = await store.GetAsync<TestDoc>("ui_config", "layout");
        Assert.IsNotNull(loaded);
        Assert.AreEqual("docked", loaded.Value);

        Assert.AreEqual(1, (await store.GetAllAsync<TestDoc>("ui_config")).Count);

        await store.DeleteAsync("ui_config", "layout");
        Assert.IsNull(await store.GetAsync<TestDoc>("ui_config", "layout"));
    }

    [TestMethod]
    public async Task Engine_Reopen_PersistsDocumentsAndTimeSeries()
    {
        var dir = Path.Combine(_testDirectory, "reopen-db");
        using (var engine = new SonnetDbEngine(dir))
        {
            var repo = new SonnetDbSessionRepository(engine, _protector);
            await repo.SaveSessionAsync(new SessionProfile { Name = "persist-me", Host = "h", Username = "u" });
            var recents = new SonnetDbRecentConnectionService(engine);
            await recents.RecordAsync(new RecentConnectionEntry { Name = "r1", Host = "h", Username = "u", Success = true });
        }

        using (var engine = new SonnetDbEngine(dir))
        {
            var repo = new SonnetDbSessionRepository(engine, _protector);
            Assert.AreEqual("persist-me", (await repo.GetAllSessionsAsync()).Single().Name);
            var recents = new SonnetDbRecentConnectionService(engine);
            Assert.AreEqual(1, (await recents.GetRecentAsync(5)).Count);
        }
    }

    private sealed class TestDoc
    {
        public string Value { get; set; } = string.Empty;
    }
}
