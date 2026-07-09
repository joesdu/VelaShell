using System.Reactive;
using VelaShell.Core.Data;
using ReactiveUI;

namespace VelaShell.Presentation.ViewModels;

public sealed class SidebarViewModel : ReactiveObject
{
    private SessionTreeViewModel? _sessionTree;

    public SidebarViewModel(IRecentConnectionService? recentConnectionService = null)
    {
        RecentConnections = new RecentConnectionsViewModel(recentConnectionService);

        SettingsCommand = ReactiveCommand.Create(() => { });
        NotificationsCommand = ReactiveCommand.Create(() => { });
    }

    public RecentConnectionsViewModel RecentConnections { get; }

    public SessionTreeViewModel? SessionTree
    {
        get => _sessionTree;
        set => this.RaiseAndSetIfChanged(ref _sessionTree, value);
    }

    public ReactiveCommand<Unit, Unit> SettingsCommand { get; }

    public ReactiveCommand<Unit, Unit> NotificationsCommand { get; }
}
