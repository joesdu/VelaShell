using VelaShell.Core.Credentials;
using VelaShell.Core.Models;

namespace VelaShell.Core.Import;

/// <summary>导出范围。分组范围会保留整个子树及其中的空分组,全部范围会保留所有分组。</summary>
/// <param name="ProfileIds">用户选中的连接。分组或全部范围会由服务根据范围重新计算。</param>
/// <param name="GroupId">要导出的分组及其后代;null 表示没有分组范围。</param>
/// <param name="ExportAll">是否导出全部连接与分组。</param>
public sealed record SessionExportScope(
    IReadOnlyCollection<Guid> ProfileIds,
    Guid? GroupId = null,
    bool ExportAll = false);

/// <summary>一次导出实际要写的连接:用户选的那些,加上它们经跳板链引用到的连接。</summary>
/// <param name="Sessions">要写的连接,按资源管理器里的顺序(分组顺序 → 名称)排好。</param>
/// <param name="RequestedCount">其中用户直接选的条数。</param>
/// <param name="DependencyCount">因为被当作跳板引用而自动带上的条数。</param>
/// <param name="GroupIds">要写出的分组 Id,包括空分组与依赖连接的祖先。</param>
public sealed record SessionExportSelection(
    IReadOnlyList<SessionProfile> Sessions,
    int RequestedCount,
    int DependencyCount,
    IReadOnlySet<Guid> GroupIds);

/// <summary>从本机数据组装一份导出(#571)。纯函数,不碰仓储 —— 读数据与写文件由调用方负责。</summary>
public static class SessionArchiveBuilder
{
    /// <summary>
    /// 定下要导出哪些连接:用户选的,加上它们的跳板(逐级往上,直到链的尽头)。
    /// </summary>
    /// <remarks>
    /// 跳板自动带上,是因为只导出「经堡垒机连的那批机器」而不导出堡垒机本身,导到另一台电脑上就全是直连 ——
    /// 而直连恰恰是连不上的那种。另一台电脑上若已经有这台堡垒机,导入时它按重复处理,引用会落到已有的那一条上。
    /// </remarks>
    /// <param name="requested">用户选中的连接 Id(不存在的忽略)。</param>
    /// <param name="all">本机全部连接。</param>
    /// <param name="groups">本机全部分组(用于排序)。</param>
    /// <returns>要导出的连接与计数。</returns>
    public static SessionExportSelection Select(
        IReadOnlyCollection<Guid> requested,
        IReadOnlyList<SessionProfile> all,
        IReadOnlyList<ServerGroup> groups) => Select(new SessionExportScope(requested), all, groups);

    /// <summary>按显式范围选择连接,并计算分组子树、祖先与跳板依赖。</summary>
    public static SessionExportSelection Select(
        SessionExportScope scope,
        IReadOnlyList<SessionProfile> all,
        IReadOnlyList<ServerGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(groups);
        var byId = all.GroupBy(static p => p.Id).ToDictionary(static g => g.Key, static g => g.First());
        var groupTree = new ServerGroupTree(groups);
        HashSet<Guid> groupIds = scope.ExportAll ? groups.Select(static g => g.Id).ToHashSet()
            : scope.GroupId is { } selected ? [.. groupTree.SubtreeOf(selected).Select(static g => g.Id)] : [];
        HashSet<Guid> requested = scope.ExportAll ? [.. byId.Keys]
            : scope.GroupId is not null ? [.. all.Where(p => p.GroupId is { } id && groupIds.Contains(id)).Select(p => p.Id)]
            : [.. scope.ProfileIds.Where(byId.ContainsKey)];
        AddGroupAndAncestors(groupIds, scope.GroupId, groupTree);
        int requestedCount = requested.Count;
        var included = new HashSet<Guid>(requested);
        var pending = new Queue<Guid>(included);
        while (pending.TryDequeue(out Guid id))
        {
            if (byId[id].JumpHostProfileId is { } jump && byId.ContainsKey(jump) && included.Add(jump))
            {
                pending.Enqueue(jump);
            }
        }

        foreach (SessionProfile profile in included.Select(id => byId[id]))
        {
            AddGroupAndAncestors(groupIds, profile.GroupId, groupTree);
        }

        IReadOnlyList<SessionProfile> ordered = Order(included.Select(id => byId[id]), groups);
        return new SessionExportSelection(
            ordered,
            requestedCount,
            included.Count - requestedCount,
            groupIds);
    }

    private static void AddGroupAndAncestors(HashSet<Guid> target, Guid? groupId, ServerGroupTree tree)
    {
        if (groupId is not { } id)
        {
            return;
        }
        foreach (ServerGroup group in tree.AncestorsOf(id))
        {
            _ = target.Add(group.Id);
        }
    }

    /// <summary>按资源管理器的顺序排:分组按树的父子顺序,未分组的在最后;组内按名称。</summary>
    /// <param name="sessions">要排的连接。</param>
    /// <param name="groups">本机全部分组。</param>
    /// <returns>排好的列表。</returns>
    public static IReadOnlyList<SessionProfile> Order(IEnumerable<SessionProfile> sessions, IReadOnlyList<ServerGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(groups);
        var rank = new Dictionary<Guid, int>();
        int index = 0;
        foreach (ServerGroup group in new ServerGroupTree(groups).OrderedGroups)
        {
            _ = rank.TryAdd(group.Id, index++);
        }
        return
        [
            .. sessions
                .OrderBy(s => s.GroupId is { } id && rank.TryGetValue(id, out int r) ? r : int.MaxValue)
                .ThenBy(static s => s.Name, StringComparer.OrdinalIgnoreCase)
        ];
    }

    /// <summary>
    /// 组装导出内容:选中的连接(此时仍带着明文机密,写文件时由 <see cref="SessionArchiveJson.Serialize" /> 剥掉或加密)、
    /// 它们所在的分组、引用到的共享凭据与隧道。
    /// </summary>
    /// <param name="selection">由 <see cref="SessionExportSelection" /> 定下的连接。</param>
    /// <param name="groups">本机全部分组。</param>
    /// <param name="credentials">本机全部共享凭据。</param>
    /// <param name="tunnels">各连接的隧道(键为连接 Id;没有的可以不在字典里)。</param>
    /// <param name="exportedAtUtc">导出时间。</param>
    /// <param name="application">程序名与版本。</param>
    /// <returns>导出内容。</returns>
    public static SessionArchive Build(
        SessionExportSelection selection,
        IReadOnlyList<ServerGroup> groups,
        IReadOnlyList<SharedCredential> credentials,
        IReadOnlyDictionary<Guid, List<TunnelConfig>> tunnels,
        DateTime exportedAtUtc,
        string? application)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(tunnels);
        var groupTree = new ServerGroupTree(groups);
        var usedGroups = selection.GroupIds.ToHashSet();
        var usedCredentials = selection.Sessions
            .Select(static s => s.CredentialSource is { } reference && reference.TryGetSharedId(out Guid id) ? id : (Guid?)null)
            .OfType<Guid>()
            .ToHashSet();
        var archive = new SessionArchive
        {
            ExportedAtUtc = exportedAtUtc,
            Application = application,
            Groups =
            [
                .. groupTree.OrderedGroups
                    .Where(g => usedGroups.Contains(g.Id))
                    .Select(g => new SessionArchiveGroup
                    {
                        Id = g.Id,
                        Name = g.Name,
                        Icon = g.Icon,
                        SortOrder = g.SortOrder,
                        ParentGroupId = groupTree.ParentOf(g.Id)
                    })
            ],
            Sessions = [.. selection.Sessions.Select(static s => s.Clone())]
        };
        List<SharedCredential> referenced =
        [
            .. credentials
                .Where(c => usedCredentials.Contains(c.Id))
                .OrderBy(static c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(static c => c.Clone())
        ];
        archive.SharedCredentials = referenced.Count > 0 ? referenced : null;
        var exportedTunnels = new Dictionary<Guid, List<TunnelConfig>>();
        foreach (SessionProfile session in selection.Sessions)
        {
            if (tunnels.TryGetValue(session.Id, out List<TunnelConfig>? list) && list is { Count: > 0 })
            {
                exportedTunnels[session.Id] = [.. list];
            }
        }
        archive.Tunnels = exportedTunnels.Count > 0 ? exportedTunnels : null;
        return archive;
    }
}
