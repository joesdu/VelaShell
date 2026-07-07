using System.Text;
using PulseTerm.Terminal.Emulation;

namespace PulseTerm.Terminal.Tests;

/// <summary>
/// Regression tests for the tab-drag data-loss bug (用户反馈): a transient column shrink
/// (the shared terminal control being laid out tiny inside the drag preview) must not
/// destroy row content — growing back restores the text. Also covers the selection copy
/// crash from out-of-range absolute rows.
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
    public void ShrinkThenGrowColumns_RestoresLineContent()
    {
        var e = New(cols: 80, rows: 4);
        Feed(e, "Linux NanoPi-R2S 6.1.63 aarch64\r\npi@NanoPi-R2S:~$");

        e.Resize(7, 4);
        Assert.AreEqual("Linux N", Line(e, 0)); // visibly truncated while narrow

        e.Resize(80, 4);
        Assert.AreEqual("Linux NanoPi-R2S 6.1.63 aarch64", Line(e, 0));
        Assert.AreEqual("pi@NanoPi-R2S:~$", Line(e, 1));
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
    public void GrowBeyondOriginalWidth_AddsBlankCells()
    {
        var e = New(cols: 10, rows: 2);
        Feed(e, "abc");

        e.Resize(30, 2);
        Assert.AreEqual("abc", Line(e, 0));
        Assert.AreEqual(30, e.Screen.ActiveLine(0).Columns);
    }

    [TestMethod]
    public void ClearedLine_DoesNotResurrectHiddenTailOnGrow()
    {
        var e = New(cols: 40, rows: 2);
        Feed(e, "confidential-old-content");

        e.Resize(5, 2);
        // Program clears the (narrow) screen: ESC[2J — the hidden tail must die with it.
        Feed(e, "\u001b[2J");
        e.Resize(40, 2);

        Assert.AreEqual(string.Empty, Line(e, 0));
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
