using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using PulseTerm.Core.Models;
using ReactiveUI;

namespace PulseTerm.Presentation.ViewModels;

public sealed class RecentConnectionsViewModel : ReactiveObject
{
    private const int MaxRecentConnections = 10;
    private SessionProfile? _selectedConnection;

    public RecentConnectionsViewModel()
    {
        Connections = new ObservableCollection<SessionProfile>();

        var hasSelection = this.WhenAnyValue(x => x.SelectedConnection)
            .Select(selection => selection is not null);

        ReconnectCommand = ReactiveCommand.Create(() => { }, hasSelection);
        ClearCommand = ReactiveCommand.Create(ClearAll);
    }

    public ObservableCollection<SessionProfile> Connections { get; }

    public SessionProfile? SelectedConnection
    {
        get => _selectedConnection;
        set => this.RaiseAndSetIfChanged(ref _selectedConnection, value);
    }

    public ReactiveCommand<Unit, Unit> ReconnectCommand { get; }

    public ReactiveCommand<Unit, Unit> ClearCommand { get; }

    public void AddRecent(SessionProfile profile)
    {
        var existing = Connections.FirstOrDefault(connection => connection.Id == profile.Id);
        if (existing is not null)
        {
            Connections.Remove(existing);
        }

        Connections.Insert(0, profile);

        while (Connections.Count > MaxRecentConnections)
        {
            Connections.RemoveAt(Connections.Count - 1);
        }
    }

    private void ClearAll()
    {
        Connections.Clear();
        SelectedConnection = null;
    }
}
