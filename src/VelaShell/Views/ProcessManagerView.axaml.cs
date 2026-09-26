using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using ReactiveUI.Primitives;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>
/// 远端任务管理器窗口。键位与 Windows 任务管理器对齐:Del 结束任务、F5 立即刷新、
/// Esc 关闭窗口。
/// </summary>
public partial class ProcessManagerView : Window
{
    /// <summary>初始化窗口并在打开时立刻取一轮数据(不必等第一个定时器周期)。</summary>
    public ProcessManagerView()
    {
        InitializeComponent();
        // 按平台装外框;最大化时卡片铺满、抓取区让位也由它管(见 WindowChrome)。
        WindowChrome.Apply(this, WindowChromeKind.Tool, ResizeGrips);
        Opened += OnOpened;
        Closed += OnClosed;
        DataContextChanged += OnDataContextChanged;
    }

    private ProcessManagerViewModel? ViewModel => DataContext as ProcessManagerViewModel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }
        Title = Strings.Format("Proc_TitleFormat", viewModel.HostLabel);

        // 确认与剪贴板都只有视图层拿得到,按隧道面板的做法用委托注入而不是服务定位。
        viewModel.ConfirmAction = (title, body) =>
            MessageDialog.ConfirmAsync(this, title, body, danger: true);
        viewModel.CopyToClipboard = async text =>
        {
            if (Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text);
            }
        };
    }

    private void OnOpened(object? sender, EventArgs e) => _ = ViewModel?.RefreshAsync();

    private void OnClosed(object? sender, EventArgs e) => ViewModel?.Dispose();

    /// <summary>无系统标题栏 —— 按住头部可拖动窗口,双击最大化/还原。</summary>
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

    /// <summary>
    /// 自绘缩放抓取区:按下即进入原生缩放。最大化时整层已隐藏。
    /// 只认左键:系统 sizing 模态循环只在左键弹起时退出(#116)。
    /// </summary>
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

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // 推迟关闭:同步 Close 会让本轮点击的后续路由打到已销毁的窗口(见 WindowCloseExtensions)。
    private void Close_Click(object? sender, RoutedEventArgs e) => this.PostClose();

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                this.PostClose();
                e.Handled = true;
                return;
            case Key.F5:
                ViewModel?.RefreshCommand.Execute().Subscribe();
                e.Handled = true;
                return;
            // Del 只在列表有焦点时结束任务:焦点在搜索框里时它得留给文本编辑。
            case Key.Delete when ProcessList.IsKeyboardFocusWithin:
                ViewModel?.EndTaskCommand.Execute().Subscribe();
                e.Handled = true;
                return;
            default:
                base.OnKeyDown(e);
                return;
        }
    }
}
