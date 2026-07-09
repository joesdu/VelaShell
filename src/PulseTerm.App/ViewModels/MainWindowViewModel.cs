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
    private AppSettings? _latestSettings;

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
        IRecentConnectionService? recentConnectionService = null,
        ISecurityAlertService? securityAlertService = null)
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
        if (sessionRepository is not null)
            _sidebar.SessionTree = new SessionTreeViewModel(sessionRepository);
        _tabBar = new TabBarViewModel();
        _statusBar = new StatusBarViewModel();

        _fileBrowser = new FileBrowserViewModel(null, System.Guid.Empty);
        _fileTransfer = new FileTransferViewModel(transferManager);

        _tabBar.WhenAnyValue(tabBar => tabBar.ActiveTab)
            .Subscribe(activeTab =>
            {
                ActiveTerminalTab = activeTab as TerminalTabViewModel;
                if (activeTab is not null)
                    activeTab.HasBellAlert = false; // 切换到该标签即清除 Bell 提醒
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

        // 安全告警(设置 → 安全审计 → 告警通道):应用内 → 状态栏;系统 → 提示音。
        if (securityAlertService is not null)
        {
            securityAlertService.Alerted += notice =>
                RxSchedulers.MainThreadScheduler.Schedule(Unit.Default, (_, _) =>
                {
                    if (notice.InApp)
                        StatusBar.Status = notice.Message;
                    if (notice.System)
                        Services.SystemSound.Alert();
                    return System.Reactive.Disposables.Disposable.Empty;
                });
        }

        StartStatusMetricsPolling();

        OpenSettingsCommand = ReactiveCommand.Create(() => SettingsRequested?.Invoke(this, EventArgs.Empty));

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
            () => NewConnectionRequested?.Invoke(this, EventArgs.Empty), Shortcut: "Ctrl+N", Icon: "Icon.plus"));
        Commands.Register(new CommandDescriptor("session.close", "关闭当前会话", "会话",
            () => TabBar.CloseActiveTabCommand.Execute().Subscribe(),
            CanExecute: () => TabBar.ActiveTab is not null, Shortcut: "Ctrl+W"));
        Commands.Register(new CommandDescriptor("session.reconnect", "重连", "操作",
            () => { if (ActiveTerminalTab is { } tab) _ = ReconnectTabAsync(tab); },
            CanExecute: () => ActiveTerminalTab?.ConnectionStatus == SessionStatus.Disconnected,
            Shortcut: "Ctrl+R"));
        Commands.Register(new CommandDescriptor("session.clone", "克隆会话", "会话",
            () => { if (ActiveTerminalTab?.Profile is { } profile) _ = TryConnectProfileAsync(profile); },
            CanExecute: () => ActiveTerminalTab?.Profile is not null,
            Shortcut: "Ctrl+Shift+N", Icon: "Icon.copy"));
        Commands.Register(new CommandDescriptor("edit.copy", "复制", "编辑",
            () => { if (ActiveTerminalControl is { } c) _ = c.CopyAsync(); },
            CanExecute: () => ActiveTerminalControl is not null, Shortcut: "Ctrl+Shift+C", Icon: "Icon.copy"));
        Commands.Register(new CommandDescriptor("edit.paste", "粘贴", "编辑",
            () => { if (ActiveTerminalControl is { } c) _ = c.PasteAsync(); },
            CanExecute: () => ActiveTerminalControl is not null, Shortcut: "Ctrl+Shift+V"));
        Commands.Register(new CommandDescriptor("terminal.export", "导出终端输出到文件", "会话",
            () => ExportBufferRequested?.Invoke(this, EventArgs.Empty),
            CanExecute: () => ActiveTerminalControl is not null, Icon: "Icon.save"));
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

        // 本地终端(§12 P1-1):按本机安装情况动态注册 PowerShell/CMD/WSL/Git Bash 入口。
        foreach (var shell in Services.LocalShellCatalog.DetectShells())
        {
            var captured = shell;
            Commands.Register(new CommandDescriptor($"local.{captured.Id}",
                $"打开本地终端:{captured.Name}", "会话",
                () => _ = OpenLocalTerminalAsync(captured), Icon: "Icon.terminal"));
        }
    }

    /// <summary>打开一个本地终端标签:走与 SSH 相同的 桥 → VT 引擎 → 自绘控件 管线,
    /// 传输层换成 ConPTY(输出恒为 UTF-8,不套用设置里的远端编码)。</summary>
    public async Task OpenLocalTerminalAsync(Services.LocalShellInfo shell)
    {
        var settings = _settingsService is not null
            ? await _settingsService.GetSettingsAsync()
            : new AppSettings();
        _latestSettings = settings;

        var terminalEmulator = _terminalEmulatorFactory();
        ConfigureTerminal(terminalEmulator, settings, TerminalType.XtermusColor256, forceUtf8: true);

        var terminalTab = new TerminalTabViewModel(terminalEmulator)
        {
            Title = shell.Name,
            ConnectionStatus = SessionStatus.Connecting,
            ConnectionSummary = $"本地 • {shell.Name}",
            TerminalTypeName = TerminalType.XtermusColor256.ToTermName(),
            EncodingName = "UTF-8",
            LocalShell = shell,
        };
        terminalTab.ReconnectRequested += (_, _) => _ = ReconnectTabAsync(terminalTab);
        terminalTab.Disconnected += (_, _) => OnTabDisconnected(terminalTab);
        if (terminalEmulator is PulseTerminalControl bellSource)
        {
            bellSource.BellRang += () =>
            {
                if (_latestSettings?.TerminalBehavior.TabFlashAlert != false
                    && !ReferenceEquals(ActiveTerminalTab, terminalTab))
                {
                    terminalTab.HasBellAlert = true;
                }
            };
        }

        var document = new TerminalDocument(terminalTab);
        TabBar.AddTab(terminalTab);
        ActiveTerminalTab = terminalTab;
        _dockFactory.AddTerminal(document);
        UpdateStatusBarForActiveTab();

        try
        {
            AttachLocalShell(terminalTab, shell, settings);
        }
        catch (Exception ex)
        {
            RemoveTerminalTab(terminalTab, document);
            LastConnectionError = $"启动 {shell.Name} 失败:{ex.Message}";
            StatusBar.Status = LastConnectionError;
        }
    }

    /// <summary>RIS(ESC c)完全重置序列:重开会话前清掉旧进程的残留缓冲。</summary>
    private static readonly byte[] RisResetSequence = [0x1B, (byte)'c']; // ESC c

    /// <summary>重开本地终端标签:RIS 清屏后重新拉起 shell(与 SSH 重连同语义)。</summary>
    private void ReopenLocalShell(TerminalTabViewModel tab, Services.LocalShellInfo shell)
    {
        tab.ConnectionStatus = SessionStatus.Connecting;
        tab.DetachTransport();
        try
        {
            tab.TerminalEmulator.Feed(RisResetSequence);
            AttachLocalShell(tab, shell, _latestSettings ?? new AppSettings());
            LastConnectionError = null;
        }
        catch (Exception ex)
        {
            tab.MarkDisconnected();
            LastConnectionError = $"重开 {shell.Name} 失败:{ex.Message}";
            StatusBar.Status = LastConnectionError;
        }
    }

    /// <summary>拉起本地 shell 进程并挂上标签(打开与重开共用)。</summary>
    private void AttachLocalShell(TerminalTabViewModel tab, Services.LocalShellInfo shell, AppSettings settings)
    {
        var stream = PulseTerm.Infrastructure.Pty.ConPtyShellStream.Start(
            shell.CommandLine,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            tab.TerminalEmulator.Columns,
            tab.TerminalEmulator.Rows);

        tab.AttachTransport(stream);
        tab.Start();
        tab.ConnectionStatus = SessionStatus.Connected;
        tab.ResetReconnectAttempts();
        StartSessionLogging(tab, settings);
        UpdateStatusBarForActiveTab();
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
            GetDefaultEditorPath = QueryDefaultEditorPathAsync,
            TransferOptions = _latestSettings?.Transfer ?? new TransferOptions(),
            ShowHiddenFiles = _latestSettings?.Transfer.ShowHiddenFiles ?? false,
        };
        if (wasVisible)
            FileBrowser.RefreshCommand.Execute().Subscribe(_ => { }, _ => { });
    }

    /// <summary>SFTP「使用默认编辑器打开」读取的编辑器命令(设置 → 文件传输 → 默认编辑器)。</summary>
    private async Task<string?> QueryDefaultEditorPathAsync()
    {
        if (_settingsService is null)
            return null;

        var settings = await _settingsService.GetSettingsAsync();
        return settings.Transfer.DefaultEditorPath;
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

    /// <summary>导出终端输出(命令面板“导出终端输出到文件”)—— 窗口弹保存对话框并落盘。</summary>
    public event EventHandler? ExportBufferRequested;

    /// <summary>取当前标签的导出内容:有选区导出选区,否则导出整个缓冲区;附建议文件名。
    /// 无活动终端时返回 null。</summary>
    public (string Text, string SuggestedFileName)? GetActiveTerminalExport()
    {
        if (ActiveTerminalControl is not { } control || ActiveTerminalTab is not { } tab)
            return null;

        var selection = control.GetSelectedText();
        var text = string.IsNullOrEmpty(selection) ? control.GetBufferText() : selection;

        var safeTitle = string.Concat(tab.Title.Select(c =>
            char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
        if (safeTitle.Length > 40)
            safeTitle = safeTitle[..40];
        return (text, $"{safeTitle}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
    }

    /// <summary>Ctrl+N / 菜单 / 命令面板“新建 SSH 连接” —— 由窗口打开新建连接弹窗。</summary>
    public event EventHandler? NewConnectionRequested;

    /// <summary>Ctrl+, / 菜单 / 侧边栏齿轮“打开设置” —— 由窗口打开设置窗口。</summary>
    public event EventHandler? SettingsRequested;
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
        // 延迟测量(ICMP)不依赖 metrics 服务,所以只要有 UI 就启动计时器。
        if (Avalonia.Application.Current is null)
            return;

        _statusMetricsTimer = new Avalonia.Threading.DispatcherTimer(
            TimeSpan.FromSeconds(1),
            Avalonia.Threading.DispatcherPriority.Background,
            (_, _) =>
            {
                _ = PollStatusMetricsAsync();
                _ = PollLatencyAsync();
            });
        _statusMetricsTimer.Start();
    }

    private bool _latencyPolling;
    private int _latencyTick;

    /// <summary>状态栏延迟指示(设计 gzmsb sbLatency,之前缺失):每 3 秒对活动标签的主机
    /// 发一次 ICMP ping,RTT 写入 tab.Latency(经既有 WhenAnyValue 管道刷新状态栏)。
    /// 目标禁 ICMP 或解析失败时清空显示,不打扰;不用 TCP 探测以免刷爆 sshd 日志。</summary>
    private async Task PollLatencyAsync()
    {
        if (_latencyPolling || _latencyTick++ % 3 != 0)
            return;

        var tab = ActiveTerminalTab;
        if (tab?.Profile is null || tab.ConnectionStatus != SessionStatus.Connected)
        {
            if (tab is not null)
                tab.Latency = null;
            return;
        }

        _latencyPolling = true;
        try
        {
            using var ping = new System.Net.NetworkInformation.Ping();
            var reply = await ping.SendPingAsync(tab.Profile.Host, TimeSpan.FromSeconds(2));

            // 探测期间用户可能切换了标签;不要把结果写到别的会话上。
            if (!ReferenceEquals(ActiveTerminalTab, tab))
                return;

            tab.Latency = reply.Status == System.Net.NetworkInformation.IPStatus.Success
                ? TimeSpan.FromMilliseconds(reply.RoundtripTime)
                : null;
        }
        catch
        {
            tab.Latency = null;
        }
        finally
        {
            _latencyPolling = false;
        }
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
        await RefreshSessionTreeAsync();
    }

    /// <summary>重新加载资源管理器会话树(新建/编辑/删除配置后调用),并同步刷新命令面板
    /// 的全量会话缓存。</summary>
    public async Task RefreshSessionTreeAsync()
    {
        if (Sidebar.SessionTree is { } tree)
        {
            try
            {
                await tree.LoadCommand.Execute();
            }
            catch
            {
                // 树加载失败不影响其余启动流程。
            }
        }

        await RefreshPaletteSessionsAsync();
    }

    // ---- 命令面板的全量会话(§12.3:面板作为中枢,收录全部已保存配置) ----

    private IReadOnlyList<SessionProfile> _paletteProfiles = [];
    private Dictionary<Guid, string> _paletteGroupNames = new();

    /// <summary>BuildPaletteItems 是同步回调,这里预取 session_profiles 全量与分组名。</summary>
    private async Task RefreshPaletteSessionsAsync()
    {
        if (_sessionRepository is null)
            return;

        try
        {
            var profiles = await _sessionRepository.GetAllSessionsAsync();
            var groups = await _sessionRepository.GetAllGroupsAsync();
            _paletteGroupNames = groups.ToDictionary(g => g.Id, g => g.Name);
            _paletteProfiles = profiles
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            // 面板会话缓存刷新失败不影响其余流程,下次刷新重试。
        }
    }

    private IReadOnlyList<CommandPaletteItem> BuildPaletteItems()
    {
        var items = new List<CommandPaletteItem>();

        // Recent connections first — the quick-access bucket (Enter connects).
        var recentProfileIds = new HashSet<Guid>();
        foreach (var item in Sidebar.RecentConnections.Connections)
        {
            var captured = item.Entry;
            if (captured.ProfileId is { } pid)
                recentProfileIds.Add(pid);
            var title = string.IsNullOrWhiteSpace(item.DisplayName) ? captured.Host : item.DisplayName;
            items.Add(new CommandPaletteItem(
                category: "最近连接",
                title: title,
                invoke: () => _ = TryConnectRecentAsync(captured),
                hint: "Enter 连接",
                isSession: true));
        }

        // All saved profiles (§12.3),带分组徽章;已出现在最近连接里的不重复列出。
        foreach (var profile in _paletteProfiles)
        {
            if (recentProfileIds.Contains(profile.Id))
                continue;

            var captured = profile;
            string? groupName = captured.GroupId is { } groupId
                && _paletteGroupNames.TryGetValue(groupId, out var name) ? name : null;
            items.Add(new CommandPaletteItem(
                category: "会话",
                title: string.IsNullOrWhiteSpace(captured.Name) ? captured.Host : captured.Name,
                invoke: () => _ = TryConnectProfileAsync(captured),
                hint: "Enter 连接",
                tag: groupName,
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

        var settings = await LoadSettingsSnapshotAsync().ConfigureAwait(true);
        var (terminalTab, document) = CreateConnectingTab(profile, settings);

        try
        {
            await RunHandshakeAsync(terminalTab, profile, settings, cancellationToken);
            return terminalTab;
        }
        catch
        {
            // 直接入口(编程/测试)保持既有语义:握手失败即撤掉标签并向上抛。
            // 交互入口 TryConnectProfileAsync 另有“保留标签 + 标签页内覆盖层”的失败处理。
            RemoveTerminalTab(terminalTab, document);
            throw;
        }
    }

    /// <summary>读取设置快照并缓存到 <see cref="_latestSettings"/>(无设置服务时用默认值)。</summary>
    private async Task<AppSettings> LoadSettingsSnapshotAsync()
    {
        var settings = _settingsService is not null
            ? await _settingsService.GetSettingsAsync()
            : new AppSettings();
        _latestSettings = settings;
        return settings;
    }

    /// <summary>立即创建一个“连接中”的终端标签并加入标签栏/停靠区(#17:慢连接不再像卡死,
    /// 用户立刻拿到可见、可关闭的标签)。握手由 <see cref="RunHandshakeAsync"/> 完成。
    /// 认证重试会复用同一标签,不重复建标签。</summary>
    private (TerminalTabViewModel Tab, TerminalDocument Document) CreateConnectingTab(SessionProfile profile, AppSettings settings)
    {
        var terminalType = TerminalTypeExtensions.FromTermName(settings.TerminalType);
        var terminalEmulator = _terminalEmulatorFactory();
        ConfigureTerminal(terminalEmulator, settings, terminalType);

        // 状态栏连接指示按设计 gzmsb 显示"SSH • <显示名称>"——不暴露用户名与 IP(安全要求);
        // 未配置名称时才退回主机地址。
        var displayName = string.IsNullOrWhiteSpace(profile.Name) ? profile.Host : profile.Name;
        var terminalTab = new TerminalTabViewModel(terminalEmulator)
        {
            Title = displayName,
            ConnectionStatus = SessionStatus.Connecting,
            ConnectionSummary = $"SSH • {displayName}",
            TerminalTypeName = terminalType.ToTermName(),
            EncodingName = string.IsNullOrWhiteSpace(settings.TerminalEncoding) ? "UTF-8" : settings.TerminalEncoding,
            Profile = profile,
        };
        terminalTab.ReconnectRequested += (_, _) => _ = ReconnectTabAsync(terminalTab);
        terminalTab.Disconnected += (_, _) => OnTabDisconnected(terminalTab);

        // 资源管理器树的状态圆点与「活跃/连接中/离线」标签(设计 FrJPu)跟随该配置
        // 最新标签的连接状态;重连复用同一标签,订阅随标签生命周期存续。
        terminalTab.WhenAnyValue(x => x.ConnectionStatus)
            .Subscribe(status => Sidebar.SessionTree?.SetSessionStatus(profile.Id, status));

        // 后台标签收到 BEL → 点亮闪烁提醒(设置 → 终端 → 标签闪烁提醒);切回标签时清除。
        if (terminalEmulator is PulseTerminalControl bellSource)
        {
            bellSource.BellRang += () =>
            {
                if (_latestSettings?.TerminalBehavior.TabFlashAlert != false
                    && !ReferenceEquals(ActiveTerminalTab, terminalTab))
                {
                    terminalTab.HasBellAlert = true;
                }
            };
        }

        var document = new TerminalDocument(terminalTab);
        // 标签页内失败覆盖层(设计 yxjmg)的“关闭标签页”按钮:闭包捕获 document 以整体移除。
        terminalTab.CloseRequested += (_, _) => RemoveTerminalTab(terminalTab, document);

        TabBar.AddTab(terminalTab);
        ActiveTerminalTab = terminalTab;
        _dockFactory.AddTerminal(document);
        UpdateStatusBarForActiveTab();

        return (terminalTab, document);
    }

    /// <summary>在一个已存在的“连接中”标签上完成 SSH 握手并挂上传输;失败时向上抛,由调用方
    /// 决定撤标签(直接入口)还是保留标签显示覆盖层(交互入口)。</summary>
    private async Task RunHandshakeAsync(TerminalTabViewModel terminalTab, SessionProfile profile, AppSettings settings, CancellationToken cancellationToken)
    {
        var terminalType = TerminalTypeExtensions.FromTermName(settings.TerminalType);

        var session = await _connectionWorkflowService!.ConnectProfileAsync(profile, cancellationToken);
        var client = _sshConnectionService!.GetClient(session.SessionId)
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
        StartSessionLogging(terminalTab, settings);
        SendStartupCommand(terminalTab, settings);

        // The session id only exists now, after the handshake — the active-tab subscription
        // fired before it was assigned, so bind the SFTP browser here (and show + load it) or
        // it stays bound to the empty placeholder and never loads a listing (#22).
        ShowFileBrowserForActiveSession();

        if (_metricsService is not null)
            terminalTab.ResourceMonitor = new ResourceMonitorViewModel(_metricsService, session.SessionId, terminalTab.Title);

        // 连接历史已由工作流写入 SonnetDB,这里刷新侧边栏“最近连接”。
        await Sidebar.RecentConnections.RefreshAsync();
        StatusBar.ResetUptime();
        UpdateStatusBarForActiveTab();
        LastConnectionError = null;
    }

    /// <summary>
    /// Reconnects a dropped session in place: it reuses the same tab, emulator and scrollback
    /// buffer, only rebuilding the transport. Triggered by Enter / Ctrl+R on a disconnected tab
    /// (or after exit/reboot) instead of forcing the user to open a fresh tab (#19).
    /// </summary>
    public async Task ReconnectTabAsync(TerminalTabViewModel tab, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);

        // Ignore reconnect requests while already connecting or connected.
        if (tab.ConnectionStatus is SessionStatus.Connecting or SessionStatus.Connected)
            return;

        // 本地终端标签:重开 = 重新拉起 shell 进程(复用同一标签与缓冲)。
        if (tab.LocalShell is { } localShell)
        {
            ReopenLocalShell(tab, localShell);
            return;
        }

        if (tab.Profile is null || _connectionWorkflowService is null || _sshConnectionService is null)
            return;

        tab.ConnectionStatus = SessionStatus.Connecting;
        tab.DetachTransport();
        UpdateStatusBarForActiveTab();

        try
        {
            var settings = _settingsService is not null
                ? await _settingsService.GetSettingsAsync()
                : new AppSettings();
            _latestSettings = settings;
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
            tab.ResetReconnectAttempts();
            StartSessionLogging(tab, settings);
            SendStartupCommand(tab, settings);
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
            // 重连失败:保留标签,标签页内覆盖层显示“连接失败 + 原因”(设计 yxjmg),不弹全局框。
            LastConnectionError = DescribeConnectionError(ex, tab.Profile);
            StatusBar.Status = LastConnectionError;
            tab.MarkDisconnected(LastConnectionError);
        }
    }

    // ---- 会话日志(设置 → 常规 → 数据与存储) ----

    private readonly System.Collections.Generic.Dictionary<TerminalTabViewModel, Services.SessionLogWriter>
        _sessionLogs = new();

    /// <summary>开启后把该会话的原始输出写入日志文件;每次(重)连接换新文件。</summary>
    private void StartSessionLogging(TerminalTabViewModel tab, AppSettings settings)
    {
        StopSessionLogging(tab);

        if (!settings.General.SessionLogging || tab.Bridge is null)
            return;

        var writer = Services.SessionLogService.CreateWriter(tab.Title);
        if (writer is null)
            return;

        tab.Bridge.DataReceived += writer.Write;
        _sessionLogs[tab] = writer;
    }

    private void StopSessionLogging(TerminalTabViewModel tab)
    {
        if (_sessionLogs.Remove(tab, out var writer))
            writer.Dispose(); // 旧桥可能还在收尾;Write 对已释放流是 no-op。
    }

    /// <summary>连接断开(设置 → 常规 → 行为/通知):状态栏提醒 + 可选提示音 +
    /// 自动重连(用户主动断开除外,按重连间隔与最大重试执行)。</summary>
    private void OnTabDisconnected(TerminalTabViewModel tab)
    {
        StopSessionLogging(tab);

        var settings = _latestSettings;
        if (settings is null)
            return;

        if (settings.General.NotifyOnDisconnect)
        {
            StatusBar.Status = $"{tab.Title} 连接已断开";
            if (!ReferenceEquals(ActiveTerminalTab, tab))
                tab.HasBellAlert = true;
        }

        if (settings.General.SoundAlerts && OperatingSystem.IsWindows())
            Services.SystemSound.Alert();

        // Headless unit tests construct this VM without an Avalonia application; no timer there.
        // 本地终端不自动重开:shell 退出(exit)是用户意图,自动拉起会没完没了。
        if (!settings.General.AutoReconnect || tab.UserRequestedDisconnect
            || tab.LocalShell is not null
            || Avalonia.Application.Current is null)
        {
            return;
        }

        int maxRetries = Math.Max(1, settings.General.MaxRetries);
        if (tab.ReconnectAttempts >= maxRetries)
            return;

        tab.IncrementReconnectAttempt();
        int delaySeconds = Math.Clamp(settings.General.ReconnectIntervalSeconds, 1, 300);
        StatusBar.Status = $"{tab.Title} 已断开,{delaySeconds} 秒后自动重连({tab.ReconnectAttempts}/{maxRetries})…";

        Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            // 等待期间用户可能已手动重连、关掉标签或主动断开。
            if (tab.ConnectionStatus == SessionStatus.Disconnected
                && !tab.UserRequestedDisconnect
                && TabBar.Tabs.Contains(tab))
            {
                _ = ReconnectTabAsync(tab);
            }
        }, TimeSpan.FromSeconds(delaySeconds));
    }

    /// <summary>bash 提示符补行脚本(内置,静默注入):命令输出末尾无换行时,经 DSR(ESC[6n)
    /// 查询光标列,不在行首则先补一个换行再画提示符(zsh 的默认行为,用户要求)。</summary>
    private const string PromptNewlineFix =
        "prompt_nl() { local c; IFS='[;' read -p $'\\e[6n' -d R -rs _ _ c; ((c>1)) && echo; }; PROMPT_COMMAND=prompt_nl";

    /// <summary>连接成功后静默注入初始化命令:内置补行脚本 + 用户配置的"连接后执行命令"
    /// (设置 → 终端 → 会话)拼接为一行,经回显抑制不在终端显示。PTY 输入由内核缓冲,
    /// shell 就绪后才会读取,无需等待提示符。</summary>
    private static void SendStartupCommand(TerminalTabViewModel tab, AppSettings settings)
    {
        var user = settings.TerminalBehavior.StartupCommand?.Trim();

        // 旧版本曾把补行脚本作为该设置项的默认值;现已内置,跳过以免重复执行。
        if (!string.IsNullOrEmpty(user) && user.Contains("PROMPT_COMMAND=prompt_nl", StringComparison.Ordinal))
            user = null;

        var payload = string.IsNullOrEmpty(user) ? PromptNewlineFix : PromptNewlineFix + "; " + user;
        tab.SendSilentCommand(payload);
    }

    private void RemoveTerminalTab(TerminalTabViewModel tab, TerminalDocument document)
    {
        StopSessionLogging(tab);
        if (TabBar.Tabs.Contains(tab))
            TabBar.CloseTabCommand.Execute(tab).Subscribe();

        _dockFactory.RemoveTerminal(document);

        if (ReferenceEquals(ActiveTerminalTab, tab))
            ActiveTerminalTab = TabBar.ActiveTab as TerminalTabViewModel;

        tab.Dispose();
    }

    /// <summary>
    /// 由窗口注入的交互式身份验证(两步弹窗):补全用户名/密码/密钥后返回更新的配置,
    /// 取消时返回 null。
    /// </summary>
    public Func<SessionProfile, Task<SessionProfile?>>? InteractiveAuthenticator { get; set; }

    /// <summary>缺少连接所需凭据(用户名/密码/私钥)时需要先走登录验证流程。</summary>
    private static bool RequiresCredentials(SessionProfile profile) =>
        string.IsNullOrWhiteSpace(profile.Username)
        || (profile.AuthMethod == AuthMethod.Password && string.IsNullOrEmpty(profile.Password))
        || (profile.AuthMethod == AuthMethod.PrivateKey && string.IsNullOrWhiteSpace(profile.PrivateKeyPath));

    /// <summary>
    /// Connects without ever letting a failure escape to the caller. Authentication failures,
    /// unreachable hosts and the like are captured in <see cref="LastConnectionError"/> and
    /// reflected in the status bar instead of crashing the app.
    /// 凭据缺失或认证失败时通过 <see cref="InteractiveAuthenticator"/> 走两步验证弹窗(最多重试 3 次)。
    /// </summary>
    public async Task<TerminalTabViewModel?> TryConnectProfileAsync(SessionProfile profile, CancellationToken cancellationToken = default)
    {
        if (_connectionWorkflowService is null || _sshConnectionService is null)
            return null;

        var current = profile;
        var settings = await LoadSettingsSnapshotAsync().ConfigureAwait(true);

        // 标签只创建一次:连接中→(失败则)标签页内覆盖层→(认证重试)复用同一标签,
        // 不再每次尝试都新建/销毁标签。慢连接不阻塞其它连接(SshConnectionService 已并发)。
        TerminalTabViewModel? tab = null;
        TerminalDocument? document = null;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var needsPrompt = attempt > 0 || RequiresCredentials(current);
            if (needsPrompt)
            {
                if (InteractiveAuthenticator is not { } prompt)
                {
                    if (attempt > 0)
                        return tab; // 无法交互重试,保留失败标签(含覆盖层)。
                }
                else
                {
                    var updated = await prompt(current);
                    if (updated is null)
                    {
                        // 用户取消:不弹连接失败提示,撤掉尚未连上的标签。
                        LastConnectionError = null;
                        if (tab is not null && document is not null)
                            RemoveTerminalTab(tab, document);
                        return null;
                    }

                    current = updated;
                }
            }

            if (tab is null)
            {
                (tab, document) = CreateConnectingTab(current, settings);
            }
            else
            {
                // 认证重试:复用标签,回到“连接中”(隐去上次的失败覆盖层)。
                tab.Profile = current;
                tab.ConnectionStatus = SessionStatus.Connecting;
            }

            try
            {
                await RunHandshakeAsync(tab, current, settings, cancellationToken);
                return tab;
            }
            catch (OperationCanceledException)
            {
                // 用户取消(超时):撤掉这个正在连接的标签。
                if (document is not null)
                    RemoveTerminalTab(tab, document);
                return null;
            }
            catch (Exception ex)
            {
                LastConnectionError = DescribeConnectionError(ex, current);
                StatusBar.Status = LastConnectionError;

                var isAuth = ex.GetType().Name == "SshAuthenticationException";

                // 认证失败但无法交互重试(headless):保持既有契约,撤标签、返回 null。
                if (isAuth && InteractiveAuthenticator is null)
                {
                    if (document is not null)
                        RemoveTerminalTab(tab, document);
                    return null;
                }

                // 认证失败且可交互:标记失败态并循环回去重新弹凭据重试。
                tab.MarkConnectionFailed(LastConnectionError);
                if (isAuth && InteractiveAuthenticator is not null)
                    continue;

                // 网络/超时等失败:保留标签,标签页内显示失败覆盖层(设计 yxjmg),不弹全局框。
                return tab;
            }
        }

        // 认证重试用尽:保留标签显示“认证失败”覆盖层,交给用户手动重连/关闭。
        return tab;
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

    /// <summary>窗口注入的多行粘贴确认弹窗(设置 → 终端 → 粘贴时确认多行内容)。</summary>
    public Func<string, Task<bool>>? MultilinePasteConfirmer { get; set; }

    private void ConfigureTerminal(ITerminalEmulator emulator, AppSettings settings, TerminalType terminalType,
        bool forceUtf8 = false)
    {
        if (emulator is PulseTerminalControl control)
            control.TerminalType = terminalType;

        ApplyLiveTerminalSettings(emulator, settings, forceUtf8);
    }

    /// <summary>The settings that are safe to change on a live session: scrollback depth, font,
    /// font size, host-output encoding plus the full 终端行为/配色 option set. Applied at tab
    /// creation and re-applied to every open tab whenever settings are saved (#3/#15/#21).</summary>
    private void ApplyLiveTerminalSettings(ITerminalEmulator emulator, AppSettings settings, bool forceUtf8 = false)
    {
        emulator.ScrollbackLines = settings.ScrollbackLines;

        if (emulator is PulseTerminalControl control)
        {
            // 本地终端(ConPTY)输出恒为 UTF-8,不套用面向远端主机的编码设置。
            control.SetEncoding(forceUtf8 ? Encoding.UTF8 : ResolveEncoding(settings.TerminalEncoding));
            if (!string.IsNullOrWhiteSpace(settings.TerminalFont))
                control.FontFamily = new Avalonia.Media.FontFamily(
                    $"{settings.TerminalFont}, JetBrains Mono, Cascadia Mono, Consolas, Microsoft YaHei, monospace");
            if (settings.TerminalFontSize > 0)
                control.FontSize = settings.TerminalFontSize;

            var behavior = settings.TerminalBehavior;
            control.LineHeight = behavior.LineHeight;
            control.CursorStyle = behavior.CursorStyle;
            control.CursorBlink = behavior.CursorBlink;
            control.BellMode = behavior.BellMode;
            control.VisualBell = behavior.VisualBell;
            control.ScrollOnOutput = behavior.ScrollOnOutput;
            control.ScrollOnKeystroke = behavior.ScrollOnKeystroke;
            control.CopyOnSelect = behavior.CopyOnSelect;
            control.RightClickPaste = behavior.RightClickPaste;
            control.TrimTrailingWhitespaceOnCopy = behavior.TrimTrailingWhitespaceOnCopy;
            control.DoubleClickSelectsWord = behavior.DoubleClickSelectsWord;
            control.ConfirmMultilinePaste = behavior.ConfirmMultilinePaste;
            control.MultilinePasteConfirmation = MultilinePasteConfirmer;
            control.CtrlCCopiesWhenSelected = behavior.CtrlCCopiesWhenSelected;
            control.ImeEnabled = behavior.ImeSupport;

            // 用户自定义的终端配色(仅覆盖改过的颜色,其余跟随主题)。
            control.PaletteOverrides = Services.TerminalAppearanceMapper.BuildPaletteOverrides(settings.Appearance);
        }
    }

    private void OnSettingsSaved(AppSettings settings)
    {
        _latestSettings = settings;

        // SaveSettingsAsync may complete on a thread-pool continuation; font/size touch layout,
        // so marshal onto the UI thread (the main scheduler is the Avalonia dispatcher).
        RxSchedulers.MainThreadScheduler.Schedule(Unit.Default, (_, _) =>
        {
            foreach (var tab in TabBar.Tabs.OfType<TerminalTabViewModel>())
                ApplyLiveTerminalSettings(tab.TerminalEmulator, settings, forceUtf8: tab.LocalShell is not null);

            // 已打开的文件浏览器同步最新的传输选项(冲突策略/并发/带宽等)。
            FileBrowser.TransferOptions = settings.Transfer;
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
        // 设计 gzmsb sbLatency 的写法是 "Latency: 12ms"(前缀由视图 StringFormat 提供)。
        StatusBar.Latency = tab.Latency is { } latency ? $"{(int)latency.TotalMilliseconds}ms" : string.Empty;
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
        StopSessionLogging(tab);
        CloseSftpForTab(tab);
        tab.Dispose();

        // 关闭标签不会再触发 ConnectionStatus 变更(已 Dispose),这里显式把树上的
        // 状态圆点复位;同配置还有其他已连接标签时保持"活跃"。
        if (tab.Profile is { } profile)
        {
            var stillConnected = TabBar.Tabs
                .OfType<TerminalTabViewModel>()
                .Any(other => !ReferenceEquals(other, tab)
                    && other.Profile?.Id == profile.Id
                    && other.ConnectionStatus == SessionStatus.Connected);
            if (!stillConnected)
                Sidebar.SessionTree?.SetSessionStatus(profile.Id, SessionStatus.Disconnected);
        }
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
