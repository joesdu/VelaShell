using System.Text;
using VelaShell.Terminal.Emulation;
using VelaShell.Terminal.Semantics;

namespace VelaShell.Terminal.Tests.Emulation;

/// <summary>
/// OSC 133(FinalTerm / FTCS 语义提示符)：把一屏输出切成结构化的命令块
/// <c>[提示符][命令][输出][退出码]</c>。
/// </summary>
/// <remarks>
/// 断言的是<b>标记落在哪一行、退出码记在哪一行</b>。标记与 <see cref="TerminalRow.Timestamp" />
/// 同一套生命周期(滚动按引用迁移、整行擦空即作废、改列宽由 reflow 搬运),这几条各有用例守着 ——
/// 尤其 reflow：漏掉它，拖一下窗口宽度命令块边界就全没了。
/// </remarks>
[TestClass]
[TestCategory("Emulator")]
public class Osc133PromptMarkTests
{
    private static TerminalEmulator New(int cols = 20, int rows = 8) => new(cols, rows, TerminalType.XtermColor256);

    private static void Feed(TerminalEmulator e, string s) => e.Feed(Encoding.UTF8.GetBytes(s));

    private const string A = "\e]133;A\e\\";
    private const string B = "\e]133;B\e\\";
    private const string C = "\e]133;C\e\\";

    private static string D(int code) => $"\e]133;D;{code}\e\\";

    /// <summary>跑一条完整的命令：提示符 → 命令回显 → 换行 → 输出 → 结束。</summary>
    private static void RunCommand(TerminalEmulator e, string command, string output, int exitCode)
    {
        Feed(e, A + "$ " + B + command + "\r\n" + C + output + "\r\n" + D(exitCode));
    }

    private static PromptMark MarkAt(TerminalEmulator e, int abs) => e.Screen.ViewLine(abs).Mark;

    [TestMethod]
    public void FullCycle_MarksPromptAndOutput_AndRecordsExitCode()
    {
        TerminalEmulator e = New();
        RunCommand(e, "ls", "a  b  c", 0);

        Assert.AreEqual(PromptMark.Prompt, MarkAt(e, 0), "第 0 行是提示符行。");
        Assert.AreEqual(PromptMark.Output, MarkAt(e, 1), "换行之后那一行是输出首行。");
        Assert.AreEqual(0, e.Screen.ViewLine(0).ExitCode, "退出码记在提示符行上。");
    }

    [TestMethod]
    public void ExitCode_LandsOnThePromptRow_NotWhereTheCursorIs()
    {
        // 命令结束时光标在输出末尾,而侧栏那个标记画在提示符行 —— 记错地方,
        // 失败标红就会红在一条无关的空行上。
        TerminalEmulator e = New();
        RunCommand(e, "false", "boom\r\nsecond line", 1);

        Assert.AreEqual(1, e.Screen.ViewLine(0).ExitCode);
        for (int abs = 1; abs < e.Screen.TotalRows; abs++)
        {
            Assert.IsNull(e.Screen.ViewLine(abs).ExitCode, $"第 {abs} 行不该背上退出码。");
        }
    }

    [TestMethod]
    public void FailedBlock_IsReportedAsFailed()
    {
        TerminalEmulator e = New();
        RunCommand(e, "false", "err", 1);

        CommandBlock block = CommandBlocks.BlockAt(e.Screen, 0)!.Value;
        Assert.IsTrue(block.Failed);
        Assert.AreEqual(1, block.ExitCode);
    }

    [TestMethod]
    public void SuccessfulBlock_IsNotFailed_AndAnUnfinishedOneIsNotEither()
    {
        // 「还没结束」与「失败了」必须分得开:两者都不是 0,但只有后者该标红。
        TerminalEmulator e = New();
        RunCommand(e, "true", "ok", 0);
        Feed(e, A + "$ " + B + "sleep 100" + "\r\n" + C); // 第二条还在跑,没有 D

        CommandBlock done = CommandBlocks.BlockAt(e.Screen, 0)!.Value;
        Assert.IsFalse(done.Failed);
        Assert.AreEqual(0, done.ExitCode);

        CommandBlock running = CommandBlocks.BlockAt(e.Screen, 2)!.Value;
        Assert.IsFalse(running.Failed, "还在跑的命令不是失败。");
        Assert.IsNull(running.ExitCode);
    }

    [TestMethod]
    public void BlockBoundaries_RunFromPromptToTheRowBeforeTheNextPrompt()
    {
        TerminalEmulator e = New();
        RunCommand(e, "one", "1", 0);   // 行 0 提示符 / 行 1 输出
        RunCommand(e, "two", "2", 0);   // 行 2 提示符 / 行 3 输出

        CommandBlock first = CommandBlocks.BlockAt(e.Screen, 1)!.Value;
        Assert.AreEqual(0, first.PromptRow);
        Assert.AreEqual(1, first.OutputStart);
        Assert.AreEqual(1, first.LastRow, "块止于下一条提示符的上一行。");
        Assert.IsTrue(first.HasOutput);
    }

    [TestMethod]
    public void BlockAt_OnThePromptRowItself_ReturnsThatBlockNotThePreviousOne()
    {
        // 用户点的是这条命令的标记,要的显然是这条命令的输出。
        TerminalEmulator e = New();
        RunCommand(e, "one", "1", 0);
        RunCommand(e, "two", "2", 0);

        Assert.AreEqual(2, CommandBlocks.BlockAt(e.Screen, 2)!.Value.PromptRow);
    }

    [TestMethod]
    public void BlockAt_AboveTheFirstPrompt_ReturnsNull()
    {
        TerminalEmulator e = New();
        Feed(e, "banner line\r\n");
        RunCommand(e, "ls", "a", 0);

        Assert.IsNull(CommandBlocks.BlockAt(e.Screen, 0), "首条提示符之上没有块。");
    }

    [TestMethod]
    public void PrevNextPrompt_WalkTheMarksAndReportMinusOneAtTheEnds()
    {
        TerminalEmulator e = New();
        RunCommand(e, "one", "1", 0);
        RunCommand(e, "two", "2", 0);

        Assert.AreEqual(2, CommandBlocks.NextPrompt(e.Screen, 0));
        Assert.AreEqual(0, CommandBlocks.PreviousPrompt(e.Screen, 2));
        Assert.AreEqual(-1, CommandBlocks.NextPrompt(e.Screen, 2), "最后一条之后没有了。");
        Assert.AreEqual(-1, CommandBlocks.PreviousPrompt(e.Screen, 0), "第一条之前没有了。");
    }

    [TestMethod]
    public void MarkWithoutC_LeavesNoSelectableOutput()
    {
        // 只发 A/B 的集成(或命令还没回车)不该让「选中输出」选到一片空气。
        TerminalEmulator e = New();
        Feed(e, A + "$ " + B + "half typed");

        CommandBlock block = CommandBlocks.BlockAt(e.Screen, 0)!.Value;
        Assert.AreEqual(-1, block.OutputStart);
        Assert.IsFalse(block.HasOutput);
    }

    [TestMethod]
    public void ExitCodeParsing_IgnoresKeyValueParametersAndMissingCodes()
    {
        // D 的尾部可能是退出码、可能只有 aid= 这类键值、也可能什么都不带。
        // 把 aid=7 里的 7 当退出码,就会去标红一条其实成功了的命令。
        TerminalEmulator e = New();
        Feed(e, A + "$ " + B + "x\r\n" + C + "out\r\n" + "\e]133;D;aid=7\e\\");
        Assert.IsNull(e.Screen.ViewLine(0).ExitCode, "aid=7 不是退出码。");

        TerminalEmulator bare = New();
        Feed(bare, A + "$ " + B + "x\r\n" + C + "out\r\n" + "\e]133;D\e\\");
        Assert.IsNull(bare.Screen.ViewLine(0).ExitCode, "D 不带参数 = 没报退出码。");
    }

    [TestMethod]
    public void ParametersOnA_AreIgnored()
    {
        // 多路复用器会带 aid=/cl= 这类键值,按规范忽略。
        TerminalEmulator e = New();
        Feed(e, "\e]133;A;aid=3;cl=m\e\\" + "$ ");

        Assert.AreEqual(PromptMark.Prompt, MarkAt(e, 0));
    }

    [TestMethod]
    public void ErasingTheLine_ClearsTheMark_ButPartialEraseDoesNot()
    {
        // 重绘型 shell 每敲一个字符都 ESC[K 擦到行尾:若"擦一下就掉标记",
        // 提示符行上的标记会在打字过程中不停闪掉。只有整行真空了才算这一行没了。
        TerminalEmulator partial = New();
        Feed(partial, A + "$ prompt");
        Feed(partial, "\e[1;3H\e[K"); // 从第 3 列擦到行尾,行首的 "$ " 还在
        Assert.AreEqual(PromptMark.Prompt, MarkAt(partial, 0), "行还有内容,标记该留着。");

        TerminalEmulator whole = New();
        Feed(whole, A + "$ prompt");
        Feed(whole, "\e[1;1H\e[2K"); // 整行擦除
        Assert.AreEqual(PromptMark.None, MarkAt(whole, 0));
        Assert.IsNull(whole.Screen.ViewLine(0).ExitCode);
    }

    [TestMethod]
    public void Reflow_CarriesMarksAndExitCodesThroughAColumnResize()
    {
        // 拖一下窗口宽度就把命令块边界与失败标记弄丢,是最容易漏掉的一处。
        TerminalEmulator e = New(20, 6);
        RunCommand(e, "one", "1", 0);
        RunCommand(e, "two", "2", 3);

        e.Resize(10, 6);

        var prompts = new List<(int Row, int? Exit)>();
        for (int abs = 0; abs < e.Screen.TotalRows; abs++)
        {
            if (e.Screen.ViewLine(abs).Mark == PromptMark.Prompt)
            {
                prompts.Add((abs, e.Screen.ViewLine(abs).ExitCode));
            }
        }
        Assert.AreEqual(2, prompts.Count, "重排前后提示符条数应当一条不差。");
        Assert.AreEqual(0, prompts[0].Exit);
        Assert.AreEqual(3, prompts[1].Exit, "失败的退出码也要活过重排。");
    }

    [TestMethod]
    public void Reflow_AWrappedPromptStillYieldsExactlyOneMark()
    {
        // 一条逻辑行是一个块边界,不是每段各算一个。铺满会让一条被换行的提示符在侧栏上
        // 冒出好几个标记,跳转也会在同一条提示符上原地跳。
        TerminalEmulator e = New(40, 6);
        Feed(e, A + "$ a-very-long-prompt-that-will-wrap-when-narrow" + B + "\r\n" + C + "out\r\n" + D(0));

        e.Resize(12, 6);

        int marks = 0;
        for (int abs = 0; abs < e.Screen.TotalRows; abs++)
        {
            if (e.Screen.ViewLine(abs).Mark == PromptMark.Prompt)
            {
                marks++;
            }
        }
        Assert.AreEqual(1, marks, "被换行拆成多段的提示符仍然只是一条提示符。");
    }

    [TestMethod]
    public void ResizeDropsTheInFlightPromptRow_SoNoUnrelatedRowGetsStamped()
    {
        // ReflowResize 会回收复用旧行对象。攥着一个已被复用的行,等 D 回来时就会把退出码
        // 盖到一条完全无关的行上 —— 屏幕上表现为某条历史输出凭空标红。
        TerminalEmulator e = New(20, 6);
        Feed(e, A + "$ " + B + "sleep\r\n" + C + "running\r\n");

        e.Resize(10, 6);
        Feed(e, D(1)); // 重排之后才回来的 D

        for (int abs = 0; abs < e.Screen.TotalRows; abs++)
        {
            Assert.IsNull(e.Screen.ViewLine(abs).ExitCode, $"第 {abs} 行不该被盖上退出码。");
        }
    }

    [TestMethod]
    public void MarksSurviveScrollingIntoScrollback()
    {
        TerminalEmulator e = New(20, 4);
        RunCommand(e, "one", "1", 7);
        for (int i = 0; i < 10; i++)
        {
            Feed(e, $"filler {i}\r\n");
        }

        int prompt = CommandBlocks.NextPrompt(e.Screen, -1);
        Assert.IsTrue(prompt >= 0, "滚进回滚区的提示符标记应当还在。");
        Assert.AreEqual(7, e.Screen.ViewLine(prompt).ExitCode);
    }

    [TestMethod]
    public void FullReset_ClearsEverything()
    {
        TerminalEmulator e = New();
        RunCommand(e, "ls", "a", 0);
        Feed(e, "\ec"); // RIS

        Assert.AreEqual(-1, CommandBlocks.NextPrompt(e.Screen, -1));
        Assert.IsFalse(e.HasPromptMarks);
    }

    [TestMethod]
    public void HasPromptMarks_StaysFalseOnAPlainSession_AndLatchesOnce()
    {
        // 这是个只进不退的锁存位:侧栏标记列据它决定显不显示,按帧扫屏幕会让列宽在滚动到
        // 没有标记的历史区时突然收起来,正文跟着左右抖。
        TerminalEmulator e = New();
        Feed(e, "just some output\r\nno shell integration here\r\n");
        Assert.IsFalse(e.HasPromptMarks, "没装集成的会话不该冒出标记列。");

        RunCommand(e, "ls", "a", 0);
        Assert.IsTrue(e.HasPromptMarks);

        for (int i = 0; i < 30; i++)
        {
            Feed(e, $"filler {i}\r\n"); // 把带标记的行全滚出活动屏
        }
        Assert.IsTrue(e.HasPromptMarks, "锁存之后不该因为当前屏上没有标记就退回去。");
    }
}
