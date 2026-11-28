using System.ClientModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// OpenAI Responses 协议下,请求体到底长什么样 —— 走真 SDK 打到本地假端点,抓报文看。
/// </summary>
/// <remarks>
/// 起因:ChatGPT 的 Codex 后端回 <c>{"detail":"System messages are not allowed"}</c>。
/// 也就是说系统提示词<b>不能以 system 消息的身份</b>发出去,得走 Responses 自己的
/// <c>instructions</c> 字段。而"<c>ChatOptions.Instructions</c> 到底映射成哪一个"
/// 只能抓包确认 —— 猜错的话提示词会<b>静默丢失</b>,模型行为变了却没有任何报错。
/// </remarks>
[TestClass]
[TestCategory("Plugins")]
public sealed class ResponsesWireTests
{
    private static IChatClient Client(string baseUrl)
    {
#pragma warning disable OPENAI001 // Responses API 在 OpenAI SDK 中标记为实验性
        return new OpenAIClient(new ApiKeyCredential("k"), new OpenAIClientOptions { Endpoint = new Uri(baseUrl) })
            .GetResponsesClient()
            .AsIChatClient("m");
#pragma warning restore OPENAI001
    }

    /// <summary>把消息发出去并抓回请求体(假端点回的不是 Responses 报文,解析失败无所谓)。</summary>
    private static async Task<string> WireBodyAsync(List<ChatMessage> messages, ChatOptions options)
    {
        await using var stub = new SseStub("", jsonContent: "{}");
        try
        {
            await Client(stub.BaseUrl).GetResponseAsync(messages, options);
        }
        catch (Exception)
        {
            // 只关心发出去的是什么,回来的能不能解析不重要
        }
        return await stub.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [TestMethod]
    public async Task Instructions_LandInTheirOwnFieldAndNotAsASystemMessage()
    {
        string body = await WireBodyAsync(
            [new ChatMessage(ChatRole.User, "你好")],
            new ChatOptions { Instructions = "INSTRUCTIONS_MARKER" });

        Assert.Contains("INSTRUCTIONS_MARKER", body, $"提示词不见了。实际请求体:{body}");
        Assert.Contains("\"instructions\"", body, $"没走 instructions 字段。实际请求体:{body}");
        Assert.DoesNotContain("\"system\"", body, $"不该出现 system 角色。实际请求体:{body}");
    }

    [TestMethod]
    public async Task ASystemMessage_DoesShowUpAsARoleOnTheWire()
    {
        // 这条是"为什么必须改"的证据:照原样发,报文里就是有 system 角色,Codex 后端会拒
        string body = await WireBodyAsync(
            [new ChatMessage(ChatRole.System, "SYSTEM_MARKER"), new ChatMessage(ChatRole.User, "你好")],
            new ChatOptions());

        Assert.Contains("SYSTEM_MARKER", body);
        Assert.Contains("\"system\"", body, $"实际请求体:{body}");
    }

    /// <summary>
    /// 真机上那个 400 的修法:把系统提示词从消息列表挪进 <c>instructions</c>,
    /// 报文里就不再有 system 角色,而提示词一个字都没少。
    /// </summary>
    [TestMethod]
    public async Task MovingTheSystemPromptOut_RemovesTheRoleButKeepsTheText()
    {
        List<ChatMessage> messages =
        [
            new ChatMessage(ChatRole.System, "SYSTEM_MARKER"),
            new ChatMessage(ChatRole.User, "你好")
        ];
        var options = new ChatOptions
        {
            Instructions = Chat.ContextBuilder.MoveSystemPromptOut(messages)
        };

        string body = await WireBodyAsync(messages, options);

        Assert.Contains("SYSTEM_MARKER", body, $"提示词丢了。实际请求体:{body}");
        Assert.Contains("\"instructions\"", body, $"实际请求体:{body}");
        Assert.DoesNotContain("\"system\"", body,
            $"还带着 system 角色,Codex 后端会回 System messages are not allowed。实际请求体:{body}");
    }

    [TestMethod]
    public void MoveSystemPromptOut_OnlyTakesTheLeadingOne()
    {
        // 装配出来的列表里 system 必定在最前;后面若还有,那是历史带来的,不该被悄悄吞掉
        List<ChatMessage> messages =
        [
            new ChatMessage(ChatRole.System, "头一条"),
            new ChatMessage(ChatRole.User, "问题"),
            new ChatMessage(ChatRole.System, "历史里的")
        ];

        string? moved = Chat.ContextBuilder.MoveSystemPromptOut(messages);

        Assert.AreEqual("头一条", moved);
        Assert.HasCount(2, messages);
        Assert.AreEqual(ChatRole.System, messages[1].Role);
    }

    [TestMethod]
    public void MoveSystemPromptOut_LeavesAListThatDoesNotStartWithSystemAlone()
    {
        List<ChatMessage> messages = [new ChatMessage(ChatRole.User, "问题")];

        Assert.IsNull(Chat.ContextBuilder.MoveSystemPromptOut(messages));
        Assert.HasCount(1, messages);
    }

    /// <summary>
    /// Codex 后端接受的<b>全部</b>顶层字段,取自它自己的请求结构
    /// (<c>openai/codex</c> 的 <c>codex-rs/core/src/client.rs</c>,<c>ResponsesApiRequest</c>)。
    /// </summary>
    private static readonly HashSet<string> CodexAcceptedFields =
    [
        with(StringComparer.Ordinal),
        "model", "instructions", "input", "tools", "tool_choice", "parallel_tool_calls",
        "reasoning", "store", "stream", "stream_options", "include", "service_tier",
        "prompt_cache_key", "text", "client_metadata", "access_programs"
    ];

    /// <summary>
    /// 发出去的报文里<b>不能有</b>它清单之外的字段。
    /// </summary>
    /// <remarks>
    /// 这条是为了<b>不再靠用户一轮一个 400 去试</b>而存在的:那个后端多收到一个不认的字段
    /// 就整轮拒,而且一次只报一个名字。把"它到底收哪些"钉成清单,以后 SDK 或适配器
    /// 多发了什么,这里当场就红,不用等真机。
    /// </remarks>
    [TestMethod]
    public async Task TheCodexShapedRequest_SendsNothingOutsideWhatThatBackendAccepts()
    {
        AiProvider codex = ApiKeyEndpointTests.RestrictedResponsesProvider();
        var resolved = new ResolvedModel(codex, codex.Models[0]);
        List<ChatMessage> messages =
        [
            new ChatMessage(ChatRole.System, "系统提示词"),
            new ChatMessage(ChatRole.User, "你好")
        ];
        // 照 ChatPanelView 那一段原样组装,包括它默认会设的那些
        var options = new ChatOptions
        {
            MaxOutputTokens = resolved.MaxTokens,
            Temperature = resolved.Temperature,
            TopP = resolved.TopP
        };
        AiSettingsStore.ApplyReasoning(options, resolved);
        AiSettingsStore.ApplyEndpointQuirks(options, resolved);
        if (!EndpointQuirks.Of(codex).AllowSystemMessages)
        {
            options.Instructions = Chat.ContextBuilder.MoveSystemPromptOut(messages);
        }

        string body = await WireBodyAsync(messages, options);

        using var document = JsonDocument.Parse(body);
        List<string> unexpected = [.. document.RootElement.EnumerateObject()
            .Select(p => p.Name)
            .Where(name => !CodexAcceptedFields.Contains(name))];
        Assert.IsEmpty(unexpected,
            $"这些字段那个后端不认,发出去就是 400:{string.Join(", ", unexpected)}。完整请求体:{body}");
    }

    /// <summary>
    /// Codex 那条路上发出去的报文,必须<b>同时</b>满足后端的三条脾气:
    /// 带 <c>store:false</c>、没有 system 角色、没有 <c>max_output_tokens</c>。
    /// 真机上这三条是一轮一个 400 挖出来的,合在一条测试里守住。
    /// </summary>
    [TestMethod]
    public async Task TheCodexShapedRequest_SatisfiesEveryKnownRestriction()
    {
        AiProvider codex = ApiKeyEndpointTests.RestrictedResponsesProvider();
        var resolved = new ResolvedModel(codex, codex.Models[0]);
        List<ChatMessage> messages =
        [
            new ChatMessage(ChatRole.System, "SYSTEM_MARKER"),
            new ChatMessage(ChatRole.User, "你好")
        ];
        var options = new ChatOptions { MaxOutputTokens = resolved.MaxTokens, Temperature = 0.7f };
        AiSettingsStore.ApplyReasoning(options, resolved);
        AiSettingsStore.ApplyEndpointQuirks(options, resolved);
        if (!codex.AllowSystemMessages)
        {
            options.Instructions = Chat.ContextBuilder.MoveSystemPromptOut(messages);
        }

        string body = await WireBodyAsync(messages, options);

        Assert.Contains("\"store\":false", body, $"实际请求体:{body}");
        Assert.DoesNotContain("\"system\"", body, $"实际请求体:{body}");
        Assert.DoesNotContain("max_output_tokens", body, $"实际请求体:{body}");
        Assert.DoesNotContain("temperature", body, $"实际请求体:{body}");
        // 提示词一个字都没少,只是换了位置
        Assert.Contains("SYSTEM_MARKER", body, $"实际请求体:{body}");
        Assert.Contains("\"instructions\"", body, $"实际请求体:{body}");
    }

    /// <summary>
    /// 自定义 Responses 接入默认发 <c>store:false</c>(#616–#636 复审①,维护者 2026-10-25 决定):工具循环早已整段重放,
    /// 服务端存着的那一轮用不到,却把含终端输出的上下文留在了供应商那边。设置页勾上开关才不发;
    /// 官方 openai 目录条目照旧(以目录为准,不发 store)。没有目录 id 的老配置同样算自定义。
    /// </summary>
    [TestMethod]
    [DataRow("custom-openai", null, true, DisplayName = "自定义接入,没动过开关:发 store:false")]
    [DataRow("custom-openai", true, false, DisplayName = "自定义接入,勾上了:不发")]
    [DataRow(null, null, true, DisplayName = "没有目录 id 的老配置:发 store:false")]
    [DataRow("openai", null, false, DisplayName = "官方 openai:照旧不发")]
    [DataRow("openai", false, false, DisplayName = "官方 openai 不看这个开关")]
    public async Task CustomResponsesEndpoint_SendsStoreFalseUnlessTheSwitchIsOn(string? catalogId, bool? store, bool expectStoreFalse)
    {
        AiProvider provider = catalogId is null
            ? new AiProvider { Name = "legacy", BaseUrl = "https://relay.example/v1", Models = [new AiModelConfig { Model = "m" }] }
            : ProviderCatalog.Find(catalogId)!.CreateProvider();
        provider.DefaultProtocol = ChatProtocol.OpenAiResponses;
        provider.Models[0].Protocol = null;
        provider.CustomResponsesStore = store;
        var resolved = new ResolvedModel(provider, provider.Models[0]);
        var options = new ChatOptions();
        AiSettingsStore.ApplyEndpointQuirks(options, resolved);

        string body = await WireBodyAsync([new ChatMessage(ChatRole.User, "hi")], options);

        Assert.AreEqual(expectStoreFalse, body.Contains("\"store\":false", StringComparison.Ordinal), $"实际请求体:{body}");
        if (!expectStoreFalse) Assert.DoesNotContain("\"store\"", body, $"实际请求体:{body}");
        Assert.AreEqual(catalogId is null or "custom-openai", EndpointQuirks.HasCustomStoreSwitch(provider));
    }

    /// <summary>每一步都让模型调一次 <c>peek</c> 的 Responses 流(response id 是真的会被拿去续接的那种)。</summary>
    private const string FunctionCallSse =
        """
        event: response.created
        data: {"type":"response.created","sequence_number":0,"response":{"id":"resp_stub","object":"response","created_at":1,"status":"in_progress","model":"m","output":[]}}

        event: response.output_item.done
        data: {"type":"response.output_item.done","sequence_number":1,"output_index":0,"item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"peek","arguments":"{}","status":"completed"}}

        event: response.completed
        data: {"type":"response.completed","sequence_number":2,"response":{"id":"resp_stub","object":"response","created_at":1,"status":"completed","model":"m","output":[{"type":"function_call","id":"fc_1","call_id":"call_1","name":"peek","arguments":"{}","status":"completed"}]}}


        """;

    /// <summary>
    /// 工具结果那一步<b>整段重发</b>历史,不带 <c>previous_response_id</c>。
    /// </summary>
    /// <remarks>
    /// 真机上那个 400:自定义的中转站(Routin,背后是 ChatGPT 的 Codex 后端)没关 <c>store</c>,
    /// 适配器就把 response id 当会话 id 交上来,循环第二步只发 <c>previous_response_id</c> + 工具输出 ——
    /// 上游没存上一步,回 <c>previous_response_id is not available for this user</c> /
    /// <c>Upstream rejected the request</c>。Agent 模式下模型一调工具就挂。
    /// 走 <see cref="AiSettingsStore.CreateClient" /> 而不是裸 SDK:要守住的是插件实际发出去的东西。
    /// </remarks>
    [TestMethod]
    public async Task ToolLoop_ResendsTheWholeHistory_InsteadOfChainingOnAStoredResponse()
    {
        await using var stub = new SseStub(FunctionCallSse);
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        var relay = new AiProvider
        {
            Name = "relay",
            BaseUrl = stub.BaseUrl,
            DefaultProtocol = ChatProtocol.OpenAiResponses,
            Models = [new AiModelConfig { Model = "m" }],
            // 真机上那一家开着 store;现在自定义接入默认关,要显式勾上才是同一份配置
            CustomResponsesStore = true
        };
        Assert.IsTrue(EndpointQuirks.Of(relay).StoreResponses, "前提:store 开着 —— 正是真机上那一家的配置");
        using IChatClient client = store.CreateClient(new ResolvedModel(relay, relay.Models[0]), ProviderCredential.Key("k"))
            .AsBuilder()
            .UseFunctionInvocation(configure: c => c.MaximumIterationsPerRequest = 1)
            .Build();
        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "TOOL_OUTPUT", "peek")] };

        await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.System, "SYSTEM_MARKER"), new ChatMessage(ChatRole.User, "看看")], options))
        {
        }

        IReadOnlyList<string> requests = stub.Requests;
        Assert.IsGreaterThanOrEqualTo(2, requests.Count, "循环该跑到工具结果那一步");
        string followUp = requests[1];
        Assert.DoesNotContain("previous_response_id", followUp,
            $"续接要服务端存着上一步,中转站背后的上游多半没存。实际请求体:{followUp}");
        Assert.Contains("SYSTEM_MARKER", followUp, $"第二步该带着整段历史。实际请求体:{followUp}");
        Assert.Contains("\"function_call\"", followUp, $"模型那次调用也得在历史里。实际请求体:{followUp}");
        Assert.Contains("TOOL_OUTPUT", followUp, $"实际请求体:{followUp}");
    }

    /// <summary>每一步都让模型调一次 <c>peek</c> 的非流式 Responses 回应(同样带着会被拿去续接的 response id)。</summary>
    private const string FunctionCallJson =
        """{"id":"resp_stub","object":"response","created_at":1,"status":"completed","model":"m","output":[{"type":"function_call","id":"fc_1","call_id":"call_1","name":"peek","arguments":"{}","status":"completed"}]}""";

    /// <summary>
    /// 非流式那条路(<c>GetResponseAsync</c>)同样整段重发:适配器在非流式回应上也会把 response id
    /// 当会话 id 交上来,只守流式那一半的话,一次性请求(桥接、后续提问)仍会只带 <c>previous_response_id</c>。
    /// </summary>
    [TestMethod]
    public async Task ToolLoop_NonStreaming_AlsoResendsTheWholeHistory()
    {
        // 裸 TCP 回固定的 Responses JSON:SseStub 的非流式回应是 Chat Completions 形状,这里用不上
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        string baseUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        Task<string[]> received = Task.Run(async () =>
        {
            string[] bodies = new string[2];
            for (int i = 0; i < bodies.Length; i++)
            {
                using TcpClient connection = await listener.AcceptTcpClientAsync();
                using NetworkStream stream = connection.GetStream();
                // Content-Length 按字节算:请求头逐字节读到空行,正文按字节读满,别让多字节字符把读取卡住
                using var head = new MemoryStream();
                byte[] one = new byte[1];
                while (!head.GetBuffer().AsSpan(0, (int)head.Length).EndsWith("\r\n\r\n"u8))
                {
                    await stream.ReadExactlyAsync(one);
                    head.WriteByte(one[0]);
                }
                string length = Encoding.ASCII.GetString(head.GetBuffer(), 0, (int)head.Length).Split("\r\n")
                    .Single(header => header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                byte[] body = new byte[int.Parse(length["Content-Length:".Length..].Trim())];
                await stream.ReadExactlyAsync(body);
                bodies[i] = Encoding.UTF8.GetString(body);
                byte[] payload = Encoding.UTF8.GetBytes(FunctionCallJson);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n"));
                await stream.WriteAsync(payload);
            }
            return bodies;
        });
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        var relay = new AiProvider
        {
            Name = "relay",
            BaseUrl = baseUrl,
            DefaultProtocol = ChatProtocol.OpenAiResponses,
            Models = [new AiModelConfig { Model = "m" }],
            CustomResponsesStore = true
        };
        Assert.IsTrue(EndpointQuirks.Of(relay).StoreResponses, "前提:store 开着");
        using IChatClient client = store.CreateClient(new ResolvedModel(relay, relay.Models[0]), ProviderCredential.Key("k"))
            .AsBuilder()
            .UseFunctionInvocation(configure: c => c.MaximumIterationsPerRequest = 1)
            .Build();
        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "TOOL_OUTPUT", "peek")] };

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.System, "SYSTEM_MARKER"), new ChatMessage(ChatRole.User, "看看")], options);

        string followUp = (await received.WaitAsync(TimeSpan.FromSeconds(10)))[1];
        Assert.DoesNotContain("\"stream\":true", followUp, $"这条守的是非流式那条路。实际请求体:{followUp}");
        Assert.DoesNotContain("previous_response_id", followUp,
            $"续接要服务端存着上一步,中转站背后的上游多半没存。实际请求体:{followUp}");
        Assert.Contains("SYSTEM_MARKER", followUp, $"第二步该带着整段历史。实际请求体:{followUp}");
        Assert.Contains("\"function_call\"", followUp, $"模型那次调用也得在历史里。实际请求体:{followUp}");
        Assert.Contains("TOOL_OUTPUT", followUp, $"实际请求体:{followUp}");
    }
}
