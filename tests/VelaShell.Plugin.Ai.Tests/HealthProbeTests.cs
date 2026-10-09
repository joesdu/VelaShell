using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 探活:极小真请求。答得上来就算通(带回文),连不上/被拒就算不通;用户取消照旧抛出。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class HealthProbeTests
{
    private static ResolvedModel ModelFor(string baseUrl, ChatProtocol protocol = ChatProtocol.OpenAiChatCompletions)
    {
        var provider = new AiProvider
        {
            Name = "stub",
            BaseUrl = baseUrl,
            DefaultProtocol = protocol,
            Models = [new AiModelConfig { Model = "probe-model" }]
        };
        return new ResolvedModel(provider, provider.Models[0]);
    }

    [TestMethod]
    [DataRow("\u200B", false)]
    [DataRow("\u0000", false)]
    [DataRow("\u200D\u202E\u0007", false)]
    [DataRow(" \u200B\r\n ", false)]
    [DataRow("\uFE0F", false)]
    [DataRow("\u200D\uFE0F", false)]
    [DataRow("\u0301", false)]
    [DataRow("\u115F\u1160", false)]
    [DataRow("\u2800", false)]
    [DataRow("\u3164\uFFA0", false)]
    [DataRow("\u3164OK", true)]
    [DataRow("OK", true)]
    [DataRow("O\u200BK", true)]
    [DataRow("😀", true)]
    [DataRow("e\u0301", true)]
    [DataRow("❤️", true)]
    [DataRow("👨‍👩‍👧‍👦", true)]
    public async Task Review_ProbeRequiresVisibleReply(string reply, bool alive)
    {
        using var stub = new SseStub(OpenAiStream(reply));
        using var context = new TestPluginContext();
        var (error, text) = await HealthProbe.ProbeAsync(new AiSettingsStore(context), ModelFor(stub.BaseUrl));
        if (alive)
        {
            Assert.IsNull(error);
            Assert.AreEqual(reply, text);
        }
        else
        {
            Assert.IsInstanceOfType<HealthProbe.EmptyReplyException>(error);
            Assert.AreEqual("", text);
        }
    }

    [TestMethod]
    public async Task ErrorEndpoint_DisconnectedUploadDoesNotStopNextRequest()
    {
        using var endpoint = new ErrorStub(HttpStatusCode.Unauthorized);
        var uri = new Uri(endpoint.BaseUrl);
        using (var disconnected = new TcpClient())
        {
            await disconnected.ConnectAsync(uri.Host, uri.Port);
            byte[] partial = Encoding.ASCII.GetBytes($"POST /v1/chat/completions HTTP/1.1\r\nHost: {uri.Authority}\r\nContent-Length: 100000\r\n\r\npartial");
            await disconnected.GetStream().WriteAsync(partial);
            Assert.IsTrue(await endpoint.RequestBodyStartedAsync.WaitAsync(TimeSpan.FromSeconds(5)),
                "HttpContext 已被服务端接受,完整请求体读取已启动且仍 pending;不能等首字符读完才断连");
            Assert.AreEqual(0, endpoint.Requests, "服务端确实正在读未完成请求体,尚不能计作完整请求");
            disconnected.Client.LingerState = new LingerOption(true, 0);
        }
        Exception failure = await endpoint.RequestFailureAsync.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(failure is IOException or HttpListenerException or ObjectDisposedException, failure.ToString());
        Assert.AreEqual(0, endpoint.Requests, "真实断连异常发生在未完成 body 的读取阶段,不是下一请求的响应阶段");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.PostAsync(endpoint.BaseUrl, new StringContent("{}"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("invalid_api_key", await response.Content.ReadAsStringAsync());
        Assert.AreEqual(1, endpoint.Requests, "只统计随后真正读完并返回 401 的请求");
    }

    [TestMethod]
    public async Task LiveEndpointPassesAndReturnsTheEcho()
    {
        using var stub = new SseStub(OpenAiStream("OK"));
        using var context = new TestPluginContext();

        (Exception? error, string text) = await HealthProbe.ProbeAsync(new AiSettingsStore(context), ModelFor(stub.BaseUrl));

        Assert.IsNull(error, error?.ToString());
        Assert.AreEqual("OK", text);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FixedCredentialProbe_MultipleUsageFramesAreSummedOnceAtCompletion(bool empty)
    {
        string sse = """
            data: {"id":"probe","object":"chat.completion.chunk","created":1,"model":"probe-model","choices":[],"usage":{"prompt_tokens":7,"completion_tokens":3,"total_tokens":10,"prompt_tokens_details":{"cached_tokens":2},"completion_tokens_details":{"reasoning_tokens":1}}}

            data: {"id":"probe","object":"chat.completion.chunk","created":1,"model":"probe-model","choices":[],"usage":{"prompt_tokens":11,"completion_tokens":5,"total_tokens":16,"prompt_tokens_details":{"cached_tokens":4},"completion_tokens_details":{"reasoning_tokens":2}}}

            """ + "\n\n" + OpenAiStream(empty ? "" : "OK");
        using var stub = new SseStub(sse);
        using var context = new TestPluginContext();
        int calls = 0;
        UsageDetails? received = null;
        // Probe 没有逐帧回调;这里只验证结束契约,不把 Request/Flush 假称为已消费首帧。
        (Exception? error, string text) = await HealthProbe.ProbeAsync(new AiSettingsStore(context), ModelFor(stub.BaseUrl),
            ProviderCredential.Key(null), usageCallback: usage => { calls++; received = usage; }).WaitAsync(TimeSpan.FromSeconds(5));
        if (empty) Assert.IsInstanceOfType(error, typeof(HealthProbe.EmptyReplyException));
        else Assert.IsNull(error, error?.ToString());
        Assert.AreEqual(empty ? "" : "OK", text);
        Assert.AreEqual(1, calls, "成功和空白失败都只在结束时结算一次");
        Assert.IsNotNull(received);
        Assert.AreEqual(18L, received.InputTokenCount);
        Assert.AreEqual(8L, received.OutputTokenCount);
        Assert.AreEqual(6L, received.CachedInputTokenCount);
        Assert.AreEqual(3L, received.ReasoningTokenCount);
    }

    [TestMethod]
    public async Task FixedCredentialProbe_AnthropicSplitUsageUsesNativeAggregation()
    {
        using var stub = new SseStub(AnthropicReply);
        using var context = new TestPluginContext();
        ResolvedModel model = ModelFor(stub.BaseUrl, ChatProtocol.AnthropicMessages);
        int calls = 0;
        UsageDetails? received = null;
        (Exception? error, string text) = await HealthProbe.ProbeAsync(new AiSettingsStore(context), model,
            ProviderCredential.Key("test-key"), usageCallback: usage => { calls++; received = usage; });
        Assert.IsNull(error, error?.ToString());
        Assert.AreEqual("OK", text);
        Assert.AreEqual(1, calls, "message_start 和 message_delta 的用量必须合为一次结算");
        Assert.IsNotNull(received);
        Assert.AreEqual(1L, received.InputTokenCount);
        Assert.AreEqual(1L, received.OutputTokenCount);
    }

    [TestMethod]
    public async Task FixedCredentialProbe_DoesNotInventUnreportedUsage()
    {
        using var stub = new SseStub(OpenAiStream("OK"));
        using var context = new TestPluginContext();
        int calls = 0;
        (Exception? error, _) = await HealthProbe.ProbeAsync(new AiSettingsStore(context), ModelFor(stub.BaseUrl),
            ProviderCredential.Key(null), usageCallback: _ => calls++);
        Assert.IsNull(error, error?.ToString());
        Assert.AreEqual(0, calls, "正文和预设输出额度不是报文用量证据");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ProbeHealthFollowsStreamingSupportRatherThanNonStreamingSupport(bool streamOnly)
    {
        using var endpoint = new ProbeEndpoint(request =>
        {
            bool streaming = request.TryGetProperty("stream", out JsonElement stream) && stream.GetBoolean();
            return streaming == streamOnly
                ? (HttpStatusCode.OK, streaming ? OpenAiStream("OK") : OpenAiReply)
                : (HttpStatusCode.BadRequest, """{"error":{"message":"unsupported response mode","type":"invalid_request_error"}}""");
        });
        using var context = new TestPluginContext();

        (Exception? error, string text) = await HealthProbe.ProbeAsync(new AiSettingsStore(context), ModelFor(endpoint.BaseUrl));
        using var request = JsonDocument.Parse(await endpoint.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.IsTrue(request.RootElement.GetProperty("stream").GetBoolean());
        if (streamOnly)
        {
            Assert.IsNull(error, error?.ToString());
            Assert.AreEqual("OK", text);
        }
        else
        {
            Assert.IsNotNull(error, "只接受非流式的端点不能成为正式流式请求的候选");
            Assert.AreEqual("", text);
        }
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("data: [DONE]\n\n")]
    [DataRow("data: {\"id\":\"probe\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"probe-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":0,\"total_tokens\":1}}\n\ndata: [DONE]\n\n")]
    [DataRow("data: {\"id\":\"probe\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"probe-model\",\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"正在思考\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n")]
    [DataRow("data: {\"id\":\"probe\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"probe-model\",\"choices\":[{\"index\":0,\"delta\":{\"reasoning\":\"正在思考\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n")]
    public async Task StreamsWithoutATextAnswerAreNotAlive(string sse)
    {
        using var stub = new SseStub(sse, jsonContent: "OK");
        using var context = new TestPluginContext();

        (Exception? error, string text) = await HealthProbe.ProbeAsync(new AiSettingsStore(context), ModelFor(stub.BaseUrl));

        Assert.IsInstanceOfType(error, typeof(HealthProbe.EmptyReplyException));
        Assert.AreEqual("", text);
    }

    [TestMethod]
    public async Task WhitespaceOnlyStreamIsNotAlive()
    {
        using var stub = new SseStub(OpenAiStream(" \t\r\n "), jsonContent: "OK");
        using var context = new TestPluginContext();

        (Exception? error, string text) = await HealthProbe.ProbeAsync(new AiSettingsStore(context), ModelFor(stub.BaseUrl));

        Assert.IsInstanceOfType(error, typeof(HealthProbe.EmptyReplyException));
        Assert.AreEqual("", text);
    }

    [TestMethod]
    public async Task ProbeWaitsForTheCompleteStreamAndCombinesTextDeltas()
    {
        string sse = OpenAiStream(" O").Replace("\"finish_reason\":\"stop\"", "\"finish_reason\":null", StringComparison.Ordinal)
            .Replace("data: [DONE]\n\n", "", StringComparison.Ordinal) + OpenAiStream("K ");
        using var stub = new SseStub(sse);
        using var context = new TestPluginContext();
        (Exception? error, string text) = await HealthProbe.ProbeAsync(new AiSettingsStore(context), ModelFor(stub.BaseUrl))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsNull(error, error?.ToString());
        Assert.AreEqual("OK", text, "首段只有 O;必须等到第二段才能组合出完整探活回复");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RealSdk_FirstParsedEventPrecedesStreamCompletionOrCancellation(bool cancel)
    {
        string sse = OpenAiStream(" O").Replace("\"finish_reason\":\"stop\"", "\"finish_reason\":null", StringComparison.Ordinal)
            .Replace("data: [DONE]\n\n", "", StringComparison.Ordinal) + OpenAiStream("K ");
        using var stub = new SseStub(sse, holdAfterFirstChunk: true);
        using var context = new TestPluginContext();
        using var cancellation = new CancellationTokenSource();
        using IChatClient client = new AiSettingsStore(context).CreateClient(ModelFor(stub.BaseUrl), ProviderCredential.Key(null));
        await using IAsyncEnumerator<ChatResponseUpdate> updates = client.GetStreamingResponseAsync(
            "Reply with exactly: OK", cancellationToken: cancellation.Token).GetAsyncEnumerator();
        try
        {
            Task<bool> first = updates.MoveNextAsync().AsTask();
            await stub.FirstChunkFlushedAsync.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(await first.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(" O", updates.Current.Text, "真实 store SDK 的首 MoveNext 已解析正文,不是服务端发送信号");
            string firstText = updates.Current.Text;
            Task<bool> next = updates.MoveNextAsync().AsTask();
            Assert.IsFalse(next.IsCompleted, "首段已经消费,下一段仍被显式闸门挡住");
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => next.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            else
            {
                var text = new StringBuilder(firstText);
                stub.Release();
                Assert.IsTrue(await next.WaitAsync(TimeSpan.FromSeconds(5)));
                text.Append(updates.Current.Text);
                while (await updates.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))) text.Append(updates.Current.Text);
                Assert.AreEqual("OK", text.ToString().Trim());
            }
        }
        finally { cancellation.Cancel(); stub.Release(); }
    }

    [TestMethod]
    public async Task RealSdk_TruncatedStreamYieldsUsageBeforeTransportFailure()
    {
        using var stub = new TruncatedSseStub("""
            data: {"id":"probe","object":"chat.completion.chunk","created":1,"model":"probe-model","choices":[],"usage":{"prompt_tokens":7,"completion_tokens":3,"total_tokens":10}}

            """);
        using var context = new TestPluginContext();
        using IChatClient client = new AiSettingsStore(context).CreateClient(ModelFor(stub.BaseUrl), ProviderCredential.Key(null));
        await using IAsyncEnumerator<ChatResponseUpdate> updates = client.GetStreamingResponseAsync("Reply with exactly: OK").GetAsyncEnumerator();
        Assert.IsTrue(await updates.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        UsageDetails usage = updates.Current.Contents.OfType<UsageContent>().Single().Details;
        Assert.AreEqual(7L, usage.InputTokenCount);
        Assert.AreEqual(3L, usage.OutputTokenCount);
        Exception failure = await Assert.ThrowsAsync<Exception>(() => updates.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsFalse(failure is TimeoutException or OperationCanceledException, "必须由真实 HTTP 截断失败,不能把等不到事件当成覆盖");
        Assert.AreEqual(1, stub.Requests);
    }

    [TestMethod]
    public async Task UserCancellationWhileAwaitingAStreamIsNotAHealthFailure()
    {
        using var stub = new SseStub(OpenAiStream("OK"), hold: true);
        using var context = new TestPluginContext();
        using var cancellation = new CancellationTokenSource();
        Task<(Exception? Error, string Text)> probe = HealthProbe.ProbeAsync(new AiSettingsStore(context), ModelFor(stub.BaseUrl),
            cancellationToken: cancellation.Token);
        try
        {
            await stub.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => probe.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            stub.Release();
        }
    }

    [TestMethod]
    public async Task RejectedEndpointIsNotAlive()
    {
        using var stub = new ErrorStub();
        using var context = new TestPluginContext();

        var (error, _) = await HealthProbe.ProbeAsync(new AiSettingsStore(context), ModelFor(stub.BaseUrl));

        Assert.IsNotNull(error, "401 的端点不算活的 —— 那正是要绕开它的时候");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("OK")]
    [DataRow("O\nK")]
    public async Task EmptyReplyIsNotAlive(string stopSequences)
    {
        // 即使已避开停止词,端点真正不回答也不能被判活。
        using var stub = new ProbeEndpoint(request =>
            (HttpStatusCode.OK, ReplyFollowingPromptAndStops(request, empty: true)));
        using var context = new TestPluginContext();

        ResolvedModel model = ModelFor(stub.BaseUrl);
        model.Config.StopSequences = stopSequences;
        var (error, text) = await HealthProbe.ProbeAsync(new AiSettingsStore(context), model);

        Assert.IsInstanceOfType(error, typeof(HealthProbe.EmptyReplyException),
            "空回复不能判通过");
        Assert.AreEqual("", text);
    }

    [TestMethod]
    [DataRow("OK")]
    [DataRow("O")]
    [DataRow("K")]
    [DataRow("O\nK\n!\n\"\n#\n$\n%")]
    [DataRow("END\nDONE\nhello world\n你好")]
    public async Task ProbeAnswerAvoidsSavedStopSequencesWithoutRemovingThem(string stopSequences)
    {
        await AssertStopSafeProbeAsync(stopSequences);
    }

    [TestMethod]
    public async Task ProbeAnswerAvoidsStopsCoveringEveryPrintableAsciiCharacter()
    {
        string stops = "OK\n" + string.Join("\n", Enumerable.Range('!', '~' - '!' + 1).Select(value => ((char)value).ToString()));
        await AssertStopSafeProbeAsync(stops);
    }

    private static async Task AssertStopSafeProbeAsync(string stopSequences)
    {
        using var endpoint = new ProbeEndpoint(request =>
            (HttpStatusCode.OK, ReplyFollowingPromptAndStops(request)));
        using var context = new TestPluginContext();
        ResolvedModel model = ModelFor(endpoint.BaseUrl);
        model.Config.StopSequences = stopSequences;

        (Exception? error, string text) = await HealthProbe.ProbeAsync(new AiSettingsStore(context), model);
        using var request = JsonDocument.Parse(await endpoint.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(5)));
        string[] sentStops = request.RootElement.GetProperty("stop").EnumerateArray().Select(stop => stop.GetString()!).ToArray();

        CollectionAssert.AreEqual(stopSequences.Split('\n'), sentStops,
            "探测必须原样下发正式停止词,不能靠删除参数通过");
        Assert.IsNull(error, error?.ToString());
        string prompt = request.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!;
        Assert.AreEqual(prompt["Reply with exactly: ".Length..], text,
            "真实遵守停止词的端点应当完整返回所要求的短答案");
    }

    [TestMethod]
    public async Task ProbeAppliesTheSameEndpointQuirksAsRealRequests()
    {
        // 有的后端(内置 Codex)拒绝 max_output_tokens 字段。探活不带上正式请求那套端点怪癖,
        // 一个能正常聊天的接入就会被自己的「测试 / 全部检测 / 切换前探测」误判成不可用。
        using var stub = new SseStub(OpenAiStream("OK"));
        using var context = new TestPluginContext();
        var provider = new AiProvider
        {
            Name = "quirky",
            BaseUrl = stub.BaseUrl,
            DefaultProtocol = ChatProtocol.OpenAiChatCompletions,
            UnsupportedParameters = "max_output_tokens",
            Models = [new AiModelConfig { Model = "probe-model" }]
        };

        (Exception? error, _) = await HealthProbe.ProbeAsync(
            new AiSettingsStore(context), new ResolvedModel(provider, provider.Models[0]));
        string body = await stub.RequestBodyAsync;

        Assert.IsNull(error, error?.ToString());
        Assert.IsFalse(body.Contains("max_tokens", StringComparison.Ordinal),
            $"探活请求没过 ApplyEndpointQuirks,把不支持的字段发出去了:{body}");
        Assert.IsFalse(body.Contains("max_completion_tokens", StringComparison.Ordinal),
            $"同上,字段名随 SDK 版本而异,两个都不该出现:{body}");
    }

    [TestMethod]
    public async Task ReasoningBudgetDoesNotMakeHealthyEndpointLookEmpty()
    {
        using var stub = new ProbeEndpoint(request =>
            (HttpStatusCode.OK, OpenAiStream(request.GetProperty("max_completion_tokens").GetInt32() >= 1024 ? "OK" : "")));
        using var context = new TestPluginContext();
        ResolvedModel model = ModelFor(stub.BaseUrl);
        model.Config.MaxTokens = 2048;

        (Exception? error, string text) = await HealthProbe.ProbeAsync(new AiSettingsStore(context), model);

        Assert.IsNull(error, error?.ToString());
        Assert.AreEqual("OK", text);
    }

    [TestMethod]
    public async Task HighReasoningRejectedByEndpointIsNotMarkedAlive()
    {
        using var endpoint = new ProbeEndpoint(request =>
            request.TryGetProperty("reasoning_effort", out JsonElement effort)
            && effort.GetString() == "high"
                ? (HttpStatusCode.BadRequest, """{"error":{"message":"high reasoning is unsupported","type":"invalid_request_error"}}""")
                : (HttpStatusCode.OK, ReplyFollowingPromptAndStops(request)));
        using var context = new TestPluginContext();
        ResolvedModel model = ModelFor(endpoint.BaseUrl).WithReasoning(ReasoningLevel.High);

        (Exception? error, _) = await HealthProbe.ProbeAsync(new AiSettingsStore(context), model);
        string body = await endpoint.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("\"reasoning_effort\":\"high\"", body,
            "探活必须下发正式流请求的 High 档位,否则不支持 High 的端点会被误判为可用");
        Assert.IsNotNull(error, "端点拒绝 High 的真实 HTTP 400 不能标活");
    }

    [TestMethod]
    [DataRow("temperature")]
    [DataRow("top_p")]
    [DataRow("stop")]
    public async Task ProbeRejectsAnEndpointThatCannotAcceptSavedSamplingOptions(string field)
    {
        using var endpoint = new ProbeEndpoint(request => request.TryGetProperty(field, out _)
            ? (HttpStatusCode.BadRequest, """{"error":{"message":"unsupported sampling parameter","type":"invalid_request_error"}}""")
            : (HttpStatusCode.OK, ReplyFollowingPromptAndStops(request)));
        using var context = new TestPluginContext();
        ResolvedModel model = ModelFor(endpoint.BaseUrl);
        model.Config.Temperature = 0.7f;
        model.Config.TopP = 0.8f;
        model.Config.StopSequences = "END\nDONE";

        (Exception? error, _) = await HealthProbe.ProbeAsync(new AiSettingsStore(context), model);

        Assert.IsNotNull(error, "端点拒绝当前采样或停止参数时,探活不能先亮绿灯再让正式请求失败");
    }

    [TestMethod]
    public async Task AnthropicThinkingBudgetAndRaisedLimitAllowHealthyProbe()
    {
        using var endpoint = new ProbeEndpoint(request =>
        {
            if (!request.TryGetProperty("thinking", out JsonElement thinking)
                || thinking.GetProperty("type").GetString() != "enabled"
                || thinking.GetProperty("budget_tokens").GetInt32() < 1024
                || !request.TryGetProperty("max_tokens", out JsonElement maxTokens)
                || maxTokens.GetInt32() <= thinking.GetProperty("budget_tokens").GetInt32())
            {
                return (HttpStatusCode.BadRequest, """{"type":"error","error":{"type":"invalid_request_error","message":"invalid thinking budget"}}""");
            }
            return (HttpStatusCode.OK, AnthropicReply);
        });
        using var context = new TestPluginContext();
        var provider = new AiProvider
        {
            Name = "anthropic",
            BaseUrl = endpoint.BaseUrl,
            DefaultProtocol = ChatProtocol.AnthropicMessages,
            Models = [new AiModelConfig { Model = "probe-model", MaxTokens = 512, Reasoning = ReasoningLevel.High }]
        };

        (Exception? error, string text) = await HealthProbe.ProbeAsync(
            new AiSettingsStore(context), new ResolvedModel(provider, provider.Models[0]), apiKeyOverride: "test-key");
        using JsonDocument sent = JsonDocument.Parse(await endpoint.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.AreEqual(1024, sent.RootElement.GetProperty("thinking").GetProperty("budget_tokens").GetInt32());
        Assert.AreEqual(2048, sent.RootElement.GetProperty("max_tokens").GetInt32(),
            "实际 Anthropic 请求得把额度抬到能装下思考和正文,不能仅改 ChatOptions.MaxOutputTokens");
        Assert.IsNull(error, error?.ToString());
        Assert.AreEqual("OK", text);
    }

    private const string OpenAiReply = """{"id":"probe","object":"chat.completion","created":1,"model":"probe-model","choices":[{"index":0,"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}]}""";
    private const string AnthropicReply = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_probe","type":"message","role":"assistant","model":"probe-model","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":0}}}

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

    private static string OpenAiStream(string text)
        => "data: " + JsonSerializer.Serialize(new
        {
            id = "probe", @object = "chat.completion.chunk", created = 1, model = "probe-model",
            choices = new[] { new { index = 0, delta = new { content = text }, finish_reason = "stop" } }
        }) + "\n\ndata: [DONE]\n\n";

    private static string ReplyFollowingPromptAndStops(JsonElement request, bool empty = false)
    {
        const string prefix = "Reply with exactly: ";
        string prompt = request.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!;
        Assert.IsTrue(prompt.StartsWith(prefix, StringComparison.Ordinal));
        string reply = prompt[prefix.Length..];
        int end = reply.Length;
        if (request.TryGetProperty("stop", out JsonElement stops))
        {
            foreach (JsonElement stop in stops.EnumerateArray())
            {
                int index = reply.IndexOf(stop.GetString()!, StringComparison.Ordinal);
                if (index >= 0)
                {
                    end = Math.Min(end, index);
                }
            }
        }
        return OpenAiStream(empty ? "" : reply[..end]);
    }

    private sealed class ProbeEndpoint : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly TaskCompletionSource<string> _request = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string BaseUrl { get; }
        public Task<string> RequestBodyAsync => _request.Task;

        public ProbeEndpoint(Func<JsonElement, (HttpStatusCode Status, string Reply)> respond)
        {
            for (int attempt = 0; ; attempt++)
            {
                var portProbe = new TcpListener(IPAddress.Loopback, 0);
                portProbe.Start();
                int port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
                portProbe.Stop();
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    listener.Start();
                    _listener = listener;
                    BaseUrl = $"http://127.0.0.1:{port}";
                    break;
                }
                catch (HttpListenerException ex) when (attempt < 9 && ex.ErrorCode is 5 or 183)
                {
                    listener.Close();
                }
                catch
                {
                    listener.Close();
                    throw;
                }
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    HttpListenerContext context = await _listener.GetContextAsync();
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    string body = await reader.ReadToEndAsync();
                    _request.TrySetResult(body);
                    using JsonDocument request = JsonDocument.Parse(body);
                    (HttpStatusCode status, string reply) = respond(request.RootElement);
                    byte[] bytes = Encoding.UTF8.GetBytes(reply);
                    context.Response.StatusCode = (int)status;
                    context.Response.ContentType = status == HttpStatusCode.OK
                        && request.RootElement.TryGetProperty("stream", out JsonElement stream) && stream.GetBoolean()
                        ? "text/event-stream" : "application/json";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
                catch (Exception ex)
                {
                    _request.TrySetException(ex);
                }
            });
        }

        public void Dispose() => _listener.Close();
    }

}
