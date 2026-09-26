using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>目录同步窗口。窗体规格与链路追踪窗口一致(按平台的卡片外框 + 自绘缩放抓取区,见 <see cref="WindowChrome" />)。</summary>
public partial class DirectorySyncWindow : Window
{
    private DirectorySyncViewModel? _boundViewModel;
    private bool _closed;

    /// <summary>初始化窗口。</summary>
    public DirectorySyncWindow()
    {
        InitializeComponent();
        // 按平台装外框;最大化时卡片铺满、抓取区让位也由它管(见 WindowChrome)。
        WindowChrome.Apply(this, WindowChromeKind.Tool, ResizeGrips);
        DataContextChanged += (_, _) => Bind(DataContext as DirectorySyncViewModel);
        Closed += (_, _) =>
        {
            _closed = true;
            DirectorySyncViewModel? vm = _boundViewModel;
            Bind(null);
            // 关窗即停:「保持远端最新」的监视与在飞的比较/同步都随窗口一起结束。
            vm?.Dispose();
        };
    }

    private void Bind(DirectorySyncViewModel? vm)
    {
        if (_boundViewModel is { } previous)
        {
            previous.Disposed -= OnViewModelDisposed;
            previous.PickLocalFolder = null;
            previous.ConfirmAsync = null;
        }
        _boundViewModel = vm;
        if (vm is null)
        {
            return;
        }
        Title = Strings.Format("Sync_TitleFormat", vm.ServerName);
        // 文件夹选择器与确认框只有视图层拿得到,按其他窗口的做法用委托注入。
        vm.PickLocalFolder = PickLocalFolderAsync;
        vm.ConfirmAsync = ConfirmAsync;
        vm.Disposed += OnViewModelDisposed;
    }

    /// <summary>文档先关了(连接断开):视图模型被释放,窗口跟着关,不留一个对着死连接的窗口。</summary>
    private void OnViewModelDisposed(object? sender, EventArgs e)
    {
        if (!_closed)
        {
            this.PostClose();
        }
    }

    private async Task<string?> PickLocalFolderAsync()
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new()
        {
            Title = Strings.Get("Sync_LocalDirectory"),
            AllowMultiple = false,
            SuggestedStartLocation = await StorageDefaults.FolderAsync(this, _boundViewModel?.LocalPath),
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private Task<bool> ConfirmAsync(string message) =>
        MessageDialog.ConfirmAsync(
            this,
            Strings.Get("Sync_Title"),
            message,
            kind: MessageDialogKind.Warning,
            danger: true);

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
        // Esc 只在空闲时关窗:比较或同步进行中按 Esc 多半是想停下,而不是连窗口带进度一起丢掉 —— 那种情况交给「取消」。
        if (e.Key == Key.Escape && _boundViewModel is not { IsBusy: true })
        {
            this.PostClose();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
