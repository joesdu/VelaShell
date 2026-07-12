using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace VelaShell.Views;

/// <summary>
/// 无边框窗体的自绘标题栏。原生窗口行为经 WindowDecorationsElementRole 交还操作系统:
/// 根 Border = TitleBar(原生拖动/双击最大化/右键系统菜单/Win11 贴靠),
/// 窗口控制按钮 = Minimize/Maximize/CloseButton(HT*BUTTON,含贴靠布局面板),
/// 功能图标按钮 = User(chrome 区域上的常规交互)。Click 处理器仅作非 Windows 平台回退。
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

    private void Minimize_Click(object? sender, RoutedEventArgs e)
    {
        if (HostWindow is { } window)
        {
            window.WindowState = WindowState.Minimized;
        }
    }

    private void Maximize_Click(object? sender, RoutedEventArgs e)
    {
        if (HostWindow is { } window)
        {
            window.WindowState = window.WindowState == WindowState.Maximized
                                     ? WindowState.Normal
                                     : WindowState.Maximized;
        }
    }

    /// <summary>关闭走 Window.Close():托盘化/退出确认逻辑在 MainWindow.OnClosing 统一处理。</summary>
    private void Close_Click(object? sender, RoutedEventArgs e) => HostWindow?.Close();
}
