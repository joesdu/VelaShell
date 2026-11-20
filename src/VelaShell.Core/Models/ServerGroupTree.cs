namespace VelaShell.Core.Models;

/// <summary>分组快照的只读索引。无效父引用在视图中落根，原始记录保留供导入与同步校验。</summary>
public sealed class ServerGroupTree
{
    /// <summary>根组深度为零。</summary>
    public const int MaxDepth = 64;

    private readonly Dictionary<Guid, ServerGroup> _groups = [];
    private readonly Dictionary<Guid, Guid?> _parents = [];
    private readonly ILookup<Guid?, ServerGroup> _children;
    private readonly List<string> _errors = [];

    /// <summary>按 ID 建立索引，并记录失效的父引用。</summary>
    public ServerGroupTree(IEnumerable<ServerGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        foreach (ServerGroup group in groups)
        {
            if (!_groups.TryAdd(group.Id, group))
            {
                _errors.Add($"Duplicate group ID {group.Id}.");
            }
        }
        foreach (ServerGroup group in _groups.Values)
        {
            Guid? parent = group.ParentGroupId;
            if (parent is { } id && !ValidChain(group.Id, id))
            {
                parent = null;
            }
            _parents.Add(group.Id, parent);
        }
        _children = _groups.Values.OrderBy(g => g.SortOrder).ThenBy(g => g.Id).ToLookup(g => _parents[g.Id]);
        OrderedGroups = [.. _children[null].SelectMany(Walk)];
    }

    /// <summary>父组优先、同级按排序号排列。</summary>
    public IReadOnlyList<ServerGroup> OrderedGroups { get; }

    /// <summary>查找原始记录。</summary>
    public ServerGroup? Find(Guid id) => _groups.GetValueOrDefault(id);
    /// <summary>获取安全视图中的直属父组。</summary>
    public Guid? ParentOf(Guid id) => _parents.GetValueOrDefault(id);
    /// <summary>返回根层或指定组的直属子组。</summary>
    public IReadOnlyList<ServerGroup> ChildrenOf(Guid? parentId) => [.. _children[parentId]];
    /// <summary>返回构造快照时发现的结构问题。</summary>
    public IReadOnlyList<string> Validate() => _errors;

    /// <summary>返回包含自身的祖先链，从根开始。</summary>
    public IReadOnlyList<ServerGroup> AncestorsOf(Guid id)
    {
        var chain = new Stack<ServerGroup>();
        for (Guid? current = id; current is { } key && _groups.TryGetValue(key, out ServerGroup? group); current = _parents[key])
        {
            chain.Push(group);
        }
        return [.. chain];
    }

    /// <summary>以祖先链生成完整显示路径。</summary>
    public string PathOf(Guid id, string separator = " > ") => string.Join(separator, AncestorsOf(id).Select(g => g.Name));
    /// <summary>按父组优先的顺序返回含自身的子树。</summary>
    public IReadOnlyList<ServerGroup> SubtreeOf(Guid id) => Find(id) is { } group ? [.. Walk(group)] : [];
    /// <summary>判断一个组是否是另一个组的严格后代。</summary>
    public bool IsDescendantOf(Guid candidateId, Guid ancestorId) => candidateId != ancestorId && AncestorsOf(candidateId).Any(g => g.Id == ancestorId);

    /// <summary>检查目标父组、环路与移动后整棵子树的深度。</summary>
    public bool CanMoveUnder(Guid groupId, Guid? targetParentId)
    {
        if (!_groups.ContainsKey(groupId) || targetParentId == groupId)
        {
            return false;
        }
        if (targetParentId is not { } parent)
        {
            return true;
        }
        if (!_groups.ContainsKey(parent) || IsDescendantOf(parent, groupId))
        {
            return false;
        }
        int height = SubtreeOf(groupId).Max(g => AncestorsOf(g.Id).Count) - AncestorsOf(groupId).Count;
        return AncestorsOf(parent).Count + height <= MaxDepth;
    }

    private IEnumerable<ServerGroup> Walk(ServerGroup group)
    {
        yield return group;
        foreach (ServerGroup descendant in _children[group.Id].SelectMany(Walk))
        {
            yield return descendant;
        }
    }

    private bool ValidChain(Guid id, Guid parentId)
    {
        var seen = new HashSet<Guid> { id };
        for (Guid? next = parentId; next is { } key;)
        {
            if (!_groups.TryGetValue(key, out ServerGroup? parent))
            {
                _errors.Add($"Unknown parent {key} for group {id}.");
                return false;
            }
            if (!seen.Add(key) || seen.Count > MaxDepth + 1)
            {
                _errors.Add($"Cyclic or excessively deep parent chain for group {id}.");
                return false;
            }
            next = parent.ParentGroupId;
        }
        return true;
    }
}
