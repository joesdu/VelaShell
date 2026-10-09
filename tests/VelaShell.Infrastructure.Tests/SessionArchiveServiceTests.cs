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
        var engine = new SonnetDbEngine(Path.Combine(directory, "sonnetdb"));
        _engines.Add(engine);
        var protector = new AesSecretProtector(Path.Combine(directory, "secret.key"));
        var sessions = new SonnetDbSessionRepository(engine, protector);
        var credentials = new SonnetDbSharedCredentialRepository(engine, protector);
        var store = new SonnetDbAppDataStore(engine);
        return (new SessionArchiveService(sessions, credentials, store), sessions, credentials, store);
    }

    [TestMethod]
    public async Task ExportWithPassphrase_ThenImportElsewhere_BringsEverythingAcross()
    {
        var (sourceService, sourceSessions, sourceCredentials, sourceStore) = Machine("source");
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

        var (targetService, targetSessions, targetCredentials, targetStore) = Machine("target");
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
        var (service, sessions, _, _) = Machine("csv");
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

    [TestMethod]
    public async Task Template_ImportsIntoAFreshStore()
    {
        var (service, sessions, _, _) = Machine("template");

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
        var (service, _, _, _) = Machine("parse");
        byte[] json = Encoding.UTF8.GetBytes("""{ "format": "velashell-sessions", "sessions": [ { "host": "h" } ] }""");
        byte[] csv = Encoding.UTF8.GetBytes("host\nh\n");

        Assert.AreEqual(SessionFileFormat.Json, service.Parse("a.json", json).Format);
        Assert.AreEqual(SessionFileFormat.Csv, service.Parse("a.csv", csv).Format);
        Assert.AreEqual(SessionFileFormat.Json, service.Parse("no-extension", json).Format);
        Assert.AreEqual(SessionFileFormat.Csv, service.Parse("no-extension", csv).Format);
        Assert.ThrowsExactly<SessionFileFormatException>(() => service.Parse("a.json", csv), "扩展名是 json 就按 json 读,读不了要明说");
    }
}
