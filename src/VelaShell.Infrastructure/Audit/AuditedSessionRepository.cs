using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Net;
using VelaShell.Core.Resources;

namespace VelaShell.Infrastructure.Audit;

/// <summary>
/// 给会话仓储加上审计:连接配置的新建、修改、删除各记一条(配置 Id 写进记录,查看器据此打开配置)。
/// 其余操作原样转给内层。
/// </summary>
/// <remarks>
/// <para>
/// <b>比的是落盘前后库里的那两份</b>,而不是调用方传进来的对象:仓储落盘时会改东西
/// (引用了共享凭据的配置清掉认证材料、目录收藏保留库里的),拿传进来的比会把没落盘的手输密码记成「改了登录凭据」。
/// 代价是每次保存多读两次库 —— 保存不是热路径。读不出改之前的那份就不记,不能把一次修改记成新建。
/// </para>
/// <para>
/// <b>不算改动的字段</b>:上次连接时间(每连一次都写)、目录收藏(只经专门的接口改,保存时保留库里的)、置顶(界面上的排序偏好)。
/// 于是连接成功后的回写、云同步拉下来一模一样的配置都不留记录。分组的拖动算「其他设置」。
/// </para>
/// </remarks>
/// <param name="inner">真正的仓储。</param>
/// <param name="auditLog">审计日志;null 时只转发。</param>
public sealed class AuditedSessionRepository(ISessionRepository inner, IAuditLogService? auditLog) : ISessionRepository, ISessionTreeDataRepository
{
    private static readonly HashSet<string> Ignored = ["lastConnectedAt", "directoryBookmarks", "isPinned"];
    private static readonly HashSet<string> AddressFields = ["host", "port", "connectionType"];

    private static readonly HashSet<string> CredentialFields =
    [
        "username", "authMethod", "password", "rememberPassword", "privateKeyPath", "privateKeyPassphrase",
        "certificatePath", "credentialSource", "pluginSecrets"
    ];

    private readonly ISessionRepository _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc />
    public Task<List<ServerGroup>> GetAllGroupsAsync() => _inner.GetAllGroupsAsync();

    /// <inheritdoc />
    public Task<List<SessionProfile>> GetAllSessionsAsync() => _inner.GetAllSessionsAsync();

    /// <inheritdoc />
    public Task<(List<ServerGroup> Groups, List<SessionProfile> Sessions)> GetAllTreeDataAsync() =>
        ISessionTreeDataRepository.ReadAsync(_inner);

    /// <inheritdoc />
    public Task<SessionProfile?> GetSessionAsync(Guid id) => _inner.GetSessionAsync(id);

    /// <inheritdoc />
    public Task SaveSessionAsync(SessionProfile session) => SaveAuditedAsync(session, _inner.SaveSessionAsync);

    /// <inheritdoc />
    public Task ReplaceSessionAsync(SessionProfile session) => SaveAuditedAsync(session, _inner.ReplaceSessionAsync);

    /// <inheritdoc />
    public Task<List<RemoteDirectoryBookmark>?> UpdateDirectoryBookmarksAsync(Guid id, Func<List<RemoteDirectoryBookmark>, bool> update) =>
        _inner.UpdateDirectoryBookmarksAsync(id, update);

    /// <inheritdoc />
    public async Task DeleteSessionAsync(Guid id)
    {
        (bool known, SessionProfile? before) = auditLog is null ? (false, null) : await TryReadAsync(id).ConfigureAwait(false);
        await _inner.DeleteSessionAsync(id).ConfigureAwait(false);
        if (known && before is not null)
        {
            await ConfigAudit.WriteAsync(auditLog!, AuditCategories.Profile, AuditActions.ProfileDeleted, id,
                ConfigAudit.Join(before.Name, Target(before))).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task SaveGroupAsync(ServerGroup group) => _inner.SaveGroupAsync(group);

    /// <summary>删除分组;组里还剩配置时(它们被仓储移回未分组)记一条汇总。</summary>
    /// <remarks>
    /// 移回未分组是仓储内部的批量改写,不经 <see cref="SaveSessionAsync" />,逐条的「修改连接配置」记不到它。
    /// 这里只在删除前数一下、删完记一条「N 条配置移回未分组」(带分组名),而不是每条配置各记一条:
    /// 那几条配置本身只是分组没了,一次删除就是一件事。会话树里删分组会先删掉组内的连接(那几次删除各有记录),
    /// 走到这里时组里通常已经空了,这时不记。
    /// </remarks>
    public async Task DeleteGroupAsync(Guid id)
    {
        if (auditLog is null)
        {
            await _inner.DeleteGroupAsync(id).ConfigureAwait(false);
            return;
        }
        (int members, string? groupName) = await CountMembersAsync(id).ConfigureAwait(false);
        await _inner.DeleteGroupAsync(id).ConfigureAwait(false);
        if (members > 0)
        {
            await ConfigAudit.WriteAsync(auditLog, AuditCategories.Profile, AuditActions.ProfilesUngrouped, null,
                Strings.Format("AuditLog_DetailProfilesUngrouped", groupName ?? id.ToString("D"), members)).ConfigureAwait(false);
        }
    }

    /// <summary>改了哪几块(资源键,按 名称 → 地址 → 登录凭据 → 其他 的顺序);一块都没改时为空。</summary>
    internal static List<string> ChangedAreas(SessionProfile before, SessionProfile after)
    {
        HashSet<string> fields = [.. ConfigAudit.ChangedFields(before, after, Ignored)];
        List<string> areas = [];
        if (fields.Contains("name"))
        {
            areas.Add("AuditLog_AreaName");
        }
        if (fields.Overlaps(AddressFields))
        {
            areas.Add("AuditLog_AreaAddress");
        }
        if (fields.Overlaps(CredentialFields))
        {
            areas.Add("AuditLog_AreaCredentials");
        }
        if (fields.Any(f => f != "name" && !AddressFields.Contains(f) && !CredentialFields.Contains(f)))
        {
            areas.Add("AuditLog_AreaOther");
        }
        return areas;
    }

    private async Task SaveAuditedAsync(SessionProfile session, Func<SessionProfile, Task> save)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (auditLog is null)
        {
            await save(session).ConfigureAwait(false);
            return;
        }
        (bool known, SessionProfile? before) = await TryReadAsync(session.Id).ConfigureAwait(false);
        await save(session).ConfigureAwait(false);
        if (!known)
        {
            return;
        }
        (_, SessionProfile? after) = await TryReadAsync(session.Id).ConfigureAwait(false);
        after ??= session;
        if (before is null)
        {
            await ConfigAudit.WriteAsync(auditLog, AuditCategories.Profile, AuditActions.ProfileCreated, after.Id,
                ConfigAudit.Join(after.Name, Target(after))).ConfigureAwait(false);
            return;
        }
        List<string> areas = ChangedAreas(before, after);
        if (areas.Count == 0)
        {
            return;
        }
        string name = before.Name == after.Name ? after.Name : $"{before.Name} → {after.Name}";
        string oldTarget = Target(before);
        string newTarget = Target(after);
        string target = oldTarget == newTarget ? newTarget : $"{oldTarget} → {newTarget}";
        await ConfigAudit.WriteAsync(auditLog, AuditCategories.Profile, AuditActions.ProfileUpdated, after.Id,
            ConfigAudit.Join(name, target, ConfigAudit.ChangedAreas(areas))).ConfigureAwait(false);
    }

    /// <summary>读库里现在的那份;读不出(库坏了、解密失败)时 <c>Known</c> 为 false —— 这时宁可不记,也不乱记。</summary>
    private async Task<(bool Known, SessionProfile? Profile)> TryReadAsync(Guid id)
    {
        try
        {
            return (true, await _inner.GetSessionAsync(id).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, null);
        }
    }

    /// <summary>
    /// 这个分组里此刻有几条配置、分组叫什么;读不出来按 0 条算(宁可少记这条汇总,也不让删除失败)。
    /// </summary>
    private async Task<(int Members, string? GroupName)> CountMembersAsync(Guid groupId)
    {
        try
        {
            List<SessionProfile> profiles = await _inner.GetAllSessionsAsync().ConfigureAwait(false);
            int members = profiles.Count(p => p.GroupId == groupId);
            if (members == 0)
            {
                return (0, null);
            }
            List<ServerGroup> groups = await _inner.GetAllGroupsAsync().ConfigureAwait(false);
            return (members, groups.FirstOrDefault(g => g.Id == groupId)?.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (0, null);
        }
    }

    /// <summary><c>用户@主机:端口</c>;没有主机(串口、插件协议的某些配置)时为空。</summary>
    private static string Target(SessionProfile profile) =>
        string.IsNullOrWhiteSpace(profile.Host) ? string.Empty : HostAddress.FormatTarget(profile.Username, profile.Host, profile.Port);
}
