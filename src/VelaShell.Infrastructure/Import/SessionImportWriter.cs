using VelaShell.Core.Data;
using VelaShell.Core.Import;
using VelaShell.Core.Models;
using VelaShell.Core.Net;

namespace VelaShell.Infrastructure.Import;

/// <summary>把一批 <see cref="ImportedSession" /> 写入仓储的共享逻辑(各来源的导入服务复用)。</summary>
internal static class SessionImportWriter
{
    /// <summary>隧道所在的集合(文档 Id = 连接 Id),与隧道面板、连接文件导入同一处。</summary>
    private const string TunnelsCollection = "tunnels";

    /// <summary>
    /// 新建一个分组承载选中的受支持会话并逐条持久化;密码由仓储 AES 重新加密落盘,
    /// 仅当密码成功还原时才设 <see cref="SessionProfile.RememberPassword" />。
    /// </summary>
    /// <param name="repository">连接仓储。</param>
    /// <param name="items">选中的会话。</param>
    /// <param name="groupName">新建分组的名称。</param>
    /// <param name="tag">打在会话上的来源标签。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="dataStore">
    /// 通用文档存储:会话带着端口转发(<see cref="ImportedSession.Tunnels" />)时写进这条连接的隧道;
    /// 为 <see langword="null" /> 时不写隧道。
    /// </param>
    public static async Task<SessionImportOutcome> WriteAsync(
        ISessionRepository repository,
        IReadOnlyList<ImportedSession> items,
        string groupName,
        string tag,
        CancellationToken cancellationToken,
        IAppDataStore? dataStore = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        var toImport = items.Where(static i => i.IsSupported).ToList();
        if (toImport.Count == 0)
        {
            return new SessionImportOutcome { Imported = 0, PasswordsRecovered = 0, GroupId = null };
        }

        // 这一批落的盘在审计里记成「导入」(Xshell / WinSCP / ssh_config 都经过这里)。
        using IDisposable source = AuditSourceScope.Enter(AuditSources.Import);
        List<ServerGroup> groups = await repository.GetAllGroupsAsync().ConfigureAwait(false);
        var root = new ServerGroup
        {
            Name = string.IsNullOrWhiteSpace(groupName) ? tag : groupName,
            SortOrder = groups.Where(static g => g.ParentGroupId is null).Select(static g => g.SortOrder).DefaultIfEmpty(-1).Max() + 1
        };
        var newGroups = new List<ServerGroup> { root };
        var children = new Dictionary<Guid, Dictionary<string, ServerGroup>>();
        var planned = new List<(ImportedSession Item, Guid GroupId)>(toImport.Count);
        foreach (ImportedSession item in toImport)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentOutOfRangeException.ThrowIfGreaterThan(item.GroupPath.Count, ServerGroupTree.MaxDepth);
            ServerGroup parent = root;
            foreach (string segment in item.GroupPath)
            {
                if (!children.TryGetValue(parent.Id, out var siblings))
                {
                    children.Add(parent.Id, siblings = new(StringComparer.Ordinal));
                }
                if (!siblings.TryGetValue(segment, out ServerGroup? child))
                {
                    child = new ServerGroup { Name = segment, ParentGroupId = parent.Id, SortOrder = siblings.Count };
                    siblings.Add(segment, child);
                    newGroups.Add(child);
                }
                parent = child;
            }
            planned.Add((item, parent.Id));
        }
        // Every group is persisted before the first profile is written. SaveSessionAsync maintains direct membership.
        foreach (ServerGroup group in newGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await repository.SaveGroupAsync(group).ConfigureAwait(false);
        }

        int recovered = 0;
        // 别名 → 刚落盘的配置,供第二趟解析 ProxyJump 用;同名取先导入的那条。
        var byAlias = new Dictionary<string, SessionProfile>(StringComparer.OrdinalIgnoreCase);
        var pendingJumps = new List<(SessionProfile Profile, string Alias)>();
        foreach ((ImportedSession item, Guid groupId) in planned)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool usesKey = item.PrivateKeyPath is { Length: > 0 };
            bool isSsh = item.ConnectionType is ConnectionType.SSH or ConnectionType.SFTP;
            var profile = new SessionProfile
            {
                ConnectionType = item.ConnectionType,
                Name = item.Name,
                // 来源里写成 [2001:db8::1] 的去掉方括号:配置里存裸地址(见 HostAddress)。
                Host = HostAddress.Normalize(item.Host),
                Port = item.Port,
                Username = item.Username,
                AuthMethod = item.AuthMethod ?? (usesKey ? AuthMethod.PrivateKey : AuthMethod.Password),
                Password = item.Password,
                RememberPassword = item.PasswordRecovered,
                PrivateKeyPath = item.PrivateKeyPath,
                CertificatePath = item.AuthMethod == AuthMethod.Certificate ? item.CertificatePath : null,
                // SSH 层的选项只对 SSH 生效(SessionProfile.Ssh 的约定:其余协议一律存 null),一项都没开也存 null。
                Ssh = isSsh && item.Ssh is { IsEmpty: false } ssh ? ssh.Clone() : null,
                GroupId = groupId,
                Tags = [tag.ToLowerInvariant()],
                Ftp = item.FtpSettings,
                PluginProtocolId = item.PluginProtocolId,
                PluginSettings = SessionProfile.CloneSettings(item.PluginSettings)
            };
            await repository.SaveSessionAsync(profile).ConfigureAwait(false);
            if (dataStore is not null && isSsh && item.Tunnels is { Count: > 0 } tunnels)
            {
                // 写成这条连接的隧道(停止状态,与隧道面板存的同一种文档);连接是新建的,没有旧隧道要合并。
                await dataStore.UpsertAsync(TunnelsCollection, profile.Id.ToString("D"), tunnels.ToList(), cancellationToken)
                    .ConfigureAwait(false);
            }
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
        await LinkJumpHostsAsync(repository, byAlias, pendingJumps, cancellationToken).ConfigureAwait(false);

        return new SessionImportOutcome
        {
            Imported = toImport.Count,
            PasswordsRecovered = recovered,
            GroupId = root.Id
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
        var byId = byAlias.Values
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
