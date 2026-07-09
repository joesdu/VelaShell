using PulseTerm.Core.Data;
using PulseTerm.Core.Models;
using PulseTerm.Core.Ssh;

namespace PulseTerm.Presentation.Services;

public sealed class ConnectionWorkflowService : IConnectionWorkflowService
{
    private readonly ISessionRepository _sessionRepository;
    private readonly ISshConnectionService _sshConnectionService;
    private readonly IRecentConnectionService? _recentConnections;
    private readonly IAuditLogService? _auditLog;
    private readonly ISettingsService? _settingsService;

    public ConnectionWorkflowService(
        ISessionRepository sessionRepository,
        ISshConnectionService sshConnectionService,
        IRecentConnectionService? recentConnections = null,
        IAuditLogService? auditLog = null,
        ISettingsService? settingsService = null)
    {
        _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
        _sshConnectionService = sshConnectionService ?? throw new ArgumentNullException(nameof(sshConnectionService));
        _recentConnections = recentConnections;
        _auditLog = auditLog;
        _settingsService = settingsService;
    }

    public async Task<IReadOnlyList<SessionProfile>> GetSavedProfilesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sessions = await _sessionRepository.GetAllSessionsAsync().ConfigureAwait(false);
        return sessions
            .OrderByDescending(profile => profile.LastConnectedAt)
            .ThenBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<SessionProfile> SaveProfileAsync(SessionProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        // 保存不要求凭据 —— 未勾选“记住密码”的配置在连接时再询问。
        ValidateProfile(profile, requireCredentials: false);
        await _sessionRepository.SaveSessionAsync(await WithPersistablePasswordAsync(profile).ConfigureAwait(false)).ConfigureAwait(false);
        return profile;
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(SessionProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        try
        {
            var session = await _sshConnectionService
                .ConnectAsync(await BuildConnectionInfoAsync(profile).ConfigureAwait(false), cancellationToken)
                .ConfigureAwait(false);

            await _sshConnectionService.DisconnectAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
            return new ConnectionTestResult(true);
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult(false, ex.Message);
        }
    }

    public async Task<SshSession> ConnectProfileAsync(SessionProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        ValidateProfile(profile);
        var startedAt = DateTimeOffset.UtcNow;
        SshSession session;
        try
        {
            session = await _sshConnectionService
                .ConnectAsync(await BuildConnectionInfoAsync(profile).ConfigureAwait(false), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await RecordHistoryAsync(profile, startedAt, success: false).ConfigureAwait(false);
            throw;
        }

        profile.LastConnectedAt = DateTime.UtcNow;
        await _sessionRepository.SaveSessionAsync(await WithPersistablePasswordAsync(profile).ConfigureAwait(false)).ConfigureAwait(false);
        await RecordHistoryAsync(profile, startedAt, success: true).ConfigureAwait(false);

        return session;
    }

    /// <summary>“记住密码”未勾选、或全局“记住密码”(设置 → 常规 → 隐私与安全)关闭时,
    /// 持久化副本不包含密码/口令(仅本次连接使用)。</summary>
    private async Task<SessionProfile> WithPersistablePasswordAsync(SessionProfile profile)
    {
        bool remember = profile.RememberPassword;
        if (remember && _settingsService is not null)
        {
            try
            {
                var settings = await _settingsService.GetSettingsAsync().ConfigureAwait(false);
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

        return new SessionProfile
        {
            Id = profile.Id,
            Name = profile.Name,
            Host = profile.Host,
            Port = profile.Port,
            Username = profile.Username,
            AuthMethod = profile.AuthMethod,
            Password = null,
            PrivateKeyPath = profile.PrivateKeyPath,
            PrivateKeyPassphrase = profile.PrivateKeyPassphrase,
            GroupId = profile.GroupId,
            LastConnectedAt = profile.LastConnectedAt,
            Tags = [.. profile.Tags],
            // 保留配置自身的勾选:全局开关只影响是否落盘,不改写每条配置的选择。
            RememberPassword = profile.RememberPassword,
            JumpHostProfileId = profile.JumpHostProfileId,
        };
    }

    /// <summary>连接结果写入连接历史与审计日志(SonnetDB 时序),失败不影响主流程。</summary>
    private async Task RecordHistoryAsync(SessionProfile profile, DateTimeOffset startedAt, bool success)
    {
        if (_auditLog is not null)
        {
            try
            {
                await _auditLog.WriteAsync(new AuditEntry
                {
                    Timestamp = startedAt,
                    Category = "connection",
                    Action = success ? "connect" : "connect-failed",
                    ProfileId = profile.Id,
                    Detail = $"{profile.Username}@{profile.Host}:{profile.Port}",
                }).ConfigureAwait(false);
            }
            catch
            {
                // 审计写入失败不阻塞连接。
            }
        }

        if (_recentConnections is null)
        {
            return;
        }

        try
        {
            var groupName = string.Empty;
            if (profile.GroupId is { } groupId)
            {
                var groups = await _sessionRepository.GetAllGroupsAsync().ConfigureAwait(false);
                groupName = groups.FirstOrDefault(g => g.Id == groupId)?.Name ?? string.Empty;
            }

            await _recentConnections.RecordAsync(new RecentConnectionEntry
            {
                ProfileId = profile.Id,
                Name = string.IsNullOrWhiteSpace(profile.Name)
                    ? $"{profile.Username}@{profile.Host}"
                    : profile.Name,
                GroupName = groupName,
                Host = profile.Host,
                Port = profile.Port,
                Username = profile.Username,
                ConnectedAt = startedAt,
                Success = success,
                DurationMs = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds,
            }).ConfigureAwait(false);
        }
        catch
        {
            // 历史记录失败不阻塞连接。
        }
    }

    public Task DisconnectAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => _sshConnectionService.DisconnectAsync(sessionId, cancellationToken);

    /// <summary>把配置解析成连接信息;JumpHostProfileId 链递归展开为嵌套 JumpHost
    /// (最多 5 跳,带环检测)。跳板配置必须已保存凭据,否则该跳认证会失败。</summary>
    private async Task<ConnectionInfo> BuildConnectionInfoAsync(SessionProfile profile)
    {
        var visited = new HashSet<Guid> { profile.Id };
        return await BuildChainAsync(profile, visited, depth: 0).ConfigureAwait(false);
    }

    private async Task<ConnectionInfo> BuildChainAsync(SessionProfile profile, HashSet<Guid> visited, int depth)
    {
        ConnectionInfo? jump = null;
        if (profile.JumpHostProfileId is { } jumpId)
        {
            if (depth >= 5)
                throw new InvalidOperationException("跳板链过长(最多 5 跳)。");
            if (!visited.Add(jumpId))
                throw new InvalidOperationException("跳板配置形成了循环引用,请检查跳板主机设置。");

            var jumpProfile = await _sessionRepository.GetSessionAsync(jumpId).ConfigureAwait(false)
                ?? throw new InvalidOperationException("跳板主机配置不存在(可能已被删除),请重新设置。");
            jump = await BuildChainAsync(jumpProfile, visited, depth + 1).ConfigureAwait(false);
        }

        return new ConnectionInfo
        {
            Host = profile.Host,
            Port = profile.Port,
            Username = profile.Username,
            AuthMethod = profile.AuthMethod,
            Password = profile.Password,
            PrivateKeyPath = profile.PrivateKeyPath,
            PrivateKeyPassphrase = profile.PrivateKeyPassphrase,
            JumpHost = jump,
        };
    }

    private static void ValidateProfile(SessionProfile profile, bool requireCredentials = true)
    {
        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            throw new ArgumentException("Connection profile name is required.", nameof(profile));
        }

        if (string.IsNullOrWhiteSpace(profile.Host))
        {
            throw new ArgumentException("Host is required.", nameof(profile));
        }

        if (string.IsNullOrWhiteSpace(profile.Username))
        {
            throw new ArgumentException("Username is required.", nameof(profile));
        }

        if (profile.Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(profile), "Port must be between 1 and 65535.");
        }

        if (!requireCredentials)
        {
            return;
        }

        if (profile.AuthMethod == AuthMethod.Password && string.IsNullOrWhiteSpace(profile.Password))
        {
            throw new ArgumentException("Password authentication requires a password.", nameof(profile));
        }

        if (profile.AuthMethod == AuthMethod.PrivateKey && string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
        {
            throw new ArgumentException("Private key authentication requires a private key path.", nameof(profile));
        }
    }
}
