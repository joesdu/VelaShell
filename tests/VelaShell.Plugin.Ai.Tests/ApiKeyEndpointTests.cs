using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

[TestClass]
[TestCategory("Plugins")]
public sealed class ApiKeyEndpointTests
{
    // A saved API-key endpoint may specify restrictions independently of a template.
    internal static AiProvider RestrictedResponsesProvider() => new()
    {
        BaseUrl = "https://relay.example/v1", DefaultProtocol = ChatProtocol.OpenAiResponses,
        AllowSystemMessages = false, StoreResponses = false,
        UnsupportedParameters = "max_output_tokens\ntemperature\ntop_p\nstop\nfrequency_penalty\npresence_penalty\nseed",
        Models = [new AiModelConfig { Model = "m" }]
    };

    [TestMethod]
    public void SavedEndpoint_StripsEveryParameterConfiguredAsUnsupported()
    {
        AiProvider provider = RestrictedResponsesProvider();
        var options = new Microsoft.Extensions.AI.ChatOptions
        {
            MaxOutputTokens = 8192,
            Temperature = 0.7f,
            TopP = 0.9f,
            StopSequences = ["STOP"],
            FrequencyPenalty = 1,
            PresencePenalty = 1,
            Seed = 42
        };

        AiSettingsStore.ApplyEndpointQuirks(options, new ResolvedModel(provider, provider.Models[0]));

        Assert.IsNull(options.MaxOutputTokens);
        Assert.IsNull(options.Temperature);
        Assert.IsNull(options.TopP);
        Assert.IsNull(options.StopSequences);
        Assert.IsNull(options.FrequencyPenalty);
        Assert.IsNull(options.PresencePenalty);
        Assert.IsNull(options.Seed);
        // 不给第三方做服务端响应存储
        Assert.IsFalse(provider.StoreResponses);
        Assert.IsNotNull(options.RawRepresentationFactory, "得把 store: false 递到原生请求上");
    }

    [TestMethod]
    public void OrdinaryProviders_KeepEveryParameterTheUserSet()
    {
        // 官方 OpenAI 走的是公开 Responses API,这些都是正常能力,不该被我们摘掉
        AiProvider openai = ProviderCatalog.Find("openai")!.CreateProvider();
        var options = new Microsoft.Extensions.AI.ChatOptions { MaxOutputTokens = 8192, Temperature = 0.7f };

        AiSettingsStore.ApplyEndpointQuirks(options, new ResolvedModel(openai, openai.Models[0]));

        Assert.AreEqual(8192, options.MaxOutputTokens);
        Assert.AreEqual(0.7f, options.Temperature);
        Assert.IsTrue(openai.StoreResponses);
        Assert.IsNull(options.RawRepresentationFactory);
    }

    [TestMethod]
    public void UnknownParameterNames_AreIgnoredRatherThanFatal()
    {
        // 目录里写错一个字,不该让整轮对话崩掉
        var provider = new AiProvider
        {
            UnsupportedParameters = "max_output_tokens\nnot_a_real_parameter\n\n  ",
            Models = [new AiModelConfig { Model = "m" }]
        };
        var options = new Microsoft.Extensions.AI.ChatOptions { MaxOutputTokens = 100, Temperature = 0.5f };

        AiSettingsStore.ApplyEndpointQuirks(options, new ResolvedModel(provider, provider.Models[0]));

        Assert.IsNull(options.MaxOutputTokens);
        Assert.AreEqual(0.5f, options.Temperature, "没列进去的不该被顺手摘掉");
    }

    [TestMethod]
    public async Task Credential_ForAPlainApiKeyProvider_CarriesNoHeaders()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        AiProvider provider = ProviderCatalog.Find("openai")!.CreateProvider();
        await store.SetApiKeyAsync(provider.Id, "sk-1");

        ProviderCredential credential =
            await store.ResolveCredentialAsync(new ResolvedModel(provider, provider.Models[0]));

        Assert.IsNull(credential.Headers);
    }
}
