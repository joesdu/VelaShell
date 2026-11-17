using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ChatCompletion = OpenAI.Chat.ChatCompletion;
using ChatTokenUsage = OpenAI.Chat.ChatTokenUsage;
using StreamingChatCompletionUpdate = OpenAI.Chat.StreamingChatCompletionUpdate;

namespace VelaShell.Plugin.Ai.Chat;

/// <summary>
/// 把 OpenAI 系适配器丢掉的缓存用量捡回来:缓存<b>写入</b>,以及只用非标准字段报的缓存<b>读取</b>。
/// </summary>
/// <remarks>
/// <para>
/// Microsoft.Extensions.AI.OpenAI(11.0)只映射 <c>prompt_tokens_details.cached_tokens</c> /
/// <c>input_tokens_details.cached_tokens</c>。转发 Claude 的中转站与 OpenRouter 会多报缓存写入
/// (<c>cache_creation_input_tokens</c>、<c>*_tokens_details.cache_write_tokens</c>),DeepSeek 旧口径只报
/// <c>prompt_cache_hit_tokens</c>。这些字段适配器不认,于是用量条上「缓存写入」永远是 0。
/// </para>
/// <para>
/// 字段并没有丢:OpenAI SDK 的用量对象把不认识的属性留在模型里,经 <see cref="ModelReaderWriter" />
/// 写回 JSON 时原样带出(实测 OpenAI 2.x)。这里从原始对象取 JSON,补进 <see cref="UsageDetails" />:
/// 写入记在 <see cref="CacheWriteKey" />(与 Anthropic 适配器同一个键,下游只认一处),
/// 读取只在适配器没给出时补。官方 OpenAI 接口本来就不报缓存写入(它的缓存不单独计费),那时仍为 0。
/// </para>
/// </remarks>
internal sealed class CacheUsageChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    /// <summary>缓存写入 token 在 <see cref="UsageDetails.AdditionalCounts" /> 里的键(Anthropic 适配器用的就是它)。</summary>
    public const string CacheWriteKey = "CacheCreationInputTokens";

    private static readonly ModelReaderWriterOptions JsonFormat = new("J");

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ChatResponse response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        Enrich(response.Usage, RawUsage(response.RawRepresentation));
        return response;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(messages, options, cancellationToken)
                           .ConfigureAwait(false))
        {
            foreach (AIContent content in update.Contents)
            {
                if (content is UsageContent usage)
                {
                    Enrich(usage.Details, usage.RawRepresentation as ChatTokenUsage ?? RawUsage(update.RawRepresentation));
                }
            }
            yield return update;
        }
    }

#pragma warning disable OPENAI001 // Responses API 在 OpenAI SDK 中标记为实验性
    /// <summary>从适配器挂着的原始响应里找出 SDK 的用量对象。</summary>
    private static object? RawUsage(object? raw) => raw switch
    {
        ChatCompletion completion => completion.Usage,
        StreamingChatCompletionUpdate chunk => chunk.Usage,
        OpenAI.Responses.ResponseResult response => response.Usage,
        OpenAI.Responses.StreamingResponseCompletedUpdate completed => completed.Response?.Usage,
        OpenAI.Responses.StreamingResponseIncompleteUpdate incomplete => incomplete.Response?.Usage,
        OpenAI.Responses.StreamingResponseFailedUpdate failed => failed.Response?.Usage,
        _ => null
    };

    private static void Enrich(UsageDetails? details, object? rawUsage)
    {
        if (details is null || rawUsage is null)
        {
            return;
        }
        BinaryData json;
        try
        {
            json = ModelReaderWriter.Write(rawUsage, JsonFormat, OpenAI.OpenAIContext.Default);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or FormatException)
        {
            return; // 拿不到原始字段就保持适配器给的数,不为这点统计让请求失败
        }
        using var document = JsonDocument.Parse(json);
        Apply(details, document.RootElement);
    }
#pragma warning restore OPENAI001

    /// <summary>按原始 usage JSON 补缓存写入与(缺失时的)缓存读取。适配器已给出的值不覆盖。</summary>
    /// <param name="details">要补的用量。</param>
    /// <param name="usage">原始 usage 对象(Chat Completions 或 Responses 口径)。</param>
    internal static void Apply(UsageDetails details, JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        if (details.AdditionalCounts?.ContainsKey(CacheWriteKey) != true
            && First(usage, ["cache_creation_input_tokens"], ["prompt_tokens_details", "cache_write_tokens"],
                ["input_tokens_details", "cache_write_tokens"], ["prompt_tokens_details", "cache_creation_tokens"],
                ["input_tokens_details", "cache_creation_tokens"]) is > 0 and var written)
        {
            details.AdditionalCounts ??= [];
            details.AdditionalCounts[CacheWriteKey] = written;
        }
        if ((details.CachedInputTokenCount ?? 0) == 0
            && First(usage, ["cache_read_input_tokens"], ["prompt_cache_hit_tokens"]) is > 0 and var read)
        {
            details.CachedInputTokenCount = read;
        }
    }

    private static long? First(JsonElement usage, params string[][] paths)
    {
        foreach (string[] path in paths)
        {
            JsonElement node = usage;
            bool found = true;
            foreach (string name in path)
            {
                if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(name, out node))
                {
                    found = false;
                    break;
                }
            }
            if (found && node.ValueKind == JsonValueKind.Number && node.TryGetInt64(out long value))
            {
                return value;
            }
        }
        return null;
    }
}
