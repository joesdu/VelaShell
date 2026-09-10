using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Controls;
using VelaShell.Docking;
using VelaShell.Docking.Controls;
using VelaShell.Docking.Model;

namespace VelaShell.Tests.Views;

/// <summary>
/// 分屏窗格的交互:最大化 / 还原、活动窗格标记、空窗格的出口、标签条滚轮与中键关闭。
/// </summary>
/// <remarks>
/// 这几条都是"模型对了但界面没跟上"最容易漏的地方 —— 尤其是最大化:它把整棵可视树
/// 换成一个窗格再换回来,而内容视图是**按文档缓存、跨宿主收养**的
/// (见 <c>DockContentBlankHuntTests</c> 记的那次"终端内容凭空消失")。
/// 还原之后内容还在不在,只有真跑一遍界面才知道。
/// </remarks>
[TestClass]
[TestCategory("DockPaneUi")]
public sealed class DockPaneUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(DockPaneUiTests).Assembly);

    [TestMethod]
    public void MaximizePane_RendersOnlyThatPane_AndRestoresContentIntact()
    {
        _session.Dispatch(() =>
        {
            (Window window, DockWorkspaceControl dock, DockWorkspace workspace, TestDocument left, TestDocument right) =
                SplitScene();
            DockGroup rightGroup = workspace.FindGroup(right)!;
            Control rightView = right.View!;

            workspace.ToggleMaximizeGroup(rightGroup);
            Pump(window);

            Assert.HasCount(1, GroupControls(dock), "最大化时只渲染那一个窗格");
            Assert.AreSame(rightGroup, GroupControls(dock)[0].Group);
            Assert.AreSame(rightView, Hosts(dock).Single().Target, "内容原样跟过来,不重建");

            workspace.ToggleMaximizeGroup(rightGroup);
            Pump(window);

            Assert.HasCount(2, GroupControls(dock), "还原后两格都回来");
            Assert.AreSame(left.View, HostOf(dock, workspace.FindGroup(left)!).Target,
                           "另一格的内容必须被重新收养回来,而不是留下一块空白");
            Assert.AreSame(rightView, HostOf(dock, rightGroup).Target, "视图实例全程只构建一次");

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void ActivePane_IsTheOnlyOneMarkedForStyling()
    {
        _session.Dispatch(() =>
        {
            (Window window, DockWorkspaceControl dock, DockWorkspace workspace, TestDocument left, TestDocument right) =
                SplitScene();

            // 分屏后每格都画着自己那条激活标签线;靠这个标记把非活动格弱化下去。
            AssertActivePane(dock, workspace.FindGroup(right)!);

            workspace.ActivateDocument(left);
            Pump(window);
            AssertActivePane(dock, workspace.FindGroup(left)!);
            SaveOptionalFrame(window, "dock-active-pane.png");

            // 把当前激活的标签整个拖进隔壁:ActiveDocument 的**值没变**,
            // ActiveDocumentChanged 不会响,但活动窗格已经易主。
            workspace.DockTo(left, workspace.FindGroup(right)!, DockPosition.Center);
            Pump(window);
            AssertActivePane(dock, workspace.FindGroup(left)!);

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void EmptyPane_OffersAWayOut()
    {
        _session.Dispatch(() =>
        {
            var workspace = new DockWorkspace();
            var only = new TestDocument("only");
            workspace.AddDocument(only);
            workspace.SplitDocument(only, DockOrientation.Horizontal); // 原组留空作为放置目标

            var dock = new DockWorkspaceControl { Workspace = workspace };
            var window = new Window { Width = 640, Height = 360, Content = dock };
            window.Show();
            Pump(window);

            DockGroup empty = workspace.AllGroups().Single(group => group.Documents.Count == 0);
            DockGroupControl emptyControl = GroupControls(dock).Single(control => ReferenceEquals(control.Group, empty));
            Assert.IsTrue(NamedPanel(emptyControl, "EmptyHint").IsVisible, "空窗格要说明自己是干什么的");
            Assert.IsTrue(NamedPanel(emptyControl, "DropTargetHint").IsVisible,
                          "旁边还有标签可以拖过来,这一格也撤得掉 —— 该给的是这一套提示");
            Assert.IsFalse(NamedPanel(emptyControl, "NoSessionHint").IsVisible);
            Button close = emptyControl.GetVisualDescendants().OfType<Button>()
                                       .Single(button => button.Name == "ClosePaneButton");
            SaveOptionalFrame(window, "dock-empty-pane.png");

            ClickCenter(window, close);
            Pump(window);

            Assert.IsInstanceOfType<DockGroup>(workspace.Root, "空窗格撤掉后单子分栏该提升");
            Assert.HasCount(1, GroupControls(dock));

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void EmptyWorkspace_SaysWhatIsGoingOn_WithoutADeadButton()
    {
        _session.Dispatch(() =>
        {
            // 一个会话都没开(刚启动 / 关光了所有标签):既没有标签可拖,也没有窗格可关。
            // 此前这里照样挂着"拖标签到这里"和一枚点了毫无反应的「关闭窗格」——
            // ClosePane 对根窗格是空操作,那枚按钮比没有按钮更糟(用户实测反馈)。
            var workspace = new DockWorkspace();
            var dock = new DockWorkspaceControl { Workspace = workspace };
            var window = new Window { Width = 480, Height = 320, Content = dock };
            window.Show();
            Pump(window);

            DockGroupControl root = GroupControls(dock).Single();
            Assert.IsNull(root.Group?.Parent, "根窗格没有父分栏 —— 判据就是它");
            Assert.IsTrue(NamedPanel(root, "EmptyHint").IsVisible);
            Assert.IsTrue(NamedPanel(root, "NoSessionHint").IsVisible, "该说的是现状与下一步");
            Assert.IsFalse(NamedPanel(root, "DropTargetHint").IsVisible,
                           "没有任何标签存在时,不该让人去拖一个不存在的标签,也不该给关不掉的按钮");
            SaveOptionalFrame(window, "dock-no-session.png");

            // 开一个再关掉,回到同一个状态 —— 这条路径走的是 CollectionChanged,不是初次构建。
            var doc = new TestDocument("one");
            workspace.AddDocument(doc);
            Pump(window);
            Assert.IsFalse(NamedPanel(root, "EmptyHint").IsVisible);

            workspace.CloseDocument(doc);
            Pump(window);
            Assert.IsTrue(NamedPanel(root, "NoSessionHint").IsVisible);
            Assert.IsFalse(NamedPanel(root, "DropTargetHint").IsVisible);

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void TabStripWheel_ScrollsTabs_OnlyWhenTheyOverflow()
    {
        _session.Dispatch(() =>
        {
            var workspace = new DockWorkspace();
            foreach (int index in Enumerable.Range(0, 8))
            {
                workspace.AddDocument(new TestDocument($"标签编号 {index}"));
            }

            var dock = new DockWorkspaceControl { Workspace = workspace };
            var window = new Window { Width = 260, Height = 300, Content = dock };
            window.Show();
            Pump(window);

            DockGroupControl group = GroupControls(dock).Single();
            ScrollViewer strip = group.GetVisualDescendants().OfType<ScrollViewer>()
                                      .Single(viewer => viewer.Name == "TabScroll");
            Assert.IsGreaterThan(strip.Viewport.Width, strip.Extent.Width, "窗口要窄到标签真的溢出,否则这条测的是空气");

            Point spot = strip.TranslatePoint(new Point(strip.Bounds.Width / 2, strip.Bounds.Height / 2), window)
                         ?? throw new AssertFailedException("标签条不在可视树上。");
            window.MouseWheel(spot, new Vector(0, -1));
            Pump(window);

            Assert.IsGreaterThan(0, strip.Offset.X, "在标签条上滚轮应当横向翻标签");

            window.MouseWheel(spot, new Vector(0, 1));
            window.MouseWheel(spot, new Vector(0, 1));
            Pump(window);
            Assert.AreEqual(0d, strip.Offset.X, 0.01, "滚回头就停在开头,不该滚出负数");

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void MiddleClickOnATab_ClosesThatTab()
    {
        _session.Dispatch(() =>
        {
            var workspace = new DockWorkspace();
            // 用插件文档:它是最省事的一种"真标签"(有 DataTemplate、走 DockTabItemBase),
            // 而中键关闭正是挂在那个基类上、五种标签共用的一条路。
            var keep = new PluginDocument("keep", "keep", "acme.demo", new Border());
            var doomed = new PluginDocument("doomed", "doomed", "acme.demo", new Border());
            workspace.AddDocument(keep);
            workspace.AddDocument(doomed);
            List<DockDocument> closed = [];
            workspace.DocumentClosed += closed.Add;

            var dock = new DockWorkspaceControl { Workspace = workspace };
            var window = new Window { Width = 640, Height = 300, Content = dock };
            window.Show();
            Pump(window);

            ItemsControl tabs = GroupControls(dock).Single().GetVisualDescendants().OfType<ItemsControl>()
                                                  .Single(items => items.Name == "TabsHost");
            Control container = tabs.ContainerFromItem(doomed)
                                ?? throw new AssertFailedException("标签容器没有实体化。");
            // 贴左缘按下,避开标签右端那枚 × —— 要测的是"标签上中键",不是"按到了关闭钮"。
            Point spot = container.TranslatePoint(new Point(6, container.Bounds.Height / 2), window)
                         ?? throw new AssertFailedException("标签不在可视树上。");
            window.MouseDown(spot, MouseButton.Middle);
            window.MouseUp(spot, MouseButton.Middle);
            Pump(window);

            Assert.AreSequenceEqual([doomed], closed.ToArray(), "中键关掉的必须是指针底下那一个");
            Assert.AreSequenceEqual([keep], workspace.PrimaryGroup.Documents.ToArray());

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    // ---- 脚手架 ----

    /// <summary>两格分屏:左边 left、右边 right(激活在 right)。</summary>
    private static (Window Window, DockWorkspaceControl Dock, DockWorkspace Workspace, TestDocument Left, TestDocument Right)
        SplitScene()
    {
        var workspace = new DockWorkspace();
        var left = new TestDocument("left");
        var right = new TestDocument("right");
        workspace.AddDocument(left);
        workspace.AddDocument(right);
        workspace.SplitDocument(right, DockOrientation.Horizontal);

        var dock = new DockWorkspaceControl { Workspace = workspace };
        var window = new Window { Width = 800, Height = 400, Content = dock };
        window.Show();
        Pump(window);
        return (window, dock, workspace, left, right);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    /// <summary>按名字取窗格里的某块面板(空状态那两套提示各是一块)。</summary>
    private static Panel NamedPanel(DockGroupControl group, string name) =>
        group.GetVisualDescendants().OfType<Panel>().Single(panel => panel.Name == name);

    private static List<DockGroupControl> GroupControls(DockWorkspaceControl dock) =>
        [.. dock.GetVisualDescendants().OfType<DockGroupControl>()];

    private static List<ReparentingHost> Hosts(DockWorkspaceControl dock) =>
        [.. dock.GetVisualDescendants().OfType<ReparentingHost>()];

    private static ReparentingHost HostOf(DockWorkspaceControl dock, DockGroup group) =>
        GroupControls(dock).Single(control => ReferenceEquals(control.Group, group))
                           .GetVisualDescendants().OfType<ReparentingHost>().Single();

    private static void AssertActivePane(DockWorkspaceControl dock, DockGroup expected)
    {
        foreach (DockGroupControl control in GroupControls(dock))
        {
            bool shouldBeActive = ReferenceEquals(control.Group, expected);
            Assert.AreEqual(shouldBeActive, control.Classes.Contains(":activepane"),
                            $"窗格 {(shouldBeActive ? "应当" : "不应")}被标记为活动窗格。");

            // 标记本身不值钱,值钱的是它真的把强调线弱化了 —— 读基值绕开 180ms 过渡的中间态。
            Border indicator = control.GetVisualDescendants().OfType<Border>()
                                      .Single(border => border.Name == "ActiveTabIndicator");
            Assert.AreEqual(shouldBeActive ? 1 : 0.3, indicator.Opacity, 0.001,
                            "非活动窗格的强调线必须弱下去,否则分屏时看不出键盘输入落在哪一格。");
        }
    }

    /// <summary>设了 VELASHELL_VISUAL_QA_DIR 就存一帧,供人眼复核样式(与 DockMotionUiTests 同款)。</summary>
    private static void SaveOptionalFrame(TopLevel topLevel, string fileName)
    {
        string? directory = Environment.GetEnvironmentVariable("VELASHELL_VISUAL_QA_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }
        Directory.CreateDirectory(directory);
        using WriteableBitmap? frame = topLevel.CaptureRenderedFrame();
        Assert.IsNotNull(frame, "Skia headless renderer should produce a visual-QA frame.");
        using FileStream output = File.Create(Path.Combine(directory, fileName));
        frame.Save(output, PngBitmapEncoderOptions.Default);
    }

    private static void ClickCenter(Window window, Control control)
    {
        Point spot = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
                     ?? throw new AssertFailedException("控件不在可视树上。");
        window.MouseDown(spot, MouseButton.Left);
        window.MouseUp(spot, MouseButton.Left);
    }

    private sealed class TestDocument : DockDocument, IDockViewProvider
    {
        public TestDocument(string title) => Title = title;

        public Control? View { get; private set; }

        public Control CreateView() => View ??= new Border { Background = Brushes.Transparent };
    }
}
