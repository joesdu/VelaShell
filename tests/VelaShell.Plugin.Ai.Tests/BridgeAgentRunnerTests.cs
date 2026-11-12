using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Bridge;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.RemoteFs;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>无头 agent 回合里那些不需要真的调模型就能钉住的行为。</summary>
[TestClass]
public sealed class BridgeAgentRunnerTests
{
    private static readonly Loc English = new("en");

    private static BridgeAgentRunner Create(TestPluginContext context)
        => new(context, new AiSettingsStore(context), new ChatHistoryStore(context), new McpManager(context));

    private static InboundMessage Message(string text = "hi")
        => new("ch1", "chat-1", false, "u1", "Ann", text, "m1", true);

    private static Task<bool> Deny(ApprovalRequest _) => Task.FromResult(false);

    [TestMethod]
    public async Task RunAsync_WithNoModelConfigured_SaysSoInsteadOfThrowing()
    {
        using var context = new TestPluginContext();
        BridgeTurn turn = await Create(context).RunAsync(new BridgeConversation("ch1", "chat-1"),
            new BridgeSettings(), Message(), Deny, English, null, null, CancellationToken.None);

        Assert.AreEqual(English["BridgeNoModel"], turn.Text);
        Assert.AreEqual("", turn.Model);
    }

    [TestMethod]
    public async Task ImageModel_IsRejectedBeforeAnyBillableRequest()
    {
        using var context = new TestPluginContext();
        var provider = new AiProvider
        {
            BaseUrl = "http://127.0.0.1:1/v1",
            DefaultProtocol = ChatProtocol.OpenAiImages,
            Models = [new AiModelConfig { Model = "image-2" }]
        };
        await new AiSettingsStore(context).SaveAsync(new AiSettings
        { Providers = [provider], ActiveModelId = provider.Models[0].Id });
        BridgeTurn turn = await Create(context).RunAsync(new BridgeConversation("ch1", "chat-1"),
            new BridgeSettings(), Message(), Deny, English, null, null, CancellationToken.None);
        Assert.AreEqual(English["ImageDesktopOnly"], turn.Text);
    }

    [TestMethod]
    public async Task EmptyModelSteps_ExhaustSharedRecovery_WithLocalizedError()
    {
        await using var endpoint = new SseStub("data: [DONE]\n\n");
        using var context = new TestPluginContext();
        var provider = new AiProvider
        {
            Name = "empty",
            BaseUrl = endpoint.BaseUrl,
            DefaultProtocol = ChatProtocol.OpenAiChatCompletions,
            Models = [new AiModelConfig { Model = "m" }]
        };
        await new AiSettingsStore(context).SaveAsync(new AiSettings
        { Providers = [provider], ActiveModelId = provider.Models[0].Id });
        var conversation = new BridgeConversation("ch1", "chat-1");
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create(context).RunAsync(conversation, new BridgeSettings { Mode = ChatMode.Chat },
                Message(), Deny, new Loc("zh-Hans"), null, null, CancellationToken.None));
        Assert.Contains(new Loc("zh-Hans")["ProbeEmptyReply"], error.Message);
        Assert.HasCount(3, endpoint.Requests);
    }

    [TestMethod]
    [DataRow(ChatMode.Chat)]
    [DataRow(ChatMode.Agent)]
    [DataRow(ChatMode.Plan)]
    public async Task WhitespaceModelSteps_RetryUnchanged_WithoutContinuationBoundary(ChatMode mode)
    {
        // 纯空白与空步骤同属未开口:各模式都原样补试,不写续接边界、不追加"已中断"提示
        await using var endpoint = new SseStub("""
            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":" \t\n "},"finish_reason":"stop"}]}

            data: [DONE]


            """);
        using var context = new TestPluginContext();
        var provider = new AiProvider
        {
            Name = "blank",
            BaseUrl = endpoint.BaseUrl,
            DefaultProtocol = ChatProtocol.OpenAiChatCompletions,
            Models = [new AiModelConfig { Model = "m" }]
        };
        await new AiSettingsStore(context).SaveAsync(new AiSettings
        { Providers = [provider], ActiveModelId = provider.Models[0].Id });
        var conversation = new BridgeConversation("ch1", "chat-1");
        var loc = new Loc("zh-Hans");
        List<string> progress = [];
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create(context).RunAsync(conversation, new BridgeSettings { Mode = mode },
                Message(), Deny, loc, progress.Add, null, CancellationToken.None));
        Assert.Contains(loc["ProbeEmptyReply"], error.Message);
        Assert.HasCount(3, endpoint.Requests);
        Assert.AreEqual(endpoint.Requests[0], endpoint.Requests[1], "补试请求必须与首次相同");
        Assert.AreEqual(endpoint.Requests[0], endpoint.Requests[2], "补试请求必须与首次相同");
        string boundary = loc["ContinuationBoundary"].Trim();
        Assert.DoesNotContain(text => text.Contains(boundary, StringComparison.Ordinal), progress, "群里不能看到续接边界");
        Assert.DoesNotContain(m => m.Text.Contains(boundary, StringComparison.Ordinal), conversation.History, "历史不能记下续接边界");
    }

    /// <summary>
    /// AI 设置必须<b>每轮现读</b>,不能吃桥接启动时的那份快照。
    /// </summary>
    /// <remarks>
    /// 这条是线上撞到的那个 bug 的形状:用户在设置窗口登录了订阅制供应商(OpenAI Codex),
    /// 聊天面板立刻好用,而桥接手里那份 <c>AiProvider</c> 还是登录之前的形态
    /// (<c>Auth</c> 仍是 ApiKey、没有 OAuth 配置),于是凭据解析走了"取 API Key"那条岔路,
    /// 把一个空 Key 发了出去 —— 群里看到 401「Could not parse your authentication token」,
    /// 而同一刻面板一切正常。
    /// <para>
    /// 用例做法:<b>先</b>造好 runner,<b>之后</b>才往库里写供应商。第一轮必须说"没配模型",
    /// 第二轮必须已经看得见它 —— 报错里带的模型名就是证据(环回端点回 401,
    /// 所以第二轮一定失败,但失败的原因得是服务商拒绝,不是"没模型")。
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task RunAsync_RereadsTheAiSettingsEveryTurn()
    {
        await using var endpoint = new ErrorStub();
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        BridgeAgentRunner runner = Create(context);
        var conversation = new BridgeConversation("ch1", "chat-1");
        var bridge = new BridgeSettings { Mode = ChatMode.Chat }; // 纯对话:不碰工具,免得牵进 MCP

        BridgeTurn before = await runner.RunAsync(conversation, bridge, Message(), Deny, English, null, null, CancellationToken.None);
        Assert.AreEqual(English["BridgeNoModel"], before.Text, "with nothing configured it should say so");

        // runner 造好之后才配上模型 —— 快照式实现在这一步之后仍然会说"没配模型"
        await store.SaveAsync(new AiSettings
        {
            Providers =
            [
                new AiProvider
                {
                    Id = "p1",
                    Name = "Local",
                    BaseUrl = endpoint.BaseUrl + "/v1",
                    DefaultProtocol = ChatProtocol.OpenAiChatCompletions,
                    Models = [new AiModelConfig { Id = "m1", Model = "probe" }]
                }
            ],
            ActiveModelId = "m1"
        });

        InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => runner.RunAsync(conversation, bridge, Message(), Deny, English, null, null, CancellationToken.None));

        // 报错里带着模型名 = 它确实读到了刚写进去的那份设置
        Assert.Contains("Local / probe", error.Message);
        Assert.IsGreaterThan(0, endpoint.Requests, "必须实际调用新配置的供应商");
    }

    /// <summary>
    /// 报错必须点名是<b>哪个模型</b>。
    /// </summary>
    /// <remarks>
    /// 群里只看到一句 401 时,人第一反应是去查飞书的凭证 —— 而那条 401 来自模型服务商。
    /// 两者差着十万八千里,不点名就得靠猜。
    /// </remarks>
    [TestMethod]
    public async Task RunAsync_NamesTheModelWhenTheProviderRejectsTheCall()
    {
        await using var endpoint = new ErrorStub();
        using var context = new TestPluginContext();
        await new AiSettingsStore(context).SaveAsync(new AiSettings
        {
            Providers =
            [
                new AiProvider
                {
                    Id = "p1",
                    Name = "Acme",
                    BaseUrl = endpoint.BaseUrl + "/v1",
                    DefaultProtocol = ChatProtocol.OpenAiChatCompletions,
                    Models = [new AiModelConfig { Id = "m1", Model = "x", Name = "some-model" }]
                }
            ],
            ActiveModelId = "m1"
        });

        InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => Create(context).RunAsync(new BridgeConversation("ch1", "chat-1"),
                new BridgeSettings { Mode = ChatMode.Chat }, Message(), Deny, English, null, null, CancellationToken.None));

        Assert.StartsWith("Acme / some-model:", error.Message);
        Assert.IsGreaterThan(0, endpoint.Requests, "必须实际收到供应商的拒绝响应");
    }

    /// <summary>
    /// 一轮收尾时,看到的传输都已终结 —— 哪怕是停止未确认的 Unknown —— 就释放 IM 进度订阅。
    /// </summary>
    /// <remarks>
    /// 停止未确认的 Unknown/TimedOut 留给显式清理,所以一直算 <c>HasRunningTransfers</c>;收尾只看那一条的话,
    /// 订阅连同捕获的渠道回调要挂到桥接重载才放。桌面卡片(<c>TransferBinding.Drain</c>)早就按"看到的都终结了"收。
    /// </remarks>
    [TestMethod]
    public async Task TransferObserver_IsReleasedAtFinishWhenEverySeenTransferIsTerminal()
    {
        using var context = new TestPluginContext();
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.StartUploadHandler = (_, source, target, options, _) => Task.FromResult(new RemoteTransferSnapshot
        {
            TransferId = "transfer",
            OperationId = options.OperationId,
            SessionId = "session",
            LocalPath = source,
            RemotePath = target,
            State = RemoteTransferState.Unknown,
            RemoteStopConfirmed = false,
            Sequence = 1,
            Generation = 1
        });
        FileTransferRunScope scope = FileTransferJobRegistry.For(context).BeginRun("owner", "run");
        int released = 0;
        using var observer = new BridgeTransferObserver(scope, English, _ => { }, _ => { }, _ => released++);
        await scope.StartAsync("session", "source", "/target", "operation", false, default);
        Assert.IsTrue(scope.HasRunningTransfers, "停止未确认的 Unknown 仍留给显式清理");

        observer.Finish();

        Assert.AreEqual(1, released, "看到的传输都已终结,收尾就该释放订阅");
    }

    /// <summary>
    /// IM 并发的两轮共用一个 <see cref="McpManager" />:外部 MCP 工具的审批必须落回发起它的那个聊天,
    /// 用的也必须是那个聊天自己的审批挡位。
    /// </summary>
    /// <remarks>
    /// 以前每轮开头把挡位与回调写到共享的 <c>McpManager</c> 上,工具调用时才去读 —— 第二个聊天一开轮次,
    /// 第一个聊天里挂着的 MCP 调用就拿着第二个聊天的回调去要审批(审批卡发到了别的群),
    /// 第二个聊天是「全部自动」时干脆不问就执行了。
    /// 用例用插件自己的对外 MCP 服务当外部服务器(真 HTTP、真 MCP 客户端),两个模型各自让模型调一次它的工具;
    /// 第一个聊天的模型请求挂住,等第二个聊天整轮跑完再放行。
    /// </remarks>
    [TestMethod]
    [DataRow(ApprovalMode.Ask, DisplayName = "第二个聊天也是每次询问:回调不能串")]
    [DataRow(ApprovalMode.Bypass, DisplayName = "第二个聊天是全部自动:挡位不能串")]
    public async Task ConcurrentTurns_ExternalMcpApprovalsStayWithTheirOwnChat(ApprovalMode secondApproval)
    {
        using var context = new TestPluginContext();
        context.FakeSessions.AddConnected(host: "prod-1", username: "root");
        var serverStore = new Interop.McpServerSettingsStore(context);
        int port = FreePort();
        await serverStore.SaveAsync(new Interop.McpServerSettings { Enabled = true, Port = port, Mode = ChatMode.Plan });
        string token = await serverStore.TokenAsync();
        await using var server = new Interop.McpEndpoint(context, serverStore);
        await server.ReloadAsync();

        string call = "data: {\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"tool_calls\":[{\"index\":0,\"id\":\"call-1\",\"type\":\"function\",\"function\":{\"name\":\"vela_list_sessions\",\"arguments\":\"{}\"}}]},\"finish_reason\":null}]}\n\n"
                      + "data: {\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n"
                      + "data: [DONE]\n\n";
        // 第一个聊天的首个请求挂住;放行后回工具调用,第二个请求(带着工具结果)收尾
        await using var first = new SseStub(call, holdFirstStream: true, streamingReply: _ => Answer("first done"));
        await using var second = new SseStub("", streamingReply: n => n == 1 ? call : Answer("second done"));
        AiProvider Provider(string name, SseStub stub) => new()
        {
            Name = name,
            BaseUrl = stub.BaseUrl,
            DefaultProtocol = ChatProtocol.OpenAiChatCompletions,
            Models = [new AiModelConfig { Model = "m" }]
        };
        AiProvider a = Provider("a", first), b = Provider("b", second);
        await new AiSettingsStore(context).SaveAsync(new AiSettings
        {
            Providers = [a, b],
            ActiveModelId = a.Models[0].Id,
            McpServers =
            [
                new McpServerConfig
                {
                    Name = "vela", Transport = McpTransportType.Http,
                    Url = $"http://127.0.0.1:{port}/mcp", Headers = $"Authorization: Bearer {token}"
                }
            ]
        });

        BridgeAgentRunner runner = Create(context);
        List<ApprovalRequest> askedFirst = [], askedSecond = [];
        Func<ApprovalRequest, Task<bool>> Approve(List<ApprovalRequest> sink) => request =>
        {
            lock (sink) sink.Add(request);
            return Task.FromResult(true);
        };
        Task<BridgeTurn> firstTurn = runner.RunAsync(new BridgeConversation("ch1", "chat-1"),
            new BridgeSettings { Mode = ChatMode.Agent, Approval = ApprovalMode.Ask, ModelId = a.Models[0].Id },
            Message(), Approve(askedFirst), English, null, null, CancellationToken.None);
        // 第一个聊天已经拿到工具、发出了模型请求 —— 此刻第二个聊天开轮次并跑完
        await first.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(30));
        BridgeTurn secondTurn = await runner.RunAsync(new BridgeConversation("ch1", "chat-2"),
                new BridgeSettings { Mode = ChatMode.Agent, Approval = secondApproval, ModelId = b.Models[0].Id },
                new InboundMessage("ch1", "chat-2", false, "u2", "Bob", "hi", "m2", true),
                Approve(askedSecond), English, null, null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(60));
        first.Release();
        BridgeTurn firstResult = await firstTurn.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.AreEqual("first done", firstResult.Text);
        Assert.AreEqual("second done", secondTurn.Text);
        Assert.HasCount(1, askedFirst, "第一个聊天的 MCP 调用要在第一个聊天自己这里问一次");
        Assert.StartsWith("mcp:vela", askedFirst[0].Kind);
        Assert.HasCount(secondApproval == ApprovalMode.Ask ? 1 : 0, askedSecond,
            "第二个聊天只为自己的那次调用被问(全部自动时一次都不问)");

        static string Answer(string text)
            => "data: {\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"content\":"
               + System.Text.Json.JsonSerializer.Serialize(text) + "},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
    }

    /// <summary>
    /// #651 复审①②(维护者 2026-10-25 决定):被截断的回答照常送进群里、附一句提示,而不是整轮报错;
    /// 工具之后补了「请继续」模型还是空的,回一句说明而不是报失败。
    /// </summary>
    [TestMethod]
    [DataRow("truncated")]
    [DataRow("empty-after-tools")]
    public async Task TruncatedOrEmptyFinal_IsDeliveredWithANoticeInsteadOfAFailure(string shape)
    {
        string call = "data: {\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"tool_calls\":[{\"index\":0,\"id\":\"call-1\",\"type\":\"function\",\"function\":{\"name\":\"list_sessions\",\"arguments\":\"{}\"}}]},\"finish_reason\":null}]}\n\n"
                      + "data: {\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n"
                      + "data: [DONE]\n\n";
        string truncated = "data: {\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"half an answer\"},\"finish_reason\":\"length\"}]}\n\ndata: [DONE]\n\n";
        await using var endpoint = new SseStub("", streamingReply: n => shape == "truncated" ? truncated : n == 1 ? call : "data: [DONE]\n\n");
        using var context = new TestPluginContext();
        var provider = new AiProvider
        {
            Name = "stub",
            BaseUrl = endpoint.BaseUrl,
            DefaultProtocol = ChatProtocol.OpenAiChatCompletions,
            Models = [new AiModelConfig { Model = "m" }]
        };
        await new AiSettingsStore(context).SaveAsync(new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id });

        BridgeTurn turn = await Create(context).RunAsync(new BridgeConversation("ch1", "chat-1"),
            new BridgeSettings { Mode = shape == "truncated" ? ChatMode.Chat : ChatMode.Agent, Approval = ApprovalMode.Bypass },
            Message(), Deny, English, null, null, CancellationToken.None);

        if (shape == "truncated")
        {
            Assert.StartsWith("half an answer", turn.Text);
            Assert.Contains(English["NoticeOutputLimit"], turn.Text);
            Assert.HasCount(1, endpoint.Requests);
        }
        else
        {
            Assert.AreEqual(English["NoticeEmptyAfterTools"], turn.Text);
            Assert.HasCount(3, endpoint.Requests, "工具一次、空答一次、补一句「请继续」再一次");
            Assert.Contains(RecoveringChatClient.ContinueAfterTools, endpoint.Requests[2]);
        }
    }

    /// <summary>
    /// 桥接指定的模型被删掉后拒绝并提示,不静默落到全局第一个模型(#616–#636 复审③,维护者 2026-10-25 决定):
    /// 那可能是另一家供应商、另一份计费。没指定(跟随面板)的照旧按面板的选择走。
    /// </summary>
    [TestMethod]
    [DataRow(true, DisplayName = "指定的模型被删")]
    [DataRow(false, DisplayName = "跟随面板")]
    public async Task PinnedModelRemoved_RefusesInsteadOfFallingBackToAnotherProvider(bool pinned)
    {
        await using var other = new SseStub("data: {\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"from the other provider\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
        using var context = new TestPluginContext();
        var provider = new AiProvider
        {
            Name = "other",
            BaseUrl = other.BaseUrl,
            DefaultProtocol = ChatProtocol.OpenAiChatCompletions,
            Models = [new AiModelConfig { Model = "m" }]
        };
        await new AiSettingsStore(context).SaveAsync(new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id });

        BridgeTurn turn = await Create(context).RunAsync(new BridgeConversation("ch1", "chat-1"),
            new BridgeSettings { Mode = ChatMode.Chat, ModelId = pinned ? "deleted-model-id" : null },
            Message(), Deny, English, null, null, CancellationToken.None);

        if (pinned)
        {
            Assert.AreEqual(English["BridgeModelMissing"], turn.Text);
            Assert.IsEmpty(other.Requests, "一个请求都不能发到别家");
        }
        else
        {
            Assert.AreEqual("from the other provider", turn.Text);
        }
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
