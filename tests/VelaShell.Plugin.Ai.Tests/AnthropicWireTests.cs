using System.Collections.Specialized;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// Anthropic 协议下,插件的聊天客户端到底发出了哪些头、什么样的请求体 ——
/// 走真 <see cref="AiSettingsStore.CreateClient" />、真 SDK、真适配器,打到本地假端点抓报文。
/// </summary>
/// <remarks>
/// 起因:Anthropic SDK 从 13.x 起 (a) 把 <c>anthropic-version</c> 从 <c>2023-06-01</c> 换成 <c>2026-10-21</c>;
/// (b) 把 <c>ApiKey</c> 也发成 <c>Authorization: Bearer</c>,不再发 <c>x-api-key</c>;
/// (c) <c>MessageCreateParams</c> 去掉了 temperature / top_p / top_k,<c>IChatClient</c> 适配器也不再映射它们。
/// 只认 <c>x-api-key</c> 的中转站因此一聊天就 401,而拉模型清单(那条请求是自己拼的)照常 ——
/// 只看其中一边查不出来,所以在线上报文这一层守住。
/// </remarks>
[TestClass]
[TestCategory("Plugins")]
public sealed class AnthropicWireTests
{
    private const string Reply = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_wire","type":"message","role":"assistant","model":"claude-wire","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":0}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"OK"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":1}}

        event: message_stop
        data: {"type":"message_stop"}


        """;

    private static ResolvedModel ModelFor(string baseUrl, ChatProtocol protocol = ChatProtocol.AnthropicMessages)
    {
        var provider = new AiProvider
        {
            Name = "wire",
            BaseUrl = baseUrl,
            DefaultProtocol = protocol,
            Models = [new AiModelConfig { Model = "claude-wire", MaxTokens = 64 }]
        };
        return new ResolvedModel(provider, provider.Models[0]);
    }

    /// <summary>用给定凭据发一轮流式对话,抓回这一次请求的头与体。</summary>
    /// <param name="credential">交给 <see cref="AiSettingsStore.CreateClient" /> 的凭据。</param>
    /// <param name="tune">发送前按正式请求的路子调一下选项(拿得到解好的模型)。</param>
    private static async Task<(NameValueCollection Headers, string Body)> SendAsync(ProviderCredential credential,
        Action<ChatOptions, ResolvedModel>? tune = null)
    {
        await using var stub = new SseStub(Reply);
        using var context = new TestPluginContext();
        ResolvedModel model = ModelFor(stub.BaseUrl);
        var options = new ChatOptions();
        tune?.Invoke(options, model);
        using IChatClient client = new AiSettingsStore(context).CreateClient(model, credential);
        string text = "";
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync("hi", options))
        {
            text += update.Text;
        }
        Assert.AreEqual("OK", text, "假端点回的是标准 Anthropic 流,整轮应当正常收尾");
        return (stub.RequestHeaders.Single(), stub.Requests.Single());
    }

    [TestMethod]
    public async Task ApiKey_GoesOutAsXApiKey_WithNoAuthorizationHeader()
    {
        (NameValueCollection headers, _) = await SendAsync(ProviderCredential.Key("sk-ant-key"));

        Assert.AreEqual(AiHttp.UserAgent, headers["User-Agent"]);
        Assert.AreEqual("sk-ant-key", headers["x-api-key"], "手填的 Key 要按 13 之前的老规矩走 x-api-key");
        Assert.IsNull(headers["Authorization"], "API Key 不能再以 Bearer 出现:只读 x-api-key 的中转站会把它当成没带 Key");
        // 版本头不动,照 SDK 的发 —— 请求体就是按这个版本拼的。SDK 再换版本时这里会红,提醒重新核对兼容层
        Assert.AreEqual("2026-10-21", headers["anthropic-version"]);
    }

    /// <summary>
    /// 拉模型清单那条自己拼的请求,版本头与鉴权头跟聊天请求一模一样(维护者 2026-10-25 决定,#657 复审跟进②):
    /// 以前清单还发 <c>2023-06-01</c>,与 SDK 13.x 聊天请求的 <c>2026-10-21</c> 不一致 —— 同一把 Key 在两条路上
    /// 碰到的可能是两个版本的接口。拿真 SDK 发出去的那一份比,SDK 再换版本号时这里会红。
    /// </summary>
    [TestMethod]
    [DataRow(false, DisplayName = "手填的 Key")]
    [DataRow(true, DisplayName = "手动指定附加头")]
    public async Task ModelListRequest_SendsTheSameVersionAndAuthHeadersAsChat(bool extraHeaders)
    {
        ProviderCredential credential = extraHeaders
            ? new ProviderCredential("sk-ant-key", false, [new("user-agent", "Relay/2")])
            : ProviderCredential.Key("sk-ant-key");
        (NameValueCollection chat, _) = await SendAsync(credential);

        using HttpRequestMessage list = EndpointModelCatalog.Request("https://api.anthropic.com",
            ChatProtocol.AnthropicMessages, credential)!;

        Assert.AreEqual(extraHeaders ? "Relay/2" : AiHttp.UserAgent, chat["User-Agent"]);
        Assert.AreEqual(chat["User-Agent"], list.Headers.UserAgent.ToString());
        foreach (string name in new[] { "anthropic-version", "x-api-key", "Authorization", "anthropic-beta" })
        {
            string? sent = list.Headers.TryGetValues(name, out IEnumerable<string>? values) ? string.Join(",", values) : null;
            Assert.AreEqual(chat[name], sent, $"{name}:清单请求与聊天请求要一致");
        }
    }

    [TestMethod]
    public void ClientRejectsBearerCredentialsBeforeIssuingARequest()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        Assert.Throws<NotSupportedException>(() => store.CreateClient(ModelFor("https://api.anthropic.com"), new ProviderCredential("old-token", true)));
    }

    [TestMethod]
    [DataRow("Authorization", "Bearer relay-token", "sk-ant-key")]
    [DataRow("Authorization", "Bearer sk-ant-key", "sk-ant-key")]
    [DataRow("x-api-key", "relay-key", "relay-key")]
    public async Task UserConfiguredHeaders_AreNeverClobbered(string name, string value, string expectedApiKey)
    {
        // 第二行是关键:用户自己写的 Authorization 恰好就是 "Bearer 这把 Key" 时,
        // 改写必须发生在附加头之前,否则会把用户明确配置的头当成 SDK 的那份一并摘掉
        var credential = new ProviderCredential("sk-ant-key", false,
            [new(name, value), new("X-Relay-Route", "r1")]);

        (NameValueCollection headers, _) = await SendAsync(credential);

        Assert.AreEqual(expectedApiKey, headers["x-api-key"]);
        Assert.AreEqual(name == "Authorization" ? value : null, headers["Authorization"]);
        Assert.AreEqual("r1", headers["X-Relay-Route"]);
    }

    [TestMethod]
    [DataRow("ANTHROPIC_API_KEY", null)]
    [DataRow(null, "ANTHROPIC_AUTH_TOKEN")]
    [DataRow("ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN")]
    public async Task NoCredential_OnAnotherHost_DoesNotSendTheMachineKey(string? apiKeyVariable, string? authTokenVariable)
    {
        // 没有凭据(供应商一把 Key 都没有、模型勾了自带 Key 却没填,或换源清掉 Key 还没填新的)时,
        // 本机的 ANTHROPIC_API_KEY / ANTHROPIC_AUTH_TOKEN 只给官方主机:中转站一个字都拿不到。
        // 两个变量各设一次:SDK 里 ApiKey 与 AuthToken 各自只关掉自己那个变量,漏掉一个另一个照样发出去
        using var machine = new MachineAnthropicVariables(apiKeyVariable is null ? null : "machine-key",
            authTokenVariable is null ? null : "machine-token");

        (NameValueCollection headers, _) = await SendAsync(ProviderCredential.Key(null));

        Assert.IsNull(headers["Authorization"], "本机的 Key 不能发往第三方地址");
        Assert.IsNull(headers["x-api-key"]);
    }

    [TestMethod]
    [DataRow("https://api.anthropic.com")]
    [DataRow("https://api.anthropic.com/")]
    [DataRow("https://API.Anthropic.com/v1")]
    public void NoCredential_OnTheOfficialHost_StillUsesTheMachineKey(string baseUrl)
    {
        // 原设计:只在本机配环境变量、不填 Key,就能直连官方 API —— 这条用法不能被换源的护栏误伤
        using var machine = new MachineAnthropicVariables("machine-key", null);
        using var context = new TestPluginContext();

        using IChatClient client = new AiSettingsStore(context).CreateClient(ModelFor(baseUrl), ProviderCredential.Key(null));

        var sdk = (Anthropic.IAnthropicClient)client.GetService(typeof(Anthropic.IAnthropicClient))!;
        Assert.AreEqual("machine-key", sdk.ApiKey, "官方主机照旧交给 SDK 去读环境变量");
    }

    [TestMethod]
    public void NoCredential_OnTheOfficialHost_NeverUsesTheMachineAuthToken()
    {
        using var machine = new MachineAnthropicVariables(null, "machine-token");
        using var context = new TestPluginContext();

        using IChatClient client = new AiSettingsStore(context)
            .CreateClient(ModelFor("https://api.anthropic.com"), ProviderCredential.Key(null));

        var sdk = (Anthropic.IAnthropicClient)client.GetService(typeof(Anthropic.IAnthropicClient))!;
        Assert.IsNull(sdk.AuthToken);
    }

    [TestMethod]
    [DataRow("http://api.anthropic.com")]
    [DataRow("https://api.anthropic.com:8443")]
    [DataRow("https://api.anthropic.com.relay.example")]
    [DataRow("https://relay.example/anthropic")]
    public void NoCredential_OnLookalikeHosts_ClearsBothMachineVariables(string baseUrl)
    {
        // 明文 http、别的端口、带着官方名字的别家主机都不是官方端点
        using var machine = new MachineAnthropicVariables("machine-key", "machine-token");
        using var context = new TestPluginContext();

        using IChatClient client = new AiSettingsStore(context).CreateClient(ModelFor(baseUrl), ProviderCredential.Key(null));

        var sdk = (Anthropic.IAnthropicClient)client.GetService(typeof(Anthropic.IAnthropicClient))!;
        Assert.IsNull(sdk.ApiKey);
        Assert.IsNull(sdk.AuthToken);
    }

    [TestMethod]
    public async Task SavedConfiguration_OnlyTheOfficialProviderFallsBackToTheMachineKey()
    {
        // 存量配置:一家只配了环境变量的官方 Anthropic(从来没填过 Key),一家中转站的 Key 已随换源清掉、还没填新的。
        // 升级之后读回来,前者照旧拿本机 Key 直连官方,后者不再把本机 Key 发往中转站
        using var machine = new MachineAnthropicVariables("machine-key", "machine-token");
        using var context = new TestPluginContext();
        var official = new AiProvider
        {
            Name = "Anthropic",
            BaseUrl = "https://api.anthropic.com",
            DefaultProtocol = ChatProtocol.AnthropicMessages,
            CatalogId = "anthropic",
            Models = [new AiModelConfig { Model = "claude-opus-5" }]
        };
        var relay = new AiProvider
        {
            Name = "Relay",
            BaseUrl = "https://relay.example",
            DefaultProtocol = ChatProtocol.AnthropicMessages,
            Models = [new AiModelConfig { Model = "claude-opus-5" }, new AiModelConfig { Model = "own", HasOwnApiKey = true }]
        };
        await new AiSettingsStore(context).SaveAsync(new AiSettings { Providers = [official, relay] });

        var store = new AiSettingsStore(context);
        AiSettings loaded = await store.LoadAsync();
        foreach (ResolvedModel model in loaded.ResolveModels())
        {
            ProviderCredential credential = await store.ResolveCredentialAsync(model);
            Assert.IsTrue(string.IsNullOrEmpty(credential.Value), "这些模型都没有已存的 Key");
            using IChatClient client = store.CreateClient(model, credential);
            var sdk = (Anthropic.IAnthropicClient)client.GetService(typeof(Anthropic.IAnthropicClient))!;
            bool isOfficial = model.Provider.Name == "Anthropic";
            Assert.AreEqual(isOfficial ? "machine-key" : null, sdk.ApiKey, model.Provider.Name + "/" + model.Model);
            // 两个都设时 SDK 只认 ANTHROPIC_API_KEY(那时 AuthToken 读回来本就是空),所以只断言别家两个都清了
            if (!isOfficial) Assert.IsNull(sdk.AuthToken, model.Provider.Name + "/" + model.Model);
        }
    }

    /// <summary>用例期间把本机的两个 Anthropic 环境变量设成给定值(null 为不设),结束时恢复原值。</summary>
    private sealed class MachineAnthropicVariables : IDisposable
    {
        private readonly string? _apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        private readonly string? _authToken = Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN");

        public MachineAnthropicVariables(string? apiKey, string? authToken)
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", apiKey);
            Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", authToken);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", _apiKey);
            Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", _authToken);
        }
    }

    [TestMethod]
    public async Task Sampling_IsNotSentOnTheAnthropicProtocol_ButStopSequencesAre()
    {
        (_, string body) = await SendAsync(ProviderCredential.Key("sk-ant-key"), (options, model) =>
        {
            // 照聊天面板的组装顺序:先填设置里的采样参数,再过一遍端点规则
            options.Temperature = 0.3f;
            options.TopP = 0.9f;
            options.TopK = 5;
            options.StopSequences = ["END_MARK"];
            AiSettingsStore.ApplyEndpointQuirks(options, model);
        });

        Assert.DoesNotContain("temperature", body, $"实际请求体:{body}");
        Assert.DoesNotContain("top_p", body, $"实际请求体:{body}");
        Assert.DoesNotContain("top_k", body, $"实际请求体:{body}");
        Assert.Contains("END_MARK", body, $"停止序列在 Anthropic 协议上照发。实际请求体:{body}");
    }

    [TestMethod]
    [DataRow(ChatProtocol.AnthropicMessages, true)]
    [DataRow(ChatProtocol.OpenAiChatCompletions, false)]
    [DataRow(ChatProtocol.OpenAiResponses, false)]
    public void EndpointQuirks_DropSamplingOnlyOnTheAnthropicProtocol(ChatProtocol protocol, bool dropped)
    {
        // 不靠 SDK 适配器"恰好不映射":2026-10-21 版的接口收到这三个字段一律 400,
        // 哪天适配器又映射回来,设置页上那句"不会发送"也得仍然成立
        var options = new ChatOptions { Temperature = 0.3f, TopP = 0.9f, TopK = 5 };

        AiSettingsStore.ApplyEndpointQuirks(options, ModelFor("http://127.0.0.1:9", protocol));

        Assert.AreEqual(dropped ? null : 0.3f, options.Temperature);
        Assert.AreEqual(dropped ? null : 0.9f, options.TopP);
        Assert.AreEqual(dropped ? null : 5, options.TopK);
    }
}
