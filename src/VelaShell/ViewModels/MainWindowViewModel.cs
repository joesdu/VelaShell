using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Avalonia;
using Avalonia.Threading;
using ReactiveUI;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Recording;
using VelaShell.Core.Resources;
using VelaShell.Core.Services;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Core.Tunnels;
using VelaShell.Docking;
using VelaShell.Docking.Model;
using VelaShell.Infrastructure.Pty;
using VelaShell.Presentation.Commands;
using VelaShell.Presentation.Services;
using VelaShell.Presentation.ViewModels;
using VelaShell.Services;
using VelaShell.Services.ZModem;
using VelaShell.Terminal;
using VelaShell.Terminal.Emulation;
using VelaShell.Terminal.Rendering;

namespace VelaShell.ViewModels;

/// <summary>
/// 主窗口视图模型:应用外壳的中枢,统筹终端标签、SSH/本地会话生命周期、停靠工作区、
/// 侧边栏、状态栏、命令面板、SFTP 文件面板与隧道面板,并串联设置、连接工作流与各项服务。
/// </summary>
public class MainWindowViewModel : ReactiveObject
{
    /// <summary>
    /// bash 提示符补行脚本(内置,静默注入):命令输出末尾无换行时,经 DSR(ESC[6n)
    /// 查询光标列,不在行首则先补一个换行再画提示符(zsh 的默认行为)。
    /// </summary>
    private const string PromptNewlineFix =
        """prompt_nl() { local c; IFS='[;' read -p $'\e[6n' -d R -rs _ _ c; ((c>1)) && echo; }; PROMPT_COMMAND=prompt_nl""";

    /// <summary>RIS(ESC c)完全重置序列:重开会话前清掉旧进程的残留缓冲。</summary>
    private static readonly byte[] RisResetSequence = [0x1B, (byte)'c']; // ESC c

    private readonly IConnectionWorkflowService? _connectionWorkflowService;
    private readonly ISessionMetricsService? _metricsService;

    // ---- 会话日志(设置 → 常规 → 数据与存储) ----

    private readonly Dictionary<TerminalTabViewModel, SessionLogWriter> _sessionLogs = [];

    // ---- 会话录制(设置 → 安全审计 → 会话录制) ----

    private readonly Dictionary<TerminalTabViewModel, SessionRecorder> _sessionRecorders = [];
    private readonly ISessionRecordingStore? _recordingStore;

    private readonly IAppDataStore? _appDataStore;
    private readonly ISessionRepository? _sessionRepository;
    private readonly ISettingsService? _settingsService;
    private readonly ISftpService? _sftpService;
    private readonly ISshConnectionService? _sshConnectionService;
    private readonly Func<ITerminalEmulator> _terminalEmulatorFactory;
    private readonly ITunnelService? _tunnelService;
    private readonly QuickCommandsViewModel? _quickCommands;
    private readonly QuickCommandRunnerViewModel? _quickCommandRunner;
    private readonly TerminalTargetSelectorViewModel _terminalTargetSelector;
    private readonly Dictionary<
        TerminalTabViewModel,
        IDisposable
    > _quickCommandTargetSubscriptions = [];

    /// <summary>同步输入频道的对等转发中枢(标签右键菜单 → 同步输入)。</summary>
    private readonly SyncInputCoordinator _syncInput = new();

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
    private AppState _appState = new();
    private bool _isApplyingSidebarState;
    private CancellationTokenSource? _sidebarStateSaveDebounce;

    /// <summary>
    /// SFTP 面板的用户开关意图(全局,跨标签)。面板对象随标签切换/会话驱逐被整体
    /// 替换,可见性不能从上一个对象“抄”过来(本地终端占位是隐藏的,会把 false 传染
    /// 给下一个远程标签);统一以本字段为准恢复。
    /// </summary>
    private bool _fileBrowserOpenIntent = true;
    private Dictionary<Guid, string> _paletteGroupNames = [];

    // ---- 命令面板的全量会话(§12.3:面板作为中枢,收录全部已保存配置) ----

    private IReadOnlyList<SessionProfile> _paletteProfiles = [];
    private SidebarViewModel _sidebar;
    private StatusBarViewModel _statusBar;
    private bool _statusMetricsPolling;

    // ---- Status-bar live metrics (spec §7: cpu / memory / net for the active session) ----

    private DispatcherTimer? _statusMetricsTimer;
    private DispatcherTimer? _fontSizePersistDebounce;
    private int _pendingFontSize;
    private TabBarViewModel _tabBar;

    /// <summary>
    /// 用可选注入的各项服务构造主窗口视图模型:装配命令补全、停靠工作区、侧边栏/标签栏/状态栏、
    /// SFTP 面板与命令注册,并订阅设置保存、外观预览、安全告警等事件、启动状态栏指标轮询。
    /// 无 UI 的单元测试可全部传 null 构造。
    /// </summary>
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
        IAppDataStore? appDataStore = null,
        ISessionRecordingStore? recordingStore = null,
        QuickCommandsViewModel? quickCommands = null,
        IQuickCommandRepository? quickCommandRepository = null
    )
    {
        _appDataStore = appDataStore;
        _recordingStore = recordingStore;
        _quickCommands = quickCommands;
        _terminalTargetSelector = new();
        _quickCommandRunner = quickCommands is null
            ? null
            : new(quickCommands, _terminalTargetSelector);
        _quickCommandRunner?.ExecutionRequested += OnQuickCommandExecutionRequested;

        // 命令补全(plan.md #16):全局命令历史 + 建议提供器(历史 ∪ 快捷命令),
        // 逐标签在 CreateConnectingTab 注入。
        CommandHistory = new(appDataStore);
        _suggestionProvider = new(CommandHistory, quickCommandRepository);
        _connectionWorkflowService = connectionWorkflowService;
        _sshConnectionService = sshConnectionService;
        _settingsService = settingsService;
        _sessionRepository = sessionRepository;
        _sftpService = sftpService;
        _tunnelService = tunnelService;
        _metricsService = metricsService;
        _terminalEmulatorFactory = terminalEmulatorFactory ?? (() => new VelaTerminalControl());
        Layout = new DockWorkspace();
        Layout.DocumentClosed += document =>
        {
            if (document is TerminalDocument terminalDocument)
            {
                OnDocumentClosed(terminalDocument);
            }
        };
        Layout.ActiveDocumentChanged += SetActiveFromDocument;
        _sidebar = new(recentConnectionService, _quickCommandRunner);
        _sidebar.PropertyChanged += OnSidebarStateChanged;
        if (sessionRepository is not null)
        {
            _sidebar.SessionTree = new(sessionRepository);
        }
        _tabBar = new();
        _tabBar.Tabs.CollectionChanged += OnTabsCollectionChanged;
        _statusBar = new();
        _fileBrowser = new(null, Guid.Empty);
        _fileTransfer = new(transferManager);
        _tabBar
            .WhenAnyValue(tabBar => tabBar.ActiveTab)
            .Subscribe(activeTab =>
            {
                ActiveTerminalTab = activeTab as TerminalTabViewModel;
                activeTab?.HasBellAlert = false; // 切换到该标签即清除 Bell 提醒
                RebindFileBrowser();
                SyncWorkspaceToActiveTab(activeTab as TerminalTabViewModel);
                RefreshQuickCommandTargets();
                RevealActiveSessionInSidebar(activeTab as TerminalTabViewModel);
            });

        // SFTP 面板“打开/关闭”的用户意图:只跟踪当前面板实例上的 IsVisible 变化
        // (工具栏切换、面板关闭按钮、连接后自动展开)。对象整体替换(切标签重绑、
        // 驱逐后的占位)属于程序行为,Skip(1) 跳过替换瞬间的初值,不污染意图。
        // RebindFileBrowser 以该意图恢复展示,而不是抄上一个面板对象的可见性。
        this.WhenAnyValue(x => x.FileBrowser)
            .Select(browser => browser.WhenAnyValue(b => b.IsVisible).Skip(1))
            .Switch()
            .Subscribe(visible => _fileBrowserOpenIntent = visible);

        // Keep the status bar in sync with the active tab: refresh when the active tab changes,
        // and when that tab's own connection state / latency changes.
        this.WhenAnyValue(x => x.ActiveTerminalTab)
            .Select(tab =>
                tab is null
                    ? Observable.Return(Unit.Default)
                    : tab.WhenAnyValue(t => t.ConnectionStatus, t => t.Latency)
                        .Select(_ => Unit.Default)
            )
            .Switch()
            .Subscribe(_ => UpdateStatusBarForActiveTab());

        this.WhenAnyValue(x => x.ActiveTerminalTab)
            .Select(tab =>
                tab is null
                    ? Observable.Return(Unit.Default)
                    : tab.WhenAnyValue(t => t.IsConnected).Select(_ => Unit.Default)
            )
            .Switch()
            .Subscribe(_ => this.RaisePropertyChanged(nameof(CanToggleFileBrowser)));

        // Saved settings re-apply to every open terminal immediately (#3/#15/#21) — scrollback,
        // font, size and encoding change live; TERM stays per-session (negotiated at connect).
        _settingsService?.SettingsSaved += OnSettingsSaved;

        // 外观即时预览(设置窗口广播,未持久化):只重刷已打开标签的终端外观,
        // 不动 _latestSettings(新建标签仍用已保存的设置)。
        settingsPreviewService?.PreviewRequested += settings =>
            RxSchedulers.MainThreadScheduler.Schedule(
                Unit.Default,
                (_, _) =>
                {
                    ApplyLiveSettingsToOpenTabs(settings);
                    return Disposable.Empty;
                }
            );

        // 安全告警(设置 → 安全审计 → 告警通道):应用内 → 状态栏;提示音 → 系统提示音。
        securityAlertService?.Alerted += notice =>
            RxSchedulers.MainThreadScheduler.Schedule(
                Unit.Default,
                (_, _) =>
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
                }
            );
        StartStatusMetricsPolling();
        OpenSettingsCommand = ReactiveCommand.Create(() =>
            SettingsRequested?.Invoke(this, EventArgs.Empty)
        );
        CommandPalette = new(BuildPaletteItems);
        OpenCommandPaletteCommand = ReactiveCommand.Create(() => CommandPalette.Open());
        IObservable<bool> canToggleFileBrowser = this.WhenAnyValue(x => x.ActiveTerminalTab)
            .Select(tab =>
                tab is null
                    ? Observable.Return(false)
                    : tab.WhenAnyValue(t => t.IsConnected).Select(_ => CanToggleFileBrowser)
            )
            .Switch();
        ToggleFileBrowserCommand = ReactiveCommand.Create(ToggleFileBrowser, canToggleFileBrowser);
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

    /// <summary>隧道面板当前是否展开显示。</summary>
    public bool IsTunnelPanelOpen
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>The self-drawn terminal control of the active tab, when it is one.</summary>
    private VelaTerminalControl? ActiveTerminalControl =>
        ActiveTerminalTab?.TerminalEmulator.Control as VelaTerminalControl;

    /// <summary>The Ctrl+P / Ctrl+K command palette overlay.</summary>
    public CommandPaletteViewModel CommandPalette { get; }

    /// <summary>打开命令面板(Ctrl+P / Ctrl+K)的命令。</summary>
    public ReactiveCommand<Unit, Unit> OpenCommandPaletteCommand { get; }

    /// <summary>显示或隐藏当前 SSH 会话的远程文件面板。</summary>
    public ReactiveCommand<Unit, Unit> ToggleFileBrowserCommand { get; }

    /// <summary>当前活动标签是否支持打开远程文件面板。</summary>
    public bool CanToggleFileBrowser =>
        _sftpService is not null
        && ActiveTerminalTab is { IsConnected: true, Profile: not null } tab
        && tab.SessionId != Guid.Empty;

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

    /// <summary>
    /// 窗口注入的 ZMODEM 下载目录选择委托(视图层实现,独占 StorageProvider)。
    /// 分发给每个新建的终端标签,供其 ZMODEM 接收时弹出保存目录选择框。
    /// </summary>
    public Func<ZModemFolderPromptRequest, CancellationToken, Task<string?>>? ZModemDownloadFolderPicker { get; set; }

    /// <summary>
    /// 窗口注入的 ZMODEM 上传文件选择委托(视图层实现,独占 StorageProvider)。
    /// 分发给每个新建的终端标签,供远端 <c>rz</c> 时弹出多选文件框。
    /// </summary>
    public Func<bool, CancellationToken, Task<IReadOnlyList<string>>>? ZModemUploadFilePicker { get; set; }

    /// <summary>
    /// 为新建的终端标签注入 ZMODEM 传输所需的依赖:下载目录选择委托、上传文件选择委托、
    /// 共享传输面板与设置读取委托。前者 + 面板 + 设置就绪时 AttachTransport 才会启用 ZMODEM 路由器。
    /// </summary>
    private void WireZModemDownload(TerminalTabViewModel terminalTab)
    {
        terminalTab.ZModemDownloadFolderPicker = ZModemDownloadFolderPicker;
        terminalTab.ZModemUploadFilePicker = ZModemUploadFilePicker;
        terminalTab.FileTransfer = _fileTransfer;
        if (_settingsService is { } settings)
        {
            terminalTab.GetSettingsAsync = settings.GetSettingsAsync;
        }
    }

    /// <summary>左侧边栏视图模型:资源管理器会话树与最近连接。</summary>
    public SidebarViewModel Sidebar
    {
        get => _sidebar;
        set => this.RaiseAndSetIfChanged(ref _sidebar, value);
    }

    /// <summary>标签栏视图模型:管理终端标签的集合与激活项。</summary>
    public TabBarViewModel TabBar
    {
        get => _tabBar;
        set => this.RaiseAndSetIfChanged(ref _tabBar, value);
    }

    /// <summary>底部状态栏视图模型:连接状态、延迟、窗口尺寸与会话资源指标。</summary>
    public StatusBarViewModel StatusBar
    {
        get => _statusBar;
        set => this.RaiseAndSetIfChanged(ref _statusBar, value);
    }

    /// <summary>当前激活的终端标签;无活动标签时为 null。</summary>
    public TerminalTabViewModel? ActiveTerminalTab
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>当前是否存在活动的终端标签。</summary>
    public bool HasActiveTerminalTab => ActiveTerminalTab is not null;

    /// <summary>自研 VelaDock 工作区:承载终端文档(标签可拖拽重排、拆分分屏)。</summary>
    public DockWorkspace Layout { get; }

    /// <summary>当前会话的 SFTP 文件浏览面板(按会话缓存,随活动标签切换重绑)。</summary>
    public FileBrowserViewModel FileBrowser
    {
        get => _fileBrowser;
        set => this.RaiseAndSetIfChanged(ref _fileBrowser, value);
    }

    /// <summary>文件传输面板视图模型:承载上传/下载任务队列与进度。</summary>
    public FileTransferViewModel FileTransfer
    {
        get => _fileTransfer;
        set => this.RaiseAndSetIfChanged(ref _fileTransfer, value);
    }

    /// <summary>打开设置窗口的命令(Ctrl+, / 菜单 / 侧边栏齿轮)。</summary>
    public ReactiveCommand<Unit, Unit> OpenSettingsCommand { get; }

    private void RegisterCommands()
    {
        Commands.Register(
            new(
                "session.new",
                Strings.Get("Cmd_NewSshConnection"),
                Strings.Get("CmdCat_Session"),
                () => NewConnectionRequested?.Invoke(this, EventArgs.Empty),
                Shortcut: "Ctrl+N",
                Icon: "Icon.plus"
            )
        );
        Commands.Register(
            new(
                "session.close",
                Strings.Get("Cmd_CloseCurrentSession"),
                Strings.Get("CmdCat_Session"),
                () => TabBar.CloseActiveTabCommand.Execute().Subscribe(),
                () => TabBar.ActiveTab is not null,
                "Ctrl+W"
            )
        );
        Commands.Register(
            new(
                "session.reconnect",
                Strings.Get("Cmd_Reconnect"),
                Strings.Get("CmdCat_Actions"),
                () =>
                {
                    if (ActiveTerminalTab is { } tab)
                    {
                        _ = ReconnectTabAsync(tab);
                    }
                },
                () => ActiveTerminalTab?.ConnectionStatus == SessionStatus.Disconnected,
                "Ctrl+R"
            )
        );
        Commands.Register(
            new(
                "session.clone",
                Strings.Get("Cmd_CloneSession"),
                Strings.Get("CmdCat_Session"),
                () =>
                {
                    if (ActiveTerminalTab?.Profile is { } profile)
                    {
                        _ = TryConnectProfileAsync(profile);
                    }
                },
                () => ActiveTerminalTab?.Profile is not null,
                "Ctrl+Shift+N",
                "Icon.copy"
            )
        );
        Commands.Register(
            new(
                "edit.copy",
                Strings.Get("Copy"),
                Strings.Get("CmdCat_Edit"),
                () =>
                {
                    if (ActiveTerminalControl is { } c)
                    {
                        _ = c.CopyAsync();
                    }
                },
                () => ActiveTerminalControl is not null,
                "Ctrl+Shift+C",
                "Icon.copy"
            )
        );
        Commands.Register(
            new(
                "edit.paste",
                Strings.Get("Cmd_Paste"),
                Strings.Get("CmdCat_Edit"),
                () =>
                {
                    if (ActiveTerminalControl is { } c)
                    {
                        _ = c.PasteAsync();
                    }
                },
                () => ActiveTerminalControl is not null,
                "Ctrl+Shift+V"
            )
        );
        Commands.Register(
            new(
                "terminal.export",
                Strings.Get("Cmd_ExportTerminalOutput"),
                Strings.Get("CmdCat_Session"),
                () => ExportBufferRequested?.Invoke(this, EventArgs.Empty),
                () => ActiveTerminalControl is not null,
                Icon: "Icon.save"
            )
        );
        Commands.Register(
            new(
                "search.terminal",
                Strings.Get("Cmd_FindInTerminal"),
                Strings.Get("CmdCat_Search"),
                () => TerminalSearchRequested?.Invoke(this, EventArgs.Empty),
                () => ActiveTerminalTab is not null,
                "Ctrl+F",
                "Icon.search"
            )
        );
        // 隧道独立于终端会话(后台自动连接),无活动标签也可用(用户反馈 #5)。
        Commands.Register(
            new(
                "tools.tunnel",
                Strings.Get("Cmd_TunnelManager"),
                Strings.Get("CmdCat_Tools"),
                ToggleTunnelPanel,
                Shortcut: "Ctrl+Shift+T",
                Icon: "Icon.route"
            )
        );
        Commands.Register(
            new(
                "tools.files",
                Strings.Get("Cmd_SftpFileManager"),
                Strings.Get("CmdCat_Tools"),
                () => ToggleFileBrowserCommand.Execute().Subscribe(),
                () => CanToggleFileBrowser,
                "Ctrl+Shift+F",
                "Icon.folder"
            )
        );
        Commands.Register(
            new(
                "tools.diagnostics",
                Strings.Get("Cmd_ConnectionDiagnostics"),
                Strings.Get("CmdCat_Tools"),
                () =>
                {
                    if (ActiveTerminalTab?.Profile is { } profile)
                    {
                        DiagnosticsRequested?.Invoke(profile);
                    }
                },
                () => ActiveTerminalTab?.Profile is not null,
                Icon: "Icon.stethoscope"
            )
        );
        Commands.Register(
            new(
                "edit.clear",
                Strings.Get("Cmd_ClearScreen"),
                Strings.Get("CmdCat_Edit"),
                () => ActiveTerminalTab?.TerminalEmulator.WriteInput([0x0C]),
                () => ActiveTerminalTab?.ConnectionStatus == SessionStatus.Connected
            )
        );
        Commands.Register(
            new(
                "terminal.linegutter",
                Strings.Get("Cmd_ToggleLineGutter"),
                Strings.Get("CmdCat_Edit"),
                ToggleLineGutter,
                Shortcut: "Ctrl+Shift+L"
            )
        );
        Commands.Register(
            new(
                "app.settings",
                Strings.Get("Cmd_OpenSettings"),
                Strings.Get("CmdCat_Edit"),
                () => OpenSettingsCommand.Execute().Subscribe(),
                Shortcut: "Ctrl+,",
                Icon: "Icon.settings"
            )
        );
        Commands.Register(
            new(
                "app.palette",
                Strings.Get("Cmd_CommandPalette"),
                Strings.Get("CmdCat_Search"),
                () => CommandPalette.Open(),
                Shortcut: "Ctrl+P",
                Icon: "Icon.zap"
            )
        );

        // 分屏(标题栏分屏按钮与命令面板共用;右键标签菜单另有直达入口)。
        Commands.Register(
            new(
                "split.horizontal",
                Strings.Get("Dock_SplitHorizontal"),
                Strings.Get("CmdCat_Actions"),
                () =>
                {
                    if (Layout.ActiveDocument is { } document)
                    {
                        Layout.SplitDocument(document, DockOrientation.Horizontal);
                    }
                },
                () => Layout.ActiveDocument is not null,
                Icon: "Icon.columns-2"
            )
        );
        Commands.Register(
            new(
                "split.vertical",
                Strings.Get("Dock_SplitVertical"),
                Strings.Get("CmdCat_Actions"),
                () =>
                {
                    if (Layout.ActiveDocument is { } document)
                    {
                        Layout.SplitDocument(document, DockOrientation.Vertical);
                    }
                },
                () => Layout.ActiveDocument is not null,
                Icon: "Icon.rows-2"
            )
        );
        // 本地终端(§12 P1-1):按本机安装情况动态注册 PowerShell/CMD/WSL/Git Bash 入口。
        foreach (LocalShellInfo shell in LocalShellCatalog.DetectShells())
        {
            LocalShellInfo captured = shell;
            Commands.Register(
                new(
                    $"local.{captured.Id}",
                    Strings.Format("Cmd_OpenLocalTerminal", captured.Name),
                    Strings.Get("CmdCat_Session"),
                    () => _ = OpenLocalTerminalAsync(captured),
                    Icon: "Icon.terminal"
                )
            );
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
            ConnectionSummary = Strings.Format("Msg_LocalPrefix", shell.Name),
            TerminalTypeName = TerminalType.XtermColor256.ToTermName(),
            EncodingName = "UTF-8",
            LocalShell = shell,
        };
        terminalTab.ReconnectRequested += (_, _) => _ = ReconnectTabAsync(terminalTab);
        terminalTab.Disconnected += (_, _) => OnTabDisconnected(terminalTab);

        // 命令补全:注入建议提供器;提交(已回显校验)的命令进全局历史。
        terminalTab.SuggestionProvider = _suggestionProvider;
        WireZModemDownload(terminalTab);
        terminalTab.CommandLineSubmitted += CommandHistory.Record;
        if (terminalEmulator is VelaTerminalControl bellSource)
        {
            bellSource.BellRang += () =>
            {
                if (
                    _latestSettings?.TerminalBehavior.TabFlashAlert != false
                    && !ReferenceEquals(ActiveTerminalTab, terminalTab)
                )
                {
                    terminalTab.HasBellAlert = true;
                }
            };
        }
        var document = new TerminalDocument(terminalTab);
        TabBar.AddTab(terminalTab);
        ActiveTerminalTab = terminalTab;
        Layout.AddDocument(document);
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
            LastConnectionError = Strings.Format(
                "Msg_LocalShellStartFailed",
                shell.Name,
                ex.Message
            );
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
            LastConnectionError = Strings.Format(
                "Msg_LocalShellReopenFailed",
                shell.Name,
                ex.Message
            );
            StatusBar.Status = LastConnectionError;
        }
    }

    /// <summary>
    /// 拉起本地 shell 进程并挂上标签(打开与重开共用)。
    /// </summary>
    [SupportedOSPlatform(nameof(OSPlatform.Windows))]
    private void AttachLocalShell(
        TerminalTabViewModel tab,
        LocalShellInfo shell,
        AppSettings settings
    )
    {
        var stream = ConPtyShellStream.Start(
            shell.CommandLine,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            tab.TerminalEmulator.Columns,
            tab.TerminalEmulator.Rows
        );
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
        if (tab is null)
        {
            return;
        }

        // 本地终端(ConPTY)没有 SFTP 会话:不得继续展示上一个 SSH 会话的文件面板
        // (用户反馈:切到 PowerShell 标签后下方仍显示 r2s 的文件)。换成隐藏的空占位;
        // 上一个面板不 Detach(仍按其会话缓存),打开意图保留在 _fileBrowserOpenIntent,
        // 切回远程标签时恢复展示。
        if (tab.LocalShell is not null)
        {
            if (FileBrowser.SessionId != Guid.Empty || FileBrowser.IsVisible)
            {
                FileBrowser = new(_sftpService, Guid.Empty) { TransferSink = FileTransfer };
            }
            return;
        }
        if (tab.SessionId == Guid.Empty)
        {
            return;
        }
        if (FileBrowser.SessionId == tab.SessionId)
        {
            return;
        }

        // Restore the user's open/closed intent so switching to (or connecting) a tab
        // never silently hides a panel the user had opened.
        bool wasVisible = _fileBrowserOpenIntent;
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
            ? string.IsNullOrWhiteSpace(profile.Name)
                ? profile.Host
                : profile.Name
            : tab.Title;
        var browser = new FileBrowserViewModel(_sftpService, tab.SessionId)
        {
            TransferSink = FileTransfer,
            IsVisible = wasVisible,
            GetDefaultEditorPath = QueryDefaultEditorPathAsync,
            TransferOptions = _latestSettings?.Transfer ?? new TransferOptions(),
            ShowHiddenFiles = _latestSettings?.Transfer.ShowHiddenFiles ?? false,
            ShowHiddenFilesToggled = PersistShowHiddenFiles,

            // 列显示先按设置铺好,回调后挂:对象初始化器按书写顺序赋值,
            // 反过来会让这几行“初始化”被当成用户切换而回写一遍设置。
            ShowSizeColumn = _latestSettings?.Transfer.ShowSizeColumn ?? true,
            ShowPermissionsColumn = _latestSettings?.Transfer.ShowPermissionsColumn ?? true,
            ShowOwnerColumn = _latestSettings?.Transfer.ShowOwnerColumn ?? true,
            ShowGroupColumn = _latestSettings?.Transfer.ShowGroupColumn ?? true,
            ShowTypeColumn = _latestSettings?.Transfer.ShowTypeColumn ?? true,
            ShowModifiedColumn = _latestSettings?.Transfer.ShowModifiedColumn ?? true,
            ColumnVisibilityToggled = PersistColumnVisibility,
            ServerDisplayName = serverName,
            AccentBrush = tab.Profile is { } p ? ConnectionAccent.BrushFor(p.Id) : null,
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

        // 会话已死,面板收起(空面板没有可看内容);用户的打开意图保留在
        // _fileBrowserOpenIntent(对象替换不触发意图跟踪),切到/重连任一
        // 远程会话时由 RebindFileBrowser 恢复展示,不会被这里的隐藏传染。
        FileBrowser = new(_sftpService, Guid.Empty) { TransferSink = FileTransfer };
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
        if (!CanToggleFileBrowser)
        {
            return;
        }
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
    /// Called once a session finishes connecting: binds the file browser to it and restores
    /// the user's runtime-wide open/closed intent. A fresh app run defaults to visible; once the
    /// user hides it, tab switches and reconnects must not force it open again.
    /// </summary>
    private void ShowFileBrowserForActiveSession()
    {
        RebindFileBrowser();
        if (_sftpService is null || FileBrowser.SessionId == Guid.Empty)
        {
            return;
        }
        FileBrowser.IsVisible = _fileBrowserOpenIntent;
        if (FileBrowser.IsVisible)
        {
            RefreshOrLoadFileBrowser();
        }
    }

    /// <summary>
    /// Raised when the user asks for in-terminal search via menu/palette; the window
    /// forwards it to the active terminal view's search bar (§5.3).
    /// </summary>
    public event EventHandler? TerminalSearchRequested;

    /// <summary>请求视图聚焦当前活动终端。</summary>
    public event EventHandler? TerminalFocusRequested;

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
        string safeTitle = string.Concat(
            tab.Title.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_')
        );
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
            var panel = new TunnelPanelViewModel(
                _tunnelService,
                servers,
                ConnectTunnelHostAsync,
                id => _sshConnectionService?.GetClient(id)?.IsConnected == true,
                id => _connectionWorkflowService?.DisconnectAsync(id) ?? Task.CompletedTask,
                _appDataStore
            );
            panel.CloseRequested += (_, _) => IsTunnelPanelOpen = false;
            TunnelPanel = panel;
        }
        _ = TunnelPanel.OpenAsync(preselect?.Id ?? ActiveTerminalTab?.Profile?.Id);
        IsTunnelPanelOpen = true;
    }

    /// <summary>为隧道面板后台建立 SSH 连接:不开终端标签,凭据缺失时走登录验证弹窗。</summary>
    private async Task<Guid> ConnectTunnelHostAsync(
        SessionProfile profile,
        CancellationToken cancellationToken
    )
    {
        if (_connectionWorkflowService is null)
        {
            throw new InvalidOperationException(Strings.Get("Msg_SshServiceNotConfigured"));
        }
        SessionProfile current = profile;
        if (RequiresCredentials(current))
        {
            SessionProfile? updated = InteractiveAuthenticator is { } prompt
                ? await prompt(current)
                : null;
            current =
                updated
                ?? throw new InvalidOperationException(Strings.Get("Msg_AuthPromptCancelled"));
        }
        SshSession session = await _connectionWorkflowService.ConnectProfileAsync(
            current,
            cancellationToken
        );
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
        _statusMetricsTimer = new(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            (_, _) =>
            {
                _ = PollStatusMetricsAsync();
                _ = PollLatencyAsync();
            }
        );
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
            tab.Latency =
                reply.Status == IPStatus.Success
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
        if (
            tab is null
            || tab.SessionId == Guid.Empty
            || tab.ConnectionStatus != SessionStatus.Connected
        )
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
            StatusBar.UpdateNetwork(
                metrics.NetRxBytesPerSec,
                metrics.NetTxBytesPerSec,
                metrics.HasNetRates
            );

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
        sb.Append(Strings.Format("Msg_CpuTooltipTotal", m.CpuPercent, m.CpuCores));
        if (m.CorePercents is { Count: > 0 } percents)
        {
            string corePrefix = Strings.Get("Msg_CpuCorePrefix");
            for (int i = 0; i < percents.Count; i++)
            {
                string name =
                    i < m.CoreCounters.Count
                        ? m.CoreCounters[i].Name.Replace("cpu", corePrefix)
                        : $"{corePrefix}{i}";
                sb.Append('\n').Append($"{name}: {percents[i]:F0}%");
            }
        }
        else if (m.CoreCounters.Count > 0)
        {
            sb.Append('\n').Append(Strings.Get("Msg_PerCoreCollecting"));
        }
        return sb.ToString();
    }

    private static string BuildMemTooltip(SessionMetrics m)
    {
        var sb = new StringBuilder();
        sb.Append(
            Strings.Format(
                "Msg_MemTooltip",
                FormatGb(m.MemUsedBytes),
                FormatGb(m.MemTotalBytes),
                m.MemPercent
            )
        );
        if (m.SwapTotalBytes > 0)
        {
            sb.Append('\n')
                .Append(
                    Strings.Format(
                        "Msg_SwapTooltip",
                        FormatGb(m.SwapUsedBytes),
                        FormatGb(m.SwapTotalBytes),
                        m.SwapPercent
                    )
                );
        }
        return sb.ToString();
    }

    private static string BuildDiskTooltip(SessionMetrics m)
    {
        if (m.Disks.Count == 0)
        {
            return m.DiskTotalBytes > 0
                ? Strings.Format(
                    "Msg_DiskRootTooltip",
                    FormatGb(m.DiskUsedBytes),
                    FormatGb(m.DiskTotalBytes),
                    m.DiskPercent
                )
                : Strings.Get("Msg_Disk");
        }
        var sb = new StringBuilder(Strings.Get("Msg_DiskUsage"));
        foreach (DiskUsage d in m.Disks)
        {
            sb.Append('\n')
                .Append(
                    Strings.Format(
                        "Msg_DiskMountLine",
                        d.MountPoint,
                        FormatGb(d.UsedBytes),
                        FormatGb(d.TotalBytes),
                        d.Percent
                    )
                );
        }
        return sb.ToString();
    }

    private static string BuildNetTooltip(SessionMetrics m)
    {
        var sb = new StringBuilder();
        sb.Append(
            m.HasNetRates
                ? Strings.Format(
                    "Msg_NetTooltipTotal",
                    StatusBarViewModel.FormatRate(m.NetRxBytesPerSec),
                    StatusBarViewModel.FormatRate(m.NetTxBytesPerSec)
                )
                : Strings.Get("Msg_NetCollecting")
        );
        if (m.NicRates is not { Count: > 0 } rates)
        {
            return sb.ToString();
        }
        foreach (NetInterfaceRate r in rates)
        {
            sb.Append('\n')
                .Append(
                    $"{r.Name}: ↓ {StatusBarViewModel.FormatRate(r.RxBytesPerSec)}  ↑ {StatusBarViewModel.FormatRate(r.TxBytesPerSec)}"
                );
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
        if (_quickCommands is not null)
        {
            await _quickCommands.LoadAsync();
        }
        if (_settingsService is not null)
        {
            _appState = await _settingsService.GetStateAsync();
            ApplySidebarState(_appState);
            ApplyShellPreferences(await LoadSettingsSnapshotAsync());
        }
        await Sidebar.RecentConnections.RefreshAsync();
        await RefreshSessionTreeAsync();
        RevealActiveSessionInSidebar();
    }

    private void ApplyShellPreferences(AppSettings settings)
    {
        Sidebar.IsQuickCommandsVisible =
            _quickCommandRunner is not null && settings.Appearance.ShowQuickCommandsPanel;
    }

    private void ApplySidebarState(AppState state)
    {
        _isApplyingSidebarState = true;
        try
        {
            Sidebar.QuickCommandsExpanded = state.SidebarQuickCommandsExpanded;
            Sidebar.QuickCommandsHeight = NormalizeSidebarHeight(
                state.SidebarQuickCommandsHeight,
                160
            );
            Sidebar.RecentConnectionsExpanded = state.SidebarRecentConnectionsExpanded;
            Sidebar.RecentConnectionsHeight = NormalizeSidebarHeight(
                state.SidebarRecentConnectionsHeight,
                180
            );
        }
        finally
        {
            _isApplyingSidebarState = false;
        }
        CaptureSidebarState();
    }

    private static double NormalizeSidebarHeight(double height, double fallback) =>
        double.IsFinite(height) ? Math.Clamp(height, 100, 1200) : fallback;

    private void OnSidebarStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (
            _isApplyingSidebarState
            || _settingsService is null
            || e.PropertyName
                is not (
                    nameof(SidebarViewModel.QuickCommandsExpanded)
                    or nameof(SidebarViewModel.QuickCommandsHeight)
                    or nameof(SidebarViewModel.RecentConnectionsExpanded)
                    or nameof(SidebarViewModel.RecentConnectionsHeight)
                )
        )
        {
            return;
        }
        CaptureSidebarState();
        CancellationTokenSource next = new();
        _sidebarStateSaveDebounce?.Cancel();
        _sidebarStateSaveDebounce = next;
        _ = SaveSidebarStateAfterDelayAsync(next.Token);
    }

    private void CaptureSidebarState()
    {
        _appState.SidebarQuickCommandsExpanded = Sidebar.QuickCommandsExpanded;
        _appState.SidebarQuickCommandsHeight = NormalizeSidebarHeight(
            Sidebar.QuickCommandsHeight,
            160
        );
        _appState.SidebarRecentConnectionsExpanded = Sidebar.RecentConnectionsExpanded;
        _appState.SidebarRecentConnectionsHeight = NormalizeSidebarHeight(
            Sidebar.RecentConnectionsHeight,
            180
        );
    }

    private async Task SaveSidebarStateAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            if (_settingsService is not null)
            {
                await _settingsService.SaveStateAsync(_appState).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 被更晚的折叠或拖动结果替代。
        }
        catch
        {
            // 布局状态保存失败不影响当前交互;关闭窗口时还会再尝试一次。
        }
    }

    internal async Task PersistSidebarStateAsync()
    {
        _sidebarStateSaveDebounce?.Cancel();
        CaptureSidebarState();
        if (_settingsService is not null)
        {
            await _settingsService.SaveStateAsync(_appState).ConfigureAwait(false);
        }
    }

    private void RevealActiveSessionInSidebar(TerminalTabViewModel? tab = null)
    {
        if ((_latestSettings?.General.FollowActiveTerminalInExplorer ?? true) != true)
        {
            return;
        }
        TerminalTabViewModel? target = tab ?? ActiveTerminalTab;
        if (target?.Profile is { Id: var profileId } && profileId != Guid.Empty)
        {
            Sidebar.SessionTree?.SelectSession(profileId);
        }
    }

    private void OnTabsCollectionChanged(
        object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e
    )
    {
        var currentTabs = TabBar.Tabs.OfType<TerminalTabViewModel>().ToHashSet();
        foreach (
            TerminalTabViewModel removed in _quickCommandTargetSubscriptions
                .Keys.Where(tab => !currentTabs.Contains(tab))
                .ToArray()
        )
        {
            _quickCommandTargetSubscriptions.Remove(removed, out IDisposable? subscription);
            subscription?.Dispose();
            _syncInput.Detach(removed);
        }
        foreach (TerminalTabViewModel added in currentTabs)
        {
            _syncInput.Attach(added);
            if (_quickCommandTargetSubscriptions.ContainsKey(added))
            {
                continue;
            }
            _quickCommandTargetSubscriptions[added] = added
                // ConnectionStatus raises before TerminalTabViewModel updates IsConnected.
                // Observe IsConnected itself so the refresh sees the final, usable state.
                .WhenAnyValue(tab => tab.IsConnected, tab => tab.Title)
                .Subscribe(_ => RefreshQuickCommandTargets());
        }
        RefreshQuickCommandTargets();
    }

    /// <summary>
    /// 重算某配置在会话树里的同步输入频道字母:该配置可能开着多个标签(复制会话),
    /// 取其中第一个已加入频道的;全部不在频道时上报空串清除标识。
    /// </summary>
    private void RefreshSessionSyncChannel(Guid profileId)
    {
        SyncInputChannel? channel = TabBar
            .Tabs.OfType<TerminalTabViewModel>()
            .Where(tab => tab.Profile?.Id == profileId)
            .Select(tab => tab.SyncChannel)
            .FirstOrDefault(c => c is not null);
        Sidebar.SessionTree?.SetSessionSyncChannel(
            profileId,
            channel?.ToString() ?? string.Empty
        );
    }

    private void RefreshQuickCommandTargets()
    {
        (Guid Id, string DisplayName)[] targets = TabBar
            .Tabs.OfType<TerminalTabViewModel>()
            .Where(tab => tab.IsConnected)
            .Select(tab => (tab.Id, tab.Title))
            .ToArray();
        _terminalTargetSelector.UpdateTargets(targets);
        _terminalTargetSelector.SetCurrentTarget(
            ActiveTerminalTab is { IsConnected: true } current ? current.Id : null
        );
    }

    private void OnQuickCommandExecutionRequested(
        object? sender,
        QuickCommandExecutionRequest request
    )
    {
        var targetIds = request.TargetIds.ToHashSet();
        TerminalTabViewModel[] targets = TabBar
            .Tabs.OfType<TerminalTabViewModel>()
            .Where(tab => tab.IsConnected && targetIds.Contains(tab.Id))
            .ToArray();
        bool sent = false;
        foreach (TerminalTabViewModel target in targets)
        {
            sent |= target.TryExecuteCommand(request.CommandText);
        }
        if (sent)
        {
            TerminalFocusRequested?.Invoke(this, EventArgs.Empty);
        }
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
        RevealActiveSessionInSidebar();
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
            _paletteProfiles = [.. profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
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
            string title = string.IsNullOrWhiteSpace(item.DisplayName)
                ? captured.Host
                : item.DisplayName;
            items.Add(
                new(
                    Strings.Get("RecentConnections"),
                    title,
                    () => _ = TryConnectRecentAsync(captured),
                    Strings.Get("Msg_EnterToConnect"),
                    isSession: true
                )
            );
        }

        // All saved profiles (§12.3),带分组徽章;已出现在最近连接里的不重复列出。
        foreach (SessionProfile profile in _paletteProfiles)
        {
            if (recentProfileIds.Contains(profile.Id))
            {
                continue;
            }
            SessionProfile captured = profile;
            string? groupName =
                captured.GroupId is { } groupId
                && _paletteGroupNames.TryGetValue(groupId, out string? name)
                    ? name
                    : null;
            items.Add(
                new(
                    Strings.Get("Sessions"),
                    string.IsNullOrWhiteSpace(captured.Name) ? captured.Host : captured.Name,
                    () => _ = TryConnectProfileAsync(captured),
                    Strings.Get("Msg_EnterToConnect"),
                    groupName,
                    true
                )
            );
        }

        // Global actions come from the shared command registry (menu/palette/shortcut parity).
        items.AddRange(
            Commands.All.Select(captured => new CommandPaletteItem(
                Strings.Get("Command"),
                captured.Title,
                () => Commands.Execute(captured.Id),
                captured.Shortcut
            ))
        );
        return items;
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
    private (TerminalTabViewModel Tab, TerminalDocument Document) CreateConnectingTab(
        SessionProfile profile,
        AppSettings settings
    )
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
                : $"SSH • {displayName} • {Strings.Get("Msg_ViaJumpHost")}",
            TerminalTypeName = terminalType.ToTermName(),
            EncodingName = string.IsNullOrWhiteSpace(settings.TerminalEncoding)
                ? "UTF-8"
                : settings.TerminalEncoding,
            Profile = profile,
        };
        terminalTab.ReconnectRequested += (_, _) => _ = ReconnectTabAsync(terminalTab);
        terminalTab.Disconnected += (_, _) => OnTabDisconnected(terminalTab);

        // 命令补全:注入建议提供器;提交(已回显校验)的命令进全局历史。
        terminalTab.SuggestionProvider = _suggestionProvider;
        WireZModemDownload(terminalTab);
        terminalTab.CommandLineSubmitted += CommandHistory.Record;

        // 资源管理器树的状态圆点与「活跃/连接中/离线」标签(设计 FrJPu)跟随该配置
        // 最新标签的连接状态;重连复用同一标签,订阅随标签生命周期存续。
        terminalTab
            .WhenAnyValue(x => x.ConnectionStatus)
            .Subscribe(status => Sidebar.SessionTree?.SetSessionStatus(profile.Id, status));

        // 资源管理器树节点名前的同步输入频道字母跟随该配置任一标签的频道归属;
        // 标签关闭时经 SyncInputCoordinator.Detach → LeaveSyncChannel 同样走到这里复位。
        terminalTab
            .WhenAnyValue(x => x.SyncChannel)
            .Subscribe(_ => RefreshSessionSyncChannel(profile.Id));

        // 后台标签收到 BEL → 点亮闪烁提醒(设置 → 终端 → 标签闪烁提醒);切回标签时清除。
        if (terminalEmulator is VelaTerminalControl bellSource)
        {
            bellSource.BellRang += () =>
            {
                if (
                    _latestSettings?.TerminalBehavior.TabFlashAlert != false
                    && !ReferenceEquals(ActiveTerminalTab, terminalTab)
                )
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
        Layout.AddDocument(document);
        UpdateStatusBarForActiveTab();
        return (terminalTab, document);
    }

    /// <summary>
    /// 在一个已存在的“连接中”标签上完成 SSH 握手并挂上传输;失败时向上抛,由调用方
    /// 决定撤标签(直接入口)还是保留标签显示覆盖层(交互入口)。
    /// </summary>
    private async Task RunHandshakeAsync(
        TerminalTabViewModel terminalTab,
        SessionProfile profile,
        AppSettings settings,
        CancellationToken cancellationToken
    )
    {
        TerminalType terminalType = TerminalTypeExtensions.FromTermName(settings.TerminalType);
        SshSession session = await _connectionWorkflowService!.ConnectProfileAsync(
            profile,
            cancellationToken
        );
        ISshClientWrapper client =
            _sshConnectionService!.GetClient(session.SessionId)
            ?? throw new InvalidOperationException("SSH client was not created for the session.");
        // CreateShellStream 是同步网络往返(打开通道 + pty-req + shell,2~3 个 RTT),
        // 放在线程池上执行,否则每连一个标签 UI 线程就冻结 RTT 的整数倍时长。
        IShellStreamWrapper shellStream = await Task.Run(
            () => client.CreateShellStream(terminalType.ToTermName(), 120, 32, 0, 0, 4096),
            cancellationToken
        );
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
            terminalTab.ResourceMonitor = new(
                _metricsService,
                session.SessionId,
                terminalTab.Title
            );
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
    public async Task ReconnectTabAsync(
        TerminalTabViewModel tab,
        CancellationToken cancellationToken = default
    )
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
        if (
            tab.Profile is null
            || _connectionWorkflowService is null
            || _sshConnectionService is null
        )
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
            SshSession session = await _connectionWorkflowService.ConnectProfileAsync(
                tab.Profile,
                cancellationToken
            );
            ISshClientWrapper client =
                _sshConnectionService.GetClient(session.SessionId)
                ?? throw new InvalidOperationException(
                    "SSH client was not created for the session."
                );
            // 同 RunHandshakeAsync:通道打开的同步网络往返不能占用 UI 线程。
            IShellStreamWrapper shellStream = await Task.Run(
                () => client.CreateShellStream(terminalType.ToTermName(), 120, 32, 0, 0, 4096),
                cancellationToken
            );

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
            string notice =
                "\e[90m● "
                + Strings.Format("Msg_JumpChainNotice", string.Join(" → ", names), target)
                + "\e[0m\r\n";
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
        if (settings.General.SessionLogging && tab.Bridge is not null)
        {
            SessionLogWriter? writer = SessionLogService.CreateWriter(tab.Title);
            if (writer is not null)
            {
                tab.Bridge.DataReceived += writer.Write;
                _sessionLogs[tab] = writer;
            }
        }

        // 会话录制(设置 → 安全审计):与会话日志同挂钩点(桥的原始输出),
        // 每次(重)连接产生一条新录制;开关只对之后建立的连接生效。
        if (
            settings.Security.RecordProductionSessions
            && _recordingStore is not null
            && tab.Bridge is not null
        )
        {
            var recorder = new SessionRecorder(_recordingStore, tab.Title);
            tab.Bridge.DataReceived += recorder.Write;
            _sessionRecorders[tab] = recorder;
        }
    }

    private void StopSessionLogging(TerminalTabViewModel tab)
    {
        if (_sessionLogs.Remove(tab, out SessionLogWriter? writer))
        {
            writer.Dispose(); // 旧桥可能还在收尾;Write 对已释放流是 no-op。
        }
        if (_sessionRecorders.Remove(tab, out SessionRecorder? recorder))
        {
            recorder.Dispose(); // 收尾写入元数据(时长/结束时间)。
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
            StatusBar.Status = Strings.Format("Msg_TabDisconnected", tab.Title);
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
        if (
            !settings.General.AutoReconnect
            || tab.UserRequestedDisconnect
            || tab.LocalShell is not null
            || Application.Current is null
        )
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
        StatusBar.Status = Strings.Format(
            "Msg_AutoReconnectCountdown",
            tab.Title,
            delaySeconds,
            tab.ReconnectAttempts,
            maxRetries
        );
        DispatcherTimer.RunOnce(
            () =>
            {
                // 等待期间用户可能已手动重连、关掉标签或主动断开。
                if (
                    tab
                        is
                    {
                        ConnectionStatus: SessionStatus.Disconnected,
                        UserRequestedDisconnect: false
                    }
                    && TabBar.Tabs.Contains(tab)
                )
                {
                    _ = ReconnectTabAsync(tab);
                }
            },
            TimeSpan.FromSeconds(delaySeconds)
        );
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
        if (
            !string.IsNullOrEmpty(user)
            && user.Contains("PROMPT_COMMAND=prompt_nl", StringComparison.Ordinal)
        )
        {
            user = null;
        }
        string payload = string.IsNullOrEmpty(user)
            ? PromptNewlineFix
            : PromptNewlineFix + "; " + user;
        tab.SendSilentCommand(payload);
    }

    private void RemoveTerminalTab(TerminalTabViewModel tab, TerminalDocument document)
    {
        StopSessionLogging(tab);
        // 防御性驱逐 SFTP 面板缓存:本路径(连接失败/取消)静默移除文档,不触发
        // DocumentClosed,若标签曾短暂连上过,缓存里的面板会悬挂。幂等,无缓存时空操作。
        CloseSftpForTab(tab);
        if (TabBar.Tabs.Contains(tab))
        {
            TabBar.CloseTabCommand.Execute(tab).Subscribe();
        }
        Layout.RemoveDocument(document);
        if (ReferenceEquals(ActiveTerminalTab, tab))
        {
            ActiveTerminalTab = TabBar.ActiveTab as TerminalTabViewModel;
        }
        tab.Dispose();
    }

    /// <summary>缺少连接所需凭据(用户名/密码/私钥)时需要先走登录验证流程。</summary>
    private static bool RequiresCredentials(SessionProfile profile) =>
        string.IsNullOrWhiteSpace(profile.Username)
        || (profile.AuthMethod == AuthMethod.Password && string.IsNullOrEmpty(profile.Password))
        || (
            profile.AuthMethod == AuthMethod.PrivateKey
            && string.IsNullOrWhiteSpace(profile.PrivateKeyPath)
        );

    /// <summary>
    /// Connects without ever letting a failure escape to the caller. Authentication failures,
    /// unreachable hosts and the like are captured in <see cref="LastConnectionError" /> and
    /// reflected in the status bar instead of crashing the app.
    /// 凭据缺失或认证失败时通过 <see cref="InteractiveAuthenticator" /> 走两步验证弹窗(最多重试 3 次)。
    /// </summary>
    public async Task<TerminalTabViewModel?> TryConnectProfileAsync(
        SessionProfile profile,
        CancellationToken cancellationToken = default
    )
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
    public async Task<TerminalTabViewModel?> TryConnectRecentAsync(
        RecentConnectionEntry entry,
        CancellationToken cancellationToken = default
    )
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
            AuthMethod = AuthMethod.Password,
        };
        return await TryConnectProfileAsync(profile, cancellationToken);
    }

    private static string DescribeConnectionError(Exception ex, SessionProfile profile)
    {
        string target = string.IsNullOrWhiteSpace(profile.Host)
            ? profile.Name
            : $"{profile.Username}@{profile.Host}:{profile.Port}";
        // Match by type name so VelaShell.App need not reference SSH.NET directly.
        return ex.GetType().Name switch
        {
            "SshAuthenticationException" => Strings.Format("Msg_AuthFailed", target),
            "SshConnectionException" => Strings.Format("Msg_ConnectFailed", target),
            "SocketException" => Strings.Format("Msg_NetworkError", target),
            "SshOperationTimeoutException" => Strings.Format("Msg_ConnectTimeout", target),
            "ProxyException" => Strings.Format("Msg_ProxyError", target),
            _ => Strings.Format("Msg_ConnectGenericFailed", target, ex.Message),
        };
    }

    private void ConfigureTerminal(
        ITerminalEmulator emulator,
        AppSettings settings,
        TerminalType terminalType,
        bool forceUtf8 = false
    )
    {
        if (emulator is VelaTerminalControl control)
        {
            control.TerminalType = terminalType;
            // 侧栏右键菜单改动 → 持久化(-= 再 += 保证单次订阅,即使本方法重入)。
            control.GutterOptionsChanged -= OnGutterOptionsChanged;
            control.GutterOptionsChanged += OnGutterOptionsChanged;
            // Ctrl+滚轮缩放字号 → 持久化(同上,单次订阅)。
            control.FontSizeChanged -= OnTerminalFontSizeChanged;
            control.FontSizeChanged += OnTerminalFontSizeChanged;
        }
        ApplyLiveTerminalSettings(emulator, settings, forceUtf8);
    }

    /// <summary>
    /// Ctrl+滚轮缩放字号后写回设置(400ms 尾沿合并:连续滚动只保存一次);
    /// SaveSettingsAsync 会广播到所有已打开标签,使各标签字号保持一致。
    /// </summary>
    private void OnTerminalFontSizeChanged(double size)
    {
        if (_settingsService is null)
        {
            return;
        }
        _pendingFontSize = (int)Math.Round(size);
        if (_fontSizePersistDebounce is null)
        {
            _fontSizePersistDebounce = new() { Interval = TimeSpan.FromMilliseconds(400) };
            _fontSizePersistDebounce.Tick += (_, _) =>
            {
                _fontSizePersistDebounce!.Stop();
                PersistTerminalFontSize(_pendingFontSize);
            };
        }
        _fontSizePersistDebounce.Stop();
        _fontSizePersistDebounce.Start();
    }

    private void PersistTerminalFontSize(int size)
    {
        if (_settingsService is null)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                AppSettings settings = await _settingsService
                    .GetSettingsAsync()
                    .ConfigureAwait(false);
                if (settings.TerminalFontSize == size)
                {
                    return;
                }
                settings.TerminalFontSize = size;
                await _settingsService.SaveSettingsAsync(settings).ConfigureAwait(false);
            }
            catch
            {
                // 写回失败只影响下次启动的初始值,不打断当前会话。
            }
        });
    }

    /// <summary>侧栏右键菜单切换部件后写回设置;SaveSettingsAsync 会广播到所有已打开标签,保持一致。</summary>
    private void OnGutterOptionsChanged(bool timestamp, bool number, bool fold, bool blank)
    {
        if (_settingsService is null)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                AppSettings settings = await _settingsService
                    .GetSettingsAsync()
                    .ConfigureAwait(false);
                TerminalBehaviorOptions b = settings.TerminalBehavior;
                if (
                    b.ShowLineTimestamp == timestamp
                    && b.ShowLineNumber == number
                    && b.ShowFoldMarker == fold
                    && b.GutterBlank == blank
                )
                {
                    return;
                }
                b.ShowLineTimestamp = timestamp;
                b.ShowLineNumber = number;
                b.ShowFoldMarker = fold;
                b.GutterBlank = blank;
                await _settingsService.SaveSettingsAsync(settings).ConfigureAwait(false);
            }
            catch
            {
                // 写回失败只影响下次启动的初始值,不打断当前会话。
            }
        });
    }

    /// <summary>
    /// The settings that are safe to change on a live session: scrollback depth, font,
    /// font size, host-output encoding plus the full 终端行为/配色 option set. Applied at tab
    /// creation and re-applied to every open tab whenever settings are saved (#3/#15/#21).
    /// </summary>
    private void ApplyLiveTerminalSettings(
        ITerminalEmulator emulator,
        AppSettings settings,
        bool forceUtf8 = false
    )
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
            control.FontFamily = new(
                $"{settings.TerminalFont}, JetBrains Mono, Cascadia Mono, Consolas, Microsoft YaHei, monospace"
            );
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
        control.ShowLineTimestamp = behavior.ShowLineTimestamp;
        control.ShowLineNumber = behavior.ShowLineNumber;
        control.ShowFoldMarker = behavior.ShowFoldMarker;
        control.GutterBlank = behavior.GutterBlank;
        control.GutterMenu = new(
            Strings.Get("Gutter_LineNumber"),
            Strings.Get("Gutter_Timestamp"),
            Strings.Get("Gutter_FoldMarker"),
            Strings.Get("Gutter_Blank")
        );
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
        control.PaletteOverrides = TerminalAppearanceMapper.BuildPaletteOverrides(
            settings.Appearance
        );
    }

    private void OnSettingsSaved(AppSettings settings)
    {
        _latestSettings = settings;

        // SaveSettingsAsync may complete on a thread-pool continuation; font/size touch layout,
        // so marshal onto the UI thread (the main scheduler is the Avalonia dispatcher).
        RxSchedulers.MainThreadScheduler.Schedule(
            Unit.Default,
            (_, _) =>
            {
                ApplyShellPreferences(settings);
                ApplyLiveSettingsToOpenTabs(settings);

                // 已打开的文件浏览器同步最新的传输选项(冲突策略/并发/带宽等)与
                // “显示隐藏文件”状态(设置审计 C-04:设置中心与工具栏共用一个来源)。
                // 面板按会话缓存后,当前实例与全部缓存实例都要广播到。
                FileBrowser.TransferOptions = settings.Transfer;
                FileBrowser.ShowHiddenFiles = settings.Transfer.ShowHiddenFiles;
                ApplyColumnVisibility(FileBrowser, settings.Transfer);
                foreach (FileBrowserViewModel browser in _fileBrowserCache.Values)
                {
                    browser.TransferOptions = settings.Transfer;
                    browser.ShowHiddenFiles = settings.Transfer.ShowHiddenFiles;
                    ApplyColumnVisibility(browser, settings.Transfer);
                }
                RevealActiveSessionInSidebar();
                return Disposable.Empty;
            }
        );
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
                AppSettings settings = await _settingsService
                    .GetSettingsAsync()
                    .ConfigureAwait(false);
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

    /// <summary>
    /// 文件浏览器表头右键切换列显示后写回持久化设置(设置审计 C-04),
    /// 使各会话的面板与下次启动共用 Transfer 的列显示这一个状态来源。
    /// </summary>
    /// <param name="columnKey">列键("size"/"permissions"/"owner"/"group"/"type"/"modified")。</param>
    /// <param name="visible">该列切换后的可见性。</param>
    private void PersistColumnVisibility(string columnKey, bool visible)
    {
        if (_settingsService is null)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                AppSettings settings = await _settingsService
                    .GetSettingsAsync()
                    .ConfigureAwait(false);
                if (!TrySetColumnVisibility(settings.Transfer, columnKey, visible))
                {
                    return;
                }
                await _settingsService.SaveSettingsAsync(settings).ConfigureAwait(false);
            }
            catch
            {
                // 写回失败只影响下次启动的初始值,不打断当前浏览。
            }
        });
    }

    /// <summary>
    /// 把列键对应的设置项置为 <paramref name="visible" />;值本就相同(或列键无法识别)
    /// 时返回 false,调用方据此跳过一次无谓的落盘。
    /// </summary>
    private static bool TrySetColumnVisibility(
        TransferOptions transfer,
        string columnKey,
        bool visible
    )
    {
        switch (columnKey)
        {
            case "size" when transfer.ShowSizeColumn != visible:
                transfer.ShowSizeColumn = visible;
                return true;
            case "permissions" when transfer.ShowPermissionsColumn != visible:
                transfer.ShowPermissionsColumn = visible;
                return true;
            case "owner" when transfer.ShowOwnerColumn != visible:
                transfer.ShowOwnerColumn = visible;
                return true;
            case "group" when transfer.ShowGroupColumn != visible:
                transfer.ShowGroupColumn = visible;
                return true;
            case "type" when transfer.ShowTypeColumn != visible:
                transfer.ShowTypeColumn = visible;
                return true;
            case "modified" when transfer.ShowModifiedColumn != visible:
                transfer.ShowModifiedColumn = visible;
                return true;
            default:
                return false;
        }
    }

    /// <summary>把设置里的列显示状态铺到某个文件浏览器面板(设置保存后广播用)。</summary>
    private static void ApplyColumnVisibility(
        FileBrowserViewModel browser,
        TransferOptions transfer
    )
    {
        browser.ShowSizeColumn = transfer.ShowSizeColumn;
        browser.ShowPermissionsColumn = transfer.ShowPermissionsColumn;
        browser.ShowOwnerColumn = transfer.ShowOwnerColumn;
        browser.ShowGroupColumn = transfer.ShowGroupColumn;
        browser.ShowTypeColumn = transfer.ShowTypeColumn;
        browser.ShowModifiedColumn = transfer.ShowModifiedColumn;
    }

    /// <summary>
    /// 一键切换整条侧栏(Ctrl+Shift+L / 命令面板):任一(时间/行号)开着就全部关掉,都关着则全部打开。
    /// 只想单独显示时间或行号的用户走设置页两个独立开关。写回持久化设置即可 ——
    /// SaveSettingsAsync 会触发 SettingsSaved → OnSettingsSaved,自动应用到所有已打开的终端标签。
    /// </summary>
    private void ToggleLineGutter()
    {
        if (_settingsService is null)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                AppSettings settings = await _settingsService
                    .GetSettingsAsync()
                    .ConfigureAwait(false);
                bool anyOn =
                    settings.TerminalBehavior.ShowLineTimestamp
                    || settings.TerminalBehavior.ShowLineNumber;
                settings.TerminalBehavior.ShowLineTimestamp = !anyOn;
                settings.TerminalBehavior.ShowLineNumber = !anyOn;
                await _settingsService.SaveSettingsAsync(settings).ConfigureAwait(false);
            }
            catch
            {
                // 切换失败只影响本次操作,不打断当前会话。
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
        StatusBar.Latency = tab.Latency is { } latency
            ? $"{(int)latency.TotalMilliseconds}ms"
            : string.Empty;
    }

    private void SetActiveFromDocument(DockDocument? dockDocument)
    {
        if (
            dockDocument is not TerminalDocument document
            || !TabBar.Tabs.Contains(document.Terminal)
        )
        {
            return;
        }
        ActiveTerminalTab = document.Terminal;
        if (!ReferenceEquals(TabBar.ActiveTab, document.Terminal))
        {
            TabBar.ActiveTab = document.Terminal;
        }
    }

    /// <summary>
    /// TabBar → 工作区反向同步:Ctrl+Tab / Ctrl+Shift+Tab 走 TabBar 的逻辑集合切换标签,
    /// 文档区必须跟着切到对应文档(原 Dock 集成缺这半边,快捷键切标签时画面不动)。
    /// </summary>
    private void SyncWorkspaceToActiveTab(TerminalTabViewModel? tab)
    {
        if (tab is null)
        {
            return;
        }
        TerminalDocument? document = Layout
            .AllDocuments()
            .OfType<TerminalDocument>()
            .FirstOrDefault(d => ReferenceEquals(d.Terminal, tab));
        if (document is not null && !ReferenceEquals(Layout.ActiveDocument, document))
        {
            Layout.ActivateDocument(document);
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
        bool stillConnected = TabBar
            .Tabs.OfType<TerminalTabViewModel>()
            .Any(other =>
                !ReferenceEquals(other, tab)
                && other.Profile?.Id == profile.Id
                && other.ConnectionStatus == SessionStatus.Connected
            );
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
