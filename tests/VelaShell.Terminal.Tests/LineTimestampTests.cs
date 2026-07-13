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
        Assert.IsTrue(e.Screen.ScrollbackCount > 0, "应已有行滚入 scrollback。");

        // 绝对行 0 是最早的历史行(= "first"),其时间戳应与写入时一致(行对象按引用迁移)。
        TerminalRow oldest = e.Screen.ViewLine(0);
        Assert.AreEqual("first", oldest.GetText());
        Assert.AreEqual(original, oldest.Timestamp, "滚入 scrollback 后时间戳应保持不变。");
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
}
