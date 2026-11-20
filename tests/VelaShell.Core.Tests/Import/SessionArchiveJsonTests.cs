using System.Text.Json;
using VelaShell.Core.Credentials;
using VelaShell.Core.Import;
using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Import;

/// <summary>VelaShell JSON 连接文件:机密永不以明文落进文件,口令加密的机密能原样解回来,坏数据只坏一条。</summary>
[TestClass]
[TestCategory("SessionArchive")]
public sealed class SessionArchiveJsonTests
{
    private const string Passphrase = "correct horse battery";

    private static SessionArchive SampleArchive(out SessionProfile web, out SessionProfile bastion, out SharedCredential credential)
    {
        var group = new SessionArchiveGroup { Id = Guid.NewGuid(), Name = "生产环境", SortOrder = 3 };
        credential = new SharedCredential
        {
            Id = Guid.NewGuid(),
            Name = "机房 admin",
            Username = "admin",
            AuthMethod = AuthMethod.Password,
            Password = "cred-secret"
        };
        bastion = new SessionProfile
        {
            Name = "bastion",
            Host = "203.0.113.1",
            Username = "jump",
            Password = "bastion-pw",
            GroupId = group.Id,
            LastConnectedAt = DateTime.UtcNow
        };
        web = new SessionProfile
        {
            Name = "web-01",
            Host = "10.0.0.11",
            Port = 2222,
            AuthMethod = AuthMethod.PrivateKey,
            PrivateKeyPath = "~/.ssh/id_ed25519",
            PrivateKeyPassphrase = "key-phrase",
            GroupId = group.Id,
            JumpHostProfileId = bastion.Id,
            Tags = ["web"],
            Notes = "前端\n第二行"
        };
        var bucket = new SessionProfile
        {
            Name = "bucket",
            Host = "s3.example.com",
            Port = 443,
            ConnectionType = ConnectionType.Plugin,
            PluginProtocolId = "velashell.s3",
            PluginSettings = new() { ["region"] = "cn-north-1" },
            PluginSecrets = new() { ["secretKey"] = "s3-secret" },
            CredentialSource = CredentialReference.ForShared(credential.Id)
        };
        return new SessionArchive
        {
            ExportedAtUtc = new DateTime(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc),
            Application = "VelaShell test",
            Groups = [group],
            Sessions = [bastion, web, bucket],
            SharedCredentials = [credential],
            Tunnels = new()
            {
                [web.Id] = [new TunnelConfig { Type = TunnelType.LocalForward, Name = "db", LocalHost = "127.0.0.1", LocalPort = 15432, RemoteHost = "db", RemotePort = 5432 }]
            }
        };
    }

    [TestMethod]
    public void Serialize_WithoutPassphrase_WritesNoSecretAtAll()
    {
        SessionArchive archive = SampleArchive(out _, out _, out _);

        string json = SessionArchiveJson.Serialize(archive, null);

        foreach (string secret in (string[])["bastion-pw", "key-phrase", "s3-secret", "cred-secret"])
        {
            Assert.DoesNotContain(secret, json, $"机密 {secret} 以明文出现在了导出文件里");
        }
        Assert.DoesNotContain("\"secrets\"", json);
        Assert.DoesNotContain("lastConnectedAt", json, "上次连接时间是本机状态,不该导出");
        Assert.AreEqual("bastion-pw", archive.Sessions[0].Password, "导出不能动传进来的对象。");
    }

    [TestMethod]
    public void Serialize_WithPassphrase_EncryptsSecrets_AndUnlockPutsThemBack()
    {
        SessionArchive archive = SampleArchive(out SessionProfile web, out SessionProfile bastion, out SharedCredential credential);

        string json = SessionArchiveJson.Serialize(archive, Passphrase);
        Assert.DoesNotContain("bastion-pw", json, "加了口令也不能有明文机密:它们只在加密段里。");
        Assert.Contains("\"secrets\"", json);

        SessionImportDocument document = SessionArchiveJson.Read("backup.json", json);
        Assert.IsTrue(document.HasEncryptedSecrets);
        Assert.IsFalse(document.SecretsUnlocked);
        Assert.IsNull(document.Candidates.Single(c => c.Profile.Id == bastion.Id).Profile.Password, "没解锁前连接上没有密码。");

        Assert.IsTrue(SessionArchiveJson.TryUnlock(document, Passphrase));

        Assert.IsTrue(document.SecretsUnlocked);
        Assert.AreEqual("bastion-pw", document.Candidates.Single(c => c.Profile.Id == bastion.Id).Profile.Password);
        Assert.AreEqual("key-phrase", document.Candidates.Single(c => c.Profile.Id == web.Id).Profile.PrivateKeyPassphrase);
        Assert.AreEqual("s3-secret", document.Candidates.Single(c => c.Profile.Name == "bucket").Profile.PluginSecrets!["secretKey"]);
        Assert.AreEqual("cred-secret", document.SharedCredentials.Single(c => c.Id == credential.Id).Password);
    }

    [TestMethod]
    public void TryUnlock_WithTheWrongPassphrase_ReturnsFalse_AndChangesNothing()
    {
        SessionArchive archive = SampleArchive(out _, out SessionProfile bastion, out _);
        SessionImportDocument document = SessionArchiveJson.Read("backup.json", SessionArchiveJson.Serialize(archive, Passphrase));

        Assert.IsFalse(SessionArchiveJson.TryUnlock(document, "wrong passphrase"));
        Assert.IsFalse(SessionArchiveJson.TryUnlock(document, string.Empty));

        Assert.IsFalse(document.SecretsUnlocked);
        Assert.IsNull(document.Candidates.Single(c => c.Profile.Id == bastion.Id).Profile.Password);
    }

    [TestMethod]
    public void RoundTrip_KeepsEveryExportedField()
    {
        SessionArchive archive = SampleArchive(out SessionProfile web, out _, out SharedCredential credential);

        SessionImportDocument document = SessionArchiveJson.Read("backup.json", SessionArchiveJson.Serialize(archive, null));

        SessionImportCandidate read = document.Candidates.Single(c => c.Profile.Id == web.Id);
        Assert.IsTrue(read.HasFileId);
        Assert.IsTrue(read.IsValid);
        Assert.AreEqual("web-01", read.Profile.Name);
        Assert.AreEqual(2222, read.Profile.Port);
        Assert.AreEqual(AuthMethod.PrivateKey, read.Profile.AuthMethod);
        Assert.AreEqual(web.JumpHostProfileId, read.Profile.JumpHostProfileId);
        Assert.AreEqual("前端\n第二行", read.Profile.Notes);
        Assert.AreEqual(web.GroupId, read.FileGroupId, "文件里的分组 Id 记在候选上;");
        Assert.IsNull(read.Profile.GroupId, "落到本机哪个分组要导入时才定,候选身上的 GroupId 先清空。");
        Assert.AreEqual("生产环境", read.GroupName);
        Assert.HasCount(1, document.Tunnels[web.Id]);
        Assert.AreEqual(credential.Id, document.SharedCredentials.Single().Id);
        Assert.AreEqual(new DateTime(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc), document.ExportedAtUtc);
    }

    /// <summary>系统密钥库的钥照原样往返(引用不是机密,明文写进文件);认证方式写成名字 <c>keyStore</c>。</summary>
    [TestMethod]
    public void RoundTrip_KeepsAKeyStoreReference()
    {
        SessionProfile tpm = new()
        {
            Name = "tpm",
            Host = "10.0.0.51",
            Username = "ops",
            AuthMethod = AuthMethod.KeyStore,
            KeyStoreKey = new SystemKeyReference
            {
                Provider = SystemKeyReference.PlatformProvider,
                KeyName = "deploy",
                MachineKey = true,
                PublicKey = "ecdsa-sha2-nistp256 AAAA deploy",
            },
            // 系统密钥库可选配的证书:与证书认证同一个字段,照样往返。
            CertificatePath = @"C:\certs\deploy-cert.pub",
        };

        string json = SessionArchiveJson.Serialize(new SessionArchive { Sessions = [tpm] }, null);
        SessionProfile read = SessionArchiveJson.Read("backup.json", json).Candidates.Single().Profile;

        Assert.Contains("\"authMethod\": \"keyStore\"", json);
        Assert.AreEqual(AuthMethod.KeyStore, read.AuthMethod);
        Assert.AreEqual(tpm.KeyStoreKey, read.KeyStoreKey);
        Assert.AreEqual(tpm.CertificatePath, read.CertificatePath);
    }

    [TestMethod]
    public void Serialize_WritesEnumsAsNames_AndReadAcceptsNumbersToo()
    {
        SessionArchive archive = SampleArchive(out _, out _, out _);
        string json = SessionArchiveJson.Serialize(archive, null);

        Assert.Contains("\"authMethod\": \"privateKey\"", json, "认证方式写成名字,人才看得懂");
        Assert.Contains("\"connectionType\": \"plugin\"", json);
        Assert.Contains("生产环境", json, "中文原样写,不转成 \\u 转义");

        const string handWritten = """
            {
              "format": "velashell-sessions",
              "sessions": [ { "name": "old", "host": "h", "authMethod": 1, "connectionType": "SFTP" } ]
            }
            """;
        SessionImportCandidate candidate = SessionArchiveJson.Read("hand.json", handWritten).Candidates.Single();
        Assert.AreEqual(AuthMethod.PrivateKey, candidate.Profile.AuthMethod);
        Assert.AreEqual(ConnectionType.SFTP, candidate.Profile.ConnectionType, "枚举名不区分大小写");
    }

    [TestMethod]
    public void Read_AHandWrittenMinimalFile_Works_WithoutFormatOrIds()
    {
        const string json = """
            {
              // 注释与尾逗号都允许:这是给人手写的文件
              "sessions": [
                { "host": "10.1.1.1", "username": "root" },
                { "name": "db", "host": "10.1.1.2", "port": 3306, },
              ]
            }
            """;

        SessionImportDocument document = SessionArchiveJson.Read("hand.json", json);

        Assert.HasCount(2, document.Candidates);
        Assert.IsFalse(document.Candidates[0].HasFileId, "没写 id 就不能拿生成的那个 id 去查重");
        Assert.AreEqual("10.1.1.1", document.Candidates[0].Profile.Name, "没写名称就用主机");
        Assert.AreEqual(22, document.Candidates[0].Profile.Port);
        Assert.AreEqual(3306, document.Candidates[1].Profile.Port);
    }

    /// <summary>
    /// 分得清「文件没提目录收藏」(目录收藏功能之前的备份、手写文件)与「文件里收藏就是空的」:
    /// 反序列化出来的对象两种都是空列表,覆盖导入时前者要留本机的收藏。
    /// </summary>
    [TestMethod]
    public void Read_TellsAMissingBookmarkFieldFromAnEmptyOne()
    {
        const string json = """
            {
              "sessions": [
                { "name": "old-backup", "host": "10.0.0.1" },
                { "name": "cleared", "host": "10.0.0.2", "directoryBookmarks": [] },
                { "name": "hand-null", "host": "10.0.0.3", "directoryBookmarks": null },
                { "name": "kept", "host": "10.0.0.4", "DirectoryBookmarks": [ { "localPath": "C:\\work", "remotePath": "/srv/app" } ] }
              ]
            }
            """;

        IReadOnlyList<SessionImportCandidate> candidates = SessionArchiveJson.Read("hand.json", json).Candidates;

        Assert.AreSequenceEqual([false, true, false, true], candidates.Select(static c => c.HasDirectoryBookmarks).ToList());
        Assert.IsTrue(candidates.All(static c => c.Profile.DirectoryBookmarks is not null), "收藏列表一律兜成非 null,后面的代码照常用");
        Assert.AreEqual("/srv/app", candidates[3].Profile.DirectoryBookmarks.Single().RemotePath, "字段名不区分大小写,与其它字段一样");

        // 只是读文件这一处:同一份选项写出去的文件照样带着收藏,别处用 Options 读出来的配置也不会是 null。
        SessionProfile roundTrip = JsonSerializer.Deserialize<SessionProfile>(
            JsonSerializer.Serialize(new SessionProfile { Host = "h" }, SessionArchiveJson.Options), SessionArchiveJson.Options)!;
        Assert.IsNotNull(roundTrip.DirectoryBookmarks);
    }

    [TestMethod]
    public void Read_OneBrokenEntry_OnlyBreaksThatEntry()
    {
        const string json = """
            {
              "format": "velashell-sessions",
              "version": 1,
              "sessions": [
                { "name": "ok", "host": "10.0.0.1" },
                { "name": "bad-port", "host": "10.0.0.2", "port": 70000 },
                { "name": "no-host" },
                { "name": "bad-type", "host": "10.0.0.3", "port": "not a number" },
                42
              ]
            }
            """;

        SessionImportDocument document = SessionArchiveJson.Read("mixed.json", json);

        Assert.HasCount(5, document.Candidates);
        Assert.IsTrue(document.Candidates[0].IsValid);
        Assert.IsFalse(document.Candidates[1].IsValid);
        Assert.IsFalse(document.Candidates[2].IsValid);
        Assert.IsFalse(document.Candidates[3].IsValid);
        Assert.IsFalse(document.Candidates[4].IsValid);
        Assert.AreEqual("bad-type", document.Candidates[3].Profile.Name, "反序列化失败的那条也要带着名字,预览里认得出是哪条");
    }

    [TestMethod]
    public void Read_NullsInAHandWrittenFile_BecomeRowErrors_NotACrash()
    {
        const string json = """
            {
              "groups": [ null, { "id": "6f1c1a52-6f43-4a3c-9a4e-3f2b2a1b0c01", "name": null } ],
              "sharedCredentials": [ null, { "id": "6f1c1a52-6f43-4a3c-9a4e-3f2b2a1b0c02", "name": null, "username": null } ],
              "sessions": [
                { "name": null, "host": null, "username": null, "tags": null },
                { "name": "ok", "host": "10.0.0.1", "tags": null }
              ]
            }
            """;

        SessionImportDocument document = SessionArchiveJson.Read("hand.json", json);

        Assert.HasCount(2, document.Candidates);
        Assert.IsFalse(document.Candidates[0].IsValid, "主机为 null 落到「主机为空」这条行级错误上");
        Assert.IsTrue(document.Candidates[1].IsValid);
        Assert.IsEmpty(document.Candidates[1].Profile.Tags);
        Assert.HasCount(1, document.Groups);
        Assert.AreEqual(string.Empty, document.Groups[0].Name);
        Assert.AreEqual(string.Empty, document.SharedCredentials.Single().Username);
        // 规划与写入也不能被这些 null 绊倒。
        SessionImportWriteSet writes = SessionImportPlanner.BuildWrites(SessionImportPlanner.Plan(document, [], [], []), new SessionImportOptions());
        Assert.AreEqual(1, writes.Created);
    }

    [TestMethod]
    public void Version_PreservesFlatCompatibility_AndSignalsEmptyOrNestedGroups()
    {
        var root = new SessionArchiveGroup { Id = Guid.NewGuid(), Name = "Team" };
        var child = new SessionArchiveGroup { Id = Guid.NewGuid(), Name = "Prod", ParentGroupId = root.Id };
        var archive = new SessionArchive { Groups = [root], Sessions = [new() { Host = "example.test", GroupId = root.Id }] };
        Assert.Contains("\"version\": 1", SessionArchiveJson.Serialize(archive, null));
        archive.Sessions.Clear();
        Assert.Contains("\"version\": 2", SessionArchiveJson.Serialize(archive, null));
        archive.Groups.Insert(0, child);
        string json = SessionArchiveJson.Serialize(archive, null);
        Assert.AreEqual(root.Id, SessionArchiveJson.Read("tree.json", json).Groups[0].ParentGroupId);
        Assert.IsNull(SessionArchiveJson.Read("flat.json", json.Replace("\"version\": 2", "\"version\": 1", StringComparison.Ordinal)).Groups[0].ParentGroupId);
        archive.Version = 3;
        Assert.ThrowsExactly<SessionFileFormatException>(() => SessionArchiveJson.Serialize(archive, null));
    }

    [TestMethod]
    public void Validation_RejectsBrokenChainsAndDuplicateIdsBeforeReadingOrWriting()
    {
        var a = new SessionArchiveGroup { Id = Guid.NewGuid() };
        var b = new SessionArchiveGroup { Id = Guid.NewGuid(), ParentGroupId = a.Id };
        var archive = new SessionArchive { Groups = [a, b] };
        void Reject()
        {
            Assert.ThrowsExactly<SessionFileFormatException>(() => SessionArchiveJson.Serialize(archive, null));
            string raw = JsonSerializer.Serialize(archive, SessionArchiveJson.Options);
            Assert.ThrowsExactly<SessionFileFormatException>(() => SessionArchiveJson.Read("bad.json", raw));
        }
        a.ParentGroupId = b.Id;
        Reject();
        a.ParentGroupId = a.Id;
        Reject();
        a.ParentGroupId = Guid.NewGuid();
        Reject();
        a.ParentGroupId = null;
        archive.Groups.Add(a);
        Reject();
        archive.Groups = [a];
        for (int depth = 1; depth <= ServerGroupTree.MaxDepth; depth++)
        {
            archive.Groups.Add(new() { Id = Guid.NewGuid(), ParentGroupId = archive.Groups[^1].Id });
        }
        Assert.IsEmpty(SessionArchiveJson.Validate(archive.Groups));
        archive.Groups.Add(new() { Id = Guid.NewGuid(), ParentGroupId = archive.Groups[^1].Id });
        Reject();
    }

    [TestMethod]
    public void Read_RejectsFilesThatAreNotOurs_OrTooNew()
    {
        Assert.ThrowsExactly<SessionFileFormatException>(() => SessionArchiveJson.Read("x.json", "{ not json"));
        Assert.ThrowsExactly<SessionFileFormatException>(() => SessionArchiveJson.Read("x.json", "[1, 2, 3]"));
        Assert.ThrowsExactly<SessionFileFormatException>(() => SessionArchiveJson.Read("x.json", """{ "format": "other-app", "sessions": [] }"""));
        Assert.ThrowsExactly<SessionFileFormatException>(() => SessionArchiveJson.Read("x.json", """{ "settings": {} }"""));
        Assert.ThrowsExactly<SessionFileFormatException>(() =>
            SessionArchiveJson.Read("x.json", $$"""{ "format": "velashell-sessions", "version": {{SessionArchive.CurrentVersion + 1}}, "sessions": [] }"""));
    }

    [TestMethod]
    public void Read_SecretsWithAnUnknownAlgorithm_AreDropped_WithAWarning()
    {
        const string json = """
            {
              "format": "velashell-sessions",
              "sessions": [ { "name": "a", "host": "h" } ],
              "secrets": { "algorithm": "future-kdf", "data": "AAAA" }
            }
            """;

        SessionImportDocument document = SessionArchiveJson.Read("future.json", json);

        Assert.IsFalse(document.HasEncryptedSecrets);
        Assert.HasCount(1, document.Warnings);
        Assert.HasCount(1, document.Candidates);
    }

    [TestMethod]
    public void CountSecrets_CountsSessionsAndCredentialsThatCarryAny()
    {
        SessionArchive archive = SampleArchive(out _, out _, out _);

        // bastion(密码)、web(私钥口令)、bucket(插件机密)、凭据(密码)
        Assert.AreEqual(4, SessionArchiveJson.CountSecrets(archive));
    }

    [TestMethod]
    public void Serialize_WithPassphraseButNoSecrets_WritesNoSecretsSection()
    {
        var archive = new SessionArchive { Sessions = [new SessionProfile { Name = "a", Host = "h", Password = "" }] };

        string json = SessionArchiveJson.Serialize(archive, Passphrase);

        using var parsed = JsonDocument.Parse(json);
        Assert.IsFalse(parsed.RootElement.TryGetProperty("secrets", out _));
    }

    [TestMethod]
    public void Tailscale_ExportAndEncryptedPayloadDiscardStaleSshMaterials()
    {
        var tail = new SessionProfile
        {
            Name = "tail",
            Host = "tail",
            Username = "deploy",
            AuthMethod = AuthMethod.Tailscale,
            Password = "stale-password",
            PrivateKeyPath = "/stale-key",
            PrivateKeyPassphrase = "stale-phrase",
            CertificatePath = "/stale-cert",
            CredentialSource = CredentialReference.ForShared(Guid.NewGuid()),
            RememberPassword = true,
            JumpHostProfileId = Guid.NewGuid()
        };
        var ordinary = new SessionProfile { Name = "password", Host = "ordinary", Password = "keep-secret" };
        var archive = new SessionArchive { Sessions = [tail, ordinary] };

        Assert.AreEqual(1, SessionArchiveJson.CountSecrets(archive));
        string json = SessionArchiveJson.Serialize(archive, Passphrase);
        Assert.Contains("\"authMethod\": \"tailscale\"", json);
        Assert.DoesNotContain("/stale-key", json);
        Assert.DoesNotContain("/stale-cert", json);
        SessionImportDocument document = SessionArchiveJson.Read("tail.json", json);
        Assert.IsTrue(SessionArchiveJson.TryUnlock(document, Passphrase));
        SessionProfile restored = document.Candidates.Single(c => c.Profile.Id == tail.Id).Profile;

        Assert.AreEqual(AuthMethod.Tailscale, restored.AuthMethod);
        Assert.AreEqual("deploy", restored.Username);
        Assert.AreEqual(tail.JumpHostProfileId, restored.JumpHostProfileId);
        Assert.IsNull(restored.CredentialSource);
        Assert.IsNull(restored.Password);
        Assert.IsNull(restored.PrivateKeyPath);
        Assert.IsNull(restored.PrivateKeyPassphrase);
        Assert.IsNull(restored.CertificatePath);
        Assert.IsFalse(restored.RememberPassword);
        Assert.AreEqual("keep-secret", document.Candidates.Single(c => c.Profile.Id == ordinary.Id).Profile.Password);
        Assert.AreEqual("stale-password", tail.Password);
        Assert.IsNotNull(tail.CredentialSource);

        string numeric = json.Replace("\"authMethod\": \"tailscale\"", "\"authMethod\": 4", StringComparison.Ordinal);
        Assert.AreEqual(AuthMethod.Tailscale, SessionArchiveJson.Read("numeric.json", numeric).Candidates[0].Profile.AuthMethod);
    }
}
