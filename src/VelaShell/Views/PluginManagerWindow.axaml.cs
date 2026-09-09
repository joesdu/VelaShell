using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Plugins;
using VelaShell.PluginSdk.Packaging;
using VelaShell.ViewModels;
using FireAndForget = VelaShell.Services.FireAndForget;

namespace VelaShell.Views;

/// <summary>
/// 插件管理窗口(自绘卡片窗口,与资源监视器同规格):列出插件、启停、卸载、
/// 撤销终端授权、从 .vpx 安装。
/// </summary>
public partial class PluginManagerWindow : Window
{
    /// <summary>初始化窗口(macOS 退回不透明矩形,与其它自绘窗体同一结论)。</summary>
    public PluginManagerWindow()
    {
        InitializeComponent();
        if (OperatingSystem.IsMacOS())
        {
            TransparencyLevelHint = [WindowTransparencyLevel.None];
            if (this.TryFindResource("VelaBgPage", out object? page) && page is IBrush brush)
            {
                Background = brush;
            }
            ApplyCardShape(rounded: false);
        }
        Closed += (_, _) => (DataContext as PluginManagerViewModel)?.Dispose();
    }

    private PluginManagerViewModel? ViewModel => DataContext as PluginManagerViewModel;

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

    private void Toggle_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: PluginRowViewModel row } && ViewModel is { } vm)
        {
            _ = vm.ToggleAsync(row);
        }
    }

    private void Reload_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: PluginRowViewModel row } && ViewModel is { } vm)
        {
            _ = vm.ReloadPluginAsync(row);
        }
    }

    private void Revoke_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: PluginRowViewModel row } && ViewModel is { } vm)
        {
            _ = vm.RevokeTerminalAsync(row);
        }
    }

    private void Uninstall_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (sender is not Control { DataContext: PluginRowViewModel row } || ViewModel is not { } vm)
        {
            return;
        }
        bool confirmed = await MessageDialog.ConfirmAsync(this,
            Strings.Get("PluginManager_Uninstall"),
            Strings.Format("PluginManager_ConfirmUninstall", row.DisplayName),
            confirmText: Strings.Get("PluginManager_Uninstall"),
            danger: true);
        if (confirmed)
        {
            await vm.UninstallAsync(row);
        }
    });

    private void Install_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (ViewModel is not { } vm)
        {
            return;
        }
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Get("PluginManager_Install"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Strings.Get("PluginManager_VpxFilter")) { Patterns = ["*.vpx"] }
            ]
        });
        if (files is [{ } file] && file.TryGetLocalPath() is { } path)
        {
            PluginPackageTrustInfo trust;
            try
            {
                trust = vm.InspectPackageTrust(path);
            }
            catch
            {
                // 让统一安装路径生成本地化错误提示(损坏摘要/错误格式等)。
                await vm.InstallFromVpxAsync(path);
                return;
            }
            bool allowUntrusted = false;
            bool trustPublisher = false;
            if (trust.State == VpxSignatureState.Unsigned)
            {
                allowUntrusted = await MessageDialog.ConfirmAsync(this,
                    Strings.Get("PluginManager_UntrustedTitle"),
                    Strings.Format("PluginManager_UnsignedWarning", Path.GetFileName(path)),
                    confirmText: Strings.Get("PluginManager_InstallAnyway"),
                    danger: true);
                if (!allowUntrusted)
                {
                    return;
                }
            }
            else if (trust.State == VpxSignatureState.Untrusted)
            {
                trustPublisher = await MessageDialog.ConfirmAsync(this,
                    Strings.Get("PluginManager_UntrustedTitle"),
                    Strings.Format("PluginManager_UntrustedWarning",
                        Path.GetFileName(path), trust.PublisherFingerprint ?? "(unavailable)"),
                    confirmText: Strings.Get("PluginManager_TrustAndInstall"),
                    danger: true);
                if (!trustPublisher)
                {
                    return;
                }
                allowUntrusted = true;
            }
            // 信任发布者这一步挪到安装之后:这个包可能正是在顶替一个已装插件的发布者身份,
            // 而那一问要等解出包里的 id 才问得出来。先把新公钥收进信任库,等于用户还没被告知
            // "换人了",就已经替这把钥匙签下了"以后它的包一律直接信任"。
            if (!await InstallWithPublisherCheckAsync(vm, path, allowUntrusted))
            {
                return;
            }
            if (trustPublisher)
            {
                try
                {
                    await vm.TrustPackagePublisherAsync(path);
                }
                catch (Exception ex)
                {
                    // 装是装上了(它自带的收据钉住了这把公钥),只是没进全局信任库 ——
                    // 失败方向朝着"信任更少",说清楚即可,不必回滚一次用户明确要的安装。
                    await MessageDialog.ShowMessageAsync(this,
                        Strings.Get("PluginManager_UntrustedTitle"), ex.Message, MessageDialogKind.Error);
                }
            }
        }
    });

    /// <summary>
    /// 装一次;撞上"发布者换了"就把两个指纹摆给用户,认了再带着授权装第二次。
    /// </summary>
    /// <remarks>
    /// 拦下时那个已经装着的插件一根毫毛都没动(闸在卸载旧版之前),所以"再来一次"是安全的。
    /// 之所以不由界面先去解析包、把这一问并进前面那个对话框:那样"问的时候看到的"与
    /// "装下去的"就成了两次独立的解析,中间留一条缝。判断只在 PluginManager 里做一次,
    /// 界面只负责把它抛出来的那几个字段念给用户听。
    /// </remarks>
    /// <returns>是否已经装上。</returns>
    private async Task<bool> InstallWithPublisherCheckAsync(
        PluginManagerViewModel vm, string path, bool allowUntrusted)
    {
        try
        {
            await vm.InstallFromVpxAsync(path, allowUntrusted);
            return true;
        }
        catch (PluginPublisherChangedException ex)
        {
            bool approved = await MessageDialog.ConfirmAsync(this,
                Strings.Get("PluginManager_PublisherChangedTitle"),
                Strings.Format("PluginManager_PublisherChangedWarning",
                    ex.DisplayName, ex.PinnedFingerprint,
                    ex.PackageFingerprint ?? Strings.Get("PluginManager_PackageUnsigned")),
                confirmText: Strings.Get("PluginManager_InstallAnyway"),
                danger: true);
            if (!approved)
            {
                return false;
            }
            await vm.InstallFromVpxAsync(path, allowUntrusted, allowPublisherChange: true);
            return true;
        }
    }

    /// <summary>点击"插件商店"链接:交给系统默认浏览器打开(地址存放在控件 Tag)。</summary>
    private void OpenMarket_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (sender is not Control { Tag: string url }
            || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }
        await top.Launcher.LaunchUriAsync(uri);
    });

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

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != WindowStateProperty)
        {
            return;
        }
        bool normal = WindowState == WindowState.Normal;
        if (this.FindControl<Panel>("ResizeGrips") is { } grips)
        {
            grips.IsVisible = normal;
        }
        if (!OperatingSystem.IsMacOS())
        {
            ApplyCardShape(normal);
        }
    }

    private const double InnerRadius = 7;

    private void ApplyCardShape(bool rounded)
    {
        if (this.FindControl<Border>("RootCard") is { } card)
        {
            card.Margin = rounded ? new Thickness(16) : default;
            card.BorderThickness = rounded ? new Thickness(1) : default;
            card.CornerRadius = rounded ? new CornerRadius(8) : default;
            // 铺满态没有外边距,投影只会被整块裁掉,白付一次模糊;圆角态再从令牌取回。
            // 不能写成 rounded ? card.BoxShadow : default —— 那样最大化清掉之后,
            // 还原时读到的已经是清掉后的空值,投影一去不返(自身赋值救不回来)。
            card.BoxShadow =
                rounded
                && this.TryFindResource("VelaShadowWindow", out object? shadow)
                && shadow is BoxShadows shadows
                    ? shadows
                    : default;
        }
        ResizeGripLayout.Apply(ResizeGrips, rounded);
        if (this.FindControl<Border>("TitleBarStrip") is { } strip)
        {
            strip.CornerRadius = rounded ? new CornerRadius(InnerRadius, InnerRadius, 0, 0) : default;
        }
    }
}
