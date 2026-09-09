using VelaShell.Core.Data;
using VelaShell.Core.Import;
using VelaShell.Core.Models;

namespace VelaShell.Infrastructure.Import;

/// <summary>把一批 <see cref="ImportedSession" /> 写入仓储的共享逻辑(各来源的导入服务复用)。</summary>
internal static class SessionImportWriter
{
    /// <summary>
    /// 新建一个分组承载选中的受支持会话并逐条持久化;密码由仓储 AES 重新加密落盘,
    /// 仅当密码成功还原时才设 <see cref="SessionProfile.RememberPassword" />。
    /// </summary>
    public static async Task<SessionImportOutcome> WriteAsync(
        ISessionRepository repository,
        IReadOnlyList<ImportedSession> items,
        string groupName,
        string tag,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        var toImport = items.Where(static i => i.IsSupported).ToList();
        if (toImport.Count == 0)
        {
            return new SessionImportOutcome { Imported = 0, PasswordsRecovered = 0, GroupId = null };
        }

        List<ServerGroup> groups = await repository.GetAllGroupsAsync().ConfigureAwait(false);
        int nextSort = groups.Count == 0 ? 0 : groups.Max(static g => g.SortOrder) + 1;
        var group = new ServerGroup
        {
            Name = string.IsNullOrWhiteSpace(groupName) ? tag : groupName,
            SortOrder = nextSort
        };

        int recovered = 0;
        // 别名 → 刚落盘的配置,供第二趟解析 ProxyJump 用;同名取先导入的那条。
        var byAlias = new Dictionary<string, SessionProfile>(StringComparer.OrdinalIgnoreCase);
        var pendingJumps = new List<(SessionProfile Profile, string Alias)>();
        foreach (ImportedSession item in toImport)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool usesKey = item.PrivateKeyPath is { Length: > 0 };
            var profile = new SessionProfile
            {
                ConnectionType = item.ConnectionType,
                Name = item.Name,
                Host = item.Host,
                Port = item.Port,
                Username = item.Username,
                AuthMethod = usesKey ? AuthMethod.PrivateKey : AuthMethod.Password,
                Password = item.Password,
                RememberPassword = item.PasswordRecovered,
                PrivateKeyPath = item.PrivateKeyPath,
                GroupId = group.Id,
                Tags = [tag.ToLowerInvariant()],
                Ftp = item.FtpSettings,
                PluginProtocolId = item.PluginProtocolId,
                PluginSettings = SessionProfile.CloneSettings(item.PluginSettings)
            };
            await repository.SaveSessionAsync(profile).ConfigureAwait(false);
            group.Sessions.Add(profile.Id);
            _ = byAlias.TryAdd(item.Name, profile);
            if (item.JumpHostAlias is { Length: > 0 } alias)
            {
                pendingJumps.Add((profile, alias));
            }
            if (item.PasswordRecovered)
            {
                recovered++;
            }
        }
        await repository.SaveGroupAsync(group).ConfigureAwait(false);
        await LinkJumpHostsAsync(repository, byAlias, pendingJumps, cancellationToken).ConfigureAwait(false);

        return new SessionImportOutcome
        {
            Imported = toImport.Count,
            PasswordsRecovered = recovered,
            GroupId = group.Id
        };
    }

    /// <summary>
    /// 第二趟:把 <see cref="ImportedSession.JumpHostAlias" /> 解析成 <see cref="SessionProfile.JumpHostProfileId" />。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 必须是第二趟 —— 跳板那条会话可能排在被跳的会话**后面**导入,第一趟走到它时 id 还不存在。
    /// </para>
    /// <para>
    /// 只在本批次内解析:别名是 config 文件里的局部概念,拿它去撞用户既有会话的名字
    /// 会把毫不相干的两台机器串成一条跳板链。批次里找不到(跳板没被勾选、或压根不在这份 config 里)
    /// 就留 null —— 会话照样导入,只是直连,用户在连接对话框里补一下即可。
    /// </para>
    /// </remarks>
    private static async Task LinkJumpHostsAsync(
        ISessionRepository repository,
        Dictionary<string, SessionProfile> byAlias,
        List<(SessionProfile Profile, string Alias)> pending,
        CancellationToken cancellationToken)
    {
        if (pending.Count == 0)
        {
            return;
        }
        Dictionary<Guid, SessionProfile> byId = byAlias.Values
            .DistinctBy(static p => p.Id)
            .ToDictionary(static p => p.Id);

        foreach ((SessionProfile profile, string alias) in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byAlias.TryGetValue(alias, out SessionProfile? jump) || jump.Id == profile.Id)
            {
                continue; // 跳板不在本批次,或指向自己。
            }
            profile.JumpHostProfileId = jump.Id;
            if (HasCycle(profile, byId))
            {
                // config 里写出了互相跳板的环(a→b→a)。留着它只会让连接时的环检测报错,
                // 断在这里并保留直连,是这两条会话里唯一还能用的形态。
                profile.JumpHostProfileId = null;
                continue;
            }
            await repository.SaveSessionAsync(profile).ConfigureAwait(false);
        }
    }

    /// <summary>沿本批次内的跳板链行走,判断 <paramref name="start" /> 是否落在一个环上。</summary>
    /// <param name="start">起点配置。</param>
    /// <param name="byId">本批次内 id 到配置的映射;链走出本批次即认定无环。</param>
    /// <returns>成环则为 <c>true</c>。</returns>
    private static bool HasCycle(SessionProfile start, Dictionary<Guid, SessionProfile> byId)
    {
        var seen = new HashSet<Guid> { start.Id };
        SessionProfile current = start;
        while (current.JumpHostProfileId is { } next)
        {
            if (!seen.Add(next))
            {
                return true;
            }
            if (!byId.TryGetValue(next, out SessionProfile? step))
            {
                return false; // 跳板指向本批次之外,后面的链路由连接工作流自己的环检测负责。
            }
            current = step;
        }
        return false;
    }
}
