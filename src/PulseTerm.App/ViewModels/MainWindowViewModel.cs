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
    private readonly Func<ITerminalEmulator> _terminalEmulatorFactory;
    private readonly TerminalDockFactory _dockFactory;
    private SidebarViewModel _sidebar;
    private TabBarViewModel _tabBar;
    private StatusBarViewModel _statusBar;
    private TerminalTabViewModel? _activeTerminalTab;

    // SFTP/File management views derived from design
    private FileBrowserViewModel _fileBrowser;
    private FileTransferViewModel _fileTransfer;

    public MainWindowViewModel(
        IConnectionWorkflowService? connectionWorkflowService = null,
        ISshConnectionService? sshConnectionService = null,
        Func<ITerminalEmulator>? terminalEmulatorFactory = null,
        ISettingsService? settingsService = null)
    {
        _connectionWorkflowService = connectionWorkflowService;
        _sshConnectionService = sshConnectionService;
        _settingsService = settingsService;
        _terminalEmulatorFactory = terminalEmulatorFactory ?? (() => new PulseTerminalControl());

        _dockFactory = new TerminalDockFactory();
        Layout = _dockFactory.CreateLayout();
        _dockFactory.InitLayout(Layout);
        _dockFactory.DocumentClosed += OnDocumentClosed;

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

        OpenSettingsCommand = ReactiveCommand.Create(() => { });

        CommandPalette = new CommandPaletteViewModel(BuildPaletteItems);
        OpenCommandPaletteCommand = ReactiveCommand.Create(() => CommandPalette.Open());
    }

    /// <summary>The Ctrl+P / Ctrl+K command palette overlay.</summary>
    public CommandPaletteViewModel CommandPalette { get; }

    public ReactiveCommand<Unit, Unit> OpenCommandPaletteCommand { get; }

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
                invoke: () => _ = ConnectProfileAsync(captured),
                hint: "Enter 连接",
                isSession: true));
        }

        // Global actions.
        items.Add(new CommandPaletteItem("命令", "新建 SSH 连接",
            () => Sidebar.QuickConnectCommand.Execute().Subscribe(), hint: "Ctrl+N"));
        items.Add(new CommandPaletteItem("命令", "打开设置",
            () => OpenSettingsCommand.Execute().Subscribe(), hint: "Ctrl+,"));

        return items;
    }

    public async Task<TerminalTabViewModel> ConnectProfileAsync(SessionProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (_connectionWorkflowService is null || _sshConnectionService is null)
        {
            throw new InvalidOperationException("SSH connection services are not configured.");
        }

        var session = await _connectionWorkflowService.ConnectProfileAsync(profile, cancellationToken);
        var client = _sshConnectionService.GetClient(session.SessionId)
            ?? throw new InvalidOperationException("SSH client was not created for the session.");

        var settings = _settingsService is not null
            ? await _settingsService.GetSettingsAsync()
            : new AppSettings();
        var terminalType = TerminalTypeExtensions.FromTermName(settings.TerminalType);

        var shellStream = client.CreateShellStream(
            terminalName: terminalType.ToTermName(),
            columns: 120,
            rows: 32,
            width: 0,
            height: 0,
            bufferSize: 4096);

        var terminalEmulator = _terminalEmulatorFactory();
        ConfigureTerminal(terminalEmulator, settings, terminalType);
        var terminalTab = new TerminalTabViewModel(terminalEmulator, shellStream)
        {
            SessionId = session.SessionId,
            Title = string.IsNullOrWhiteSpace(profile.Name) ? profile.Host : profile.Name,
            ConnectionStatus = SessionStatus.Connected,
        };

        terminalTab.Start();
        TabBar.AddTab(terminalTab);
        ActiveTerminalTab = terminalTab;
        _dockFactory.AddTerminal(new TerminalDocument(terminalTab));

        Sidebar.RecentConnections.AddRecent(profile);
        UpdateStatusBar(profile, session);

        return terminalTab;
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

    private void UpdateStatusBar(SessionProfile profile, SshSession session)
    {
        StatusBar.Status = Strings.Connected;
        StatusBar.StatusText = Strings.Connected;
        StatusBar.ConnectionInfo = $"SSH • {profile.Username}@{profile.Host}:{profile.Port}";
        StatusBar.TerminalType = "xterm-256color";
        StatusBar.WindowSize = "120×32";
        StatusBar.Encoding = "UTF-8";
        StatusBar.Latency = string.Empty;
        StatusBar.ResetUptime();
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
