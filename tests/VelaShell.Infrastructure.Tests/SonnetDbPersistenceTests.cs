using SonnetDB.Catalog;
using SonnetDB.Storage.Format;
using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Persistence;

namespace VelaShell.Infrastructure.Tests;

[TestClass]
public sealed class SonnetDbPersistenceTests : IDisposable
{
    private SonnetDbEngine? _engine;
    private readonly AesSecretProtector _protector;
    private readonly string _testDirectory;

    private SonnetDbEngine Engine => _engine ??= new(Path.Combine(_testDirectory, "sonnetdb"));

    public SonnetDbPersistenceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"velashell_sndbtest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _protector = new(Path.Combine(_testDirectory, "secret.key"));
    }

    public void Dispose()
    {
        _engine?.Dispose();
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    [TestMethod]
    public async Task SessionRepository_SaveAndLoad_RoundTrips()
    {
        var repo = new SonnetDbSessionRepository(Engine, _protector);
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
        List<ServerGroup> groups = await repo.GetAllGroupsAsync();
        List<SessionProfile> sessions = await repo.GetAllSessionsAsync();
        Assert.HasCount(1, groups);
        Assert.AreEqual("生产环境", groups[0].Name);
        Assert.HasCount(1, sessions);
        Assert.AreEqual("web-prod-01", sessions[0].Name);
        Assert.AreEqual("s3cret", sessions[0].Password, "读出的密码应已解密");
        Assert.AreEqual(ConnectionType.SSH, sessions[0].ConnectionType);
        SessionProfile? single = await repo.GetSessionAsync(profile.Id);
        Assert.IsNotNull(single);
        Assert.AreEqual(profile.Host, single.Host);

        (List<ServerGroup> Groups, List<SessionProfile> Sessions) = await repo.GetAllTreeDataAsync();
        Assert.AreEqual(group.Id, Groups.Single().Id);
        Assert.AreEqual(group.Id, Sessions.Single().GroupId);
        Assert.AreEqual(profile.Password, Sessions.Single().Password);
    }

    [TestMethod]
    public async Task SessionRepository_HierarchySurvivesReopenAndMovesOnlyDirectMembership()
    {
        var repo = new SonnetDbSessionRepository(Engine, _protector);
        var root = new ServerGroup { Name = "A" };
        var child = new ServerGroup { Name = "B", ParentGroupId = root.Id };
        var leaf = new ServerGroup { Name = "C", ParentGroupId = child.Id };
        var profile = new SessionProfile { GroupId = leaf.Id, Name = "db" };
        await repo.SaveGroupAsync(root);
        await repo.SaveGroupAsync(child);
        await repo.SaveGroupAsync(leaf);
        await repo.SaveSessionAsync(profile);
        _engine!.Dispose();
        _engine = null;
        repo = new SonnetDbSessionRepository(Engine, _protector);
        var tree = new ServerGroupTree(await repo.GetAllGroupsAsync());
        Assert.AreEqual("A > B > C", tree.PathOf(leaf.Id));
        Assert.AreEqual(profile.Id, tree.Find(leaf.Id)!.Sessions.Single());
        Assert.IsEmpty(tree.Find(child.Id)!.Sessions);
        profile.GroupId = child.Id;
        await repo.ReplaceSessionAsync(profile);
        tree = new(await repo.GetAllGroupsAsync());
        Assert.AreEqual(profile.Id, tree.Find(child.Id)!.Sessions.Single());
        Assert.IsEmpty(tree.Find(leaf.Id)!.Sessions);
        await repo.DeleteGroupAsync(child.Id);
        tree = new(await repo.GetAllGroupsAsync());
        Assert.IsEmpty(tree.Validate());
        Assert.IsNull(tree.Find(leaf.Id)!.ParentGroupId);
        Assert.IsNull((await repo.GetSessionAsync(profile.Id))!.GroupId);
    }

    [TestMethod]
    public async Task SessionRepository_SftpProfile_RoundTripsThroughProtectedDocument()
    {
        var repo = new SonnetDbSessionRepository(Engine, _protector);
        var profile = new SessionProfile
        {
            Name = "files",
            Host = "sftp.example.com",
            Username = "deploy",
            Password = "secret",
            ConnectionType = ConnectionType.SFTP,
        };

        await repo.SaveSessionAsync(profile);

        SessionProfile loaded = (await repo.GetSessionAsync(profile.Id))!;
        Assert.AreEqual(ConnectionType.SFTP, loaded.ConnectionType);
        Assert.AreEqual("secret", loaded.Password);
    }

    /// <summary>
    /// 「认证后执行命令」是**每条配置各一份**的设置,必须真的跟着这条配置落盘 ——
    /// 仓储写的是 <c>Protect</c> 出来的副本,漏抄一个字段的表现就是"存的时候好好的,
    /// 重开软件命令没了",而且不报任何错。
    /// </summary>
    [TestMethod]
    public async Task SessionRepository_PostAuthCommand_RoundTripsPerProfile()
    {
        var repo = new SonnetDbSessionRepository(Engine, _protector);
        var bastion = new SessionProfile
        {
            Name = "bastion",
            Host = "10.0.0.1",
            Username = "ops",
            PostAuthCommand = "sudo su -",
            PostAuthCommandDelaySeconds = 3,
        };
        var dev = new SessionProfile
        {
            Name = "dev",
            Host = "10.0.0.2",
            Username = "dev",
            PostAuthCommand = "tmux attach",
            PostAuthCommandDelaySeconds = 0,
        };

        await repo.SaveSessionAsync(bastion);
        await repo.SaveSessionAsync(dev);

        SessionProfile loadedBastion = (await repo.GetSessionAsync(bastion.Id))!;
        SessionProfile loadedDev = (await repo.GetSessionAsync(dev.Id))!;
        Assert.AreEqual("sudo su -", loadedBastion.PostAuthCommand);
        Assert.AreEqual(3, loadedBastion.PostAuthCommandDelaySeconds);
        // 两条配置各配各的,互不串味 —— 这正是它区别于全局「连接后执行命令」的地方。
        Assert.AreEqual("tmux attach", loadedDev.PostAuthCommand);
        Assert.AreEqual(0, loadedDev.PostAuthCommandDelaySeconds);
    }

    /// <summary>
    /// 备注(#549)原样落盘:换行不丢、不加密(它要在资源管理器的悬停提示里直接显示);
    /// 加字段之前存下的配置读回来是 null,而不是空串。
    /// </summary>
    [TestMethod]
    public async Task SessionRepository_Notes_RoundTripWithLineBreaks()
    {
        var repo = new SonnetDbSessionRepository(Engine, _protector);
        var noted = new SessionProfile
        {
            Name = "bastion",
            Host = "10.0.0.1",
            Username = "ops",
            Notes = "跳板机,只开 22\r\n值班:运维二组\n\n改配置先报备",
        };
        var bare = new SessionProfile { Name = "bare", Host = "10.0.0.2", Username = "ops" };

        await repo.SaveSessionAsync(noted);
        await repo.SaveSessionAsync(bare);

        Assert.AreEqual(noted.Notes, (await repo.GetSessionAsync(noted.Id))!.Notes);
        Assert.IsNull((await repo.GetSessionAsync(bare.Id))!.Notes);
    }

    /// <summary>
    /// 整条写回(连接成功、置顶、拖动分组、编辑配置……)拿的多半是早先读出来的副本:
    /// 它不带别处后来加的目录收藏,写回时收藏必须保留库里的那份,其余字段照传入的写。
    /// </summary>
    [TestMethod]
    public async Task SessionRepository_SaveFromAStaleCopy_KeepsStoredBookmarks()
    {
        var repo = new SonnetDbSessionRepository(Engine, _protector);
        var profile = new SessionProfile { Name = "files", Host = "10.0.0.1", Username = "ops", Password = "secret" };
        await repo.SaveSessionAsync(profile);
        SessionProfile staleCopy = profile.Clone();
        await repo.UpdateDirectoryBookmarksAsync(profile.Id, bookmarks =>
        {
            bookmarks.Add(new RemoteDirectoryBookmark { Name = "logs", LocalPath = "/tmp/logs", RemotePath = "/var/log" });
            return true;
        });

        staleCopy.IsPinned = true;
        await repo.SaveSessionAsync(staleCopy);

        SessionProfile stored = (await repo.GetSessionAsync(profile.Id))!;
        Assert.IsTrue(stored.IsPinned, "整条写回的其余字段照常生效");
        Assert.AreEqual("secret", stored.Password);
        Assert.AreEqual("/var/log", stored.DirectoryBookmarks.Single().RemotePath, "旧副本里没有的收藏不能被它抹掉");
    }

    /// <summary>同步拉取、导入备份要连收藏一起覆盖:走 <see cref="ISessionRepository.ReplaceSessionAsync" />。</summary>
    [TestMethod]
    public async Task SessionRepository_Replace_OverwritesBookmarksToo()
    {
        var repo = new SonnetDbSessionRepository(Engine, _protector);
        var profile = new SessionProfile
        {
            Name = "files",
            Host = "10.0.0.1",
            Username = "ops",
            DirectoryBookmarks = [new RemoteDirectoryBookmark { Name = "old", LocalPath = "/tmp/old", RemotePath = "/old" }],
        };
        await repo.SaveSessionAsync(profile);
        SessionProfile incoming = profile.Clone();
        incoming.DirectoryBookmarks = [new RemoteDirectoryBookmark { Name = "new", LocalPath = "/tmp/new", RemotePath = "/new" }];

        await repo.SaveSessionAsync(incoming);
        Assert.AreEqual("/old", (await repo.GetSessionAsync(profile.Id))!.DirectoryBookmarks.Single().RemotePath);

        await repo.ReplaceSessionAsync(incoming);
        Assert.AreEqual("/new", (await repo.GetSessionAsync(profile.Id))!.DirectoryBookmarks.Single().RemotePath);
    }

    /// <summary>
    /// 收藏修改只改收藏:在库里最新的那份上改,配置的其余字段(包括加密的密码)原样不动;
    /// 回调返回 false 不写;配置不在库里时交回 null 且不调用回调。
    /// </summary>
    [TestMethod]
    public async Task SessionRepository_UpdateBookmarks_ChangesOnlyTheBookmarks()
    {
        var repo = new SonnetDbSessionRepository(Engine, _protector);
        var profile = new SessionProfile { Name = "files", Host = "10.0.0.1", Port = 2222, Username = "ops", Password = "secret" };
        await repo.SaveSessionAsync(profile);
        var bookmark = new RemoteDirectoryBookmark { Name = "logs", LocalPath = "/tmp/logs", RemotePath = "/var/log" };

        List<RemoteDirectoryBookmark>? result = await repo.UpdateDirectoryBookmarksAsync(profile.Id, bookmarks =>
        {
            bookmarks.Add(bookmark);
            return true;
        });

        Assert.AreEqual(bookmark.Id, result!.Single().Id);
        Assert.AreNotSame(bookmark, result.Single(), "交回的是副本");
        SessionProfile stored = (await repo.GetSessionAsync(profile.Id))!;
        Assert.AreEqual(("files", 2222, "secret"), (stored.Name, stored.Port, stored.Password));
        Assert.AreEqual("/var/log", stored.DirectoryBookmarks.Single().RemotePath);

        List<RemoteDirectoryBookmark>? unchanged = await repo.UpdateDirectoryBookmarksAsync(profile.Id, bookmarks =>
        {
            bookmarks.Clear();
            return false;
        });
        Assert.HasCount(1, unchanged!, "返回 false 不写回,交回的仍是库里那份");
        Assert.HasCount(1, (await repo.GetSessionAsync(profile.Id))!.DirectoryBookmarks);

        bool called = false;
        Assert.IsNull(await repo.UpdateDirectoryBookmarksAsync(Guid.NewGuid(), _ => called = true));
        Assert.IsFalse(called);
    }

    [TestMethod]
    public async Task SessionRepository_LegacyDocumentWithoutConnectionType_DefaultsToSsh()
    {
        var profileId = Guid.NewGuid();
        string json = $$"""{"id":"{{profileId}}","name":"legacy","host":"legacy.example.com","username":"ops","authMethod":0}""";
        await Engine.WithCollectionAsync<object?>(SonnetDbEngine.ProfilesCollection, store =>
        {
            store.Upsert(profileId.ToString("D"), json);
            return null;
        });
        var repo = new SonnetDbSessionRepository(Engine, _protector);

        SessionProfile loaded = (await repo.GetSessionAsync(profileId))!;

        Assert.AreEqual(ConnectionType.SSH, loaded.ConnectionType);
    }

    [TestMethod]
    public async Task SessionRepository_Password_IsEncryptedAtRest()
    {
        var repo = new SonnetDbSessionRepository(Engine, _protector);
        var profile = new SessionProfile
        {
            Name = "n",
            Host = "h",
            Username = "u",
            Password = "plaintext-pass",
        };
        await repo.SaveSessionAsync(profile);
        string rawJson = await Engine.WithCollectionAsync(
            SonnetDbEngine.ProfilesCollection,
            store => store.Get(profile.Id.ToString("D"))!.Json
        );
        Assert.DoesNotContain("plaintext-pass", rawJson, "落盘 JSON 不应包含明文密码");
        Assert.Contains("enc1:", rawJson, "落盘密码应带加密前缀");
    }

    [TestMethod]
    [DataRow(ConnectionType.SSH)]
    [DataRow(ConnectionType.SFTP)]
    public async Task SessionRepository_Tailscale_SaveAndLoadDoesNotPersistStaleMaterials(ConnectionType type)
    {
        var repo = new SonnetDbSessionRepository(Engine, _protector);
        var profile = new SessionProfile
        {
            Name = "tail",
            Host = "tail",
            Username = "deploy",
            ConnectionType = type,
            AuthMethod = AuthMethod.Tailscale,
            Password = "stale-password",
            PrivateKeyPath = "/stale-key",
            PrivateKeyPassphrase = "stale-phrase",
            CertificatePath = "/stale-cert",
            RememberPassword = true,
            CredentialSource = CredentialReference.ForShared(Guid.NewGuid()),
            JumpHostProfileId = Guid.NewGuid()
        };

        await repo.SaveSessionAsync(profile);
        string rawJson = await Engine.WithCollectionAsync(SonnetDbEngine.ProfilesCollection,
            store => store.Get(profile.Id.ToString("D"))!.Json);
        SessionProfile loaded = (await repo.GetSessionAsync(profile.Id))!;

        Assert.AreEqual(AuthMethod.Tailscale, loaded.AuthMethod);
        Assert.AreEqual(type, loaded.ConnectionType);
        Assert.AreEqual("deploy", loaded.Username);
        Assert.AreEqual(profile.JumpHostProfileId, loaded.JumpHostProfileId);
        Assert.IsNull(loaded.CredentialSource);
        Assert.IsNull(loaded.Password);
        Assert.IsNull(loaded.PrivateKeyPath);
        Assert.IsNull(loaded.PrivateKeyPassphrase);
        Assert.IsNull(loaded.CertificatePath);
        Assert.IsFalse(loaded.RememberPassword);
        foreach (string stale in (string[])["stale-password", "/stale-key", "stale-phrase", "/stale-cert", "enc1:"])
        {
            Assert.DoesNotContain(stale, rawJson);
        }
        Assert.AreEqual("stale-password", profile.Password, "保存仅清理副本");
        Assert.IsNotNull(profile.CredentialSource);
    }

    [TestMethod]
    public async Task SessionRepository_Tailscale_LegacyMaterialsAreClearedBeforeDecrypting()
    {
        var profile = new SessionProfile
        {
            Name = "tail",
            Host = "tail",
            Username = "deploy",
            AuthMethod = AuthMethod.Tailscale,
            Password = "enc1:invalid",
            PrivateKeyPath = "/old-key",
            PrivateKeyPassphrase = "enc1:invalid",
            CertificatePath = "/old-cert",
            RememberPassword = true,
            CredentialSource = CredentialReference.ForShared(Guid.NewGuid()),
            JumpHostProfileId = Guid.NewGuid()
        };
        await Engine.WithCollectionAsync<object?>(SonnetDbEngine.ProfilesCollection, store =>
        {
            store.Upsert(profile.Id.ToString("D"), SonnetDbJson.Serialize(profile));
            return null;
        });
        var repo = new SonnetDbSessionRepository(Engine, _protector);

        SessionProfile single = (await repo.GetSessionAsync(profile.Id))!;
        SessionProfile all = (await repo.GetAllSessionsAsync()).Single();

        foreach (SessionProfile loaded in (SessionProfile[])[single, all])
        {
            Assert.AreEqual(AuthMethod.Tailscale, loaded.AuthMethod);
            Assert.AreEqual("deploy", loaded.Username);
            Assert.AreEqual(profile.JumpHostProfileId, loaded.JumpHostProfileId);
            Assert.IsNull(loaded.CredentialSource);
            Assert.IsNull(loaded.Password);
            Assert.IsNull(loaded.PrivateKeyPath);
            Assert.IsNull(loaded.PrivateKeyPassphrase);
            Assert.IsNull(loaded.CertificatePath);
            Assert.IsFalse(loaded.RememberPassword);
        }
    }

    [TestMethod]
    public async Task SessionRepository_DeleteGroup_DetachesSessions()
    {
        var repo = new SonnetDbSessionRepository(Engine, _protector);
        var group = new ServerGroup { Name = "g" };
        var profile = new SessionProfile
        {
            Name = "n",
            Host = "h",
            Username = "u",
            GroupId = group.Id,
        };
        await repo.SaveGroupAsync(group);
        await repo.SaveSessionAsync(profile);
        await repo.DeleteGroupAsync(group.Id);
        List<SessionProfile> sessions = await repo.GetAllSessionsAsync();
        Assert.IsNull(sessions.Single().GroupId);
        Assert.IsEmpty(await repo.GetAllGroupsAsync());
    }

    [TestMethod]
    public async Task SessionRepository_ImportsLegacyJson_Once()
    {
        string legacyFile = Path.Combine(_testDirectory, "sessions.json");
        var legacyId = Guid.NewGuid();
        await File.WriteAllTextAsync(
            legacyFile,
            $$"""{"groups":[{"id":"{{Guid.NewGuid()}}","name":"旧分组","sortOrder":0,"sessions":[]}],"sessions":[{"id":"{{legacyId}}","name":"legacy","host":"1.2.3.4","port":22,"username":"ops","authMethod":0,"password":"oldpass","tags":[]}]}"""
        );
        var repo = new SonnetDbSessionRepository(Engine, _protector, legacyFile);
        List<SessionProfile> sessions = await repo.GetAllSessionsAsync();
        Assert.HasCount(1, sessions);
        Assert.AreEqual("legacy", sessions[0].Name);
        Assert.AreEqual("oldpass", sessions[0].Password);
        Assert.IsFalse(File.Exists(legacyFile), "导入成功后旧文件应被改名");
        Assert.IsTrue(File.Exists(legacyFile + ".migrated.bak"));
    }

    /// <summary>
    /// 换了一把本机密钥(数据目录从别处拷来、系统密钥链这次没打开):载入的密码与口令原样是密文 ——
    /// 存回去不丢(历史契约),连接前据此认出「解不开」(<see cref="CredentialMaterial.HasUnreadableSecret(SessionProfile)" />),
    /// 不拿它去登录。钉住的是仓储与这条判定之间的约定:哪天改成解不开交回 null,这条会红。
    /// </summary>
    [TestMethod]
    public async Task SessionRepository_WithAnotherKey_LoadsSecretsThatAreRecognisedAsUnreadable()
    {
        var profile = new SessionProfile
        {
            Name = "web",
            Host = "h",
            Username = "root",
            AuthMethod = AuthMethod.Password,
            Password = "s3cret"
        };
        var keyed = new SessionProfile
        {
            Name = "keyed",
            Host = "h",
            Username = "root",
            AuthMethod = AuthMethod.PrivateKey,
            PrivateKeyPath = "/keys/id",
            PrivateKeyPassphrase = "pp"
        };
        await new SonnetDbSessionRepository(Engine, _protector).SaveSessionAsync(profile);
        await new SonnetDbSessionRepository(Engine, _protector).SaveSessionAsync(keyed);

        var otherKey = new AesSecretProtector(Path.Combine(_testDirectory, "other.key"));
        var repo = new SonnetDbSessionRepository(Engine, otherKey);
        SessionProfile loaded = (await repo.GetSessionAsync(profile.Id))!;
        SessionProfile loadedKeyed = (await repo.GetSessionAsync(keyed.Id))!;

        Assert.IsTrue(CredentialMaterial.HasUnreadableSecret(loaded));
        Assert.IsTrue(CredentialMaterial.HasUnreadableSecret(loadedKeyed));
        Assert.IsFalse(CredentialMaterial.HasUnreadableSecret((await new SonnetDbSessionRepository(Engine, _protector).GetSessionAsync(profile.Id))!),
            "密钥对得上时载入的是明文");

        await repo.SaveSessionAsync(loaded);
        Assert.AreEqual("s3cret", (await new SonnetDbSessionRepository(Engine, _protector).GetSessionAsync(profile.Id))!.Password,
            "解不开的那份存回去不丢");
    }

    [TestMethod]
    public async Task SettingsService_SaveAndLoad_RoundTrips_AndRaisesEvent()
    {
        var service = new SonnetDbSettingsService(Engine);
        AppSettings? observed = null;
        service.SettingsSaved += s => observed = s;
        AppSettings settings = await service.GetSettingsAsync();
        settings.Theme = "light";
        settings.TerminalFontSize = 16;
        await service.SaveSettingsAsync(settings);
        AppSettings reloaded = await service.GetSettingsAsync();
        Assert.AreEqual("light", reloaded.Theme);
        Assert.AreEqual(16, reloaded.TerminalFontSize);
        Assert.IsNotNull(observed);
        AppState state = await service.GetStateAsync();
        state.LastActiveTab = "tab-1";
        await service.SaveStateAsync(state);
        Assert.AreEqual("tab-1", (await service.GetStateAsync()).LastActiveTab);
    }

    [TestMethod]
    public async Task SettingsService_PersistsNestedOptionGroups()
    {
        var service = new SonnetDbSettingsService(Engine);
        AppSettings settings = await service.GetSettingsAsync();
        settings.General.AutoReconnect = false;
        settings.General.ReconnectIntervalSeconds = 15;
        settings.Appearance.CursorColor = "#FF00FF";
        settings.TerminalBehavior.CopyOnSelect = true;
        settings.Transfer.ConflictPolicy = "rename";
        settings.Security.AlertWebhook = true;
        settings.Keys.DefaultKeyName = "id_ed25519";
        await service.SaveSettingsAsync(settings);
        AppSettings reloaded = await service.GetSettingsAsync();
        Assert.IsFalse(reloaded.General.AutoReconnect);
        Assert.AreEqual(15, reloaded.General.ReconnectIntervalSeconds);
        Assert.AreEqual("#FF00FF", reloaded.Appearance.CursorColor);
        Assert.IsTrue(reloaded.TerminalBehavior.CopyOnSelect);
        Assert.AreEqual("rename", reloaded.Transfer.ConflictPolicy);
        Assert.IsTrue(reloaded.Security.AlertWebhook);
        Assert.AreEqual("id_ed25519", reloaded.Keys.DefaultKeyName);
        Assert.HasCount(8, reloaded.Appearance.AnsiNormal);
    }

    /// <summary>
    /// 旧字段 <c>autoLoadToAgent</c> 默认 <see langword="true" /> 却从没有消费者,存量配置里几乎都存着那个 <c>true</c>。
    /// 它不是用户的选择,不能被当成「同意往 agent 里放私钥」—— 新开关必须保持关闭。
    /// </summary>
    [TestMethod]
    public void Settings_LegacyAutoLoadToAgentDoesNotTurnOnAddKeysToAgent()
    {
        AppSettings? settings = SonnetDbJson.Deserialize<AppSettings>(
            """{ "keys": { "defaultKeyName": "id_rsa", "autoLoadToAgent": true } }""");

        Assert.IsNotNull(settings);
        Assert.AreEqual("id_rsa", settings.Keys.DefaultKeyName);
        Assert.IsFalse(settings.Keys.AddKeysToAgent);
    }

    [TestMethod]
    public async Task HostKeyService_TrustAndVerify_Works()
    {
        var service = new SonnetDbHostKeyService(Engine);
        Assert.AreEqual(
            HostKeyVerification.Unknown,
            await service.VerifyHostKeyAsync("host1", 22, "ssh-ed25519", "SHA256:abc")
        );
        await service.TrustHostKeyAsync("host1", 22, "ssh-ed25519", "SHA256:abc");
        Assert.AreEqual(
            HostKeyVerification.Trusted,
            await service.VerifyHostKeyAsync("host1", 22, "ssh-ed25519", "SHA256:abc")
        );
        Assert.AreEqual(
            HostKeyVerification.Changed,
            await service.VerifyHostKeyAsync("host1", 22, "ssh-ed25519", "SHA256:different")
        );
        await service.RemoveKnownHostAsync("host1", 22);
        Assert.IsEmpty(await service.GetKnownHostsAsync());
    }

    /// <summary>
    /// 〔API-H4〕一台主机的每种密钥类型各记一条:接受一把新类型的钥不覆盖原来那一把,谈成哪一种都认;
    /// 同一种类型换了钥才是变更;删除只删那一种。
    /// </summary>
    [TestMethod]
    public async Task HostKeyService_KeepsOneRecordPerKeyType()
    {
        var service = new SonnetDbHostKeyService(Engine);
        await service.TrustHostKeyAsync("multi", 22, "ssh-rsa", "SHA256:rsa");
        Assert.AreEqual(HostKeyVerification.Changed, await service.VerifyHostKeyAsync("multi", 22, "ssh-ed25519", "SHA256:ed"),
            "记着钥、却没有这一种:仍是变更,由用户决定");

        await service.TrustHostKeyAsync("multi", 22, "ssh-ed25519", "SHA256:ed");
        Assert.AreEqual(HostKeyVerification.Trusted, await service.VerifyHostKeyAsync("multi", 22, "ssh-ed25519", "SHA256:ed"));
        Assert.AreEqual(HostKeyVerification.Trusted, await service.VerifyHostKeyAsync("multi", 22, "ssh-rsa", "SHA256:rsa"),
            "接受新类型的钥没有覆盖原来那一把");
        Assert.AreEqual(HostKeyVerification.Changed, await service.VerifyHostKeyAsync("multi", 22, "ssh-rsa", "SHA256:other"));
        Assert.HasCount(2, await service.FindKnownHostKeysAsync("multi", 22));

        await service.TrustHostKeyAsync("multi", 22, "ssh-rsa", "SHA256:rsa2");
        Assert.AreEqual(HostKeyVerification.Changed, await service.VerifyHostKeyAsync("multi", 22, "ssh-rsa", "SHA256:rsa"),
            "同一种类型换了钥:旧的那一把不再受信");
        Assert.HasCount(2, await service.FindKnownHostKeysAsync("multi", 22));

        await service.RemoveKnownHostAsync("multi", 22, "ssh-rsa");
        KnownHost left = (await service.FindKnownHostKeysAsync("multi", 22)).Single();
        Assert.AreEqual("ssh-ed25519", left.KeyType);
        await service.RemoveKnownHostAsync("multi", 22);
        Assert.IsEmpty(await service.FindKnownHostKeysAsync("multi", 22));
    }

    /// <summary>〔API-H4〕按类型分开存之前的单条记录(<c>host:port</c>)照常认;信任一把别的类型的钥时,它搬成按类型的记录、照样受信。</summary>
    [TestMethod]
    public async Task HostKeyService_LegacySingleRecord_IsMigratedPerKeyType()
    {
        KnownHost legacy = new() { Host = "legacy2", Port = 2200, KeyType = "ssh-rsa", Fingerprint = "SHA256:rsa" };
        await Engine.WithCollectionAsync<object?>(SonnetDbEngine.KnownHostsCollection, store =>
        {
            store.Upsert("legacy2:2200", SonnetDbJson.Serialize(legacy));
            return null;
        });

        var service = new SonnetDbHostKeyService(Engine);
        Assert.AreEqual(HostKeyVerification.Trusted, await service.VerifyHostKeyAsync("legacy2", 2200, "ssh-rsa", "SHA256:rsa"));

        await service.TrustHostKeyAsync("legacy2", 2200, "ssh-ed25519", "SHA256:ed");
        Assert.AreEqual(HostKeyVerification.Trusted, await service.VerifyHostKeyAsync("legacy2", 2200, "ssh-rsa", "SHA256:rsa"));
        Assert.AreEqual(HostKeyVerification.Trusted, await service.VerifyHostKeyAsync("legacy2", 2200, "ssh-ed25519", "SHA256:ed"));
        Assert.HasCount(2, await service.FindKnownHostKeysAsync("legacy2", 2200));
        Assert.HasCount(2, (await service.GetKnownHostsAsync()).Where(h => h.Host == "legacy2").ToList(), "旧的单条记录不留重复");
    }

    /// <summary>
    /// 换库之前存下的指纹是裸 base64(上一版库不带 <c>SHA256:</c> 前缀),现在拿到的带前缀。
    /// 两者必须判为同一把钥 —— 否则换库之后每台已保存的主机都会弹「指纹已变更」。
    /// </summary>
    [TestMethod]
    public async Task HostKeyService_LegacyBareFingerprint_StillVerifiesAsTrusted()
    {
        var service = new SonnetDbHostKeyService(Engine);
        await service.TrustHostKeyAsync("legacy", 22, "PublicKey", "kmYcvdi2Y9bXAbc+/Def");

        Assert.AreEqual(
            HostKeyVerification.Trusted,
            await service.VerifyHostKeyAsync("legacy", 22, "ssh-ed25519", "SHA256:kmYcvdi2Y9bXAbc+/Def"));
        Assert.AreEqual(
            HostKeyVerification.Trusted,
            await service.VerifyHostKeyAsync("legacy", 22, "ssh-ed25519", "SHA256:kmYcvdi2Y9bXAbc+/Def="));
        Assert.AreEqual(
            HostKeyVerification.Changed,
            await service.VerifyHostKeyAsync("legacy", 22, "ssh-ed25519", "SHA256:somethingElse"));
    }

    /// <summary>
    /// 指纹变更弹窗要摆出"原来记的是哪一把"(#476),所以单条记录必须取得到,
    /// 而不是让调用方去 <c>GetKnownHostsAsync</c> 里翻。
    /// </summary>
    [TestMethod]
    public async Task HostKeyService_FindKnownHost_ReturnsRecordedFingerprint()
    {
        var service = new SonnetDbHostKeyService(Engine);
        Assert.IsNull(await service.FindKnownHostAsync("host2", 2222));

        await service.TrustHostKeyAsync("host2", 2222, "ssh-ed25519", "SHA256:old");
        KnownHost? found = await service.FindKnownHostAsync("host2", 2222);

        Assert.IsNotNull(found);
        Assert.AreEqual("SHA256:old", found.Fingerprint);
        Assert.AreEqual("ssh-ed25519", found.KeyType);
        // 端口是主键的一半:同主机不同端口不能互相串。
        Assert.IsNull(await service.FindKnownHostAsync("host2", 22));
    }

    [TestMethod]
    public async Task RecentConnections_RecordAndQuery_DedupesAndOrdersByTimeDesc()
    {
        var service = new SonnetDbRecentConnectionService(Engine);
        var profileId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await service.RecordAsync(
            new()
            {
                ProfileId = profileId,
                Name = "web-prod-01",
                GroupName = "生产环境",
                Host = "192.168.1.100",
                Port = 22,
                Username = "root",
                ConnectionType = ConnectionType.SFTP,
                ConnectedAt = now.AddHours(-2),
                Success = true,
                DurationMs = 120,
            }
        );
        await service.RecordAsync(
            new()
            {
                ProfileId = profileId,
                Name = "web-prod-01",
                GroupName = "生产环境",
                Host = "192.168.1.100",
                Port = 22,
                Username = "root",
                ConnectionType = ConnectionType.SFTP,
                ConnectedAt = now.AddMinutes(-5),
                Success = true,
                DurationMs = 80,
            }
        );
        await service.RecordAsync(
            new()
            {
                Name = "deploy@staging.example.com",
                Host = "staging.example.com",
                Port = 22,
                Username = "deploy",
                ConnectedAt = now.AddDays(-1),
                Success = true,
                DurationMs = 300,
            }
        );
        await service.RecordAsync(
            new()
            {
                Name = "failed@10.0.0.9",
                Host = "10.0.0.9",
                Port = 22,
                Username = "x",
                ConnectedAt = now,
                Success = false,
                DurationMs = 5000,
            }
        );
        List<RecentConnectionEntry> recent = await service.GetRecentAsync(10);
        Assert.HasCount(2, recent, "同一配置去重、失败连接不出现");
        Assert.AreEqual("web-prod-01", recent[0].Name);
        Assert.AreEqual("生产环境", recent[0].GroupName);
        Assert.AreEqual(profileId, recent[0].ProfileId);
        Assert.AreEqual(ConnectionType.SFTP, recent[0].ConnectionType);
        Assert.AreEqual("deploy@staging.example.com", recent[1].Name);
        Assert.IsGreaterThan(recent[1].ConnectedAt, recent[0].ConnectedAt);
        await service.ClearAsync();
        Assert.IsEmpty(await service.GetRecentAsync(10));
    }

    [TestMethod]
    public async Task RecentConnections_InvalidConnectionType_FallsBackToSsh()
    {
        await Engine.WritePointAsync(
            SonnetDbEngine.ConnHistoryMeasurement,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string>
            {
                ["host"] = "legacy.example.com",
                ["username"] = "ops",
            },
            new Dictionary<string, SonnetDB.Model.FieldValue>
            {
                ["name"] = SonnetDB.Model.FieldValue.FromString("legacy"),
                ["port"] = SonnetDB.Model.FieldValue.FromLong(22),
                ["connection_type"] = SonnetDB.Model.FieldValue.FromLong(99),
                ["success"] = SonnetDB.Model.FieldValue.FromBool(true),
            }
        );

        RecentConnectionEntry entry = (await new SonnetDbRecentConnectionService(Engine).GetRecentAsync(1)).Single();

        Assert.AreEqual(ConnectionType.SSH, entry.ConnectionType);
    }

    [TestMethod]
    public async Task RecentConnections_ConnectionTypeBeyondInt32Range_FallsBackToSsh()
    {
        await Engine.WritePointAsync(
            SonnetDbEngine.ConnHistoryMeasurement,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string>
            {
                ["host"] = "overflow.example.com",
                ["username"] = "ops",
            },
            new Dictionary<string, SonnetDB.Model.FieldValue>
            {
                ["name"] = SonnetDB.Model.FieldValue.FromString("overflow"),
                ["port"] = SonnetDB.Model.FieldValue.FromLong(22),
                ["connection_type"] = SonnetDB.Model.FieldValue.FromLong((long)int.MaxValue + 1),
                ["success"] = SonnetDB.Model.FieldValue.FromBool(true),
            }
        );

        RecentConnectionEntry entry = (await new SonnetDbRecentConnectionService(Engine).GetRecentAsync(1)).Single();

        Assert.AreEqual(ConnectionType.SSH, entry.ConnectionType);
    }

    /// <summary>
    /// 保留策略:早于截止时刻的审计记录删掉,之后的原样留着 —— 同一类别、同一动作(同一条序列)
    /// 里新旧混着的也要分得开,不能整条序列一起删。
    /// </summary>
    [TestMethod]
    public async Task AuditLog_DeleteOlderThan_KeepsWhatIsInsideTheWindow()
    {
        var service = new SonnetDbAuditLogService(Engine);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await service.WriteAsync(new() { Timestamp = now.AddDays(-400), Category = "connection", Action = "connect", Detail = "old" });
        await service.WriteAsync(new() { Timestamp = now.AddDays(-10), Category = "connection", Action = "connect", Detail = "recent" });
        await service.WriteAsync(new() { Timestamp = now.AddDays(-300), Category = "security", Action = "hostkey-rejected", Detail = "old-security" });
        await service.WriteAsync(new() { Timestamp = now.AddMinutes(-1), Category = "security", Action = "hostkey-rejected", Detail = "new-security" });

        await service.DeleteOlderThanAsync(now.AddDays(-180));

        List<AuditEntry> left = await service.QueryAsync(10);
        Assert.AreSequenceEqual(["recent", "new-security"], [.. left.Select(e => e.Detail)], SequenceOrder.InAnyOrder);
    }

    /// <summary>连接历史与审计日志同一口径:删旧的,「最近连接」照常取得到新的。</summary>
    [TestMethod]
    public async Task RecentConnections_DeleteOlderThan_KeepsRecentOnes()
    {
        var service = new SonnetDbRecentConnectionService(Engine);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await service.RecordAsync(new() { Name = "ancient", Host = "a", Username = "u", ConnectedAt = now.AddDays(-365), Success = true });
        await service.RecordAsync(new() { Name = "fresh", Host = "b", Username = "u", ConnectedAt = now.AddDays(-1), Success = true });

        await service.DeleteOlderThanAsync(now.AddDays(-180));

        List<RecentConnectionEntry> recent = await service.GetRecentAsync(10);
        Assert.AreEqual("fresh", recent.Single().Name);
    }

    /// <summary>
    /// 时间段下推到查询里:落在最近 N 条之外的那一段也要查得到,按时间倒序,上限不含。
    /// 类别与时间段一起用时两个条件都生效(取值走参数,类别里带引号也不会拼坏 SQL)。
    /// </summary>
    [TestMethod]
    public async Task AuditLog_Query_HonoursTheTimeRange()
    {
        var service = new SonnetDbAuditLogService(Engine);
        DateTimeOffset start = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        string[] actions = ["connect", "connect-failed", "profile-updated"];
        for (int day = 0; day < 30; day++)
        {
            await service.WriteAsync(new() { Timestamp = start.AddDays(day), Category = day % 2 == 0 ? "connection" : "profile", Action = actions[day % 3], Detail = $"d{day}" });
        }

        List<AuditEntry> week = await service.QueryAsync(100, from: start.AddDays(10), to: start.AddDays(17));
        Assert.AreSequenceEqual(["d16", "d15", "d14", "d13", "d12", "d11", "d10"], [.. week.Select(e => e.Detail)]);

        List<AuditEntry> latestTwo = await service.QueryAsync(2, from: start.AddDays(10), to: start.AddDays(17));
        Assert.AreSequenceEqual(["d16", "d15"], [.. latestTwo.Select(e => e.Detail)], "上限截的是时间段里最新的");

        List<AuditEntry> connections = await service.QueryAsync(100, "connection", start.AddDays(10), start.AddDays(17));
        Assert.AreSequenceEqual(["d16", "d14", "d12", "d10"], [.. connections.Select(e => e.Detail)]);

        Assert.IsEmpty(await service.QueryAsync(100, "x' OR '1'='1"));
    }

    /// <summary>来源一列原样存取;没写来源的记录(连接、安全两类)读回来是 null。</summary>
    [TestMethod]
    public async Task AuditLog_Source_RoundTrips()
    {
        var service = new SonnetDbAuditLogService(Engine);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await service.WriteAsync(new() { Timestamp = now.AddMinutes(-2), Category = "connection", Action = "connect", Detail = "no-source" });
        await service.WriteAsync(new() { Timestamp = now.AddMinutes(-1), Category = "profile", Action = "profile-updated", Detail = "synced", Source = AuditSources.CloudSync });

        List<AuditEntry> entries = await service.QueryAsync(10);

        Assert.AreEqual(AuditSources.CloudSync, entries.Single(e => e.Detail == "synced").Source);
        Assert.IsNull(entries.Single(e => e.Detail == "no-source").Source);
    }

    /// <summary>
    /// 老库的 audit_log 建表时没有 source 一列:在第一条带来源的记录写进来之前,查询不能因为点名未知列而报错;
    /// 写进来之后(SonnetDB 自动补列)新记录带着来源、旧记录为 null,重开库之后照样。
    /// </summary>
    [TestMethod]
    public async Task AuditLog_DatabaseCreatedBeforeTheSourceColumn_KeepsWorking()
    {
        string root = Path.Combine(_testDirectory, "legacy-audit");
        var legacySchema = MeasurementSchema.Create(SonnetDbEngine.AuditLogMeasurement,
        [
            new("category", MeasurementColumnRole.Tag, FieldType.String),
            new("action", MeasurementColumnRole.Tag, FieldType.String),
            new("profile_id", MeasurementColumnRole.Tag, FieldType.String),
            new("detail", MeasurementColumnRole.Field, FieldType.String)
        ]);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using (var engine = new SonnetDbEngine(root))
        {
            // 换成加 source 之前的那份表结构,模拟升级前就有的库。
            await engine.DropMeasurementAsync(SonnetDbEngine.AuditLogMeasurement);
            await engine.EnsureMeasurementAsync(legacySchema);
            Assert.IsNull((await engine.GetMeasurementAsync(SonnetDbEngine.AuditLogMeasurement))!.TryGetColumn(SonnetDbAuditLogService.SourceColumn));
            var service = new SonnetDbAuditLogService(engine);
            await service.WriteAsync(new() { Timestamp = now.AddMinutes(-2), Category = "profile", Action = "profile-updated", Detail = "old" });

            Assert.IsNull((await service.QueryAsync(10)).Single().Source, "还没有 source 一列时照常查得出来");

            await service.WriteAsync(new() { Timestamp = now.AddMinutes(-1), Category = "profile", Action = "profile-updated", Detail = "new", Source = AuditSources.Import });
            List<AuditEntry> both = await service.QueryAsync(10);
            Assert.AreSequenceEqual([AuditSources.Import, null], [.. both.Select(e => e.Source)]);
        }
        using var reopened = new SonnetDbEngine(root);
        List<AuditEntry> entries = await new SonnetDbAuditLogService(reopened).QueryAsync(10);
        Assert.AreSequenceEqual(["new", "old"], [.. entries.Select(e => e.Detail)]);
        Assert.AreEqual(AuditSources.Import, entries[0].Source, "补上的列随库持久化");
    }

    [TestMethod]
    public async Task AuditLog_WriteAndQuery_FiltersByCategory()
    {
        var service = new SonnetDbAuditLogService(Engine);
        await service.WriteAsync(
            new()
            {
                Category = "connection",
                Action = "connect",
                Detail = "root@h1",
            }
        );
        await service.WriteAsync(
            new()
            {
                Category = "settings",
                Action = "save",
                Detail = "theme",
            }
        );
        List<AuditEntry> all = await service.QueryAsync(10);
        Assert.HasCount(2, all);
        List<AuditEntry> connections = await service.QueryAsync(10, "connection");
        Assert.HasCount(1, connections);
        Assert.AreEqual("connect", connections[0].Action);
    }

    [TestMethod]
    public void SecretProtector_RoundTrips_And_PassesThroughLegacyPlaintext()
    {
        string? cipher = _protector.Protect("你好 password!");
        Assert.IsNotNull(cipher);
        Assert.StartsWith("enc1:", cipher);
        Assert.AreEqual("你好 password!", _protector.Unprotect(cipher));
        Assert.AreEqual("legacy-plain", _protector.Unprotect("legacy-plain"));
        Assert.AreEqual(cipher, _protector.Protect(cipher), "已加密的值不应二次加密");
        Assert.IsNull(_protector.Protect(null));
    }

    [TestMethod]
    public async Task AppDataStore_UpsertGetDelete_Works()
    {
        var store = new SonnetDbAppDataStore(Engine);
        await store.UpsertAsync("ui_config", "layout", new TestDoc { Value = "docked" });
        TestDoc? loaded = await store.GetAsync<TestDoc>("ui_config", "layout");
        Assert.IsNotNull(loaded);
        Assert.AreEqual("docked", loaded.Value);
        Assert.HasCount(1, await store.GetAllAsync<TestDoc>("ui_config"));
        await store.DeleteAsync("ui_config", "layout");
        Assert.IsNull(await store.GetAsync<TestDoc>("ui_config", "layout"));
    }

    [TestMethod]
    public async Task QuickCommands_V1Document_MigratesToV2AndCreatesBackup()
    {
        var store = new SonnetDbAppDataStore(Engine);
        await store.UpsertAsync(
            "quick_commands",
            "commands",
            new
            {
                marker = "preserved",
                commands = new[]
                {
                    new
                    {
                        id = Guid.Empty,
                        name = "first",
                        category = "",
                        commandText = "echo first",
                        description = "one",
                        isBuiltIn = false,
                    },
                    new
                    {
                        id = Guid.Empty,
                        name = "second",
                        category = "Ops",
                        commandText = "echo second",
                        description = "two",
                        isBuiltIn = false,
                    },
                    new
                    {
                        id = Guid.Empty,
                        name = "third",
                        category = "ops",
                        commandText = "echo third",
                        description = "three",
                        isBuiltIn = false,
                    },
                },
            }
        );
        var repository = new SonnetDbQuickCommandRepository(store);

        QuickCommandLoadResult result = await repository.LoadAsync();

        Assert.IsTrue(result.Migrated);
        Assert.AreEqual(QuickCommandData.CurrentSchemaVersion, result.Data.SchemaVersion);
        Assert.HasCount(3, result.Data.Commands);
        Assert.AreEqual(
            QuickCommandGroupCatalog.DefaultGroupId,
            result.Data.Commands.Single(command => command.Name == "first").GroupId
        );
        QuickCommandGroup[] opsGroups =
        [
            .. result.Data.Groups.Where(group =>
                string.Equals(group.Name, "Ops", StringComparison.OrdinalIgnoreCase)
            ),
        ];
        Assert.HasCount(1, opsGroups);
        Assert.IsTrue(
            result
                .Data.Commands.Where(command => command.Name is "second" or "third")
                .All(command => command.GroupId == opsGroups[0].Id)
        );
        Assert.IsTrue(result.Data.Commands.All(command => command.Id != Guid.Empty));
        LegacyQuickCommandDataProbe? backup = await store.GetAsync<LegacyQuickCommandDataProbe>(
            "quick_commands",
            "commands.v1.backup"
        );
        Assert.IsNotNull(backup);
        Assert.AreEqual("preserved", backup.Marker);

        Guid[] ids = [.. result.Data.Commands.Select(command => command.Id)];
        var secondRepository = new SonnetDbQuickCommandRepository(store);
        QuickCommandLoadResult secondLoad = await secondRepository.LoadAsync();
        Assert.AreSequenceEqual(
            ids, [.. secondLoad.Data.Commands.Select(command => command.Id)]
        );
    }

    [TestMethod]
    public async Task QuickCommands_LegacyJson_ImportsAsV2ThenRenamesFile()
    {
        string legacyPath = Path.Combine(_testDirectory, "quick-commands.json");
        await File.WriteAllTextAsync(
            legacyPath,
            """
            {"commands":[{"name":"legacy","category":"Custom","commandText":"uptime","description":"old"}]}
            """
        );
        var store = new SonnetDbAppDataStore(Engine);
        var repository = new SonnetDbQuickCommandRepository(store, legacyPath);

        QuickCommandLoadResult result = await repository.LoadAsync();

        Assert.IsTrue(result.Migrated);
        Assert.ContainsSingle(command => command.Name == "legacy", result.Data.Commands);
        Assert.IsFalse(File.Exists(legacyPath));
        Assert.IsTrue(File.Exists(legacyPath + ".migrated.bak"));
    }

    [TestMethod]
    public async Task QuickCommands_NewerSchema_IsNotOverwritten()
    {
        var store = new SonnetDbAppDataStore(Engine);
        await store.UpsertAsync(
            "quick_commands",
            "commands",
            new { schemaVersion = 99, marker = "keep" }
        );
        var repository = new SonnetDbQuickCommandRepository(store);

        QuickCommandLoadResult result = await repository.LoadAsync();

        Assert.IsFalse(string.IsNullOrEmpty(result.Error));
        VersionProbe? original = await store.GetAsync<VersionProbe>("quick_commands", "commands");
        Assert.IsNotNull(original);
        Assert.AreEqual(99, original.SchemaVersion);
        Assert.AreEqual("keep", original.Marker);
    }

    [TestMethod]
    public async Task QuickCommands_SyncV1_MigratesCategoriesAndIgnoresBuiltIns()
    {
        var store = new SonnetDbAppDataStore(Engine);
        var repository = new SonnetDbQuickCommandRepository(store);

        await repository.ApplySyncAsync(
            new()
            {
                SchemaVersion = 1,
                Commands =
                [
                    new()
                    {
                        Name = "remote",
                        Category = " Ops ",
                        CommandText = "uptime",
                        Description = "from v1",
                    },
                    new()
                    {
                        Name = "built in",
                        Category = "System",
                        CommandText = "systemctl status",
                        IsBuiltIn = true,
                    },
                ],
            }
        );

        QuickCommandData data = (await repository.LoadAsync()).Data;
        QuickCommand command = Assert.ContainsSingle(data.Commands);
        QuickCommandGroup group = data.Groups.Single(item => item.Id == command.GroupId);
        Assert.AreEqual("Ops", group.Name);
        Assert.AreEqual("remote", command.Name);
    }

    [TestMethod]
    public async Task QuickCommands_SyncV2_RoundTripsGroupsSortAndCompatibilityCategory()
    {
        var store = new SonnetDbAppDataStore(Engine);
        var repository = new SonnetDbQuickCommandRepository(store);
        var groupId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        await repository.ApplySyncAsync(
            new()
            {
                SchemaVersion = 2,
                Groups =
                [
                    new()
                    {
                        Id = groupId,
                        Name = "Deploy",
                        Kind = QuickCommandGroupKind.User,
                        SortOrder = 7,
                    },
                ],
                Commands =
                [
                    new()
                    {
                        Id = commandId,
                        GroupId = groupId,
                        Name = "release",
                        Category = "legacy ignored",
                        CommandText = "./release.sh",
                        SortOrder = 3,
                    },
                ],
            }
        );

        QuickCommandSyncData exported = await repository.ExportSyncAsync();

        Assert.AreEqual(2, exported.SchemaVersion);
        QuickCommandGroup group = exported.Groups.Single(item => item.Id == groupId);
        QuickCommandSyncItem command = exported.Commands.Single(item => item.Id == commandId);
        Assert.AreEqual(7, group.SortOrder);
        Assert.AreEqual(3, command.SortOrder);
        Assert.AreEqual(groupId, command.GroupId);
        Assert.AreEqual("Deploy", command.Category);
    }

    [TestMethod]
    public async Task QuickCommands_SyncV2_InvalidGroupFallsBackToDefault()
    {
        var repository = new SonnetDbQuickCommandRepository(new SonnetDbAppDataStore(Engine));
        await repository.ApplySyncAsync(
            new()
            {
                SchemaVersion = 2,
                Commands =
                [
                    new()
                    {
                        Id = Guid.NewGuid(),
                        GroupId = Guid.NewGuid(),
                        Name = "orphan",
                        CommandText = "pwd",
                    },
                ],
            }
        );

        QuickCommand command = Assert.ContainsSingle((await repository.LoadAsync()).Data.Commands);
        Assert.AreEqual(QuickCommandGroupCatalog.DefaultGroupId, command.GroupId);
    }

    [TestMethod]
    public async Task QuickCommands_SyncNewerSchema_RejectsWithoutOverwritingLocalData()
    {
        var repository = new SonnetDbQuickCommandRepository(new SonnetDbAppDataStore(Engine));
        await repository.SaveAsync(
            new()
            {
                Groups = QuickCommandGroupCatalog.CreateSystemGroups(),
                Commands = [new() { Name = "local", CommandText = "pwd" }],
            }
        );

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            repository.ApplySyncAsync(new() { SchemaVersion = 99 })
        );

        Assert.AreEqual(
            "local",
            Assert.ContainsSingle((await repository.LoadAsync()).Data.Commands).Name
        );
    }

    /// <summary>
    /// #555 的覆盖层与排序:删掉的内置命令(含本版本不认识的标识)、内置分组之间的先后、
    /// 每个分组的组内顺序,都要原样落盘、装回,并经云同步导出 / 应用后不丢。
    /// </summary>
    [TestMethod]
    public async Task QuickCommands_OverlayAndOrder_RoundTripThroughStorageAndSync()
    {
        var store = new SonnetDbAppDataStore(Engine);
        var repository = new SonnetDbQuickCommandRepository(store);
        QuickCommand hidden = QuickCommandCatalog.BuiltIns[0];
        QuickCommand overridden = QuickCommandCatalog.BuiltIns[1];
        var unknown = Guid.NewGuid();
        List<QuickCommandGroup> groups = QuickCommandGroupCatalog.CreateSystemGroups();
        QuickCommandGroup firstBuiltIn = groups.First(group => group.Kind == QuickCommandGroupKind.BuiltIn);
        QuickCommandGroup lastBuiltIn = groups.Last(group => group.Kind == QuickCommandGroupKind.BuiltIn);
        (firstBuiltIn.SortOrder, lastBuiltIn.SortOrder) = (lastBuiltIn.SortOrder, firstBuiltIn.SortOrder);
        Guid[] builtInOrder = [overridden.Id, Guid.NewGuid()];
        firstBuiltIn.CommandOrder = [.. builtInOrder];
        var user = new QuickCommandGroup
        {
            Id = Guid.NewGuid(),
            Name = "Ops",
            Kind = QuickCommandGroupKind.User,
            SortOrder = 9,
        };
        var custom = new QuickCommand { Id = Guid.NewGuid(), GroupId = user.Id, Name = "deploy", CommandText = "./deploy.sh" };
        user.CommandOrder = [custom.Id];
        groups.Add(user);
        await repository.SaveAsync(
            new()
            {
                Groups = groups,
                Commands =
                [
                    custom,
                    new() { Id = overridden.Id, GroupId = firstBuiltIn.Id, Name = "edited", CommandText = "echo edited" },
                ],
                HiddenBuiltInIds = [hidden.Id, unknown],
            }
        );

        QuickCommandLoadResult loaded = await new SonnetDbQuickCommandRepository(store).LoadAsync();

        Assert.IsFalse(loaded.Migrated, "刚存下的文档再装回来不该被当成要修复 —— 那会让每次启动都触发一次同步");
        AssertOverlay(loaded.Data);

        QuickCommandSyncData exported = await repository.ExportSyncAsync();
        Assert.AreSequenceEqual([hidden.Id, unknown], [.. exported.HiddenBuiltInIds]);
        using var remoteEngine = new SonnetDbEngine(Path.Combine(_testDirectory, "remote-db"));
        var remote = new SonnetDbQuickCommandRepository(new SonnetDbAppDataStore(remoteEngine));
        await remote.ApplySyncAsync(exported);
        AssertOverlay((await remote.LoadAsync()).Data);

        void AssertOverlay(QuickCommandData data)
        {
            Assert.AreSequenceEqual([hidden.Id, unknown], [.. data.HiddenBuiltInIds], "不认识的标识也要留着,否则同步回去会把新版本删掉的命令放出来");
            // 两个内置分组对调过先后:存的是对调后的,不能被目录里的出厂顺序盖回去。
            Assert.AreEqual(firstBuiltIn.SortOrder, data.Groups.Single(group => group.Id == firstBuiltIn.Id).SortOrder);
            Assert.AreEqual(lastBuiltIn.SortOrder, data.Groups.Single(group => group.Id == lastBuiltIn.Id).SortOrder);
            Assert.AreNotEqual(
                QuickCommandGroupCatalog.BuiltIns.Single(group => group.Id == firstBuiltIn.Id).SortOrder,
                data.Groups.Single(group => group.Id == firstBuiltIn.Id).SortOrder
            );
            Assert.AreSequenceEqual(builtInOrder, [.. data.Groups.Single(group => group.Id == firstBuiltIn.Id).CommandOrder]);
            Assert.AreSequenceEqual([custom.Id], [.. data.Groups.Single(group => group.Id == user.Id).CommandOrder]);
            Assert.AreEqual("edited", data.Commands.Single(command => command.Id == overridden.Id).Name);
            Assert.AreEqual(
                int.MaxValue,
                data.Groups.Single(group => group.Kind == QuickCommandGroupKind.Default).SortOrder,
                "「未分组」固定垫底,它的顺序不跟存档走"
            );
        }
    }

    /// <summary>
    /// 旧版本存下的 v2 文档(没有隐藏清单与顺序表)原样装回,不当成要修复的数据 ——
    /// 否则升级后第一次启动就会无端标记「本地有改动」并推一次同步。
    /// </summary>
    [TestMethod]
    public async Task QuickCommands_DocumentFromBefore555_LoadsWithoutRepair()
    {
        var store = new SonnetDbAppDataStore(Engine);
        await store.UpsertAsync(
            "quick_commands",
            "commands",
            new
            {
                schemaVersion = 2,
                groups = QuickCommandGroupCatalog
                    .CreateSystemGroups()
                    .Select(group => new
                    {
                        id = group.Id,
                        name = group.Name,
                        sortOrder = group.SortOrder,
                        kind = group.Kind.ToString(),
                    }),
                commands = new[]
                {
                    new
                    {
                        id = Guid.NewGuid(),
                        groupId = QuickCommandGroupCatalog.DefaultGroupId,
                        name = "mine",
                        commandText = "pwd",
                        description = "",
                        sortOrder = 0,
                    },
                },
            }
        );

        QuickCommandLoadResult result = await new SonnetDbQuickCommandRepository(store).LoadAsync();

        Assert.IsFalse(result.Migrated);
        Assert.IsEmpty(result.Data.HiddenBuiltInIds);
        Assert.IsTrue(result.Data.Groups.All(group => group.CommandOrder.Count == 0));
        Assert.AreEqual("mine", Assert.ContainsSingle(result.Data.Commands).Name);
    }

    [TestMethod]
    public async Task Engine_Reopen_PersistsDocumentsAndTimeSeries()
    {
        string dir = Path.Combine(_testDirectory, "reopen-db");
        using (var engine = new SonnetDbEngine(dir))
        {
            var repo = new SonnetDbSessionRepository(engine, _protector);
            await repo.SaveSessionAsync(
                new()
                {
                    Name = "persist-me",
                    Host = "h",
                    Username = "u",
                }
            );
            var recents = new SonnetDbRecentConnectionService(engine);
            await recents.RecordAsync(
                new()
                {
                    Name = "r1",
                    Host = "h",
                    Username = "u",
                    Success = true,
                }
            );
        }
        using (var engine = new SonnetDbEngine(dir))
        {
            var repo = new SonnetDbSessionRepository(engine, _protector);
            Assert.AreEqual("persist-me", (await repo.GetAllSessionsAsync()).Single().Name);
            var recents = new SonnetDbRecentConnectionService(engine);
            Assert.HasCount(1, await recents.GetRecentAsync(5));
        }
    }

    private sealed class TestDoc
    {
        public string Value { get; set; } = string.Empty;
    }

    private sealed class LegacyQuickCommandDataProbe
    {
        public string Marker { get; set; } = string.Empty;

        public List<object> Commands { get; set; } = [];
    }

    private sealed class VersionProbe
    {
        public int SchemaVersion { get; set; }

        public string Marker { get; set; } = string.Empty;
    }
}
