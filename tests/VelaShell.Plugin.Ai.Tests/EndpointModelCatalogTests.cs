using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 问端点"你供应哪些模型":地址怎么拼、鉴权怎么带、回应怎么解。
/// </summary>
/// <remarks>
/// <b>一次网都不联</b>:地址与请求头是纯函数,回应解析喂固定报文。三种回应形状都照抄自线上
/// (OpenAI / Anthropic 的 <c>{"data":[…]}</c>、部分中转站的裸数组、Ollama 的
/// <c>{"models":[…]}</c>)—— 真出问题只会是"某天对方改了字段名",那不是单元测试拦得住的;
/// 这里守的是我们自己的解析与降级。
/// </remarks>
[TestClass]
[TestCategory("Plugins")]
public sealed class EndpointModelCatalogTests
{
    // ---- 地址 ----

    [TestMethod]
    public void Endpoint_OpenAiAppendsToTheBaseUrlAsTheSdkDoes()
    {
        // OpenAI 系的基地址按惯例已含 /v1,SDK 在其后接 /chat/completions —— 这里照样接 /models
        Assert.AreEqual("https://api.openai.com/v1/models",
            EndpointModelCatalog.Endpoint("https://api.openai.com/v1", ChatProtocol.OpenAiChatCompletions)!.ToString());
        Assert.AreEqual("https://relay.example/v1/models",
            EndpointModelCatalog.Endpoint("https://relay.example/v1/", ChatProtocol.OpenAiResponses)!.ToString());
    }

    [TestMethod]
    public void Endpoint_AnthropicAddsTheVersionSegmentItselfButNeverTwice()
    {
        // Anthropic 的基地址按惯例不含 /v1(SDK 自己补),这条接口得自己补
        Assert.AreEqual("https://api.anthropic.com/v1/models",
            EndpointModelCatalog.Endpoint("https://api.anthropic.com", ChatProtocol.AnthropicMessages)!.ToString());
        // 用户误带了 /v1 也不该变成 /v1/v1
        Assert.AreEqual("https://api.anthropic.com/v1/models",
            EndpointModelCatalog.Endpoint("https://api.anthropic.com/v1", ChatProtocol.AnthropicMessages)!.ToString());
    }

    [TestMethod]
    public void Endpoint_UnusableBaseUrlsAreNull()
    {
        Assert.IsNull(EndpointModelCatalog.Endpoint("", ChatProtocol.OpenAiChatCompletions));
        Assert.IsNull(EndpointModelCatalog.Endpoint("   ", ChatProtocol.OpenAiChatCompletions));
        Assert.IsNull(EndpointModelCatalog.Endpoint(null, ChatProtocol.OpenAiChatCompletions));
        Assert.IsNull(EndpointModelCatalog.Endpoint("api.openai.com/v1", ChatProtocol.OpenAiChatCompletions),
            "相对地址发不出去");
        Assert.IsNull(EndpointModelCatalog.Endpoint("file:///etc/passwd", ChatProtocol.OpenAiChatCompletions),
            "只走 http(s)");
    }

    // ---- 鉴权 ----

    [TestMethod]
    public void Request_OpenAiSendsABearerToken()
    {
        using HttpRequestMessage request = EndpointModelCatalog.Request(
            "https://relay.example/v1", ChatProtocol.OpenAiChatCompletions, ProviderCredential.Key("sk-abc"))!;

        Assert.AreEqual("Bearer sk-abc", request.Headers.GetValues("Authorization").Single());
    }

    [TestMethod]
    public void Request_AnthropicSendsAnApiKeyHeaderAndTheVersion()
    {
        using HttpRequestMessage request = EndpointModelCatalog.Request(
            "https://api.anthropic.com", ChatProtocol.AnthropicMessages, ProviderCredential.Key("sk-ant"))!;

        Assert.AreEqual("sk-ant", request.Headers.GetValues("x-api-key").Single());
        Assert.AreEqual(EndpointModelCatalog.AnthropicVersion,
            request.Headers.GetValues("anthropic-version").Single());
        Assert.IsFalse(request.Headers.Contains("Authorization"), "手填的 Key 走 x-api-key,不是 Bearer");
    }

    [TestMethod]
    [DataRow(true, null)]
    [DataRow(false, "https://account.example/v1")]
    public void Request_RejectsSubscriptionCredentials(bool bearer, string? accountEndpoint)
    {
        Assert.Throws<NotSupportedException>(() => EndpointModelCatalog.Request(
            "https://api.anthropic.com", ChatProtocol.AnthropicMessages,
            new ProviderCredential("retired", bearer, BaseUrl: accountEndpoint)));
    }

    [TestMethod]
    public void Request_CarriesTheProviderSExtraHeaders()
    {
        using HttpRequestMessage request = EndpointModelCatalog.Request(
            "https://relay.example/v1", ChatProtocol.OpenAiResponses,
            new ProviderCredential("api-key", false, [new("x-relay-project", "project-1")]))!;

        Assert.AreEqual("project-1", request.Headers.GetValues("x-relay-project").Single());
    }

    [TestMethod]
    public void Request_WithoutACredentialStillGoesOut()
    {
        // 本地 Ollama 就没有 Key;而 Key 漏填时,服务端回的 401 比本地猜测有用得多
        using HttpRequestMessage request = EndpointModelCatalog.Request(
            "http://localhost:11434/v1", ChatProtocol.OpenAiChatCompletions, ProviderCredential.Key(null))!;

        Assert.IsFalse(request.Headers.Contains("Authorization"));
        Assert.AreEqual("http://localhost:11434/v1/models", request.RequestUri!.ToString());
    }

    [TestMethod]
    public void Request_WithAnUnusableBaseUrlIsNull()
        => Assert.IsNull(EndpointModelCatalog.Request("", ChatProtocol.OpenAiChatCompletions,
            ProviderCredential.Key("sk")));

    // ---- 解析 ----

    [TestMethod]
    public void Parse_ReadsTheOpenAiShape()
    {
        IReadOnlyList<(string Id, int ContextTokens)> ids = EndpointModelCatalog.Parse("""
            {"object":"list","data":[
              {"id":"gpt-5","object":"model","created":1,"owned_by":"openai"},
              {"id":"gpt-4o-audio-preview","object":"model","created":2,"owned_by":"openai"}
            ]}
            """);

        Assert.AreSequenceEqual(["gpt-4o-audio-preview", "gpt-5"], [.. ids.Select(model => model.Id)]);
    }

    [TestMethod]
    public void Parse_ReadsTheAnthropicShape()
    {
        IReadOnlyList<(string Id, int ContextTokens)> ids = EndpointModelCatalog.Parse("""
            {"data":[{"type":"model","id":"claude-sonnet-4","display_name":"Claude Sonnet 4"}],
             "has_more":false}
            """);

        Assert.AreEqual("claude-sonnet-4", ids.Single().Id);
    }

    [TestMethod]
    public void Parse_ReadsABareArrayAsSomeRelaysReturn()
        => Assert.AreEqual("deepseek-chat",
            EndpointModelCatalog.Parse("""[{"id":"deepseek-chat"}]""").Single().Id);

    [TestMethod]
    public void Parse_ReadsTheOllamaShape()
    {
        IReadOnlyList<(string Id, int ContextTokens)> ids = EndpointModelCatalog.Parse("""
            {"models":[{"name":"llama3.1:8b","size":1},{"name":"qwen3:14b","size":2}]}
            """);

        Assert.AreSequenceEqual(["llama3.1:8b", "qwen3:14b"], [.. ids.Select(model => model.Id)]);
    }

    [TestMethod]
    public void Parse_DeduplicatesAndSorts()
    {
        IReadOnlyList<(string Id, int ContextTokens)> ids = EndpointModelCatalog.Parse("""
            {"data":[{"id":"b"},{"id":"a"},{"id":"B"}]}
            """);

        Assert.AreSequenceEqual(["a", "b"], [.. ids.Select(model => model.Id)]);
    }

    [TestMethod]
    public void Parse_KeepsImageModelsButDropsUnsupportedModalities()
    {
        // /models 也报图片模型;图片接口可用,向量、语音与审核仍不支持。
        IReadOnlyList<(string Id, int ContextTokens)> ids = EndpointModelCatalog.Parse("""
            {"data":[
              {"id":"gpt-5"},
              {"id":"text-embedding-3-large"},
              {"id":"bge-embed"},
              {"id":"whisper-1"},
              {"id":"tts-1-hd"},
              {"id":"dall-e-3"},
              {"id":"omni-moderation-latest"},
              {"id":"bge-reranker-v2"}
            ]}
            """);

        Assert.AreSequenceEqual(["dall-e-3", "gpt-5"], [.. ids.Select(model => model.Id)]);
    }

    [TestMethod]
    public void Parse_DoesNotOverReach()
    {
        // 宁可漏筛也不误筛:误筛一个,用户在下拉里永远找不到它,而且不会有任何提示
        IReadOnlyList<(string Id, int ContextTokens)> ids = EndpointModelCatalog.Parse("""
            {"data":[{"id":"gpt-4o-audio-preview"},{"id":"gemini-2.5-pro-vision"},{"id":"pixtral-large"}]}
            """);

        Assert.HasCount(3, ids);
    }

    [TestMethod]
    public void Parse_JunkIsEmptyNotAnException()
    {
        Assert.IsEmpty(EndpointModelCatalog.Parse("<html>404</html>"), "被网关挡下来时拿到的常是 HTML");
        Assert.IsEmpty(EndpointModelCatalog.Parse("{}"));
        Assert.IsEmpty(EndpointModelCatalog.Parse("""{"error":{"message":"invalid key"}}"""));
        Assert.IsEmpty(EndpointModelCatalog.Parse("""{"data":[{"object":"model"},{"id":"  "}]}"""));
    }

    [TestMethod]
    [DataRow("65536", 65536)]
    [DataRow("0", 0)]
    [DataRow("-1", 0)]
    [DataRow("null", 0)]
    [DataRow("\"65536\"", 0)]
    [DataRow("1.5", 0)]
    [DataRow("2147483648", 0)]
    public void Parse_ReadsOnlyValidPositiveContextLengths(string value, int expected)
    {
        (string id, int contextTokens) = EndpointModelCatalog.Parse("{\"data\":[{\"id\":\"private-model\",\"context_length\":" + value + "}]}").Single();
        Assert.AreEqual("private-model", id);
        Assert.AreEqual(expected, contextTokens);
    }

    [TestMethod]
    public void Parse_DuplicateIdKeepsOriginalSpellingAndFillsMissingWindow()
    {
        (string id, int contextTokens) = EndpointModelCatalog.Parse("""
            {"data":[{"id":"Private-model"},{"id":"PRIVATE-MODEL","context_length":65536},
                     {"id":"private-model","context_length":0}]}
            """).Single();
        Assert.AreEqual("Private-model", id);
        Assert.AreEqual(65536, contextTokens);
    }

    // ---- 发请求 ----

    [TestMethod]
    public async Task Fetch_AnErrorStatusYieldsAnEmptyListSoTheCallerCanFallBack()
    {
        using var http = new HttpClient(new StatusHandler(System.Net.HttpStatusCode.NotFound));

        Assert.IsEmpty(await EndpointModelCatalog.FetchAsync(http, "https://relay.example/v1",
            ChatProtocol.OpenAiChatCompletions, ProviderCredential.Key("sk")));
    }

    [TestMethod]
    public async Task Fetch_WithoutABaseUrlNeverLeavesTheProcess()
    {
        var handler = new StatusHandler(System.Net.HttpStatusCode.OK);
        using var http = new HttpClient(handler);

        Assert.IsEmpty(await EndpointModelCatalog.FetchAsync(http, "", ChatProtocol.OpenAiChatCompletions,
            ProviderCredential.Key("sk")));
        Assert.AreEqual(0, handler.Calls);
    }

    /// <summary>
    /// Anthropic 的 <c>/v1/models</c> 默认一页 20 条:只认第一页,后面几页的模型会被当成"已下架"清掉。
    /// 第一次请求不带参数(不认查询参数的中转站照旧),说还有下一页才带 <c>limit</c> 与 <c>after_id</c> 翻。
    /// </summary>
    [TestMethod]
    public async Task Fetch_FollowsAnthropicPagesUntilHasMoreIsFalse()
    {
        var handler = new PagedHandler(
            """{"data":[{"id":"claude-a"},{"id":"claude-b"}],"has_more":true,"first_id":"claude-a","last_id":"claude-b"}""",
            """{"data":[{"id":"claude-c"}],"has_more":false,"first_id":"claude-c","last_id":"claude-c"}""");
        using var http = new HttpClient(handler);

        IReadOnlyList<(string Id, int ContextTokens)> models = await EndpointModelCatalog.FetchAsync(http,
            "https://api.anthropic.com", ChatProtocol.AnthropicMessages, ProviderCredential.Key("sk-ant"));

        Assert.AreSequenceEqual(["claude-a", "claude-b", "claude-c"], [.. models.Select(m => m.Id)]);
        Assert.HasCount(2, handler.Requests);
        Assert.AreEqual("https://api.anthropic.com/v1/models", handler.Requests[0].RequestUri!.ToString());
        Assert.AreEqual("?limit=1000&after_id=claude-b", handler.Requests[1].RequestUri!.Query);
        Assert.AreEqual("sk-ant", handler.Requests[1].Headers.GetValues("x-api-key").Single(), "翻页请求也得带鉴权");
    }

    [TestMethod]
    [DataRow("""{"data":[{"id":"claude-c"}],"has_more":false}""", 500)]
    [DataRow("""{"data":[{"id":"claude-c"}],"has_more":true,"last_id":"claude-b"}""", 200)]
    [DataRow("""{"data":[{"id":"claude-c"}],"has_more":true}""", 200)]
    public async Task Fetch_AnIncompletePageChainYieldsNothingRatherThanAPartialList(string second, int status)
    {
        // 翻到一半失败、游标不前进或缺失:拿不到完整清单就按"拿不到"处理,不能拿前几页去清理模型
        var handler = new PagedHandler(
            """{"data":[{"id":"claude-a"},{"id":"claude-b"}],"has_more":true,"last_id":"claude-b"}""", second)
        {
            SecondStatus = (System.Net.HttpStatusCode)status
        };
        using var http = new HttpClient(handler);

        Assert.IsEmpty(await EndpointModelCatalog.FetchAsync(http, "https://api.anthropic.com",
            ChatProtocol.AnthropicMessages, ProviderCredential.Key("sk-ant")));
    }

    private sealed class PagedHandler(params string[] pages) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public System.Net.HttpStatusCode SecondStatus { get; init; } = System.Net.HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            int index = Math.Min(Requests.Count, pages.Length) - 1;
            return Task.FromResult(new HttpResponseMessage(index == 0 ? System.Net.HttpStatusCode.OK : SecondStatus)
            {
                Content = new StringContent(pages[index])
            });
        }
    }

    private sealed class StatusHandler(System.Net.HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
        }
    }
}
