using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.Tests;

/// <summary>侧栏几何与折叠列命中判定:决定「鼠标点在哪算点到折叠列」,是折叠交互的坐标基础。</summary>
[TestClass]
[TestCategory("GutterLayout")]
public class GutterLayoutTests
{
    private const double CellW = 8;

    [TestMethod]
    public void AllOff_IsDisabled_ZeroWidth()
    {
        var g = new GutterLayout(CellW, false, false, false, false);
        Assert.IsFalse(g.Enabled);
        Assert.AreEqual(0, g.TotalWidth);
        Assert.IsFalse(g.ContainsX(0));
        Assert.IsFalse(g.IsFoldColumnHit(0));
    }

    [TestMethod]
    public void Widths_AddUp_LeftToRight()
    {
        var g = new GutterLayout(CellW, showTimestamp: true, showNumber: true, showFold: true, blank: true);
        Assert.AreEqual(11 * CellW, g.TimeWidth);
        Assert.AreEqual((GutterLayout.NumberDigits + 1) * CellW, g.NumberWidth);
        Assert.AreEqual(Math.Ceiling(CellW * 1.6), g.FoldWidth);
        Assert.AreEqual(GutterLayout.BlankPixels, g.BlankWidth);
        Assert.AreEqual(g.TimeWidth, g.NumberLeft);
        Assert.AreEqual(g.TimeWidth + g.NumberWidth, g.FoldLeft);
        Assert.AreEqual(g.TimeWidth + g.NumberWidth + g.FoldWidth + g.BlankWidth, g.TotalWidth);
    }

    [TestMethod]
    public void FoldColumnHit_OnlyWithinFoldPlusBlank()
    {
        // 时间+行号+折叠+空白都开:折叠列在时间/行号之后。
        var g = new GutterLayout(CellW, true, true, true, true);
        Assert.IsFalse(g.IsFoldColumnHit(g.FoldLeft - 1), "折叠列左边缘之前不算命中。");
        Assert.IsTrue(g.IsFoldColumnHit(g.FoldLeft), "折叠列左边缘算命中。");
        Assert.IsTrue(g.IsFoldColumnHit(g.FoldLeft + g.FoldWidth), "折叠列右侧空白区仍算命中(便于点击)。");
        Assert.IsFalse(g.IsFoldColumnHit(g.TotalWidth), "侧栏右边缘之外不算命中。");
    }

    [TestMethod]
    public void FoldOff_NeverFoldHit()
    {
        var g = new GutterLayout(CellW, showTimestamp: true, showNumber: true, showFold: false, blank: true);
        Assert.AreEqual(0, g.FoldWidth);
        Assert.IsFalse(g.IsFoldColumnHit(g.FoldLeft));
        Assert.IsFalse(g.IsFoldColumnHit(g.TotalWidth - 1));
    }

    [TestMethod]
    public void FoldOnly_FoldColumnStartsAtZero()
    {
        var g = new GutterLayout(CellW, showTimestamp: false, showNumber: false, showFold: true, blank: false);
        Assert.AreEqual(0, g.FoldLeft);
        Assert.IsTrue(g.IsFoldColumnHit(0));
        Assert.IsTrue(g.IsFoldColumnHit(g.FoldWidth - 1));
    }
}
