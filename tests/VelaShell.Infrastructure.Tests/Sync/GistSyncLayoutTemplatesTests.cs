using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Layouts;
using VelaShell.Core.Models;
using VelaShell.Core.Sync;
using VelaShell.Infrastructure.Sync;

namespace VelaShell.Infrastructure.Tests.Sync;

/// <summary>
/// 布局模板的云同步:Gist 里单独一个文件、按模板合并。核心是旧客户端那条路 ——
/// 它只认识 <c>velashell-sync.json</c>,拉下来、再推上去,也碰不到旁边的模板文件。
/// </summary>
/// <remarks>
/// Gist 用一个「文件名 → 内容」的字典模拟,推送按 GitHub「更新 Gist」的约定只改请求里列出的文件。
/// 旧客户端 = 不带模板仓储的 <see cref="GistSyncService" />:它的 <see cref="GistSyncService.BuildGistContentAsync" />
/// 与旧版本是同一段代码,推送时也只写主载荷那一个文件(旧版本的 PushCoreAsync 就是这样)。
/// </remarks>
[TestClass]
public sealed class GistSyncLayoutTemplatesTests
{
    private const string SyncFile = "velashell-sync.json";
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private sealed class MemoryStore : IAppDataStore
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly ConcurrentDictionary<(string Collection, string Id), string> _rows = new();

        public Task<T?> GetAsync<T>(string collection, string id, CancellationToken cancellationToken = default) where T : class =>
            Task.FromResult(_rows.TryGetValue((collection, id), out string? json) ? JsonSerializer.Deserialize<T>(json, Json) : null);

        public Task<List<T>> GetAllAsync<T>(string collection, CancellationToken cancellationToken = default) where T : class =>
            Task.FromResult(_rows.Where(row => row.Key.Collection == collection)
                                 .Select(row => JsonSerializer.Deserialize<T>(row.Value, Json)!)
                                 .ToList());

        public Task UpsertAsync<T>(string collection, string id, T value, CancellationToken cancellationToken = default) where T : class
        {
            _rows[(collection, id)] = JsonSerializer.Serialize(value, Json);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string collection, string id, CancellationToken cancellationToken = default)
        {
            _rows.TryRemove((collection, id), out _);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>一台设备:自己的库、自己的同步服务;构造时 supportsTemplates 为假即没有这个功能的旧客户端。</summary>
    private sealed class Device
    {
        public Device(string name, bool supportsTemplates = true, string? passphrase = null, FixedTime? time = null)
        {
            Store = new MemoryStore();
            Templates = new LayoutTemplateStore(Store, time ?? new FixedTime(Now));
            ISecretProtector protector = Substitute.For<ISecretProtector>();
            // 同步口令经 TryUnprotect 取(解不开要拒绝推送),替身按它配。
            protector.TryUnprotect("protected-passphrase", out Arg.Any<string?>()).Returns(call =>
            {
                call[1] = passphrase;
                return true;
            });
            Sync = new GistSyncService(Substitute.For<ISettingsService>(), Substitute.For<ISessionRepository>(), Store,
                Substitute.For<IQuickCommandRepository>(), protector, layoutTemplates: supportsTemplates ? Templates : null);
            Config = new SyncSettings
            {
                DeviceName = name,
                SyncAppSettings = false,
                SyncProfiles = false,
                SyncSnippets = false,
                ProtectedPassphrase = passphrase is null ? null : "protected-passphrase",
            };
        }

        public MemoryStore Store { get; }

        public LayoutTemplateStore Templates { get; }

        public GistSyncService Sync { get; }

        public SyncSettings Config { get; }

        /// <summary>推送:主载荷 + (合并过云端那份的)模板文件,与 PushCoreAsync 写的是同一组文件。</summary>
        public async Task PushAsync(Dictionary<string, string> gist)
        {
            gist[SyncFile] = await Sync.BuildGistContentAsync(Config, CancellationToken.None);
            if (await Sync.BuildLayoutTemplatesUploadAsync(Config, gist.GetValueOrDefault(GistSyncService.LayoutTemplatesFileName), CancellationToken.None)
                is { } layouts)
            {
                gist[GistSyncService.LayoutTemplatesFileName] = layouts;
            }
        }

        /// <summary>拉取:应用主载荷,再把模板文件合并进来(ReconcileLayoutTemplatesAsync 推送之前的那一步)。</summary>
        public async Task<string?> PullAsync(Dictionary<string, string> gist)
        {
            SyncResult applied = await Sync.ApplyRemoteAsync(Config, GistSyncService.ParseEnvelope(gist[SyncFile]), "rev", CancellationToken.None);
            Assert.IsTrue(applied.Success, applied.Message);
            if (!Config.SyncLayoutTemplates)
            {
                return null;
            }
            (_, string? note) = await Sync.MergeRemoteLayoutTemplatesAsync(
                Config, gist.GetValueOrDefault(GistSyncService.LayoutTemplatesFileName), CancellationToken.None);
            return note;
        }

        public async Task<string[]> NamesAsync() => [.. (await Templates.LoadAllAsync()).Select(t => t.Name)];
    }

    private static LayoutTemplate Template(string name, DateTimeOffset? updatedAt = null) => new()
    {
        Name = name,
        CreatedAt = updatedAt ?? Now.AddHours(-1),
        UpdatedAt = updatedAt ?? Now.AddHours(-1),
        Root = new() { Documents = [new() { Kind = LayoutDocumentKind.Terminal, ProfileId = Guid.NewGuid(), Title = "root@" + name }] },
    };

    [TestMethod]
    public async Task NewPushes_OldPullsAndPushes_NewPulls_TheTemplatesAreStillThere()
    {
        var gist = new Dictionary<string, string>();
        var desktop = new Device("desktop");
        await desktop.Templates.SaveAsync(Template("机房 A"));
        await desktop.Templates.SaveAsync(Template("机房 B"));
        await desktop.PushAsync(gist);
        string layoutsAfterNewPush = gist[GistSyncService.LayoutTemplatesFileName];

        var old = new Device("old-laptop", supportsTemplates: false);
        await old.PullAsync(gist);
        gist[SyncFile] = await old.Sync.BuildGistContentAsync(old.Config, CancellationToken.None); // 旧版本只写这一个文件

        Assert.AreEqual(layoutsAfterNewPush, gist[GistSyncService.LayoutTemplatesFileName], "旧客户端碰不到旁边的模板文件");
        var fresh = new Device("new-laptop");
        Assert.IsNull(await fresh.PullAsync(gist));
        Assert.AreSequenceEqual(["机房 A", "机房 B"], await fresh.NamesAsync());
    }

    [TestMethod]
    public async Task TheMainPayload_DoesNotCarryTheTemplates()
    {
        var gist = new Dictionary<string, string>();
        var desktop = new Device("desktop");
        await desktop.Templates.SaveAsync(Template("rack-A"));

        await desktop.PushAsync(gist);

        Assert.DoesNotContain("rack-A", gist[SyncFile], "在主载荷里就会被旧客户端整份推回时抹掉(名字特意用 ASCII:同步的 JSON 会把中文转义掉)");
        Assert.Contains("rack-A", gist[GistSyncService.LayoutTemplatesFileName]);
    }

    [TestMethod]
    public async Task ADeletion_TravelsThroughAnOldClient_ToTheOtherDevice()
    {
        var gist = new Dictionary<string, string>();
        var time = new FixedTime(Now);
        var desktop = new Device("desktop", time: time);
        LayoutTemplate doomed = Template("doomed");
        await desktop.Templates.SaveAsync(doomed);
        await desktop.Templates.SaveAsync(Template("keep"));
        await desktop.PushAsync(gist);
        var laptop = new Device("laptop");
        await laptop.PullAsync(gist);
        Assert.HasCount(2, await laptop.NamesAsync());

        await desktop.Templates.DeleteAsync(doomed.Id);
        await desktop.PushAsync(gist);
        var old = new Device("old", supportsTemplates: false);
        await old.PullAsync(gist);
        gist[SyncFile] = await old.Sync.BuildGistContentAsync(old.Config, CancellationToken.None);
        await laptop.PullAsync(gist);

        Assert.AreSequenceEqual(["keep"], await laptop.NamesAsync(), "墓碑在旧客户端不碰的那个文件里,删除照样传到");
        await laptop.PushAsync(gist);
        await desktop.PullAsync(gist);
        Assert.AreSequenceEqual(["keep"], await desktop.NamesAsync(), "笔记本再推一次也不会把删掉的那个加回来");
    }

    [TestMethod]
    public async Task TwoDevicesSavingDifferentTemplates_BothSurvive()
    {
        var gist = new Dictionary<string, string>();
        var desktop = new Device("desktop");
        var laptop = new Device("laptop");
        await desktop.Templates.SaveAsync(Template("desk"));
        await laptop.Templates.SaveAsync(Template("lap"));

        await desktop.PushAsync(gist);
        await laptop.PushAsync(gist); // 后推的这台先把云端那份合进来再写

        await desktop.PullAsync(gist);
        Assert.AreSequenceEqual(["desk", "lap"], await desktop.NamesAsync());
        Assert.AreSequenceEqual(["desk", "lap"], await laptop.NamesAsync());
    }

    [TestMethod]
    public async Task WhenBothSidesAgree_NothingIsUploaded()
    {
        var gist = new Dictionary<string, string>();
        var desktop = new Device("desktop");
        await desktop.Templates.SaveAsync(Template("desk"));
        await desktop.PushAsync(gist);

        string? again = await desktop.Sync.BuildLayoutTemplatesUploadAsync(
            desktop.Config, gist[GistSyncService.LayoutTemplatesFileName], CancellationToken.None);

        Assert.IsNull(again, "没变就不写:两次推送之间模板文件不动");
        var blank = new Device("blank");
        Assert.IsNull(await blank.Sync.BuildLayoutTemplatesUploadAsync(blank.Config, null, CancellationToken.None),
            "没用过模板的人,Gist 里不多出一个空文件");
    }

    [TestMethod]
    public async Task WithAPassphrase_TheFileIsCiphertext_AndTheOtherDeviceReadsItBack()
    {
        var gist = new Dictionary<string, string>();
        var desktop = new Device("desktop", passphrase: "correct horse");
        await desktop.Templates.SaveAsync(Template("rack-A"));
        await desktop.PushAsync(gist);

        string file = gist[GistSyncService.LayoutTemplatesFileName];
        Assert.DoesNotContain("rack-A", file);
        Assert.DoesNotContain("root@", file, "标签标题(常常就是用户@主机)也在密文里");
        var laptop = new Device("laptop", passphrase: "correct horse");
        Assert.IsNull(await laptop.PullAsync(gist));
        Assert.AreSequenceEqual(["rack-A"], await laptop.NamesAsync());
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("a different passphrase")]
    public async Task AnEncryptedFile_ThisDeviceCannotOpen_IsLeftAlone(string? passphrase)
    {
        var desktop = new Device("desktop", passphrase: "correct horse");
        await desktop.Templates.SaveAsync(Template("机房 A"));
        string file = (await desktop.Sync.BuildLayoutTemplatesUploadAsync(desktop.Config, null, CancellationToken.None))!;
        var other = new Device("other", passphrase: passphrase);
        await other.Templates.SaveAsync(Template("mine"));

        (LayoutTemplateMergeResult? merge, string? note) =
            await other.Sync.MergeRemoteLayoutTemplatesAsync(other.Config, file, CancellationToken.None);

        Assert.IsNull(merge);
        Assert.AreEqual(Core.Resources.Strings.Get("SyncSvc_LayoutsLocked"), note);
        Assert.IsNull(await other.Sync.BuildLayoutTemplatesUploadAsync(other.Config, file, CancellationToken.None),
            "读不了就不写:不能拿本机的明文盖掉别人加了密的那份");
        Assert.AreSequenceEqual(["mine"], await other.NamesAsync());
    }

    [TestMethod]
    public async Task AFileFromANewerVersion_IsNeitherReadNorWritten()
    {
        var desktop = new Device("desktop");
        await desktop.Templates.SaveAsync(Template("mine"));
        string newer = """
            { "format": "velashell-layout-templates-sync", "schemaVersion": 2, "encrypted": false,
              "data": { "templates": [], "tombstones": [] }, "somethingNew": {} }
            """;

        (LayoutTemplateMergeResult? merge, string? note) =
            await desktop.Sync.MergeRemoteLayoutTemplatesAsync(desktop.Config, newer, CancellationToken.None);

        Assert.IsNull(merge);
        Assert.AreEqual(Core.Resources.Strings.Get("SyncSvc_LayoutsNewer"), note);
        Assert.IsNull(await desktop.Sync.BuildLayoutTemplatesUploadAsync(desktop.Config, newer, CancellationToken.None),
            "写回去就是拿旧形状盖掉新版本的数据");
    }

    [TestMethod]
    [DataRow("not json")]
    [DataRow("""{"format":"something-else","data":{"templates":[]}}""")]
    [DataRow("""{"format":"velashell-layout-templates-sync","schemaVersion":1,"encrypted":true,"cipherText":"%%%"}""")]
    public async Task ADamagedFile_IsTreatedAsMissing_AndRewritten(string damaged)
    {
        var desktop = new Device("desktop", passphrase: damaged.Contains("cipherText", StringComparison.Ordinal) ? "pw" : null);
        await desktop.Templates.SaveAsync(Template("mine"));

        string? rewritten = await desktop.Sync.BuildLayoutTemplatesUploadAsync(desktop.Config, damaged, CancellationToken.None);

        Assert.IsNotNull(rewritten, "坏文件没法合并,用本机的重写;旧内容还在 Gist 的历史版本里");
        Assert.AreEqual("mine", desktop.Sync.ReadLayoutTemplatesFile(desktop.Config, rewritten).State!.Templates.Single().Name);
    }

    [TestMethod]
    public async Task WithTemplateSyncTurnedOff_TheFileIsNotTouched()
    {
        var gist = new Dictionary<string, string>();
        var desktop = new Device("desktop");
        await desktop.Templates.SaveAsync(Template("desk"));
        await desktop.PushAsync(gist);
        var laptop = new Device("laptop");
        laptop.Config.SyncLayoutTemplates = false;
        await laptop.Templates.SaveAsync(Template("lap"));

        string before = gist[GistSyncService.LayoutTemplatesFileName];
        await laptop.PushAsync(gist);
        await laptop.PullAsync(gist);

        Assert.AreEqual(before, gist[GistSyncService.LayoutTemplatesFileName]);
        Assert.AreSequenceEqual(["lap"], await laptop.NamesAsync());
    }

    [TestMethod]
    public async Task RestoringARevision_BringsItsTemplatesBack_EvenIfDeletedSince()
    {
        var time = new FixedTime(Now);
        var desktop = new Device("desktop", time: time);
        LayoutTemplate template = Template("prod");
        await desktop.Templates.SaveAsync(template);
        string revision = (await desktop.Sync.BuildLayoutTemplatesUploadAsync(desktop.Config, null, CancellationToken.None))!;
        await desktop.Templates.DeleteAsync(template.Id);
        string current = (await desktop.Sync.BuildLayoutTemplatesUploadAsync(desktop.Config, revision, CancellationToken.None))!;
        time.Now = Now.AddHours(1);

        Assert.IsNull(await desktop.Sync.RestoreLayoutTemplatesAsync(desktop.Config, revision));
        string? pushed = await desktop.Sync.BuildLayoutTemplatesUploadAsync(desktop.Config, current, CancellationToken.None);

        Assert.AreSequenceEqual(["prod"], await desktop.NamesAsync());
        Assert.AreEqual("prod", desktop.Sync.ReadLayoutTemplatesFile(desktop.Config, pushed).State!.Templates.Single().Name,
            "恢复之后的推送胜过云端那块墓碑");
    }

    /// <summary>
    /// 假的 GitHub Gist 接口:读交回全部文件与最新 revision;改只动请求里列出的文件(「更新 Gist」的约定),revision 加一。
    /// </summary>
    private sealed class FakeGistApi : HttpMessageHandler
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

        public int Revision { get; set; } = 1;

        public string Version => $"v{Revision}";

        /// <summary>每次改写了哪几个文件。</summary>
        public List<string[]> Patches { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Patch)
            {
                using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var written = new List<string>();
                foreach (JsonProperty file in body.RootElement.GetProperty("files").EnumerateObject())
                {
                    Files[file.Name] = file.Value.GetProperty("content").GetString()!;
                    written.Add(file.Name);
                }
                Patches.Add([.. written]);
                Revision++;
            }
            var files = new System.Text.Json.Nodes.JsonObject();
            foreach ((string name, string content) in Files)
            {
                files[name] = new System.Text.Json.Nodes.JsonObject { ["content"] = content };
            }
            var gist = new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = "gist-1",
                ["history"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["version"] = Version }),
                ["files"] = files,
            };
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(gist.ToJsonString()) };
        }
    }

    /// <summary>一份会记住保存结果的设置服务替身(拉取写进去的,下一次推送读得到)。</summary>
    private static ISettingsService Settings(AppSettings initial)
    {
        AppSettings current = initial;
        ISettingsService settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(_ => Task.FromResult(JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(current))!));
        settings.SaveSettingsAsync(Arg.Any<AppSettings>()).Returns(call =>
        {
            current = call.Arg<AppSettings>();
            return Task.CompletedTask;
        });
        return settings;
    }

    /// <summary>
    /// 只存了一个模板(主载荷没打「本机改过」)、而别的设备刚推过新的设置:这一轮同步要先把那份设置拉下来,
    /// 再把模板合并推上去 —— 推上去的主载荷是拉下来之后的,别的设备的改动不丢。
    /// </summary>
    /// <remarks>
    /// 存模板曾经给主载荷打标记(App.WireAutoSync),于是这一轮走「双端都改、较新者胜」,刚存模板的这台以为自己更新,
    /// 把旧设置整份推上去盖掉了别的设备的改动。现在接线不打标记;这一条守的是不打标记时模板照样上得去。
    /// </remarks>
    [TestMethod]
    public async Task SyncNow_AfterOnlyATemplateChanged_PullsTheNewerSettings_AndStillUploadsTheTemplate()
    {
        var api = new FakeGistApi();
        GistApiClient.UseHttpClientForTests(new HttpClient(api));
        try
        {
            // 另一台设备把字号改成了 18 并推了上去(云端 revision 从本机上次看到的 v1 走到了 v2)。
            var otherConfig = new SyncSettings { DeviceName = "laptop", SyncProfiles = false, SyncSnippets = false };
            var other = new GistSyncService(Settings(new AppSettings { TerminalFontSize = 18 }), Substitute.For<ISessionRepository>(),
                new MemoryStore(), Substitute.For<IQuickCommandRepository>(), Substitute.For<ISecretProtector>());
            api.Files[SyncFile] = await other.BuildGistContentAsync(otherConfig, CancellationToken.None);
            api.Revision = 2;

            // 本机还停在字号 14、云端 v1,自那以后只存了一个模板。
            var store = new MemoryStore();
            var templates = new LayoutTemplateStore(store, new FixedTime(Now));
            ISettingsService settings = Settings(new AppSettings { TerminalFontSize = 14 });
            ISecretProtector protector = Substitute.For<ISecretProtector>();
            protector.TryUnprotect("protected-token", out Arg.Any<string?>()).Returns(call =>
            {
                call[1] = "ghp_test";
                return true;
            });
            var desktop = new GistSyncService(settings, Substitute.For<ISessionRepository>(), store,
                Substitute.For<IQuickCommandRepository>(), protector, layoutTemplates: templates);
            await desktop.SaveSyncSettingsAsync(new SyncSettings
            {
                Enabled = true,
                ProtectedToken = "protected-token",
                GistId = "gist-1",
                DeviceName = "desktop",
                SyncProfiles = false,
                SyncSnippets = false,
                LastRemoteVersion = "v1",
                LastSyncAtUtc = DateTime.UtcNow.AddHours(-1),
            });
            await templates.SaveAsync(Template("rack-A"));

            SyncResult result = await desktop.SyncNowAsync();

            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual(SyncAction.Pulled, result.Action, result.Message);
            Assert.AreEqual(18, (await settings.GetSettingsAsync()).TerminalFontSize, "云端那份较新的设置拉下来了");
            Assert.AreEqual(18, GistSyncService.ParseEnvelope(api.Files[SyncFile]).Payload!.Settings!.TerminalFontSize,
                "随模板一起推上去的主载荷是拉下来之后的那份,另一台设备的改动没被盖掉");
            Assert.Contains("rack-A", api.Files[GistSyncService.LayoutTemplatesFileName], "模板照样推上去了");
            Assert.HasCount(1, api.Patches);
            SyncSettings after = await desktop.GetSyncSettingsAsync();
            Assert.AreEqual(api.Version, after.LastRemoteVersion);
            Assert.IsNull(after.LastLocalChangeAtUtc);
        }
        finally
        {
            GistApiClient.UseHttpClientForTests(null);
        }
    }

    [TestMethod]
    public void ExistingSyncConfigurations_SyncTemplatesByDefault()
    {
        SyncSettings stored = JsonSerializer.Deserialize<SyncSettings>(
            """{"enabled":true,"syncAppSettings":true,"syncProfiles":true,"syncSnippets":false}""",
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;

        Assert.IsTrue(stored.SyncLayoutTemplates, "与其余同步范围一样默认开;旧配置里没有这个字段");
    }
}
