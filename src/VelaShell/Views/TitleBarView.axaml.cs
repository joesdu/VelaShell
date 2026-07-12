using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace VelaShell.Views;

/// <summary>
/// 无边框窗体的自绘标题栏:空白区拖动窗口、双击切换最大化,
/// 右侧为窗口控制按钮(最小化 / 最大化(还原) / 关闭)。
/// 功能图标经命令注册表执行(与命令面板同源)。
/// </summary>
public partial class TitleBarView : UserControl
{
    private Window? _observedWindow;

    public TitleBarView()
    {
        InitializeComponent();
    }

    private Window? HostWindow => VisualRoot as Window;

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // 最大化按钮图标随窗口状态切换:方块 = 可最大化,双矩形 = 可还原。
        if (HostWindow is { } window)
        {
            _observedWindow = window;
            window.PropertyChanged += OnWindowPropertyChanged;
            UpdateMaximizeGlyph(window.WindowState);
        }
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_observedWindow is { } window)
        {
            window.PropertyChanged -= OnWindowPropertyChanged;
            _observedWindow = null;
        }
    }

    private void OnWindowPropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty && sender is Window window)
        {
            UpdateMaximizeGlyph(window.WindowState);
        }
    }

    private void UpdateMaximizeGlyph(WindowState state)
    {
        string key = state == WindowState.Maximized ? "Icon.copy" : "Icon.square";
        if (this.TryFindResource(key, out object? value) && value is Geometry geometry)
        {
            MaximizeIcon.Data = geometry;
        }
    }

    private void Bar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // 按钮(功能图标/窗口控制)自行消费点击;保险起见,源头在任何按钮内都不启动拖动,
        // 以免吞掉后续的 Click。
        if (e.Source is Control source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        if (HostWindow is not { } window)
        {
            return;
        }
        // 最大化下拖动 = 先还原再进入移动(操作系统标题栏的标准手感)。
        if (window.WindowState == WindowState.Maximized)
        {
            window.WindowState = WindowState.Normal;
        }
        window.BeginMoveDrag(e);
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e)
    {
        if (HostWindow is { } window)
        {
            window.WindowState = WindowState.Minimized;
        }
    }

    private void Maximize_Click(object? sender, RoutedEventArgs e) => ToggleMaximize();

    /// <summary>关闭走 Window.Close():托盘化/退出确认逻辑在 MainWindow.OnClosing 统一处理。</summary>
    private void Close_Click(object? sender, RoutedEventArgs e) => HostWindow?.Close();

    private void ToggleMaximize()
    {
        if (HostWindow is { } window)
        {
            window.WindowState = window.WindowState == WindowState.Maximized
                                     ? WindowState.Normal
                                     : WindowState.Maximized;
        }
    }
}
