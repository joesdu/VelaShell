using System.Text;
using VelaShell.Core.Models;
using VelaShell.Terminal.Emulation;

namespace VelaShell.Terminal.Tests.Emulation;

[TestClass]
[TestCategory("Emulator")]
public class TerminalEmulatorTests
{
    /// <summary>DA2 / DECRQSS / CPR 三条应答,其余探针不应答。</summary>
    private static readonly string[] ExpectedProbeReplies = ["\x1b[>41;360;0c", "\x1bP1$r0 q\x1b\\", "\x1b[1;1R"];

    private static TerminalEmulator New(int cols = 20, int rows = 6, TerminalType type = TerminalType.XtermColor256) => new(cols, rows, type);

    private static void Feed(TerminalEmulator e, string s) => e.Feed(Encoding.UTF8.GetBytes(s));

    private static string Line(TerminalEmulator e, int row) => e.Screen.ActiveLine(row).GetText();

    [TestMethod]
    public void BeginNewSession_ArchivesMainScreenAndResetsInterruptedAlternateScreen()
    {
        TerminalEmulator e = New(rows: 3);
        Feed(e, "history\r\nfirst\r\n\r\n\e]8;;https://example.org\aold\e]8;;\a");
        Feed(e, "\e[2;3r\e[?1049hfull-screen\e[?2004h\e]2;unfinished");
        e.Feed(new byte[] { 0xe2 }); // 旧流在 OSC 载荷的 UTF-8 字符中间断开。
        e.BeginNewSession("new session");
        Feed(e, "ready");

        Assert.IsFalse(e.IsAlternateScreen);
        Assert.IsFalse(e.Modes.BracketedPaste);
        Assert.AreEqual(0, e.Screen.ScrollTop);
        CollectionAssert.AreEqual(new[] { "history", "first", "", "old", "new session", "ready", "" },
            Enumerable.Range(0, e.Screen.TotalRows).Select(i => e.Screen.ViewLine(i).GetText()).ToArray());
        Assert.AreEqual("https://example.org", e.Hyperlinks.UriOf(e.Screen.ViewLine(3).LinkAt(0)));
        Assert.AreEqual(0, e.Screen.ActiveLine(1).LinkAt(0));
        Feed(e, "\ec");
        Assert.AreEqual(0, e.Screen.ScrollbackCount); // 真正的 RIS 仍清空历史。
    }

    [TestMethod]
    public void BeginNewSession_WithoutSeparatorClearsHistoryAndStartsAtFirstCell()
    {
        TerminalEmulator e = New(rows: 2);
        Feed(e, "history\r\nold\r\nlast\e[?1049happ\e]2;unfinished");
        e.BeginNewSession(null);
        Feed(e, "ready");
        Assert.IsFalse(e.IsAlternateScreen);
        Assert.AreEqual(0, e.Screen.ScrollbackCount);
        Assert.AreEqual("ready", Line(e, 0));
        Assert.AreEqual("", Line(e, 1));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public void BeginNewSession_RespectsLimitWithoutArchivingUnusedBottomRows(int limit)
    {
        var e = new TerminalEmulator(columns: 12, rows: 5, scrollback: limit);
        Feed(e, "older\r\nlast\e[H"); // 光标不一定在最后一行内容之后。
        e.BeginNewSession("new session");
        Assert.AreEqual(limit, e.Screen.ScrollbackCount);
        Assert.AreEqual(limit == 0 ? "new session" : "last", e.Screen.ViewLine(0).GetText());
    }

    [TestMethod]
    public void Print_WritesTextAndAdvancesCursor()
    {
        TerminalEmulator e = New();
        Feed(e, "hello");
        Assert.AreEqual("hello", Line(e, 0));
        Assert.AreEqual(5, e.CursorX);
        Assert.AreEqual(0, e.CursorY);
    }

    [TestMethod]
    public void CarriageReturnLineFeed_MovesToNextRow()
    {
        TerminalEmulator e = New();
        Feed(e, "ab\r\ncd");
        Assert.AreEqual("ab", Line(e, 0));
        Assert.AreEqual("cd", Line(e, 1));
        Assert.AreEqual(1, e.CursorY);
        Assert.AreEqual(2, e.CursorX);
    }

    [TestMethod]
    public void Autowrap_WrapsAtRightMargin()
    {
        TerminalEmulator e = New(4, 4);
        Feed(e, "abcdef");
        Assert.AreEqual("abcd", Line(e, 0));
        Assert.AreEqual("ef", Line(e, 1));
    }

    [TestMethod]
    public void CursorPosition_CsiH_IsOneBased()
    {
        TerminalEmulator e = New();
        Feed(e, "\x1b[2;3HX");
        Assert.AreEqual("  X", Line(e, 1)); // row 2, col 3
        Assert.AreEqual(1, e.CursorY);
    }

    [TestMethod]
    public void EraseInLine_FromCursor_ClearsToEnd()
    {
        TerminalEmulator e = New();
        Feed(e, "abcdef\x1b[4G\x1b[0K"); // move to col 4, erase to end
        Assert.AreEqual("abc", Line(e, 0));
    }

    [TestMethod]
    public void EraseInDisplay_All_ClearsScreen()
    {
        TerminalEmulator e = New();
        Feed(e, "line1\r\nline2\x1b[2J");
        Assert.AreEqual(string.Empty, Line(e, 0));
        Assert.AreEqual(string.Empty, Line(e, 1));
    }

    [TestMethod]
    public void DeleteChars_ShiftsRemainderLeft()
    {
        TerminalEmulator e = New();
        Feed(e, "abcdef\x1b[1G\x1b[2P"); // home, delete 2 chars
        Assert.AreEqual("cdef", Line(e, 0));
    }

    [TestMethod]
    public void InsertChars_ShiftsRemainderRight()
    {
        TerminalEmulator e = New();
        Feed(e, "abcdef\x1b[1G\x1b[2@");
        Assert.AreEqual("  abcdef", Line(e, 0));
    }

    [TestMethod]
    public void Sgr_256Color_SetsIndexedForeground()
    {
        TerminalEmulator e = New();
        Feed(e, "\x1b[38;5;196mX");
        TerminalCell cell = e.Screen.GetCell(0, 0);
        Assert.AreEqual(TerminalColorKind.Indexed, cell.Foreground.Kind);
        Assert.AreEqual(196, cell.Foreground.Index);
    }

    [TestMethod]
    public void Sgr_Truecolor_SetsRgbForeground()
    {
        TerminalEmulator e = New();
        Feed(e, "\x1b[38;2;10;20;30mX");
        TerminalCell cell = e.Screen.GetCell(0, 0);
        Assert.AreEqual(TerminalColorKind.Rgb, cell.Foreground.Kind);
        Assert.AreEqual(10, cell.Foreground.R);
        Assert.AreEqual(20, cell.Foreground.G);
        Assert.AreEqual(30, cell.Foreground.B);
    }

    [TestMethod]
    public void Sgr_BoldAndBasicColor_AppliesFlags()
    {
        TerminalEmulator e = New();
        Feed(e, "\x1b[1;31mX");
        TerminalCell cell = e.Screen.GetCell(0, 0);
        Assert.IsTrue(cell.Flags.HasFlag(CellFlags.Bold));
        Assert.AreEqual(1, cell.Foreground.Index);
    }

    [TestMethod]
    public void ScrollRegion_ScrollsWithinMargins()
    {
        TerminalEmulator e = New(6, 5);
        Feed(e, "\x1b[2;4r");                      // set margins rows 2..4
        Feed(e, "\x1b[2;1Habc\r\n");               // row2 "abc"
        Feed(e, "def\r\n");                        // row3
        Feed(e, "ghi\r\n");                        // row4
        Feed(e, "jkl");                            // triggers scroll within region
        Assert.AreEqual(string.Empty, Line(e, 0)); // row1 untouched (outside region)
        Assert.AreEqual("def", Line(e, 1));
        Assert.AreEqual("ghi", Line(e, 2));
        Assert.AreEqual("jkl", Line(e, 3));
    }

    [TestMethod]
    public void FullScreenScroll_PushesToScrollback()
    {
        TerminalEmulator e = New(4, 2);
        Feed(e, "a\r\nb\r\nc");
        Assert.IsGreaterThan(0, e.Screen.ScrollbackCount);
        // Newest lines occupy the active screen.
        Assert.AreEqual("c", Line(e, 1));
    }

    /// <summary>
    /// 区域上边界在首行、底部钉着一行状态(apt 的进度条就是这个布局,#634):
    /// 从区域顶部滚出去的行照样进历史,状态行原地不动、也不进历史。
    /// </summary>
    [TestMethod]
    public void TopAnchoredScrollRegion_PushesRetiredLinesToScrollback()
    {
        TerminalEmulator e = New(20, 5);
        // apt(Dpkg::Progress-Fancy)的开场:存光标、把区域设成首行到倒数第二行、恢复光标,
        // 之后每次刷新都跳到最后一行画进度条再跳回来。
        Feed(e, "\n\e7\e[0;4r\e8\e[1A");
        Feed(e, "\e7\e[5;0fProgress: [ 50%]\e8");
        for (int i = 0; i < 10; i++)
        {
            Feed(e, $"L{i}\r\n");
        }

        Assert.AreEqual(7, e.Screen.ScrollbackCount, "L0..L6 都从区域顶部滚出去了,一行都不该丢。");
        for (int i = 0; i < 7; i++)
        {
            Assert.AreEqual($"L{i}", e.Screen.ViewLine(i).GetText());
        }
        Assert.AreEqual("L7", Line(e, 0));
        Assert.AreEqual("L9", Line(e, 2));
        Assert.AreEqual("Progress: [ 50%]", Line(e, 4), "区域下方的状态行不随区域滚动。");
    }

    [TestMethod]
    public void ScrollRegionBelowFixedRows_KeepsRetiredLinesOutOfScrollback()
    {
        TerminalEmulator e = New(6, 5);
        Feed(e, "head\e[2;4r\e[2;1H");
        for (int i = 0; i < 10; i++)
        {
            Feed(e, $"L{i}\r\n");
        }

        // 上方有固定行时,滚出去的行夹在固定行和区域之间,历史里没有它的位置。
        Assert.AreEqual(0, e.Screen.ScrollbackCount);
        Assert.AreEqual("head", Line(e, 0));
    }

    [TestMethod]
    public void TopAnchoredScrollRegion_OnTheAlternateScreen_StaysOutOfScrollback()
    {
        TerminalEmulator e = New(10, 5);
        Feed(e, "a\r\nb\r\nc\r\nd\r\ne\r\nf\r\n");
        int mainHistory = e.Screen.ScrollbackCount;
        Assert.IsGreaterThan(0, mainHistory, "样本要先在主屏攒出历史。");

        // vim 一类程序在备用屏上设区域(状态栏、分屏)再滚动。
        Feed(e, "\e[?1049h\e[1;4r");
        for (int i = 0; i < 10; i++)
        {
            Feed(e, $"V{i}\r\n");
        }
        Assert.AreEqual(0, e.Screen.ScrollbackCount, "备用屏的退休行不该进历史。");

        Feed(e, "\e[?1049l");
        Assert.AreEqual(mainHistory, e.Screen.ScrollbackCount, "主屏历史不该被备用屏的滚动改动。");
    }

    [TestMethod]
    public void AlternateScreen_SwitchAndRestore()
    {
        TerminalEmulator e = New();
        Feed(e, "main");
        Feed(e, "\x1b[?1049h"); // enter alt
        Assert.IsTrue(e.IsAlternateScreen);
        Assert.AreEqual(string.Empty, Line(e, 0));
        Feed(e, "\x1b[?1049l"); // exit alt
        Assert.IsFalse(e.IsAlternateScreen);
        Assert.AreEqual("main", Line(e, 0));
    }

    [TestMethod]
    public void PrimaryDeviceAttributes_XtermTypeReplies()
    {
        TerminalEmulator e = New(type: TerminalType.XtermColor256);
        string? reply = null;
        e.Response += b => reply = Encoding.ASCII.GetString(b);
        Feed(e, "\x1b[c");
        Assert.AreEqual("\x1b[?64;1;2;6;9;15;18;21;22c", reply);
    }

    [TestMethod]
    public void PrimaryDeviceAttributes_Vt220Replies()
    {
        TerminalEmulator e = New(type: TerminalType.Vt220);
        string? reply = null;
        e.Response += b => reply = Encoding.ASCII.GetString(b);
        Feed(e, "\x1b[c");
        Assert.AreEqual("\x1b[?62;1;2;6;7;8;9c", reply);
    }

    [TestMethod]
    public void CursorPositionReport_Dsr6_ReportsPosition()
    {
        TerminalEmulator e = New();
        string? reply = null;
        e.Response += b => reply = Encoding.ASCII.GetString(b);
        Feed(e, "\x1b[3;5H\x1b[6n");
        Assert.AreEqual("\x1b[3;5R", reply);
    }

    [TestMethod]
    public void Utf8WideChar_OccupiesTwoCells()
    {
        TerminalEmulator e = New();
        Feed(e, "中X");
        Assert.AreEqual('中', e.Screen.GetCell(0, 0).Rune);
        Assert.IsTrue(e.Screen.GetCell(1, 0).IsWideTrailing);
        Assert.AreEqual('X', e.Screen.GetCell(2, 0).Rune);
        Assert.AreEqual(3, e.CursorX);
    }

    [TestMethod]
    public void DecLineDrawing_MapsToBoxGlyphs()
    {
        TerminalEmulator e = New();
        Feed(e, "\x1b(0q\x1b(B"); // select special graphics, print 'q', reset
        Assert.AreEqual('─', e.Screen.GetCell(0, 0).Rune);
    }

    [TestMethod]
    public void Backspace_MovesCursorLeft()
    {
        TerminalEmulator e = New();
        Feed(e, "abc\b\b");
        Assert.AreEqual(1, e.CursorX);
    }

    [TestMethod]
    public void InsertMode_ShiftsExistingText()
    {
        TerminalEmulator e = New();
        Feed(e, "world\x1b[1G\x1b[4hAB");
        Assert.AreEqual("ABworld", Line(e, 0));
    }

    [TestMethod]
    public void SetEncoding_Gbk_DecodesChineseBytes()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        TerminalEmulator e = New();
        e.SetEncoding(Encoding.GetEncoding("GBK"));
        // "中文" in GBK.
        e.Feed([0xD6, 0xD0, 0xCE, 0xC4]);
        Assert.AreEqual('中', e.Screen.GetCell(0, 0).Rune);
        Assert.AreEqual('文', e.Screen.GetCell(2, 0).Rune);
    }

    [TestMethod]
    [DataRow("xterm-256color", TerminalType.XtermColor256)]
    [DataRow("vt220", TerminalType.Vt220)]
    [DataRow("vt52", TerminalType.Vt52)]
    [DataRow("unknown-term", TerminalType.XtermColor256)]
    public void FromTermName_ParsesKnownProfiles(string term, TerminalType expected) => Assert.AreEqual(expected, TerminalTypeExtensions.FromTermName(term));

    [TestMethod]
    public void EveryOfferedTermNameIsOneTheEmulatorActuallyKnows()
    {
        // 下拉里的档位若仿真器不认识,FromTermName 会把它静默当成 xterm-256color ——
        // 用户选了 A、发出去的是 B,而界面上看不出任何异样("linux"就这么在连接配置里躺过一阵)。
        foreach (string term in TerminalTypes.All)
        {
            Assert.AreEqual(term, TerminalTypeExtensions.FromTermName(term).ToTermName(), $"「{term}」不在仿真器认识的档位里。");
        }
    }

    [TestMethod]
    public void ReverseIndex_ScrollsDownAtTop()
    {
        TerminalEmulator e = New(4, 3);
        Feed(e, "a\r\nb\r\nc"); // rows a,b,c
        Feed(e, "\x1b[1;1H");   // home
        Feed(e, "\x1bM");       // reverse index -> scroll down
        Assert.AreEqual(string.Empty, Line(e, 0));
        Assert.AreEqual("a", Line(e, 1));
    }

    // ---- OSC 52(远端写剪贴板) ----

    [TestMethod]
    public void Osc52_SetClipboard_RaisesClipboardWriteRequested()
    {
        TerminalEmulator e = New();
        e.AllowOsc52ClipboardWrite = true;
        string? clipboard = null;
        e.ClipboardWriteRequested += text => clipboard = text;
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("hello 世界"));
        Feed(e, $"\x1b]52;c;{payload}\x07");
        Assert.AreEqual("hello 世界", clipboard);
    }

    [TestMethod]
    public void Osc52_IsIgnoredByDefault_ForSecurity()
    {
        TerminalEmulator e = New();
        string? clipboard = null;
        e.ClipboardWriteRequested += text => clipboard = text;
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("poisoned"));

        Feed(e, $"\x1b]52;c;{payload}\x07");

        Assert.IsNull(clipboard);
    }

    [TestMethod]
    public void Osc52_Query_IsIgnored_ForSecurity()
    {
        TerminalEmulator e = New();
        string? clipboard = null;
        string? reply = null;
        e.ClipboardWriteRequested += text => clipboard = text;
        e.Response += b => reply = Encoding.ASCII.GetString(b);
        Feed(e, "\x1b]52;c;?\x07");
        Assert.IsNull(clipboard);
        Assert.IsNull(reply); // 不回读本地剪贴板
    }

    [TestMethod]
    public void Osc52_InvalidBase64_IsIgnored()
    {
        TerminalEmulator e = New();
        string? clipboard = null;
        e.ClipboardWriteRequested += text => clipboard = text;
        Feed(e, "\x1b]52;c;not-base64!!\x07");
        Assert.IsNull(clipboard);
    }

    [TestMethod]
    public void Osc_TerminatedByStringTerminator_StillDispatches()
    {
        // 回归:全局“ESC 重启序列”曾把 ST(ESC \)结尾的 OSC/DCS 整段丢弃,
        // 只有 BEL 结尾能用(修于 2026-07-09)。
        TerminalEmulator e = New();
        string? title = null;
        e.TitleChanged += t => title = t;
        Feed(e, "\x1b]0;my-title\x1b\\");
        Assert.AreEqual("my-title", title);
    }

    // ---- DECRQSS(DCS $ q Pt ST) ----

    [TestMethod]
    public void Decrqss_Sgr_ReportsCurrentPen()
    {
        TerminalEmulator e = New();
        string? reply = null;
        e.Response += b => reply = Encoding.ASCII.GetString(b);
        Feed(e, "\x1b[1;31m");     // bold + red
        Feed(e, "\x1bP$qm\x1b\\"); // DECRQSS "m"
        Assert.AreEqual("\x1bP1$r0;1;31m\x1b\\", reply);
    }

    [TestMethod]
    public void Decrqss_Decstbm_ReportsScrollRegion()
    {
        TerminalEmulator e = New();
        string? reply = null;
        e.Response += b => reply = Encoding.ASCII.GetString(b);
        Feed(e, "\x1b[2;4r");      // margins rows 2..4
        Feed(e, "\x1bP$qr\x1b\\"); // DECRQSS "r"
        Assert.AreEqual("\x1bP1$r2;4r\x1b\\", reply);
    }

    [TestMethod]
    public void Decrqss_UnknownRequest_ReportsInvalid()
    {
        TerminalEmulator e = New();
        string? reply = null;
        e.Response += b => reply = Encoding.ASCII.GetString(b);
        Feed(e, "\x1bP$q\"p\x1b\\"); // DECSCL:未实现
        Assert.AreEqual("\x1bP0$r\x1b\\", reply);
    }

    // ---- 回归:issue #112「打开 vim 会自动输入奇怪字符」 ----

    [TestMethod]
    public void Decrqss_Decscusr_ReportsCursorStyle()
    {
        // vim 启动时必发 t_RS = DCS $ q SP q ST,且只认 "DCS 1 $ r <一位数字> SP q ST"。
        // 曾经回 "DCS 0 $ r ST"(无效请求),vim 匹配失败后把整段当键入:
        // Esc P 0 $ r … → E353 报错 + 响铃 + 光标跳到行尾。
        TerminalEmulator e = New();
        string? reply = null;
        e.Response += b => reply = Encoding.ASCII.GetString(b);
        Feed(e, "\x1bP$q q\x1b\\");
        Assert.AreEqual("\x1bP1$r0 q\x1b\\", reply);
    }

    [TestMethod]
    public void Decscusr_SetStyle_IsEchoedByDecrqss()
    {
        TerminalEmulator e = New();
        string? reply = null;
        e.Response += b => reply = Encoding.ASCII.GetString(b);
        Feed(e, "\x1b[4 q");       // DECSCUSR:稳定下划线
        Feed(e, "\x1bP$q q\x1b\\");
        Assert.AreEqual("\x1bP1$r4 q\x1b\\", reply);
    }

    [TestMethod]
    public void Decscusr_OutOfRangeStyle_IsClampedToOneDigit()
    {
        // 应答必须是单个数字,否则 vim 又会解析失败并把应答当键入。
        TerminalEmulator e = New();
        string? reply = null;
        e.Response += b => reply = Encoding.ASCII.GetString(b);
        Feed(e, "\x1b[42 q");
        Feed(e, "\x1bP$q q\x1b\\");
        Assert.AreEqual("\x1bP1$r6 q\x1b\\", reply);
    }

    [TestMethod]
    public void ModifyOtherKeys_IsNotTreatedAsSgr()
    {
        // vim 启动/退出时发 "CSI > 4 ; 2 m" / "CSI > 4 ; m":私有前缀的序列不是 SGR,
        // 曾被裸分派成 SGR 4;2(下划线 + 变暗),整屏跟着变样。
        TerminalEmulator e = New();
        Feed(e, "\x1b[>4;2mX");
        Assert.AreEqual(CellFlags.None, e.Screen.GetCell(0, 0).Flags);
    }

    [TestMethod]
    public void CsiWithIntermediate_IsNotTreatedAsPlainFinal()
    {
        // vim 的 xterm 兼容性探针:未知 DCS 与带中间字节的 CSI 都应被静默忽略,
        // 光标不许动、屏幕不许出字符(vim 靠随后的 CPR 判定终端是否 xterm 兼容)。
        TerminalEmulator e = New();
        Feed(e, "\x1b[3;5H");
        Feed(e, "\x1bPzz\x1b\\\x1b[0%m");
        Assert.AreEqual(4, e.CursorX);
        Assert.AreEqual(2, e.CursorY);
        Assert.AreEqual(string.Empty, Line(e, 2));
    }

    [TestMethod]
    public void VimStartupHandshake_OnlyRepliesWithSequencesVimCanParse()
    {
        // vim 启动时的整套探询(我们回报为 xterm 补丁级 360,于是 vim 会问光标形状)。
        // 每一条应答都必须是 vim 认得的形状:任何一条对不上,vim 都会把它当键入吞进去。
        TerminalEmulator e = New();
        var replies = new List<string>();
        e.Response += b => replies.Add(Encoding.ASCII.GetString(b));
        Feed(e, "\x1b[>4;2m");           // modifyOtherKeys:不应答
        Feed(e, "\x1b[>c");              // DA2
        Feed(e, "\x1bP+q544e\x1b\\");    // XTGETTCAP:不应答
        Feed(e, "\x1b[?12$p");           // DECRQM 光标闪烁:不应答
        Feed(e, "\x1bP$q q\x1b\\");      // DECRQSS 光标形状
        Feed(e, "\x1b]11;?\x1b\\");      // OSC 11 背景色:不应答
        Feed(e, "\x1bPzz\x1b\\\x1b[0%m"); // xterm 兼容性探针:不应答、不动光标
        Feed(e, "\x1b[6n");              // CPR
        Assert.AreSequenceEqual(ExpectedProbeReplies, replies);
    }

    [TestMethod]
    public void KittyKeyboardProtocol_DoesNotRestoreCursor()
    {
        // "CSI = 0 ; 1 u" / "CSI > 1 u" 是 kitty 键盘协议,不是 ANSI.SYS 的恢复光标;
        // 裸按 final 分派会让光标跳回上次保存的位置。
        TerminalEmulator e = New();
        Feed(e, "\x1b[1;1H\x1b[s");  // 在 (0,0) 保存
        Feed(e, "\x1b[5;7H");        // 移到别处
        Feed(e, "\x1b[=0;1u\x1b[>1u");
        Assert.AreEqual(6, e.CursorX);
        Assert.AreEqual(4, e.CursorY);
    }
}
