using System.Net.Http.Headers;
using System.Text.Json;

namespace VelaShell.Plugin.Ai.Configuration;

/// <summary>
/// 去端点<b>自己</b>那儿问"你供应哪些模型"(OpenAI 系的 <c>/models</c>、Anthropic 的 <c>/v1/models</c>)。
/// </summary>
/// <remarks>
/// <para>
/// <b>它与 <see cref="ModelsDevCatalog" /> 是互补,不是替代。</b> 两者各知道一半:
/// </para>
/// <list type="bullet">
/// <item>端点知道<b>这个地址实际供应哪些型号</b> —— 中转站只转发其中一部分,自行部署的
/// Ollama 装了哪几个权重,models.dev 一概不知道,也没法知道。</item>
/// <item>models.dev 提供<b>规格</b>(上下文窗口、三档单价);端点若提供正数 <c>context_length</c>,也保留作未匹配模型的窗口回退。
/// 那两项恰恰是本插件里最难填、填错了又<b>不报错</b>的东西(窗口错则输入框下方的占比错,
/// 单价错则花费估算错)。</item>
/// </list>
/// <para>
/// 所以拉取的顺序是:先问端点拿真实清单,再按 id 去 models.dev 配规格
/// (见 <see cref="ModelsDevCatalog.Describe" />);端点没有这条接口(订阅型私有后端就没有)
/// 或请求失败时,整条退回 models.dev 的清单 —— 少一份准确,不至于一无所有。
/// </para>
/// <para>
/// <b>不发请求就不知道结果。</b> 这里只负责"把请求拼对 + 把回应解开",不判断某家有没有这条接口:
/// 兼容端点的花样太多,与其维护一张必然过期的名单,不如发一次 GET,失败就回落。
/// </para>
/// </remarks>
internal static class EndpointModelCatalog
{
    /// <summary>Anthropic 要求每条请求都带的 API 版本头。</summary>
    /// <remarks>
    /// 与聊天请求一致:聊天请求由 SDK 自己带版本头,13.x 起是 <c>2026-10-21</c>,这条自己拼的列表请求跟着发同一个
    /// (维护者 2026-10-25 决定,#657 复审跟进②)。以前这里留着 <c>2023-06-01</c>,同一把 Key 在拉清单与聊天两条路上
    /// 碰到的是两个版本的接口,清单拉得到、聊天却 400 这类问题就无从对照。两条路都用 <c>x-api-key</c>。<c>AnthropicWireTests</c> 拿真 SDK 的请求头比对,SDK 换版本号时会红。
    /// </remarks>
    public const string AnthropicVersion = "2026-10-21";

    /// <summary>
    /// 拼出模型列表接口的地址;地址不可用时返回 null。
    /// </summary>
    /// <remarks>
    /// <b>与建聊天客户端时的处理保持一致</b>(见 <c>AiSettingsStore.CreateClientAsync</c>):
    /// OpenAI 系的基地址按惯例已含 <c>/v1</c>,SDK 在其后接 <c>/chat/completions</c>,
    /// 这里照样在其后接 <c>/models</c>;Anthropic 的基地址按惯例<b>不含</b> <c>/v1</c>
    /// (SDK 自己补),所以这里得自己补上。两边都不猜、不改写用户填的东西。
    /// </remarks>
    /// <param name="baseUrl">供应商基地址。</param>
    /// <param name="protocol">线协议。</param>
    public static Uri? Endpoint(string? baseUrl, ChatProtocol protocol)
    {
        string root = (baseUrl ?? "").Trim().TrimEnd('/');
        if (root.Length == 0)
        {
            return null;
        }
        if (protocol == ChatProtocol.AnthropicMessages && !root.EndsWith("/v1", StringComparison.Ordinal))
        {
            root += "/v1";
        }
        return Uri.TryCreate($"{root}/models", UriKind.Absolute, out Uri? url)
               && url.Scheme is "http" or "https"
            ? url
            : null;
    }

    /// <summary>
    /// 拼一条带好鉴权的请求;地址不可用时返回 null。
    /// </summary>
    /// <remarks>
    /// API Key 的请求头跟随协议：OpenAI 用 <c>Authorization: Bearer</c>，Anthropic 用 <c>x-api-key</c>。
    /// </remarks>
    /// <param name="baseUrl">供应商基地址。</param>
    /// <param name="protocol">线协议。</param>
    /// <param name="credential">API Key 凭据。</param>
    public static HttpRequestMessage? Request(string? baseUrl, ChatProtocol protocol, ProviderCredential credential)
    {
        if (credential.IsBearerToken || credential.BaseUrl is not null)
            throw new NotSupportedException("Account sign-in is no longer supported; configure an API key instead.");
        if (Endpoint(baseUrl, protocol) is not { } url)
        {
            return null;
        }
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        string? secret = credential.Value;
        if (protocol == ChatProtocol.AnthropicMessages)
        {
            request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
            if (!string.IsNullOrWhiteSpace(secret))
            {
                request.Headers.TryAddWithoutValidation("x-api-key", secret);
            }
        }
        else if (!string.IsNullOrWhiteSpace(secret))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {secret}");
        }
        foreach ((string name, string value) in credential.Headers ?? [])
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
        if (!request.Headers.Contains("User-Agent")) request.Headers.UserAgent.ParseAdd(AiHttp.UserAgent);
        return request;
    }

    /// <summary>翻页时每页要多少条:Anthropic 的上限(默认只给 20)。</summary>
    private const int PageSize = 1000;

    /// <summary>最多翻几页:一直说"还有"的端点不能把拉取拖成死循环。</summary>
    private const int MaxPages = 20;

    /// <summary>
    /// 拿这个端点供应的模型 id 与可选上下文窗口;回应声明还有下一页时接着翻完。
    /// </summary>
    /// <remarks>
    /// <para>拿不到不抛异常 —— 调用方要的是"回落到 models.dev",不是一个要处理的异常。</para>
    /// <para>
    /// <b>清单必须是全的。</b>拉回来的清单会被当成"这个端点只供应这些"去清理自动模型,
    /// 只拿到第一页就等于把后面几页的模型全判成下架。Anthropic 的 <c>/v1/models</c> 默认一页 20 条,
    /// 靠 <c>has_more</c> / <c>last_id</c> 翻页;第一次请求照旧不带参数(不认查询参数的中转站不受影响),
    /// 回应说还有下一页时才带 <c>limit</c> 与 <c>after_id</c> 接着要。翻到一半失败、
    /// 游标缺失或不前进,都按"拿不到完整清单"返回空。
    /// </para>
    /// </remarks>
    /// <param name="http">发请求用的客户端。</param>
    /// <param name="baseUrl">供应商基地址。</param>
    /// <param name="protocol">线协议。</param>
    /// <param name="credential">凭据。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>按型号排序去重的模型及窗口;窗口未知时为0,拿不到完整清单时为空。</returns>
    public static async Task<IReadOnlyList<(string Id, int ContextTokens)>> FetchAsync(HttpClient http, string? baseUrl,
        ChatProtocol protocol, ProviderCredential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        var models = new SortedDictionary<string, (string Id, int ContextTokens)>(StringComparer.OrdinalIgnoreCase);
        string? after = null;
        for (int page = 0; page < MaxPages; page++)
        {
            using HttpRequestMessage? request = Request(baseUrl, protocol, credential);
            if (request is null)
            {
                return [];
            }
            if (after is not null)
            {
                Uri first = request.RequestUri!;
                request.RequestUri = new Uri(
                    $"{first.AbsoluteUri}{(first.Query.Length == 0 ? "?" : "&")}limit={PageSize}&after_id={Uri.EscapeDataString(after)}");
            }
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!Collect(body, models, out bool hasMore, out string? lastId))
            {
                return [];
            }
            if (!hasMore)
            {
                return [.. models.Values];
            }
            if (string.IsNullOrEmpty(lastId) || lastId == after)
            {
                return [];
            }
            after = lastId;
        }
        return [];
    }

    /// <summary>
    /// 从回应里解出模型 id。
    /// </summary>
    /// <remarks>
    /// 认三种形状,因为线上确实就这三种:OpenAI 与 Anthropic 都是
    /// <c>{"data":[{"id":…}]}</c>;有的中转站直接回一个裸数组;Ollama 的原生接口是
    /// <c>{"models":[{"name":…}]}</c>。差别只在外面那层壳和 id 的字段名,解一次就够。
    /// </remarks>
    /// <param name="json">回应正文。</param>
    public static IReadOnlyList<(string Id, int ContextTokens)> Parse(string json)
    {
        var models = new SortedDictionary<string, (string Id, int ContextTokens)>(StringComparer.OrdinalIgnoreCase);
        Collect(json, models, out _, out _);
        return [.. models.Values];
    }

    /// <summary>解一页并并进 <paramref name="models" />;回应里根本没有模型列表时返回 false。</summary>
    /// <param name="json">回应正文。</param>
    /// <param name="models">跨页累积的结果(按型号去重)。</param>
    /// <param name="hasMore">回应声明还有下一页(Anthropic 的 <c>has_more</c>)。</param>
    /// <param name="lastId">下一页的游标(Anthropic 的 <c>last_id</c>)。</param>
    private static bool Collect(string json, SortedDictionary<string, (string Id, int ContextTokens)> models,
        out bool hasMore, out string? lastId)
    {
        hasMore = false;
        lastId = null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return false;
        }
        using (document)
        {
            JsonElement root = document.RootElement;
            JsonElement list = root.ValueKind switch
            {
                JsonValueKind.Array => root,
                JsonValueKind.Object => Array(root, "data") ?? Array(root, "models") ?? default,
                _ => default
            };
            if (list.ValueKind != JsonValueKind.Array)
            {
                return false;
            }
            hasMore = root.ValueKind == JsonValueKind.Object
                      && root.TryGetProperty("has_more", out JsonElement more) && more.ValueKind == JsonValueKind.True;
            lastId = root.ValueKind == JsonValueKind.Object ? Text(root, "last_id") : null;
            foreach (JsonElement item in list.EnumerateArray())
            {
                if (IdOf(item) is { } id && IsUsableForChat(id))
                {
                    int contextTokens = item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("context_length", out JsonElement length)
                        && length.ValueKind == JsonValueKind.Number && length.TryGetInt32(out int value) && value > 0
                            ? value : 0;
                    if (!models.TryGetValue(id, out (string Id, int ContextTokens) existing)) models.Add(id, (id, contextTokens));
                    else if (existing.ContextTokens == 0 && contextTokens > 0) models[id] = (existing.Id, contextTokens);
                }
            }
            return true;
        }
    }

    /// <summary>
    /// 这一条值不值得摆进模型下拉。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>/models</c> 把一家的<b>全部</b>模型都报上来,包括向量、语音、画图、审核那些
    /// 根本进不了对话的。它们不像 models.dev 那样带着能力字段可判,只能按名字认 ——
    /// 所以这里是<b>启发式</b>,宁可漏筛也不误筛:漏一个,列表里多一条用不上的;
    /// 误筛一个,用户在下拉里<b>永远找不到</b>自己要的模型,而且不会有任何提示。
    /// </para>
    /// <para>
    /// 长词按子串认(<c>embedding</c> 不会出现在聊天模型的名字里);短词按<b>整段</b>认
    /// (<c>tts</c> 当子串会误伤,比如某天出现一个叫 <c>…-ttsx-…</c> 的东西)。
    /// </para>
    /// </remarks>
    private static bool IsUsableForChat(string id)
    {
        foreach (string word in NonChatWords)
        {
            if (id.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        foreach (string segment in id.Split(SegmentSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string word in NonChatSegments)
            {
                if (string.Equals(segment, word, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>按子串认的非聊天模型(足够长、不会误伤)。</summary>
    private static readonly string[] NonChatWords =
        ["embedding", "rerank", "whisper", "moderation", "stable-diffusion", "midjourney"];

    /// <summary>按整段认的非聊天模型(太短,当子串会误伤)。</summary>
    private static readonly string[] NonChatSegments = ["embed", "tts", "stt", "ocr", "asr"];

    private static readonly char[] SegmentSeparators = ['-', '_', '.', '/', ':'];

    /// <summary>一条记录的 id:两套字段名都试,裸字符串也认。</summary>
    private static string? IdOf(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.String)
        {
            return Trimmed(item.GetString());
        }
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        return Trimmed(Text(item, "id")) ?? Trimmed(Text(item, "name")) ?? Trimmed(Text(item, "model"));
    }

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Text(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static JsonElement? Array(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? value
            : null;
}
