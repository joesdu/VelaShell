using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Net;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;

namespace VelaShell.Presentation.Services;

/// <summary>
/// 连接工作流服务:统筹会话配置的加载、保存、连接测试与实际连接,
/// 并在连接前后处理密码持久化策略、跳板链构建与历史/审计记录。
/// </summary>
public sealed class ConnectionWorkflowService(
    ISessionRepository sessionRepository,
    ISshConnectionService sshConnectionService,
    IRecentConnectionService? recentConnections = null,
    IAuditLogService? auditLog = null,
    ISettingsService? settingsService = null,
    ICredentialResolver? credentialResolver = null)
    : IConnectionWorkflowService
{
    private readonly ISessionRepository _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
    private readonly ISshConnectionService _sshConnectionService = sshConnectionService ?? throw new ArgumentNullException(nameof(sshConnectionService));

    /// <summary>获取全部已保存的会话配置,按最近连接时间倒序、再按名称升序排列。</summary>
    public async Task<IReadOnlyList<SessionProfile>> GetSavedProfilesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<SessionProfile> sessions = await _sessionRepository.GetAllSessionsAsync().ConfigureAwait(false);
        return [.. sessions
               .OrderByDescending(profile => profile.LastConnectedAt)
               .ThenBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>校验并保存会话配置(保存不强制凭据),按记住密码策略决定是否落盘密码。</summary>
    public async Task<SessionProfile> SaveProfileAsync(SessionProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        // 保存不要求凭据 —— 未勾选“记住密码”的配置在连接时再询问。
        ValidateProfile(profile, false);
        await _sessionRepository.SaveSessionAsync(await WithPersistablePasswordAsync(profile).ConfigureAwait(false)).ConfigureAwait(false);
        return profile;
    }

    /// <inheritdoc />
    public PluginConnectionProbe? PluginProbe { get; set; }

    /// <summary>
    /// 测试连接:建立后立即断开,返回成功与否及失败原因,不抛出连接异常。
    /// <para>
    /// 插件连接类型走**另一条**路。它们的握手不是 SSH:拿 SSH 去连 Redis 的 6379
    /// 或 S3 的 443,只会在 TCP 连上之后卡在版本交换里,最后报一个与真实原因毫无
    /// 关系的"连接超时" —— 用户会以为端口不通,于是去查防火墙。没有探针可用时
    /// 宁可明说"这种连接类型测不了",也不给一个假的失败原因。
    /// </para>
    /// </summary>
    public Task<ConnectionTestResult> TestConnectionAsync(SessionProfile profile, CancellationToken cancellationToken = default) =>
        TestConnectionCoreAsync(profile, packetTrace: null, cancellationToken);

    /// <inheritdoc />
    public Task<ConnectionTestResult> TestConnectionAsync(
        SessionProfile profile, SshPacketTrace packetTrace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packetTrace);
        return TestConnectionCoreAsync(profile, packetTrace, cancellationToken);
    }

    private async Task<ConnectionTestResult> TestConnectionCoreAsync(
        SessionProfile profile, SshPacketTrace? packetTrace, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.ConnectionType == ConnectionType.Plugin)
        {
            if (PluginProbe is not { } probe)
            {
                return new(false, Strings.Get("Plugin_TestUnavailable"));
            }
            try
            {
                await probe(profile, cancellationToken).ConfigureAwait(false);
                return new(true);
            }
            catch (Exception ex)
            {
                return new(false, ex.Message);
            }
        }
        try
        {
            SshSession session = await _sshConnectionService
                                       .ConnectAsync(
                                           await BuildConnectionInfoAsync(profile, automaticReconnect: false, packetTrace, cancellationToken).ConfigureAwait(false),
                                           cancellationToken)
                                       .ConfigureAwait(false);
            await _sshConnectionService.DisconnectAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
            return new(true);
        }
        catch (Exception ex)
        {
            return new(false, ex.Message, ex);
        }
    }

    /// <inheritdoc />
    public async Task<ConnectionTestResult> TestConnectionUnattendedAsync(SessionProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.ConnectionType == ConnectionType.Plugin)
        {
            // 插件的探针会开真会话、可能弹插件自己的框:巡检不碰。
            return new(false, Strings.Get("Plugin_TestUnavailable"));
        }
        try
        {
            ConnectionInfo info = await BuildConnectionInfoAsync(profile, automaticReconnect: false, packetTrace: null, cancellationToken, unattended: true)
                                      .ConfigureAwait(false);
            SshSession session = await _sshConnectionService.ConnectAsync(info, cancellationToken).ConfigureAwait(false);
            await _sshConnectionService.DisconnectAsync(session.SessionId, CancellationToken.None).ConfigureAwait(false);
            return new(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方的取消(巡检被停下、单台到点)照原样往外抛:那不是「这台连不上」。
            throw;
        }
        catch (Exception ex)
        {
            return new(false, ex.Message, ex);
        }
    }

    /// <summary>
    /// 连接指定配置:成功后更新最近连接时间(只对库里已有的配置落盘;现搭的临时配置不落库),同时记录连接历史与审计。
    /// </summary>
    /// <remarks>
    /// 落盘的只有最近连接时间这一项,改的是库里最新的那份 —— 不把调用方手里的副本整份写回(见 <see cref="TouchLastConnectedAsync" />)。
    /// 登录框交回的凭据另由 <see cref="SaveLoginChangesAsync" /> 写。
    /// </remarks>
    public Task<SshSession> ConnectProfileAsync(SessionProfile profile, CancellationToken cancellationToken = default) =>
        ConnectProfileCoreAsync(profile, automaticReconnect: false, cancellationToken);

    /// <inheritdoc />
    public Task<SshSession> ReconnectProfileAutomaticallyAsync(SessionProfile profile, CancellationToken cancellationToken = default) =>
        ConnectProfileCoreAsync(profile, automaticReconnect: true, cancellationToken);

    private async Task<SshSession> ConnectProfileCoreAsync(SessionProfile profile, bool automaticReconnect, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ValidateProfile(profile);
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        SshSession session;
        try
        {
            session = await _sshConnectionService
                            .ConnectAsync(
                                await BuildConnectionInfoAsync(profile, automaticReconnect, packetTrace: null, cancellationToken).ConfigureAwait(false),
                                cancellationToken)
                            .ConfigureAwait(false);
        }
        catch
        {
            await RecordHistoryAsync(profile, profile.Username, startedAt, false).ConfigureAwait(false);
            throw;
        }
        profile.LastConnectedAt = DateTime.UtcNow;
        await TouchLastConnectedAsync(profile.Id, profile.LastConnectedAt.Value).ConfigureAwait(false);
        // 历史里记实际登录的用户名 —— 引用共享凭据的配置自己可以不填用户名。
        await RecordHistoryAsync(profile, session.ConnectionInfo.Username, startedAt, true).ConfigureAwait(false);
        return session;
    }

    /// <summary>
    /// 连上之后把库里那份的最近连接时间改掉,其余字段以库里为准。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>只回写库里已有的配置。</b>不在库里的是现搭的临时配置 —— 外部拉起(Xshell 兼容登录)、「最近连接」里那条配置已删掉时重建的 ——
    /// 只活在本次运行:标签照常开、照常重连,但不进会话树,也就不随云同步上传(§252)。连着的标签所属的配置被删掉之后,
    /// 重连同样不会把它写回来。
    /// </para>
    /// <para>
    /// <b>改的是库里最新的那份,不是调用方手里的副本。</b>以前这里把调用方那份整份存回去:终端标签重连拿的是标签里的副本,
    /// 开着的这段时间里别处(云同步、编辑框、巡检窗口的「放开这几个算法」)刚改的,一次重连就被旧副本盖掉了(§298 的欠账)。
    /// 连接流程本身该落盘的只有两样:这个时间,与登录框交回的凭据(<see cref="SaveLoginChangesAsync" />,只在弹过登录框时由调用方写);
    /// 主机指纹记在已知主机里、不在配置上,算法补救、证书信任各有各的写法。
    /// </para>
    /// <para>
    /// 存之前照旧按「记住密码」与常规页的总开关过一遍(<see cref="WithPersistablePasswordAsync" />):以前每次连上都这样做,
    /// 关掉总开关之后连一次,库里存着的密码随之清掉。
    /// </para>
    /// </remarks>
    private async Task TouchLastConnectedAsync(Guid profileId, DateTime connectedAt)
    {
        if (await _sessionRepository.GetSessionAsync(profileId).ConfigureAwait(false) is not { } stored)
        {
            return;
        }
        stored.LastConnectedAt = connectedAt;
        await _sessionRepository.SaveSessionAsync(await WithPersistablePasswordAsync(stored).ConfigureAwait(false)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveLoginChangesAsync(SessionProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();
        if (await _sessionRepository.GetSessionAsync(profile.Id).ConfigureAwait(false) is not { } stored)
        {
            // 临时配置:登录框上本来就不给「记住密码」(存不下,§252),这里同样不落库。
            return;
        }
        SessionProfileMerge.CopyLoginFields(profile, stored);
        // 存回去的是库里那份改了这几项之后的样子,不是解析出凭据的副本:凭据的明文到不了仓储(#550)。
        await _sessionRepository.SaveSessionAsync(await WithPersistablePasswordAsync(stored).ConfigureAwait(false)).ConfigureAwait(false);
    }

    /// <summary>断开指定会话 Id 对应的 SSH 连接。</summary>
    public Task DisconnectAsync(Guid sessionId, CancellationToken cancellationToken = default) => _sshConnectionService.DisconnectAsync(sessionId, cancellationToken);

    /// <summary>
    /// “记住密码”未勾选、或全局“记住密码”(设置 → 常规 → 隐私与安全)关闭时,
    /// 持久化副本不包含密码/口令(仅本次连接使用)。
    /// </summary>
    private async Task<SessionProfile> WithPersistablePasswordAsync(SessionProfile profile)
    {
        bool remember = profile.RememberPassword;
        if (remember && settingsService is not null)
        {
            try
            {
                AppSettings settings = await settingsService.GetSettingsAsync().ConfigureAwait(false);
                remember = settings.General.RememberPasswords;
            }
            catch
            {
                // 设置不可读时沿用配置自身的勾选。
            }
        }
        if (remember)
        {
            return profile;
        }
        // 只把密码抹掉,其余原样深拷贝。配置自身的 RememberPassword 勾选保留 ——
        // 全局开关只影响是否落盘,不改写每条配置的选择。
        // 原先这里逐字段手写,每加一个字段就得记得回来补一行。
        SessionProfile stripped = profile.Clone();
        stripped.Password = null;
        return stripped;
    }

    /// <summary>连接结果写入连接历史与审计日志(SonnetDB 时序),失败不影响主流程。</summary>
    /// <param name="profile">连接配置。</param>
    /// <param name="username">登录用户名(连上时取实际用的那个,配置可能把它留给了共享凭据)。</param>
    /// <param name="startedAt">开始连接的时刻。</param>
    /// <param name="success">是否连上。</param>
    private async Task RecordHistoryAsync(SessionProfile profile, string username, DateTimeOffset startedAt, bool success)
    {
        if (auditLog is not null)
        {
            try
            {
                await auditLog.WriteAsync(new()
                {
                    Timestamp = startedAt,
                    Category = "connection",
                    Action = success ? "connect" : "connect-failed",
                    ProfileId = profile.Id,
                    Detail = HostAddress.FormatTarget(username, profile.Host, profile.Port)
                }).ConfigureAwait(false);
            }
            catch
            {
                // 审计写入失败不阻塞连接。
            }
        }
        if (recentConnections is null)
        {
            return;
        }
        try
        {
            string groupName = string.Empty;
            if (profile.GroupId is { } groupId)
            {
                List<ServerGroup> groups = await _sessionRepository.GetAllGroupsAsync().ConfigureAwait(false);
                groupName = groups.FirstOrDefault(g => g.Id == groupId)?.Name ?? string.Empty;
            }
            await recentConnections.RecordAsync(new()
            {
                ProfileId = profile.Id,
                ConnectionType = profile.ConnectionType,
                Name = string.IsNullOrWhiteSpace(profile.Name)
                           ? $"{username}@{profile.Host}"
                           : profile.Name,
                GroupName = groupName,
                Host = profile.Host,
                Port = profile.Port,
                Username = username,
                ConnectedAt = startedAt,
                Success = success,
                DurationMs = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds
            }).ConfigureAwait(false);
        }
        catch
        {
            // 历史记录失败不阻塞连接。
        }
    }

    /// <summary>
    /// 把配置解析成连接信息;JumpHostProfileId 链递归展开为嵌套 JumpHost
    /// (最多 5 跳,带环检测)。跳板配置必须已保存凭据(或引用共享凭据),否则该跳认证会失败。
    /// </summary>
    private async Task<ConnectionInfo> BuildConnectionInfoAsync(
        SessionProfile profile, bool automaticReconnect, SshPacketTrace? packetTrace, CancellationToken cancellationToken, bool unattended = false)
    {
        var visited = new HashSet<Guid> { profile.Id };
        string fileNameEncoding = await ResolveSessionEncodingAsync(profile).ConfigureAwait(false);
        return await BuildChainAsync(profile, visited, 0, automaticReconnect, unattended, fileNameEncoding, packetTrace, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 这条配置生效的会话字符集(会话覆盖优先于全局,与终端标签同一个口径);SFTP 按它解读文件名。
    /// </summary>
    /// <remarks>设置读不出来(或单测没给设置服务)时按全局默认值算,不让编码这件小事挡住连接。</remarks>
    private async Task<string> ResolveSessionEncodingAsync(SessionProfile profile)
    {
        AppSettings settings = new();
        if (settingsService is not null)
        {
            try
            {
                settings = await settingsService.GetSettingsAsync().ConfigureAwait(false);
            }
            catch
            {
                // 沿用默认值。
            }
        }
        return SessionTerminalSettings.Encoding(profile, settings);
    }

    private async Task<ConnectionInfo> BuildChainAsync(
        SessionProfile profile, HashSet<Guid> visited, int depth, bool automaticReconnect, bool unattended, string? fileNameEncoding,
        SshPacketTrace? packetTrace, CancellationToken cancellationToken)
    {
        // 每一跳各自解析自己引用的凭据(#550);解析出的明文只进 ConnectionInfo,不回写配置。
        SessionProfile effective = await ResolveHopCredentialAsync(profile, depth, cancellationToken).ConfigureAwait(false);
        ConnectionInfo? jump = null;
        if (profile.JumpHostProfileId is { } jumpId)
        {
            if (depth >= 5)
            {
                throw new InvalidOperationException(Strings.Get("Svc_JumpChainTooLong"));
            }
            if (!visited.Add(jumpId))
            {
                throw new InvalidOperationException(Strings.Get("Svc_JumpChainLoop"));
            }
            SessionProfile jumpProfile = await _sessionRepository.GetSessionAsync(jumpId).ConfigureAwait(false) ?? throw new InvalidOperationException(Strings.Get("Svc_JumpHostMissing"));
            // 跳板只开隧道、不开 SFTP:文件名编码只带在终点那一跳上。
            jump = await BuildChainAsync(jumpProfile, visited, depth + 1, automaticReconnect, unattended, null, packetTrace: null, cancellationToken).ConfigureAwait(false);
        }
        return new()
        {
            // 早先存下的配置里可能还是 [2001:db8::1]:去掉方括号再交出去(见 HostAddress)。
            Host = HostAddress.Normalize(effective.Host),
            Port = effective.Port,
            Username = effective.Username,
            AuthMethod = effective.AuthMethod,
            Password = effective.Password,
            PrivateKeyPath = effective.PrivateKeyPath,
            PrivateKeyPassphrase = effective.PrivateKeyPassphrase,
            CertificatePath = effective.CertificatePath,
            KeyStoreKey = effective.KeyStoreKey,
            // 会话级保活覆盖(F-06);null = 跟随全局。跳板链上每一跳各带各的。
            KeepAliveSeconds = effective.Terminal?.KeepAliveSeconds,
            Ssh = effective.Ssh?.Clone(),
            FileNameEncoding = fileNameEncoding,
            JumpHost = jump,
            // 一条链共用一个主机指纹策略(由最外层这一跳决定),每一跳都标上只是让每份 ConnectionInfo 自己说得清。
            IsAutomaticReconnect = automaticReconnect,
            // 连接诊断的协议轨迹只挂在最外层这一份上(递归建跳板时不传),装配那一层拿它记链上的每一跳。
            PacketTrace = packetTrace,
            IsUnattended = unattended
        };
    }

    /// <summary>取这一跳要用的凭据;没有解析器(单测、设计期)时用配置自己的。</summary>
    /// <remarks>
    /// <para>
    /// 跳板那几跳取不到凭据时**不**抛 <see cref="CredentialProviderException" />,而是改抛一个普通的连接错误:
    /// 连接流程见到前者会退回登录框,可那个框问的是目标机的凭据 —— 拿用户填的目标机密码去顶跳板机,
    /// 只会换来又一次失败。跳板向来是不弹框的,这里维持原样,只把是哪一跳、为什么说清楚。
    /// </para>
    /// <para>
    /// 配置自己存着的密码 / 私钥口令在这台设备上解不开时(<see cref="CredentialMaterial.HasUnreadableSecret(SessionProfile)" />)
    /// 同样不拿它去登录:界面上的连接在这之前就已经先弹登录框了(<c>RequiresCredentials</c>),
    /// 走到这里的是不弹框的那几条路(隧道、测试连接、巡检、跳板),给一句说得清的错误,而不是一次注定失败的认证。
    /// </para>
    /// </remarks>
    private async Task<SessionProfile> ResolveHopCredentialAsync(SessionProfile profile, int depth, CancellationToken cancellationToken)
    {
        try
        {
            SessionProfile effective = credentialResolver is null
                ? profile
                : await credentialResolver.ResolveAsync(profile, cancellationToken).ConfigureAwait(false);
            if (CredentialMaterial.HasUnreadableSecret(effective))
            {
                throw new CredentialProviderException(
                    CredentialFailure.UnreadableSecret,
                    profile.CredentialSource?.ProviderId ?? string.Empty,
                    Strings.Get("Cred_Failure_StoredSecretUnreadable"));
            }
            return effective;
        }
        catch (CredentialProviderException ex) when (depth > 0)
        {
            string name = string.IsNullOrWhiteSpace(profile.Name) ? profile.Host : profile.Name;
            throw new InvalidOperationException(Strings.Format("Svc_JumpCredentialFailed", name, ex.Message), ex);
        }
    }

    private static void ValidateProfile(SessionProfile profile, bool requireCredentials = true)
    {
        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            throw new ArgumentException(Strings.Get("Svc_ProfileNameRequired"), nameof(profile));
        }
        if (string.IsNullOrWhiteSpace(profile.Host))
        {
            throw new ArgumentException(Strings.Get("Svc_HostRequired"), nameof(profile));
        }
        // 「必须填用户名」这条并非对所有协议成立:FTP 匿名登录与插件协议的匿名访问
        // (S3 的公开只读桶)都不需要。连接配置页的保存按钮已按同一口径放行,
        // 这里再无条件拦一道,结果就是按钮亮着、一保存就抛。
        bool anonymousAllowed = profile.ConnectionType == ConnectionType.Plugin
                                || (profile.ConnectionType == ConnectionType.FTP && profile.Ftp?.Anonymous == true);
        // 引用共享凭据的配置:用户名可以留给凭据提供,认证材料要到连接那一刻才取 ——
        // 这两样在这里都查不了,缺了由解析器报出具体原因(#550)。
        if (!anonymousAllowed && (profile.CredentialSource is null || profile.AuthMethod == AuthMethod.Tailscale)
            && string.IsNullOrWhiteSpace(profile.Username))
        {
            throw new ArgumentException(Strings.Get("Svc_UsernameRequired"), nameof(profile));
        }
        if (profile.Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(profile), Strings.Get("Svc_PortRange"));
        }
        if (!requireCredentials || CredentialMaterial.NeedsResolution(profile))
        {
            return;
        }
        // ReSharper disable once ConvertIfStatementToSwitchStatement
        if (profile.AuthMethod == AuthMethod.Password && string.IsNullOrWhiteSpace(profile.Password))
        {
            throw new ArgumentException(Strings.Get("Svc_PasswordRequired"), nameof(profile));
        }
        if (profile.AuthMethod == AuthMethod.PrivateKey && string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
        {
            throw new ArgumentException(Strings.Get("Svc_PrivateKeyRequired"), nameof(profile));
        }
        // 系统密钥库那一路只要那把钥的引用(私钥在密钥库里,配置里本来就没有)。
        if (profile.AuthMethod == AuthMethod.KeyStore && profile.KeyStoreKey is null)
        {
            throw new ArgumentException(Strings.Get("Svc_KeyStoreKeyRequired"), nameof(profile));
        }
        // 证书认证缺两样中的任意一样都连不上:证书标明"我是谁",私钥才是签名的那把。
        // 分两条消息而不是合成一句,是为了让用户知道该去补哪一个。
        if (profile.AuthMethod == AuthMethod.Certificate)
        {
            if (string.IsNullOrWhiteSpace(profile.CertificatePath))
            {
                throw new ArgumentException(Strings.Get("Svc_CertificateRequired"), nameof(profile));
            }
            if (string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
            {
                throw new ArgumentException(Strings.Get("Svc_CertificateKeyRequired"), nameof(profile));
            }
        }
    }
}
