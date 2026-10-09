using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// Headless UI 端到端:用 Avalonia headless 平台实例化真实的 <see cref="VelaTerminalControl" />,
/// 渲染后派发真实鼠标事件,验证「在折叠列点击 → 真的折叠 / 再点展开」。这一层覆盖单元测试够不到的
/// Avalonia 事件投递 + 渲染 + 命中链路。
/// </summary>
[TestClass]
[TestCategory("GutterFoldUi")]
public class GutterFoldUiTests
{
    /// <summary>全程序集共用的 headless 会话(见 HeadlessTestSession:每类各起一个时,拆除会互相踩)。</summary>
    private static HeadlessUnitTestSession _session => HeadlessTestSession.Current;

    private static void OnUi(Action body) =>
        _session.Dispatch(() =>
        {
            body();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();

    [TestMethod]
    public void RealPointerClick_InFoldColumn_FoldsAndExpands()
    {
        OnUi(() =>
        {
            var control = new VelaTerminalControl { ShowFoldMarker = true };
            control.Feed(Encoding.UTF8.GetBytes("L0\r\nL1\r\nL2\r\nL3\r\nL4\r\nL5"));

            var window = new Window { Width = 480, Height = 320, Content = control };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                window.CaptureRenderedFrame(); // 强制一帧渲染 → 填充屏幕行映射

                Assert.IsGreaterThan(0, control.CellHeightForTest, "渲染后应有有效的单元格高度。");
                Assert.AreEqual(0, control.FoldCountForTest);

                // 只开折叠标记时折叠列从 x=0 开始;点击第 3 屏幕行(L0..L5 占屏幕行 0..5)。
                GutterLayout gutter = control.GutterForTest;
                var point = new Point(gutter.FoldLeft + 2, 3 * control.CellHeightForTest + 2);
                Assert.IsTrue(gutter.IsFoldColumnHit(point.X));

                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();

                Assert.AreEqual(1, control.FoldCountForTest, "在折叠列点击应产生一个折叠区域。");

                // 折叠后 L3 成为折叠头,位于屏幕顶行;点它展开。
                window.CaptureRenderedFrame();
                var expandPoint = new Point(gutter.FoldLeft + 2, 0 * control.CellHeightForTest + 2);
                window.MouseDown(expandPoint, MouseButton.Left);
                window.MouseUp(expandPoint, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();

                Assert.AreEqual(0, control.FoldCountForTest, "点击折叠头应展开(折叠数归零)。");
            }
            finally
            {
                // 窗口必须关:留在共享 UI 线程上的渲染会拖到会话拆除时才跑(见 HeadlessTestSession)。
                window.Close();
            }
        });
    }

    [TestMethod]
    public void RealPointerClick_OnBlankRowBelowOutput_DoesNotFold()
    {
        // 2026-07-23 内容消失事故回归:最后一行输出之下的空白屏幕行也是合法活动屏行,
        // 此前点击其折叠列会把上方内容整段折叠——用户视角就是"终端内容凭空消失"。
        OnUi(() =>
        {
            var control = new VelaTerminalControl { ShowFoldMarker = true };
            control.Feed(Encoding.UTF8.GetBytes("L0\r\nL1\r\nL2\r\nL3\r\nL4\r\nL5"));

            var window = new Window { Width = 480, Height = 320, Content = control };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                window.CaptureRenderedFrame();

                GutterLayout gutter = control.GutterForTest;

                // 内容行可折叠,空白行不可(直接断言守卫谓词)。
                Assert.IsTrue(control.IsFoldTargetRow(3), "有内容的行应可作为折叠目标。");
                Assert.IsFalse(control.IsFoldTargetRow(10), "输出之下的空白行不得作为折叠目标。");

                // 真实点击空白区域的折叠列:必须毫无反应。
                var blankPoint = new Point(gutter.FoldLeft + 2, 10 * control.CellHeightForTest + 2);
                Assert.IsTrue(gutter.IsFoldColumnHit(blankPoint.X));
                window.MouseDown(blankPoint, MouseButton.Left);
                window.MouseUp(blankPoint, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();

                Assert.AreEqual(0, control.FoldCountForTest, "点击空白行的折叠列不得产生折叠。");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void GutterContextMenu_HasFiveToggles_ReflectingCurrentState()
    {
        OnUi(() =>
        {
            var control = new VelaTerminalControl { ShowLineNumber = true, ShowFoldGuideLine = true };
            ContextMenu menu = control.BuildGutterContextMenu();
            Assert.AreEqual(5, menu.Items.Count, "菜单应含 5 个部件开关(行号/时间戳/分隔线/折叠标记/空白)。");

            var lineNumberItem = (MenuItem)menu.Items[0]!;
            var timestampItem = (MenuItem)menu.Items[1]!;
            var guideLineItem = (MenuItem)menu.Items[2]!;
            var foldItem = (MenuItem)menu.Items[3]!;
            Assert.IsTrue(lineNumberItem.IsChecked, "行号已开,菜单项应为勾选态。");
            Assert.IsFalse(timestampItem.IsChecked, "时间戳未开,菜单项应为未勾选态。");
            Assert.IsTrue(guideLineItem.IsChecked, "分隔线已开,菜单项应为勾选态。");
            Assert.IsFalse(foldItem.IsChecked, "分隔线与折叠是两个开关:只开线时折叠项不勾。");
        });
    }

    /// <summary>右键菜单切分隔线:改的是分隔线自己,上报给上层持久化时也单独带着这一位。</summary>
    [TestMethod]
    public void GutterContextMenu_ClickingGuideLine_TogglesOnlyGuideLine_AndReportsIt()
    {
        OnUi(() =>
        {
            (VelaTerminalControl control, ContextMenu menu, Window window) = ShowGutterMenu(new() { ShowFoldMarker = true });
            bool? reportedFold = null, reportedLine = null;
            control.GutterOptionsChanged += (_, _, fold, _, line) =>
            {
                reportedFold = fold;
                reportedLine = line;
            };
            try
            {
                ClickMenuItem((MenuItem)menu.Items[2]!);

                Assert.IsTrue(control.ShowFoldGuideLine, "点「分隔线」应打开分隔线。");
                Assert.IsTrue(control.ShowFoldMarker, "折叠开关不受影响。");
                Assert.IsTrue(reportedLine, "上报的分隔线状态应为开。");
                Assert.IsTrue(reportedFold, "上报的折叠状态应保持原样。");
            }
            finally
            {
                menu.Close();
                window.Close();
            }
        });
    }

    /// <summary>菜单项 → 属性开关的接线:走真实指针点击,不用合成事件(见 ClickMenuItem 的说明)。</summary>
    [TestMethod]
    public void GutterContextMenu_ClickingItem_TogglesOption()
    {
        OnUi(() =>
        {
            (VelaTerminalControl control, ContextMenu menu, Window window) = ShowGutterMenu(new() { ShowLineNumber = true });
            try
            {
                ClickMenuItem((MenuItem)menu.Items[0]!);

                Assert.IsFalse(control.ShowLineNumber, "点击「行号」菜单项应关闭行号。");
            }
            finally
            {
                menu.Close();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 勾号必须由勾选列渲染,不能拼进 Header —— 拼字符会让文字随开关左右跳,且勾号颜色不跟随主题。
    /// </summary>
    [TestMethod]
    public void GutterContextMenu_RendersCheckInToggleColumn_NotAsHeaderText()
    {
        OnUi(() =>
        {
            var control = new VelaTerminalControl { ShowLineNumber = true };
            ContextMenu menu = control.BuildGutterContextMenu();

            foreach (object? raw in menu.Items)
            {
                var item = (MenuItem)raw!;
                Assert.AreEqual(MenuItemToggleType.CheckBox, item.ToggleType, "菜单项应是复选型。");

                // Header 只放标签:开与关的 Header 必须完全一致,文字才不会随勾选状态位移。
                string header = (string)item.Header!;
                Assert.AreEqual(header.Trim(), header, "Header 不应含用于占位/勾号的空白前缀。");
                Assert.IsFalse(header.Contains('✔'), "勾号应由勾选列渲染,不应拼进 Header。");
            }
        });
    }

    /// <summary>
    /// 菜单点击后不关闭(StaysOpenOnClick),同一项可被连点;开关必须跟着来回翻,
    /// 不能因为读了构建时捕获的旧状态而卡住。
    /// </summary>
    [TestMethod]
    public void GutterContextMenu_ClickingSameItemTwice_TogglesOptionBackAndForth()
    {
        OnUi(() =>
        {
            (VelaTerminalControl control, ContextMenu menu, Window window) = ShowGutterMenu(new() { ShowLineNumber = true });
            try
            {
                var item = (MenuItem)menu.Items[0]!;

                ClickMenuItem(item);
                Assert.IsFalse(control.ShowLineNumber, "第一次点击应关闭行号。");

                ClickMenuItem(item);
                Assert.IsTrue(control.ShowLineNumber, "菜单不关闭,再点同一项应重新打开行号。");
            }
            finally
            {
                menu.Close();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 把终端挂进窗口并弹出侧栏右键菜单,返回控件、已打开的菜单(菜单项此时才有模板与命中区)与宿主窗口 ——
    /// 调用方用完必须关掉菜单与窗口。
    /// </summary>
    private static (VelaTerminalControl Control, ContextMenu Menu, Window Window) ShowGutterMenu(VelaTerminalControl control)
    {
        var window = new Window { Width = 480, Height = 320, Content = control };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        ContextMenu menu = control.BuildGutterContextMenu();
        menu.Open(control);
        Dispatcher.UIThread.RunJobs();
        TopLevel.GetTopLevel((Control)menu.Items[0]!)?.UpdateLayout();
        return (control, menu, window);
    }

    /// <summary>
    /// 派发真实指针事件而非 RaiseEvent(ClickEvent):IsChecked 由 MenuItem 的输入链路在 Click 之前
    /// 翻转,合成事件绕过这一步,会让「点击后读 IsChecked」的接线看起来是坏的(实际不是)。
    /// </summary>
    private static void ClickMenuItem(MenuItem item)
    {
        var popup = (Window?)TopLevel.GetTopLevel(item);
        Assert.IsNotNull(popup, "菜单项应已在弹层里完成布局。");
        Assert.IsGreaterThan(0, item.Bounds.Width, "菜单项应有可命中的区域。");

        Point center = item.TranslatePoint(new(item.Bounds.Width / 2, item.Bounds.Height / 2), popup)!.Value;
        popup.MouseDown(center, MouseButton.Left);
        popup.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [TestMethod]
    public void RealPointerClick_OutsideFoldColumn_DoesNotFold()
    {
        OnUi(() =>
        {
            var control = new VelaTerminalControl { ShowFoldMarker = true };
            control.Feed(Encoding.UTF8.GetBytes("L0\r\nL1\r\nL2\r\nL3"));
            var window = new Window { Width = 480, Height = 320, Content = control };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                window.CaptureRenderedFrame();

                GutterLayout gutter = control.GutterForTest;
                // 点正文区域(侧栏右侧),不应折叠。
                var point = new Point(gutter.TotalWidth + 40, 2 * control.CellHeightForTest + 2);
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();

                Assert.AreEqual(0, control.FoldCountForTest, "正文区域点击不应折叠。");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// #586 回归:从左往右拖选整行时,指针常常落在折叠列与正文之间的空白里。
    /// 那段空白以前也算折叠命中,一按就把上方输出整段折掉。
    /// </summary>
    [TestMethod]
    public void RealPointerClick_InBlankGap_DoesNotFold()
    {
        OnUi(() =>
        {
            var control = new VelaTerminalControl { ShowFoldMarker = true, ShowFoldGuideLine = true, GutterBlank = true };
            control.Feed(Encoding.UTF8.GetBytes("L0\r\nL1\r\nL2\r\nL3\r\nL4\r\nL5"));
            var window = new Window { Width = 480, Height = 320, Content = control };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                window.CaptureRenderedFrame();

                GutterLayout gutter = control.GutterForTest;
                var point = new Point(gutter.FoldLeft + gutter.FoldWidth + GutterLayout.BlankPixels / 2, 3 * control.CellHeightForTest + 2);
                Assert.IsTrue(gutter.ContainsX(point.X), "点在侧栏的空白里。");
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();

                Assert.AreEqual(0, control.FoldCountForTest, "空白不是折叠列,点它不应折叠。");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>#586:只开分隔线、不开折叠时,在那一列上怎么点都不折叠。</summary>
    [TestMethod]
    public void RealPointerClick_GuideLineOnly_DoesNotFold()
    {
        OnUi(() =>
        {
            var control = new VelaTerminalControl { ShowFoldGuideLine = true };
            control.Feed(Encoding.UTF8.GetBytes("L0\r\nL1\r\nL2\r\nL3\r\nL4\r\nL5"));
            var window = new Window { Width = 480, Height = 320, Content = control };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                window.CaptureRenderedFrame();

                GutterLayout gutter = control.GutterForTest;
                Assert.IsGreaterThan(0, gutter.FoldWidth, "只开分隔线,这一列照样占位。");
                var point = new Point(gutter.FoldLeft + gutter.FoldWidth / 2, 3 * control.CellHeightForTest + 2);
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();

                Assert.AreEqual(0, control.FoldCountForTest, "没开折叠,点分隔线不应折叠。");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 关掉折叠后折叠头不再画、也点不动;分隔线开着时侧栏宽度不变,不会经由重排顺手清掉折叠 ——
    /// 必须由开关自己展开,否则被折起来的那段输出再也找不回来。
    /// </summary>
    [TestMethod]
    public void TurningFoldOff_WithGuideLineOn_ExpandsExistingFolds()
    {
        OnUi(() =>
        {
            var control = new VelaTerminalControl { ShowFoldMarker = true, ShowFoldGuideLine = true };
            control.Feed(Encoding.UTF8.GetBytes("L0\r\nL1\r\nL2\r\nL3\r\nL4\r\nL5"));
            var window = new Window { Width = 480, Height = 320, Content = control };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                window.CaptureRenderedFrame();

                GutterLayout gutter = control.GutterForTest;
                var point = new Point(gutter.FoldLeft + 2, 3 * control.CellHeightForTest + 2);
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                Assert.AreEqual(1, control.FoldCountForTest, "前提:先折出一个区域。");

                double widthBefore = control.GutterForTest.TotalWidth;
                control.ShowFoldMarker = false;
                Dispatcher.UIThread.RunJobs();

                Assert.AreEqual(widthBefore, control.GutterForTest.TotalWidth, "分隔线开着,关折叠不改侧栏宽度。");
                Assert.AreEqual(0, control.FoldCountForTest, "关掉折叠应展开已有的折叠。");
            }
            finally
            {
                window.Close();
            }
        });
    }
}
