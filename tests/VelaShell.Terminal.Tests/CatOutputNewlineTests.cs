using System.Text;
using VelaShell.Terminal.Emulation;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// 复现用户报告的 cat 输出换行问题(文件末尾"测试"后提示符粘连):
/// 覆盖尾换行/无尾换行/CRLF 文件/分块切割/行尾 pendingWrap/OSC 前缀提示符等场景,
/// 确认换行不会在任何一层被吞掉。
/// </summary>
[TestClass]
public class CatOutputNewlineTests
{
    private static TerminalEmulator NewTerm(int cols = 80, int rows = 24) => new(cols, rows);

    private static string Line(TerminalEmulator e, int row) => e.Screen.ActiveLine(row).GetText();

    private static void FeedText(TerminalEmulator e, string text) => e.Feed(Encoding.UTF8.GetBytes(text));

    [TestMethod]
    public void TrailingNewline_PromptStartsOnFreshLine()
    {
        TerminalEmulator e = NewTerm();
        // ONLCR 后的字节流:内容行 + \r\n,随后提示符。
        FeedText(e, "测试\r\npi@host:~$ ");
        Assert.AreEqual("测试", Line(e, 0).TrimEnd());
        Assert.AreEqual("pi@host:~$", Line(e, 1).TrimEnd());
        Assert.AreEqual(1, e.CursorY);
    }

    [TestMethod]
    public void NoTrailingNewline_PromptGluesToContent_MatchesRealTerminals()
    {
        TerminalEmulator e = NewTerm();
        FeedText(e, "测试pi@host:~$ ");

        // 无尾换行时粘连是忠实行为(bash 不感知列位置)。
        Assert.AreEqual("测试pi@host:~$", Line(e, 0).TrimEnd());
    }

    [TestMethod]
    public void CrLfFileThroughOnlcr_PromptOnFreshLine()
    {
        TerminalEmulator e = NewTerm();
        // CRLF 文件里的 \r\n 经 tty ONLCR 变成 \r\r\n。
        FeedText(e, "测试\r\r\npi@host:~$ ");
        Assert.AreEqual("测试", Line(e, 0).TrimEnd());
        Assert.AreEqual("pi@host:~$", Line(e, 1).TrimEnd());
    }

    [TestMethod]
    public void ChunkSplit_AtEveryByteBoundary_YieldsSameResult()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("Gauge32 X\r\n\r\n测试\r\npi@host:~$ ");
        for (int split = 1; split < bytes.Length; split++)
        {
            TerminalEmulator e = NewTerm();
            e.Feed(bytes.AsSpan(0, split));
            e.Feed(bytes.AsSpan(split));
            Assert.AreEqual("Gauge32 X", Line(e, 0).TrimEnd(), $"split={split}");
            Assert.AreEqual("", Line(e, 1).TrimEnd(), $"split={split}");
            Assert.AreEqual("测试", Line(e, 2).TrimEnd(), $"split={split}");
            Assert.AreEqual("pi@host:~$", Line(e, 3).TrimEnd(), $"split={split}");
        }
    }

    [TestMethod]
    public void NewlineRightAfterFullLine_NoExtraOrMissingRow()
    {
        TerminalEmulator e = NewTerm(10);
        // 写满整行触发 pendingWrap,紧跟 \r\n:应只换一行,提示符在下一行。
        FeedText(e, "0123456789\r\npi@host:~$");
        Assert.AreEqual("0123456789", Line(e, 0));
        Assert.AreEqual("pi@host:~$", Line(e, 1).TrimEnd());
        Assert.AreEqual(1, e.CursorY);
    }

    [TestMethod]
    public void WideCharEndsAtLastColumn_ThenCrLf_PromptOnFreshLine()
    {
        TerminalEmulator e = NewTerm(4);
        // 两个宽字符占满 4 列 → pendingWrap;随后 \r\n。
        FeedText(e, "测试\r\nab");
        Assert.AreEqual("测试", Line(e, 0));
        Assert.AreEqual("ab", Line(e, 1).TrimEnd());
    }

    [TestMethod]
    public void OscTitlePrefixedPrompt_AfterTrailingNewline_StaysOnFreshLine()
    {
        TerminalEmulator e = NewTerm();
        // Debian bash PS1 以 OSC 0 标题开头:ESC ]0;...BEL。
        FeedText(e, "测试\r\n\x1b]0;pi@host: ~\apy@host:~$ ");
        Assert.AreEqual("测试", Line(e, 0).TrimEnd());
        Assert.AreEqual("py@host:~$", Line(e, 1).TrimEnd());
    }

    [TestMethod]
    public void OscTitle_SplitAcrossFeeds_DoesNotEatFollowingText()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("测试\r\n\x1b]0;pi@host: ~\apy@host:~$ ");
        for (int split = 1; split < bytes.Length; split++)
        {
            TerminalEmulator e = NewTerm();
            e.Feed(bytes.AsSpan(0, split));
            e.Feed(bytes.AsSpan(split));
            Assert.AreEqual("测试", Line(e, 0).TrimEnd(), $"split={split}");
            Assert.AreEqual("py@host:~$", Line(e, 1).TrimEnd(), $"split={split}");
        }
    }

    [TestMethod]
    public void TabsInContent_DoNotSwallowNewline()
    {
        TerminalEmulator e = NewTerm();
        FeedText(e, "A\tB\tC\r\n测试\r\npi@host:~$ ");
        Assert.StartsWith("A", Line(e, 0));
        Assert.AreEqual("测试", Line(e, 1).TrimEnd());
        Assert.AreEqual("pi@host:~$", Line(e, 2).TrimEnd());
    }
}
