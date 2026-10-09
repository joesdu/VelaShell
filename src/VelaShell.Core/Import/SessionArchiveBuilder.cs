using VelaShell.Core.Credentials;
using VelaShell.Core.Models;

namespace VelaShell.Core.Import;

/// <summary>一次导出实际要写的连接:用户选的那些,加上它们经跳板链引用到的连接。</summary>
/// <param name="Sessions">要写的连接,按资源管理器里的顺序(分组顺序 → 名称)排好。</param>
/// <param name="RequestedCount">其中用户直接选的条数。</param>
/// <param name="DependencyCount">因为被当作跳板引用而自动带上的条数。</param>
public sealed record SessionExportSelection(IReadOnlyList<SessionProfile> Sessions, int RequestedCount, int DependencyCount);

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
        IReadOnlyList<ServerGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(groups);
        var byId = new Dictionary<Guid, SessionProfile>();
        foreach (SessionProfile profile in all)
        {
            _ = byId.TryAdd(profile.Id, profile);
        }
        var included = new HashSet<Guid>(requested.Where(byId.ContainsKey));
        int requestedCount = included.Count;
        var pending = new Queue<Guid>(included);
        while (pending.TryDequeue(out Guid id))
        {
            if (byId[id].JumpHostProfileId is { } jump && byId.ContainsKey(jump) && included.Add(jump))
            {
                pending.Enqueue(jump);
            }
        }
        IReadOnlyList<SessionProfile> ordered = Order(included.Select(id => byId[id]), groups);
        return new SessionExportSelection(ordered, requestedCount, included.Count - requestedCount);
    }

    /// <summary>按资源管理器的顺序排:分组按排序序号,未分组的在最后;组内按名称。</summary>
    /// <param name="sessions">要排的连接。</param>
    /// <param name="groups">本机全部分组。</param>
    /// <returns>排好的列表。</returns>
    public static IReadOnlyList<SessionProfile> Order(IEnumerable<SessionProfile> sessions, IReadOnlyList<ServerGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(groups);
        var rank = new Dictionary<Guid, int>();
        int index = 0;
        foreach (ServerGroup group in groups.OrderBy(static g => g.SortOrder))
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
    /// <param name="selection">由 <see cref="Select" /> 定下的连接。</param>
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
        var usedGroups = selection.Sessions.Select(static s => s.GroupId).OfType<Guid>().ToHashSet();
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
                .. groups
                    .Where(g => usedGroups.Contains(g.Id))
                    .OrderBy(static g => g.SortOrder)
                    .Select(static g => new SessionArchiveGroup { Id = g.Id, Name = g.Name, Icon = g.Icon, SortOrder = g.SortOrder })
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
