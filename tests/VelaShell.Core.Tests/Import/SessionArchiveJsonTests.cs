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

        using JsonDocument parsed = JsonDocument.Parse(json);
        Assert.IsFalse(parsed.RootElement.TryGetProperty("secrets", out _));
    }
}
