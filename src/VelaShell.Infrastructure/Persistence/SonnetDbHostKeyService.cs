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

    /// <summary>校验主机密钥指纹:未记录返回 Unknown,一致返回 Trusted,不一致返回 Changed。</summary>
    public async Task<HostKeyVerification> VerifyHostKeyAsync(string host, int port, string keyType, string fingerprint, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        KnownHost? existing = await GetAsync(host, port, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return HostKeyVerification.Unknown;
        }
        return SameFingerprint(existing.Fingerprint, fingerprint)
                   ? HostKeyVerification.Trusted
                   : HostKeyVerification.Changed;
    }

    /// <summary>取指定 host:port 的已知主机记录;没有记录返回 null。</summary>
    public async Task<KnownHost?> FindKnownHostAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        return await GetAsync(host, port, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>信任指定主机的密钥:新增或更新 known_hosts 记录的类型、指纹与最近可见时间。</summary>
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

    /// <summary>返回全部已知主机密钥记录。</summary>
    public async Task<List<KnownHost>> GetKnownHostsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        List<KnownHost?> hosts = await _engine.WithCollectionAsync(SonnetDbEngine.KnownHostsCollection, store =>
                                         store.Scan().Select(row => SonnetDbJson.Deserialize<KnownHost>(row.Json)).ToList(),
                                     cancellationToken).ConfigureAwait(false);
        return [.. hosts.Where(h => h is not null).Cast<KnownHost>()];
    }

    /// <summary>移除指定 host:port 的已知主机密钥记录。</summary>
    public async Task RemoveKnownHostAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await _engine.WithCollectionAsync<object?>(SonnetDbEngine.KnownHostsCollection, store =>
        {
            store.Delete(DocId(host, port));
            return null;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 两个 SHA-256 指纹是不是同一把钥。
    /// </summary>
    /// <remarks>
    /// 换底层库之前存下的记录是**裸 base64**(上一版库的 <c>SHA256FingerPrint</c> 不带前缀),
    /// 现在拿到的是 OpenSSH 风格的 <c>SHA256:</c> 前缀形式。逐字节比的话,换库之后每一台
    /// 已保存的主机都会被判成「指纹已变更」—— 开了「变更即阻断」的用户会一台也连不上。
    /// 所以比之前两边都去掉前缀与 base64 填充;旧记录不迁移,下次信任时自然改写成新形式。
    /// </remarks>
    internal static bool SameFingerprint(string? stored, string? current) =>
        stored is not null && current is not null
        && string.Equals(NormalizeFingerprint(stored), NormalizeFingerprint(current), StringComparison.Ordinal);

    private static string NormalizeFingerprint(string fingerprint)
    {
        string value = fingerprint.Trim();
        if (value.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
        {
            value = value["SHA256:".Length..];
        }
        return value.TrimEnd('=');
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
