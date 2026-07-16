using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>保持视觉空白、把键盘与 IME 输入实时路由到多个终端的捕获栏。</summary>
public partial class BroadcastInputView : UserControl
{
    private BroadcastImeClient? _imeClient;

    /// <summary>创建多终端广播输入捕获视图并注册输入路由。</summary>
    public BroadcastInputView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, OnBroadcastTextInput, RoutingStrategies.Tunnel);
        AddHandler(TextInputMethodClientRequestedEvent, OnImeClientRequested);
    }

    /// <summary>显示广播栏后聚焦无文本捕获区。</summary>
    public void FocusCapture() =>
        Dispatcher.UIThread.Post(() => CaptureBorder.Focus(), DispatcherPriority.Input);

    private async void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel || !viewModel.BroadcastInput.IsVisible)
        {
            return;
        }
        if (
            e.Key == Key.B
            && e.KeyModifiers
                == (Avalonia.Input.KeyModifiers.Control | Avalonia.Input.KeyModifiers.Shift)
        )
        {
            viewModel.BroadcastInput.CloseCommand.Execute().Subscribe();
            e.Handled = true;
            return;
        }
        if (
            e
            is {
                    Key: Key.V,
                    KeyModifiers: Avalonia.Input.KeyModifiers.Control
                        | Avalonia.Input.KeyModifiers.Shift,
                }
                or { Key: Key.Insert, KeyModifiers: Avalonia.Input.KeyModifiers.Shift }
        )
        {
            IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            string? text = clipboard is null ? null : await clipboard.TryGetTextAsync();
            if (!string.IsNullOrEmpty(text))
            {
                await viewModel.BroadcastPasteInputAsync(text);
            }
            FocusCapture();
            e.Handled = true;
            return;
        }
        if (viewModel.BroadcastKeyInput(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
        }
    }

    private void OnBroadcastTextInput(object? sender, TextInputEventArgs e)
    {
        if (
            DataContext is MainWindowViewModel viewModel
            && viewModel.BroadcastInput.IsVisible
            && !string.IsNullOrEmpty(e.Text)
        )
        {
            viewModel.BroadcastTextInput(e.Text);
            e.Handled = true;
        }
    }

    private void OnImeClientRequested(object? sender, TextInputMethodClientRequestedEventArgs e)
    {
        _imeClient ??= new(this);
        e.Client = _imeClient;
    }

    private void Capture_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        FocusCapture();
        e.Handled = true;
    }

    private sealed class BroadcastImeClient(BroadcastInputView owner) : TextInputMethodClient
    {
        public override Visual TextViewVisual => owner;

        public override bool SupportsPreedit => false;

        public override bool SupportsSurroundingText => false;

        public override string SurroundingText => string.Empty;

        public override Rect CursorRectangle => new(8, 8, Math.Max(1, owner.Bounds.Width - 16), 20);

        public override TextSelection Selection
        {
            get => default;
            set { }
        }
    }
}
