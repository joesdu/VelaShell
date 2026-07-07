using PulseTerm.Core.Data;
using PulseTerm.Core.Models;
using PulseTerm.Core.Ssh;

namespace PulseTerm.Presentation.Services;

public sealed class ConnectionWorkflowService : IConnectionWorkflowService
{
    private readonly ISessionRepository _sessionRepository;
    private readonly ISshConnectionService _sshConnectionService;
    private readonly IRecentConnectionService? _recentConnections;

    public ConnectionWorkflowService(
        ISessionRepository sessionRepository,
        ISshConnectionService sshConnectionService,
        IRecentConnectionService? recentConnections = null)
    {
        _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
        _sshConnectionService = sshConnectionService ?? throw new ArgumentNullException(nameof(sshConnectionService));
        _recentConnections = recentConnections;
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
        await _sessionRepository.SaveSessionAsync(WithPersistablePassword(profile)).ConfigureAwait(false);
        return profile;
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(SessionProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        try
        {
            var session = await _sshConnectionService
                .ConnectAsync(BuildConnectionInfo(profile), cancellationToken)
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
                .ConnectAsync(BuildConnectionInfo(profile), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await RecordHistoryAsync(profile, startedAt, success: false).ConfigureAwait(false);
            throw;
        }

        profile.LastConnectedAt = DateTime.UtcNow;
        await _sessionRepository.SaveSessionAsync(WithPersistablePassword(profile)).ConfigureAwait(false);
        await RecordHistoryAsync(profile, startedAt, success: true).ConfigureAwait(false);

        return session;
    }

    /// <summary>“记住密码”未勾选时,持久化副本不包含密码/口令(仅本次连接使用)。</summary>
    private static SessionProfile WithPersistablePassword(SessionProfile profile)
    {
        if (profile.RememberPassword)
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
            RememberPassword = false,
        };
    }

    /// <summary>连接结果写入连接历史(SonnetDB 时序),失败不影响主流程。</summary>
    private async Task RecordHistoryAsync(SessionProfile profile, DateTimeOffset startedAt, bool success)
    {
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

    private static ConnectionInfo BuildConnectionInfo(SessionProfile profile)
    {
        return new ConnectionInfo
        {
            Host = profile.Host,
            Port = profile.Port,
            Username = profile.Username,
            AuthMethod = profile.AuthMethod,
            Password = profile.Password,
            PrivateKeyPath = profile.PrivateKeyPath,
            PrivateKeyPassphrase = profile.PrivateKeyPassphrase
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
