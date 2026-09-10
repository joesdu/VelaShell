using System.Collections.ObjectModel;

namespace VelaShell.Docking.Model;

/// <summary>
/// 标签组(布局树的叶子):一条标签条 + 一个内容区。对应原 Dock 的 DocumentDock。
/// </summary>
public sealed class DockGroup : DockNode
{
    /// <summary>本组标签条中承载的全部文档,顺序即标签显示顺序。</summary>
    public ObservableCollection<DockDocument> Documents { get; } = [];

    /// <summary>本组当前显示的文档(标签选中态)。</summary>
    public DockDocument? ActiveDocument
    {
        get;
        set => SetField(ref field, value);
    }

    /// <summary>标签条相对内容区的摆放位置(默认置顶)。</summary>
    public DockTabsPosition TabsPosition
    {
        get;
        set => SetField(ref field, value);
    } = DockTabsPosition.Top;

    /// <summary>
    /// 主组:布局里那个兜底的组 —— 它是唯一一个即使清空也不会被折叠掉的组,
    /// 因而永远有地方接住新文档。整个工作区有且只有一个。
    /// </summary>
    /// <remarks>
    /// 这个身份**可以易主**:主组清空而布局里还有别的组时,
    /// <see cref="DockWorkspace" /> 会把它交给幸存的邻居,自己退场。钉死在最初那个组上的话,
    /// 分屏后把左边一路关完,右半屏会一直挤在右边 —— 因为左边那块空白正是"永不折叠"的主组本人。
    /// </remarks>
    public bool IsPrimary { get; internal set; }
}
