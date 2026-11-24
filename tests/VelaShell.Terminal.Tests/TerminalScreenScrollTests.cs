using VelaShell.Terminal.Emulation;

namespace VelaShell.Terminal.Tests;

[TestClass]
[TestCategory("Scrollback")]
public sealed class TerminalScreenScrollTests
{
    [TestMethod]
    public void ScrollUp_BulkMove_PreservesRowsAndRetiresOriginalIdentities()
    {
        var screen = new TerminalScreen(columns: 8, rows: 5, maxScrollback: 10);
        TerminalCell blank = TerminalCell.Empty;
        TerminalRow[] original = FillRows(screen);

        screen.ScrollUp(3, blank);

        Assert.AreEqual(3, screen.ScrollbackCount);
        Assert.AreSame(original[0], screen.ViewLine(0));
        Assert.AreSame(original[1], screen.ViewLine(1));
        Assert.AreSame(original[2], screen.ViewLine(2));
        Assert.AreSame(original[3], screen.ActiveLine(0));
        Assert.AreSame(original[4], screen.ActiveLine(1));
        Assert.AreNotSame(original[0], screen.ActiveLine(2));
        Assert.AreNotSame(original[1], screen.ActiveLine(3));
        Assert.AreNotSame(original[2], screen.ActiveLine(4));
        Assert.AreEqual(0, screen.ActiveLine(2)[0].Rune);
    }

    [TestMethod]
    public void ScrollUp_AtPartialMargin_PreservesRowsOutsideRegion()
    {
        var screen = new TerminalScreen(columns: 8, rows: 5, maxScrollback: 10);
        TerminalCell blank = TerminalCell.Empty;
        TerminalRow[] original = FillRows(screen);
        screen.SetMargins(1, 3);

        screen.ScrollUp(2, blank);

        Assert.AreSame(original[0], screen.ActiveLine(0));
        Assert.AreSame(original[3], screen.ActiveLine(1));
        Assert.AreSame(original[4], screen.ActiveLine(4));
        Assert.AreEqual(0, screen.ScrollbackCount);
        Assert.AreNotSame(original[1], screen.ActiveLine(2));
        Assert.AreNotSame(original[2], screen.ActiveLine(3));
    }

    [TestMethod]
    public void ScrollDown_BulkMove_PreservesRowsOutsideAndBelowRegion()
    {
        var screen = new TerminalScreen(columns: 8, rows: 5, maxScrollback: 10);
        TerminalCell blank = TerminalCell.Empty;
        TerminalRow[] original = FillRows(screen);
        screen.SetMargins(1, 4);

        screen.ScrollDown(2, blank);

        Assert.AreSame(original[0], screen.ActiveLine(0));
        Assert.AreNotSame(original[1], screen.ActiveLine(1));
        Assert.AreNotSame(original[2], screen.ActiveLine(2));
        Assert.AreSame(original[1], screen.ActiveLine(3));
        Assert.AreSame(original[2], screen.ActiveLine(4));
        Assert.AreEqual(0, screen.ActiveLine(1)[0].Rune);
        Assert.AreEqual(0, screen.ActiveLine(2)[0].Rune);
    }

    [TestMethod]
    public void InsertAndDeleteLines_BulkMove_PreserveSurvivingRowIdentities()
    {
        var screen = new TerminalScreen(columns: 8, rows: 6, maxScrollback: 10);
        TerminalCell blank = TerminalCell.Empty;
        TerminalRow[] original = FillRows(screen);
        screen.SetCursor(0, 2);
        screen.SetMargins(1, 4);

        screen.InsertLines(2, blank);

        Assert.AreSame(original[0], screen.ActiveLine(0));
        Assert.AreSame(original[1], screen.ActiveLine(1));
        Assert.AreNotSame(original[2], screen.ActiveLine(2));
        Assert.AreNotSame(original[3], screen.ActiveLine(3));
        Assert.AreSame(original[2], screen.ActiveLine(4));
        Assert.AreSame(original[5], screen.ActiveLine(5));

        screen.SetCursor(0, 2);
        screen.DeleteLines(2, blank);

        Assert.AreSame(original[2], screen.ActiveLine(2));
        Assert.AreNotSame(original[2], screen.ActiveLine(3));
        Assert.AreNotSame(original[2], screen.ActiveLine(4));
        Assert.AreSame(original[5], screen.ActiveLine(5));
    }

    private static TerminalRow[] FillRows(TerminalScreen screen)
    {
        var rows = new TerminalRow[screen.Rows];
        for (int row = 0; row < screen.Rows; row++)
        {
            rows[row] = screen.ActiveLine(row);
            screen.SetCell(0, row, new TerminalCell { Rune = 'A' + row });
        }
        return rows;
    }
}
