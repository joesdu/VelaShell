using System.Collections.ObjectModel;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.Presentation.ViewModels;

/// <summary>分组拖放时的目标区域。</summary>
public enum GroupDropPlacement
{
    /// <summary>放到目标同级之前。</summary>
    Before,
    /// <summary>放到目标分组的子级末尾。</summary>
    Inside,
    /// <summary>放到目标同级之后。</summary>
    After,
    /// <summary>放到目标父级的同级末尾。</summary>
    End,
}

/// <summary>分组拖放解析后的父分组与同级插入位置。</summary>
public readonly record struct GroupDropTarget(Guid? ParentGroupId, int Index);

public sealed partial class SessionTreeViewModel
{
    private const int MaxGroupDepth = ServerGroupTree.MaxDepth;

    /// <summary>
    /// 创建一个可以暂时没有连接的空分组。新分组会插入目标父分组的同级末尾并立即落库。
    /// </summary>
    /// <param name="name">分组名称。</param>
    /// <param name="parentGroupId">父分组 Id，null 表示根级。</param>
    /// <returns>新建的分组节点，名称为空或父分组不存在时返回 null。</returns>
    public async Task<SessionTreeNodeViewModel?> CreateGroupAsync(string? name, Guid? parentGroupId = null)
    {
        string trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return null;
        }
        SessionTreeNodeViewModel? parent = parentGroupId is { } id ? FindGroupNode(id) : null;
        if (parentGroupId is not null && parent is null)
        {
            return null;
        }
        int depth = parent is null ? 0 : parent.Depth + 1;
        if (depth > MaxGroupDepth)
        {
            return null;
        }

        List<ServerGroup> stored = await _repository.GetAllGroupsAsync();
        IEnumerable<ServerGroup> siblings = stored.Where(group => group.ParentGroupId == parentGroupId);
        if (siblings.Any(group => string.Equals(group.Name, trimmed, StringComparison.OrdinalIgnoreCase))
            || SiblingGroups(parent).Any(group => string.Equals(group.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
        var group = new ServerGroup
        {
            Name = trimmed,
            ParentGroupId = parentGroupId,
            SortOrder = siblings.Select(static item => item.SortOrder).DefaultIfEmpty(-1).Max() + 1,
        };
        await _repository.SaveGroupAsync(group);

        var node = new SessionTreeNodeViewModel(group.Id, group.Name, true)
        {
            ParentGroup = parent,
            Depth = depth,
            GroupPath = parent is null ? group.Name : $"{parent.GroupPath} > {group.Name}",
            IsExpanded = true,
            GroupColorIndex = SiblingGroups(parent).Count % 3,
        };
        if (parent is null)
        {
            InsertRootGroupNode(node);
        }
        else
        {
            InsertChildGroupNode(parent, node);
            ExpandAncestors(parent);
        }
        RefreshGroupMenuOrder();
        RefreshHasNoSessions();
        SyncRows();
        return node;
    }

    /// <summary>更改分组名称并刷新子树路径，不移动分组或连接。</summary>
    public async Task<bool> RenameGroupAsync(Guid groupId, string? name)
    {
        string trimmed = name?.Trim() ?? string.Empty;
        if (FindGroupNode(groupId) is not { } node || trimmed.Length == 0)
        {
            return false;
        }
        if (node.Name == trimmed)
        {
            return true;
        }
        if (trimmed.Contains('>'))
        {
            return false;
        }

        List<ServerGroup> stored = await _repository.GetAllGroupsAsync();
        ServerGroup? record = stored.Find(group => group.Id == groupId);
        if (record is null || stored.Any(group => group.Id != groupId && group.ParentGroupId == record.ParentGroupId
            && string.Equals(group.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        record.Name = trimmed;
        await _repository.SaveGroupAsync(record);

        node.Name = trimmed;
        UpdateHierarchyMetadata(node, node.ParentGroup, node.Depth,
            node.ParentGroup is { } parent ? $"{parent.GroupPath} > {trimmed}" : trimmed);
        if (IsFiltering)
        {
            ExpandGroupsWithMatches();
        }
        SyncRows();
        return true;
    }

    /// <summary>解析分组拖放目标，支持根级同级、跨父级同级以及放入分组。</summary>
    public GroupDropTarget? ResolveHierarchyGroupDrop(
        Guid movingGroupId,
        SessionTreeNodeViewModel? hovered,
        GroupDropPlacement placement)
    {
        SessionTreeNodeViewModel? moving = FindGroupNode(movingGroupId);
        if (moving is null)
        {
            return null;
        }

        SessionTreeNodeViewModel? parent;
        int index;
        if (hovered is { IsGroup: true } hoveredGroup)
        {
            if (placement == GroupDropPlacement.Inside)
            {
                parent = hoveredGroup;
                index = SiblingGroups(parent).Count;
            }
            else
            {
                parent = hoveredGroup.ParentGroup;
                List<SessionTreeNodeViewModel> siblings = SiblingGroups(parent);
                int hoveredIndex = siblings.IndexOf(hoveredGroup);
                if (hoveredIndex < 0)
                {
                    return null;
                }
                index = placement == GroupDropPlacement.Before ? hoveredIndex : hoveredIndex + 1;
            }
        }
        else
        {
            if (hovered is { IsGroup: false, IsPinned: false, ParentGroup: { } owner })
            {
                parent = owner.ParentGroup;
                List<SessionTreeNodeViewModel> siblings = SiblingGroups(parent);
                int ownerIndex = siblings.IndexOf(owner);
                index = ownerIndex < 0 ? siblings.Count : ownerIndex + 1;
            }
            else
            {
                // 根级会话与置顶会话在视觉上不代表某个父分组,落点是根级末尾。
                parent = null;
                index = SiblingGroups(null).Count;
            }
        }

        if (parent is not null && (parent == moving || IsGroupDescendant(parent, moving)))
        {
            return null;
        }
        int subtreeHeight = MaxSubtreeDepth(moving);
        if ((parent?.Depth ?? -1) + 1 + subtreeHeight > MaxGroupDepth)
        {
            return null;
        }
        return new(parent?.Id, index);
    }

    /// <summary>把分组移动到解析出的父分组与同级位置，并保存父引用与两侧同级排序号。</summary>
    public async Task<bool> MoveGroupToAsync(Guid groupId, Guid? targetParentId, int targetIndex)
    {
        SessionTreeNodeViewModel? moving = FindGroupNode(groupId);
        SessionTreeNodeViewModel? targetParent = targetParentId is { } id ? FindGroupNode(id) : null;
        if (moving is null || (targetParentId is not null && targetParent is null))
        {
            return false;
        }
        if (targetParent is not null && (targetParent == moving || IsGroupDescendant(targetParent, moving)))
        {
            return false;
        }
        int subtreeHeight = MaxSubtreeDepth(moving);
        if ((targetParent?.Depth ?? -1) + 1 + subtreeHeight > MaxGroupDepth)
        {
            return false;
        }

        SessionTreeNodeViewModel? oldParent = moving.ParentGroup;
        List<SessionTreeNodeViewModel> oldSiblings = SiblingGroups(oldParent);
        List<SessionTreeNodeViewModel> targetSiblings = SiblingGroups(targetParent);
        int oldIndex = oldSiblings.IndexOf(moving);
        if (oldIndex < 0)
        {
            return false;
        }
        targetIndex = Math.Clamp(targetIndex, 0, targetSiblings.Count);
        if (oldParent == targetParent && oldIndex < targetIndex)
        {
            targetIndex--;
        }
        if (oldParent == targetParent && oldIndex == targetIndex)
        {
            return false;
        }

        DetachGroupNode(moving, oldParent);
        targetSiblings = SiblingGroups(targetParent);
        targetIndex = Math.Clamp(targetIndex, 0, targetSiblings.Count);
        AttachGroupNode(moving, targetParent, targetIndex);
        UpdateHierarchyMetadata(moving, targetParent, targetParent is null ? 0 : targetParent.Depth + 1,
            targetParent is null ? moving.Name : $"{targetParent.GroupPath} > {moving.Name}");
        ExpandAncestors(targetParent);
        RefreshGroupMenuOrder();
        SyncRows();

        List<ServerGroup> stored = await _repository.GetAllGroupsAsync();
        var byId = stored.ToDictionary(static group => group.Id);
        foreach (SessionTreeNodeViewModel sibling in oldSiblings.Concat(SiblingGroups(targetParent)).Distinct())
        {
            if (!byId.TryGetValue(sibling.Id, out ServerGroup? record))
            {
                continue;
            }
            record.ParentGroupId = sibling.ParentGroup?.Id;
            record.SortOrder = SiblingGroups(sibling.ParentGroup).IndexOf(sibling);
            await _repository.SaveGroupAsync(record);
        }
        return true;
    }

    /// <summary>判断分组落点是否改变父级或同级位置。</summary>
    public bool GroupDropWouldMove(Guid groupId, GroupDropTarget target)
    {
        SessionTreeNodeViewModel? moving = FindGroupNode(groupId);
        SessionTreeNodeViewModel? parent = target.ParentGroupId is { } id ? FindGroupNode(id) : null;
        if (moving is null || target.ParentGroupId is not null && parent is null)
        {
            return false;
        }
        if (moving.ParentGroup != parent)
        {
            return true;
        }
        List<SessionTreeNodeViewModel> siblings = SiblingGroups(parent);
        int from = siblings.IndexOf(moving);
        int index = Math.Clamp(target.Index, 0, siblings.Count);
        if (from < index)
        {
            index--;
        }
        return from >= 0 && from != index;
    }

    /// <summary>分组拖放提示所用的完整落点描述。</summary>
    public string DescribeHierarchyDrop(GroupDropTarget target, GroupDropPlacement placement)
    {
        SessionTreeNodeViewModel? parent = target.ParentGroupId is { } id ? FindGroupNode(id) : null;
        if (placement == GroupDropPlacement.Inside && parent is not null)
        {
            return Strings.Format("Tree_DragGroupInside", parent.GroupPath);
        }
        List<SessionTreeNodeViewModel> siblings = SiblingGroups(parent);
        return target.Index < siblings.Count
            ? Strings.Format("Tree_DragGroupBefore", siblings[target.Index].GroupPath)
            : Strings.Get("Tree_DragGroupToEnd");
    }

    private List<SessionTreeNodeViewModel> SiblingGroups(SessionTreeNodeViewModel? parent) =>
        [.. (parent?.Children ?? Nodes).Where(static node => node.IsGroup)];

    private static int MaxSubtreeDepth(SessionTreeNodeViewModel group)
    {
        int max = 0;
        foreach (SessionTreeNodeViewModel child in group.Children.Where(static node => node.IsGroup))
        {
            max = Math.Max(max, 1 + MaxSubtreeDepth(child));
        }
        return max;
    }

    private static bool IsGroupDescendant(SessionTreeNodeViewModel candidate, SessionTreeNodeViewModel ancestor)
    {
        for (SessionTreeNodeViewModel? current = candidate.ParentGroup; current is not null; current = current.ParentGroup)
        {
            if (current == ancestor)
            {
                return true;
            }
        }
        return false;
    }

    private void DetachGroupNode(SessionTreeNodeViewModel group, SessionTreeNodeViewModel? parent)
    {
        if (parent is null)
        {
            Nodes.Remove(group);
        }
        else
        {
            parent.Children.Remove(group);
        }
    }

    private void AttachGroupNode(SessionTreeNodeViewModel group, SessionTreeNodeViewModel? parent, int groupIndex)
    {
        group.ParentGroup = parent;
        ObservableCollection<SessionTreeNodeViewModel> children = parent?.Children ?? Nodes;
        int childIndex = children.TakeWhile(static node => node.IsGroup).Count();
        List<SessionTreeNodeViewModel> groups = SiblingGroups(parent);
        if (groupIndex < groups.Count)
        {
            childIndex = children.IndexOf(groups[groupIndex]);
        }
        children.Insert(childIndex, group);
    }

    private void InsertRootGroupNode(SessionTreeNodeViewModel group) => AttachGroupNode(group, null, SiblingGroups(null).Count);

    private void InsertChildGroupNode(SessionTreeNodeViewModel parent, SessionTreeNodeViewModel group) =>
        AttachGroupNode(group, parent, SiblingGroups(parent).Count);

    private static void UpdateHierarchyMetadata(
        SessionTreeNodeViewModel group,
        SessionTreeNodeViewModel? parent,
        int depth,
        string path)
    {
        group.ParentGroup = parent;
        group.Depth = depth;
        group.GroupPath = path;
        foreach (SessionTreeNodeViewModel child in group.Children)
        {
            if (child.IsGroup)
            {
                UpdateHierarchyMetadata(child, group, depth + 1, $"{path} > {child.Name}");
            }
            else
            {
                child.ParentGroup = group;
                child.Depth = depth + 1;
                child.GroupPath = path;
                child.IsRootLevel = false;
            }
        }
    }

    private static void ExpandAncestors(SessionTreeNodeViewModel? group)
    {
        for (SessionTreeNodeViewModel? current = group; current is not null; current = current.ParentGroup)
        {
            current.IsExpanded = true;
        }
    }
}
