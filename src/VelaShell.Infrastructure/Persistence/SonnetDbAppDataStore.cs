using SonnetDB.Documents;
using VelaShell.Core.Data;

namespace VelaShell.Infrastructure.Persistence;

/// <summary>基于 SonnetDB 文档集合的通用文档存储(UI 配置、快捷命令等业务数据)。</summary>
public sealed class SonnetDbAppDataStore(SonnetDbEngine engine) : IAppDataStore
{
    private readonly SonnetDbEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    public async Task<T?> GetAsync<T>(string collection, string id, CancellationToken cancellationToken = default) where T : class =>
        await _engine.WithCollectionAsync(collection, store =>
        {
            DocumentRow? row = store.Get(id);
            return row is null ? null : SonnetDbJson.Deserialize<T>(row.Json);
        }, cancellationToken).ConfigureAwait(false);

    public async Task<List<T>> GetAllAsync<T>(string collection, CancellationToken cancellationToken = default) where T : class
    {
        List<T?> items = await _engine.WithCollectionAsync(collection, store =>
                                 store.Scan().Select(row => SonnetDbJson.Deserialize<T>(row.Json)).ToList(),
                             cancellationToken).ConfigureAwait(false);
        return items.Where(i => i is not null).Cast<T>().ToList();
    }

    public async Task UpsertAsync<T>(string collection, string id, T value, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        string json = SonnetDbJson.Serialize(value);
        await _engine.WithCollectionAsync<object?>(collection, store =>
        {
            store.Upsert(id, json);
            return null;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string collection, string id, CancellationToken cancellationToken = default) =>
        await _engine.WithCollectionAsync<object?>(collection, store =>
        {
            store.Delete(id);
            return null;
        }, cancellationToken).ConfigureAwait(false);
}
