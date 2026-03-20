using PulseTerm.Core.Models;
using PulseTerm.Core.Resources;
using ReactiveUI;

namespace PulseTerm.Presentation.ViewModels;

public class TabViewModel : ReactiveObject
{
    private string _title;
    private SessionStatus _connectionStatus;
    private bool _isActive;

    public TabViewModel()
    {
        _title = Strings.NewTab;
        _connectionStatus = SessionStatus.Disconnected;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public string Title
    {
        get => _title;
        set => this.RaiseAndSetIfChanged(ref _title, value);
    }

    public SessionStatus ConnectionStatus
    {
        get => _connectionStatus;
        set => this.RaiseAndSetIfChanged(ref _connectionStatus, value);
    }

    public bool IsActive
    {
        get => _isActive;
        set => this.RaiseAndSetIfChanged(ref _isActive, value);
    }
}
