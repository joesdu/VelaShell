using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace VelaShell.Docking.Model;

/// <summary>
/// 分栏(布局树的枝干):按方向排列若干子节点,子节点间由分割条隔开。
/// 对应原 Dock 的 ProportionalDock。子节点的 <see cref="DockNode.Parent" /> 由本类维护。
/// </summary>
public sealed class DockSplit : DockNode
{
    /// <summary>创建指定排列方向的分栏,并开始跟踪子节点集合以维护父子关系。</summary>
    /// <param name="orientation">子节点的排列方向(水平/垂直)。</param>
    public DockSplit(DockOrientation orientation)
    {
        Orientation = orientation;
        Children.CollectionChanged += OnChildrenChanged;
    }

    /// <summary>子节点的排列方向(水平或垂直),构造后不可变。</summary>
    public DockOrientation Orientation { get; }

    /// <summary>本分栏的子节点集合;增删时自动维护子节点的 <see cref="DockNode.Parent" />。</summary>
    public ObservableCollection<DockNode> Children { get; } = [];

    private void OnChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (DockNode node in e.OldItems.OfType<DockNode>())
            {
                if (ReferenceEquals(node.Parent, this))
                {
                    node.Parent = null;
                }
            }
        }
        if (e.NewItems is not null)
        {
            foreach (DockNode node in e.NewItems.OfType<DockNode>())
            {
                node.Parent = this;
            }
        }
    }
}
