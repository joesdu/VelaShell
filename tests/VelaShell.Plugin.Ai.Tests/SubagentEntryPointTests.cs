using System.Text.Json;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Bridge;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Interop;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>聊天面板之外的两个入口(IM 桥接、对外 MCP)上的子 Agent。</summary>
[TestClass]
public sealed class SubagentEntryPointTests
{
    private static readonly Loc English = new("en");

    private static string Answer(string text)
        => "data: {\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"content\":"
           + JsonSerializer.Serialize(text) + "},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";

    private static string ToolCall(string name, object arguments)
        => "data: " + JsonSerializer.Serialize(new
        {
            id = "1",
            @object = "chat.completion.chunk",
            created = 1,
            model = "m",
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new
                    {
                        tool_calls = new[]
                        {
                            new { index = 0, id = "call-1", type = "function", function = new { name, arguments = JsonSerializer.Serialize(arguments) } }
                        }
                    },
                    finish_reason = "tool_calls"
                }
            }
        }) + "\n\ndata: [DONE]\n\n";

    private static AiProvider Provider(string name, string baseUrl) => new()
    {
        Name = name,
        BaseUrl = baseUrl,
        DefaultProtocol = ChatProtocol.OpenAiChatCompletions,
        Models = [new AiModelConfig { Model = name + "-model" }]
    };

    /// <summary>
    /// IM 那头的模型委派完就停下:这一轮要等子任务跑完、把报告送回去再收尾,群里看到的是最终结论;
    /// 等待期间进度消息末尾带一行「已结束 n/m」。
    /// </summary>
    [TestMethod]
    public async Task Bridge_DelegatesWaitsForTheReportAndShowsProgress()
    {
        await using var childEndpoint = new SseStub(Answer("child report: disk is fine"));
        AiProvider child = Provider("child", childEndpoint.BaseUrl);
        await using var parentEndpoint = new SseStub("", streamingReply: n => n switch
        {
            1 => ToolCall("delegate_subagent", new { prompt = "Check the disk.", role = "disk", modelId = child.Models[0].Id }),
            2 => Answer("Delegated."),
            _ => Answer(" Disk is fine.")
        });
        AiProvider parent = Provider("parent", parentEndpoint.BaseUrl);
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        await store.SaveAsync(new AiSettings { Providers = [parent, child], ActiveModelId = parent.Models[0].Id });
        var runner = new BridgeAgentRunner(context, store, new ChatHistoryStore(context), new McpManager(context));
        var progress = new System.Collections.Concurrent.ConcurrentQueue<string>();

        BridgeTurn turn = await runner.RunAsync(new BridgeConversation("ch1", "chat-1"),
                new BridgeSettings { Mode = ChatMode.Agent },
                new InboundMessage("ch1", "chat-1", false, "u1", "Ann", "Have a subagent check the disk.", "m1", true),
                _ => Task.FromResult(false), English, progress.Enqueue, null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(60));

        Assert.AreEqual("Delegated. Disk is fine.", turn.Text);
        Assert.HasCount(3, parentEndpoint.Requests, "委派 → 模型停下 → 收到子任务通知后收尾");
        Assert.Contains("Subagents are AI workers", parentEndpoint.Requests[0], "有委派工具时系统提示词要带委派引导");
        Assert.Contains("untrusted-subagent-results", parentEndpoint.Requests[2]);
        Assert.Contains("child report: disk is fine", parentEndpoint.Requests[2]);
        Assert.HasCount(1, childEndpoint.Requests);
        using (JsonDocument request = JsonDocument.Parse(childEndpoint.Requests[0]))
        {
            Assert.AreEqual("child-model", request.RootElement.GetProperty("model").GetString());
            string[] tools = [.. request.RootElement.GetProperty("tools").EnumerateArray()
                .Select(t => t.GetProperty("function").GetProperty("name").GetString()!)];
            Assert.Contains("read_remote_file", tools);
            Assert.DoesNotContain("run_command", tools);
            Assert.DoesNotContain("delegate_subagent", tools);
            Assert.DoesNotContain("send_file", tools);
        }
        Assert.IsTrue(progress.Any(text => text.Contains("0/1 finished", StringComparison.Ordinal)), string.Join(" | ", progress));
        Assert.IsTrue(progress.Any(text => text.Contains("1/1 finished", StringComparison.Ordinal)), string.Join(" | ", progress));
    }

    [TestMethod]
    [DataRow(ChatMode.Plan)]
    [DataRow(ChatMode.Chat)]
    public async Task Bridge_OffersNoDelegationOutsideAgentMode(ChatMode mode)
    {
        await using var endpoint = new SseStub(Answer("answer"));
        AiProvider provider = Provider("parent", endpoint.BaseUrl);
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        await store.SaveAsync(new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id });
        var runner = new BridgeAgentRunner(context, store, new ChatHistoryStore(context), new McpManager(context));

        BridgeTurn turn = await runner.RunAsync(new BridgeConversation("ch1", "chat-1"), new BridgeSettings { Mode = mode },
            new InboundMessage("ch1", "chat-1", false, "u1", "Ann", "hi", "m1", true),
            _ => Task.FromResult(false), English, null, null, CancellationToken.None);

        Assert.AreEqual("answer", turn.Text);
        Assert.DoesNotContain("delegate_subagent", endpoint.Requests.Single());
        Assert.DoesNotContain("Subagents are AI workers", endpoint.Requests.Single());
    }

    /// <summary>
    /// 对外 MCP:外部 agent 自己就是模型,这里唤不醒它 —— 委派的回执要明说结果得自己取,
    /// 取的时候用的是 VelaShell 里配置的模型。
    /// </summary>
    [TestMethod]
    public async Task ExternalMcp_DelegatesToTheConfiguredModelAndTheCallerCollectsTheResult()
    {
        await using var endpoint = new SseStub(Answer("report: two sessions connected"));
        AiProvider provider = Provider("vela", endpoint.BaseUrl);
        using var context = new TestPluginContext();
        context.FakeSessions.AddConnected();
        var store = new AiSettingsStore(context);
        await store.SaveAsync(new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id });
        await using McpToolHost host = await McpToolHost.CreateAsync(context,
            new McpServerSettings { Mode = ChatMode.Agent, Approval = ApprovalMode.ReadOnlyAuto }, store, CancellationToken.None);

        Assert.IsTrue(host.Tools.Any(t => t.Name == "delegate_subagent"));
        Assert.Contains("subagent_wait", host.Tools.Single(t => t.Name == "delegate_subagent").Description);
        Assert.DoesNotContain("delivered to you automatically", host.Tools.Single(t => t.Name == "delegate_subagent").Description);
        Assert.Contains("delegate_subagent", host.Instructions);

        string started = await host.CallAsync("delegate_subagent",
            new Dictionary<string, object?> { ["prompt"] = "List the sessions." }, CancellationToken.None);
        Assert.Contains("NOT pushed", started);
        string id = System.Text.RegularExpressions.Regex.Match(started, "sub-[a-f0-9]{8}").Value;
        string result = await host.CallAsync("subagent_wait",
            new Dictionary<string, object?> { ["id"] = id, ["timeoutSeconds"] = 30 }, CancellationToken.None);

        using JsonDocument json = JsonDocument.Parse(result);
        Assert.AreEqual("completed", json.RootElement.GetProperty("status").GetString(), result);
        Assert.AreEqual("report: two sessions connected", json.RootElement.GetProperty("output").GetString());
        Assert.AreEqual(provider.Models[0].Id, json.RootElement.GetProperty("modelId").GetString());
        Assert.IsFalse(host.HasActiveSubagents);
        using JsonDocument request = JsonDocument.Parse(endpoint.Requests.Single());
        string[] tools = [.. request.RootElement.GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("function").GetProperty("name").GetString()!)];
        Assert.Contains("list_sessions", tools);
        Assert.DoesNotContain("run_command", tools);
        Assert.DoesNotContain("write_remote_file", tools);
    }

    [TestMethod]
    public async Task ExternalMcp_RunningSubagentKeepsTheConnectionAlive_AndDiesWithIt()
    {
        await using var endpoint = new SseStub(Answer("late"), holdFirstStream: true);
        AiProvider provider = Provider("vela", endpoint.BaseUrl);
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        await store.SaveAsync(new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id });
        McpToolHost host = await McpToolHost.CreateAsync(context,
            new McpServerSettings { Mode = ChatMode.Agent }, store, CancellationToken.None);
        string started = await host.CallAsync("delegate_subagent",
            new Dictionary<string, object?> { ["prompt"] = "Wait." }, CancellationToken.None);
        string id = System.Text.RegularExpressions.Regex.Match(started, "sub-[a-f0-9]{8}").Value;

        Assert.IsTrue(host.HasActiveSubagents, "子任务在跑时这条连接不该被当成闲置回收");
        // 查询与取消不排在写操作锁后面
        string status = await host.CallAsync("subagent_status",
            new Dictionary<string, object?> { ["id"] = id }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("\"status\":\"running\"", status);

        await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.IsFalse(host.HasActiveSubagents, "连接关闭时子任务一并取消");
    }

    [TestMethod]
    public async Task ExternalMcp_WithoutAModelOrOutsideAgentMode_OffersNoDelegation()
    {
        await using var endpoint = new SseStub(Answer("unused"));
        AiProvider provider = Provider("vela", endpoint.BaseUrl);
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);

        await using (McpToolHost noModel = await McpToolHost.CreateAsync(context,
                         new McpServerSettings { Mode = ChatMode.Agent }, store, CancellationToken.None))
        {
            Assert.IsFalse(noModel.Tools.Any(t => t.Name == "delegate_subagent"), "一个聊天模型都没配时不摆委派工具");
        }

        await store.SaveAsync(new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id });
        await using (McpToolHost plan = await McpToolHost.CreateAsync(context,
                         new McpServerSettings { Mode = ChatMode.Plan }, store, CancellationToken.None))
        {
            Assert.IsFalse(plan.Tools.Any(t => t.Name.Contains("subagent", StringComparison.Ordinal)));
        }
        await using (McpToolHost disabled = await McpToolHost.CreateAsync(context,
                         new McpServerSettings { Mode = ChatMode.Agent, DisabledTools = "delegate_subagent" }, store, CancellationToken.None))
        {
            Assert.IsFalse(disabled.Tools.Any(t => t.Name == "delegate_subagent"), "用户在「配置工具」里停用后不再暴露");
        }
        await using (McpToolHost withoutStore = await McpToolHost.CreateAsync(context,
                         new McpServerSettings { Mode = ChatMode.Agent }, null, CancellationToken.None))
        {
            Assert.IsFalse(withoutStore.Tools.Any(t => t.Name == "delegate_subagent"));
        }
        Assert.IsEmpty(endpoint.Requests);
    }
}
