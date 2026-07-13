using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;
using Avalonia.Threading;
using VelaShell.Terminal.Emulation;
using VelaShell.Terminal.Semantics;

// ReSharper disable AsyncVoidMethod
// ReSharper disable AsyncVoidEventHandlerMethod
// ReSharper disable UnusedMember.Global

namespace VelaShell.Terminal.Rendering;

/// <summary>
/// A fully self-drawn terminal control. It owns a <see cref="TerminalEmulator" />, renders the
/// screen buffer with cached glyph runs, and translates keyboard / mouse / clipboard input into
/// host bytes. Implements <see cref="ITerminalEmulator" /> so it drops straight into the existing
/// <c>SshTerminalBridge</c> and views without any changes to the wiring.
/// </summary>
public sealed partial class VelaTerminalControl : Control, ITerminalEmulator
{
    private static readonly ImmutableSolidColorBrush BellFlashBrush = new(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));

    // ---- Search highlights (spec §5.3: 命中项高亮) --------------------------

    private static readonly Rgba SearchMatchBg = new(0x59, 0xFD, 0xCB, 0x6E);   // amber, ~35%
    private static readonly Rgba SearchCurrentBg = new(0x73, 0x00, 0xD4, 0xAA); // accent, ~45%
    private readonly Dictionary<uint, ImmutableSolidColorBrush> _brushCache = [];

    // Cache of shaped, colored glyphs keyed by (rune, combining, foreground, style). Terminal
    // output draws from a tiny alphabet, so hit rate is ~100% and per-frame text shaping —
    // the dominant render cost — effectively disappears. Cleared when the font/size changes.
    private readonly Dictionary<GlyphKey, FormattedText> _glyphCache = [];
    private readonly List<char> _runChars = [];
    private readonly List<GlyphInfo> _runGlyphs = [];
    private readonly SemanticMatcher _semanticMatcher = new();

    // Client-side semantic coloring (URLs, IPs, error/warning/success words, option flags, numbers)
    // for text the remote program left in the default color, so plain logs/MOTD get highlighted
    // without ever clobbering explicit SGR colors (ls --color, git, etc.). Regex results are cached
    // by line text since the visible lines are re-scanned every frame (cursor blink, output).
    private readonly Dictionary<string, IReadOnlyList<SemanticSpan>> _semanticSpanCache = [];

    // ---- Glyph-run batching -------------------------------------------------
    // Each visible line is drawn as a handful of GlyphRuns — one per contiguous run of cells
    // sharing the same font style and foreground — instead of one DrawText per cell. A full-screen
    // TUI (htop/vim/nano) has thousands of cells; issuing one draw op per cell is what made the
    // cursor feel sluggish, since every frame recorded thousands of draw operations on the UI
    // thread. Advances are pinned to the cell width so monospace alignment is exact, spaces are
    // folded into advances (never drawn), and any glyph the primary font lacks (CJK, symbols) or
    // any combining sequence falls back to the per-cell FormattedText path so fallback still works.
    private readonly GlyphTypeface?[] _styleTypefaces = new GlyphTypeface?[4];
    private double _baselineOffset;

    private DateTime _bellFlashUntil = DateTime.MinValue;
    private double _cellHeight = 16;
    private double _cellWidth = 8;
    private DispatcherTimer? _cursorBlinkTimer;
    private bool _cursorBlinkVisible = true;

    // Latches on the first time the batched GlyphRun path throws at runtime (unexpected platform
    // behavior), permanently reverting to the proven per-cell FormattedText path so a rendering
    // API surprise can never leave text missing — it only forfeits the batching speedup.
    private bool _glyphRunUnsupported;
    private double _glyphYOffset;
    private bool _hasFocus;

    // ---- IME ------------------------------------------------------------------

    private TerminalImeClient? _imeClient;
    private (int Col, int Row) _lastMouseReportCell = (-1, -1);
    private int _lastScrollbackCount; // scrollback size at the previous output update

    // Mouse reporting to the app (htop/btop/vim/tmux): the button held after a reported press, and
    // the last cell reported, so drag/motion only emits when the cell actually changes.
    private TerminalMouseButton? _mouseButtonDown;
    private ImmutableSolidColorBrush? _runBrush;
    private uint _runFg;
    private int _runPrevCol;
    private int _runPrevWidth;
    private int _runStartCol;
    private int _runStyle = -1; // -1 = no active run; else (bold?1) | (italic?2)

    private int _scrollOffset; // lines scrolled up from the bottom (0 = live)

    /// <summary>Search spans per absolute buffer row; the current hit is tinted differently.</summary>
    private Dictionary<int, List<(int Start, int End, bool Current)>>? _searchHighlights;

    private bool _selecting;

    // Selection (linear), in absolute-row space.
    private (int Row, int Col)? _selectionAnchor;
    private (int Row, int Col)? _selectionCaret;
    private bool _styleTypefacesReady;

    public VelaTerminalControl()
        : this(new(120, 32)) { }

    private VelaTerminalControl(TerminalEmulator emulator)
    {
        Emulator = emulator;
        Focusable = true;
        ClipToBounds = true;
        ApplyDesignPalette(Emulator.Palette);
        RecomputeMetrics();
        Emulator.Updated += OnEmulatorUpdated;
        Emulator.Response += bytes => UserInput?.Invoke(bytes); // 协议自动应答:发往 PTY 但不算用户键入(不进 TypedInput)。
        Emulator.Bell += OnBell;
        Emulator.ClipboardWriteRequested += OnRemoteClipboardWrite;

        // 终端配色跟随应用主题(暗=Dracula,亮=Solarized Light);切换主题时重灌调色板并重绘。
        ActualThemeVariantChanged += (_, _) => ApplyThemePalette();
        AddHandler(TextInputMethodClientRequestedEvent, OnTextInputMethodClientRequested);
    }

    /// <summary>Toggles client-side semantic highlighting of default-colored output.</summary>
    private bool SemanticHighlightingEnabled { get; } = true;

    /// <summary>When true, releasing a selection copies it to the clipboard automatically.</summary>
    public bool CopyOnSelect { get; set; } = true;

    // ---- 设置 → 终端(行为选项,由 ApplyLiveTerminalSettings 下发) ----------

    /// <summary>Cursor shape: "bar" (vertical line), "block" (filled cell) or "underline".</summary>
    public string CursorStyle
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                InvalidateVisual();
            }
        }
        // 默认值与设置模型(TerminalBehaviorOptions.CursorStyle)一致;运行时由
        // ApplyLiveTerminalSettings 下发,这里不声明独立的业务默认值(设置审计 C-07)。
    } = "bar";

    /// <summary>Whether the focused cursor blinks (设置 → 终端 → 光标闪烁).</summary>
    public bool CursorBlink
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }
            field = value;
            UpdateCursorBlinkTimer();
        }
    } = true;

    /// <summary>
    /// Line-height multiplier (1.0 = font natural height). Extra space is distributed
    /// evenly above/below the glyphs.
    /// </summary>
    public double LineHeight
    {
        get;
        set
        {
            double clamped = Math.Clamp(double.IsFinite(value) && value > 0 ? value : 1.0, 0.8, 2.0);
            if (Math.Abs(clamped - field) < 0.001)
            {
                return;
            }
            field = clamped;
            RecomputeMetrics();
            RelayoutFromBounds();
            InvalidateVisual();
        }
    } = 1.0;

    /// <summary>Right-click pastes the clipboard (off = right-click does nothing).</summary>
    public bool RightClickPaste { get; set; } = true;

    /// <summary>Strip trailing whitespace from each copied line.</summary>
    public bool TrimTrailingWhitespaceOnCopy { get; set; } = true;

    /// <summary>Double-click selects the word under the pointer.</summary>
    public bool DoubleClickSelectsWord { get; set; } = true;

    /// <summary>Ask before pasting text that contains newlines (accidental multi-line runs).</summary>
    public bool ConfirmMultilinePaste { get; set; } = true;

    /// <summary>
    /// Host-provided confirmation for multi-line pastes (returns false to abort).
    /// Null = never ask, the control itself cannot show dialogs.
    /// </summary>
    public Func<string, Task<bool>>? MultilinePasteConfirmation { get; set; }

    /// <summary>
    /// 选中时 Ctrl+C 复制:开 = 有选区时 Ctrl+C 复制选中内容而不发送中断(无选区
    /// 仍发送中断);关 = Ctrl+C 始终作为中断信号 ^C 发往 PTY。
    /// </summary>
    public bool CtrlCCopiesWhenSelected { get; set; }

    /// <summary>Typing snaps the view back to the live bottom.</summary>
    public bool ScrollOnKeystroke { get; set; } = true;

    /// <summary>
    /// New output snaps a history-scrolled view back to the bottom; off keeps the
    /// user's history view pinned (#15 behavior).
    /// </summary>
    public bool ScrollOnOutput { get; set; }

    /// <summary>BEL handling: "system" (beep), "none" (silent) or "visual" (screen flash).</summary>
    public string BellMode { get; set; } = "system";

    /// <summary>
    /// Enables the OS input method (Chinese/Japanese/Korean composition). Off = the
    /// terminal never provides an IME client.
    /// </summary>
    public bool ImeEnabled { get; set; } = true;

    /// <summary>Maximum lines that can be scrolled up (size of the scrollback history).</summary>
    public int MaxScrollOffset => Emulator.Screen.ScrollbackCount;

    /// <summary>Lines currently scrolled up from the live bottom (0 = following output).</summary>
    public int ScrollOffset
    {
        get => _scrollOffset;
        set
        {
            int clamped = Math.Clamp(value, 0, MaxScrollOffset);
            if (clamped == _scrollOffset)
            {
                return;
            }
            _scrollOffset = clamped;
            InvalidateVisual();
            ScrollChanged?.Invoke();
        }
    }

    /// <summary>
    /// 用户自定义终端配色(设置 → 外观 → 终端颜色/ANSI 调色板):只包含用户实际
    /// 改过的颜色,叠加在主题调色板之上;null 或空对象 = 完全跟随主题。
    /// </summary>
    public TerminalPaletteOverrides? PaletteOverrides
    {
        get;
        set
        {
            field = value;
            ApplyThemePalette();
        }
    }

    private TerminalEmulator Emulator { get; }

    public TerminalType TerminalType
    {
        get => Emulator.Type;
        set => Emulator.SetTerminalType(value);
    }

    public FontFamily FontFamily
    {
        get;
        set
        {
            field = value;
            RecomputeMetrics();
            RelayoutFromBounds();
            InvalidateVisual();
        }
    } = new("Cascadia Mono, Consolas, JetBrains Mono, Microsoft YaHei, Segoe UI, monospace");

    public double FontSize
    {
        get;
        set
        {
            field = value;
            RecomputeMetrics();
            RelayoutFromBounds();
            InvalidateVisual();
        }
    } = 14;

    // ---- ITerminalEmulator --------------------------------------------------

    public event Action<byte[]>? UserInput;

    public event Action<int, int>? PtySizeChanged;

    public void Feed(byte[] data) => Emulator.Feed(data);

    public void Resize(int cols, int rows)
    {
        Emulator.Resize(cols, rows);
        _scrollOffset = 0;
        _lastScrollbackCount = Emulator.Screen.ScrollbackCount;
        // Row indexes in the selection are absolute and shift on resize; drop it rather than
        // let a stale range mark (or copy) the wrong text.
        ClearSelection();
        InvalidateVisual();
        ScrollChanged?.Invoke();
    }

    public void WriteInput(byte[] data) => SendTypedInput(data);

    public event Action<byte[]>? TypedInput;

    /// <summary>
    /// 用户产生的输入(键盘/IME/鼠标上报/粘贴/程序化写入)统一出口:发往 PTY
    /// (<see cref="UserInput" />)并同步通知补全跟踪(<see cref="TypedInput" />)。
    /// 终端协议自动应答不走这里(见构造函数 Response 挂接)。
    /// </summary>
    private void SendTypedInput(byte[] data)
    {
        TypedInput?.Invoke(data);
        UserInput?.Invoke(data);
    }

    public string GetBufferLine(int row) => Emulator.Screen.ActiveLine(row).GetText();

    public int CursorRow => Emulator.CursorY;

    public int CursorCol => Emulator.CursorX;

    public int ScrollbackLines
    {
        get => Emulator.Screen.MaxScrollback;
        set => Emulator.Screen.MaxScrollback = value;
    }

    public Control Control => this;

    public int Columns => Emulator.Columns;

    public int Rows => Emulator.Rows;

    // Legacy interface members: kept for source compatibility with existing bindings.
    public ScrollbackBuffer ScrollbackBuffer { get; } = new(1);

    public int TotalLines => Emulator.Screen.TotalRows;

    public int ViewportRow => Math.Max(0, Emulator.Screen.TotalRows - Emulator.Rows - _scrollOffset);

    public void Dispose()
    {
        Emulator.Updated -= OnEmulatorUpdated;
        Emulator.Bell -= OnBell;
        Emulator.ClipboardWriteRequested -= OnRemoteClipboardWrite;
        _cursorBlinkTimer?.Stop();
        _cursorBlinkTimer = null;
    }

    /// <summary>
    /// Raised (on the UI thread) whenever the remote sends BEL — hosts use it for
    /// tab-flash alerts.
    /// </summary>
    public event Action? BellRang;

    /// <summary>Raised whenever the scroll position or scrollable extent changes.</summary>
    public event Action? ScrollChanged;

    /// <summary>
    /// Computes the scroll offset to keep the same history content visible after new lines were
    /// pushed into scrollback. At the live bottom (offset 0) the view follows output; when the
    /// user has scrolled up, the offset grows with the scrollback so the view stays pinned.
    /// </summary>
    internal static int PinScrollOffset(int currentOffset, int lastScrollback, int newScrollback)
    {
        if (currentOffset <= 0)
        {
            return 0;
        }
        int growth = newScrollback - lastScrollback;
        int pinned = growth > 0 ? currentOffset + growth : currentOffset;
        return Math.Clamp(pinned, 0, Math.Max(0, newScrollback));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // ActualThemeVariant 在挂树后才最终确定,构造时灌的是暗色缺省。
        ApplyThemePalette();
    }

    private void ApplyThemePalette()
    {
        ApplyDesignPalette(Emulator.Palette, ActualThemeVariant == ThemeVariant.Light);
        ApplyPaletteOverrides(Emulator.Palette);
        InvalidateVisual();
    }

    private void ApplyPaletteOverrides(TerminalPalette palette)
    {
        if (PaletteOverrides is not { } o)
        {
            return;
        }
        if (o.Foreground is { } fg)
        {
            palette.DefaultForeground = fg;
        }
        if (o.Background is { } bg)
        {
            palette.DefaultBackground = bg;
        }
        if (o.Cursor is { } cur)
        {
            palette.CursorColor = cur;
        }
        if (o.Selection is { } sel)
        // 用户给的是不带透明度的选区色;按既有方案以 ~35% 透明叠加,避免盖住文字。
        {
            palette.SelectionBackground = new(0x59, sel.R, sel.G, sel.B);
        }
        for (int i = 0; i < TerminalPaletteOverrides.AnsiCount; i++)
        {
            if (o.Ansi[i] is { } c)
            {
                palette.SetAnsi(i, c);
            }
        }
    }

    // ReSharper disable once EventNeverSubscribedTo.Global
    public event Action<string>? TitleChanged
    {
        add => Emulator.TitleChanged += value;
        remove => Emulator.TitleChanged -= value;
    }

    /// <summary>Sets the host-output charset (UTF-8 default; GBK/Big5/etc. supported).</summary>
    public void SetEncoding(Encoding encoding) => Emulator.SetEncoding(encoding);

    /// <summary>OSC 52:远端 yank(tmux/vim)写入系统剪贴板;事件来自 feed 线程,落板走 UI 线程。</summary>
    private void OnRemoteClipboardWrite(string text)
    {
        // ReSharper disable once AsyncVoidMethod
        Dispatcher.UIThread.Post(async void () =>
        {
            IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(text);
            }
        });
    }

    // ---- Bell (设置 → 终端 → 提示音与通知) ----------------------------------

    /// <summary>
    /// Fires on the feed thread; marshals to the UI thread, then flashes / beeps per
    /// <see cref="BellMode" /> and notifies the host (tab flash).
    /// </summary>
    private void OnBell()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(OnBell);
            return;
        }
        BellRang?.Invoke();
        if (BellMode == "visual")
        {
            _bellFlashUntil = DateTime.UtcNow.AddMilliseconds(120);
            InvalidateVisual();
            DispatcherTimer.RunOnce(InvalidateVisual, TimeSpan.FromMilliseconds(140));
        }
        else if (BellMode == "system" && OperatingSystem.IsWindows())
        {
            NativeMethods.MessageBeep(0);
        }
    }

    // ---- Cursor blink --------------------------------------------------------

    /// <summary>
    /// Runs the blink timer only while focused with blinking enabled; otherwise the
    /// cursor stays solid and no per-500ms repaints happen.
    /// </summary>
    private void UpdateCursorBlinkTimer()
    {
        bool shouldRun = _hasFocus && (CursorBlink || Emulator.Modes.CursorBlink);
        if (shouldRun)
        {
            _cursorBlinkTimer ??= new(TimeSpan.FromMilliseconds(530), DispatcherPriority.Background,
                (_, _) =>
                {
                    _cursorBlinkVisible = !_cursorBlinkVisible;
                    InvalidateVisual();
                });
            if (!_cursorBlinkTimer.IsEnabled)
            {
                _cursorBlinkTimer.Start();
            }
        }
        else
        {
            _cursorBlinkTimer?.Stop();
            if (_cursorBlinkVisible)
            {
                return;
            }
            _cursorBlinkVisible = true;
            InvalidateVisual();
        }
    }

    /// <summary>Typing resets the blink phase so the cursor is visible right where input lands.</summary>
    private void ResetCursorBlink()
    {
        if (_cursorBlinkTimer is { IsEnabled: true } timer)
        {
            timer.Stop();
            timer.Start();
        }
        if (!_cursorBlinkVisible)
        {
            _cursorBlinkVisible = true;
            InvalidateVisual();
        }
    }

    private void OnEmulatorUpdated()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyOutputUpdate();
        }
        else
        {
            Dispatcher.UIThread.Post(ApplyOutputUpdate);
        }
    }

    private void ApplyOutputUpdate()
    {
        // Follow output only when already at the bottom; otherwise keep the user's history
        // view pinned so background output doesn't yank them back down (fixes #15) — unless
        // 设置 → 终端 → 有输出时自动滚动 is on, which snaps the view back to the live bottom.
        int scrollback = Emulator.Screen.ScrollbackCount;
        if (ScrollOnOutput && _scrollOffset > 0 && scrollback > _lastScrollbackCount)
        {
            _scrollOffset = 0;
        }
        else
        {
            _scrollOffset = PinScrollOffset(_scrollOffset, _lastScrollbackCount, scrollback);
        }
        _lastScrollbackCount = scrollback;
        InvalidateVisual();
        ScrollChanged?.Invoke();
        _imeClient?.NotifyCursorMoved();
    }

    /// <summary>
    /// Hands the OS input method a client anchored at the terminal cursor, so the IME
    /// candidate window (Chinese/Japanese/Korean composition) opens next to where the text will
    /// land instead of at the window corner (#14b).
    /// </summary>
    private void OnTextInputMethodClientRequested(object? sender, TextInputMethodClientRequestedEventArgs e)
    {
        if (!ImeEnabled)
        {
            return;
        }
        _imeClient ??= new(this);
        e.Client = _imeClient;
    }

    /// <summary>光标单元格在控件坐标系中的矩形(命令补全弹层锚点,与 IME 光标同一套计算)。</summary>
    public Rect GetCursorRect() => GetImeCursorRect();

    /// <summary>The cursor cell's rectangle in control coordinates (same math as RenderCursor).</summary>
    private Rect GetImeCursorRect()
    {
        TerminalScreen screen = Emulator.Screen;
        int topAbsolute = screen.TotalRows - screen.Rows - _scrollOffset;
        int cursorAbsolute = screen.TotalRows - screen.Rows + screen.CursorY;
        int screenRow = cursorAbsolute - topAbsolute;
        return new(screen.CursorX * _cellWidth, screenRow * _cellHeight, _cellWidth, _cellHeight);
    }

    // ---- Palette ------------------------------------------------------------

    /// <summary>
    /// Seeds the palette for the given theme variant (跟随应用主题的默认配色):
    /// dark = Dracula(官方 Windows Terminal 方案),
    /// light = Solarized Light(与设置 → 外观 内置方案同一套色值)。
    /// </summary>
    private static void ApplyDesignPalette(TerminalPalette palette, bool light = false)
    {
        if (light)
        {
            palette.DefaultForeground = Rgba.FromRgb(0x65, 0x7B, 0x83); // base00
            palette.DefaultBackground = Rgba.FromRgb(0xFD, 0xF6, 0xE3); // base3
            palette.CursorColor = Rgba.FromRgb(0x65, 0x7B, 0x83);
            palette.SelectionBackground = new(0x40, 0x58, 0x6E, 0x75);  // base01 @25%(方案原生选区 base2 与背景过近,取更可辨的半透明灰蓝)
            palette.SetAnsi(0, Rgba.FromRgb(0x07, 0x36, 0x42));         // black  = base02
            palette.SetAnsi(1, Rgba.FromRgb(0xDC, 0x32, 0x2F));         // red
            palette.SetAnsi(2, Rgba.FromRgb(0x85, 0x99, 0x00));         // green
            palette.SetAnsi(3, Rgba.FromRgb(0xB5, 0x89, 0x00));         // yellow
            palette.SetAnsi(4, Rgba.FromRgb(0x26, 0x8B, 0xD2));         // blue
            palette.SetAnsi(5, Rgba.FromRgb(0xD3, 0x36, 0x82));         // magenta
            palette.SetAnsi(6, Rgba.FromRgb(0x2A, 0xA1, 0x98));         // cyan
            palette.SetAnsi(7, Rgba.FromRgb(0xEE, 0xE8, 0xD5));         // white  = base2
            palette.SetAnsi(8, Rgba.FromRgb(0x58, 0x6E, 0x75));         // bright black = base01
            palette.SetAnsi(9, Rgba.FromRgb(0xCB, 0x4B, 0x16));         // bright red (orange)
            palette.SetAnsi(10, Rgba.FromRgb(0x85, 0x99, 0x00));
            palette.SetAnsi(11, Rgba.FromRgb(0xB5, 0x89, 0x00));
            palette.SetAnsi(12, Rgba.FromRgb(0x26, 0x8B, 0xD2));
            palette.SetAnsi(13, Rgba.FromRgb(0x6C, 0x71, 0xC4));        // bright magenta (violet)
            palette.SetAnsi(14, Rgba.FromRgb(0x93, 0xA1, 0xA1));        // bright cyan = base1
            palette.SetAnsi(15, Rgba.FromRgb(0xFD, 0xF6, 0xE3));        // bright white = base3
            return;
        }
        palette.DefaultForeground = Rgba.FromRgb(0xF8, 0xF8, 0xF2);
        palette.DefaultBackground = Rgba.FromRgb(0x28, 0x2A, 0x36);
        palette.CursorColor = Rgba.FromRgb(0xF8, 0xF8, 0xF2);
        palette.SelectionBackground = new(0x99, 0x44, 0x47, 0x5A); // dracula selection
        palette.SetAnsi(0, Rgba.FromRgb(0x21, 0x22, 0x2C));        // black
        palette.SetAnsi(1, Rgba.FromRgb(0xFF, 0x55, 0x55));        // red
        palette.SetAnsi(2, Rgba.FromRgb(0x50, 0xFA, 0x7B));        // green
        palette.SetAnsi(3, Rgba.FromRgb(0xF1, 0xFA, 0x8C));        // yellow
        palette.SetAnsi(4, Rgba.FromRgb(0xBD, 0x93, 0xF9));        // blue (dracula purple)
        palette.SetAnsi(5, Rgba.FromRgb(0xFF, 0x79, 0xC6));        // magenta (dracula pink)
        palette.SetAnsi(6, Rgba.FromRgb(0x8B, 0xE9, 0xFD));        // cyan
        palette.SetAnsi(7, Rgba.FromRgb(0xF8, 0xF8, 0xF2));        // white
        palette.SetAnsi(8, Rgba.FromRgb(0x62, 0x72, 0xA4));        // bright black (comment)
        palette.SetAnsi(9, Rgba.FromRgb(0xFF, 0x6E, 0x6E));
        palette.SetAnsi(10, Rgba.FromRgb(0x69, 0xFF, 0x94));
        palette.SetAnsi(11, Rgba.FromRgb(0xFF, 0xFF, 0xA5));
        palette.SetAnsi(12, Rgba.FromRgb(0xD6, 0xAC, 0xFF));
        palette.SetAnsi(13, Rgba.FromRgb(0xFF, 0x92, 0xDF));
        palette.SetAnsi(14, Rgba.FromRgb(0xA4, 0xFF, 0xFF));
        palette.SetAnsi(15, Rgba.FromRgb(0xFF, 0xFF, 0xFF));
    }

    private ImmutableSolidColorBrush BrushFor(Rgba c)
    {
        if (_brushCache.TryGetValue(c.Packed, out ImmutableSolidColorBrush? brush))
        {
            return brush;
        }
        brush = new(Color.FromArgb(c.A, c.R, c.G, c.B));
        _brushCache[c.Packed] = brush;
        return brush;
    }

    /// <summary>
    /// Returns a cached <see cref="FormattedText" /> for a single cell's glyph. Each glyph is
    /// still drawn at its own grid position by the caller, so wide (CJK) cells and monospace
    /// alignment are preserved exactly; only the expensive shaping is amortized.
    /// </summary>
    private FormattedText GlyphFor(in TerminalCell cell, Rgba fg, bool bold, bool italic)
    {
        int style = (bold ? 1 : 0) | (italic ? 2 : 0);
        var key = new GlyphKey(cell.Rune, cell.Combining, fg.Packed, style);
        if (_glyphCache.TryGetValue(key, out FormattedText? cached))
        {
            return cached;
        }

        // Bound the cache; a full clear is fine because it refills within a frame or two.
        if (_glyphCache.Count > 8192)
        {
            _glyphCache.Clear();
        }
        var typeface = new Typeface(FontFamily,
            italic ? FontStyle.Italic : FontStyle.Normal,
            bold ? FontWeight.Bold : FontWeight.Normal);
        var ft = new FormattedText(cell.GetText(), CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, typeface, FontSize, BrushFor(fg));
        _glyphCache[key] = ft;
        return ft;
    }

    // ---- Metrics & layout ---------------------------------------------------

    private void RecomputeMetrics()
    {
        var typeface = new Typeface(FontFamily);
        var probe = new FormattedText("0", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, FontSize, Brushes.White);
        _cellWidth = Math.Max(1, Math.Round(probe.WidthIncludingTrailingWhitespace));
        // 行高倍数(设置 → 终端 → 行高):多出的空间上下均分,字形垂直居中。
        _cellHeight = Math.Max(1, Math.Ceiling(probe.Height * LineHeight));
        _glyphYOffset = Math.Max(0, (_cellHeight - probe.Height) / 2);
        _baselineOffset = probe.Baseline + _glyphYOffset;

        // Cached glyphs are bound to the old typeface/size; drop them on any metric change.
        _glyphCache.Clear();
        _styleTypefacesReady = false;
    }

    /// <summary>
    /// Resolves (and caches) the primary <see cref="GlyphTypeface" /> for a bold/italic
    /// style combination, used by the batched glyph-run path. Null when the platform can't supply
    /// one, in which case the caller falls back to the per-cell FormattedText path.
    /// </summary>
    private GlyphTypeface? StyleTypeface(int style)
    {
        if (!_styleTypefacesReady)
        {
            for (int s = 0; s < 4; s++)
            {
                var tf = new Typeface(FontFamily,
                    (s & 2) != 0 ? FontStyle.Italic : FontStyle.Normal,
                    (s & 1) != 0 ? FontWeight.Bold : FontWeight.Normal);
                try
                {
                    _styleTypefaces[s] = tf.GlyphTypeface;
                }
                catch
                {
                    _styleTypefaces[s] = null;
                }
            }
            _styleTypefacesReady = true;
        }
        return _styleTypefaces[style];
    }

    /// <summary>
    /// Adds one glyph to the pending run, starting a fresh run (after flushing the current
    /// one) whenever the style or foreground changes. Columns skipped since the previous glyph
    /// (spaces, blanks) are folded into the previous glyph's advance so alignment stays exact.
    /// </summary>
    private void AppendGlyph(DrawingContext context, double y, int style, Rgba fg, int col, int width, ushort glyphId, char ch)
    {
        if (_runGlyphs.Count > 0 && (style != _runStyle || fg.Packed != _runFg))
        {
            FlushGlyphRun(context, y);
        }
        if (_runGlyphs.Count == 0)
        {
            _runStyle = style;
            _runFg = fg.Packed;
            _runBrush = BrushFor(fg);
            _runStartCol = col;
        }
        else
        {
            int gapCells = col - (_runPrevCol + _runPrevWidth);
            if (gapCells > 0)
            {
                GlyphInfo last = _runGlyphs[^1];
                _runGlyphs[^1] = new(last.GlyphIndex, last.GlyphCluster,
                    last.GlyphAdvance + gapCells * _cellWidth, last.GlyphOffset);
            }
        }
        _runGlyphs.Add(new(glyphId, _runChars.Count, width * _cellWidth));
        _runChars.Add(ch);
        _runPrevCol = col;
        _runPrevWidth = width;
    }

    /// <summary>Emits the pending glyph run (if any) as a single DrawGlyphRun and resets the buffers.</summary>
    private void FlushGlyphRun(DrawingContext context, double y)
    {
        if (_runGlyphs.Count == 0)
        {
            return;
        }
        GlyphTypeface? gtf = _runStyle >= 0 ? _styleTypefaces[_runStyle] : null;
        if (gtf is not null && _runBrush is not null)
        {
            try
            {
                var run = new GlyphRun(gtf, FontSize, _runChars.ToArray().AsMemory(),
                    _runGlyphs.ToArray(),
                    new Point(_runStartCol * _cellWidth, y + _baselineOffset));
                context.DrawGlyphRun(_runBrush, run);
            }
            catch
            {
                // Should never happen, but if the platform rejects our glyph run, stop batching
                // for the rest of the session and repaint so everything re-renders via the
                // per-cell FormattedText path (correct, just slower).
                _glyphRunUnsupported = true;
                Dispatcher.UIThread.Post(InvalidateVisual);
            }
        }
        _runGlyphs.Clear();
        _runChars.Clear();
        _runStyle = -1;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Size result = base.ArrangeOverride(finalSize);
        ApplyLayoutSize(finalSize);
        return result;
    }

    private void RelayoutFromBounds() => ApplyLayoutSize(Bounds.Size);

    private void ApplyLayoutSize(Size size)
    {
        if (_cellWidth <= 0 || _cellHeight <= 0)
        {
            return;
        }
        int cols = (int)(size.Width / _cellWidth);
        int rows = (int)(size.Height / _cellHeight);

        // Ignore early/degenerate layout passes (zero or sub-cell size). Collapsing the grid
        // to a single column here is what made the login banner render one char per line: every
        // subsequent character autowrapped. Keep the current (or default 120x32) grid until a
        // real size arrives.
        if (cols < 2 || rows < 2)
        {
            return;
        }
        if (cols == Emulator.Columns && rows == Emulator.Rows)
        {
            return;
        }

        // The local grid reflows immediately so dragging feels live, and the PTY is told
        // right away too — the mainstream approach. Local and remote must stay in lockstep:
        // an earlier debounced notify let the remote's beliefs (readline's prompt row math)
        // lag many reflows behind, so its relative cursor moves and erasures landed on the
        // wrong rows and progressively destroyed buffer content. The transport layer
        // serializes the sends in order, collapsing bursts to the latest size.
        Emulator.Resize(cols, rows);
        _scrollOffset = Math.Clamp(_scrollOffset, 0, Emulator.Screen.ScrollbackCount);
        _lastScrollbackCount = Emulator.Screen.ScrollbackCount;
        // Reflow shifts absolute rows; a stale selection would mark (and copy) the wrong text.
        ClearSelection();
        InvalidateVisual();
        ScrollChanged?.Invoke();
        PtySizeChanged?.Invoke(cols, rows);
    }

    // ---- Rendering ----------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        TerminalScreen screen = Emulator.Screen;
        TerminalPalette palette = Emulator.Palette;
        context.FillRectangle(BrushFor(palette.DefaultBackground), new(Bounds.Size));
        int rows = screen.Rows;
        int cols = screen.Columns;
        int topAbsolute = Math.Max(0, screen.TotalRows - rows - _scrollOffset);
        ((int Row, int Col) Start, (int Row, int Col) End)? sel = NormalizedSelection();
        for (int screenRow = 0; screenRow < rows; screenRow++)
        {
            int absoluteRow = topAbsolute + screenRow;
            if (absoluteRow >= screen.TotalRows)
            {
                break;
            }
            TerminalRow line = screen.ViewLine(absoluteRow);
            double y = screenRow * _cellHeight;
            RenderLine(context, palette, line, cols, y, absoluteRow, sel);
        }
        if (_scrollOffset == 0)
        {
            RenderCursor(context, screen, palette, topAbsolute);
        }

        // Visual bell: a brief translucent flash over the whole terminal (§终端 → 视觉闪烁).
        if (_bellFlashUntil > DateTime.UtcNow)
        {
            context.FillRectangle(BellFlashBrush, new(Bounds.Size));
        }
    }

    private void RenderLine(DrawingContext context,
        TerminalPalette palette,
        TerminalRow line,
        int cols,
        double y,
        int absoluteRow,
        ((int Row, int Col) Start, (int Row, int Col) End)? sel)
    {
        SemanticKind?[]? semantic = SemanticHighlightingEnabled ? ComputeSemanticColumns(line, cols) : null;
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
            bool inverse = (cell.Flags & CellFlags.Inverse) != 0 ^ Emulator.Modes.ReverseVideo;
            bool bold = (cell.Flags & CellFlags.Bold) != 0;
            Rgba fg = palette.Resolve(cell.Foreground, false, bold);
            Rgba bg = palette.Resolve(cell.Background, true, false);
            if (inverse)
            {
                (fg, bg) = (bg, fg);
            }
            if (IsSelected(sel, absoluteRow, col))
            {
                bg = palette.SelectionBackground;
            }
            if (_searchHighlights is not null &&
                _searchHighlights.TryGetValue(absoluteRow, out List<(int Start, int End, bool Current)>? searchSpans))
            {
                foreach ((int Start, int End, bool Current) in searchSpans)
                {
                    if (col >= Start && col < End)
                    {
                        bg = Current ? SearchCurrentBg : SearchMatchBg;
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
            {
                context.FillRectangle(BrushFor(bg), cellRect);
            }

            // A blank/space/invisible cell draws no glyph; it just leaves a gap the next run's
            // advance absorbs. Everything else is batched into a GlyphRun when the primary font
            // covers it, or falls back to a per-cell FormattedText draw (CJK, symbols, combining).
            if (cell.Rune != 0 && cell.Rune != ' ' && (cell.Flags & CellFlags.Invisible) == 0)
            {
                bool italic = (cell.Flags & CellFlags.Italic) != 0;
                int style = (bold ? 1 : 0) | (italic ? 2 : 0);
                if (!_glyphRunUnsupported && cell.Combining is null && cell.Rune <= 0xFFFF && StyleTypeface(style) is { } gtf && gtf.CharacterToGlyphMap.TryGetGlyph(cell.Rune, out ushort glyphId))
                {
                    AppendGlyph(context, y, style, fg, col, width, glyphId, (char)cell.Rune);
                }
                else
                {
                    FlushGlyphRun(context, y);
                    FormattedText ft = GlyphFor(cell, fg, bold, italic);
                    context.DrawText(ft, new(col * _cellWidth, y + _glyphYOffset));
                }
            }
            if ((cell.Flags & (CellFlags.Underline | CellFlags.DoubleUnderline)) != 0 || semanticUnderline)
            {
                double uy = y + _cellHeight - 1.5;
                context.DrawLine(new Pen(BrushFor(fg)),
                    new(col * _cellWidth, uy), new((col + width) * _cellWidth, uy));
            }
            if ((cell.Flags & CellFlags.Strikethrough) != 0)
            {
                double sy = y + _cellHeight / 2;
                context.DrawLine(new Pen(BrushFor(fg)),
                    new(col * _cellWidth, sy), new((col + width) * _cellWidth, sy));
            }
            col += width;
        }

        // Emit whatever glyphs remain batched for this line (runs never cross line boundaries).
        FlushGlyphRun(context, y);
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
        {
            if (line[i].Rune != 0)
            {
                lastNonBlank = i;
            }
        }
        if (lastNonBlank < 0)
        {
            return null;
        }
        var sb = new StringBuilder(lastNonBlank + 1);
        var colByChar = new List<int>(lastNonBlank + 1);
        for (int i = 0; i <= lastNonBlank; i++)
        {
            TerminalCell cell = line[i];
            if (cell.IsWideTrailing)
            {
                continue;
            }
            int before = sb.Length;
            cell.AppendText(sb);
            for (int k = before; k < sb.Length; k++)
            {
                colByChar.Add(i);
            }
        }
        IReadOnlyList<SemanticSpan> spans = SemanticSpansFor(sb.ToString());
        if (spans.Count == 0)
        {
            return null;
        }
        var byColumn = new SemanticKind?[cols];
        foreach (SemanticSpan span in spans)
        {
            int end = Math.Min(span.End, colByChar.Count);
            for (int ci = span.Start; ci < end; ci++)
            {
                int c = colByChar[ci];
                if (c >= 0 && c < cols)
                {
                    byColumn[c] = span.Kind;
                }
            }
        }
        return byColumn;
    }

    private IReadOnlyList<SemanticSpan> SemanticSpansFor(string text)
    {
        if (_semanticSpanCache.TryGetValue(text, out IReadOnlyList<SemanticSpan>? cached))
        {
            return cached;
        }

        // Bound the cache; terminal output has huge line variety, so just reset when it grows.
        if (_semanticSpanCache.Count > 1024)
        {
            _semanticSpanCache.Clear();
        }
        IReadOnlyList<SemanticSpan> spans = SemanticMatcher.Match(text);
        _semanticSpanCache[text] = spans;
        return spans;
    }

    /// <summary>Maps a semantic kind to a themeable ANSI color (respects the active .pen palette).</summary>
    private static Rgba SemanticColor(TerminalPalette palette, SemanticKind kind) =>
        kind switch
        {
            SemanticKind.Error => palette[9],  // bright red
            SemanticKind.Warning => palette[11], // bright yellow
            SemanticKind.Success => palette[10], // bright green
            SemanticKind.Url => palette[12], // bright blue
            SemanticKind.IpAddress => palette[14], // bright cyan
            SemanticKind.Option => palette[13], // bright magenta
            SemanticKind.Number => palette[6],  // cyan
            _ => palette.DefaultForeground
        };

    private void RenderCursor(DrawingContext context, TerminalScreen screen, TerminalPalette palette, int topAbsolute)
    {
        if (!Emulator.Modes.CursorVisible)
        {
            return;
        }
        int cursorAbsolute = screen.TotalRows - screen.Rows + screen.CursorY;
        int screenRow = cursorAbsolute - topAbsolute;
        if (screenRow < 0 || screenRow >= screen.Rows)
        {
            return;
        }
        double x = screen.CursorX * _cellWidth;
        double y = screenRow * _cellHeight;
        var rect = new Rect(x, y, _cellWidth, _cellHeight);
        ImmutableSolidColorBrush cursorBrush = BrushFor(palette.CursorColor);
        if (!_hasFocus)
        {
            // Unfocused: hollow outline regardless of style, so the position stays visible.
            context.DrawRectangle(new Pen(cursorBrush), rect);
            return;
        }

        // Blink phase: the "off" half simply skips drawing (focused only; unfocused outline
        // never blinks).
        if ((CursorBlink || Emulator.Modes.CursorBlink) && !_cursorBlinkVisible)
        {
            return;
        }
        switch (CursorStyle)
        {
            case "bar":
                context.FillRectangle(cursorBrush, new(x, y, Math.Max(1.5, _cellWidth * 0.15), _cellHeight));
                break;
            case "underline":
                context.FillRectangle(cursorBrush, new(x, y + _cellHeight - 2, _cellWidth, 2));
                break;
            default: // block
                context.FillRectangle(cursorBrush, rect);
                // Redraw the glyph under the cursor in the background color for contrast.
                TerminalCell cell = screen.GetCell(screen.CursorX, screen.CursorY);
                if (cell.Rune != 0)
                {
                    FormattedText ft = GlyphFor(cell, palette.DefaultBackground, false, false);
                    context.DrawText(ft, new(x, y + _glyphYOffset));
                }
                break;
        }
    }

    // ---- Selection ----------------------------------------------------------

    private ((int Row, int Col) Start, (int Row, int Col) End)? NormalizedSelection()
    {
        if (_selectionAnchor is not { } a || _selectionCaret is not { } c)
        {
            return null;
        }
        if (a.Row < c.Row || (a.Row == c.Row && a.Col <= c.Col))
        {
            return (a, c);
        }
        return (c, a);
    }

    private static bool IsSelected(((int Row, int Col) Start, (int Row, int Col) End)? sel, int row, int col)
    {
        if (sel is not { } s)
        {
            return false;
        }
        if (row < s.Start.Row || row > s.End.Row)
        {
            return false;
        }
        if (row == s.Start.Row && col < s.Start.Col)
        {
            return false;
        }
        if (row == s.End.Row && col >= s.End.Col)
        {
            return false;
        }
        return true;
    }

    /// <summary>Searches the whole buffer (scrollback + screen), case-insensitive (spec §5.3).</summary>
    public IReadOnlyList<BufferSearchHit> SearchBuffer(string query) => BufferSearch.FindAll(Emulator.Screen, query);

    /// <summary>
    /// 导出整个缓冲区(scrollback + 当前屏幕)为纯文本:逐行去尾空格,
    /// 末尾的空白行不输出(“保存输出到文件”,§12.4)。
    /// </summary>
    public string GetBufferText()
    {
        TerminalScreen screen = Emulator.Screen;
        var sb = new StringBuilder();
        int lastNonEmpty = -1;
        for (int row = 0; row < screen.TotalRows; row++)
        {
            string text = screen.ViewLine(row).GetText().TrimEnd();
            sb.AppendLine(text);
            if (text.Length > 0)
            {
                lastNonEmpty = sb.Length;
            }
        }
        if (lastNonEmpty < 0)
        {
            return string.Empty;
        }
        sb.Length = lastNonEmpty;
        return sb.ToString();
    }

    /// <summary>
    /// Paints every search hit (amber) with the current one in accent. Rows are
    /// absolute buffer rows, so highlights stay attached while scrolling.
    /// </summary>
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
            BufferSearchHit hit = hits[i];
            if (!map.TryGetValue(hit.Row, out List<(int, int, bool)>? spans))
            {
                map[hit.Row] = spans = [];
            }
            spans.Add((hit.StartCol, hit.StartCol + hit.Length, i == currentIndex));
        }
        _searchHighlights = map;
        InvalidateVisual();
    }

    public void ClearSearchHighlights()
    {
        if (_searchHighlights is null)
        {
            return;
        }
        _searchHighlights = null;
        InvalidateVisual();
    }

    /// <summary>
    /// Scrolls a search hit into view (roughly centered) and selects it so the
    /// existing selection highlight marks the match.
    /// </summary>
    public void ShowHit(BufferSearchHit hit)
    {
        _selectionAnchor = (hit.Row, hit.StartCol);
        _selectionCaret = (hit.Row, hit.StartCol + hit.Length);
        int totalRows = Emulator.Screen.TotalRows;
        int rows = Emulator.Rows;
        int desiredTop = Math.Max(0, hit.Row - rows / 2);
        int maxTop = Math.Max(0, totalRows - rows);
        ScrollOffset = maxTop - Math.Min(desiredTop, maxTop);
        InvalidateVisual();
    }

    public string GetSelectedText()
    {
        ((int Row, int Col) Start, (int Row, int Col) End)? sel = NormalizedSelection();
        if (sel is not { } s)
        {
            return string.Empty;
        }
        TerminalScreen screen = Emulator.Screen;
        var sb = new StringBuilder();
        for (int row = s.Start.Row; row <= s.End.Row && row < screen.TotalRows; row++)
        {
            TerminalRow line = screen.ViewLine(row);
            int from = row == s.Start.Row ? s.Start.Col : 0;
            int to = row == s.End.Row ? s.End.Col : line.Columns;
            int lineStart = sb.Length;
            for (int col = Math.Max(0, from); col < Math.Min(line.Columns, to); col++)
            {
                TerminalCell cell = line[col];
                if (!cell.IsWideTrailing)
                {
                    sb.Append(cell.Rune == 0 ? " " : char.ConvertFromUtf32(cell.Rune));
                }
            }
            // 复制时去除每行尾部空格(设置 → 终端 → 选择与复制)。
            if (TrimTrailingWhitespaceOnCopy)
            {
                while (sb.Length > lineStart && sb[^1] == ' ')
                {
                    sb.Length--;
                }
            }
            if (row != s.End.Row)
            {
                sb.Append('\n');
            }
        }
        return TrimTrailingWhitespaceOnCopy ? sb.ToString().TrimEnd() : sb.ToString();
    }

    private (int Row, int Col) PointToCell(Point p)
    {
        int screenRow = (int)(p.Y / _cellHeight);
        int col = (int)(p.X / _cellWidth);
        int topAbsolute = Math.Max(0, Emulator.Screen.TotalRows - Emulator.Rows - _scrollOffset);
        // Clamp the row: while the pointer is captured a drag can leave the control (negative
        // p.Y), and a negative absolute row used to crash selection copy (#用户反馈).
        int row = Math.Clamp(topAbsolute + screenRow, 0, Math.Max(0, Emulator.Screen.TotalRows - 1));
        return (row, Math.Clamp(col, 0, Emulator.Columns));
    }

    // ---- Input --------------------------------------------------------------

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _hasFocus = true;
        UpdateCursorBlinkTimer();
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _hasFocus = false;
        UpdateCursorBlinkTimer();
        InvalidateVisual();
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Text))
        {
            byte[] bytes = InputEncoder.EncodeText(e.Text);
            if (bytes.Length > 0)
            {
                SendTypedInput(bytes);
                if (ScrollOnKeystroke)
                {
                    _scrollOffset = 0;
                }
                ClearSelection();
                ResetCursorBlink();
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
            // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
            switch (e.Key)
            {
                case Key.C:
                    await CopyAsync();
                    e.Handled = true;
                    return;
                case Key.V:
                    await PasteAsync();
                    e.Handled = true;
                    return;
            }
        }

        // Shift+Insert pastes (classic X11 / terminal convention). Intercept before the
        // encoder, which would otherwise send this as a CSI 2~ sequence.
        if (e is { Key: Key.Insert, KeyModifiers: KeyModifiers.Shift })
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
             (e.KeyModifiers == KeyModifiers.None && Emulator.Screen.MaxScrollback > 0)))
        {
            int page = Math.Max(1, Emulator.Rows - 1);
            ScrollOffset += e.Key == Key.PageUp ? page : -page;
            e.Handled = true;
            return;
        }

        // Shift+Home/End jump the shell cursor to line start/end (用户反馈): send the plain
        // Home/End sequences, which readline binds — the shifted CSI 1;2H/F variants it ignores.
        KeyModifiers effectiveModifiers = e.KeyModifiers;
        switch (e.Key)
        {
            case Key.Home or Key.End when e.KeyModifiers == KeyModifiers.Shift:
                effectiveModifiers = KeyModifiers.None;
                break;
            // 有选中文本时 Ctrl+C 复制而非发送中断(设置 → 终端 → 输入 → 选中时 Ctrl+C 复制)。
            case Key.C when e.KeyModifiers == KeyModifiers.Control && TryCopyOnCtrlC():
                e.Handled = true;
                return;
        }
        byte[]? encoded = InputEncoder.Encode(e.Key, effectiveModifiers, Emulator.Modes, Emulator.Type);
        if (encoded is { Length: > 0 })
        {
            SendTypedInput(encoded);
            if (ScrollOnKeystroke)
            {
                _scrollOffset = 0;
            }
            ClearSelection();
            ResetCursorBlink();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        Point point = e.GetPosition(this);
        PointerPointProperties props = e.GetCurrentPoint(this).Properties;

        // Ctrl+click on a detected URL opens it in the default browser (#9).
        if (props.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            (int row, int col) = PointToCell(point);
            string lineText = row < Emulator.Screen.TotalRows ? Emulator.Screen.ViewLine(row).GetText() : string.Empty;
            string? url = SemanticMatcher.UrlAt(lineText, col);
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
        if (Emulator.Modes.Mouse != MouseTracking.None && _scrollOffset == 0 && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            TerminalMouseButton? button =
                props.IsLeftButtonPressed
                    ? TerminalMouseButton.Left
                    : props.IsRightButtonPressed
                        ? TerminalMouseButton.Right
                        : props.IsMiddleButtonPressed
                            ? TerminalMouseButton.Middle
                            : null;
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
            // 双击选择整个单词(设置 → 终端 → 选择与复制)。
            if (e.ClickCount == 2 && DoubleClickSelectsWord)
            {
                SelectWordAt(PointToCell(point));
                e.Handled = true;
                return;
            }
            _selecting = true;
            _selectionAnchor = PointToCell(point);
            _selectionCaret = _selectionAnchor;
            InvalidateVisual();
        }
        else if (props.IsRightButtonPressed && RightClickPaste)
        {
            // Right click pastes, matching common terminal behavior (可在设置中关闭).
            _ = PasteAsync();
        }
        e.Handled = true;
    }

    /// <summary>
    /// Selects the contiguous word (letters/digits and common path characters) around
    /// the given cell; with 选中即复制 on, the word lands on the clipboard immediately.
    /// </summary>
    private void SelectWordAt((int Row, int Col) cell)
    {
        TerminalScreen screen = Emulator.Screen;
        if (cell.Row >= screen.TotalRows)
        {
            return;
        }
        TerminalRow line = screen.ViewLine(cell.Row);
        if (line.Columns <= 0)
        {
            return;
        }
        int col = Math.Clamp(cell.Col, 0, line.Columns - 1);
        if (!IsWordCell(line, col))
        {
            return;
        }
        int start = col;
        while (start > 0 && IsWordCell(line, start - 1))
        {
            start--;
        }
        int end = col + 1;
        while (end < line.Columns && IsWordCell(line, end))
        {
            end++;
        }
        _selectionAnchor = (cell.Row, start);
        _selectionCaret = (cell.Row, end);
        _selecting = false;
        InvalidateVisual();
        if (CopyOnSelect)
        {
            _ = CopyAsync();
        }
    }

    private static bool IsWordCell(TerminalRow line, int col)
    {
        TerminalCell cell = line[col];
        if (cell.IsWideTrailing)
        {
            return true; // part of the wide rune that leads it
        }
        if (cell.Rune is 0 or ' ')
        {
            return false;
        }
        return (Rune.TryCreate(cell.Rune, out Rune rune) && Rune.IsLetterOrDigit(rune)) || cell.Rune is '_' or '-' or '.' or '/' or '~' or '+' or '@' or ':';
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        // Report motion to the app in button-event (?1002, only while a button is down) and
        // any-event (?1003, always) modes, but only when a cell boundary is crossed.
        MouseTracking tracking = Emulator.Modes.Mouse;
        switch (_selecting)
        {
            case false when _scrollOffset == 0 && tracking is MouseTracking.ButtonEvent or MouseTracking.AnyEvent:
                {
                    bool held = _mouseButtonDown is not null;
                    if (tracking != MouseTracking.AnyEvent && !held)
                    {
                        return;
                    }
                    Point position = e.GetPosition(this);
                    (int Col, int Row) cell = ScreenCell(position);
                    if (cell == _lastMouseReportCell)
                    {
                        return;
                    }
                    _lastMouseReportCell = cell;
                    TerminalMouseButton button = _mouseButtonDown ?? TerminalMouseButton.None;
                    SendMouse(TerminalMouseEventType.Move, button, position, e.KeyModifiers);
                    return;
                }
            case true:
                _selectionCaret = PointToCell(e.GetPosition(this));
                InvalidateVisual();
                break;
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
        if (!_selecting)
        {
            return;
        }
        _selecting = false;
        // Select-to-copy: releasing a non-empty selection copies it, so the user never
        // needs a copy shortcut (design §8). A plain click has an empty selection and no-ops.
        if (CopyOnSelect)
        {
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
            double next = Math.Clamp(FontSize + (e.Delta.Y > 0 ? 1 : -1), 6, 40);
            if (Math.Abs(next - FontSize) > 0.01)
            {
                FontSize = next;
                FontSizeChanged?.Invoke(next);
            }
            e.Handled = true;
            return;
        }

        // On the live screen with mouse tracking on, the wheel scrolls the app (htop/btop lists,
        // less, vim) rather than the local scrollback.
        if (Emulator.Modes.Mouse != MouseTracking.None && _scrollOffset == 0 && e.Delta.Y != 0)
        {
            TerminalMouseButton button = e.Delta.Y > 0 ? TerminalMouseButton.WheelUp : TerminalMouseButton.WheelDown;
            if (SendMouse(TerminalMouseEventType.Press, button, e.GetPosition(this), e.KeyModifiers))
            {
                e.Handled = true;
                return;
            }
        }
        int delta = (int)(e.Delta.Y * 3);
        int maxOffset = Emulator.Screen.ScrollbackCount;
        _scrollOffset = Math.Clamp(_scrollOffset + delta, 0, maxOffset);
        InvalidateVisual();
        ScrollChanged?.Invoke();
        e.Handled = true;
    }

    /// <summary>Maps a pointer position to a 0-based cell within the visible screen.</summary>
    private (int Col, int Row) ScreenCell(Point p)
    {
        int col = Math.Clamp((int)(p.X / _cellWidth), 0, Math.Max(0, Emulator.Columns - 1));
        int row = Math.Clamp((int)(p.Y / _cellHeight), 0, Math.Max(0, Emulator.Rows - 1));
        return (col, row);
    }

    /// <summary>
    /// Encodes a mouse event under the active tracking mode and sends it to the PTY.
    /// Returns false when the current mode does not report this event.
    /// </summary>
    private bool SendMouse(TerminalMouseEventType type, TerminalMouseButton button, Point p, KeyModifiers mods)
    {
        (int col, int row) = ScreenCell(p);
        byte[]? bytes = MouseEncoder.Encode(type, button, col, row,
            mods.HasFlag(KeyModifiers.Shift),
            mods.HasFlag(KeyModifiers.Alt),
            mods.HasFlag(KeyModifiers.Control),
            Emulator.Modes);
        if (bytes is null || bytes.Length == 0)
        {
            return false;
        }
        SendTypedInput(bytes);
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
        {
            return;
        }
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            await top.Launcher.LaunchUriAsync(uri);
        }
    }

    /// <summary>
    /// Ctrl+C 的复制分支:仅当「选中时 Ctrl+C 复制」开启且当前有选区时,复制选中
    /// 内容并清除选区,返回 true(调用方不再发送 ^C);否则返回 false,Ctrl+C 照常作为中断
    /// 信号发往 PTY。TerminalTabView 的快捷键回退层也调用这里,保证两层行为一致跟随设置。
    /// </summary>
    public bool TryCopyOnCtrlC()
    {
        if (!CtrlCCopiesWhenSelected || NormalizedSelection() is null)
        {
            return false;
        }
        _ = CopyAsync();
        ClearSelection();
        InvalidateVisual();
        return true;
    }

    public async Task CopyAsync()
    {
        string text = GetSelectedText();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    public async Task PasteAsync()
    {
        IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }
        string? text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // 粘贴多行内容前确认(设置 → 终端 → 输入),防止误执行整段脚本。
        if (ConfirmMultilinePaste && MultilinePasteConfirmation is { } confirm && text.IndexOfAny(['\r', '\n']) >= 0 && text.TrimEnd('\r', '\n').IndexOfAny(['\r', '\n']) >= 0 && !await confirm(text))
        {
            return;
        }
        var payload = new StringBuilder();
        if (Emulator.Modes.BracketedPaste)
        {
            payload.Append("\e[200~");
        }
        payload.Append(text.Replace("\r\n", "\r").Replace('\n', '\r'));
        if (Emulator.Modes.BracketedPaste)
        {
            payload.Append("\e[201~");
        }
        SendTypedInput(Encoding.UTF8.GetBytes(payload.ToString()));
    }

    private readonly record struct GlyphKey(int Rune, string? Combining, uint Foreground, int Style);

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool MessageBeep(uint type);
    }

    /// <summary>
    /// Minimal IME client: no preedit-in-buffer, no surrounding text — the terminal is
    /// not an editable document; committed text arrives through OnTextInput as host bytes. Only
    /// the cursor rectangle matters, to position the candidate window.
    /// </summary>
    private sealed class TerminalImeClient(VelaTerminalControl owner) : TextInputMethodClient
    {
        public override Visual TextViewVisual => owner;

        public override bool SupportsPreedit => false;

        public override bool SupportsSurroundingText => false;

        public override string SurroundingText => string.Empty;

        public override Rect CursorRectangle => owner.GetImeCursorRect();

        public override TextSelection Selection
        {
            get => default;
            set { }
        }

        public void NotifyCursorMoved() => RaiseCursorRectangleChanged();
    }
}
