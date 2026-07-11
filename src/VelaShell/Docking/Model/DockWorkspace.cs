namespace VelaShell.Docking.Model;

/// <summary>
/// 终端工作区布局:一棵 分栏/标签组 树 + 全局激活文档,并提供全部结构操作
/// (docs/dock-replacement-plan.md §2.3)。取代原 TerminalDockFactory + Dock.Model:
/// 新文档进主组;用户关闭走 <see cref="CloseDocument" />(触发 <see cref="DocumentClosed" />,
/// 下游据此断 SSH/SFTP/日志);程序撤除走 <see cref="RemoveDocument" />(静默)。
/// 空的非主组自动折叠,单子分栏自动提升。
/// </summary>
public sealed class DockWorkspace : DockElement
{
    private DockNode _root;
    private DockDocument? _activeDocument;

    public DockWorkspace()
    {
        PrimaryGroup = new DockGroup { IsPrimary = true };
        _root = PrimaryGroup;
    }

    /// <summary>布局树根:单组时就是主组,拆分后为最外层分栏。</summary>
    public DockNode Root
    {
        get => _root;
        private set => SetField(ref _root, value);
    }

    /// <summary>新文档默认加入的组;清空后也不折叠。</summary>
    public DockGroup PrimaryGroup { get; }

    /// <summary>全局激活文档(最后交互的组的选中标签),驱动 ActiveTerminalTab/状态栏联动。</summary>
    public DockDocument? ActiveDocument
    {
        get => _activeDocument;
        private set
        {
            if (SetField(ref _activeDocument, value))
            {
                ActiveDocumentChanged?.Invoke(value);
            }
        }
    }

    /// <summary>激活文档变化(合并了原 Dock 的 ActiveDockableChanged + FocusedDockableChanged)。</summary>
    public event Action<DockDocument?>? ActiveDocumentChanged;

    /// <summary>用户语义的“关闭标签”完成后触发;程序性 <see cref="RemoveDocument" /> 不触发。</summary>
    public event Action<DockDocument>? DocumentClosed;

    /// <summary>
    /// 文档离开工作区时触发(无论用户关闭还是程序撤除;组间移动不算),
    /// 供控件层清理视图缓存。
    /// </summary>
    public event Action<DockDocument>? DocumentRemoved;

    // ---- 查询 ----

    public IEnumerable<DockGroup> AllGroups() => EnumerateGroups(Root);

    public IEnumerable<DockDocument> AllDocuments() => AllGroups().SelectMany(group => group.Documents);

    public DockGroup? FindGroup(DockDocument document) =>
        AllGroups().FirstOrDefault(group => group.Documents.Contains(document));

    private static IEnumerable<DockGroup> EnumerateGroups(DockNode node)
    {
        switch (node)
        {
            case DockGroup group:
                yield return group;
                break;
            case DockSplit split:
                foreach (DockNode child in split.Children.ToArray())
                {
                    foreach (DockGroup group in EnumerateGroups(child))
                    {
                        yield return group;
                    }
                }
                break;
        }
    }

    // ---- 增删与激活 ----

    /// <summary>加入主组并激活(与原 Dock 行为一致:新终端总进第一组)。</summary>
    public void AddDocument(DockDocument document)
    {
        PrimaryGroup.Documents.Add(document);
        ActivateDocument(document);
    }

    /// <summary>激活文档:选中其标签并设为全局激活。</summary>
    public void ActivateDocument(DockDocument document)
    {
        if (FindGroup(document) is not { } group)
        {
            return;
        }
        group.ActiveDocument = document;
        ActiveDocument = document;
    }

    /// <summary>
    /// 静默移除(撤掉连接失败的标签等程序行为),不触发 <see cref="DocumentClosed" />。
    /// </summary>
    public void RemoveDocument(DockDocument document)
    {
        if (FindGroup(document) is not { } group)
        {
            return;
        }
        int index = group.Documents.IndexOf(document);
        group.Documents.RemoveAt(index);
        if (ReferenceEquals(group.ActiveDocument, document))
        {
            group.ActiveDocument = group.Documents.Count > 0
                                       ? group.Documents[Math.Min(index, group.Documents.Count - 1)]
                                       : null;
        }
        CollapseIfEmpty(group);
        if (ReferenceEquals(ActiveDocument, document))
        {
            // 组仍在树上则接管其新选中标签;组已折叠则退回仍有文档的第一个组。
            ActiveDocument = group.ActiveDocument
                             ?? AllGroups().FirstOrDefault(g => g.ActiveDocument is not null)?.ActiveDocument;
        }
        DocumentRemoved?.Invoke(document);
    }

    /// <summary>用户语义的关闭:尊重 CanClose,移除后触发 <see cref="DocumentClosed" />。</summary>
    public void CloseDocument(DockDocument document)
    {
        if (!document.CanClose || FindGroup(document) is null)
        {
            return;
        }
        RemoveDocument(document);
        DocumentClosed?.Invoke(document);
    }

    /// <summary>关闭同组内除该文档以外的所有标签(组内语义与原 Dock 一致)。</summary>
    public void CloseOtherDocuments(DockDocument document)
    {
        if (FindGroup(document) is not { } group)
        {
            return;
        }
        foreach (DockDocument other in group.Documents.Where(d => !ReferenceEquals(d, document)).ToArray())
        {
            CloseDocument(other);
        }
    }

    /// <summary>关闭该文档所在组的所有标签(含自身)。</summary>
    public void CloseAllDocuments(DockDocument document)
    {
        if (FindGroup(document) is not { } group)
        {
            return;
        }
        foreach (DockDocument doc in group.Documents.ToArray())
        {
            CloseDocument(doc);
        }
    }

    public void CloseLeftDocuments(DockDocument document) => CloseToSide(document, left: true);

    public void CloseRightDocuments(DockDocument document) => CloseToSide(document, left: false);

    private void CloseToSide(DockDocument document, bool left)
    {
        if (FindGroup(document) is not { } group)
        {
            return;
        }
        int index = group.Documents.IndexOf(document);
        DockDocument[] targets = left
                                     ? group.Documents.Take(index).ToArray()
                                     : group.Documents.Skip(index + 1).ToArray();
        foreach (DockDocument doc in targets)
        {
            CloseDocument(doc);
        }
    }

    // ---- 拆分与停靠 ----

    /// <summary>
    /// 右键菜单“水平/垂直拆分”:把文档移入紧邻其所属组新建的组
    /// (水平 = 新组在右,垂直 = 新组在下,各占一半)。
    /// 组内唯一文档且组非主组时为无效操作(新组建成的瞬间旧组即折叠,净效果为零)。
    /// </summary>
    public void SplitDocument(DockDocument document, DockOrientation orientation)
    {
        if (FindGroup(document) is not { } group)
        {
            return;
        }
        if (group.Documents.Count == 1 && !group.IsPrimary)
        {
            return;
        }
        DockGroup newGroup = DetachToNewGroup(document, group);
        InsertNeighbor(group, newGroup, orientation, after: true);
        ActivateDocument(document);
    }

    /// <summary>
    /// 拖放停靠:Center = 并入目标组(index 为插入位,-1 为末尾);
    /// 四边 = 在目标组对应侧拆分出新组。
    /// </summary>
    public void DockTo(DockDocument document, DockGroup target, DockPosition position, int index = -1)
    {
        if (FindGroup(document) is not { } source)
        {
            return;
        }
        if (position == DockPosition.Center)
        {
            MoveToGroup(document, source, target, index);
            return;
        }

        // 拖到自身组的边缘且组里只有它自己:拆出去旧组立即折叠,净效果为零,直接忽略。
        if (ReferenceEquals(source, target) && source.Documents.Count == 1 && !source.IsPrimary)
        {
            return;
        }
        DockOrientation orientation = position is DockPosition.Left or DockPosition.Right
                                          ? DockOrientation.Horizontal
                                          : DockOrientation.Vertical;
        bool after = position is DockPosition.Right or DockPosition.Bottom;
        DockGroup newGroup = DetachToNewGroup(document, source);
        // source 若因清空被折叠,锚点可能已不在树上;此时锚定到目标组曾经的位置没有意义,
        // 直接锚定主组(仅发生在 source == target 且为主组之外的情况,前面已挡掉)。
        DockGroup anchor = FindNode(target) ? target : PrimaryGroup;
        InsertNeighbor(anchor, newGroup, orientation, after);
        ActivateDocument(document);
    }

    /// <summary>组内拖拽重排。</summary>
    public void MoveDocument(DockDocument document, int newIndex)
    {
        if (FindGroup(document) is not { } group)
        {
            return;
        }
        int oldIndex = group.Documents.IndexOf(document);
        newIndex = Math.Clamp(newIndex, 0, group.Documents.Count - 1);
        if (oldIndex != newIndex)
        {
            group.Documents.Move(oldIndex, newIndex);
        }
    }

    private void MoveToGroup(DockDocument document, DockGroup source, DockGroup target, int index)
    {
        if (ReferenceEquals(source, target))
        {
            if (index >= 0)
            {
                MoveDocument(document, Math.Min(index, source.Documents.Count - 1));
            }
            ActivateDocument(document);
            return;
        }
        source.Documents.Remove(document);
        if (ReferenceEquals(source.ActiveDocument, document))
        {
            source.ActiveDocument = source.Documents.FirstOrDefault();
        }
        target.Documents.Insert(index < 0 || index > target.Documents.Count ? target.Documents.Count : index, document);
        CollapseIfEmpty(source);
        ActivateDocument(document);
    }

    /// <summary>把文档从原组摘出放入一个新组(原组按需折叠),返回新组。</summary>
    private DockGroup DetachToNewGroup(DockDocument document, DockGroup source)
    {
        source.Documents.Remove(document);
        if (ReferenceEquals(source.ActiveDocument, document))
        {
            source.ActiveDocument = source.Documents.FirstOrDefault();
        }
        var newGroup = new DockGroup();
        newGroup.Documents.Add(document);
        newGroup.ActiveDocument = document;
        CollapseIfEmpty(source);
        return newGroup;
    }

    /// <summary>
    /// 把 newGroup 插到 anchor 的旁边:父分栏方向相同则同级插入(平分 anchor 的比例),
    /// 否则用新分栏替换 anchor 再装入两者(各占一半)。
    /// </summary>
    private void InsertNeighbor(DockGroup anchor, DockGroup newGroup, DockOrientation orientation, bool after)
    {
        if (anchor.Parent is { } parent && parent.Orientation == orientation)
        {
            int i = parent.Children.IndexOf(anchor);
            if (!double.IsNaN(anchor.Proportion))
            {
                double half = anchor.Proportion / 2;
                anchor.Proportion = half;
                newGroup.Proportion = half;
            }
            parent.Children.Insert(after ? i + 1 : i, newGroup);
            return;
        }
        var split = new DockSplit(orientation) { Proportion = anchor.Proportion };
        ReplaceNode(anchor, split);
        anchor.Proportion = double.NaN;
        split.Children.Add(after ? anchor : newGroup);
        split.Children.Add(after ? newGroup : anchor);
    }

    // ---- 树维护 ----

    private bool FindNode(DockNode node)
    {
        DockNode current = node;
        while (current.Parent is { } parent)
        {
            current = parent;
        }
        return ReferenceEquals(current, Root);
    }

    private void ReplaceNode(DockNode oldNode, DockNode newNode)
    {
        if (ReferenceEquals(Root, oldNode))
        {
            Root = newNode;
            newNode.Parent = null;
            return;
        }
        DockSplit parent = oldNode.Parent!;
        int index = parent.Children.IndexOf(oldNode);
        parent.Children[index] = newNode;
    }

    private void CollapseIfEmpty(DockGroup group)
    {
        if (group.IsPrimary || group.Documents.Count > 0 || group.Parent is not { } parent)
        {
            return;
        }
        parent.Children.Remove(group);
        HoistIfSingle(parent);
    }

    private void HoistIfSingle(DockSplit split)
    {
        if (split.Children.Count != 1)
        {
            return;
        }
        DockNode child = split.Children[0];
        split.Children.RemoveAt(0);
        child.Proportion = split.Proportion;
        ReplaceNode(split, child);
    }
}
