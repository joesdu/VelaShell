using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using System.Linq;
using System.Reactive.Linq;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using PulseTerm.App.ViewModels;
using PulseTerm.Core.Models;
using PulseTerm.Presentation.Services;
using ReactiveUI;

namespace PulseTerm.App.Views;

public partial class MainWindow : Window
{
    /// <summary>File panel height restored when the panel reopens (§6 drag to grow). Default 360
    /// = sidebar recent-connections block (320) + footer (40), so the file panel's top divider
    /// lands on the same horizontal line as the sidebar's tree/recent splitter (用户反馈).</summary>
    private double _lastFileRowHeight = 360;
    private IDisposable? _fileBrowserVisibilitySub;

    private PulseTerm.Core.Data.ISettingsService? _settingsService;
    private AppSettings? _settings;
    private bool _forceClose;
    private bool _sidebarOnRight;

    public MainWindow()
    {
        InitializeComponent();

        if (this.FindControl<SidebarView>("SidebarHost") is { } sidebar)
        {
            sidebar.OpenConnectionProfileRequested += OnOpenConnectionProfileRequested;
            sidebar.RecentConnectRequested += OnSidebarRecentConnectRequested;
            sidebar.SettingsRequested += (_, _) => _ = OpenSettingsAsync();
        }

        DataContextChanged += (_, _) => HookFileBrowserVisibility();
        Opened += OnWindowOpened;
    }

    /// <summary>Collapses/expands the file-panel rows as FileBrowser.IsVisible flips. WhenAnyValue
    /// tracks through the FileBrowser property itself, so per-tab rebinds re-subscribe automatically.</summary>
    private void HookFileBrowserVisibility()
    {
        _fileBrowserVisibilitySub?.Dispose();
        _fileBrowserVisibilitySub = null;

        if (DataContext is not MainWindowViewModel vm)
            return;

        _fileBrowserVisibilitySub = vm
            .WhenAnyValue(x => x.FileBrowser.IsVisible)
            .Subscribe(visible => Avalonia.Threading.Dispatcher.UIThread.Post(() => SetFileRowsVisible(visible)));
    }

    private void SetFileRowsVisible(bool visible)
    {
        if (this.FindControl<Grid>("MainAreaGrid") is not { RowDefinitions.Count: >= 3 } grid)
            return;

        var splitterRow = grid.RowDefinitions[1];
        var fileRow = grid.RowDefinitions[2];

        if (visible)
        {
            splitterRow.Height = new GridLength(5);
            fileRow.MinHeight = 120;
            fileRow.Height = new GridLength(Math.Max(_lastFileRowHeight, 120));
        }
        else
        {
            // Remember the user's dragged height so reopening restores it.
            if (fileRow.Height.IsAbsolute && fileRow.Height.Value > 0)
                _lastFileRowHeight = fileRow.Height.Value;

            fileRow.MinHeight = 0;
            fileRow.Height = new GridLength(0);
            splitterRow.Height = new GridLength(0);
        }
    }

    private async void OnWindowOpened(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.TerminalSearchRequested += OnTerminalSearchRequested;
            vm.NewConnectionRequested += (_, _) => _ = OpenProfileDialogAsync(existing: null);
            vm.SettingsRequested += (_, _) => _ = OpenSettingsAsync();
            vm.InteractiveAuthenticator = PromptCredentialsAsync;
            vm.MultilinePasteConfirmer = ConfirmMultilinePasteAsync;
            vm.ExportBufferRequested += (_, _) => _ = ExportTerminalBufferAsync(vm);

            // 资源管理器树:右键连接/双击连接 + 右键编辑。
            if (vm.Sidebar.SessionTree is { } tree)
            {
                tree.ConnectRequested += profile => Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => _ = vm.TryConnectProfileAsync(profile));
                tree.EditRequested += profile => Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => _ = OpenProfileDialogAsync(profile));

                // 打开 SFTP:先连接(已连接则新开标签),随后展开文件浏览面板。
                tree.OpenSftpRequested += profile => Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
                {
                    // 连接失败已在标签页内以覆盖层提示(设计 yxjmg),这里不再弹全局框;
                    // 仅在连上后展开 SFTP 面板。
                    var tab = await vm.TryConnectProfileAsync(profile);
                    if (tab is { ConnectionStatus: PulseTerm.Core.Models.SessionStatus.Connected }
                        && !vm.FileBrowser.IsVisible)
                    {
                        vm.ToggleFileBrowser();
                    }
                });

                // 端口转发:打开隧道管理面板(全局非模态,见 fuXS7)。
                tree.PortForwardRequested += _ => Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => vm.IsTunnelPanelOpen = true);

                // 断开连接:断开该会话所有已连接的终端标签(保留缓冲以便重连)。
                tree.DisconnectRequested += profile => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    foreach (var tab in vm.TabBar.Tabs.OfType<TerminalTabViewModel>().Where(t =>
                                 t.SessionId == profile.Id
                                 && t.ConnectionStatus != PulseTerm.Core.Models.SessionStatus.Disconnected))
                    {
                        tab.DisconnectCommand.Execute().Subscribe();
                    }
                });
            }

            await vm.InitializeAsync();
        }

        // 外观/行为设置:启动时应用一次,设置保存后热更新。
        if (App.Current is App { Services: { } services }
            && services.GetService<PulseTerm.Core.Data.ISettingsService>() is { } settingsService)
        {
            _settingsService = settingsService;
            settingsService.SettingsSaved += OnSettingsSavedForWindow;
            Closed += (_, _) => settingsService.SettingsSaved -= OnSettingsSavedForWindow;
            try
            {
                _settings = await settingsService.GetSettingsAsync();
                ApplyWindowAppearance(_settings);
            }
            catch
            {
                // 设置读取失败不影响窗口本身。
            }

            await RestoreSessionsAsync(_settings);
        }
    }

    /// <summary>恢复会话(设置 → 常规 → 启动):重连上次退出时在线的连接。缺凭据的
    /// 配置会走既有的登录验证弹窗;单个失败不影响其余会话。</summary>
    private async Task RestoreSessionsAsync(AppSettings? settings)
    {
        if (settings?.General.RestoreSessionsOnStartup != true
            || settings.General.LastOpenProfileIds.Count == 0
            || DataContext is not MainWindowViewModel vm
            || App.Current is not App { Services: { } services }
            || services.GetService<PulseTerm.Core.Data.ISessionRepository>() is not { } repository)
        {
            return;
        }

        foreach (var profileId in settings.General.LastOpenProfileIds.Distinct().ToList())
        {
            try
            {
                var profile = await repository.GetSessionAsync(profileId);
                if (profile is not null)
                    await vm.TryConnectProfileAsync(profile);
            }
            catch
            {
                // 配置已删除或连接失败:跳过,继续恢复其余会话。
            }
        }
    }

    private void OnSettingsSavedForWindow(AppSettings settings) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _settings = settings;
            ApplyWindowAppearance(settings);
        });

    /// <summary>应用设置 → 外观:窗口透明度、菜单栏显隐、侧边栏位置、界面字体/字号。</summary>
    private void ApplyWindowAppearance(AppSettings settings)
    {
        var a = settings.Appearance;

        Opacity = Math.Clamp(a.WindowOpacityPercent, 50, 100) / 100.0;

        if (this.FindControl<MenuBarView>("MenuBarHost") is { } menuBar)
            menuBar.IsVisible = a.ShowMenuBar;

        ApplySidebarPosition(a.SidebarPosition == "right");

        // 界面字体:shadow 主题的 PulseUiFont 令牌(空或默认 Inter 时还原主题字体)。
        if (Application.Current is { } app)
        {
            var uiFont = a.UiFont?.Trim();
            if (string.IsNullOrEmpty(uiFont) || string.Equals(uiFont, "Inter", StringComparison.OrdinalIgnoreCase))
                app.Resources.Remove("PulseUiFont");
            else
                app.Resources["PulseUiFont"] = new FontFamily($"{uiFont}, Segoe UI, Microsoft YaHei, sans-serif");
        }

        // 界面字号:窗口级默认字号,未显式指定 FontSize 的控件继承。
        if (a.UiFontSize is >= 9 and <= 24)
            FontSize = a.UiFontSize;
    }

    /// <summary>侧边栏位置(设置 → 外观):交换侧边栏与主区所在列,分隔条留在中间。</summary>
    private void ApplySidebarPosition(bool right)
    {
        if (right == _sidebarOnRight)
            return;

        if (this.FindControl<SidebarView>("SidebarHost") is not { } sidebar
            || this.FindControl<Grid>("MainAreaGrid") is not { } main
            || sidebar.Parent is not Grid contentGrid
            || contentGrid.ColumnDefinitions.Count < 3)
        {
            return;
        }

        _sidebarOnRight = right;
        var cols = contentGrid.ColumnDefinitions;
        int sidebarCol = right ? 2 : 0;
        int mainCol = right ? 0 : 2;

        // 保留用户拖出来的侧边栏宽度。
        var sidebarWidth = cols[right ? 0 : 2].Width;
        if (!sidebarWidth.IsAbsolute)
            sidebarWidth = new GridLength(260);

        cols[sidebarCol].Width = sidebarWidth;
        cols[sidebarCol].MinWidth = 180;
        cols[sidebarCol].MaxWidth = 520;
        cols[mainCol].Width = new GridLength(1, GridUnitType.Star);
        cols[mainCol].MinWidth = 400;
        cols[mainCol].MaxWidth = double.PositiveInfinity;

        Grid.SetColumn(sidebar, sidebarCol);
        Grid.SetColumn(main, mainCol);
    }

    /// <summary>托盘“退出”/关闭确认后的真正退出:跳过托盘拦截与确认弹窗。</summary>
    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    /// <summary>关闭链路(设置 → 常规):最小化到托盘 → 关闭前确认 → 记住窗口状态。
    /// 系统关机/应用程序退出(CloseReason ≠ 用户点关闭)不拦截。</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        var settings = _settings;
        bool userInitiated = e.CloseReason == WindowCloseReason.WindowClosing;

        if (!_forceClose && userInitiated && settings is not null)
        {
            if (settings.General.MinimizeToTray
                && App.Current is App app && HasActiveTrayIcon(app))
            {
                e.Cancel = true;
                Hide();
                base.OnClosing(e);
                return;
            }

            if (settings.General.ConfirmBeforeClose && HasConnectedSessions())
            {
                e.Cancel = true;
                _ = ConfirmCloseAsync();
                base.OnClosing(e);
                return;
            }
        }

        PersistWindowBounds(settings);
        base.OnClosing(e);
    }

    private static bool HasActiveTrayIcon(App app) => app.TrayIconActive;

    private bool HasConnectedSessions() =>
        DataContext is MainWindowViewModel vm
        && vm.TabBar.Tabs.OfType<TerminalTabViewModel>().Any(t => t.IsConnected);

    private async Task ConfirmCloseAsync()
    {
        var confirmed = await MessageDialog.ConfirmAsync(this, "关闭 PulseTerm",
            "仍有活动的 SSH 会话,关闭窗口将断开所有连接。确定退出吗?");
        if (confirmed)
            ForceClose();
    }

    /// <summary>退出时的状态记忆:窗口尺寸/最大化(启动时窗口状态 = 记住上次)与
    /// 已连接会话的配置 id(恢复会话)。同步等待,本地写入很快。</summary>
    private void PersistWindowBounds(AppSettings? settings)
    {
        if (_settingsService is null || settings is null)
            return;

        bool rememberWindow = settings.Appearance.StartupWindowState == "remember";
        bool rememberSessions = settings.General.RestoreSessionsOnStartup;
        if (!rememberWindow && !rememberSessions)
            return;

        try
        {
            if (rememberWindow)
            {
                settings.Appearance.LastWindowMaximized = WindowState == WindowState.Maximized;
                if (WindowState == WindowState.Normal)
                {
                    settings.Appearance.LastWindowWidth = Width;
                    settings.Appearance.LastWindowHeight = Height;
                }
            }

            if (rememberSessions && DataContext is MainWindowViewModel vm)
            {
                settings.General.LastOpenProfileIds = vm.TabBar.Tabs
                    .OfType<TerminalTabViewModel>()
                    .Where(t => t.IsConnected && t.Profile is { } p && p.Id != Guid.Empty)
                    .Select(t => t.Profile!.Id)
                    .Distinct()
                    .ToList();
            }

            _settingsService.SaveSettingsAsync(settings).GetAwaiter().GetResult();
        }
        catch
        {
            // 记忆退出状态失败不阻塞退出。
        }
    }

    /// <summary>Menu/palette 终端内查找 → opens the visible terminal view's search bar.</summary>
    private void OnTerminalSearchRequested(object? sender, EventArgs e)
    {
        foreach (var view in this.GetVisualDescendants().OfType<TerminalTabView>())
        {
            if (view.IsEffectivelyVisible)
            {
                view.OpenSearch();
                return;
            }
        }
    }

    // The window uses the native OS title bar per design spec §2 — no custom chrome.

    private void OnSidebarRecentConnectRequested(object? sender, RecentConnectionEntry entry)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        // 连接失败已在标签页内以覆盖层提示(设计 yxjmg),不再弹全局框。
        _ = vm.TryConnectRecentAsync(entry);
    }

    private void OnOpenConnectionProfileRequested(object? sender, EventArgs e)
        => _ = OpenProfileDialogAsync(existing: null);

    /// <summary>打开“新建连接”弹窗;传入 existing 时为编辑既有配置。</summary>
    private async Task OpenProfileDialogAsync(SessionProfile? existing)
    {
        if (DataContext is not MainWindowViewModel mainWindowViewModel)
        {
            return;
        }

        if (App.Current is not App app || app.Services is null)
        {
            return;
        }

        // 新建连接的默认端口与默认密钥(设置 → 常规 / 密钥管理)。
        int defaultPort = _settings?.DefaultPort ?? 22;
        string? defaultKeyPath = null;
        if (_settings?.Keys.DefaultKeyName is { Length: > 0 } keyName)
        {
            var candidate = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", keyName);
            if (System.IO.File.Exists(candidate))
                defaultKeyPath = candidate;
        }

        var connectionProfileViewModel = new ConnectionProfileViewModel(
            existing: existing,
            connectionWorkflowService: app.Services.GetService<IConnectionWorkflowService>(),
            sessionRepository: app.Services.GetService<PulseTerm.Core.Data.ISessionRepository>(),
            defaultPort: defaultPort,
            defaultPrivateKeyPath: defaultKeyPath);

        var dialog = new ConnectionProfileView
        {
            DataContext = connectionProfileViewModel
        };

        var profile = await dialog.ShowDialog<SessionProfile?>(this);
        if (profile is null)
        {
            return;
        }

        // 保存/连接均已持久化配置 —— 资源管理器树同步刷新。
        await mainWindowViewModel.RefreshSessionTreeAsync();

        // 仅“连接”按钮触发实际连接;“保存”只落库。
        if (!connectionProfileViewModel.ConnectAfterClose)
        {
            return;
        }

        // TryConnectProfileAsync never throws — 连接失败已在标签页内以覆盖层提示(设计 yxjmg),
        // 不再弹全局框。
        await mainWindowViewModel.TryConnectProfileAsync(profile);
    }

    /// <summary>打开设置窗口(设计 §14):DI 单例 VM,打开时重新加载持久化设置。</summary>
    private async Task OpenSettingsAsync()
    {
        if (App.Current is not App app
            || app.Services?.GetService<SettingsViewModel>() is not { } settingsViewModel)
        {
            return;
        }

        await settingsViewModel.LoadCommand.Execute();
        var dialog = new SettingsView { DataContext = settingsViewModel };
        await dialog.ShowDialog(this);
    }

    /// <summary>
    /// 登录验证流程(设计:身份验证 第1步/第2步):补全用户名与认证凭据。
    /// 已信任主机显示其指纹;首次连接提示握手时记录(TOFU)。取消返回 null。
    /// </summary>
    private async Task<SessionProfile?> PromptCredentialsAsync(SessionProfile profile)
    {
        string? knownFingerprint = null;
        if (App.Current is App app
            && app.Services?.GetService<PulseTerm.Core.Ssh.IHostKeyService>() is { } hostKeys)
        {
            try
            {
                var hosts = await hostKeys.GetKnownHostsAsync();
                knownFingerprint = hosts
                    .FirstOrDefault(h => h.Host == profile.Host && h.Port == profile.Port)
                    ?.Fingerprint;
            }
            catch
            {
                // 指纹仅用于展示,读取失败不阻塞验证流程。
            }
        }

        var viewModel = new AuthenticationDialogViewModel(
            profile.Host,
            profile.Port,
            profile.Username,
            knownFingerprint,
            profile.AuthMethod);

        var dialog = new AuthenticationDialogView { DataContext = viewModel };
        var result = await dialog.ShowDialog<AuthenticationResult?>(this);
        if (result is null)
        {
            return null;
        }

        profile.Username = result.Username;
        profile.AuthMethod = result.AuthMethod;
        if (result.AuthMethod == PulseTerm.Core.Models.AuthMethod.Password)
        {
            // 交接点:SecureString → 管线所需的明文,随即释放 SecureString。
            using (result.Password)
            {
                profile.Password = PulseTerm.App.Security.SecureStringConvert.ToPlaintext(result.Password);
            }

            profile.RememberPassword = result.RememberPassword;
        }
        else
        {
            profile.PrivateKeyPath = result.PrivateKeyPath;
            profile.PrivateKeyPassphrase = result.PrivateKeyPassphrase;
        }

        return profile;
    }

    /// <summary>导出终端输出(§12.4):有选区导出选区,否则导出整个缓冲区(scrollback+屏幕)。</summary>
    private async Task ExportTerminalBufferAsync(MainWindowViewModel vm)
    {
        var export = vm.GetActiveTerminalExport();
        if (export is null)
            return;
        var (text, suggestedName) = export.Value;

        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "导出终端输出",
            SuggestedFileName = suggestedName,
            DefaultExtension = "txt",
            FileTypeChoices =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("文本文件") { Patterns = ["*.txt"] },
                new Avalonia.Platform.Storage.FilePickerFileType("日志文件") { Patterns = ["*.log"] },
            ],
        });

        var path = file is null ? null : Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(file);
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            await System.IO.File.WriteAllTextAsync(path, text);
            vm.StatusBar.Status = $"终端输出已导出:{path}";
        }
        catch (Exception ex)
        {
            await MessageDialog.ShowMessageAsync(this, "导出失败", ex.Message, MessageDialogKind.Error);
        }
    }

    /// <summary>多行粘贴确认(设置 → 终端 → 粘贴时确认多行内容):预览前几行,防止把
    /// 整段脚本误粘进 shell 直接执行。</summary>
    private Task<bool> ConfirmMultilinePasteAsync(string text)
    {
        var lines = text.Split('\n');
        var previewLines = lines.Take(5).Select(l => l.TrimEnd('\r'));
        var preview = string.Join('\n', previewLines);
        if (lines.Length > 5)
            preview += "\n…";

        return MessageDialog.ConfirmAsync(this, "粘贴多行内容",
            $"剪贴板包含 {lines.Length} 行内容,粘贴后可能被终端立即执行:\n\n{preview}\n\n确定粘贴吗?");
    }
}
