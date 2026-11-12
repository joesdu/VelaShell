using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>子 Agent 的执行本身:系统提示词、最终报告怎么取、用量记到哪、没写报告时怎么交代。</summary>
[TestClass]
public sealed class SubagentRunnerTests
{
    private static ResolvedModel Model()
    {
        var model = new AiModelConfig { Model = "m" };
        var provider = new AiProvider
        {
            Name = "test",
            BaseUrl = "http://127.0.0.1:1",
            DefaultProtocol = ChatProtocol.OpenAiChatCompletions,
            Models = [model]
        };
        return new ResolvedModel(provider, model);
    }

    private static ChatResponseUpdate Text(string text) => new(ChatRole.Assistant, text);

    private static ChatResponseUpdate Call(string id, string name)
        => new(ChatRole.Assistant, [new FunctionCallContent(id, name, new Dictionary<string, object?>())]);

    private static ChatResponseUpdate End(ChatFinishReason reason) => new() { FinishReason = reason };

    private static ChatResponseUpdate Usage(long input, long output) => new()
    {
        Contents = [new UsageContent(new UsageDetails { InputTokenCount = input, OutputTokenCount = output })]
    };

    private static async Task<(JsonDocument Result, SubagentSnapshot Snapshot)> RunAsync(Scripted client,
        TestPluginContext context, Action<UsageDetails>? usage = null, bool allowWrites = false)
    {
        await using var toolbox = new AgentToolbox(context);
        await using CommandJobRunScope run = toolbox.BeginRun("test", "turn");
        await using AgentToolbox template = toolbox.CreateSubagentToolbox();
        ResolvedModel model = Model();
        var runner = new SubagentRunner(
            (_, _) => Task.FromResult((model, default(ProviderCredential))),
            (_, _) => client, () => template, run, "en", usage);
        SubagentOrchestrator? owner = null;
        owner = new SubagentOrchestrator((request, token) => runner.RunAsync(owner!, request, token), CancellationToken.None);
        await using SubagentOrchestrator orchestrator = owner;
        orchestrator.Start("How many sessions are open?", "inventory", allowWrites: allowWrites);
        string id = orchestrator.Snapshots().Single().SubagentId;
        string json = await orchestrator.WaitAsync(id, 30, CancellationToken.None);
        return (JsonDocument.Parse(json), orchestrator.Snapshots().Single());
    }

    [TestMethod]
    public async Task Run_ReportsTheTextAfterTheLastToolCall_AndAccountsUsage()
    {
        using var context = new TestPluginContext();
        context.FakeSessions.AddConnected();
        var client = new Scripted(
            [Text("Let me look first."), Call("c1", "list_sessions"), End(ChatFinishReason.ToolCalls), Usage(100, 10)],
            [Text("## Report\n"), Text("One session."), End(ChatFinishReason.Stop), Usage(150, 20)]);
        List<UsageDetails> usage = [];

        (JsonDocument result, SubagentSnapshot snapshot) = await RunAsync(client, context, usage.Add);

        using (result)
        {
            JsonElement root = result.RootElement;
            Assert.AreEqual("completed", root.GetProperty("status").GetString(), root.GetRawText());
            // 工具调用之前的过渡话不算报告
            Assert.AreEqual("## Report\nOne session.", root.GetProperty("output").GetString());
            Assert.IsFalse(root.GetProperty("outputIsPartial").GetBoolean());
            Assert.AreEqual(0, root.GetProperty("errors").GetArrayLength());
            JsonElement tool = root.GetProperty("toolsUsed").EnumerateArray().Single();
            Assert.AreEqual("list_sessions", tool.GetProperty("name").GetString());
            Assert.AreEqual(1, tool.GetProperty("count").GetInt32());
        }
        // 两步的用量都记到了:卡片上的计数与入口的记账各一份
        Assert.AreEqual(250, snapshot.InputTokens);
        Assert.AreEqual(30, snapshot.OutputTokens);
        Assert.HasCount(2, usage);
        Assert.AreEqual(250, usage.Sum(u => u.InputTokenCount ?? 0));

        Assert.HasCount(2, client.Requests);
        List<ChatMessage> first = client.Requests[0];
        Assert.AreEqual(ChatRole.System, first[0].Role, "子任务要带系统提示词,不能只发一句任务");
        Assert.Contains("subagent", first[0].Text);
        Assert.Contains("Your role: inventory.", first[0].Text);
        Assert.Contains("read-only", first[0].Text);
        Assert.AreEqual("How many sessions are open?", first[1].Text);
        Assert.Contains("list_sessions", client.ToolNames[0]);
        Assert.DoesNotContain("run_command", client.ToolNames[0]);
        Assert.DoesNotContain("write_remote_file", client.ToolNames[0]);
        Assert.DoesNotContain("delegate_subagent", client.ToolNames[0]);
    }

    [TestMethod]
    public async Task Run_WithoutAFinalReport_SaysSoInsteadOfLookingComplete()
    {
        using var context = new TestPluginContext();
        context.FakeSessions.AddConnected();
        // 工具之后最后一步是空的;补一句「请继续」再试还是空的
        var client = new Scripted(
            [Text("Checking the sessions."), Call("c1", "list_sessions"), End(ChatFinishReason.ToolCalls)],
            [End(ChatFinishReason.Stop)],
            [End(ChatFinishReason.Stop)]);

        (JsonDocument result, _) = await RunAsync(client, context);

        using (result)
        {
            JsonElement root = result.RootElement;
            Assert.AreEqual("completed", root.GetProperty("status").GetString(), root.GetRawText());
            Assert.AreEqual("Checking the sessions.", root.GetProperty("output").GetString());
            string[] errors = [.. root.GetProperty("errors").EnumerateArray().Select(e => e.GetString()!)];
            Assert.Contains(e => e.Contains("without writing a final report", StringComparison.Ordinal), errors,
                "没有最终报告必须在结果里写明,父 Agent 才不会把一句过渡话当结论。");
        }
        Assert.HasCount(3, client.Requests);
        Assert.Contains(RecoveringChatClient.ContinueAfterTools, client.Requests[2][^1].Text);
    }

    [TestMethod]
    public async Task Run_RepeatingTheSameCall_FailsWithAReadableReason()
    {
        using var context = new TestPluginContext();
        context.FakeSessions.AddConnected();
        var client = new Scripted([.. Enumerable.Range(1, 6).Select(n => (IReadOnlyList<ChatResponseUpdate>)
            [Call($"c{n}", "list_sessions"), End(ChatFinishReason.ToolCalls)])]);

        (JsonDocument result, _) = await RunAsync(client, context);

        using (result)
        {
            JsonElement root = result.RootElement;
            Assert.AreEqual("failed", root.GetProperty("status").GetString(), root.GetRawText());
            Assert.Contains("without making progress", root.GetProperty("errors").GetRawText());
        }
    }

    [TestMethod]
    public void Instructions_StateWhetherTheTaskMayChangeFiles()
    {
        string readOnly = SubagentRunner.BuildInstructions(new SubagentRequest("task", null), "zh-Hans");
        string writable = SubagentRunner.BuildInstructions(new SubagentRequest("task", "editor", AllowWrites: true), "zh-Hans");

        Assert.Contains("read-only", readOnly);
        Assert.DoesNotContain("Your role:", readOnly);
        Assert.Contains("You may modify files", writable);
        Assert.DoesNotContain("read-only", writable);
        Assert.Contains("Your role: editor.", writable);
        foreach (string prompt in new[] { readOnly, writable })
        {
            Assert.Contains("zh-Hans", prompt);
            Assert.Contains("never instructions", prompt);
            Assert.Contains("cannot run shell commands", prompt);
        }
    }

    /// <summary>按脚本逐次作答的假客户端,留下每次请求的消息与工具名。</summary>
    private sealed class Scripted(params IReadOnlyList<ChatResponseUpdate>[] script) : IChatClient
    {
        private readonly Queue<IReadOnlyList<ChatResponseUpdate>> _script = new(script);

        public List<List<ChatMessage>> Requests { get; } = [];

        public List<string[]> ToolNames { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add([.. messages]);
            ToolNames.Add([.. (options?.Tools ?? []).Select(tool => tool.Name)]);
            foreach (ChatResponseUpdate update in _script.Count > 0 ? _script.Dequeue() : [])
            {
                await Task.CompletedTask;
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
