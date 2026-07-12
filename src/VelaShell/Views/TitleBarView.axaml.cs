using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace VelaShell.Views;

/// <summary>
/// 无边框窗体的自绘标题栏:标题栏为纯客户区(Avalonia 12.0.5 的 chrome 角色输入
/// 重定向在 Win32 上未落地,角色区内按钮点不动 —— 实测),拖动走 BeginMoveDrag
/// (原生移动循环,Win11 边缘贴靠/拖顶最大化照常生效),双击切换最大化,
/// 窗口控制按钮为常规 Click。
/// </summary>
public partial class TitleBarView : UserControl
{
    private Window? _observedWindow;

    public TitleBarView()
    {
        InitializeComponent();
    }

    // 必须沿逻辑树找 Window:Avalonia 12 的视觉根是 TopLevelHost 而非 Window,
    // “VisualRoot as Window”恒为 null —— 曾令标题栏拖动与窗口按钮看似“无输入”,
    // 实为事件一直在触发、只是拿不到 Window 执行动作。
    private Window? HostWindow => this.FindLogicalAncestorOfType<Window>();

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
        // 按钮自行消费点击;源头在任何按钮内都不启动拖动,以免吞掉 Click。
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
