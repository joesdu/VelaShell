using System.Text.Json;
using PulseTerm.Core.Data;
using PulseTerm.Core.Models;

namespace PulseTerm.Infrastructure.Persistence;

/// <summary>
/// 基于 SonnetDB 文档集合的会话仓储:分组存 <c>session_groups</c>、
/// 会话配置存 <c>session_profiles</c>(文档 Id 均为 Guid 字符串)。
/// 密码与私钥口令通过 <see cref="ISecretProtector"/> 以 AES-256 加密后落盘。
/// 首次运行时自动导入既有 sessions.json。
/// </summary>
public sealed class SonnetDbSessionRepository : ISessionRepository
{
    private readonly SonnetDbEngine _engine;
    private readonly ISecretProtector _protector;
    private readonly string? _legacySessionsFile;
    private readonly SemaphoreSlim _migrationLock = new(1, 1);
    private bool _migrationChecked;

    public SonnetDbSessionRepository(SonnetDbEngine engine, ISecretProtector protector, string? legacySessionsFile = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _legacySessionsFile = legacySessionsFile;
    }

    public async Task<List<ServerGroup>> GetAllGroupsAsync()
    {
        await EnsureMigratedAsync().ConfigureAwait(false);
        var groups = await _engine.WithCollectionAsync(SonnetDbEngine.GroupsCollection, store =>
            store.Scan().Select(row => SonnetDbJson.Deserialize<ServerGroup>(row.Json)).ToList()).ConfigureAwait(false);
        return groups.Where(g => g is not null).Cast<ServerGroup>().OrderBy(g => g.SortOrder).ToList();
    }

    public async Task<List<SessionProfile>> GetAllSessionsAsync()
    {
        await EnsureMigratedAsync().ConfigureAwait(false);
        var sessions = await _engine.WithCollectionAsync(SonnetDbEngine.ProfilesCollection, store =>
            store.Scan().Select(row => SonnetDbJson.Deserialize<SessionProfile>(row.Json)).ToList()).ConfigureAwait(false);
        return sessions.Where(s => s is not null).Cast<SessionProfile>().Select(Unprotect).ToList();
    }

    public async Task<SessionProfile?> GetSessionAsync(Guid id)
    {
        await EnsureMigratedAsync().ConfigureAwait(false);
        var profile = await _engine.WithCollectionAsync(SonnetDbEngine.ProfilesCollection, store =>
        {
            var row = store.Get(id.ToString("D"));
            return row is null ? null : SonnetDbJson.Deserialize<SessionProfile>(row.Json);
        }).ConfigureAwait(false);
        return profile is null ? null : Unprotect(profile);
    }

    public async Task SaveSessionAsync(SessionProfile session)
    {
        ArgumentNullException.ThrowIfNull(session);
        await EnsureMigratedAsync().ConfigureAwait(false);
        var json = SonnetDbJson.Serialize(Protect(session));
        await _engine.WithCollectionAsync<object?>(SonnetDbEngine.ProfilesCollection, store =>
        {
            store.Upsert(session.Id.ToString("D"), json);
            return null;
        }).ConfigureAwait(false);
    }

    public async Task DeleteSessionAsync(Guid id)
    {
        await EnsureMigratedAsync().ConfigureAwait(false);
        await _engine.WithCollectionAsync<object?>(SonnetDbEngine.ProfilesCollection, store =>
        {
            store.Delete(id.ToString("D"));
            return null;
        }).ConfigureAwait(false);

        // 从各分组的会话列表中移除引用。
        await _engine.WithCollectionAsync<object?>(SonnetDbEngine.GroupsCollection, store =>
        {
            foreach (var row in store.Scan())
            {
                var group = SonnetDbJson.Deserialize<ServerGroup>(row.Json);
                if (group is not null && group.Sessions.Remove(id))
                {
                    store.Upsert(row.Id, SonnetDbJson.Serialize(group));
                }
            }

            return null;
        }).ConfigureAwait(false);
    }

    public async Task SaveGroupAsync(ServerGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        await EnsureMigratedAsync().ConfigureAwait(false);
        var json = SonnetDbJson.Serialize(group);
        await _engine.WithCollectionAsync<object?>(SonnetDbEngine.GroupsCollection, store =>
        {
            store.Upsert(group.Id.ToString("D"), json);
            return null;
        }).ConfigureAwait(false);
    }

    public async Task DeleteGroupAsync(Guid id)
    {
        await EnsureMigratedAsync().ConfigureAwait(false);
        await _engine.WithCollectionAsync<object?>(SonnetDbEngine.GroupsCollection, store =>
        {
            store.Delete(id.ToString("D"));
            return null;
        }).ConfigureAwait(false);

        // 归属该分组的会话回到未分组状态。
        await _engine.WithCollectionAsync<object?>(SonnetDbEngine.ProfilesCollection, store =>
        {
            foreach (var row in store.Scan())
            {
                var profile = SonnetDbJson.Deserialize<SessionProfile>(row.Json);
                if (profile?.GroupId == id)
                {
                    profile.GroupId = null;
                    store.Upsert(row.Id, SonnetDbJson.Serialize(profile));
                }
            }

            return null;
        }).ConfigureAwait(false);
    }

    private SessionProfile Protect(SessionProfile profile)
    {
        profile.Password = _protector.Protect(profile.Password);
        profile.PrivateKeyPassphrase = _protector.Protect(profile.PrivateKeyPassphrase);
        return profile;
    }

    private SessionProfile Unprotect(SessionProfile profile)
    {
        profile.Password = _protector.Unprotect(profile.Password);
        profile.PrivateKeyPassphrase = _protector.Unprotect(profile.PrivateKeyPassphrase);
        return profile;
    }

    private async Task EnsureMigratedAsync()
    {
        if (_migrationChecked)
        {
            return;
        }

        await _migrationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_migrationChecked)
            {
                return;
            }

            if (!string.IsNullOrEmpty(_legacySessionsFile) && File.Exists(_legacySessionsFile))
            {
                var isEmpty = await _engine.WithCollectionAsync(SonnetDbEngine.ProfilesCollection,
                    store => store.Count() == 0).ConfigureAwait(false)
                    && await _engine.WithCollectionAsync(SonnetDbEngine.GroupsCollection,
                    store => store.Count() == 0).ConfigureAwait(false);

                if (isEmpty)
                {
                    await ImportLegacyAsync(_legacySessionsFile).ConfigureAwait(false);
                }
            }

            _migrationChecked = true;
        }
        finally
        {
            _migrationLock.Release();
        }
    }

    private async Task ImportLegacyAsync(string path)
    {
        LegacySessionData? data;
        try
        {
            data = SonnetDbJson.Deserialize<LegacySessionData>(await File.ReadAllTextAsync(path).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return; // 旧文件损坏时跳过导入,不阻塞启动。
        }

        if (data is null)
        {
            return;
        }

        await _engine.WithCollectionAsync<object?>(SonnetDbEngine.GroupsCollection, store =>
        {
            foreach (var group in data.Groups)
            {
                store.Upsert(group.Id.ToString("D"), SonnetDbJson.Serialize(group));
            }

            return null;
        }).ConfigureAwait(false);

        await _engine.WithCollectionAsync<object?>(SonnetDbEngine.ProfilesCollection, store =>
        {
            foreach (var session in data.Sessions)
            {
                store.Upsert(session.Id.ToString("D"), SonnetDbJson.Serialize(Protect(session)));
            }

            return null;
        }).ConfigureAwait(false);

        TryRename(path);
    }

    private static void TryRename(string path)
    {
        try
        {
            File.Move(path, path + ".migrated.bak", overwrite: true);
        }
        catch (IOException)
        {
            // 保留原文件也无碍:集合已非空,后续不会重复导入。
        }
    }

    private sealed class LegacySessionData
    {
        public List<ServerGroup> Groups { get; set; } = new();
        public List<SessionProfile> Sessions { get; set; } = new();
    }
}
