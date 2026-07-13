using VelaShell.Docking.Model;

namespace VelaShell.Tests.Docking;

/// <summary>
/// VelaDock 模型层(自研 Dock.Avalonia 替换,docs/dock-replacement-plan.md)的结构操作测试:
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
        CollectionAssert.AreEqual(new[] { a }, ws.PrimaryGroup.Documents.ToArray());
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
        CollectionAssert.AreEqual(new[] { a }, closed);
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
        CollectionAssert.AreEqual(new[] { b, c }, ws.PrimaryGroup.Documents.ToArray());

        ws.CloseRightDocuments(b);
        CollectionAssert.AreEqual(new[] { b }, ws.PrimaryGroup.Documents.ToArray());

        ws.CloseOtherDocuments(d);
        Assert.Contains(b, ws.AllDocuments(), "其他组的标签不受影响");

        ws.CloseAllDocuments(b);
        CollectionAssert.AreEqual(new[] { d }, ws.AllDocuments().ToArray());
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
        CollectionAssert.AreEqual(new[] { b }, newGroup.Documents.ToArray());
        Assert.AreSame(b, newGroup.ActiveDocument);
        Assert.AreSame(b, ws.ActiveDocument);
        CollectionAssert.AreEqual(new[] { a }, ws.PrimaryGroup.Documents.ToArray());
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
        // 用户反馈:唯一标签的次级组拆分也必须生效(与主组行为一致),原组留空作放置目标。
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
        CollectionAssert.AreEqual(new[] { b }, g3.Documents.ToArray());
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
    public void PrimaryGroup_NeverCollapses()
    {
        var ws = new DockWorkspace();
        TestDocument a = NewDoc("a");
        TestDocument b = NewDoc("b");
        ws.AddDocument(a);
        ws.AddDocument(b);
        ws.SplitDocument(b, DockOrientation.Horizontal);

        ws.CloseDocument(a); // 清空主组

        var split = ws.Root as DockSplit;
        Assert.IsNotNull(split, "主组即使为空也保留");
        Assert.HasCount(2, split.Children);
        Assert.IsEmpty(ws.PrimaryGroup.Documents);
        // 新文档仍然进主组
        TestDocument c = NewDoc("c");
        ws.AddDocument(c);
        CollectionAssert.AreEqual(new[] { c }, ws.PrimaryGroup.Documents.ToArray());
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

        CollectionAssert.AreEqual(new[] { a }, ws.PrimaryGroup.Documents.ToArray());
        CollectionAssert.AreEqual(new[] { c, b }, newGroup.Documents.ToArray());
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

        CollectionAssert.AreEqual(new[] { c, a, b }, ws.PrimaryGroup.Documents.ToArray());
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
        CollectionAssert.AreEqual(new[] { b }, newGroup.Documents.ToArray());
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
        CollectionAssert.AreEqual(new[] { b }, newGroup.Documents.ToArray());
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
        CollectionAssert.AreEqual(new[] { b }, g3.Documents.ToArray());
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

        CollectionAssert.AreEqual(new[] { b, c, a }, ws.PrimaryGroup.Documents.ToArray());
    }
}
