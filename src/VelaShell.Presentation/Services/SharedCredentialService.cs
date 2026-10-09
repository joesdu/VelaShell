using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Models;

namespace VelaShell.Presentation.Services;

/// <summary>
/// 共享凭据(#550)与连接配置之间的批量操作:谁在用、让一批连接改用 / 不再用、删除。
/// </summary>
/// <remarks>
/// <para>
/// 这些操作都要同时改两边(凭据仓储与会话仓储),放在界面里写就是设置页、编辑框各写一份。
/// </para>
/// <para>
/// <b>不再用一条凭据时,把凭据拷回连接</b>(<see cref="DetachAsync" />)。解除引用之后连接若什么都没有,
/// 下次连接就要用户手输 —— 而用户只是想"这台机器不跟着那条凭据走了",不是想让它连不上。
/// 删除凭据同理:引用它的连接各自留一份同样的凭据,删完照样能连。
/// </para>
/// </remarks>
public sealed class SharedCredentialService(ISharedCredentialRepository credentials, ISessionRepository sessions)
{
    private readonly ISharedCredentialRepository _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    private readonly ISessionRepository _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));

    /// <summary>这条连接引用的是哪条共享凭据;没有引用或引用的不是共享凭据时为 null。</summary>
    /// <param name="profile">连接配置。</param>
    /// <returns>凭据 Id,或 null。</returns>
    public static Guid? SharedCredentialIdOf(SessionProfile profile) =>
        profile.CredentialSource is { } reference && reference.TryGetSharedId(out Guid id) ? id : null;

    /// <summary>
    /// 这条凭据能不能给这条连接用:FTP 与插件协议只认密码,匿名 FTP 不发任何凭据。
    /// </summary>
    /// <param name="credential">共享凭据。</param>
    /// <param name="profile">连接配置。</param>
    /// <returns>能用时为 true。</returns>
    public static bool IsCompatible(SharedCredential credential, SessionProfile profile) =>
        CredentialCompatibility.Supports(profile, credential.AuthMethod);

    /// <summary>每条共享凭据被多少条连接引用(没人引用的不在字典里)。</summary>
    /// <returns>凭据 Id → 引用数。</returns>
    public async Task<IReadOnlyDictionary<Guid, int>> CountUsagesAsync()
    {
        List<SessionProfile> profiles = await _sessions.GetAllSessionsAsync().ConfigureAwait(false);
        return profiles
               .Select(SharedCredentialIdOf)
               .OfType<Guid>()
               .GroupBy(static id => id)
               .ToDictionary(static g => g.Key, static g => g.Count());
    }

    /// <summary>
    /// 这条连接是不是认证材料与凭据完全相同、但还各存各的 —— 把现有连接迁到共享凭据上时,
    /// 用它一次勾出"其实本来就是同一套"的那一批。
    /// </summary>
    /// <remarks>
    /// 用户名只在凭据带了用户名时才参与比对:不带用户名的凭据本来就是给不同登录名共用的。
    /// 比对在本机内存里做,明文不出这个方法。
    /// </remarks>
    /// <param name="credential">共享凭据(可以是编辑框里还没保存的那一份)。</param>
    /// <param name="profile">连接配置(仓储读出的,密码已解密)。</param>
    /// <returns>是这样一条连接时为 true;已经引用某条凭据的一律为 false。</returns>
    public static bool HasSameCredential(SharedCredential credential, SessionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(profile);
        // 没有材料可比,或 Agent 根本没有材料:匹配出来的只会是一堆不相干的连接。
        return credential.HasSecret
               && credential.AuthMethod != AuthMethod.Agent
               && profile.CredentialSource is null
               && IsCompatible(credential, profile)
               && SameMaterial(credential, profile);
    }

    /// <summary>让这些连接改用这条凭据。</summary>
    /// <remarks>
    /// 连接上原有的认证材料清掉(仓储本来也会清)。连接的用户名与凭据的相同时一并清掉,
    /// 让它跟着凭据走;不同的保留 —— 那是有意的覆盖(同一套密钥配不同的登录名)。
    /// </remarks>
    /// <param name="credential">共享凭据。</param>
    /// <param name="profileIds">要改用它的连接。</param>
    public async Task AttachAsync(SharedCredential credential, IEnumerable<Guid> profileIds)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(profileIds);
        foreach (Guid profileId in profileIds)
        {
            if (await _sessions.GetSessionAsync(profileId).ConfigureAwait(false) is not { } profile
                || !IsCompatible(credential, profile))
            {
                continue;
            }
            profile.CredentialSource = CredentialReference.ForShared(credential.Id);
            CredentialMaterial.ClearInline(profile);
            if (credential.Username.Length > 0
                && string.Equals(profile.Username.Trim(), credential.Username, StringComparison.Ordinal))
            {
                profile.Username = string.Empty;
            }
            await _sessions.SaveSessionAsync(profile).ConfigureAwait(false);
        }
    }

    /// <summary>让这些连接不再用这条凭据:解除引用,并把凭据拷回连接,连接照样能连。</summary>
    /// <param name="credential">共享凭据。</param>
    /// <param name="profileIds">要解除的连接(没引用它的跳过)。</param>
    public async Task DetachAsync(SharedCredential credential, IEnumerable<Guid> profileIds)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(profileIds);
        foreach (Guid profileId in profileIds)
        {
            if (await _sessions.GetSessionAsync(profileId).ConfigureAwait(false) is not { } profile
                || SharedCredentialIdOf(profile) != credential.Id)
            {
                continue;
            }
            CopyInto(credential, profile);
            await _sessions.SaveSessionAsync(profile).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 保存一条凭据,并把"谁在用它"改成 <paramref name="users" /> 这一批(编辑框的确定)。
    /// </summary>
    /// <remarks>
    /// <para>先改连接、再存凭据:凭据仓储的 Changed 是界面刷新的信号,发出来时连接已经改完了。</para>
    /// <para>
    /// 改了认证方式之后用不上这条凭据的连接(把密码换成私钥,而 FTP 只认密码)同样解除并拷回,
    /// 不留一条注定解析失败的引用。
    /// </para>
    /// </remarks>
    /// <param name="credential">要保存的凭据。</param>
    /// <param name="users">保存后引用它的全部连接。</param>
    public async Task SaveAsync(SharedCredential credential, IReadOnlyCollection<Guid> users)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(users);
        var profiles =
            (await _sessions.GetAllSessionsAsync().ConfigureAwait(false)).ToDictionary(static p => p.Id);
        HashSet<Guid> wanted =
            [.. users.Where(id => profiles.TryGetValue(id, out SessionProfile? p) && IsCompatible(credential, p))];
        Guid[] current = [.. profiles.Values.Where(p => SharedCredentialIdOf(p) == credential.Id).Select(static p => p.Id)];
        await DetachAsync(credential, current.Where(id => !wanted.Contains(id))).ConfigureAwait(false);
        await AttachAsync(credential, wanted.Except(current)).ConfigureAwait(false);
        await _credentials.SaveAsync(credential).ConfigureAwait(false);
    }

    /// <summary>删除一条凭据;引用它的连接先各自留一份同样的凭据(见 <see cref="DetachAsync" />)。</summary>
    /// <param name="credential">要删的凭据。</param>
    public async Task DeleteAsync(SharedCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        List<SessionProfile> profiles = await _sessions.GetAllSessionsAsync().ConfigureAwait(false);
        await DetachAsync(credential, [.. profiles.Where(p => SharedCredentialIdOf(p) == credential.Id).Select(static p => p.Id)])
            .ConfigureAwait(false);
        await _credentials.DeleteAsync(credential.Id).ConfigureAwait(false);
    }

    /// <summary>把凭据拷回连接本身并解除引用。连接自己的用户名(覆盖)保留。</summary>
    private static void CopyInto(SharedCredential credential, SessionProfile profile)
    {
        profile.CredentialSource = null;
        if (string.IsNullOrWhiteSpace(profile.Username))
        {
            profile.Username = credential.Username;
        }
        profile.AuthMethod = credential.AuthMethod;
        profile.Password = credential.Password;
        profile.PrivateKeyPath = credential.PrivateKeyPath;
        profile.PrivateKeyPassphrase = credential.PrivateKeyPassphrase;
        profile.CertificatePath = credential.CertificatePath;
        // 拷回来的密码是从共享凭据里来的,用户当初就是让它保存的。
        profile.RememberPassword = true;
    }

    private static bool SameMaterial(SharedCredential credential, SessionProfile profile)
    {
        if (profile.AuthMethod != credential.AuthMethod)
        {
            return false;
        }
        if (credential.Username.Length > 0
            && !string.Equals(profile.Username.Trim(), credential.Username, StringComparison.Ordinal))
        {
            return false;
        }
        return credential.AuthMethod switch
        {
            AuthMethod.Password => string.Equals(profile.Password, credential.Password, StringComparison.Ordinal),
            AuthMethod.PrivateKey => SamePath(profile.PrivateKeyPath, credential.PrivateKeyPath),
            AuthMethod.Certificate => SamePath(profile.PrivateKeyPath, credential.PrivateKeyPath)
                                      && SamePath(profile.CertificatePath, credential.CertificatePath),
            _ => false
        };
    }

    /// <summary>路径按本机文件系统的大小写口径比(Windows 不分大小写)。</summary>
    private static bool SamePath(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a)
        && string.Equals(a.Trim(), b?.Trim(), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
