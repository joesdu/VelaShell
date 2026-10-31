using VelaShell.Docking.Model;

namespace VelaShell.Tests.Docking;

/// <summary>
/// VelaDock 模型层(Dock.Avalonia 的替代,docs/dock-replacement-plan.md)的结构操作测试:
/// 增删/激活/关闭语义、拆分、停靠、空组折叠与单子分栏提升。
/// </summary>
[TestClass]
[TestCategory("Docking")]
public class DockWorkspaceTests
{
    private sealed class TestDocument : DockDocument
    {
        public TestDocument(string title, bool canClose)
        {
            Title = title;
            CanClose = canClose;
        }

        public override string ToString() => Title;
    }

    private static TestDocument NewDoc(string title, bool canClose = true) => new(title, canClose);

    // ---- 添加与激活 ----

    [TestMethod]
    public void AddDocument_GoesToPrimaryGroup_AndActivates()
    {
        var ws = new DockWorkspace();
        DockDocument? observed = null;
        ws.ActiveDocumentChanged += d => observed = d;

        TestDocument a = NewDoc("a");
        ws.AddDocument(a);

        Assert.AreSame(ws.PrimaryGroup, ws.Root);
        Assert.AreSequenceEqual([a], ws.PrimaryGroup.Documents.ToArray());
        Assert.AreSame(a, ws.PrimaryGroup.ActiveDocument);
        Assert.AreSame(a, ws.ActiveDocument);
        Assert.AreSame(a, observed);
    }

    [TestMethod]
    public void ActivateDocument_SwitchesGroupSelectionAndGlobalActive()
    {
        var ws = new DockWorkspace();
        TestDocument a = NewDoc("a");
        TestDocument b = NewDoc("b");
        ws.AddDocument(a);
        ws.AddDocument(b);

        ws.ActivateDocument(a);

        Assert.AreSame(a, ws.PrimaryGroup.ActiveDocument);
        Assert.AreSame(a, ws.ActiveDocument);
    }

    // ---- 关闭语义 ----

    [TestMethod]
    public void CloseDocument_RaisesDocumentClosed_AndRemoves()
    {
        var ws = new DockWorkspace();
        var closed = new List<DockDocument>();
        ws.DocumentClosed += closed.Add;
        TestDocument a = NewDoc("a");
        ws.AddDocument(a);

        ws.CloseDocument(a);

        Assert.IsEmpty(ws.PrimaryGroup.Documents);
        Assert.AreSequenceEqual([a], closed);
        Assert.IsNull(ws.ActiveDocument);
    }

    [TestMethod]
    public void RemoveDocument_IsSilent()
    {
        var ws = new DockWorkspace();
        int closedCount = 0;
        ws.DocumentClosed += _ => closedCount++;
        TestDocument a = NewDoc("a");
        ws.AddDocument(a);

        ws.RemoveDocument(a);

        Assert.IsEmpty(ws.PrimaryGroup.Documents);
        Assert.AreEqual(0, closedCount);
    }

    [TestMethod]
    public void CloseDocument_RespectsCanClose()
    {
        var ws = new DockWorkspace();
        int closedCount = 0;
        ws.DocumentClosed += _ => closedCount++;
        TestDocument a = NewDoc("a", canClose: false);
        ws.AddDocument(a);

        ws.CloseDocument(a);

        Assert.HasCount(1, ws.PrimaryGroup.Documents);
        Assert.AreEqual(0, closedCount);
    }

    [TestMethod]
    public void RemovingActiveDocument_SelectsNeighborAtSameIndex()
    {
        var ws = new DockWorkspace();
        TestDocument a = NewDoc("a");
        TestDocument b = NewDoc("b");
        TestDocument c = NewDoc("c");
        ws.AddDocument(a);
        ws.AddDocument(b);
        ws.AddDocument(c);
        ws.ActivateDocument(b);

        ws.CloseDocument(b);

        // b 位于 index 1,移除后同位补上的是 c。
        Assert.AreSame(c, ws.PrimaryGroup.ActiveDocument);
        Assert.AreSame(c, ws.ActiveDocument);
    }

    [TestMethod]
    public void CloseOtherAndSideCommands_AreGroupScoped()
    {
        var ws = new DockWorkspace();
        TestDocument a = NewDoc("a");
        TestDocument b = NewDoc("b");
        TestDocument c = NewDoc("c");
        TestDocument d = NewDoc("d");
        ws.AddDocument(a);
        ws.AddDocument(b);
        ws.AddDocument(c);
        ws.AddDocument(d);
        // 把 d 拆到右侧新组,验证关闭命令只作用于所属组。
        ws.SplitDocument(d, DockOrientation.Horizontal);

        ws.CloseLeftDocuments(b);
        Assert.AreSequenceEqual([b, c], ws.PrimaryGroup.Documents.ToArray());

        ws.CloseRightDocuments(b);
        Assert.AreSequenceEqual([b], ws.PrimaryGroup.Documents.ToArray());

        ws.CloseOtherDocuments(d);
        Assert.Contains(b, ws.AllDocuments(), "其他组的标签不受影响");

        ws.CloseAllDocuments(b);
        Assert.AreSequenceEqual([d], ws.AllDocuments().ToArray());
    }

    // ---- 拆分 ----

    [TestMethod]
    public void SplitDocument_Horizontal_CreatesTwoPaneSplit()
    {
        var ws = new DockWorkspace();
        TestDocument a = NewDoc("a");
        TestDocument b = NewDoc("b");
        ws.AddDocument(a);
        ws.AddDocument(b);

        ws.SplitDocument(b, DockOrientation.Horizontal);

        var split = ws.Root as DockSplit;
        Assert.IsNotNull(split);
        Assert.AreEqual(DockOrientation.Horizontal, split.Orientation);
        Assert.HasCount(2, split.Children);
        Assert.AreSame(ws.PrimaryGroup, split.Children[0]);
        var newGroup = (DockGroup)split.Children[1];
        Assert.AreSequenceEqual([b], newGroup.Documents.ToArray());
        Assert.AreSame(b, newGroup.ActiveDocument);
        Assert.AreSame(b, ws.ActiveDocument);
        Assert.AreSequenceEqual([a], ws.PrimaryGroup.Documents.ToArray());
    }

    [TestMethod]
    public void SplitDocument_SameOrientation_InsertsSibling()
    {
        var ws = new DockWorkspace();
        TestDocument a = NewDoc("a");
        TestDocument b = NewDoc("b");
        TestDocument c = NewDoc("c");
        ws.AddDocument(a);
        ws.AddDocument(b);
        ws.AddDocument(c);
        ws.SplitDocument(b, DockOrientation.Horizontal);

        ws.SplitDocument(c, DockOrientation.Horizontal);

        var split = (DockSplit)ws.Root;
        Assert.HasCount(3, split.Children, "同方向拆分应插入兄弟节点而非嵌套分栏");
    }

    [TestMethod]
    public void SplitDocument_CrossOrientation_Nests()
    {
        var ws = new DockWorkspace();
        TestDocument a = NewDoc("a");
        TestDocument b = NewDoc("b");
        TestDocument c = NewDoc("c");
        ws.AddDocument(a);
        ws.AddDocument(b);
        ws.AddDocument(c);
        ws.SplitDocument(b, DockOrientation.Horizontal);

        ws.SplitDocument(c, DockOrientation.Vertical);

        var root = (DockSplit)ws.Root;
        Assert.AreEqual(DockOrientation.Horizontal, root.Orientation);
        var nested = root.Children[0] as DockSplit;
        Assert.IsNotNull(nested, "主组位置应被垂直分栏替换");
        Assert.AreEqual(DockOrientation.Vertical, nested.Orientation);
        Assert.AreSame(ws.PrimaryGroup, nested.Children[0]);
    }

    [TestMethod]
    public void SplitDocument_LastDocOfSecondaryGroup_LeavesEmptyGroupBehind()
    {
        // 唯一标签的次级组拆分也必须生效(与主组行为一致),原组留空作放置目标。
        var ws = new DockWorkspace();
        TestDocument a = NewDoc("a");
        TestDocument b = NewDoc("b");
        ws.AddDocument(a);
        ws.AddDocument(b);
        ws.SplitDocument(b, DockOrientation.Horizontal);
        var g2 = (DockGroup)((DockSplit)ws.Root).Children[1];

        ws.SplitDocument(b, DockOrientation.Vertical);

        var root = (DockSplit)ws.Root;
        var nested = root.Children[1] as DockSplit;
        Assert.IsNotNull(nested, "次级组位置应被垂直分栏替换");
        Assert.AreEqual(DockOrientation.Vertical, nested.Orientation);
        Assert.AreSame(g2, nested.Children[0]);
        Assert.IsEmpty(g2.Documents, "原组留空");
        var g3 = (DockGroup)nested.Children[1];
        Assert.AreSequenceEqual([b], g3.Documents.ToArray());
        Assert.AreSame(b, ws.ActiveDocument);
    }

    [TestMethod]
    public void EmptySplitRemnant_IsCollapsedWhenSiblingCloses()
    {
        // 拆分留下的空面板在其兄弟全部关闭(分栏收敛)时自动回收,不留死空格。
        var ws = new DockWorkspace();
        TestDocument a = NewDoc("a");
        TestDocument b = NewDoc("b");
        ws.AddDocument(a);
        ws.AddDocument(b);
        ws.SplitDocument(b, DockOrientation.Horizontal); // root: [主组(a) | g2(b)]
        ws.SplitDocument(b, DockOrientation.Vertical);   // root: [主组(a) | v[g2空, g3(b)]]

        ws.CloseDocument(b); // g3 折叠 → v 分栏收敛出空 g2 → g2 一并回收

        Assert.AreSame(ws.PrimaryGroup, ws.Root, "空面板不应在兄弟关闭后残留");
        Assert.AreSame(a, ws.ActiveDocument);
    }

    // ---- 空组折叠与提升 ----

    [TestMethod]
    public void ClosingLastDocOfSplitGroup_CollapsesBackToSingleGroup()
    {
        var ws = new DockWorkspace();
        TestDocument a = NewDoc("a");
        TestDocument b = NewDoc("b");
        ws.AddDocument(a);
        ws.AddDocument(b);
        ws.SplitDocument(b, DockOrientation.Horizontal);

        ws.CloseDocument(b);

        Assert.AreSame(ws.PrimaryGroup, ws.Root, "空的次级组应折叠,单子分栏应提升");
        Assert.AreSame(a, ws.ActiveDocument);
    }

    [TestMethod]
    public void ClosingLastDocOfPrimaryGroup_CollapsesItAndFillsTheArea()
    {
        // 用户实测报回来的那一条:拖出分屏后把左半屏的标签关光,右半屏还是缩在右边。
        // 留在那儿的正是空的主组 —— 早先它被写成"永不折叠"。
        var ws = new DockWorkspace();
        TestDocument a = NewDoc("a");
        TestDocument b = NewDoc("b");
        ws.AddDocument(a);
        ws.AddDocument(b);
        ws.SplitDocument(b, DockOrientation.Horizontal); // root: [主组(a) | g2(b)]
        var g2 = (DockGroup)((DockSplit)ws.Root).Children[1];

        ws.CloseDocument(a);

        Assert.AreSame(g2, ws.Root, "空主组必须退场,剩下的窗格铺满整片区域");
        Assert.AreSame(g2, ws.PrimaryGroup, "兜底身份交给幸存的邻居");
        Assert.IsTrue(g2.IsPrimary);
        Assert.AreSame(b, ws.ActiveDocument);

        TestDocument c = NewDoc("c");
        ws.AddDocument(c);
        Assert.AreSequenceEqual([b, c], g2.Documents.ToArray(), "新文档进接手之后的主组");
    }

    [TestMethod]
    public void DraggingLastDocOutOfPrimaryGroup_CollapsesItToo()
    {
        // 与上一条同因异形:把主组最后一个标签拖进邻居,主组同样该退场。
        var ws = new DockWorkspace();
        TestDocument a = NewDoc("a");
        TestDocument b = NewDoc("b");
        ws.AddDocument(a);
        ws.AddDocument(b);
        ws.SplitDocument(b, DockOrientation.Horizontal);
        var g2 = (DockGroup)((DockSplit)ws.Root).Children[1];

        ws.DockTo(a, g2, DockPosition.Center);

        Assert.AreSame(g2, ws.Root);
        Assert.AreSame(g2, ws.PrimaryGroup);
        Assert.AreSequenceEqual([b, a], g2.Documents.ToArray());
    }

    [TestMethod]
    public void LastGroup_StaysEvenWhenEmpty()
    {
        // 不折叠的是**根**,不是某个特定的组 —— 布局树总得有个底,新文档才有地方落。
        var ws = new DockWorkspace();
        TestDocument a = NewDoc("a");
        ws.AddDocument(a);

        ws.CloseDocument(a);

        Assert.AreSame(ws.PrimaryGroup, ws.Root);
        Assert.IsEmpty(ws.PrimaryGroup.Documents);

        TestDocument b = NewDoc("b");
        ws.AddDocument(b);
        Assert.AreSequenceEqual([b], ws.PrimaryGroup.Documents.ToArray());
    }

    [TestMethod]
    public void Proportions_AreHoistedOnCollapse()
    {
        var ws = new DockWorkspace();
        TestDocument a = NewDoc("a");
        TestDocument b = NewDoc("b");
        ws.AddDocument(a);
        ws.AddDocument(b);
        ws.SplitDocument(b, DockOrientation.Horizontal);
        ws.Root.Proportion = 0.7; // 模拟外层比例
        ws.PrimaryGroup.Proportion = 0.4;

        ws.CloseDocument(b);

        Assert.AreEqual(0.7, ws.PrimaryGroup.Proportion, 1e-9, "提升时继承分栏的比例");
    }

    // ---- 停靠(拖放) ----

    [TestMethod]
    public void DockTo_Center_MovesAcrossGroups()
    {
        var ws = new DockWorkspace();
        TestDocument a = NewDoc("a");
        TestDocument b = NewDoc("b");
        TestDocument c = NewDoc("c");
        ws.AddDocument(a);
        ws.AddDocument(b);
        ws.AddDocument(c);
        ws.SplitDocument(c, DockOrientation.Horizontal);
        var newGroup = (DockGroup)((DockSplit)ws.Root).Children[1];

        ws.DockTo(b, newGroup, DockPosition.Center);

        Assert.AreSequenceEqual([a], ws.PrimaryGroup.Documents.ToArray());
        Assert.AreSequenceEqual([c, b], newGroup.Documents.ToArray());
        Assert.AreSame(b, ws.ActiveDocument);
    }

    [TestMethod]
    public void DockTo_CenterWithIndex_ReordersWithinGroup()
    {
        var ws = new DockWorkspace();
        TestDocument a = NewDoc("a");
        TestDocument b = NewDoc("b");
        TestDocument c = NewDoc("c");
        ws.AddDocument(a);
        ws.AddDocument(b);
        ws.AddDocument(c);

        ws.DockTo(c, ws.PrimaryGroup, DockPosition.Center, 0);

        Assert.AreSequenceEqual([c, a, b], ws.PrimaryGroup.Documents.ToArray());
    }

    [TestMethod]
    public void DockTo_LeftEdge_SplitsWithNewGroupFirst()
    {
        var ws = new DockWorkspace();
        TestDocument a = NewDoc("a");
        TestDocument b = NewDoc("b");
        ws.AddDocument(a);
        ws.AddDocument(b);

        ws.DockTo(b, ws.PrimaryGroup, DockPosition.Left);

        var split = (DockSplit)ws.Root;
        Assert.AreEqual(DockOrientation.Horizontal, split.Orientation);
        var newGroup = (DockGroup)split.Children[0];
        Assert.AreSequenceEqual([b], newGroup.Documents.ToArray());
        Assert.AreSame(ws.PrimaryGroup, split.Children[1]);
    }

    [TestMethod]
    public void DockTo_BottomEdge_SplitsVertically()
    {
        var ws = new DockWorkspace();
        TestDocument a = NewDoc("a");
        TestDocument b = NewDoc("b");
        ws.AddDocument(a);
        ws.AddDocument(b);

        ws.DockTo(b, ws.PrimaryGroup, DockPosition.Bottom);

        var split = (DockSplit)ws.Root;
        Assert.AreEqual(DockOrientation.Vertical, split.Orientation);
        Assert.AreSame(ws.PrimaryGroup, split.Children[0]);
        var newGroup = (DockGroup)split.Children[1];
        Assert.AreSequenceEqual([b], newGroup.Documents.ToArray());
    }

    [TestMethod]
    public void DockTo_OwnEdge_WhenOnlyDoc_BehavesLikeSplit()
    {
        var ws = new DockWorkspace();
        TestDocument a = NewDoc("a");
        TestDocument b = NewDoc("b");
        ws.AddDocument(a);
        ws.AddDocument(b);
        ws.SplitDocument(b, DockOrientation.Horizontal);
        var g2 = (DockGroup)((DockSplit)ws.Root).Children[1];

        ws.DockTo(b, g2, DockPosition.Right); // 拖到自己组的右缘 = 拆分,原组留空

        var root = (DockSplit)ws.Root;
        Assert.HasCount(3, root.Children, "同方向:空的原组 + 新组同级插入");
        Assert.AreSame(g2, root.Children[1]);
        Assert.IsEmpty(g2.Documents);
        var g3 = (DockGroup)root.Children[2];
        Assert.AreSequenceEqual([b], g3.Documents.ToArray());
    }

    // ---- 新文档的落点 ----

    [TestMethod]
    public void AddDocument_FillsTheEmptyPaneLeftBehindBySplit()
    {
        // 拆分单标签的组会原地留下一块"拖放标签到这里"的空面板。那块空白就是等着被填的:
        // 新会话该落进去,而不是挤进旁边那条已经有标签的条,把空面板晾着。
        var ws = new DockWorkspace();
        TestDocument a = NewDoc("a");
        ws.AddDocument(a);
        ws.SplitDocument(a, DockOrientation.Horizontal);
        DockGroup empty = ws.AllGroups().Single(group => group.Documents.Count == 0);

        TestDocument b = NewDoc("b");
        ws.AddDocument(b);

        Assert.AreSame(empty, ws.FindGroup(b));
        Assert.HasCount(2, ws.AllGroups(), "填空而已,不该再添一格");
    }

    [TestMethod]
    public void AddDocument_GoesToTheActivePane_WhenNoPaneIsEmpty()
    {
        // 分屏之后人眼盯着右半屏,新标签却开在左半屏的标签条上 —— 焦点跑了,视线还留在原处。
        var ws = new DockWorkspace();
        TestDocument a = NewDoc("a");
        TestDocument b = NewDoc("b");
        ws.AddDocument(a);
        ws.AddDocument(b);
        ws.SplitDocument(b, DockOrientation.Horizontal); // 激活留在新拆出来的 g2
        var g2 = (DockGroup)((DockSplit)ws.Root).Children[1];

        TestDocument c = NewDoc("c");
        ws.AddDocument(c);

        Assert.AreSame(g2, ws.FindGroup(c), "新标签开在正在用的那半边");
        Assert.AreSame(c, ws.ActiveDocument);
        Assert.AreSequenceEqual([a], ws.PrimaryGroup.Documents.ToArray(), "另一半原样不动");
    }

    // ---- 显式落点(标签条上的「+」) ----

    /// <summary>布局:左格 [a]、一块空窗格、右格 [b](拆分只有一个标签的组会原地留下空窗格)。</summary>
    private static (DockWorkspace Ws, DockGroup Left, DockGroup Empty) LayoutWithAnEmptyPane()
    {
        var ws = new DockWorkspace();
        TestDocument a = NewDoc("a");
        TestDocument b = NewDoc("b");
        ws.AddDocument(a);
        ws.AddDocument(b);
        ws.SplitDocument(b, DockOrientation.Horizontal);
        ws.SplitDocument(b, DockOrientation.Vertical);
        return (ws, ws.FindGroup(a)!, ws.AllGroups().Single(g => g.Documents.Count == 0));
    }

    [TestMethod]
    public void AddDocument_WithAnExplicitTarget_BeatsTheEmptyPane()
    {
        (DockWorkspace ws, DockGroup left, DockGroup empty) = LayoutWithAnEmptyPane();

        TestDocument c = NewDoc("c");
        ws.AddDocument(c, left);

        Assert.AreSame(left, ws.FindGroup(c), "按下的那一格优先于空窗格");
        Assert.AreSame(c, ws.ActiveDocument);
        Assert.IsEmpty(empty.Documents);
    }

    [TestMethod]
    public void AddDocument_WithATargetNoLongerOnTheTree_FallsBackToTheDefault()
    {
        (DockWorkspace ws, DockGroup left, DockGroup empty) = LayoutWithAnEmptyPane();
        var gone = new DockGroup();

        TestDocument c = NewDoc("c");
        ws.AddDocument(c, gone);

        Assert.AreSame(empty, ws.FindGroup(c));
        Assert.HasCount(1, left.Documents);
    }

    /// <summary>
    /// 票据顺着 <c>await</c> 传进流程深处那一下 <c>AddDocument</c>(连接流程一个参数都不加);只认第一个,释放即作废。
    /// </summary>
    [TestMethod]
    public async Task TargetTicket_FlowsThroughAwaits_IsUsedOnce_AndExpiresOnDispose()
    {
        (DockWorkspace ws, DockGroup left, DockGroup empty) = LayoutWithAnEmptyPane();
        TestDocument first = NewDoc("first");
        TestDocument second = NewDoc("second");
        TestDocument afterwards = NewDoc("afterwards");
        Task? lingering = null;
        var release = new TaskCompletionSource();

        using (ws.TargetNewDocumentsAt(left))
        {
            await Task.Yield();
            await Task.Run(() => { }); // 换个线程回来:异步上下文照样带着票据。
            ws.AddDocument(first);
            ws.AddDocument(second);
            // 流程里另起的后台任务继承上下文,但它在流程结束之后才建的东西不该落进这一格。
            lingering = Task.Run(async () =>
            {
                await release.Task;
                ws.AddDocument(afterwards);
            });
        }
        release.SetResult();
        await lingering;

        Assert.AreSame(left, ws.FindGroup(first), "第一个认领票据");
        Assert.AreSame(empty, ws.FindGroup(second), "一张票只用一次,第二个照默认规则(填空窗格)");
        Assert.AreNotSame(left, ws.FindGroup(afterwards), "释放之后作废");
    }

    /// <summary>
    /// 流程里没建标签(对话框上只按了「保存」、取消了):票据随流程作废 ——
    /// 流程里另起、之后才建东西的后台任务继承了上下文,也拿不到这张票。
    /// </summary>
    [TestMethod]
    public async Task TargetTicket_LeftUnused_ExpiresWithTheFlow()
    {
        (DockWorkspace ws, DockGroup left, DockGroup empty) = LayoutWithAnEmptyPane();
        TestDocument late = NewDoc("late");
        var release = new TaskCompletionSource();
        Task lingering;

        using (ws.TargetNewDocumentsAt(left))
        {
            await Task.Yield();
            lingering = Task.Run(async () =>
            {
                await release.Task;
                ws.AddDocument(late);
            });
        }
        release.SetResult();
        await lingering;

        Assert.AreSame(empty, ws.FindGroup(late), "流程结束之后建的照默认规则");
    }

    /// <summary>别的流程(自动重连、还原布局模板)看不到这张票,不会被拐进这一格。</summary>
    [TestMethod]
    public async Task TargetTicket_IsInvisibleToOtherFlows()
    {
        (DockWorkspace ws, DockGroup left, DockGroup empty) = LayoutWithAnEmptyPane();
        var opened = new TaskCompletionSource();
        var otherDone = new TaskCompletionSource();
        TestDocument mine = NewDoc("mine");
        TestDocument other = NewDoc("other");

        Task plusFlow = PlusFlowAsync();
        var otherFlow = Task.Run(async () =>
        {
            await opened.Task;
            ws.AddDocument(other);
            otherDone.SetResult();
        });
        await Task.WhenAll(plusFlow, otherFlow);

        Assert.AreSame(empty, ws.FindGroup(other), "没拿票的流程照默认规则");
        Assert.AreSame(left, ws.FindGroup(mine));

        async Task PlusFlowAsync()
        {
            using IDisposable ticket = ws.TargetNewDocumentsAt(left);
            opened.SetResult();
            await otherDone.Task;
            ws.AddDocument(mine);
        }
    }

    /// <summary>
    /// 流程里另起、迟迟不结束的后台任务(SSH 的收包、保活循环)捕获了执行上下文,也就一直攥着这张票:
    /// 票作废时得放掉那一格,否则用户关掉的窗格回收不了。
    /// </summary>
    [TestMethod]
    public async Task TargetTicket_HeldByALingeringBackgroundTask_LetsGoOfThePaneOnceExpired()
    {
        var ws = new DockWorkspace();
        var release = new TaskCompletionSource();

        (Task lingering, WeakReference pane) = await TicketHeldByALingeringTaskAsync(ws, release.Task);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.IsFalse(pane.IsAlive, "后台任务还挂着,但作废的票不该再攥着那个已经关掉的窗格");
        release.SetResult();
        await lingering;
    }

    /// <summary>对一格空窗格发票、流程里起一个等 <paramref name="release" /> 的后台任务、流程结束,再把那一格关掉。</summary>
    /// <remarks>窗格只在这个方法里出现过:测试方法自己的局部变量不能攥着它。</remarks>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<(Task Lingering, WeakReference Pane)> TicketHeldByALingeringTaskAsync(DockWorkspace ws, Task release)
    {
        TestDocument a = NewDoc("a");
        ws.AddDocument(a);
        ws.SplitDocument(a, DockOrientation.Horizontal);
        DockGroup empty = ws.AllGroups().Single(g => g.Documents.Count == 0);
        Task lingering;
        using (ws.TargetNewDocumentsAt(empty))
        {
            await Task.Yield();
            lingering = Task.Run(async () => await release);
        }
        ws.ClosePane(empty);
        Assert.DoesNotContain(empty, ws.AllGroups().ToList());
        return (lingering, new WeakReference(empty));
    }

    // ---- 关闭窗格 / 最大化 / 平分 ----

    [TestMethod]
    public void ClosePane_OnEmptyPane_RemovesItFromTheLayout()
    {
        // 空面板在此之前没有任何撤销入口 —— 只能把邻居的标签拖进去再拖回来,靠副作用挤掉它。
        var ws = new DockWorkspace();
        TestDocument a = NewDoc("a");
        ws.AddDocument(a);
        ws.SplitDocument(a, DockOrientation.Horizontal);
        DockGroup empty = ws.AllGroups().Single(group => group.Documents.Count == 0);

        ws.ClosePane(empty);

        var root = ws.Root as DockGroup;
        Assert.IsNotNull(root, "空面板撤掉后单子分栏该提升,不留一层空壳");
        Assert.AreSequenceEqual([a], root.Documents.ToArray());
    }

    [TestMethod]
    public void ClosePane_WithDocuments_ClosesThemAndCollapsesThePane()
    {
        var ws = new DockWorkspace();
        TestDocument a = NewDoc("a");
        TestDocument b = NewDoc("b");
        TestDocument c = NewDoc("c");
        ws.AddDocument(a);
        ws.AddDocument(b);
        ws.AddDocument(c);
        ws.SplitDocument(c, DockOrientation.Horizontal);
        var g2 = (DockGroup)((DockSplit)ws.Root).Children[1];
        List<DockDocument> closed = [];
        ws.DocumentClosed += closed.Add;

        ws.ClosePane(g2);

        Assert.AreSequenceEqual([c], closed.ToArray(), "关窗格 = 关掉里面的标签,按用户语义走");
        Assert.AreSame(ws.PrimaryGroup, ws.Root);
    }

    [TestMethod]
    public void ToggleMaximize_NeedsMoreThanOnePane()
    {
        var ws = new DockWorkspace();
        TestDocument a = NewDoc("a");
        ws.AddDocument(a);

        ws.ToggleMaximizeGroup(ws.PrimaryGroup);

        Assert.IsNull(ws.MaximizedGroup, "只有一格时最大化没有意义,别留下一个解不掉的状态");
    }

    [TestMethod]
    public void ToggleMaximize_IsReleasedWhenFocusMovesToAnotherPane()
    {
        // 与 tmux 的 select-pane 同一条规矩:焦点已经在别处、屏幕上却还是那一格,只会让人发愣。
        var ws = new DockWorkspace();
        TestDocument a = NewDoc("a");
        TestDocument b = NewDoc("b");
        ws.AddDocument(a);
        ws.AddDocument(b);
        ws.SplitDocument(b, DockOrientation.Horizontal);
        var g2 = (DockGroup)((DockSplit)ws.Root).Children[1];

        ws.ToggleMaximizeGroup(g2);
        Assert.AreSame(g2, ws.MaximizedGroup);

        ws.ActivateDocument(a);

        Assert.IsNull(ws.MaximizedGroup);
        Assert.AreSame(a, ws.ActiveDocument);
    }

    [TestMethod]
    public void ToggleMaximize_IsReleasedWhenThePaneGoesAway()
    {
        var ws = new DockWorkspace();
        TestDocument a = NewDoc("a");
        TestDocument b = NewDoc("b");
        ws.AddDocument(a);
        ws.AddDocument(b);
        ws.SplitDocument(b, DockOrientation.Horizontal);
        var g2 = (DockGroup)((DockSplit)ws.Root).Children[1];
        ws.ToggleMaximizeGroup(g2);

        ws.CloseDocument(b); // g2 空了 → 折叠

        Assert.IsNull(ws.MaximizedGroup, "被最大化的窗格已经不在树上,状态必须一起消失");
        Assert.AreSame(ws.PrimaryGroup, ws.Root);
    }

    [TestMethod]
    public void EqualizePanes_ResetsEveryProportion()
    {
        var ws = new DockWorkspace();
        TestDocument a = NewDoc("a");
        TestDocument b = NewDoc("b");
        ws.AddDocument(a);
        ws.AddDocument(b);
        ws.SplitDocument(b, DockOrientation.Horizontal);
        var split = (DockSplit)ws.Root;
        split.Children[0].Proportion = 0.85;
        split.Children[1].Proportion = 0.15;

        ws.EqualizePanes();

        Assert.IsTrue(double.IsNaN(split.Children[0].Proportion));
        Assert.IsTrue(double.IsNaN(split.Children[1].Proportion));
    }

    [TestMethod]
    public void MoveDocument_ReordersWithinGroup()
    {
        var ws = new DockWorkspace();
        TestDocument a = NewDoc("a");
        TestDocument b = NewDoc("b");
        TestDocument c = NewDoc("c");
        ws.AddDocument(a);
        ws.AddDocument(b);
        ws.AddDocument(c);

        ws.MoveDocument(a, 2);

        Assert.AreSequenceEqual([b, c, a], ws.PrimaryGroup.Documents.ToArray());
    }
}
