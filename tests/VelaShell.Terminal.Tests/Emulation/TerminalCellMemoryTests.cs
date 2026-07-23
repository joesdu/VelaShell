using System.Runtime.CompilerServices;
using System.Text;
using VelaShell.Terminal.Emulation;

namespace VelaShell.Terminal.Tests.Emulation;

/// <summary>
/// <see cref="TerminalCell" /> 的内存布局与组合标记驻留池回归:
/// 回滚缓冲可达数百万格,这里锁住"单元格不含托管引用(GC 不逐格扫描)"的优化成果。
/// 组合字符一律用 \u 转义书写,避免源文件编码链偷偷把 e+U+0301 规范化成预组合字符。
/// </summary>
[TestClass]
[TestCategory("CellMemory")]
public class TerminalCellMemoryTests
{
    private const string AcuteAccent = "\u0301"; // 组合尖音符
    private const string GraveAccent = "\u0300"; // 组合重音符
    private const string Diaeresis = "\u0308";   // 组合分音符

    [TestMethod]
    public void TerminalCell_ContainsNoManagedReferences()
    {
        // 这是本结构的内存契约:一旦有人往 cell 里加回引用类型字段,
        // 整个回滚缓冲会重新变成 GC 扫描对象,数百万格的代价悄然回归。
        Assert.IsFalse(
            RuntimeHelpers.IsReferenceOrContainsReferences<TerminalCell>(),
            "TerminalCell 必须保持 blittable:组合标记等引用数据请走 CombiningPool 驻留索引。");
    }

    [TestMethod]
    public void Combining_RoundTripsThroughPool_AndPreservesEqualitySemantics()
    {
        TerminalCell cell = TerminalCell.Empty;
        cell.Rune = 'e';
        Assert.IsNull(cell.Combining);

        cell.Combining = AcuteAccent;
        Assert.AreEqual(AcuteAccent, cell.Combining);

        TerminalCell same = TerminalCell.Empty;
        same.Rune = 'e';
        same.Combining = AcuteAccent;
        Assert.AreEqual(cell, same, "相同组合标记经驻留后必须等值(同串同索引)。");

        TerminalCell different = TerminalCell.Empty;
        different.Rune = 'e';
        different.Combining = GraveAccent;
        Assert.AreNotEqual(cell, different);

        cell.Combining = null;
        Assert.IsNull(cell.Combining);
        Assert.AreEqual(0, cell.CombiningIndex);
    }

    [TestMethod]
    public void Combining_AppendText_EmitsBaseRunePlusMarks()
    {
        TerminalCell cell = TerminalCell.Empty;
        cell.Rune = 'a';
        cell.Combining = Diaeresis;
        var sb = new StringBuilder();
        cell.AppendText(sb);
        Assert.AreEqual("a" + Diaeresis, sb.ToString());
        Assert.AreEqual("a" + Diaeresis, cell.GetText());
    }

    [TestMethod]
    public void Emulator_CombiningMark_FoldsIntoPrecedingCell()
    {
        // 端到端:经 VT 流写入的组合字符仍折叠进基础格(驻留池改造不改变行为)。
        var emulator = new TerminalEmulator(20, 4);
        emulator.Feed(Encoding.UTF8.GetBytes("e" + AcuteAccent));
        TerminalCell cell = emulator.Screen.ViewLine(0)[0];
        Assert.AreEqual('e', cell.Rune);
        Assert.AreEqual(AcuteAccent, cell.Combining);
        Assert.AreEqual("e" + AcuteAccent, cell.GetText());
    }
}
