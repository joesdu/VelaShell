using System.Text;
using VelaShell.Terminal.Emulation;

namespace VelaShell.Terminal.Tests.Emulation;

[TestClass]
[TestCategory("Emulator")]
public class TerminalEmulatorTests
{
    private static TerminalEmulator New(int cols = 20, int rows = 6, TerminalType type = TerminalType.XtermColor256) => new(cols, rows, type);

    private static void Feed(TerminalEmulator e, string s) => e.Feed(Encoding.UTF8.GetBytes(s));

    private static string Line(TerminalEmulator e, int row) => e.Screen.ActiveLine(row).GetText();

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
        string? clipboard = null;
        e.ClipboardWriteRequested += text => clipboard = text;
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("hello 世界"));
        Feed(e, $"\x1b]52;c;{payload}\x07");
        Assert.AreEqual("hello 世界", clipboard);
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
        Feed(e, "\x1bP$q q\x1b\\"); // DECSCUSR:未实现
        Assert.AreEqual("\x1bP0$r\x1b\\", reply);
    }
}
