namespace VelaShell.Infrastructure.Plugins.Market;

/// <summary>插件商店的匿名只读客户端:浏览、详情、查更新与下载。无登录、上传或评价操作。</summary>
public interface IPluginMarketClient
{
    /// <summary>商店根地址(展示与日志用)。</summary>
    string BaseUrl { get; }

    /// <summary>分页浏览与搜索;请求失败抛错,与成功但无结果区分。</summary>
    Task<PluginMarketPage> SearchAsync(string? query = null, string sort = "updated", int page = 1,
        CancellationToken cancellationToken = default);

    /// <summary>读取详情与已发布版本;请求失败或条目下架时抛错。</summary>
    Task<PluginMarketDetail> GetDetailAsync(string pluginId, CancellationToken cancellationToken = default);

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
    /// 商店上没有这个版本;商店地址或下载地址不是 https;商店没给 SHA-256 摘要(没摘要就不下);
    /// 或者下载回来的字节与商店声明的摘要对不上。
    /// </exception>
    Task DownloadAsync(
        string pluginId,
        string version,
        string destinationPath,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);
}
