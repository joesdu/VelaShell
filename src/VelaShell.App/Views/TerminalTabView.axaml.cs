using System;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.App.Services;
using VelaShell.App.ViewModels;
using VelaShell.Core.Models;
using VelaShell.Terminal.Rendering;

namespace VelaShell.App.Views;

public partial class TerminalTabView : UserControl
{
    private readonly IKeyboardShortcutService _shortcutService;
    private PulseTerminalControl? _termControl;
    private bool _syncingScrollBar;

    public TerminalTabView()
        : this(new KeyboardShortcutService())
    {
    }

    public TerminalTabView(IKeyboardShortcutService shortcutService)
    {
        _shortcutService = shortcutService ?? throw new ArgumentNullException(nameof(shortcutService));
        InitializeComponent();

        Focusable = true;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        DataContextChanged += (_, _) => HookTerminalControl();

        if (ScrollBarView is not null)
            ScrollBarView.Scroll += OnScrollBarScroll;

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
        if (e.Key == Key.F && e.KeyModifiers == Avalonia.Input.KeyModifiers.Control)
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

        if (DataContext is not TerminalTabViewModel vm || vm.ConnectionStatus != SessionStatus.Disconnected)
            return;

        var reconnect =
            (e.Key == Key.Enter && e.KeyModifiers == Avalonia.Input.KeyModifiers.None) ||
            (e.Key == Key.R && e.KeyModifiers == Avalonia.Input.KeyModifiers.Control);

        if (reconnect)
        {
            vm.RequestReconnect();
            e.Handled = true;
        }
    }

    // ---- In-terminal search (spec §5.3) ------------------------------------

    private IReadOnlyList<VelaShell.Terminal.BufferSearchHit> _searchHits =
        Array.Empty<VelaShell.Terminal.BufferSearchHit>();
    private int _searchIndex = -1;

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
        _searchHits = Array.Empty<VelaShell.Terminal.BufferSearchHit>();
        _searchIndex = -1;
        _termControl?.ClearSearchHighlights();
        FocusTerminal();
    }

    private void RunSearch()
    {
        if (_termControl is null || !SearchBar.IsVisible)
            return;

        var query = SearchBox.Text ?? string.Empty;
        _searchHits = _termControl.SearchBuffer(query);
        _searchIndex = _searchHits.Count > 0 ? 0 : -1;
        ShowCurrentHit();
    }

    private void MoveHit(int delta)
    {
        if (_searchHits.Count == 0)
            return;
        _searchIndex = ((_searchIndex + delta) % _searchHits.Count + _searchHits.Count) % _searchHits.Count;
        ShowCurrentHit();
    }

    private void ShowCurrentHit()
    {
        SearchCount.Text = _searchHits.Count == 0
            ? (string.IsNullOrEmpty(SearchBox.Text) ? "" : "无匹配")
            : $"{_searchIndex + 1}/{_searchHits.Count}";

        // All hits get a persistent highlight; the current one is tinted accent (§5.3).
        _termControl?.SetSearchHighlights(_searchHits, _searchIndex);

        if (_searchIndex >= 0 && _termControl is not null)
            _termControl.ShowHit(_searchHits[_searchIndex]);
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
            _termControl.ScrollChanged -= OnTerminalScrollChanged;
        _termControl = null;
    }

    // The terminal control is a single shared instance reparented across split panes, so each
    // view (re)binds the scrollbar to whichever control it currently hosts.
    private void HookTerminalControl()
    {
        var ctrl = (DataContext as TerminalTabViewModel)?.TerminalEmulator.Control as PulseTerminalControl;
        if (ReferenceEquals(ctrl, _termControl))
        {
            SyncScrollBar();
            return;
        }

        if (_termControl is not null)
            _termControl.ScrollChanged -= OnTerminalScrollChanged;
        _termControl = ctrl;
        if (_termControl is not null)
            _termControl.ScrollChanged += OnTerminalScrollChanged;

        SyncScrollBar();
    }

    private void OnTerminalScrollChanged() => SyncScrollBar();

    private void SyncScrollBar()
    {
        if (ScrollBarView is null || _termControl is null)
            return;

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
            return;

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
        var modifiers = MapModifiers(e.KeyModifiers);
        var key = MapKey(e.Key);

        if (key == KeyCode.None)
        {
            base.OnKeyDown(e);
            return;
        }

        var action = _shortcutService.Resolve(modifiers, key, ShortcutContext.Terminal);

        switch (action)
        {
            case ShortcutAction.Copy:
                _ = CopySelectionAsync();
                e.Handled = true;
                return;

            case ShortcutAction.Paste:
                _ = PasteFromClipboardAsync();
                e.Handled = true;
                return;

            case ShortcutAction.SendInterrupt:
                SendBytesToTerminal(new byte[] { 0x03 });
                e.Handled = true;
                return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Text))
        {
            var bytes = Encoding.UTF8.GetBytes(e.Text);
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

    private async System.Threading.Tasks.Task CopySelectionAsync()
    {
        var clipboard = GetClipboard();
        if (clipboard == null)
            return;

        var selectedText = GetSelectedText();
        if (!string.IsNullOrEmpty(selectedText))
        {
            await clipboard.SetTextAsync(selectedText);
        }
    }

    private async System.Threading.Tasks.Task PasteFromClipboardAsync()
    {
        var clipboard = GetClipboard();
        if (clipboard == null)
            return;

        var text = await clipboard.TryGetTextAsync();
        if (!string.IsNullOrEmpty(text))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            SendBytesToTerminal(bytes);
        }
    }

    private IClipboard? GetClipboard()
    {
        return TopLevel.GetTopLevel(this)?.Clipboard;
    }

    private string GetSelectedText()
    {
        if (DataContext is TerminalTabViewModel vm)
        {
            var emulator = vm.TerminalEmulator;
            var sb = new StringBuilder();
            for (int row = 0; row < emulator.Rows; row++)
            {
                var line = emulator.GetBufferLine(row);
                if (!string.IsNullOrEmpty(line))
                {
                    if (sb.Length > 0)
                        sb.AppendLine();
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

    private static Services.KeyModifiers MapModifiers(Avalonia.Input.KeyModifiers avaloniaModifiers)
    {
        var result = Services.KeyModifiers.None;

        if (avaloniaModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control))
            result |= Services.KeyModifiers.Ctrl;
        if (avaloniaModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift))
            result |= Services.KeyModifiers.Shift;
        if (avaloniaModifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt))
            result |= Services.KeyModifiers.Alt;
        if (avaloniaModifiers.HasFlag(Avalonia.Input.KeyModifiers.Meta))
            result |= Services.KeyModifiers.Meta;

        return result;
    }

    private static KeyCode MapKey(Key avaloniaKey)
    {
        return avaloniaKey switch
        {
            Key.C => KeyCode.C,
            Key.V => KeyCode.V,
            Key.T => KeyCode.T,
            Key.W => KeyCode.W,
            Key.Tab => KeyCode.Tab,
            Key.OemComma => KeyCode.Comma,
            _ => KeyCode.None
        };
    }
}
