using System.Text;
using PulseTerm.Terminal.Emulation;

namespace PulseTerm.Terminal.Tests;

/// <summary>
/// Reflow-on-resize tests (mainstream behavior — Windows Terminal / iTerm2 / VTE / kitty):
/// primary-screen column changes rejoin soft-wrapped rows into logical lines and re-wrap
/// them at the new width, so narrowing (including the transient tab-drag squeeze) never
/// destroys content; the alternate screen is hard-resized because full-screen programs
/// repaint themselves on SIGWINCH. Also covers the selection-copy crash from out-of-range
/// absolute rows.
/// </summary>
[TestClass]
[TestCategory("Emulator")]
public class ResizePreservationTests
{
    private static TerminalEmulator New(int cols = 80, int rows = 6)
        => new(cols, rows, TerminalType.XtermusColor256);

    private static void Feed(TerminalEmulator e, string s) => e.Feed(Encoding.UTF8.GetBytes(s));

    private static string Line(TerminalEmulator e, int row) => e.Screen.ActiveLine(row).GetText();

    [TestMethod]
    public void NarrowResize_ReflowsInsteadOfTruncating()
    {
        var e = New(cols: 80, rows: 4);
        Feed(e, "Linux NanoPi-R2S 6.1.63 aarch64\r\npi@NanoPi-R2S:~$");

        e.Resize(7, 4);

        // The long line is re-wrapped across rows (starting at the top of the buffer),
        // not cut down to its first 7 characters.
        Assert.AreEqual(7, e.Screen.Columns);
        Assert.AreEqual("Linux N", e.Screen.ViewLine(0).GetText());
        Assert.AreEqual("anoPi-R", e.Screen.ViewLine(1).GetText());
        Assert.IsTrue(e.Screen.ViewLine(0).Wrapped);
    }

    [TestMethod]
    public void ShrinkThenGrowColumns_RestoresLinesAndCursor()
    {
        var e = New(cols: 80, rows: 4);
        Feed(e, "Linux NanoPi-R2S 6.1.63 aarch64\r\npi@NanoPi-R2S:~$");

        e.Resize(7, 4);
        e.Resize(80, 4);

        Assert.AreEqual("Linux NanoPi-R2S 6.1.63 aarch64", Line(e, 0));
        Assert.AreEqual("pi@NanoPi-R2S:~$", Line(e, 1));
        // The cursor followed its character through both reflows.
        Assert.AreEqual(16, e.CursorX);
        Assert.AreEqual(1, e.CursorY);
    }

    [TestMethod]
    public void Reflow_KeepsWideCharactersAtomic()
    {
        var e = New(cols: 80, rows: 4);
        Feed(e, "abcde中文");

        e.Resize(6, 4);

        // 中 needs two cells and doesn't fit after "abcde" in a 6-column row, so it wraps
        // whole instead of being split across the boundary.
        Assert.AreEqual("abcde", e.Screen.ViewLine(0).GetText());
        Assert.AreEqual("中文", e.Screen.ViewLine(1).GetText());
    }

    [TestMethod]
    public void Reflow_RewrapsPreviouslyAutowrappedLines()
    {
        var e = New(cols: 10, rows: 4);
        Feed(e, "0123456789ABCDEF"); // autowraps at column 10 into two physical rows

        e.Resize(16, 4);

        // Widening rejoins the soft-wrapped pair into one 16-character line.
        Assert.AreEqual("0123456789ABCDEF", Line(e, 0));
        Assert.AreEqual("", Line(e, 1));
    }

    [TestMethod]
    public void ShrinkThenGrowRows_RestoresLinesFromScrollback()
    {
        var e = New(cols: 20, rows: 6);
        Feed(e, "one\r\ntwo\r\nthree\r\nfour\r\nfive\r\nsix");

        e.Resize(20, 2);
        e.Resize(20, 6);

        var all = new List<string>();
        for (int r = 0; r < e.Screen.TotalRows; r++)
            all.Add(e.Screen.ViewLine(r).GetText());

        CollectionAssert.Contains(all, "one");
        CollectionAssert.Contains(all, "six");
    }

    [TestMethod]
    public void GrowBeyondOriginalWidth_KeepsContent()
    {
        var e = New(cols: 10, rows: 2);
        Feed(e, "abc");

        e.Resize(30, 2);

        Assert.AreEqual("abc", Line(e, 0));
        Assert.AreEqual(30, e.Screen.ActiveLine(0).Columns);
    }

    [TestMethod]
    public void AltScreen_IsHardResized_NotReflowed()
    {
        var e = New(cols: 20, rows: 4);
        Feed(e, "\u001b[?1049h");   // enter the alternate screen (htop/vim territory)
        Feed(e, "PANEL VIEW");

        e.Resize(8, 4);

        // Full-screen apps repaint on SIGWINCH; the alt screen just truncates to the new
        // grid instead of spilling wrapped fragments into a scrollback it doesn't have.
        Assert.AreEqual(8, e.Screen.Columns);
        Assert.AreEqual("PANEL VI", Line(e, 0));
    }

    [TestMethod]
    public void ViewLine_OutOfRangeRows_ClampInsteadOfThrow()
    {
        var e = New(cols: 10, rows: 3);
        Feed(e, "top\r\nmid\r\nbot");

        // Negative (pointer dragged above the control) and beyond-total rows must not throw.
        Assert.AreEqual("top", e.Screen.ViewLine(-5).GetText());
        Assert.AreEqual("bot", e.Screen.ViewLine(e.Screen.TotalRows + 10).GetText());
    }
}
