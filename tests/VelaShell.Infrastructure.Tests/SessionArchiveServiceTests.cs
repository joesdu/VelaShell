using System.Text;
using VelaShell.Core.Credentials;
using VelaShell.Core.Import;
using VelaShell.Core.Models;
using VelaShell.Infrastructure.Import;
using VelaShell.Infrastructure.Persistence;

namespace VelaShell.Infrastructure.Tests;

/// <summary>
/// 连接导入导出(#571)走真实的 SonnetDB 仓储:从一台「电脑」导出、在另一台(另一个库、另一把机器密钥)导入,
/// 分组、跳板、共享凭据、隧道与加密的密码都要原样到位;再导一次同一份文件,默认什么都不该重复。
/// </summary>
[TestClass]
[TestCategory("SessionArchive")]
public sealed class SessionArchiveServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"velashell_archive_{Guid.NewGuid():N}");
    private readonly List<SonnetDbEngine> _engines = [];

    public void Dispose()
    {
        foreach (SonnetDbEngine engine in _engines)
        {
            engine.Dispose();
        }
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    /// <summary>一台「电脑」:自己的库、自己的机器密钥。</summary>
    private (SessionArchiveService Service, SonnetDbSessionRepository Sessions, SonnetDbSharedCredentialRepository Credentials, SonnetDbAppDataStore Store) Machine(string name)
    {
        string directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        SonnetDbEngine engine = SonnetDbTestTemplate.Open(Path.Combine(directory, "sonnetdb"));
        _engines.Add(engine);
        var protector = new AesSecretProtector(Path.Combine(directory, "secret.key"));
        var sessions = new SonnetDbSessionRepository(engine, protector);
        var credentials = new SonnetDbSharedCredentialRepository(engine, protector);
        var store = new SonnetDbAppDataStore(engine);
        return (new SessionArchiveService(sessions, credentials, store), sessions, credentials, store);
    }

    [TestMethod]
    public async Task GroupOnlyExport_PreviewAndArchiveContainTheSameSubtree_AndRestoreItOnce()
    {
        (SessionArchiveService? source, SonnetDbSessionRepository? repo, SonnetDbSharedCredentialRepository _, SonnetDbAppDataStore _) = Machine("source");
        var root = new ServerGroup { Name = "Root" };
        var child = new ServerGroup { Name = "Child", ParentGroupId = root.Id };
        var empty = new ServerGroup { Name = "Empty", ParentGroupId = child.Id };
        var other = new ServerGroup { Name = "Other" };
        foreach (ServerGroup group in new[] { root, child, empty, other })
        {
            await repo.SaveGroupAsync(group);
        }
        var scope = new SessionExportScope([], child.Id);
        SessionExportPreview preview = await source.PreviewExportAsync(scope);
        SessionExportFile file = await source.ExportAsync(scope, SessionFileFormat.Json, null);
        (SessionArchiveService? target, SonnetDbSessionRepository? targetRepo, SonnetDbSharedCredentialRepository _, SonnetDbAppDataStore _) = Machine("target");
        SessionImportDocument doc = target.Parse("empty.json", file.Content);
        Assert.AreEqual(0, preview.SessionCount);
        Assert.AreEqual(3, preview.GroupCount);
        Assert.HasCount(preview.GroupCount, doc.Groups);
        Assert.AreSequenceEqual([root.Id, child.Id, empty.Id], [.. doc.Groups.Select(g => g.Id)]);
        var options = new SessionImportOptions();
        Assert.AreEqual(3, (await target.ImportAsync(await target.PlanAsync(doc), options)).GroupsCreated);
        Assert.AreEqual(0, (await target.ImportAsync(await target.PlanAsync(doc), options)).GroupsCreated);
        Assert.AreEqual("Root > Child > Empty", new ServerGroupTree(await targetRepo.GetAllGroupsAsync()).PathOf(empty.Id));
    }

    [TestMethod]
    public async Task ExportWithPassphrase_ThenImportElsewhere_BringsEverythingAcross()
    {
        (SessionArchiveService? sourceService, SonnetDbSessionRepository? sourceSessions, SonnetDbSharedCredentialRepository? sourceCredentials, SonnetDbAppDataStore? sourceStore) = Machine("source");
        var group = new ServerGroup { Name = "生产环境", SortOrder = 0 };
        var credential = new SharedCredential { Name = "ops", Username = "ops", Password = "cred-pw" };
        var bastion = new SessionProfile { Name = "bastion", Host = "203.0.113.1", Username = "jump", Password = "bastion-pw", GroupId = group.Id };
        var app = new SessionProfile
        {
            Name = "app",
            Host = "10.0.0.5",
            GroupId = group.Id,
            JumpHostProfileId = bastion.Id,
            CredentialSource = CredentialReference.ForShared(credential.Id),
            Terminal = new TerminalOverrides { Encoding = "GBK" }
        };
        await sourceSessions.SaveGroupAsync(group);
        await sourceCredentials.SaveAsync(credential);
        await sourceSessions.SaveSessionAsync(bastion);
        await sourceSessions.SaveSessionAsync(app);
        await sourceStore.UpsertAsync("tunnels", app.Id.ToString("D"), new List<TunnelConfig>
        {
            new() { Type = TunnelType.LocalForward, Name = "db", LocalHost = "127.0.0.1", LocalPort = 15432, RemoteHost = "db", RemotePort = 5432 }
        });

        // 只选 app:跳板机自动带上。
        SessionExportPreview preview = await sourceService.PreviewExportAsync([app.Id]);
        Assert.AreEqual(2, preview.SessionCount);
        Assert.AreEqual(1, preview.DependencyCount);
        Assert.AreEqual(2, preview.SecretCount, "bastion 的密码 + 共享凭据的密码");
        SessionExportFile file = await sourceService.ExportAsync([app.Id], SessionFileFormat.Json, "correct horse battery");
        Assert.IsTrue(file.SecretsIncluded);
        Assert.DoesNotContain("bastion-pw", Encoding.UTF8.GetString(file.Content));

        (SessionArchiveService? targetService, SonnetDbSessionRepository? targetSessions, SonnetDbSharedCredentialRepository? targetCredentials, SonnetDbAppDataStore? targetStore) = Machine("target");
        SessionImportDocument document = targetService.Parse("backup.json", file.Content);
        Assert.IsTrue(SessionArchiveJson.TryUnlock(document, "correct horse battery"));
        SessionImportPlan plan = await targetService.PlanAsync(document);
        SessionFileImportOutcome outcome = await targetService.ImportAsync(plan, new SessionImportOptions());

        Assert.AreEqual(2, outcome.Created);
        Assert.AreEqual(1, outcome.GroupsCreated);
        Assert.IsEmpty(outcome.Warnings);
        List<SessionProfile> imported = await targetSessions.GetAllSessionsAsync();
        SessionProfile importedBastion = imported.Single(s => s.Name == "bastion");
        SessionProfile importedApp = imported.Single(s => s.Name == "app");
        Assert.AreEqual(bastion.Id, importedBastion.Id, "Id 不冲突就沿用:两台开着云同步的电脑之间看到的仍是同一条");
        Assert.AreEqual("bastion-pw", importedBastion.Password, "解锁后的密码由目标机器自己的密钥重新加密落盘");
        Assert.AreEqual(importedBastion.Id, importedApp.JumpHostProfileId);
        Assert.AreEqual("GBK", importedApp.Terminal!.Encoding);
        ServerGroup importedGroup = (await targetSessions.GetAllGroupsAsync()).Single();
        Assert.AreEqual("生产环境", importedGroup.Name);
        Assert.AreEqual(importedGroup.Id, importedApp.GroupId);
        SharedCredential importedCredential = (await targetCredentials.GetAllAsync()).Single();
        Assert.AreEqual("cred-pw", importedCredential.Password);
        Assert.IsTrue(importedApp.CredentialSource!.TryGetSharedId(out Guid credentialId) && credentialId == importedCredential.Id);
        List<TunnelConfig>? tunnels = await targetStore.GetAsync<List<TunnelConfig>>("tunnels", importedApp.Id.ToString("D"));
        Assert.AreEqual("db", tunnels!.Single().Name);

        // 同一份文件再导一次:默认「跳过」,一条都不重复。
        SessionFileImportOutcome again = await targetService.ImportAsync(await targetService.PlanAsync(targetService.Parse("backup.json", file.Content)), new SessionImportOptions());
        Assert.AreEqual(0, again.Created);
        Assert.AreEqual(2, again.Skipped);
        Assert.HasCount(2, await targetSessions.GetAllSessionsAsync());
        Assert.HasCount(1, await targetCredentials.GetAllAsync());
    }

    [TestMethod]
    public async Task CsvRoundTrip_EditedInExcel_OverwritesOnlyTheEditedColumns()
    {
        (SessionArchiveService? service, SonnetDbSessionRepository? sessions, SonnetDbSharedCredentialRepository _, SonnetDbAppDataStore _) = Machine("csv");
        var profile = new SessionProfile
        {
            Name = "switch-01",
            Host = "192.168.1.1",
            Username = "admin",
            Password = "keep-me",
            Terminal = new TerminalOverrides { Encoding = "GBK" }
        };
        await sessions.SaveSessionAsync(profile);

        SessionExportFile file = await service.ExportAsync([profile.Id], SessionFileFormat.Csv, "ignored for csv");
        Assert.IsFalse(file.SecretsIncluded);
        string csv = SessionFileText.Decode(file.Content);
        Assert.DoesNotContain("keep-me", csv);
        // 「在 Excel 里」把用户名改掉。
        string edited = csv.Replace(",admin,", ",netops,", StringComparison.Ordinal);

        SessionImportPlan plan = await service.PlanAsync(service.Parse("edited.csv", SessionFileText.EncodeCsv(edited)));
        Assert.AreEqual(SessionImportMatch.SameId, plan.Items.Single().Match);
        SessionFileImportOutcome outcome = await service.ImportAsync(plan, new SessionImportOptions { Conflict = SessionImportConflict.Overwrite });

        Assert.AreEqual(1, outcome.Updated);
        SessionProfile updated = (await sessions.GetAllSessionsAsync()).Single();
        Assert.AreEqual("netops", updated.Username);
        Assert.AreEqual("keep-me", updated.Password, "CSV 里密码列是空的:覆盖时不能把密码清掉");
        Assert.AreEqual("GBK", updated.Terminal!.Encoding, "CSV 表达不了的设置原样保留");
    }

    /// <summary>
    /// 覆盖导入一份目录收藏功能之前的 JSON 备份(连接里没有收藏字段):本机的收藏留着;
    /// 文件里写着空数组的,才是真的清空 —— 与 Gist 拉取同一口径。
    /// </summary>
    [TestMethod]
    public async Task OverwriteImport_OfABackupWithoutBookmarks_KeepsTheLocalOnes_ButAnEmptyFieldClearsThem()
    {
        (SessionArchiveService? service, SonnetDbSessionRepository? sessions, SonnetDbSharedCredentialRepository _, SonnetDbAppDataStore _) = Machine("bookmarks");
        var profile = new SessionProfile { Name = "web", Host = "10.0.0.5", Username = "deploy" };
        await sessions.SaveSessionAsync(profile);
        SessionExportFile file = await service.ExportAsync([profile.Id], SessionFileFormat.Json, null);
        // 备份导出之后才加的收藏(也就是用户在本机上的近况)。
        _ = await sessions.UpdateDirectoryBookmarksAsync(profile.Id, list =>
        {
            list.Add(new RemoteDirectoryBookmark { Name = "app", LocalPath = @"C:\work", RemotePath = "/srv/app" });
            return true;
        });

        string oldBackup = WithoutBookmarkField(SessionFileText.Decode(file.Content), "renamed-by-old-backup");
        SessionFileImportOutcome outcome = await service.ImportAsync(
            await service.PlanAsync(service.Parse("old.json", Encoding.UTF8.GetBytes(oldBackup))),
            new SessionImportOptions { Conflict = SessionImportConflict.Overwrite });

        Assert.AreEqual(1, outcome.Updated);
        SessionProfile afterOld = (await sessions.GetAllSessionsAsync()).Single();
        Assert.AreEqual("renamed-by-old-backup", afterOld.Name, "其余字段照样被备份覆盖");
        Assert.AreEqual("/srv/app", afterOld.DirectoryBookmarks.Single().RemotePath, "旧备份没带收藏字段,却把本机的收藏清空了");

        // 新版本导出的文件带着 "directoryBookmarks": [](导出那一刻确实没有收藏):覆盖就是清空。
        Assert.Contains("\"directoryBookmarks\": []", SessionFileText.Decode(file.Content));
        _ = await service.ImportAsync(
            await service.PlanAsync(service.Parse("new.json", file.Content)),
            new SessionImportOptions { Conflict = SessionImportConflict.Overwrite });
        Assert.IsEmpty((await sessions.GetAllSessionsAsync()).Single().DirectoryBookmarks);
    }

    /// <summary>把一份导出文件改成旧版本的样子:连接里没有目录收藏字段;顺手改个名字,好看出覆盖确实发生了。</summary>
    private static string WithoutBookmarkField(string json, string newName)
    {
        System.Text.Json.Nodes.JsonNode root = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        foreach (System.Text.Json.Nodes.JsonNode? session in root["sessions"]!.AsArray())
        {
            System.Text.Json.Nodes.JsonObject entry = session!.AsObject();
            Assert.IsTrue(entry.Remove("directoryBookmarks"), "导出文件里本该有收藏字段");
            entry["name"] = newName;
        }
        return root.ToJsonString();
    }

    [TestMethod]
    public async Task Template_ImportsIntoAFreshStore()
    {
        (SessionArchiveService? service, SonnetDbSessionRepository? sessions, SonnetDbSharedCredentialRepository _, SonnetDbAppDataStore _) = Machine("template");

        SessionImportDocument document = service.Parse("template.csv", service.CreateCsvTemplate());
        SessionFileImportOutcome outcome = await service.ImportAsync(await service.PlanAsync(document), new SessionImportOptions());

        Assert.AreEqual(2, outcome.Created);
        List<SessionProfile> imported = await sessions.GetAllSessionsAsync();
        SessionProfile db = imported.Single(s => s.Name == "example-db");
        Assert.AreEqual(imported.Single(s => s.Name == "example-web").Id, db.JumpHostProfileId);
        Assert.AreEqual(AuthMethod.PrivateKey, db.AuthMethod);
    }

    [TestMethod]
    public void Parse_PicksTheFormatByExtension_ThenByContent()
    {
        (SessionArchiveService? service, SonnetDbSessionRepository _, SonnetDbSharedCredentialRepository _, SonnetDbAppDataStore _) = Machine("parse");
        byte[] json = Encoding.UTF8.GetBytes("""{ "format": "velashell-sessions", "sessions": [ { "host": "h" } ] }""");
        byte[] csv = Encoding.UTF8.GetBytes("host\nh\n");

        Assert.AreEqual(SessionFileFormat.Json, service.Parse("a.json", json).Format);
        Assert.AreEqual(SessionFileFormat.Csv, service.Parse("a.csv", csv).Format);
        Assert.AreEqual(SessionFileFormat.Json, service.Parse("no-extension", json).Format);
        Assert.AreEqual(SessionFileFormat.Csv, service.Parse("no-extension", csv).Format);
        Assert.ThrowsExactly<SessionFileFormatException>(() => service.Parse("a.json", csv), "扩展名是 json 就按 json 读,读不了要明说");
    }
}
