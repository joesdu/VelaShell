namespace VelaShell.Infrastructure.Plugins.Market;

/// <summary>插件商店的**只读**客户端:查最新版、把包取回来。</summary>
/// <remarks>
/// 只读是刻意的 —— 宿主不上传、不评价、不登录,也就没有任何需要带凭据的请求。
/// 这个接口存在的全部理由只有两条:回答"我装的这几个插件有没有新版",
/// 以及在用户点了更新之后把那个 <c>.vpx</c> 取回来。
/// </remarks>
public interface IPluginMarketClient
{
    /// <summary>商店根地址(展示与日志用)。</summary>
    string BaseUrl { get; }

    /// <summary>批量查这些 id 在商店上的最新已发布版本。</summary>
    /// <remarks>
    /// 商店上没有的 id(手工旁装的、私有的)与已下架的插件**不出现在结果里**,那不是错误。
    /// 整个请求失败(离线、内网、地址不通)时同样返回空表:检查更新查不到,
    /// 只意味着这次没查到,不该把一个红色错误糊到用户脸上。
    /// </remarks>
    /// <param name="pluginIds">要查的插件 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按插件 id 索引的最新版本表。</returns>
    Task<IReadOnlyDictionary<string, PluginMarketVersion>> GetLatestAsync(
        IReadOnlyCollection<string> pluginIds,
        CancellationToken cancellationToken = default);

    /// <summary>把某个版本的 <c>.vpx</c> 下载到指定路径,并核对商店声明的 SHA-256。</summary>
    /// <param name="pluginId">插件 id。</param>
    /// <param name="version">版本号。</param>
    /// <param name="destinationPath">落盘路径。</param>
    /// <param name="progress">下载进度(0–100)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="InvalidOperationException">
    /// 商店上没有这个版本,或者下载回来的字节与商店声明的摘要对不上。
    /// </exception>
    Task DownloadAsync(
        string pluginId,
        string version,
        string destinationPath,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);
}
