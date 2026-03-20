using System;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using PulseTerm.Core.Models;
using PulseTerm.Core.Resources;
using PulseTerm.Core.Ssh;
using PulseTerm.Presentation.ViewModels;
using PulseTerm.Presentation.Services;
using PulseTerm.Terminal;
using ReactiveUI;
using System.Reactive.Linq;

namespace PulseTerm.App.ViewModels;

public class MainWindowViewModel : ReactiveObject
{
    private readonly IConnectionWorkflowService? _connectionWorkflowService;
    private readonly ISshConnectionService? _sshConnectionService;
    private readonly Func<ITerminalEmulator> _terminalEmulatorFactory;
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
        Func<ITerminalEmulator>? terminalEmulatorFactory = null)
    {
        _connectionWorkflowService = connectionWorkflowService;
        _sshConnectionService = sshConnectionService;
        _terminalEmulatorFactory = terminalEmulatorFactory ?? (() => new AvaloniaTerminalEmulator());

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

        var shellStream = client.CreateShellStream(
            terminalName: "xterm-256color",
            columns: 120,
            rows: 32,
            width: 0,
            height: 0,
            bufferSize: 4096);

        var terminalEmulator = _terminalEmulatorFactory();
        var terminalTab = new TerminalTabViewModel(terminalEmulator, shellStream)
        {
            SessionId = session.SessionId,
            Title = string.IsNullOrWhiteSpace(profile.Name) ? profile.Host : profile.Name,
            ConnectionStatus = SessionStatus.Connected,
        };

        terminalTab.Start();
        TabBar.AddTab(terminalTab);
        ActiveTerminalTab = terminalTab;

        Sidebar.RecentConnections.AddRecent(profile);
        UpdateStatusBar(profile, session);

        return terminalTab;
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
