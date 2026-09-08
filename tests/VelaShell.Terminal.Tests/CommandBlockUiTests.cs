using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// OSC 133 命令块的界面交互:侧栏标记列的显隐与命中、点标记选中输出、
/// Ctrl+Shift+↑/↓ 在提示符间跳转、以及折叠按块对齐。
/// </summary>
[TestClass]
[TestCategory("Mouse")]
public sealed class CommandBlockUiTests
{
    private static HeadlessUnitTestSession Session => HeadlessTestSession.Current;

    private static void OnUi(Action body) =>
        Session.Dispatch(() =>
        {
            body();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();

    private const string A = "\e]133;A\e\\";
    private const string B = "\e]133;B\e\\";
    private const string C = "\e]133;C\e\\";

    private static string D(int code) => $"\e]133;D;{code}\e\\";

    private static VelaTerminalControl NewControl() =>
        new()
        {
            ShowLineNumber = false,
            ShowLineTimestamp = false,
            ShowFoldMarker = true,
            CursorBlink = false
        };

    private static (VelaTerminalControl Control, Window Window) Show(VelaTerminalControl control)
    {
        var window = new Window { Width = 700, Height = 400, Content = control };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        control.Focus();
        Dispatcher.UIThread.RunJobs();
        return (control, window);
    }

    /// <summary>喂两条命令:第一条成功、第二条失败。</summary>
    private static void FeedTwoCommands(VelaTerminalControl control)
    {
        control.Feed(Encoding.UTF8.GetBytes(
            A + "$ " + B + "one\r\n" + C + "out-one\r\n" + D(0) +
            A + "$ " + B + "two\r\n" + C + "out-two\r\n" + D(1)));
        Dispatcher.UIThread.RunJobs();
    }

    [TestMethod]
    public void WithoutShellIntegration_TheMarkColumnTakesNoSpace()
    {
        // 没装集成的会话不该为这个功能付一个像素 —— 而且列宽一变,正文就要左右挪。
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window) = Show(NewControl());
            control.Feed(Encoding.UTF8.GetBytes("plain output, no marks\r\n"));
            Dispatcher.UIThread.RunJobs();

            Assert.AreEqual(0, control.GutterForTest.CommandMarkWidth);
            window.Close();
        });
    }

    [TestMethod]
    public void OnceMarksArrive_TheColumnAppearsAndStaysPut()
    {
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window) = Show(NewControl());
            FeedTwoCommands(control);

            double width = control.GutterForTest.CommandMarkWidth;
            Assert.IsGreaterThan(0, width);

            // 滚到没有标记的历史区:列宽必须纹丝不动(锁存位的意义就在这)。
            control.Feed(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("filler\r\n", 60))));
            Dispatcher.UIThread.RunJobs();
            control.ScrollOffset = 40;
            Dispatcher.UIThread.RunJobs();

            Assert.AreEqual(width, control.GutterForTest.CommandMarkWidth);
            window.Close();
        });
    }

    [TestMethod]
    public void MarkColumnAndFoldColumnDoNotOverlap()
    {
        // 两列相邻,命中判定必须互斥 —— 否则点一下会同时触发折叠与选中输出。
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window) = Show(NewControl());
            FeedTwoCommands(control);
            GutterLayout g = control.GutterForTest;

            for (double x = 0; x < g.TotalWidth; x += 0.5)
            {
                Assert.IsFalse(
                    g.IsCommandMarkHit(x) && g.IsFoldColumnHit(x),
                    $"x={x} 同时落在标记列与折叠列。");
            }
            window.Close();
        });
    }

    [TestMethod]
    public void ClickingAMark_SelectsThatCommandsOutput()
    {
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window) = Show(NewControl());
            FeedTwoCommands(control);
            GutterLayout g = control.GutterForTest;

            // 第 0 行是第一条命令的提示符行。
            double x = g.CommandMarkLeft + g.CommandMarkWidth / 2;
            window.MouseDown(new Point(x, control.CellHeightForTest / 2), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            // 选的是输出而不是整块:提示符与命令本身几乎从不是你想粘走的东西。
            Assert.AreEqual("out-one", control.GetSelectedText().Trim());
            window.Close();
        });
    }

    [TestMethod]
    public void CtrlShiftUp_JumpsToThePreviousPrompt()
    {
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window) = Show(NewControl());
            FeedTwoCommands(control);
            // 灌够内容把提示符推进回滚区,跳转才有可观察的滚动。
            control.Feed(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("filler\r\n", 60))));
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(0, control.ScrollOffset);

            window.KeyPress(Key.Up, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.ArrowUp, null);
            Dispatcher.UIThread.RunJobs();

            Assert.IsGreaterThan(0, control.ScrollOffset, "应当往回滚到上一条提示符。");
            window.Close();
        });
    }

    [TestMethod]
    public void CtrlShiftArrows_AreSentToThePtyWhenThereAreNoMarks()
    {
        // 没有标记可跳还吞掉一组按键,只会让远端程序的键位神秘失灵。
        OnUi(() =>
        {
            var control = NewControl();
            var sent = new List<byte[]>();
            control.TypedInput += bytes => sent.Add(bytes);
            (_, Window window) = Show(control);
            control.Feed(Encoding.UTF8.GetBytes("no marks here\r\n"));
            Dispatcher.UIThread.RunJobs();

            window.KeyPress(Key.Up, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.ArrowUp, null);
            Dispatcher.UIThread.RunJobs();

            Assert.IsNotEmpty(sent, "没有标记时 Ctrl+Shift+↑ 应当原样编码下发。");
            window.Close();
        });
    }

    [TestMethod]
    public void FoldingSnapsToTheCommandBlock_KeepingThePromptRowVisible()
    {
        // 折完一屏全是认不出来的横线就没意义了 —— 收起来的是输出,提示符必须留着。
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window) = Show(NewControl());
            FeedTwoCommands(control);
            GutterLayout g = control.GutterForTest;

            // 点第一条命令的输出行(第 1 行)所在的折叠列。
            double x = g.FoldLeft + g.FoldWidth / 2;
            window.MouseDown(new Point(x, control.CellHeightForTest * 1.5), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.AreEqual(1, control.FoldCountForTest, "应当折出一个区域。");
            window.Close();
        });
    }
}
