using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Avalonia;
using Avalonia.Threading;
using ReactiveUI;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Concurrency;
using ReactiveUI.Primitives.Signals;
using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Diagnostics;
using VelaShell.Core.Ftp;
using VelaShell.Core.Layouts;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Core.Net;
using VelaShell.Core.Notifications;
using VelaShell.Core.Processes;
using VelaShell.Core.Protocols;
using VelaShell.Core.Recording;
using VelaShell.Core.Resources;
using VelaShell.Core.Services;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Core.Sync;
using VelaShell.Core.Tunnels;
using VelaShell.Core.XServer;
using VelaShell.Docking;
using VelaShell.Docking.Model;
using VelaShell.Infrastructure.Diagnostics;
using VelaShell.Infrastructure.Net;
using VelaShell.Infrastructure.Plugins.Protocols;
using VelaShell.Infrastructure.Pty;
using VelaShell.Infrastructure.Ssh;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.Presentation.Commands;
using VelaShell.Presentation.Services;
using VelaShell.Presentation.ViewModels;
using VelaShell.Services;
using VelaShell.Services.Layouts;
using VelaShell.Terminal;
using VelaShell.Terminal.Emulation;
using VelaShell.Terminal.Rendering;

namespace VelaShell.ViewModels;

/// <summary>
/// 主窗口视图模型:应用外壳的中枢,统筹终端标签、SSH/本地会话生命周期、停靠工作区、
/// 侧边栏、状态栏、命令面板、SFTP 文件面板与隧道面板,并串联设置、连接工作流与各项服务。
/// </summary>
public class MainWindowViewModel : ReactiveObject, Services.Plugins.ITerminalResolver
{
    /// <summary>RIS(ESC c)完全重置序列:重开会话前清掉旧进程的残留缓冲。</summary>
    private static readonly byte[] RisResetSequence = [0x1B, (byte)'c']; // ESC c

    private readonly IConnectionWorkflowService? _connectionWorkflowService;

    /// <summary>
    /// 凭据解析器(#550):FTP 与插件协议不走 SSH 工作流,连接前在这里把引用换成凭据。
    /// 无 UI 单测不注入,此时配置原样交出。
    /// </summary>
    private readonly ICredentialResolver? _credentialResolver;

    /// <summary>共享凭据变更通知到 UI 线程之前是否已有一次树刷新在排队(同步拉取会连发很多次)。</summary>
    private int _credentialRefreshQueued;
    private readonly ISessionMetricsService? _metricsService;
    private readonly IRemoteProcessService? _remoteProcessService;

    /// <summary>远端搜索(find / grep),挂到 SSH 会话的文件面板上;没有 SSH 连接服务(单测)时为 null,面板上就没有「搜索」。</summary>
    private readonly IRemoteSearchService? _remoteSearch;

    // ---- 会话日志(设置 → 常规 → 数据与存储) ----

    private readonly Dictionary<TerminalTabViewModel, SessionLogWriter> _sessionLogs = [];

    // ---- 会话录制(设置 → 安全审计 → 会话录制) ----

    private readonly Dictionary<TerminalTabViewModel, SessionRecorder> _sessionRecorders = [];
    private readonly ISessionRecordingStore? _recordingStore;

    /// <summary>挂在日志 / 录制前面的输出遮盖(设置 → 安全审计,默认开);停止记录时先把扣着的那一点放出去。</summary>
    private readonly Dictionary<TerminalTabViewModel, List<SessionOutputMasker>> _captureMaskers = [];

    private readonly IAppDataStore? _appDataStore;
    private readonly PaletteRecency _paletteRecency;

    /// <summary>生效中的键位表(命令面板右侧的键位提示跟着它走);没有时按出厂键位。</summary>
    private readonly ShortcutKeymapService? _shortcutKeymap;
    private readonly ISessionRepository? _sessionRepository;
    private readonly ISettingsService? _settingsService;
    private readonly ISftpService? _sftpService;
    private readonly IFtpSessionService? _ftpSessionService;
    private readonly IPluginProtocolSessionService? _pluginProtocols;

    /// <summary>协议注册表:开插件协议文档时要按协议 id 取回它的动作与能力位。</summary>
    private readonly PluginProtocolRegistry? _protocolRegistry;

    /// <summary>工作台连接类型(Redis 等)的会话启动器;无插件宿主的单测里为 null。</summary>
    private readonly PluginWorkspaceLauncher? _workspaceLauncher;

    /// <summary>工作台会话 id → 已打开的停靠文档(插件被停用时要按 id 找到它并关掉)。</summary>
    private readonly ConcurrentDictionary<Guid, PluginWorkspaceDocument> _workspaceDocuments = new();

    /// <summary>
    /// 每条配置名下都有谁活着,以及树上那个圆点该显示什么状态(见 <see cref="SessionStatusRegistry" />)。
    /// </summary>
    /// <remarks>
    /// 终端标签那边靠 <see cref="TerminalTabs" /> 自己就能枚举(#321 已按这条纪律改过),
    /// 文档型会话没有等价的集合可枚举(<c>Layout.AllDocuments()</c> 在 DocumentClosed 触发时
    /// 已经把正在关的那个摘掉了,但迟到的状态事件仍会引用它),所以账本里只记文档那一半。
    /// </remarks>
    private readonly SessionStatusRegistry _sessionStatuses = new();

    /// <summary>工作台会话 id → 为它建的隧道 id(文档关闭时要拆掉,否则本地端口一直占着)。</summary>
    private readonly ConcurrentDictionary<Guid, Guid> _workspaceTunnels = new();
    private readonly ISshConnectionService? _sshConnectionService;

    /// <summary>当前界面主题(具名主题目录),用于给终端下发配套的终端配色。</summary>
    private readonly IThemeService? _themeService;
    private readonly Func<ITerminalEmulator> _terminalEmulatorFactory;
    private readonly ITunnelService? _tunnelService;
    private readonly ITunnelWorkflowService? _tunnelWorkflowService;
    private readonly QuickCommandsViewModel? _quickCommands;
    private readonly QuickCommandRunnerViewModel? _quickCommandRunner;
    private readonly TerminalTargetSelectorViewModel _terminalTargetSelector;
    private readonly Dictionary<TerminalTabViewModel, IDisposable> _quickCommandTargetSubscriptions = [];

    /// <summary>
    /// 每个终端标签的连接状态订阅,用于重算它那条配置在会话树上的状态标签
    /// (见 <see cref="RefreshSessionStatus" />)。与快捷命令目标订阅同生命周期:
    /// 在 <see cref="SyncTabSubscriptions" /> 里随文档进出工作区挂上与退订。
    /// </summary>
    private readonly Dictionary<TerminalTabViewModel, IDisposable> _sessionStatusSubscriptions = [];

    /// <summary>
    /// 还在握手的终端标签 → 撤销这次握手的取消源(关标签时要拉的那根绳)。
    /// </summary>
    /// <remarks>
    /// 文档型连接的这根绳挂在 <see cref="DocumentConnectUi" /> 上,终端标签这边一直没有:
    /// 关掉一个正在连的标签只是把标签移走,握手照旧在后台跑到底 —— 右下角圆环上那条
    /// 「连接中」赖着不走,几十秒后还要为一个早就没了的标签弹一句"无法连接"。
    /// 登记在这里,六个关闭入口(标签 ×、Ctrl+W、右键那一族、命令面板)一并管住。
    /// </remarks>
    private readonly Dictionary<TerminalTabViewModel, CancellationTokenSource> _tabConnectCancellations = [];

    /// <summary>同步输入频道的对等转发中枢(标签右键菜单 → 同步输入)。</summary>
    private readonly SyncInputCoordinator _syncInput = new();

    /// <summary>全局命令历史(命令补全数据源;终端标签提交命令后写入)。</summary>
    public CommandHistoryService CommandHistory { get; }

    /// <summary>补全建议提供器(历史 ∪ 快捷命令),注入到每个终端标签。</summary>
    private readonly CommandSuggestionProvider _suggestionProvider;

    // SFTP/文件管理视图(源自设计稿)
    private FileBrowserViewModel _fileBrowser;

    /// <summary>
    /// 按会话缓存的 SFTP 面板实例:切换标签复用(保留路径/列表/排序/列宽,免重复列目录),
    /// 标签关闭或连接断开时经 <see cref="EvictFileBrowser" /> 驱逐。
    /// </summary>
    private readonly Dictionary<Guid, FileBrowserViewModel> _fileBrowserCache = [];
    private readonly Lock _sftpCloseTasksSync = new();
    private readonly Dictionary<SftpDocumentBase, Task> _sftpCloseTasks = [];

    /// <summary>
    /// 正在关闭中的终端文档。
    /// </summary>
    /// <remarks>
    /// <c>DocumentClosed</c> 是同步事件,而关一个终端文档要拆桥、断连接 —— 全是 I/O。
    /// 与 SFTP 文档同一条路子:同步登记一件在跑的关闭工作,退出路径上一起等完;
    /// 用例也靠它等关闭真正跑完,而不是 sleep 一下赌它结束了。
    /// </remarks>
    private readonly Dictionary<TerminalDocument, Task> _terminalCloseTasks = [];

    /// <summary>
    /// 后台活动账本:连接期间在这里登记一条,右下角那个圆环才转得起来。
    /// </summary>
    /// <remarks>
    /// 原先只把它转接给状态栏(<see cref="WireBackgroundActivity" />),自己一条都不登记 ——
    /// 于是圆环只有插件装载与配置同步时才出现,连一台机器连三十秒它一动不动(#385 反馈)。
    /// 无界面单测不注入,故全部调用点都走 <c>?.</c>。
    /// </remarks>
    private readonly IBackgroundActivityService? _backgroundActivity;
    private FileTransferViewModel _fileTransfer;

    private AppSettings? _latestSettings;
    private AppState _appState = new();
    private bool _startupStatePrepared;
    private bool _isApplyingSidebarState;
    private CancellationTokenSource? _sidebarStateSaveDebounce;

    private Dictionary<Guid, string> _paletteGroupNames = [];

    // ---- 命令面板的全量会话(§12.3:面板作为中枢,收录全部已保存配置) ----

    private IReadOnlyList<SessionProfile> _paletteProfiles = [];
    private SidebarViewModel _sidebar;
    private StatusBarViewModel _statusBar;

    /// <summary>
    /// 状态栏那一排实时指标(CPU / 内存 / 磁盘 / 网络 / 延迟)的采样循环。
    /// </summary>
    /// <remarks>
    /// 直接把协作者交出去,而不是在这里再包两个转发方法:窗口要按 <c>WindowState</c> 暂停、
    /// 按 <c>Activated/Deactivated</c> 降频,那是采样循环自己的事,让主窗口视图模型
    /// 替它转述一遍只是把同一件事写两处。
    /// </remarks>
    public StatusMetricsPoller StatusMetrics { get; }
    private DispatcherTimer? _fontSizePersistDebounce;
    private int _pendingFontSize;

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
        ITunnelService? tunnelService = null,
        ITunnelWorkflowService? tunnelWorkflowService = null,
        ISessionMetricsService? metricsService = null,
        IRecentConnectionService? recentConnectionService = null,
        ISecurityAlertService? securityAlertService = null,
        ISettingsPreviewService? settingsPreviewService = null,
        IAppDataStore? appDataStore = null,
        ISessionRecordingStore? recordingStore = null,
        QuickCommandsViewModel? quickCommands = null,
        IQuickCommandRepository? quickCommandRepository = null,
        IRemoteProcessService? remoteProcessService = null,
        ITraceRouteService? traceRouteService = null,
        ICommandRegistry? commandRegistry = null,
        IFtpSessionService? ftpSessionService = null,
        IPluginProtocolSessionService? pluginProtocolService = null,
        PluginProtocolRegistry? protocolRegistry = null,
        PluginWorkspaceLauncher? workspaceLauncher = null,
        IGistSyncService? gistSyncService = null,
        IBackgroundActivityService? backgroundActivity = null,
        INotificationCenter? notificationCenter = null,
        IAnnouncementFeed? announcementFeed = null,
        IUpdateService? updateService = null,
        IThemeService? themeService = null,
        IConnectivityMonitor? connectivityMonitor = null,
        ILocalXServer? localXServer = null,
        ILocalizationService? localization = null,
        ShortcutKeymapService? shortcutKeymap = null,
        ICredentialResolver? credentialResolver = null,
        ISharedCredentialRepository? sharedCredentials = null,
        BackgroundUpdateDownload? backgroundUpdateDownload = null,
        IMasterKeyStatusSource? masterKeyStatus = null,
        LayoutTemplateStore? layoutTemplateStore = null
    )
    {
        _credentialResolver = credentialResolver;
        _backgroundUpdateDownload = backgroundUpdateDownload;
        _masterKeyStatus = masterKeyStatus;
        // 共享凭据被改名、删除(删除会把凭据拷回引用它的连接)之后,树上缓存的配置就旧了:
        // 带着一条已删除的引用去连,只会换来一个"凭据不存在"的登录框。
        sharedCredentials?.Changed += OnSharedCredentialsChanged;
        // 注册表可注入(DI 里与插件命令桥共享同一单例);无 UI 单测传 null 时自己创建一个。
        Commands = commandRegistry ?? new CommandRegistry();
        _shortcutKeymap = shortcutKeymap;
        _remoteProcessService = remoteProcessService;
        TraceRouteService = traceRouteService;
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
        _remoteSearch = sshConnectionService is null ? null : new RemoteSearchService(sshConnectionService);
        _themeService = themeService;
        // 切主题 → 终端画面跟着换整套配色。不能只靠控件自己听 ThemeVariant:
        // 具名主题里有多套暗色,VelaDark 换到 Tokyo Night 时变体压根没变(#主题目录)。
        if (themeService is not null)
        {
            themeService.ThemeChanged += _ => RefreshTerminalThemePalette();
            // 「跟随系统」下系统明暗翻转:主题服务不动,只有实际变体变了,同样要重下发。
            // 只在有主题服务时才挂:没有它的那些单测会造出成百个视图模型,
            // 每个都往共用的 Application 上挂一个再也不会摘掉的处理器。
            if (Application.Current is { } themeHost)
            {
                themeHost.ActualThemeVariantChanged += (_, _) =>
                {
                    // 只管「跟随系统」这一种情形。选具名主题时变体也会跟着变,那条路上
                    // ThemeChanged 已经重下发过一次 —— 不判断的话每次跨明暗切主题都要把
                    // 所有终端标签的设置刷两遍(每个标签一次全量重排 + 重绘)。
                    if (UiThemeCatalog.Find(_themeService?.CurrentTheme) is null)
                    {
                        RefreshTerminalThemePalette();
                    }
                };
            }
        }
        _settingsService = settingsService;
        _sessionRepository = sessionRepository;
        _sftpService = sftpService;
        _ftpSessionService = ftpSessionService;
        _pluginProtocols = pluginProtocolService;
        _protocolRegistry = protocolRegistry;
        _workspaceLauncher = workspaceLauncher;
        gistSyncService?.ProfilesApplied += OnSyncProfilesApplied;
        gistSyncService?.WebhookUrlMissing += OnSyncWebhookUrlMissing;
        // 插件被停用/卸载 → 它名下的工作台文档已无人应答,走正常关闭路径撤掉标签页。
        workspaceLauncher?.SessionAbandoned += OnWorkspaceSessionAbandoned;
        // 新建连接对话框里的「测试」对插件连接类型没法拿 SSH 去试(那只会撞出一个
        // 与真实原因无关的超时)。探针挂在这里而不是注进工作流服务:真开一次插件会话
        // 要用到隧道链路与凭据解密,那些都只在界面层有。
        _connectionWorkflowService?.PluginProbe = ProbePluginConnectionAsync;
        // FTP 与插件协议都没有 SSH 那种可订阅的长驻会话对象:断线只在下一次操作时暴露。
        // 由服务主动上报,树上的状态圆点才能自动变灰,而不是一直停在绿点上。
        ftpSessionService?.SessionStateChanged += OnFtpSessionStateChanged;
        pluginProtocolService?.SessionStateChanged += OnPluginSessionStateChanged;
        _tunnelService = tunnelService;
        _tunnelWorkflowService = tunnelWorkflowService;
        _metricsService = metricsService;
        _terminalEmulatorFactory = terminalEmulatorFactory ?? (() => new VelaTerminalControl());
        Layout = new DockWorkspace();
        Layout.DocumentClosed += document =>
        {
            if (document is TerminalDocument terminalDocument)
            {
                // DocumentClosed 是同步事件,而关一个终端文档要断连接、拆桥 —— 全是 I/O。
                // 与下面 SftpDocument 那条同一个写法:把它登记成一件在跑的关闭工作,
                // 退出路径上由 WaitForDocumentClosesAsync 统一等完。
                _ = TrackTerminalCloseTask(terminalDocument);
            }
            else if (document is SftpDocumentBase sftpDocument)
            {
                _ = GetOrCreateSftpCloseTask(sftpDocument);
            }
            else if (document is PluginWorkspaceDocument workspaceDocument)
            {
                _ = CloseWorkspaceDocumentAsync(workspaceDocument);
            }
            else if (document is ConnectingDocument connecting)
            {
                // 关掉一个还在连的占位标签 = 不连了。挂在 DocumentClosed 这一个点上,
                // 六个关闭入口(标签 ×、Ctrl+W、右键那一族、覆盖层上的「取消」)一并管住;
                // 连接流程收到取消后会自己收尾(该断的断掉,占位由它撤走)。
                connecting.Cancel();
            }
        };
        Layout.ActiveDocumentChanged += SetActiveFromDocument;
        // 每个标签要挂的那几条订阅(同步输入、会话状态圆点、快捷命令目标)随文档进出工作区
        // 挂上与退订。原先监听的是 TabBar 那份平行标签列表的 CollectionChanged ——
        // 两份集合各自变化,谁先谁后取决于调用顺序,漏同步一次就留下悬挂的订阅。
        Layout.DocumentAdded += _ => SyncTabSubscriptions();
        Layout.DocumentRemoved += _ => SyncTabSubscriptions();
        // 关闭已连接会话前的确认闸。装在工作区这一层,六个关闭入口(标签 ×、Ctrl+W、
        // 命令面板、右键的 关闭其他/全部/左侧/右侧)一处管住。
        Layout.CloseInterceptor = ConfirmCloseDocumentsAsync;
        // 标签右键菜单里切了「多行显示标签页」:写回设置,设置页与下次启动都认这一个值。
        Layout.PropertyChanged += OnLayoutPropertyChanged;
        // 命名布局模板(以及「恢复会话」按上次的分屏还原):开标签一律走下面这几条既有的连接路径,
        // 缺凭据弹框、认证重试、证书确认都在它们里面,模板这边一处不重写。
        LayoutTemplates = new(
            // DI 里与云同步共用一个实例(本机改动推得出去、拉下来的界面看得见);单测不给时按文档库现建。
            layoutTemplateStore ?? new LayoutTemplateStore(appDataStore),
            Layout,
            new LayoutDocumentOpener(
                FindProfileForLayoutAsync,
                shellId => LocalShellCatalog.DetectShells().FirstOrDefault(shell => shell.Id == shellId),
                new LayoutOpenActions(
                    profile => TryConnectProfileAsync(profile),
                    profile => OpenSftpDocumentForProfileAsync(profile),
                    profile => OpenFtpDocumentForProfileAsync(profile),
                    profile => OpenPluginDocumentForProfileAsync(profile),
                    profile => OpenWorkspaceDocumentForProfileAsync(profile),
                    (left, right) => OpenDualSftpDocumentAsync(left, right),
                    OpenLocalTerminalAsync)),
            document => LayoutDocumentDescriber.Describe(
                document,
                profile => _protocolRegistry?.KindOf(profile.PluginProtocolId) == PluginConnectionKind.Workspace),
            async profileId => await FindProfileForLayoutAsync(profileId).ConfigureAwait(true) is not null,
            Toasts);

        // 网络恢复 / 睡眠唤醒:立刻把断开的会话拉起来,而不是干等下一个退避周期。
        if (connectivityMonitor is { } connectivity)
        {
            connectivity.Resumed += () =>
                RxSchedulers.MainThreadScheduler.Schedule(ReconnectAllAfterResume);
        }
        _sidebar = new(recentConnectionService, _quickCommandRunner);
        _sidebar.PropertyChanged += OnSidebarStateChanged;
        if (sessionRepository is not null)
        {
            // 设置服务一并传进去:会话树要据此决定分组的初始展开态(#474)。
            _sidebar.SessionTree = new(sessionRepository, settingsService);
        }
        _statusBar = new();
        // 采样循环拆成了独立协作者(Q-01):定时器、重入闸、失焦降频与四段悬停提示
        // 彼此紧密、与主窗口其余职责毫不相干。活动标签用委托现取,不缓存 ——
        // 缓存一份就要再操心"什么时候刷新"。
        StatusMetrics = new(_statusBar, () => ActiveTerminalTab, metricsService);
        _backgroundActivity = backgroundActivity;
        WireBackgroundActivity(backgroundActivity);
        _fileBrowser = new(null, Guid.Empty);
        _fileTransfer = new(appDataStore)
        {
            // 传输浮窗上「已暂停的批次」点「继续」:找回(或重新打开)那条连接接着传(见 ResumeQueuedTransfersAsync)。
            ResumeHandler = ResumeQueuedTransfersAsync
        };
        // 活动标签换人时要做的那几件事。挂在 ActiveTerminalTab 上而不是某个标签集合上:
        // 停靠布局是唯一事实来源,而 ActiveTerminalTab 正是从它的 ActiveDocumentChanged 派生的。
        // 原先这条订阅挂在 TabBar.ActiveTab 上,于是"谁是活动标签"有两个各自会变的来源,
        // 还要在两边互相同步并防着回环。
        this.WhenAnyValue(x => x.ActiveTerminalTab)
            .Subscribe(activeTab =>
            {
                RefreshQuickCommandTargets();
                RevealActiveSessionInSidebar(activeTab);
            });

        // SFTP 面板“打开/关闭”是每个标签自己的状态:跟踪当前面板实例上的 IsVisible
        // 变化(标题栏切换、面板关闭按钮),回写到拥有该会话的标签
        // (TerminalTabViewModel.FileBrowserOpen),切回该标签时按此恢复。对象整体替换
        // (切标签重绑、驱逐后的占位)属于程序行为,Skip(1) 跳过替换瞬间的初值,
        // 不污染标签状态。
        this.WhenAnyValue(x => x.FileBrowser)
            .SwitchMap(browser => browser
                .WhenAnyValue(b => b.IsVisible)
                .Skip(1)
                .Select(visible => (browser.SessionId, Visible: visible)))
            .Subscribe(change => RememberFileBrowserStateForTab(change.SessionId, change.Visible));

        // 状态栏随活动标签同步:活动标签变化时以及该标签自身的连接状态/延迟变化时刷新。
        this.WhenAnyValue(x => x.ActiveTerminalTab)
            .SwitchMap(tab =>
                tab is null
                    ? Signal.Emit(RxVoid.Default)
                    : tab.WhenAnyValue(t => t.ConnectionStatus, t => t.Latency)
                        .Select(_ => RxVoid.Default)
            )
            .Subscribe(_ => UpdateStatusBarForActiveTab());

        // 选区字符数:只订阅**活动**标签那一个控件,切标签时改挂。
        // 订阅全部标签的话,后台标签里的残留选区会把状态栏写成别人的数字。
        this.WhenAnyValue(x => x.ActiveTerminalTab)
            .Subscribe(_ => RebindSelectionCounter());

        this.WhenAnyValue(x => x.ActiveTerminalTab)
            .SwitchMap(tab =>
                tab is null
                    ? Signal.Emit(RxVoid.Default)
                    : tab.WhenAnyValue(t => t.IsConnected).Select(_ => RxVoid.Default)
            )
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(CanToggleFileBrowser));
                this.RaisePropertyChanged(nameof(CanOpenProcessManager));
            });

        // 已保存的设置即时应用到所有已打开的终端(#3/#15/#21) —— 回滚、字体、
        // 字号与编码实时生效;TERM 按会话保持(在连接时协商)。
        _settingsService?.SettingsSaved += OnSettingsSaved;

        // 外观即时预览(设置窗口广播,未持久化):只重刷已打开标签的终端外观,
        // 不动 _latestSettings(新建标签仍用已保存的设置)。
        settingsPreviewService?.PreviewRequested += settings =>
            RxSchedulers.MainThreadScheduler.Schedule(() => ApplyLiveSettingsToOpenTabs(settings));

        // 安全告警(设置 → 安全审计 → 告警通道):应用内 → 状态栏;提示音 → 系统提示音。
        securityAlertService?.Alerted += notice =>
            RxSchedulers.MainThreadScheduler.Schedule(() =>
            {
                if (notice.InApp)
                {
                    // 安全告警按警告级走浮层:它以前写进状态栏那一个字符串,
                    // 下一次切标签(会把 Status 刷成"已连接")就被抹掉了 —— 用户可能根本没看见。
                    Toasts.Warning(notice.Message);
                }
                if (notice.Sound)
                {
                    SystemSound.Alert();
                }
            });
        StatusMetrics.Start();
        SetUpNotificationCenter(notificationCenter, announcementFeed, updateService);
        OpenSettingsCommand = ReactiveCommand.Create(() =>
            SettingsRequested?.Invoke(this, EventArgs.Empty)
        );
        XServer = new(localXServer, Toasts,
            () => SettingsSectionRequested?.Invoke(this, SettingsSectionKey.XServer));
        // X Server 浮层与隧道面板默认都在菜单栏下方右侧:开一个就收起另一个,免得叠在一起(隧道面板拖开了也照样,简单起见)。
        XServer.WhenAnyValue(x => x.IsPanelOpen).Where(open => open).Subscribe(_ => IsTunnelPanelOpen = false);
        this.WhenAnyValue(x => x.IsTunnelPanelOpen).Where(open => open).Subscribe(_ => XServer.IsPanelOpen = false);
        // 面板开着才每秒刷速率与连接列表(见 TunnelPanelViewModel.IsOpen);面板总是先建好再打开。
        this.WhenAnyValue(x => x.IsTunnelPanelOpen).Subscribe(open =>
        {
            if (TunnelPanel is { } tunnels)
            {
                tunnels.IsOpen = open;
            }
        });
        // 最近使用记录:让常用命令/会话在同分时排到前面。载入是异步的,先建后载 ——
        // 载入完成前只是没有加权,不影响面板可用。
        _paletteRecency = new(appDataStore);
        CommandPalette = new(BuildPaletteItems, _paletteRecency);
        _ = _paletteRecency.LoadAsync();
        OpenCommandPaletteCommand = ReactiveCommand.Create(() => CommandPalette.Open());
        IObservable<bool> canToggleFileBrowser = this.WhenAnyValue(x => x.ActiveTerminalTab)
            .SwitchMap(tab =>
                tab is null
                    ? Signal.Emit(false)
                    : tab.WhenAnyValue(t => t.IsConnected).Select(_ => CanToggleFileBrowser)
            );
        ToggleFileBrowserCommand = ReactiveCommand.Create(ToggleFileBrowser, canToggleFileBrowser);
        // 侧栏折叠与会话状态无关(没有活动标签时照样能收),故不挂 canExecute。
        ToggleSidebarCommand = ReactiveCommand.Create(ToggleSidebar);
        IObservable<bool> canOpenProcessManager = this.WhenAnyValue(x => x.ActiveTerminalTab)
            .SwitchMap(tab =>
                tab is null
                    ? Signal.Emit(false)
                    : tab.WhenAnyValue(t => t.IsConnected).Select(_ => CanOpenProcessManager)
            );
        OpenProcessManagerCommand = ReactiveCommand.Create(OpenProcessManager, canOpenProcessManager);
        OpenResourceMonitorCommand = ReactiveCommand.Create(OpenResourceMonitor, canOpenProcessManager);
        // 命令注入状态栏,而不是让状态栏去 $parent[Window].DataContext 找:
        // 视图加载早于窗口 DataContext 赋值,跨树查找会在启动时刷一条绑定错误。
        StatusBar.OpenResourceMonitorCommand = OpenResourceMonitorCommand;
        // 状态栏点编码 → 当场切当前会话的编码。只影响本会话,不写回设置:
        // 用户多半是在试"这台机器到底是 GBK 还是 UTF-8",试错不该污染全局配置。
        StatusBar.AvailableEncodings = TerminalEncodings.All;
        StatusBar.ChangeEncodingCommand = ReactiveCommand.Create<string>(ChangeActiveTabEncoding);
        OpenTraceRouteCommand = ReactiveCommand.Create(OpenTraceRoute, canToggleFileBrowser);
        CloseActiveTabCommand = ReactiveCommand.Create(CloseActiveTab);
        RegisterCommands();
        RunCommand = ReactiveCommand.Create<string>(id => Commands.Execute(id));
        ShortcutCommand = ReactiveCommand.Create<string>(id => ExecuteShortcut(id));
        localization?.LanguageChanged += _ => RefreshLocalizedText();
    }

    /// <summary>
    /// 换语言后重算 C# 侧拼好存着的文案 —— XAML 里的 {loc:Localize} 自己会刷新,这些不会:
    /// 命令的标题与分类(注册时就取好了,命令面板照着显示)、标题栏 X Server 按钮的悬停提示、
    /// 会话树的置顶菜单项 / 状态标签 / 「未分组」、最近连接的相对时间与提示、状态栏、
    /// 消息中心、隧道面板、终端标签的连接 / 断开覆盖层。
    /// </summary>
    private void RefreshLocalizedText()
    {
        // 按 id 重新注册即替换,顺序不变
        RegisterCommands();
        XServer.RefreshLocalizedText();
        Sidebar.SessionTree?.RefreshLocalizedText();
        Sidebar.RecentConnections.RefreshLocalizedText();
        StatusBar.RefreshLocalizedText();
        if (ActiveTerminalTab is null)
        {
            StatusBar.ClearSessionMetrics();
        }
        UpdateStatusBarForActiveTab();
        NotificationPanel?.RefreshLocalizedText();
        TunnelPanel?.RefreshLocalizedText();
        foreach (TerminalTabViewModel tab in TerminalTabs)
        {
            tab.RefreshLocalizedText();
        }
    }

    /// <summary>
    /// 菜单栏、命令面板与快捷键共用的单条命令来源(设计稿 §4A.1)——每个入口展示的名称、提示与行为都一致。
    /// </summary>
    public ICommandRegistry Commands { get; }

    /// <summary>通过 id 执行一条注册命令(菜单项通过 CommandParameter 使用)。</summary>
    public ReactiveCommand<string, RxVoid>? RunCommand { get; private set; }

    /// <summary>
    /// 键位绑定执行的命令(主窗口按键位表登记的 KeyBindings 都指向它),参数是绑定的命令 id。
    /// </summary>
    public ReactiveCommand<string, RxVoid> ShortcutCommand { get; }

    /// <summary>
    /// 执行一条键位绑定:命令注册表里的直接执行;标签循环(<c>tab.next</c> / <c>tab.prev</c>)
    /// 不进命令面板,在这里单独接。
    /// </summary>
    /// <returns>命令是否真的执行了(不存在或当前不可用时为 false)。</returns>
    public bool ExecuteShortcut(string commandId)
    {
        switch (commandId)
        {
            case "tab.next":
                NextTabCommand.Execute().Subscribe();
                return true;
            case "tab.prev":
                PreviousTabCommand.Execute().Subscribe();
                return true;
            default:
                return Commands.Execute(commandId);
        }
    }

    /// <summary>活动会话的隧道管理面板(设计 fuXS7,规范 §10)。</summary>
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

    /// <summary>消息中心面板(侧边栏铃铛)。</summary>
    public NotificationPanelViewModel? NotificationPanel
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>消息中心面板当前是否展开显示。</summary>
    public bool IsNotificationPanelOpen
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>活动标签的自绘终端控件(当活动标签存在时)。</summary>
    private VelaTerminalControl? ActiveTerminalControl =>
        ActiveTerminalTab?.TerminalEmulator.Control as VelaTerminalControl;

    /// <summary>Ctrl+P 命令面板浮层。</summary>
    public CommandPaletteViewModel CommandPalette { get; }

    /// <summary>打开命令面板(Ctrl+P)的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> OpenCommandPaletteCommand { get; }

    /// <summary>显示或隐藏当前 SSH 会话的远程文件面板。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ToggleFileBrowserCommand { get; }

    /// <summary>把左侧资源管理器折叠成 40px 图标细条,或还原成完整侧栏(Ctrl+B / 命令面板)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ToggleSidebarCommand { get; }

    /// <summary>
    /// 折叠/展开左侧资源管理器。列宽的收放在 <see cref="Views.MainWindow" /> 里做,
    /// 这里只翻状态位 —— Ctrl+B 与命令面板走这条;侧栏工具栏的折叠按钮与细条顶部的
    /// 展开按钮各自直接置位(它们只出现在其中一态,取反反而绕)。
    /// </summary>
    public void ToggleSidebar() => Sidebar.IsCollapsed = !Sidebar.IsCollapsed;

    /// <summary>当前活动标签是否支持打开远程文件面板。</summary>
    public bool CanToggleFileBrowser =>
        _sftpService is not null
        && ActiveTerminalTab is { IsConnected: true, Profile: not null } tab
        && tab.SessionId != Guid.Empty;

    /// <summary>
    /// 任务管理器是否可用:必须是一个已连接的 SSH 终端标签。本地终端(LocalShell 非空)
    /// 没有远端可管;聚焦 SFTP 标签时 ActiveTerminalTab 已被置空(见 SetActiveFromDocument),
    /// 两种情况按钮都自动变灰。
    /// </summary>
    public bool CanOpenProcessManager =>
        _remoteProcessService is not null
        && ActiveTerminalTab is { IsConnected: true, Profile: not null, LocalShell: null } tab
        && tab.SessionId != Guid.Empty;

    /// <summary>
    /// 由窗口注入的交互式身份验证(两步弹窗):补全用户名/密码/密钥后返回更新的配置,
    /// 取消时返回 null。
    /// </summary>
    public Func<SessionProfile, Task<SessionProfile?>>? InteractiveAuthenticator { get; set; }

    /// <summary>
    /// FTPS 服务器证书未通过校验时的信任提示;返回 true 表示用户同意信任该指纹。
    /// 由 View 层挂上(与 <see cref="InteractiveAuthenticator" /> 同样的手法);未挂时按拒绝处理。
    /// </summary>
    public Func<SessionProfile, VelaFtpCertificateException, Task<bool>>? FtpCertificateTrustPrompt { get; set; }

    /// <summary>
    /// 插件协议端点的证书未通过校验时的信任提示;返回 true 表示用户同意信任该指纹。
    /// 自行部署的 MinIO / Ceph 用自签证书是常态,没有这条路径这类端点根本连不上。
    /// </summary>
    public Func<SessionProfile, PluginProtocolCertificateException, Task<bool>>? PluginCertificateTrustPrompt { get; set; }

    /// <summary>
    /// 文档型连接(SFTP / FTP / 插件文件系统 / 工作台)首次连接失败时的提示弹窗。
    /// <para>
    /// SSH 标签的失败画在标签页内的覆盖层上(设计 yxjmg),所以它不需要这条路径;而上面这四类
    /// <b>连不上就根本没有标签页</b> —— 失败原因只落到状态栏的话,用户看到的就是"点了连接,
    /// 什么都没发生"。本地没起 Redis 时点开一条 Redis 会话正是这个样子。
    /// </para>
    /// <para>
    /// 由 View 层挂上(与 <see cref="InteractiveAuthenticator" /> 同样的手法);未挂时(headless
    /// 测试、插件代开会话)退回状态栏,不影响连接流程本身。
    /// </para>
    /// </summary>
    public Func<SessionProfile, string, Task>? ConnectionFailureReporter { get; set; }

    /// <summary>最近一次连接的错误消息,若上次尝试成功则为 null。</summary>
    public string? LastConnectionError
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>窗口注入的多行粘贴确认弹窗(设置 → 终端 → 粘贴时确认多行内容)。</summary>
    public Func<string, Task<bool>>? MultilinePasteConfirmer { get; set; }

    /// <summary>
    /// 窗口注入的快捷命令变量询问框:参数是命令名、解析好的模板与每一格的初值(上一次填的值,
    /// 没记过的是占位的默认值),确认时返回「变量名 → 值」,取消返回 null(整条不发)。
    /// 未挂时(headless)含占位的命令原样发送。
    /// </summary>
    public Func<string?, QuickCommandTemplate, IReadOnlyDictionary<string, string>, Task<IReadOnlyDictionary<string, string>?>>? QuickCommandVariablePrompt { get; set; }

    /// <summary>询问框的「上一次填的值」,只在进程里(见 <see cref="QuickCommandVariableMemory" />)。</summary>
    private readonly QuickCommandVariableMemory _quickCommandVariables = new();

    /// <summary>
    /// 窗口注入的多行快捷命令确认框:目标终端里有没开括号粘贴的,多行命令发过去会逐行执行,先问一句。
    /// 参数是将要发送的正文,确认返回 true。未挂时(headless)直接发送。
    /// </summary>
    public Func<string, Task<bool>>? QuickCommandMultilineConfirmer { get; set; }

    /// <summary>
    /// 窗口注入的「重跑多行命令」确认框:对端没开括号粘贴时每一行都会当场执行,先问一句
    /// (开关同样是「粘贴时确认多行内容」)。参数是命令文本,确认返回 true。未挂时(headless)直接发送。
    /// </summary>
    public Func<string, Task<bool>>? CommandRerunConfirmer { get; set; }

    /// <summary>左侧边栏视图模型:资源管理器会话树与最近连接。</summary>
    public SidebarViewModel Sidebar
    {
        get => _sidebar;
        set => this.RaiseAndSetIfChanged(ref _sidebar, value);
    }

    /// <summary>切到当前标签组的下一个标签(Ctrl+Tab),到尾回绕。</summary>
    public ReactiveCommand<RxVoid, RxVoid> NextTabCommand =>
        field ??= ReactiveCommand.Create(() => CycleTab(1));

    /// <summary>切到当前标签组的上一个标签(Ctrl+Shift+Tab),到头回绕。</summary>
    public ReactiveCommand<RxVoid, RxVoid> PreviousTabCommand =>
        field ??= ReactiveCommand.Create(() => CycleTab(-1));

    /// <summary>
    /// 在当前标签组内循环切换标签。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只在<b>当前组</b>内循环,而不是全布局的所有文档:分屏之后 Ctrl+Tab 跳到另一个窗格里
    /// 会让人完全找不到北 —— 焦点跑了,而视线还停在原来那半边。
    /// </para>
    /// <para>
    /// 循环范围是组里的<b>全部</b>文档,不只是终端。这与改动前的行为有一处差别:
    /// 原先它循环的是 <c>TabBar.Tabs</c>(只有终端标签),于是同时开着终端与 SFTP 面板时,
    /// Ctrl+Tab 永远到不了 SFTP 那一页 —— 那更像是双模型留下的疏漏,而不是有意设计。
    /// </para>
    /// </remarks>
    /// <param name="step">+1 = 下一个,-1 = 上一个。</param>
    private void CycleTab(int step)
    {
        DockGroup? group = Layout.ActiveDocument is { } active
            ? Layout.FindGroup(active) ?? Layout.PrimaryGroup
            : Layout.PrimaryGroup;
        if (group is null || group.Documents.Count == 0)
        {
            return;
        }
        int current = group.ActiveDocument is { } activeDocument
            ? group.Documents.IndexOf(activeDocument)
            : 0;
        int count = group.Documents.Count;
        int next = (((current + step) % count) + count) % count;
        Layout.ActivateDocument(group.Documents[next]);
        TerminalFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 插件终端能力经此按会话 id 解析到仿真器与人类可读标签(<see cref="Services.Plugins.ITerminalResolver" />)。
    /// </summary>
    (ITerminalEmulator Emulator, string Label)? Services.Plugins.ITerminalResolver.Resolve(Guid sessionId)
    {
        foreach (TerminalTabViewModel tab in TerminalTabs)
        {
            if (tab.SessionId == sessionId)
            {
                string label = tab.Profile is { } p
                    ? (string.IsNullOrWhiteSpace(p.Name) ? p.Host : p.Name)
                    : sessionId.ToString("N")[..8];
                return (tab.TerminalEmulator, label);
            }
        }
        return null;
    }

    /// <summary>给活动会话发 BREAK,执行了没有用一条提示说出来(没执行多半是服务端不支持,或者会话没有终端)。</summary>
    private async Task SendBreakToActiveSessionAsync()
    {
        if (ActiveTerminalTab?.ShellStream is not { SupportsBreak: true } stream)
        {
            return;
        }

        if (await stream.SendBreakAsync(CancellationToken.None))
        {
            Toasts.Info(Strings.Get("Toast_BreakSent"));
        }
        else
        {
            Toasts.Warning(Strings.Get("Toast_BreakNotPerformed"));
        }
    }

    /// <summary>
    /// 运行时反馈的浮层通道(断线、重连倒计时、连接失败、导出成功……)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这些消息以前全往 <c>StatusBar.Status</c> 那一个字符串里写,后写覆盖先写:
    /// 三条消息挤在一秒内到达时,用户只会看到最后一条,而那条未必是最要紧的;
    /// 错误与"已导出"也没有任何分级差别。
    /// </para>
    /// <para>
    /// 还顺带修掉一个具体缺陷:<c>StatusBar.Status</c> 的 setter 会按
    /// <c>value == Strings.Connected</c> 推出 <c>IsConnected</c> —— 于是往它写任何一条
    /// 瞬时消息,状态栏那个连接圆点就变灰,尽管会话好好的。改道之后 <c>Status</c>
    /// 只承载连接状态本身,那个推断重新成立。
    /// </para>
    /// </remarks>
    public ToastHostViewModel Toasts { get; } = new(
        // 到期回调要在 UI 线程上撤提示;DispatcherTimer.RunOnce 是本仓一贯的做法
        // (侧栏动画、认证后命令延迟都用它),返回的定时器停掉即取消。
        (delay, callback) =>
        {
            DispatcherTimer timer = new() { Interval = delay };
            timer.Tick += (_, _) =>
            {
                // DispatcherTimer 被调度器强引用,不停表会一直唤醒窗口(同 TerminalTabView 的处置)。
                timer.Stop();
                callback();
            };
            timer.Start();
            return new TimerHandle(timer);
        });

    /// <summary>把一个 <see cref="DispatcherTimer" /> 包成可释放的取消句柄。</summary>
    private sealed class TimerHandle(DispatcherTimer timer) : IDisposable
    {
        /// <inheritdoc />
        public void Dispose() => timer.Stop();
    }

    /// <summary>
    /// 当前打开的全部终端标签,按停靠布局里的顺序。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="Layout" /> 是标签集合的唯一事实来源。</b>VelaDock 落地之前,
    /// <c>TabBarViewModel</c> 另存了一份平行的标签列表,于是每一次新增 / 关闭 / 激活
    /// 都要在两个模型之间手工同步 —— 漏一处的表现是"标签关掉了但树上状态还亮着"
    /// 或者"分屏之后 Ctrl+Tab 跳错窗格",而两处的症状毫不相干,极难联想到同一个根因
    /// (§24 / §39 修过两次的就是这类同形 bug)。
    /// </para>
    /// <para>
    /// 每次访问都重新枚举,不做缓存:停靠树本来就很小(几个组、十几个文档),
    /// 而缓存一份就等于把刚拆掉的那份平行状态又请了回来。
    /// </para>
    /// </remarks>
    public IEnumerable<TerminalTabViewModel> TerminalTabs =>
        Layout.AllDocuments().OfType<TerminalDocument>().Select(document => document.Terminal);

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

    /// <summary>VelaDock 工作区:承载终端文档(标签可拖拽重排、拆分分屏)。</summary>
    public DockWorkspace Layout { get; }

    /// <summary>右侧 AI 专属区,独立于文档标签与终端分屏。</summary>
    public AiAssistantRegionViewModel AiAssistant { get; } = new();

    /// <summary>命名布局模板:把当前分屏存成模板、打开模板,以及「恢复会话」按上次的分屏还原。</summary>
    public LayoutTemplatesViewModel LayoutTemplates { get; }

    /// <summary>还原布局时按 Id 从库里取配置(取最新的那份,不用模板保存时的旧副本);没有仓储(无界面单测)时一律取不到。</summary>
    private Task<SessionProfile?> FindProfileForLayoutAsync(Guid profileId) =>
        _sessionRepository?.GetSessionAsync(profileId) ?? Task.FromResult<SessionProfile?>(null);

    /// <summary>当前会话的 SFTP 文件浏览面板(按会话缓存,随活动标签切换重绑)。</summary>
    public FileBrowserViewModel FileBrowser
    {
        get => _fileBrowser;
        set => this.RaiseAndSetIfChanged(ref _fileBrowser, value);
    }

    /// <summary>链路追踪服务;窗口打开时用它构造面板视图模型。</summary>
    public ITraceRouteService? TraceRouteService { get; }

    /// <summary>文件传输面板视图模型:承载上传/下载任务队列与进度。</summary>
    public FileTransferViewModel FileTransfer
    {
        get => _fileTransfer;
        set => this.RaiseAndSetIfChanged(ref _fileTransfer, value);
    }

    /// <summary>打开设置窗口的命令(Ctrl+, / 菜单 / 侧边栏齿轮)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> OpenSettingsCommand { get; }

    /// <summary>关闭当前活动标签(Ctrl+W);走停靠层的关闭语义,保证传输层同时拆除。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CloseActiveTabCommand { get; }

    /// <summary>打开当前 SSH 会话的任务管理器;本地终端与 SFTP 标签下不可用。</summary>
    public ReactiveCommand<RxVoid, RxVoid> OpenProcessManagerCommand { get; }

    /// <summary>打开链路追踪窗口;启用条件与 SFTP 资源管理器一致。</summary>
    public ReactiveCommand<RxVoid, RxVoid> OpenTraceRouteCommand { get; }

    /// <summary>标题栏的本机 X Server 开关(Windows 上拉起 VcXsrv,显示远端 X11 转发过来的图形程序)。</summary>
    public XServerToggleViewModel XServer { get; }

    /// <summary>
    /// 请求为某个会话打开任务管理器窗口。参数依次为会话标识与窗口标题用的会话名称;
    /// 由 MainWindow 承接(视图层才建得了窗口)。
    /// </summary>
    public event Action<Guid, string>? ProcessManagerRequested;

    /// <summary>把打开任务管理器的请求转交视图层;条件不满足时是空操作。</summary>
    private void OpenProcessManager()
    {
        if (!CanOpenProcessManager || ActiveTerminalTab is not { Profile: { } profile } tab)
        {
            return;
        }
        string label = string.IsNullOrWhiteSpace(profile.Name)
                           ? HostAddress.FormatEndPoint(profile.Host, profile.Port)
                           : profile.Name;
        ProcessManagerRequested?.Invoke(tab.SessionId, label);
    }

    /// <summary>打开当前 SSH 会话的资源监视窗口(状态栏右下角的指示器按钮)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> OpenResourceMonitorCommand { get; }

    /// <summary>
    /// 请求为某个会话打开资源监视窗口。参数依次为会话标识与窗口标题用的会话名称;
    /// 由 MainWindow 承接(视图层才建得了窗口)。
    /// </summary>
    public event Action<Guid, string>? ResourceMonitorRequested;

    /// <summary>把打开资源监视窗口的请求转交视图层;条件不满足时是空操作。</summary>
    private void OpenResourceMonitor()
    {
        if (!CanOpenProcessManager || ActiveTerminalTab is not { Profile: { } profile } tab)
        {
            return;
        }
        string label = string.IsNullOrWhiteSpace(profile.Name)
                           ? HostAddress.FormatEndPoint(profile.Host, profile.Port)
                           : profile.Name;
        ResourceMonitorRequested?.Invoke(tab.SessionId, label);
    }

    private void RegisterCommands()
    {
        Commands.Register(
            new(
                "session.new",
                Strings.Get("Cmd_NewSshConnection"),
                Strings.Get("CmdCat_Session"),
                () => _ = OpenNewConnectionAsync(),
                Icon: "Icon.plus"
            )
        );
        Commands.Register(
            new(
                "session.close",
                Strings.Get("Cmd_CloseCurrentSession"),
                Strings.Get("CmdCat_Session"),
                () => CloseActiveTabCommand.Execute().Subscribe(),
                () => Layout.ActiveDocument is not null
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
                Icon: "Icon.copy"
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
                Icon: "Icon.search"
            )
        );
        Commands.Register(new(
            "terminal.fillSharedPassword", Strings.Get("PasswordFill_Title"), Strings.Get("CmdCat_Session"),
            () => SharedPasswordFillRequested?.Invoke(this, EventArgs.Empty),
            () => ActiveTerminalTab is { IsConnected: true, Bridge: not null }, Icon: "Icon.key-round"));
        // 隧道独立于终端会话(后台自动连接),无活动标签也可用。
        Commands.Register(
            new(
                "tools.tunnel",
                Strings.Get("Cmd_TunnelManager"),
                Strings.Get("CmdCat_Tools"),
                ToggleTunnelPanel,
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
                Icon: "Icon.folder"
            )
        );
        Commands.Register(
            new(
                "tools.processes",
                Strings.Get("Cmd_ProcessManager"),
                Strings.Get("CmdCat_Tools"),
                () => OpenProcessManagerCommand.Execute().Subscribe(),
                () => CanOpenProcessManager,
                Icon: "Icon.activity"
            )
        );
        // 本机 X Server:与标题栏按钮同一个开关。只在支持的平台(Windows)上登记,
        // 别的平台上命令面板里出现一个永远点不动的条目只会让人困惑。
        if (XServer.IsSupported)
        {
            Commands.Register(
                new(
                    "tools.xserver",
                    Strings.Get("Cmd_ToggleXServer"),
                    Strings.Get("CmdCat_Tools"),
                    () => XServer.ToggleCommand.Execute().Subscribe(),
                    () => !XServer.IsStarting,
                    Icon: "Icon.xorg"
                )
            );
        }
        Commands.Register(
            new(
                "tools.healthCheck",
                Strings.Get("Cmd_HealthCheckAll"),
                Strings.Get("CmdCat_Tools"),
                () => _ = RequestHealthCheckForAllAsync(),
                () => _sessionRepository is not null,
                Icon: "Icon.activity"
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
                "tools.connectionInfo",
                Strings.Get("Cmd_ConnectionInfo"),
                Strings.Get("CmdCat_Tools"),
                () => OpenConnectionInfo(ActiveTerminalTab),
                () => ActiveTerminalTab is { IsSshTerminal: true, IsConnected: true },
                Icon: "Icon.info"
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
        // BREAK(RFC 4335):经 SSH 连串口控制台服务器、网络设备的 console 时进 ROMMON / 引导菜单用。
        // 只有 SSH 会话支持;服务端执行了没有用一条提示说出来。
        Commands.Register(
            new(
                "session.break",
                Strings.Get("Cmd_SendBreak"),
                Strings.Get("CmdCat_Session"),
                () => _ = SendBreakToActiveSessionAsync(),
                () => ActiveTerminalTab is { ConnectionStatus: SessionStatus.Connected, ShellStream.SupportsBreak: true }
            )
        );

        // 字号缩放:与 Ctrl+滚轮同源(都走 VelaTerminalControl.AdjustFontSize → FontSizeChanged),
        // 因此缩放结果会跟着现有那条链路持久化,不需要第二套。
        Commands.Register(
            new(
                "view.zoom.in",
                Strings.Get("Cmd_ZoomIn"),
                Strings.Get("CmdCat_Edit"),
                () => ActiveTerminalControl?.AdjustFontSize(1),
                () => ActiveTerminalControl is not null
            )
        );
        Commands.Register(
            new(
                "view.zoom.out",
                Strings.Get("Cmd_ZoomOut"),
                Strings.Get("CmdCat_Edit"),
                () => ActiveTerminalControl?.AdjustFontSize(-1),
                () => ActiveTerminalControl is not null
            )
        );
        Commands.Register(
            new(
                "view.zoom.reset",
                Strings.Get("Cmd_ZoomReset"),
                Strings.Get("CmdCat_Edit"),
                () => ActiveTerminalControl?.ResetFontSize(_latestSettings?.TerminalFontSize ?? 14),
                () => ActiveTerminalControl is not null
            )
        );

        // 跳到第 N 个标签:按**活动组内**的第 N 个,而不是全局标签集合 ——
        // 分屏后每个组有自己的标签条,用户数的是眼前那一条。
        for (int slot = 1; slot <= 8; slot++)
        {
            int captured = slot;
            Commands.Register(
                new(
                    $"tab.goto.{slot}",
                    Strings.Format("Cmd_GotoTab", slot),
                    Strings.Get("CmdCat_Session"),
                    () => GotoTab(captured - 1),
                    () => Layout.AllDocuments().Any()
                )
            );
        }
        Commands.Register(
            new(
                "tab.goto.last",
                Strings.Get("Cmd_GotoLastTab"),
                Strings.Get("CmdCat_Session"),
                () => GotoTab(-1),
                () => Layout.AllDocuments().Any()
            )
        );

        // 窗格移焦。这几条**不进** Window.KeyBindings —— 那里是无条件抢键,而 Alt+方向
        // 在 zsh / fish 里有用户绑定。改由 MainWindow 的隧道处理器在"确有多个窗格"时才吃掉。
        RegisterPaneFocusCommand("pane.focus.left", "Cmd_FocusPaneLeft", DockDirection.Left, "Alt+Left");
        RegisterPaneFocusCommand("pane.focus.right", "Cmd_FocusPaneRight", DockDirection.Right, "Alt+Right");
        RegisterPaneFocusCommand("pane.focus.up", "Cmd_FocusPaneUp", DockDirection.Up, "Alt+Up");
        RegisterPaneFocusCommand("pane.focus.down", "Cmd_FocusPaneDown", DockDirection.Down, "Alt+Down");

        Commands.Register(
            new(
                "session.close.all",
                Strings.Get("Cmd_CloseAllTabs"),
                Strings.Get("CmdCat_Session"),
                CloseAllTabs,
                () => Layout.AllDocuments().Any()
            )
        );
        Commands.Register(
            new(
                "terminal.linegutter",
                Strings.Get("Cmd_ToggleLineGutter"),
                Strings.Get("CmdCat_Edit"),
                ToggleLineGutter
            )
        );
        Commands.Register(
            new(
                "view.sidebar",
                Strings.Get("Cmd_ToggleSidebar"),
                Strings.Get("CmdCat_Actions"),
                ToggleSidebar,
                Icon: "Icon.panel-left"
            )
        );
        Commands.Register(
            new(
                "app.settings",
                Strings.Get("Cmd_OpenSettings"),
                Strings.Get("CmdCat_Edit"),
                () => OpenSettingsCommand.Execute().Subscribe(),
                Icon: "Icon.settings"
            )
        );
        Commands.Register(
            new(
                "app.settings.about",
                Strings.Get("Cmd_OpenAbout"),
                Strings.Get("CmdCat_Edit"),
                () => SettingsSectionRequested?.Invoke(this, SettingsSectionKey.About),
                Icon: "Icon.info"
            )
        );
        // 安全审计页:消息中心里「云同步拉到的设置开着 Webhook 告警却没有地址」那一条的动作。
        Commands.Register(
            new(
                NotificationSources.OpenSecuritySettingsCommandId,
                Strings.Get("Cmd_OpenSecuritySettings"),
                Strings.Get("CmdCat_Edit"),
                () => SettingsSectionRequested?.Invoke(this, SettingsSectionKey.Security),
                Icon: "Icon.shield"
            )
        );
        Commands.Register(
            new(
                "app.logs.open",
                Strings.Get("Cmd_OpenLogs"),
                Strings.Get("CmdCat_Actions"),
                () => DiagnosticLog.OpenLogsDirectory(),
                Icon: "Icon.folder-open"
            )
        );
        // 消息中心里「外部编辑的改动没传回服务器」那几条的动作;没有草稿目录时灰掉。
        Commands.Register(
            new(
                NotificationSources.OpenRemoteEditDraftsCommandId,
                Strings.Get("Cmd_OpenRemoteEditDrafts"),
                Strings.Get("CmdCat_Actions"),
                () => RemoteEditDrafts.OpenDraftsFolder(RemoteEditSessionManager.TempRoot),
                () => Directory.Exists(RemoteEditSessionManager.TempRoot),
                Icon: "Icon.folder-open"
            )
        );
        Commands.Register(
            new(
                "app.palette",
                Strings.Get("Cmd_CommandPalette"),
                Strings.Get("CmdCat_Search"),
                CommandPalette.Open,
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
        // 窗格最大化(tmux 的 resize-pane -Z):分屏之后想把某一格看仔细,不必先把布局
        // 拆了、看完再照原样拼回去 —— 那两步手工活正是分屏用起来累的原因。
        Commands.Register(
            new(
                "pane.maximize",
                Strings.Get("Dock_ToggleMaximizePane"),
                Strings.Get("CmdCat_Actions"),
                () =>
                {
                    if (Layout.ActiveDocument is { } document && Layout.FindGroup(document) is { } group)
                    {
                        Layout.ToggleMaximizeGroup(group);
                    }
                },
                () => Layout.HasMultipleGroups,
                Icon: "Icon.maximize"
            )
        );
        // 平分窗格:分割条拖歪之后的一键复位(双击任意一条分割条只平分那一条所在的分栏)。
        Commands.Register(
            new(
                "pane.equalize",
                Strings.Get("Dock_EqualizePanes"),
                Strings.Get("CmdCat_Actions"),
                Layout.EqualizePanes,
                () => Layout.HasMultipleGroups,
                Icon: "Icon.layout-grid"
            )
        );
        // 布局模板:把当前分屏存成模板 / 打开管理窗口。每个模板本身另作一条可直接打开的条目
        // 出现在命令面板里(见 BuildPaletteItems)。
        Commands.Register(
            new(
                "layout.save",
                Strings.Get("LayoutTemplate_SaveCurrent"),
                Strings.Get("CmdCat_Actions"),
                () => _ = LayoutTemplates.SaveCurrentAsync(),
                () => Layout.AllDocuments().Any(),
                Icon: "Icon.layout-template"
            )
        );
        Commands.Register(
            new(
                "layout.manage",
                Strings.Get("LayoutTemplate_Manage"),
                Strings.Get("CmdCat_Actions"),
                LayoutTemplates.OpenManager,
                Icon: "Icon.layout-template"
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
            ? await _settingsService.GetSnapshotAsync()
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
        // shell 退出(exit)后覆盖层上的“关闭标签”按钮靠这条订阅生效;缺了它本地终端
        // 标签退出后点关闭没有任何反应。
        terminalTab.CloseRequested += (_, _) => CloseTerminalTab(terminalTab);

        // 命令补全:注入建议提供器;提交(已回显校验)的命令进全局历史。
        terminalTab.SuggestionProvider = _suggestionProvider;
        terminalTab.QuickCommandTextResolver = command =>
            ResolveQuickCommandTextAsync(command.Id, command.Name, command.CommandText, terminalTab);
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
        // Layout.AddDocument 自带激活(见 DockWorkspace),ActiveTerminalTab 由
        // ActiveDocumentChanged 派生 —— 不必也不该在这里再手工设一遍。
        Layout.AddDocument(document);
        UpdateStatusBarForActiveTab();
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                await AttachLocalShellAsync(terminalTab, shell, settings).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            await RemoveTerminalTabAsync(terminalTab, document).ConfigureAwait(true);
            LastConnectionError = Strings.Format(
                "Msg_LocalShellStartFailed",
                shell.Name,
                ex.Message
            );
            Toasts.Error(LastConnectionError);
        }
    }

    /// <summary>重开本地终端标签:RIS 清屏后重新拉起 shell(与 SSH 重连同语义)。</summary>
    private async Task ReopenLocalShellAsync(TerminalTabViewModel tab, LocalShellInfo shell)
    {
        tab.ConnectionStatus = SessionStatus.Connecting;
        await tab.DetachTransportAsync().ConfigureAwait(true);
        try
        {
            tab.TerminalEmulator.Feed(RisResetSequence);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                await AttachLocalShellAsync(tab, shell, _latestSettings ?? new AppSettings()).ConfigureAwait(true);
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
            Toasts.Error(LastConnectionError);
        }
    }

    /// <summary>
    /// 拉起本地 shell 进程并挂上标签(打开与重开共用)。
    /// </summary>
    [SupportedOSPlatform(nameof(OSPlatform.Windows))]
    private async Task AttachLocalShellAsync(
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
        await tab.AttachTransportAsync(stream).ConfigureAwait(true);
        tab.Start();
        tab.ConnectionStatus = SessionStatus.Connected;
        tab.ResetReconnectAttempts();
        StartSessionLogging(tab, settings);
        UpdateStatusBarForActiveTab();
    }

    /// <summary>创建一个空白占位 FileBrowserViewModel 用于隐藏底部面板。</summary>
    private FileBrowserViewModel CreatePlaceholderFileBrowser() =>
        new(_sftpService, Guid.Empty) { TransferSink = FileTransfer };

    /// <summary>
    /// 将 SFTP 文件浏览器指向活动标签的会话(#22)。每个已连接标签有一个根植于自己会话的浏览器;
    /// 未连接会话时面板显示为空。
    /// 面板实例按会话缓存:切回已看过的标签直接复用(旧列表秒显 + 后台静默刷新),
    /// 保留浏览路径/排序/列宽,不再每次切换都重建对象、重新列目录。
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

        // 这三种标签都没有可用的 SFTP 会话,不得继续展示上一个 SSH 会话的文件面板:
        //   1. 本地终端(ConPTY)与插件终端协议(Telnet…)—— 它们压根没有 SFTP 通道,
        //      否则切到 PowerShell / Telnet 标签后下方仍显示上一个 SSH 会话的文件面板;
        //   2. 还在握手的 SSH 标签 —— 会话 id 要到握手完成才分配(见 RunHandshakeAsync),
        //      在那之前 SessionId 是 Guid.Empty。这一条以前只是 return,于是新开标签期间
        //      下方仍挂着上一个会话的文件面板,直到连上才换掉(#385)。
        // 统一换成隐藏的空占位;上一个面板不 Detach(仍按其会话缓存),其开关状态留在
        // 缓存实例与所属标签上,切回那个标签时恢复展示。
        if (tab.LocalShell is not null
            || tab.Profile?.ConnectionType == ConnectionType.Plugin
            || tab.SessionId == Guid.Empty)
        {
            if (FileBrowser.SessionId != Guid.Empty || FileBrowser.IsVisible)
            {
                FileBrowser = CreatePlaceholderFileBrowser();
            }
            return;
        }
        if (FileBrowser.SessionId == tab.SessionId)
        {
            return;
        }

        // 切回看过的标签:面板照它自己的状态恢复(开着的自动展示并静默刷新,
        // 关着的保持隐藏、不加载数据),与其他标签的开关互不影响。
        if (_fileBrowserCache.TryGetValue(tab.SessionId, out FileBrowserViewModel? cached))
        {
            FileBrowser = cached;
            if (cached.IsVisible)
            {
                _ = cached.RefreshSilentlyAsync();
            }
            return;
        }

        // 本标签首次建面板:初始开关取标签生命周期内记忆的状态(断线重连沿用),
        // 没有记忆时按设置「连接后自动打开文件浏览器」的当前值决定。
        bool wasVisible = tab.FileBrowserOpen
            ?? _latestSettings?.TerminalBehavior.AutoOpenFileBrowser
            ?? new TerminalBehaviorOptions().AutoOpenFileBrowser;
        tab.FileBrowserOpen = wasVisible;

        string serverName = tab.Profile is { } profile
            ? string.IsNullOrWhiteSpace(profile.Name)
                ? profile.Host
                : profile.Name
            : tab.Title;
        var browser = new FileBrowserViewModel(_sftpService, tab.SessionId)
        {
            TransferSink = FileTransfer,
            // 上传 / 下载批次记进传输队列,断线或退出时没传完的按这条配置找回连接接着传。
            QueueOrigin = tab.Profile is { } origin ? new(origin.Id, serverName) : null,
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
            AccentBrush = tab.Profile is { } p ? ConnectionAccent.BrushForProfile(p) : null,
            RemoteSearch = _remoteSearch,
        };
        // 「文件浏览器跟随终端目录」(map-pin):该会话终端 shell 的 cwd(OSC 7)变化 → 面板同步(仅在开启跟随时)。
        // 先播种当前已知 cwd(供开启开关时立即同步),再订阅后续变化。二者同会话、同生共死,eviction 时解绑。
        if (tab.TerminalWorkingDirectory is { } cwd)
        {
            browser.OnTerminalWorkingDirectoryChanged(cwd);
        }
        tab.WorkingDirectoryChanged += browser.OnTerminalWorkingDirectoryChanged;
        // 右键「在此处打开终端」:终端侧栏的面板挂在 SSH 会话上(插件终端与本地 shell 前面已经排除了)。
        WireOpenTerminalHere(browser, () => tab.Profile);

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
            // 解绑「跟随终端目录」订阅(该会话的终端标签 → 面板),再拆除面板。
            if (TerminalTabs.FirstOrDefault(t => t.SessionId == sessionId) is { } tab)
            {
                tab.WorkingDirectoryChanged -= cached.OnTerminalWorkingDirectoryChanged;
            }
            cached.Detach();
        }
        if (FileBrowser.SessionId != sessionId)
        {
            return;
        }
        FileBrowser.Detach();

        // 会话已死,面板收起(空面板没有可看内容);该标签的开关状态留在
        // TerminalTabViewModel.FileBrowserOpen(对象替换不触发状态跟踪),
        // 重连后由 RebindFileBrowser 按标签记忆恢复,不会被这里的隐藏传染。
        FileBrowser = new(_sftpService, Guid.Empty) { TransferSink = FileTransfer };
    }

    /// <summary>
    /// 把面板实例上的显示/隐藏变化回写到拥有该会话的标签
    /// (<see cref="TerminalTabViewModel.FileBrowserOpen" />),作为该标签的生命周期记忆。
    /// 只记在标签上:面板开/关是会话级的临时状态,不回写设置项「连接后自动打开
    /// 文件浏览器」—— 该设置只由用户在设置页改动(#377)。
    /// </summary>
    private void RememberFileBrowserStateForTab(Guid sessionId, bool visible)
    {
        if (sessionId == Guid.Empty)
        {
            return;
        }
        TerminalTabViewModel? owner = TerminalTabs.FirstOrDefault(t => t.SessionId == sessionId);
        owner?.FileBrowserOpen = visible;
    }

    /// <summary>
    /// 接着传一批停下的传输(断线之后,或上次退出时还没传完的)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 按批次记下的连接配置找连接:先找已经连着这条配置的文件标签,再找打开过文件面板的终端标签;
    /// 都没有就照常打开一个文件标签(SSH / SFTP、FTP、插件文件协议各走自己的那条打开路径,
    /// 认证弹窗、指纹核对、失败卡片与平时双击连接一模一样)。启动时不做这件事 —— 只在用户点「继续」时才连。
    /// </para>
    /// <para>
    /// 配置已经删了:不去猜它原来连的是哪台(主机名可能早已指向别处),如实说「配置已删除」,只能放弃。
    /// </para>
    /// </remarks>
    private async Task<TransferResumeOutcome> ResumeQueuedTransfersAsync(PausedTransferBatch batch)
    {
        if (_sessionRepository is null)
        {
            return new(TransferResumeResult.NotConnected);
        }
        IReadOnlyList<SessionProfile> saved = await _sessionRepository.GetAllSessionsAsync().ConfigureAwait(true);
        if (saved.FirstOrDefault(p => p.Id == batch.Origin.ProfileId) is not { } profile)
        {
            return new(TransferResumeResult.ProfileMissing);
        }
        FileBrowserViewModel? browser = FindConnectedFileBrowser(profile.Id);
        if (browser is null)
        {
            SftpDocument? document = profile.ConnectionType switch
            {
                ConnectionType.SSH or ConnectionType.SFTP => await OpenSftpDocumentForProfileAsync(profile).ConfigureAwait(true),
                ConnectionType.FTP => await OpenFtpDocumentForProfileAsync(profile).ConfigureAwait(true),
                ConnectionType.Plugin => await OpenPluginDocumentForProfileAsync(profile).ConfigureAwait(true),
                _ => null,
            };
            // 没连上:失败原因已经由打开路径报过了(浮层 + 失败卡片);用户取消同样原样留着,稍后可以再点。
            browser = document?.ViewModel.RemoteFiles;
        }
        if (browser is null)
        {
            return new(TransferResumeResult.NotConnected);
        }
        return await browser.ResumeQueuedTransfersAsync(batch).ConfigureAwait(true)
            ? new(TransferResumeResult.Started)
            : new(TransferResumeResult.Failed, browser.ErrorMessage);
    }

    /// <summary>开着的独立文件标签(单栏、双栏远程)里的全部远端栏。终端侧栏的面板不在其中,它们在 <see cref="_fileBrowserCache" /> 里。</summary>
    private IEnumerable<FileBrowserViewModel> DocumentFilePanes() =>
        Layout.AllDocuments().OfType<SftpDocumentBase>().SelectMany(static document => document.Content.RemotePanes);

    /// <summary>已经连着这条配置、能直接拿来传文件的面板;没有返回 null。</summary>
    private FileBrowserViewModel? FindConnectedFileBrowser(Guid profileId)
    {
        foreach (SftpDocument document in Layout.AllDocuments().OfType<SftpDocument>())
        {
            if (document.ViewModel.Profile.Id == profileId && document.ViewModel.Status == SessionStatus.Connected)
            {
                return document.ViewModel.RemoteFiles;
            }
        }
        // 终端标签只认打开过文件面板的(缓存里有):没打开过的,替它在后台建一个面板没有意义 ——
        // 用户看不到它,新开一个文件标签反而一眼就知道文件在往哪儿传。
        foreach (TerminalTabViewModel tab in TerminalTabs)
        {
            if (tab.Profile?.Id == profileId
                && tab.ConnectionStatus == SessionStatus.Connected
                && tab.SessionId != Guid.Empty
                && _fileBrowserCache.TryGetValue(tab.SessionId, out FileBrowserViewModel? cached))
            {
                return cached;
            }
        }
        return null;
    }

    /// <summary>SFTP「使用默认编辑器打开」读取的编辑器命令(设置 → 文件传输 → 默认编辑器)。</summary>
    private async Task<string?> QueryDefaultEditorPathAsync()
    {
        if (_settingsService is null)
        {
            return null;
        }
        AppSettings settings = await _settingsService.GetSnapshotAsync();
        return settings.Transfer.DefaultEditorPath;
    }

    /// <summary>
    /// 切换活动会话的 SFTP 面板(#22,规范 §9)。打开时(若尚未绑定)将浏览器绑定到当前会话并加载初始列表。
    /// </summary>
    public void ToggleFileBrowser()
    {
        if (!CanToggleFileBrowser)
        {
            return;
        }
        // 在展示前确保浏览器指向活动标签(此时已连接)的会话。
        // 活动标签订阅靠自己做不到:会话 Id 在标签激活后才分配,因此我们也在此按需重新绑定。
        RebindFileBrowser();
        FileBrowser.IsVisible = !FileBrowser.IsVisible;
        if (FileBrowser.IsVisible && FileBrowser.SessionId != Guid.Empty)
        {
            RefreshOrLoadFileBrowser();
        }
    }

    /// <summary>
    /// 请求为当前会话打开链路追踪窗口。参数依次为目标主机与窗口标题用的会话名称;
    /// 与任务管理器一样由 MainWindow 承接(视图层才建得了窗口)。
    /// </summary>
    public event Action<string, string>? TraceRouteRequested;

    /// <summary>打开链路追踪窗口,目标默认取当前会话的主机。</summary>
    private void OpenTraceRoute()
    {
        if (!CanToggleFileBrowser || ActiveTerminalTab?.Profile is not { } profile)
        {
            return;
        }
        string label = string.IsNullOrWhiteSpace(profile.Name)
                           ? HostAddress.FormatEndPoint(profile.Host, profile.Port)
                           : profile.Name;
        // 目标不用用户再抄一遍 IP —— 这正是内建追踪相对 mtr 的意义。
        TraceRouteRequested?.Invoke(profile.Host, label);
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
    /// 连接完成后调用:将文件浏览器绑定到该会话。 面板是否展示
    /// 由 <see cref="RebindFileBrowser" /> 按标签自己的状态决定(首次连接取设置
    /// 「连接后自动打开文件浏览器」的当前值,断线重连沿用标签生命周期内的记忆)。
    /// </summary>
    private void ShowFileBrowserForActiveSession() => RebindFileBrowser();

    /// <summary>
    /// 用户通过菜单/面板请求终端内搜索时触发;窗口将其转发到活动终端视图的搜索栏(§5.3)。
    /// </summary>
    public event EventHandler? TerminalSearchRequested;

    /// <summary>主动打开当前会话的共享密码选择器。</summary>
    public event EventHandler? SharedPasswordFillRequested;

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

    /// <summary>
    /// Ctrl+N / 菜单 / 命令面板“新建 SSH 连接” —— 窗口注入的新建连接整条流程(弹框 → 保存 → 连接),走完才返回。
    /// </summary>
    /// <remarks>
    /// 要能等它走完:标签条上的「+」在它外面挂着「落进这一格」的票据(<see cref="DockWorkspace.TargetNewDocumentsAt" />),
    /// 流程结束才作废。未挂时(headless)什么都不做。
    /// </remarks>
    public Func<Task>? NewConnectionDialog { get; set; }

    /// <summary>走一遍新建连接(<see cref="NewConnectionDialog" />);未挂时是空操作。</summary>
    private Task OpenNewConnectionAsync() => NewConnectionDialog?.Invoke() ?? Task.CompletedTask;

    /// <summary>Ctrl+, / 菜单 / 侧边栏齿轮“打开设置” —— 由窗口打开设置窗口。</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>
    /// 打开设置并直接落到某一分区 —— 由窗口打开设置窗口后调 <c>SelectSection</c>。
    /// 消息中心的「有可用更新」就走这条路进「关于」页,用户点完通知即可就地更新,
    /// 而不是被丢在设置首页自己去找。
    /// </summary>
    public event EventHandler<SettingsSectionKey>? SettingsSectionRequested;

    /// <summary>工具菜单“连接诊断”(针对当前标签的配置)—— 由窗口打开诊断中心弹窗。</summary>
    public event Action<SessionProfile>? DiagnosticsRequested;

    /// <summary>
    /// 命令面板「会话健康巡检(全部连接)」:对库里全部连接做一次巡检,由窗口打开结果窗口。
    /// 资源管理器的分组 / 多选菜单走 <c>SessionTreeViewModel.HealthCheckRequested</c>,落到同一扇窗。
    /// </summary>
    public event Action<IReadOnlyList<SessionProfile>>? HealthCheckRequested;

    /// <summary>取库里全部连接(按名称排)交给 <see cref="HealthCheckRequested" />;库里一条都没有时什么都不做。</summary>
    public async Task RequestHealthCheckForAllAsync()
    {
        if (_sessionRepository is not { } repository)
        {
            return;
        }
        List<SessionProfile> profiles = await repository.GetAllSessionsAsync().ConfigureAwait(true);
        if (profiles.Count > 0)
        {
            HealthCheckRequested?.Invoke([.. profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)]);
        }
    }

    /// <summary>
    /// 标签右键 / 命令面板「连接信息」—— 由窗口打开(或前置)这个标签的连接信息窗口,
    /// 面板的视图模型向 <see cref="CreateConnectionInfo" /> 要。
    /// </summary>
    public event Action<TerminalTabViewModel>? ConnectionInfoRequested;

    /// <summary>请求为一个 SSH 会话标签打开连接信息窗口;不是 SSH 会话标签时是空操作。</summary>
    public void OpenConnectionInfo(TerminalTabViewModel? tab)
    {
        if (tab is { IsSshTerminal: true })
        {
            ConnectionInfoRequested?.Invoke(tab);
        }
    }

    /// <summary>为一个标签建连接信息面板的视图模型。</summary>
    /// <remarks>
    /// 取数按标签<b>此刻</b>的会话去取:重连之后会话 id 换了,面板点一下刷新就跟上新连接;
    /// 标签断着、会话已经拆了时给 <see langword="null" />,面板如实说「没有连着」。
    /// </remarks>
    public SshConnectionInfoViewModel CreateConnectionInfo(TerminalTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        string name = tab.Profile is { } profile && !string.IsNullOrWhiteSpace(profile.Name)
            ? profile.Name
            : tab.Title;
        return new(name, () => tab.IsConnected ? _sshConnectionService?.GetClient(tab.SessionId)?.GetConnectionDetails() : null);
    }

    /// <summary>单例切换(规范 §17.2):再次打开时聚焦现有面板。</summary>
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
    /// 会话无关:无需先打开终端标签,创建隧道时由面板在后台自己建 SSH 连接。
    /// </summary>
    public void OpenTunnelPanel(SessionProfile? preselect = null)
    {
        // 隧道要跑在 SSH 连接上:纯文件协议(FTP / 插件协议)没有可承载端口转发的通道,
        // 拿它们预选只会开出一个永远连不上的面板。SFTP 虽走 SSH,但按既有约定同样不从这里进。
        if (preselect?.ConnectionType is ConnectionType.SFTP or ConnectionType.FTP or ConnectionType.Plugin)
        {
            return;
        }
        if (EnsureTunnelPanel() is not { } panel)
        {
            return;
        }
        _ = panel.OpenAsync(preselect?.Id ?? ActiveTerminalTab?.Profile?.Id);
        IsTunnelPanelOpen = true;
    }

    /// <summary>
    /// 取得(第一次时建出)隧道面板的视图模型;没有隧道服务的宿主返回 null。
    /// 面板只建一份:打开面板与启动时自动建立隧道用的是同一份,后台连接与条目缓存都在它身上。
    /// </summary>
    private TunnelPanelViewModel? EnsureTunnelPanel()
    {
        if (_tunnelWorkflowService is null)
        {
            return null;
        }
        if (TunnelPanel is null)
        {
            Func<Task<IReadOnlyList<SessionProfile>>>? servers = _sessionRepository is null
                ? null
                : async () => await _sessionRepository.GetAllSessionsAsync();
            var panel = new TunnelPanelViewModel(
                _tunnelWorkflowService,
                servers,
                ConnectTunnelHostAsync,
                id => _sshConnectionService?.GetClient(id)?.IsConnected == true,
                id => _connectionWorkflowService?.DisconnectAsync(id) ?? Task.CompletedTask,
                _appDataStore,
                // 面板外的失败(启动时自动建立隧道)走右下角的错误提示;能重试的带一个「重试」。
                (message, retry) => Toasts.Error(message, retry is null ? null : Strings.Get("Tunnel_RetryAction"), retry),
                // 点了「重试」、隧道建起来了:面板多半没开,报一声。
                message => Toasts.Info(message)
            );
            panel.CloseRequested += (_, _) => IsTunnelPanelOpen = false;
            TunnelPanel = panel;
        }
        return TunnelPanel;
    }

    /// <summary>
    /// 启动时把勾了「程序启动时自动建立」的隧道建好(见 <see cref="TunnelPanelViewModel.AutoStartTunnelsAsync" />)。
    /// 没有存储就没有持久化的隧道配置,也就无事可做 —— 不为它白建一份面板。
    /// </summary>
    private void StartAutoStartTunnels()
    {
        if (_appDataStore is null || EnsureTunnelPanel() is not { } panel)
        {
            return;
        }
        _ = panel.AutoStartTunnelsAsync();
    }

    // ---- 消息中心(侧边栏铃铛) ----

    private INotificationCenter? _notificationCenter;

    /// <summary>
    /// 消息中心载入上次留下的消息(<see cref="INotificationCenter.LoadAsync" />)那一步;没有消息中心时为 null。
    /// 后台来源(启动时的自动同步)要等它完了再投 —— 载入会整份换成存着的列表,赶在它前面投的会被盖掉。
    /// </summary>
    private Task? _notificationsLoaded;

    /// <summary>启动检查发现新版本后的后台下载入口(设置 → 常规 → 自动下载更新);null = 只检查不下载(单测)。</summary>
    private readonly BackgroundUpdateDownload? _backgroundUpdateDownload;

    /// <summary>本机加密密钥的解析结果(系统密钥链用不了、旧密钥没取到时投一条提示);null = 不投(单测)。</summary>
    private readonly IMasterKeyStatusSource? _masterKeyStatus;

    /// <summary>
    /// 装配消息中心:面板、铃铛角标,以及两个内容来源(本地的更新检查 + 订阅的资讯源)。
    /// 无消息中心(单元测试)时整块跳过。
    /// </summary>
    private void SetUpNotificationCenter(
        INotificationCenter? center, IAnnouncementFeed? feed, IUpdateService? updateService)
    {
        if (center is null)
        {
            return;
        }
        _notificationCenter = center;
        var panel = new NotificationPanelViewModel(center, Commands.Execute, OpenExternalUrlAsync, _appDataStore);
        panel.CloseRequested += (_, _) => IsNotificationPanelOpen = false;
        NotificationPanel = panel;

        // 铃铛角标:未读数由消息中心推给侧边栏,侧边栏不关心它从哪来。
        center.Changed += () => RxSchedulers.MainThreadScheduler.Schedule(() =>
            Sidebar.NotificationUnreadCount = center.UnreadCount);
        Sidebar.NotificationsRequested += (_, _) => IsNotificationPanelOpen = !IsNotificationPanelOpen;

        _ = InitializeNotificationsAsync(center, feed, updateService);

        // 周期拉取。计时器按固定的半小时跳,真正拉不拉由 FeedIntervalHours 决定 ——
        // 这样用户在设置里改完间隔,下一跳就生效,不用重启也不用去重设计时器。
        // 无 Avalonia 应用(单元测试)时不起表。
        if (Application.Current is null || feed is null)
        {
            return;
        }
        _feedTimer = new()
        {
            Interval = TimeSpan.FromMinutes(30)
        };
        _feedTimer.Tick += (_, _) =>
        {
            AppSettings settings = _latestSettings ?? new();
            var due = TimeSpan.FromHours(Math.Max(1, settings.Notifications.FeedIntervalHours));
            if (DateTime.UtcNow - _lastFeedFetch < due)
            {
                return;
            }
            _lastFeedFetch = DateTime.UtcNow;
            _ = RefreshNotificationSourcesAsync(center, feed, updateService: null);
        };
        _feedTimer.Start();
    }

    private DispatcherTimer? _feedTimer;

    /// <summary>上次拉取资讯源的时刻,用于按 <c>FeedIntervalHours</c> 判断是否到点。</summary>
    private DateTime _lastFeedFetch = DateTime.UtcNow;

    /// <summary>
    /// 载入历史消息,再拉一次内容源。整段吞异常:消息中心是锦上添花的东西,
    /// 它出问题不该拦住应用启动。
    /// </summary>
    private async Task InitializeNotificationsAsync(
        INotificationCenter center, IAnnouncementFeed? feed, IUpdateService? updateService)
    {
        try
        {
            _notificationsLoaded = center.LoadAsync();
            await _notificationsLoaded.ConfigureAwait(true);
            Sidebar.NotificationUnreadCount = center.UnreadCount;
            // 与更新检查并行:后者可能要后台下载好几分钟,密钥的提示不该排在它后面。
            Task keyNotices = PublishMasterKeyNoticesAsync(center);
            await PublishRemoteEditDraftsAsync(center).ConfigureAwait(true);
            await RefreshNotificationSourcesAsync(center, feed, updateService).ConfigureAwait(true);
            await keyNotices.ConfigureAwait(true);
        }
        catch
        {
            // 载入/拉取失败时铃铛照常可用,只是没有新内容。
        }
    }

    /// <summary>
    /// 本机加密密钥有要用户知道的事(系统密钥链用不了、旧密钥没取到、密钥文件被换过)时投进消息中心。
    /// </summary>
    /// <remarks>
    /// 密钥只在进程里解析一次,提示也只在启动这一轮投(资讯源的定时刷新不再投)。
    /// 解析可能要等密钥链解锁,放到后台线程上等。整段吞异常:提示投不出去不该拦住铃铛,
    /// 解析本身失败(数据目录写不进去)会在第一次读写机密时照样暴露。
    /// </remarks>
    private async Task PublishMasterKeyNoticesAsync(INotificationCenter center)
    {
        if (_masterKeyStatus is not { } source)
        {
            return;
        }
        try
        {
            MasterKeyStatus status = await Task.Run(source.GetKeyStatus).ConfigureAwait(true);
            IReadOnlyList<NotificationItem> notices = NotificationSources.BuildMasterKeyNotifications(status, DateTime.UtcNow);
            if (notices.Count > 0)
            {
                await center.PublishAsync(notices).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Secrets] Master key notice could not be published: {ex.Message}");
        }
    }

    /// <summary>
    /// 上次运行留下的外部编辑草稿(有或可能有没传回服务器的改动)一份一条投进消息中心;草稿已经不在的,撤掉它那一条。
    /// </summary>
    /// <remarks>
    /// 只在启动时做一次:运行中留下的草稿当场就在文件面板报过位置(<c>Svc_RemoteEditDraftKept</c>),
    /// 下次启动再在这里列。扫目录是磁盘 IO,放到线程池;正开着的编辑会话不算草稿。
    /// </remarks>
    internal static async Task PublishRemoteEditDraftsAsync(INotificationCenter center, string? root = null)
    {
        string draftsRoot = root ?? RemoteEditSessionManager.TempRoot;
        string[] active = [.. RemoteEditSessionManager.ActiveSessions.Select(static s => Path.GetDirectoryName(s.LocalPath)!)];
        IReadOnlyList<RemoteEditDraft> drafts = await Task.Run(() => RemoteEditDrafts.ListDrafts(draftsRoot, active)).ConfigureAwait(true);
        foreach (string stale in NotificationSources.StaleRemoteEditDraftIds(center.Items, drafts))
        {
            await center.RemoveAsync(stale).ConfigureAwait(true);
        }
        if (drafts.Count > 0)
        {
            await center.PublishAsync(NotificationSources.BuildRemoteEditDraftNotifications(drafts)).ConfigureAwait(true);
        }
    }

    /// <summary>把「有可用更新」与订阅资讯源的内容投进消息中心。</summary>
    /// <remarks>
    /// "投什么"由 <see cref="NotificationSources" /> 决定(那是可以单独测的决策);
    /// 这里只负责把结果交给消息中心。
    /// </remarks>
    private async Task RefreshNotificationSourcesAsync(
        INotificationCenter center, IAnnouncementFeed? feed, IUpdateService? updateService)
    {
        // 启动这一轮要查更新时,设置必须是真读出来的:「启动时检查更新」关掉的人一个请求都不该发,
        // 「自动下载更新」开着的人也不能因为快照还没缓存就被当成默认值(关)。
        AppSettings settings = _latestSettings
            ?? (updateService is null ? new() : await LoadSettingsSnapshotAsync().ConfigureAwait(true));
        // 更新与资讯源分开投:自动下载要几十秒到几分钟,公告与崩溃提示不该排在它后面。
        IReadOnlyList<NotificationItem> incoming =
            await NotificationSources.CollectAsync(settings, feed, updateService: null).ConfigureAwait(true);
        if (incoming.Count > 0)
        {
            await center.PublishAsync(incoming).ConfigureAwait(true);
        }
        if (updateService is not null
            && await NotificationSources.CollectUpdateAsync(settings, updateService, _backgroundUpdateDownload)
                                        .ConfigureAwait(true) is { } update)
        {
            await center.PublishAsync([update]).ConfigureAwait(true);
        }
    }

    /// <summary>在系统浏览器里打开外链(由消息中心的外链条目调用)。</summary>
    private Task OpenExternalUrlAsync(string url)
    {
        ExternalUrlRequested?.Invoke(this, url);
        return Task.CompletedTask;
    }

    /// <summary>请求在系统浏览器中打开一个网址 —— 由窗口层执行(ViewModel 不碰 TopLevel)。</summary>
    public event EventHandler<string>? ExternalUrlRequested;

    /// <summary>为隧道面板后台建立 SSH 连接:不开终端标签,凭据缺失时走登录验证弹窗。</summary>
    /// <remarks>
    /// <paramref name="automaticReconnect" /> = 断线后的自动恢复:主机指纹要人裁决时直接拒绝、不弹窗
    /// (<see cref="ConnectionInfo.IsAutomaticReconnect" />)。凭据缺失时的登录框照旧 —— 那一条有它自己的退避。
    /// </remarks>
    private async Task<Guid> ConnectTunnelHostAsync(
        SessionProfile profile,
        bool automaticReconnect,
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
        SshSession session = automaticReconnect
            ? await _connectionWorkflowService.ReconnectProfileAutomaticallyAsync(current, cancellationToken)
            : await _connectionWorkflowService.ConnectProfileAsync(current, cancellationToken);
        return session.SessionId;
    }

    /// <summary>
    /// 加载已持久化的最近连接历史(SonnetDB)到侧边栏,使重启后仍保留。
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
            bool stateAlreadyPrepared = _startupStatePrepared;
            if (!stateAlreadyPrepared)
            {
                _appState = await _settingsService.GetStateAsync();
                ApplySidebarState(_appState);
            }
            ApplyShellPreferences(await LoadSettingsSnapshotAsync());
            _startupStatePrepared = false;
        }
        await Sidebar.RecentConnections.RefreshAsync();
        await RefreshSessionTreeAsync();
        await LayoutTemplates.LoadAsync();
        RevealActiveSessionInSidebar();
        // 隧道的启停状态不落盘:重启后一律是已停止,只有勾了「程序启动时自动建立」的在这里替用户建好。
        // 不等它 —— 拨号可能要好几秒、还可能弹凭据提示,不该拖住启动。
        StartAutoStartTunnels();
    }

    /// <summary>
    /// 在主窗口显示前回填会影响布局的持久化状态。若等到 <see cref="InitializeAsync" />
    /// 的首帧之后才读取，侧栏折叠状态和多行标签设置会把整棵布局树再排一次，启动时就会闪动。
    /// </summary>
    public void PrepareForStartup(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_settingsService is not null)
        {
            try
            {
                _appState = _settingsService.GetStateAsync().GetAwaiter().GetResult();
                ApplySidebarState(_appState);
            }
            catch
            {
                // 状态文档损坏或暂时不可读时沿用默认布局，不能让启动准备阶段阻断窗口显示。
            }
        }
        ApplyShellPreferences(settings);
        _startupStatePrepared = true;
    }

    private void ApplyShellPreferences(AppSettings settings)
    {
        Sidebar.IsQuickCommandsVisible =
            _quickCommandRunner is not null && settings.Appearance.ShowQuickCommandsPanel;
        Layout.MultiRowTabs = settings.Appearance.MultiRowTabs;
        ApplyTabBarPosition(settings.Appearance);
    }

    /// <summary>
    /// 设置「标签栏位置」→ 停靠层:横排的标签条停在顶部还是底部,所有窗格一起变。
    /// 启动、保存设置时由 <see cref="ApplyShellPreferences" /> 调用;设置页的外观预览也走这里
    /// (主窗口的预览处理器),所以下拉一改当场可见,取消时随预览一起回滚。
    /// </summary>
    /// <remarks>
    /// 只认 <c>bottom</c>,其余值(含手改配置留下的大小写变体)按顶部 —— 与设置页下拉的映射
    /// (<c>SettingsViewModel.TabBarPositionIndex</c>)同一口径,两处才不会一个说顶部、一个摆在底部。
    /// </remarks>
    /// <param name="appearance">要应用的外观设置。</param>
    public void ApplyTabBarPosition(AppearanceOptions appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        Layout.TabsAtBottom = appearance.TabBarPosition == "bottom";
    }

    /// <summary>
    /// 标签条上的「+」(<c>DockWorkspaceControl.NewTabRequested</c>):在那一格里新开会话。
    /// </summary>
    /// <param name="pane">按下「+」的那一格。</param>
    public void OpenNewTabInPane(DockGroup pane) => _ = OpenNewTabInPaneAsync(pane);

    /// <summary>
    /// 「+」的整条流程:那一格先成为活动窗格,再走与 <c>Ctrl+T</c> 同一个新建连接对话框;
    /// 连出来的标签<b>精确落进按下的那一格</b>,哪怕布局里另有空窗格(维护者 2026-10-25 定:按下的那格优先于空窗格)。
    /// </summary>
    /// <remarks>
    /// 对话框与 <c>TryConnectProfileAsync</c> 一个参数都没加:流程外面挂一张「落进这一格」的票据
    /// (<see cref="DockWorkspace.TargetNewDocumentsAt" />,与布局模板还原的槽位票据同一个思路),
    /// 流程里建的第一个标签认领它;流程走完(连上、失败、取消、只按了「保存」)票据作废。
    /// </remarks>
    /// <param name="pane">按下「+」的那一格。</param>
    public async Task OpenNewTabInPaneAsync(DockGroup pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        if (pane.ActiveDocument is { } active && !ReferenceEquals(Layout.ActiveDocument, active))
        {
            Layout.ActivateDocument(active);
        }
        using IDisposable target = Layout.TargetNewDocumentsAt(pane);
        await OpenNewConnectionAsync().ConfigureAwait(true);
    }

    private void OnLayoutPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DockWorkspace.MultiRowTabs))
        {
            PersistMultiRowTabs(Layout.MultiRowTabs);
        }
    }

    /// <summary>
    /// 标签右键菜单切换「多行显示标签页」后写回持久化设置。设置本来就是这个值时(启动、
    /// 设置页保存后回灌到布局)什么也不做 —— 否则每次保存设置都会绕回来再存一遍。
    /// </summary>
    /// <param name="multiRow">切换后的值。</param>
    private void PersistMultiRowTabs(bool multiRow)
    {
        if (_settingsService is null || _latestSettings?.Appearance.MultiRowTabs == multiRow)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                AppSettings settings = await _settingsService.GetSettingsAsync().ConfigureAwait(false);
                if (settings.Appearance.MultiRowTabs == multiRow)
                {
                    return;
                }
                settings.Appearance.MultiRowTabs = multiRow;
                await _settingsService.SaveSettingsAsync(settings).ConfigureAwait(false);
            }
            catch
            {
                // 写回失败只影响下次启动的初始值,界面上已经切过去了。
            }
        });
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
            Sidebar.IsCollapsed = state.SidebarCollapsed;
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
                    or nameof(SidebarViewModel.IsCollapsed)
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
        _appState.SidebarCollapsed = Sidebar.IsCollapsed;
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
        if (!(_latestSettings?.General.FollowActiveTerminalInExplorer ?? true))
        {
            return;
        }
        TerminalTabViewModel? target = tab ?? ActiveTerminalTab;
        if (target?.Profile is { Id: var profileId } && profileId != Guid.Empty)
        {
            Sidebar.SessionTree?.SelectSession(profileId);
        }
    }

    /// <summary>
    /// 按当前工作区里的终端文档,对齐每个标签的订阅(同步输入、会话状态、快捷命令目标)。
    /// </summary>
    /// <remarks>
    /// 做成"全量对齐"而不是"按增删事件增量处理":这些订阅的登记表是字典,与工作区取差集
    /// 是幂等的,重复调一次不会出错。增量处理则要求每一次增删都恰好通知一次 ——
    /// 而这正是 §24 / §39 两次同形 bug 的来源。
    /// </remarks>
    private void SyncTabSubscriptions()
    {
        var currentTabs = TerminalTabs.ToHashSet();
        foreach (
            TerminalTabViewModel removed in _quickCommandTargetSubscriptions
                .Keys.Where(tab => !currentTabs.Contains(tab))
                .ToArray()
        )
        {
            _quickCommandTargetSubscriptions.Remove(removed, out IDisposable? subscription);
            subscription?.Dispose();
            _sessionStatusSubscriptions.Remove(removed, out IDisposable? statusSubscription);
            statusSubscription?.Dispose();
            _syncInput.Detach(removed);
            // 标签离开标签栏(关闭、连接失败或取消后静默移除)→ 重算它那条配置的树上状态,
            // 否则节点会停在这个已经不存在的标签留下的状态上(#321)。
            if (removed.Profile is { Id: var removedProfileId } && removedProfileId != Guid.Empty)
            {
                RefreshSessionStatus(removedProfileId);
            }
        }
        foreach (TerminalTabViewModel added in currentTabs)
        {
            _syncInput.Attach(added);
            // 资源管理器树的状态圆点与「活跃/连接中/离线」标签(设计 FrJPu)跟随该配置
            // 名下**所有**标签的合并状态;订阅随标签在标签栏里的存续期存在。
            if (
                !_sessionStatusSubscriptions.ContainsKey(added)
                && added.Profile is { Id: var addedProfileId }
                && addedProfileId != Guid.Empty
            )
            {
                _sessionStatusSubscriptions[added] = added
                    .WhenAnyValue(tab => tab.ConnectionStatus)
                    .Subscribe(_ => RefreshSessionStatus(addedProfileId));
            }
            if (_quickCommandTargetSubscriptions.ContainsKey(added))
            {
                continue;
            }
            _quickCommandTargetSubscriptions[added] = added
                // ConnectionStatus 在 TerminalTabViewModel 更新 IsConnected 之前触发。
                // 观察 IsConnected 本身,使刷新看到最终可用的状态。
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
        SyncInputChannel? channel = TerminalTabs
            .Where(tab => tab.Profile?.Id == profileId)
            .Select(tab => tab.SyncChannel)
            .FirstOrDefault(c => c is not null);
        Sidebar.SessionTree?.SetSessionSyncChannel(
            profileId,
            channel?.ToString() ?? string.Empty
        );
    }

    /// <summary>
    /// 重算某配置在会话树里的状态标签:一条配置可以同时开着多个标签(复制会话、
    /// 对同一台机器再开一个),而树上只有一个节点 —— 取这些标签里"最活跃"的那个状态,
    /// 而不是最后一次变更的那个标签的状态。
    /// <para>
    /// 合并优先级 Connected &gt; Connecting &gt; Error &gt; Disconnected:一条已经连上的会话
    /// 不该因为旁边多了个正在握手或握手失败的标签而被写成「连接中」/「离线」。
    /// 按"最后一次变更"来更新会留下一个走不出去的状态(#321:在已连上的会话上再开一个
    /// 标签、趁它还在连接时立刻关掉,节点会永远停在「连接中」)。
    /// </para>
    /// <para>
    /// 参与合并的不只有终端标签,还有该配置名下**活着的文档型会话**(独立 SFTP / FTP /
    /// S3 等插件文件系统 / Redis 等工作台,见 <see cref="SessionStatusRegistry" />)。
    /// 这些连接一样可以对同一条配置开好几个,而它们原先各自直接往树上写状态、最后一次说了算 ——
    /// 于是"点快了开出两个 FTP 标签,关掉一个,树上的圆点就灭了"(明明还有一个活着)。
    /// </para>
    /// <para>没有任何标签或文档属于该配置时归零为 Disconnected —— 最后一个关掉即回到未连接。</para>
    /// </summary>
    private void RefreshSessionStatus(Guid profileId)
    {
        SessionStatus status = _sessionStatuses.Merge(
            profileId,
            TerminalTabs.Where(tab => tab.Profile?.Id == profileId).Select(tab => tab.ConnectionStatus));
        Sidebar.SessionTree?.SetSessionStatus(profileId, status);
    }

    /// <summary>
    /// 登记一条刚建好的文档型会话并刷新它那条配置在树上的状态。
    /// </summary>
    /// <param name="sessionId">会话标识(摘除与状态更新都按它定位)。</param>
    /// <param name="profileId">
    /// 树上节点对应的配置标识。刻意由调用方给出**原始** profile 的 Id:登录弹窗可能换过
    /// 文档里那份配置的字段,而树上的节点始终是按最初那条配置的 Id 建的。
    /// </param>
    /// <param name="status">初始状态(建出文档即已连上)。</param>
    private void TrackDocumentSession(Guid sessionId, Guid profileId, SessionStatus status) =>
        RefreshIfNeeded(_sessionStatuses.Track(sessionId, profileId, status));

    /// <summary>
    /// 更新一条在册文档型会话的状态(掉线 / 重新连上)并刷新树。
    /// <para>
    /// 不在册的会话**不复活**:文档已经关掉之后仍可能收到一条迟到的状态事件
    /// (FTP 的失效是在下一次操作时才暴露的),照单全收会把刚灭掉的圆点重新点亮。
    /// </para>
    /// </summary>
    private void UpdateDocumentSessionStatus(Guid sessionId, SessionStatus status) =>
        RefreshIfNeeded(_sessionStatuses.Update(sessionId, status));

    /// <summary>
    /// 摘掉一条已关闭的文档型会话并刷新树。幂等 —— 关闭路径与协议自己的 <c>Closed</c> 事件
    /// 都会走到这里,谁先到都行。
    /// </summary>
    private void ForgetDocumentSession(Guid sessionId) =>
        RefreshIfNeeded(_sessionStatuses.Forget(sessionId));

    /// <summary>账本说有配置要刷新时,把刷新排到 UI 线程上。</summary>
    private void RefreshIfNeeded(Guid? profileId)
    {
        if (profileId is { } id)
        {
            ScheduleSessionStatusRefresh(id);
        }
    }

    private void RefreshQuickCommandTargets()
    {
        (Guid Id, string DisplayName)[] targets = [.. TerminalTabs.Where(tab => tab.IsConnected).Select(tab => (tab.Id, tab.Title))];
        _terminalTargetSelector.UpdateTargets(targets);
        _terminalTargetSelector.SetCurrentTarget(ActiveTerminalTab is { IsConnected: true } current ? current.Id : null);
    }

    private void OnQuickCommandExecutionRequested(
        object? sender,
        QuickCommandExecutionRequest request
    )
    {
        var template = QuickCommandTemplate.Parse(request.CommandText);
        if (!template.HasVariables || QuickCommandVariablePrompt is null)
        {
            // 没有占位(或没有窗口可以问,如 headless 测试)时原样发送,与引入占位之前一致。
            DeliverQuickCommand(request.TargetIds, request.CommandText);
            return;
        }
        FireAndForget.Run(async () =>
        {
            if (await AskQuickCommandVariablesAsync(request.CommandId, request.CommandName, template) is not { } rendered)
            {
                return;
            }
            // 弹框期间目标可能已经断开:发送时再按当下的标签挑一遍,只发给仍连着的。
            DeliverQuickCommand(request.TargetIds, rendered.Text, rendered.Secrets);
        });
    }

    /// <summary>
    /// 快捷命令变量的询问:有占位就弹框(每格预填上一次填的值),确认后返回替换好的命令;取消返回 null。
    /// 没有占位、或没有窗口可以问(headless)时原样返回。
    /// </summary>
    /// <remarks>
    /// 快捷命令区的发送与终端命令补全选中一条快捷命令,走的是这同一处 —— 两个入口问的是同一个框、
    /// 记的是同一份值,在补全里填过的 pod 名,到快捷命令区再发同一条命令时照样预填。
    /// </remarks>
    /// <param name="commandId">快捷命令 id;询问框按它记值,没有时按正文记。</param>
    /// <param name="commandName">命令显示名(弹框标题)。</param>
    /// <param name="commandText">命令正文。</param>
    /// <param name="target">
    /// 正文要写进哪个标签的命令行(补全那一路);凭据类占位填进去的值先挡在它的命令历史之外
    /// (<see cref="TerminalTabViewModel.KeepOutOfCommandHistory" />)。
    /// </param>
    /// <returns>要发出去的命令正文,或 null(用户取消)。</returns>
    internal async Task<string?> ResolveQuickCommandTextAsync(
        Guid? commandId,
        string? commandName,
        string commandText,
        TerminalTabViewModel? target = null)
    {
        var template = QuickCommandTemplate.Parse(commandText);
        if (!template.HasVariables || QuickCommandVariablePrompt is null)
        {
            return commandText;
        }
        if (await AskQuickCommandVariablesAsync(commandId, commandName, template) is not { } rendered)
        {
            return null;
        }
        target?.KeepOutOfCommandHistory(rendered.Secrets);
        return rendered.Text;
    }

    /// <summary>替换好的快捷命令:正文,以及其中凭据类占位填进去的值(不进命令历史)。</summary>
    private readonly record struct RenderedQuickCommand(string Text, IReadOnlyList<string> Secrets);

    private async Task<RenderedQuickCommand?> AskQuickCommandVariablesAsync(Guid? commandId, string? commandName, QuickCommandTemplate template)
    {
        if (QuickCommandVariablePrompt is not { } prompt)
        {
            return new(template.Text, []);
        }
        IReadOnlyDictionary<string, string> initial = _quickCommandVariables.Prefill(commandId, template.Text, template);
        IReadOnlyDictionary<string, string>? values = await prompt(commandName, template, initial);
        if (values is null)
        {
            return null;
        }
        _quickCommandVariables.Remember(commandId, template.Text, template, values);
        return new(template.Render(values), QuickCommandVariableMemory.SensitiveValues(template, values));
    }

    /// <summary>
    /// 多行命令要发给没开括号粘贴的终端时先确认(那边会逐行执行),其余情况直接发送。
    /// 确认开关沿用「设置 → 终端 → 粘贴时确认多行内容」:关掉它的人已经表示过不想被这类问题打断。
    /// </summary>
    /// <param name="targetIds">目标标签。</param>
    /// <param name="commandText">要发的正文。</param>
    /// <param name="historySecrets">正文里凭据类占位填进去的值:各目标标签提交含它们的那一行时不进命令历史。</param>
    private void DeliverQuickCommand(IReadOnlyList<Guid> targetIds, string commandText, IReadOnlyList<string>? historySecrets = null)
    {
        if (
            QuickCommandMultilineConfirmer is { } confirm
            && _latestSettings?.TerminalBehavior.ConfirmMultilinePaste != false
            && QuickCommandText.IsMultiline(commandText)
            && QuickCommandTargets(targetIds).Any(tab => !tab.TerminalEmulator.IsBracketedPasteEnabled)
        )
        {
            FireAndForget.Run(async () =>
            {
                if (await confirm(commandText))
                {
                    SendQuickCommandText(targetIds, commandText, historySecrets);
                }
            });
            return;
        }
        SendQuickCommandText(targetIds, commandText, historySecrets);
    }

    private TerminalTabViewModel[] QuickCommandTargets(IReadOnlyList<Guid> targetIds)
    {
        var ids = targetIds.ToHashSet();
        return [.. TerminalTabs.Where(tab => tab.IsConnected && ids.Contains(tab.Id))];
    }

    private void SendQuickCommandText(IReadOnlyList<Guid> targetIds, string commandText, IReadOnlyList<string>? historySecrets)
    {
        TerminalTabViewModel[] targets = QuickCommandTargets(targetIds);
        bool sent = false;
        foreach (TerminalTabViewModel target in targets)
        {
            if (historySecrets is { Count: > 0 })
            {
                // 正文只写进命令行、回车由用户按:先挡住,等那一行提交时它就不进命令历史。
                target.KeepOutOfCommandHistory(historySecrets);
            }
            sent |= target.TrySendCommandText(commandText);
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
                await tree.LoadCommand.Execute().FirstAsync();
            }
            catch
            {
                // 树加载失败不影响其余启动流程。
            }
        }
        await RefreshPaletteSessionsAsync();
        RevealActiveSessionInSidebar();
    }

    /// <summary>
    /// 云同步在后台线程完成 Profile upsert 后刷新所有会话入口。除侧边栏树外还要刷新
    /// 命令面板的全量会话缓存,否则树已出现新连接而命令面板仍需重启才可搜索到。
    /// </summary>
    private void OnSyncProfilesApplied(object? sender, EventArgs args)
    {
        _ = sender;
        _ = args;
        RxSchedulers.MainThreadScheduler.Schedule(() => _ = RefreshSessionTreeAsync());
    }

    /// <summary>
    /// 云同步拉到「Webhook 告警开着、这台设备上却没有地址」的设置:投一条消息中心提示,带「前往安全审计设置」。
    /// 同一份设置只来一次(服务按设置指纹去重并记在同步配置里),消息 id 也带着指纹。可能来自后台线程。
    /// </summary>
    /// <remarks>
    /// 云同步本来是静默的(启动拉取、保存后防抖推送);设置导入遇到同样的情形会当场提示重填,
    /// 同步这条路以前却只是静默留空,告警就这样悄悄停了(维护者 2026-10-25 定,第二轮第 17 条)。
    /// </remarks>
    private void OnSyncWebhookUrlMissing(object? sender, string fingerprint)
    {
        _ = sender;
        if (_notificationCenter is not { } center)
        {
            return;
        }
        _ = PublishSyncNoticeAsync(center, NotificationSources.BuildSyncWebhookMissingNotification(fingerprint, DateTime.UtcNow));
    }

    /// <summary>等消息中心载入完上次的消息再投一条(整段吞异常:提示投不出去不该拦住同步)。</summary>
    private async Task PublishSyncNoticeAsync(INotificationCenter center, NotificationItem item)
    {
        try
        {
            if (_notificationsLoaded is { } loaded)
            {
                await loaded.ConfigureAwait(false);
            }
        }
        catch
        {
            // 载入失败照投:铃铛照常可用,只是没有历史消息。
        }
        try
        {
            await center.PublishAsync([item]).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Sync] Notice could not be published: {ex.Message}");
        }
    }

    /// <summary>
    /// 共享凭据有变 → 重读会话树(树缓存里的配置可能刚被改写)。可能来自任意线程;
    /// 同步拉取会一条一条地存,连发的通知合并成一次刷新。
    /// </summary>
    private void OnSharedCredentialsChanged(object? sender, EventArgs args)
    {
        _ = sender;
        _ = args;
        if (Interlocked.Exchange(ref _credentialRefreshQueued, 1) == 1)
        {
            return;
        }
        RxSchedulers.MainThreadScheduler.Schedule(() =>
        {
            Interlocked.Exchange(ref _credentialRefreshQueued, 0);
            _ = RefreshSessionTreeAsync();
        });
    }

    /// <summary>
    /// 取这一次连接要用的配置:引用了共享凭据就交出一份填好凭据的副本,原配置不动(#550)。
    /// 没有解析器(无 UI 单测)时原样返回。
    /// </summary>
    /// <remarks>只给不走 SSH 工作流的几条路径用(FTP、插件协议);SSH 由工作流自己解析,含跳板的每一跳。</remarks>
    private Task<SessionProfile> ResolveCredentialsAsync(SessionProfile profile, CancellationToken cancellationToken) =>
        _credentialResolver is { } resolver
            ? resolver.ResolveAsync(profile, cancellationToken)
            : Task.FromResult(profile);

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

    private List<CommandPaletteItem> BuildPaletteItems()
    {
        var items = new List<CommandPaletteItem>();

        // 最近连接优先 —— 快捷访问桶(Enter 连接)。
        var recentProfileIds = new HashSet<Guid>();
        Dictionary<Guid, SessionProfile> profilesById = [];
        foreach (SessionProfile profile in _paletteProfiles)
        {
            _ = profilesById.TryAdd(profile.Id, profile);
        }
        foreach (RecentConnectionItemViewModel item in Sidebar.RecentConnections.Connections)
        {
            RecentConnectionEntry captured = item.Entry;
            if (captured.ProfileId is { } pid)
            {
                recentProfileIds.Add(pid);
            }
            // 最近连接里那条若对得上一条已保存的配置,标签与备注同样能搜到它 ——
            // 它在「会话」桶里因为去重不再出现,不带上的话按标签就搜不到最常用的那几台。
            IReadOnlyList<string>? recentKeywords =
                captured.ProfileId is { } recentId && profilesById.TryGetValue(recentId, out SessionProfile? recentProfile)
                    ? PaletteKeywords(recentProfile)
                    : null;
            string title = string.IsNullOrWhiteSpace(item.DisplayName)
                ? captured.Host
                : item.DisplayName;
            items.Add(
                new(
                    Strings.Get("RecentConnections"),
                    title,
                    () => _ = TryConnectRecentAsync(captured),
                    Strings.Get("Msg_EnterToConnect"),
                    isSession: true,
                    // 同一台机器在"最近连接"与"会话"两个桶里用同一个 id,
                    // 使用痕迹才不会被拆成两半。
                    id: captured.ProfileId is { } recentProfileId
                        ? $"session:{recentProfileId}"
                        : $"recent:{captured.Host}",
                    keywords: recentKeywords
                )
            );
        }

        // 全部已保存配置(§12.3),带分组徽章;已出现在最近连接里的不重复列出。
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
                    true,
                    $"session:{captured.Id}",
                    PaletteKeywords(captured)
                )
            );
        }

        // 布局模板:Enter 直接按模板开出这一组连接、排好分屏。
        foreach (LayoutTemplateItemViewModel template in LayoutTemplates.Templates)
        {
            LayoutTemplateItemViewModel captured = template;
            items.Add(
                new(
                    Strings.Get("LayoutTemplate_Title"),
                    captured.Name,
                    () => _ = LayoutTemplates.OpenAsync(captured),
                    Strings.Get("LayoutTemplate_EnterToOpen"),
                    id: $"layout:{captured.Template.Id}"
                )
            );
        }

        // 全局操作来自共享命令注册表(菜单/面板/快捷键一致)。右侧的键位提示归键位表管的
        // 取当前生效的那一个(改过键、解绑都跟着变),其余用命令注册时自带的提示。
        ShortcutKeymap keymap = _shortcutKeymap?.Current ?? ShortcutKeymap.Default;
        items.AddRange(
            Commands.All.Select(captured => new CommandPaletteItem(
                Strings.Get("Command"),
                captured.Title,
                () => Commands.Execute(captured.Id),
                keymap.HintFor(captured.Id, captured.Shortcut),
                id: captured.Id
            ))
        );
        return items;
    }

    /// <summary>
    /// 命令面板里一条会话的检索词:标签、主机、备注,按这个先后(越靠前权重越高,见 <see cref="PaletteScorer" />)。
    /// </summary>
    /// <remarks>
    /// 标签原先只有编辑框与导入导出在读(§180),面板与会话树都不认,存了等于没存;备注(#549)也只能悬停时看到。
    /// 主机一并带上:有名字的配置标题是名字,用户手里却常常只有一个 IP。
    /// </remarks>
    /// <param name="profile">连接配置。</param>
    /// <returns>检索词。</returns>
    internal static IReadOnlyList<string> PaletteKeywords(SessionProfile profile)
    {
        List<string> keywords = [.. (profile.Tags ?? []).Where(static tag => !string.IsNullOrWhiteSpace(tag))];
        if (!string.IsNullOrWhiteSpace(profile.Host))
        {
            keywords.Add(profile.Host);
        }
        if (!string.IsNullOrWhiteSpace(profile.Notes))
        {
            keywords.Add(profile.Notes);
        }
        return keywords;
    }

    /// <summary>读取设置快照并缓存到 <see cref="_latestSettings" />(无设置服务时用默认值)。</summary>
    private async Task<AppSettings> LoadSettingsSnapshotAsync()
    {
        AppSettings settings = _settingsService is not null
            ? await _settingsService.GetSnapshotAsync()
            : new();
        _latestSettings = settings;
        return settings;
    }

    /// <summary>
    /// 立即创建一个“连接中”的终端标签并加入标签栏/停靠区(#17:慢连接不再像卡死,
    /// 用户立刻拿到可见、可关闭的标签)。握手由 <see cref="RunHandshakeAsync" /> 完成。
    /// 认证重试会复用同一标签,不重复建标签。
    /// </summary>
    /// <param name="profile">连接配置。</param>
    /// <param name="settings">设置快照。</param>
    /// <param name="protocolLabel">状态栏上的协议名;插件终端协议传自己的页签名,默认 SSH。</param>
    /// <param name="tabTitle">标签页标题;null = 配置的显示名(外部拉起的 <c>-newtab</c> 名字从这里进来)。</param>
    private (TerminalTabViewModel Tab, TerminalDocument Document) CreateConnectingTab(
        SessionProfile profile,
        AppSettings settings,
        string? protocolLabel = null,
        string? tabTitle = null
    )
    {
        TerminalType terminalType = TerminalTypeExtensions.FromTermName(SessionTerminalSettings.TerminalType(profile, settings));
        ITerminalEmulator terminalEmulator = _terminalEmulatorFactory();
        ConfigureTerminal(terminalEmulator, settings, terminalType, profile: profile);

        // 状态栏连接指示按设计 gzmsb 显示"SSH • <显示名称>"——不暴露用户名与 IP(安全要求);
        // 未配置名称时才退回主机地址。
        string displayName = string.IsNullOrWhiteSpace(profile.Name) ? profile.Host : profile.Name;
        // 协议名默认 SSH;插件终端协议(Telnet…)传自己的页签名进来 ——
        // 状态栏写死 "SSH • xxx" 会让一条 Telnet 会话看起来是加密的。
        string protocol = string.IsNullOrWhiteSpace(protocolLabel) ? "SSH" : protocolLabel;
        var terminalTab = new TerminalTabViewModel(terminalEmulator)
        {
            // 标签名可以由调用方指定,状态栏那一行不跟着改:它说的是「真连到了哪」。
            Title = tabTitle ?? displayName,
            ConnectionStatus = SessionStatus.Connecting,
            // 配了跳板的会话在状态栏点明经由跳板,确认链路生效。
            ConnectionSummary = profile.JumpHostProfileId is null
                ? $"{protocol} • {displayName}"
                : $"{protocol} • {displayName} • {Strings.Get("Msg_ViaJumpHost")}",
            TerminalTypeName = terminalType.ToTermName(),
            // 会话级覆盖优先于全局(F-06):同一个人同时连 UTF-8 的容器和 GBK 的老服务器,
            // 全局只能配一个,另一边就是满屏乱码。
            EncodingName = SessionTerminalSettings.Encoding(profile, settings),
            Profile = profile,
        };
        terminalTab.ReconnectRequested += (_, _) => _ = ReconnectTabAsync(terminalTab);
        terminalTab.Disconnected += (_, _) => OnTabDisconnected(terminalTab);
        // 标签右键「连接信息」。
        terminalTab.ConnectionInfoRequested += (_, _) => OpenConnectionInfo(terminalTab);
        // 失败覆盖层上的「放开这几个算法并重连」。
        terminalTab.AllowAlgorithmsRequested += (_, _) => _ = AllowAlgorithmsAndReconnectAsync(terminalTab);

        // 命令补全:注入建议提供器;提交(已回显校验)的命令进全局历史。
        terminalTab.SuggestionProvider = _suggestionProvider;
        terminalTab.QuickCommandTextResolver = command =>
            ResolveQuickCommandTextAsync(command.Id, command.Name, command.CommandText, terminalTab);
        terminalTab.CommandLineSubmitted += CommandHistory.Record;

        // 树上的状态圆点与「活跃/连接中/离线」标签不在这里订阅:一条配置可能同时开着
        // 多个标签,得取合并结果而不是某一个标签的状态。订阅随标签进出标签栏在
        // SyncTabSubscriptions 里挂上与退订(#321)。

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
        terminalTab.CloseRequested += (_, _) => CloseTerminalTab(terminalTab);
        // Layout.AddDocument 自带激活(见 DockWorkspace),ActiveTerminalTab 由
        // ActiveDocumentChanged 派生 —— 不必也不该在这里再手工设一遍。
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
        TerminalType terminalType = TerminalTypeExtensions.FromTermName(SessionTerminalSettings.TerminalType(profile, settings));
        // 握手全程在右下角圆环上登记一条:标签页内已经有「连接中」覆盖层,而窗口右下角
        // 是"这台机器上还有什么在跑"的统一去处 —— 切到别的标签之后也看得见这条还在连。
        using IBackgroundActivityScope? activity =
            _backgroundActivity?.Begin(Strings.Connecting, ProfileDisplayName(profile));
        SshSession session = await _connectionWorkflowService!.ConnectProfileAsync(
            profile,
            cancellationToken
        );
        try
        {
            await CompleteHandshakeAsync(terminalTab, profile, settings, session, terminalType, cancellationToken);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // 关标签正好赶在握手完成的同一瞬间:会话已经在服务里建起来了,标签却已经没了。
            // 不在这里拆,就留下一条谁都看不见、也永远不会被关掉的连接(端口、隧道一并挂着)。
            TeardownSshSession(session.SessionId);
            throw;
        }
    }

    /// <summary>
    /// 握手成功之后的收尾:开 shell 通道、挂上传输、拉起日志/监视/文件面板。
    /// 与 <see cref="RunHandshakeAsync" /> 分开只是为了让"取消就拆会话"那层
    /// try 包住全部会用到这条会话的步骤。
    /// </summary>
    private async Task CompleteHandshakeAsync(
        TerminalTabViewModel terminalTab,
        SessionProfile profile,
        AppSettings settings,
        SshSession session,
        TerminalType terminalType,
        CancellationToken cancellationToken
    )
    {
        // 标签可能在握手的最后一刻被关掉:此时开 shell 通道等于给一个已经不存在的
        // 标签接线,交给上面那层 catch 去拆会话。
        cancellationToken.ThrowIfCancellationRequested();
        ISshClientWrapper client =
            _sshConnectionService!.GetClient(session.SessionId)
            ?? throw new InvalidOperationException("SSH client was not created for the session.");
        // 先问一句对端是**哪一种** shell,再决定注入哪一段目录上报脚本(#305)。
        // 独立 exec 通道,用完即关;每台主机只探一次,之后走缓存。
        RemoteShellKind shellKind = await ProbeShellKindAsync(client, profile, settings, cancellationToken);
        // 通道打开是网络往返(pty-req + shell,2~3 个 RTT);真异步 API,UI 线程零阻塞。
        IShellStreamWrapper shellStream = await client.CreateShellStreamAsync(
            terminalType.ToTermName(), 120, 32, 0, 0, 4096,
            cancellationToken: cancellationToken
        );
        terminalTab.SessionId = session.SessionId;
        await terminalTab.AttachTransportAsync(shellStream).ConfigureAwait(true);
        terminalTab.Start();
        terminalTab.ConnectionStatus = SessionStatus.Connected;
        await FeedJumpChainNoticeAsync(terminalTab, profile);
        FeedShellStreamNotices(terminalTab, shellStream);
        StartSessionLogging(terminalTab, settings);
        // 探针结论先落到标签上:SendSilentCommand 靠它决定注入行接哪一段摘历史前缀
        // (zsh 有它自己的一段;bash 那段在 fish 里会让整行解析失败,见 ShellHistoryScrub.For)。
        terminalTab.RemoteShellKind = shellKind;
        ArmNestedShellFollow(terminalTab, client, settings, shellKind);
        SendSessionInjections(terminalTab, settings, shellKind, profile);

        // 会话 Id 从现在起才存在(握手完成后)——活动标签订阅在它被赋值前已触发,
        // 因此在这里绑定 SFTP 浏览器(并展示+加载),否则它将一直指向空占位,永远加载不到列表(#22)。
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
    /// 在同一位置重连一个已断开的会话:复用同一个标签、模拟器与回滚缓冲,
    /// 仅重建传输层。由已断开标签上的 Enter / Ctrl+R 触发(或在 exit/reboot 后),
    /// 省去用户打开新标签的操作(#19)。
    /// </summary>
    public Task ReconnectTabAsync(
        TerminalTabViewModel tab,
        CancellationToken cancellationToken = default
    ) =>
        ReconnectTabCoreAsync(tab, automatic: false, cancellationToken);

    /// <summary>
    /// 窗口注入的「放开这几个算法并重连」确认框:参数是标题与正文,确认返回 true。未挂时(headless)不改配置、不重连 ——
    /// 这一步会降低那条连接的安全性,没人点头不能做。
    /// </summary>
    public Func<string, string, Task<bool>>? AllowAlgorithmsConfirmer { get; set; }

    /// <summary>
    /// 覆盖层上的「放开这几个算法并重连」:确认后把诊断点名的那几个算法写进谈不成的那一跳的连接配置
    /// (自定义算法清单,<c>+</c> 追加),然后就地重连。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 只写诊断点名的那几个(<see cref="SshAlgorithmRemedy" />),不开「允许老算法」总开关 ——
    /// 那会一次放开三类 SHA-1 算法外加 SHA-1 的 RSA 用户签名,而谈不成的通常只是其中一类。
    /// </para>
    /// <para>
    /// 跳板上谈不成时改的是那台跳板自己的配置(顺着「跳板主机」往上数到那一跳),不是终点的。
    /// 库里有这条配置就改库里那份并落盘(不经标签手里那份:登录框交回来的副本可能带着用户没让记住的密码);
    /// 终点那一跳还要同步改标签手里的副本,重连用的是它。「最近连接」重建的临时配置不在库里,只改这一次。
    /// </para>
    /// </remarks>
    public async Task AllowAlgorithmsAndReconnectAsync(TerminalTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (tab.AlgorithmRemedy is not { } remedy || tab.Profile is not { } profile
            || tab.ConnectionStatus != SessionStatus.Disconnected || AllowAlgorithmsConfirmer is not { } confirm)
        {
            return;
        }
        if (await AlgorithmRemedies.ConfirmAndSaveAsync(
                profile, remedy, SshAlgorithmRemedyFollowUp.Reconnect, (title, body, _) => confirm(title, body), message => Toasts.Error(message),
                () => tab.AlgorithmRemedy == remedy && tab.ConnectionStatus == SessionStatus.Disconnected).ConfigureAwait(true)
            is not { } applied)
        {
            return;
        }
        if (applied.Stored is not null)
        {
            _ = RefreshSessionTreeAsync();
        }
        if (remedy.HopDepth == 0)
        {
            profile.Ssh = remedy.ApplyTo(profile.Ssh);
        }
        await ReconnectTabAsync(tab).ConfigureAwait(true);
    }

    /// <summary>
    /// 独立 SFTP 文档(含双栏远程里的 SSH / SFTP 那一栏)失败卡片上的「放开这几个算法并重连」:
    /// 与终端标签同一个确认框、同一套写法,确认后经卡片自己的「重新连接」重走一遍连接流程。
    /// </summary>
    /// <param name="document">失败的占位标签。</param>
    /// <param name="profile">这次没连上的那份配置(卡片上的「重新连接」用的就是它)。</param>
    /// <param name="remedy">诊断点名的补救。</param>
    private async Task AllowAlgorithmsAndRetryDocumentAsync(ConnectingDocument document, SessionProfile profile, SshAlgorithmRemedy remedy)
    {
        if (!document.HasError || document.AlgorithmRemedy != remedy || AllowAlgorithmsConfirmer is not { } confirm)
        {
            return;
        }
        if (await AlgorithmRemedies.ConfirmAndSaveAsync(
                profile, remedy, SshAlgorithmRemedyFollowUp.Reconnect, (title, body, _) => confirm(title, body), message => Toasts.Error(message),
                () => document.HasError && document.AlgorithmRemedy == remedy).ConfigureAwait(true)
            is not { } applied)
        {
            return;
        }
        if (applied.Stored is not null)
        {
            _ = RefreshSessionTreeAsync();
        }
        if (remedy.HopDepth == 0)
        {
            // 改的是重试要用的那一份(失败卡片的「重新连接」拿它重走流程),理由同终端标签那条路径。
            profile.Ssh = remedy.ApplyTo(profile.Ssh);
        }
        document.Retry();
    }

    /// <summary>「放开这几个算法」各入口共用的那一段(找到要改的配置、确认、写库)。</summary>
    private SshAlgorithmRemedyFlow AlgorithmRemedies => field ??= new(_sessionRepository, _connectionWorkflowService);

    /// <summary>
    /// 断线后的自动重连(倒计时到点、睡眠唤醒后的批量重连):同 <see cref="ReconnectTabAsync" />,
    /// 但主机指纹要人裁决时直接拒绝、不弹窗(<see cref="ConnectionInfo.IsAutomaticReconnect" />)——
    /// 这时多半没有人在屏幕前,唤醒后一起重连的几个标签还会叠出一摞没人点的模态框。
    /// </summary>
    private Task AutoReconnectTabAsync(TerminalTabViewModel tab) =>
        ReconnectTabCoreAsync(tab, automatic: true, CancellationToken.None);

    private async Task ReconnectTabCoreAsync(
        TerminalTabViewModel tab,
        bool automatic,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(tab);

        // 忽略正在连接或已连接时的重连请求。
        if (tab.ConnectionStatus is SessionStatus.Connecting or SessionStatus.Connected
            || (automatic && tab.AuthenticationRequiresUserAction))
        {
            return;
        }

        // 本地终端标签:重开 = 重新拉起 shell 进程(复用同一标签与缓冲)。
        if (tab.LocalShell is { } localShell)
        {
            await ReopenLocalShellAsync(tab, localShell).ConfigureAwait(true);
            return;
        }

        // 插件终端协议(Telnet…):它们没有 SSH 会话,落进下面的 SSH 重连路径会
        // 拿 Telnet 的主机端口去做 SSH 握手 —— 表现为"重连一次就报认证失败"。
        if (tab.Profile is { ConnectionType: ConnectionType.Plugin } pluginProfile)
        {
            CancellationToken pluginToken = BeginTabConnect(tab, cancellationToken);
            try
            {
                await ReconnectPluginTerminalAsync(tab, pluginProfile, pluginToken).ConfigureAwait(true);
            }
            finally
            {
                EndTabConnect(tab);
            }
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
        await tab.DetachTransportAsync().ConfigureAwait(true);
        UpdateStatusBarForActiveTab();
        // 重连与首连同等对待:右下角圆环也要转起来 —— 自动重连尤其是在后台发生的,
        // 用户多半不在那个标签上。
        using IBackgroundActivityScope? activity =
            _backgroundActivity?.Begin(Strings.Connecting, ProfileDisplayName(tab.Profile));
        // 重连同样是"关标签就不连了":自动重连多半在后台发生,用户看见的往往只有
        // 右下角那条「连接中」,关掉标签是他能表达"不要了"的唯一方式。
        CancellationToken reconnectToken = BeginTabConnect(tab, cancellationToken);
        SshSession? established = null;
        try
        {
            AppSettings settings = _settingsService is not null
                ? await _settingsService.GetSnapshotAsync()
                : new();
            _latestSettings = settings;
            TerminalType terminalType = TerminalTypeExtensions.FromTermName(SessionTerminalSettings.TerminalType(tab.Profile, settings));
            if (!automatic)
            {
                // 用户手动重连:这一次会弹窗问他,上一次败在指纹上的那条不再拦着之后的自动重连。
                tab.ClearHostKeyRejected();
            }
            SshSession session = automatic
                ? await _connectionWorkflowService.ReconnectProfileAutomaticallyAsync(tab.Profile, reconnectToken)
                : await _connectionWorkflowService.ConnectProfileAsync(tab.Profile, reconnectToken);
            established = session;
            ISshClientWrapper client =
                _sshConnectionService.GetClient(session.SessionId)
                ?? throw new InvalidOperationException(
                    "SSH client was not created for the session."
                );
            // 同 RunHandshakeAsync:注入前先问出 shell 种类(#305)。首连已探过的主机命中缓存,不再发探针。
            RemoteShellKind shellKind = await ProbeShellKindAsync(client, tab.Profile, settings, reconnectToken);
            // 同 RunHandshakeAsync:通道打开走真异步 API,UI 线程零阻塞。
            IShellStreamWrapper shellStream = await client.CreateShellStreamAsync(
                terminalType.ToTermName(), 120, 32, 0, 0, 4096,
                cancellationToken: reconnectToken
            );

            // 在新会话输出到达前做一次完全复位(RIS),使新的标语不至于附加在旧缓冲内容之后。
            tab.TerminalEmulator.Feed("\ec"u8.ToArray());
            tab.SessionId = session.SessionId;
            await tab.AttachTransportAsync(shellStream).ConfigureAwait(true);
            tab.Start();
            tab.ConnectionStatus = SessionStatus.Connected;
            await FeedJumpChainNoticeAsync(tab, tab.Profile);
            FeedShellStreamNotices(tab, shellStream);
            tab.ResetReconnectAttempts();
            StartSessionLogging(tab, settings);
            tab.RemoteShellKind = shellKind; // 同 RunHandshakeAsync:注入前先落结论
            ArmNestedShellFollow(tab, client, settings, shellKind);
            // 重连也要跑:配置里那条命令描述的是"每次登进这台机器要做什么"
            // (进 tmux、切目录、sudo),断线重连回来同样成立。
            SendSessionInjections(tab, settings, shellKind, tab.Profile);
            if (_metricsService is not null)
            {
                tab.ResourceMonitor = new(_metricsService, session.SessionId, tab.Title);
            }

            // 重连产生全新的会话 id;重新绑定 SFTP 浏览器并重新加载(#22)。
            ShowFileBrowserForActiveSession();
            StatusBar.ResetUptime();
            UpdateStatusBarForActiveTab();
            LastConnectionError = null;
        }
        catch (Exception) when (reconnectToken.IsCancellationRequested)
        {
            // 取消(关标签 / 调用方撤销):不报错。会话若已经建起来了就一并拆掉 ——
            // 标签已经没了,没人会再去关它。
            if (established is not null)
            {
                TeardownSshSession(established.SessionId);
            }
            tab.MarkDisconnected();
        }
        catch (VelaSshAuthenticationCancelledException)
        {
            // 在动态码框上点了取消:按用户主动断开处理 —— 否则自动重连过几秒又来弹同一个框。
            LastConnectionError = null;
            tab.MarkDisconnectedByUser();
        }
        catch (VelaSshTailscaleAuthenticationException ex) when (ex.RequiresUserAction)
        {
            // 不需要人处理的(认证途中断线、没等到验证链接就超时)落到下面的通用分支,照常退避重试。
            LastConnectionError = DescribeConnectionError(ex, tab.Profile);
            Toasts.Error(LastConnectionError);
            tab.MarkAuthenticationRequiresUserAction(LastConnectionError);
        }
        catch (VelaSshHostKeyRejectedException ex)
        {
            // 主机指纹没过(用户拒绝、变更被阻断,或自动重连时没法问人):再自动试多少次结果都一样 ——
            // 停掉自动重连,等用户手动重连、在弹窗里核对。曾经接着自动试,每试一次就再弹一遍指纹确认框。
            LastConnectionError = DescribeConnectionError(ex, tab.Profile);
            Toasts.Error(LastConnectionError);
            tab.MarkHostKeyRejected(LastConnectionError);
        }
        catch (Exception ex)
        {
            // 重连失败:保留标签,标签页内覆盖层显示“连接失败 + 原因”(设计 yxjmg),不弹全局框。
            LastConnectionError = DescribeConnectionError(ex, tab.Profile);
            Toasts.Error(LastConnectionError);
            // 败在算法协商上、对端有本版实现了只是没放开的:覆盖层多一颗「放开这几个算法并重连」。
            tab.MarkDisconnected(LastConnectionError, SshAlgorithmRemedy.Find(ex));
        }
        finally
        {
            EndTabConnect(tab);
        }
    }

    /// <summary>
    /// 把打开 shell 时附带的提示(X11 / agent 转发开没开成)写进终端顶部,与跳板链那行同一格式。
    /// </summary>
    /// <remarks>
    /// 开成了用灰字,只是告诉用户「这条会话上有这个能力」;没开成用黄字,并带上原因 ——
    /// shell 照样开着,不说一声的话用户只会对着远端一句 <c>cannot open display</c> 摸不着头脑。
    /// </remarks>
    private static void FeedShellStreamNotices(TerminalTabViewModel tab, IShellStreamWrapper shellStream)
    {
        foreach (ShellStreamNotice notice in shellStream.Notices)
        {
            string line = (notice.IsWarning ? "\e[33m▲ " : "\e[90m● ") + notice.Text + "\e[0m\r\n";
            // 同跳板链那行:字节按会话字符集编码,写死 UTF-8 在 GBK 会话上就是乱码。
            tab.TerminalEmulator.Feed(tab.TerminalEmulator.SessionEncoding.GetBytes(line));
        }
    }

    /// <summary>
    /// 经由跳板建立的会话在终端顶部显示灰色提示,标注实际经过的跳板链路。
    /// 纯装饰,失败不影响连接。
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
            // 与断开横幅同理:喂进去的是字节,解码走会话字符集,写死 UTF-8 会让这行提示在
            // GBK 会话上变成乱码。
            tab.TerminalEmulator.Feed(tab.TerminalEmulator.SessionEncoding.GetBytes(notice));
        }
        catch
        {
            // 提示为纯装饰,读取跳板名失败时静默跳过。
        }
    }

    /// <summary>
    /// 关闭标签背后的 SSH 会话:标签的 DisconnectCommand 只拆终端
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
            SessionLogWriter? writer = SessionLogService.CreateWriter(
                tab.Title,
                reason => ReportCaptureStopped("SessionLog_StoppedTitle", "SessionLog_StoppedBody", tab.Title, reason));
            if (writer is not null)
            {
                tab.Bridge.DataReceived += CaptureSink(tab, settings, writer.Write);
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
            // 尺寸取连接建立那一刻的真实列/行,导出 asciicast 的头部才对得上;
            // 此前写死 120×32,任何别的尺寸导出后回放都错行。
            var recorder = new SessionRecorder(
                _recordingStore,
                tab.Title,
                tab.TerminalEmulator.Columns,
                tab.TerminalEmulator.Rows,
                reason => ReportCaptureStopped("Recorder_StoppedTitle", "Recorder_StoppedBody", tab.Title, reason));
            tab.Bridge.DataReceived += CaptureSink(tab, settings, recorder.Write);
            _sessionRecorders[tab] = recorder;
        }
    }

    /// <summary>
    /// 记录收什么:开着「遮盖口令与令牌」(默认)时在记录前面挂一个 <see cref="SessionOutputMasker" />,
    /// 否则原样收。只动记录的那一份,屏幕显示走的是另一条路。
    /// </summary>
    private Action<byte[]> CaptureSink(TerminalTabViewModel tab, AppSettings settings, Action<byte[]> sink)
    {
        if (!settings.Security.MaskSecretsInCaptures)
        {
            return sink;
        }
        var masker = new SessionOutputMasker(sink);
        if (!_captureMaskers.TryGetValue(tab, out List<SessionOutputMasker>? maskers))
        {
            _captureMaskers[tab] = maskers = [];
        }
        maskers.Add(masker);
        return masker.Write;
    }

    /// <summary>
    /// 录制或会话日志中途停了 —— 必须让用户知道。
    /// </summary>
    /// <remarks>
    /// 这两件事都是"开着就不管了"的后台功能:悄悄停掉,用户会一直以为整场操作都留着记录,
    /// 直到事后要用时才发现只有开头。所以走消息中心(可回看),而不是一闪而过的状态栏。
    /// 回调可能来自任意线程,发布本身是异步的,这里不等它。
    /// </remarks>
    private void ReportCaptureStopped(string titleKey, string bodyKey, string sessionTitle, string reason)
    {
        System.Diagnostics.Trace.WriteLine($"[VelaShell] {titleKey}: {sessionTitle}: {reason}");
        if (_notificationCenter is not { } center)
        {
            return;
        }
        _ = center.PublishAsync([
            new NotificationItem
            {
                // 每个会话每次连接只报一条,重复投递会被中心按 id 去重。
                Id = $"capture-stopped:{titleKey}:{sessionTitle}:{DateTime.UtcNow:yyyyMMddHHmm}",
                Kind = NotificationKind.System,
                Severity = NotificationSeverity.Warning,
                Title = Strings.Get(titleKey),
                Body = Strings.Format(bodyKey, sessionTitle, reason),
                PublishedAt = DateTime.UtcNow
            }
        ]);
    }

    private void StopSessionLogging(TerminalTabViewModel tab)
    {
        if (_captureMaskers.Remove(tab, out List<SessionOutputMasker>? maskers))
        {
            // 扣着的半行(正写到一个值的中间)遮完放进记录,再关记录
            foreach (SessionOutputMasker masker in maskers)
            {
                masker.Flush();
            }
        }
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
    /// <summary>
    /// 网络恢复 / 唤醒之后:把所有"非用户主动断开"的远端会话立刻重连一次,并重置退避计数。
    /// </summary>
    /// <remarks>
    /// 重置计数是关键的一半。之前重试可能已经退到几十秒一次、甚至耗尽了 MaxRetries;
    /// 而"网络刚回来"是一个全新的、成功率很高的时机,不该继续按旧的挫败节奏走。
    /// <para>
    /// 本地终端不在此列:shell 退出是用户意图,自动拉起会没完没了。
    /// 用户自己点了断开的也不碰 —— 那是明确的意图。
    /// </para>
    /// </remarks>
    internal void ReconnectAllAfterResume()
    {
        if (_latestSettings?.General.AutoReconnect != true)
        {
            return;
        }
        foreach (TerminalTabViewModel tab in TerminalTabs.ToArray())
        {
            if (ReconnectPolicy.ShouldReconnect(
                    autoReconnectEnabled: true,
                    tab.ConnectionStatus == SessionStatus.Disconnected,
                    tab.UserRequestedDisconnect,
                    tab.LocalShell is not null,
                    tab.RemoteShellExited,
                    tab.HostKeyRejected,
                    tab.AuthenticationRequiresUserAction))
            {
                tab.ResetReconnectAttempts();
                _ = AutoReconnectTabAsync(tab);
            }
        }
    }

    /// <summary>
    /// 第 <paramref name="attempt" /> 次自动重连之前该等多久(秒)。
    /// </summary>
    /// <remarks>
    /// 指数退避 1、2、4、8… 封顶在用户配的 <paramref name="configuredSeconds" />。
    /// 固定间隔在两头都不对:网线刚插回来的那一瞬,等满 30 秒才试是白等;
    /// 而服务器真的宕了,每 30 秒敲一次门也只是徒劳地刷状态栏。
    /// 从 1 秒起跳能抓住"抖一下就好"的绝大多数情况,退到设置值之后与原行为一致。
    /// </remarks>
    /// <param name="attempt">第几次尝试(从 1 起)。</param>
    /// <param name="configuredSeconds">设置里的重连间隔,作为退避上限。</param>
    /// <returns>等待秒数。</returns>
    internal static int ReconnectDelaySeconds(int attempt, int configuredSeconds) =>
        ReconnectPolicy.DelaySeconds(attempt, configuredSeconds);

    private void OnTabDisconnected(TerminalTabViewModel tab)
    {
        StopSessionLogging(tab);

        // 会话断开后 SFTP 通道随之失效:驱逐缓存的文件面板、收掉它开出来的外部编辑会话并释放 SFTP 客户端。
        // 重连会拿到新的 SessionId,面板届时按新会话重建(旧会话名下的编辑会话不会跟过去)。
        _ = CloseSftpForTab(tab);

        // 不论主动断开还是远端掉线,都把底层 SSH 客户端一并拆掉;
        // 重连会新建会话,不受影响。
        TeardownSshSession(tab.SessionId);
        AppSettings? settings = _latestSettings;
        if (settings is null)
        {
            return;
        }
        // 先问清楚"接下来会不会自动重连",再决定要报几条 —— 顺序反过来的话,
        // 断线那一条已经推出去了,后面的倒计时只能在它旁边再叠一条同义的。
        // 无头单元测试在没有 Avalonia 应用的情况下构造此 VM;此处无计时器。
        // 本地终端不自动重开:shell 退出(exit)是用户意图,自动拉起会没完没了。
        // 远端 shell 退出同理 —— 在远端敲 exit 和点断开按钮说的是同一句话(#383)。
        int maxRetries = Math.Max(1, settings.General.MaxRetries);
        bool autoReconnectApplies =
            Application.Current is not null
            && ReconnectPolicy.ShouldReconnect(
                   settings.General.AutoReconnect,
                   isDisconnected: true,
                   tab.UserRequestedDisconnect,
                   tab.LocalShell is not null,
                   tab.RemoteShellExited,
                   tab.HostKeyRejected,
                   tab.AuthenticationRequiresUserAction);
        if (autoReconnectApplies)
        {
            tab.MaxReconnectAttempts = maxRetries; // 全部自动重连路径共用同一权威值(设置审计 C-02)
        }
        bool willAutoReconnect =
            autoReconnectApplies
            && ReconnectPolicy.HasAttemptsLeft(tab.ReconnectAttempts, maxRetries);

        if (settings.General.NotifyOnDisconnect)
        {
            // 只在"没人会替你重连"时才单报一条断线。会自动重连的时候,下面那条倒计时
            // 已经把断开、还剩几次、下次什么时候都说全了 —— 再叠一条「已断开 + 立即重连」
            // 是同一件事说两遍,而且两条一起占掉右下角,谁也没多告诉用户一个字。
            // 反过来,重连不介入(关了开关、次数用尽、本地 shell)时,这条带按钮的提示
            // 就是唯一的出口,必须留着。
            if (!willAutoReconnect)
            {
                // 断线带一个「立即重连」按钮:这是用户看到这条消息时唯一想做的事,
                // 而原先它只是状态栏里一句会被下一条消息盖掉的文字。
                Toasts.Error(
                    Strings.Format("Msg_TabDisconnected", tab.Title),
                    Strings.Get("Toast_ReconnectNow"),
                    () => _ = ReconnectTabAsync(tab));
            }
            if (!ReferenceEquals(ActiveTerminalTab, tab))
            {
                tab.HasBellAlert = true;
            }
        }
        if (settings.General.SoundAlerts && OperatingSystem.IsWindows())
        {
            SystemSound.Alert();
        }

        if (!willAutoReconnect)
        {
            return;
        }
        tab.IncrementReconnectAttempt();
        int delaySeconds = ReconnectDelaySeconds(tab.ReconnectAttempts, settings.General.ReconnectIntervalSeconds);
        // 倒计时按标签合并:每次重试都刷新同一条,而不是每试一次堆一条。
        // 三个标签同时掉线时也各占一条,互不覆盖 —— 状态栏那一个字符串做不到这件事。
        Toasts.Warning(
            Strings.Format(
                "Msg_AutoReconnectCountdown",
                tab.Title,
                delaySeconds,
                tab.ReconnectAttempts,
                maxRetries
            ),
            mergeKey: $"reconnect:{tab.SessionId}"
        );
        DispatcherTimer.RunOnce(
            () =>
            {
                // 等待期间用户可能已手动重连、关掉标签、主动断开,
                // 或者(重连成功后)在远端敲了 exit —— 后者同样不该再被拉回来。
                // 手动重连在弹窗里拒绝了指纹的,同样不再自动试。
                if (
                    tab
                        is
                    {
                        ConnectionStatus: SessionStatus.Disconnected,
                        UserRequestedDisconnect: false,
                        RemoteShellExited: false,
                        HostKeyRejected: false,
                        AuthenticationRequiresUserAction: false
                    }
                    && TerminalTabs.Contains(tab)
                )
                {
                    _ = AutoReconnectTabAsync(tab);
                }
            },
            TimeSpan.FromSeconds(delaySeconds)
        );
    }

    /// <summary>
    /// 探一句对端是<b>哪一种</b> shell(#305),据此决定注入
    /// <see cref="ShellIntegrationScript" /> 里的哪一段目录上报脚本,或者干脆不注入。
    /// 「上报终端工作目录」关着、也没有给 X 程序的环境变量要注入(见 <see cref="X11EnvironmentScript" />)时直接返回
    /// <see cref="RemoteShellKind.Unknown" /> —— 连探针那条 exec 通道都不开,守住"关掉就一个字节都不发"的承诺(#286)。
    /// 结果按主机缓存,只有每台机器的首次连接付这次往返。
    /// </summary>
    /// <remarks>
    /// 刻意排在开 shell 通道**之前**、而不是与之并行:一是探针用完即关,不与 shell 通道并存,
    /// 对 <c>MaxSessions 1</c> 的严苛服务端也只占一个通道名额;二是结论在注入前就已就绪,
    /// 注入仍然赶在 shell 画出提示符之前,bash 那段末尾的清行才落在该落的地方。
    /// </remarks>
    private static Task<RemoteShellKind> ProbeShellKindAsync(
        ISshClientWrapper client,
        SessionProfile? profile,
        AppSettings settings,
        CancellationToken cancellationToken
    ) =>
        settings.TerminalBehavior.ReportWorkingDirectory || WantsInputMethodEnvironment(settings, profile) || WantsRendererEnvironment(profile)
            ? RemoteShellProbe.DetectAsync(
                client,
                RemoteShellProbe.CacheKey(profile?.Host, profile?.Port ?? 22, profile?.Username),
                cancellationToken
            )
            : Task.FromResult(RemoteShellKind.Unknown);

    /// <summary>
    /// 这次连接会注入目录上报钩子时,顺带挂上嵌套 shell 的补注(#637,见 <see cref="NestedShellFollower" />):
    /// 用户之后 <c>su - node</c>、<c>sudo -i</c> 进了新 shell,也照样跟随。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 必须挂在 <see cref="SendSessionInjections" /> <b>之前</b>:配置里的认证后命令(<c>sudo su -</c>)
    /// 就在那一串注入里发出,挂晚了它那一行就没人看见。
    /// </para>
    /// <para>
    /// 不注入钩子的连接(开关关着、探不出 shell 种类、Windows 对端)一律不挂 —— 登录 shell 都没装,
    /// 谈不上"补"。开关在会话中途被关掉时也停手:<c>enabled</c> 每次动手前现读设置。
    /// </para>
    /// </remarks>
    private void ArmNestedShellFollow(
        TerminalTabViewModel tab,
        ISshClientWrapper client,
        AppSettings settings,
        RemoteShellKind shellKind)
    {
        if (WorkingDirectoryScript(settings, shellKind).Length == 0)
        {
            tab.DisableNestedShellFollow();
            return;
        }
        tab.EnableNestedShellFollow(
            ct => ForegroundShellProbe.DetectAsync(client, ct),
            () => (_latestSettings ?? settings).TerminalBehavior.ReportWorkingDirectory);
    }

    /// <summary>注入前要等对端静默多久。</summary>
    private static readonly TimeSpan InjectionIdleWindow = TimeSpan.FromMilliseconds(300);

    /// <summary>等静默的上限:有的服务器登录后一直在刷东西,不能为此永远不注入。</summary>
    private static readonly TimeSpan InjectionMaxWait = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 连接/重连成功后的全部注入(目录上报钩子 → 全局启动命令 → 本条配置的初始目录与认证后命令),
    /// <b>统一推迟到对端安静下来之后</b>再发。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么必须等。</b>目录上报脚本的注入窗口是"从现在起整段扣住,直到哨兵返回"
    /// (理由见 <see cref="EchoSuppressor.OpenWindow" />:那一行太长,回显会被各家 shell
    /// 折行重绘,逐字节匹配咬不住)。窗口 armed 得太早就会把横幅、MOTD、rc 的欢迎语
    /// 连同第一个提示符一起吞掉 —— 用户登进去面对一块空屏。等它们先放完,窗口里就只剩我们自己的东西。
    /// </para>
    /// <para>
    /// <b>为什么四条一起推迟,而不是只推迟钩子。</b>顺序是有意义的:钩子先装好,用户命令里的
    /// <c>cd</c> 才报得出来;而用户命令里可能有 <c>exec zsh</c> / <c>tmux attach</c> 这种会换掉
    /// 整个 shell 的东西,它必须排在钩子之后。只推迟钩子就会把这个顺序颠倒过来。
    /// </para>
    /// <para>
    /// 这几百毫秒里标签可能已经断开、被关掉,或者已经重连成另一个会话 —— 回调里因此重新验一遍
    /// 桥还是不是原来那个,不然就是把命令灌进了别人的 shell。
    /// </para>
    /// </remarks>
    private void SendSessionInjections(
        TerminalTabViewModel tab,
        AppSettings settings,
        RemoteShellKind shellKind,
        SessionProfile? profile
    )
    {
        if (tab.Bridge is not { } bridge)
        {
            return;
        }
        bridge.RunWhenOutputIdle(
            InjectionIdleWindow,
            InjectionMaxWait,
            () =>
            {
                if (!ReferenceEquals(tab.Bridge, bridge) || !tab.IsConnected)
                {
                    return;
                }
                SendStartupCommand(tab, settings, shellKind, profile);
                if (profile is not null)
                {
                    SendPostAuthCommand(tab, profile);
                }
            });
    }

    /// <summary>
    /// 连接成功后按设置注入目录上报钩子,并追加用户配置的"连接后执行命令"
    /// (设置 → 终端 → 会话)。PTY 输入由内核缓冲,shell 就绪后才会读取,
    /// 无需等待提示符。
    /// </summary>
    /// <param name="tab">已挂上传输的终端标签。</param>
    /// <param name="settings">当前设置。</param>
    /// <param name="shellKind">
    /// <see cref="ProbeShellKindAsync" /> 的结论,决定发哪一段脚本;
    /// <see cref="RemoteShellKind.Unknown" />/<see cref="RemoteShellKind.NonPosix" /> 时一个字节都不发。
    /// 用户自己配的"连接后执行命令"不受它影响 —— 那是用户明确要求执行的东西,
    /// 对端是什么 shell 由用户自己负责。
    /// </param>
    /// <param name="profile">本次连接的配置:开着 X11 转发时连同给 X 程序的环境变量一起注入(<c>XMODIFIERS</c>、<c>GSK_RENDERER</c>,见 <see cref="X11EnvironmentScript" />)。</param>
    /// <remarks>
    /// <para>
    /// <b>钩子与用户命令分两条发,钩子那条带「注入窗口」</b>(把注入<b>引发的</b>输出也一并扣住,
    /// 见 <see cref="EchoSuppressor" />),用户命令那条不带 —— 它是用户明确要跑的东西,
    /// 输出与报错都必须照常显示。
    /// </para>
    /// <para>
    /// 两条分开发而不是拼成一行,靠的是抑制器**一个实例装多根针**:后来那条只是搭车剥回显,
    /// 不会把还开着的窗口顶掉。而字节序天然把两者分得很干净 —— shell 是一行一行读的,
    /// <c>回显(钩子) → 钩子的副作用 → 提示符(带 OSC 7,窗口在此闭合) → 回显(用户命令) →
    /// 用户命令的输出</c>,窗口罩住的正好只有钩子那一段。
    /// </para>
    /// <para>
    /// 顺序是「先钩子、后用户命令」:钩子先装好,用户命令里的 <c>cd</c> 才报得出来。
    /// </para>
    /// </remarks>
    private static void SendStartupCommand(
        TerminalTabViewModel tab,
        AppSettings settings,
        RemoteShellKind shellKind,
        SessionProfile? profile
    )
    {
        string script = WorkingDirectoryScript(settings, shellKind);
        string environment = X11EnvironmentScript(settings, profile, shellKind);
        if (script.Length > 0 || environment.Length > 0)
        {
            // 装载排在哨兵之前(可能炸,而用户没敲过这一行,凭什么为它的报错买单);
            // 末尾那次上报排在哨兵之后(必须让仿真器看见,否则文件浏览器要等到用户第一次 cd)。
            // 给 X 程序的那几句环境变量(XMODIFIERS、GSK_RENDERER)也藏在哨兵之前,与目录钩子同一行发出,不多占一个提示符周期。
            string hidden = environment.Length == 0 ? script : script.Length == 0 ? environment : environment + "; " + script;
            tab.SendShellIntegration(hidden, script.Length > 0 ? ShellIntegrationScript.FunctionName : string.Empty);
        }
        if (settings.TerminalBehavior.StartupCommand?.Trim() is { Length: > 0 } userCommand)
        {
            tab.SendSilentCommand(userCommand);
        }
    }

    /// <summary>
    /// 这个会话的 X 程序会不会经内置 X Server 用本机输入法(F5 第二步):开着 X11 转发、没指定显示地址(于是用 VelaShell 管理的 X Server)、
    /// 引擎是内置的、设置里开着「X 窗口里用本机输入法」。
    /// </summary>
    internal static bool WantsInputMethodEnvironment(AppSettings settings, SessionProfile? profile) =>
        settings.XServer is { Engine: XServerEngines.BuiltIn, UseHostInputMethod: true }
        && profile?.Ssh is { X11Forwarding: true } ssh
        && string.IsNullOrWhiteSpace(ssh.X11Display);

    /// <summary>
    /// 本次要注入的 <c>XMODIFIERS</c>(见 <see cref="XServerInputMethod.ShellExport" />):Xlib 只在它写了 <c>@im=velashell</c> 时才去连内置 X Server
    /// 的输入法服务端,sshd 又通常不放行这个变量的 env 请求。远端 shell 里已经设了的不动;不该注入、探不出 shell 种类时为空串。
    /// </summary>
    internal static string InputMethodEnvironmentScript(AppSettings settings, SessionProfile? profile, RemoteShellKind shellKind) =>
        WantsInputMethodEnvironment(settings, profile) ? XServerInputMethod.ShellExport(shellKind) : string.Empty;

    /// <summary>
    /// 这个会话开着 X11 转发(不论显示交给哪个 X 服务端):远端的 GTK 4 程序默认用 GL,经 SSH 只能退到 llvmpipe、每帧把整窗像素发过来,
    /// 连接后注入 <c>GSK_RENDERER=cairo</c>(见 <see cref="RemoteGtkRenderer" />)。
    /// </summary>
    internal static bool WantsRendererEnvironment(SessionProfile? profile) => profile?.Ssh is { X11Forwarding: true };

    /// <summary>
    /// 连接后为 X 程序注入的环境变量:<c>XMODIFIERS</c>(见 <see cref="InputMethodEnvironmentScript" />)与 <c>GSK_RENDERER</c>
    /// (见 <see cref="RemoteGtkRenderer" />),各自已经设了的不动;都不该注入、探不出 shell 种类时为空串。
    /// </summary>
    internal static string X11EnvironmentScript(AppSettings settings, SessionProfile? profile, RemoteShellKind shellKind)
    {
        string inputMethod = InputMethodEnvironmentScript(settings, profile, shellKind);
        string renderer = WantsRendererEnvironment(profile) ? RemoteGtkRenderer.ShellExport(shellKind) : string.Empty;
        return inputMethod.Length == 0 ? renderer : renderer.Length == 0 ? inputMethod : inputMethod + "; " + renderer;
    }

    /// <summary>
    /// 本次该注入的目录上报脚本:开关关着、或探不出 shell 种类时一律返回空串
    /// (<see cref="TerminalTabViewModel.SendShellIntegration" /> 对空串直接不发,连回车都不会多出来)。
    /// </summary>
    /// <param name="settings">当前设置 —— 「上报终端工作目录」开关在这里(#286)。</param>
    /// <param name="shellKind">探针结论;见 <see cref="ShellIntegrationScript.For" />。</param>
    internal static string WorkingDirectoryScript(AppSettings settings, RemoteShellKind shellKind) =>
        settings.TerminalBehavior.ReportWorkingDirectory
            ? ShellIntegrationScript.For(shellKind)
            : string.Empty;

    /// <summary>
    /// 注入**这一条配置专属**的「认证后执行命令」(连接对话框 → 高级选项)。
    /// <para>
    /// 与 <see cref="SendStartupCommand" /> 分开发而不是拼进同一串:那条是全局的、每个终端都跑,
    /// 这条只跟着一条配置走,而且带自己的延迟。拼在一起就没法各自延迟,也说不清谁先谁后。
    /// 顺序固定为「先全局、后本条」—— 与用户在两个界面上看到的顺序一致。
    /// </para>
    /// <para>
    /// 延迟 &gt; 0 时用 <see cref="DispatcherTimer.RunOnce" /> 延后发,而不是 <c>await Task.Delay</c>:
    /// 握手方法不能因为用户配了 5 秒延迟就把连接流程(刷新最近连接、绑定 SFTP 面板、状态栏)
    /// 一起挂起 5 秒。定时器回调里重新验一遍会话身份 —— 这几秒里标签可能已经断开、被关掉,
    /// 或者已经重连成另一个会话,那时候再把命令灌进去就是灌进了别人的 shell。
    /// </para>
    /// </summary>
    /// <param name="tab">已挂上传输、状态已置为已连接的终端标签。</param>
    /// <param name="profile">本次连接所用的配置。</param>
    private void SendPostAuthCommand(TerminalTabViewModel tab, SessionProfile profile)
    {
        // 初始目录(F-06)先走一步:它是"进去之后先站在哪儿",本条命令则是"站好之后干什么",
        // 顺序反了的话一条 `tail -f ./app.log` 会在家目录里找不到文件。
        // 与本条命令拼成一串不行 —— 那条有自己的延迟,而切目录不该等。
        SendStartupDirectory(tab, profile);
        if (profile.PostAuthCommand?.Trim() is not { Length: > 0 } command)
        {
            return;
        }
        int delaySeconds = Math.Clamp(
            profile.PostAuthCommandDelaySeconds,
            0,
            SessionProfile.MaxPostAuthCommandDelaySeconds
        );
        if (delaySeconds == 0)
        {
            tab.SendSilentCommand(command);
            return;
        }

        // 会话 id 在握手里刚被赋值;它就是"还是同一条会话吗"的判据。
        Guid sessionId = tab.SessionId;
        void Arm() => DispatcherTimer.RunOnce(
            () =>
            {
                if (tab.SessionId == sessionId && tab.ConnectionStatus == SessionStatus.Connected)
                {
                    tab.SendSilentCommand(command);
                }
            },
            TimeSpan.FromSeconds(delaySeconds)
        );

        // 整条注入链现在推迟到「对端安静下来」之后才跑,而那一下是在线程池线程上触发的
        // (见 SendSessionInjections);DispatcherTimer 只能在 UI 线程上挂,所以先编组回去。
        if (Dispatcher.UIThread.CheckAccess())
        {
            Arm();
        }
        else
        {
            Dispatcher.UIThread.Post(Arm);
        }
    }

    /// <summary>
    /// 登录后切到会话配置里指定的初始目录(F-06);未指定则什么都不做。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 发的是一条静默 <c>cd</c>,与「认证后执行命令」同一条通道 —— 不另起 exec 通道,
    /// 因为切目录必须发生在<b>用户那个交互 shell</b> 里,而 exec 通道是另一个进程。
    /// </para>
    /// <para>
    /// 路径用单引号裹住并转义内部的单引号:目录名里带空格是常事(<c>/opt/my app</c>),
    /// 不裹就变成了两个参数;而带引号的路径不转义则会把后面的内容当成新命令 ——
    /// 那是把用户自己的配置变成了一条注入。引号按登录 shell 的规则加(<see cref="RemoteSearch.QuoteFor" />):
    /// fish 的单引号里 <c>\'</c> 是转义,POSIX 那种 <c>'\''</c> 碰上以 <c>\</c> 结尾的一段就会提前收引号。
    /// </para>
    /// <para>
    /// 目录名里有控制字符时<b>不发</b>、改为提示一句(见 <see cref="SilentCommand.CanTypeLiterally" />):
    /// 这一行是敲进 PTY 的,引号挡不住行规程与行编辑器 —— 自从「在此处打开终端」把远端列表里的目录名送到这里,
    /// 别人建的一个带 <c>^U</c>、回车的目录就能把整行换成他的命令。
    /// </para>
    /// </remarks>
    private void SendStartupDirectory(TerminalTabViewModel tab, SessionProfile profile)
    {
        // 文件栏「在此处打开终端」开出来的标签有自己的目录,压过配置里的初始目录(见 OpenTerminalHereAsync)。
        if ((tab.StartupDirectoryOverride ?? SessionTerminalSettings.StartupDirectory(profile)) is not { } directory)
        {
            return;
        }
        if (!SilentCommand.CanTypeLiterally(directory))
        {
            // 注入链跑在对端安静下来之后的线程池线程上(见 SendSessionInjections),浮层提示只能在 UI 线程上推。
            void Warn() => Toasts.Warning(Strings.Get("Sftp_OpenTerminalHereUnsafeDirectory"));
            if (Dispatcher.UIThread.CheckAccess())
            {
                Warn();
            }
            else
            {
                Dispatcher.UIThread.Post(Warn);
            }
            return;
        }
        tab.SendSilentCommand("cd " + RemoteSearch.QuoteFor(tab.RemoteShellKind, directory));
    }

    /// <summary>
    /// 用户语义的关闭标签统一入口(覆盖层关闭按钮 / Esc / Ctrl+W / 命令面板)。
    /// 必须走 <see cref="DockWorkspace.CloseDocument" />:只有它会触发 DocumentClosed,
    /// 从而把停靠文档、会话日志与底层 SSH/PTY 传输一并拆干净。
    /// </summary>
    public void CloseTerminalTab(TerminalTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        // 文档已被静默移除(连接失败路径)时这里找不到 —— 那时该做的收尾在
        // RemoveTerminalTab 里已经做完了,本方法无事可做。
        if (FindDocument(tab) is { } document)
        {
            // RequestClose 而不是 CloseDocument:这是**用户语义**的关闭,要过确认闸。
            Layout.RequestClose(document);
        }
    }

    /// <summary>找到承载这个标签的停靠文档;标签已被静默移除时为 null。</summary>
    private TerminalDocument? FindDocument(TerminalTabViewModel tab) =>
        Layout.AllDocuments()
              .OfType<TerminalDocument>()
              .FirstOrDefault(d => ReferenceEquals(d.Terminal, tab));

    /// <summary>
    /// 跳到当前标签条上的第 N 个文档(0 起;负数 = 最后一个)。
    /// </summary>
    /// <remarks>
    /// 按**活动组内**的顺序而不是全局标签集合:分屏后每个组有自己的标签条,
    /// 用户数的是眼前那一条。索引超出时落到最后一个,而不是什么都不做 ——
    /// "Ctrl+Alt+8 但只有 5 个标签"时跳到最后一个比毫无反应更符合预期。
    /// </remarks>
    private void GotoTab(int index)
    {
        DockGroup? group = Layout.ActiveDocument is { } active
            ? Layout.FindGroup(active) ?? Layout.PrimaryGroup
            : Layout.PrimaryGroup;
        if (group is null || group.Documents.Count == 0)
        {
            return;
        }
        int slot = index < 0
            ? group.Documents.Count - 1
            : Math.Min(index, group.Documents.Count - 1);
        Layout.ActivateDocument(group.Documents[slot]);
        TerminalFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>注册一条窗格移焦命令(Alt+方向)。</summary>
    private void RegisterPaneFocusCommand(string id, string titleKey, DockDirection direction, string shortcut) =>
        Commands.Register(
            new(
                id,
                Strings.Get(titleKey),
                Strings.Get("CmdCat_Actions"),
                () => FocusPane(direction),
                () => Layout.HasMultipleGroups,
                shortcut
            )
        );

    /// <summary>把焦点移到指定方向上相邻的窗格(没有邻居就什么也不做)。</summary>
    private void FocusPane(DockDirection direction)
    {
        if (Layout.ActiveDocument is not { } active
            || Layout.FindGroup(active) is not { } current
            || DockWorkspace.FindNeighborGroup(current, direction) is not { } neighbor)
        {
            return;
        }
        DockDocument? target = neighbor.ActiveDocument ?? neighbor.Documents.FirstOrDefault();
        if (target is null)
        {
            return;
        }
        Layout.ActivateDocument(target);
        TerminalFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>关闭所有标签(Ctrl+Shift+W)。</summary>
    /// <remarks>
    /// 走 <c>RequestCloseMany</c>:一次确认放行全部,而不是逐个弹框;
    /// 也先取了快照,免得边遍历边关漏掉一半。
    /// </remarks>
    private void CloseAllTabs() => Layout.RequestCloseMany([.. Layout.AllDocuments()]);

    /// <summary>
    /// 关闭前的确认闸:标签的会话还连着,并且用户没关掉这个开关时,先问一句。
    /// </summary>
    /// <remarks>
    /// 装在 <see cref="DockWorkspace.CloseInterceptor" /> 上,六个关闭入口一处管住。
    /// 已断开的标签、SFTP / 插件文档一律直接放行 —— 关掉它们不丢任何东西。
    /// 没有装确认回调(无头测试)时同样放行,不让测试挂在一个永远不会有人点的对话框上。
    /// </remarks>
    private async Task<bool> ConfirmCloseDocumentsAsync(IReadOnlyList<DockDocument> documents)
    {
        if (_latestSettings?.General.ConfirmCloseConnectedTab != true || CloseConfirmer is not { } confirm)
        {
            return true;
        }
        TerminalTabViewModel[] connected =
        [
            .. documents.OfType<TerminalDocument>()
                .Select(document => document.Terminal)
                .Where(tab => tab.IsConnected)
        ];
        return connected.Length switch
        {
            0 => true,
            1 => await confirm(
                Strings.Get("Main_CloseTabConfirmTitle"),
                Strings.Format("Main_CloseTabConfirmBody", connected[0].Title)).ConfigureAwait(true),
            _ => await confirm(
                Strings.Get("Main_CloseTabConfirmTitle"),
                Strings.Format("Main_CloseTabConfirmMany", connected.Length)).ConfigureAwait(true)
        };
    }

    /// <summary>
    /// 由视图注入的确认对话框(标题、正文 → 用户是否确认)。为 null 时一律放行。
    /// </summary>
    public Func<string, string, Task<bool>>? CloseConfirmer { get; set; }

    /// <summary>关闭当前活动标签,终端与 SFTP 文档同等对待(Ctrl+W / session.close)。</summary>
    private void CloseActiveTab()
    {
        if (Layout.ActiveDocument is { } document)
        {
            Layout.RequestClose(document);
        }
    }

    /// <summary>
    /// 为一个「连接中」标签登记取消源:返回的令牌 = 调用方的令牌 ∪ 用户关掉这个标签。
    /// </summary>
    /// <remarks>
    /// 每次尝试(首连、认证重试、重连)各登记一次;上一轮的源在这里先收掉 ——
    /// 留着的话,关标签拉的是一根早就断了的绳。
    /// </remarks>
    /// <param name="tab">正在连接的标签。</param>
    /// <param name="outer">调用方的取消令牌。</param>
    /// <returns>本次握手要用的取消令牌。</returns>
    private CancellationToken BeginTabConnect(TerminalTabViewModel tab, CancellationToken outer)
    {
        EndTabConnect(tab);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(outer);
        _tabConnectCancellations[tab] = cancellation;
        return cancellation.Token;
    }

    /// <summary>本次握手结束(连上、失败或被取消):注销并释放取消源。</summary>
    /// <remarks>释放放在流程结束这一处,而不是取消的那一刻:握手还在飞的时候释放取消源,
    /// 底层库再往这个令牌上挂回调就会撞上 <see cref="ObjectDisposedException" /> ——
    /// 那正是"取消"被翻译成一句莫名其妙的连接错误的来路。</remarks>
    /// <param name="tab">刚结束握手的标签。</param>
    private void EndTabConnect(TerminalTabViewModel tab)
    {
        if (_tabConnectCancellations.Remove(tab, out CancellationTokenSource? cancellation))
        {
            cancellation.Dispose();
        }
    }

    /// <summary>撤销这个标签正在进行的握手(标签被关掉时调;没在连接时是空操作)。</summary>
    /// <param name="tab">被关掉的标签。</param>
    private void CancelTabConnect(TerminalTabViewModel tab)
    {
        if (_tabConnectCancellations.TryGetValue(tab, out CancellationTokenSource? cancellation))
        {
            cancellation.Cancel();
        }
    }

    private async Task RemoveTerminalTabAsync(TerminalTabViewModel tab, TerminalDocument document)
    {
        // 静默移除也是"这个标签不要了":还在飞的握手一并撤掉(幂等,通常此刻已经取消过)。
        CancelTabConnect(tab);
        StopSessionLogging(tab);
        // 防御性驱逐 SFTP 面板缓存:本路径(连接失败/取消)静默移除文档,不触发
        // DocumentClosed,若标签曾短暂连上过,缓存里的面板会悬挂。幂等,无缓存时空操作。
        _ = CloseSftpForTab(tab);
        Layout.RemoveDocument(document);
        if (ReferenceEquals(ActiveTerminalTab, tab))
        {
            // 静默移除不触发 ActiveDocumentChanged,活动标签得自己补一次:
            // 停靠布局里还剩的第一个终端文档,没有就置空。
            ActiveTerminalTab = TerminalTabs.FirstOrDefault();
        }
        await tab.DisposeAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 指代一条连接的名称:优先用用户起的显示名(他认得的那个),没起名才退回主机地址。
    /// </summary>
    /// <remarks>刻意不带用户名与端口:这个名字会出现在状态栏与后台任务清单里(安全要求,设计 gzmsb)。</remarks>
    /// <param name="profile">连接配置。</param>
    /// <returns>显示名。</returns>
    private static string ProfileDisplayName(SessionProfile profile) =>
        string.IsNullOrWhiteSpace(profile.Name) ? profile.Host : profile.Name;

    /// <summary>缺少连接所需凭据(用户名/密码/私钥/证书)时需要先走登录验证流程。</summary>
    /// <remarks>
    /// <para>
    /// 引用共享凭据的配置一律不先弹(#550):用户名与认证材料都由凭据提供(手输过的材料也算在内),
    /// 真缺了什么,解析时会以 <see cref="CredentialProviderException" /> 报出来,连接流程再退回登录框。
    /// </para>
    /// <para>
    /// 存着的密码 / 私钥口令在这台设备上解不开(<see cref="CredentialMaterial.HasUnreadableSecret(SessionProfile)" />)
    /// 也算缺:以前当成有密码,拿密文去试一次、认证失败了才弹框 —— 白白一次失败认证,还可能撞上 fail2ban。
    /// 现在直接弹,登录框上说明为什么(第二轮第 20 条)。
    /// </para>
    /// </remarks>
    private static bool RequiresCredentials(SessionProfile profile) =>
        profile.AuthMethod == AuthMethod.Tailscale
            ? string.IsNullOrWhiteSpace(profile.Username)
            : profile.CredentialSource is null
              && (RequiresInlineCredentials(profile) || CredentialMaterial.HasUnreadableSecret(profile));

    private static bool RequiresInlineCredentials(SessionProfile profile) =>
        string.IsNullOrWhiteSpace(profile.Username)
        || (profile.AuthMethod == AuthMethod.Password && string.IsNullOrEmpty(profile.Password))
        || (
            profile.AuthMethod == AuthMethod.PrivateKey
            && string.IsNullOrWhiteSpace(profile.PrivateKeyPath)
        )
        // 证书那一路缺任一个文件都得先问用户:证书说明"我是谁",私钥才是签名的那把,
        // 少了哪个都只会换来一句笼统的 publickey 被拒。
        || (
            profile.AuthMethod == AuthMethod.Certificate
            && (string.IsNullOrWhiteSpace(profile.CertificatePath)
                || string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
        );

    /// <summary>一次打开多条连接时,同时在握手的最多几条(#571)。</summary>
    /// <remarks>
    /// 恢复会话那一处是一次全发(#118),那里通常只有几条;整组打开面对的可能是几十上百台设备,
    /// 全发就是几十个密钥交换同时抢 CPU、几十个终端同时建,界面会卡上好一阵。限个并发,
    /// 标签照样按顺序一个个冒出来,慢的那台也不会拖住其余的。
    /// </remarks>
    public const int OpenManyConcurrency = 6;

    /// <summary>
    /// 一次打开多条连接(资源管理器的整组打开 / 多选打开,#571):按列表顺序发起,最多
    /// <see cref="OpenManyConcurrency" /> 条同时握手。单条失败只在它自己的标签页里提示,不影响其余。
    /// </summary>
    /// <param name="profiles">要打开的连接。</param>
    public Task OpenProfilesAsync(IReadOnlyList<SessionProfile> profiles) => OpenProfilesCoreAsync(profiles, opened: null);

    /// <summary>
    /// 打开一组连接并把开出来的终端拉进同一个同步输入频道:对一组机器批量下发命令
    /// (资源管理器分组菜单「打开全部并同步输入」、多选菜单「打开 N 个连接并同步输入」)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>只开终端。</b>同步输入是往终端里打字,SFTP / FTP / 插件的文件协议与工作台开出来的是文档,进不了频道 ——
    /// 这些连接跳过不开(打开它们是「打开全部连接」的事),结束时的提示里报一个数。插件协议是不是终端要问插件
    /// (可能触发惰性激活,与真去连它时一样)。
    /// </para>
    /// <para>
    /// <b>总是新开标签,已经开着的同配置标签不动。</b>与「打开全部连接」(§180)同一口径;而且把一个开着的标签
    /// 拉进频道,它可能正停在 vim 里、或者本来就在别的频道 —— 广播打进去的后果用户看不见也撤不回。
    /// 同一台机器因此可能开着两个标签,但只有这一批新开的在频道里,命令不会打两遍。
    /// </para>
    /// <para>
    /// <b>频道挑第一个空着的</b>(A → D,没有任何标签在里面,也没被另一批正在开的预订);四个都在用就一个都不开,
    /// 浮层说明原因 —— 混进一个已有成员的频道,这批命令就会打到不相干的机器上。
    /// 每个标签连完(连上或失败)就入频道:失败的那个之后重连上来,照样收得到;握手期间敲的字它收不到
    /// (频道只往连着的标签转发),所以全部连完时报一句「几个进了频道、几个没连上」。
    /// 超过 10 条先确认、最多 6 条同时握手,与 §180 相同(确认在视图层,<c>SessionTreeActions</c>)。
    /// </para>
    /// </remarks>
    /// <param name="profiles">要打开的连接(按树上的顺序)。</param>
    /// <returns>挑中的频道;没开(没有终端类连接、四个频道都在用)时为 null。</returns>
    public async Task<SyncInputChannel?> OpenProfilesInSyncChannelAsync(IReadOnlyList<SessionProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        List<SessionProfile> terminals = [];
        foreach (SessionProfile profile in profiles)
        {
            if (await OpensTerminalAsync(profile).ConfigureAwait(true))
            {
                terminals.Add(profile);
            }
        }
        int skipped = profiles.Count - terminals.Count;
        if (terminals.Count == 0)
        {
            Toasts.Warning(Strings.Get("Tree_SyncedNoTerminals"));
            return null;
        }
        if (FreeSyncChannel() is not { } channel)
        {
            Toasts.Warning(Strings.Get("Tree_SyncedNoFreeChannel"));
            return null;
        }

        _reservedSyncChannels.Add(channel);
        List<TerminalTabViewModel> joined = [];
        try
        {
            await OpenProfilesCoreAsync(terminals, tab =>
            {
                // 连接途中用户把标签关了:那条就不算了。
                if (FindDocument(tab) is null)
                {
                    return;
                }
                tab.JoinSyncChannel(channel);
                joined.Add(tab);
            }).ConfigureAwait(true);
        }
        finally
        {
            _reservedSyncChannels.Remove(channel);
        }

        if (joined.Count == 0)
        {
            return null;
        }
        int connected = joined.Count(static tab => tab.IsConnected);
        List<string> parts = [Strings.Format("Tree_SyncedDone", joined.Count, channel)];
        if (connected < joined.Count)
        {
            parts.Add(Strings.Format("Tree_SyncedNotConnected", joined.Count - connected));
        }
        if (skipped > 0)
        {
            parts.Add(Strings.Format("Tree_SyncedSkipped", skipped));
        }
        string message = string.Join(Strings.Get("BatchEdit_PreviewSeparator"), parts);
        if (connected < joined.Count || skipped > 0)
        {
            Toasts.Warning(message);
        }
        else
        {
            Toasts.Info(message);
        }
        return channel;
    }

    /// <summary>正被「打开并同步输入」预订着的频道(那一批还在开,频道里暂时一个标签都没有)。</summary>
    private readonly HashSet<SyncInputChannel> _reservedSyncChannels = [];

    /// <summary>第一个空着的同步输入频道(A → D):没有任何标签在里面,也没被正在开的另一批预订;都在用时为 null。</summary>
    private SyncInputChannel? FreeSyncChannel()
    {
        var used = TerminalTabs.Select(static tab => tab.SyncChannel).OfType<SyncInputChannel>().ToHashSet();
        foreach (SyncInputChannel candidate in Enum.GetValues<SyncInputChannel>())
        {
            if (!used.Contains(candidate) && !_reservedSyncChannels.Contains(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>这条连接打开之后是不是一个终端(能进同步输入频道)。</summary>
    private async Task<bool> OpensTerminalAsync(SessionProfile profile)
    {
        switch (profile.ConnectionType)
        {
            case ConnectionType.SSH:
                return true;
            case ConnectionType.Plugin when _protocolRegistry is { } protocols:
                if (protocols.KindOf(profile.PluginProtocolId) == PluginConnectionKind.Workspace)
                {
                    return false;
                }
                try
                {
                    PluginProtocolRegistration? registration =
                        await protocols.ResolveAsync(profile.PluginProtocolId).ConfigureAwait(true);
                    return registration is { Terminal: not null };
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // 插件激活失败:这条开不出终端,跳过;真去连它时那边会如实报错。
                    System.Diagnostics.Trace.WriteLine($"[VelaShell] Resolve {profile.PluginProtocolId} failed: {ex.Message}");
                    return false;
                }
            default:
                return false;
        }
    }

    /// <summary>
    /// <see cref="OpenProfilesAsync" /> 的本体:限并发地逐条连,每条连完(拿到终端标签时)回调一次。
    /// </summary>
    private async Task OpenProfilesCoreAsync(IReadOnlyList<SessionProfile> profiles, Action<TerminalTabViewModel>? opened)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        using var gate = new SemaphoreSlim(OpenManyConcurrency);
        await Task.WhenAll(
            profiles
                .Select(async profile =>
                {
                    await gate.WaitAsync().ConfigureAwait(true);
                    try
                    {
                        if (await TryConnectProfileAsync(profile).ConfigureAwait(true) is { } tab)
                        {
                            opened?.Invoke(tab);
                        }
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // 连接失败已在标签页内以覆盖层提示(设计 yxjmg);这里只保证一条的失败不掀掉其余。
                        System.Diagnostics.Trace.WriteLine($"[VelaShell] Open {profile.Name} failed: {ex.Message}");
                    }
                    finally
                    {
                        gate.Release();
                    }
                })
                .ToList()
        ).ConfigureAwait(true);
    }

    /// <summary>
    /// 执行连接且绝不让异常逃逸到调用方。认证失败、主机不可达等被捕获进
    /// <see cref="LastConnectionError" /> 并反映在状态栏中,而非让应用崩溃。
    /// 凭据缺失或认证失败时通过 <see cref="InteractiveAuthenticator" /> 走两步验证弹窗(最多重试 3 次)。
    /// </summary>
    public Task<TerminalTabViewModel?> TryConnectProfileAsync(
        SessionProfile profile,
        CancellationToken cancellationToken = default
    ) =>
        TryConnectProfileAsync(profile, tabTitle: null, cancellationToken);

    /// <summary>
    /// 同 <see cref="TryConnectProfileAsync(SessionProfile, CancellationToken)" />,但标签页标题用调用方给的名字。
    /// </summary>
    /// <remarks>
    /// 外部拉起(Xshell 兼容登录)的 <c>-newtab &lt;会话名&gt;</c> 从这里进来(#475):堡垒机给一排标签起的名字
    /// 是用户分辨它们的唯一线索,而配置名是现搭的 <c>user@堡垒机:端口</c>,一排都一样。
    /// 名字只上标签(含 SFTP / FTP 的占位与成品标签),不改配置本身 —— 复用已保存的配置时改了它的名称,
    /// 会随最近连接时间一起落盘;状态栏也照旧显示配置的名称,那一行说的是「真连到了哪」。
    /// </remarks>
    /// <param name="profile">连接配置。</param>
    /// <param name="tabTitle">标签页标题;null 或空白 = 按配置起名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>终端标签(终端类连接),或 null(文档类连接、取消、失败撤标签)。</returns>
    public Task<TerminalTabViewModel?> TryConnectProfileAsync(
        SessionProfile profile,
        string? tabTitle,
        CancellationToken cancellationToken = default
    ) =>
        TryConnectProfileCoreAsync(profile, tabTitle, startupDirectory: null, cancellationToken);

    /// <summary>
    /// 文件栏右键「在此处打开终端」(维护者 2026-10-25 定,只对 SSH 会话):开一个连同一配置的终端标签,
    /// 认证后 <c>cd</c> 进 <paramref name="remoteDirectory" />。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>cd</c> 走认证后命令那一条路(<see cref="SendStartupDirectory" />,单引号转义),与配置里的「初始目录」、
    /// 跟随终端目录的钩子共用同一套静默注入与防回显 —— 用户屏幕上不会多出一行没敲过的命令,
    /// 带空格、带引号的目录名也不会变成两个参数或一条注入;带控制字符的目录名不 <c>cd</c>、只提示一句(敲进 PTY 的控制字符
    /// 引号挡不住,见那边)。它压过配置里的初始目录,只挂在这个标签上,不写进配置。
    /// </para>
    /// <para>
    /// 连接本身就是普通的「打开这条配置」:凭据缺了照样弹登录框,认证失败照样原地重试,与双击资源管理器里那一条一样;
    /// 传进来的是文件栏手里那份配置(登录框交回的副本),刚输过却没让记住的密码不用再输一遍。
    /// </para>
    /// </remarks>
    /// <param name="profile">文件栏所连的那条配置(连接类型须是 SSH,否则没有 shell 可开)。</param>
    /// <param name="remoteDirectory">远端绝对路径。</param>
    internal Task<TerminalTabViewModel?> OpenTerminalHereAsync(SessionProfile profile, string remoteDirectory)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteDirectory);
        return profile.ConnectionType == ConnectionType.SSH
            ? TryConnectProfileCoreAsync(profile, tabTitle: null, remoteDirectory, CancellationToken.None)
            : Task.FromResult<TerminalTabViewModel?>(null);
    }

    /// <param name="profile">连接配置。</param>
    /// <param name="tabTitle">标签页标题;null 或空白 = 按配置起名。</param>
    /// <param name="startupDirectory">认证后先 cd 进去的目录(见 <see cref="OpenTerminalHereAsync" />);null = 按配置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<TerminalTabViewModel?> TryConnectProfileCoreAsync(
        SessionProfile profile,
        string? tabTitle,
        string? startupDirectory,
        CancellationToken cancellationToken
    )
    {
        tabTitle = string.IsNullOrWhiteSpace(tabTitle) ? null : tabTitle;
        if (profile.ConnectionType == ConnectionType.SFTP)
        {
            await OpenSftpDocumentForProfileAsync(profile, null, cancellationToken, tabTitle).ConfigureAwait(true);
            return null;
        }
        if (profile.ConnectionType == ConnectionType.FTP)
        {
            await OpenFtpDocumentForProfileAsync(profile, null, cancellationToken, tabTitle).ConfigureAwait(true);
            return null;
        }
        if (profile.ConnectionType == ConnectionType.Plugin)
        {
            // 形态由插件的**声明**决定,查它是同步的、不会装载任何程序集,所以放在最前:
            // 工作台(Redis…)→ 向插件索取一个控件挂成停靠文档。
            if (_protocolRegistry?.KindOf(profile.PluginProtocolId) == PluginConnectionKind.Workspace)
            {
                await OpenWorkspaceDocumentForProfileAsync(profile, cancellationToken).ConfigureAwait(true);
                return null;
            }
            // 其余插件协议有两种:注册了终端实现的(Telnet…)开终端标签,
            // 注册了文件系统的(S3…)开双栏文件面板。判据只看注册表里有什么,
            // 宿主依旧不认识任何一种具体协议。可能触发惰性激活。
            PluginProtocolRegistration? registration = _protocolRegistry is { } protocols
                ? await protocols.ResolveAsync(profile.PluginProtocolId).ConfigureAwait(true)
                : null;
            if (registration is { Terminal: not null })
            {
                return await OpenPluginTerminalForProfileAsync(profile, registration, cancellationToken)
                    .ConfigureAwait(true);
            }
            await OpenPluginDocumentForProfileAsync(profile, cancellationToken).ConfigureAwait(true);
            return null;
        }
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
                            await RemoveTerminalTabAsync(tab, document).ConfigureAwait(true);
                        }
                        return null;
                    }
                    current = updated;
                }
            }
            // 弹凭据框的这段时间里用户可能已经把这个「连接中」标签关掉了 ——
            // 那和在框上点取消是同一句话:不连了。取消令牌管不到这一段(此刻等的是用户
            // 而不是网络,标签上还没有绳可拉),所以在这里按"标签还在不在"判一次。
            if (tab is not null && FindDocument(tab) is null)
            {
                LastConnectionError = null;
                return null;
            }
            if (tab is null)
            {
                (tab, document) = CreateConnectingTab(current, settings, tabTitle: tabTitle);
                tab.StartupDirectoryOverride = startupDirectory;
            }
            else
            {
                // 认证重试:复用标签,回到“连接中”(隐去上次的失败覆盖层)。
                tab.Profile = current;
                tab.ConnectionStatus = SessionStatus.Connecting;
            }
            // 从这里到本次尝试结束,「关掉这个标签」与调用方的令牌并联成一根绳。
            CancellationToken connectToken = BeginTabConnect(tab, cancellationToken);
            try
            {
                await RunHandshakeAsync(tab, current, settings, connectToken);
                return tab;
            }
            catch (Exception) when (connectToken.IsCancellationRequested)
            {
                // 用户取消(关掉这个正在连的标签 / 超时):撤掉标签,不弹失败提示 ——
                // 取消在底层未必现成一个 OperationCanceledException(连接被拆掉时也可能
                // 是一句 IO 错误),按令牌判定才不会为一个已经没了的标签报"无法连接"。
                if (document is not null)
                {
                    await RemoveTerminalTabAsync(tab, document).ConfigureAwait(true);
                }
                return null;
            }
            catch (VelaSshAuthenticationCancelledException)
            {
                // 在动态码框上点了取消:与在凭据框上取消同一句话 —— 不连了,不报错,撤掉标签。
                LastConnectionError = null;
                if (document is not null)
                {
                    await RemoveTerminalTabAsync(tab, document).ConfigureAwait(true);
                }
                return null;
            }
            catch (VelaSshTailscaleAuthenticationException ex)
            {
                LastConnectionError = DescribeConnectionError(ex, current);
                Toasts.Error(LastConnectionError);
                if (ex.RequiresUserAction)
                {
                    tab.MarkAuthenticationRequiresUserAction(LastConnectionError);
                }
                else
                {
                    // 认证途中断线、没等到验证链接就超时:与网络失败一样留在失败覆盖层,不弹凭据框
                    // (Tailscale 没有凭据可问),也不暂停之后的自动重试。
                    tab.MarkConnectionFailed(LastConnectionError);
                }
                return tab;
            }
            catch (Exception ex)
            {
                LastConnectionError = DescribeConnectionError(ex, current);
                Toasts.Error(LastConnectionError);
                // 共享凭据取不到(被删了、在本机没有密码)与认证被拒同等对待:退回登录框让用户手输(#550)。
                bool isAuth = ex is VelaSshAuthenticationException or CredentialProviderException;

                // 认证失败但无法交互重试(headless):保持既有契约,撤标签、返回 null。
                if (isAuth && InteractiveAuthenticator is null)
                {
                    if (document is not null)
                    {
                        await RemoveTerminalTabAsync(tab, document).ConfigureAwait(true);
                    }
                    return null;
                }

                // 认证失败且可交互:标记失败态并循环回去重新弹凭据重试。
                // 败在算法协商上的同时带上一键补救(见 SshAlgorithmRemedy)。
                tab.MarkConnectionFailed(LastConnectionError, SshAlgorithmRemedy.Find(ex));
                if (isAuth && InteractiveAuthenticator is not null)
                {
                    continue;
                }

                // 网络/超时等失败:保留标签,标签页内显示失败覆盖层(设计 yxjmg),不弹全局框。
                return tab;
            }
            finally
            {
                // 这一轮的绳子用完了(连上、失败、或已被拉断):注销并释放。
                EndTabConnect(tab);
            }
        }

        // 认证重试用尽:保留标签显示“认证失败”覆盖层,交给用户手动重连/关闭。
        return tab;
    }

    /// <summary>
    /// 一次文档型连接在界面上的「进行中」表示:工作区里的占位标签、右下角圆环里的一条活动,
    /// 以及用户点「取消」时要拉的那根绳。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 四条文档型连接路径(独立 SFTP、FTP、插件协议、插件工作台)共用这一个 ——
    /// 同一套反馈在四处各写一遍,漏一处就是一条"点了没反应"的 bug,而它们的连接流程
    /// (缺凭据先弹框、认证失败原地重试三次、证书未信任提示后重连)本来就是一模一样的。
    /// </para>
    /// <para>
    /// 与终端标签的分工一致:标签在握手**开始前**就在,连上之后由
    /// <see cref="DockWorkspace.ReplaceDocument" /> 原位换成真文档。
    /// </para>
    /// </remarks>
    private sealed class DocumentConnectUi : IDisposable
    {
        private readonly MainWindowViewModel _owner;
        private readonly CancellationTokenSource _cancellation;
        private IBackgroundActivityScope? _activity;
        private bool _disposed;

        /// <summary>建立(或接手)一次文档型连接的界面表示。</summary>
        /// <param name="owner">宿主视图模型。</param>
        /// <param name="profile">正在连接的配置。</param>
        /// <param name="typeLabel">连接类型展示名(SFTP / FTP / S3 / Redis…)。</param>
        /// <param name="outer">调用方的取消令牌,与占位标签上的「取消」并联。</param>
        /// <param name="reuse">重试时接手的既有占位标签;首次连接传 <see langword="null" />。</param>
        /// <param name="retry">失败卡片上「重新连接」要跑的流程。</param>
        /// <param name="displayName">占位标签上的名称;不给则取配置的显示名(见 <see cref="ConnectingDocument" />)。</param>
        /// <param name="secondary">双栏远程文档的右栏配置(见 <see cref="ConnectingDocument.SecondaryProfile" />)。</param>
        public DocumentConnectUi(
            MainWindowViewModel owner,
            SessionProfile profile,
            string typeLabel,
            ConnectingDocument? reuse,
            Func<ConnectingDocument, Task> retry,
            CancellationToken outer,
            string? displayName = null,
            SessionProfile? secondary = null)
        {
            _owner = owner;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(outer);
            if (reuse is null)
            {
                Document = new(profile, typeLabel, displayName) { SecondaryProfile = secondary };
                _owner.Layout.AddDocument(Document);
            }
            else
            {
                Document = reuse;
            }
            // 每次重试都重挂:上一轮的委托指向的是那一轮已经释放掉的取消源。
            Document.CancelRequested = Cancel;
            Document.RetryRequested = () => _ = retry(Document);
        }

        /// <summary>工作区里的占位标签。</summary>
        public ConnectingDocument Document { get; }

        /// <summary>连接流程要用的取消令牌(调用方的令牌 ∪ 用户点的「取消」)。</summary>
        public CancellationToken Token => _cancellation.Token;

        /// <summary>撤销这次连接。占位标签被关掉时(标签 ×、Ctrl+W、覆盖层的「取消」)由宿主调。</summary>
        public void Cancel()
        {
            if (!_disposed)
            {
                _cancellation.Cancel();
            }
        }

        /// <summary>一次尝试开始:占位回到「连接中」,右下角圆环点亮。</summary>
        public void BeginAttempt()
        {
            Document.MarkConnecting();
            _activity ??= _owner._backgroundActivity?.Begin(Strings.Connecting, Document.DisplayName);
        }

        /// <summary>
        /// 一次尝试结束。要弹凭据框/证书框之前必须调:此刻等的是用户,不是网络,
        /// 圆环继续转就是在撒谎。
        /// </summary>
        public void EndAttempt()
        {
            _activity?.Dispose();
            _activity = null;
        }

        /// <summary>连上了:占位原位换成真文档(位置与激活状态一并交接)。</summary>
        /// <param name="real">连接成功后建好的真文档。</param>
        public void HandOver(DockDocument real) => _owner.Layout.ReplaceDocument(Document, real);

        /// <summary>连不上了:占位留在原地换成失败卡片(重新连接 / 关闭标签页)。</summary>
        /// <param name="message">面向用户的失败原因。</param>
        /// <param name="remedy">算法协商失败、能一键放开时给(见 <see cref="ConnectingDocument.AlgorithmRemedy" />)。</param>
        public void Fail(string message, SshAlgorithmRemedy? remedy = null) => Document.MarkFailed(message, remedy);

        /// <summary>用户中途取消:撤掉占位标签(已被用户关掉时是空操作)。</summary>
        public void Abandon() => _owner.Layout.RemoveDocument(Document);

        /// <summary>结束这次连接的界面表示:熄掉圆环并释放取消源。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            EndAttempt();
            _cancellation.Dispose();
        }
    }

    /// <summary>
    /// 为 SSH 或 SFTP 配置打开一个独立的 SFTP 文档。此路径绝不创建终端标签或 shell 流。
    /// </summary>
    public async Task<TerminalTabViewModel?> OpenSftpForProfileAsync(
        SessionProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await OpenSftpDocumentForProfileAsync(profile, cancellationToken).ConfigureAwait(true);
        return null;
    }

    /// <summary>
    /// 通过常规工作流连接,并在认证成功后才创建一个文档范围的串行化 SFTP 通道。
    /// </summary>
    public Task<SftpDocument?> OpenSftpDocumentForProfileAsync(
        SessionProfile profile,
        CancellationToken cancellationToken = default) =>
        OpenSftpDocumentForProfileAsync(profile, null, cancellationToken);

    /// <param name="profile">会话配置。</param>
    /// <param name="reuse">失败卡片上点「重新连接」时接手的既有占位标签。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="tabTitle">标签页标题;null = 配置的显示名(外部拉起的 <c>-newtab</c> 名字从这里进来)。</param>
    private async Task<SftpDocument?> OpenSftpDocumentForProfileAsync(
        SessionProfile profile,
        ConnectingDocument? reuse,
        CancellationToken cancellationToken,
        string? tabTitle = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (_connectionWorkflowService is null)
        {
            return null;
        }

        // 失败卡片上的「重新连接」用最近一次尝试的配置:登录框交回的是副本、不改调用方那一份,
        // 用户刚输过却没让记住的密码只在副本里 —— 拿最初那份重试,就得把密码再输一遍。
        SessionProfile latest = profile;
        using var ui = new DocumentConnectUi(
            this, profile, "SFTP", reuse,
            document => OpenSftpDocumentForProfileAsync(latest, document, CancellationToken.None, tabTitle),
            cancellationToken,
            tabTitle);
        (DocumentSessionConnection? connection, DocumentConnectFailure failure) =
            await ConnectSshDocumentSessionAsync(profile, ui).ConfigureAwait(true);
        if (connection is null)
        {
            latest = failure.Profile;
            FinishFailedDocumentConnect(ui, failure);
            return null;
        }
        if (_sftpService is null)
        {
            await DisconnectQuietlyAsync(connection).ConfigureAwait(true);
            ui.Abandon();
            return null;
        }
        AppSettings settings = _latestSettings ?? await LoadSettingsSnapshotAsync().ConfigureAwait(true);
        var sshViewModel = new SftpDocumentViewModel(
            connection.Profile,
            connection.Session!,
            _connectionWorkflowService,
            _sftpService,
            settings.Transfer,
            FileTransfer,
            QueryDefaultEditorPathAsync,
            UpdateProfileBookmarksAsync);
        WireDocumentPanesToSettings(sshViewModel);
        // 远端搜索走这条 SSH 连接的 exec 通道(FTP 与插件协议的文档没有,不挂)。
        sshViewModel.RemoteFiles.RemoteSearch = _remoteSearch;
        SessionProfile terminalProfile = connection.Profile;
        WireOpenTerminalHere(sshViewModel.RemoteFiles, () => terminalProfile);
        var document = new SftpDocument(sshViewModel);
        if (tabTitle is not null)
        {
            document.Title = tabTitle;
        }
        ui.HandOver(document);
        // 与 FTP 同理:连接续体可能落在后台线程上,树节点是绑定属性,必须回主线程再改。
        TrackDocumentSession(connection.SessionId, connection.Profile.Id, SessionStatus.Connected);
        return document;
    }

    /// <summary>
    /// 一次文档型连接连上的那条会话:连它所用的配置(登录弹窗可能改过字段)、会话标识、
    /// SSH 会话对象(FTP 与插件协议没有,为 null),以及断开它的回调。
    /// </summary>
    /// <param name="Profile">连它所用的配置。</param>
    /// <param name="SessionId">会话标识。</param>
    /// <param name="Session">SSH 会话对象;FTP 与插件协议为 null。</param>
    /// <param name="DisconnectAsync">断开它的回调。</param>
    /// <param name="Descriptor">插件协议的描述(右键动作、标签图标);内建协议为 null。</param>
    /// <param name="AcceptsStreamedUploads">
    /// 能否接收流式上传(双栏远程之间的中转要靠它):内建协议恒能;插件协议看它有没有兼实现
    /// <see cref="IProtocolStreamUpload" />。
    /// </param>
    private sealed record DocumentSessionConnection(
        SessionProfile Profile,
        Guid SessionId,
        SshSession? Session,
        Func<Guid, CancellationToken, Task> DisconnectAsync,
        ProtocolDescriptor? Descriptor = null,
        bool AcceptsStreamedUploads = true);

    /// <summary>
    /// 文档型连接没连上的结局。<see cref="Error" /> 为 null = 用户取消了(撤占位,不报错);
    /// <see cref="Message" /> 非 null 时原样作为失败文案(用户自己拒绝了证书,原因他清楚,不再套话)。
    /// </summary>
    private readonly record struct DocumentConnectFailure(SessionProfile Profile, Exception? Error, string? Message = null);

    /// <summary>把一次没连上的结局落到界面上:取消 → 撤占位;失败 → 浮层 + 失败卡片。</summary>
    private void FinishFailedDocumentConnect(DocumentConnectUi ui, DocumentConnectFailure failure)
    {
        if (failure.Error is null)
        {
            ui.Abandon();
            return;
        }
        if (failure.Message is { } message)
        {
            LastConnectionError = message;
            Toasts.Error(message);
            ui.Fail(message);
            return;
        }
        ReportDocumentConnectFailure(ui, failure.Profile, failure.Error);
    }

    /// <summary>
    /// 为文件标签连上一条 SSH 会话(SSH 与独立 SFTP 配置都走这条):缺凭据先弹框、认证失败原地重试三次。
    /// 只负责连上,不建文档 —— 单栏与双栏文档共用它。
    /// </summary>
    private async Task<(DocumentSessionConnection? Connection, DocumentConnectFailure Failure)> ConnectSshDocumentSessionAsync(
        SessionProfile profile,
        DocumentConnectUi ui)
    {
        if (_connectionWorkflowService is not { } workflow)
        {
            return (null, new(profile, null));
        }
        SessionProfile current = profile;
        // 三次认证都没过时的最后一条原因:循环走完就没人再报了,要写进占位标签的失败卡片。
        Exception? lastAuthFailure = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0 || RequiresCredentials(current))
            {
                if (InteractiveAuthenticator is not { } prompt)
                {
                    return (null, new(current, null));
                }
                ui.EndAttempt();
                SessionProfile? prompted = await prompt(current).ConfigureAwait(true);
                if (prompted is null)
                {
                    // 用户取消:这是"不连了",不是失败,不弹提示,占位标签一并撤走。
                    LastConnectionError = null;
                    return (null, new(current, null));
                }
                current = prompted;
            }

            SshSession? session = null;
            try
            {
                ui.BeginAttempt();
                session = await workflow.ConnectProfileAsync(current, ui.Token).ConfigureAwait(true);
                return session is null
                    ? (null, new(current, null))
                    : (new(current, session.SessionId, session, workflow.DisconnectAsync), default);
            }
            catch (Exception ex) when (ex is OperationCanceledException or VelaSshAuthenticationCancelledException)
            {
                // 后者是在动态码框上点了取消 —— 同样是「不连了」,撤占位、不报错。
                await DisconnectQuietlyAsync(session?.SessionId).ConfigureAwait(true);
                return (null, new(current, null));
            }
            catch (VelaSshTailscaleAuthenticationException ex)
            {
                await DisconnectQuietlyAsync(session?.SessionId).ConfigureAwait(true);
                return (null, new(current, ex));
            }
            catch (Exception auth) when (auth is VelaSshAuthenticationException or CredentialProviderException)
            {
                // 后者是共享凭据取不到:同样退回登录框(#550)。
                await DisconnectQuietlyAsync(session?.SessionId).ConfigureAwait(true);
                lastAuthFailure = auth;
            }
            catch (Exception ex)
            {
                await DisconnectQuietlyAsync(session?.SessionId).ConfigureAwait(true);
                return (null, new(current, ex));
            }
        }
        return (null, new(current, lastAuthFailure));
    }

    /// <summary>断开一条已经连上的文档会话(收尾用,不带调用方的取消令牌,失败不抛)。</summary>
    private static async Task DisconnectQuietlyAsync(DocumentSessionConnection connection)
    {
        try
        {
            await connection.DisconnectAsync(connection.SessionId, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // 收尾失败没有下一步可走(理由同另一个重载)。
        }
    }

    /// <summary>
    /// 收尾用的断开:不带调用方的取消令牌。
    /// </summary>
    /// <remarks>
    /// 这里的调用点全在"连接已经失败/被取消"之后,而那正是原令牌已经取消的时刻 ——
    /// 把它传给断开,等于让清理动作自己取消掉,留下一条没人关的 SSH 连接。
    /// </remarks>
    /// <param name="sessionId">要断开的会话;<see langword="null" /> 表示还没连上,无事可做。</param>
    private async Task DisconnectQuietlyAsync(Guid? sessionId)
    {
        if (sessionId is not { } id || _connectionWorkflowService is null)
        {
            return;
        }
        try
        {
            await _connectionWorkflowService.DisconnectAsync(id, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // 收尾失败没有下一步可走,更不该盖掉用户真正要看的那条失败原因。
        }
    }

    /// <summary>
    /// 文档型连接失败的统一上报:浮层一条 + 把原因写进占位标签的失败卡片。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 标签留在原地而不是撤掉:浮层几秒就没了,而"哪条连接失败了、为什么"是用户接下来
    /// 要处理的事;卡片上的「重新连接」也才有地方放。
    /// </para>
    /// <para>
    /// 终端标签早就把连接失败从全局对话框改成了标签页内的覆盖层(设计 yxjmg),
    /// 文档型标签有了自己的失败卡片之后同理:失败留在它所属的那个标签里,
    /// 而不是拿一扇模态框挡住用户手上正在做的别的事。
    /// </para>
    /// </remarks>
    /// <param name="ui">这次连接的界面表示。</param>
    /// <param name="profile">连接配置。</param>
    /// <param name="error">失败原因。</param>
    private void ReportDocumentConnectFailure(
        DocumentConnectUi ui,
        SessionProfile profile,
        Exception error)
    {
        string message = DescribeConnectionError(error, profile);
        LastConnectionError = message;
        Toasts.Error(message);
        // 败在算法协商上的同时带上一键补救(与终端标签同一份,见 SshAlgorithmRemedy)。只认 SSH / SFTP 配置:
        // 那一跳的 SSH 连接就是按这份配置连的;插件工作台经 SSH 隧道连出去的那条不归它管,改错了配置更糟。
        SshAlgorithmRemedy? remedy = profile.ConnectionType is ConnectionType.SSH or ConnectionType.SFTP
            ? SshAlgorithmRemedy.Find(error)
            : null;
        ui.Fail(message, remedy);
        if (remedy is not null)
        {
            ConnectingDocument document = ui.Document;
            document.AllowAlgorithmsRequested = () => _ = AllowAlgorithmsAndRetryDocumentAsync(document, profile, remedy);
        }
    }

    /// <summary>
    /// 打开一个 FTP / FTPS 文档标签:建立 FTP 会话并复用与 SFTP 完全相同的双栏文件面板。
    /// <para>
    /// 与 SFTP 的两点不同:一是不走 <see cref="IConnectionWorkflowService" />(那是 SSH 握手),
    /// 二是 FTPS 证书没过校验时会抛 <see cref="VelaFtpCertificateException" /> ——
    /// 此时弹一次信任提示,用户同意就把指纹记进配置再重连(刻意不在证书回调里同步等 UI,那样极易死锁)。
    /// </para>
    /// </summary>
    public Task<SftpDocument?> OpenFtpDocumentForProfileAsync(
        SessionProfile profile,
        CancellationToken cancellationToken = default) =>
        OpenFtpDocumentForProfileAsync(profile, null, cancellationToken);

    /// <param name="profile">会话配置。</param>
    /// <param name="reuse">失败卡片上点「重新连接」时接手的既有占位标签。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="tabTitle">标签页标题;null = 配置的显示名(外部拉起的 <c>-newtab</c> 名字从这里进来)。</param>
    private async Task<SftpDocument?> OpenFtpDocumentForProfileAsync(
        SessionProfile profile,
        ConnectingDocument? reuse,
        CancellationToken cancellationToken,
        string? tabTitle = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (_ftpSessionService is null || _sftpService is null)
        {
            return null;
        }

        // 失败卡片上的「重新连接」用最近一次尝试的配置(理由同 SFTP 那条路径)。
        SessionProfile latest = profile;
        using var ui = new DocumentConnectUi(
            this, profile, FtpTypeLabel(profile), reuse,
            document => OpenFtpDocumentForProfileAsync(latest, document, CancellationToken.None, tabTitle),
            cancellationToken,
            tabTitle);
        (DocumentSessionConnection? connection, DocumentConnectFailure failure) =
            await ConnectFtpDocumentSessionAsync(profile, ui).ConfigureAwait(true);
        if (connection is null)
        {
            latest = failure.Profile;
            FinishFailedDocumentConnect(ui, failure);
            return null;
        }
        AppSettings settings = _latestSettings ?? await LoadSettingsSnapshotAsync().ConfigureAwait(true);
        var ftpViewModel = new SftpDocumentViewModel(
            connection.Profile,
            connection.SessionId,
            connection.DisconnectAsync,
            _sftpService,
            settings.Transfer,
            FileTransfer,
            QueryDefaultEditorPathAsync,
            UpdateProfileBookmarksAsync);
        WireDocumentPanesToSettings(ftpViewModel);
        var document = new SftpDocument(ftpViewModel);
        if (tabTitle is not null)
        {
            document.Title = tabTitle;
        }
        ui.HandOver(document);
        TrackDocumentSession(connection.SessionId, profile.Id, SessionStatus.Connected);
        return document;
    }

    /// <summary>
    /// 为文件标签连上一条 FTP / FTPS 会话:缺凭据先弹框、认证失败原地重试三次、证书未信任时提示后重连。
    /// 只负责连上,不建文档 —— 单栏与双栏文档共用它。
    /// </summary>
    private async Task<(DocumentSessionConnection? Connection, DocumentConnectFailure Failure)> ConnectFtpDocumentSessionAsync(
        SessionProfile profile,
        DocumentConnectUi ui)
    {
        if (_ftpSessionService is not { } ftp)
        {
            return (null, new(profile, null));
        }
        SessionProfile current = profile;
        // 三次认证都没过时的最后一条原因:循环走完就没人再报了,要写进占位标签的失败卡片。
        Exception? lastAuthFailure = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0 || RequiresFtpCredentials(current))
            {
                if (InteractiveAuthenticator is not { } prompt)
                {
                    return (null, new(current, null));
                }
                ui.EndAttempt();
                SessionProfile? prompted = await prompt(current).ConfigureAwait(true);
                if (prompted is null)
                {
                    // 用户取消:这是"不连了",不是失败,不弹提示,占位标签一并撤走。
                    LastConnectionError = null;
                    return (null, new(current, null));
                }
                current = prompted;
            }

            try
            {
                ui.BeginAttempt();
                // 共享凭据在这一刻才取,只进 FtpConnectionInfo;文档拿的仍是不带明文的 current(#550)。
                SessionProfile resolved = await ResolveCredentialsAsync(current, ui.Token).ConfigureAwait(true);
                Guid sessionId = await ftp
                    .OpenSessionAsync(FtpConnectionInfo.FromProfile(resolved), ui.Token)
                    .ConfigureAwait(true);
                return (new(current, sessionId, null, ftp.CloseSessionAsync), default);
            }
            catch (OperationCanceledException)
            {
                return (null, new(current, null));
            }
            catch (VelaFtpCertificateException certificate)
            {
                // 用户同意信任 → 记下指纹后重来一次;拒绝(或没有提示钩子)→ 按普通连接失败上报。
                ui.EndAttempt();
                if (FtpCertificateTrustPrompt is { } trustPrompt &&
                    await trustPrompt(current, certificate).ConfigureAwait(true))
                {
                    current = WithTrustedCertificate(current, certificate.Thumbprint);
                    await PersistProfileIfSavedAsync(current).ConfigureAwait(true);
                    attempt--; // 信任后的这次重连不算认证重试
                    continue;
                }
                // 用户自己点了"不信任",原因他清楚 —— 再弹一扇框只是复述他刚做的决定。
                return (null, new(current, certificate, certificate.Message));
            }
            catch (Exception auth) when (auth is VelaFtpAuthenticationException or CredentialProviderException)
            {
                lastAuthFailure = auth;
            }
            catch (Exception ex)
            {
                return (null, new(current, ex));
            }
        }
        return (null, new(current, lastAuthFailure));
    }

    /// <summary>
    /// 在一个「远程 + 远程」双栏文件标签里打开两条连接(资源管理器 Ctrl 双选后的右键菜单)。
    /// 两条依次连接,<b>任一条没连上(失败或被取消)就整体回滚</b>:已经连上的那条随即断开,
    /// 不会留下半个文档或一条没人管的连接。
    /// </summary>
    /// <param name="left">左栏的配置(先选中的那条)。</param>
    /// <param name="right">右栏的配置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已打开的文档;失败或取消时为 null。</returns>
    /// <exception cref="ArgumentException">两栏是同一条配置(同机内复制请用单栏文件浏览器)。</exception>
    public Task<DualSftpDocument?> OpenDualSftpDocumentAsync(
        SessionProfile left,
        SessionProfile right,
        CancellationToken cancellationToken = default) =>
        OpenDualSftpDocumentAsync(left, right, null, cancellationToken);

    /// <param name="left">左栏的配置。</param>
    /// <param name="right">右栏的配置。</param>
    /// <param name="reuse">失败卡片上点「重新连接」时接手的既有占位标签。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<DualSftpDocument?> OpenDualSftpDocumentAsync(
        SessionProfile left,
        SessionProfile right,
        ConnectingDocument? reuse,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.Id == right.Id)
        {
            throw new ArgumentException("The two panes must use two different connection profiles.", nameof(right));
        }
        if (_sftpService is null)
        {
            return null;
        }

        // 失败卡片上的「重新连接」用两栏各自最近一次尝试的配置(理由同 SFTP 那条路径)。
        SessionProfile latestLeft = left;
        SessionProfile latestRight = right;
        using var ui = new DocumentConnectUi(
            this, left, "SFTP", reuse,
            document => OpenDualSftpDocumentAsync(latestLeft, latestRight, document, CancellationToken.None),
            cancellationToken,
            $"{ProfileDisplayName(left)} ⇄ {ProfileDisplayName(right)}",
            right);
        var connections = new List<DocumentSessionConnection>(2);
        try
        {
            foreach (SessionProfile profile in (SessionProfile[])[left, right])
            {
                (DocumentSessionConnection? connection, DocumentConnectFailure failure) = profile.ConnectionType switch
                {
                    ConnectionType.SSH or ConnectionType.SFTP => await ConnectSshDocumentSessionAsync(profile, ui).ConfigureAwait(true),
                    ConnectionType.FTP => await ConnectFtpDocumentSessionAsync(profile, ui).ConfigureAwait(true),
                    ConnectionType.Plugin when CanOpenInDualSftp(profile) => await ConnectPluginForDualAsync(profile, ui).ConfigureAwait(true),
                    _ => (null, new(profile, new NotSupportedException(Strings.Get("Tree_OpenDualSftpUnsupported")))),
                };
                SessionProfile attempted = connection?.Profile ?? failure.Profile;
                if (ReferenceEquals(profile, left))
                {
                    latestLeft = attempted;
                }
                else
                {
                    latestRight = attempted;
                }
                if (connection is null)
                {
                    // 整体回滚:先把已经连上的那条断掉,再按这一条的结局收尾(取消 → 撤占位,失败 → 失败卡片)。
                    await DisconnectAllQuietlyAsync(connections).ConfigureAwait(true);
                    FinishFailedDocumentConnect(ui, failure);
                    return null;
                }
                connections.Add(connection);
            }

            AppSettings settings = _latestSettings ?? await LoadSettingsSnapshotAsync().ConfigureAwait(true);
            var dualViewModel = new DualSftpDocumentViewModel(
                ToEndpoint(connections[0]),
                ToEndpoint(connections[1]),
                _sftpService,
                settings.Transfer,
                FileTransfer,
                QueryDefaultEditorPathAsync);
            WireDocumentPanesToSettings(dualViewModel);
            // 两栏各自判断:是 SSH 会话的那一栏才出现「搜索」(见 FileBrowserViewModel.CanSearchRemote)。
            dualViewModel.LeftFiles.RemoteSearch = _remoteSearch;
            dualViewModel.RightFiles.RemoteSearch = _remoteSearch;
            WireOpenTerminalHere(dualViewModel.LeftFiles, () => dualViewModel.LeftProfile);
            WireOpenTerminalHere(dualViewModel.RightFiles, () => dualViewModel.RightProfile);
            var document = new DualSftpDocument(dualViewModel);
            ui.HandOver(document);
            // 树上两条配置各自点亮(登记用原始配置的 Id,理由见 TrackDocumentSession)。
            TrackDocumentSession(connections[0].SessionId, left.Id, SessionStatus.Connected);
            TrackDocumentSession(connections[1].SessionId, right.Id, SessionStatus.Connected);
            return document;
        }
        catch (Exception ex)
        {
            await DisconnectAllQuietlyAsync(connections).ConfigureAwait(true);
            ReportDocumentConnectFailure(ui, left, ex);
            return null;
        }

        DualSftpEndpoint ToEndpoint(DocumentSessionConnection connection) =>
            new(connection.Profile, connection.SessionId, connection.Session, connection.DisconnectAsync)
            {
                Protocol = connection.Descriptor,
                AcceptsStreamedUploads = connection.AcceptsStreamedUploads,
                InvokeProtocolAction = connection.Descriptor is { Actions.Count: > 0 }
                    ? ProtocolActionInvoker(connection.SessionId)
                    : null,
            };
    }

    /// <summary>回滚用:把已经连上的几条会话全部断开。</summary>
    private static async Task DisconnectAllQuietlyAsync(IEnumerable<DocumentSessionConnection> connections)
    {
        foreach (DocumentSessionConnection connection in connections)
        {
            await DisconnectQuietlyAsync(connection).ConfigureAwait(true);
        }
    }

    /// <summary>占位标签上写的连接类型:FTPS 与明文 FTP 是两件事,别都写成 "FTP"。</summary>
    /// <param name="profile">会话配置。</param>
    /// <returns>FTP 或 FTPS。</returns>
    private static string FtpTypeLabel(SessionProfile profile) =>
        profile.Ftp?.EncryptionMode is null or FtpEncryptionMode.None ? "FTP" : "FTPS";

    /// <summary>
    /// 打开一个插件协议文档标签(S3、WebDAV…):建立会话并复用与 SFTP 完全相同的双栏文件面板。
    /// <para>
    /// 结构与 <see cref="OpenFtpDocumentForProfileAsync(SessionProfile, CancellationToken)" /> 一一对应(同样不走
    /// <see cref="IConnectionWorkflowService" /> —— 那是 SSH 握手,同样在证书不可信时
    /// 弹一次信任提示后重连)。差别只在缺少凭据的判定:声明了 AnonymousAccess 的协议
    /// (如 S3 的公开只读桶)不填凭据也是一条正当路径,弹框会把它堵死。
    /// </para>
    /// <para>
    /// 这个方法**对具体协议一无所知**:能力位、右键动作、证书字段全部从协议描述里读。
    /// 再接入一种协议时它一行都不用改。
    /// </para>
    /// </summary>
    /// <param name="profile">会话配置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已打开的文档;失败或取消时为 null。</returns>
    public Task<SftpDocument?> OpenPluginDocumentForProfileAsync(
        SessionProfile profile,
        CancellationToken cancellationToken = default) =>
        OpenPluginDocumentForProfileAsync(profile, null, cancellationToken);

    /// <param name="profile">会话配置。</param>
    /// <param name="reuse">失败卡片上点「重新连接」时接手的既有占位标签。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<SftpDocument?> OpenPluginDocumentForProfileAsync(
        SessionProfile profile,
        ConnectingDocument? reuse,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (_pluginProtocols is null || _sftpService is null)
        {
            return null;
        }

        // 占位标签建在解析协议之前:解析可能触发插件的惰性激活(装配、启动进程),
        // 那往往就是这条路上最慢的一步 —— 等它完再建标签,慢的那段照样没有任何回执。
        // 类型名此刻还问不到,先挂协议 id,解析出来再换成展示名。
        // 失败卡片上的「重新连接」用最近一次尝试的配置(理由同 SFTP 那条路径)。
        SessionProfile latest = profile;
        using var ui = new DocumentConnectUi(
            this, profile, profile.PluginProtocolId ?? string.Empty, reuse,
            document => OpenPluginDocumentForProfileAsync(latest, document, CancellationToken.None),
            cancellationToken);
        ui.BeginAttempt();

        ProtocolDescriptor? descriptor = null;
        if (profile.PluginProtocolId is { Length: > 0 } protocolId && _protocolRegistry is { } registry)
        {
            // 可能触发插件的惰性激活(用户刚从「最近连接」点开一条 S3 会话)。
            descriptor = (await registry.ResolveAsync(protocolId).ConfigureAwait(true))?.Descriptor;
        }
        if (descriptor is { DisplayName.Length: > 0 })
        {
            ui.Document.TypeLabel = descriptor.DisplayName;
        }

        (DocumentSessionConnection? connection, DocumentConnectFailure failure) =
            await ConnectPluginDocumentSessionAsync(profile, ui, descriptor).ConfigureAwait(true);
        if (connection is null)
        {
            latest = failure.Profile;
            FinishFailedDocumentConnect(ui, failure);
            return null;
        }
        AppSettings settings = _latestSettings ?? await LoadSettingsSnapshotAsync().ConfigureAwait(true);
        var viewModel = new SftpDocumentViewModel(
            connection.Profile,
            connection.SessionId,
            connection.DisconnectAsync,
            _sftpService,
            settings.Transfer,
            FileTransfer,
            QueryDefaultEditorPathAsync,
            UpdateProfileBookmarksAsync);
        WireDocumentPanesToSettings(viewModel);
        // 协议专属的右键菜单项:声明式,按下右键那一帧就能画出来。
        if (descriptor is { Actions.Count: > 0 })
        {
            // 传协议声明本身:菜单在每次右键时按命中行重建(见 FileBrowserViewModel.ContextTarget)。
            viewModel.RemoteFiles.SetProtocolActions(descriptor.DisplayName, descriptor.Actions);
            viewModel.RemoteFiles.InvokeProtocolAction = ProtocolActionInvoker(connection.SessionId);
        }
        // 插件文件协议(S3 …)的标签图标由插件自报;没给就退回通用插头。
        var document = new SftpDocument(viewModel) { PluginTabIcon = descriptor?.Icon };
        ui.HandOver(document);
        TrackDocumentSession(connection.SessionId, profile.Id, SessionStatus.Connected);
        return document;
    }

    /// <summary>插件协议右键动作的执行入口:转给插件,带上这条会话。</summary>
    private Func<string, string, Task> ProtocolActionInvoker(Guid sessionId) =>
        (actionId, path) => _pluginProtocols!.InvokeActionAsync(sessionId, actionId, path, CancellationToken.None);

    /// <summary>
    /// 为文件标签连上一条插件协议会话:缺凭据先弹框(声明了匿名访问的协议不弹)、认证失败原地重试三次、
    /// 证书未信任时提示后重连。只负责连上,不建文档 —— 单栏与双栏文档共用它。
    /// </summary>
    /// <param name="profile">会话配置。</param>
    /// <param name="ui">这次连接的界面表示。</param>
    /// <param name="descriptor">协议描述(判断是否允许匿名);插件还没解析出来时为 null。</param>
    private async Task<(DocumentSessionConnection? Connection, DocumentConnectFailure Failure)> ConnectPluginDocumentSessionAsync(
        SessionProfile profile,
        DocumentConnectUi ui,
        ProtocolDescriptor? descriptor)
    {
        if (_pluginProtocols is not { } plugins)
        {
            return (null, new(profile, null));
        }
        bool allowsAnonymous = descriptor?.Features.HasFlag(ProtocolFeatures.AnonymousAccess) == true;

        SessionProfile current = profile;
        // 证书提示单独计数:attempt-- 会与 attempt++ 相抵,把 3 次上限彻底架空。
        // 多节点各自签一张证书的端点(DNS 轮询的 MinIO/Ceph)每轮都是"新证书",
        // 不设独立上限的话用户只能靠点"不信任"才退得出来。
        int certPrompts = 0;
        // 三次认证都没过时的最后一条原因(理由同工作台那条路径)。
        Exception? lastAuthFailure = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0 || RequiresPluginCredentials(current, allowsAnonymous))
            {
                if (InteractiveAuthenticator is not { } prompt)
                {
                    return (null, new(current, null));
                }
                ui.EndAttempt();
                SessionProfile? prompted = await prompt(current).ConfigureAwait(true);
                if (prompted is null)
                {
                    // 用户取消:这是"不连了",不是失败,不弹提示,占位标签一并撤走。
                    LastConnectionError = null;
                    return (null, new(current, null));
                }
                current = prompted;
            }

            try
            {
                ui.BeginAttempt();
                // 共享凭据只进交给插件的那一份;文档拿的仍是不带明文的 current(#550)。
                SessionProfile resolved = await ResolveCredentialsAsync(current, ui.Token).ConfigureAwait(true);
                Guid sessionId = await plugins.OpenSessionAsync(resolved, ui.Token).ConfigureAwait(true);
                return (new(current, sessionId, null, plugins.CloseSessionAsync), default);
            }
            catch (OperationCanceledException)
            {
                return (null, new(current, null));
            }
            catch (PluginProtocolCertificateException certificate)
            {
                // 用户同意信任 → 记下指纹后重来一次;拒绝(或没有提示钩子)→ 按普通连接失败上报。
                ui.EndAttempt();
                if (PluginCertificateTrustPrompt is { } trustPrompt &&
                    await trustPrompt(current, certificate).ConfigureAwait(true))
                {
                    current = WithTrustedPluginCertificate(current, certificate);
                    await PersistProfileIfSavedAsync(current).ConfigureAwait(true);
                    if (certificate.SettingKey is { Length: > 0 } && ++certPrompts <= 2)
                    {
                        // 指纹确实记下了,下次重连不会再撞同一张证书 —— 这一次不算认证重试。
                        // 但最多宽容两次:协议没声明存放位置、或端点每次出示不同证书时,
                        // current 根本没变化,再减下去就是无限弹框。
                        attempt--;
                    }
                    continue;
                }
                // 用户自己点了"不信任",原因他清楚 —— 再弹一扇框只是复述他刚做的决定。
                return (null, new(current, certificate, certificate.Message));
            }
            catch (Exception auth) when (auth is PluginProtocolAuthenticationException or CredentialProviderException)
            {
                lastAuthFailure = auth;
            }
            catch (Exception ex)
            {
                return (null, new(current, ex));
            }
        }
        return (null, new(current, lastAuthFailure));
    }

    /// <summary>
    /// 双栏远程文档里的一栏连插件协议:先解析协议(可能触发惰性激活),只接文件协议,
    /// 再走共用的连接循环,并记下这一栏能不能接收流式上传与协议的右键动作。
    /// </summary>
    private async Task<(DocumentSessionConnection? Connection, DocumentConnectFailure Failure)> ConnectPluginForDualAsync(
        SessionProfile profile,
        DocumentConnectUi ui)
    {
        PluginProtocolRegistration? registration = _protocolRegistry is { } registry
            ? await registry.ResolveAsync(profile.PluginProtocolId).ConfigureAwait(true)
            : null;
        // 解析不到(插件没装 / 被禁用)不在这里拦:下面打开会话时会抛「协议不可用」,那条提示更准确。
        // 解析到了却没有文件系统的,是终端协议(Telnet…),进不了文件标签。
        if (registration is { FileSystem: null })
        {
            string name = registration.Descriptor.DisplayName is { Length: > 0 } display ? display : ProfileDisplayName(profile);
            return (null, new(profile, new NotSupportedException(Strings.Format("DualSftp_NotAFileProtocol", name))));
        }
        (DocumentSessionConnection? connection, DocumentConnectFailure failure) =
            await ConnectPluginDocumentSessionAsync(profile, ui, registration?.Descriptor).ConfigureAwait(true);
        return connection is null
            ? (null, failure)
            : (connection with
            {
                Descriptor = registration?.Descriptor,
                AcceptsStreamedUploads = registration?.FileSystem is IProtocolStreamUpload,
            }, default);
    }

    /// <summary>
    /// 资源管理器问:这条配置能不能进双栏。SSH / SFTP / FTP 可以;插件协议里排掉工作台(Redis…),
    /// 这一问是同步的、不装载插件。终端协议(Telnet…)要激活后才认得出,打开时再报。
    /// </summary>
    /// <param name="profile">连接配置。</param>
    /// <returns>能进双栏为 true。</returns>
    public bool CanOpenInDualSftp(SessionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.ConnectionType switch
        {
            ConnectionType.SSH or ConnectionType.SFTP or ConnectionType.FTP => true,
            ConnectionType.Plugin => _protocolRegistry?.KindOf(profile.PluginProtocolId) is not PluginConnectionKind.Workspace,
            _ => false,
        };
    }

    /// <summary>
    /// 打开一条**插件终端协议**会话(Telnet、串口…):走与 SSH / 本地终端完全相同的
    /// 桥 → VT 引擎 → 自绘控件 管线,只是传输层由插件提供。
    /// <para>
    /// 与 SSH 那条路径的三点差别,都是协议决定的而非偷懒:
    /// </para>
    /// <list type="bullet">
    ///   <item>不走 <see cref="IConnectionWorkflowService" /> —— 那是 SSH 握手;
    ///     因此没有 SessionId,SFTP 面板、任务管理器、资源监视器、隧道自动灰掉。</item>
    ///   <item>不发"连接后执行命令" —— Telnet 连上先看到的是对端的 <c>login:</c>,
    ///     此时注入命令等于把它打进登录提示符。</item>
    ///   <item>凭据不缺就不弹登录框:声明了 NoCredentials 的协议根本没有凭据可填。</item>
    /// </list>
    /// </summary>
    /// <param name="profile">会话配置。</param>
    /// <param name="registration">已解析的协议注册(带终端实现)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已打开的标签;取消时为 null(连接失败也返回标签,失败信息画在标签页内)。</returns>
    public async Task<TerminalTabViewModel?> OpenPluginTerminalForProfileAsync(
        SessionProfile profile,
        PluginProtocolRegistration registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(registration);
        AppSettings settings = await LoadSettingsSnapshotAsync().ConfigureAwait(true);
        (TerminalTabViewModel tab, TerminalDocument document) =
            CreateConnectingTab(profile, settings, registration.Descriptor.DisplayName);
        // 与 SSH 同一条纪律:关掉这个「连接中」标签就把连接撤掉(见 BeginTabConnect)。
        CancellationToken connectToken = BeginTabConnect(tab, cancellationToken);
        try
        {
            await AttachPluginTerminalAsync(tab, profile, registration, settings, connectToken)
                .ConfigureAwait(true);
            await Sidebar.RecentConnections.RefreshAsync().ConfigureAwait(true);
            return tab;
        }
        catch (Exception) when (connectToken.IsCancellationRequested)
        {
            await RemoveTerminalTabAsync(tab, document).ConfigureAwait(true);
            return null;
        }
        catch (Exception ex)
        {
            // 与 SSH 同口径:标签留着,失败原因画在标签页内的覆盖层上(设计 yxjmg),
            // 用户按 Enter 即重连 —— 撤掉标签会连带把错误信息一起吞掉。
            LastConnectionError = DescribeConnectionError(ex, profile);
            Toasts.Error(LastConnectionError);
            tab.MarkConnectionFailed(LastConnectionError);
            return tab;
        }
        finally
        {
            EndTabConnect(tab);
        }
    }

    /// <summary>在一个"连接中"标签上建立插件终端会话并挂上传输(打开与重连共用)。</summary>
    private async Task AttachPluginTerminalAsync(
        TerminalTabViewModel tab,
        SessionProfile profile,
        PluginProtocolRegistration registration,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        TerminalType terminalType = TerminalTypeExtensions.FromTermName(SessionTerminalSettings.TerminalType(profile, settings));
        // 标签页图标由插件自报(串口的接口字形、Telnet 的…),没给就退回通用插头。
        // 放在这里而不是建标签处:那时协议还没解析,拿不到描述符。
        tab.PluginTabIcon = registration.Descriptor.Icon;
        // 初始行列取模拟器当前值:标签刚建出来还没布局过时它是默认值,
        // 真实尺寸随后由控件的 Resize 通知补上(Telnet 会重发一次 NAWS)。
        var options = new ProtocolTerminalOptions(
            terminalType.ToTermName(),
            Math.Max(2, tab.TerminalEmulator.Columns),
            Math.Max(2, tab.TerminalEmulator.Rows));
        // 打开与重连都经过这里,右下角圆环的登记放在这一处就够(理由同 RunHandshakeAsync)。
        using IBackgroundActivityScope? activity =
            _backgroundActivity?.Begin(Strings.Connecting, ProfileDisplayName(profile));
        // 共享凭据在这一刻才取(打开与重连都经过这里,重连自然用上凭据的新值,#550)。
        SessionProfile resolved = await ResolveCredentialsAsync(profile, cancellationToken).ConfigureAwait(true);
        IShellStreamWrapper stream = await PluginProtocolTerminalConnector
            .OpenAsync(registration, resolved, options, cancellationToken)
            .ConfigureAwait(true);
        await tab.AttachTransportAsync(stream).ConfigureAwait(true);
        tab.Start();
        tab.ConnectionStatus = SessionStatus.Connected;
        tab.ResetReconnectAttempts();
        StartSessionLogging(tab, settings);
        StatusBar.ResetUptime();
        UpdateStatusBarForActiveTab();
        LastConnectionError = null;
    }

    /// <summary>重连一条插件终端会话:复用同一标签与回滚缓冲,RIS 复位后重建传输。</summary>
    private async Task ReconnectPluginTerminalAsync(
        TerminalTabViewModel tab,
        SessionProfile profile,
        CancellationToken cancellationToken)
    {
        tab.ConnectionStatus = SessionStatus.Connecting;
        await tab.DetachTransportAsync().ConfigureAwait(true);
        UpdateStatusBarForActiveTab();
        try
        {
            AppSettings settings = await LoadSettingsSnapshotAsync().ConfigureAwait(true);
            PluginProtocolRegistration? registration = _protocolRegistry is { } protocols
                ? await protocols.ResolveAsync(profile.PluginProtocolId).ConfigureAwait(true)
                : null;
            if (registration is not { Terminal: not null })
            {
                // 插件在这中间被禁用/卸载了:如实说,别停在"连接中"。
                tab.MarkConnectionFailed(Strings.Get("Plugin_ProtocolUnavailable"));
                return;
            }
            // 新会话的输出到达前完全复位(RIS),免得新标语附在旧缓冲后面。
            tab.TerminalEmulator.Feed(RisResetSequence);
            await AttachPluginTerminalAsync(tab, profile, registration, settings, cancellationToken)
                .ConfigureAwait(true);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // 取消(多半是标签被关掉了):安静收场,别为一个已经没了的标签弹失败提示。
            tab.MarkDisconnected();
        }
        catch (Exception ex)
        {
            LastConnectionError = DescribeConnectionError(ex, profile);
            Toasts.Error(LastConnectionError);
            tab.MarkConnectionFailed(LastConnectionError);
        }
    }

    /// <summary>
    /// 打开一条**工作台**会话(Redis 等由插件全权渲染界面的连接类型)。
    /// <para>
    /// 与 <see cref="OpenPluginDocumentForProfileAsync(SessionProfile, CancellationToken)" /> 共用同一套连接流程纪律:
    /// 缺凭据先弹登录框、认证失败原地重试(至多三次)、证书未信任走"提示 → 记指纹 → 重连"
    /// 且证书提示单独计数(否则 <c>attempt--</c> 会把三次上限彻底架空)。
    /// 区别只在最后一步:那边打开宿主的双栏浏览器,这边把插件的控件挂成停靠文档。
    /// </para>
    /// </summary>
    /// <param name="profile">连接配置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已打开的文档;失败或用户取消时为 null。</returns>
    public Task<PluginWorkspaceDocument?> OpenWorkspaceDocumentForProfileAsync(
        SessionProfile profile,
        CancellationToken cancellationToken = default) =>
        OpenWorkspaceDocumentForProfileAsync(profile, null, cancellationToken);

    /// <param name="profile">连接配置。</param>
    /// <param name="reuse">失败卡片上点「重新连接」时接手的既有占位标签。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<PluginWorkspaceDocument?> OpenWorkspaceDocumentForProfileAsync(
        SessionProfile profile,
        ConnectingDocument? reuse,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (_workspaceLauncher is not { } launcher)
        {
            return null;
        }

        // 同插件协议那条路径:占位标签建在解析之前 —— 惰性激活插件往往是最慢的一步。
        // 失败卡片上的「重新连接」用最近一次尝试的配置(理由同 SFTP 那条路径)。
        SessionProfile latest = profile;
        using var ui = new DocumentConnectUi(
            this, profile, profile.PluginProtocolId ?? string.Empty, reuse,
            document => OpenWorkspaceDocumentForProfileAsync(latest, document, CancellationToken.None),
            cancellationToken);
        ui.BeginAttempt();

        bool allowsAnonymous = false;
        // 这一档根本没有凭据(SQLite 是个文件):不弹登录框、认证失败也不重弹、不去取共享凭据 ——
        // 填什么插件都收不到(启动器对这一档一律交空串)。配置里若还留着一个用户名
        // (表单收起两栏之前填的、或旧版本存下的),按"有用户名没口令"弹框,用户取消就连不上了。
        bool noCredentials = false;
        // 描述符要活到建文档那一步 —— 标签页图标也从它身上取(descriptor.Icon)。
        WorkspaceDescriptor? descriptor = null;
        if (_protocolRegistry is { } registry)
        {
            // 可能触发插件的惰性激活(用户刚从「最近连接」点开一条 Redis 会话)。
            descriptor =
                (await registry.ResolveWorkspaceAsync(profile.PluginProtocolId).ConfigureAwait(true))?.Descriptor;
            // **必须按变体取能力位,不能只看描述符本身。**
            // 一个连接类型可以按某个设置字段切换形态(数据库插件的「数据库」页签下有好几种方言),
            // 而"要不要凭据"正是随之变的:SQLite 是一个本地文件,没有用户名口令这回事。
            // 只看描述符的话,SQLite 会话在打开前会被判成"缺凭据"→ 弹一个填了也没用的登录框,
            // 而连接框里那两栏早就按变体收起来了 —— 两处判据不一致。
            // NoCredentials 蕴含匿名,与连接配置页(PluginConnectionForm)同一条判定。
            if (descriptor is not null)
            {
                WorkspaceFeatures features = WorkspaceVariants.EffectiveFeatures(descriptor, profile.PluginSettings);
                allowsAnonymous = WorkspaceVariants.AllowsAnonymous(features);
                noCredentials = features.HasFlag(WorkspaceFeatures.NoCredentials);
            }
            if (descriptor is { DisplayName.Length: > 0 })
            {
                ui.Document.TypeLabel = descriptor.DisplayName;
            }
        }

        SessionProfile current = profile;
        int certPrompts = 0;
        // 三次认证都没过时的最后一条原因:循环走完就没人再报了,要写进占位标签的失败卡片 ——
        // 不记下来的话用户面对的是"密码弹了三次,然后什么都没有"。
        Exception? lastAuthFailure = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (!noCredentials && (attempt > 0 || RequiresPluginCredentials(current, allowsAnonymous)))
            {
                if (InteractiveAuthenticator is not { } prompt)
                {
                    ui.Abandon();
                    return null;
                }
                ui.EndAttempt();
                SessionProfile? prompted = await prompt(current).ConfigureAwait(true);
                if (prompted is null)
                {
                    // 用户取消:这是"不连了",不是失败,不弹提示,占位标签一并撤走。
                    LastConnectionError = null;
                    ui.Abandon();
                    return null;
                }
                current = prompted;
                latest = current;
            }

            try
            {
                ui.BeginAttempt();
                // 声明了 SshTunnel 且用户选了跳板机 → 宿主先把 SSH 会话与本地转发建好,
                // 插件只看到一个已经能连的本地端点(凭据永不出宿主)。
                // 共享凭据先取:取不到就别白建一条隧道(#550)。没有凭据的那一档不取(理由见 noCredentials)。
                SessionProfile resolved = noCredentials
                    ? current
                    : await ResolveCredentialsAsync(current, ui.Token).ConfigureAwait(true);
                (WorkspaceEndpoint? endpoint, Guid tunnelId) =
                    await EstablishWorkspaceTunnelAsync(current, ui.Token).ConfigureAwait(true);
                PluginWorkspaceSession session;
                try
                {
                    // 明文只进交给插件的那一份;文档拿的仍是不带明文的 current。
                    session = await launcher.OpenAsync(resolved, endpoint, ui.Token).ConfigureAwait(true);
                }
                catch
                {
                    // 连接失败就把刚建的隧道拆掉 —— 留着它等于占着一个本地端口和一条 SSH 通道。
                    await RemoveWorkspaceTunnelAsync(tunnelId).ConfigureAwait(true);
                    throw;
                }
                var document = new PluginWorkspaceDocument(
                    current, session.SessionId, session.TypeName, session.Document, descriptor?.Icon);
                if (tunnelId != Guid.Empty)
                {
                    _workspaceTunnels[session.SessionId] = tunnelId;
                }
                _workspaceDocuments[session.SessionId] = document;
                session.Document.StatusChanged += OnWorkspaceStatusChanged;
                ui.HandOver(document);
                TrackDocumentSession(session.SessionId, profile.Id, SessionStatus.Connected);
                return document;
            }
            catch (Exception ex) when (ex is OperationCanceledException or VelaSshAuthenticationCancelledException)
            {
                LastConnectionError = null;
                ui.Abandon();
                return null;
            }
            catch (PluginProtocolCertificateException certificate)
            {
                ui.EndAttempt();
                if (PluginCertificateTrustPrompt is { } trustPrompt &&
                    await trustPrompt(current, certificate).ConfigureAwait(true))
                {
                    current = WithTrustedPluginCertificate(current, certificate);
                    await PersistProfileIfSavedAsync(current).ConfigureAwait(true);
                    if (certificate.SettingKey is { Length: > 0 } && ++certPrompts <= 2)
                    {
                        attempt--;
                    }
                    continue;
                }
                // 用户自己点了"不信任",原因他清楚 —— 再弹一扇框只是复述他刚做的决定。
                LastConnectionError = certificate.Message;
                Toasts.Error(LastConnectionError);
                ui.Fail(certificate.Message);
                return null;
            }
            catch (Exception auth) when (auth is PluginProtocolAuthenticationException or CredentialProviderException)
            {
                lastAuthFailure = auth;
                if (noCredentials)
                {
                    // 没有凭据可重填:再弹登录框也改变不了什么,原样重试三次只是把同一个失败等三遍。
                    break;
                }
                continue;
            }
            catch (Exception ex)
            {
                ReportDocumentConnectFailure(ui, current, ex);
                return null;
            }
        }
        if (lastAuthFailure is not null)
        {
            ReportDocumentConnectFailure(ui, current, lastAuthFailure);
        }
        return null;
    }

    /// <summary>
    /// 插件连接的「测试」:真开一次会话,随即原路关掉。
    /// <para>
    /// 为什么必须走这条路而不是探个 TCP 端口:能连上 6379 不等于这条配置能用 ——
    /// 口令错、ACL 用户没权限、库号越界、TLS 证书不被信任,全都是端口通着却连不上的。
    /// 「测试」要能替用户排除的正是这些,所以它跑的必须是与「连接」同一套握手。
    /// </para>
    /// <para>
    /// 隧道与文档都在 finally 里拆:测试不留任何东西 —— 既不占本地转发端口,
    /// 也不给插件留一条没人管的连接。
    /// </para>
    /// </summary>
    /// <param name="profile">要测的配置(凭据已由弹窗填好)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task ProbePluginConnectionAsync(SessionProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        // 引用了共享凭据的配置先把凭据取出来(#550);取不到时异常的消息就是测试结论。
        profile = await ResolveCredentialsAsync(profile, cancellationToken).ConfigureAwait(true);
        // 文件系统形态(S3 之类)与工作台形态(Redis 之类)是两套打开路径,按声明分流。
        // 查形态不会装载任何插件程序集。
        if (_protocolRegistry is { } registry
            && registry.KindOf(profile.PluginProtocolId) == PluginConnectionKind.FileSystem)
        {
            if (_pluginProtocols is not { } sessions)
            {
                throw new InvalidOperationException(Strings.Get("Plugin_TestUnavailable"));
            }
            Guid fileSessionId = await sessions.OpenSessionAsync(profile, cancellationToken).ConfigureAwait(true);
            await sessions.CloseSessionAsync(fileSessionId, CancellationToken.None).ConfigureAwait(true);
            return;
        }

        if (_workspaceLauncher is not { } launcher)
        {
            throw new InvalidOperationException(Strings.Get("Plugin_TestUnavailable"));
        }
        (WorkspaceEndpoint? endpoint, Guid tunnelId) =
            await EstablishWorkspaceTunnelAsync(profile, cancellationToken).ConfigureAwait(true);
        PluginWorkspaceSession? session = null;
        try
        {
            session = await launcher.OpenAsync(profile, endpoint, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            if (session is not null)
            {
                launcher.Forget(session.SessionId);
                try
                {
                    await session.Document.DisposeAsync().ConfigureAwait(true);
                }
                catch (Exception)
                {
                    // 测试的收尾失败不该改变测试结论 —— 结论已经由 OpenAsync 给出了。
                }
            }
            await RemoveWorkspaceTunnelAsync(tunnelId).ConfigureAwait(true);
        }
    }

    /// <summary>工作台文档关闭:摘登记、退订状态、拆隧道、释放插件那边的连接。幂等。</summary>
    private async Task CloseWorkspaceDocumentAsync(PluginWorkspaceDocument document)
    {
        document.Workspace.StatusChanged -= OnWorkspaceStatusChanged;
        _workspaceDocuments.TryRemove(document.SessionId, out _);
        _workspaceLauncher?.Forget(document.SessionId);
        // 摘掉这一条会话再重算,而不是直接把节点写成「未连接」:
        // 同一条配置名下可能还开着别的文档或终端标签(#321 那条纪律的文档版)。
        ForgetDocumentSession(document.SessionId);
        // 先关插件那边的连接,再拆隧道:反过来会让插件的关闭流程对着一条已经断掉的通道超时。
        await document.CloseAsync().ConfigureAwait(true);
        if (_workspaceTunnels.TryRemove(document.SessionId, out Guid tunnelId))
        {
            await RemoveWorkspaceTunnelAsync(tunnelId).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 按连接配置里选的跳板会话建立 SSH 会话与本地端口转发。
    /// <para>
    /// 这一步刻意留在界面层:建 SSH 会话要走宿主既有的两步认证、指纹校验与 ProxyJump 链路,
    /// 而**凭据永不出宿主**是硬规则。插件只拿到"一个已经能连的本地端点"。
    /// </para>
    /// <para>
    /// 已连着的会话优先复用 —— 用户刚在终端里连上那台跳板机,再为同一台开第二条 SSH
    /// 只是白付一次握手与一份内存。
    /// </para>
    /// </summary>
    /// <returns>端点与隧道 id;没有配跳板机时两者都为空。</returns>
    private async Task<(WorkspaceEndpoint? Endpoint, Guid TunnelId)> EstablishWorkspaceTunnelAsync(
        SessionProfile profile,
        CancellationToken cancellationToken)
    {
        if (_protocolRegistry is not { } registry
            || _tunnelService is null
            || _sessionRepository is null
            || _connectionWorkflowService is null)
        {
            return (null, Guid.Empty);
        }
        WorkspaceDescriptor? descriptor =
            (await registry.ResolveWorkspaceAsync(profile.PluginProtocolId).ConfigureAwait(true))?.Descriptor;
        // **能力位同样要按变体取**,理由与打开前的凭据判定一样。漏了这一格的后果是具体的:
        // 数据库插件的基础描述符带 SshTunnel,而 SQLite 变体不带 —— 用户先在 MySQL 下选了跳板会话、
        // 再把方言切成 SQLite,那一行只是**收起来**、值仍在 PluginSettings 里。于是打开一个本地
        // .db 文件时,宿主会真的去连跳板机,并把 `RemoteHost` 设成 `D:\app.db` 建本地转发。
        if (descriptor is null
            || !WorkspaceVariants.EffectiveFeatures(descriptor, profile.PluginSettings)
                .HasFlag(WorkspaceFeatures.SshTunnel))
        {
            return (null, Guid.Empty);
        }
        // 找到那个 SshSession 形态的字段,读出用户选的跳板配置 id。
        ProtocolSettingField? field = descriptor.Fields
            .FirstOrDefault(candidate => candidate.Kind == ProtocolSettingKind.SshSession);
        string? raw = field is null
            ? null
            : profile.PluginSettings?.GetValueOrDefault(field.Key);
        if (string.IsNullOrWhiteSpace(raw) || !Guid.TryParse(raw, out Guid jumpProfileId))
        {
            return (null, Guid.Empty);
        }

        IReadOnlyList<SessionProfile> saved = await _sessionRepository.GetAllSessionsAsync().ConfigureAwait(true);
        // 跳板配置被删了。**不静默直连** —— 那会把一条本该走内网的连接直接打到公网上去。
        SessionProfile? jump = saved.FirstOrDefault(candidate => candidate.Id == jumpProfileId) ?? throw new PluginProtocolConnectionException(Strings.Get("Plugin_JumpSessionMissing"));

        // 按"目标主机 + 端口 + 用户"匹配已连着的会话。这不是权宜:隧道要穿的是**那台主机**,
        // 谁开的那条 SSH 无关紧要 —— 而 SshSession 上本就没有"来自哪条配置"这个信息。
        // 跳板引用共享凭据而自己没填用户名时,实际登录名在凭据里(#550);取不到就不复用,下面新连时会如实报错。
        string jumpUsername = jump.Username;
        if (CredentialMaterial.NeedsResolution(jump) && string.IsNullOrWhiteSpace(jumpUsername))
        {
            try
            {
                jumpUsername = (await ResolveCredentialsAsync(jump, cancellationToken).ConfigureAwait(true)).Username;
            }
            catch (CredentialProviderException)
            {
                // 留给 ConnectProfileAsync 去报。
            }
        }
        Guid sshSessionId = _sshConnectionService?.Sessions
            .FirstOrDefault(session => session.Status == SessionStatus.Connected
                                       && string.Equals(session.ConnectionInfo.Host, jump.Host, StringComparison.OrdinalIgnoreCase)
                                       && session.ConnectionInfo.Port == jump.Port
                                       && string.Equals(session.ConnectionInfo.Username, jumpUsername, StringComparison.Ordinal))
            ?.SessionId ?? Guid.Empty;
        if (sshSessionId == Guid.Empty)
        {
            SshSession connected;
            try
            {
                connected = await _connectionWorkflowService
                    .ConnectProfileAsync(jump, cancellationToken).ConfigureAwait(true);
            }
            catch (CredentialProviderException ex)
            {
                // 跳板的共享凭据取不到:改报普通的连接错误。原样抛出去的话,工作台的连接循环会把它
                // 当成「这条工作台缺凭据」退回登录框 —— 问的是 Redis 的口令,缺的却是跳板的(#550)。
                throw new PluginProtocolConnectionException(Strings.Format("Svc_JumpCredentialFailed", jump.Name, ex.Message));
            }
            sshSessionId = connected.SessionId;
        }

        // 本地端口自己挑:隧道服务按配置里的端口监听,没有"由内核分配后回报"这条路。
        // 先 bind 0 拿一个空闲端口再放掉,是这种情况下的标准做法(有极小的竞争窗口,
        // 撞上了表现为"端口已被占用"的明确失败,而不是静默连错地方)。
        int localPort = ReserveLocalPort();
        var config = new TunnelConfig
        {
            Type = TunnelType.LocalForward,
            Name = $"{profile.Name} ↝ {jump.Name}",
            LocalHost = "127.0.0.1",
            LocalPort = (uint)localPort,
            RemoteHost = profile.Host,
            RemotePort = (uint)profile.Port
        };
        TunnelInfo tunnel = await _tunnelService
            .CreateLocalForwardAsync(sshSessionId, config, cancellationToken).ConfigureAwait(true);
        return (new("127.0.0.1", localPort, profile.Host, profile.Port, jump.Name), tunnel.Id);
    }

    /// <summary>拆掉一条为工作台建的隧道(尽力而为:拆不掉不该把关闭流程也带崩)。</summary>
    private async Task RemoveWorkspaceTunnelAsync(Guid tunnelId)
    {
        if (tunnelId == Guid.Empty || _tunnelService is null)
        {
            return;
        }
        try
        {
            await _tunnelService.RemoveTunnelAsync(tunnelId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Workspace] Removing tunnel {tunnelId} failed: {ex.Message}");
        }
    }

    /// <summary>借一个空闲的本地端口号(bind 0 → 读端口 → 放掉)。</summary>
    private static int ReserveLocalPort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// 提供该连接类型的插件被停用/卸载:把它名下还开着的标签页撤掉。
    /// <para>
    /// 走 <see cref="DockWorkspace.CloseDocument" /> 而不是直接释放 —— 只有它会触发
    /// <c>DocumentClosed</c>,不然界面上会留下一个再也不会应答的面板。
    /// </para>
    /// </summary>
    private void OnWorkspaceSessionAbandoned(Guid sessionId) =>
        RxSchedulers.MainThreadScheduler.Schedule(() =>
        {
            if (_workspaceDocuments.TryGetValue(sessionId, out PluginWorkspaceDocument? document))
            {
                Layout.CloseDocument(document);
            }
        });

    /// <summary>工作台会话状态变化 → 资源管理器树的状态圆点(理由与线程约束同 FTP 侧)。</summary>
    private void OnWorkspaceStatusChanged(object? sender, WorkspaceStatus status)
    {
        if (sender is not IWorkspaceDocument document)
        {
            return;
        }
        Guid sessionId = _workspaceDocuments
            .FirstOrDefault(pair => ReferenceEquals(pair.Value.Workspace, document)).Key;
        if (sessionId == Guid.Empty)
        {
            return;
        }
        UpdateDocumentSessionStatus(sessionId, status.State switch
        {
            ProtocolSessionState.Connected => SessionStatus.Connected,
            ProtocolSessionState.Faulted => SessionStatus.Error,
            _ => SessionStatus.Disconnected,
        });
    }

    /// <summary>插件协议会话状态变化 → 资源管理器树的状态圆点(理由与线程约束同 FTP 侧)。</summary>
    private void OnPluginSessionStateChanged(object? sender, PluginProtocolSessionStateChange change)
    {
        if (change.State == PluginProtocolSessionState.Closed)
        {
            // 会话没了就从册子上摘掉,而不是把节点写成「未连接」——
            // 同一条配置的另一个文档可能还开着。
            ForgetDocumentSession(change.SessionId);
            return;
        }
        UpdateDocumentSessionStatus(change.SessionId, change.State switch
        {
            PluginProtocolSessionState.Connected => SessionStatus.Connected,
            PluginProtocolSessionState.Faulted => SessionStatus.Error,
            _ => SessionStatus.Disconnected,
        });
    }

    /// <summary>
    /// 插件协议缺少登录凭据时才需要弹登录框。允许匿名的协议(S3 的公开只读桶)下
    /// **两者都空 = 匿名访问**,是一条正当路径,不能弹框;
    /// 只有「填了用户名却没有口令」才是真的缺东西。
    /// </summary>
    private static bool RequiresPluginCredentials(SessionProfile profile, bool allowsAnonymous) =>
        // 引用共享凭据的不先弹,理由同 RequiresCredentials;存着的口令解不开也算缺,同样理由。
        profile.CredentialSource is null
        && (allowsAnonymous
            ? !string.IsNullOrWhiteSpace(profile.Username) && MissingPassword(profile)
            : string.IsNullOrWhiteSpace(profile.Username) || MissingPassword(profile));

    /// <summary>没有能用的密码:空着,或者存着的那份在这台设备上解不开(FTP 与插件协议只认密码)。</summary>
    private static bool MissingPassword(SessionProfile profile) =>
        string.IsNullOrEmpty(profile.Password) || StoredSecret.IsUnreadable(profile.Password);

    /// <summary>
    /// 返回一份把服务器证书指纹记为已信任的配置副本。指纹写进协议自己声明的那个隐藏字段
    /// —— 宿主不知道该协议管它叫什么,所以字段键由异常带过来。
    /// </summary>
    private static SessionProfile WithTrustedPluginCertificate(SessionProfile profile, PluginProtocolCertificateException certificate)
    {
        if (certificate.SettingKey is not { Length: > 0 } key)
        {
            // 协议没声明存放位置:只能本次连接内信任,不落盘。
            return profile;
        }
        Dictionary<string, string> settings = SessionProfile.CloneSettings(profile.PluginSettings) ?? [with(StringComparer.Ordinal)];
        settings[key] = certificate.Thumbprint;
        profile.PluginSettings = settings;
        return profile;
    }

    /// <summary>
    /// FTP 会话状态变化 → 资源管理器树的状态圆点。可能来自任意线程(操作失败的那条),
    /// 因此统一切回 UI 线程再改绑定属性。
    /// </summary>
    private void OnFtpSessionStateChanged(object? sender, FtpSessionStateChange change)
    {
        if (change.State == FtpSessionState.Closed)
        {
            // 会话没了就从册子上摘掉,而不是把节点写成「未连接」——
            // 同一条配置的另一个 FTP 文档可能还开着(点快了开出两个,关掉其中一个)。
            ForgetDocumentSession(change.SessionId);
            return;
        }
        UpdateDocumentSessionStatus(change.SessionId, change.State switch
        {
            FtpSessionState.Connected => SessionStatus.Connected,
            // 掉线显示为「离线」而不是「未连接」:文档还开着,用户需要看出是断了而不是没连过。
            _ => SessionStatus.Error,
        });
    }

    /// <summary>
    /// 在主线程上重算并写回树节点状态(绑定属性不得在后台线程改)。
    /// <para>
    /// 重算而不是直接写一个状态进去:一条配置名下可能同时开着多个标签与多个文档,
    /// 树上只有一个节点,谁都不该按自己那一份覆盖别人的(见 <see cref="RefreshSessionStatus" />)。
    /// </para>
    /// <para>
    /// 用 <c>RxSchedulers.MainThreadScheduler</c> 而不是裸 <c>Dispatcher.UIThread.Post</c>:
    /// 与本类其它 VM 更新一致,并且在没有跑 Avalonia 消息循环的宿主(headless 测试)里也能落地
    /// —— 直接 Post 的作业在那种环境下永远不会被执行。
    /// </para>
    /// </summary>
    private void ScheduleSessionStatusRefresh(Guid profileId) =>
        RxSchedulers.MainThreadScheduler.Schedule(() => RefreshSessionStatus(profileId));

    /// <summary>FTP 缺少登录凭据时才需要弹登录框;匿名登录不需要用户名与口令。</summary>
    private static bool RequiresFtpCredentials(SessionProfile profile) =>
        profile.Ftp?.Anonymous != true &&
        // 引用共享凭据的不先弹,理由同 RequiresCredentials;存着的口令解不开也算缺。
        profile.CredentialSource is null &&
        (string.IsNullOrWhiteSpace(profile.Username) || MissingPassword(profile));

    /// <summary>返回一份把服务器证书指纹记为已信任的配置副本。</summary>
    private static SessionProfile WithTrustedCertificate(SessionProfile profile, string thumbprint)
    {
        FtpSettings settings = profile.Ftp?.Clone() ?? new FtpSettings();
        settings.TrustedCertificateThumbprint = thumbprint;
        profile.Ftp = settings;
        return profile;
    }

    /// <summary>
    /// 把配置写回仓储(仅对**已保存**的配置;「最近连接」重建的临时配置只在本次连接内有效)。
    /// 目前的用途是记住用户刚刚信任的服务器证书指纹,FTPS 与插件协议共用。
    /// </summary>
    private async Task<bool> PersistProfileIfSavedAsync(SessionProfile profile)
    {
        if (_sessionRepository is null)
        {
            return true;
        }
        try
        {
            if (await _sessionRepository.GetSessionAsync(profile.Id).ConfigureAwait(true) is null)
            {
                return true;
            }
            // 经工作流保存:登录框交回来的副本可能带着用户这次手输、却没让记住的密码,
            // 工作流会按「记住密码」把它剥掉 —— 直接落仓储等于替用户记住了他明确不让记的东西。
            if (_connectionWorkflowService is { } workflow)
            {
                await workflow.SaveProfileAsync(profile).ConfigureAwait(true);
            }
            else
            {
                await _sessionRepository.SaveSessionAsync(profile).ConfigureAwait(true);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            // 信任指纹没落盘不影响本次连接,下次会再问一遍。
            return false;
        }
    }

    /// <summary>
    /// 目录收藏的落盘入口(#621):只改库里那一条配置的收藏,其余字段不碰。
    /// 不走 <see cref="PersistProfileIfSavedAsync" /> 的整条写回 —— 文档手里那份配置可能早就过时了
    /// (编辑框改过端口或名字、另一个文档加过收藏),整条写回会把那些改动悄悄回滚。
    /// </summary>
    /// <returns>改后的收藏;配置没保存过(「最近连接」重建的临时配置)时为 null,收藏只在本文档内有效。</returns>
    private async Task<List<RemoteDirectoryBookmark>?> UpdateProfileBookmarksAsync(
        Guid profileId, Func<List<RemoteDirectoryBookmark>, bool> update) =>
        _sessionRepository is null
            ? null
            : await _sessionRepository.UpdateDirectoryBookmarksAsync(profileId, update).ConfigureAwait(true);

    /// <summary>
    /// 连接侧边栏"最近连接"条目:有 id 时解析已保存配置,否则从记录的主机/端口/用户名重建临时配置。
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
            ConnectionType = entry.ConnectionType,
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
        // 用户名为空是一条正当路径(匿名 S3 桶、没设 requirepass 的 Redis),
        // 那时不能拼出 "@127.0.0.1:6379" 这种前面缺了一截的目标。
        string target = string.IsNullOrWhiteSpace(profile.Host)
            ? profile.Name
            : HostAddress.FormatTarget(string.IsNullOrWhiteSpace(profile.Username) ? null : profile.Username, profile.Host, profile.Port);
        // 消息里已经是可读的原因了 —— 不再需要从 "The connection could not be established - …"
        // 这种前缀里切一段出来。那段解析是上一版底层库逼出来的:它把原因码声明成 internal,
        // 只能按消息前缀猜,而上游改一次措辞就静默失效。现在原因是强类型的
        // (SshFailureReason),分流在 Infrastructure 的 SshInterop 里一次做完。
        string detail = ex.Message;
        // 直接匹配 Core 的中立异常族(VelaSsh*Exception)。
        // 曾经这里按类型名字符串匹配 SSH.NET 的旧名("SshAuthenticationException" 等),
        // 换库之后实际类型已是 VelaSshAuthenticationException,没有一个分支能命中,
        // 所有连接错误都掉进兜底文案。派生类型必须排在基类型前面。
        return ex switch
        {
            VelaSshTailscaleAuthenticationException => detail,
            // 以前这里还补一句「若要求两步验证本版无法连接」:那时宿主没有输动态码的界面。
            // 现在服务端要动态码会弹框问(KeyboardInteractivePromptDialogService),那句话已经不成立。
            VelaSshAuthenticationException => $"{Strings.Format("Msg_AuthFailed", target)}\n{detail}",
            // TimeoutException 来自 SshConnectionService:底层库内部超时(调用方并未取消)时它对外
            // 统一抛这个类型。不列进来的话真超时会掉进兜底文案,显示一句英文原始消息。
            VelaSshOperationTimeoutException or TimeoutException => Strings.Format("Msg_ConnectTimeout", target),
            VelaSshConnectionException => $"{Strings.Format("Msg_ConnectFailed", target)}\n{detail}",
            SocketException => Strings.Format("Msg_NetworkError", target),
            // 插件协议族的消息按 SDK 契约就是**面向用户**的,并且已经带上了端点。
            // 再套一层"连接 X 失败:"只会把同一个地址写两遍。
            PluginProtocolConnectionException
                or PluginProtocolAuthenticationException
                or PluginProtocolUnavailableException => detail,
            // 共享凭据取不到:消息本身就是面向用户的,而且写明了是哪条凭据、缺什么(#550)。
            CredentialProviderException => detail,
            _ => Strings.Format("Msg_ConnectGenericFailed", target, detail),
        };
    }

    private void ConfigureTerminal(
        ITerminalEmulator emulator,
        AppSettings settings,
        TerminalType terminalType,
        bool forceUtf8 = false,
        SessionProfile? profile = null
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
        ApplyLiveTerminalSettings(emulator, settings, forceUtf8, profile);
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
                _fontSizePersistDebounce.Stop();
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
    private void OnGutterOptionsChanged(bool timestamp, bool number, bool fold, bool blank, bool guideLine)
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
                    && b.ShowFoldGuideLine == guideLine
                )
                {
                    return;
                }
                b.ShowLineTimestamp = timestamp;
                b.ShowLineNumber = number;
                b.ShowFoldMarker = fold;
                b.GutterBlank = blank;
                b.ShowFoldGuideLine = guideLine;
                await _settingsService.SaveSettingsAsync(settings).ConfigureAwait(false);
            }
            catch
            {
                // 写回失败只影响下次启动的初始值,不打断当前会话。
            }
        });
    }

    /// <summary>
    /// 可在活动会话上安全更改的设置:回滚深度、字体、字号、主机字符集以及完整的
    /// 终端行为/配色选项集。在标签创建时应用,并每次保存设置后重新应用到所有已打开的标签(#3/#15/#21)。
    /// </summary>
    private void ApplyLiveTerminalSettings(
        ITerminalEmulator emulator,
        AppSettings settings,
        bool forceUtf8 = false,
        SessionProfile? profile = null
    ) =>
        TerminalSettingsApplier.Apply(
            emulator, settings, ActiveUiTheme, forceUtf8, profile, MultilinePasteConfirmer, CommandRerunConfirmer);

    /// <summary>
    /// 当前实际生效的界面主题:「跟随系统」按应用的实际变体落到 VelaDark / VelaLight。
    /// 没有主题服务(单测)时同样按变体兜底。
    /// </summary>
    private static UiTheme ActiveUiThemeFor(IThemeService? themeService) =>
        UiThemeCatalog.Resolve(
            themeService?.CurrentTheme,
            Application.Current?.ActualThemeVariant != Avalonia.Styling.ThemeVariant.Light
        );

    private UiTheme ActiveUiTheme => ActiveUiThemeFor(_themeService);

    /// <summary>主题变了 → 把新主题的终端配色下发到所有已打开的终端标签。</summary>
    private void RefreshTerminalThemePalette() =>
        RxSchedulers.MainThreadScheduler.Schedule(() =>
            ApplyLiveSettingsToOpenTabs(_latestSettings ?? new AppSettings())
        );

    /// <summary>
    /// 把终端默认背景不透明度实时应用到所有已打开的终端标签(背景图即时预览用:拖动滑杆时视图层直接调,
    /// 不经保存/重建标签)。opacity=1 即不透明,还原默认。
    /// </summary>
    public void ApplyTerminalBackgroundOpacityToAllTabs(double opacity)
    {
        foreach (TerminalTabViewModel tab in TerminalTabs)
        {
            if (tab.TerminalEmulator.Control is VelaTerminalControl control)
            {
                control.BackgroundOpacity = opacity;
            }
        }
    }

    private void OnSettingsSaved(AppSettings settings)
    {
        // 代理设置变了要当场生效:新连接本来就每次重读设置,这里再补上半截 ——
        // 把一个「连接失败」的标签自动重连一次。用户改代理多半正是因为刚才连不上,
        // 不该逼他改完设置再手动点一次「重新连接」(甚至以为要重启应用)。
        AppSettings? previous = _latestSettings;
        bool proxyChanged = previous is not null && ProxyChanged(previous.Proxy, settings.Proxy);
        _latestSettings = settings;

        // SaveSettingsAsync 可能在线程池的回调中完成;字体/字号涉及布局,
        // 因此编组到 UI 线程(主调度器即 Avalonia 的 Dispatcher)。
        RxSchedulers.MainThreadScheduler.Schedule(() =>
            {
                ApplyShellPreferences(settings);
                ApplyLiveSettingsToOpenTabs(settings);
                // 采样间隔改了要当场生效,而不是等下次启动 —— 用户调它多半正是因为
                // 现在这个频率让远端不好受。
                StatusMetrics.ConfiguredIntervalSeconds = settings.General.StatusMetricsIntervalSeconds;

                // 已打开的文件浏览器同步最新的传输选项(冲突策略/并发/带宽等)与
                // “显示隐藏文件”状态(设置审计 C-04:设置中心与工具栏共用一个来源)。
                // 面板按会话缓存后,当前实例与全部缓存实例都要广播到。
                FileBrowser.TransferOptions = settings.Transfer;
                FileBrowser.ApplyDisplayOptions(settings.Transfer);
                foreach (FileBrowserViewModel browser in _fileBrowserCache.Values)
                {
                    browser.TransferOptions = settings.Transfer;
                    browser.ApplyDisplayOptions(settings.Transfer);
                }
                // 独立文件标签(单栏、双栏远程)的远端栏同样换上新选项:它们打开时拿的是那一刻的设置,
                // 不广播的话改了冲突策略、并发、重试次数、双击行为,开着的文件标签还照旧(差距分析 A7);
                // 隐藏文件与列显示也在这里跟上 —— 在别的面板上切了,这里一起变(差距分析 A8)。
                // 限速、保留时间戳、上传后落盘由 SftpService 每次传输现读设置,本来就跟得上。
                foreach (FileBrowserViewModel pane in DocumentFilePanes())
                {
                    pane.TransferOptions = settings.Transfer;
                    pane.ApplyDisplayOptions(settings.Transfer);
                }

                RevealActiveSessionInSidebar();

                if (proxyChanged)
                {
                    ReconnectFailedTabsAfterProxyChange();
                }
            }
        );
    }

    /// <summary>
    /// 代理配置是否真的变了(类型/端点/凭据/DNS 任一)。<see cref="ProxyOptions" /> 没有值相等语义,
    /// 逐字段比 —— 只在真变了的时候才去动用户的标签。
    /// </summary>
    internal static bool ProxyChanged(ProxyOptions? before, ProxyOptions after)
    {
        if (before is null)
        {
            return false;
        }
        return !string.Equals(before.Type, after.Type, StringComparison.Ordinal)
               || !string.Equals(before.Host, after.Host, StringComparison.Ordinal)
               || before.Port != after.Port
               || !string.Equals(before.Username, after.Username, StringComparison.Ordinal)
               || !string.Equals(before.Password, after.Password, StringComparison.Ordinal)
               || before.ProxyDns != after.ProxyDns;
    }

    /// <summary>
    /// 代理设置变更后,把**连接失败**的 SSH 标签自动重连一次。
    /// </summary>
    /// <remarks>
    /// 只碰失败覆盖层还停在「连接失败」的那些标签,判据见
    /// <see cref="ReconnectPolicy.ShouldReconnectAfterProxyChange" /> —— 注意终端标签
    /// **没有** <see cref="SessionStatus.Error" /> 这一态,按它判会一个都选不中。
    /// 干净断开的标签不在此列:那是用户自己的意图(或已有 <see cref="ReconnectAllAfterResume" />
    /// 那条按 AutoReconnect 设置走的自动重连),不能因为改了个代理就替他做主。
    /// 本地终端与插件协议同样跳过 —— 它们不看这份代理设置。
    /// </remarks>
    private void ReconnectFailedTabsAfterProxyChange()
    {
        foreach (TerminalTabViewModel tab in FailedSshTabsAfterProxyChange(TerminalTabs))
        {
            // 这是用户刚改完设置的一次显式重试,不该继承上一轮自动重连烧掉的次数额度。
            tab.ResetReconnectAttempts();
            _ = ReconnectTabAsync(tab);
        }
    }

    /// <summary>
    /// 代理设置变更后该被救回来的那些标签:停在「连接失败」覆盖层上的 SSH 标签。
    /// </summary>
    /// <remarks>
    /// 拆成可测的一步,是因为**这一步**才是会错的地方 —— 第一版按
    /// <see cref="SessionStatus.Error" /> 筛,而终端标签根本没有那一态
    /// (<c>MarkConnectionFailed</c> 写的是 <c>Disconnected</c> + <c>ConnectionError</c>),
    /// 于是整段成了死代码:编译得过、测试全绿、功能一次都没触发过(#464)。
    /// 判定条件本身在 <see cref="ReconnectPolicy.ShouldReconnectAfterProxyChange" />。
    /// <para>结果是一份快照:重连会改标签状态,不能边遍历边动。</para>
    /// </remarks>
    internal static List<TerminalTabViewModel> FailedSshTabsAfterProxyChange(IEnumerable<TerminalTabViewModel> tabs) =>
    [
        .. tabs.Where(tab =>
            // 本地终端与插件协议不看这份代理设置。
            tab.Profile is { ConnectionType: ConnectionType.SSH }
            && ReconnectPolicy.ShouldReconnectAfterProxyChange(
                tab.ConnectionStatus == SessionStatus.Disconnected,
                tab.HasConnectionError,
                tab.UserRequestedDisconnect,
                tab.LocalShell is not null,
                tab.RemoteShellExited,
                tab.AuthenticationRequiresUserAction))
    ];

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

    /// <summary>
    /// 文件标签(单栏、双栏远程)的远端栏挂上写回设置的回调:工具栏切「显示隐藏文件」、表头右键切列显示,
    /// 与终端侧栏的面板一样写回 <c>Transfer</c>,保存后再由 <see cref="OnSettingsSaved" /> 广播给所有面板
    /// (设置审计 C-04,差距分析 A8)。此前文件标签的面板不读也不写这两样:设置里开着隐藏文件,文件标签照样不显示;
    /// 在文件标签里切了,也不留到下次。
    /// 初始值由文档视图模型按打开时的设置铺好(<see cref="FileBrowserViewModel.ApplyDisplayOptions" />),这里只挂回调。
    /// </summary>
    /// <summary>
    /// 给文件栏挂上右键「在此处打开终端」(见 <see cref="OpenTerminalHereAsync" />),只给连接类型为 SSH 的配置:
    /// SFTP 专用配置多半就是没有 shell 的账号,FTP 与插件协议更没有。配置在点的那一刻现取 ——
    /// 终端标签认证重试之后换上的是登录框交回的新副本。
    /// </summary>
    private void WireOpenTerminalHere(FileBrowserViewModel pane, Func<SessionProfile?> profile)
    {
        if (profile() is { ConnectionType: ConnectionType.SSH })
        {
            pane.OpenTerminalAt = directory => profile() is { } current ? OpenTerminalHereAsync(current, directory) : Task.CompletedTask;
        }
    }

    private void WireDocumentPanesToSettings(ISftpDocumentContent content)
    {
        foreach (FileBrowserViewModel pane in content.RemotePanes)
        {
            pane.ShowHiddenFilesToggled = PersistShowHiddenFiles;
            pane.ColumnVisibilityToggled = PersistColumnVisibility;
        }
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

    /// <summary>
    /// 把当前设置的终端外观应用到一个**插件**的终端视图。
    /// <para>
    /// 走的是宿主自己那条路(<see cref="ApplyLiveTerminalSettings" />),不是另抄一份 ——
    /// 用户调过一次终端字体,不该因为换到插件的面板里就得再调一次;
    /// 以后加的外观项也自动跟着走,不会有一处忘了同步。
    /// </para>
    /// </summary>
    internal void ApplyTerminalAppearanceToPluginView(ITerminalEmulator emulator) =>
        ApplyLiveTerminalSettings(emulator, _latestSettings ?? new AppSettings());

    /// <summary>把一份设置应用到所有已打开的终端标签(保存与外观预览共用)。</summary>
    private void ApplyLiveSettingsToOpenTabs(AppSettings settings)
    {
        foreach (TerminalTabViewModel tab in TerminalTabs)
        {
            ApplyLiveTerminalSettings(tab.TerminalEmulator, settings, tab.LocalShell is not null, tab.Profile);
        }
    }

    /// <summary>当前挂着选区计数的控件(切标签时要先摘掉旧的,否则事件会累加)。</summary>
    private VelaTerminalControl? _selectionCounterSource;

    /// <summary>把状态栏的选区计数改挂到当前活动标签的终端控件上。</summary>
    private void RebindSelectionCounter()
    {
        if (_selectionCounterSource is { } previous)
        {
            previous.SelectionChanged -= OnActiveSelectionChanged;
        }
        _selectionCounterSource = ActiveTerminalControl;
        if (_selectionCounterSource is { } current)
        {
            current.SelectionChanged += OnActiveSelectionChanged;
        }
        // 切过去的标签可能本来就带着选区,先同步一次当前值。
        StatusBar.SelectionLength = 0;
    }

    private void OnActiveSelectionChanged(int length) => StatusBar.SelectionLength = length;

    /// <summary>
    /// 把活动标签的编码当场换掉(状态栏的编码菜单)。
    /// </summary>
    /// <remarks>
    /// 只作用于当前会话、不写回设置:用户点这个菜单多半是在试"这台机器到底是 GBK 还是
    /// UTF-8",一次试错不该改掉所有新会话的默认值。想固化就去设置里改。
    /// </remarks>
    private void ChangeActiveTabEncoding(string name)
    {
        if (ActiveTerminalTab is not { } tab || ActiveTerminalControl is not { } control)
        {
            return;
        }
        control.SetEncoding(TerminalSettingsApplier.ResolveEncoding(name));
        tab.EncodingName = name;
        StatusBar.Encoding = name;
    }

    /// <summary>
    /// 当前会话使用的文本编码:活动标签自己的编码优先,其次是全局设置,都没有就是 UTF-8。
    /// </summary>
    /// <remarks>
    /// 内置远程编辑器用它做"UTF-8 解不通时回落到什么"。连 GBK 服务器的人,
    /// 服务器上的文本文件多半也是 GBK —— 会话编码就是现成的、最靠谱的那个答案。
    /// </remarks>
    public Encoding ActiveSessionEncoding =>
        TerminalSettingsApplier.ResolveEncoding(ActiveTerminalTab?.EncodingName ?? _latestSettings?.TerminalEncoding);

    /// <summary>
    /// 将当前活动终端标签的连接详情投射到状态栏中,使左下角的指示器始终反映用户正在看的标签。
    /// </summary>
    /// <summary>
    /// 把后台活动账本接到状态栏的圆环上。
    /// <para>
    /// 账本在后台线程上报(插件装载、内容校验、预读都不在 UI 线程),而状态栏属性是给绑定读的,
    /// 因此这里是唯一的切线程点。<c>Post</c> 而不是 <c>InvokeAsync</c>:圆环晚一帧更新
    /// 没有任何影响,但让一条后台链去等 UI 线程就有卡住它的可能。
    /// </para>
    /// <para>本视图模型与账本同为应用级单例、同生共死,故不解挂事件。</para>
    /// </summary>
    /// <param name="activity">后台活动账本;无界面单测传 null 时整块跳过。</param>
    private void WireBackgroundActivity(IBackgroundActivityService? activity)
    {
        if (activity is null)
        {
            return;
        }
        activity.Changed += () =>
            Dispatcher.UIThread.Post(() => StatusBar.ApplyBackgroundActivities(activity.Activities),
                DispatcherPriority.Background);
        StatusBar.ApplyBackgroundActivities(activity.Activities);
    }

    private void UpdateStatusBarForActiveTab()
    {
        TerminalTabViewModel? tab = ActiveTerminalTab;
        Sidebar.ActiveIdentity = ResolveActiveIdentity(tab);
        // 读屏器读到的终端名字:与侧栏底部显示的身份同源,于是"听到的"与"看到的"一致。
        if (ActiveTerminalControl is { } accessible)
        {
            accessible.AccessibleName = ResolveActiveIdentity(tab) ?? tab?.Title;
        }
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

    /// <summary>
    /// 侧边栏底部那一行显示什么身份:远端会话给 <c>用户名@主机</c>(没有用户名时只给主机),
    /// 本地终端给本机用户名,没有活动标签就什么都不显示。
    /// 跟着 <see cref="UpdateStatusBarForActiveTab" /> 走 —— 它已经订阅了活动标签与连接状态,
    /// 切标签/断线都会走到,不需要第二套触发源。
    /// </summary>
    internal static string? ResolveActiveIdentity(TerminalTabViewModel? tab) => tab switch
    {
        null => null,
        { Profile: { } profile } => string.IsNullOrWhiteSpace(profile.Username)
            ? profile.Host
            : $"{profile.Username}@{profile.Host}",
        { LocalShell: not null } => Environment.UserName,
        _ => null
    };

    private void SetActiveFromDocument(DockDocument? dockDocument)
    {
        // 切到**工具面板**(AI 聊天,以及任何插件用 PanelDisplayMode.Document 开的面板)时
        // 什么都不动:它不改变"我在哪台机器上"。
        //
        // 曾经这里写的是"不是终端文档就清空活动标签、收起文件浏览器",于是点一下 AI 面板
        // 状态栏整个空掉、文件浏览器凭空消失,点回终端才回来 —— 而用户开着它就是要它一直在。
        // 判据改成文档自己声明的 IsSessionDocument,新增文档类型时不必再回来改这里
        // (那张白名单正是上一次踩坑的原因)。
        if (dockDocument is not null && !dockDocument.IsSessionDocument)
        {
            return;
        }
        // 到这里要么是会话文档,要么一个文档都没剩 —— 两种情况下"当前终端"都换人了。
        if (dockDocument is not TerminalDocument document)
        {
            ActiveTerminalTab = null;
            UpdateStatusBarForActiveTab();
            if (FileBrowser.SessionId != Guid.Empty || FileBrowser.IsVisible)
            {
                // 用空白占位替换底部文件浏览器,使网格行塌缩;
                // 终端的浏览器保留在 _fileBrowserCache 中,保持其真实的 IsVisible 状态。
                FileBrowser = CreatePlaceholderFileBrowser();
            }
            return;
        }
        // 这里就是"活动终端标签"的**唯一**赋值处:停靠布局说哪个文档活着,哪个就活着。
        // 原先还要反手把 TabBar.ActiveTab 也设一遍(并且防着回环),那份平行状态已经拆掉。
        ActiveTerminalTab = document.Terminal;
        document.Terminal.HasBellAlert = false; // 切到该标签即清除 Bell 提醒
        RebindFileBrowser();
    }

    private Task GetOrCreateSftpCloseTask(SftpDocumentBase document)
    {
        lock (_sftpCloseTasksSync)
        {
            if (_sftpCloseTasks.TryGetValue(document, out Task? existing))
            {
                return existing;
            }

            Task task = CloseSftpDocumentCoreAsync(document);
            _sftpCloseTasks[document] = task;
            _ = task.ContinueWith(
                _ => RemoveSftpCloseTask(document, task),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return task;
        }
    }

    /// <summary>登记一件终端文档的关闭工作;同一个文档重复触发时复用同一件。</summary>
    private Task TrackTerminalCloseTask(TerminalDocument document)
    {
        lock (_sftpCloseTasksSync)
        {
            if (_terminalCloseTasks.TryGetValue(document, out Task? existing))
            {
                return existing;
            }

            Task task = OnDocumentClosedAsync(document);
            _terminalCloseTasks[document] = task;
            _ = task.ContinueWith(
                _ =>
                {
                    lock (_sftpCloseTasksSync)
                    {
                        if (_terminalCloseTasks.TryGetValue(document, out Task? current)
                            && ReferenceEquals(current, task))
                        {
                            _terminalCloseTasks.Remove(document);
                        }
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return task;
        }
    }

    /// <summary>测试探针:取该终端文档正在进行的关闭任务;没有则返回已完成任务。</summary>
    internal Task TerminalCloseTaskFor(TerminalDocument document)
    {
        lock (_sftpCloseTasksSync)
        {
            return _terminalCloseTasks.TryGetValue(document, out Task? task) ? task : Task.CompletedTask;
        }
    }

    private void RemoveSftpCloseTask(SftpDocumentBase document, Task task)
    {
        lock (_sftpCloseTasksSync)
        {
            if (_sftpCloseTasks.TryGetValue(document, out Task? current) && ReferenceEquals(current, task))
            {
                _sftpCloseTasks.Remove(document);
            }
        }
    }

    /// <summary>
    /// 测试探针:取该文档正在进行的关闭任务;没有(尚未关闭或已收尾)则返回已完成任务。
    /// <para>
    /// <c>DocumentClosed</c> 的处理里是**同步**登记这个任务的,因此
    /// <c>Layout.CloseDocument(doc)</c> 一返回就能取到它。测试借它等关闭真正跑完,
    /// 而不是去猜一个毫秒数 —— 关闭是异步的,树上的状态又在关闭的收尾里才更新。
    /// </para>
    /// </summary>
    internal Task GetStandaloneSftpCloseTask(SftpDocumentBase document)
    {
        lock (_sftpCloseTasksSync)
        {
            return _sftpCloseTasks.TryGetValue(document, out Task? task) ? task : Task.CompletedTask;
        }
    }

    internal bool HasPendingStandaloneSftpDocuments()
    {
        lock (_sftpCloseTasksSync)
        {
            return _sftpCloseTasks.Count > 0
                || Layout.AllDocuments().OfType<SftpDocumentBase>().Any();
        }
    }

    private async Task CloseSftpDocumentCoreAsync(SftpDocumentBase document)
    {
        try
        {
            await document.Content.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => LastConnectionError = ex.Message);
        }
        finally
        {
            // 摘掉这一条会话再重算,而不是无条件把节点写成「未连接」——
            // 用户点快了对同一条配置开出两个文档、关掉其中一个时,旧写法会把还活着的那条
            // 也一起熄掉(树上只有一个节点,而这里按配置 Id 直接覆盖)。
            // 放在 finally 里:关闭本身失败(服务器已经没影了)也必须把它从册子上摘掉,
            // 否则那条会话永远"活着",节点再也回不到未连接。
            foreach (Guid sessionId in document.Content.SessionIds)
            {
                ForgetDocumentSession(sessionId);
            }
        }
    }

    internal async Task CloseStandaloneSftpDocumentsAsync()
    {
        Task[] closeTasks;
        lock (_sftpCloseTasksSync)
        {
            Task[] trackedTasks = [.. _sftpCloseTasks.Values];
            Task[] currentDocumentTasks = [.. Layout
                .AllDocuments()
                .OfType<SftpDocumentBase>()
                .Select(GetOrCreateSftpCloseTask)];
            // 终端文档的关闭同样是异步的,一起等 —— 漏掉它的话,退出时会有连接
            // 还没断干净进程就没了,远端看到的是被掐断而不是一次干净的关闭。
            closeTasks = [.. trackedTasks
                .Concat(currentDocumentTasks)
                .Concat(_terminalCloseTasks.Values)
                .Distinct()];
        }
        await Task.WhenAll(closeTasks);

        // 传输队列平时攒一秒落一次盘;退出前补落一次,刚传完的文件下次启动就不会又出现在「待继续」里。
        // 只等两秒:库卡住了也不能把退出拖住。
        try
        {
            await FileTransfer.Journal.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
        }
    }

    private async Task OnDocumentClosedAsync(TerminalDocument document)
    {
        TerminalTabViewModel tab = document.Terminal;
        // 关掉一个还在连的标签 = 不连了。与 ConnectingDocument 同一条纪律:六个关闭入口
        // 都汇到 DocumentClosed,取消挂在这一个点上。握手流程收到取消后自己收尾
        // (会话若已建起就断掉),右下角圆环上那条「连接中」随之熄灭,也不再弹失败提示。
        CancelTabConnect(tab);
        StopSessionLogging(tab);
        Task sidebarClosed = CloseSftpForTab(tab);
        await tab.DisposeAsync().ConfigureAwait(true);
        // 侧栏面板开出来的外部编辑会话要赶在 SSH 断开之前把没传完的那一次传掉(理由同 SFTP 文档标签关闭),
        // 所以等它收完再拆连接;没有编辑会话时它当场就完成了。
        await sidebarClosed.ConfigureAwait(true);
        // Dispose 只拆终端传输;底层 SSH 客户端也要断开释放。
        TeardownSshSession(tab.SessionId);

        // 关闭标签不会再触发 ConnectionStatus 变更(已 Dispose),这里按剩下的标签重算
        // 树上的状态:同配置还有其他标签时取它们的合并状态,一个不剩才回到未连接。
        // 文档离开工作区时 SyncTabSubscriptions 已经算过一次,这里是幂等兜底
        // ——文档关闭时标签可能已经不在标签栏里了。
        if (tab.Profile is { Id: var profileId } && profileId != Guid.Empty)
        {
            RefreshSessionStatus(profileId);
        }
    }

    /// <summary>
    /// 拆除正在关闭的标签对应会话的 SFTP 通道,并在浏览器当前绑定到该会话时将面板替换为空白占位。
    /// 面板仍在显示该会话时,取消绑定并隐藏 —— 关闭 SSH 标签不应留下一个活动的、可操作的 SFTP 面板(#22)。
    /// </summary>
    /// <remarks>
    /// 面板驱逐当场做完;返回的任务是收尾的后半段:先收掉这个面板开出来的外部编辑会话
    /// (<see cref="RemoteEditSessionManager.CloseScopeAsync" />),再拆 SFTP 通道。以前侧栏这一路没人收,
    /// watcher 一直挂到应用退出,对着的会话早就没了,之后每存一次都换来一次注定失败的上传。
    /// 标签关闭时调用方等它收完才断 SSH;断线驱逐时连接已经没了,未传的改动照原口径留成草稿并提示位置。
    /// 任务不会失败(两步各自兜住),不等它的调用方可以直接丢弃。
    /// </remarks>
    private Task CloseSftpForTab(TerminalTabViewModel tab)
    {
        if (_sftpService is null || tab.SessionId == Guid.Empty)
        {
            return Task.CompletedTask;
        }
        Guid closedSessionId = tab.SessionId;

        // 在拆除 SFTP 通道前先驱逐:使在飞的列举被取消,而非与客户端释放争抢
        // (否则 SSH.NET 会从 ListDirectory 内部抛 NRE)。
        EvictFileBrowser(closedSessionId);
        return CloseSidebarScopeAsync(_sftpService, closedSessionId);
    }

    /// <summary>侧栏面板的收尾后半段:外部编辑会话 → SFTP 通道,顺序与 SFTP 文档标签关闭相同。</summary>
    private static async Task CloseSidebarScopeAsync(ISftpService sftp, Guid sessionId)
    {
        try
        {
            await RemoteEditSessionManager.CloseScopeAsync(sessionId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 收尾不该挡住拆通道;草稿保不保由各会话自己的 Dispose 决定,这里只留痕。
            System.Diagnostics.Trace.WriteLine($"[VelaShell] Remote edit scope close failed: {ex.Message}");
        }
        try
        {
            await sftp.CloseSessionAsync(sessionId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[VelaShell] SFTP close failed: {ex.Message}");
        }
    }
}
