using System;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PulseTerm.App.Services;
using PulseTerm.App.ViewModels;
using PulseTerm.Terminal.Rendering;

namespace PulseTerm.App.Views;

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
