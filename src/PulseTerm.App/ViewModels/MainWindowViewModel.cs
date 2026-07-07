using System;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using Dock.Model.Controls;
using PulseTerm.App.Docking;
using PulseTerm.Core.Data;
using PulseTerm.Core.Models;
using PulseTerm.Core.Resources;
using PulseTerm.Core.Sftp;
using PulseTerm.Core.Ssh;
using PulseTerm.Terminal.Emulation;
using PulseTerm.Presentation.ViewModels;
using PulseTerm.Presentation.Services;
using PulseTerm.Presentation.Commands;
using PulseTerm.Terminal;
using PulseTerm.Terminal.Rendering;
using ReactiveUI;
using System.Reactive.Linq;

namespace PulseTerm.App.ViewModels;

public class MainWindowViewModel : ReactiveObject
{
    private readonly IConnectionWorkflowService? _connectionWorkflowService;
    private readonly ISshConnectionService? _sshConnectionService;
    private readonly ISettingsService? _settingsService;
    private readonly ISessionRepository? _sessionRepository;
    private readonly ISftpService? _sftpService;
    private readonly PulseTerm.Core.Tunnels.ITunnelService? _tunnelService;
    private readonly PulseTerm.Core.Services.ISessionMetricsService? _metricsService;
    private TunnelPanelViewModel? _tunnelPanel;
    private bool _isTunnelPanelOpen;
    private readonly Func<ITerminalEmulator> _terminalEmulatorFactory;
    private readonly TerminalDockFactory _dockFactory;
    private SidebarViewModel _sidebar;
    private TabBarViewModel _tabBar;
    private StatusBarViewModel _statusBar;
    private TerminalTabViewModel? _activeTerminalTab;
    private string? _lastConnectionError;

    // SFTP/File management views derived from design
    private FileBrowserViewModel _fileBrowser;
    private FileTransferViewModel _fileTransfer;

    public MainWindowViewModel(
        IConnectionWorkflowService? connectionWorkflowService = null,
        ISshConnectionService? sshConnectionService = null,
        Func<ITerminalEmulator>? terminalEmulatorFactory = null,
        ISettingsService? settingsService = null,
        ISessionRepository? sessionRepository = null,
        ISftpService? sftpService = null,
        ITransferManager? transferManager = null,
        PulseTerm.Core.Tunnels.ITunnelService? tunnelService = null,
        PulseTerm.Core.Services.ISessionMetricsService? metricsService = null,
        IRecentConnectionService? recentConnectionService = null)
    {
        _connectionWorkflowService = connectionWorkflowService;
        _sshConnectionService = sshConnectionService;
        _settingsService = settingsService;
        _sessionRepository = sessionRepository;
        _sftpService = sftpService;
        _tunnelService = tunnelService;
        _metricsService = metricsService;
        _terminalEmulatorFactory = terminalEmulatorFactory ?? (() => new PulseTerminalControl());

        _dockFactory = new TerminalDockFactory();
        Layout = _dockFactory.CreateLayout();
        _dockFactory.InitLayout(Layout);
        _dockFactory.DocumentClosed += OnDocumentClosed;
        _dockFactory.ActiveDockableChanged += OnActiveDockableChanged;
        _dockFactory.FocusedDockableChanged += OnFocusedDockableChanged;

        _sidebar = new SidebarViewModel(recentConnectionService);
        _tabBar = new TabBarViewModel();
        _statusBar = new StatusBarViewModel();

        _fileBrowser = new FileBrowserViewModel(null, System.Guid.Empty);
        _fileTransfer = new FileTransferViewModel(transferManager);

        _tabBar.WhenAnyValue(tabBar => tabBar.ActiveTab)
            .Subscribe(activeTab =>
            {
                ActiveTerminalTab = activeTab as TerminalTabViewModel;
                RebindFileBrowser();
            });

        // Keep the status bar in sync with the active tab: refresh when the active tab changes,
        // and when that tab's own connection state / latency changes.
        this.WhenAnyValue(x => x.ActiveTerminalTab)
            .Select(tab => tab is null
                ? Observable.Return(Unit.Default)
                : tab.WhenAnyValue(t => t.ConnectionStatus, t => t.Latency).Select(_ => Unit.Default))
            .Switch()
            .Subscribe(_ => UpdateStatusBarForActiveTab());

        // Saved settings re-apply to every open terminal immediately (#3/#15/#21) — scrollback,
        // font, size and encoding change live; TERM stays per-session (negotiated at connect).
        if (_settingsService is not null)
            _settingsService.SettingsSaved += OnSettingsSaved;

        StartStatusMetricsPolling();

        OpenSettingsCommand = ReactiveCommand.Create(() => { });

        CommandPalette = new CommandPaletteViewModel(BuildPaletteItems);
        OpenCommandPaletteCommand = ReactiveCommand.Create(() => CommandPalette.Open());

        RegisterCommands();
        RunCommand = ReactiveCommand.Create<string>(id => Commands.Execute(id));
    }

    /// <summary>
    /// The single command source shared by the menu bar, command palette and shortcuts
    /// (design spec §4A.1) — every entry point shows the same name, hint and behavior.
    /// </summary>
    public ICommandRegistry Commands { get; } = new CommandRegistry();

    /// <summary>Executes a registry command by id (used by menu entries via CommandParameter).</summary>
    public ReactiveCommand<string, Unit> RunCommand { get; private set; } = null!;

    private void RegisterCommands()
    {
        Commands.Register(new CommandDescriptor("session.new", "新建 SSH 连接", "会话",
            () => Sidebar.QuickConnectCommand.Execute().Subscribe(), Shortcut: "Ctrl+N", Icon: "Icon.plus"));
        Commands.Register(new CommandDescriptor("session.close", "关闭当前会话", "会话",
            () => TabBar.CloseActiveTabCommand.Execute().Subscribe(),
            CanExecute: () => TabBar.ActiveTab is not null, Shortcut: "Ctrl+W"));
        Commands.Register(new CommandDescriptor("session.reconnect", "重连", "操作",
            () => { if (ActiveTerminalTab is { } tab) _ = ReconnectTabAsync(tab); },
            CanExecute: () => ActiveTerminalTab?.ConnectionStatus == SessionStatus.Disconnected,
            Shortcut: "Ctrl+R"));
        Commands.Register(new CommandDescriptor("edit.copy", "复制", "编辑",
            () => { if (ActiveTerminalControl is { } c) _ = c.CopyAsync(); },
            CanExecute: () => ActiveTerminalControl is not null, Shortcut: "Ctrl+Shift+C", Icon: "Icon.copy"));
        Commands.Register(new CommandDescriptor("edit.paste", "粘贴", "编辑",
            () => { if (ActiveTerminalControl is { } c) _ = c.PasteAsync(); },
            CanExecute: () => ActiveTerminalControl is not null, Shortcut: "Ctrl+Shift+V"));
        Commands.Register(new CommandDescriptor("search.terminal", "终端内查找", "搜索",
            () => TerminalSearchRequested?.Invoke(this, EventArgs.Empty),
            CanExecute: () => ActiveTerminalTab is not null, Shortcut: "Ctrl+F", Icon: "Icon.search"));
        Commands.Register(new CommandDescriptor("tools.tunnel", "隧道管理", "工具",
            ToggleTunnelPanel,
            CanExecute: () => ActiveTerminalTab is not null, Shortcut: "Ctrl+Shift+T", Icon: "Icon.route"));
        Commands.Register(new CommandDescriptor("tools.files", "SFTP 文件管理器", "工具",
            ToggleFileBrowser,
            CanExecute: () => ActiveTerminalTab is not null, Shortcut: "Ctrl+Shift+F", Icon: "Icon.folder"));
        Commands.Register(new CommandDescriptor("edit.clear", "清屏", "编辑",
            () => ActiveTerminalTab?.TerminalEmulator.WriteInput(new byte[] { 0x0C }),
            CanExecute: () => ActiveTerminalTab?.ConnectionStatus == SessionStatus.Connected));
        Commands.Register(new CommandDescriptor("app.settings", "打开设置", "编辑",
            () => OpenSettingsCommand.Execute().Subscribe(), Shortcut: "Ctrl+,", Icon: "Icon.settings"));
        Commands.Register(new CommandDescriptor("app.palette", "命令面板", "搜索",
            () => CommandPalette.Open(), Shortcut: "Ctrl+P", Icon: "Icon.zap"));
    }

    /// <summary>
    /// Points the SFTP file browser at the active tab's session (#22). Each connected tab gets
    /// a browser rooted at its own session; without a connected session the panel shows empty.
    /// </summary>
    private void RebindFileBrowser()
    {
        if (_sftpService is null)
            return;

        var tab = ActiveTerminalTab;
        if (tab is null || tab.SessionId == Guid.Empty)
            return;

        if (FileBrowser.SessionId == tab.SessionId)
            return;

        // Carry the open/closed state across the rebind so switching to (or connecting) a tab
        // never silently hides a panel the user had opened.
        var wasVisible = FileBrowser.IsVisible;
        FileBrowser = new FileBrowserViewModel(_sftpService, tab.SessionId)
        {
            TransferSink = FileTransfer,
            IsVisible = wasVisible,
        };
        if (wasVisible)
            FileBrowser.RefreshCommand.Execute().Subscribe(_ => { }, _ => { });
    }

    /// <summary>Toggles the SFTP panel for the active session (#22, spec §9). Opening it binds the
    /// browser to the current session (if not already) and loads the initial listing.</summary>
    public void ToggleFileBrowser()
    {
        // Ensure the browser points at the active tab's (now-connected) session before showing it.
        // The active-tab subscription can't do this on its own because the session id is assigned
        // after the tab is activated, so we rebind on demand here as well.
        RebindFileBrowser();

        FileBrowser.IsVisible = !FileBrowser.IsVisible;
        if (FileBrowser.IsVisible && FileBrowser.SessionId != Guid.Empty)
            FileBrowser.RefreshCommand.Execute().Subscribe(_ => { }, _ => { });
    }

    /// <summary>Called once a session finishes connecting: binds the file browser to it and shows
    /// the listing. Per spec §9 the file area is part of the session view (visible by default,
    /// collapsible), so a fresh connection surfaces its files without the user hunting for a toggle.</summary>
    private void ShowFileBrowserForActiveSession()
    {
        RebindFileBrowser();

        if (_sftpService is null || FileBrowser.SessionId == Guid.Empty)
            return;

        FileBrowser.IsVisible = true;
        FileBrowser.LoadInitialCommand.Execute().Subscribe(_ => { }, _ => { });
    }
    /// <summary>Raised when the user asks for in-terminal search via menu/palette; the window
    /// forwards it to the active terminal view's search bar (§5.3).</summary>
    public event EventHandler? TerminalSearchRequested;
    /// <summary>Tunnel manager panel for the active session (design fuXS7, spec §10).</summary>
    public TunnelPanelViewModel? TunnelPanel
    {
        get => _tunnelPanel;
        private set => this.RaiseAndSetIfChanged(ref _tunnelPanel, value);
    }

    public bool IsTunnelPanelOpen
    {
        get => _isTunnelPanelOpen;
        set => this.RaiseAndSetIfChanged(ref _isTunnelPanelOpen, value);
    }

    /// <summary>Singleton toggle (spec §17.2): reopening focuses the existing panel.</summary>
    public void ToggleTunnelPanel()
    {
        if (IsTunnelPanelOpen)
        {
            IsTunnelPanelOpen = false;
            return;
        }

        if (_tunnelService is null || ActiveTerminalTab is not { } tab || tab.SessionId == Guid.Empty)
            return;

        if (TunnelPanel is null || TunnelPanel.SessionId != tab.SessionId)
        {
            // Saved sessions feed the remote-host picker (用户反馈 #4).
            Func<Task<IReadOnlyList<SessionProfile>>>? targets = _sessionRepository is null
                ? null
                : async () => await _sessionRepository.GetAllSessionsAsync();

            TunnelPanel = new TunnelPanelViewModel(_tunnelService, tab.SessionId, targets)
            {
                NewLocalHost = "127.0.0.1",
            };
        }

        _ = TunnelPanel.LoadSavedTargetsAsync();
        IsTunnelPanelOpen = true;
    }
    /// <summary>The self-drawn terminal control of the active tab, when it is one.</summary>
    private PulseTerminalControl? ActiveTerminalControl =>
        ActiveTerminalTab?.TerminalEmulator.Control as PulseTerminalControl;

    // ---- Status-bar live metrics (spec §7: cpu / memory / net for the active session) ----

    private Avalonia.Threading.DispatcherTimer? _statusMetricsTimer;
    private bool _statusMetricsPolling;

    /// <summary>Polls the active session's metrics once a second into the status bar. The
    /// probe runs on a dedicated SSH exec channel, so it never touches the terminal stream;
    /// consecutive samples give the collector real instantaneous CPU% and network rates.</summary>
    private void StartStatusMetricsPolling()
    {
        // Headless unit tests construct this VM without an Avalonia application; skip there.
        if (_metricsService is null || Avalonia.Application.Current is null)
            return;

        _statusMetricsTimer = new Avalonia.Threading.DispatcherTimer(
            TimeSpan.FromSeconds(1),
            Avalonia.Threading.DispatcherPriority.Background,
            (_, _) => _ = PollStatusMetricsAsync());
        _statusMetricsTimer.Start();
    }

    private async Task PollStatusMetricsAsync()
    {
        if (_statusMetricsPolling || _metricsService is null)
            return;

        var tab = ActiveTerminalTab;
        if (tab is null || tab.SessionId == Guid.Empty || tab.ConnectionStatus != SessionStatus.Connected)
        {
            StatusBar.ClearSessionMetrics();
            return;
        }

        _statusMetricsPolling = true;
        try
        {
            var metrics = await _metricsService.GetMetricsAsync(tab.SessionId);

            // The user may have switched tabs while the probe ran; don't show stale data.
            if (!ReferenceEquals(ActiveTerminalTab, tab))
                return;

            if (metrics is null)
            {
                StatusBar.ClearSessionMetrics();
                return;
            }

            StatusBar.CpuUsage = $"{metrics.CpuPercent:F2}%";
            StatusBar.MemUsage = $"{metrics.MemPercent:F1}%";
            StatusBar.SwapUsage = metrics.SwapTotalBytes > 0 ? $"{metrics.SwapPercent:F1}%" : "--";
            StatusBar.DiskUsage = metrics.DiskTotalBytes > 0 ? $"{metrics.DiskPercent:F1}%" : "--";
            StatusBar.UpdateNetwork(metrics.NetRxBytesPerSec, metrics.NetTxBytesPerSec, metrics.HasNetRates);
        }
        catch
        {
            // Never let a failed probe surface in the UI loop; the next tick retries.
        }
        finally
        {
            _statusMetricsPolling = false;
        }
    }

    /// <summary>The Ctrl+P / Ctrl+K command palette overlay.</summary>
    public CommandPaletteViewModel CommandPalette { get; }

    public ReactiveCommand<Unit, Unit> OpenCommandPaletteCommand { get; }

    /// <summary>
    /// Loads the persisted recent-connection history (SonnetDB) into the sidebar so it
    /// survives restarts.
    /// </summary>
    public async Task InitializeAsync()
    {
        await Sidebar.RecentConnections.RefreshAsync();
    }

    private IReadOnlyList<CommandPaletteItem> BuildPaletteItems()
    {
        var items = new List<CommandPaletteItem>();

        // Sessions from recent connections — Enter connects.
        foreach (var item in Sidebar.RecentConnections.Connections)
        {
            var captured = item.Entry;
            var title = string.IsNullOrWhiteSpace(item.DisplayName) ? captured.Host : item.DisplayName;
            items.Add(new CommandPaletteItem(
                category: "会话",
                title: title,
                invoke: () => _ = TryConnectRecentAsync(captured),
                hint: "Enter 连接",
                isSession: true));
        }

        // Global actions come from the shared command registry (menu/palette/shortcut parity).
        foreach (var command in Commands.All)
        {
            var captured = command;
            items.Add(new CommandPaletteItem("命令", captured.Title,
                () => Commands.Execute(captured.Id), hint: captured.Shortcut));
        }

        return items;
    }

    public async Task<TerminalTabViewModel> ConnectProfileAsync(SessionProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (_connectionWorkflowService is null || _sshConnectionService is null)
        {
            throw new InvalidOperationException("SSH connection services are not configured.");
        }

        var settings = _settingsService is not null
            ? await _settingsService.GetSettingsAsync()
            : new AppSettings();
        var terminalType = TerminalTypeExtensions.FromTermName(settings.TerminalType);

        // Show the tab immediately in a connecting state, then perform the (already async, non-UI-
        // blocking) handshake. A slow or timing-out connection no longer looks like a frozen app,
        // and the user gets a visible, closable tab right away (#17).
        var terminalEmulator = _terminalEmulatorFactory();
        ConfigureTerminal(terminalEmulator, settings, terminalType);
        var terminalTab = new TerminalTabViewModel(terminalEmulator)
        {
            Title = string.IsNullOrWhiteSpace(profile.Name) ? profile.Host : profile.Name,
            ConnectionStatus = SessionStatus.Connecting,
            ConnectionSummary = $"SSH • {profile.Username}@{profile.Host}:{profile.Port}",
            TerminalTypeName = terminalType.ToTermName(),
            EncodingName = string.IsNullOrWhiteSpace(settings.TerminalEncoding) ? "UTF-8" : settings.TerminalEncoding,
            Profile = profile,
        };
        terminalTab.ReconnectRequested += (_, _) => _ = ReconnectTabAsync(terminalTab);
        var document = new TerminalDocument(terminalTab);
        TabBar.AddTab(terminalTab);
        ActiveTerminalTab = terminalTab;
        _dockFactory.AddTerminal(document);
        UpdateStatusBarForActiveTab();

        try
        {
            var session = await _connectionWorkflowService.ConnectProfileAsync(profile, cancellationToken);
            var client = _sshConnectionService.GetClient(session.SessionId)
                ?? throw new InvalidOperationException("SSH client was not created for the session.");

            var shellStream = client.CreateShellStream(
                terminalName: terminalType.ToTermName(),
                columns: 120,
                rows: 32,
                width: 0,
                height: 0,
                bufferSize: 4096);

            terminalTab.SessionId = session.SessionId;
            terminalTab.AttachTransport(shellStream);
            terminalTab.Start();
            terminalTab.ConnectionStatus = SessionStatus.Connected;

            // The session id only exists now, after the handshake — the active-tab subscription
            // fired before it was assigned, so bind the SFTP browser here (and show + load it) or
            // it stays bound to the empty placeholder and never loads a listing (#22).
            ShowFileBrowserForActiveSession();

            if (_metricsService is not null)
                terminalTab.ResourceMonitor = new ResourceMonitorViewModel(_metricsService, session.SessionId, terminalTab.Title);
        }
        catch
        {
            // The handshake failed (auth/network/timeout): retract the connecting tab so the
            // caller sees a clean failure instead of a dead tab.
            RemoveTerminalTab(terminalTab, document);
            throw;
        }

        // 连接历史已由工作流写入 SonnetDB,这里刷新侧边栏“最近连接”。
        await Sidebar.RecentConnections.RefreshAsync();
        StatusBar.ResetUptime();
        UpdateStatusBarForActiveTab();
        LastConnectionError = null;

        return terminalTab;
    }

    /// <summary>
    /// Reconnects a dropped session in place: it reuses the same tab, emulator and scrollback
    /// buffer, only rebuilding the transport. Triggered by Enter / Ctrl+R on a disconnected tab
    /// (or after exit/reboot) instead of forcing the user to open a fresh tab (#19).
    /// </summary>
    public async Task ReconnectTabAsync(TerminalTabViewModel tab, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);

        if (tab.Profile is null || _connectionWorkflowService is null || _sshConnectionService is null)
            return;

        // Ignore reconnect requests while already connecting or connected.
        if (tab.ConnectionStatus is SessionStatus.Connecting or SessionStatus.Connected)
            return;

        tab.ConnectionStatus = SessionStatus.Connecting;
        tab.DetachTransport();
        UpdateStatusBarForActiveTab();

        try
        {
            var settings = _settingsService is not null
                ? await _settingsService.GetSettingsAsync()
                : new AppSettings();
            var terminalType = TerminalTypeExtensions.FromTermName(settings.TerminalType);

            var session = await _connectionWorkflowService.ConnectProfileAsync(tab.Profile, cancellationToken);
            var client = _sshConnectionService.GetClient(session.SessionId)
                ?? throw new InvalidOperationException("SSH client was not created for the session.");

            var shellStream = client.CreateShellStream(
                terminalName: terminalType.ToTermName(),
                columns: 120,
                rows: 32,
                width: 0,
                height: 0,
                bufferSize: 4096);

            // Full-reset (RIS) the emulator before the new session's output arrives, so the
            // fresh MOTD doesn't append after the old buffer's content (用户反馈 #1).
            tab.TerminalEmulator.Feed("\u001bc"u8.ToArray());

            tab.SessionId = session.SessionId;
            tab.AttachTransport(shellStream);
            tab.Start();
            tab.ConnectionStatus = SessionStatus.Connected;
            if (_metricsService is not null)
                tab.ResourceMonitor = new ResourceMonitorViewModel(_metricsService, session.SessionId, tab.Title);

            // Reconnect mints a fresh session id; rebind the SFTP browser to it and reload (#22).
            ShowFileBrowserForActiveSession();

            StatusBar.ResetUptime();
            UpdateStatusBarForActiveTab();
            LastConnectionError = null;
        }
        catch (OperationCanceledException)
        {
            tab.MarkDisconnected();
        }
        catch (Exception ex)
        {
            tab.MarkDisconnected();
            LastConnectionError = DescribeConnectionError(ex, tab.Profile);
            StatusBar.Status = LastConnectionError;
        }
    }

    private void RemoveTerminalTab(TerminalTabViewModel tab, TerminalDocument document)
    {
        if (TabBar.Tabs.Contains(tab))
            TabBar.CloseTabCommand.Execute(tab).Subscribe();

        _dockFactory.RemoveTerminal(document);

        if (ReferenceEquals(ActiveTerminalTab, tab))
            ActiveTerminalTab = TabBar.ActiveTab as TerminalTabViewModel;

        tab.Dispose();
    }

    /// <summary>
    /// Connects without ever letting a failure escape to the caller. Authentication failures,
    /// unreachable hosts and the like are captured in <see cref="LastConnectionError"/> and
    /// reflected in the status bar instead of crashing the app.
    /// </summary>
    public async Task<TerminalTabViewModel?> TryConnectProfileAsync(SessionProfile profile, CancellationToken cancellationToken = default)
    {
        try
        {
            return await ConnectProfileAsync(profile, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            LastConnectionError = DescribeConnectionError(ex, profile);
            StatusBar.Status = LastConnectionError;
            return null;
        }
    }

    /// <summary>
    /// Connects a sidebar "最近连接" entry: resolves the saved profile by id when available,
    /// otherwise reconstructs an ad-hoc profile from the recorded host/port/username.
    /// </summary>
    public async Task<TerminalTabViewModel?> TryConnectRecentAsync(RecentConnectionEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        SessionProfile? profile = null;
        if (entry.ProfileId is { } profileId && _sessionRepository is not null)
        {
            try
            {
                profile = await _sessionRepository.GetSessionAsync(profileId);
            }
            catch
            {
                // 配置读取失败时退回到临时档案。
            }
        }

        profile ??= new SessionProfile
        {
            Name = entry.Name,
            Host = entry.Host,
            Port = entry.Port,
            Username = entry.Username,
            AuthMethod = AuthMethod.Password,
        };

        return await TryConnectProfileAsync(profile, cancellationToken);
    }

    /// <summary>The most recent connection error message, or null if the last attempt succeeded.</summary>
    public string? LastConnectionError
    {
        get => _lastConnectionError;
        private set => this.RaiseAndSetIfChanged(ref _lastConnectionError, value);
    }

    private static string DescribeConnectionError(Exception ex, SessionProfile profile)
    {
        var target = string.IsNullOrWhiteSpace(profile.Host) ? profile.Name : $"{profile.Username}@{profile.Host}:{profile.Port}";
        // Match by type name so PulseTerm.App need not reference SSH.NET directly.
        return ex.GetType().Name switch
        {
            "SshAuthenticationException" => $"认证失败：{target} 的用户名、密码或密钥不正确。",
            "SshConnectionException" => $"连接失败：无法与 {target} 建立 SSH 会话。",
            "SocketException" => $"网络错误：无法连接到 {target}，请检查主机与端口。",
            "SshOperationTimeoutException" => $"连接超时：{target} 未响应。",
            "ProxyException" => $"代理错误：无法通过代理连接到 {target}。",
            _ => $"连接 {target} 失败：{ex.Message}",
        };
    }

    private static void ConfigureTerminal(ITerminalEmulator emulator, AppSettings settings, TerminalType terminalType)
    {
        if (emulator is PulseTerminalControl control)
            control.TerminalType = terminalType;

        ApplyLiveTerminalSettings(emulator, settings);
    }

    /// <summary>The settings that are safe to change on a live session: scrollback depth, font,
    /// font size and host-output encoding. Applied at tab creation and re-applied to every open
    /// tab whenever settings are saved (#3/#15/#21).</summary>
    private static void ApplyLiveTerminalSettings(ITerminalEmulator emulator, AppSettings settings)
    {
        emulator.ScrollbackLines = settings.ScrollbackLines;

        if (emulator is PulseTerminalControl control)
        {
            control.SetEncoding(ResolveEncoding(settings.TerminalEncoding));
            if (!string.IsNullOrWhiteSpace(settings.TerminalFont))
                control.FontFamily = new Avalonia.Media.FontFamily(
                    $"{settings.TerminalFont}, JetBrains Mono, Cascadia Mono, Consolas, Microsoft YaHei, monospace");
            if (settings.TerminalFontSize > 0)
                control.FontSize = settings.TerminalFontSize;
        }
    }

    private void OnSettingsSaved(AppSettings settings)
    {
        // SaveSettingsAsync may complete on a thread-pool continuation; font/size touch layout,
        // so marshal onto the UI thread (the main scheduler is the Avalonia dispatcher).
        RxSchedulers.MainThreadScheduler.Schedule(Unit.Default, (_, _) =>
        {
            foreach (var tab in TabBar.Tabs.OfType<TerminalTabViewModel>())
                ApplyLiveTerminalSettings(tab.TerminalEmulator, settings);
            return System.Reactive.Disposables.Disposable.Empty;
        });
    }

    private static Encoding ResolveEncoding(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Encoding.UTF8;
        try
        {
            return Encoding.GetEncoding(name);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    /// <summary>
    /// Projects the currently active terminal tab's connection details onto the status bar so
    /// the bottom-left indicator always reflects the tab the user is looking at.
    /// </summary>
    private void UpdateStatusBarForActiveTab()
    {
        var tab = ActiveTerminalTab;
        if (tab is null)
        {
            StatusBar.Status = Strings.Ready;
            StatusBar.StatusText = Strings.Ready;
            StatusBar.ConnectionInfo = string.Empty;
            StatusBar.Latency = string.Empty;
            StatusBar.WindowSize = string.Empty;
            return;
        }

        bool connected = tab.ConnectionStatus == SessionStatus.Connected;
        StatusBar.Status = connected ? Strings.Connected : Strings.Disconnected;
        StatusBar.StatusText = StatusBar.Status;
        StatusBar.ConnectionInfo = tab.ConnectionSummary;
        StatusBar.TerminalType = tab.TerminalTypeName;
        StatusBar.Encoding = tab.EncodingName;
        StatusBar.WindowSize = $"{tab.TerminalEmulator.Columns}×{tab.TerminalEmulator.Rows}";
        StatusBar.Latency = tab.Latency is { } latency ? $"{(int)latency.TotalMilliseconds} ms" : string.Empty;
    }

    public SidebarViewModel Sidebar
    {
        get => _sidebar;
        set => this.RaiseAndSetIfChanged(ref _sidebar, value);
    }

    public TabBarViewModel TabBar
    {
        get => _tabBar;
        set => this.RaiseAndSetIfChanged(ref _tabBar, value);
    }

    public StatusBarViewModel StatusBar
    {
        get => _statusBar;
        set => this.RaiseAndSetIfChanged(ref _statusBar, value);
    }

    public TerminalTabViewModel? ActiveTerminalTab
    {
        get => _activeTerminalTab;
        private set => this.RaiseAndSetIfChanged(ref _activeTerminalTab, value);
    }

    public bool HasActiveTerminalTab => ActiveTerminalTab is not null;

    /// <summary>The Dock.Avalonia layout hosting terminal documents (draggable, floatable, splittable).</summary>
    public IRootDock Layout { get; }

    private void OnActiveDockableChanged(object? sender, Dock.Model.Core.Events.ActiveDockableChangedEventArgs e)
        => SetActiveFromDockable(e.Dockable);

    private void OnFocusedDockableChanged(object? sender, Dock.Model.Core.Events.FocusedDockableChangedEventArgs e)
        => SetActiveFromDockable(e.Dockable);

    private void SetActiveFromDockable(Dock.Model.Core.IDockable? dockable)
    {
        if (dockable is TerminalDocument document && TabBar.Tabs.Contains(document.Terminal))
        {
            ActiveTerminalTab = document.Terminal;
            if (!ReferenceEquals(TabBar.ActiveTab, document.Terminal))
                TabBar.ActiveTab = document.Terminal;
        }
    }

    private void OnDocumentClosed(TerminalDocument document)
    {
        var tab = document.Terminal;
        if (TabBar.Tabs.Contains(tab))
        {
            TabBar.CloseTabCommand.Execute(tab).Subscribe();
        }
        CloseSftpForTab(tab);
        tab.Dispose();
    }

    /// <summary>Tears down the SFTP channel bound to a closing tab's session and, if the browser is
    /// still showing that session, unbinds and hides it — closing the SSH tab must not leave a live,
    /// operable SFTP panel behind (#22).</summary>
    private void CloseSftpForTab(TerminalTabViewModel tab)
    {
        if (_sftpService is null || tab.SessionId == Guid.Empty)
            return;

        var closedSessionId = tab.SessionId;
        _ = _sftpService.CloseSessionAsync(closedSessionId);

        // The active-tab change from closing may have already rebound the browser to another
        // session; only reset when it still points at the one we just closed.
        if (FileBrowser.SessionId == closedSessionId)
            FileBrowser = new FileBrowserViewModel(_sftpService, Guid.Empty);
    }

    public FileBrowserViewModel FileBrowser
    {
        get => _fileBrowser;
        set => this.RaiseAndSetIfChanged(ref _fileBrowser, value);
    }

    public FileTransferViewModel FileTransfer
    {
        get => _fileTransfer;
        set => this.RaiseAndSetIfChanged(ref _fileTransfer, value);
    }

    public ReactiveCommand<Unit, Unit> OpenSettingsCommand { get; }
}
