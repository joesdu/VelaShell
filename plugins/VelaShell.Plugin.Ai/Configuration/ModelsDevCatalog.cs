using System.Text.Json;
using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Ai.Configuration;

/// <summary>
/// 一个模型的规格:够把 <see cref="AiModelConfig" /> 里那几项<b>本来要用户手填</b>的东西填满。
/// </summary>
/// <param name="Id">模型 id(直接就是请求里要发的那个)。</param>
/// <param name="Name">显示名。</param>
/// <param name="ContextTokens">上下文窗口;0 = 未知。</param>
/// <param name="OutputTokens">单轮最大输出;0 = 未知。</param>
/// <param name="InputPrice">每百万输入 token 单价;0 = 未知。</param>
/// <param name="OutputPrice">每百万输出 token 单价。</param>
/// <param name="CachedInputPrice">每百万缓存命中输入 token 单价。</param>
/// <param name="Reasoning">这个模型支不支持思考。</param>
/// <param name="DefaultContextWindow">窗口是否为无法获取容量后的默认估值。</param>
public sealed record ModelSpec(
    string Id,
    string Name,
    int ContextTokens,
    int OutputTokens,
    double InputPrice,
    double OutputPrice,
    double CachedInputPrice,
    bool? Reasoning,
    bool DefaultContextWindow = false);

/// <summary>
/// 模型规格库,数据来自开源的 <b>models.dev</b>(<c>github.com/sst/models.dev</c>)。
/// </summary>
/// <remarks>
/// <para>
/// <b>它与 <see cref="EndpointModelCatalog" /> 各知道一半,合起来才够用。</b>
/// 供应商自己的 <c>/v1/models</c> 只给一串 id,给不出上下文窗口和单价 —— 而那几项恰恰是
/// 本插件里最难填、填错了又<b>不报错</b>的东西(窗口填错,输入框下方的占比就是错的;
/// 单价填错,花费估算跟着错),何况订阅型的私有后端(ChatGPT 的 Codex 后端就是)根本没有那条接口;
/// 这一份记的正是 id + 窗口 + 单价 + 能力位,却不知道你接的这家中转站到底转发了哪几个。
/// 所以清单以端点为准、规格来这儿补,取舍见 <see cref="ModelPull" />。
/// </para>
/// <para>
/// <b>下载一次、瘦身落盘。</b>原始 <c>api.json</c> 有四百多万字节(两百多家供应商),
/// 但本插件只要每个模型的七个字段 —— 下载后当场转成精简索引再落盘(约五分之一大小),
/// 之后每次读都只解析这一份。缓存放插件私有数据目录,卸载插件时随之清除。
/// </para>
/// <para>
/// <b>拉不到不是错误。</b>没网、被墙、对方改版,一律退回已有的缓存;缓存也没有就退回
/// 目录里的出厂示例。模型清单是"锦上添花",不该拦住"这一家已经连上了"这件事。
/// </para>
/// </remarks>
/// <param name="context">插件上下文(用它的数据目录与日志)。</param>
public sealed class ModelsDevCatalog(IPluginContext context)
{
    /// <summary>数据源。</summary>
    public const string SourceUrl = "https://models.dev/api.json";

    /// <summary>缓存多久算新鲜。各家出新模型是以周计的事,一天一拉纯属浪费。</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);

    /// <summary>精简索引的落盘位置。</summary>
    private string CacheFile => Path.Combine(context.DataDirectory, "models-dev.json");

    /// <summary>本次进程内解析好的索引;第一次用到时才读盘。</summary>
    private Dictionary<string, List<ModelSpec>>? _index;

    /// <summary>缓存文件的时间(没有缓存时为 null)。</summary>
    public DateTimeOffset? CachedAt
    {
        get
        {
            try
            {
                return File.Exists(CacheFile) ? File.GetLastWriteTimeUtc(CacheFile) : null;
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// 本地缓存与内置快照都没有,或较新的那份也已经过期。
    /// 内置快照还新鲜时(刚发的版本)不必联网 —— 没有外网的环境装完就有可用的规格。
    /// </summary>
    public bool IsStale => Newest is not { } at || DateTimeOffset.UtcNow - at > CacheLifetime;

    /// <summary>本地缓存与内置快照中较新的那份的时间;都没有为 null。</summary>
    private DateTimeOffset? Newest => (CachedAt, BundledAt) switch
    {
        ({ } cached, { } bundled) => cached > bundled ? cached : bundled,
        ({ } cached, null) => cached,
        (null, { } bundled) => bundled,
        _ => null
    };

    /// <summary>
    /// 内置快照的来源:发版流水线跑 <c>scripts/Update-ModelsDevSnapshot.ps1</c> 抓的那份,作为嵌入资源打进插件
    /// (gzip 后的 api.json 子集 + 抓取时刻)。本地开发构建没有它,返回 null,行为与以前一样。测试可替换。
    /// </summary>
    internal Func<(Func<Stream> Open, DateTimeOffset At)?> BundledSource { get; init; } = LoadBundledResource;

    private (Func<Stream> Open, DateTimeOffset At)? _bundled;
    private bool _bundledProbed;

    /// <summary>内置快照的抓取时刻;没有内置快照为 null。</summary>
    public DateTimeOffset? BundledAt => Bundled()?.At;

    private (Func<Stream> Open, DateTimeOffset At)? Bundled()
    {
        if (!_bundledProbed)
        {
            _bundledProbed = true;
            try { _bundled = BundledSource(); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException)
            {
                context.Log.Warn($"models.dev: the bundled snapshot could not be read: {ex.Message}");
                _bundled = null;
            }
        }
        return _bundled;
    }

    private const string BundledResource = "VelaShell.Plugin.Ai.models-dev.json.gz";
    private const string BundledStampResource = "VelaShell.Plugin.Ai.models-dev.stamp";

    private static (Func<Stream> Open, DateTimeOffset At)? LoadBundledResource()
    {
        System.Reflection.Assembly assembly = typeof(ModelsDevCatalog).Assembly;
        using Stream? stamp = assembly.GetManifestResourceStream(BundledStampResource);
        if (stamp is null || assembly.GetManifestResourceInfo(BundledResource) is null)
        {
            return null;
        }
        using var reader = new StreamReader(stamp);
        if (!DateTimeOffset.TryParse(reader.ReadToEnd().Trim(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out DateTimeOffset at))
        {
            return null;
        }
        return (() => assembly.GetManifestResourceStream(BundledResource)
                      ?? throw new IOException("The bundled models.dev snapshot is missing."), at);
    }

    /// <summary>以 <paramref name="older" /> 为底,<paramref name="newer" /> 里有的型号覆盖、没有的补上;每家按 id 排序。</summary>
    internal static Dictionary<string, List<ModelSpec>> Merge(Dictionary<string, List<ModelSpec>> older,
        Dictionary<string, List<ModelSpec>> newer)
    {
        var merged = new Dictionary<string, List<ModelSpec>>(StringComparer.OrdinalIgnoreCase);
        foreach (string provider in older.Keys.Union(newer.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var byId = new Dictionary<string, ModelSpec>(StringComparer.OrdinalIgnoreCase);
            foreach (ModelSpec spec in older.GetValueOrDefault(provider) ?? []) byId[spec.Id] = spec;
            foreach (ModelSpec spec in newer.GetValueOrDefault(provider) ?? []) byId[spec.Id] = spec;
            List<ModelSpec> specs = [.. byId.Values];
            specs.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            merged[provider] = specs;
        }
        return merged;
    }

    /// <summary>解压并解析内置快照;读不出来返回空表(由调用方退回缓存)。</summary>
    private Dictionary<string, List<ModelSpec>> ParseBundled((Func<Stream> Open, DateTimeOffset At) bundled)
    {
        try
        {
            using Stream data = bundled.Open();
            using var gzip = new System.IO.Compression.GZipStream(data, System.IO.Compression.CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            return Parse(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            context.Log.Warn($"models.dev: the bundled snapshot could not be read: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// 取某家供应商的全部模型规格(按 id 排序);没有这一家、或还没有缓存时返回空。
    /// </summary>
    /// <param name="modelsDevId">models.dev 里的供应商 id(见 <see cref="ProviderCatalogEntry.ModelsDevId" />)。</param>
    public IReadOnlyList<ModelSpec> ForProvider(string? modelsDevId)
    {
        if (string.IsNullOrWhiteSpace(modelsDevId))
        {
            return [];
        }
        return Index().TryGetValue(modelsDevId, out List<ModelSpec>? models) ? models : [];
    }

    /// <summary>
    /// 给端点报上来的一串 id 配规格(见 <see cref="EndpointModelCatalog" />)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>清单以端点为准,规格来这儿补。</b> 返回的每条 <see cref="ModelSpec.Id" /> 一定是
    /// <paramref name="ids" /> 里那个原样的 id —— 请求里要发的是它,不是 models.dev 那边的写法。
    /// 配不上时优先采用端点的正数 context_length,否则用 128000；不借用其它供应商单价。
    /// 应用规格时只更新仍等于上次自动值的字段,保留用户编辑和没有来源标记的旧配置。
    /// </para>
    /// <para>
    /// <b>配不上这一家时,跨供应商按 id 找,但只补容量、不补单价。</b> 自定义端点与中转站没有
    /// <see cref="ProviderCatalogEntry.ModelsDevId" />,只能这么找。同一个型号在谁家跑,
    /// 上下文窗口都是同一个数(那是模型自身的属性);<b>价目却是各家自己定的</b> ——
    /// 中转站普遍加价,照抄原厂单价会让花费估算<b>静默地</b>偏低,而那正是最难被发现的一类错。
    /// 宁可让单价空着:空着至少还写在脸上。
    /// </para>
    /// </remarks>
    /// <param name="modelsDevId">这一家在 models.dev 的 id;空表示那边没收录(自定义 / 自行部署)。</param>
    /// <param name="ids">端点报上来的模型 id。</param>
    public IReadOnlyList<ModelSpec> Describe(string? modelsDevId, IReadOnlyList<(string Id, int ContextTokens)> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var own = new Dictionary<string, ModelSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (ModelSpec spec in ForProvider(modelsDevId))
        {
            own[spec.Id] = spec;
        }
        var described = new List<ModelSpec>(ids.Count);
        foreach ((string id, int contextTokens) in ids)
        {
            int fallbackContext = contextTokens > 0 ? contextTokens : 128000;
            if (own.TryGetValue(id, out ModelSpec? exact))
            {
                described.Add(exact with
                {
                    Id = id,
                    ContextTokens = exact.ContextTokens > 0 ? exact.ContextTokens : fallbackContext,
                    DefaultContextWindow = exact.ContextTokens <= 0 && contextTokens <= 0
                });
                continue;
            }
            described.Add(FindAnywhere(id) is { } loose
                ? loose with
                {
                    Id = id,
                    ContextTokens = loose.ContextTokens > 0 ? loose.ContextTokens : fallbackContext,
                    DefaultContextWindow = loose.ContextTokens <= 0 && contextTokens <= 0,
                    InputPrice = 0,
                    OutputPrice = 0,
                    CachedInputPrice = 0
                }
                : new ModelSpec(id, id, fallbackContext, 0, 0, 0, 0, false,
                    DefaultContextWindow: contextTokens <= 0));
        }
        return described;
    }

    /// <summary>
    /// 不限供应商,按 id 找一条规格。
    /// </summary>
    /// <remarks>
    /// 先找完整 id,再忽略供应商/路由前缀;仍不匹配时仅忽略已知部署标签,选最长完整型号候选。
    /// 不使用编辑距离猜版本或参数量;同优先级候选按多数取值(见 <see cref="Consensus" />),
    /// 容量没有多数时退回端点规格。
    /// </remarks>
    private ModelSpec? FindAnywhere(string id)
    {
        ReadOnlySpan<char> bare = BareModelId(id);
        var tied = new List<ModelSpec>();
        int bestScore = 0;
        foreach (List<ModelSpec> specs in Index().Values)
        {
            foreach (ModelSpec spec in specs)
            {
                ReadOnlySpan<char> candidate = BareModelId(spec.Id);
                int score = string.Equals(spec.Id, id, StringComparison.OrdinalIgnoreCase) ? int.MaxValue
                    : candidate.Equals(bare, StringComparison.OrdinalIgnoreCase) ? int.MaxValue - 1
                    : bare.Length > candidate.Length && bare[candidate.Length] == '-'
                        && bare.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)
                        && IsDeploymentSuffix(bare[(candidate.Length + 1)..]) ? candidate.Length : 0;
                if (score == 0 || score < bestScore)
                {
                    continue;
                }
                if (score > bestScore)
                {
                    tied.Clear();
                    bestScore = score;
                }
                tied.Add(spec);
            }
        }
        return tied.Count == 0 ? null : Consensus(tied);
    }

    /// <summary>同优先级的候选按多数定规格;窗口没有多数(并列)才算真歧义。</summary>
    /// <remarks>
    /// models.dev 里同一个型号常挂在十几家名下,总有一两家写法略有出入(输出上限 16000 对 16384、
    /// 窗口 1000000 对 1047576、某家漏标 reasoning)。要求全体一致的话,gpt-5、gpt-4o、gemini-2.5-pro
    /// 这些最常用的型号全会被判成歧义、退回 128000 的默认窗口,并把思考档位藏起来。
    /// 所以:窗口取正值里的多数,票数并列才放弃(那时谁先谁后只是索引顺序,不能拿来定);
    /// 同窗口内输出上限取多数,并列取小的 —— 超过端点上限的 max_tokens 会整轮 400;
    /// 思考能力在标注过的家里过半(含一半)即认,漏标的少数家不该把整个型号的思考档位藏掉。
    /// </remarks>
    private static ModelSpec? Consensus(List<ModelSpec> tied)
    {
        List<ModelSpec> known = tied.Exists(spec => spec.ContextTokens > 0)
            ? tied.FindAll(spec => spec.ContextTokens > 0)
            : tied;
        List<IGrouping<int, ModelSpec>> windows = [.. known.GroupBy(spec => spec.ContextTokens)
            .OrderByDescending(group => group.Count())];
        if (windows.Count > 1 && windows[0].Count() == windows[1].Count())
        {
            return null;
        }
        List<ModelSpec> agreed = [.. windows[0]];
        List<ModelSpec> outputs = agreed.Exists(spec => spec.OutputTokens > 0)
            ? agreed.FindAll(spec => spec.OutputTokens > 0)
            : agreed;
        int output = outputs.GroupBy(spec => spec.OutputTokens)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key).First().Key;
        int yes = agreed.Count(spec => spec.Reasoning == true);
        int no = agreed.Count(spec => spec.Reasoning == false);
        return agreed[0] with { OutputTokens = output, Reasoning = yes + no == 0 ? null : yes >= no };
    }

    private static readonly string[] DeploymentLabels =
        ["sg", "us", "eu", "cn", "jp", "kr", "hk", "tw", "in", "de", "fr", "gb", "uk", "ca", "au",
         "nvfp4", "fp4", "fp8", "fp16", "fp32", "bf16", "int4", "int8", "awq", "gptq"];

    private static bool IsDeploymentSuffix(ReadOnlySpan<char> suffix)
    {
        // ponytail: 仅忽略已知地区/精度标签；新部署命名需要扩充标签,不猜 thinking/mini 等真实型号变体。
        foreach (Range part in suffix.Split('-'))
        {
            bool known = false;
            foreach (string label in DeploymentLabels)
                if (suffix[part].Equals(label, StringComparison.OrdinalIgnoreCase)) { known = true; break; }
            if (!known) return false;
        }
        return !suffix.IsEmpty;
    }

    /// <summary>
    /// 冒号前只认这几个路由前缀(<c>global:deepseek-v4.1-flash-sg</c>)。按实际见到的写法扩充,与 <see cref="DeploymentLabels" /> 同一口径。
    /// </summary>
    /// <remarks>
    /// 冒号同样是 Ollama「型号:标签」的分隔符(<c>phi4:latest</c>、<c>llama3:instruct</c>、<c>phi3:mini</c>)。
    /// 以前按形状猜(冒号前不含 <c>-</c> / <c>.</c>、冒号后不是数字就剥),把型号剥掉、留下标签,
    /// 再拿 <c>latest</c> 去配目录里别的型号的同名标签 —— 静默借来另一个模型的窗口与输出上限(#582 复审⑥)。
    /// 两种错的代价不对称:认不出的前缀只是配不上目录、退回端点给的窗口(界面提示核实);剥错了却是悄悄用错规格。
    /// </remarks>
    private static readonly string[] RoutePrefixes = ["global"];

    private static ReadOnlySpan<char> BareModelId(string id)
    {
        ReadOnlySpan<char> bare = id.AsSpan(id.LastIndexOf('/') + 1);
        int colon = bare.IndexOf(':');
        if (colon > 0 && colon + 1 < bare.Length)
        {
            foreach (string prefix in RoutePrefixes)
            {
                if (bare[..colon].Equals(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return bare[(colon + 1)..];
                }
            }
        }
        return bare;
    }

    /// <summary>
    /// 需要时把索引拉新(缓存还新鲜就直接返回 false,不发请求)。
    /// </summary>
    /// <param name="http">发请求用的客户端。</param>
    /// <param name="force">忽略新鲜度,强制重下。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>这次是否真的更新了缓存。</returns>
    public async Task<bool> RefreshAsync(HttpClient http, bool force = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (!force && !IsStale)
        {
            return false;
        }
        try
        {
            string payload = await http.GetStringAsync(SourceUrl, cancellationToken).ConfigureAwait(false);
            Dictionary<string, List<ModelSpec>> parsed = Parse(payload);
            if (parsed.Count == 0)
            {
                return false; // 对方改版了或者拿到一份空的,别拿它把好缓存盖掉
            }
            await File.WriteAllTextAsync(CacheFile, Serialize(parsed), cancellationToken).ConfigureAwait(false);
            _index = parsed;
            context.Log.Info($"models.dev: cached {parsed.Count} providers.");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // 没网/被墙/对方改版 —— 退回已有缓存,连接流程照常。
            // 超时也在这里:HttpClient.Timeout 到期抛的是 TaskCanceledException(取消异常的一种),
            // 以前「不是取消才吞」让它一路冒上去,models.dev 连不上就把整次拉取(连同上游清单)打断了。
            // 只有调用方自己的令牌取消才往外抛。
            context.Log.Warn($"models.dev: refresh failed, keeping the cached copy: {ex.Message}");
            return false;
        }
    }

    private Dictionary<string, List<ModelSpec>> Index()
    {
        if (_index is not null)
        {
            return _index;
        }
        Dictionary<string, List<ModelSpec>> fromCache;
        try
        {
            fromCache = File.Exists(CacheFile) ? Parse(File.ReadAllText(CacheFile)) : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            context.Log.Warn($"models.dev: the cached index could not be read: {ex.Message}");
            fromCache = [];
        }
        // 本地缓存与内置快照合并,同一型号以较新的那份为准。不能整份二选一:内置快照只留近一年的型号
        // (见 scripts/Update-ModelsDevSnapshot.ps1),刚升级时它比旧缓存新,整份换过去的话,
        // 能联网的用户在快照新鲜期内也丢了 gpt-4o 这类老型号的规格。合并之后老型号从缓存来,新型号与新价从快照来。
        if (Bundled() is not { } bundled)
        {
            return _index = fromCache;
        }
        Dictionary<string, List<ModelSpec>> fromBundle = ParseBundled(bundled);
        if (fromBundle.Count == 0) return _index = fromCache;
        if (fromCache.Count == 0) return _index = fromBundle;
        bool bundleIsNewer = CachedAt is not { } cachedAt || bundled.At > cachedAt;
        return _index = bundleIsNewer ? Merge(fromCache, fromBundle) : Merge(fromBundle, fromCache);
    }

    /// <summary>
    /// 解析。<b>原始 <c>api.json</c> 与我们自己的精简索引都认</b> —— 两者的层级一样
    /// (供应商 → 模型 → 规格),只是字段名不同,靠"有没有 <c>models</c> 这一层"区分。
    /// 这样刷新与读缓存共用一条路径,不必写两份解析。
    /// </summary>
    internal static Dictionary<string, List<ModelSpec>> Parse(string json)
    {
        var result = new Dictionary<string, List<ModelSpec>>(StringComparer.OrdinalIgnoreCase);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return result;
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return result;
            }
            foreach (JsonProperty provider in document.RootElement.EnumerateObject())
            {
                if (provider.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                // 原始格式外面还包一层 {id,name,doc,models:{…}};精简索引直接就是模型表
                JsonElement models = provider.Value.TryGetProperty("models", out JsonElement nested)
                                     && nested.ValueKind == JsonValueKind.Object
                    ? nested
                    : provider.Value;
                var specs = new List<ModelSpec>();
                foreach (JsonProperty model in models.EnumerateObject())
                {
                    if (model.Value.ValueKind == JsonValueKind.Object
                        && IsUsableForChat(model.Name, model.Value)
                        && ReadSpec(model.Name, model.Value) is { } spec)
                    {
                        specs.Add(spec);
                    }
                }
                if (specs.Count > 0)
                {
                    specs.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
                    result[provider.Name] = specs;
                }
            }
        }
        return result;
    }

    /// <summary>
    /// 这一条值不值得摆进模型下拉。<b>在解析时就筛掉</b>,缓存里存的就是干净的。
    /// </summary>
    /// <remarks>
    /// 三条判据,全部取自数据本身,不猜:
    /// <list type="bullet">
    /// <item><c>status == "deprecated"</c> —— 已下架。上游明确标了的(线上两百来个),
    /// 摆出来只会让人选中一个用不了的型号。<c>beta</c> 保留:那是能用的。</item>
    /// <item>输出上限为 0 —— 画图那类根本不产出 token,进聊天下拉没有意义。</item>
    /// <item>id 里带 <c>embedding</c> —— 向量模型。这一条是<b>名字启发式</b>,因为
    /// 上游没有能把它和聊天模型分开的字段(它的 <c>limit.output</c> 填的是向量维度,
    /// 不是 token 数)。风险很低:没有哪个聊天模型叫 embedding。</item>
    /// </list>
    /// </remarks>
    private static bool IsUsableForChat(string key, JsonElement model)
    {
        if (string.Equals(Text(model, "status"), "deprecated", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        string id = Text(model, "id") ?? key;
        if (id.Contains("embedding", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        // 精简缓存里没有 limit 这一层,那时不判(存进去的本来就是筛过的)
        JsonElement limit = Object(model, "limit");
        return limit.ValueKind != JsonValueKind.Object || Number(limit, "output") is not (null or <= 0);
    }

    /// <summary>读一条模型记录;两套字段名都试(原始 / 精简)。</summary>
    private static ModelSpec? ReadSpec(string key, JsonElement model)
    {
        string id = Text(model, "id") ?? key;
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }
        JsonElement limit = Object(model, "limit");
        JsonElement cost = Object(model, "cost");
        return new ModelSpec(
            id,
            Text(model, "name") ?? id,
            Number(limit, "context") ?? Number(model, "ctx") ?? 0,
            Number(limit, "output") ?? Number(model, "max") ?? 0,
            Decimal(cost, "input") ?? Decimal(model, "pin") ?? 0,
            Decimal(cost, "output") ?? Decimal(model, "pout") ?? 0,
            Decimal(cost, "cache_read") ?? Decimal(model, "pcache") ?? 0,
            Bool(model, "reasoning") ?? Bool(model, "think") ?? false);
    }

    /// <summary>写成精简索引:每个模型只留七个字段,体积约为原始的五分之一。</summary>
    internal static string Serialize(Dictionary<string, List<ModelSpec>> index)
    {
        ArgumentNullException.ThrowIfNull(index);
        var slim = new Dictionary<string, Dictionary<string, Dictionary<string, object?>>>(StringComparer.Ordinal);
        foreach ((string provider, List<ModelSpec> models) in index)
        {
            var table = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
            foreach (ModelSpec spec in models)
            {
                table[spec.Id] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = spec.Name,
                    ["ctx"] = spec.ContextTokens,
                    ["max"] = spec.OutputTokens,
                    ["pin"] = spec.InputPrice,
                    ["pout"] = spec.OutputPrice,
                    ["pcache"] = spec.CachedInputPrice,
                    ["think"] = spec.Reasoning
                };
            }
            slim[provider] = table;
        }
        return JsonSerializer.Serialize(slim);
    }

    private static JsonElement Object(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    private static string? Text(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object
           && parent.TryGetProperty(name, out JsonElement value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Number(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object
           && parent.TryGetProperty(name, out JsonElement value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out int number)
            ? number
            : null;

    private static double? Decimal(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object
           && parent.TryGetProperty(name, out JsonElement value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetDouble(out double number)
            ? number
            : null;

    private static bool? Bool(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object
           && parent.TryGetProperty(name, out JsonElement value)
           && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    /// <summary>
    /// 把规格填进模型配置。
    /// </summary>
    /// <remarks>
    /// <b>只填知道的,不知道的一律不动</b> —— 上游没给窗口/单价时,把用户已经填好的值
    /// 覆盖成 0 比不填还糟(窗口 0 会让上下文占比整个消失,单价 0 会让花费估算静默停掉)。
    /// </remarks>
    /// <param name="model">要填的模型配置。</param>
    /// <param name="spec">规格。</param>
    /// <param name="newModel">新拉取模型可初始化规格；已有模型只更新仍未手动修改的自动值。</param>
    public static void Apply(AiModelConfig model, ModelSpec spec, bool newModel = false)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(spec);
        ModelSpec? previous = model.LastFetchedSpec ?? (newModel ? spec : null);
        if (previous is null) return;
        bool identifier = newModel || model.Model == previous.Id;
        bool input = newModel || !model.ContextWindowIsManual && model.MaxInputTokens == previous.ContextTokens
            && (!spec.DefaultContextWindow || model.DefaultContextWindow == true);
        bool output = newModel || model.MaxTokens == previous.OutputTokens;
        bool priceIn = newModel || model.InputPricePerMillion == previous.InputPrice;
        bool priceOut = newModel || model.OutputPricePerMillion == previous.OutputPrice;
        bool priceCached = newModel || model.CachedInputPricePerMillion == previous.CachedInputPrice;
        bool reasoning = newModel || model.SupportsReasoning == previous.Reasoning;
        if (identifier) model.Model = spec.Id;
        if (input && spec.ContextTokens > 0)
        {
            bool changed = model.MaxInputTokens != spec.ContextTokens || previous.Id != spec.Id;
            model.MaxInputTokens = spec.ContextTokens;
            if (newModel || changed || !spec.DefaultContextWindow) model.DefaultContextWindow = spec.DefaultContextWindow;
        }
        else if (newModel && model.MaxInputTokens == 128000)
        {
            model.DefaultContextWindow = true;
        }

        if (output && spec.OutputTokens > 0) model.MaxTokens = spec.OutputTokens;
        if (priceIn && spec.InputPrice > 0) model.InputPricePerMillion = spec.InputPrice;
        if (priceOut && spec.OutputPrice > 0) model.OutputPricePerMillion = spec.OutputPrice;
        if (priceCached && spec.CachedInputPrice > 0) model.CachedInputPricePerMillion = spec.CachedInputPrice;
        if (reasoning) model.SupportsReasoning = spec.Reasoning;
        if (!newModel && (!identifier || model.Model == previous.Id)
            && (!input || model.MaxInputTokens == previous.ContextTokens)
            && (!input || (model.DefaultContextWindow == true) == previous.DefaultContextWindow)
            && (!output || model.MaxTokens == previous.OutputTokens)
            && (!priceIn || model.InputPricePerMillion == previous.InputPrice)
            && (!priceOut || model.OutputPricePerMillion == previous.OutputPrice)
            && (!priceCached || model.CachedInputPricePerMillion == previous.CachedInputPrice)
            && (!reasoning || model.SupportsReasoning == previous.Reasoning))
        {
            return;
        }

        model.LastFetchedSpec = new ModelSpec(identifier ? model.Model : previous.Id, spec.Name,
            input ? model.MaxInputTokens : previous.ContextTokens,
            output ? model.MaxTokens : previous.OutputTokens,
            priceIn ? model.InputPricePerMillion : previous.InputPrice,
            priceOut ? model.OutputPricePerMillion : previous.OutputPrice,
            priceCached ? model.CachedInputPricePerMillion : previous.CachedInputPrice,
            reasoning ? model.SupportsReasoning : previous.Reasoning,
            input ? model.DefaultContextWindow == true : previous.DefaultContextWindow);
    }

    /// <summary>
    /// 把清单<b>落成真正可选的模型</b>,并返回这一家现在共有多少个模型。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只把 id 存进 <see cref="AiProvider.AvailableModels" /> 是不够的 —— 顶栏那个模型下拉读的是
    /// <see cref="AiProvider.Models" />(经 <see cref="AiSettings.ResolveModels" />),
    /// 清单不落进去,用户在界面上就一个也挑不着,"拉取"等于没做。
    /// </para>
    /// <para>
    /// <b>已有的模型原地更新,不重建</b>:<see cref="AiSettings.ActiveModelId" /> 指着某个
    /// <see cref="AiModelConfig.Id" />,重建会让当前选中的模型凭空消失;而且用户可能已经给它
    /// 改过名字、调过思考档位或专用提示词,那些都得留着 —— 这里只补规格,不动用户改过的东西。
    /// </para>
    /// </remarks>
    /// <param name="provider">要填充的供应商。</param>
    /// <param name="specs">拿到的规格清单。</param>
    public static int Materialise(AiProvider provider, IReadOnlyList<ModelSpec> specs)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(specs);
        if (specs.Count == 0)
        {
            return provider.Models.Count;
        }

        // 出厂那一条(通常是目录给的示例)先对齐到清单里真实存在的型号 ——
        // 它的 Id 可能正被 ActiveModelId 指着,换掉的话当前选中的模型就没了
        if (provider.Models.Count > 0 && provider.Models[0].LastFetchedSpec is { } previous
            && provider.Models[0].Model == previous.Id && ChooseDefault(provider.Models[0].Model, specs) is { } chosen)
        {
            Apply(provider.Models[0], chosen);
        }

        var known = new HashSet<string>(
            provider.Models.Select(m => m.Model).Where(id => id.Length > 0), StringComparer.OrdinalIgnoreCase);
        foreach (ModelSpec spec in specs)
        {
            if (!known.Add(spec.Id))
            {
                // 已经有了:补上规格(用户没填过的窗口/单价这时才有值),名字之类一律不碰
                AiModelConfig existing = provider.Models.First(
                    m => string.Equals(m.Model, spec.Id, StringComparison.OrdinalIgnoreCase));
                Apply(existing, spec);
                continue;
            }
            var model = new AiModelConfig();
            Apply(model, spec, newModel: true);
            provider.Models.Add(model);
        }
        return provider.Models.Count;
    }

    /// <summary>
    /// 拿到清单后这一家该默认用哪个模型。
    /// </summary>
    /// <remarks>
    /// 目录里的出厂示例优先 —— 那是挑过的。它已经不在清单里了(型号更新换代),
    /// 就退回<b>同前缀里最新的那个</b>:<c>gpt-5-codex</c> 没了,选 <c>gpt-5.3-codex</c>
    /// 显然比选字母序第一个的 <c>gpt-3.5</c> 合理。都对不上才取第一个。
    /// </remarks>
    /// <param name="preferred">目录里的出厂示例。</param>
    /// <param name="available">拿到的清单。</param>
    public static ModelSpec? ChooseDefault(string preferred, IReadOnlyList<ModelSpec> available)
    {
        ArgumentNullException.ThrowIfNull(available);
        if (available.Count == 0)
        {
            return null;
        }
        foreach (ModelSpec spec in available)
        {
            if (string.Equals(spec.Id, preferred, StringComparison.OrdinalIgnoreCase))
            {
                return spec;
            }
        }
        // 出厂示例没了:按"最长的共同前缀"找同一族里最新的那个
        ModelSpec? best = null;
        int bestScore = 0;
        foreach (ModelSpec spec in available)
        {
            int score = CommonPrefix(spec.Id, preferred);
            if (score > bestScore || (score == bestScore && best is not null
                                                         && string.CompareOrdinal(spec.Id, best.Id) > 0))
            {
                best = spec;
                bestScore = score;
            }
        }
        // 前缀太短(两三个字母)说明根本不是一族,别硬凑
        return bestScore >= 4 ? best : available[0];
    }

    private static int CommonPrefix(string left, string right)
    {
        int max = Math.Min(left.Length, right.Length);
        int count = 0;
        while (count < max && char.ToLowerInvariant(left[count]) == char.ToLowerInvariant(right[count]))
        {
            count++;
        }
        return count;
    }
}
