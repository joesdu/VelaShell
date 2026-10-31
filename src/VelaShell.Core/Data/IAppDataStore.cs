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

/// <summary>
/// <see cref="IAppDataStore" /> 的可选能力:逐行读一个集合,读不出来的行(坏 JSON、字段类型对不上)
/// 不让整次读取失败,而是把它们的 Id 交给调用方处理。
/// </summary>
/// <remarks>
/// 单独一面,而不是改 <see cref="IAppDataStore.GetAllAsync{T}" /> 的语义:设置、布局模板这类集合里读不出来的行
/// 多半是新版本写下的,旧版本把它们当坏数据跳过、再整份写回,丢的就是用户的数据 —— 那些集合宁可整次读失败。
/// 只有「坏一行就该丢一行」的账(传输队列)才走这一面。
/// </remarks>
public interface ITolerantAppDataStore
{
    /// <summary>读出指定集合里能读出来的全部文档;读不出来的那些行只交回 Id。</summary>
    Task<(List<T> Items, List<string> UnreadableIds)> GetAllReadableAsync<T>(string collection, CancellationToken cancellationToken = default)
        where T : class;
}
