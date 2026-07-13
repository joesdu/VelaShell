using System.Text;

namespace VelaShell.Terminal.Emulation;

/// <summary>
/// The terminal "brain": consumes parsed escape-sequence events from <see cref="VtParser" />
/// and applies them to a <see cref="TerminalScreen" />. Owns the current graphic rendition
/// (pen), character sets, terminal modes, tab stops and the saved-cursor state, and produces
/// host-bound replies (Device Attributes, cursor reports, etc.) via <see cref="Response" />.
/// Behavior is gated by the active <see cref="TerminalType" /> so the same engine can emulate
/// anything from a VT52 up to xterm-256color.
/// </summary>
public sealed class TerminalEmulator : IVtActions
{
    /// <summary>OSC 52 载荷上限(base64 解码后),防远端滥发撑爆剪贴板。</summary>
    private const int MaxOsc52Bytes = 1024 * 1024;

    // Character sets: G0..G3 designations, GL/GR invocation, single shift.
    private readonly bool[] _decGraphics = new bool[4]; // true => DEC special graphics
    private readonly TerminalScreen _mainScreen;
    private readonly VtParser _parser;
    private readonly Utf8Sink _utf8 = new();

    // Separate save slot for the alternate-screen switch (DECSET 1049). Keeping it distinct from
    // _saved is what xterm does: an app running in the alt screen (e.g. nano) uses DECSC/DECRC
    // freely without clobbering the main-screen cursor that must be restored on exit (#14b).
    private SavedCursor? _altSaved;
    private TerminalScreen? _altScreen; // alternate buffer (no scrollback)
    private TerminalColor _bg = TerminalColor.Default;

    // Current pen
    private TerminalColor _fg = TerminalColor.Default;
    private CellFlags _flags = CellFlags.None;
    private int _gl; // active GL set index

    private bool _pendingWrap; // deferred autowrap at end of line
    private DateTime _feedTimestamp = DateTime.Now; // 当前 Feed 到达时刻,用于给写入的行盖时间戳(行号侧栏)

    // Saved cursor (DECSC / DECRC, CSI s/u, DECSET 1048)
    private SavedCursor? _saved;

    private int _singleShift = -1;
    private bool[] _tabStops;

    /// <summary>Creates an emulator with the given screen geometry, terminal type and scrollback capacity.</summary>
    public TerminalEmulator(int columns = 80, int rows = 24, TerminalType type = TerminalType.XtermColor256, int scrollback = 10_000)
    {
        Type = type;
        Palette = new();
        Modes = new();
        Screen = new(columns, rows, scrollback);
        _mainScreen = Screen;
        _tabStops = BuildDefaultTabs(columns);
        _parser = new(this) { Vt52Mode = type == TerminalType.Vt52 };
    }

    /// <summary>The terminal type currently being emulated; gates feature behavior.</summary>
    public TerminalType Type { get; private set; }

    /// <summary>The active terminal modes (autowrap, origin, mouse tracking, etc.).</summary>
    public TerminalModes Modes { get; }

    /// <summary>The color palette used to resolve indexed colors to concrete RGB values.</summary>
    public TerminalPalette Palette { get; }

    /// <summary>The screen buffer currently in effect (main or alternate).</summary>
    public TerminalScreen Screen { get; private set; }

    /// <summary>Number of columns in the current screen.</summary>
    public int Columns => Screen.Columns;

    /// <summary>Number of rows in the current screen.</summary>
    public int Rows => Screen.Rows;

    /// <summary>Current cursor column (0-based).</summary>
    public int CursorX => Screen.CursorX;

    /// <summary>Current cursor row (0-based).</summary>
    public int CursorY => Screen.CursorY;

    /// <summary>True while the alternate screen buffer is active (DECSET 1047/1049).</summary>
    public bool IsAlternateScreen { get; private set; }

    // ---- IVtActions: printing ----------------------------------------------

    /// <summary>Writes a printable character at the cursor, handling charset translation, wide/combining glyphs and autowrap.</summary>
    public void Print(int rune)
    {
        // Apply active charset translation.
        int setIndex = _singleShift >= 0 ? _singleShift : _gl;
        _singleShift = -1;
        if (_decGraphics[setIndex])
        {
            rune = Charsets.MapDecSpecial(rune);
        }
        int width = CharWidth.Of(rune);

        // Combining marks attach to the previous cell without advancing the cursor.
        if (width == 0)
        {
            AttachCombining(rune);
            return;
        }
        if (_pendingWrap)
        {
            Screen.ActiveLine(Screen.CursorY).Wrapped = true;
            CarriageReturnLineFeed();
            _pendingWrap = false;
        }

        // Autowrap check for wide chars that won't fit.
        if (width == 2 && Screen.CursorX == Screen.Columns - 1)
        {
            if (Modes.AutoWrap)
            {
                Screen.ActiveLine(Screen.CursorY).Wrapped = true;
                CarriageReturnLineFeed();
            }
            else
            {
                Screen.SetCursorX(Screen.Columns - 2);
            }
        }
        if (Modes.InsertMode)
        {
            Screen.InsertChars(width, Blank());
        }
        var cell = new TerminalCell
        {
            Rune = rune,
            Foreground = _fg,
            Background = _bg,
            Flags = _flags
        };
        Screen.SetCell(Screen.CursorX, Screen.CursorY, cell);
        // 行时间戳取「本次 Feed 到达时刻」——按 chunk 取一次,避免逐字符 DateTime.Now;
        // 同一行被多次写入时以最后一次为准(= 该行最后收到输出的时间)。
        Screen.ActiveLine(Screen.CursorY).Timestamp = _feedTimestamp;
        if (width == 2)
        {
            TerminalCell trailing = cell;
            trailing.Rune = 0;
            trailing.Flags |= CellFlags.WideTrailing;
            Screen.SetCell(Screen.CursorX + 1, Screen.CursorY, trailing);
        }
        if (Screen.CursorX + width >= Screen.Columns)
        {
            if (Modes.AutoWrap)
            {
                Screen.SetCursorX(Screen.Columns - 1);
                _pendingWrap = true;
            }
            else
            {
                Screen.SetCursorX(Screen.Columns - 1);
            }
        }
        else
        {
            Screen.SetCursorX(Screen.CursorX + width);
        }
    }

    // ---- IVtActions: C0 controls -------------------------------------------

    /// <summary>Executes a C0 control character (BEL, BS, HT, LF/VT/FF, CR, SO/SI).</summary>
    public void Execute(char control)
    {
        switch (control)
        {
            case '\a': // BEL
                Bell?.Invoke();
                break;
            case '\b': // BS
                if (_pendingWrap)
                {
                    _pendingWrap = false;
                }
                else if (Screen.CursorX > 0)
                {
                    Screen.SetCursorX(Screen.CursorX - 1);
                }
                break;
            case '\t': // HT
                HorizontalTab();
                break;
            case '\n': // LF
            case '\v': // VT
            case '\f': // FF
                _pendingWrap = false;
                IndexAndStamp();
                if (Modes.NewLineMode)
                {
                    Screen.SetCursorX(0);
                }
                break;
            case '\r': // CR
                _pendingWrap = false;
                Screen.SetCursorX(0);
                break;
            case '\x0E': // SO -> invoke G1 into GL
                _gl = 1;
                break;
            case '\x0F': // SI -> invoke G0 into GL
                _gl = 0;
                break;
        }
    }

    // ---- IVtActions: ESC ----------------------------------------------------

    /// <summary>Dispatches an ESC sequence (charset designation, IND/RI/NEL, DECSC/DECRC, RIS, etc.).</summary>
    public void EscDispatch(string intermediates, char final)
    {
        if (Type == TerminalType.Vt52 && intermediates.Length == 0)
        {
            EscDispatchVt52(final);
            return;
        }
        if (intermediates.Length > 0)
        {
            char inter = intermediates[0];
            switch (inter)
            {
                case '(' or ')' or '*' or '+': // designate G0..G3
                    int g = inter switch { '(' => 0, ')' => 1, '*' => 2, _ => 3 };
                    _decGraphics[g] = final == '0';
                    return;
                case '#':
                    if (final == '8')
                    {
                        FillScreenWithE(); // DECALN
                    }
                    return;
            }
            return;
        }
        switch (final)
        {
            case 'D':
                IndexAndStamp();
                break; // IND
            case 'M':
                Screen.ReverseIndex(Blank());
                break; // RI
            case 'E':
                Screen.SetCursorX(0);
                IndexAndStamp();
                break; // NEL
            case 'H':
                _tabStops[Math.Clamp(Screen.CursorX, 0, _tabStops.Length - 1)] = true;
                break; // HTS
            case '7':
                SaveCursor();
                break; // DECSC
            case '8':
                RestoreCursor();
                break; // DECRC
            case '=':
                Modes.ApplicationKeypad = true;
                break; // DECCKPAM
            case '>':
                Modes.ApplicationKeypad = false;
                break; // DECKPNM
            case 'c':
                FullReset();
                break; // RIS
            case '\\':
                break; // ST (string terminator)
            case 'n':
                _gl = 2;
                break; // LS2
            case 'o':
                _gl = 3;
                break; // LS3
        }
    }

    // ---- IVtActions: CSI ----------------------------------------------------

    /// <summary>Dispatches a CSI sequence (cursor movement, erase, insert/delete, SGR, mode set/reset, reports, etc.).</summary>
    public void CsiDispatch(char prefix, IReadOnlyList<int> p, string intermediates, char final)
    {
        if (prefix == '?')
        {
            HandlePrivateMode(p, final);
            return;
        }
        switch (intermediates)
        {
            // Intermediate '!' + 'p' => DECSTR soft reset.
            case "!" when final == 'p':
                SoftReset();
                return;
            case " " when final == 'q':
                return; // DECSCUSR cursor style (accepted, style handled in UI)
        }
        switch (final)
        {
            case '@':
                Screen.InsertChars(P(0), Blank());
                break; // ICH
            case 'A':
                MoveCursor(0, -P(0));
                break; // CUU
            case 'B':
                MoveCursor(0, P(0));
                break; // CUD
            case 'C':
                MoveCursor(P(0), 0);
                break; // CUF
            case 'D':
                MoveCursor(-P(0), 0);
                break; // CUB
            case 'E':
                Screen.SetCursorX(0);
                MoveCursor(0, P(0));
                break; // CNL
            case 'F':
                Screen.SetCursorX(0);
                MoveCursor(0, -P(0));
                break; // CPL
            case '`':
            case 'G':
                SetCursorColumn(P(0) - 1);
                break; // CHA / HPA
            case 'd':
                SetCursorRow(P(0) - 1);
                break; // VPA
            case 'H':
            case 'f':
                CursorPosition(P(0) - 1, P(1) - 1);
                break; // CUP / HVP
            case 'I':
                TabForward(P(0));
                break; // CHT
            case 'Z':
                TabBackward(P(0));
                break; // CBT
            case 'J':
                Screen.EraseInDisplay(P0(0), Blank());
                _pendingWrap = false;
                break; // ED
            case 'K':
                Screen.EraseInLine(P0(0), Blank());
                _pendingWrap = false;
                break; // EL
            case 'L':
                Screen.InsertLines(P(0), Blank());
                break; // IL
            case 'M':
                Screen.DeleteLines(P(0), Blank());
                break; // DL
            case 'P':
                Screen.DeleteChars(P(0), Blank());
                break; // DCH
            case 'X':
                Screen.EraseChars(P(0), Blank());
                break; // ECH
            case 'S':
                Screen.ScrollUp(P(0), Blank());
                break; // SU
            case 'T':
                Screen.ScrollDown(P(0), Blank());
                break; // SD
            case 'm':
                ApplySgr(p);
                break; // SGR
            case 'r':
                SetScrollRegion(p);
                break; // DECSTBM
            case 'h':
                SetAnsiMode(p, true);
                break;
            case 'l':
                SetAnsiMode(p, false);
                break;
            case 'g':
                ClearTabs(P0(0));
                break; // TBC
            case 'c':
                DeviceAttributes(prefix);
                break; // DA
            case 'n':
                DeviceStatusReport(P0(0));
                break; // DSR
            case 's':
                SaveCursor();
                break; // ANSI.SYS save
            case 'u':
                RestoreCursor();
                break; // ANSI.SYS restore
            case 't':
                break; // window ops (ignored)
        }
        return;

        int P(int index, int def = 1)
        {
            if (index >= p.Count)
            {
                return def;
            }
            int v = p[index];
            return v == 0 ? def : v;
        }

        int P0(int index) => index < p.Count ? p[index] : 0;
    }

    /// <summary>Dispatches an OSC command (window title changes, OSC 52 clipboard writes).</summary>
    public void OscDispatch(IReadOnlyList<string> p)
    {
        if (p.Count == 0)
        {
            return;
        }
        if (!int.TryParse(p[0], out int cmd))
        {
            return;
        }
        switch (cmd)
        {
            case 0:
            case 2:
                if (p.Count > 1)
                {
                    TitleChanged?.Invoke(p[1]);
                }
                break;
            case 52:
                // 形如 52;c;<base64>(c/p/s… 选区种类一律当系统剪贴板处理)。
                if (p.Count > 2 && p[2] is { Length: > 0 } payload && payload != "?" && payload.Length <= MaxOsc52Bytes / 3 * 4 + 4)
                {
                    try
                    {
                        string text = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                        if (text.Length > 0)
                        {
                            ClipboardWriteRequested?.Invoke(text);
                        }
                    }
                    catch (FormatException)
                    {
                        // 非法 base64:按规范静默忽略。
                    }
                }
                break;
                // 4 (palette), 8 (hyperlink) intentionally accepted-and-ignored for now.
        }
    }

    /// <summary>Dispatches a DCS sequence; currently handles DECRQSS status requests and silently consumes the rest.</summary>
    public void DcsDispatch(char prefix, IReadOnlyList<int> parameters, string intermediates, char final, string data)
    {
        // DECRQSS(DCS $ q Pt ST):按 xterm 惯例应答 DCS 1 $ r <设定> ST(1=有效,0=无效)。
        // sixel 仍未实现,静默消费。
        if (final != 'q' || intermediates != "$")
        {
            return;
        }
        switch (data)
        {
            case "m": // SGR:回报当前画笔属性
                Send($"\eP1$r{BuildSgrReport()}m\e\\");
                break;
            case "r": // DECSTBM:回报当前滚动区域(1 基)
                Send($"\eP1$r{Screen.ScrollTop + 1};{Screen.ScrollBottom + 1}r\e\\");
                break;
            default:
                Send("\eP0$r\e\\");
                break;
        }
    }

    /// <summary>Bytes the terminal needs to send back to the host (DA/DSR/etc.).</summary>
    public event Action<byte[]>? Response;

    /// <summary>OSC 0/2 window-title changes.</summary>
    public event Action<string>? TitleChanged;

    /// <summary>BEL (0x07) received.</summary>
    public event Action? Bell;

    /// <summary>Raised after a chunk of input has been applied so the UI can repaint.</summary>
    public event Action? Updated;

    /// <summary>Switches the emulated terminal type, updating VT52 parsing accordingly.</summary>
    public void SetTerminalType(TerminalType type)
    {
        Type = type;
        _parser.Vt52Mode = type == TerminalType.Vt52;
    }

    /// <summary>Changes the byte-decoding charset (UTF-8 by default). Pending bytes are dropped.</summary>
    public void SetEncoding(Encoding encoding) => _utf8.SetEncoding(encoding);

    // ---- Input --------------------------------------------------------------

    /// <summary>Feeds raw bytes from the host. UTF-8 is decoded before parsing.</summary>
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        _feedTimestamp = DateTime.Now;
        string decoded = _utf8.Decode(bytes);
        if (decoded.Length > 0)
        {
            _parser.Parse(decoded);
        }
        Updated?.Invoke();
    }

    /// <summary>Feeds raw bytes from the host. UTF-8 is decoded before parsing.</summary>
    public void Feed(byte[] bytes) => Feed(bytes.AsSpan());

    /// <summary>Resizes both the main and alternate screens (and tab stops) to the given geometry.</summary>
    public void Resize(int columns, int rows)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);
        _mainScreen.Resize(columns, rows, Blank());
        _altScreen?.Resize(columns, rows, Blank());
        _tabStops = ResizeTabs(_tabStops, columns);
        _pendingWrap = false;
    }

    // ---- Helpers ------------------------------------------------------------

    private TerminalCell Blank() => TerminalCell.Blank(_bg, _flags);

    private static bool[] BuildDefaultTabs(int columns)
    {
        bool[] tabs = new bool[Math.Max(1, columns)];
        for (int i = 0; i < tabs.Length; i++)
        {
            tabs[i] = i % 8 == 0 && i != 0;
        }
        return tabs;
    }

    private static bool[] ResizeTabs(bool[] old, int columns)
    {
        bool[] tabs = new bool[Math.Max(1, columns)];
        for (int i = 0; i < tabs.Length; i++)
        {
            tabs[i] = i < old.Length ? old[i] : i % 8 == 0 && i != 0;
        }
        return tabs;
    }

    private void AttachCombining(int rune)
    {
        int x = Screen.CursorX - 1;
        int y = Screen.CursorY;
        if (x < 0)
        {
            return;
        }
        ref TerminalCell cell = ref Screen.CellRef(Math.Clamp(x, 0, Screen.Columns - 1), y);
        if (cell.IsWideTrailing && x - 1 >= 0)
        {
            x -= 1;
            cell = ref Screen.CellRef(x, y);
        }
        cell.Combining = (cell.Combining ?? string.Empty) + char.ConvertFromUtf32(rune);
    }

    private void CarriageReturnLineFeed()
    {
        Screen.SetCursorX(0);
        IndexAndStamp();
    }

    /// <summary>
    /// Line feed 并给落到的行盖上本次 Feed 的时间戳。这样即使是输出里的空行(仅 \r\n,无可打印字符),
    /// 也被视为「该次输出产生的真实行」,侧栏据此显示其行号/时间;而光标从未到过的屏幕底部空行不会被盖章。
    /// </summary>
    private void IndexAndStamp()
    {
        Screen.Index(Blank());
        Screen.ActiveLine(Screen.CursorY).Timestamp = _feedTimestamp;
    }

    private void HorizontalTab()
    {
        int x = Screen.CursorX;
        for (int i = x + 1; i < Screen.Columns; i++)
        {
            if (!_tabStops[i])
            {
                continue;
            }
            Screen.SetCursorX(i);
            return;
        }
        Screen.SetCursorX(Screen.Columns - 1);
    }

    private void EscDispatchVt52(char final)
    {
        switch (final)
        {
            case 'A':
                Screen.SetCursor(Screen.CursorX, Screen.CursorY - 1);
                break;
            case 'B':
                Screen.SetCursor(Screen.CursorX, Screen.CursorY + 1);
                break;
            case 'C':
                Screen.SetCursor(Screen.CursorX + 1, Screen.CursorY);
                break;
            case 'D':
                Screen.SetCursor(Screen.CursorX - 1, Screen.CursorY);
                break;
            case 'H':
                Screen.SetCursor(0, 0);
                break;
            case 'I':
                Screen.ReverseIndex(Blank());
                break;
            case 'J':
                Screen.EraseInDisplay(0, Blank());
                break;
            case 'K':
                Screen.EraseInLine(0, Blank());
                break;
            case 'Z':
                Send("\e/Z");
                break; // VT52 identify
            case '<':
                SetTerminalType(TerminalType.Vt100);
                break; // exit VT52 mode
            case '=':
                Modes.ApplicationKeypad = true;
                break;
            case '>':
                Modes.ApplicationKeypad = false;
                break;
            case 'F':
                _decGraphics[0] = true;
                break; // enter graphics
            case 'G':
                _decGraphics[0] = false;
                break; // exit graphics
        }
    }

    private void HandlePrivateMode(IReadOnlyList<int> p, char final)
    {
        if (final == 'c')
        {
            // Some hosts send "CSI ? ... c" style; treat as DA if final is c is handled elsewhere.
            return;
        }
        bool set = final == 'h';
        if (final is not 'h' and not 'l')
        {
            return;
        }
        foreach (int mode in p)
        {
            switch (mode)
            {
                case 1:
                    Modes.ApplicationCursorKeys = set;
                    break; // DECCKM
                case 2:
                    if (!set)
                    {
                        SetTerminalType(TerminalType.Vt52);
                    }
                    break; // DECANM (reset -> VT52)
                case 3:
                    ColumnMode(set);
                    break; // DECCOLM 132/80
                case 5:
                    Modes.ReverseVideo = set;
                    break; // DECSCNM
                case 6:
                    Modes.OriginMode = set;
                    HomeCursor();
                    break; // DECOM
                case 7:
                    Modes.AutoWrap = set;
                    break; // DECAWM
                case 9:
                    Modes.Mouse = set ? MouseTracking.X10 : MouseTracking.None;
                    break;
                case 12:
                    Modes.CursorBlink = set;
                    break;
                case 25:
                    Modes.CursorVisible = set;
                    break; // DECTCEM
                case 1000:
                    Modes.Mouse = set ? MouseTracking.Normal : MouseTracking.None;
                    break;
                case 1002:
                    Modes.Mouse = set ? MouseTracking.ButtonEvent : MouseTracking.None;
                    break;
                case 1003:
                    Modes.Mouse = set ? MouseTracking.AnyEvent : MouseTracking.None;
                    break;
                case 1004:
                    break; // focus reporting (accepted)
                case 1006:
                    Modes.MouseEncoding = set ? MouseEncoding.Sgr : MouseEncoding.Default;
                    break;
                case 1015:
                    Modes.MouseEncoding = set ? MouseEncoding.Urxvt : MouseEncoding.Default;
                    break;
                case 1047:
                    SwitchAlternate(set);
                    break;
                case 1048:
                    if (set)
                    {
                        SaveCursor();
                    }
                    else
                    {
                        RestoreCursor();
                    }
                    break;
                case 1049:
                    SwitchAlternate(set, true);
                    break;
                case 2004:
                    Modes.BracketedPaste = set;
                    break;
            }
        }
    }

    private void SetAnsiMode(IReadOnlyList<int> p, bool set)
    {
        foreach (int mode in p)
        {
            switch (mode)
            {
                case 4:
                    Modes.InsertMode = set;
                    break; // IRM
                case 20:
                    Modes.NewLineMode = set;
                    break; // LNM
            }
        }
    }

    // ---- Cursor operations --------------------------------------------------

    private void MoveCursor(int dx, int dy)
    {
        _pendingWrap = false;
        int y = Screen.CursorY + dy;
        if (Modes.OriginMode)
        {
            y = Math.Clamp(y, Screen.ScrollTop, Screen.ScrollBottom);
        }
        Screen.SetCursor(Screen.CursorX + dx, y);
    }

    private void SetCursorColumn(int col)
    {
        _pendingWrap = false;
        Screen.SetCursorX(col);
    }

    private void SetCursorRow(int row)
    {
        _pendingWrap = false;
        if (Modes.OriginMode)
        {
            row += Screen.ScrollTop;
        }
        Screen.SetCursorY(Modes.OriginMode ? Math.Clamp(row, Screen.ScrollTop, Screen.ScrollBottom) : row);
    }

    private void CursorPosition(int row, int col)
    {
        _pendingWrap = false;
        if (Modes.OriginMode)
        {
            row += Screen.ScrollTop;
            row = Math.Clamp(row, Screen.ScrollTop, Screen.ScrollBottom);
        }
        Screen.SetCursor(col, row);
    }

    private void HomeCursor()
    {
        if (Modes.OriginMode)
        {
            Screen.SetCursor(0, Screen.ScrollTop);
        }
        else
        {
            Screen.SetCursor(0, 0);
        }
    }

    private void TabForward(int count)
    {
        for (int i = 0; i < count; i++)
        {
            HorizontalTab();
        }
    }

    private void TabBackward(int count)
    {
        for (int c = 0; c < count; c++)
        {
            int x = Screen.CursorX;
            int target = 0;
            for (int i = x - 1; i > 0; i--)
            {
                if (!_tabStops[i])
                {
                    continue;
                }
                target = i;
                break;
            }
            Screen.SetCursorX(target);
        }
    }

    private void ClearTabs(int mode)
    {
        switch (mode)
        {
            case 0:
                _tabStops[Math.Clamp(Screen.CursorX, 0, _tabStops.Length - 1)] = false;
                break;
            case 3:
                Array.Clear(_tabStops, 0, _tabStops.Length);
                break;
        }
    }

    private void SetScrollRegion(IReadOnlyList<int> p)
    {
        int top = (p.Count > 0 && p[0] > 0 ? p[0] : 1) - 1;
        int bottom = (p.Count > 1 && p[1] > 0 ? p[1] : Screen.Rows) - 1;
        Screen.SetMargins(top, bottom);
        HomeCursor();
    }

    // ---- SGR ---------------------------------------------------------------

    private void ApplySgr(IReadOnlyList<int> p)
    {
        if (p.Count == 0)
        {
            ResetPen();
            return;
        }
        for (int i = 0; i < p.Count; i++)
        {
            int code = p[i];
            switch (code)
            {
                case 0:
                    ResetPen();
                    break;
                case 1:
                    _flags |= CellFlags.Bold;
                    break;
                case 2:
                    _flags |= CellFlags.Dim;
                    break;
                case 3:
                    _flags |= CellFlags.Italic;
                    break;
                case 4:
                    _flags |= CellFlags.Underline;
                    break;
                case 5:
                case 6:
                    _flags |= CellFlags.Blink;
                    break;
                case 7:
                    _flags |= CellFlags.Inverse;
                    break;
                case 8:
                    _flags |= CellFlags.Invisible;
                    break;
                case 9:
                    _flags |= CellFlags.Strikethrough;
                    break;
                case 21:
                    _flags |= CellFlags.DoubleUnderline;
                    break;
                case 22:
                    _flags &= ~(CellFlags.Bold | CellFlags.Dim);
                    break;
                case 23:
                    _flags &= ~CellFlags.Italic;
                    break;
                case 24:
                    _flags &= ~(CellFlags.Underline | CellFlags.DoubleUnderline);
                    break;
                case 25:
                    _flags &= ~CellFlags.Blink;
                    break;
                case 27:
                    _flags &= ~CellFlags.Inverse;
                    break;
                case 28:
                    _flags &= ~CellFlags.Invisible;
                    break;
                case 29:
                    _flags &= ~CellFlags.Strikethrough;
                    break;
                case >= 30 and <= 37:
                    _fg = TerminalColor.FromIndex(code - 30);
                    break;
                case 38:
                    i = ParseExtendedColor(p, i, ref _fg);
                    break;
                case 39:
                    _fg = TerminalColor.Default;
                    break;
                case >= 40 and <= 47:
                    _bg = TerminalColor.FromIndex(code - 40);
                    break;
                case 48:
                    i = ParseExtendedColor(p, i, ref _bg);
                    break;
                case 49:
                    _bg = TerminalColor.Default;
                    break;
                case >= 90 and <= 97:
                    _fg = TerminalColor.FromIndex(code - 90 + 8);
                    break;
                case >= 100 and <= 107:
                    _bg = TerminalColor.FromIndex(code - 100 + 8);
                    break;
            }
        }
    }

    /// <summary>Parses <c>38;5;n</c> / <c>48;5;n</c> (256-color) and <c>38;2;r;g;b</c> (truecolor).</summary>
    private int ParseExtendedColor(IReadOnlyList<int> p, int i, ref TerminalColor target)
    {
        if (i + 1 >= p.Count)
        {
            return i;
        }
        int kind = p[i + 1];
        switch (kind)
        {
            case 5 when i + 2 < p.Count:
                {
                    if (Type.SupportsColor())
                    {
                        target = TerminalColor.FromIndex(p[i + 2]);
                    }
                    return i + 2;
                }
            case 2 when i + 4 < p.Count:
                {
                    if (Type.SupportsColor())
                    {
                        target = TerminalColor.FromRgb((byte)p[i + 2], (byte)p[i + 3], (byte)p[i + 4]);
                    }
                    return i + 4;
                }
            default:
                return i + 1;
        }
    }

    private void ResetPen()
    {
        _fg = TerminalColor.Default;
        _bg = TerminalColor.Default;
        _flags = CellFlags.None;
    }

    // ---- Modes / reset ------------------------------------------------------

    private void ColumnMode(bool set)
    {
        // DECCOLM: switch 132/80 columns, clearing the screen.
        int cols = set ? 132 : 80;
        Screen.EraseInDisplay(2, Blank());
        Screen.SetCursor(0, 0);
        Resize(cols, Screen.Rows);
    }

    private void SwitchAlternate(bool enable, bool saveCursor = false)
    {
        if (enable == IsAlternateScreen)
        {
            return;
        }
        if (enable)
        {
            // Save the MAIN cursor into the dedicated alt slot before switching.
            if (saveCursor)
            {
                _altSaved = CaptureCursor();
            }
            _altScreen = new(_mainScreen.Columns, _mainScreen.Rows, 0);
            _altScreen.ResetToBlank(Blank());
            IsAlternateScreen = true;
            Screen = _altScreen;
            Screen.SetCursor(0, 0);
        }
        else
        {
            _altScreen = null;
            IsAlternateScreen = false;
            Screen = _mainScreen;
            // Restore the main cursor from the dedicated alt slot — never from _saved, which the
            // alt-screen app may have overwritten via DECSC.
            if (saveCursor)
            {
                ApplyCursor(_altSaved);
            }
        }
        _pendingWrap = false;
    }

    private SavedCursor CaptureCursor() =>
        new()
        {
            X = Screen.CursorX,
            Y = Screen.CursorY,
            Fg = _fg,
            Bg = _bg,
            Flags = _flags,
            Gl = _gl,
            DecGraphics = (bool[])_decGraphics.Clone(),
            OriginMode = Modes.OriginMode
        };

    private void ApplyCursor(SavedCursor? saved)
    {
        if (saved is not { } s)
        {
            Screen.SetCursor(0, 0);
            return;
        }
        Screen.SetCursor(s.X, s.Y);
        _fg = s.Fg;
        _bg = s.Bg;
        _flags = s.Flags;
        _gl = s.Gl;
        Array.Copy(s.DecGraphics, _decGraphics, _decGraphics.Length);
        Modes.OriginMode = s.OriginMode;
        _pendingWrap = false;
    }

    private void SaveCursor() => _saved = CaptureCursor();

    private void RestoreCursor() => ApplyCursor(_saved);

    private void FillScreenWithE()
    {
        var cell = new TerminalCell { Rune = 'E', Foreground = _fg, Background = _bg, Flags = _flags };
        for (int y = 0; y < Screen.Rows; y++)
            for (int x = 0; x < Screen.Columns; x++)
            {
                Screen.SetCell(x, y, cell);
            }
    }

    private void SoftReset()
    {
        Modes.Reset();
        Screen.ResetMargins();
        ResetPen();
        _gl = 0;
        Array.Clear(_decGraphics, 0, _decGraphics.Length);
        _saved = null;
        _altSaved = null;
        _pendingWrap = false;
    }

    private void FullReset()
    {
        SoftReset();
        _tabStops = BuildDefaultTabs(Screen.Columns);
        if (IsAlternateScreen)
        {
            SwitchAlternate(false);
        }
        Screen.ResetToBlank(Blank());
        Screen.ClearScrollback();
        _utf8.Reset();
        _parser.Reset();
    }

    // ---- Reports ------------------------------------------------------------

    private void DeviceAttributes(char prefix)
    {
        switch (prefix)
        {
            case '>':
                Send(Type.SecondaryDeviceAttributes());
                break;
            case '\0':
                Send(Type.PrimaryDeviceAttributes());
                break;
        }
    }

    private void DeviceStatusReport(int p)
    {
        switch (p)
        {
            case 5:
                Send("\e[0n");
                break; // OK
            case 6:
                int row = Screen.CursorY + 1;
                int col = Screen.CursorX + 1;
                if (Modes.OriginMode)
                {
                    row = Screen.CursorY - Screen.ScrollTop + 1;
                }
                Send($"\e[{row};{col}R");
                break;
        }
    }

    private void Send(string ascii) => Response?.Invoke(Encoding.ASCII.GetBytes(ascii));

    // ---- IVtActions: OSC / DCS ---------------------------------------------

    /// <summary>
    /// OSC 52:远端程序(tmux/vim 的 yank)请求写系统剪贴板。只支持写方向;
    /// 查询("?")一律不应答,防止远端读取本地剪贴板内容(安全)。宿主控件订阅后落剪贴板。
    /// </summary>
    public event Action<string>? ClipboardWriteRequested;

    /// <summary>把当前画笔状态编码为 SGR 参数串(DECRQSS "m" 应答用),始终以 0 开头。</summary>
    private string BuildSgrReport()
    {
        var sb = new StringBuilder("0");
        if ((_flags & CellFlags.Bold) != 0)
        {
            sb.Append(";1");
        }
        if ((_flags & CellFlags.Dim) != 0)
        {
            sb.Append(";2");
        }
        if ((_flags & CellFlags.Italic) != 0)
        {
            sb.Append(";3");
        }
        if ((_flags & CellFlags.Underline) != 0)
        {
            sb.Append(";4");
        }
        if ((_flags & CellFlags.Blink) != 0)
        {
            sb.Append(";5");
        }
        if ((_flags & CellFlags.Inverse) != 0)
        {
            sb.Append(";7");
        }
        if ((_flags & CellFlags.Invisible) != 0)
        {
            sb.Append(";8");
        }
        if ((_flags & CellFlags.Strikethrough) != 0)
        {
            sb.Append(";9");
        }
        AppendSgrColor(sb, _fg, true);
        AppendSgrColor(sb, _bg, false);
        return sb.ToString();
    }

    private static void AppendSgrColor(StringBuilder sb, TerminalColor color, bool isForeground)
    {
        // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
        switch (color.Kind)
        {
            case TerminalColorKind.Indexed when color.Index < 8:
                sb.Append(';').Append((isForeground ? 30 : 40) + color.Index);
                break;
            case TerminalColorKind.Indexed when color.Index < 16:
                sb.Append(';').Append((isForeground ? 90 : 100) + color.Index - 8);
                break;
            case TerminalColorKind.Indexed:
                sb.Append(isForeground ? ";38;5;" : ";48;5;").Append(color.Index);
                break;
            case TerminalColorKind.Rgb:
                sb.Append(isForeground ? ";38;2;" : ";48;2;")
                  .Append(color.R).Append(';').Append(color.G).Append(';').Append(color.B);
                break;
                // Default:SGR 0 已覆盖,无需追加。
        }
    }

    private struct SavedCursor
    {
        public int X, Y;
        public TerminalColor Fg, Bg;
        public CellFlags Flags;
        public int Gl;
        public bool[] DecGraphics;
        public bool OriginMode;
    }
}
