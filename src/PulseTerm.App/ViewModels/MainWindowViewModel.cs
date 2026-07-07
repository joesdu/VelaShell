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
        ISessionRepository? sessionRepository = null)
    {
        _connectionWorkflowService = connectionWorkflowService;
        _sshConnectionService = sshConnectionService;
        _settingsService = settingsService;
        _sessionRepository = sessionRepository;
        _terminalEmulatorFactory = terminalEmulatorFactory ?? (() => new PulseTerminalControl());

        _dockFactory = new TerminalDockFactory();
        Layout = _dockFactory.CreateLayout();
        _dockFactory.InitLayout(Layout);
        _dockFactory.DocumentClosed += OnDocumentClosed;
        _dockFactory.ActiveDockableChanged += OnActiveDockableChanged;
        _dockFactory.FocusedDockableChanged += OnFocusedDockableChanged;

        _sidebar = new SidebarViewModel();
        _tabBar = new TabBarViewModel();
        _statusBar = new StatusBarViewModel();

        _fileBrowser = new FileBrowserViewModel(null, System.Guid.Empty);
        _fileTransfer = new FileTransferViewModel(null);

        _tabBar.WhenAnyValue(tabBar => tabBar.ActiveTab)
            .Subscribe(activeTab =>
            {
                ActiveTerminalTab = activeTab as TerminalTabViewModel;
            });

        // Keep the status bar in sync with the active tab: refresh when the active tab changes,
        // and when that tab's own connection state / latency changes.
        this.WhenAnyValue(x => x.ActiveTerminalTab)
            .Select(tab => tab is null
                ? Observable.Return(Unit.Default)
                : tab.WhenAnyValue(t => t.ConnectionStatus, t => t.Latency).Select(_ => Unit.Default))
            .Switch()
            .Subscribe(_ => UpdateStatusBarForActiveTab());

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
        Commands.Register(new CommandDescriptor("app.settings", "打开设置", "编辑",
            () => OpenSettingsCommand.Execute().Subscribe(), Shortcut: "Ctrl+,", Icon: "Icon.settings"));
        Commands.Register(new CommandDescriptor("app.palette", "命令面板", "搜索",
            () => CommandPalette.Open(), Shortcut: "Ctrl+P", Icon: "Icon.zap"));
    }

    /// <summary>The self-drawn terminal control of the active tab, when it is one.</summary>
    private PulseTerminalControl? ActiveTerminalControl =>
        ActiveTerminalTab?.TerminalEmulator.Control as PulseTerminalControl;

    /// <summary>The Ctrl+P / Ctrl+K command palette overlay.</summary>
    public CommandPaletteViewModel CommandPalette { get; }

    public ReactiveCommand<Unit, Unit> OpenCommandPaletteCommand { get; }

    /// <summary>
    /// Loads persisted connection profiles from disk into the sidebar so saved connections
    /// survive restarts and can be reused without re-entering their details.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_sessionRepository is null)
            return;

        try
        {
            var sessions = await _sessionRepository.GetAllSessionsAsync();
            foreach (var session in sessions)
                Sidebar.RecentConnections.AddRecent(session);
        }
        catch
        {
            // A corrupt or missing store must not prevent the app from starting.
        }
    }

    private IReadOnlyList<CommandPaletteItem> BuildPaletteItems()
    {
        var items = new List<CommandPaletteItem>();

        // Sessions from recent connections — Enter connects.
        foreach (var profile in Sidebar.RecentConnections.Connections)
        {
            var captured = profile;
            var title = string.IsNullOrWhiteSpace(captured.Name) ? captured.Host : captured.Name;
            items.Add(new CommandPaletteItem(
                category: "会话",
                title: title,
                invoke: () => _ = TryConnectProfileAsync(captured),
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
        }
        catch
        {
            // The handshake failed (auth/network/timeout): retract the connecting tab so the
            // caller sees a clean failure instead of a dead tab.
            RemoveTerminalTab(terminalTab, document);
            throw;
        }

        Sidebar.RecentConnections.AddRecent(profile);
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

            tab.SessionId = session.SessionId;
            tab.AttachTransport(shellStream);
            tab.Start();
            tab.ConnectionStatus = SessionStatus.Connected;

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
        emulator.ScrollbackLines = settings.ScrollbackLines;

        if (emulator is PulseTerminalControl control)
        {
            control.TerminalType = terminalType;
            control.SetEncoding(ResolveEncoding(settings.TerminalEncoding));
            if (!string.IsNullOrWhiteSpace(settings.TerminalFont))
                control.FontFamily = new Avalonia.Media.FontFamily(
                    $"{settings.TerminalFont}, JetBrains Mono, Cascadia Mono, Consolas, Microsoft YaHei, monospace");
            if (settings.TerminalFontSize > 0)
                control.FontSize = settings.TerminalFontSize;
        }
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
        tab.Dispose();
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
