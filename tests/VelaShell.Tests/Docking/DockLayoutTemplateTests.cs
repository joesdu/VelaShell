using System.Text.Json;
using System.Text.Json.Serialization;
using VelaShell.Core.Layouts;
using VelaShell.Core.Models;
using VelaShell.Docking;
using VelaShell.Docking.Model;
using VelaShell.Services;
using VelaShell.Services.Layouts;

namespace VelaShell.Tests.Docking;

/// <summary>
/// 布局模板的两个方向:把停靠布局拍成模板树(<see cref="DockLayoutSnapshot.Capture" />),
/// 以及按模板树搭骨架、开标签、让每个标签落回原位(<see cref="LayoutTemplateLauncher" />)。
/// </summary>
/// <remarks>
/// 开标签的那一面换成假的:它像宿主的连接流程一样只调 <see cref="DockWorkspace.AddDocument" />,
/// 不知道自己该落到哪 —— 落点全靠还原器经 <see cref="DockWorkspace.PlacementResolver" /> 认出来。
/// </remarks>
[TestClass]
[TestCategory("Docking")]
public sealed class DockLayoutTemplateTests
{
    /// <summary>与文档库同一套 JSON 约定(camelCase、不写 null),模板要原样穿过它。</summary>
    private static readonly JsonSerializerOptions StoreJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>用户工作区里的一个标签:记着自己在模板里该叫什么。</summary>
    private sealed class LiveDocument : DockDocument
    {
        public LiveDocument(string title, string kind, Guid? profileId = null, string? shellId = null)
        {
            Title = title;
            Kind = kind;
            ProfileId = profileId;
            ShellId = shellId;
        }

        public string Kind { get; }

        public Guid? ProfileId { get; }

        public string? ShellId { get; }

        public override string ToString() => Title;
    }

    /// <summary>还原出来的标签:记着自己是按哪一条请求开的。</summary>
    private sealed class RestoredDocument : DockDocument
    {
        public RestoredDocument(LayoutOpenRequest request)
        {
            Request = request;
            Title = request.Profile?.Name ?? request.Shell?.Name ?? "?";
        }

        public LayoutOpenRequest Request { get; }

        public override string ToString() => Title;
    }

    /// <summary>不可还原的标签(插件面板之类)。</summary>
    private sealed class PanelDocument : DockDocument
    {
        public PanelDocument(string title) => Title = title;
    }

    /// <summary>假的「宿主连接流程」:按请求开一个标签;可以让某条配置晚到、或者干脆不建标签。</summary>
    private sealed class FakeOpener(DockWorkspace workspace) : ILayoutDocumentOpener
    {
        public Dictionary<Guid, SessionProfile> Profiles { get; } = [];

        public HashSet<string> Shells { get; } = [with(StringComparer.Ordinal), "pwsh"];

        /// <summary>这些配置的标签要等闸门放行才建出来(模拟先弹凭据框)。</summary>
        public Dictionary<Guid, TaskCompletionSource> Delays { get; } = [];

        /// <summary>这些配置的流程不建任何标签(模拟在凭据框上点了取消)。</summary>
        public HashSet<Guid> Cancelled { get; } = [];

        /// <summary>这些配置先放一个占位,闸门放行后原位换成真标签(模拟文档型连接)。</summary>
        public Dictionary<Guid, TaskCompletionSource> Placeholders { get; } = [];

        /// <summary>建完标签之后,流程里另起的后台任务再建的那个标签(模拟上下文泄漏)。</summary>
        public Dictionary<Guid, TaskCompletionSource> LateExtras { get; } = [];

        public List<DockDocument> Extras { get; } = [];

        public List<LayoutOpenRequest> Requests { get; } = [];

        public SessionProfile Add(string name)
        {
            var profile = new SessionProfile { Name = name, Host = $"{name}.example" };
            Profiles[profile.Id] = profile;
            return profile;
        }

        public Task<SessionProfile?> FindProfileAsync(Guid profileId) =>
            Task.FromResult(Profiles.GetValueOrDefault(profileId));

        public LocalShellInfo? FindLocalShell(string shellId) =>
            Shells.Contains(shellId) ? new(shellId, shellId, shellId) : null;

        public async Task OpenAsync(LayoutOpenRequest request)
        {
            Requests.Add(request);
            Guid id = request.Profile?.Id ?? Guid.Empty;
            if (Cancelled.Contains(id))
            {
                return;
            }
            if (Delays.TryGetValue(id, out TaskCompletionSource? delay))
            {
                await delay.Task;
            }
            var real = new RestoredDocument(request);
            if (Placeholders.TryGetValue(id, out TaskCompletionSource? connected))
            {
                var placeholder = new PanelDocument("connecting");
                workspace.AddDocument(placeholder);
                await connected.Task;
                workspace.ReplaceDocument(placeholder, real);
            }
            else
            {
                workspace.AddDocument(real);
            }
            if (LateExtras.TryGetValue(id, out TaskCompletionSource? later))
            {
                _ = AddLaterAsync(later);
            }
        }

        private async Task AddLaterAsync(TaskCompletionSource later)
        {
            await later.Task;
            var extra = new PanelDocument("extra");
            Extras.Add(extra);
            workspace.AddDocument(extra);
        }
    }

    private static LayoutDocument? Describe(DockDocument document) => document is LiveDocument live
        ? new LayoutDocument { Kind = live.Kind, ProfileId = live.ProfileId, ShellId = live.ShellId }
        : null;

    private static LayoutNode Group(params LayoutDocument[] documents) => new() { Documents = [.. documents] };

    private static LayoutNode Split(string orientation, params LayoutNode[] children) =>
        new() { Orientation = orientation, Children = [.. children] };

    private static LayoutDocument Terminal(SessionProfile profile, bool pinned = false) =>
        new() { Kind = LayoutDocumentKind.Terminal, ProfileId = profile.Id, Title = profile.Name, IsPinned = pinned };

    private static string Titles(DockGroup group) =>
        string.Join(",", group.Documents.Select(d => d.IsPinned ? d.Title.ToUpperInvariant() + "*" : d.Title));

    private static T JsonRoundTrip<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, StoreJson), StoreJson)!;

    // ---- 往返 ----

    [TestMethod]
    public async Task CaptureThenOpen_RestoresSplitsProportionsOrderSelectionAndPins()
    {
        var source = new DockWorkspace();
        var scratch = new FakeOpener(source);
        SessionProfile pa = scratch.Add("a"), pb = scratch.Add("b"), pc = scratch.Add("c");
        var a = new LiveDocument("a", LayoutDocumentKind.Terminal, pa.Id);
        var b = new LiveDocument("b", LayoutDocumentKind.Files, pb.Id);
        var c = new LiveDocument("c", LayoutDocumentKind.Workspace, pc.Id);
        var shell = new LiveDocument("pwsh", LayoutDocumentKind.LocalShell, shellId: "pwsh");
        var panel = new PanelDocument("AI chat");
        source.AddDocument(a);
        source.AddDocument(b);
        source.AddDocument(c);
        source.SplitDocument(c, DockOrientation.Horizontal);
        source.AddDocument(shell);
        source.SplitDocument(shell, DockOrientation.Vertical);
        DockGroup left = source.FindGroup(a)!;
        source.ActivateDocument(a);
        source.AddDocument(panel); // 落进当前窗格(左边),但它没法重新打开
        source.ActivateDocument(a);
        source.SetPinned(b, pinned: true);
        var root = (DockSplit)source.Root;
        root.Children[0].Proportion = 0.7;
        root.Children[1].Proportion = 0.3;
        source.FindGroup(shell)!.TabsPosition = DockTabsPosition.Left;
        Assert.AreEqual("B*,a,AI chat", Titles(left));

        var skipped = new List<DockDocument>();
        LayoutNode captured = DockLayoutSnapshot.Capture(source, Describe, skipped)!;
        captured = JsonRoundTrip(captured);

        Assert.AreSequenceEqual([panel], skipped.ToArray(), "插件面板之类没法重新打开的标签跳过并报出来");
        Assert.IsNull(captured.Proportion, "根的比例没人读,不记");

        var target = new DockWorkspace();
        var opener = new FakeOpener(target);
        foreach (SessionProfile profile in scratch.Profiles.Values)
        {
            opener.Profiles[profile.Id] = profile;
        }
        var launcher = new LayoutTemplateLauncher(target, opener);

        LayoutOpenResult result = await launcher.OpenAsync(captured);

        Assert.AreEqual(4, result.Opened);
        Assert.IsEmpty(result.Missing);
        var restoredRoot = (DockSplit)target.Root;
        Assert.AreEqual(DockOrientation.Horizontal, restoredRoot.Orientation);
        Assert.AreEqual(0.7, restoredRoot.Children[0].Proportion, 1e-9);
        Assert.AreEqual(0.3, restoredRoot.Children[1].Proportion, 1e-9);
        var restoredLeft = (DockGroup)restoredRoot.Children[0];
        Assert.AreEqual("B*,a", Titles(restoredLeft), "顺序与固定状态原样回来");
        Assert.AreEqual("a", restoredLeft.ActiveDocument!.Title, "组里选中的还是原来那个");
        Assert.AreEqual("a", target.ActiveDocument!.Title, "整个工作区的活动标签也回来了");
        var right = (DockSplit)restoredRoot.Children[1];
        Assert.AreEqual(DockOrientation.Vertical, right.Orientation);
        Assert.AreEqual("c", Titles((DockGroup)right.Children[0]));
        var shellGroup = (DockGroup)right.Children[1];
        Assert.AreEqual("pwsh", Titles(shellGroup));
        Assert.AreEqual(DockTabsPosition.Left, shellGroup.TabsPosition);
        Assert.IsTrue(target.PrimaryGroup.IsPrimary && target.AllGroups().Count(g => g.IsPrimary) == 1,
            "兜底身份交给了模板里的组,且只有一个");

        LayoutOpenRequest[] kinds = [.. opener.Requests];
        Assert.AreSequenceEqual(
            [LayoutDocumentKind.Files, LayoutDocumentKind.Terminal, LayoutDocumentKind.Workspace, LayoutDocumentKind.LocalShell],
            [.. kinds.Select(r => r.Kind)],
            "按模板顺序发起,各自带着自己的类型");
    }

    [TestMethod]
    public void Capture_EmptyWorkspaceOrOnlyPanels_ReturnsNull()
    {
        var workspace = new DockWorkspace();
        Assert.IsNull(DockLayoutSnapshot.Capture(workspace, Describe));

        workspace.AddDocument(new PanelDocument("AI chat"));
        Assert.IsNull(DockLayoutSnapshot.Capture(workspace, Describe));
    }

    [TestMethod]
    public void Capture_DropsPanesLeftEmpty_AndHoistsTheSurvivor()
    {
        var workspace = new DockWorkspace();
        var a = new LiveDocument("a", LayoutDocumentKind.Terminal, Guid.NewGuid());
        var panel = new PanelDocument("panel");
        workspace.AddDocument(a);
        workspace.AddDocument(panel);
        workspace.SplitDocument(panel, DockOrientation.Horizontal);

        LayoutNode captured = DockLayoutSnapshot.Capture(workspace, Describe)!;

        Assert.IsFalse(captured.IsSplit, "右边那格只有插件面板,去掉之后分栏只剩一个子节点,提上来");
        Assert.HasCount(1, captured.Documents);
    }

    // ---- 后到的标签 ----

    [TestMethod]
    public async Task Open_LateArrivalsStillLandInTemplateOrder()
    {
        var workspace = new DockWorkspace();
        var opener = new FakeOpener(workspace);
        SessionProfile a = opener.Add("a"), b = opener.Add("b"), c = opener.Add("c");
        var gate = new TaskCompletionSource();
        opener.Delays[a.Id] = gate; // a 先弹凭据框,最后才到
        var launcher = new LayoutTemplateLauncher(workspace, opener);

        Task<LayoutOpenResult> opening = launcher.OpenAsync(Group(Terminal(a, pinned: true), Terminal(b), Terminal(c)));
        Assert.AreEqual("b,c", Titles(workspace.PrimaryGroup));
        gate.SetResult();
        await opening;

        Assert.AreEqual("A*,b,c", Titles(workspace.PrimaryGroup), "晚到的 a 插回最前,而且是固定的");
    }

    [TestMethod]
    public async Task Open_PlaceholderReplacedByRealDocument_KeepsItsSlotForLaterArrivals()
    {
        var workspace = new DockWorkspace();
        var opener = new FakeOpener(workspace);
        SessionProfile a = opener.Add("a"), b = opener.Add("b"), c = opener.Add("c");
        var aGate = new TaskCompletionSource();
        var bConnected = new TaskCompletionSource();
        opener.Delays[a.Id] = aGate;
        opener.Placeholders[b.Id] = bConnected;
        var launcher = new LayoutTemplateLauncher(workspace, opener);

        Task<LayoutOpenResult> opening = launcher.OpenAsync(Group(Terminal(a), Terminal(b), Terminal(c)));
        Assert.AreEqual("connecting,c", Titles(workspace.PrimaryGroup));
        bConnected.SetResult(); // 占位原位换成真文档
        Assert.AreEqual("b,c", Titles(workspace.PrimaryGroup));
        aGate.SetResult(); // a 这时才到:得插在 b(已经换过手的那个)前面
        await opening;

        Assert.AreEqual("a,b,c", Titles(workspace.PrimaryGroup));
    }

    [TestMethod]
    public async Task Open_DocumentsAddedLaterByTheSameFlow_DoNotReuseTheSlot()
    {
        var workspace = new DockWorkspace();
        var opener = new FakeOpener(workspace);
        SessionProfile a = opener.Add("a"), b = opener.Add("b");
        var later = new TaskCompletionSource();
        opener.LateExtras[a.Id] = later;
        var launcher = new LayoutTemplateLauncher(workspace, opener);

        await launcher.OpenAsync(Split(LayoutOrientation.Horizontal,
            Group(Terminal(a, pinned: true)), Group(Terminal(b))));
        later.SetResult(); // a 的流程里另起的后台任务,这时才又建了一个标签

        DockDocument extra = opener.Extras.Single();
        Assert.IsFalse(extra.IsPinned, "票据只认第一个标签:后来的不该冒充 a 的槽位(否则它也会被固定)");
    }

    /// <summary>
    /// 槽位流程里另起、迟迟不结束的后台任务(SSH 连接的收包、保活循环)捕获了执行上下文,也就一直攥着那张票。
    /// 票作废时得放掉这一次还原的记账:记账里有这次放下的每一个标签,不放的话用户关掉的标签一个也回收不了。
    /// </summary>
    [TestMethod]
    public async Task Open_LingeringBackgroundTaskOfOneSlot_DoesNotKeepClosedTabsAlive()
    {
        var workspace = new DockWorkspace();
        var opener = new FakeOpener(workspace);
        SessionProfile a = opener.Add("a"), b = opener.Add("b");
        var lingering = new TaskCompletionSource();
        opener.LateExtras[a.Id] = lingering; // a 的流程里另起的后台任务一直挂着(像一条长连着的 SSH 收包循环)
        var launcher = new LayoutTemplateLauncher(workspace, opener);
        await launcher.OpenAsync(Group(Terminal(a), Terminal(b)));

        WeakReference closed = Close(workspace, "b");
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.IsFalse(closed.IsAlive, "a 的后台任务还挂着,但它攥着的票不该再连着 b 这个已经关掉的标签");
        lingering.SetResult();
    }

    /// <summary>关掉一个还原出来的标签;标签只在这个方法里出现过,测试方法自己不攥着它。</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference Close(DockWorkspace workspace, string title)
    {
        DockDocument document = workspace.AllDocuments().Single(d => d.Title == title);
        workspace.RemoveDocument(document);
        return new WeakReference(document);
    }

    // ---- 缺失与收尾 ----

    [TestMethod]
    public async Task Open_DeletedProfileIsSkippedAndReported_OthersStillOpen()
    {
        var workspace = new DockWorkspace();
        var opener = new FakeOpener(workspace);
        SessionProfile a = opener.Add("a"), c = opener.Add("c");
        var deleted = new LayoutDocument { Kind = LayoutDocumentKind.Terminal, ProfileId = Guid.NewGuid(), Title = "gone" };
        var unknown = new LayoutDocument { Kind = "hologram", ProfileId = a.Id, Title = "future" };
        var missingShell = new LayoutDocument { Kind = LayoutDocumentKind.LocalShell, ShellId = "fish", Title = "fish" };
        LayoutNode right = Split(LayoutOrientation.Vertical, Group(deleted), Group(Terminal(c), unknown, missingShell));
        right.Proportion = 0.4;
        var launcher = new LayoutTemplateLauncher(workspace, opener);

        LayoutOpenResult result = await launcher.OpenAsync(Split(LayoutOrientation.Horizontal, Group(Terminal(a)), right));

        Assert.AreEqual(2, result.Opened);
        Assert.AreSequenceEqual(
            [
                new LayoutMissingDocument("gone", LayoutMissingReason.ProfileDeleted),
                new LayoutMissingDocument("future", LayoutMissingReason.Unsupported),
                new LayoutMissingDocument("fish", LayoutMissingReason.ShellUnavailable),
            ],
            [.. result.Missing]);
        var root = (DockSplit)workspace.Root;
        Assert.HasCount(2, root.Children);
        var survivor = (DockGroup)root.Children[1];
        Assert.AreEqual("c", Titles(survivor), "只剩一格的分栏被提上来");
        Assert.AreEqual(0.4, survivor.Proportion, 1e-9, "提上来的那格接手分栏原来的份额");
    }

    [TestMethod]
    public async Task Open_EverythingMissing_LeavesTheWorkspaceAlone()
    {
        var workspace = new DockWorkspace();
        DockGroup before = workspace.PrimaryGroup;
        var launcher = new LayoutTemplateLauncher(workspace, new FakeOpener(workspace));

        LayoutOpenResult result = await launcher.OpenAsync(Group(
            new LayoutDocument { Kind = LayoutDocumentKind.Terminal, ProfileId = Guid.NewGuid(), Title = "gone" }));

        Assert.AreEqual(0, result.Opened);
        Assert.HasCount(1, result.Missing);
        Assert.AreSame(before, workspace.Root, "什么都开不了时不去动布局");
    }

    [TestMethod]
    public async Task Open_PaneThatNeverReceivesATab_IsCollapsedAtTheEnd()
    {
        var workspace = new DockWorkspace();
        var opener = new FakeOpener(workspace);
        SessionProfile a = opener.Add("a"), b = opener.Add("b");
        opener.Cancelled.Add(b.Id); // b 在凭据框上取消了,一个标签都没建
        var launcher = new LayoutTemplateLauncher(workspace, opener);

        await launcher.OpenAsync(Split(LayoutOrientation.Horizontal, Group(Terminal(a)), Group(Terminal(b))));

        Assert.IsInstanceOfType<DockGroup>(workspace.Root, "等不来标签的那格收掉,不留一块空白");
        Assert.AreEqual("a", Titles((DockGroup)workspace.Root));
    }

    [TestMethod]
    public async Task Open_IntoAWorkspaceThatHasTabs_AddsTheTemplateAsAColumnOnTheRight()
    {
        var workspace = new DockWorkspace();
        var mine = new PanelDocument("mine");
        workspace.AddDocument(mine);
        DockGroup myGroup = workspace.PrimaryGroup;
        var opener = new FakeOpener(workspace);
        SessionProfile a = opener.Add("a"), b = opener.Add("b");
        var launcher = new LayoutTemplateLauncher(workspace, opener);

        await launcher.OpenAsync(Split(LayoutOrientation.Vertical, Group(Terminal(a)), Group(Terminal(b))));

        var root = (DockSplit)workspace.Root;
        Assert.AreEqual(DockOrientation.Horizontal, root.Orientation);
        Assert.AreSame(myGroup, root.Children[0], "原来的标签原地不动");
        Assert.AreEqual("mine", Titles(myGroup));
        Assert.AreSame(myGroup, workspace.PrimaryGroup, "兜底身份不易主");
        var template = (DockSplit)root.Children[1];
        Assert.AreEqual(DockOrientation.Vertical, template.Orientation);
        Assert.AreEqual("a", Titles((DockGroup)template.Children[0]));
        Assert.AreEqual("b", Titles((DockGroup)template.Children[1]));
    }

    [TestMethod]
    public async Task Open_DualFilesNeedsBothProfiles()
    {
        var workspace = new DockWorkspace();
        var opener = new FakeOpener(workspace);
        SessionProfile left = opener.Add("left"), right = opener.Add("right");
        var launcher = new LayoutTemplateLauncher(workspace, opener);

        LayoutOpenResult result = await launcher.OpenAsync(Group(
            new LayoutDocument { Kind = LayoutDocumentKind.DualFiles, ProfileId = left.Id, SecondProfileId = right.Id, Title = "ok" },
            new LayoutDocument { Kind = LayoutDocumentKind.DualFiles, ProfileId = left.Id, SecondProfileId = Guid.NewGuid(), Title = "half" },
            new LayoutDocument { Kind = LayoutDocumentKind.DualFiles, ProfileId = left.Id, SecondProfileId = left.Id, Title = "same" }));

        Assert.AreEqual(1, result.Opened);
        LayoutOpenRequest request = opener.Requests.Single();
        Assert.AreSame(left, request.Profile);
        Assert.AreSame(right, request.SecondProfile);
        Assert.AreSequenceEqual(
            [LayoutMissingReason.ProfileDeleted, LayoutMissingReason.Unsupported],
            [.. result.Missing.Select(m => m.Reason)]);
    }

    // ---- 模型层的落点裁决 ----

    [TestMethod]
    public void AddDocument_WithPlacement_DoesNotStealFocusUnlessAsked()
    {
        var workspace = new DockWorkspace();
        var mine = new PanelDocument("mine");
        workspace.AddDocument(mine);
        var other = new DockGroup();
        workspace.InsertLayout(other);
        DockPlacement? next = new DockPlacement(other, 0, Pinned: false, SelectInGroup: true, Activate: false);
        workspace.PlacementResolver = _ => next;

        var quiet = new PanelDocument("quiet");
        workspace.AddDocument(quiet);

        Assert.AreSame(mine, workspace.ActiveDocument, "不要求激活的标签不抢焦点");
        Assert.AreSame(quiet, other.ActiveDocument, "但在自己的组里选中");

        next = new DockPlacement(other, 0, Pinned: true, SelectInGroup: false, Activate: true);
        var loud = new PanelDocument("loud");
        workspace.AddDocument(loud);

        Assert.AreSame(loud, workspace.ActiveDocument);
        Assert.IsTrue(loud.IsPinned);
        Assert.AreEqual("LOUD*,quiet", Titles(other));
    }

    [TestMethod]
    public void AddDocument_SelectingInTheGroupThatHoldsTheActiveDocument_MovesFocusWithIt()
    {
        // 否则全局活动文档会是组里一个看不见的标签:状态栏、快捷键都对着它,屏幕上却是另一个。
        var workspace = new DockWorkspace();
        var first = new PanelDocument("first");
        workspace.AddDocument(first);
        workspace.PlacementResolver = _ => new DockPlacement(workspace.PrimaryGroup, 1, false, SelectInGroup: true, Activate: false);

        var second = new PanelDocument("second");
        workspace.AddDocument(second);

        Assert.AreSame(second, workspace.PrimaryGroup.ActiveDocument);
        Assert.AreSame(second, workspace.ActiveDocument);
    }

    [TestMethod]
    public void AddDocument_PlacementIntoAPaneNoLongerOnTheTree_FallsBackToTheDefault()
    {
        var workspace = new DockWorkspace();
        var detached = new DockGroup();
        workspace.PlacementResolver = _ => new DockPlacement(detached, 0, true, true, true);

        var document = new PanelDocument("x");
        workspace.AddDocument(document);

        Assert.AreSame(workspace.PrimaryGroup, workspace.FindGroup(document));
        Assert.IsFalse(document.IsPinned, "落点作废时固定状态也不带");
    }

    [TestMethod]
    public void InsertLayout_RejectsATreeAlreadyInUse()
    {
        var workspace = new DockWorkspace();
        Assert.ThrowsExactly<ArgumentException>(() => workspace.InsertLayout(workspace.PrimaryGroup));
        Assert.ThrowsExactly<ArgumentException>(() => workspace.InsertLayout(new DockSplit(DockOrientation.Horizontal)));
    }

    [TestMethod]
    public void ParseTabsPosition_UnknownNamesAndNumbersFallBackToTop()
    {
        Assert.AreEqual(DockTabsPosition.Right, DockLayoutSnapshot.ParseTabsPosition("right"));
        Assert.AreEqual(DockTabsPosition.Top, DockLayoutSnapshot.ParseTabsPosition("Bottom-ish"));
        Assert.AreEqual(DockTabsPosition.Top, DockLayoutSnapshot.ParseTabsPosition("7"));
        Assert.AreEqual(DockTabsPosition.Top, DockLayoutSnapshot.ParseTabsPosition(null));
    }
}
