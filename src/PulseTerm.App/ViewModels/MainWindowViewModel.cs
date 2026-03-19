using System.Reactive;
using PulseTerm.Presentation.ViewModels;
using ReactiveUI;

namespace PulseTerm.App.ViewModels;

public class MainWindowViewModel : ReactiveObject
{
    private SidebarViewModel _sidebar;
    private TabBarViewModel _tabBar;
    private StatusBarViewModel _statusBar;

    // SFTP/File management views derived from design
    private FileBrowserViewModel _fileBrowser;
    private FileTransferViewModel _fileTransfer;

    public MainWindowViewModel()
    {
        _sidebar = new SidebarViewModel();
        _tabBar = new TabBarViewModel();
        _statusBar = new StatusBarViewModel();

        _fileBrowser = new FileBrowserViewModel(null, System.Guid.Empty);
        _fileTransfer = new FileTransferViewModel(null);

        OpenSettingsCommand = ReactiveCommand.Create(() => { });
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
