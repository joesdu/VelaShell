using System.Text;
using VelaShell.Terminal.Emulation;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// 行时间戳(时间/行号侧栏的数据基础):写入的行盖上「本次 Feed 到达时刻」,该时间戳随行对象在
/// 滚动时迁入 scrollback 而保留;从未写入内容的空行时间戳为 null(侧栏据此不显示时间)。
/// </summary>
[TestClass]
[TestCategory("LineTimestamp")]
public class LineTimestampTests
{
    private static TerminalEmulator New(int cols = 20, int rows = 4) => new(cols, rows);

    private static void Feed(TerminalEmulator e, string s) => e.Feed(Encoding.UTF8.GetBytes(s));

    [TestMethod]
    public void WrittenRow_GetsTimestamp()
    {
        TerminalEmulator e = New();
        DateTime before = DateTime.Now;
        Feed(e, "hello");
        DateTime after = DateTime.Now;

        DateTime? ts = e.Screen.ActiveLine(0).Timestamp;
        Assert.IsNotNull(ts, "写入内容的行应被盖上时间戳。");
        Assert.IsTrue(ts >= before && ts <= after, "时间戳应落在该次写入的时刻区间内。");
    }

    [TestMethod]
    public void UntouchedRow_HasNoTimestamp()
    {
        TerminalEmulator e = New();
        Feed(e, "only first row");
        Assert.IsNull(e.Screen.ActiveLine(1).Timestamp, "从未写入内容的空行不应有时间戳。");
    }

    [TestMethod]
    public void Timestamp_TravelsIntoScrollback()
    {
        TerminalEmulator e = New(20, 2); // 仅 2 行,便于把第一行挤入 scrollback
        Feed(e, "first");
        DateTime? original = e.Screen.ActiveLine(0).Timestamp;
        Assert.IsNotNull(original);

        // 换足够多行把 "first" 顶出屏幕、进入 scrollback。
        Feed(e, "\r\nsecond\r\nthird\r\nfourth");
        Assert.IsGreaterThan(0, e.Screen.ScrollbackCount, "应已有行滚入 scrollback。");

        // 绝对行 0 是最早的历史行(= "first"),其时间戳应与写入时一致(行对象按引用迁移)。
        TerminalRow oldest = e.Screen.ViewLine(0);
        Assert.AreEqual("first", oldest.GetText());
        Assert.AreEqual(original, oldest.Timestamp, "滚入 scrollback 后时间戳应保持不变。");
    }

    [TestMethod]
    public void Timestamp_SurvivesColumnReflow()
    {
        // 开关侧栏会改变可用列宽 → 触发主屏 reflow(重建行对象)。时间戳必须穿过 reflow,
        // 否则切换侧栏后历史行的时间/行号信息全部丢失(用户反馈的核心 bug)。
        TerminalEmulator e = New(40, 4);
        e.Screen.MaxScrollback = 1000;
        Feed(e, "alpha\r\nbeta\r\ngamma");
        DateTime? alphaTs = e.Screen.ViewLine(0).Timestamp;
        Assert.IsNotNull(alphaTs);

        // 模拟开启侧栏:列宽变窄触发 reflow。
        e.Resize(30, 4);

        // 内容与时间戳都应保留。
        bool found = false;
        for (int r = 0; r < e.Screen.TotalRows; r++)
        {
            TerminalRow row = e.Screen.ViewLine(r);
            if (row.GetText() == "alpha")
            {
                Assert.AreEqual(alphaTs, row.Timestamp, "reflow 后行时间戳应保持不变。");
                found = true;
            }
        }
        Assert.IsTrue(found, "reflow 后仍应能找到原始行 alpha。");
    }

    [TestMethod]
    public void BlankLineWithinOutput_GetsTimestamp_TrailingRowsDoNot()
    {
        // 侧栏行号可见性依赖时间戳:输出中间的空行(光标经过)应有时间戳→显示行号;
        // 光标从未到过的屏幕底部空行不应有时间戳→不显示行号(避免空屏凭空冒出几十行编号)。
        TerminalEmulator e = New(20, 8);
        Feed(e, "L0\r\n\r\nL2"); // L0、空行、L2

        Assert.IsNotNull(e.Screen.ActiveLine(0).Timestamp, "内容行应有时间戳。");
        Assert.IsNotNull(e.Screen.ActiveLine(1).Timestamp, "输出中间的空行也应有时间戳(光标经过)。");
        Assert.IsNotNull(e.Screen.ActiveLine(2).Timestamp);
        Assert.IsNull(e.Screen.ActiveLine(4).Timestamp, "光标从未到过的底部空行不应有时间戳。");
        Assert.IsNull(e.Screen.ActiveLine(7).Timestamp);
    }

    [TestMethod]
    public void ErasingRow_ClearsTimestamp()
    {
        TerminalEmulator e = New();
        Feed(e, "content");
        Assert.IsNotNull(e.Screen.ActiveLine(0).Timestamp);

        // 整行擦除(EL 模式 2)后该行视为空行,时间戳应作废。
        Feed(e, "\r\x1b[2K");
        Assert.IsNull(e.Screen.ActiveLine(0).Timestamp, "整行擦除后时间戳应清空。");
    }

    /// <summary>
    /// EL 模式 0(ESC[K,擦到行尾)是重绘型 shell 的主力擦除手段 —— PSReadLine 每重画一行都发它。
    /// 它同样把行擦空,时间戳就同样该作废,否则侧栏会在空行上显示时间。
    /// </summary>
    [TestMethod]
    public void ErasingToEndOfLine_ClearsTimestamp_WhenRowBecomesBlank()
    {
        TerminalEmulator e = New();
        Feed(e, "content");
        Assert.IsNotNull(e.Screen.ActiveLine(0).Timestamp);

        Feed(e, "\r\x1b[K");
        Assert.IsNull(e.Screen.ActiveLine(0).Timestamp, "擦到行尾后整行已空,时间戳应清空。");
    }

    /// <summary>擦除只清掉一部分时,行还有内容,时间戳必须留着 —— 别矫枉过正。</summary>
    [TestMethod]
    public void ErasingToEndOfLine_KeepsTimestamp_WhenContentRemains()
    {
        TerminalEmulator e = New();
        Feed(e, "abcdef");
        DateTime? ts = e.Screen.ActiveLine(0).Timestamp;

        // 光标回到第 3 列再擦到行尾:留下 "abc"。
        Feed(e, "\r\x1b[3C\x1b[K");
        Assert.AreEqual("abc", e.Screen.ActiveLine(0).GetText(), "应只擦掉光标之后的部分。");
        Assert.AreEqual(ts, e.Screen.ActiveLine(0).Timestamp, "行仍有内容,时间戳应保留。");
    }

    /// <summary>
    /// 用户实测的场景:PowerShell + PSReadLine 在提示符下方画出历史/预测列表,撤销输入后又逐行
    /// 用 ESC[K 擦掉。被擦掉的行必须不再有时间戳,否则侧栏的时间线会一直拖到提示符下方,
    /// 折叠导引线也随之画过光标位置(光标被盖住)。
    /// </summary>
    [TestMethod]
    public void PredictionRowsErasedWithEl0_LeaveNoTimestampBelowThePrompt()
    {
        TerminalEmulator e = New(40, 8);
        Feed(e, "PS> c");                       // 提示符 + 输入(第 0 行)
        Feed(e, "\r\n> history one");           // 下方列表
        Feed(e, "\r\n> history two");

        Assert.IsNotNull(e.Screen.ActiveLine(1).Timestamp, "列表行此时确有内容。");
        Assert.IsNotNull(e.Screen.ActiveLine(2).Timestamp);

        // 撤销输入:PSReadLine 逐行 ESC[K 擦掉列表,光标回到提示符行。
        Feed(e, "\r\x1b[K");                    // 擦第 2 行
        Feed(e, "\x1b[A\r\x1b[K");              // 上移,擦第 1 行
        Feed(e, "\x1b[A\r\x1b[KPS> ");          // 上移,重画提示符行

        Assert.IsNull(e.Screen.ActiveLine(1).Timestamp, "被擦空的列表行不应再有时间戳。");
        Assert.IsNull(e.Screen.ActiveLine(2).Timestamp, "被擦空的列表行不应再有时间戳。");
        Assert.IsNotNull(e.Screen.ActiveLine(0).Timestamp, "提示符行仍有内容,应保留时间戳。");
    }
}
