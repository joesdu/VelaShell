using System.Collections.ObjectModel;
using ReactiveUI;
using VelaShell.Core.Models;

namespace VelaShell.Presentation.ViewModels;

/// <summary>一个可折叠的快捷命令分组。</summary>
public sealed class QuickCommandGroupViewModel(QuickCommandGroup model, string displayName)
    : ReactiveObject
{
    /// <summary>底层分组模型。</summary>
    internal QuickCommandGroup Model { get; } =
        model ?? throw new ArgumentNullException(nameof(model));

    /// <summary>分组稳定标识。</summary>
    public Guid Id => Model.Id;

    /// <summary>分组显示名称。</summary>
    public string Name { get; } = displayName;

    /// <summary>分组来源。</summary>
    public QuickCommandGroupKind Kind => Model.Kind;

    /// <summary>是否为不可编辑的系统分组。</summary>
    public bool IsSystem => Kind != QuickCommandGroupKind.User;

    /// <summary>分组全部命令。</summary>
    public ObservableCollection<QuickCommandViewModel> Commands { get; } = [];

    /// <summary>应用搜索后的可见命令。</summary>
    public ObservableCollection<QuickCommandViewModel> FilteredCommands { get; } = [];

    /// <summary>分组是否展开。</summary>
    public bool IsExpanded
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;
}
