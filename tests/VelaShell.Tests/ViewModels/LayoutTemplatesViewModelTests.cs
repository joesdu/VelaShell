using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Headless;
using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Layouts;
using VelaShell.Core.Models;
using VelaShell.Docking;
using VelaShell.Docking.Model;
using VelaShell.Presentation.ViewModels;
using VelaShell.Services;
using VelaShell.Services.Layouts;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 命名布局模板的增删改与打开,以及「恢复会话」顺带的那份退出时的布局(含主窗口退出时的接线)。
/// </summary>
[TestClass]
[TestCategory("Docking")]
public sealed class LayoutTemplatesViewModelTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Initialize(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(LayoutTemplatesViewModelTests).Assembly);

    /// <summary>内存里的文档库:与 SonnetDB 一样每次存取都过一遍 JSON,改了对象不会悄悄改到库里。</summary>
    private sealed class MemoryStore : IAppDataStore
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public Dictionary<(string Collection, string Id), string> Rows { get; } = [];

        public bool FailWrites { get; set; }

        public Task<T?> GetAsync<T>(string collection, string id, CancellationToken cancellationToken = default) where T : class =>
            Task.FromResult(Rows.TryGetValue((collection, id), out string? json) ? JsonSerializer.Deserialize<T>(json, Json) : null);

        public Task<List<T>> GetAllAsync<T>(string collection, CancellationToken cancellationToken = default) where T : class =>
            Task.FromResult(Rows.Where(row => row.Key.Collection == collection)
                                .Select(row => JsonSerializer.Deserialize<T>(row.Value, Json)!)
                                .ToList());

        public Task UpsertAsync<T>(string collection, string id, T value, CancellationToken cancellationToken = default) where T : class
        {
            if (FailWrites)
            {
                throw new IOException("disk full");
            }
            Rows[(collection, id)] = JsonSerializer.Serialize(value, Json);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string collection, string id, CancellationToken cancellationToken = default)
        {
            Rows.Remove((collection, id));
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedDialogs : ILayoutTemplateDialogs
    {
        public Queue<string?> Names { get; } = new();

        public bool ConfirmAnswer { get; set; } = true;

        public List<string> Asked { get; } = [];

        public Task<string?> PromptNameAsync(string title, string initialValue, string? message)
        {
            Asked.Add($"prompt:{initialValue}");
            return Task.FromResult(Names.Count > 0 ? Names.Dequeue() : initialValue);
        }

        public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger)
        {
            Asked.Add(danger ? $"danger:{message}" : $"confirm:{message}");
            return Task.FromResult(ConfirmAnswer);
        }

        /// <summary>多选一的回答(按钮下标)。</summary>
        public int ChooseAnswer { get; set; }

        public Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> choices, int cancelResult)
        {
            Asked.Add($"choose:{message}");
            return Task.FromResult(ChooseAnswer);
        }

        /// <summary>文件对话框交回的路径;null = 用户取消。</summary>
        public string? FilePath { get; set; }

        public Task<string?> PickExportPathAsync(string suggestedFileName) => Task.FromResult(FilePath);

        public Task<string?> PickImportPathAsync() => Task.FromResult(FilePath);
    }

    private sealed class RestoredDocument(LayoutOpenRequest request) : DockDocument
    {
        public LayoutOpenRequest Request { get; } = request;
    }

    private sealed class FakeOpener(DockWorkspace workspace) : ILayoutDocumentOpener
    {
        public Dictionary<Guid, SessionProfile> Profiles { get; } = [];

        public Task<SessionProfile?> FindProfileAsync(Guid profileId) => Task.FromResult(Profiles.GetValueOrDefault(profileId));

        public LocalShellInfo? FindLocalShell(string shellId) => null;

        public Task OpenAsync(LayoutOpenRequest request)
        {
            workspace.AddDocument(new RestoredDocument(request) { Title = request.Profile?.Name ?? "?" });
            return Task.CompletedTask;
        }
    }

    private sealed class NoTimer : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed record Scene(
        LayoutTemplatesViewModel ViewModel,
        DockWorkspace Workspace,
        MemoryStore Store,
        FakeOpener Opener,
        ScriptedDialogs Dialogs,
        ToastHostViewModel Toasts,
        LayoutTemplateStore TemplateStore);

    private static Scene NewScene()
    {
        var workspace = new DockWorkspace();
        var store = new MemoryStore();
        var templateStore = new LayoutTemplateStore(store);
        var opener = new FakeOpener(workspace);
        var dialogs = new ScriptedDialogs();
        var toasts = new ToastHostViewModel((_, _) => new NoTimer());
        var vm = new LayoutTemplatesViewModel(
            templateStore,
            workspace,
            opener,
            document => LayoutDocumentDescriber.Describe(document, _ => false),
            id => Task.FromResult(opener.Profiles.ContainsKey(id)),
            toasts,
            () => new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero))
        {
            Dialogs = dialogs,
        };
        return new(vm, workspace, store, opener, dialogs, toasts, templateStore);
    }

    private static TerminalDocument OpenTerminal(Scene scene, string name, bool saved = true, bool connected = true)
    {
        var profile = new SessionProfile { Name = name, Host = $"{name}.example" };
        if (saved)
        {
            scene.Opener.Profiles[profile.Id] = profile;
        }
        var document = new TerminalDocument(new TerminalTabViewModel(FakeTerminal.Emulator())
        {
            Title = name,
            Profile = profile,
            ConnectionStatus = connected ? SessionStatus.Connected : SessionStatus.Disconnected,
        });
        scene.Workspace.AddDocument(document);
        return document;
    }

    private static string LastToast(Scene scene) => scene.Toasts.Toasts.First().Message;

    [TestMethod]
    public async Task SaveCurrent_StoresTheLayout_SkippingOneOffConnectionsAndSayingSo()
    {
        Scene scene = NewScene();
        TerminalDocument web = OpenTerminal(scene, "web");
        OpenTerminal(scene, "quick", saved: false); // 快速连接:配置不在库里
        scene.Workspace.SetPinned(web, pinned: true);
        scene.Dialogs.Names.Enqueue("  prod  ");

        Assert.IsTrue(await scene.ViewModel.SaveCurrentAsync());

        LayoutTemplateItemViewModel item = scene.ViewModel.Templates.Single();
        Assert.AreEqual("prod", item.Name, "名字去掉首尾空白");
        Assert.AreEqual(1, item.DocumentCount);
        LayoutDocument saved = LayoutTree.Documents(item.Template.Root).Single();
        Assert.AreEqual(web.Terminal.Profile!.Id, saved.ProfileId);
        Assert.IsTrue(saved.IsPinned, "固定状态跟着存");
        Assert.AreEqual("prompt:" + Core.Resources.Strings.Format("LayoutTemplate_DefaultName", 1), scene.Dialogs.Asked[0]);
        Assert.IsTrue(scene.Store.Rows.ContainsKey((LayoutTemplateStore.Collection, item.Template.Id.ToString("D"))));
        Assert.Contains("quick", LastToast(scene), "没存进去的那个点名说出来");

        // 另起一个视图模型重新读一遍:列表来自库,不只是内存。
        var reloaded = new LayoutTemplatesViewModel(
            new LayoutTemplateStore(scene.Store), new DockWorkspace(), scene.Opener,
            document => LayoutDocumentDescriber.Describe(document, _ => false),
            _ => Task.FromResult(true), scene.Toasts);
        await reloaded.LoadAsync();
        Assert.AreEqual("prod", reloaded.Templates.Single().Name);
        Assert.IsTrue(LayoutTree.Documents(reloaded.Templates.Single().Template.Root).Single().IsPinned);
    }

    [TestMethod]
    public async Task SaveCurrent_WithNothingReopenable_WarnsWithoutAsking()
    {
        Scene scene = NewScene();
        OpenTerminal(scene, "quick", saved: false);

        Assert.IsFalse(await scene.ViewModel.SaveCurrentAsync());

        Assert.IsEmpty(scene.Dialogs.Asked);
        Assert.IsEmpty(scene.Store.Rows);
        Assert.AreEqual(Core.Resources.Strings.Get("LayoutTemplate_NothingToSave"), LastToast(scene));
    }

    [TestMethod]
    public async Task SaveCurrent_UnderAnExistingName_AsksAndReplacesInPlace()
    {
        Scene scene = NewScene();
        OpenTerminal(scene, "web");
        scene.Dialogs.Names.Enqueue("prod");
        await scene.ViewModel.SaveCurrentAsync();
        Guid id = scene.ViewModel.Templates.Single().Template.Id;
        OpenTerminal(scene, "db");

        scene.Dialogs.ConfirmAnswer = false;
        scene.Dialogs.Names.Enqueue("PROD");
        Assert.IsFalse(await scene.ViewModel.SaveCurrentAsync(), "不替换就什么都不存");
        Assert.AreEqual(1, scene.ViewModel.Templates.Single().DocumentCount);

        scene.Dialogs.ConfirmAnswer = true;
        scene.Dialogs.Names.Enqueue("PROD");
        Assert.IsTrue(await scene.ViewModel.SaveCurrentAsync());

        LayoutTemplateItemViewModel item = scene.ViewModel.Templates.Single();
        Assert.AreEqual(id, item.Template.Id, "替换的是原来那一条,不是又多出一条");
        Assert.AreEqual(2, item.DocumentCount);
    }

    [TestMethod]
    public async Task Rename_RefusesANameThatBelongsToAnotherTemplate()
    {
        Scene scene = NewScene();
        OpenTerminal(scene, "web");
        scene.Dialogs.Names.Enqueue("alpha");
        await scene.ViewModel.SaveCurrentAsync();
        scene.Dialogs.Names.Enqueue("beta");
        await scene.ViewModel.SaveCurrentAsync();
        LayoutTemplateItemViewModel beta = scene.ViewModel.Templates.Single(t => t.Name == "beta");

        scene.Dialogs.Names.Enqueue("Alpha");
        Assert.IsFalse(await scene.ViewModel.RenameAsync(beta));
        Assert.Contains("alpha", LastToast(scene));

        scene.Dialogs.Names.Enqueue("gamma");
        Assert.IsTrue(await scene.ViewModel.RenameAsync(beta));
        Assert.AreSequenceEqual(["alpha", "gamma"], [.. scene.ViewModel.Templates.Select(t => t.Name)]);
        Assert.AreEqual(beta.Template.Id, scene.ViewModel.Templates.Single(t => t.Name == "gamma").Template.Id);
    }

    [TestMethod]
    public async Task Overwrite_KeepsNameAndId_AndTakesTheCurrentLayout()
    {
        Scene scene = NewScene();
        OpenTerminal(scene, "web");
        scene.Dialogs.Names.Enqueue("prod");
        await scene.ViewModel.SaveCurrentAsync();
        LayoutTemplateItemViewModel original = scene.ViewModel.Templates.Single();
        TerminalDocument db = OpenTerminal(scene, "db");
        scene.Workspace.SplitDocument(db, DockOrientation.Horizontal);

        Assert.IsTrue(await scene.ViewModel.OverwriteAsync(original));

        LayoutTemplateItemViewModel item = scene.ViewModel.Templates.Single();
        Assert.AreEqual((original.Template.Id, "prod"), (item.Template.Id, item.Name));
        Assert.AreEqual(2, LayoutTree.GroupCount(item.Template.Root));
        Assert.AreEqual(original.Template.CreatedAt, item.Template.CreatedAt);
    }

    [TestMethod]
    public async Task Delete_AsksAsADangerousAction_ThenRemovesOnlyTheTemplate()
    {
        Scene scene = NewScene();
        OpenTerminal(scene, "web");
        scene.Dialogs.Names.Enqueue("prod");
        await scene.ViewModel.SaveCurrentAsync();
        LayoutTemplateItemViewModel item = scene.ViewModel.Templates.Single();

        Assert.IsTrue(await scene.ViewModel.DeleteAsync(item));

        Assert.IsTrue(scene.Dialogs.Asked.Last().StartsWith("danger:", StringComparison.Ordinal));
        Assert.IsFalse(scene.ViewModel.HasTemplates);
        Assert.DoesNotContain(key => key.Collection == LayoutTemplateStore.Collection, scene.Store.Rows.Keys);
        Assert.AreEqual(item.Template.Id.ToString("D"), scene.Store.Rows.Keys.Single().Id, "只留一块墓碑,同步靠它把删除传出去");
        Assert.HasCount(1, scene.Workspace.AllDocuments().ToList(), "删模板不碰开着的标签");
    }

    [TestMethod]
    public async Task SaveFailure_IsReportedAndTheListStaysAsItWas()
    {
        Scene scene = NewScene();
        OpenTerminal(scene, "web");
        scene.Store.FailWrites = true;
        scene.Dialogs.Names.Enqueue("prod");

        Assert.IsFalse(await scene.ViewModel.SaveCurrentAsync());

        Assert.IsFalse(scene.ViewModel.HasTemplates);
        Assert.IsTrue(scene.Toasts.Toasts.First().IsError);
        Assert.Contains("disk full", LastToast(scene));
    }

    [TestMethod]
    public async Task Open_ReportsDeletedConnections_AndOpensTheRest()
    {
        Scene scene = NewScene();
        TerminalDocument web = OpenTerminal(scene, "web");
        TerminalDocument db = OpenTerminal(scene, "db");
        scene.Dialogs.Names.Enqueue("prod");
        await scene.ViewModel.SaveCurrentAsync();
        scene.Workspace.RemoveDocument(web);
        scene.Workspace.RemoveDocument(db);
        scene.Opener.Profiles.Remove(db.Terminal.Profile!.Id); // db 那条配置后来被删了

        LayoutOpenResult result = (await scene.ViewModel.OpenAsync(scene.ViewModel.Templates.Single()))!;

        Assert.AreEqual(1, result.Opened);
        Assert.AreEqual("web", scene.Workspace.AllDocuments().Single().Title);
        string toast = LastToast(scene);
        Assert.Contains(Core.Resources.Strings.Format("LayoutTemplate_MissingProfile", "db"), toast);
    }

    [TestMethod]
    public async Task Open_ManyTabs_AsksFirst()
    {
        Scene scene = NewScene();
        for (int i = 0; i <= LayoutTemplatesViewModel.OpenManyConfirmThreshold; i++)
        {
            OpenTerminal(scene, $"host{i}");
        }
        scene.Dialogs.Names.Enqueue("fleet");
        await scene.ViewModel.SaveCurrentAsync();
        scene.Dialogs.ConfirmAnswer = false;

        Assert.IsNull(await scene.ViewModel.OpenAsync(scene.ViewModel.Templates.Single()));

        Assert.IsEmpty(scene.Workspace.AllDocuments().OfType<RestoredDocument>());
    }

    // ---- 导出 / 导入 ----

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), "velashell-layout-tests", Guid.NewGuid().ToString("N") + ".json");

    private static async Task<string> WriteFileAsync(string json)
    {
        string path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, json);
        return path;
    }

    private static async Task<LayoutTemplateItemViewModel> SaveAsync(Scene scene, string name, params string[] hosts)
    {
        var documents = new List<TerminalDocument>();
        foreach (string host in hosts)
        {
            documents.Add(OpenTerminal(scene, host));
        }
        scene.Dialogs.Names.Enqueue(name);
        Assert.IsTrue(await scene.ViewModel.SaveCurrentAsync());
        foreach (TerminalDocument document in documents)
        {
            scene.Workspace.RemoveDocument(document);
        }
        return scene.ViewModel.Templates.Single(t => t.Name == name);
    }

    [TestMethod]
    public async Task ExportThenImportOnAnotherDevice_BringsTheTemplatesOver_WithTheirIds()
    {
        Scene desktop = NewScene();
        LayoutTemplateItemViewModel prod = await SaveAsync(desktop, "prod", "web", "db");
        desktop.Dialogs.FilePath = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(desktop.Dialogs.FilePath)!);
        try
        {
            Assert.IsTrue(await desktop.ViewModel.ExportAsync());
            string json = await File.ReadAllTextAsync(desktop.Dialogs.FilePath);
            Assert.Contains(LayoutTemplateFile.FormatId, json);
            Assert.Contains(desktop.Dialogs.FilePath, LastToast(desktop));

            Scene laptop = NewScene();
            foreach (SessionProfile profile in desktop.Opener.Profiles.Values)
            {
                laptop.Opener.Profiles[profile.Id] = profile; // 连接配置随同步过来了
            }
            laptop.Dialogs.FilePath = desktop.Dialogs.FilePath;

            Assert.AreEqual(1, await laptop.ViewModel.ImportAsync());

            LayoutTemplateItemViewModel imported = laptop.ViewModel.Templates.Single();
            Assert.AreEqual((prod.Template.Id, "prod", 2), (imported.Template.Id, imported.Name, imported.DocumentCount));
            Assert.AreEqual(Core.Resources.Strings.Format("LayoutTemplate_Imported", 1), LastToast(laptop));
            Assert.DoesNotContain(a => a.StartsWith("choose:", StringComparison.Ordinal), laptop.Dialogs.Asked, "没有冲突就不问");
        }
        finally
        {
            File.Delete(desktop.Dialogs.FilePath);
        }
    }

    [TestMethod]
    public async Task Import_OfTemplatesWhoseConnectionsAreMissingHere_SaysSoUpFront()
    {
        Scene desktop = NewScene();
        LayoutTemplateItemViewModel prod = await SaveAsync(desktop, "prod", "web");
        string path = await WriteFileAsync(LayoutTemplateFile.Export([prod.Template], DateTime.UtcNow));
        try
        {
            Scene other = NewScene(); // 这台设备上没有那条连接
            other.Dialogs.FilePath = path;

            Assert.AreEqual(1, await other.ViewModel.ImportAsync());

            Assert.AreEqual(Core.Resources.Strings.Format("LayoutTemplate_ImportedMissing", 1, 1), LastToast(other));
            Assert.HasCount(1, other.ViewModel.Templates, "照样导入:配置以后可能随同步过来,打开时照现有口径跳过");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    [DataRow(0, DisplayName = "替换")]
    [DataRow(1, DisplayName = "另存")]
    [DataRow(2, DisplayName = "取消")]
    public async Task Import_ConflictsBySameIdOrName_AreAskedAboutOnce(int answer)
    {
        Scene scene = NewScene();
        LayoutTemplateItemViewModel prod = await SaveAsync(scene, "prod", "web");
        LayoutTemplateItemViewModel db = await SaveAsync(scene, "db", "db1");
        LayoutTemplate sameId = new()
        {
            Id = prod.Template.Id,
            Name = "prod",
            Root = new() { Documents = [new() { Kind = LayoutDocumentKind.LocalShell, ShellId = "pwsh", Title = "a" }, new() { Kind = LayoutDocumentKind.LocalShell, ShellId = "cmd", Title = "b" }] },
        };
        LayoutTemplate sameName = new()
        {
            Name = "DB",
            Root = new() { Documents = [new() { Kind = LayoutDocumentKind.LocalShell, ShellId = "pwsh", Title = "c" }] },
        };
        LayoutTemplate fresh = new()
        {
            Name = "fresh",
            Root = new() { Documents = [new() { Kind = LayoutDocumentKind.LocalShell, ShellId = "pwsh", Title = "d" }] },
        };
        string path = await WriteFileAsync(LayoutTemplateFile.Export([sameId, sameName, fresh], DateTime.UtcNow));
        int rowsBefore = scene.Store.Rows.Count;
        scene.Dialogs.FilePath = path;
        scene.Dialogs.ChooseAnswer = answer;
        try
        {
            int? imported = await scene.ViewModel.ImportAsync();

            string choose = scene.Dialogs.Asked.Single(a => a.StartsWith("choose:", StringComparison.Ordinal));
            Assert.Contains("prod", choose);
            Assert.Contains("DB", choose);
            Assert.DoesNotContain("fresh", choose, "不冲突的不点名");
            string[] names = [.. scene.ViewModel.Templates.Select(t => t.Name)];
            switch (answer)
            {
                case 0:
                    Assert.AreEqual(3, imported);
                    Assert.AreSequenceEqual(["DB", "fresh", "prod"], names);
                    LayoutTemplate replacedById = scene.ViewModel.Templates.Single(t => t.Name == "prod").Template;
                    Assert.AreEqual((prod.Template.Id, 2), (replacedById.Id, LayoutTree.Documents(replacedById.Root).Count()));
                    LayoutTemplate replacedByName = scene.ViewModel.Templates.Single(t => t.Name == "DB").Template;
                    Assert.AreEqual(db.Template.Id, replacedByName.Id, "重名的替换沿用本机那一条的 Id,不是又多出一条");
                    Assert.AreEqual(db.Template.CreatedAt, replacedByName.CreatedAt);
                    break;
                case 1:
                    Assert.AreEqual(3, imported);
                    Assert.AreSequenceEqual(["db", "DB (2)", "fresh", "prod", "prod (2)"], names);
                    Assert.HasCount(1, LayoutTree.Documents(scene.ViewModel.Templates.Single(t => t.Name == "prod").Template.Root),
                        "本机原来那份不动");
                    Assert.AreNotEqual(prod.Template.Id, scene.ViewModel.Templates.Single(t => t.Name == "prod (2)").Template.Id);
                    break;
                default:
                    Assert.IsNull(imported);
                    Assert.AreSequenceEqual(["db", "prod"], names);
                    Assert.HasCount(rowsBefore, scene.Store.Rows, "取消:一条都不写");
                    break;
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static LayoutTemplate ShellTemplate(string name, int tabs, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = name,
        Root = new() { Documents = [.. Enumerable.Range(0, tabs).Select(i => new LayoutDocument { Kind = LayoutDocumentKind.LocalShell, ShellId = "pwsh", Title = $"t{i}" })] },
    };

    /// <summary>
    /// 别的设备上把「prod」改名成「prod-old」、又新建了一个「prod」,一起导出:文件里一条与本机同 Id、一条与本机同名,
    /// 两条都对上本机同一条。「替换」时同 Id 的那条替换它,同名的那条另存 —— 两条都在,提示的数目也对。
    /// </summary>
    [TestMethod]
    [DataRow(false, DisplayName = "同 Id 的在前")]
    [DataRow(true, DisplayName = "同名的在前")]
    public async Task Import_Replace_WhenTwoIncomingTemplatesHitTheSameLocalOne_KeepsBoth(bool sameNameFirst)
    {
        Scene scene = NewScene();
        LayoutTemplateItemViewModel prod = await SaveAsync(scene, "prod", "web");
        LayoutTemplate renamed = ShellTemplate("prod-old", 2, prod.Template.Id);
        LayoutTemplate newProd = ShellTemplate("prod", 3);
        string path = await WriteFileAsync(LayoutTemplateFile.Export(sameNameFirst ? [newProd, renamed] : [renamed, newProd], DateTime.UtcNow));
        scene.Dialogs.FilePath = path;
        scene.Dialogs.ChooseAnswer = 0; // 替换
        try
        {
            Assert.AreEqual(2, await scene.ViewModel.ImportAsync());

            Assert.AreSequenceEqual(["prod", "prod-old"], scene.ViewModel.Templates.Select(t => t.Name).ToArray());
            LayoutTemplate old = scene.ViewModel.Templates.Single(t => t.Name == "prod-old").Template;
            Assert.AreEqual((prod.Template.Id, prod.Template.CreatedAt, 2), (old.Id, old.CreatedAt, LayoutTree.Documents(old.Root).Count()),
                "同 Id 的替换本机那一条");
            LayoutTemplate added = scene.ViewModel.Templates.Single(t => t.Name == "prod").Template;
            Assert.AreEqual((newProd.Id, 3), (added.Id, LayoutTree.Documents(added.Root).Count()),
                "同名的那条没被盖掉:留着自己的 Id 另存,本机原来的名字已经空出来了,不补序号");
            Assert.HasCount(2, await scene.TemplateStore.LoadAllAsync());
            Assert.AreEqual(Core.Resources.Strings.Format("LayoutTemplate_Imported", 2), LastToast(scene));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task Import_Replace_WhenTwoIncomingTemplatesShareTheLocalName_TheSecondIsSavedAsACopy()
    {
        Scene scene = NewScene();
        LayoutTemplateItemViewModel prod = await SaveAsync(scene, "prod", "web");
        LayoutTemplate first = ShellTemplate("prod", 2);
        LayoutTemplate second = ShellTemplate("PROD", 3);
        string path = await WriteFileAsync(LayoutTemplateFile.Export([first, second], DateTime.UtcNow));
        scene.Dialogs.FilePath = path;
        scene.Dialogs.ChooseAnswer = 0; // 替换
        try
        {
            Assert.AreEqual(2, await scene.ViewModel.ImportAsync());

            LayoutTemplate replaced = scene.ViewModel.Templates.Single(t => t.Template.Id == prod.Template.Id).Template;
            Assert.AreEqual(("prod", 2), (replaced.Name, LayoutTree.Documents(replaced.Root).Count()), "文件里靠前的那条替换本机那一条");
            LayoutTemplate copy = scene.ViewModel.Templates.Single(t => t.Template.Id == second.Id).Template;
            Assert.AreEqual(("PROD (2)", 3), (copy.Name, LayoutTree.Documents(copy.Root).Count()), "另一条名字还被占着,补序号另存");
            Assert.HasCount(2, await scene.TemplateStore.LoadAllAsync());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task Import_OfADeletedTemplate_LiftsItsTombstone()
    {
        Scene scene = NewScene();
        LayoutTemplateItemViewModel prod = await SaveAsync(scene, "prod", "web");
        string path = await WriteFileAsync(LayoutTemplateFile.Export([prod.Template], DateTime.UtcNow));
        Assert.IsTrue(await scene.ViewModel.DeleteAsync(prod));
        scene.Dialogs.FilePath = path;
        try
        {
            Assert.AreEqual(1, await scene.ViewModel.ImportAsync());

            LayoutTemplateSyncState state = await scene.TemplateStore.LoadSyncStateAsync();
            Assert.AreEqual(prod.Template.Id, state.Templates.Single().Id);
            Assert.IsEmpty(state.Tombstones, "导回来的模板撤掉自己的墓碑");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task Import_OfSomethingElse_SaysItIsNotATemplateFile()
    {
        Scene scene = NewScene();
        string path = await WriteFileAsync("""{"general":{"language":"en"}}""");
        scene.Dialogs.FilePath = path;
        try
        {
            Assert.IsNull(await scene.ViewModel.ImportAsync());

            Assert.AreEqual(Core.Resources.Strings.Get("LayoutTemplate_ImportInvalid"), LastToast(scene));
            Assert.IsEmpty(scene.Store.Rows);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task Export_WithNoTemplates_WarnsWithoutAskingForAFile()
    {
        Scene scene = NewScene();
        scene.Dialogs.FilePath = TempFile();

        Assert.IsFalse(await scene.ViewModel.ExportAsync());

        Assert.AreEqual(Core.Resources.Strings.Get("LayoutTemplate_NothingToExport"), LastToast(scene));
        Assert.IsFalse(File.Exists(scene.Dialogs.FilePath));
    }

    [TestMethod]
    public async Task TemplatesArrivingFromSync_ShowUpInTheList() => await _session.RunOnUiAsync(async () =>
    {
        Scene scene = NewScene();
        var remote = new LayoutTemplateSyncState(
            [new LayoutTemplate { Name = "from laptop", UpdatedAt = DateTimeOffset.UtcNow, Root = new() { Documents = [new() { Kind = LayoutDocumentKind.LocalShell, ShellId = "pwsh", Title = "x" }] } }],
            []);

        await scene.TemplateStore.MergeAsync(remote);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.AreEqual("from laptop", scene.ViewModel.Templates.Single().Name, "同步写进来的,界面重读之后看得见");
    });

    // ---- 退出时的布局 ----

    [TestMethod]
    public async Task SessionLayout_RecordsOnlyLiveTabs_AndRestoresThem()
    {
        Scene scene = NewScene();
        OpenTerminal(scene, "up");
        OpenTerminal(scene, "down", connected: false);

        LayoutTemplate layout = scene.ViewModel.CaptureSessionLayout()!;
        Assert.AreEqual("up", LayoutTree.Documents(layout.Root).Single().Title, "断开着的不记:下次启动不替用户去连");
        await scene.ViewModel.SaveSessionLayoutAsync(layout);

        Scene next = NewScene(); // 下一次启动:新的工作区,同一个库
        var restoring = new LayoutTemplatesViewModel(
            new LayoutTemplateStore(scene.Store), next.Workspace, next.Opener,
            document => LayoutDocumentDescriber.Describe(document, _ => false),
            _ => Task.FromResult(true), next.Toasts);
        foreach (SessionProfile profile in scene.Opener.Profiles.Values)
        {
            next.Opener.Profiles[profile.Id] = profile;
        }

        Assert.IsTrue(await restoring.RestoreSessionLayoutAsync());
        Assert.AreEqual("up", next.Workspace.AllDocuments().Single().Title);

        await restoring.SaveSessionLayoutAsync(null);
        Assert.IsFalse(await restoring.RestoreSessionLayoutAsync(), "没有记下的布局:交给调用方逐条重连");
    }

    [TestMethod]
    public async Task MainWindowClose_WritesTheSessionLayoutOnlyWhenTheOptionIsOn() => await _session.RunOnUiAsync(() =>
    {
        var store = new MemoryStore();
        var vm = new MainWindowViewModel(appDataStore: store);
        vm.Layout.AddDocument(new TerminalDocument(new TerminalTabViewModel(FakeTerminal.Emulator())
        {
            Title = "web",
            Profile = new SessionProfile { Name = "web", Host = "web.example" },
            ConnectionStatus = SessionStatus.Connected,
        }));
        var window = new MainWindow { DataContext = vm };
        typeof(MainWindow)
            .GetField("_settingsService", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, Substitute.For<ISettingsService>());
        MethodInfo persist = typeof(MainWindow).GetMethod("PersistWindowBounds", BindingFlags.Instance | BindingFlags.NonPublic)!;
        (string, string) key = (LayoutTemplateStore.SessionCollection, LayoutTemplateStore.LastSessionId);

        var settings = new AppSettings();
        settings.General.RestoreSessionsOnStartup = true;
        persist.Invoke(window, [settings]);
        Assert.IsFalse(store.Rows.ContainsKey(key), "默认关:不记布局");

        settings.General.RestoreLayoutOnStartup = true;
        persist.Invoke(window, [settings]);
        Assert.IsTrue(store.Rows.ContainsKey(key));
        Assert.Contains("\"title\":\"web\"", store.Rows[key]);

        settings.General.RestoreLayoutOnStartup = false;
        persist.Invoke(window, [settings]);
        Assert.IsFalse(store.Rows.ContainsKey(key), "关掉之后清掉旧的,免得哪天重新打开时还原出一份过期的");
        return Task.CompletedTask;
    });

    [TestMethod]
    public async Task MainWindowViewModel_ListsTemplatesInThePaletteAndRegistersTheCommands()
    {
        var store = new MemoryStore();
        await new LayoutTemplateStore(store).SaveAsync(new LayoutTemplate
        {
            Name = "prod",
            Root = new() { Documents = [new() { Kind = LayoutDocumentKind.Terminal, ProfileId = Guid.NewGuid(), Title = "web" }] },
        });
        var vm = new MainWindowViewModel(appDataStore: store);
        await vm.LayoutTemplates.LoadAsync();

        var items = (List<CommandPaletteItem>)typeof(MainWindowViewModel)
            .GetMethod("BuildPaletteItems", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, null)!;

        CommandPaletteItem template = items.Single(item => item.Id.StartsWith("layout:", StringComparison.Ordinal));
        Assert.AreEqual("prod", template.Title);
        Assert.AreEqual(Core.Resources.Strings.Get("LayoutTemplate_Title"), template.Category);
        string[] ids = [.. vm.Commands.All.Select(command => command.Id)];
        CollectionAssert.IsSubsetOf(new[] { "layout.save", "layout.manage" }, ids);
    }
}
