using System.Net.NetworkInformation;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Avalonia;
using Avalonia.Threading;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Core.Events;
using ReactiveUI;
using VelaShell.Services;
using VelaShell.ViewModels;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Services;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Core.Tunnels;
using VelaShell.Docking;
using VelaShell.Infrastructure.Pty;
using VelaShell.Presentation.Commands;
using VelaShell.Presentation.Services;
using VelaShell.Presentation.ViewModels;
using VelaShell.Terminal;
using VelaShell.Terminal.Emulation;
using VelaShell.Terminal.Rendering;

namespace VelaShell.ViewModels;

public class MainWindowViewModel : ReactiveObject
{
    /// <summary>
    /// bash 提示符补行脚本(内置,静默注入):命令输出末尾无换行时,经 DSR(ESC[6n)
    /// 查询光标列,不在行首则先补一个换行再画提示符(zsh 的默认行为,用户要求)。
    /// </summary>
    private const string PromptNewlineFix = """prompt_nl() { local c; IFS='[;' read -p $'\e[6n' -d R -rs _ _ c; ((c>1)) && echo; }; PROMPT_COMMAND=prompt_nl""";

    /// <summary>RIS(ESC c)完全重置序列:重开会话前清掉旧进程的残留缓冲。</summary>
    private static readonly byte[] RisResetSequence = [0x1B, (byte)'c']; // ESC c

    private readonly IConnectionWorkflowService? _connectionWorkflowService;
    private readonly TerminalDockFactory _dockFactory;
    private readonly ISessionMetricsService? _metricsService;

    // ---- 会话日志(设置 → 常规 → 数据与存储) ----

    private readonly Dictionary<TerminalTabViewModel, SessionLogWriter>
        _sessionLogs = [];

    private readonly IAppDataStore? _appDataStore;
    private readonly ISessionRepository? _sessionRepository;
    private readonly ISettingsService? _settingsService;
    private readonly ISftpService? _sftpService;
    private readonly ISshConnectionService? _sshConnectionService;
    private readonly Func<ITerminalEmulator> _terminalEmulatorFactory;
    private readonly ITunnelService? _tunnelService;

    /// <summary>全局命令历史(命令补全数据源;终端标签提交命令后写入)。</summary>
    public CommandHistoryService CommandHistory { get; }

    /// <summary>补全建议提供器(历史 ∪ 快捷命令),注入到每个终端标签。</summary>
    private readonly CommandSuggestionProvider _suggestionProvider;

    // SFTP/File management views derived from design
    private FileBrowserViewModel _fileBrowser;

    /// <summary>
    /// 按会话缓存的 SFTP 面板实例:切换标签复用(保留路径/列表/排序/列宽,免重复列目录),
    /// 标签关闭或连接断开时经 <see cref="EvictFileBrowser" /> 驱逐。
    /// </summary>
    private readonly Dictionary<Guid, FileBrowserViewModel> _fileBrowserCache = [];

    private FileTransferViewModel _fileTransfer;

    private bool _latencyPolling;
    private int _latencyTick;
    private AppSettings? _latestSettings;
    private Dictionary<Guid, string> _paletteGroupNames = [];

    // ---- 命令面板的全量会话(§12.3:面板作为中枢,收录全部已保存配置) ----

    private IReadOnlyList<SessionProfile> _paletteProfiles = [];
    private SidebarViewModel _sidebar;
    private StatusBarViewModel _statusBar;
    private bool _statusMetricsPolling;

    // ---- Status-bar live metrics (spec §7: cpu / memory / net for the active session) ----

    private DispatcherTimer? _statusMetricsTimer;
    private TabBarViewModel _tabBar;

    public MainWindowViewModel(
        IConnectionWorkflowService? connectionWorkflowService = null,
        ISshConnectionService? sshConnectionService = null,
        Func<ITerminalEmulator>? terminalEmulatorFactory = null,
        ISettingsService? settingsService = null,
        ISessionRepository? sessionRepository = null,
        ISftpService? sftpService = null,
        ITransferManager? transferManager = null,
        ITunnelService? tunnelService = null,
        ISessionMetricsService? metricsService = null,
        IRecentConnectionService? recentConnectionService = null,
        ISecurityAlertService? securityAlertService = null,
        ISettingsPreviewService? settingsPreviewService = null,
        IAppDataStore? appDataStore = null)
    {
        _appDataStore = appDataStore;

        // 命令补全(plan.md #16):全局命令历史 + 建议提供器(历史 ∪ 快捷命令),
        // 逐标签在 CreateConnectingTab 注入。
        CommandHistory = new(appDataStore);
        _suggestionProvider = new(CommandHistory, appDataStore);
        _connectionWorkflowService = connectionWorkflowService;
        _sshConnectionService = sshConnectionService;
        _settingsService = settingsService;
        _sessionRepository = sessionRepository;
        _sftpService = sftpService;
        _tunnelService = tunnelService;
        _metricsService = metricsService;
        _terminalEmulatorFactory = terminalEmulatorFactory ?? (() => new VelaTerminalControl());
        _dockFactory = new();
        Layout = _dockFactory.CreateLayout();
        _dockFactory.InitLayout(Layout);
        _dockFactory.DocumentClosed += OnDocumentClosed;
        _dockFactory.ActiveDockableChanged += OnActiveDockableChanged;
        _dockFactory.FocusedDockableChanged += OnFocusedDockableChanged;
        _sidebar = new(recentConnectionService);
        if (sessionRepository is not null)
        {
            _sidebar.SessionTree = new(sessionRepository);
        }
        _tabBar = new();
        _statusBar = new();
        _fileBrowser = new(null, Guid.Empty);
        _fileTransfer = new(transferManager);
        _tabBar.WhenAnyValue(tabBar => tabBar.ActiveTab)
               .Subscribe(activeTab =>
               {
                   ActiveTerminalTab = activeTab as TerminalTabViewModel;
                   activeTab?.HasBellAlert = false; // 切换到该标签即清除 Bell 提醒
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
        _settingsService?.SettingsSaved += OnSettingsSaved;

        // 外观即时预览(设置窗口广播,未持久化):只重刷已打开标签的终端外观,
        // 不动 _latestSettings(新建标签仍用已保存的设置)。
        settingsPreviewService?.PreviewRequested += settings =>
                RxSchedulers.MainThreadScheduler.Schedule(Unit.Default, (_, _) =>
                {
                    ApplyLiveSettingsToOpenTabs(settings);
                    return Disposable.Empty;
                });

        // 安全告警(设置 → 安全审计 → 告警通道):应用内 → 状态栏;提示音 → 系统提示音。
        securityAlertService?.Alerted += notice =>
                RxSchedulers.MainThreadScheduler.Schedule(Unit.Default, (_, _) =>
                {
                    if (notice.InApp)
                    {
                        StatusBar.Status = notice.Message;
                    }
                    if (notice.Sound)
                    {
                        SystemSound.Alert();
                    }
                    return Disposable.Empty;
                });
        StartStatusMetricsPolling();
        OpenSettingsCommand = ReactiveCommand.Create(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
        CommandPalette = new(BuildPaletteItems);
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
    public ReactiveCommand<string, Unit>? RunCommand { get; private set; }

    /// <summary>Tunnel manager panel for the active session (design fuXS7, spec §10).</summary>
    public TunnelPanelViewModel? TunnelPanel
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool IsTunnelPanelOpen
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>The self-drawn terminal control of the active tab, when it is one.</summary>
    private VelaTerminalControl? ActiveTerminalControl => ActiveTerminalTab?.TerminalEmulator.Control as VelaTerminalControl;

    /// <summary>The Ctrl+P / Ctrl+K command palette overlay.</summary>
    public CommandPaletteViewModel CommandPalette { get; }

    public ReactiveCommand<Unit, Unit> OpenCommandPaletteCommand { get; }

    /// <summary>
    /// 由窗口注入的交互式身份验证(两步弹窗):补全用户名/密码/密钥后返回更新的配置,
    /// 取消时返回 null。
    /// </summary>
    public Func<SessionProfile, Task<SessionProfile?>>? InteractiveAuthenticator { get; set; }

    /// <summary>The most recent connection error message, or null if the last attempt succeeded.</summary>
    public string? LastConnectionError
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>窗口注入的多行粘贴确认弹窗(设置 → 终端 → 粘贴时确认多行内容)。</summary>
    public Func<string, Task<bool>>? MultilinePasteConfirmer { get; set; }

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
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool HasActiveTerminalTab => ActiveTerminalTab is not null;

    /// <summary>The Dock.Avalonia layout hosting terminal documents (draggable, floatable, splittable).</summary>
    public IRootDock Layout { get; }

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

    private void RegisterCommands()
    {
        Commands.Register(new("session.new", "新建 SSH 连接", "会话",
            () => NewConnectionRequested?.Invoke(this, EventArgs.Empty), Shortcut: "Ctrl+N", Icon: "Icon.plus"));
        Commands.Register(new("session.close", "关闭当前会话", "会话",
            () => TabBar.CloseActiveTabCommand.Execute().Subscribe(),
            () => TabBar.ActiveTab is not null, "Ctrl+W"));
        Commands.Register(new("session.reconnect", "重连", "操作",
            () =>
            {
                if (ActiveTerminalTab is { } tab)
                {
                    _ = ReconnectTabAsync(tab);
                }
            },
            () => ActiveTerminalTab?.ConnectionStatus == SessionStatus.Disconnected,
            "Ctrl+R"));
        Commands.Register(new("session.clone", "克隆会话", "会话",
            () =>
            {
                if (ActiveTerminalTab?.Profile is { } profile)
                {
                    _ = TryConnectProfileAsync(profile);
                }
            },
            () => ActiveTerminalTab?.Profile is not null,
            "Ctrl+Shift+N", "Icon.copy"));
        Commands.Register(new("edit.copy", "复制", "编辑",
            () =>
            {
                if (ActiveTerminalControl is { } c)
                {
                    _ = c.CopyAsync();
                }
            },
            () => ActiveTerminalControl is not null, "Ctrl+Shift+C", "Icon.copy"));
        Commands.Register(new("edit.paste", "粘贴", "编辑",
            () =>
            {
                if (ActiveTerminalControl is { } c)
                {
                    _ = c.PasteAsync();
                }
            },
            () => ActiveTerminalControl is not null, "Ctrl+Shift+V"));
        Commands.Register(new("terminal.export", "导出终端输出到文件", "会话",
            () => ExportBufferRequested?.Invoke(this, EventArgs.Empty),
            () => ActiveTerminalControl is not null, Icon: "Icon.save"));
        Commands.Register(new("search.terminal", "终端内查找", "搜索",
            () => TerminalSearchRequested?.Invoke(this, EventArgs.Empty),
            () => ActiveTerminalTab is not null, "Ctrl+F", "Icon.search"));
        // 隧道独立于终端会话(后台自动连接),无活动标签也可用(用户反馈 #5)。
        Commands.Register(new("tools.tunnel", "隧道管理", "工具",
            ToggleTunnelPanel, Shortcut: "Ctrl+Shift+T", Icon: "Icon.route"));
        Commands.Register(new("tools.files", "SFTP 文件管理器", "工具",
            ToggleFileBrowser,
            () => ActiveTerminalTab is not null, "Ctrl+Shift+F", "Icon.folder"));
        Commands.Register(new("tools.diagnostics", "连接诊断", "工具",
            () =>
            {
                if (ActiveTerminalTab?.Profile is { } profile)
                {
                    DiagnosticsRequested?.Invoke(profile);
                }
            },
            () => ActiveTerminalTab?.Profile is not null, Icon: "Icon.stethoscope"));
        Commands.Register(new("edit.clear", "清屏", "编辑",
            () => ActiveTerminalTab?.TerminalEmulator.WriteInput([0x0C]),
            () => ActiveTerminalTab?.ConnectionStatus == SessionStatus.Connected));
        Commands.Register(new("app.settings", "打开设置", "编辑",
            () => OpenSettingsCommand.Execute().Subscribe(), Shortcut: "Ctrl+,", Icon: "Icon.settings"));
        Commands.Register(new("app.palette", "命令面板", "搜索",
            () => CommandPalette.Open(), Shortcut: "Ctrl+P", Icon: "Icon.zap"));

        // 本地终端(§12 P1-1):按本机安装情况动态注册 PowerShell/CMD/WSL/Git Bash 入口。
        foreach (LocalShellInfo shell in LocalShellCatalog.DetectShells())
        {
            LocalShellInfo captured = shell;
            Commands.Register(new($"local.{captured.Id}",
                $"打开本地终端:{captured.Name}", "会话",
                () => _ = OpenLocalTerminalAsync(captured), Icon: "Icon.terminal"));
        }
    }

    /// <summary>
    /// 打开一个本地终端标签:走与 SSH 相同的 桥 → VT 引擎 → 自绘控件 管线,
    /// 传输层换成 ConPTY(输出恒为 UTF-8,不套用设置里的远端编码)。
    /// </summary>
    public async Task OpenLocalTerminalAsync(LocalShellInfo shell)
    {
        AppSettings settings = _settingsService is not null
                                   ? await _settingsService.GetSettingsAsync()
                                   : new();
        _latestSettings = settings;
        ITerminalEmulator terminalEmulator = _terminalEmulatorFactory();
        ConfigureTerminal(terminalEmulator, settings, TerminalType.XtermColor256, true);
        var terminalTab = new TerminalTabViewModel(terminalEmulator)
        {
            Title = shell.Name,
            ConnectionStatus = SessionStatus.Connecting,
            ConnectionSummary = $"本地 • {shell.Name}",
            TerminalTypeName = TerminalType.XtermColor256.ToTermName(),
            EncodingName = "UTF-8",
            LocalShell = shell
        };
        terminalTab.ReconnectRequested += (_, _) => _ = ReconnectTabAsync(terminalTab);
        terminalTab.Disconnected += (_, _) => OnTabDisconnected(terminalTab);

        // 命令补全:注入建议提供器;提交(已回显校验)的命令进全局历史。
        terminalTab.SuggestionProvider = _suggestionProvider;
        terminalTab.CommandLineSubmitted += CommandHistory.Record;
        if (terminalEmulator is VelaTerminalControl bellSource)
        {
            bellSource.BellRang += () =>
            {
                if (_latestSettings?.TerminalBehavior.TabFlashAlert != false && !ReferenceEquals(ActiveTerminalTab, terminalTab))
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
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                AttachLocalShell(terminalTab, shell, settings);
            }
        }
        catch (Exception ex)
        {
            RemoveTerminalTab(terminalTab, document);
            LastConnectionError = $"启动 {shell.Name} 失败:{ex.Message}";
            StatusBar.Status = LastConnectionError;
        }
    }

    /// <summary>重开本地终端标签:RIS 清屏后重新拉起 shell(与 SSH 重连同语义)。</summary>
    private void ReopenLocalShell(TerminalTabViewModel tab, LocalShellInfo shell)
    {
        tab.ConnectionStatus = SessionStatus.Connecting;
        tab.DetachTransport();
        try
        {
            tab.TerminalEmulator.Feed(RisResetSequence);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                AttachLocalShell(tab, shell, _latestSettings ?? new AppSettings());
            }
            LastConnectionError = null;
        }
        catch (Exception ex)
        {
            tab.MarkDisconnected();
            LastConnectionError = $"重开 {shell.Name} 失败:{ex.Message}";
            StatusBar.Status = LastConnectionError;
        }
    }

    /// <summary>
    /// 拉起本地 shell 进程并挂上标签(打开与重开共用)。
    /// </summary>
    [SupportedOSPlatform(nameof(OSPlatform.Windows))]
    private void AttachLocalShell(TerminalTabViewModel tab, LocalShellInfo shell, AppSettings settings)
    {
        var stream = ConPtyShellStream.Start(shell.CommandLine,
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
    /// 面板实例按会话缓存:切回已看过的标签直接复用(旧列表秒显 + 后台静默刷新),
    /// 保留浏览路径/排序/列宽,不再每次切换都重建对象、重新列目录(用户优化需求)。
    /// 缓存的驱逐点:标签关闭与连接断开(<see cref="EvictFileBrowser" />)。
    /// </summary>
    private void RebindFileBrowser()
    {
        if (_sftpService is null)
        {
            return;
        }
        TerminalTabViewModel? tab = ActiveTerminalTab;
        if (tab is null || tab.SessionId == Guid.Empty)
        {
            return;
        }
        if (FileBrowser.SessionId == tab.SessionId)
        {
            return;
        }

        // Carry the open/closed state across the rebind so switching to (or connecting) a tab
        // never silently hides a panel the user had opened.
        bool wasVisible = FileBrowser.IsVisible;
        if (_fileBrowserCache.TryGetValue(tab.SessionId, out FileBrowserViewModel? cached))
        {
            cached.IsVisible = wasVisible;
            FileBrowser = cached;
            if (wasVisible)
            {
                _ = cached.RefreshSilentlyAsync();
            }
            return;
        }

        string serverName = tab.Profile is { } profile
                                ? string.IsNullOrWhiteSpace(profile.Name) ? profile.Host : profile.Name
                                : tab.Title;
        var browser = new FileBrowserViewModel(_sftpService, tab.SessionId)
        {
            TransferSink = FileTransfer,
            IsVisible = wasVisible,
            GetDefaultEditorPath = QueryDefaultEditorPathAsync,
            TransferOptions = _latestSettings?.Transfer ?? new TransferOptions(),
            ShowHiddenFiles = _latestSettings?.Transfer.ShowHiddenFiles ?? false,
            ShowHiddenFilesToggled = PersistShowHiddenFiles,
            ServerDisplayName = serverName,
            AccentBrush = tab.Profile is { } p ? ConnectionAccent.BrushFor(p.Id) : null
        };
        _fileBrowserCache[tab.SessionId] = browser;
        FileBrowser = browser;
        if (wasVisible)
        {
            // 全新面板首次展示:走初始加载(定位到登录家目录),而不是刷新根目录。
            FileBrowser.LoadInitialCommand.Execute().Subscribe(_ => { }, _ => { });
        }
    }

    /// <summary>
    /// 驱逐一个会话的缓存面板(标签关闭/连接断开):取消其在飞操作并移出缓存;
    /// 若当前面板正指向该会话,换成隐藏的空占位。
    /// </summary>
    private void EvictFileBrowser(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
        {
            return;
        }
        if (_fileBrowserCache.Remove(sessionId, out FileBrowserViewModel? cached))
        {
            cached.Detach();
        }
        if (FileBrowser.SessionId != sessionId)
        {
            return;
        }
        FileBrowser.Detach();

        // 占位面板必须继承被驱逐面板的打开状态:面板开/关是用户的全局意图,
        // RebindFileBrowser 切标签时以“当前面板的 IsVisible”为准搬运。若这里
        // 固定为隐藏,断开/关闭一个标签后切回其它标签,已打开的 SFTP 面板会被
        // 这个 false 传染而静默消失。
        FileBrowser = new(_sftpService, Guid.Empty)
        {
            TransferSink = FileTransfer,
            IsVisible = FileBrowser.IsVisible
        };
    }

    /// <summary>SFTP「使用默认编辑器打开」读取的编辑器命令(设置 → 文件传输 → 默认编辑器)。</summary>
    private async Task<string?> QueryDefaultEditorPathAsync()
    {
        if (_settingsService is null)
        {
            return null;
        }
        AppSettings settings = await _settingsService.GetSettingsAsync();
        return settings.Transfer.DefaultEditorPath;
    }

    /// <summary>
    /// Toggles the SFTP panel for the active session (#22, spec §9). Opening it binds the
    /// browser to the current session (if not already) and loads the initial listing.
    /// </summary>
    public void ToggleFileBrowser()
    {
        // Ensure the browser points at the active tab's (now-connected) session before showing it.
        // The active-tab subscription can't do this on its own because the session id is assigned
        // after the tab is activated, so we rebind on demand here as well.
        RebindFileBrowser();
        FileBrowser.IsVisible = !FileBrowser.IsVisible;
        if (FileBrowser.IsVisible && FileBrowser.SessionId != Guid.Empty)
        {
            RefreshOrLoadFileBrowser();
        }
    }

    /// <summary>已加载过的面板静默刷新(保留旧列表秒显),从未加载过的走完整初始加载。</summary>
    private void RefreshOrLoadFileBrowser()
    {
        if (FileBrowser.HasLoaded)
        {
            _ = FileBrowser.RefreshSilentlyAsync();
        }
        else
        {
            FileBrowser.LoadInitialCommand.Execute().Subscribe(_ => { }, _ => { });
        }
    }

    /// <summary>
    /// Called once a session finishes connecting: binds the file browser to it and shows
    /// the listing. Per spec §9 the file area is part of the session view (visible by default,
    /// collapsible), so a fresh connection surfaces its files without the user hunting for a toggle.
    /// </summary>
    private void ShowFileBrowserForActiveSession()
    {
        RebindFileBrowser();
        if (_sftpService is null || FileBrowser.SessionId == Guid.Empty)
        {
            return;
        }
        FileBrowser.IsVisible = true;
        RefreshOrLoadFileBrowser();
    }

    /// <summary>
    /// Raised when the user asks for in-terminal search via menu/palette; the window
    /// forwards it to the active terminal view's search bar (§5.3).
    /// </summary>
    public event EventHandler? TerminalSearchRequested;

    /// <summary>导出终端输出(命令面板“导出终端输出到文件”)—— 窗口弹保存对话框并落盘。</summary>
    public event EventHandler? ExportBufferRequested;

    /// <summary>
    /// 取当前标签的导出内容:有选区导出选区,否则导出整个缓冲区;附建议文件名。
    /// 无活动终端时返回 null。
    /// </summary>
    public (string Text, string SuggestedFileName)? GetActiveTerminalExport()
    {
        if (ActiveTerminalControl is not { } control || ActiveTerminalTab is not { } tab)
        {
            return null;
        }
        string selection = control.GetSelectedText();
        string text = string.IsNullOrEmpty(selection) ? control.GetBufferText() : selection;
        string safeTitle = string.Concat(tab.Title.Select(c =>
            char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
        if (safeTitle.Length > 40)
        {
            safeTitle = safeTitle[..40];
        }
        return (text, $"{safeTitle}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
    }

    /// <summary>Ctrl+N / 菜单 / 命令面板“新建 SSH 连接” —— 由窗口打开新建连接弹窗。</summary>
    public event EventHandler? NewConnectionRequested;

    /// <summary>Ctrl+, / 菜单 / 侧边栏齿轮“打开设置” —— 由窗口打开设置窗口。</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>工具菜单“连接诊断”(针对当前标签的配置)—— 由窗口打开诊断中心弹窗。</summary>
    public event Action<SessionProfile>? DiagnosticsRequested;

    /// <summary>Singleton toggle (spec §17.2): reopening focuses the existing panel.</summary>
    public void ToggleTunnelPanel()
    {
        if (IsTunnelPanelOpen)
        {
            IsTunnelPanelOpen = false;
            return;
        }
        OpenTunnelPanel();
    }

    /// <summary>
    /// 打开隧道面板(可选预选某台服务器)。面板以服务器为中心、生命周期与终端
    /// 会话无关:无需先打开终端标签,创建隧道时由面板后台自建 SSH 连接(用户反馈 #5)。
    /// </summary>
    public void OpenTunnelPanel(SessionProfile? preselect = null)
    {
        if (_tunnelService is null)
        {
            return;
        }
        if (TunnelPanel is null)
        {
            Func<Task<IReadOnlyList<SessionProfile>>>? servers = _sessionRepository is null
                                                                     ? null
                                                                     : async () => await _sessionRepository.GetAllSessionsAsync();
            var panel = new TunnelPanelViewModel(_tunnelService,
                servers,
                ConnectTunnelHostAsync,
                id => _sshConnectionService?.GetClient(id)?.IsConnected == true,
                id => _connectionWorkflowService?.DisconnectAsync(id) ?? Task.CompletedTask,
                _appDataStore);
            panel.CloseRequested += (_, _) => IsTunnelPanelOpen = false;
            TunnelPanel = panel;
        }
        _ = TunnelPanel.OpenAsync(preselect?.Id ?? ActiveTerminalTab?.Profile?.Id);
        IsTunnelPanelOpen = true;
    }

    /// <summary>为隧道面板后台建立 SSH 连接:不开终端标签,凭据缺失时走登录验证弹窗。</summary>
    private async Task<Guid> ConnectTunnelHostAsync(SessionProfile profile, CancellationToken cancellationToken)
    {
        if (_connectionWorkflowService is null)
        {
            throw new InvalidOperationException("SSH 连接服务未配置。");
        }
        SessionProfile current = profile;
        if (RequiresCredentials(current))
        {
            SessionProfile? updated = InteractiveAuthenticator is { } prompt ? await prompt(current) : null;
            current = updated ?? throw new InvalidOperationException("该配置未保存凭据,已取消登录验证。");
        }
        SshSession session = await _connectionWorkflowService.ConnectProfileAsync(current, cancellationToken);
        return session.SessionId;
    }

    /// <summary>
    /// Polls the active session's metrics once a second into the status bar. The
    /// probe runs on a dedicated SSH exec channel, so it never touches the terminal stream;
    /// consecutive samples give the collector real instantaneous CPU% and network rates.
    /// </summary>
    private void StartStatusMetricsPolling()
    {
        // Headless unit tests construct this VM without an Avalonia application; skip there.
        // 延迟测量(ICMP)不依赖 metrics 服务,所以只要有 UI 就启动计时器。
        if (Application.Current is null)
        {
            return;
        }
        _statusMetricsTimer = new(TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            (_, _) =>
            {
                _ = PollStatusMetricsAsync();
                _ = PollLatencyAsync();
            });
        _statusMetricsTimer.Start();
    }

    /// <summary>
    /// 状态栏延迟指示(设计 gzmsb sbLatency,之前缺失):每 3 秒对活动标签的主机
    /// 发一次 ICMP ping,RTT 写入 tab.Latency(经既有 WhenAnyValue 管道刷新状态栏)。
    /// 目标禁 ICMP 或解析失败时清空显示,不打扰;不用 TCP 探测以免刷爆 sshd 日志。
    /// </summary>
    private async Task PollLatencyAsync()
    {
        if (_latencyPolling || _latencyTick++ % 3 != 0)
        {
            return;
        }
        TerminalTabViewModel? tab = ActiveTerminalTab;
        if (tab?.Profile is null || tab.ConnectionStatus != SessionStatus.Connected)
        {
            tab?.Latency = null;
            return;
        }
        _latencyPolling = true;
        try
        {
            using var ping = new Ping();
            PingReply reply = await ping.SendPingAsync(tab.Profile.Host, TimeSpan.FromSeconds(2));

            // 探测期间用户可能切换了标签;不要把结果写到别的会话上。
            if (!ReferenceEquals(ActiveTerminalTab, tab))
            {
                return;
            }
            tab.Latency = reply.Status == IPStatus.Success
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
        {
            return;
        }
        TerminalTabViewModel? tab = ActiveTerminalTab;
        if (tab is null || tab.SessionId == Guid.Empty || tab.ConnectionStatus != SessionStatus.Connected)
        {
            StatusBar.ClearSessionMetrics();
            return;
        }
        _statusMetricsPolling = true;
        try
        {
            SessionMetrics? metrics = await _metricsService.GetMetricsAsync(tab.SessionId);

            // The user may have switched tabs while the probe ran; don't show stale data.
            if (!ReferenceEquals(ActiveTerminalTab, tab))
            {
                return;
            }
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

            // 悬停提示的详情(用户反馈):CPU 逐核心、磁盘逐挂载点、网速逐网卡。
            StatusBar.CpuTooltip = BuildCpuTooltip(metrics);
            StatusBar.MemTooltip = BuildMemTooltip(metrics);
            StatusBar.DiskTooltip = BuildDiskTooltip(metrics);
            StatusBar.NetTooltip = BuildNetTooltip(metrics);
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

    private static string BuildCpuTooltip(SessionMetrics m)
    {
        var sb = new StringBuilder();
        sb.Append($"CPU 总占用: {m.CpuPercent:F1}%（{m.CpuCores} 核）");
        if (m.CorePercents is { Count: > 0 } percents)
        {
            for (int i = 0; i < percents.Count; i++)
            {
                string name = i < m.CoreCounters.Count ? m.CoreCounters[i].Name.Replace("cpu", "核心 ") : $"核心 {i}";
                sb.Append('\n').Append($"{name}: {percents[i]:F0}%");
            }
        }
        else if (m.CoreCounters.Count > 0)
        {
            sb.Append("\n每核心占用统计中…");
        }
        return sb.ToString();
    }

    private static string BuildMemTooltip(SessionMetrics m)
    {
        var sb = new StringBuilder();
        sb.Append($"内存: {FormatGb(m.MemUsedBytes)} / {FormatGb(m.MemTotalBytes)} GB（{m.MemPercent:F1}%）");
        if (m.SwapTotalBytes > 0)
        {
            sb.Append('\n').Append($"交换: {FormatGb(m.SwapUsedBytes)} / {FormatGb(m.SwapTotalBytes)} GB（{m.SwapPercent:F1}%）");
        }
        return sb.ToString();
    }

    private static string BuildDiskTooltip(SessionMetrics m)
    {
        if (m.Disks.Count == 0)
        {
            return m.DiskTotalBytes > 0
                       ? $"磁盘 (/): {FormatGb(m.DiskUsedBytes)} / {FormatGb(m.DiskTotalBytes)} GB（{m.DiskPercent:F0}%）"
                       : "磁盘";
        }
        var sb = new StringBuilder("磁盘占用");
        foreach (DiskUsage d in m.Disks)
        {
            sb.Append('\n').Append($"{d.MountPoint}: {FormatGb(d.UsedBytes)} / {FormatGb(d.TotalBytes)} GB（{d.Percent:F0}%）");
        }
        return sb.ToString();
    }

    private static string BuildNetTooltip(SessionMetrics m)
    {
        var sb = new StringBuilder();
        sb.Append(m.HasNetRates
                      ? $"网速（合计）: ↓ {StatusBarViewModel.FormatRate(m.NetRxBytesPerSec)}  ↑ {StatusBarViewModel.FormatRate(m.NetTxBytesPerSec)}"
                      : "网速统计中…");
        if (m.NicRates is not { Count: > 0 } rates)
        {
            return sb.ToString();
        }
        foreach (NetInterfaceRate r in rates)
        {
            sb.Append('\n').Append($"{r.Name}: ↓ {StatusBarViewModel.FormatRate(r.RxBytesPerSec)}  ↑ {StatusBarViewModel.FormatRate(r.TxBytesPerSec)}");
        }
        return sb.ToString();
    }

    private static string FormatGb(long bytes) => (bytes / 1024.0 / 1024.0 / 1024.0).ToString("F1");

    /// <summary>
    /// Loads the persisted recent-connection history (SonnetDB) into the sidebar so it
    /// survives restarts.
    /// </summary>
    public async Task InitializeAsync()
    {
        await CommandHistory.LoadAsync();
        await Sidebar.RecentConnections.RefreshAsync();
        await RefreshSessionTreeAsync();
    }

    /// <summary>
    /// 重新加载资源管理器会话树(新建/编辑/删除配置后调用),并同步刷新命令面板
    /// 的全量会话缓存。
    /// </summary>
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

    /// <summary>BuildPaletteItems 是同步回调,这里预取 session_profiles 全量与分组名。</summary>
    private async Task RefreshPaletteSessionsAsync()
    {
        if (_sessionRepository is null)
        {
            return;
        }
        try
        {
            List<SessionProfile> profiles = await _sessionRepository.GetAllSessionsAsync();
            List<ServerGroup> groups = await _sessionRepository.GetAllGroupsAsync();
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
        foreach (RecentConnectionItemViewModel item in Sidebar.RecentConnections.Connections)
        {
            RecentConnectionEntry captured = item.Entry;
            if (captured.ProfileId is { } pid)
            {
                recentProfileIds.Add(pid);
            }
            string title = string.IsNullOrWhiteSpace(item.DisplayName) ? captured.Host : item.DisplayName;
            items.Add(new("最近连接",
                title,
                () => _ = TryConnectRecentAsync(captured),
                "Enter 连接",
                isSession: true));
        }

        // All saved profiles (§12.3),带分组徽章;已出现在最近连接里的不重复列出。
        foreach (SessionProfile profile in _paletteProfiles)
        {
            if (recentProfileIds.Contains(profile.Id))
            {
                continue;
            }
            SessionProfile captured = profile;
            string? groupName = captured.GroupId is { } groupId && _paletteGroupNames.TryGetValue(groupId, out string? name) ? name : null;
            items.Add(new("会话",
                string.IsNullOrWhiteSpace(captured.Name) ? captured.Host : captured.Name,
                () => _ = TryConnectProfileAsync(captured),
                "Enter 连接",
                groupName,
                true));
        }

        // Global actions come from the shared command registry (menu/palette/shortcut parity).
        items.AddRange(Commands.All.Select(captured => new CommandPaletteItem("命令", captured.Title, () => Commands.Execute(captured.Id), captured.Shortcut)));
        return items;
    }

    public async Task<TerminalTabViewModel> ConnectProfileAsync(SessionProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (_connectionWorkflowService is null || _sshConnectionService is null)
        {
            throw new InvalidOperationException("SSH connection services are not configured.");
        }
        AppSettings settings = await LoadSettingsSnapshotAsync().ConfigureAwait(true);
        (TerminalTabViewModel terminalTab, TerminalDocument document) = CreateConnectingTab(profile, settings);
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

    /// <summary>读取设置快照并缓存到 <see cref="_latestSettings" />(无设置服务时用默认值)。</summary>
    private async Task<AppSettings> LoadSettingsSnapshotAsync()
    {
        AppSettings settings = _settingsService is not null
                                   ? await _settingsService.GetSettingsAsync()
                                   : new();
        _latestSettings = settings;
        return settings;
    }

    /// <summary>
    /// 立即创建一个“连接中”的终端标签并加入标签栏/停靠区(#17:慢连接不再像卡死,
    /// 用户立刻拿到可见、可关闭的标签)。握手由 <see cref="RunHandshakeAsync" /> 完成。
    /// 认证重试会复用同一标签,不重复建标签。
    /// </summary>
    private (TerminalTabViewModel Tab, TerminalDocument Document) CreateConnectingTab(SessionProfile profile, AppSettings settings)
    {
        TerminalType terminalType = TerminalTypeExtensions.FromTermName(settings.TerminalType);
        ITerminalEmulator terminalEmulator = _terminalEmulatorFactory();
        ConfigureTerminal(terminalEmulator, settings, terminalType);

        // 状态栏连接指示按设计 gzmsb 显示"SSH • <显示名称>"——不暴露用户名与 IP(安全要求);
        // 未配置名称时才退回主机地址。
        string displayName = string.IsNullOrWhiteSpace(profile.Name) ? profile.Host : profile.Name;
        var terminalTab = new TerminalTabViewModel(terminalEmulator)
        {
            Title = displayName,
            ConnectionStatus = SessionStatus.Connecting,
            // 配了跳板的会话在状态栏点明"经由跳板",让用户一眼确认链路生效(用户反馈 #1)。
            ConnectionSummary = profile.JumpHostProfileId is null
                                    ? $"SSH • {displayName}"
                                    : $"SSH • {displayName} • 经由跳板",
            TerminalTypeName = terminalType.ToTermName(),
            EncodingName = string.IsNullOrWhiteSpace(settings.TerminalEncoding) ? "UTF-8" : settings.TerminalEncoding,
            Profile = profile
        };
        terminalTab.ReconnectRequested += (_, _) => _ = ReconnectTabAsync(terminalTab);
        terminalTab.Disconnected += (_, _) => OnTabDisconnected(terminalTab);

        // 命令补全:注入建议提供器;提交(已回显校验)的命令进全局历史。
        terminalTab.SuggestionProvider = _suggestionProvider;
        terminalTab.CommandLineSubmitted += CommandHistory.Record;

        // 资源管理器树的状态圆点与「活跃/连接中/离线」标签(设计 FrJPu)跟随该配置
        // 最新标签的连接状态;重连复用同一标签,订阅随标签生命周期存续。
        terminalTab.WhenAnyValue(x => x.ConnectionStatus)
                   .Subscribe(status => Sidebar.SessionTree?.SetSessionStatus(profile.Id, status));

        // 后台标签收到 BEL → 点亮闪烁提醒(设置 → 终端 → 标签闪烁提醒);切回标签时清除。
        if (terminalEmulator is VelaTerminalControl bellSource)
        {
            bellSource.BellRang += () =>
            {
                if (_latestSettings?.TerminalBehavior.TabFlashAlert != false && !ReferenceEquals(ActiveTerminalTab, terminalTab))
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

    /// <summary>
    /// 在一个已存在的“连接中”标签上完成 SSH 握手并挂上传输;失败时向上抛,由调用方
    /// 决定撤标签(直接入口)还是保留标签显示覆盖层(交互入口)。
    /// </summary>
    private async Task RunHandshakeAsync(TerminalTabViewModel terminalTab, SessionProfile profile, AppSettings settings, CancellationToken cancellationToken)
    {
        TerminalType terminalType = TerminalTypeExtensions.FromTermName(settings.TerminalType);
        SshSession session = await _connectionWorkflowService!.ConnectProfileAsync(profile, cancellationToken);
        ISshClientWrapper client = _sshConnectionService!.GetClient(session.SessionId) ?? throw new InvalidOperationException("SSH client was not created for the session.");
        // CreateShellStream 是同步网络往返(打开通道 + pty-req + shell,2~3 个 RTT),
        // 放在线程池上执行,否则每连一个标签 UI 线程就冻结 RTT 的整数倍时长。
        IShellStreamWrapper shellStream = await Task.Run(() => client.CreateShellStream(terminalType.ToTermName(),
            120,
            32,
            0,
            0,
            4096), cancellationToken);
        terminalTab.SessionId = session.SessionId;
        terminalTab.AttachTransport(shellStream);
        terminalTab.Start();
        terminalTab.ConnectionStatus = SessionStatus.Connected;
        await FeedJumpChainNoticeAsync(terminalTab, profile);
        StartSessionLogging(terminalTab, settings);
        SendStartupCommand(terminalTab, settings);

        // The session id only exists now, after the handshake — the active-tab subscription
        // fired before it was assigned, so bind the SFTP browser here (and show + load it) or
        // it stays bound to the empty placeholder and never loads a listing (#22).
        ShowFileBrowserForActiveSession();
        if (_metricsService is not null)
        {
            terminalTab.ResourceMonitor = new(_metricsService, session.SessionId, terminalTab.Title);
        }

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
        {
            return;
        }

        // 本地终端标签:重开 = 重新拉起 shell 进程(复用同一标签与缓冲)。
        if (tab.LocalShell is { } localShell)
        {
            ReopenLocalShell(tab, localShell);
            return;
        }
        if (tab.Profile is null || _connectionWorkflowService is null || _sshConnectionService is null)
        {
            return;
        }
        tab.ConnectionStatus = SessionStatus.Connecting;
        tab.DetachTransport();
        UpdateStatusBarForActiveTab();
        try
        {
            AppSettings settings = _settingsService is not null
                                       ? await _settingsService.GetSettingsAsync()
                                       : new();
            _latestSettings = settings;
            TerminalType terminalType = TerminalTypeExtensions.FromTermName(settings.TerminalType);
            SshSession session = await _connectionWorkflowService.ConnectProfileAsync(tab.Profile, cancellationToken);
            ISshClientWrapper client = _sshConnectionService.GetClient(session.SessionId) ?? throw new InvalidOperationException("SSH client was not created for the session.");
            // 同 RunHandshakeAsync:通道打开的同步网络往返不能占用 UI 线程。
            IShellStreamWrapper shellStream = await Task.Run(() => client.CreateShellStream(terminalType.ToTermName(),
                120,
                32,
                0,
                0,
                4096), cancellationToken);

            // Full-reset (RIS) the emulator before the new session's output arrives, so the
            // fresh MOTD doesn't append after the old buffer's content (用户反馈 #1).
            tab.TerminalEmulator.Feed("\ec"u8.ToArray());
            tab.SessionId = session.SessionId;
            tab.AttachTransport(shellStream);
            tab.Start();
            tab.ConnectionStatus = SessionStatus.Connected;
            await FeedJumpChainNoticeAsync(tab, tab.Profile);
            tab.ResetReconnectAttempts();
            StartSessionLogging(tab, settings);
            SendStartupCommand(tab, settings);
            if (_metricsService is not null)
            {
                tab.ResourceMonitor = new(_metricsService, session.SessionId, tab.Title);
            }

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

    /// <summary>
    /// 跳板链可见反馈(用户反馈 #1):经由跳板建立的会话在终端顶部打一行灰色提示,
    /// 标注实际经过的跳板链路,让用户确认跳板真的生效。纯装饰,失败不影响连接。
    /// </summary>
    private async Task FeedJumpChainNoticeAsync(TerminalTabViewModel tab, SessionProfile profile)
    {
        if (_sessionRepository is null || profile.JumpHostProfileId is null)
        {
            return;
        }
        try
        {
            var names = new List<string>();
            var visited = new HashSet<Guid> { profile.Id };
            Guid? jumpId = profile.JumpHostProfileId;
            while (jumpId is { } id && visited.Add(id) && names.Count < 5)
            {
                SessionProfile? jump = await _sessionRepository.GetSessionAsync(id);
                if (jump is null)
                {
                    break;
                }
                names.Add(string.IsNullOrWhiteSpace(jump.Name) ? jump.Host : jump.Name);
                jumpId = jump.JumpHostProfileId;
            }
            if (names.Count == 0)
            {
                return;
            }

            // 配置里跳板由内向外嵌套;反转成"本机 → 最外层跳板 → … → 目标"的阅读顺序。
            names.Reverse();
            string target = string.IsNullOrWhiteSpace(profile.Name) ? profile.Host : profile.Name;
            string notice = "\e[90m● 已经由跳板链路连接:本机 → " + string.Join(" → ", names) + " → " + target + "\e[0m\r\n";
            tab.TerminalEmulator.Feed(Encoding.UTF8.GetBytes(notice));
        }
        catch
        {
            // 提示为纯装饰,读取跳板名失败时静默跳过。
        }
    }

    /// <summary>
    /// 关闭标签背后的 SSH 会话(用户反馈 #2):标签的 DisconnectCommand 只拆终端
    /// 传输层,底层 SshClient 仍保持 TCP 连接;这里显式断开并释放,避免"界面显示已断开、
    /// 连接实际还活着"。该会话上的隧道也一并停止。
    /// </summary>
    private void TeardownSshSession(Guid sessionId)
    {
        if (sessionId == Guid.Empty || _connectionWorkflowService is null)
        {
            return;
        }
        ITunnelService? tunnelService = _tunnelService;
        _ = Task.Run(async () =>
        {
            if (tunnelService is not null)
            {
                try
                {
                    await tunnelService.StopAllForSessionAsync(sessionId);
                }
                catch
                {
                    // 隧道清理失败不阻塞断开。
                }
            }
            try
            {
                await _connectionWorkflowService.DisconnectAsync(sessionId);
            }
            catch
            {
                // 会话可能已被服务端拆除或从未完成握手。
            }
        });
    }

    /// <summary>开启后把该会话的原始输出写入日志文件;每次(重)连接换新文件。</summary>
    private void StartSessionLogging(TerminalTabViewModel tab, AppSettings settings)
    {
        StopSessionLogging(tab);
        if (!settings.General.SessionLogging || tab.Bridge is null)
        {
            return;
        }
        SessionLogWriter? writer = SessionLogService.CreateWriter(tab.Title);
        if (writer is null)
        {
            return;
        }
        tab.Bridge.DataReceived += writer.Write;
        _sessionLogs[tab] = writer;
    }

    private void StopSessionLogging(TerminalTabViewModel tab)
    {
        if (_sessionLogs.Remove(tab, out SessionLogWriter? writer))
        {
            writer.Dispose(); // 旧桥可能还在收尾;Write 对已释放流是 no-op。
        }
    }

    /// <summary>
    /// 连接断开(设置 → 常规 → 行为/通知):状态栏提醒 + 可选提示音 +
    /// 自动重连(用户主动断开除外,按重连间隔与最大重试执行)。
    /// </summary>
    private void OnTabDisconnected(TerminalTabViewModel tab)
    {
        StopSessionLogging(tab);

        // 会话断开后 SFTP 通道随之失效:驱逐缓存的文件面板并释放 SFTP 客户端。
        // 重连会拿到新的 SessionId,面板届时按新会话重建。
        CloseSftpForTab(tab);

        // 不论主动断开还是远端掉线,都把底层 SSH 客户端一并拆掉(用户反馈 #2);
        // 重连会新建会话,不受影响。
        TeardownSshSession(tab.SessionId);
        AppSettings? settings = _latestSettings;
        if (settings is null)
        {
            return;
        }
        if (settings.General.NotifyOnDisconnect)
        {
            StatusBar.Status = $"{tab.Title} 连接已断开";
            if (!ReferenceEquals(ActiveTerminalTab, tab))
            {
                tab.HasBellAlert = true;
            }
        }
        if (settings.General.SoundAlerts && OperatingSystem.IsWindows())
        {
            SystemSound.Alert();
        }

        // Headless unit tests construct this VM without an Avalonia application; no timer there.
        // 本地终端不自动重开:shell 退出(exit)是用户意图,自动拉起会没完没了。
        if (!settings.General.AutoReconnect || tab.UserRequestedDisconnect || tab.LocalShell is not null || Application.Current is null)
        {
            return;
        }
        int maxRetries = Math.Max(1, settings.General.MaxRetries);
        tab.MaxReconnectAttempts = maxRetries; // 全部自动重连路径共用同一权威值(设置审计 C-02)
        if (tab.ReconnectAttempts >= maxRetries)
        {
            return;
        }
        tab.IncrementReconnectAttempt();
        int delaySeconds = Math.Clamp(settings.General.ReconnectIntervalSeconds, 1, 300);
        StatusBar.Status = $"{tab.Title} 已断开,{delaySeconds} 秒后自动重连({tab.ReconnectAttempts}/{maxRetries})…";
        DispatcherTimer.RunOnce(() =>
        {
            // 等待期间用户可能已手动重连、关掉标签或主动断开。
            if (tab is { ConnectionStatus: SessionStatus.Disconnected, UserRequestedDisconnect: false } && TabBar.Tabs.Contains(tab))
            {
                _ = ReconnectTabAsync(tab);
            }
        }, TimeSpan.FromSeconds(delaySeconds));
    }

    /// <summary>
    /// 连接成功后静默注入初始化命令:内置补行脚本 + 用户配置的"连接后执行命令"
    /// (设置 → 终端 → 会话)拼接为一行,经回显抑制不在终端显示。PTY 输入由内核缓冲,
    /// shell 就绪后才会读取,无需等待提示符。
    /// </summary>
    private static void SendStartupCommand(TerminalTabViewModel tab, AppSettings settings)
    {
        string? user = settings.TerminalBehavior.StartupCommand.Trim();

        // 旧版本曾把补行脚本作为该设置项的默认值;现已内置,跳过以免重复执行。
        if (!string.IsNullOrEmpty(user) && user.Contains("PROMPT_COMMAND=prompt_nl", StringComparison.Ordinal))
        {
            user = null;
        }
        string payload = string.IsNullOrEmpty(user) ? PromptNewlineFix : PromptNewlineFix + "; " + user;
        tab.SendSilentCommand(payload);
    }

    private void RemoveTerminalTab(TerminalTabViewModel tab, TerminalDocument document)
    {
        StopSessionLogging(tab);
        if (TabBar.Tabs.Contains(tab))
        {
            TabBar.CloseTabCommand.Execute(tab).Subscribe();
        }
        _dockFactory.RemoveTerminal(document);
        if (ReferenceEquals(ActiveTerminalTab, tab))
        {
            ActiveTerminalTab = TabBar.ActiveTab as TerminalTabViewModel;
        }
        tab.Dispose();
    }

    /// <summary>缺少连接所需凭据(用户名/密码/私钥)时需要先走登录验证流程。</summary>
    private static bool RequiresCredentials(SessionProfile profile) => string.IsNullOrWhiteSpace(profile.Username) || (profile.AuthMethod == AuthMethod.Password && string.IsNullOrEmpty(profile.Password)) || (profile.AuthMethod == AuthMethod.PrivateKey && string.IsNullOrWhiteSpace(profile.PrivateKeyPath));

    /// <summary>
    /// Connects without ever letting a failure escape to the caller. Authentication failures,
    /// unreachable hosts and the like are captured in <see cref="LastConnectionError" /> and
    /// reflected in the status bar instead of crashing the app.
    /// 凭据缺失或认证失败时通过 <see cref="InteractiveAuthenticator" /> 走两步验证弹窗(最多重试 3 次)。
    /// </summary>
    public async Task<TerminalTabViewModel?> TryConnectProfileAsync(SessionProfile profile, CancellationToken cancellationToken = default)
    {
        if (_connectionWorkflowService is null || _sshConnectionService is null)
        {
            return null;
        }
        SessionProfile current = profile;
        AppSettings settings = await LoadSettingsSnapshotAsync().ConfigureAwait(true);

        // 标签只创建一次:连接中→(失败则)标签页内覆盖层→(认证重试)复用同一标签,
        // 不再每次尝试都新建/销毁标签。慢连接不阻塞其它连接(SshConnectionService 已并发)。
        TerminalTabViewModel? tab = null;
        TerminalDocument? document = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            bool needsPrompt = attempt > 0 || RequiresCredentials(current);
            if (needsPrompt)
            {
                if (InteractiveAuthenticator is not { } prompt)
                {
                    if (attempt > 0)
                    {
                        return tab; // 无法交互重试,保留失败标签(含覆盖层)。
                    }
                }
                else
                {
                    SessionProfile? updated = await prompt(current);
                    if (updated is null)
                    {
                        // 用户取消:不弹连接失败提示,撤掉尚未连上的标签。
                        LastConnectionError = null;
                        if (tab is not null && document is not null)
                        {
                            RemoveTerminalTab(tab, document);
                        }
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
                {
                    RemoveTerminalTab(tab, document);
                }
                return null;
            }
            catch (Exception ex)
            {
                LastConnectionError = DescribeConnectionError(ex, current);
                StatusBar.Status = LastConnectionError;
                bool isAuth = ex.GetType().Name == "SshAuthenticationException";

                // 认证失败但无法交互重试(headless):保持既有契约,撤标签、返回 null。
                if (isAuth && InteractiveAuthenticator is null)
                {
                    if (document is not null)
                    {
                        RemoveTerminalTab(tab, document);
                    }
                    return null;
                }

                // 认证失败且可交互:标记失败态并循环回去重新弹凭据重试。
                tab.MarkConnectionFailed(LastConnectionError);
                if (isAuth && InteractiveAuthenticator is not null)
                {
                    continue;
                }

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
        profile ??= new()
        {
            Name = entry.Name,
            Host = entry.Host,
            Port = entry.Port,
            Username = entry.Username,
            AuthMethod = AuthMethod.Password
        };
        return await TryConnectProfileAsync(profile, cancellationToken);
    }

    private static string DescribeConnectionError(Exception ex, SessionProfile profile)
    {
        string target = string.IsNullOrWhiteSpace(profile.Host) ? profile.Name : $"{profile.Username}@{profile.Host}:{profile.Port}";
        // Match by type name so VelaShell.App need not reference SSH.NET directly.
        return ex.GetType().Name switch
        {
            "SshAuthenticationException" => $"认证失败：{target} 的用户名、密码或密钥不正确。",
            "SshConnectionException" => $"连接失败：无法与 {target} 建立 SSH 会话。",
            "SocketException" => $"网络错误：无法连接到 {target}，请检查主机与端口。",
            "SshOperationTimeoutException" => $"连接超时：{target} 未响应。",
            "ProxyException" => $"代理错误：无法通过代理连接到 {target}。",
            _ => $"连接 {target} 失败：{ex.Message}"
        };
    }

    private void ConfigureTerminal(ITerminalEmulator emulator,
        AppSettings settings,
        TerminalType terminalType,
        bool forceUtf8 = false)
    {
        if (emulator is VelaTerminalControl control)
        {
            control.TerminalType = terminalType;
        }
        ApplyLiveTerminalSettings(emulator, settings, forceUtf8);
    }

    /// <summary>
    /// The settings that are safe to change on a live session: scrollback depth, font,
    /// font size, host-output encoding plus the full 终端行为/配色 option set. Applied at tab
    /// creation and re-applied to every open tab whenever settings are saved (#3/#15/#21).
    /// </summary>
    private void ApplyLiveTerminalSettings(ITerminalEmulator emulator, AppSettings settings, bool forceUtf8 = false)
    {
        emulator.ScrollbackLines = settings.ScrollbackLines;
        if (emulator is not VelaTerminalControl control)
        {
            return;
        }
        // 本地终端(ConPTY)输出恒为 UTF-8,不套用面向远端主机的编码设置。
        control.SetEncoding(forceUtf8 ? Encoding.UTF8 : ResolveEncoding(settings.TerminalEncoding));
        if (!string.IsNullOrWhiteSpace(settings.TerminalFont))
        {
            control.FontFamily = new($"{settings.TerminalFont}, JetBrains Mono, Cascadia Mono, Consolas, Microsoft YaHei, monospace");
        }
        if (settings.TerminalFontSize > 0)
        {
            control.FontSize = settings.TerminalFontSize;
        }
        TerminalBehaviorOptions behavior = settings.TerminalBehavior;
        control.LineHeight = behavior.LineHeight;
        control.CursorStyle = behavior.CursorStyle;
        control.CursorBlink = behavior.CursorBlink;
        control.BellMode = behavior.BellMode;
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
        control.PaletteOverrides = TerminalAppearanceMapper.BuildPaletteOverrides(settings.Appearance);
    }

    private void OnSettingsSaved(AppSettings settings)
    {
        _latestSettings = settings;

        // SaveSettingsAsync may complete on a thread-pool continuation; font/size touch layout,
        // so marshal onto the UI thread (the main scheduler is the Avalonia dispatcher).
        RxSchedulers.MainThreadScheduler.Schedule(Unit.Default, (_, _) =>
        {
            ApplyLiveSettingsToOpenTabs(settings);

            // 已打开的文件浏览器同步最新的传输选项(冲突策略/并发/带宽等)与
            // “显示隐藏文件”状态(设置审计 C-04:设置中心与工具栏共用一个来源)。
            // 面板按会话缓存后,当前实例与全部缓存实例都要广播到。
            FileBrowser.TransferOptions = settings.Transfer;
            FileBrowser.ShowHiddenFiles = settings.Transfer.ShowHiddenFiles;
            foreach (FileBrowserViewModel browser in _fileBrowserCache.Values)
            {
                browser.TransferOptions = settings.Transfer;
                browser.ShowHiddenFiles = settings.Transfer.ShowHiddenFiles;
            }
            return Disposable.Empty;
        });
    }

    /// <summary>
    /// 文件浏览器工具栏切换“显示隐藏文件”后写回持久化设置(设置审计 C-04),
    /// 使设置中心与工具栏共用 Transfer.ShowHiddenFiles 这一个状态来源。
    /// </summary>
    private void PersistShowHiddenFiles(bool value)
    {
        if (_settingsService is null)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                AppSettings settings = await _settingsService.GetSettingsAsync().ConfigureAwait(false);
                if (settings.Transfer.ShowHiddenFiles == value)
                {
                    return;
                }
                settings.Transfer.ShowHiddenFiles = value;
                await _settingsService.SaveSettingsAsync(settings).ConfigureAwait(false);
            }
            catch
            {
                // 写回失败只影响下次启动的初始值,不打断当前浏览。
            }
        });
    }

    /// <summary>把一份设置应用到所有已打开的终端标签(保存与外观预览共用)。</summary>
    private void ApplyLiveSettingsToOpenTabs(AppSettings settings)
    {
        foreach (TerminalTabViewModel tab in TabBar.Tabs.OfType<TerminalTabViewModel>())
        {
            ApplyLiveTerminalSettings(tab.TerminalEmulator, settings, tab.LocalShell is not null);
        }
    }

    private static Encoding ResolveEncoding(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Encoding.UTF8;
        }
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
        TerminalTabViewModel? tab = ActiveTerminalTab;
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

    private void OnActiveDockableChanged(object? sender, ActiveDockableChangedEventArgs e) => SetActiveFromDockable(e.Dockable);

    private void OnFocusedDockableChanged(object? sender, FocusedDockableChangedEventArgs e) => SetActiveFromDockable(e.Dockable);

    private void SetActiveFromDockable(IDockable? dockable)
    {
        if (dockable is not TerminalDocument document || !TabBar.Tabs.Contains(document.Terminal))
        {
            return;
        }
        ActiveTerminalTab = document.Terminal;
        if (!ReferenceEquals(TabBar.ActiveTab, document.Terminal))
        {
            TabBar.ActiveTab = document.Terminal;
        }
    }

    private void OnDocumentClosed(TerminalDocument document)
    {
        TerminalTabViewModel tab = document.Terminal;
        if (TabBar.Tabs.Contains(tab))
        {
            TabBar.CloseTabCommand.Execute(tab).Subscribe();
        }
        StopSessionLogging(tab);
        CloseSftpForTab(tab);
        tab.Dispose();
        // Dispose 只拆终端传输;底层 SSH 客户端也要断开释放(用户反馈 #2)。
        TeardownSshSession(tab.SessionId);

        // 关闭标签不会再触发 ConnectionStatus 变更(已 Dispose),这里显式把树上的
        // 状态圆点复位;同配置还有其他已连接标签时保持"活跃"。
        if (tab.Profile is not { } profile)
        {
            return;
        }
        bool stillConnected = TabBar.Tabs
                                    .OfType<TerminalTabViewModel>()
                                    .Any(other => !ReferenceEquals(other, tab) && other.Profile?.Id == profile.Id && other.ConnectionStatus == SessionStatus.Connected);
        if (!stillConnected)
        {
            Sidebar.SessionTree?.SetSessionStatus(profile.Id, SessionStatus.Disconnected);
        }
    }

    /// <summary>
    /// Tears down the SFTP channel bound to a closing tab's session and, if the browser is
    /// still showing that session, unbinds and hides it — closing the SSH tab must not leave a live,
    /// operable SFTP panel behind (#22).
    /// </summary>
    private void CloseSftpForTab(TerminalTabViewModel tab)
    {
        if (_sftpService is null || tab.SessionId == Guid.Empty)
        {
            return;
        }
        Guid closedSessionId = tab.SessionId;

        // Evict BEFORE tearing the SFTP channel down so an in-flight listing is cancelled
        // rather than racing the client disposal (SSH.NET NREs from inside ListDirectory
        // otherwise).
        EvictFileBrowser(closedSessionId);
        _ = _sftpService.CloseSessionAsync(closedSessionId);
    }
}
