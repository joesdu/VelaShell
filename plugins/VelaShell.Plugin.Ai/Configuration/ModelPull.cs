using VelaShell.Plugin.Ai.Bridge;
using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Ai.Configuration;

/// <summary>这一次拉取从哪儿拿到的清单。</summary>
public enum ModelSource
{
    /// <summary>两条路都没拿到东西 —— 保持原样。</summary>
    None,

    /// <summary>端点自己报的(见 <see cref="EndpointModelCatalog" />)。</summary>
    Endpoint,

    /// <summary>models.dev 收录的(见 <see cref="ModelsDevCatalog" />)。</summary>
    Catalogue
}

/// <summary>拉取结果。</summary>
/// <param name="Source">清单是从哪儿来的。</param>
/// <param name="Listed">这一次拉到多少个模型 id。</param>
/// <param name="Total">拉完之后这一家共有多少个模型配置。</param>
/// <param name="CatalogueUnavailable">
/// 这次该刷新 models.dev 却没连上(超时、没网、被墙):清单照常按上游落成模型,
/// 窗口与单价用的是旧缓存或默认值。界面据此补一句说明。
/// </param>
public readonly record struct ModelPullResult(ModelSource Source, int Listed, int Total, bool CatalogueUnavailable = false);

/// <summary>
/// 「拉取模型」这一件事本身:问清单 → 配规格 → 落成可选的模型。
/// </summary>
/// <remarks>
/// <para>
/// 单独摆一个类,是因为两处界面都要做同一件事:「连接供应商」页接上之后自动拉一次
/// (<c>ProviderSetupView</c>),「模型设置」页那个按钮再拉一次(<c>SettingsView</c>)。
/// 两边各写一份的话,今天改了取舍、明天就只有一边跟上了。
/// </para>
/// <para>
/// <b>落盘与刷界面不在这儿</b> —— 那两件事两边的做法不一样(一个还要重建展开区、
/// 一个还要重建左栏),硬塞进来反而要传一堆回调。这里只改 <see cref="AiProvider" /> 这个对象。
/// </para>
/// </remarks>
public static class ModelPull
{
    /// <summary>拉取用的超时。列表接口都是小回应,慢过这个数基本就是不通。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// models.dev 刷新单独的期限。它只负责补规格,连不上(常见于被墙或没有外网的内网环境)时
    /// 不该让拉取一直挂着 —— 上游清单早就到手了。
    /// </summary>
    internal static TimeSpan CatalogueTimeout { get; set; } = TimeSpan.FromSeconds(12);

    /// <summary>
    /// 给一家供应商拉一次模型清单。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>端点优先。</b> 先问端点它实际供应什么(那是唯一知道这件事的人 —— 中转站只转发
    /// 其中一部分,自行部署的 Ollama 装了哪几个权重,目录那边一概不知),再拿 id 去 models.dev
    /// 配窗口与单价。端点没有这条接口、或请求失败时,整条退回 models.dev 的清单,
    /// 也就是这个功能存在之前的行为。
    /// </para>
    /// <para>
    /// <b>一步都不抛异常。</b> 拉模型是"锦上添花":它失败不该让"这一家已经连上了"这件事打折扣,
    /// 更不该把异常冒到登录流程的错误处理里去。拿不到就返回 <see cref="ModelSource.None" />。
    /// </para>
    /// </remarks>
    /// <param name="provider">要填充的供应商(就地修改)。</param>
    /// <param name="modelsDevId">这一家在 models.dev 的 id;空表示那边没收录。</param>
    /// <param name="catalogue">规格库。</param>
    /// <param name="store">设置存储(用它解出这一家的凭据)。</param>
    /// <param name="apiKeyOverride">设置页表单里还没保存的那把 Key;null = 用已存的。</param>
    /// <param name="force">用户明确点了「拉取模型」—— 这时还拿七天前的规格缓存糊弄他就没意义了。</param>
    /// <param name="health">可选共享健康记录；只使本次规格应用真正改变的模型请求配置失效。</param>
    /// <param name="materialiseInto">可选分离的规格草稿;网络请求仍使用 provider 的当前地址和固定凭据。</param>
    /// <param name="catalogueHttp">可选索引传输客户端,由调用方管理生命周期；不承载端点凭据或模型列表请求。</param>
    /// <param name="referencedModelIds">仍被引用、即便端点清单里没有也不清理的模型配置 id(见 <see cref="ReferencedModelIdsAsync" />)。</param>
    /// <param name="cancellationToken">取消。</param>
    public static async Task<ModelPullResult> RunAsync(AiProvider provider, string? modelsDevId,
        ModelsDevCatalog catalogue, AiSettingsStore store, string? apiKeyOverride = null, bool force = false,
        ProviderHealth? health = null, AiProvider? materialiseInto = null, HttpClient? catalogueHttp = null,
        IReadOnlySet<string>? referencedModelIds = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(store);
        AiProvider target = materialiseInto ?? provider;
        if (apiKeyOverride is null)
            await store.ValidateProviderCredentialScopeAsync(provider, cancellationToken).ConfigureAwait(false);

        using var http = new HttpClient { Timeout = Timeout };
        // 规格库与上游清单并行:models.dev 只负责补窗口与单价,它连不上(超时、没网、被墙)
        // 绝不能挡住上游清单 —— 以前两步串行,models.dev 一超时整次拉取就失败了。
        // 规格缓存:没点拉取时只在过期了才重下(见 ModelsDevCatalog.CacheLifetime)——
        // 各家出新模型是以周计的事,为一次登录拖四百万字节下来纯属浪费
        Task<bool> refreshed = TryRefreshCatalogueAsync(catalogue, catalogueHttp ?? http, force, cancellationToken);

        IReadOnlyList<(string Id, int ContextTokens)> models = await ListAsync(provider, store, apiKeyOverride, http, cancellationToken)
            .ConfigureAwait(false);
        bool catalogueUnavailable = !await refreshed.ConfigureAwait(false);
        IReadOnlyList<ModelSpec> specs = models.Count > 0
            ? catalogue.Describe(modelsDevId, models)
            : catalogue.ForProvider(modelsDevId);
        if (specs.Count == 0)
        {
            return new ModelPullResult(ModelSource.None, 0, target.Models.Count, catalogueUnavailable);
        }

        // 网络在途期间用户可能已保存新配置；只能比较实际应用规格的同步前后。
        (AiModelConfig Config, string Model, int InputTokens, int OutputTokens, bool? Reasoning)[] before =
            health is null ? [] : target.Models.Select(model =>
                (model, model.Model, model.MaxInputTokens, model.MaxTokens, model.SupportsReasoning)).ToArray();
        target.AvailableModels = [.. specs.Select(s => s.Id)];
        ModelsDevCatalog.Materialise(target, specs);
        if (models.Count > 0)
        {
            // 公共目录不能证明实例已下架某个型号;只有实例端点的清单可清理自动配置。
            // 中转站的渠道被自动禁用时清单也会暂时少几个,所以当前选中、故障转移链与 IM 桥接
            // 还指着的模型一律保留 —— 删了它们,选择会漂到别家、用户排好的链会永久丢一站。
            var listed = new HashSet<string>(target.AvailableModels, StringComparer.OrdinalIgnoreCase);
            target.Models.RemoveAll(model => !listed.Contains(model.Model) && IsUnmodifiedAutomatic(model)
                && referencedModelIds?.Contains(model.Id) != true);
        }
        foreach ((AiModelConfig model, string previousModel, int previousInputTokens, int previousOutputTokens, bool? previousReasoning) in before)
        {
            if (!target.Models.Contains(model) || model.Model != previousModel || model.MaxInputTokens != previousInputTokens
                || model.MaxTokens != previousOutputTokens || model.SupportsReasoning != previousReasoning)
            {
                health!.Invalidate(model.Id);
            }
        }
        // 新清单恢复默认折叠：上次展开时面对的可能只有出厂那一条，拉取后可能有几百个。
        target.ModelsExpanded = null;
        return new ModelPullResult(models.Count > 0 ? ModelSource.Endpoint : ModelSource.Catalogue, specs.Count, target.Models.Count,
            catalogueUnavailable);
    }

    /// <summary>
    /// 需要时刷新 models.dev(缓存还新鲜且没点拉取就不发请求),带自己的短期限;任何失败都只记一句、返回 false。
    /// 只有调用方取消才往外抛。
    /// </summary>
    /// <returns>规格库可用(没必要刷新,或刷新成功)为 true;该刷新却没连上为 false。</returns>
    private static async Task<bool> TryRefreshCatalogueAsync(ModelsDevCatalog catalogue, HttpClient http, bool force,
        CancellationToken cancellationToken)
    {
        if (!force && !catalogue.IsStale) return true;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(CatalogueTimeout);
        try
        {
            return await catalogue.RefreshAsync(http, force, limit.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>仍被引用的模型配置 id:当前选中、故障转移链,以及 IM 桥接指定的模型。</summary>
    /// <remarks>桥接设置单独一个存储键(见 <see cref="BridgeSettings" />),读不出来就让这次拉取失败,不冒险清理。</remarks>
    /// <param name="settings">面板共享的设置。</param>
    /// <param name="context">插件上下文(读桥接设置)。</param>
    /// <param name="cancellationToken">取消。</param>
    public static async Task<IReadOnlySet<string>> ReferencedModelIdsAsync(AiSettings settings, IPluginContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(context);
        var ids = new HashSet<string>(settings.FailoverChain.Select(entry => entry.ModelId), StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(settings.ActiveModelId)) ids.Add(settings.ActiveModelId);
        BridgeSettings bridge = await new BridgeSettingsStore(context).LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(bridge.ModelId)) ids.Add(bridge.ModelId);
        return ids;
    }

    // 缺少来源或任何用户定制时都保留,不把手动模型误判为过期目录项。
    private static bool IsUnmodifiedAutomatic(AiModelConfig model)
        => model.LastFetchedSpec is { } spec && model.Model == spec.Id
            && model.Name.Length == 0 && model.Protocol is null && !model.HasOwnApiKey
            && string.IsNullOrEmpty(model.BaseUrlOverride) && !model.ContextWindowIsManual
            && model.MaxInputTokens == spec.ContextTokens && model.MaxTokens == spec.OutputTokens
            && model.InputPricePerMillion == spec.InputPrice && model.OutputPricePerMillion == spec.OutputPrice
            && model.CachedInputPricePerMillion == spec.CachedInputPrice && model.SupportsReasoning == spec.Reasoning
            && model.DefaultContextWindow == spec.DefaultContextWindow
            && model.PromptCaching && model.Temperature is null && model.TopP is null
            && model.StopSequences.Length == 0 && string.IsNullOrEmpty(model.SystemPrompt)
            && model.Reasoning == ReasoningLevel.Default;

    /// <summary>问端点要清单;问不到(没这条接口、没网、401)一律返回空,由调用方回落。</summary>
    private static async Task<IReadOnlyList<(string Id, int ContextTokens)>> ListAsync(AiProvider provider, AiSettingsStore store,
        string? apiKeyOverride, HttpClient http, CancellationToken cancellationToken)
    {
        try
        {
            ProviderCredential credential = apiKeyOverride is null
                ? await store.ResolveProviderCredentialAsync(provider, cancellationToken).ConfigureAwait(false)
                : ProviderCredential.Key(apiKeyOverride);
            // 订阅登录换回来的令牌可能自带地址(见 OAuthTokens.BaseUrl),那时以它为准
            string baseUrl = string.IsNullOrWhiteSpace(credential.BaseUrl) ? provider.BaseUrl : credential.BaseUrl;
            return await EndpointModelCatalog
                         .FetchAsync(http, baseUrl, provider.DefaultProtocol, credential, cancellationToken)
                         .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not AiSettingsStore.ApiKeySlotChangedException)
        {
            return [];
        }
    }
}
