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
            await InstallWithPromptsAsync(vm, path);
        }
    });

    /// <summary>
    /// 走一遍"该问的都问过"的安装:未签名 / 陌生发布者先确认一次,装的时候撞上"换了发布者"
    /// 再把两个指纹摆出来问第二次。
    /// </summary>
    /// <remarks>
    /// 手动装 <c>.vpx</c> 与"更新时发布者变过"共用这一条。两条路各写一份的话,
    /// 迟早有一份会漏掉其中一问 —— 而漏掉的那一问正是拦住冒名覆盖安装的那道闸。
    /// </remarks>
    /// <param name="vm">插件管理视图模型。</param>
    /// <param name="path">包路径。</param>
    private async Task InstallWithPromptsAsync(PluginManagerViewModel vm, string path)
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

    /// <summary>行内「更新到 x.y.z」:先把包下下来,再按发布者变没变决定怎么装。</summary>
    /// <remarks>
    /// 指纹与安装时钉住的那一个对得上,就直接装 —— 用户当初点头认下的就是这把钥匙,
    /// 为同一个人的下一版再问一遍,问的是同一个问题。对不上(换人了,或者这一版干脆没签名)
    /// 则一律走完整确认流程,把指纹摆出来让用户自己判断。
    /// </remarks>
    private void Update_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (sender is Control { DataContext: PluginRowViewModel row } && ViewModel is { } vm)
        {
            await UpdateRowAsync(vm, row);
        }
    });

    /// <summary>「全部更新」:把此刻能直接装的都装了。</summary>
    private void UpdateAll_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (ViewModel is not { } vm)
        {
            return;
        }
        // 先快照:每装完一个,Changed 事件都会把 Plugins 整体重建,边遍历边重建必然出事。
        foreach (PluginRowViewModel row in vm.Plugins.Where(r => r.CanUpdate).ToList())
        {
            await UpdateRowAsync(vm, row);
        }
    });

    /// <summary>「检查更新」:离线时那一次查不到,用户得有个地方重来。</summary>
    private void CheckUpdates_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (ViewModel is { } vm)
        {
            await vm.CheckUpdatesAsync();
        }
    });

    /// <summary>下载并安装某一行的新版本。</summary>
    /// <param name="vm">插件管理视图模型。</param>
    /// <param name="row">要更新的那一行。</param>
    private async Task UpdateRowAsync(PluginManagerViewModel vm, PluginRowViewModel row)
    {
        if (await vm.DownloadUpdateAsync(row) is not { } package)
        {
            return;
        }
        if (row.PublisherUnchanged)
        {
            // 同一把私钥签的下一版:发布者连续性那一闸自会放行。这里的 allowUntrustedPackage
            // 补的是另一种情况 —— 这把公钥本就不在全局信任库里(它当初正是这么装进来的),
            // 不给放行的话,一个从未换过人的插件反而永远升不了级。
            await vm.InstallFromVpxAsync(package, allowUntrustedPackage: true);
            return;
        }
        await InstallWithPromptsAsync(vm, package);
    }


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
