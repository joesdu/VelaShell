using System.Text;
using System.Threading;
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
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) => _session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));

    [ClassCleanup]
    public static void Cleanup() => _session.Dispose();

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
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame(); // 强制一帧渲染 → 填充屏幕行映射

            Assert.IsTrue(control.CellHeightForTest > 0, "渲染后应有有效的单元格高度。");
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
        });
    }

    [TestMethod]
    public void GutterContextMenu_HasFourToggles_ReflectsState_AndItemTogglesOption()
    {
        // 直接构建菜单(不弹出弹层,避免 headless 下 popup 阻塞),验证内容 + 菜单项→属性开关的接线。
        OnUi(() =>
        {
            var control = new VelaTerminalControl { ShowLineNumber = true };
            ContextMenu menu = control.BuildGutterContextMenu();
            Assert.AreEqual(4, menu.Items.Count, "菜单应含 4 个部件开关(行号/时间戳/折叠标记/空白)。");

            var lineNumberItem = (MenuItem)menu.Items[0]!;
            var timestampItem = (MenuItem)menu.Items[1]!;
            StringAssert.StartsWith((string)lineNumberItem.Header!, "✔", "行号已开,菜单项应带勾号。");
            StringAssert.StartsWith((string)timestampItem.Header!, " ", "时间戳未开,菜单项应无勾号(空格前缀)。");

            // 触发「行号」项 → 关闭行号,证明菜单项确实驱动属性开关。
            lineNumberItem.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Assert.IsFalse(control.ShowLineNumber, "点击「行号」菜单项应关闭行号。");
        });
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
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();

            GutterLayout gutter = control.GutterForTest;
            // 点正文区域(侧栏右侧),不应折叠。
            var point = new Point(gutter.TotalWidth + 40, 2 * control.CellHeightForTest + 2);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.AreEqual(0, control.FoldCountForTest, "正文区域点击不应折叠。");
        });
    }
}

/// <summary>headless 测试用的最小 Avalonia 应用。</summary>
public class HeadlessTestApp : Application
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HeadlessTestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
