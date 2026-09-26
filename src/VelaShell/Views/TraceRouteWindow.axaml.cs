using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ReactiveUI.Primitives;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>链路追踪窗口。窗体规格与任务管理器一致(按平台的卡片外框 + 自绘缩放抓取区,见 <see cref="WindowChrome" />)。</summary>
public partial class TraceRouteWindow : Window
{
    /// <summary>初始化窗口;打开即对当前会话的主机发起一次追踪。</summary>
    public TraceRouteWindow()
    {
        InitializeComponent();
        // 按平台装外框;最大化时卡片铺满、抓取区让位也由它管。
        WindowChrome.Apply(this, WindowChromeKind.Tool, ResizeGrips);
        Opened += OnOpened;
        Closed += (_, _) => ViewModel?.Dispose();
        DataContextChanged += (_, _) =>
        {
            if (ViewModel is not { } vm)
            {
                return;
            }
            Title = Strings.Format("Trace_TitleFormat", vm.SessionLabel);
            // 文件选择器与浏览器只有视图层拿得到,按隧道面板的做法用委托注入。
            vm.DatabaseFilePicker = PickDatabaseAsync;
            vm.UrlOpener = OpenUrlAsync;
        };
    }

    private TraceRouteViewModel? ViewModel => DataContext as TraceRouteViewModel;

    /// <summary>打开即开跑 —— 用户点这个按钮就是想看结果,不该再点一次开始。</summary>
    private void OnOpened(object? sender, EventArgs e) => ViewModel?.StartCommand.Execute().Subscribe();

    /// <summary>选择离线归属地库。官方下载是 .mmdb.gz,因此两种后缀都收,解压交给 VM。</summary>
    private async Task<string?> PickDatabaseAsync()
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = Strings.Get("Trace_GeoPick"),
            AllowMultiple = false,
            SuggestedStartLocation = await StorageDefaults.DownloadsAsync(this),
            FileTypeFilter =
            [
                new(Strings.Get("Trace_GeoFileType")) { Patterns = ["*.mmdb", "*.gz"] }
            ]
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async Task OpenUrlAsync(string url)
    {
        if (GetTopLevel(this)?.Launcher is { } launcher)
        {
            await launcher.LaunchUriAsync(new(url));
        }
    }

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
