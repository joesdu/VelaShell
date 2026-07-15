using ReactiveUI;

namespace VelaShell.Presentation.ViewModels;

/// <summary>可接收快捷命令的已连接终端目标。</summary>
public sealed class QuickCommandTargetViewModel(Guid id, string displayName) : ReactiveObject
{
    /// <summary>对应终端标签的稳定标识。</summary>
    public Guid Id { get; } = id;

    /// <summary>终端目标在多选列表中的显示名称。</summary>
    public string DisplayName
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = displayName;

    /// <summary>是否显式选择该终端作为广播目标。</summary>
    public bool IsSelected
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }
}
