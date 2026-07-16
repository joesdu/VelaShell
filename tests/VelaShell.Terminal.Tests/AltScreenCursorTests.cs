using System.Text;
using VelaShell.Terminal.Emulation;

namespace VelaShell.Terminal.Tests;

[TestClass]
[TestCategory("Emulation")]
public class AltScreenCursorTests
{
    // ESC as its own literal is safe; never inline "\x1b7" — the \x escape greedily eats the 7.
    private const string ESC = "\x1b";

    private static void Feed(TerminalEmulator e, string s) => e.Feed(Encoding.ASCII.GetBytes(s));

    /// <summary>
    /// 复刻并验证 ZMODEM 会话结束时的终端自愈:若杂散协议字节把终端切到备用屏(DECSET 1049),
    /// 主屏内容会"整屏消失"。SshTerminalBridge 在每次 ZMODEM 会话收尾补发的 DECRST 1049
    /// (即本测试的 <c>ESC[?1049l</c>)必须切回主屏并让原内容重新可见。
    /// </summary>
    [TestMethod]
    public void Exit1049_RecoversMainScreenContent_AfterStrayAltSwitch()
    {
        var e = new TerminalEmulator(80, 24);

        // 主屏有内容(用户的 shell 输出)。
        Feed(e, "hello world");
        Assert.IsFalse(e.IsAlternateScreen);

        // 杂散字节把终端切到空白备用屏——主屏内容被挡住,表现为"整屏消失"。
        Feed(e, ESC + "[?1049h");
        Assert.IsTrue(e.IsAlternateScreen);

        // 桥在 ZMODEM 会话收尾补发的自愈序列:切回主屏。
        Feed(e, ESC + "[?1049l");
        Assert.IsFalse(e.IsAlternateScreen, "1049l 应切回主屏");

        // 主屏原内容仍在(备用屏只是遮挡,不销毁主屏缓冲)。
        Assert.AreEqual("hello world", e.Screen.ActiveLine(0).GetText().TrimEnd());
    }

    /// <summary>本就在主屏时补发 DECRST 1049 应为无副作用的空操作(模拟器短路返回)。</summary>
    [TestMethod]
    public void Exit1049_OnMainScreen_IsHarmlessNoOp()
    {
        var e = new TerminalEmulator(80, 24);
        Feed(e, "sample text");
        Assert.IsFalse(e.IsAlternateScreen);
        int cx = e.CursorX, cy = e.CursorY;

        Feed(e, ESC + "[?1049l"); // 桥在正常会话结束时也会补发——不该有任何影响。

        Assert.IsFalse(e.IsAlternateScreen);
        Assert.AreEqual(cx, e.CursorX);
        Assert.AreEqual(cy, e.CursorY);
        Assert.AreEqual("sample text", e.Screen.ActiveLine(0).GetText().TrimEnd());
    }

    [TestMethod]
    public void Exit1049_RestoresMainCursor_EvenWhenAppUsedDecscInAltScreen()
    {
        var e = new TerminalEmulator(80, 24);

        // Main-screen cursor at (col 5, row 3) — where "nano file" was launched.
        Feed(e, ESC + "[4;6H");
        Assert.AreEqual(5, e.CursorX);
        Assert.AreEqual(3, e.CursorY);

        // Enter alt screen (DECSET 1049 saves the main cursor).
        Feed(e, ESC + "[?1049h");
        Assert.IsTrue(e.IsAlternateScreen);

        // The app moves around and uses DECSC inside the alt screen — this must NOT clobber the
        // main cursor saved by 1049. Without the fix, exit restores this (10,10) onto the main
        // screen, landing the cursor "over old content".
        Feed(e, ESC + "[11;11H");
        Feed(e, ESC + "7");        // DECSC in alt screen
        Feed(e, ESC + "[1;1H");

        // Exit alt screen (DECRST 1049) — cursor returns to the MAIN position, not the alt one.
        Feed(e, ESC + "[?1049l");
        Assert.IsFalse(e.IsAlternateScreen);
        Assert.AreEqual(5, e.CursorX);
        Assert.AreEqual(3, e.CursorY);
    }

    [TestMethod]
    public void DecscDecrc_StillWorkIndependentlyOfAltScreen()
    {
        var e = new TerminalEmulator(80, 24);

        Feed(e, ESC + "[2;3H"); // (col 2, row 1)
        Feed(e, ESC + "7");     // DECSC saves (2, 1)
        Feed(e, ESC + "[10;10H");
        Feed(e, ESC + "8");     // DECRC restores (2, 1)

        Assert.AreEqual(2, e.CursorX);
        Assert.AreEqual(1, e.CursorY);
    }
}
