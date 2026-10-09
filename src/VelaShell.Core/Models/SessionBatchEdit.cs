using VelaShell.Core.Credentials;

namespace VelaShell.Core.Models;

/// <summary>批量修改认证时改成哪一种。</summary>
public enum SessionBatchAuthKind
{
    /// <summary>引用一条共享凭据(#550)。</summary>
    SharedCredential,

    /// <summary>密码(可以留空,连接时再问)。</summary>
    Password,

    /// <summary>私钥文件。</summary>
    PrivateKey,

    /// <summary>本机 ssh-agent。</summary>
    Agent
}

/// <summary>批量修改里的认证一项。</summary>
public sealed class SessionBatchAuth
{
    /// <summary>改成哪一种认证。</summary>
    public required SessionBatchAuthKind Kind { get; init; }

    /// <summary><see cref="SessionBatchAuthKind.SharedCredential" /> 时引用的凭据(要它的认证方式来判断协议支不支持)。</summary>
    public SharedCredential? Credential { get; init; }

    /// <summary>密码;null / 空 = 不存,连接时再问。</summary>
    public string? Password { get; init; }

    /// <summary>私钥文件路径。</summary>
    public string? PrivateKeyPath { get; init; }

    /// <summary>私钥口令;私钥没加密时为空。</summary>
    public string? PrivateKeyPassphrase { get; init; }

    /// <summary>这一项落到连接上的认证方式。</summary>
    public AuthMethod Method => Kind switch
    {
        SessionBatchAuthKind.SharedCredential => Credential?.AuthMethod ?? AuthMethod.Password,
        SessionBatchAuthKind.PrivateKey => AuthMethod.PrivateKey,
        SessionBatchAuthKind.Agent => AuthMethod.Agent,
        _ => AuthMethod.Password
    };
}

/// <summary>
/// 对一批连接统一改几项通用设置(#571):用户名、端口、认证、跳板机。null / false 的项不动。
/// </summary>
/// <remarks>
/// <para>
/// 分组不在这里:资源管理器多选菜单里的「移动到分组」就是改分组,而且它带着「分组空了就删掉」那条规矩,
/// 这里再给一个改分组的入口,两边的规矩迟早对不上。
/// </para>
/// <para>
/// 协议不支持的项跳过而不是报错:一批里混着 SSH 和 FTP 时,把私钥认证套给 FTP 只会让它连不上,
/// 跳过并在结果里说一声,比整批拒绝更有用。
/// </para>
/// </remarks>
public sealed class SessionBatchEdit
{
    /// <summary>新用户名;null = 不改。</summary>
    public string? Username { get; init; }

    /// <summary>新端口;null = 不改。</summary>
    public int? Port { get; init; }

    /// <summary>新认证;null = 不改。</summary>
    public SessionBatchAuth? Auth { get; init; }

    /// <summary>是否改跳板机。</summary>
    public bool ChangeJumpHost { get; init; }

    /// <summary><see cref="ChangeJumpHost" /> 时的新跳板机;null = 改为直连。</summary>
    public Guid? JumpHostProfileId { get; init; }

    /// <summary>有没有任何一项要改。</summary>
    public bool HasChanges => Username is not null || Port is not null || Auth is not null || ChangeJumpHost;

    /// <summary>
    /// 把修改套到一批连接上,返回改过的副本(原对象不动)。
    /// </summary>
    /// <param name="targets">要改的连接。</param>
    /// <param name="allSessions">本机全部连接(判断跳板链会不会成环)。</param>
    /// <returns>改过的副本与跳过的计数。</returns>
    public SessionBatchEditResult Apply(IReadOnlyList<SessionProfile> targets, IReadOnlyList<SessionProfile> allSessions)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(allSessions);
        var result = new SessionBatchEditResult();
        bool jumpFormsCycle = ChangeJumpHost && JumpHostProfileId is { } jump && ReachesAny(jump, targets, allSessions);
        foreach (SessionProfile target in targets)
        {
            SessionProfile profile = target.Clone();
            bool changed = false;
            if (Username is { } username && profile.Username != username.Trim())
            {
                profile.Username = username.Trim();
                changed = true;
            }
            if (Port is { } port && port is >= 1 and <= 65535 && profile.Port != port)
            {
                profile.Port = port;
                changed = true;
            }
            if (Auth is { } auth)
            {
                if (CredentialCompatibility.Supports(profile, auth.Method))
                {
                    ApplyAuth(profile, auth);
                    changed = true;
                }
                else
                {
                    result.AuthSkipped++;
                }
            }
            if (ChangeJumpHost)
            {
                // 跳板只对走 SSH 的连接有意义;不能拿自己当跳板,也不能接出一个环。
                if (profile.ConnectionType is ConnectionType.SSH or ConnectionType.SFTP
                    && JumpHostProfileId != profile.Id
                    && !jumpFormsCycle)
                {
                    if (profile.JumpHostProfileId != JumpHostProfileId)
                    {
                        profile.JumpHostProfileId = JumpHostProfileId;
                        changed = true;
                    }
                }
                else
                {
                    result.JumpHostSkipped++;
                }
            }
            if (changed)
            {
                result.Changed.Add(profile);
            }
        }
        return result;
    }

    private static void ApplyAuth(SessionProfile profile, SessionBatchAuth auth)
    {
        switch (auth.Kind)
        {
            case SessionBatchAuthKind.SharedCredential when auth.Credential is { } credential:
                profile.CredentialSource = CredentialReference.ForShared(credential.Id);
                profile.AuthMethod = credential.AuthMethod;
                // 引用了凭据的连接身上不留认证材料(仓储层也会清,这里先清,免得副本在内存里带着旧密码)。
                CredentialMaterial.ClearInline(profile);
                break;
            case SessionBatchAuthKind.Password:
                profile.CredentialSource = null;
                profile.AuthMethod = AuthMethod.Password;
                profile.Password = string.IsNullOrEmpty(auth.Password) ? null : auth.Password;
                profile.RememberPassword = profile.Password is not null;
                break;
            case SessionBatchAuthKind.PrivateKey:
                profile.CredentialSource = null;
                profile.AuthMethod = AuthMethod.PrivateKey;
                profile.PrivateKeyPath = string.IsNullOrWhiteSpace(auth.PrivateKeyPath) ? null : auth.PrivateKeyPath.Trim();
                profile.PrivateKeyPassphrase = string.IsNullOrEmpty(auth.PrivateKeyPassphrase) ? null : auth.PrivateKeyPassphrase;
                break;
            case SessionBatchAuthKind.Agent:
                profile.CredentialSource = null;
                profile.AuthMethod = AuthMethod.Agent;
                break;
        }
    }

    /// <summary>从 <paramref name="start" /> 沿跳板链往上走,会不会走到这批连接里的任何一条(含它自己就在这批里)。</summary>
    private static bool ReachesAny(Guid start, IReadOnlyList<SessionProfile> targets, IReadOnlyList<SessionProfile> allSessions)
    {
        var targetIds = targets.Select(static t => t.Id).ToHashSet();
        var byId = new Dictionary<Guid, SessionProfile>();
        foreach (SessionProfile session in allSessions)
        {
            _ = byId.TryAdd(session.Id, session);
        }
        var seen = new HashSet<Guid>();
        Guid? current = start;
        while (current is { } id && seen.Add(id))
        {
            if (targetIds.Contains(id))
            {
                return true;
            }
            current = byId.TryGetValue(id, out SessionProfile? step) ? step.JumpHostProfileId : null;
        }
        return false;
    }
}

/// <summary>批量修改的结果。</summary>
public sealed class SessionBatchEditResult
{
    /// <summary>改过的副本(没有任何变化的连接不在里面)。</summary>
    public List<SessionProfile> Changed { get; } = [];

    /// <summary>协议不支持所选认证方式而跳过的条数。</summary>
    public int AuthSkipped { get; internal set; }

    /// <summary>跳板机没改成的条数:不走 SSH、选了自己,或会接出一个环。</summary>
    public int JumpHostSkipped { get; internal set; }
}
