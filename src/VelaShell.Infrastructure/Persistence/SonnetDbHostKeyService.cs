using System.Text.Json;
using SonnetDB.Documents;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;

// ReSharper disable AutoPropertyCanBeMadeGetOnly.Local

namespace VelaShell.Infrastructure.Persistence;

/// <summary>
/// 基于 SonnetDB 文档集合 <c>known_hosts</c> 的主机密钥信任存储,文档 Id 为 <c>host:port</c>。
/// 首次运行导入既有 known_hosts.json。
/// </summary>
public sealed class SonnetDbHostKeyService(SonnetDbEngine engine, string? legacyFile = null) : IHostKeyService
{
    private readonly SonnetDbEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    private readonly SemaphoreSlim _migrationLock = new(1, 1);
    private bool _migrationChecked;

    public async Task<HostKeyVerification> VerifyHostKeyAsync(string host, int port, string keyType, string fingerprint, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        KnownHost? existing = await GetAsync(host, port, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return HostKeyVerification.Unknown;
        }
        return existing.Fingerprint == fingerprint
                   ? HostKeyVerification.Trusted
                   : HostKeyVerification.Changed;
    }

    public async Task TrustHostKeyAsync(string host, int port, string keyType, string fingerprint, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        KnownHost? existing = await GetAsync(host, port, cancellationToken).ConfigureAwait(false);
        KnownHost entry = existing ?? new KnownHost { Host = host, Port = port, FirstSeenAt = DateTime.UtcNow };
        entry.KeyType = keyType;
        entry.Fingerprint = fingerprint;
        entry.LastSeenAt = DateTime.UtcNow;
        string json = SonnetDbJson.Serialize(entry);
        await _engine.WithCollectionAsync<object?>(SonnetDbEngine.KnownHostsCollection, store =>
        {
            store.Upsert(DocId(host, port), json);
            return null;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<KnownHost>> GetKnownHostsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        List<KnownHost?> hosts = await _engine.WithCollectionAsync(SonnetDbEngine.KnownHostsCollection, store =>
                                         store.Scan().Select(row => SonnetDbJson.Deserialize<KnownHost>(row.Json)).ToList(),
                                     cancellationToken).ConfigureAwait(false);
        return [.. hosts.Where(h => h is not null).Cast<KnownHost>()];
    }

    public async Task RemoveKnownHostAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await _engine.WithCollectionAsync<object?>(SonnetDbEngine.KnownHostsCollection, store =>
        {
            store.Delete(DocId(host, port));
            return null;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static string DocId(string host, int port) => $"{host}:{port}";

    private async Task<KnownHost?> GetAsync(string host, int port, CancellationToken cancellationToken) =>
        await _engine.WithCollectionAsync(SonnetDbEngine.KnownHostsCollection, store =>
        {
            DocumentRow? row = store.Get(DocId(host, port));
            return row is null ? null : SonnetDbJson.Deserialize<KnownHost>(row.Json);
        }, cancellationToken).ConfigureAwait(false);

    private async Task EnsureMigratedAsync(CancellationToken cancellationToken)
    {
        if (_migrationChecked)
        {
            return;
        }
        await _migrationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_migrationChecked)
            {
                return;
            }
            if (!string.IsNullOrEmpty(legacyFile) && File.Exists(legacyFile))
            {
                bool isEmpty = await _engine.WithCollectionAsync(SonnetDbEngine.KnownHostsCollection,
                                   store => store.Count() == 0, cancellationToken).ConfigureAwait(false);
                if (isEmpty)
                {
                    await ImportLegacyAsync(legacyFile, cancellationToken).ConfigureAwait(false);
                }
            }
            _migrationChecked = true;
        }
        finally
        {
            _migrationLock.Release();
        }
    }

    private async Task ImportLegacyAsync(string path, CancellationToken cancellationToken)
    {
        LegacyKnownHostData? data;
        try
        {
            data = SonnetDbJson.Deserialize<LegacyKnownHostData>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return;
        }
        if (data is null)
        {
            return;
        }
        await _engine.WithCollectionAsync<object?>(SonnetDbEngine.KnownHostsCollection, store =>
        {
            foreach (KnownHost hostEntry in data.Hosts)
            {
                store.Upsert(DocId(hostEntry.Host, hostEntry.Port), SonnetDbJson.Serialize(hostEntry));
            }
            return null;
        }, cancellationToken).ConfigureAwait(false);
    }

    // ReSharper disable once ClassNeverInstantiated.Local
    private sealed class LegacyKnownHostData
    {
        // JSON 反序列化需要 setter:get-only 集合属性 System.Text.Json 默认不填充。
        public List<KnownHost> Hosts { get; set; } = [];
    }
}
