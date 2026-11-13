using System.Text;
using VelaShell.Terminal.Emulation;

namespace VelaShell.Terminal.Tests.Emulation;

/// <summary>
/// REP(CSI Ps b):重复最近一个图形字符。ncurses 在 terminfo 带 <c>rep</c> 时(xterm-256color 就带)
/// 用它压缩横线与整段空白;不认的话 menuconfig 的框线只剩一格、其后内容整体左移(#749)。
/// </summary>
[TestClass]
[TestCategory("Emulator")]
public class RepeatCharacterTests
{
    private static TerminalEmulator New(int cols = 20, int rows = 4) => new(cols, rows);

    private static void Feed(TerminalEmulator e, string s) => e.Feed(Encoding.UTF8.GetBytes(s));

    private static string Line(TerminalEmulator e, int row) => e.Screen.ActiveLine(row).GetText();

    [TestMethod]
    public void Rep_RepeatsLastAsciiCharacter_AndAdvancesCursor()
    {
        TerminalEmulator e = New();
        Feed(e, "ab\x1b[3bX");
        Assert.AreEqual("abbbbX", Line(e, 0));
        Assert.AreEqual(6, e.CursorX);
    }

    [TestMethod]
    public void Rep_DefaultCountIsOne()
    {
        TerminalEmulator e = New();
        Feed(e, "a\x1b[b\x1b[0b");
        Assert.AreEqual("aaa", Line(e, 0));
    }

    [TestMethod]
    public void Rep_RepeatsDecLineDrawingGlyph_AsMenuconfigDrawsIt()
    {
        // menuconfig 画对话框顶边的真实字节形状:ACS 进图形集,一个 q 加 REP,再出图形集写后续文字。
        TerminalEmulator e = New();
        Feed(e, "\x1b(0lq\x1b[4bk\x1b(BZ");
        Assert.AreEqual("┌─────┐Z", Line(e, 0));
    }

    [TestMethod]
    public void Rep_AfterLeavingGraphicsSet_StillRepeatsTranslatedGlyph()
    {
        TerminalEmulator e = New();
        Feed(e, "\x1b(0q\x1b(B\x1b[2b");
        Assert.AreEqual("───", Line(e, 0));
    }

    [TestMethod]
    public void Rep_RepeatsWideCharacter()
    {
        TerminalEmulator e = New();
        Feed(e, "中\x1b[2b");
        Assert.AreEqual(6, e.CursorX);
        Assert.AreEqual("中中中", Line(e, 0));
    }

    [TestMethod]
    public void Rep_WrapsAtRightMarginLikeOrdinaryPrinting()
    {
        TerminalEmulator e = New(cols: 4);
        Feed(e, "a\x1b[5b");
        Assert.AreEqual("aaaa", Line(e, 0));
        Assert.AreEqual("aa", Line(e, 1));
    }

    [TestMethod]
    public void Rep_UsesCurrentPen()
    {
        TerminalEmulator e = New();
        Feed(e, "\x1b[41m \x1b[3b");
        TerminalCell last = e.Screen.ActiveLine(0)[3];
        Assert.AreEqual(e.Screen.ActiveLine(0)[0].Background, last.Background);
        Assert.AreEqual(4, e.CursorX);
    }

    [TestMethod]
    public void Rep_WithoutPrecedingGraphic_IsIgnored()
    {
        TerminalEmulator e = New();
        Feed(e, "\x1b[5bX");
        Assert.AreEqual("X", Line(e, 0));
        Assert.AreEqual(1, e.CursorX);
    }

    [TestMethod]
    public void Rep_AfterFullReset_IsIgnored()
    {
        TerminalEmulator e = New();
        Feed(e, "a\u001bc\x1b[3bX"); // RIS;不能写 \x1bc —— C# 会贪吃成 U+01BC
        Assert.AreEqual("X", Line(e, 0));
    }

    [TestMethod]
    public void Rep_DoesNotRepeatCombiningMark()
    {
        TerminalEmulator e = New();
        Feed(e, "é\x1b[1b");
        Assert.AreEqual(2, e.CursorX);
    }
}
