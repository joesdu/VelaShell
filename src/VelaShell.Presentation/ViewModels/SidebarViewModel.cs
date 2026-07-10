using System.Reactive;
using ReactiveUI;
using VelaShell.Core.Data;

namespace VelaShell.Presentation.ViewModels;

public sealed class SidebarViewModel(IRecentConnectionService? recentConnectionService = null) : ReactiveObject
{
    public RecentConnectionsViewModel RecentConnections { get; } = new(recentConnectionService);

    public SessionTreeViewModel? SessionTree
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ReactiveCommand<Unit, Unit> SettingsCommand { get; } = ReactiveCommand.Create(() => { });

    public ReactiveCommand<Unit, Unit> NotificationsCommand { get; } = ReactiveCommand.Create(() => { });
}
