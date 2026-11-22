using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>承载 AI 插件内容,提供固定、悬浮、折叠与拖动宽度。</summary>
public partial class AiAssistantRegionView : UserControl
{
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private Point? _dragStart;
    private double _dragWidth;
    private AiAssistantRegionViewModel? State => DataContext as AiAssistantRegionViewModel;

    /// <summary>初始化专属区的输入与自动隐藏行为。</summary>
    public AiAssistantRegionView()
    {
        InitializeComponent();
        _hideTimer.Tick += (_, _) =>
        {
            if (IsPointerOver || _dragStart is not null || IsRegionFocused)
            {
                _hideTimer.Stop();
                return;
            }

            // 下拉菜单在独立 PopupRoot 内,指针离开面板并不代表离开了插件的操作区域。
            if (State?.Content is { } content && content.GetVisualDescendants()
                    .Any(control => control is Popup { IsOpen: true } or ComboBox { IsDropDownOpen: true }
                        or MenuItem { IsSubMenuOpen: true } or Control { ContextMenu.IsOpen: true }))
            {
                return;
            }

            _hideTimer.Stop();
            if (State is { IsDocked: false }) State.Collapse();
        };
        PointerEntered += (_, _) => _hideTimer.Stop();
        this.GetControl<Button>("ExpandButton").PointerEntered += (_, _) =>
        {
            _hideTimer.Stop();
            if (State is { IsAutoHide: true, IsVisible: true }) State.Activate();
        };
        PointerExited += (_, _) => UpdateHideTimer();
        AddHandler(GotFocusEvent, (_, _) => _hideTimer.Stop(), RoutingStrategies.Bubble);
        AddHandler(LostFocusEvent, (_, _) => Dispatcher.UIThread.Post(UpdateHideTimer), RoutingStrategies.Bubble);
        DetachedFromVisualTree += (_, _) => _hideTimer.Stop();
    }

    private bool IsRegionFocused => IsKeyboardFocusWithin;

    private void UpdateHideTimer()
    {
        if (State is not { IsDocked: false, IsExpanded: true } || IsPointerOver || _dragStart is not null || IsRegionFocused)
        {
            _hideTimer.Stop();
            return;
        }

        _hideTimer.Start();
    }

    private void PinClicked(object? sender, RoutedEventArgs e) => State?.TogglePin();
    private void CollapseClicked(object? sender, RoutedEventArgs e) => State?.Collapse();
    private void HideClicked(object? sender, RoutedEventArgs e) => State?.Hide();
    private void ExpandClicked(object? sender, RoutedEventArgs e) => State?.Activate();

    private void ResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (State is not { } state || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragStart = e.GetPosition(TopLevel.GetTopLevel(this));
        _dragWidth = state.PaneWidth;
        e.Pointer.Capture(sender as IInputElement);
        e.Handled = true;
    }

    private void ResizeMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is not { } start) return;
        State?.Resize(_dragWidth + start.X - e.GetPosition(TopLevel.GetTopLevel(this)).X);
        e.Handled = true;
    }

    private void ResizeReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragStart = null;
        e.Pointer.Capture(null);
    }

    private void ResizeCaptureLost(object? sender, PointerCaptureLostEventArgs e) => _dragStart = null;
}
