using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>
/// 资源监视窗口。窗体规格与任务管理器、链路追踪一致(按平台的卡片外框 + 自绘缩放抓取区,
/// 见 <see cref="WindowChrome" />),打开即采样一次,关闭时停表。
/// </summary>
/// <remarks>
/// 贴着卡片四角、又有不透明背景的子元素是标题条(上两角)与侧栏(左下角),各挂对应的圆角类;
/// 右下角是无背景的 Panel,露的是卡片自己。
/// </remarks>
public partial class ResourceMonitorWindow : Window
{
    /// <summary>初始化窗口并接线关闭时的清理。</summary>
    public ResourceMonitorWindow()
    {
        InitializeComponent();
        // 按平台装外框;最大化时卡片铺满、抓取区让位也由它管。
        WindowChrome.Apply(this, WindowChromeKind.Tool, ResizeGrips);
        Opened += OnOpened;
        Closed += (_, _) => ViewModel?.Dispose();
        DataContextChanged += (_, _) =>
        {
            if (ViewModel is { } vm)
            {
                Title = Strings.Format("Monitor_TitleFormat", vm.HostName);
            }
        };
    }

    private ResourceMonitorWindowViewModel? ViewModel => DataContext as ResourceMonitorWindowViewModel;

    /// <summary>打开即拉一次数据 —— 不让用户对着空图表等一个采样周期。</summary>
    private void OnOpened(object? sender, EventArgs e) => _ = ViewModel?.RefreshAsync();

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }
        this.BeginWindowMoveDrag(e);
    }

    /// <summary>缩放抓取区。只认左键:系统 sizing 模态循环只在左键弹起时退出(#116)。</summary>
    private void ResizeEdge_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        if (WindowState == WindowState.Normal
            && sender is Border { Tag: string tag }
            && Enum.TryParse(tag, out WindowEdge edge))
        {
            BeginResizeDrag(edge, e);
        }
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object? sender, RoutedEventArgs e) => this.PostClose();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            this.PostClose();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
