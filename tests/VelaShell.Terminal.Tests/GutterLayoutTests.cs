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
        Assert.AreEqual(15 * CellW, g.TimeWidth);
        Assert.AreEqual((GutterLayout.NumberDigits + 1) * CellW, g.NumberWidth);
        Assert.AreEqual(Math.Ceiling(CellW * 2), g.FoldWidth);
        Assert.AreEqual(GutterLayout.BlankPixels, g.BlankWidth);
        Assert.AreEqual(g.TimeWidth, g.NumberLeft);
        Assert.AreEqual(g.TimeWidth + g.NumberWidth, g.FoldLeft);
        Assert.AreEqual(g.TimeWidth + g.NumberWidth + g.FoldWidth + g.BlankWidth, g.TotalWidth);
    }

    [TestMethod]
    public void TimestampMillisOff_ShrinksToSecondsWidth()
    {
        // 毫秒开关关闭时时间戳列退回秒级宽度("[HH:mm:ss] " = 11 列),侧栏随之变窄。
        var g = new GutterLayout(CellW, showTimestamp: true, showNumber: true, showFold: true, blank: true, showTimestampMillis: false);
        Assert.AreEqual(11 * CellW, g.TimeWidth);
        Assert.AreEqual(g.TimeWidth, g.NumberLeft);
    }

    [TestMethod]
    public void FoldColumnHit_OnlyWithinFoldColumn()
    {
        // 时间+行号+折叠+空白都开:折叠列在时间/行号之后。
        var g = new GutterLayout(CellW, true, true, true, true);
        Assert.IsFalse(g.IsFoldColumnHit(g.FoldLeft - 1), "折叠列左边缘之前不算命中。");
        Assert.IsTrue(g.IsFoldColumnHit(g.FoldLeft), "折叠列左边缘算命中。");
        Assert.IsTrue(g.IsFoldColumnHit(g.FoldLeft + g.FoldWidth - 0.5), "折叠列右边缘以内算命中。");
        Assert.IsFalse(g.IsFoldColumnHit(g.TotalWidth), "侧栏右边缘之外不算命中。");
    }

    /// <summary>
    /// #586:空白紧贴正文第一列,从左往右拖选整行时指针最容易落在那里。它以前也算折叠命中,
    /// 一按就把上方输出折掉 —— 现在它只是间隔。
    /// </summary>
    [TestMethod]
    public void BlankGap_IsNotFoldHit()
    {
        var g = new GutterLayout(CellW, showTimestamp: false, showNumber: false, showFold: true, blank: true);
        for (double x = g.FoldLeft + g.FoldWidth; x < g.TotalWidth; x += 0.5)
        {
            Assert.IsTrue(g.ContainsX(x), $"x={x} 仍在侧栏内(左键照旧吞掉,不选文本)。");
            Assert.IsFalse(g.IsFoldColumnHit(x), $"x={x} 在空白里,不该触发折叠。");
        }
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

    /// <summary>#586:只要线、不要折叠 —— 整列照样占位画线,但整列都点不出折叠。</summary>
    [TestMethod]
    public void GuideLineOnly_OccupiesColumn_ButNeverFoldHit()
    {
        var g = new GutterLayout(CellW, showTimestamp: false, showNumber: false, showFold: false, blank: false, showGuideLine: true);
        Assert.IsTrue(g.Enabled);
        Assert.IsTrue(g.GuideLineEnabled);
        Assert.IsFalse(g.FoldEnabled);
        Assert.AreEqual(Math.Ceiling(CellW * 2), g.FoldWidth);
        for (double x = 0; x < g.TotalWidth; x += 0.5)
        {
            Assert.IsFalse(g.IsFoldColumnHit(x), $"x={x}:没开折叠,不该有折叠命中。");
        }
    }

    /// <summary>分隔线开着时来回切折叠,侧栏宽度不变 —— 否则可用列数一变,PTY 就得跟着改宽、正文左右跳。</summary>
    [TestMethod]
    public void TogglingFold_WithGuideLineOn_KeepsGutterWidth()
    {
        var withFold = new GutterLayout(CellW, true, true, showFold: true, blank: true, showGuideLine: true);
        var lineOnly = new GutterLayout(CellW, true, true, showFold: false, blank: true, showGuideLine: true);
        Assert.AreEqual(withFold.TotalWidth, lineOnly.TotalWidth);
        Assert.AreEqual(withFold.FoldLeft, lineOnly.FoldLeft);
    }

    /// <summary>#586 顺带把方框加大一圈(原先 7–11px),且始终是奇数、放得进折叠列。</summary>
    [TestMethod]
    [DataRow(8.0, 16.0, 9)]
    [DataRow(8.4, 19.0, 11)]
    [DataRow(9.6, 22.0, 13)]
    [DataRow(12.0, 40.0, 13)]
    public void FoldBox_IsLargerOdd_AndFitsColumn(double cellW, double cellH, int expected)
    {
        var g = new GutterLayout(cellW, false, false, showFold: true, blank: false);
        int box = g.FoldBoxSize(cellH);
        Assert.AreEqual(expected, box);
        Assert.AreEqual(1, box % 2, "边长取奇数,± 才有单像素中心。");
        Assert.IsLessThanOrEqualTo(g.FoldWidth - 2, (double)box, "方框左右至少各给折叠列留 1px。");
    }

    [TestMethod]
    public void FoldBox_ShrinksToFit_NarrowColumn()
    {
        // 极小字号 + 大行高:行高算出的边长放不进列宽时让位给列宽,仍保持奇数。
        var g = new GutterLayout(3.6, false, false, showFold: true, blank: false);
        int box = g.FoldBoxSize(30);
        Assert.IsLessThanOrEqualTo(g.FoldWidth - 2, (double)box);
        Assert.AreEqual(1, box % 2);
    }
}
