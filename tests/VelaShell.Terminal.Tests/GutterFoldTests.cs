using System.Text;
using VelaShell.Terminal.Emulation;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// 侧栏折叠模型的行为验证(WindTerm 式历史折叠):折叠隐藏正确的行、点折叠头展开、多段折叠、
/// 折叠随行对象滚入 scrollback 保留、折叠头被淘汰后自动清除。GutterFoldModel 与控件解耦,可无头验证。
/// </summary>
[TestClass]
[TestCategory("GutterFold")]
public class GutterFoldTests
{
    private static TerminalScreen ScreenWithLines(int screenRows, int count, int maxScrollback = 0)
    {
        var e = new TerminalEmulator(40, screenRows, TerminalType.XtermColor256);
        if (maxScrollback > 0)
        {
            e.Screen.MaxScrollback = maxScrollback;
        }
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            sb.Append('L').Append(i);
            if (i < count - 1)
            {
                sb.Append("\r\n");
            }
        }
        e.Feed(Encoding.UTF8.GetBytes(sb.ToString()));
        return e.Screen;
    }

    private static int FindAbs(TerminalScreen screen, string text)
    {
        for (int a = 0; a < screen.TotalRows; a++)
        {
            if (screen.ViewLine(a).GetText() == text)
            {
                return a;
            }
        }
        return -1;
    }

    [TestMethod]
    public void NoFolds_VisibleRowsIsIdentity()
    {
        TerminalScreen screen = ScreenWithLines(8, 6);
        var model = new GutterFoldModel();
        Assert.IsFalse(model.HasFolds);
        Assert.IsNull(model.VisibleRowsOrNull(screen), "无折叠应返回 null(走连续快路径)。");
    }

    [TestMethod]
    public void Fold_HidesRowsAboveAnchor_AnchorStaysVisible()
    {
        TerminalScreen screen = ScreenWithLines(8, 6); // L0..L5 在 abs 0..5
        var model = new GutterFoldModel();

        Assert.IsTrue(model.Toggle(screen, 3), "点第 3 行应折叠 L0..L2。");
        Assert.IsTrue(model.HasFolds);
        Assert.IsTrue(model.IsAnchor(screen, 3), "被点击行 L3 应成为折叠头。");
        Assert.IsFalse(model.IsAnchor(screen, 4));

        List<int>? vis = model.VisibleRowsOrNull(screen);
        Assert.IsNotNull(vis);
        Assert.DoesNotContain(0, vis);
        Assert.DoesNotContain(1, vis);
        Assert.DoesNotContain(2, vis);
        Assert.Contains(3, vis); // 折叠头可见
        Assert.Contains(4, vis);
        Assert.Contains(5, vis);
    }

    [TestMethod]
    public void Toggle_OnAnchor_Expands()
    {
        TerminalScreen screen = ScreenWithLines(8, 6);
        var model = new GutterFoldModel();
        model.Toggle(screen, 3);
        Assert.IsTrue(model.HasFolds);

        Assert.IsTrue(model.Toggle(screen, 3), "再点折叠头应展开。");
        Assert.IsFalse(model.HasFolds);
        Assert.IsNull(model.VisibleRowsOrNull(screen));
    }

    [TestMethod]
    public void Fold_AtTopRow_NothingBefore_NoOp()
    {
        TerminalScreen screen = ScreenWithLines(8, 6);
        var model = new GutterFoldModel();
        Assert.IsFalse(model.Toggle(screen, 0), "第 0 行之前无内容,不应折叠。");
        Assert.IsFalse(model.HasFolds);
    }

    [TestMethod]
    public void Fold_OutOfRange_NoOp()
    {
        TerminalScreen screen = ScreenWithLines(8, 6);
        var model = new GutterFoldModel();
        Assert.IsFalse(model.Toggle(screen, -1));
        Assert.IsFalse(model.Toggle(screen, screen.TotalRows));
        Assert.IsFalse(model.HasFolds);
    }

    [TestMethod]
    public void Fold_MultipleRegions_EachHidesItsBlock()
    {
        TerminalScreen screen = ScreenWithLines(10, 8); // L0..L7
        var model = new GutterFoldModel();
        Assert.IsTrue(model.Toggle(screen, 3)); // 区域1:隐藏 L0..L2,头 L3
        Assert.IsTrue(model.Toggle(screen, 6)); // 区域2:隐藏 L4..L5,头 L6(边界在头1 L3 之后)
        Assert.AreEqual(2, model.Count);

        List<int>? vis = model.VisibleRowsOrNull(screen);
        Assert.IsNotNull(vis);
        foreach (int hidden in new[] { 0, 1, 2, 4, 5 })
        {
            Assert.DoesNotContain(hidden, vis);
        }
        foreach (int shown in new[] { 3, 6, 7 })
        {
            Assert.Contains(shown, vis);
        }
        Assert.IsTrue(model.IsAnchor(screen, 3));
        Assert.IsTrue(model.IsAnchor(screen, 6));
    }

    [TestMethod]
    public void Clear_RemovesAllFolds()
    {
        TerminalScreen screen = ScreenWithLines(8, 6);
        var model = new GutterFoldModel();
        model.Toggle(screen, 3);
        model.Clear();
        Assert.IsFalse(model.HasFolds);
        Assert.IsNull(model.VisibleRowsOrNull(screen));
    }

    [TestMethod]
    public void Fold_PersistsAcrossScroll_ByRowReference()
    {
        // 折叠后继续输出使行滚入 scrollback(行对象按引用迁移),折叠应随内容保留。
        var e = new TerminalEmulator(40, 4, TerminalType.XtermColor256);
        e.Screen.MaxScrollback = 100;
        e.Feed(Encoding.UTF8.GetBytes("L0\r\nL1\r\nL2\r\nL3"));
        var model = new GutterFoldModel();
        Assert.IsTrue(model.Toggle(e.Screen, FindAbs(e.Screen, "L2"))); // 折叠 L0..L1,头 L2

        e.Feed(Encoding.UTF8.GetBytes("\r\nL4\r\nL5\r\nL6")); // 顶几行进 scrollback

        int absL2 = FindAbs(e.Screen, "L2");
        Assert.IsGreaterThanOrEqualTo(0, absL2, "L2 应仍在缓冲区(scrollback)。");
        Assert.IsTrue(model.IsAnchor(e.Screen, absL2), "滚动后折叠头应随行对象保留。");
        List<int>? vis = model.VisibleRowsOrNull(e.Screen);
        Assert.IsNotNull(vis);
        Assert.DoesNotContain(FindAbs(e.Screen, "L0"), vis);
        Assert.DoesNotContain(FindAbs(e.Screen, "L1"), vis);
        Assert.Contains(absL2, vis);
    }

    [TestMethod]
    public void FillScreenRowMap_NoFolds_IsIdentityAndClampsOffset()
    {
        int[] map = new int[4];
        int offset = 0;
        // total=10, screen=4, offset=0 → 底部 4 行 abs 6..9。
        GutterFoldModel.FillScreenRowMap(map, null, totalRows: 10, screenRows: 4, ref offset);
        Assert.AreSequenceEqual([6, 7, 8, 9], map);

        // offset 超范围应夹到 (total-screen)=6。
        offset = 999;
        GutterFoldModel.FillScreenRowMap(map, null, 10, 4, ref offset);
        Assert.AreEqual(6, offset);
        Assert.AreSequenceEqual([0, 1, 2, 3], map);
    }

    [TestMethod]
    public void FillScreenRowMap_WithFolds_UsesVisibleSequence()
    {
        int[] map = new int[4];
        int offset = 0;
        // 折叠后可见序列(隐藏了 1,2):[0,3,4,5]。屏幕 4 行 → 全部显示。
        var visible = new List<int> { 0, 3, 4, 5 };
        GutterFoldModel.FillScreenRowMap(map, visible, totalRows: 6, screenRows: 4, ref offset);
        Assert.AreSequenceEqual([0, 3, 4, 5], map);
    }

    [TestMethod]
    public void ClickInFoldColumn_FoldsThatRow_ThenExpands_EndToEnd()
    {
        // 复刻控件点击链路:GutterLayout 命中折叠列 → y→屏幕行 → FillScreenRowMap→绝对行 → model.Toggle。
        const double cellW = 8, cellH = 16;
        TerminalScreen screen = ScreenWithLines(8, 6); // L0..L5 在 abs 0..5,屏幕行 0..5
        var model = new GutterFoldModel();
        var layout = new GutterLayout(cellW, showTimestamp: false, showNumber: false, showFold: true, blank: false);

        // 用户点击折叠列的第 3 屏幕行。
        double clickX = layout.FoldLeft + 2;
        double clickY = 3 * cellH + 4;
        Assert.IsTrue(layout.ContainsX(clickX));
        Assert.IsTrue(layout.IsFoldColumnHit(clickX));
        int screenRow = (int)(clickY / cellH);
        Assert.AreEqual(3, screenRow);

        int[] map = new int[screen.Rows];
        int offset = 0;
        GutterFoldModel.FillScreenRowMap(map, model.VisibleRowsOrNull(screen), screen.TotalRows, screen.Rows, ref offset);
        int abs = map[screenRow];
        Assert.AreEqual(3, abs, "第 3 屏幕行应映射到绝对行 3(L3)。");

        Assert.IsTrue(model.Toggle(screen, abs), "点击应触发折叠。");
        Assert.IsTrue(model.IsAnchor(screen, 3));
        List<int>? vis = model.VisibleRowsOrNull(screen);
        Assert.IsNotNull(vis);
        foreach (int hidden in new[] { 0, 1, 2 })
        {
            Assert.DoesNotContain(hidden, vis);
        }
        Assert.Contains(3, vis);

        // 折叠头 L3 现在在屏幕行 0;再点它 → 展开。
        offset = 0;
        GutterFoldModel.FillScreenRowMap(map, model.VisibleRowsOrNull(screen), screen.TotalRows, screen.Rows, ref offset);
        int anchorScreenRow = Array.IndexOf(map, 3);
        Assert.AreEqual(0, anchorScreenRow, "折叠后 L3 应位于屏幕顶行。");
        Assert.IsTrue(model.Toggle(screen, map[anchorScreenRow]));
        Assert.IsFalse(model.HasFolds, "再点折叠头应展开。");
    }

    [TestMethod]
    public void Fold_PrunedWhenAnchorEvicted()
    {
        var e = new TerminalEmulator(40, 3, TerminalType.XtermColor256);
        e.Screen.MaxScrollback = 2;
        e.Feed(Encoding.UTF8.GetBytes("L0\r\nL1\r\nL2"));
        var model = new GutterFoldModel();
        Assert.IsTrue(model.Toggle(e.Screen, FindAbs(e.Screen, "L2"))); // 头 L2
        Assert.IsTrue(model.HasFolds);

        for (int i = 3; i < 30; i++) // 大量输出把 L2 挤出 2 行的 scrollback
        {
            e.Feed(Encoding.UTF8.GetBytes("\r\nL" + i));
        }
        Assert.IsLessThan(0, FindAbs(e.Screen, "L2"), "L2 应已被淘汰出缓冲区。");
        model.PruneStale(e.Screen);
        Assert.IsFalse(model.HasFolds, "折叠头被淘汰后折叠应自动清除。");
    }
}
