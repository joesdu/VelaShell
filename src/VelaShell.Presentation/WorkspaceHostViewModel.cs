using ReactiveUI;

namespace VelaShell.Presentation;

public sealed class WorkspaceHostViewModel : ReactiveObject
{
    private string _title = "VelaShell Workspace";

    public string Title
    {
        get => _title;
        set => this.RaiseAndSetIfChanged(ref _title, value);
    }
}
