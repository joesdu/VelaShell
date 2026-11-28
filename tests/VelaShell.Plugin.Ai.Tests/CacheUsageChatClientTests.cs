using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// OpenAI 系适配器丢掉的缓存用量要捡回来:中转站 / OpenRouter 报的缓存写入、DeepSeek 旧口径只报的缓存读取。
/// 走真 SDK + 真适配器 —— 字段是在 SDK 模型里「被保留但没映射」,只有这样才测得出。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class CacheUsageChatClientTests
{
    private static async Task<UsageDetails> StreamUsageAsync(ChatProtocol protocol, string sse)
    {
        await using var stub = new SseStub(sse);
        using var context = new TestPluginContext();
        var provider = new AiProvider { BaseUrl = stub.BaseUrl, DefaultProtocol = protocol, Models = [new AiModelConfig { Model = "m" }] };
        var settings = new AiSettings { Providers = [provider] };
        var store = new AiSettingsStore(context);
        await store.AddProviderApiKeyAsync(settings, provider, "k");
        using IChatClient client = await store.CreateClientAsync(new ResolvedModel(provider, provider.Models[0]));
        UsageDetails? found = null;
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync("hi"))
        {
            found = update.Contents.OfType<UsageContent>().LastOrDefault()?.Details ?? found;
        }
        Assert.IsNotNull(found, "流里应当带用量");
        return found;
    }

    [TestMethod]
    public async Task ChatCompletions_RelayCacheWrite_IsReported()
    {
        UsageDetails usage = await StreamUsageAsync(ChatProtocol.OpenAiChatCompletions, """
            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"hi"},"finish_reason":null}]}

            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[],"usage":{"prompt_tokens":1120,"completion_tokens":5,"total_tokens":1125,"prompt_tokens_details":{"cached_tokens":120},"cache_creation_input_tokens":800}}

            data: [DONE]


            """);
        Assert.AreEqual(120, usage.CachedInputTokenCount);
        Assert.AreEqual(800, usage.AdditionalCounts?.GetValueOrDefault(CacheUsageChatClient.CacheWriteKey));
    }

    [TestMethod]
    public async Task ChatCompletions_OpenRouterWriteAndDeepSeekRead_AreReported()
    {
        UsageDetails usage = await StreamUsageAsync(ChatProtocol.OpenAiChatCompletions, """
            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[],"usage":{"prompt_tokens":1000,"completion_tokens":5,"total_tokens":1005,"prompt_tokens_details":{"cache_write_tokens":300},"prompt_cache_hit_tokens":600,"prompt_cache_miss_tokens":400}}

            data: [DONE]


            """);
        Assert.AreEqual(600, usage.CachedInputTokenCount, "没有 cached_tokens 时取 prompt_cache_hit_tokens");
        Assert.AreEqual(300, usage.AdditionalCounts?.GetValueOrDefault(CacheUsageChatClient.CacheWriteKey));
    }

    [TestMethod]
    public async Task Responses_CacheWrite_IsReported()
    {
        UsageDetails usage = await StreamUsageAsync(ChatProtocol.OpenAiResponses, """
            event: response.output_text.delta
            data: {"type":"response.output_text.delta","item_id":"i","output_index":0,"content_index":0,"delta":"hi","sequence_number":1}

            event: response.completed
            data: {"type":"response.completed","sequence_number":2,"response":{"id":"r","object":"response","created_at":1,"status":"completed","model":"m","output":[],"usage":{"input_tokens":1120,"input_tokens_details":{"cached_tokens":120,"cache_write_tokens":800},"output_tokens":5,"output_tokens_details":{"reasoning_tokens":0},"total_tokens":1125}}}


            """);
        Assert.AreEqual(120, usage.CachedInputTokenCount);
        Assert.AreEqual(800, usage.AdditionalCounts?.GetValueOrDefault(CacheUsageChatClient.CacheWriteKey));
    }

    [TestMethod]
    public async Task OfficialOpenAiUsage_WithoutWrites_StaysUntouched()
    {
        UsageDetails usage = await StreamUsageAsync(ChatProtocol.OpenAiChatCompletions, """
            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[],"usage":{"prompt_tokens":1000,"completion_tokens":5,"total_tokens":1005,"prompt_tokens_details":{"cached_tokens":800}}}

            data: [DONE]


            """);
        Assert.AreEqual(800, usage.CachedInputTokenCount);
        Assert.AreNotEqual(true, usage.AdditionalCounts?.ContainsKey(CacheUsageChatClient.CacheWriteKey), "官方接口不报写入就不编一个");
    }
}
