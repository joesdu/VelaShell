using System.Text;

namespace PulseTerm.Terminal.Emulation;

/// <summary>
/// The terminal "brain": consumes parsed escape-sequence events from <see cref="VtParser"/>
/// and applies them to a <see cref="TerminalScreen"/>. Owns the current graphic rendition
/// (pen), character sets, terminal modes, tab stops and the saved-cursor state, and produces
/// host-bound replies (Device Attributes, cursor reports, etc.) via <see cref="Response"/>.
///
/// Behavior is gated by the active <see cref="TerminalType"/> so the same engine can emulate
/// anything from a VT52 up to xterm-256color.
/// </summary>
public sealed class TerminalEmulator : IVtActions
{
    private readonly VtParser _parser;
    private readonly Utf8Sink _utf8 = new();

    private TerminalScreen _screen;
    private readonly TerminalScreen _mainScreen;
    private TerminalScreen? _altScreen;   // alternate buffer (no scrollback)
    private bool _alternate;

    // Current pen
    private TerminalColor _fg = TerminalColor.Default;
    private TerminalColor _bg = TerminalColor.Default;
    private CellFlags _flags = CellFlags.None;

    // Character sets: G0..G3 designations, GL/GR invocation, single shift.
    private readonly bool[] _decGraphics = new bool[4]; // true => DEC special graphics
    private int _gl;                                     // active GL set index
    private int _singleShift = -1;

    private bool _pendingWrap;                           // deferred autowrap at end of line
    private bool[] _tabStops;

    // Saved cursor (DECSC / DECRC, CSI s/u, DECSET 1048)
    private SavedCursor? _saved;

    // Separate save slot for the alternate-screen switch (DECSET 1049). Keeping it distinct from
    // _saved is what xterm does: an app running in the alt screen (e.g. nano) uses DECSC/DECRC
    // freely without clobbering the main-screen cursor that must be restored on exit (#14b).
    private SavedCursor? _altSaved;

    private struct SavedCursor
    {
        public int X, Y;
        public TerminalColor Fg, Bg;
        public CellFlags Flags;
        public int Gl;
        public bool[] DecGraphics;
        public bool OriginMode;
    }

    public TerminalEmulator(int columns = 80, int rows = 24, TerminalType type = TerminalType.XtermusColor256, int scrollback = 10_000)
    {
        Type = type;
        Palette = new TerminalPalette();
        Modes = new TerminalModes();
        _screen = new TerminalScreen(columns, rows, scrollback);
        _mainScreen = _screen;
        _tabStops = BuildDefaultTabs(columns);
        _parser = new VtParser(this) { Vt52Mode = type == TerminalType.Vt52 };
    }

    public TerminalType Type { get; private set; }
    public TerminalModes Modes { get; }
    public TerminalPalette Palette { get; }
    public TerminalScreen Screen => _screen;

    public int Columns => _screen.Columns;
    public int Rows => _screen.Rows;
    public int CursorX => _screen.CursorX;
    public int CursorY => _screen.CursorY;
    public bool IsAlternateScreen => _alternate;

    /// <summary>Bytes the terminal needs to send back to the host (DA/DSR/etc.).</summary>
    public event Action<byte[]>? Response;

    /// <summary>OSC 0/2 window-title changes.</summary>
    public event Action<string>? TitleChanged;

    /// <summary>BEL (0x07) received.</summary>
    public event Action? Bell;

    /// <summary>Raised after a chunk of input has been applied so the UI can repaint.</summary>
    public event Action? Updated;

    public void SetTerminalType(TerminalType type)
    {
        Type = type;
        _parser.Vt52Mode = type == TerminalType.Vt52;
    }

    /// <summary>Changes the byte-decoding charset (UTF-8 by default). Pending bytes are dropped.</summary>
    public void SetEncoding(System.Text.Encoding encoding) => _utf8.SetEncoding(encoding);

    // ---- Input --------------------------------------------------------------

    /// <summary>Feeds raw bytes from the host. UTF-8 is decoded before parsing.</summary>
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        string decoded = _utf8.Decode(bytes);
        if (decoded.Length > 0)
            _parser.Parse(decoded);
        Updated?.Invoke();
    }

    public void Feed(byte[] bytes) => Feed(bytes.AsSpan());

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
        var tabs = new bool[Math.Max(1, columns)];
        for (int i = 0; i < tabs.Length; i++)
            tabs[i] = i % 8 == 0 && i != 0;
        return tabs;
    }

    private static bool[] ResizeTabs(bool[] old, int columns)
    {
        var tabs = new bool[Math.Max(1, columns)];
        for (int i = 0; i < tabs.Length; i++)
            tabs[i] = i < old.Length ? old[i] : (i % 8 == 0 && i != 0);
        return tabs;
    }

    // ---- IVtActions: printing ----------------------------------------------

    public void Print(int rune)
    {
        // Apply active charset translation.
        int setIndex = _singleShift >= 0 ? _singleShift : _gl;
        _singleShift = -1;
        if (_decGraphics[setIndex])
            rune = Charsets.MapDecSpecial(rune);

        int width = CharWidth.Of(rune);

        // Combining marks attach to the previous cell without advancing the cursor.
        if (width == 0)
        {
            AttachCombining(rune);
            return;
        }

        if (_pendingWrap)
        {
            _screen.ActiveLine(_screen.CursorY).Wrapped = true;
            CarriageReturnLineFeed(wrap: true);
            _pendingWrap = false;
        }

        // Autowrap check for wide chars that won't fit.
        if (width == 2 && _screen.CursorX == _screen.Columns - 1)
        {
            if (Modes.AutoWrap)
            {
                _screen.ActiveLine(_screen.CursorY).Wrapped = true;
                CarriageReturnLineFeed(wrap: true);
            }
            else
            {
                _screen.SetCursorX(_screen.Columns - 2);
            }
        }

        if (Modes.InsertMode)
            _screen.InsertChars(width, Blank());

        var cell = new TerminalCell
        {
            Rune = rune,
            Foreground = _fg,
            Background = _bg,
            Flags = _flags,
        };
        _screen.SetCell(_screen.CursorX, _screen.CursorY, cell);

        if (width == 2)
        {
            var trailing = cell;
            trailing.Rune = 0;
            trailing.Flags |= CellFlags.WideTrailing;
            _screen.SetCell(_screen.CursorX + 1, _screen.CursorY, trailing);
        }

        int advance = width;
        if (_screen.CursorX + advance >= _screen.Columns)
        {
            if (Modes.AutoWrap)
            {
                _screen.SetCursorX(_screen.Columns - 1);
                _pendingWrap = true;
            }
            else
            {
                _screen.SetCursorX(_screen.Columns - 1);
            }
        }
        else
        {
            _screen.SetCursorX(_screen.CursorX + advance);
        }
    }

    private void AttachCombining(int rune)
    {
        int x = _screen.CursorX - 1;
        int y = _screen.CursorY;
        if (x < 0) return;
        ref TerminalCell cell = ref _screen.CellRef(Math.Clamp(x, 0, _screen.Columns - 1), y);
        if (cell.IsWideTrailing && x - 1 >= 0)
        {
            x -= 1;
            cell = ref _screen.CellRef(x, y);
        }
        cell.Combining = (cell.Combining ?? string.Empty) + char.ConvertFromUtf32(rune);
    }

    private void CarriageReturnLineFeed(bool wrap)
    {
        if (!wrap)
            _screen.SetCursorX(0);
        else
            _screen.SetCursorX(0);
        _screen.Index(Blank());
    }

    // ---- IVtActions: C0 controls -------------------------------------------

    public void Execute(char control)
    {
        switch (control)
        {
            case '\a': // BEL
                Bell?.Invoke();
                break;
            case '\b': // BS
                if (_pendingWrap) _pendingWrap = false;
                else if (_screen.CursorX > 0) _screen.SetCursorX(_screen.CursorX - 1);
                break;
            case '\t': // HT
                HorizontalTab();
                break;
            case '\n': // LF
            case '\v': // VT
            case '\f': // FF
                _pendingWrap = false;
                _screen.Index(Blank());
                if (Modes.NewLineMode)
                    _screen.SetCursorX(0);
                break;
            case '\r': // CR
                _pendingWrap = false;
                _screen.SetCursorX(0);
                break;
            case '\x0E': // SO -> invoke G1 into GL
                _gl = 1;
                break;
            case '\x0F': // SI -> invoke G0 into GL
                _gl = 0;
                break;
        }
    }

    private void HorizontalTab()
    {
        int x = _screen.CursorX;
        for (int i = x + 1; i < _screen.Columns; i++)
        {
            if (_tabStops[i])
            {
                _screen.SetCursorX(i);
                return;
            }
        }
        _screen.SetCursorX(_screen.Columns - 1);
    }

    // ---- IVtActions: ESC ----------------------------------------------------

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
                    if (final == '8') FillScreenWithE(); // DECALN
                    return;
            }
            return;
        }

        switch (final)
        {
            case 'D': _screen.Index(Blank()); break;                 // IND
            case 'M': _screen.ReverseIndex(Blank()); break;          // RI
            case 'E': _screen.SetCursorX(0); _screen.Index(Blank()); break; // NEL
            case 'H': _tabStops[Math.Clamp(_screen.CursorX, 0, _tabStops.Length - 1)] = true; break; // HTS
            case '7': SaveCursor(); break;                           // DECSC
            case '8': RestoreCursor(); break;                        // DECRC
            case '=': Modes.ApplicationKeypad = true; break;         // DECKPAM
            case '>': Modes.ApplicationKeypad = false; break;        // DECKPNM
            case 'c': FullReset(); break;                            // RIS
            case '\\': break;                                        // ST (string terminator)
            case 'n': _gl = 2; break;                                // LS2
            case 'o': _gl = 3; break;                                // LS3
        }
    }

    private void EscDispatchVt52(char final)
    {
        switch (final)
        {
            case 'A': _screen.SetCursor(_screen.CursorX, _screen.CursorY - 1); break;
            case 'B': _screen.SetCursor(_screen.CursorX, _screen.CursorY + 1); break;
            case 'C': _screen.SetCursor(_screen.CursorX + 1, _screen.CursorY); break;
            case 'D': _screen.SetCursor(_screen.CursorX - 1, _screen.CursorY); break;
            case 'H': _screen.SetCursor(0, 0); break;
            case 'I': _screen.ReverseIndex(Blank()); break;
            case 'J': _screen.EraseInDisplay(0, Blank()); break;
            case 'K': _screen.EraseInLine(0, Blank()); break;
            case 'Z': Send("\x1b/Z"); break;                         // VT52 identify
            case '<': SetTerminalType(TerminalType.Vt100); break;    // exit VT52 mode
            case '=': Modes.ApplicationKeypad = true; break;
            case '>': Modes.ApplicationKeypad = false; break;
            case 'F': _decGraphics[0] = true; break;                 // enter graphics
            case 'G': _decGraphics[0] = false; break;                // exit graphics
        }
    }

    // ---- IVtActions: CSI ----------------------------------------------------

    public void CsiDispatch(char prefix, IReadOnlyList<int> p, string intermediates, char final)
    {
        int P(int index, int def = 1)
        {
            if (index >= p.Count) return def;
            int v = p[index];
            return v == 0 ? def : v;
        }
        int P0(int index) => index < p.Count ? p[index] : 0;

        if (prefix == '?')
        {
            HandlePrivateMode(p, final);
            return;
        }

        // Intermediate '!' + 'p' => DECSTR soft reset.
        if (intermediates == "!" && final == 'p') { SoftReset(); return; }
        if (intermediates == " " && final == 'q') { return; } // DECSCUSR cursor style (accepted, style handled in UI)

        switch (final)
        {
            case '@': _screen.InsertChars(P(0), Blank()); break;                          // ICH
            case 'A': MoveCursor(0, -P(0)); break;                                        // CUU
            case 'B': MoveCursor(0, P(0)); break;                                         // CUD
            case 'C': MoveCursor(P(0), 0); break;                                         // CUF
            case 'D': MoveCursor(-P(0), 0); break;                                        // CUB
            case 'E': _screen.SetCursorX(0); MoveCursor(0, P(0)); break;                  // CNL
            case 'F': _screen.SetCursorX(0); MoveCursor(0, -P(0)); break;                 // CPL
            case '`':
            case 'G': SetCursorColumn(P(0) - 1); break;                                   // CHA / HPA
            case 'd': SetCursorRow(P(0) - 1); break;                                      // VPA
            case 'H':
            case 'f': CursorPosition(P(0) - 1, P(1) - 1); break;                          // CUP / HVP
            case 'I': TabForward(P(0)); break;                                            // CHT
            case 'Z': TabBackward(P(0)); break;                                           // CBT
            case 'J': _screen.EraseInDisplay(P0(0), Blank()); _pendingWrap = false; break;// ED
            case 'K': _screen.EraseInLine(P0(0), Blank()); _pendingWrap = false; break;   // EL
            case 'L': _screen.InsertLines(P(0), Blank()); break;                          // IL
            case 'M': _screen.DeleteLines(P(0), Blank()); break;                          // DL
            case 'P': _screen.DeleteChars(P(0), Blank()); break;                          // DCH
            case 'X': _screen.EraseChars(P(0), Blank()); break;                           // ECH
            case 'S': _screen.ScrollUp(P(0), Blank()); break;                             // SU
            case 'T': _screen.ScrollDown(P(0), Blank()); break;                           // SD
            case 'm': ApplySgr(p); break;                                                 // SGR
            case 'r': SetScrollRegion(p); break;                                          // DECSTBM
            case 'h': SetAnsiMode(p, true); break;
            case 'l': SetAnsiMode(p, false); break;
            case 'g': ClearTabs(P0(0)); break;                                            // TBC
            case 'c': DeviceAttributes(prefix, P0(0)); break;                             // DA
            case 'n': DeviceStatusReport(P0(0)); break;                                   // DSR
            case 's': SaveCursor(); break;                                                // ANSI.SYS save
            case 'u': RestoreCursor(); break;                                             // ANSI.SYS restore
            case 't': break;                                                              // window ops (ignored)
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
        if (final != 'h' && final != 'l')
            return;

        foreach (int mode in p)
        {
            switch (mode)
            {
                case 1: Modes.ApplicationCursorKeys = set; break;             // DECCKM
                case 2: if (!set) SetTerminalType(TerminalType.Vt52); break;   // DECANM (reset -> VT52)
                case 3: ColumnMode(set); break;                               // DECCOLM 132/80
                case 5: Modes.ReverseVideo = set; break;                      // DECSCNM
                case 6: Modes.OriginMode = set; HomeCursor(); break;          // DECOM
                case 7: Modes.AutoWrap = set; break;                          // DECAWM
                case 9: Modes.Mouse = set ? MouseTracking.X10 : MouseTracking.None; break;
                case 12: Modes.CursorBlink = set; break;
                case 25: Modes.CursorVisible = set; break;                    // DECTCEM
                case 1000: Modes.Mouse = set ? MouseTracking.Normal : MouseTracking.None; break;
                case 1002: Modes.Mouse = set ? MouseTracking.ButtonEvent : MouseTracking.None; break;
                case 1003: Modes.Mouse = set ? MouseTracking.AnyEvent : MouseTracking.None; break;
                case 1004: break; // focus reporting (accepted)
                case 1006: Modes.MouseEncoding = set ? MouseEncoding.Sgr : MouseEncoding.Default; break;
                case 1015: Modes.MouseEncoding = set ? MouseEncoding.Urxvt : MouseEncoding.Default; break;
                case 1047: SwitchAlternate(set, clearOnExit: true); break;
                case 1048: if (set) SaveCursor(); else RestoreCursor(); break;
                case 1049: SwitchAlternate(set, clearOnExit: true, saveCursor: true); break;
                case 2004: Modes.BracketedPaste = set; break;
            }
        }
    }

    private void SetAnsiMode(IReadOnlyList<int> p, bool set)
    {
        foreach (int mode in p)
        {
            switch (mode)
            {
                case 4: Modes.InsertMode = set; break;   // IRM
                case 20: Modes.NewLineMode = set; break; // LNM
            }
        }
    }

    // ---- Cursor operations --------------------------------------------------

    private void MoveCursor(int dx, int dy)
    {
        _pendingWrap = false;
        int y = _screen.CursorY + dy;
        if (Modes.OriginMode)
            y = Math.Clamp(y, _screen.ScrollTop, _screen.ScrollBottom);
        _screen.SetCursor(_screen.CursorX + dx, y);
    }

    private void SetCursorColumn(int col)
    {
        _pendingWrap = false;
        _screen.SetCursorX(col);
    }

    private void SetCursorRow(int row)
    {
        _pendingWrap = false;
        if (Modes.OriginMode)
            row += _screen.ScrollTop;
        _screen.SetCursorY(Modes.OriginMode ? Math.Clamp(row, _screen.ScrollTop, _screen.ScrollBottom) : row);
    }

    private void CursorPosition(int row, int col)
    {
        _pendingWrap = false;
        if (Modes.OriginMode)
        {
            row += _screen.ScrollTop;
            row = Math.Clamp(row, _screen.ScrollTop, _screen.ScrollBottom);
        }
        _screen.SetCursor(col, row);
    }

    private void HomeCursor()
    {
        if (Modes.OriginMode)
            _screen.SetCursor(0, _screen.ScrollTop);
        else
            _screen.SetCursor(0, 0);
    }

    private void TabForward(int count)
    {
        for (int i = 0; i < count; i++) HorizontalTab();
    }

    private void TabBackward(int count)
    {
        for (int c = 0; c < count; c++)
        {
            int x = _screen.CursorX;
            int target = 0;
            for (int i = x - 1; i > 0; i--)
                if (_tabStops[i]) { target = i; break; }
            _screen.SetCursorX(target);
        }
    }

    private void ClearTabs(int mode)
    {
        if (mode == 0)
            _tabStops[Math.Clamp(_screen.CursorX, 0, _tabStops.Length - 1)] = false;
        else if (mode == 3)
            Array.Clear(_tabStops, 0, _tabStops.Length);
    }

    private void SetScrollRegion(IReadOnlyList<int> p)
    {
        int top = (p.Count > 0 && p[0] > 0 ? p[0] : 1) - 1;
        int bottom = (p.Count > 1 && p[1] > 0 ? p[1] : _screen.Rows) - 1;
        _screen.SetMargins(top, bottom);
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
                case 0: ResetPen(); break;
                case 1: _flags |= CellFlags.Bold; break;
                case 2: _flags |= CellFlags.Dim; break;
                case 3: _flags |= CellFlags.Italic; break;
                case 4: _flags |= CellFlags.Underline; break;
                case 5: case 6: _flags |= CellFlags.Blink; break;
                case 7: _flags |= CellFlags.Inverse; break;
                case 8: _flags |= CellFlags.Invisible; break;
                case 9: _flags |= CellFlags.Strikethrough; break;
                case 21: _flags |= CellFlags.DoubleUnderline; break;
                case 22: _flags &= ~(CellFlags.Bold | CellFlags.Dim); break;
                case 23: _flags &= ~CellFlags.Italic; break;
                case 24: _flags &= ~(CellFlags.Underline | CellFlags.DoubleUnderline); break;
                case 25: _flags &= ~CellFlags.Blink; break;
                case 27: _flags &= ~CellFlags.Inverse; break;
                case 28: _flags &= ~CellFlags.Invisible; break;
                case 29: _flags &= ~CellFlags.Strikethrough; break;
                case >= 30 and <= 37: _fg = TerminalColor.FromIndex(code - 30); break;
                case 38: i = ParseExtendedColor(p, i, ref _fg); break;
                case 39: _fg = TerminalColor.Default; break;
                case >= 40 and <= 47: _bg = TerminalColor.FromIndex(code - 40); break;
                case 48: i = ParseExtendedColor(p, i, ref _bg); break;
                case 49: _bg = TerminalColor.Default; break;
                case >= 90 and <= 97: _fg = TerminalColor.FromIndex(code - 90 + 8); break;
                case >= 100 and <= 107: _bg = TerminalColor.FromIndex(code - 100 + 8); break;
            }
        }
    }

    /// <summary>Parses <c>38;5;n</c> / <c>48;5;n</c> (256-color) and <c>38;2;r;g;b</c> (truecolor).</summary>
    private int ParseExtendedColor(IReadOnlyList<int> p, int i, ref TerminalColor target)
    {
        if (i + 1 >= p.Count)
            return i;
        int kind = p[i + 1];
        if (kind == 5 && i + 2 < p.Count)
        {
            if (Type.SupportsColor())
                target = TerminalColor.FromIndex(p[i + 2]);
            return i + 2;
        }
        if (kind == 2 && i + 4 < p.Count)
        {
            if (Type.SupportsColor())
                target = TerminalColor.FromRgb((byte)p[i + 2], (byte)p[i + 3], (byte)p[i + 4]);
            return i + 4;
        }
        return i + 1;
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
        _screen.EraseInDisplay(2, Blank());
        _screen.SetCursor(0, 0);
        Resize(cols, _screen.Rows);
    }

    private void SwitchAlternate(bool enable, bool clearOnExit, bool saveCursor = false)
    {
        if (enable == _alternate)
            return;

        if (enable)
        {
            // Save the MAIN cursor into the dedicated alt slot before switching.
            if (saveCursor) _altSaved = CaptureCursor();
            _altScreen = new TerminalScreen(_mainScreen.Columns, _mainScreen.Rows, maxScrollback: 0);
            _altScreen.ResetToBlank(Blank());
            _alternate = true;
            _screen = _altScreen;
            _screen.SetCursor(0, 0);
        }
        else
        {
            _altScreen = null;
            _alternate = false;
            _screen = _mainScreen;
            // Restore the main cursor from the dedicated alt slot — never from _saved, which the
            // alt-screen app may have overwritten via DECSC.
            if (saveCursor) ApplyCursor(_altSaved);
        }
        _pendingWrap = false;
    }

    private SavedCursor CaptureCursor() => new()
    {
        X = _screen.CursorX,
        Y = _screen.CursorY,
        Fg = _fg,
        Bg = _bg,
        Flags = _flags,
        Gl = _gl,
        DecGraphics = (bool[])_decGraphics.Clone(),
        OriginMode = Modes.OriginMode,
    };

    private void ApplyCursor(SavedCursor? saved)
    {
        if (saved is not { } s)
        {
            _screen.SetCursor(0, 0);
            return;
        }
        _screen.SetCursor(s.X, s.Y);
        _fg = s.Fg; _bg = s.Bg; _flags = s.Flags; _gl = s.Gl;
        Array.Copy(s.DecGraphics, _decGraphics, _decGraphics.Length);
        Modes.OriginMode = s.OriginMode;
        _pendingWrap = false;
    }

    private void SaveCursor() => _saved = CaptureCursor();

    private void RestoreCursor() => ApplyCursor(_saved);

    private void FillScreenWithE()
    {
        var cell = new TerminalCell { Rune = 'E', Foreground = _fg, Background = _bg, Flags = _flags };
        for (int y = 0; y < _screen.Rows; y++)
            for (int x = 0; x < _screen.Columns; x++)
                _screen.SetCell(x, y, cell);
    }

    private void SoftReset()
    {
        Modes.Reset();
        _screen.ResetMargins();
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
        _tabStops = BuildDefaultTabs(_screen.Columns);
        if (_alternate)
            SwitchAlternate(false, clearOnExit: true);
        _screen.ResetToBlank(Blank());
        _screen.ClearScrollback();
        _utf8.Reset();
        _parser.Reset();
    }

    // ---- Reports ------------------------------------------------------------

    private void DeviceAttributes(char prefix, int p)
    {
        if (prefix == '>')
            Send(Type.SecondaryDeviceAttributes());
        else if (prefix == '\0')
            Send(Type.PrimaryDeviceAttributes());
    }

    private void DeviceStatusReport(int p)
    {
        switch (p)
        {
            case 5: Send("\x1b[0n"); break; // OK
            case 6:
                int row = _screen.CursorY + 1;
                int col = _screen.CursorX + 1;
                if (Modes.OriginMode)
                    row = _screen.CursorY - _screen.ScrollTop + 1;
                Send($"\x1b[{row};{col}R");
                break;
        }
    }

    private void Send(string ascii) => Response?.Invoke(Encoding.ASCII.GetBytes(ascii));

    // ---- IVtActions: OSC / DCS ---------------------------------------------

    public void OscDispatch(IReadOnlyList<string> p)
    {
        if (p.Count == 0)
            return;
        if (!int.TryParse(p[0], out int cmd))
            return;
        switch (cmd)
        {
            case 0:
            case 2:
                if (p.Count > 1)
                    TitleChanged?.Invoke(p[1]);
                break;
            // 4 (palette), 8 (hyperlink), 52 (clipboard) intentionally accepted-and-ignored for now.
        }
    }

    public void DcsDispatch(char prefix, IReadOnlyList<int> parameters, string intermediates, char final, string data)
    {
        // DECRQSS and sixel are not yet implemented; consumed silently.
    }
}
