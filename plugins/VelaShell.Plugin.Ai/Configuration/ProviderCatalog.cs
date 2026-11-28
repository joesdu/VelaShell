namespace VelaShell.Plugin.Ai.Configuration;

/// <summary>
/// 一家端点的"脾气":它不收哪些东西。
/// </summary>
/// <remarks>
/// <para>
/// <b>这份数据的权威副本在目录里,不在用户的配置里。</b>订阅型的私有后端是标准协议的受限子集,
/// 多发一个它不认的字段就整轮 400,而且一次只告诉你一个 —— 于是这个清单会随着实测不断变长。
/// </para>
/// <para>
/// 一开始它是跟着供应商一起落盘的,结果是:<b>每加一条新规则,已经连上的用户都得重新登录一次
/// 才能拿到</b>(字段是后加的,老配置反序列化出来是默认值)。真机验收时就栽在这儿 ——
/// 同一个 400 修了两轮还在。现在改成发请求时按 <see cref="AiProvider.CatalogId" />
/// 从目录现读,新规则对所有人立即生效。
/// </para>
/// </remarks>
/// <param name="StoreResponses">允许服务端存下这一轮(OpenAI Responses 的 <c>store</c>)。</param>
/// <param name="AllowSystemMessages">收不收 <c>system</c> 角色的消息。</param>
/// <param name="UnsupportedParameters">不认的请求参数,每行一个线上字段名。</param>
public sealed record EndpointQuirks(
    bool StoreResponses = true,
    bool AllowSystemMessages = true,
    string UnsupportedParameters = "")
{
    /// <summary>标准端点:什么都收。</summary>
    public static readonly EndpointQuirks None = new();

    /// <summary>
    /// 这个供应商此刻该按哪份脾气发请求。
    /// </summary>
    /// <remarks>
    /// 目录里有就用目录的(<b>永远是最新的</b>);用户手工建的自定义供应商没有目录 id,
    /// 那就用它自己身上那份。
    /// </remarks>
    /// <param name="provider">供应商。</param>
    public static EndpointQuirks Of(AiProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        // store 在用户自己接的端点上跟着设置页的开关走、默认关(见 AiProvider.CustomResponsesStore);
        // 目录里那些具名的供应商(含官方 openai)照旧以目录为准
        bool customStore = provider.CustomResponsesStore == true;
        return ProviderCatalog.Find(provider.CatalogId) is { } entry
            ? entry.UserDefinedEndpoint ? entry.Quirks with { StoreResponses = customStore } : entry.Quirks
            : new EndpointQuirks(customStore, provider.AllowSystemMessages, provider.UnsupportedParameters);
    }

    /// <summary>这个供应商的 <c>store</c> 归不归设置页那个开关管(用户自己接的端点)。</summary>
    /// <param name="provider">供应商。</param>
    public static bool HasCustomStoreSwitch(AiProvider provider)
        => ProviderCatalog.Find(provider.CatalogId) is not { } entry || entry.UserDefinedEndpoint;
}

/// <summary>
/// 目录里的一条:一家供应商的出厂配置 + 它支持哪种接入方式。
/// </summary>
/// <param name="Id">目录 id;落在 <see cref="AiProvider.CatalogId" /> 上,用来认出"这一家已经加过了"。</param>
/// <param name="Name">显示名。</param>
/// <param name="Models">副标题:示例模型。<b>只是出厂示例</b>,各家的型号更新得比本程序快,加完请按实际改。</param>
/// <param name="Monogram">
/// 列表左侧那枚方牌上的字。<b>刻意用字母而不是各家的商标</b>:商标是别人的资产,
/// 随程序分发要看授权脸色,而一枚字母牌在这套等宽、克制的界面里也并不吃亏。
/// </param>
/// <param name="Auth">出厂的接入方式。</param>
/// <remarks>见 <see cref="EndpointQuirks" />。</remarks>
/// <param name="Create">造一份出厂配置(含模型、协议、地址)。</param>
public sealed record ProviderCatalogEntry(
    string Id,
    string Name,
    string Models,
    string Monogram,
    AuthMethod Auth,
    Func<AiProvider> Create)
{
    /// <summary>要用户自己填地址才用得起来(自行部署 / 自定义 / 按资源分配地址的云服务)。</summary>
    public bool NeedsBaseUrl { get; init; }

    /// <summary>
    /// 这一家在 <b>models.dev</b> 里的供应商 id;空 = 那边没有收录(本地自部署、自定义端点)。
    /// </summary>
    /// <remarks>
    /// 两边的 id 不总是一样(我们叫 <c>moonshot</c>,那边叫 <c>moonshotai</c>),所以单列一个字段
    /// 而不是拿目录 id 硬套 —— 套不上时是<b>静默</b>拉不到模型,最难查的那种。
    /// </remarks>
    public string ModelsDevId { get; init; } = "";

    /// <summary>
    /// 这一条依赖的是<b>没有公开承诺稳定</b>的接口。界面上会明确标出来。
    /// </summary>
    /// <remarks>
    /// 用户有权知道自己接的是哪一类东西:官方公开 API 坏了是事故,
    /// 而这类接口本来就可能在任何一天换掉请求形状 —— 到时候"AI 突然不能用了"
    /// 不该让人以为是本程序的 bug。
    /// </remarks>
    public bool Experimental { get; init; }

    /// <summary>这一家不收哪些东西(权威副本在这儿,不在用户配置里)。</summary>
    public EndpointQuirks Quirks { get; init; } = EndpointQuirks.None;

    /// <summary>这一条走的是订阅登录。</summary>
    public bool IsSubscription => Auth == AuthMethod.Subscription;

    /// <summary>
    /// 用户自己接的端点：地址、协议都是用户定的，
    /// 目录里没有一份可信的脾气可抄,<c>store</c> 由设置页的开关决定(见 <see cref="AiProvider.CustomResponsesStore" />)。
    /// </summary>
    public bool UserDefinedEndpoint => Id.StartsWith("custom-", StringComparison.Ordinal);

    /// <summary>
    /// 根据模板和目录 id 初始化供应商。
    /// </summary>
    public AiProvider CreateProvider()
    {
        AiProvider provider = Create();
        provider.CatalogId = Id;
        // 也拷一份到供应商身上:设置页要显示、自定义供应商要能改。
        // 但发请求时以目录那份为准(见 EndpointQuirks.Of)—— 落盘的这份只会越来越旧。
        provider.StoreResponses = Quirks.StoreResponses;
        provider.AllowSystemMessages = Quirks.AllowSystemMessages;
        provider.UnsupportedParameters = Quirks.UnsupportedParameters;
        foreach (AiModelConfig model in provider.Models)
        {
            model.LastFetchedSpec = new ModelSpec(model.Model, model.Name, model.MaxInputTokens, model.MaxTokens,
                model.InputPricePerMillion, model.OutputPricePerMillion, model.CachedInputPricePerMillion,
                model.SupportsReasoning);
        }

        return provider;
    }
}

/// <summary>新建仅使用自定义端点，已保存的命名模板仍可解析。</summary>
public static class ProviderCatalog
{
    /// <summary>
    /// 本目录的 id → <b>models.dev</b> 里的供应商 id。
    /// </summary>
    /// <remarks>
    /// 集中放一处,而不是在每条记录里各插一行:两边命名不一致的有好几家
    /// (<c>moonshot</c>→<c>moonshotai</c>、<c>zhipu</c>→<c>zhipuai</c>、<c>qwen</c>→<c>alibaba</c>、
    /// <c>together</c>→<c>togetherai</c>、<c>fireworks</c>→<c>fireworks-ai</c>),
    /// 散在各处对不上时是<b>静默</b>拉不到模型,最难查的那一类。
    /// <para>
    /// 没列进来的是那边确实没有的:本地自部署(Ollama)与两条自定义端点。
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, string> ModelsDevIds = new(StringComparer.Ordinal)
    {
        ["openai"] = "openai",
        ["anthropic"] = "anthropic",
        ["xai"] = "xai",
        ["google"] = "google",
        ["deepseek"] = "deepseek",
        ["moonshot"] = "moonshotai",
        ["zhipu"] = "zhipuai",
        ["qwen"] = "alibaba",
        ["together"] = "togetherai",
        ["fireworks"] = "fireworks-ai",
        ["groq"] = "groq",
        ["mistral"] = "mistral",
        ["cerebras"] = "cerebras",
        ["deepinfra"] = "deepinfra",
        ["nvidia"] = "nvidia",
        ["perplexity"] = "perplexity",
        ["cohere"] = "cohere",
        ["baseten"] = "baseten",
        ["upstage"] = "upstage",
        ["minimax"] = "minimax",
        ["zenmux"] = "zenmux",
        ["llmgateway"] = "llmgateway",
    };

    /// <summary>包括既有 API Key 配置使用的命名模板。</summary>
    private static IReadOnlyList<ProviderCatalogEntry> Entries { get; } =
    [
        // ── 填 API Key ──────────────────────────────────────────────
        new("openai", "OpenAI", "e.g. gpt-5 · gpt-5-mini · o4-mini", "AI", AuthMethod.ApiKey,
            () => Key("OpenAI", "https://api.openai.com/v1", ChatProtocol.OpenAiResponses,
                new AiModelConfig { Model = "gpt-5", MaxInputTokens = 400000 })),

        new("anthropic", "Anthropic Claude", "e.g. claude-opus-5 · claude-sonnet-5", "AN", AuthMethod.ApiKey,
            () => Key("Anthropic", "https://api.anthropic.com", ChatProtocol.AnthropicMessages,
                new AiModelConfig { Model = "claude-opus-5", MaxInputTokens = 200000 })),

        new("xai", "xAI Grok", "e.g. grok-4.6 · grok-4.5 · grok-4.3", "XA", AuthMethod.ApiKey,
            () => Key("xAI", "https://api.x.ai/v1", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "grok-4.6", MaxInputTokens = 256000 })),

        new("google", "Google Gemini", "e.g. gemini-2.5-pro · gemini-2.5-flash", "GE", AuthMethod.ApiKey,
            () => Key("Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai",
                ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "gemini-2.5-pro", MaxInputTokens = 1000000 })),

        new("deepseek", "DeepSeek", "e.g. deepseek-v4-pro · deepseek-v4-flash", "DS", AuthMethod.ApiKey,
            () => Key("DeepSeek", "https://api.deepseek.com/v1", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "deepseek-v4-pro", MaxInputTokens = 64000 })),

        new("moonshot", "Moonshot Kimi", "e.g. kimi-k3 · kimi-k2.7-code", "KI", AuthMethod.ApiKey,
            () => Key("Moonshot", "https://api.moonshot.cn/v1", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "kimi-k3", MaxInputTokens = 128000 })),

        new("zhipu", "Z.AI / 智谱 GLM", "e.g. glm-5.3 · glm-4.7", "GL", AuthMethod.ApiKey,
            () => Key("Z.AI", "https://open.bigmodel.cn/api/paas/v4", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "glm-5.3", MaxInputTokens = 128000 })),

        new("qwen", "阿里云百炼 Qwen", "e.g. qwen-max · qwen-plus", "QW", AuthMethod.ApiKey,
            () => Key("Qwen", "https://dashscope.aliyuncs.com/compatible-mode/v1",
                ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "qwen-max", MaxInputTokens = 128000 })),

        new("together", "Together AI", "e.g. deepseek-ai/DeepSeek-V3 · Qwen/Qwen2.5-72B-Instruct", "TG",
            AuthMethod.ApiKey,
            () => Key("Together AI", "https://api.together.ai/v1", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "deepseek-ai/DeepSeek-V3", MaxInputTokens = 128000 })),

        new("fireworks", "Fireworks AI", "e.g. deepseek-v4-pro · kimi-k3 · glm-5.3", "FW", AuthMethod.ApiKey,
            () => Key("Fireworks AI", "https://api.fireworks.ai/inference/v1", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig
                {
                    Model = "accounts/fireworks/models/deepseek-v4-pro-0813",
                    MaxInputTokens = 128000
                })),

        new("groq", "Groq", "e.g. llama-3.3-70b-versatile", "GQ", AuthMethod.ApiKey,
            () => Key("Groq", "https://api.groq.com/openai/v1", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "llama-3.3-70b-versatile", MaxInputTokens = 128000 })),

        new("mistral", "Mistral AI", "e.g. mistral-large-latest", "MI", AuthMethod.ApiKey,
            () => Key("Mistral AI", "https://api.mistral.ai/v1", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "mistral-large-latest", MaxInputTokens = 128000 })),

        new("cerebras", "Cerebras", "e.g. qwen-3-coder-480b · gpt-oss-120b", "CB", AuthMethod.ApiKey,
            () => Key("Cerebras", "https://api.cerebras.ai/v1", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "qwen-3-coder-480b", MaxInputTokens = 128000 })),

        new("deepinfra", "Deep Infra", "e.g. deepseek-ai/DeepSeek-V3 · Qwen/Qwen3-235B", "DI", AuthMethod.ApiKey,
            () => Key("Deep Infra", "https://api.deepinfra.com/v1/openai", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "deepseek-ai/DeepSeek-V3", MaxInputTokens = 128000 })),

        new("nvidia", "NVIDIA NIM", "e.g. deepseek-ai/deepseek-r1 · qwen/qwen3-coder", "NV", AuthMethod.ApiKey,
            () => Key("NVIDIA NIM", "https://integrate.api.nvidia.com/v1", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "deepseek-ai/deepseek-r1", MaxInputTokens = 128000 })),

        new("perplexity", "Perplexity", "e.g. sonar-pro · sonar-reasoning-pro", "PX", AuthMethod.ApiKey,
            () => Key("Perplexity", "https://api.perplexity.ai", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "sonar-pro", MaxInputTokens = 200000 })),

        new("cohere", "Cohere", "e.g. command-a-03-2025 · command-r-plus", "CO", AuthMethod.ApiKey,
            () => Key("Cohere", "https://api.cohere.com/compatibility/v1", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "command-a-03-2025", MaxInputTokens = 256000 })),

        new("baseten", "Baseten", "e.g. deepseek-ai/DeepSeek-V3 · moonshotai/Kimi-K2", "BT", AuthMethod.ApiKey,
            () => Key("Baseten", "https://inference.baseten.co/v1", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "deepseek-ai/DeepSeek-V3", MaxInputTokens = 128000 })),

        new("upstage", "Upstage Solar", "e.g. solar-pro2", "UP", AuthMethod.ApiKey,
            () => Key("Upstage", "https://api.upstage.ai/v1/solar", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "solar-pro2", MaxInputTokens = 64000 })),

        new("minimax", "MiniMax", "e.g. MiniMax-M2", "MM", AuthMethod.ApiKey,
            // 这一家给的是 Anthropic 兼容端点,不是 OpenAI 的 —— 协议别跟着邻居抄
            () => Key("MiniMax", "https://api.minimax.io/anthropic/v1", ChatProtocol.AnthropicMessages,
                new AiModelConfig { Model = "MiniMax-M2", MaxInputTokens = 200000 })),

        new("zenmux", "ZenMux", "Multiple models behind one key", "ZM", AuthMethod.ApiKey,
            () => Key("ZenMux", "https://zenmux.ai/api/v1", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "anthropic/claude-sonnet-4.5", MaxInputTokens = 200000 })),

        new("llmgateway", "LLM Gateway", "Multiple models behind one key", "LG", AuthMethod.ApiKey,
            () => Key("LLM Gateway", "https://api.llmgateway.io/v1", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "openai/gpt-5", MaxInputTokens = 200000 })),

        new("ollama", "Ollama (local)", "Locally served models · no key needed", "OL", AuthMethod.ApiKey,
            () => Key("Ollama", "http://localhost:11434/v1", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "llama3.1" })),

        new("custom-openai", "Custom (OpenAI compatible)", "Any relay or self-hosted OpenAI-compatible endpoint", "CU",
            AuthMethod.ApiKey,
            () => Key("Custom", "", ChatProtocol.OpenAiChatCompletions, new AiModelConfig()))
        {
            NeedsBaseUrl = true
        },

        new("custom-anthropic", "Custom (Anthropic compatible)", "Any relay speaking the Anthropic Messages API", "CA",
            AuthMethod.ApiKey,
            () => Key("Custom (Anthropic)", "", ChatProtocol.AnthropicMessages, new AiModelConfig()))
        {
            NeedsBaseUrl = true
        }
    ];

    /// <summary>完整模板目录供已保存的配置解析端点限制和模型规格；不作为新增入口。</summary>
    private static IReadOnlyList<ProviderCatalogEntry> Templates { get; } =
        [.. Entries.Select(entry => ModelsDevIds.TryGetValue(entry.Id, out string? id)
            ? entry with { ModelsDevId = id }
            : entry)];

    /// <summary>新增供应商仅提供这两种自定义 API 端点。</summary>
    public static IReadOnlyList<ProviderCatalogEntry> All { get; } =
        [.. Templates.Where(entry => entry.Id is "custom-openai" or "custom-anthropic")];

    /// <summary>按已保存的目录 id 解析完整模板，包括不再提供新建入口的模板。</summary>
    public static ProviderCatalogEntry? Find(string? catalogId)
        => catalogId is null ? null : Templates.FirstOrDefault(entry => entry.Id == catalogId);

    /// <summary>「自定义(OpenAI 兼容)」那一条 —— 手工新建供应商时的兜底出厂值。</summary>
    public static ProviderCatalogEntry Custom => All.First(e => e.Id == "custom-openai");

    private static AiProvider Key(string name, string baseUrl, ChatProtocol protocol, AiModelConfig model)
        => new()
        {
            Name = name,
            BaseUrl = baseUrl,
            DefaultProtocol = protocol,
            Auth = AuthMethod.ApiKey,
            Models = [model]
        };
}
