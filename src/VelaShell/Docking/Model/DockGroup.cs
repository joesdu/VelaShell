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
    /// 主组:新终端默认加入的组,清空后也不折叠(对应原 DocumentDock 的
    /// IsCollapsable=false)。整个工作区有且只有一个。
    /// </summary>
    public bool IsPrimary { get; init; }
}
