using System.Text.Json.Nodes;
using VelaShell.Core.Layouts;
using VelaShell.Infrastructure.Persistence;

namespace VelaShell.Tests.Services;

/// <summary>
/// 布局模板经真实的 SonnetDB 文档库落盘、关库重开之后原样读回(序列化往返)。
/// </summary>
[TestClass]
[TestCategory("DataStore")]
public sealed class LayoutTemplateStoreTests
{
    private string _root = null!;

    [TestInitialize]
    public void SetUp() =>
        _root = Path.Combine(Path.GetTempPath(), "velashell-layout-tests", Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Windows 上被映射的段文件要等进程退出才删得掉;临时目录,留着无妨。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static LayoutTemplate Sample(string name)
    {
        var profile = Guid.NewGuid();
        return new()
        {
            Name = name,
            CreatedAt = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 10, 24, 9, 30, 0, TimeSpan.Zero),
            Root = new()
            {
                Orientation = LayoutOrientation.Horizontal,
                Children =
                [
                    new()
                    {
                        Proportion = 0.65,
                        ActiveIndex = 1,
                        TabsPosition = "Left",
                        Documents =
                        [
                            new() { Kind = LayoutDocumentKind.Terminal, ProfileId = profile, Title = "web-1", IsPinned = true },
                            new() { Kind = LayoutDocumentKind.Files, ProfileId = profile, Title = "web-1 files", IsActive = true },
                        ],
                    },
                    new()
                    {
                        Documents =
                        [
                            new() { Kind = LayoutDocumentKind.DualFiles, ProfileId = profile, SecondProfileId = Guid.NewGuid(), Title = "a ⇄ b" },
                            new() { Kind = LayoutDocumentKind.LocalShell, ShellId = "pwsh", Title = "PowerShell" },
                        ],
                    },
                ],
            },
        };
    }

    [TestMethod]
    public async Task Templates_RoundTripThroughSonnetDb_AndComeBackInNaturalOrder()
    {
        LayoutTemplate ten = Sample("机房 10");
        LayoutTemplate two = Sample("机房 2");
        using (SonnetDbEngine engine = SonnetDbTestTemplate.Open(_root))
        {
            var store = new LayoutTemplateStore(new SonnetDbAppDataStore(engine));
            await store.SaveAsync(ten);
            await store.SaveAsync(two);
        }

        using (SonnetDbEngine engine = SonnetDbTestTemplate.Open(_root))
        {
            var store = new LayoutTemplateStore(new SonnetDbAppDataStore(engine));
            IReadOnlyList<LayoutTemplate> loaded = await store.LoadAllAsync();

            Assert.AreSequenceEqual(["机房 2", "机房 10"], [.. loaded.Select(t => t.Name)], "自然顺序:2 在 10 前面");
            LayoutTemplate back = loaded[1];
            Assert.AreEqual(ten.Id, back.Id);
            Assert.AreEqual(ten.CreatedAt, back.CreatedAt);
            Assert.AreEqual(ten.UpdatedAt, back.UpdatedAt);
            Assert.AreEqual(LayoutTemplate.CurrentSchemaVersion, back.SchemaVersion);
            Assert.AreEqual(LayoutOrientation.Horizontal, back.Root!.Orientation);
            Assert.IsNull(back.Root.Children[1].Proportion, "均分落盘成「没写」,读回来还是均分");
            LayoutNode left = back.Root.Children[0];
            Assert.AreEqual(0.65, left.Proportion);
            Assert.AreEqual(1, left.ActiveIndex);
            Assert.AreEqual("Left", left.TabsPosition);
            Assert.IsTrue(left.Documents[0].IsPinned);
            Assert.IsTrue(left.Documents[1].IsActive);
            LayoutDocument[] expected = [.. LayoutTree.Documents(ten.Root)];
            LayoutDocument[] actual = [.. LayoutTree.Documents(back.Root)];
            Assert.AreSequenceEqual(
                expected.Select(d => (d.Kind, d.ProfileId, d.SecondProfileId, d.ShellId, d.Title, d.IsPinned, d.IsActive)).ToArray(),
                [.. actual.Select(d => (d.Kind, d.ProfileId, d.SecondProfileId, d.ShellId, d.Title, d.IsPinned, d.IsActive))]);

            await store.DeleteAsync(two.Id);
            Assert.AreSequenceEqual([ten.Id], [.. (await store.LoadAllAsync()).Select(t => t.Id)]);
        }
    }

    [TestMethod]
    public async Task SessionLayout_IsKeptApartFromTheNamedTemplates_AndCanBeCleared()
    {
        using SonnetDbEngine engine = SonnetDbTestTemplate.Open(_root);
        var store = new LayoutTemplateStore(new SonnetDbAppDataStore(engine));
        LayoutTemplate session = Sample(string.Empty);
        session.Id = Guid.Empty;

        await store.SaveLastSessionAsync(session);

        Assert.IsEmpty(await store.LoadAllAsync(), "退出时的布局不混进命名模板的列表");
        Assert.HasCount(4, LayoutTree.Documents((await store.LoadLastSessionAsync())!.Root));

        await store.SaveLastSessionAsync(null);
        Assert.IsNull(await store.LoadLastSessionAsync());
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [TestMethod]
    public async Task Delete_LeavesATombstone_AndSavingTheSameIdAgainLiftsIt()
    {
        var time = new FixedTime(new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.Zero));
        using SonnetDbEngine engine = SonnetDbTestTemplate.Open(_root);
        var store = new LayoutTemplateStore(new SonnetDbAppDataStore(engine), time);
        int changed = 0;
        store.Changed += (_, _) => changed++;
        LayoutTemplate template = Sample("prod");
        await store.SaveAsync(template);

        await store.DeleteAsync(template.Id);

        LayoutTemplateSyncState state = await store.LoadSyncStateAsync();
        Assert.IsEmpty(state.Templates);
        LayoutTemplateTombstone tombstone = state.Tombstones.Single();
        Assert.AreEqual((template.Id, time.Now), (tombstone.Id, tombstone.DeletedAt), "删除时间落盘,同步靠它把删除传出去");

        await store.SaveAsync(template); // 导入、恢复历史版本会让同一个 Id 回来
        state = await store.LoadSyncStateAsync();
        Assert.HasCount(1, state.Templates);
        Assert.IsEmpty(state.Tombstones, "墓碑不撤,下一次同步它又会把模板删掉");
        Assert.AreEqual(3, changed, "保存、删除都是本机改动:自动同步据此推送");
    }

    [TestMethod]
    public async Task Merge_WritesTheCloudChanges_AndRaisesSyncAppliedRatherThanChanged()
    {
        var time = new FixedTime(new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.Zero));
        using SonnetDbEngine engine = SonnetDbTestTemplate.Open(_root);
        var store = new LayoutTemplateStore(new SonnetDbAppDataStore(engine), time);
        LayoutTemplate keep = Sample("keep");
        LayoutTemplate doomed = Sample("doomed");
        await store.SaveAsync(keep);
        await store.SaveAsync(doomed);
        int changed = 0, applied = 0;
        store.Changed += (_, _) => changed++;
        store.SyncApplied += (_, _) => applied++;
        LayoutTemplate incoming = Sample("from laptop");
        var remote = new LayoutTemplateSyncState(
            [incoming],
            [new LayoutTemplateTombstone { Id = doomed.Id, DeletedAt = time.Now.AddMinutes(-1) }]);

        LayoutTemplateMergeResult result = await store.MergeAsync(remote);

        Assert.IsTrue(result.RemoteChanged, "云端缺本机的 keep");
        IReadOnlyList<LayoutTemplate> after = await store.LoadAllAsync();
        Assert.AreSequenceEqual(["from laptop", "keep"], [.. after.Select(t => t.Name)]);
        Assert.AreEqual(doomed.Id, (await store.LoadSyncStateAsync()).Tombstones.Single().Id);
        Assert.AreEqual((0, 1), (changed, applied), "同步写进来的不算本机改动,否则会再触发一次推送");

        await store.MergeAsync(result.Merged);
        Assert.AreEqual(1, applied, "两边一致之后再合并:本机什么都不写,也不通知界面");
    }

    [TestMethod]
    public async Task FieldsWrittenByANewerVersion_SurviveBeingLoadedAndSavedHere()
    {
        var id = Guid.NewGuid();
        using SonnetDbEngine engine = SonnetDbTestTemplate.Open(_root);
        var data = new SonnetDbAppDataStore(engine);
        // 更新的版本存下来的形状:模板、节点、标签上各多一个本版本不认识的字段。
        await data.UpsertAsync(LayoutTemplateStore.Collection, id.ToString("D"), new
        {
            id,
            name = "future",
            color = "teal",
            root = new { glow = true, documents = new[] { new { kind = "terminal", title = "x", beam = 3 } } },
        });
        var store = new LayoutTemplateStore(data);

        LayoutTemplate loaded = (await store.LoadAllAsync()).Single();
        loaded.Name = "renamed here";
        await store.SaveAsync(loaded);

        JsonObject raw = (await data.GetAsync<JsonObject>(LayoutTemplateStore.Collection, id.ToString("D")))!;
        Assert.AreEqual("renamed here", (string?)raw["name"]);
        Assert.AreEqual("teal", (string?)raw["color"], "这一版改过、存过,新版本加的字段还在 —— 同步出去也不会被它抹掉");
        Assert.IsTrue((bool)raw["root"]!["glow"]!);
        Assert.AreEqual(3, (int)raw["root"]!["documents"]![0]!["beam"]!);
    }

    [TestMethod]
    public async Task Restore_BringsDeletedTemplatesBack_StampedNow()
    {
        var time = new FixedTime(new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.Zero));
        using SonnetDbEngine engine = SonnetDbTestTemplate.Open(_root);
        var store = new LayoutTemplateStore(new SonnetDbAppDataStore(engine), time);
        LayoutTemplate template = Sample("prod");
        await store.SaveAsync(template);
        await store.DeleteAsync(template.Id);
        time.Now = time.Now.AddHours(1);
        int applied = 0;
        store.SyncApplied += (_, _) => applied++;

        Assert.AreEqual(1, await store.RestoreAsync([WithId(Sample("prod"), template.Id)]));

        LayoutTemplateSyncState state = await store.LoadSyncStateAsync();
        Assert.AreEqual(time.Now, state.Templates.Single().UpdatedAt, "时间刷成现在:随后的推送里它要胜过云端那块墓碑");
        Assert.IsEmpty(state.Tombstones);
        Assert.AreEqual(1, applied);
    }

    private static LayoutTemplate WithId(LayoutTemplate template, Guid id)
    {
        template.Id = id;
        return template;
    }

    [TestMethod]
    public async Task WithoutADataStore_EverythingIsANoOp()
    {
        var store = new LayoutTemplateStore(null);

        await store.SaveAsync(Sample("x"));
        await store.SaveLastSessionAsync(Sample("y"));

        Assert.IsEmpty(await store.LoadAllAsync());
        Assert.IsNull(await store.LoadLastSessionAsync());
    }
}
