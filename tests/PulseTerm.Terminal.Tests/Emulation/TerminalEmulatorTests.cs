using System.Text;
using FluentAssertions;
using PulseTerm.Terminal.Emulation;
using Xunit;

namespace PulseTerm.Terminal.Tests.Emulation;

[Trait("Category", "Emulator")]
public class TerminalEmulatorTests
{
    private static TerminalEmulator New(int cols = 20, int rows = 6, TerminalType type = TerminalType.XtermusColor256)
        => new(cols, rows, type);

    private static void Feed(TerminalEmulator e, string s) => e.Feed(Encoding.UTF8.GetBytes(s));

    private static string Line(TerminalEmulator e, int row) => e.Screen.ActiveLine(row).GetText();

    [Fact]
    public void Print_WritesTextAndAdvancesCursor()
    {
        var e = New();
        Feed(e, "hello");
        Line(e, 0).Should().Be("hello");
        e.CursorX.Should().Be(5);
        e.CursorY.Should().Be(0);
    }

    [Fact]
    public void CarriageReturnLineFeed_MovesToNextRow()
    {
        var e = New();
        Feed(e, "ab\r\ncd");
        Line(e, 0).Should().Be("ab");
        Line(e, 1).Should().Be("cd");
        e.CursorY.Should().Be(1);
        e.CursorX.Should().Be(2);
    }

    [Fact]
    public void Autowrap_WrapsAtRightMargin()
    {
        var e = New(cols: 4, rows: 4);
        Feed(e, "abcdef");
        Line(e, 0).Should().Be("abcd");
        Line(e, 1).Should().Be("ef");
    }

    [Fact]
    public void CursorPosition_CsiH_IsOneBased()
    {
        var e = New();
        Feed(e, "\x1b[2;3HX");
        Line(e, 1).Should().Be("  X");   // row 2, col 3
        e.CursorY.Should().Be(1);
    }

    [Fact]
    public void EraseInLine_FromCursor_ClearsToEnd()
    {
        var e = New();
        Feed(e, "abcdef\x1b[4G\x1b[0K"); // move to col 4, erase to end
        Line(e, 0).Should().Be("abc");
    }

    [Fact]
    public void EraseInDisplay_All_ClearsScreen()
    {
        var e = New();
        Feed(e, "line1\r\nline2\x1b[2J");
        Line(e, 0).Should().BeEmpty();
        Line(e, 1).Should().BeEmpty();
    }

    [Fact]
    public void DeleteChars_ShiftsRemainderLeft()
    {
        var e = New();
        Feed(e, "abcdef\x1b[1G\x1b[2P"); // home, delete 2 chars
        Line(e, 0).Should().Be("cdef");
    }

    [Fact]
    public void InsertChars_ShiftsRemainderRight()
    {
        var e = New();
        Feed(e, "abcdef\x1b[1G\x1b[2@");
        Line(e, 0).Should().Be("  abcdef");
    }

    [Fact]
    public void Sgr_256Color_SetsIndexedForeground()
    {
        var e = New();
        Feed(e, "\x1b[38;5;196mX");
        var cell = e.Screen.GetCell(0, 0);
        cell.Foreground.Kind.Should().Be(TerminalColorKind.Indexed);
        cell.Foreground.Index.Should().Be(196);
    }

    [Fact]
    public void Sgr_Truecolor_SetsRgbForeground()
    {
        var e = New();
        Feed(e, "\x1b[38;2;10;20;30mX");
        var cell = e.Screen.GetCell(0, 0);
        cell.Foreground.Kind.Should().Be(TerminalColorKind.Rgb);
        cell.Foreground.R.Should().Be(10);
        cell.Foreground.G.Should().Be(20);
        cell.Foreground.B.Should().Be(30);
    }

    [Fact]
    public void Sgr_BoldAndBasicColor_AppliesFlags()
    {
        var e = New();
        Feed(e, "\x1b[1;31mX");
        var cell = e.Screen.GetCell(0, 0);
        cell.Flags.Should().HaveFlag(CellFlags.Bold);
        cell.Foreground.Index.Should().Be(1);
    }

    [Fact]
    public void ScrollRegion_ScrollsWithinMargins()
    {
        var e = New(cols: 6, rows: 5);
        Feed(e, "\x1b[2;4r");           // set margins rows 2..4
        Feed(e, "\x1b[2;1Habc\r\n");    // row2 "abc"
        Feed(e, "def\r\n");             // row3
        Feed(e, "ghi\r\n");             // row4
        Feed(e, "jkl");                 // triggers scroll within region
        Line(e, 0).Should().BeEmpty();  // row1 untouched (outside region)
        Line(e, 1).Should().Be("def");
        Line(e, 2).Should().Be("ghi");
        Line(e, 3).Should().Be("jkl");
    }

    [Fact]
    public void FullScreenScroll_PushesToScrollback()
    {
        var e = New(cols: 4, rows: 2);
        Feed(e, "a\r\nb\r\nc");
        e.Screen.ScrollbackCount.Should().BeGreaterThan(0);
        // Newest lines occupy the active screen.
        Line(e, 1).Should().Be("c");
    }

    [Fact]
    public void AlternateScreen_SwitchAndRestore()
    {
        var e = New();
        Feed(e, "main");
        Feed(e, "\x1b[?1049h");         // enter alt
        e.IsAlternateScreen.Should().BeTrue();
        Line(e, 0).Should().BeEmpty();
        Feed(e, "\x1b[?1049l");         // exit alt
        e.IsAlternateScreen.Should().BeFalse();
        Line(e, 0).Should().Be("main");
    }

    [Fact]
    public void PrimaryDeviceAttributes_XtermTypeReplies()
    {
        var e = New(type: TerminalType.XtermusColor256);
        string? reply = null;
        e.Response += b => reply = Encoding.ASCII.GetString(b);
        Feed(e, "\x1b[c");
        reply.Should().Be("\x1b[?64;1;2;6;9;15;18;21;22c");
    }

    [Fact]
    public void PrimaryDeviceAttributes_Vt220Replies()
    {
        var e = New(type: TerminalType.Vt220);
        string? reply = null;
        e.Response += b => reply = Encoding.ASCII.GetString(b);
        Feed(e, "\x1b[c");
        reply.Should().Be("\x1b[?62;1;2;6;7;8;9c");
    }

    [Fact]
    public void CursorPositionReport_Dsr6_ReportsPosition()
    {
        var e = New();
        string? reply = null;
        e.Response += b => reply = Encoding.ASCII.GetString(b);
        Feed(e, "\x1b[3;5H\x1b[6n");
        reply.Should().Be("\x1b[3;5R");
    }

    [Fact]
    public void Utf8WideChar_OccupiesTwoCells()
    {
        var e = New();
        Feed(e, "中X");
        e.Screen.GetCell(0, 0).Rune.Should().Be('中');
        e.Screen.GetCell(1, 0).IsWideTrailing.Should().BeTrue();
        e.Screen.GetCell(2, 0).Rune.Should().Be('X');
        e.CursorX.Should().Be(3);
    }

    [Fact]
    public void DecLineDrawing_MapsToBoxGlyphs()
    {
        var e = New();
        Feed(e, "\x1b(0q\x1b(B");   // select special graphics, print 'q', reset
        e.Screen.GetCell(0, 0).Rune.Should().Be('─');
    }

    [Fact]
    public void Backspace_MovesCursorLeft()
    {
        var e = New();
        Feed(e, "abc\b\b");
        e.CursorX.Should().Be(1);
    }

    [Fact]
    public void InsertMode_ShiftsExistingText()
    {
        var e = New();
        Feed(e, "world\x1b[1G\x1b[4hAB");
        Line(e, 0).Should().Be("ABworld");
    }

    [Fact]
    public void SetEncoding_Gbk_DecodesChineseBytes()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var e = New();
        e.SetEncoding(Encoding.GetEncoding("GBK"));
        // "中文" in GBK.
        e.Feed(new byte[] { 0xD6, 0xD0, 0xCE, 0xC4 });
        e.Screen.GetCell(0, 0).Rune.Should().Be('中');
        e.Screen.GetCell(2, 0).Rune.Should().Be('文');
    }

    [Theory]
    [InlineData("xterm-256color", TerminalType.XtermusColor256)]
    [InlineData("vt220", TerminalType.Vt220)]
    [InlineData("vt52", TerminalType.Vt52)]
    [InlineData("unknown-term", TerminalType.XtermusColor256)]
    public void FromTermName_ParsesKnownProfiles(string term, TerminalType expected)
    {
        TerminalTypeExtensions.FromTermName(term).Should().Be(expected);
    }

    [Fact]
    public void ReverseIndex_ScrollsDownAtTop()
    {
        var e = New(cols: 4, rows: 3);
        Feed(e, "a\r\nb\r\nc");        // rows a,b,c
        Feed(e, "\x1b[1;1H");          // home
        Feed(e, "\x1bM");              // reverse index -> scroll down
        Line(e, 0).Should().BeEmpty();
        Line(e, 1).Should().Be("a");
    }
}
