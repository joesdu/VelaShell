using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Import;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Audit;
using VelaShell.Infrastructure.Import;
using VelaShell.Infrastructure.Persistence;

namespace VelaShell.Infrastructure.Tests.Audit;

/// <summary>
/// 配置类审计:连接配置、设置、共享凭据的改动在落盘这一层记下来 —— 只记哪一块变了,不记值;
/// 没改到东西的保存(连接成功的回写、窗口尺寸、云同步拉下来一样的)不留记录。
/// </summary>
[TestClass]
[TestCategory("DataStore")]
public sealed class ConfigAuditTests : IDisposable
{
    private const string Secret = "hunter2-must-never-reach-the-audit-log";

    private readonly SonnetDbEngine _engine;
    private readonly AesSecretProtector _protector;
    private readonly string _directory;
    private readonly RecordingAuditLog _audit = new();

    public ConfigAuditTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"velashell_audit_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _engine = SonnetDbTestTemplate.Open(Path.Combine(_directory, "sonnetdb"));
        _protector = new(Path.Combine(_directory, "secret.key"));
    }

    public void Dispose()
    {
        _engine.Dispose();
        Directory.Delete(_directory, true);
    }

    // ———————————————————— 连接配置 ————————————————————

    [TestMethod]
    public async Task Profile_CreateUpdateDelete_EachLeaveOneRecord()
    {
        AuditedSessionRepository repo = Sessions();
        var profile = new SessionProfile { Name = "web-prod", Host = "10.0.0.1", Port = 22, Username = "root", Password = Secret };

        await repo.SaveSessionAsync(profile);
        AuditEntry created = _audit.Single();
        Assert.AreEqual(AuditCategories.Profile, created.Category);
        Assert.AreEqual(AuditActions.ProfileCreated, created.Action);
        Assert.AreEqual(profile.Id, created.ProfileId, "记录里带着配置 Id,查看器据此打开配置");
        Assert.AreEqual("web-prod · root@10.0.0.1:22", created.Detail);

        _audit.Entries.Clear();
        profile.Host = "10.0.0.2";
        await repo.SaveSessionAsync(profile);
        AuditEntry updated = _audit.Single();
        Assert.AreEqual(AuditActions.ProfileUpdated, updated.Action);
        Assert.AreEqual(
            $"web-prod · root@10.0.0.1:22 → root@10.0.0.2:22 · {Strings.Format("AuditLog_DetailChanged", Strings.Get("AuditLog_AreaAddress"))}",
            updated.Detail);

        _audit.Entries.Clear();
        await repo.DeleteSessionAsync(profile.Id);
        AuditEntry deleted = _audit.Single();
        Assert.AreEqual(AuditActions.ProfileDeleted, deleted.Action);
        Assert.AreEqual("web-prod · root@10.0.0.2:22", deleted.Detail);
    }

    [TestMethod]
    public async Task Profile_PasswordChange_IsRecordedAsCredentials_WithoutTheValue()
    {
        AuditedSessionRepository repo = Sessions();
        var profile = new SessionProfile { Name = "db", Host = "db.internal", Username = "ops", Password = "old" };
        await repo.SaveSessionAsync(profile);
        _audit.Entries.Clear();

        profile.Password = Secret;
        await repo.SaveSessionAsync(profile);

        AuditEntry entry = _audit.Single();
        Assert.Contains(Strings.Get("AuditLog_AreaCredentials"), entry.Detail);
        Assert.DoesNotContain(Secret, entry.Detail);
    }

    [TestMethod]
    public async Task Profile_SavesThatChangeNothingReal_LeaveNoRecord()
    {
        AuditedSessionRepository repo = Sessions();
        var profile = new SessionProfile { Name = "web", Host = "10.0.0.1", Username = "root" };
        await repo.SaveSessionAsync(profile);
        _audit.Entries.Clear();

        // 连接成功的回写、置顶、原样再存一次(云同步拉下来的一模一样)。
        profile.LastConnectedAt = DateTime.UtcNow;
        await repo.SaveSessionAsync(profile);
        profile.IsPinned = true;
        await repo.SaveSessionAsync(profile);
        await repo.ReplaceSessionAsync(profile.Clone());

        Assert.IsEmpty(_audit.Entries);
    }

    /// <summary>
    /// 引用了共享凭据的配置,仓储落盘前会清掉认证材料 —— 内存里那份手输的密码没落盘,不能记成「改了登录凭据」。
    /// </summary>
    [TestMethod]
    public async Task Profile_ComparesWhatWasPersisted_NotWhatWasPassedIn()
    {
        AuditedSessionRepository repo = Sessions();
        var profile = new SessionProfile
        {
            Name = "edge",
            Host = "10.0.0.9",
            Username = "root",
            CredentialSource = CredentialReference.ForShared(Guid.NewGuid())
        };
        await repo.SaveSessionAsync(profile);
        _audit.Entries.Clear();

        profile.Password = Secret; // 登录框里手输的,这次连接用
        await repo.SaveSessionAsync(profile);

        Assert.IsEmpty(_audit.Entries);
    }

    [TestMethod]
    public async Task Profile_AnAuditThatCannotBeWritten_DoesNotFailTheSave()
    {
        var repo = new AuditedSessionRepository(new SonnetDbSessionRepository(_engine, _protector), new FailingAuditLog());
        var profile = new SessionProfile { Name = "web", Host = "h" };

        await repo.SaveSessionAsync(profile);

        Assert.IsNotNull(await repo.GetSessionAsync(profile.Id));
    }

    // ———————————————————— 共享凭据 ————————————————————

    [TestMethod]
    public async Task Credential_PasswordChange_RecordsHowManyConnectionsUseIt()
    {
        AuditedSessionRepository sessions = Sessions();
        AuditedSharedCredentialRepository credentials = Credentials(sessions);
        var credential = new SharedCredential { Name = "ops-root", Username = "root", Password = "old" };
        await credentials.SaveAsync(credential);
        Assert.AreEqual(AuditActions.CredentialCreated, _audit.Single().Action);
        foreach (string host in new[] { "a", "b", "c" })
        {
            await sessions.SaveSessionAsync(new SessionProfile { Name = host, Host = host, CredentialSource = CredentialReference.ForShared(credential.Id) });
        }
        _audit.Entries.Clear();

        credential.Password = Secret;
        await credentials.SaveAsync(credential);

        AuditEntry entry = _audit.Single();
        Assert.AreEqual(AuditCategories.Credential, entry.Category);
        Assert.AreEqual(AuditActions.CredentialUpdated, entry.Action);
        Assert.AreEqual(
            $"ops-root · {Strings.Format("AuditLog_DetailChanged", Strings.Get("AuditLog_AreaCredentials"))} · {Strings.Format("AuditLog_DetailCredentialUsers", 3)}",
            entry.Detail);
        Assert.DoesNotContain(Secret, entry.Detail);
    }

    [TestMethod]
    public async Task Credential_UnchangedSave_LeavesNoRecord_AndDeleteIsRecorded()
    {
        AuditedSharedCredentialRepository credentials = Credentials(Sessions());
        var credential = new SharedCredential { Name = "ops", Username = "root", Password = "pw" };
        await credentials.SaveAsync(credential);
        _audit.Entries.Clear();

        await credentials.SaveAsync(credential.Clone());
        Assert.IsEmpty(_audit.Entries);

        await credentials.DeleteAsync(credential.Id);
        AuditEntry deleted = _audit.Single();
        Assert.AreEqual(AuditActions.CredentialDeleted, deleted.Action);
        Assert.AreEqual("ops", deleted.Detail, "没人引用时不写连接数");
    }

    [TestMethod]
    public async Task Credential_ChangedEvent_StillReachesSubscribers()
    {
        AuditedSharedCredentialRepository credentials = Credentials(Sessions());
        int raised = 0;
        credentials.Changed += (_, _) => raised++;

        await credentials.SaveAsync(new SharedCredential { Name = "x" });

        Assert.AreEqual(1, raised);
    }

    // ———————————————————— 设置 ————————————————————

    [TestMethod]
    public async Task Settings_RecordWhichCategoriesChanged_NotTheValues()
    {
        var service = new AuditedSettingsService(new MemorySettingsService(), _audit);
        AppSettings settings = await service.GetSettingsAsync();

        settings.Theme = "light";
        settings.Proxy.Password = Secret;
        await service.SaveSettingsAsync(settings);

        AuditEntry entry = _audit.Single();
        Assert.AreEqual(AuditCategories.Settings, entry.Category);
        Assert.AreEqual(AuditActions.SettingsChanged, entry.Action);
        Assert.AreEqual(
            SettingsCategories.JoinLabels([SettingsCategories.Find("appearance")!, SettingsCategories.Find("proxy")!]),
            entry.Detail);
        Assert.DoesNotContain(Secret, entry.Detail);
    }

    [TestMethod]
    public async Task Settings_RuntimeStateOnly_LeavesNoRecord()
    {
        var service = new AuditedSettingsService(new MemorySettingsService(), _audit);
        AppSettings settings = await service.GetSettingsAsync();

        // 每次退出都会写的窗口尺寸与上次打开的会话。
        settings.Appearance.LastWindowWidth = 1600;
        settings.General.LastOpenProfileIds = [Guid.NewGuid()];
        await service.SaveSettingsAsync(settings);
        await service.SaveSettingsAsync(settings);

        Assert.IsEmpty(_audit.Entries);
    }

    [TestMethod]
    public async Task Settings_SavedEvent_AndSnapshot_StillComeFromTheInnerService()
    {
        var service = new AuditedSettingsService(new MemorySettingsService(), _audit);
        AppSettings? saved = null;
        service.SettingsSaved += s => saved = s;
        AppSettings settings = await service.GetSettingsAsync();
        settings.TerminalFontSize = 19;

        await service.SaveSettingsAsync(settings);

        Assert.AreSame(settings, saved);
        Assert.AreEqual(19, service.CurrentSnapshot?.TerminalFontSize);
    }

    // ———————————————————— 来源 ————————————————————

    [TestMethod]
    public async Task Source_IsLocalByDefault_AndFollowsTheScope_IncludingNesting()
    {
        AuditedSessionRepository repo = Sessions();
        var profile = new SessionProfile { Name = "web", Host = "10.0.0.1" };

        await repo.SaveSessionAsync(profile);
        Assert.AreEqual(AuditSources.Local, _audit.Single().Source, "没设作用域的保存记成本机编辑");

        _audit.Entries.Clear();
        using (AuditSourceScope.Enter(AuditSources.CloudSync))
        {
            profile.Host = "10.0.0.2";
            await repo.SaveSessionAsync(profile);
            using (AuditSourceScope.Enter(AuditSources.Import))
            {
                profile.Host = "10.0.0.3";
                await repo.SaveSessionAsync(profile);
            }
            profile.Host = "10.0.0.4";
            await repo.SaveSessionAsync(profile);
        }
        profile.Host = "10.0.0.5";
        await repo.SaveSessionAsync(profile);

        Assert.AreSequenceEqual(
            [AuditSources.CloudSync, AuditSources.Import, AuditSources.CloudSync, AuditSources.Local],
            [.. _audit.Entries.Select(e => e.Source)],
            "嵌套的作用域退出后回到外层的来源,全部退出后回到本机");
    }

    /// <summary>
    /// 用 AsyncLocal 而不是静态开关的原因:云同步在后台应用载荷的同时,用户在编辑框里手改的那一条不能记成「云同步」。
    /// </summary>
    [TestMethod]
    public async Task Source_DoesNotLeakIntoAConcurrentFlow()
    {
        AuditedSessionRepository repo = Sessions();
        var synced = new SessionProfile { Name = "synced", Host = "10.0.0.1" };
        var edited = new SessionProfile { Name = "edited", Host = "10.0.0.2" };
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var sync = Task.Run(async () =>
        {
            using IDisposable _ = AuditSourceScope.Enter(AuditSources.CloudSync);
            entered.SetResult();
            await gate.Task;
            await repo.SaveSessionAsync(synced);
        });
        await entered.Task;
        await repo.SaveSessionAsync(edited); // 同步进行到一半时,本机的一次手改
        gate.SetResult();
        await sync;

        Assert.AreEqual(AuditSources.Local, _audit.Entries.Single(e => e.ProfileId == edited.Id).Source);
        Assert.AreEqual(AuditSources.CloudSync, _audit.Entries.Single(e => e.ProfileId == synced.Id).Source);
        Assert.AreEqual(AuditSources.Local, AuditSourceScope.Current);
    }

    [TestMethod]
    public async Task Source_SettingsAndCredentials_CarryTheScopeToo()
    {
        var settingsService = new AuditedSettingsService(new MemorySettingsService(), _audit);
        AuditedSharedCredentialRepository credentials = Credentials(Sessions());
        AppSettings settings = await settingsService.GetSettingsAsync();

        using (AuditSourceScope.Enter(AuditSources.CloudSync))
        {
            settings.Theme = "light";
            await settingsService.SaveSettingsAsync(settings);
        }
        using (AuditSourceScope.Enter(AuditSources.LoginPrompt))
        {
            await credentials.SaveAsync(new SharedCredential { Name = "ops", Username = "root", Password = "pw" });
        }

        Assert.AreSequenceEqual([AuditSources.CloudSync, AuditSources.LoginPrompt], [.. _audit.Entries.Select(e => e.Source)]);
    }

    /// <summary>导入连接文件:凭据与连接的每一条都记成「导入」,导入完回到本机。</summary>
    [TestMethod]
    public async Task Source_ConnectionFileImport_IsRecordedAsImport()
    {
        AuditedSessionRepository sessions = Sessions();
        var service = new SessionArchiveService(sessions, Credentials(sessions));

        SessionImportDocument document = service.Parse("template.csv", service.CreateCsvTemplate());
        SessionFileImportOutcome outcome = await service.ImportAsync(await service.PlanAsync(document), new SessionImportOptions());

        Assert.AreEqual(2, outcome.Created);
        Assert.IsNotEmpty(_audit.Entries);
        Assert.IsTrue(_audit.Entries.All(e => e.Source == AuditSources.Import),
            string.Join(" | ", _audit.Entries.Select(e => $"{e.Action}: {e.Source}")));
        Assert.AreEqual(AuditSources.Local, AuditSourceScope.Current);
    }

    /// <summary>从 Xshell / WinSCP / ssh_config 导入会话都经 <c>SessionImportWriter</c>:同样记成「导入」。</summary>
    [TestMethod]
    public async Task Source_SessionImportFromOtherTools_IsRecordedAsImport()
    {
        var service = new SshConfigImportService(Sessions());

        await service.ImportAsync(
            [new ImportedSession { Name = "prod", Host = "10.0.0.5" }, new ImportedSession { Name = "jump", Host = "10.0.0.6" }],
            "ssh_config");

        Assert.HasCount(2, _audit.Entries);
        Assert.IsTrue(_audit.Entries.All(e => e.Source == AuditSources.Import));
    }

    // ———————————————————— 删除分组 ————————————————————

    /// <summary>
    /// 删除分组时组里还剩的配置被仓储移回未分组(内部批量改写,不经 SaveSessionAsync):记一条汇总,不逐条记;
    /// 组里已经空了(会话树先删了组内连接)就不记。
    /// </summary>
    [TestMethod]
    public async Task DeleteGroup_WithMembersLeft_RecordsOneSummary_EmptyGroupRecordsNothing()
    {
        AuditedSessionRepository repo = Sessions();
        var group = new ServerGroup { Name = "生产" };
        var empty = new ServerGroup { Name = "空组" };
        await repo.SaveGroupAsync(group);
        await repo.SaveGroupAsync(empty);
        foreach (string host in new[] { "a", "b", "c" })
        {
            await repo.SaveSessionAsync(new SessionProfile { Name = host, Host = host, GroupId = group.Id });
        }
        await repo.SaveSessionAsync(new SessionProfile { Name = "other", Host = "o" });
        _audit.Entries.Clear();

        await repo.DeleteGroupAsync(empty.Id);
        Assert.IsEmpty(_audit.Entries);

        await repo.DeleteGroupAsync(group.Id);
        AuditEntry entry = _audit.Single();
        Assert.AreEqual(AuditCategories.Profile, entry.Category);
        Assert.AreEqual(AuditActions.ProfilesUngrouped, entry.Action);
        Assert.IsNull(entry.ProfileId);
        Assert.AreEqual(Strings.Format("AuditLog_DetailProfilesUngrouped", "生产", 3), entry.Detail);
        Assert.AreEqual(AuditSources.Local, entry.Source);
        Assert.IsTrue((await repo.GetAllSessionsAsync()).All(p => p.GroupId is null), "配置照样移回了未分组");
    }

    private AuditedSessionRepository Sessions() => new(new SonnetDbSessionRepository(_engine, _protector), _audit);

    private AuditedSharedCredentialRepository Credentials(ISessionRepository sessions) =>
        new(new SonnetDbSharedCredentialRepository(_engine, _protector), sessions, _audit);

    private sealed class RecordingAuditLog : IAuditLogService
    {
        public List<AuditEntry> Entries { get; } = [];

        public AuditEntry Single()
        {
            Assert.HasCount(1, Entries, string.Join(" | ", Entries.Select(e => $"{e.Action}: {e.Detail}")));
            return Entries[0];
        }

        public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<List<AuditEntry>> QueryAsync(int limit, string? category = null, DateTimeOffset? from = null,
            DateTimeOffset? to = null, CancellationToken cancellationToken = default) => Task.FromResult(Entries.ToList());

        public Task DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>
    /// 内存里的设置服务:真的 <c>SonnetDbSettingsService</c> 保存时会把外观镜像进本机 <c>~/.velashell</c>
    /// 的启动外观文件,拿它测「主题改成亮色」会改掉跑测试这台机器上的真设置。快照与独立实例的口径照搬它。
    /// </summary>
    private sealed class MemorySettingsService : ISettingsService
    {
        private string _json = System.Text.Json.JsonSerializer.Serialize(new AppSettings());

        public event Action<AppSettings>? SettingsSaved;

        public AppSettings? CurrentSnapshot { get; private set; }

        public Task<AppSettings> GetSettingsAsync()
        {
            AppSettings settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(_json)!;
            settings.Normalize();
            CurrentSnapshot ??= System.Text.Json.JsonSerializer.Deserialize<AppSettings>(_json);
            return Task.FromResult(settings);
        }

        public Task SaveSettingsAsync(AppSettings settings)
        {
            _json = System.Text.Json.JsonSerializer.Serialize(settings);
            CurrentSnapshot = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(_json);
            SettingsSaved?.Invoke(settings);
            return Task.CompletedTask;
        }

        public Task<AppState> GetStateAsync() => Task.FromResult(new AppState());

        public Task SaveStateAsync(AppState state) => Task.CompletedTask;
    }

    private sealed class FailingAuditLog : IAuditLogService
    {
        public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default) =>
            throw new IOException("disk full");

        public Task<List<AuditEntry>> QueryAsync(int limit, string? category = null, DateTimeOffset? from = null,
            DateTimeOffset? to = null, CancellationToken cancellationToken = default) => Task.FromResult(new List<AuditEntry>());

        public Task DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
