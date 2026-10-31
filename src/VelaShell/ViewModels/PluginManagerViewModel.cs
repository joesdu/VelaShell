using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Threading;
using ReactiveUI;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Plugins;
using VelaShell.Infrastructure.Plugins.Market;
using VelaShell.PluginSdk.Versioning;

namespace VelaShell.ViewModels;

/// <summary>一行插件在「有没有新版」这件事上的处境。</summary>
public enum PluginUpdateState
{
    /// <summary>还没查过,或者商店上没有这个插件(旁装的、私有的、应用自带的)。</summary>
    Unknown,

    /// <summary>查过了,本地这一版就是最新的。</summary>
    UpToDate,

    /// <summary>有新版,而且这台宿主装得上。</summary>
    Available,

    /// <summary>有新版,但宿主太旧 —— 得先升级 VelaShell。</summary>
    BlockedByHost
}

/// <summary>插件管理页里的一行。</summary>
public sealed class PluginRowViewModel(PluginDescriptor descriptor, bool hasTerminalGrant) : ReactiveObject
{
    /// <summary>插件 id。</summary>
    public string Id => descriptor.Id;

    /// <summary>显示名称(清单缺失时退化为 id)。</summary>
    public string DisplayName => descriptor.Manifest?.DisplayName ?? descriptor.Id;

    /// <summary>版本(清单缺失时空)。</summary>
    public string Version => descriptor.Manifest is { } m ? $"v{m.Version}" : "";

    /// <summary>宿主模式标签。</summary>
    public string HostMode => descriptor.Manifest?.HostMode.ToString() ?? "";

    /// <summary>
    /// 作者展示文案(如 <c>作者:Joe</c>)。清单的 <c>author</c> 缺省时退回 <c>publisher</c> ——
    /// 老插件只填了 publisher,不该因为新增字段就显示成"无作者"。
    /// </summary>
    public string AuthorText => HasAuthor
        ? Strings.Format("PluginManager_Author", descriptor.Manifest?.Author ?? descriptor.Manifest?.Publisher ?? "")
        : "";

    /// <summary>是否有作者可展示(两个字段都缺时整块隐藏)。</summary>
    public bool HasAuthor => !string.IsNullOrWhiteSpace(descriptor.Manifest?.Author)
                             || !string.IsNullOrWhiteSpace(descriptor.Manifest?.Publisher);

    /// <summary>是否为开发期挂载的插件(显示 DEV 角标)。</summary>
    public bool IsDevelopment => descriptor.IsDevelopment;

    /// <summary>
    /// 已本地化的状态文案。已激活但名下没有开着的面板/标签时显示"后台运行":
    /// 进程内插件激活后常驻,关掉它的标签不会停用它,一律写"运行中"会让人以为关标签没生效。
    /// </summary>
    public string StatusText => descriptor.State == PluginState.Active && OpenSurfaces == 0
        ? Strings.Get("PluginState_Background")
        : Strings.Get($"PluginState_{descriptor.State}");

    /// <summary>该插件此刻开着的面板 / 工作台文档 / 协议会话数(见 <see cref="PluginManager.GetOpenSurfaceCount" />)。</summary>
    public int OpenSurfaces { get; init; }

    /// <summary>状态点着色:运行中为绿。</summary>
    public bool IsOk => descriptor.State == PluginState.Active;

    /// <summary>状态点着色:重启中为黄。</summary>
    public bool IsWarn => descriptor.State == PluginState.Crashed;

    /// <summary>状态点着色:失败/无效/不兼容为红。</summary>
    public bool IsErr => descriptor.State is PluginState.Failed or PluginState.Invalid or PluginState.Incompatible;

    /// <summary>状态点着色:其余(待激活/禁用/已停用)为灰。</summary>
    public bool IsIdle => !IsOk && !IsWarn && !IsErr;

    /// <summary>错误/原因(有则展示)。</summary>
    public string? Error => descriptor.Error;

    /// <summary>是否可切换启停(清单有效才行)。</summary>
    public bool CanToggle => descriptor.Manifest is not null
        && descriptor.State is not (PluginState.Invalid or PluginState.Incompatible);

    /// <summary>当前是否已禁用(决定按钮显示"启用"还是"禁用")。</summary>
    public bool IsDisabled => descriptor.State == PluginState.Disabled;

    /// <summary>启用/禁用按钮文案。</summary>
    public string ToggleText => Strings.Get(IsDisabled ? "PluginManager_Enable" : "PluginManager_Disable");

    /// <summary>该插件当前是否持有敏感能力授权或上传租约(展示撤销入口)。</summary>
    public bool HasTerminalGrant
    {
        get;
        internal set => this.RaiseAndSetIfChanged(ref field, value);
    } = hasTerminalGrant;

    /// <summary>撤销敏感能力授权按钮文案。</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML 绑定只解析实例成员。")]
    public string RevokeText => Strings.Get("PluginManager_RevokePermission");

    /// <summary>
    /// 是否可"重新加载"。只对开发期挂载的插件出现:它的意义是"我刚重编了,跑新代码",
    /// 对已安装插件没有对应的用户动作(那条路是重装)。
    /// </summary>
    public bool CanReload => descriptor.IsDevelopment
                             && descriptor.Manifest is not null
                             && descriptor.State is not (PluginState.Invalid or PluginState.Incompatible
                                 or PluginState.Disabled);

    /// <summary>重新加载按钮文案。</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML 绑定只解析实例成员。")]
    public string ReloadText => Strings.Get("PluginManager_Reload");

    /// <summary>
    /// 是否可「查看日志」。清单有效的插件都有:禁用了、激活失败了正是最要看日志的时候。
    /// 清单都读不出来的(无效)从没跑过,没有它自己的日志可看 —— 原因已经写在那一行的错误里。
    /// </summary>
    public bool CanViewLog => descriptor.Manifest is not null;

    /// <summary>「查看日志」按钮文案。</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML 绑定只解析实例成员。")]
    public string ViewLogText => Strings.Get("PluginManager_ViewLog");

    /// <summary>是否可卸载(用户安装,非应用自带)。</summary>
    public bool CanUninstall { get; init; }

    /// <summary>用户是否关掉了这个插件往消息中心发的通知。</summary>
    public bool NotificationsMuted { get; init; }

    /// <summary>
    /// 是否摆出「静音通知 / 恢复通知」。只在它真往消息中心发过消息(列表里还有)或已经静音时出现:
    /// 每一行都摆一个,对从不发通知的插件只是多一个看不懂的按钮。
    /// </summary>
    public bool CanToggleNotifications { get; init; }

    /// <summary>静音开关的文案。</summary>
    public string NotificationsToggleText =>
        Strings.Get(NotificationsMuted ? "PluginManager_UnmuteNotifications" : "PluginManager_MuteNotifications");

    /// <summary>静音开关的悬停说明。</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML 绑定只解析实例成员。")]
    public string NotificationsToggleTip => Strings.Get("PluginManager_MuteNotificationsTip");

    /// <summary>
    /// 安装时钉住的发布者公钥指纹;<see langword="null" /> = 这一条没有可核对的身份
    /// (应用自带、命令行旁装、或者装的就是个未签名的包)。
    /// </summary>
    public string? PublisherFingerprint { get; init; }

    /// <summary>是否有发布者身份可展示(没有就整块隐藏,而不是显示一行"未知")。</summary>
    public bool HasPublisher => !string.IsNullOrEmpty(PublisherFingerprint);

    /// <summary>
    /// 发布者展示文案。指纹<b>给全</b>,由界面负责截断显示与悬停出全文 ——
    /// 在这里先截成八位再交出去,用户拿到的就是一串没法跟作者官方渠道对照的东西,
    /// 而"看着像能核对"比"明说不能核对"更糟。
    /// </summary>
    public string PublisherText =>
        HasPublisher ? Strings.Format("PluginManager_PublisherPinned", PublisherFingerprint) : "";

    /// <summary>商店上的最新版本号;没有比本地更新的版本时为 <see langword="null" />。</summary>
    public string? AvailableVersion { get; init; }

    /// <summary>这一行的更新处境。</summary>
    public PluginUpdateState UpdateState { get; init; }

    /// <summary>宿主太旧时缺的那一项(如 <c>VelaShell 0.6.0</c>);其余情况为 <see langword="null" />。</summary>
    public string? UpdateBlocker { get; init; }

    /// <summary>
    /// 商店上那一版的发布者,与安装时钉住的是不是同一个。
    /// </summary>
    /// <remarks>
    /// 为真时更新可以直接装 —— 用户当初点头认下的就是这把钥匙。为假(换人了、这一版没签名、
    /// 或者本机压根没钉住过谁)则必须走完整确认流程,把指纹摆出来让用户自己判断。
    /// </remarks>
    public bool PublisherUnchanged { get; init; }

    /// <summary>是否显示「更新到 x.y.z」按钮。</summary>
    public bool CanUpdate => UpdateState == PluginUpdateState.Available && CanUninstall;

    /// <summary>是否显示「先升级 VelaShell」的提示。</summary>
    public bool IsUpdateBlocked => UpdateState == PluginUpdateState.BlockedByHost;

    /// <summary>更新按钮文案。</summary>
    public string UpdateText => Strings.Format("PluginManager_UpdateTo", AvailableVersion ?? "");

    /// <summary>宿主太旧时的提示文案。</summary>
    public string UpdateBlockedText =>
        Strings.Format("PluginManager_UpdateNeedsHost", AvailableVersion ?? "", UpdateBlocker ?? "");

    /// <summary>卸载按钮文案。</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML 绑定只解析实例成员。")]
    public string UninstallText => Strings.Get("PluginManager_Uninstall");
}

/// <summary>
/// 插件管理页视图模型:列出全部插件、启停、撤销敏感能力授权。
/// 订阅 <see cref="PluginManager.Changed" /> 自动刷新(封送到 UI 线程)。
/// </summary>
public sealed class PluginManagerViewModel : ReactiveObject, IDisposable
{
    private readonly PluginManager _manager;
    private readonly PluginPermissionGate? _gate;
    private readonly IPluginMarketClient? _market;
    private readonly PluginNotificationService? _notifications;

    /// <summary>上次问到的商店版本表(按插件 id)。每次刷新列表都拿它比对,不再重新外呼。</summary>
    private IReadOnlyDictionary<string, PluginMarketVersion> _latest =
        new Dictionary<string, PluginMarketVersion>(StringComparer.Ordinal);

    /// <summary>检查更新的重入闸。与界面无关,纯粹是"别把同一个请求发两遍"。</summary>
    private int _checking;
    private int _refreshingPermissions;
    private bool _disposed;
    private readonly Action _onChanged;
    private readonly Action<string, int> _onDebugAttach;

    /// <summary>插件行集合(UI 线程更新)。</summary>
    public ObservableCollection<PluginRowViewModel> Plugins { get; } = [];

    /// <summary>标题文案。</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML 绑定只解析实例成员。")]
    public string Title => Strings.Get("PluginManager_Title");

    /// <summary>"安装 .vpx" 按钮文案。</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML 绑定只解析实例成员。")]
    public string InstallText => Strings.Get("PluginManager_Install");

    /// <summary>是否可安装(有可写用户目录)。</summary>
    public bool CanInstall => _manager.IsInstallSupported;

    /// <summary>
    /// 安装按钮上方的安全须知。安装是本页唯一会引入外部代码的动作,风险说明摆在
    /// 按下之前 —— 签名对话框只在包未签名/发布者陌生时才弹,不能当成唯一的告知点。
    /// </summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML 绑定只解析实例成员。")]
    public string SecurityTipText => Strings.Get("PluginManager_SecurityTip");

    /// <summary>插件商店链接文案。</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML 绑定只解析实例成员。")]
    public string MarketText => Strings.Get("PluginManager_Market");

    /// <summary>插件商店链接的悬停提示。</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML 绑定只解析实例成员。")]
    public string MarketTipText => Strings.Get("PluginManager_MarketTip");

    /// <summary>插件商店地址(点击链接后交给系统浏览器)。</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML 绑定只解析实例成员。")]
    public string MarketUrl => "https://market.easilynet.top";

    /// <summary>「检查更新」按钮文案。</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML 绑定只解析实例成员。")]
    public string CheckUpdatesText => Strings.Get("PluginManager_CheckUpdates");

    /// <summary>「全部更新」按钮文案。</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML 绑定只解析实例成员。")]
    public string UpdateAllText => Strings.Get("PluginManager_UpdateAll");

    /// <summary>能不能检查更新(有商店客户端,且这台机器装得了插件)。</summary>
    public bool CanCheckUpdates => _market is not null && _manager.IsInstallSupported;

    /// <summary>是否存在可以直接装的更新(「全部更新」按钮据此显隐)。</summary>
    public bool HasUpdates
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>空态文案。</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML 绑定只解析实例成员。")]
    public string EmptyText => Strings.Get("PluginManager_Empty");

    /// <summary>顶部状态提示(安装成功/失败),null 时不显示。</summary>
    public string? Notice
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>是否无插件(空态)。</summary>
    public bool IsEmpty
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    /// <summary>构造并加载。</summary>
    /// <param name="manager">插件运行时。</param>
    /// <param name="gate">敏感能力授权闸;缺席时不显示撤销入口。</param>
    /// <param name="market">
    /// 插件商店的只读客户端;缺席时整个更新检查不存在(headless 测试与离线部署走这条)。
    /// </param>
    /// <param name="notifications">插件通知(静音开关);缺席时不显示静音入口。</param>
    public PluginManagerViewModel(PluginManager manager, PluginPermissionGate? gate,
        IPluginMarketClient? market = null, PluginNotificationService? notifications = null)
    {
        _manager = manager;
        _gate = gate;
        _market = market;
        _notifications = notifications;
        _onChanged = () => Dispatcher.UIThread.Post(() => _ = ReloadAsync());
        // 等待调试器的隔离插件:把 pid 摆到管理页上。它同时进日志、落 pid 文件,
        // 但开发者此刻多半正开着这个页面,让他去翻日志属于本可以省掉的一步。
        _onDebugAttach = (pluginId, pid) =>
            SetNotice(Strings.Format("PluginManager_WaitingForDebugger", pluginId, pid));
        _manager.Changed += _onChanged;
        _manager.DebugAttachRequested += _onDebugAttach;
        _ = ReloadAsync();
        // 打开这个页面的人本来就是来管插件的,此刻问一次商店符合预期;
        // 其余任何时候都不外呼(见 PRIVACY.md 对每一条出站请求的逐条交代)。
        _ = CheckUpdatesAsync();
    }

    /// <summary>切换某插件启停。</summary>
    public async Task ToggleAsync(PluginRowViewModel row)
    {
        if (!row.CanToggle)
        {
            return;
        }
        if (row.IsDisabled)
        {
            await _manager.EnableAsync(row.Id).ConfigureAwait(false);
        }
        else
        {
            await _manager.DisableAsync(row.Id).ConfigureAwait(false);
        }
        // Changed 事件会触发刷新;这里不重复。
    }

    /// <summary>「查看日志」窗口的视图模型:读的是这个插件自己那份日志的尾部。</summary>
    /// <param name="row">插件行。</param>
    public PluginLogViewModel CreateLogViewer(PluginRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        string id = row.Id;
        return new(id, row.DisplayName, () => _manager.ReadLogTail(id), _manager.LogDirectory);
    }

    /// <summary>
    /// 重新加载某个开发期插件:停用 → 重读清单 → 重新装载。开发内环的一步:
    /// 改完代码 <c>dotnet build</c>,点这里就跑上新代码,不必重启 VelaShell。
    /// </summary>
    public async Task ReloadPluginAsync(PluginRowViewModel row)
    {
        if (!row.CanReload)
        {
            return;
        }
        await _manager.ReloadAsync(row.Id).ConfigureAwait(false);
        // Changed 事件会触发刷新;这里不重复。
    }

    /// <summary>刷新当前行的撤销入口，不重建列表、不查询插件商店。</summary>
    internal async Task RefreshPermissionGrantsAsync()
    {
        if (_gate is null || _disposed || Interlocked.CompareExchange(ref _refreshingPermissions, 1, 0) != 0) return;
        try
        {
            PluginRowViewModel[] rows = await Dispatcher.UIThread.InvokeAsync(() => Plugins.ToArray());
            foreach (PluginRowViewModel row in rows)
            {
                bool granted = await _gate.HasGrantAsync(row.Id).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!_disposed && Plugins.Contains(row)) row.HasTerminalGrant = granted;
                });
            }
        }
        finally { Interlocked.Exchange(ref _refreshingPermissions, 0); }
    }

    /// <summary>撤销某插件的敏感能力授权和上传租约。</summary>
    public async Task RevokeTerminalAsync(PluginRowViewModel row)
    {
        if (_gate is not null)
        {
            await _gate.RevokeAsync(row.Id).ConfigureAwait(false);
            await ReloadAsync().ConfigureAwait(false);
        }
    }

    /// <summary>静音或恢复某插件的通知(已经发进消息中心的那些不动,要删在消息中心里删)。</summary>
    public async Task ToggleNotificationsAsync(PluginRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (_notifications is null || !row.CanToggleNotifications)
        {
            return;
        }
        await _notifications.SetMutedAsync(row.Id, !row.NotificationsMuted).ConfigureAwait(false);
        await ReloadAsync().ConfigureAwait(false);
    }

    /// <summary>卸载某插件(用户安装的)。调用方已确认。</summary>
    public async Task UninstallAsync(PluginRowViewModel row)
    {
        if (row.CanUninstall)
        {
            await _manager.UninstallAsync(row.Id).ConfigureAwait(false);
            // Changed 事件会触发刷新。
        }
    }

    /// <summary>问一次商店:本机装的这些插件有没有新版。结果留在内存里,不重复外呼。</summary>
    /// <remarks>
    /// 只把**用户自己装的**插件 id 带出门:应用自带件与开发期挂载的插件商店上根本没有,
    /// 把它们的 id 一并发出去,多说的每一个字都没有换来任何东西。
    /// </remarks>
    public async Task CheckUpdatesAsync()
    {
        if (_market is null || Interlocked.CompareExchange(ref _checking, 1, 0) != 0)
        {
            return;
        }
        try
        {
            string[] ids = [.. _manager.Plugins
                                       .Where(p => p.Manifest is not null && _manager.IsUninstallable(p.Id))
                                       .Select(p => p.Id)];
            if (ids.Length == 0)
            {
                return;
            }
            SetNotice(Strings.Get("PluginManager_CheckingUpdates"));
            _latest = await _market.GetLatestAsync(ids).ConfigureAwait(false);
            await ReloadAsync().ConfigureAwait(false);
            int outdated = _manager.Plugins.Count(p =>
                ResolveUpdate(p, _manager.IsUninstallable(p.Id)).State
                    is PluginUpdateState.Available or PluginUpdateState.BlockedByHost);
            SetNotice(outdated == 0
                ? Strings.Get("PluginManager_NoUpdates")
                : Strings.Format("PluginManager_UpdatesFound", outdated));
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    /// <summary>把某一行对应的新版包下到数据根的 <c>plugin-updates</c> 目录并返回路径;失败时返回 <see langword="null" />。</summary>
    /// <remarks>
    /// **只下载,不安装。** 装不装、要不要先问一句,由窗口按发布者变没变来决定 ——
    /// 那一问要摆两个指纹给用户看,只有界面问得出口。用完由窗口交给 <see cref="DiscardDownloadedUpdate" /> 删掉。
    /// 不下到系统临时目录的原因见 <see cref="Infrastructure.Persistence.VelaShellStoragePaths.PluginUpdateDirectory" />。
    /// </remarks>
    /// <param name="row">要更新的那一行。</param>
    public async Task<string?> DownloadUpdateAsync(PluginRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (_market is null || row.AvailableVersion is not { Length: > 0 } version)
        {
            return null;
        }
        string destination = Path.Combine(
            new Infrastructure.Persistence.VelaShellStoragePaths().PluginUpdateDirectory, $"{row.Id}-{version}.vpx");
        SetNotice(Strings.Format("PluginManager_Updating", row.DisplayName));
        try
        {
            await _market.DownloadAsync(row.Id, version, destination).ConfigureAwait(false);
            return destination;
        }
        catch (Exception ex)
        {
            SetNotice(Strings.Format("PluginManager_UpdateFailed", ex.Message));
            return null;
        }
    }

    /// <summary>删掉 <see cref="DownloadUpdateAsync" /> 下的那个包(装上了、没装、用户取消都删);删不掉只记一行。</summary>
    /// <param name="path">包路径。</param>
    public static void DiscardDownloadedUpdate(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[VelaShell] The downloaded plugin update could not be deleted ({path}): {ex.Message}");
        }
    }

    /// <summary>校验插件包并返回发布者信任状态与公钥指纹。</summary>
    public PluginPackageTrustInfo InspectPackageTrust(string vpxPath) =>
        _manager.InspectPackageTrust(vpxPath);

    /// <summary>把已由用户核对的签名发布者加入本机信任库。</summary>
    public Task<string> TrustPackagePublisherAsync(string vpxPath) =>
        _manager.TrustPackagePublisherAsync(vpxPath);

    /// <summary>信任库读不出来时的原因;可用时为 <see langword="null" />。</summary>
    public Task<string?> GetTrustStoreErrorAsync() => _manager.GetTrustStoreErrorAsync();

    /// <summary>重建读不出来的信任库。调用方已向用户说明代价并取得确认。</summary>
    public async Task<bool> ResetTrustStoreAsync()
    {
        try
        {
            await _manager.ResetTrustStoreAsync().ConfigureAwait(false);
            SetNotice(Strings.Get("PluginManager_TrustStoreResetDone"));
            return true;
        }
        catch (Exception ex)
        {
            SetNotice(Strings.Format("PluginManager_TrustStoreResetFailed", ex.Message));
            return false;
        }
    }

    /// <summary>从 .vpx 文件安装。未知来源只能由界面明确确认后单次放行。</summary>
    /// <param name="vpxPath">包路径。</param>
    /// <param name="allowUntrustedPackage">是否单次放行未签名 / 发布者陌生的包。</param>
    /// <param name="allowPublisherChange">是否单次放行"换了发布者"的覆盖安装。</param>
    /// <exception cref="PluginPublisherChangedException">
    /// 发布者与钉住的那一个对不上,而调用方没给授权。**这一条不吞** —— 它不是失败,
    /// 而是只有界面才问得出口的一个问题(要摆两个指纹给用户看)。吞成一行状态提示,
    /// 用户看到的就是"装不上",既不知道为什么,也没有回答的机会。
    /// </exception>
    public async Task InstallFromVpxAsync(string vpxPath, bool allowUntrustedPackage = false,
        bool allowPublisherChange = false)
    {
        try
        {
            string id = await _manager
                              .InstallFromVpxAsync(vpxPath, allowUntrustedPackage, allowPublisherChange)
                              .ConfigureAwait(false);
            SetNotice(Strings.Format("PluginManager_Installed", id));
        }
        catch (PluginPublisherChangedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetNotice(Strings.Format("PluginManager_InstallFailed", ex.Message));
        }
    }

    private void SetNotice(string text) =>
        Dispatcher.UIThread.Post(() => Notice = text);

    private async Task ReloadAsync()
    {
        IReadOnlyList<PluginDescriptor> descriptors = _manager.Plugins;
        var rows = new List<PluginRowViewModel>(descriptors.Count);
        foreach (PluginDescriptor descriptor in descriptors)
        {
            bool grant = _gate is not null && await _gate.HasGrantAsync(descriptor.Id).ConfigureAwait(false);
            bool uninstallable = _manager.IsUninstallable(descriptor.Id);
            string? pinned = await _manager.GetPinnedPublisherFingerprintAsync(descriptor.Id).ConfigureAwait(false);
            (PluginUpdateState state, string? available, string? blocker) = ResolveUpdate(descriptor, uninstallable);
            bool muted = _notifications is not null && await _notifications.IsMutedAsync(descriptor.Id).ConfigureAwait(false);
            rows.Add(new(descriptor, grant)
            {
                CanUninstall = uninstallable,
                NotificationsMuted = muted,
                CanToggleNotifications = _notifications is not null && (muted || _notifications.HasMessages(descriptor.Id)),
                OpenSurfaces = _manager.GetOpenSurfaceCount(descriptor.Id),
                PublisherFingerprint = pinned,
                UpdateState = state,
                AvailableVersion = available,
                UpdateBlocker = blocker,
                PublisherUnchanged = pinned is not null
                                     && _latest.TryGetValue(descriptor.Id, out PluginMarketVersion? offered)
                                     && offered.PublisherFingerprint is { Length: > 0 } fingerprint
                                     && string.Equals(fingerprint, pinned, StringComparison.OrdinalIgnoreCase)
            });
        }
        void Apply()
        {
            Plugins.Clear();
            foreach (PluginRowViewModel row in rows.OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                Plugins.Add(row);
            }
            IsEmpty = Plugins.Count == 0;
            HasUpdates = Plugins.Any(p => p.CanUpdate);
        }
        if (Dispatcher.UIThread.CheckAccess())
        {
            Apply();
        }
        else
        {
            await Dispatcher.UIThread.InvokeAsync(Apply);
        }
    }

    /// <summary>把「商店上那一版」与「本地这一版」对起来:该不该升、升不升得上去。</summary>
    /// <remarks>
    /// 「哪个更新」按 <see cref="SemanticVersion.CompareTo" /> 的完整优先级比(与应用自更新、CLI、商店同一份),**不是**
    /// <c>PluginManager</c> 装载闸用的 <see cref="SemanticVersion.CompareCoreTo" /> —— 后者只比数字段,
    /// 本地装着 <c>1.2.0-beta.1</c> 时它会认为商店上的 <c>1.2.0</c> 不算更新。
    /// </remarks>
    /// <param name="descriptor">本地插件。</param>
    /// <param name="uninstallable">是不是用户自己装的(只有这种才谈得上更新)。</param>
    private (PluginUpdateState State, string? Available, string? Blocker) ResolveUpdate(
        PluginDescriptor descriptor, bool uninstallable)
    {
        if (!uninstallable
            || descriptor.Manifest is not { } manifest
            || !_latest.TryGetValue(descriptor.Id, out PluginMarketVersion? latest))
        {
            // 应用自带、开发期挂载、商店上查不到的,都没有"更新"这个动作。
            return (PluginUpdateState.Unknown, null, null);
        }
        if (CompareForUpdate(latest.Version, manifest.Version) is { } settled)
        {
            if (settled == PluginUpdateState.Unknown)
            {
                Trace.WriteLine($"[PluginManager] Cannot compare versions of {descriptor.Id}: local '{manifest.Version}', store '{latest.Version}'.");
            }
            return (settled, null, null);
        }
        string? blocker = _manager.DescribeUpdateBlocker(latest.ApiLevel, latest.MinHostVersion, latest.MinSdkVersion);
        return blocker is null
            ? (PluginUpdateState.Available, latest.Version, null)
            : (PluginUpdateState.BlockedByHost, latest.Version, blocker);
    }

    /// <summary>
    /// 商店那一版比本地新时为 <see langword="null" />(还要再看装不装得上);不新时为 <see cref="PluginUpdateState.UpToDate" />;
    /// 任一侧解析不出时为 <see cref="PluginUpdateState.Unknown" /> —— 比不了就不报「有新版」,也不冒充「已是最新」
    /// (与 vela-plugin 同一口径)。旧的 UpdateVersion 对后缀宽松,会把本地的 <c>1.0.0-beta.01</c> 与商店的
    /// <c>1.0.0</c> 比出「有新版」;这类清单现在发现期就判 Invalid,走到这里的只剩商店返回了怪写法。
    /// </summary>
    internal static PluginUpdateState? CompareForUpdate(string? remoteVersion, string? localVersion) =>
        !SemanticVersion.TryParse(remoteVersion, out SemanticVersion remote)
        || !SemanticVersion.TryParse(localVersion, out SemanticVersion local)
            ? PluginUpdateState.Unknown
            : remote.CompareTo(local) > 0 ? null : PluginUpdateState.UpToDate;

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _manager.Changed -= _onChanged;
        _manager.DebugAttachRequested -= _onDebugAttach;
    }
}
