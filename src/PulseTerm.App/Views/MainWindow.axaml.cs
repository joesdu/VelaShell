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

            // 资源管理器树:右键连接/双击连接 + 右键编辑。
            if (vm.Sidebar.SessionTree is { } tree)
            {
                tree.ConnectRequested += profile => Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
                {
                    var tab = await vm.TryConnectProfileAsync(profile);
                    if (tab is null && vm.LastConnectionError is { Length: > 0 } error)
                        await ShowConnectionErrorAsync(error);
                });
                tree.EditRequested += profile => Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => _ = OpenProfileDialogAsync(profile));

                // 打开 SFTP:先连接(已连接则新开标签),随后展开文件浏览面板。
                tree.OpenSftpRequested += profile => Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
                {
                    var tab = await vm.TryConnectProfileAsync(profile);
                    if (tab is null)
                    {
                        if (vm.LastConnectionError is { Length: > 0 } error)
                            await ShowConnectionErrorAsync(error);
                        return;
                    }

                    if (!vm.FileBrowser.IsVisible)
                        vm.ToggleFileBrowser();
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

    private async void OnSidebarRecentConnectRequested(object? sender, RecentConnectionEntry entry)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var tab = await vm.TryConnectRecentAsync(entry);
        if (tab is null && vm.LastConnectionError is { Length: > 0 } error)
            await ShowConnectionErrorAsync(error);
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

        var connectionProfileViewModel = new ConnectionProfileViewModel(
            existing: existing,
            connectionWorkflowService: app.Services.GetService<IConnectionWorkflowService>(),
            sessionRepository: app.Services.GetService<PulseTerm.Core.Data.ISessionRepository>());

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

        // TryConnectProfileAsync never throws — a failed auth/connection is reported, not crashed.
        var tab = await mainWindowViewModel.TryConnectProfileAsync(profile);
        if (tab is null && mainWindowViewModel.LastConnectionError is { Length: > 0 } error)
        {
            await ShowConnectionErrorAsync(error);
        }
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
            profile.Password = result.Password;
            profile.RememberPassword = result.RememberPassword;
        }
        else
        {
            profile.PrivateKeyPath = result.PrivateKeyPath;
            profile.PrivateKeyPassphrase = result.PrivateKeyPassphrase;
        }

        return profile;
    }

    private Task ShowConnectionErrorAsync(string message) =>
        MessageDialog.ShowMessageAsync(this, "连接失败", message, MessageDialogKind.Error);
}
