using ReactiveUI;

namespace VelaShell.Presentation;

public sealed class WorkspaceHostViewModel : ReactiveObject
{
    public string Title
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "VelaShell Workspace";
}
