namespace VelaShell.Core.Data;

/// <summary>
/// 通用文档存储(SonnetDB 文档集合):按集合名 + 文档 Id 存取任意可 JSON 序列化的对象。
/// 用于 UI 配置、快捷命令等业务数据。
/// </summary>
public interface IAppDataStore
{
    /// <summary>按集合名与文档 Id 读取单个文档,不存在时返回 null。</summary>
    Task<T?> GetAsync<T>(string collection, string id, CancellationToken cancellationToken = default) where T : class;

    /// <summary>读取指定集合中的全部文档。</summary>
    Task<List<T>> GetAllAsync<T>(string collection, CancellationToken cancellationToken = default) where T : class;

    /// <summary>按集合名与文档 Id 插入或更新文档。</summary>
    Task UpsertAsync<T>(string collection, string id, T value, CancellationToken cancellationToken = default) where T : class;

    /// <summary>按集合名与文档 Id 删除文档。</summary>
    Task DeleteAsync(string collection, string id, CancellationToken cancellationToken = default);
}
