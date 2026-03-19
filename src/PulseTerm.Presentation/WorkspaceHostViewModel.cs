using ReactiveUI;

namespace PulseTerm.Presentation;

public sealed class WorkspaceHostViewModel : ReactiveObject
{
    private string _title = "PulseTerm Workspace";

    public string Title
    {
        get => _title;
        set => this.RaiseAndSetIfChanged(ref _title, value);
    }
}
