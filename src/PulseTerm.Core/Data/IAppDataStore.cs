namespace PulseTerm.Core.Data;

/// <summary>
/// 通用文档存储(SonnetDB 文档集合):按集合名 + 文档 Id 存取任意可 JSON 序列化的对象。
/// 用于 UI 配置、快捷命令等业务数据。
/// </summary>
public interface IAppDataStore
{
    Task<T?> GetAsync<T>(string collection, string id, CancellationToken cancellationToken = default) where T : class;

    Task<List<T>> GetAllAsync<T>(string collection, CancellationToken cancellationToken = default) where T : class;

    Task UpsertAsync<T>(string collection, string id, T value, CancellationToken cancellationToken = default) where T : class;

    Task DeleteAsync(string collection, string id, CancellationToken cancellationToken = default);
}
