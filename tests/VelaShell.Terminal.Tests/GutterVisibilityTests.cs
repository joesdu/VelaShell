using System.Text;
using VelaShell.Terminal.Emulation;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// 侧栏(行号/时间戳/折叠导引线)的显示范围判定 —— <see cref="VelaTerminalControl.ShowsGutterFor" />。
/// 时间线不得越过光标:换行会给经过的行盖时间戳(哪怕没写内容),重绘型 shell 又常在提示符下方
/// 来回涂改,不设界的话时间线会拖到提示符下方,折叠导引线也会画过光标把光标盖住。
/// </summary>
[TestClass]
[TestCategory("GutterVisibility")]
public class GutterVisibilityTests
{
    private static TerminalRow RowWith(string text, DateTime? timestamp)
    {
        var row = new TerminalRow(20) { Timestamp = timestamp };
        for (int i = 0; i < text.Length; i++)
        {
            row[i] = new TerminalCell { Rune = text[i] };
        }
        return row;
    }

    [TestMethod]
    public void ContentRow_IsShown_EvenBelowTheCursor()
    {
        // 满屏重绘型程序(vim 等)光标下方也有内容,不能按光标位置砍掉。
        TerminalRow row = RowWith("text", DateTime.Now);
        Assert.IsTrue(VelaTerminalControl.ShowsGutterFor(row, absoluteRow: 9, cursorAbsoluteRow: 3));
    }

    [TestMethod]
    public void ContentRow_WithoutTimestamp_IsStillShown()
    {
        TerminalRow row = RowWith("text", null);
        Assert.IsTrue(VelaTerminalControl.ShowsGutterFor(row, absoluteRow: 1, cursorAbsoluteRow: 3));
    }

    /// <summary>输出中间、光标经过留下的空行:显示时间(时间线不该在中间断开)。</summary>
    [TestMethod]
    public void BlankRow_WithTimestamp_AboveCursor_IsShown()
    {
        TerminalRow row = RowWith("", DateTime.Now);
        Assert.IsTrue(VelaTerminalControl.ShowsGutterFor(row, absoluteRow: 1, cursorAbsoluteRow: 3));
    }

    [TestMethod]
    public void BlankRow_WithTimestamp_AtCursor_IsShown()
    {
        TerminalRow row = RowWith("", DateTime.Now);
        Assert.IsTrue(VelaTerminalControl.ShowsGutterFor(row, absoluteRow: 3, cursorAbsoluteRow: 3));
    }

    /// <summary>提示符下方被涂改过、如今空白的行:不显示 —— 时间线到光标为止。</summary>
    [TestMethod]
    public void BlankRow_WithTimestamp_BelowCursor_IsHidden()
    {
        TerminalRow row = RowWith("", DateTime.Now);
        Assert.IsFalse(VelaTerminalControl.ShowsGutterFor(row, absoluteRow: 4, cursorAbsoluteRow: 3));
    }

    [TestMethod]
    public void BlankRow_WithoutTimestamp_IsHidden()
    {
        TerminalRow row = RowWith("", null);
        Assert.IsFalse(VelaTerminalControl.ShowsGutterFor(row, absoluteRow: 1, cursorAbsoluteRow: 3));
    }

    /// <summary>
    /// 端到端:PSReadLine 式的「提示符下方画列表 → 撤销 → 逐行 ESC[K 擦掉 → 光标回到提示符」,
    /// 之后提示符下方不应还有任何行显示侧栏。
    /// </summary>
    [TestMethod]
    public void AfterPredictionListIsErased_NoGutterBelowThePrompt()
    {
        var e = new TerminalEmulator(40, 8);
        void Feed(string s) => e.Feed(Encoding.UTF8.GetBytes(s));

        Feed("PS> c");
        Feed("\r\n> history one");
        Feed("\r\n> history two");

        // 撤销:逐行擦掉列表并回到提示符行。
        Feed("\r\x1b[K");
        Feed("\x1b[A\r\x1b[K");
        Feed("\x1b[A\r\x1b[KPS> ");

        int cursorAbs = e.Screen.ScrollbackCount + e.Screen.CursorY;
        Assert.AreEqual(0, e.Screen.CursorY, "光标应已回到提示符行。");

        for (int row = cursorAbs + 1; row < e.Screen.TotalRows; row++)
        {
            Assert.IsFalse(VelaTerminalControl.ShowsGutterFor(e.Screen.ViewLine(row), row, cursorAbs),
                           $"提示符下方第 {row} 行已空,不应再显示侧栏。");
        }
    }
}
