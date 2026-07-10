using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VelaShell.App.Services;
using VelaShell.App.ViewModels;
using VelaShell.Core.Models;
using VelaShell.Terminal;
using VelaShell.Terminal.Rendering;
using KeyModifiers = VelaShell.App.Services.KeyModifiers;

namespace VelaShell.App.Views;

public partial class TerminalTabView : UserControl
{
    private readonly IKeyboardShortcutService _shortcutService;

    // ---- In-terminal search (spec §5.3) ------------------------------------

    private IReadOnlyList<BufferSearchHit> _searchHits =
        [];

    private int _searchIndex = -1;
    private bool _syncingScrollBar;
    private VelaTerminalControl? _termControl;

    public TerminalTabView()
        : this(new KeyboardShortcutService()) { }

    public TerminalTabView(IKeyboardShortcutService shortcutService)
    {
        _shortcutService = shortcutService ?? throw new ArgumentNullException(nameof(shortcutService));
        InitializeComponent();
        Focusable = true;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        DataContextChanged += (_, _) => HookTerminalControl();
        if (ScrollBarView is not null)
        {
            ScrollBarView.Scroll += OnScrollBarScroll;
        }

        // Tunnel so a disconnected tab can catch Enter / Ctrl+R for reconnect (and Ctrl+F can
        // open search) before the terminal control consumes the keys for the PTY.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        SearchBox.TextChanged += (_, _) => RunSearch();
        SearchNext.Click += (_, _) => MoveHit(+1);
        SearchPrev.Click += (_, _) => MoveHit(-1);
        SearchClose.Click += (_, _) => CloseSearch();
        SearchBox.KeyDown += OnSearchBoxKeyDown;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // Ctrl+F toggles the in-terminal search bar (spec §5.3).
        if (e is { Key: Key.F, KeyModifiers: Avalonia.Input.KeyModifiers.Control })
        {
            OpenSearch();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && SearchBar.IsVisible)
        {
            CloseSearch();
            e.Handled = true;
            return;
        }
        if (DataContext is not TerminalTabViewModel { ConnectionStatus: SessionStatus.Disconnected } vm)
        {
            return;
        }
        bool reconnect = e is { Key: Key.Enter, KeyModifiers: Avalonia.Input.KeyModifiers.None } or { Key: Key.R, KeyModifiers: Avalonia.Input.KeyModifiers.Control };
        if (!reconnect)
        {
            return;
        }
        vm.RequestReconnect();
        e.Handled = true;
    }

    internal void OpenSearch()
    {
        SearchBar.IsVisible = true;
        SearchBox.Focus();
        SearchBox.SelectAll();
        RunSearch();
    }

    private void CloseSearch()
    {
        SearchBar.IsVisible = false;
        _searchHits = [];
        _searchIndex = -1;
        _termControl?.ClearSearchHighlights();
        FocusTerminal();
    }

    private void RunSearch()
    {
        if (_termControl is null || !SearchBar.IsVisible)
        {
            return;
        }
        string query = SearchBox.Text ?? string.Empty;
        _searchHits = _termControl.SearchBuffer(query);
        _searchIndex = _searchHits.Count > 0 ? 0 : -1;
        ShowCurrentHit();
    }

    private void MoveHit(int delta)
    {
        if (_searchHits.Count == 0)
        {
            return;
        }
        _searchIndex = (((_searchIndex + delta) % _searchHits.Count) + _searchHits.Count) % _searchHits.Count;
        ShowCurrentHit();
    }

    private void ShowCurrentHit()
    {
        SearchCount.Text = _searchHits.Count == 0
                               ? string.IsNullOrEmpty(SearchBox.Text) ? "" : "无匹配"
                               : $"{_searchIndex + 1}/{_searchHits.Count}";

        // All hits get a persistent highlight; the current one is tinted accent (§5.3).
        _termControl?.SetSearchHighlights(_searchHits, _searchIndex);
        if (_searchIndex >= 0 && _termControl is not null)
        {
            _termControl.ShowHit(_searchHits[_searchIndex]);
        }
    }

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            MoveHit(e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift) ? -1 : +1);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseSearch();
            e.Handled = true;
        }
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        HookTerminalControl();
        FocusTerminal();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_termControl is not null)
        {
            _termControl.ScrollChanged -= OnTerminalScrollChanged;
        }
        _termControl = null;
    }

    // The terminal control is a single shared instance reparented across split panes, so each
    // view (re)binds the scrollbar to whichever control it currently hosts.
    private void HookTerminalControl()
    {
        var ctrl = (DataContext as TerminalTabViewModel)?.TerminalEmulator.Control as VelaTerminalControl;
        if (ReferenceEquals(ctrl, _termControl))
        {
            SyncScrollBar();
            return;
        }
        if (_termControl is not null)
        {
            _termControl.ScrollChanged -= OnTerminalScrollChanged;
        }
        _termControl = ctrl;
        if (_termControl is not null)
        {
            _termControl.ScrollChanged += OnTerminalScrollChanged;
        }
        SyncScrollBar();
    }

    private void OnTerminalScrollChanged() => SyncScrollBar();

    private void SyncScrollBar()
    {
        if (ScrollBarView is null || _termControl is null)
        {
            return;
        }
        _syncingScrollBar = true;
        try
        {
            int max = _termControl.MaxScrollOffset;
            ScrollBarView.Maximum = max;
            ScrollBarView.ViewportSize = Math.Max(1, _termControl.Rows);
            // Thumb sits at the bottom when following live output (offset 0) and at the top
            // when fully scrolled back into history.
            ScrollBarView.Value = max - _termControl.ScrollOffset;
            ScrollBarView.IsEnabled = max > 0;
        }
        finally
        {
            _syncingScrollBar = false;
        }
    }

    private void OnScrollBarScroll(object? sender, ScrollEventArgs e)
    {
        if (_syncingScrollBar || _termControl is null || ScrollBarView is null)
        {
            return;
        }
        int max = _termControl.MaxScrollOffset;
        _termControl.ScrollOffset = max - (int)Math.Round(ScrollBarView.Value);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        FocusTerminal();
        base.OnPointerPressed(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        KeyModifiers modifiers = MapModifiers(e.KeyModifiers);
        KeyCode key = MapKey(e.Key);
        if (key == KeyCode.None)
        {
            base.OnKeyDown(e);
            return;
        }
        ShortcutAction action = _shortcutService.Resolve(modifiers, key, ShortcutContext.Terminal);
        switch (action)
        {
            // 本层是焦点落在标签视图(而非终端控件)时的回退:正常情况下终端控件自己的
            // OnKeyDown 已处理这些键。回退层必须与控件行为一致地跟随设置,否则同一个
            // 快捷键会因焦点位置不同而表现不同(用户反馈:选中即复制/Ctrl+C 不受控)。
            case ShortcutAction.Copy:
                // 复用控件的复制:只复制选中内容,并尊重「复制时去除尾部空格」等设置。
                if (_termControl is not null)
                {
                    _ = _termControl.CopyAsync();
                }
                else
                {
                    _ = CopySelectionAsync();
                }
                e.Handled = true;
                return;
            case ShortcutAction.Paste:
                if (_termControl is not null)
                {
                    _ = _termControl.PasteAsync();
                }
                else
                {
                    _ = PasteFromClipboardAsync();
                }
                e.Handled = true;
                return;
            case ShortcutAction.SendInterrupt:
                // 与 VelaTerminalControl.OnKeyDown 同规则:「选中时 Ctrl+C 复制」开启且有
                // 选区 → 复制;否则发送中断信号 ^C。
                if (_termControl?.TryCopyOnCtrlC() != true)
                {
                    SendBytesToTerminal([0x03]);
                }
                e.Handled = true;
                return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Text))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(e.Text);
            SendBytesToTerminal(bytes);
            e.Handled = true;
        }
        base.OnTextInput(e);
    }

    private void SendBytesToTerminal(byte[] data)
    {
        if (DataContext is TerminalTabViewModel vm)
        {
            vm.TerminalEmulator.WriteInput(data);
        }
    }

    private async Task CopySelectionAsync()
    {
        IClipboard? clipboard = GetClipboard();
        if (clipboard == null)
        {
            return;
        }
        string selectedText = GetSelectedText();
        if (!string.IsNullOrEmpty(selectedText))
        {
            await clipboard.SetTextAsync(selectedText);
        }
    }

    private async Task PasteFromClipboardAsync()
    {
        IClipboard? clipboard = GetClipboard();
        if (clipboard == null)
        {
            return;
        }
        string? text = await clipboard.TryGetTextAsync();
        if (!string.IsNullOrEmpty(text))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            SendBytesToTerminal(bytes);
        }
    }

    private IClipboard? GetClipboard() => TopLevel.GetTopLevel(this)?.Clipboard;

    private string GetSelectedText()
    {
        if (DataContext is TerminalTabViewModel vm)
        {
            ITerminalEmulator emulator = vm.TerminalEmulator;
            var sb = new StringBuilder();
            for (int row = 0; row < emulator.Rows; row++)
            {
                string line = emulator.GetBufferLine(row);
                if (!string.IsNullOrEmpty(line))
                {
                    if (sb.Length > 0)
                    {
                        sb.AppendLine();
                    }
                    sb.Append(line);
                }
            }
            return sb.ToString();
        }
        return string.Empty;
    }

    private void FocusTerminal()
    {
        if (DataContext is not TerminalTabViewModel vm)
        {
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            Focus();
            vm.TerminalEmulator.Control.Focus();
        }, DispatcherPriority.Input);
    }

    private static KeyModifiers MapModifiers(Avalonia.Input.KeyModifiers avaloniaModifiers)
    {
        KeyModifiers result = KeyModifiers.None;
        if (avaloniaModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control))
        {
            result |= KeyModifiers.Ctrl;
        }
        if (avaloniaModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift))
        {
            result |= KeyModifiers.Shift;
        }
        if (avaloniaModifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt))
        {
            result |= KeyModifiers.Alt;
        }
        if (avaloniaModifiers.HasFlag(Avalonia.Input.KeyModifiers.Meta))
        {
            result |= KeyModifiers.Meta;
        }
        return result;
    }

    private static KeyCode MapKey(Key avaloniaKey)
    {
        return avaloniaKey switch
        {
            Key.C        => KeyCode.C,
            Key.V        => KeyCode.V,
            Key.T        => KeyCode.T,
            Key.W        => KeyCode.W,
            Key.Tab      => KeyCode.Tab,
            Key.OemComma => KeyCode.Comma,
            _            => KeyCode.None
        };
    }
}
