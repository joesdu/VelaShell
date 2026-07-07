using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using PulseTerm.Terminal.Emulation;
using PulseTerm.Terminal.Semantics;

namespace PulseTerm.Terminal.Rendering;

/// <summary>
/// A fully self-drawn terminal control. It owns a <see cref="TerminalEmulator"/>, renders the
/// screen buffer with cached glyph runs, and translates keyboard / mouse / clipboard input into
/// host bytes. Implements <see cref="ITerminalEmulator"/> so it drops straight into the existing
/// <c>SshTerminalBridge</c> and views without any changes to the wiring.
/// </summary>
public sealed class PulseTerminalControl : Control, ITerminalEmulator
{
    private readonly TerminalEmulator _emulator;
    private readonly SemanticMatcher _semanticMatcher = new();
    private readonly Dictionary<uint, ImmutableSolidColorBrush> _brushCache = new();

    // Client-side semantic coloring (URLs, IPs, error/warning/success words, option flags, numbers)
    // for text the remote program left in the default color, so plain logs/MOTD get highlighted
    // without ever clobbering explicit SGR colors (ls --color, git, etc.). Regex results are cached
    // by line text since the visible lines are re-scanned every frame (cursor blink, output).
    private readonly Dictionary<string, IReadOnlyList<SemanticSpan>> _semanticSpanCache = new();

    /// <summary>Toggles client-side semantic highlighting of default-colored output.</summary>
    public bool SemanticHighlightingEnabled { get; set; } = true;

    // Cache of shaped, colored glyphs keyed by (rune, combining, foreground, style). Terminal
    // output draws from a tiny alphabet, so hit rate is ~100% and per-frame text shaping —
    // the dominant render cost — effectively disappears. Cleared when the font/size changes.
    private readonly Dictionary<GlyphKey, FormattedText> _glyphCache = new();

    private readonly record struct GlyphKey(int Rune, string? Combining, uint Foreground, int Style);

    private FontFamily _fontFamily = new("JetBrains Mono, Cascadia Mono, Consolas, Microsoft YaHei, Segoe UI, monospace");
    private double _fontSize = 14;
    private double _cellWidth = 8;
    private double _cellHeight = 16;
    private double _baselineOffset;

    private int _scrollOffset;            // lines scrolled up from the bottom (0 = live)
    private int _lastScrollbackCount;     // scrollback size at the previous output update
    private bool _hasFocus;

    /// <summary>When true, releasing a selection copies it to the clipboard automatically.</summary>
    public bool CopyOnSelect { get; set; } = true;

    /// <summary>Raised whenever the scroll position or scrollable extent changes.</summary>
    public event Action? ScrollChanged;

    /// <summary>Maximum lines that can be scrolled up (size of the scrollback history).</summary>
    public int MaxScrollOffset => _emulator.Screen.ScrollbackCount;

    /// <summary>Lines currently scrolled up from the live bottom (0 = following output).</summary>
    public int ScrollOffset
    {
        get => _scrollOffset;
        set
        {
            int clamped = Math.Clamp(value, 0, MaxScrollOffset);
            if (clamped == _scrollOffset)
                return;
            _scrollOffset = clamped;
            InvalidateVisual();
            ScrollChanged?.Invoke();
        }
    }

    /// <summary>
    /// Computes the scroll offset to keep the same history content visible after new lines were
    /// pushed into scrollback. At the live bottom (offset 0) the view follows output; when the
    /// user has scrolled up, the offset grows with the scrollback so the view stays pinned.
    /// </summary>
    internal static int PinScrollOffset(int currentOffset, int lastScrollback, int newScrollback)
    {
        if (currentOffset <= 0)
            return 0;
        int growth = newScrollback - lastScrollback;
        int pinned = growth > 0 ? currentOffset + growth : currentOffset;
        return Math.Clamp(pinned, 0, Math.Max(0, newScrollback));
    }

    // Selection (linear), in absolute-row space.
    private (int Row, int Col)? _selectionAnchor;
    private (int Row, int Col)? _selectionCaret;
    private bool _selecting;

    // Mouse reporting to the app (htop/btop/vim/tmux): the button held after a reported press, and
    // the last cell reported, so drag/motion only emits when the cell actually changes.
    private TerminalMouseButton? _mouseButtonDown;
    private (int Col, int Row) _lastMouseReportCell = (-1, -1);

    public PulseTerminalControl()
        : this(new TerminalEmulator(120, 32))
    {
    }

    public PulseTerminalControl(TerminalEmulator emulator)
    {
        _emulator = emulator;
        Focusable = true;
        ClipToBounds = true;

        ApplyDesignPalette(_emulator.Palette);
        RecomputeMetrics();

        _emulator.Updated += OnEmulatorUpdated;
        _emulator.Response += bytes => UserInput?.Invoke(bytes);

        AddHandler(TextInputMethodClientRequestedEvent, OnTextInputMethodClientRequested);
    }

    // ---- ITerminalEmulator --------------------------------------------------

    public event Action<byte[]>? UserInput;

    public event Action<int, int>? PtySizeChanged;

    public event Action<string>? TitleChanged
    {
        add => _emulator.TitleChanged += value;
        remove => _emulator.TitleChanged -= value;
    }

    public TerminalEmulator Emulator => _emulator;

    public void Feed(byte[] data) => _emulator.Feed(data);

    public void Resize(int cols, int rows)
    {
        _emulator.Resize(cols, rows);
        _scrollOffset = 0;
        _lastScrollbackCount = _emulator.Screen.ScrollbackCount;
        // Row indexes in the selection are absolute and shift on resize; drop it rather than
        // let a stale range mark (or copy) the wrong text.
        ClearSelection();
        InvalidateVisual();
        ScrollChanged?.Invoke();
    }

    public void WriteInput(byte[] data) => UserInput?.Invoke(data);

    public string GetBufferLine(int row) => _emulator.Screen.ActiveLine(row).GetText();

    public int CursorRow => _emulator.CursorY;
    public int CursorCol => _emulator.CursorX;

    public int ScrollbackLines
    {
        get => _emulator.Screen.MaxScrollback;
        set => _emulator.Screen.MaxScrollback = value;
    }

    public Control Control => this;
    public int Columns => _emulator.Columns;
    public int Rows => _emulator.Rows;

    // Legacy interface members: kept for source compatibility with existing bindings.
    public ScrollbackBuffer ScrollbackBuffer { get; } = new(1);
    public int TotalLines => _emulator.Screen.TotalRows;
    public int ViewportRow => Math.Max(0, _emulator.Screen.TotalRows - _emulator.Rows - _scrollOffset);

    public TerminalType TerminalType
    {
        get => _emulator.Type;
        set => _emulator.SetTerminalType(value);
    }

    /// <summary>Sets the host-output charset (UTF-8 default; GBK/Big5/etc. supported).</summary>
    public void SetEncoding(System.Text.Encoding encoding) => _emulator.SetEncoding(encoding);

    public FontFamily FontFamily
    {
        get => _fontFamily;
        set { _fontFamily = value; RecomputeMetrics(); RelayoutFromBounds(); InvalidateVisual(); }
    }

    public double FontSize
    {
        get => _fontSize;
        set { _fontSize = value; RecomputeMetrics(); RelayoutFromBounds(); InvalidateVisual(); }
    }

    public void Dispose()
    {
        _emulator.Updated -= OnEmulatorUpdated;
        _layoutResizeDebounce?.Stop();
    }

    private void OnEmulatorUpdated()
    {
        if (Dispatcher.UIThread.CheckAccess())
            ApplyOutputUpdate();
        else
            Dispatcher.UIThread.Post(ApplyOutputUpdate);
    }

    private void ApplyOutputUpdate()
    {
        // Follow output only when already at the bottom; otherwise keep the user's history
        // view pinned so background output doesn't yank them back down (fixes #15).
        int scrollback = _emulator.Screen.ScrollbackCount;
        _scrollOffset = PinScrollOffset(_scrollOffset, _lastScrollbackCount, scrollback);
        _lastScrollbackCount = scrollback;
        InvalidateVisual();
        ScrollChanged?.Invoke();
        _imeClient?.NotifyCursorMoved();
    }

    // ---- IME ------------------------------------------------------------------

    private TerminalImeClient? _imeClient;

    /// <summary>Hands the OS input method a client anchored at the terminal cursor, so the IME
    /// candidate window (Chinese/Japanese/Korean composition) opens next to where the text will
    /// land instead of at the window corner (#14b).</summary>
    private void OnTextInputMethodClientRequested(object? sender, TextInputMethodClientRequestedEventArgs e)
    {
        _imeClient ??= new TerminalImeClient(this);
        e.Client = _imeClient;
    }

    /// <summary>The cursor cell's rectangle in control coordinates (same math as RenderCursor).</summary>
    internal Rect GetImeCursorRect()
    {
        var screen = _emulator.Screen;
        int topAbsolute = screen.TotalRows - screen.Rows - _scrollOffset;
        int cursorAbsolute = screen.TotalRows - screen.Rows + screen.CursorY;
        int screenRow = cursorAbsolute - topAbsolute;
        return new Rect(screen.CursorX * _cellWidth, screenRow * _cellHeight, _cellWidth, _cellHeight);
    }

    /// <summary>Minimal IME client: no preedit-in-buffer, no surrounding text — the terminal is
    /// not an editable document; committed text arrives through OnTextInput as host bytes. Only
    /// the cursor rectangle matters, to position the candidate window.</summary>
    private sealed class TerminalImeClient : TextInputMethodClient
    {
        private readonly PulseTerminalControl _owner;

        public TerminalImeClient(PulseTerminalControl owner) => _owner = owner;

        public override Visual TextViewVisual => _owner;
        public override bool SupportsPreedit => false;
        public override bool SupportsSurroundingText => false;
        public override string SurroundingText => string.Empty;
        public override Rect CursorRectangle => _owner.GetImeCursorRect();
        public override TextSelection Selection { get => default; set { } }

        public void NotifyCursorMoved() => RaiseCursorRectangleChanged();
    }

    // ---- Palette ------------------------------------------------------------

    /// <summary>Seeds the palette with the PulseTerm design's dark-theme terminal colors.</summary>
    public static void ApplyDesignPalette(TerminalPalette palette)
    {
        palette.DefaultForeground = Rgba.FromRgb(0xE0, 0xE6, 0xED);   // text-primary
        palette.DefaultBackground = Rgba.FromRgb(0x08, 0x0C, 0x12);   // bg-terminal
        palette.CursorColor = Rgba.FromRgb(0x00, 0xD4, 0xAA);         // accent
        palette.SelectionBackground = new Rgba(0x99, 0x1C, 0x2A, 0x3F);

        palette.SetAnsi(0, Rgba.FromRgb(0x0A, 0x0E, 0x14));  // black
        palette.SetAnsi(1, Rgba.FromRgb(0xFF, 0x6B, 0x6B));  // red   (term-red)
        palette.SetAnsi(2, Rgba.FromRgb(0x69, 0xFF, 0x94));  // green (term-green)
        palette.SetAnsi(3, Rgba.FromRgb(0xFD, 0xCB, 0x6E));  // yellow(term-yellow)
        palette.SetAnsi(4, Rgba.FromRgb(0x74, 0xB9, 0xFF));  // blue  (term-blue)
        palette.SetAnsi(5, Rgba.FromRgb(0xD9, 0x80, 0xFA));  // magenta(term-magenta)
        palette.SetAnsi(6, Rgba.FromRgb(0x00, 0xD4, 0xAA));  // cyan  (term-cyan)
        palette.SetAnsi(7, Rgba.FromRgb(0xE0, 0xE6, 0xED));  // white (term-white)
        palette.SetAnsi(8, Rgba.FromRgb(0x3D, 0x4F, 0x63));  // bright black
        palette.SetAnsi(9, Rgba.FromRgb(0xFF, 0x8A, 0x8A));
        palette.SetAnsi(10, Rgba.FromRgb(0x9B, 0xFF, 0xB6));
        palette.SetAnsi(11, Rgba.FromRgb(0xFF, 0xE0, 0x9B));
        palette.SetAnsi(12, Rgba.FromRgb(0xA5, 0xD1, 0xFF));
        palette.SetAnsi(13, Rgba.FromRgb(0xE9, 0xB0, 0xFF));
        palette.SetAnsi(14, Rgba.FromRgb(0x6B, 0xFF, 0xE8));
        palette.SetAnsi(15, Rgba.FromRgb(0xFF, 0xFF, 0xFF));
    }

    private ImmutableSolidColorBrush BrushFor(Rgba c)
    {
        if (_brushCache.TryGetValue(c.Packed, out var brush))
            return brush;
        brush = new ImmutableSolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
        _brushCache[c.Packed] = brush;
        return brush;
    }

    /// <summary>
    /// Returns a cached <see cref="FormattedText"/> for a single cell's glyph. Each glyph is
    /// still drawn at its own grid position by the caller, so wide (CJK) cells and monospace
    /// alignment are preserved exactly; only the expensive shaping is amortized.
    /// </summary>
    private FormattedText GlyphFor(in TerminalCell cell, Rgba fg, bool bold, bool italic)
    {
        int style = (bold ? 1 : 0) | (italic ? 2 : 0);
        var key = new GlyphKey(cell.Rune, cell.Combining, fg.Packed, style);
        if (_glyphCache.TryGetValue(key, out var cached))
            return cached;

        // Bound the cache; a full clear is fine because it refills within a frame or two.
        if (_glyphCache.Count > 8192)
            _glyphCache.Clear();

        var typeface = new Typeface(_fontFamily,
            italic ? FontStyle.Italic : FontStyle.Normal,
            bold ? FontWeight.Bold : FontWeight.Normal);
        var ft = new FormattedText(cell.GetText(), CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, typeface, _fontSize, BrushFor(fg));
        _glyphCache[key] = ft;
        return ft;
    }

    // ---- Metrics & layout ---------------------------------------------------

    private void RecomputeMetrics()
    {
        var typeface = new Typeface(_fontFamily);
        var probe = new FormattedText("0", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, _fontSize, Brushes.White);
        _cellWidth = Math.Max(1, Math.Round(probe.WidthIncludingTrailingWhitespace));
        _cellHeight = Math.Max(1, Math.Ceiling(probe.Height));
        _baselineOffset = probe.Baseline;

        // Cached glyphs are bound to the old typeface/size; drop them on any metric change.
        _glyphCache.Clear();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        ApplyLayoutSize(finalSize);
        return result;
    }

    private void RelayoutFromBounds() => ApplyLayoutSize(Bounds.Size);

    // The layout grid awaiting the debounce commit (see ApplyLayoutSize).
    private int _pendingCols;
    private int _pendingRows;
    private DispatcherTimer? _layoutResizeDebounce;

    private void ApplyLayoutSize(Size size)
    {
        if (_cellWidth <= 0 || _cellHeight <= 0)
            return;

        int cols = (int)(size.Width / _cellWidth);
        int rows = (int)(size.Height / _cellHeight);

        // Ignore early/degenerate layout passes (zero or sub-cell size). Collapsing the grid
        // to a single column here is what made the login banner render one char per line: every
        // subsequent character autowrapped. Keep the current (or default 120x32) grid until a
        // real size arrives.
        if (cols < 2 || rows < 2)
            return;

        // Debounce resize storms (splitter drags, the tab-drag preview squeezing the shared
        // control) the way xterm.js/VS Code throttle theirs: only the settled size is
        // committed, so the remote shell gets one WINCH instead of dozens. Interleaved
        // redraws at stale widths were what littered the prompt line with duplicated
        // fragments after fast drags. Returning to the current grid cancels the pending
        // commit outright — a drag that ends where it started never resizes at all.
        _pendingCols = cols;
        _pendingRows = rows;

        if (cols == _emulator.Columns && rows == _emulator.Rows)
        {
            _layoutResizeDebounce?.Stop();
            return;
        }

        if (_layoutResizeDebounce is null)
        {
            _layoutResizeDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(125) };
            _layoutResizeDebounce.Tick += (_, _) =>
            {
                _layoutResizeDebounce!.Stop();
                CommitPendingLayout();
            };
        }

        _layoutResizeDebounce.Stop();
        _layoutResizeDebounce.Start();
    }

    private void CommitPendingLayout()
    {
        if (_pendingCols == _emulator.Columns && _pendingRows == _emulator.Rows)
            return;

        _emulator.Resize(_pendingCols, _pendingRows);
        _scrollOffset = Math.Clamp(_scrollOffset, 0, _emulator.Screen.ScrollbackCount);
        _lastScrollbackCount = _emulator.Screen.ScrollbackCount;
        // Reflow shifts absolute rows; a stale selection would mark (and copy) the wrong text.
        ClearSelection();
        PtySizeChanged?.Invoke(_pendingCols, _pendingRows);
        InvalidateVisual();
        ScrollChanged?.Invoke();
    }

    // ---- Rendering ----------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        var screen = _emulator.Screen;
        var palette = _emulator.Palette;

        context.FillRectangle(BrushFor(palette.DefaultBackground), new Rect(Bounds.Size));

        int rows = screen.Rows;
        int cols = screen.Columns;
        int topAbsolute = Math.Max(0, screen.TotalRows - rows - _scrollOffset);

        var sel = NormalizedSelection();

        for (int screenRow = 0; screenRow < rows; screenRow++)
        {
            int absoluteRow = topAbsolute + screenRow;
            if (absoluteRow >= screen.TotalRows)
                break;
            var line = screen.ViewLine(absoluteRow);
            double y = screenRow * _cellHeight;
            RenderLine(context, palette, line, cols, y, absoluteRow, sel);
        }

        if (_scrollOffset == 0)
            RenderCursor(context, screen, palette, topAbsolute);
    }

    private void RenderLine(DrawingContext context, TerminalPalette palette, TerminalRow line,
        int cols, double y, int absoluteRow, ((int Row, int Col) Start, (int Row, int Col) End)? sel)
    {
        var semantic = SemanticHighlightingEnabled ? ComputeSemanticColumns(line, cols) : null;

        int col = 0;
        while (col < cols)
        {
            TerminalCell cell = line[col];
            if (cell.IsWideTrailing)
            {
                col++;
                continue;
            }

            int width = cell.Rune == 0 ? 1 : Math.Max(1, CharWidth.Of(cell.Rune));
            bool inverse = (cell.Flags & CellFlags.Inverse) != 0 ^ _emulator.Modes.ReverseVideo;
            bool bold = (cell.Flags & CellFlags.Bold) != 0;

            Rgba fg = palette.Resolve(cell.Foreground, isBackground: false, bold);
            Rgba bg = palette.Resolve(cell.Background, isBackground: true, bold: false);
            if (inverse)
                (fg, bg) = (bg, fg);
            if (IsSelected(sel, absoluteRow, col))
            {
                bg = palette.SelectionBackground;
            }

            if (_searchHighlights is not null &&
                _searchHighlights.TryGetValue(absoluteRow, out var searchSpans))
            {
                foreach (var span in searchSpans)
                {
                    if (col >= span.Start && col < span.End)
                    {
                        bg = span.Current ? SearchCurrentBg : SearchMatchBg;
                        break;
                    }
                }
            }

            // Recolor only text the program left in the default color, so explicit SGR colors
            // (ls --color, git, prompts) are never overridden. URLs and IPs also get an underline
            // to signal they are Ctrl+clickable.
            bool semanticUnderline = false;
            if (semantic is not null && !inverse && cell.Foreground.IsDefault && semantic[col] is { } kind)
            {
                fg = SemanticColor(palette, kind);
                semanticUnderline = kind is SemanticKind.Url or SemanticKind.IpAddress;
            }

            var cellRect = new Rect(col * _cellWidth, y, _cellWidth * width, _cellHeight);
            if (!bg.Equals(palette.DefaultBackground))
                context.FillRectangle(BrushFor(bg), cellRect);

            if (cell.Rune != 0 && (cell.Flags & CellFlags.Invisible) == 0)
            {
                bool italic = (cell.Flags & CellFlags.Italic) != 0;
                var ft = GlyphFor(cell, fg, bold, italic);
                double gx = col * _cellWidth;
                context.DrawText(ft, new Point(gx, y));
            }

            if ((cell.Flags & (CellFlags.Underline | CellFlags.DoubleUnderline)) != 0 || semanticUnderline)
            {
                double uy = y + _cellHeight - 1.5;
                context.DrawLine(new Pen(BrushFor(fg), 1),
                    new Point(col * _cellWidth, uy), new Point((col + width) * _cellWidth, uy));
            }
            if ((cell.Flags & CellFlags.Strikethrough) != 0)
            {
                double sy = y + _cellHeight / 2;
                context.DrawLine(new Pen(BrushFor(fg), 1),
                    new Point(col * _cellWidth, sy), new Point((col + width) * _cellWidth, sy));
            }

            col += width;
        }
    }

    /// <summary>
    /// Builds a per-column map of semantic kinds for a line: reconstructs the line text (mapping
    /// each character back to its source column so wide runes line up), matches it, and marks the
    /// columns each span covers. Returns null when highlighting yields nothing for the line.
    /// </summary>
    private SemanticKind?[]? ComputeSemanticColumns(TerminalRow line, int cols)
    {
        int lastNonBlank = -1;
        for (int i = 0; i < cols; i++)
            if (line[i].Rune != 0)
                lastNonBlank = i;
        if (lastNonBlank < 0)
            return null;

        var sb = new System.Text.StringBuilder(lastNonBlank + 1);
        var colByChar = new List<int>(lastNonBlank + 1);
        for (int i = 0; i <= lastNonBlank; i++)
        {
            TerminalCell cell = line[i];
            if (cell.IsWideTrailing)
                continue;
            int before = sb.Length;
            cell.AppendText(sb);
            for (int k = before; k < sb.Length; k++)
                colByChar.Add(i);
        }

        var spans = SemanticSpansFor(sb.ToString());
        if (spans.Count == 0)
            return null;

        var byColumn = new SemanticKind?[cols];
        foreach (var span in spans)
        {
            int end = Math.Min(span.End, colByChar.Count);
            for (int ci = span.Start; ci < end; ci++)
            {
                int c = colByChar[ci];
                if (c >= 0 && c < cols)
                    byColumn[c] = span.Kind;
            }
        }
        return byColumn;
    }

    private IReadOnlyList<SemanticSpan> SemanticSpansFor(string text)
    {
        if (_semanticSpanCache.TryGetValue(text, out var cached))
            return cached;

        // Bound the cache; terminal output has huge line variety, so just reset when it grows.
        if (_semanticSpanCache.Count > 1024)
            _semanticSpanCache.Clear();

        var spans = _semanticMatcher.Match(text);
        _semanticSpanCache[text] = spans;
        return spans;
    }

    /// <summary>Maps a semantic kind to a themeable ANSI color (respects the active .pen palette).</summary>
    private static Rgba SemanticColor(TerminalPalette palette, SemanticKind kind) => kind switch
    {
        SemanticKind.Error => palette[9],       // bright red
        SemanticKind.Warning => palette[11],    // bright yellow
        SemanticKind.Success => palette[10],    // bright green
        SemanticKind.Url => palette[12],        // bright blue
        SemanticKind.IpAddress => palette[14],  // bright cyan
        SemanticKind.Option => palette[13],     // bright magenta
        SemanticKind.Number => palette[6],      // cyan
        _ => palette.DefaultForeground,
    };

    private void RenderCursor(DrawingContext context, TerminalScreen screen, TerminalPalette palette, int topAbsolute)
    {
        if (!_emulator.Modes.CursorVisible)
            return;
        int cursorAbsolute = screen.TotalRows - screen.Rows + screen.CursorY;
        int screenRow = cursorAbsolute - topAbsolute;
        if (screenRow < 0 || screenRow >= screen.Rows)
            return;

        double x = screen.CursorX * _cellWidth;
        double y = screenRow * _cellHeight;
        var rect = new Rect(x, y, _cellWidth, _cellHeight);
        var cursorBrush = BrushFor(palette.CursorColor);

        if (_hasFocus)
        {
            context.FillRectangle(cursorBrush, rect);
            // Redraw the glyph under the cursor in the background color for contrast.
            var cell = screen.GetCell(screen.CursorX, screen.CursorY);
            if (cell.Rune != 0)
            {
                var ft = GlyphFor(cell, palette.DefaultBackground, bold: false, italic: false);
                context.DrawText(ft, new Point(x, y));
            }
        }
        else
        {
            context.DrawRectangle(new Pen(cursorBrush, 1), rect);
        }
    }

    // ---- Selection ----------------------------------------------------------

    private ((int Row, int Col) Start, (int Row, int Col) End)? NormalizedSelection()
    {
        if (_selectionAnchor is not { } a || _selectionCaret is not { } c)
            return null;
        if (a.Row < c.Row || (a.Row == c.Row && a.Col <= c.Col))
            return (a, c);
        return (c, a);
    }

    private static bool IsSelected(((int Row, int Col) Start, (int Row, int Col) End)? sel, int row, int col)
    {
        if (sel is not { } s)
            return false;
        if (row < s.Start.Row || row > s.End.Row)
            return false;
        if (row == s.Start.Row && col < s.Start.Col)
            return false;
        if (row == s.End.Row && col >= s.End.Col)
            return false;
        return true;
    }

    /// <summary>Searches the whole buffer (scrollback + screen), case-insensitive (spec §5.3).</summary>
    public IReadOnlyList<BufferSearchHit> SearchBuffer(string query) =>
        BufferSearch.FindAll(_emulator.Screen, query);

    // ---- Search highlights (spec §5.3: 命中项高亮) --------------------------

    private static readonly Rgba SearchMatchBg = new(0x59, 0xFD, 0xCB, 0x6E);   // amber, ~35%
    private static readonly Rgba SearchCurrentBg = new(0x73, 0x00, 0xD4, 0xAA); // accent, ~45%

    /// <summary>Search spans per absolute buffer row; the current hit is tinted differently.</summary>
    private Dictionary<int, List<(int Start, int End, bool Current)>>? _searchHighlights;

    /// <summary>Paints every search hit (amber) with the current one in accent. Rows are
    /// absolute buffer rows, so highlights stay attached while scrolling.</summary>
    public void SetSearchHighlights(IReadOnlyList<BufferSearchHit> hits, int currentIndex)
    {
        if (hits.Count == 0)
        {
            ClearSearchHighlights();
            return;
        }

        var map = new Dictionary<int, List<(int, int, bool)>>();
        for (int i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            if (!map.TryGetValue(hit.Row, out var spans))
                map[hit.Row] = spans = new List<(int, int, bool)>();
            spans.Add((hit.StartCol, hit.StartCol + hit.Length, i == currentIndex));
        }

        _searchHighlights = map;
        InvalidateVisual();
    }

    public void ClearSearchHighlights()
    {
        if (_searchHighlights is null)
            return;

        _searchHighlights = null;
        InvalidateVisual();
    }

    /// <summary>Scrolls a search hit into view (roughly centered) and selects it so the
    /// existing selection highlight marks the match.</summary>
    public void ShowHit(BufferSearchHit hit)
    {
        _selectionAnchor = (hit.Row, hit.StartCol);
        _selectionCaret = (hit.Row, hit.StartCol + hit.Length);

        int totalRows = _emulator.Screen.TotalRows;
        int rows = _emulator.Rows;
        int desiredTop = Math.Max(0, hit.Row - rows / 2);
        int maxTop = Math.Max(0, totalRows - rows);
        ScrollOffset = maxTop - Math.Min(desiredTop, maxTop);
        InvalidateVisual();
    }

    public string GetSelectedText()
    {
        var sel = NormalizedSelection();
        if (sel is not { } s)
            return string.Empty;
        var screen = _emulator.Screen;
        var sb = new StringBuilder();
        for (int row = s.Start.Row; row <= s.End.Row && row < screen.TotalRows; row++)
        {
            var line = screen.ViewLine(row);
            int from = row == s.Start.Row ? s.Start.Col : 0;
            int to = row == s.End.Row ? s.End.Col : line.Columns;
            for (int col = Math.Max(0, from); col < Math.Min(line.Columns, to); col++)
            {
                var cell = line[col];
                if (!cell.IsWideTrailing)
                    sb.Append(cell.Rune == 0 ? " " : char.ConvertFromUtf32(cell.Rune));
            }
            if (row != s.End.Row)
                sb.Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    private (int Row, int Col) PointToCell(Point p)
    {
        int screenRow = (int)(p.Y / _cellHeight);
        int col = (int)(p.X / _cellWidth);
        int topAbsolute = Math.Max(0, _emulator.Screen.TotalRows - _emulator.Rows - _scrollOffset);
        // Clamp the row: while the pointer is captured a drag can leave the control (negative
        // p.Y), and a negative absolute row used to crash selection copy (#用户反馈).
        int row = Math.Clamp(topAbsolute + screenRow, 0, Math.Max(0, _emulator.Screen.TotalRows - 1));
        return (row, Math.Clamp(col, 0, _emulator.Columns));
    }

    // ---- Input --------------------------------------------------------------

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _hasFocus = true;
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _hasFocus = false;
        InvalidateVisual();
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Text))
        {
            var bytes = InputEncoder.EncodeText(e.Text);
            if (bytes.Length > 0)
            {
                UserInput?.Invoke(bytes);
                ClearSelection();
                e.Handled = true;
            }
        }
        base.OnTextInput(e);
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        // While an IME is composing (e.g. picking a Chinese candidate), the keys it consumes are
        // delivered as ImeProcessed. Encoding them would send stray ESC / arrows / Enter to the
        // PTY — which is what made typing Chinese into htop's F3 search kill htop (#14a). The
        // committed text still arrives separately via OnTextInput.
        if (e.Key == Key.ImeProcessed)
        {
            base.OnKeyDown(e);
            return;
        }

        // Clipboard shortcuts.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (e.Key == Key.C) { await CopyAsync(); e.Handled = true; return; }
            if (e.Key == Key.V) { await PasteAsync(); e.Handled = true; return; }
        }

        // Shift+Insert pastes (classic X11 / terminal convention). Intercept before the
        // encoder, which would otherwise send this as a CSI 2~ sequence.
        if (e.Key == Key.Insert && e.KeyModifiers == KeyModifiers.Shift)
        {
            await PasteAsync();
            e.Handled = true;
            return;
        }

        // View paging (用户反馈): PageUp/PageDown page through the scrollback on the primary
        // screen (full-screen apps on the alternate screen still receive them as CSI 5~/6~);
        // the Shift+ variants page everywhere.
        if (e.Key is Key.PageUp or Key.PageDown &&
            (e.KeyModifiers == KeyModifiers.Shift ||
             (e.KeyModifiers == KeyModifiers.None && _emulator.Screen.MaxScrollback > 0)))
        {
            int page = Math.Max(1, _emulator.Rows - 1);
            ScrollOffset += e.Key == Key.PageUp ? page : -page;
            e.Handled = true;
            return;
        }

        // Shift+Home/End jump the shell cursor to line start/end (用户反馈): send the plain
        // Home/End sequences, which readline binds — the shifted CSI 1;2H/F variants it ignores.
        var effectiveModifiers = e.KeyModifiers;
        if (e.Key is Key.Home or Key.End && e.KeyModifiers == KeyModifiers.Shift)
            effectiveModifiers = KeyModifiers.None;

        var encoded = InputEncoder.Encode(e.Key, effectiveModifiers, _emulator.Modes, _emulator.Type);
        if (encoded is { Length: > 0 })
        {
            UserInput?.Invoke(encoded);
            _scrollOffset = 0;
            ClearSelection();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;

        // Ctrl+click on a detected URL opens it in the default browser (#9).
        if (props.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var (row, col) = PointToCell(point);
            string lineText = row < _emulator.Screen.TotalRows ? _emulator.Screen.ViewLine(row).GetText() : string.Empty;
            var url = _semanticMatcher.UrlAt(lineText, col);
            if (url is not null)
            {
                OpenLink(url);
                e.Handled = true;
                return;
            }
        }

        // When the app has enabled mouse tracking, forward the click to it (htop tabs/buttons,
        // btop, vim, tmux). Holding Shift bypasses reporting so the user can still select text.
        // Reporting only makes sense on the live screen, not while scrolled into history.
        if (_emulator.Modes.Mouse != MouseTracking.None && _scrollOffset == 0
            && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            TerminalMouseButton? button =
                props.IsLeftButtonPressed ? TerminalMouseButton.Left :
                props.IsRightButtonPressed ? TerminalMouseButton.Right :
                props.IsMiddleButtonPressed ? TerminalMouseButton.Middle : null;

            if (button is { } b && SendMouse(TerminalMouseEventType.Press, b, point, e.KeyModifiers))
            {
                _mouseButtonDown = b;
                _lastMouseReportCell = ScreenCell(point);
                e.Handled = true;
                return;
            }
        }

        if (props.IsLeftButtonPressed)
        {
            _selecting = true;
            _selectionAnchor = PointToCell(point);
            _selectionCaret = _selectionAnchor;
            InvalidateVisual();
        }
        else if (props.IsRightButtonPressed)
        {
            // Right click pastes, matching common terminal behavior.
            _ = PasteAsync();
        }
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        // Report motion to the app in button-event (?1002, only while a button is down) and
        // any-event (?1003, always) modes, but only when a cell boundary is crossed.
        var tracking = _emulator.Modes.Mouse;
        if (!_selecting && _scrollOffset == 0
            && tracking is MouseTracking.ButtonEvent or MouseTracking.AnyEvent)
        {
            bool held = _mouseButtonDown is not null;
            if (tracking == MouseTracking.AnyEvent || held)
            {
                var position = e.GetPosition(this);
                var cell = ScreenCell(position);
                if (cell != _lastMouseReportCell)
                {
                    _lastMouseReportCell = cell;
                    var button = _mouseButtonDown ?? TerminalMouseButton.None;
                    SendMouse(TerminalMouseEventType.Move, button, position, e.KeyModifiers);
                }
            }
            return;
        }

        if (_selecting)
        {
            _selectionCaret = PointToCell(e.GetPosition(this));
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        // Complete an app-reported drag/click with a release event.
        if (_mouseButtonDown is { } down)
        {
            SendMouse(TerminalMouseEventType.Release, down, e.GetPosition(this), e.KeyModifiers);
            _mouseButtonDown = null;
            _lastMouseReportCell = (-1, -1);
            e.Handled = true;
            return;
        }

        if (_selecting)
        {
            _selecting = false;
            // Select-to-copy: releasing a non-empty selection copies it, so the user never
            // needs a copy shortcut (design §8). A plain click has an empty selection and no-ops.
            if (CopyOnSelect)
                _ = CopyAsync();
        }
    }

    /// <summary>Raised when the font size changes via Ctrl+wheel zoom, so the host can persist it.</summary>
    public event Action<double>? FontSizeChanged;

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        // Ctrl+wheel zooms the font instead of scrolling (#21). Changing FontSize recomputes the
        // cell metrics, reflows the grid and resizes the PTY.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            double next = Math.Clamp(_fontSize + (e.Delta.Y > 0 ? 1 : -1), 6, 40);
            if (Math.Abs(next - _fontSize) > 0.01)
            {
                FontSize = next;
                FontSizeChanged?.Invoke(next);
            }
            e.Handled = true;
            return;
        }

        // On the live screen with mouse tracking on, the wheel scrolls the app (htop/btop lists,
        // less, vim) rather than the local scrollback.
        if (_emulator.Modes.Mouse != MouseTracking.None && _scrollOffset == 0 && e.Delta.Y != 0)
        {
            var button = e.Delta.Y > 0 ? TerminalMouseButton.WheelUp : TerminalMouseButton.WheelDown;
            if (SendMouse(TerminalMouseEventType.Press, button, e.GetPosition(this), e.KeyModifiers))
            {
                e.Handled = true;
                return;
            }
        }

        int delta = (int)(e.Delta.Y * 3);
        int maxOffset = _emulator.Screen.ScrollbackCount;
        _scrollOffset = Math.Clamp(_scrollOffset + delta, 0, maxOffset);
        InvalidateVisual();
        ScrollChanged?.Invoke();
        e.Handled = true;
    }

    /// <summary>Maps a pointer position to a 0-based cell within the visible screen.</summary>
    private (int Col, int Row) ScreenCell(Point p)
    {
        int col = Math.Clamp((int)(p.X / _cellWidth), 0, Math.Max(0, _emulator.Columns - 1));
        int row = Math.Clamp((int)(p.Y / _cellHeight), 0, Math.Max(0, _emulator.Rows - 1));
        return (col, row);
    }

    /// <summary>Encodes a mouse event under the active tracking mode and sends it to the PTY.
    /// Returns false when the current mode does not report this event.</summary>
    private bool SendMouse(TerminalMouseEventType type, TerminalMouseButton button, Point p, KeyModifiers mods)
    {
        var (col, row) = ScreenCell(p);
        var bytes = MouseEncoder.Encode(
            type, button, col, row,
            mods.HasFlag(KeyModifiers.Shift),
            mods.HasFlag(KeyModifiers.Alt),
            mods.HasFlag(KeyModifiers.Control),
            _emulator.Modes);

        if (bytes is null || bytes.Length == 0)
            return false;

        UserInput?.Invoke(bytes);
        return true;
    }

    private void ClearSelection()
    {
        _selectionAnchor = null;
        _selectionCaret = null;
    }

    private async void OpenLink(string url)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
            return;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            await top.Launcher.LaunchUriAsync(uri);
    }

    public async Task CopyAsync()
    {
        var text = GetSelectedText();
        if (string.IsNullOrEmpty(text))
            return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(text);
    }

    public async Task PasteAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;
        var text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrEmpty(text))
            return;

        var payload = new StringBuilder();
        if (_emulator.Modes.BracketedPaste)
            payload.Append("\x1b[200~");
        payload.Append(text.Replace("\r\n", "\r").Replace('\n', '\r'));
        if (_emulator.Modes.BracketedPaste)
            payload.Append("\x1b[201~");

        UserInput?.Invoke(Encoding.UTF8.GetBytes(payload.ToString()));
    }
}
