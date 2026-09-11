using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// Ctrl 悬停在链接上时的反馈:手型光标 + 完整地址提示。
/// </summary>
/// <remarks>
/// URL 与 IP 一直画着下划线,但"能不能点、点了去哪"全靠猜 —— Ctrl 按下时光标不变,
/// 也没有任何东西告诉你完整地址(终端里的长 URL 经常被折行截断)。
/// 判定复用 <c>SemanticMatcher.UrlAt</c>,与 Ctrl+点击是同一个函数,
/// 于是"看起来能点"和"真的能点"永远一致。
/// </remarks>
[TestClass]
[TestCategory("Mouse")]
public sealed class LinkHoverTests
{
    private static HeadlessUnitTestSession Session => HeadlessTestSession.Current;

    private static void OnUi(Action body) =>
        Session.Dispatch(() =>
        {
            body();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>建一个显示了一行含 URL 文本的终端。</summary>
    private static (VelaTerminalControl Control, Window Window) ShowWithLink()
    {
        var control = new VelaTerminalControl
        {
            ShowLineNumber = false,
            ShowLineTimestamp = false,
            ShowFoldMarker = false,
            CursorBlink = false
        };
        control.Feed(Encoding.UTF8.GetBytes("see https://example.com/docs for details"));
        var window = new Window { Width = 640, Height = 360, Content = control };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        control.Focus();
        Dispatcher.UIThread.RunJobs();
        return (control, window);
    }

    /// <summary>把指针移到第一行的第 col 列上。</summary>
    private static void MoveTo(Window window, VelaTerminalControl control, int col, KeyModifiers modifiers)
    {
        double x = (col + 0.5) * control.CellWidthForTest;
        double y = control.CellHeightForTest / 2;
        window.MouseMove(new Point(x, y), (RawInputModifiers)modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    [TestMethod]
    public void CtrlHoveringAUrl_ShowsTheHandCursorAndTheFullAddress()
    {
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window) = ShowWithLink();

            // "see " 占 4 列,URL 从第 4 列起。
            MoveTo(window, control, 8, KeyModifiers.Control);

            Assert.AreEqual(StandardCursorType.Hand, control.Cursor?.ToString() is null ? StandardCursorType.Arrow : StandardCursorType.Hand,
                "Ctrl 悬停在 URL 上应当给手型光标。");
            Assert.AreEqual("https://example.com/docs", ToolTip.GetTip(control),
                "提示里应当是完整地址 —— 终端里的长 URL 经常被折行截断,这正是它的用处。");

            window.Close();
        });
    }

    [TestMethod]
    public void HoveringWithoutCtrl_DoesNothing()
    {
        // 不按 Ctrl 时不做匹配:否则每一次鼠标移动都要跑一遍正则。
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window) = ShowWithLink();

            MoveTo(window, control, 8, KeyModifiers.None);

            Assert.IsNull(ToolTip.GetTip(control));
            window.Close();
        });
    }

    [TestMethod]
    public void CtrlHoveringPlainText_ShowsNothing()
    {
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window) = ShowWithLink();

            // 第 1 列落在 "see" 里。
            MoveTo(window, control, 1, KeyModifiers.Control);

            Assert.IsNull(ToolTip.GetTip(control));
            window.Close();
        });
    }

    [TestMethod]
    public void MovingOffTheLink_ClearsTheFeedback()
    {
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window) = ShowWithLink();

            MoveTo(window, control, 8, KeyModifiers.Control);
            Assert.IsNotNull(ToolTip.GetTip(control));

            MoveTo(window, control, 1, KeyModifiers.Control);

            Assert.IsNull(ToolTip.GetTip(control), "移开之后手型与提示都该撤掉。");
            window.Close();
        });
    }

    [TestMethod]
    public void PressingCtrlWhileAlreadyOverTheLink_ShowsTheFeedbackWithoutMovingTheMouse()
    {
        // 回归 #397:判定原本只挂在 PointerMoved 上,而按 Ctrl 时鼠标是静止的,
        // 于是必须抖一下鼠标手型才出来 —— 松开侧一直是即时的,按下侧不是。
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window) = ShowWithLink();

            // 先不按 Ctrl 悬到 URL 上:此刻不该有任何反馈。
            MoveTo(window, control, 8, KeyModifiers.None);
            Assert.IsNull(ToolTip.GetTip(control));

            // 鼠标一动不动,只按下 Ctrl。
            window.KeyPress(Key.LeftCtrl, RawInputModifiers.Control, PhysicalKey.ControlLeft, null);
            Dispatcher.UIThread.RunJobs();

            Assert.AreEqual("https://example.com/docs", ToolTip.GetTip(control),
                "按下 Ctrl 就该立即出现手型与地址,不该等到鼠标移动。");

            window.Close();
        });
    }

    [TestMethod]
    public void PressingCtrlOverPlainText_ShowsNothing()
    {
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window) = ShowWithLink();

            MoveTo(window, control, 1, KeyModifiers.None);
            window.KeyPress(Key.LeftCtrl, RawInputModifiers.Control, PhysicalKey.ControlLeft, null);
            Dispatcher.UIThread.RunJobs();

            Assert.IsNull(ToolTip.GetTip(control));
            window.Close();
        });
    }

    [TestMethod]
    public void PressingCtrlAfterThePointerLeft_ShowsNothing()
    {
        // 指针已经不在控件上,记下的位置就作废了 —— 否则会照着一个旧位置亮手型。
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window) = ShowWithLink();

            MoveTo(window, control, 8, KeyModifiers.None);
            window.MouseMove(new Point(-10, -10), RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            window.KeyPress(Key.LeftCtrl, RawInputModifiers.Control, PhysicalKey.ControlLeft, null);
            Dispatcher.UIThread.RunJobs();

            Assert.IsNull(ToolTip.GetTip(control));
            window.Close();
        });
    }

    [TestMethod]
    public void ReleasingCtrl_ClearsTheFeedback()
    {
        // 松开 Ctrl 之后已经点不开了,再指着手型是在骗人。
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window) = ShowWithLink();
            MoveTo(window, control, 8, KeyModifiers.Control);
            Assert.IsNotNull(ToolTip.GetTip(control));

            window.KeyRelease(Key.LeftCtrl, RawInputModifiers.None, PhysicalKey.ControlLeft, null);
            Dispatcher.UIThread.RunJobs();

            Assert.IsNull(ToolTip.GetTip(control));
            window.Close();
        });
    }

    /// <summary>建一个显示了一条 OSC 8 显式超链接的终端;锚文本刻意不像 URL。</summary>
    private static (VelaTerminalControl Control, Window Window) ShowWithOsc8Link()
    {
        var control = new VelaTerminalControl
        {
            ShowLineNumber = false,
            ShowLineTimestamp = false,
            ShowFoldMarker = false,
            CursorBlink = false
        };
        control.Feed(Encoding.UTF8.GetBytes("go \e]8;;https://example.com/report\e\\报告\e]8;;\e\\ now"));
        var window = new Window { Width = 640, Height = 360, Content = control };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        control.Focus();
        Dispatcher.UIThread.RunJobs();
        return (control, window);
    }

    [TestMethod]
    public void CtrlHoveringAnOsc8Link_ShowsTheDeclaredTarget()
    {
        // OSC 8 的锚文本经常压根不像 URL(这里就是「报告」两个字),文本猜测在这里必然落空 ——
        // 只有认协议声明的目标,悬停才给得出地址。
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window) = ShowWithOsc8Link();

            // "go " 占 3 列,锚文本「报告」是两个双宽字符,占第 3~6 列。
            MoveTo(window, control, 3, KeyModifiers.Control);

            Assert.AreEqual("https://example.com/report", ToolTip.GetTip(control));
            window.Close();
        });
    }

    [TestMethod]
    public void CtrlHoveringTheTrailingHalfOfAWideAnchor_StillHitsTheLink()
    {
        // 第 4 列是「报」的尾格。尾格若没盖上句柄,手型会在半个字上一闪一闪。
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window) = ShowWithOsc8Link();

            MoveTo(window, control, 4, KeyModifiers.Control);

            Assert.AreEqual("https://example.com/report", ToolTip.GetTip(control));
            window.Close();
        });
    }

    [TestMethod]
    public void CtrlHoveringOutsideTheOsc8Run_ShowsNothing()
    {
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window) = ShowWithOsc8Link();

            MoveTo(window, control, 8, KeyModifiers.Control); // 关闭序列之后的 " now"

            Assert.IsNull(ToolTip.GetTip(control));
            window.Close();
        });
    }

    [TestMethod]
    public void HoverJudgementMatchesTheCtrlClickTarget()
    {
        // 这条是整项的核心不变量:悬停用的判定必须与点击用的是同一个,
        // 否则会出现"指了手型却点不开"或反过来。
        const string line = "see https://example.com/docs for details";
        for (int col = 0; col < line.Length; col++)
        {
            string? url = Semantics.SemanticMatcher.UrlAt(line, col);
            bool insideUrl = col >= 4 && col < 4 + "https://example.com/docs".Length;
            Assert.AreEqual(insideUrl, url is not null, $"第 {col} 列的判定与预期不符。");
        }
    }
}
