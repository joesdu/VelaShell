using ReactiveUI;

namespace VelaShell.Presentation;

/// <summary>工作区宿主视图模型,承载整个工作区窗口的顶层状态(如标题)。</summary>
public sealed class WorkspaceHostViewModel : ReactiveObject
{
    /// <summary>工作区窗口显示的标题文本。</summary>
    public string Title
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "VelaShell Workspace";
}
