using System.Text.Json;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Bridge;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 长任务与团队模式的第二轮完善:检查点随会话删除 / 清空 / 截断一并清理且不再补写、
/// 续跑交代未落定的后台命令、成员按需执行命令、团队开跑前的计划确认、IM 的团队进度。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class LongRunFollowUpTests
{
    private string _directory = "";

    [TestInitialize]
    public void Setup() => _directory = Directory.CreateTempSubdirectory("vela-longrun-").FullName;

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private static RunCheckpoint Checkpoint(string conversation, DateTimeOffset startedAt, params ChatMessage[] messages)
        => new(conversation, "run", "task", startedAt, DateTimeOffset.UtcNow, "running",
            RunCheckpointStore.Capture(messages.Length > 0 ? messages
                : [new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "list_sessions")])]));

    [TestMethod]
    public void ForgottenConversation_IsDeletedAndNeverWrittenAgain()
    {
        var store = new RunCheckpointStore(_directory);
        store.Save(Checkpoint("conv", DateTimeOffset.UtcNow));

        store.Forget("conv");
        // 删的正是在跑的那段:停下这一轮的收尾还会补写一份
        store.Save(Checkpoint("conv", DateTimeOffset.UtcNow));

        Assert.IsNull(store.Load("conv"));
    }

    [TestMethod]
    public void DeleteAll_RemovesEverythingAndDropsWritesFromRunsStartedBefore()
    {
        var store = new RunCheckpointStore(_directory);
        DateTimeOffset before = DateTimeOffset.UtcNow.AddSeconds(-5);
        store.Save(Checkpoint("a", before));
        store.Save(Checkpoint("b", before));

        store.DeleteAll();
        store.Save(Checkpoint("a", before)); // 清空时还在跑的那一轮,之后的写入作废
        store.Save(Checkpoint("c", DateTimeOffset.UtcNow.AddSeconds(5))); // 清空之后才开跑的照常

        Assert.IsNull(store.Load("a"));
        Assert.IsNull(store.Load("b"));
        Assert.IsNotNull(store.Load("c"));
    }

    [TestMethod]
    public void Writer_DiscardWinsOverAPendingTimedWrite()
    {
        var store = new RunCheckpointStore(_directory);
        var execution = new AgentExecution("run");
        execution.CompleteStep(new ChatResponse([new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "list_sessions")])]));
        using var writer = new RunCheckpointWriter(store, execution, "conv", "run", "task", "model", () => "");
        execution.RecordNotification(new ChatMessage(ChatRole.User, "progress")); // 排一次延迟写

        writer.Discard();
        Thread.Sleep(2000); // 延迟写本该在 1.5 秒后触发

        Assert.IsNull(store.Load("conv"), "正常收尾删掉之后,排着的定时写入不能把文件写回来");
    }

    [TestMethod]
    public void PendingBackgroundCommands_ListsCommandsWithoutAFinalState()
    {
        ChatMessage[] messages =
        [
            new(ChatRole.Assistant, [
                new FunctionCallContent("c1", "run_command", new Dictionary<string, object?> { ["command"] = "apt-get upgrade -y", ["sessionId"] = "web-01" }),
                new FunctionCallContent("c2", "run_command", new Dictionary<string, object?> { ["command"] = "tar czf /tmp/logs.tgz /var/log", ["sessionId"] = "web-01" }),
                new FunctionCallContent("c3", "run_command", new Dictionary<string, object?> { ["command"] = "du -sh /", ["sessionId"] = "web-01" })
            ]),
            new(ChatRole.Tool, [
                new FunctionResultContent("c1", "{\"taskId\":\"t-1\",\"sessionId\":\"web-01\",\"state\":\"Running\"}"),
                new FunctionResultContent("c2", "{\"taskId\":\"t-2\",\"sessionId\":\"web-01\",\"state\":\"Running\"}"),
                new FunctionResultContent("c3", "{\"taskId\":\"t-3\",\"sessionId\":\"web-01\",\"state\":\"Succeeded\",\"exitCode\":0}")
            ]),
            new(ChatRole.Assistant, [new FunctionCallContent("c4", "task_wait", new Dictionary<string, object?> { ["taskId"] = "t-2" })]),
            new(ChatRole.Tool, [new FunctionResultContent("c4", "{\"taskId\":\"t-2\",\"sessionId\":\"web-01\",\"state\":\"Succeeded\"}")])
        ];
        RunCheckpoint checkpoint = Checkpoint("conv", DateTimeOffset.UtcNow, messages);

        IReadOnlyList<string> pending = RunCheckpointStore.PendingBackgroundCommands(checkpoint);

        Assert.HasCount(1, pending);
        StringAssert.Contains(pending[0], "apt-get upgrade -y");
        StringAssert.Contains(pending[0], "t-1");
        StringAssert.Contains(RunCheckpointStore.BuildResumePrompt(checkpoint, ""), "apt-get upgrade -y",
            "续跑说明里列出未落定的后台命令,让模型先核实而不是原样再起");
    }

    [TestMethod]
    public void SubagentTools_IncludeCommandsOnlyWhenExplicitlyAllowed()
    {
        using var context = new TestPluginContext();
        var toolbox = new AgentToolbox(context);
        CommandJobRunScope run = toolbox.BeginRun("o", "r");
        try
        {
            string[] readOnly = [.. toolbox.CreateSubagentTools(false, run).OfType<AIFunction>().Select(f => f.Name)];
            string[] commands = [.. toolbox.CreateSubagentTools(false, run, allowCommands: true).OfType<AIFunction>().Select(f => f.Name)];

            Assert.DoesNotContain("run_command", readOnly);
            Assert.Contains("run_command", commands);
            Assert.Contains("task_wait", commands);
            Assert.DoesNotContain("run_on_sessions", commands, "多机并行仍不给");
            Assert.DoesNotContain("write_terminal", commands, "终端输入仍不给");
        }
        finally
        {
            run.DisposeAsync().AsTask().GetAwaiter().GetResult();
            toolbox.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static AIFunctionArguments TeamArguments() => new()
    {
        ["goal"] = "fix the proxy",
        ["members"] = JsonDocument.Parse("""[{"name":"scout","role":"researcher"},{"name":"fixer","role":"engineer","allowCommands":true}]""").RootElement,
        ["tasks"] = JsonDocument.Parse("""[{"id":"t1","title":"look","detail":"look at it","assignee":"scout"},{"id":"t2","title":"fix","detail":"fix it","assignee":"fixer","dependsOn":["t1"]}]""").RootElement
    };

    /// <summary>
    /// 主模型给成员挑模型要有依据:候选清单除了 id 与名字,还带配置里已知的事实(窗口、输出上限、单价、会不会思考、
    /// 哪个是主控自己用的);没有的不写 —— 单价没填不写价格,窗口只是默认估值也不写。图片模型与别的供应商实例不在其中。
    /// </summary>
    [TestMethod]
    public void ModelCatalog_ListsKnownFactsForEachCandidate()
    {
        var parent = new AiModelConfig
        {
            Model = "big",
            Name = "Big",
            MaxInputTokens = 400_000,
            DefaultContextWindow = false,
            MaxTokens = 32_000,
            InputPricePerMillion = 5,
            OutputPricePerMillion = 25,
            SupportsReasoning = true
        };
        var cheap = new AiModelConfig
        {
            Model = "small",
            Name = "Small",
            MaxInputTokens = 128_000,
            DefaultContextWindow = true,
            MaxTokens = 8_192,
            InputPricePerMillion = 0.5,
            OutputPricePerMillion = 2,
            SupportsReasoning = false
        };
        var unknown = new AiModelConfig { Model = "mystery", MaxInputTokens = 0 };
        var image = new AiModelConfig { Model = "gpt-image-test" };
        var provider = new AiProvider { Name = "acme", BaseUrl = "http://127.0.0.1:1", Models = [parent, cheap, unknown, image] };
        var other = new AiProvider { Name = "elsewhere", BaseUrl = "http://127.0.0.1:2", Models = [new AiModelConfig { Model = "foreign" }] };
        var settings = new AiSettings { Providers = [provider, other], ActiveModelId = parent.Id };

        JsonElement[] listed = [.. JsonDocument.Parse(SubagentRunner.ModelCatalog(settings, provider.Id, parent.Id)).RootElement.EnumerateArray()];

        CollectionAssert.AreEquivalent((string[])[parent.Id, cheap.Id, unknown.Id], listed.Select(m => m.GetProperty("id").GetString()).ToArray(),
            "候选与 ModelChoices 一致:当前供应商实例的聊天模型");
        CollectionAssert.AreEquivalent(SubagentRunner.ModelChoices(settings, provider.Id).Keys.ToArray(),
            listed.Select(m => m.GetProperty("id").GetString()).ToArray());
        JsonElement big = listed.Single(m => m.GetProperty("id").GetString() == parent.Id);
        Assert.IsTrue(big.GetProperty("parent").GetBoolean(), "标出主控自己在用的那个");
        Assert.AreEqual("acme / Big (big)", big.GetProperty("name").GetString());
        Assert.AreEqual(400_000, big.GetProperty("contextTokens").GetInt32());
        Assert.AreEqual(32_000, big.GetProperty("maxOutputTokens").GetInt32());
        Assert.AreEqual(5, big.GetProperty("inputPricePerMTok").GetDouble());
        Assert.AreEqual(25, big.GetProperty("outputPricePerMTok").GetDouble());
        Assert.IsTrue(big.GetProperty("reasoning").GetBoolean());

        JsonElement small = listed.Single(m => m.GetProperty("id").GetString() == cheap.Id);
        Assert.IsFalse(small.TryGetProperty("parent", out _));
        Assert.IsFalse(small.TryGetProperty("contextTokens", out _), "取不到规格时的默认估值不拿出来比大小");
        Assert.AreEqual(0.5, small.GetProperty("inputPricePerMTok").GetDouble());
        Assert.IsFalse(small.GetProperty("reasoning").GetBoolean());

        JsonElement bare = listed.Single(m => m.GetProperty("id").GetString() == unknown.Id);
        Assert.IsFalse(bare.TryGetProperty("inputPricePerMTok", out _), "没填单价就不写价格");
        Assert.IsFalse(bare.TryGetProperty("contextTokens", out _));
        Assert.IsFalse(bare.TryGetProperty("reasoning", out _), "不知道会不会思考就不写");
    }

    /// <summary>
    /// 清单与「按任务挑模型」的说明真的到了主模型手里:两件委派工具的说明里带着候选的事实;
    /// 系统提示词要求按这些事实给每名成员定模型(便宜的做例行收集、大窗口的读大量内容、会思考 / 更贵的做设计与要紧改动、
    /// 审查者尽量换一个模型),并且只按事实、不按名字。没给清单的入口退回只列 id 与名字。
    /// </summary>
    [TestMethod]
    public async Task ModelFactsAndAssignmentGuidance_ReachTheLead()
    {
        using var context = new TestPluginContext();
        await using var orchestrator = new SubagentOrchestrator((_, _) => Task.FromResult(SubagentExecutionResult.FromText("ok")), CancellationToken.None)
        {
            ModelChoices = new Dictionary<string, string> { ["m-big"] = "acme / Big (big)", ["m-small"] = "acme / Small (small)" },
            ModelCatalog = """[{"id":"m-big","parent":true,"contextTokens":400000},{"id":"m-small","inputPricePerMTok":0.5}]""",
            DefaultModelId = "m-big"
        };
        await using var toolbox = new AgentToolbox(context) { Subagents = orchestrator };
        AIFunction[] tools = [.. toolbox.CreateTools(ChatMode.Agent).OfType<AIFunction>()];

        foreach (string name in (string[])["team_run", "delegate_subagent"])
        {
            string description = tools.Single(t => t.Name == name).Description;
            StringAssert.Contains(description, "\"contextTokens\":400000", $"{name} 的说明里带候选的事实");
            StringAssert.Contains(description, "\"inputPricePerMTok\":0.5");
            StringAssert.Contains(description, "never by a model's name");
        }
        StringAssert.Contains(tools.Single(t => t.Name == "team_run").Description, "different model for a reviewer");

        orchestrator.ModelCatalog = null;
        string plain = toolbox.CreateTools(ChatMode.Agent).OfType<AIFunction>().Single(t => t.Name == "team_run").Description;
        StringAssert.Contains(plain, "acme / Small (small)", "没给清单时退回只列 id 与名字");

        StringAssert.Contains(SubagentRunner.TeamGuidance, "Give every member the model that fits its task");
        StringAssert.Contains(SubagentRunner.TeamGuidance, "never a model's name");
        StringAssert.Contains(SubagentRunner.TeamGuidance, "so the check is independent");
        StringAssert.Contains(SubagentRunner.DelegationGuidance, "match the model to the subtask using the facts");
        Assert.DoesNotContain("otherwise inherit the parent when no supported reason", SubagentRunner.DelegationGuidance,
            "以前这句让主模型一律沿用自己的模型");
    }

    /// <summary>
    /// 主模型给成员各指定了模型:确认卡上写的是模型名字(不是配置 id),每名成员的任务真的跑在指定的模型上,
    /// 没指定的沿用主控的;看板与各次运行都记着实际用的模型。
    /// </summary>
    [TestMethod]
    public async Task TeamMembers_RunOnTheModelsTheLeadAssigned()
    {
        using var context = new TestPluginContext();
        ApprovalRequest? asked = null;
        var requests = new List<SubagentRequest>();
        await using var orchestrator = new SubagentOrchestrator((request, _) =>
        {
            lock (requests) requests.Add(request);
            return Task.FromResult(SubagentExecutionResult.FromText("ok"));
        }, CancellationToken.None)
        {
            ModelChoices = new Dictionary<string, string> { ["m-big"] = "acme / Big (big)", ["m-small"] = "acme / Small (small)" },
            DefaultModelId = "m-big"
        };
        await using var toolbox = new AgentToolbox(context)
        {
            Subagents = orchestrator,
            Approval = ApprovalMode.Ask,
            ApprovalHandler = request => { asked = request; return Task.FromResult(true); }
        };
        await using CommandJobRunScope run = toolbox.BeginRun("o", "r");
        AIFunction teamRun = toolbox.CreateTools(ChatMode.Agent, runScope: run).OfType<AIFunction>().Single(f => f.Name == "team_run");

        await teamRun.InvokeAsync(new AIFunctionArguments
        {
            ["goal"] = "fix the proxy",
            ["members"] = JsonDocument.Parse("""[{"name":"scout","role":"researcher","modelId":"m-small"},{"name":"fixer","role":"engineer"}]""").RootElement,
            ["tasks"] = JsonDocument.Parse("""[{"id":"t1","title":"look","detail":"look at it","assignee":"scout"},{"id":"t2","title":"fix","detail":"fix it","assignee":"fixer","dependsOn":["t1"]}]""").RootElement
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (orchestrator.HasPendingResults && (await orchestrator.WaitForCompletionAsync(timeout.Token)).Count == 0) { }

        StringAssert.Contains(asked!.Value.Detail, "scout (researcher, acme / Small (small))", "确认卡上写模型名字,用户看得出谁用哪个模型");
        StringAssert.Contains(asked.Value.Detail, "fixer (engineer)", "没指定的不写(沿用主控的)");
        Assert.AreEqual("m-small", requests.Single(r => r.Prompt.Contains("## Your task #t1", StringComparison.Ordinal)).ModelId);
        TeamBoardSnapshot board = orchestrator.Team!.Snapshot()!;
        Assert.AreEqual("m-small", board.Members.Single(m => m.Name == "scout").ModelId);
        Assert.AreEqual("m-big", board.Members.Single(m => m.Name == "fixer").ModelId, "看板上写实际跑的那个,而不是空着");
        var ranOn = orchestrator.Snapshots().ToDictionary(s => s.TeamTaskId!, s => s.ModelId);
        Assert.AreEqual("m-small", ranOn["t1"]);
        Assert.AreEqual("m-big", ranOn["t2"]);
    }

    /// <summary>团队开跑前按挡位确认计划:拒绝只退回给主模型,不开团队,也不把整轮标为「执行被拒」。</summary>
    [TestMethod]
    public async Task TeamPlan_DeclinedDoesNotStartTheTeamOrStopTheTurn()
    {
        using var context = new TestPluginContext();
        ApprovalRequest? asked = null;
        await using var orchestrator = new SubagentOrchestrator((_, _) => Task.FromResult(SubagentExecutionResult.FromText("ok")), CancellationToken.None);
        await using var toolbox = new AgentToolbox(context)
        {
            Subagents = orchestrator,
            Approval = ApprovalMode.Ask,
            ApprovalHandler = request => { asked = request; return Task.FromResult(false); }
        };
        await using CommandJobRunScope run = toolbox.BeginRun("o", "r");
        AIFunction teamRun = toolbox.CreateTools(ChatMode.Agent, runScope: run).OfType<AIFunction>().Single(f => f.Name == "team_run");

        string result = (await teamRun.InvokeAsync(TeamArguments()))?.ToString() ?? "";

        Assert.IsNotNull(asked);
        Assert.AreEqual("team_run", asked.Value.Kind);
        StringAssert.Contains(asked.Value.Detail, "fixer (engineer, >_)", "确认卡上写明谁能执行命令");
        StringAssert.Contains(asked.Value.Detail, "t2 [fixer] fix  ← t1");
        StringAssert.Contains(result, "declined");
        Assert.IsNull(orchestrator.Team?.Snapshot(), "没有开跑");
        Assert.IsFalse(run.HasDeniedExecution, "拒绝的是这个计划,不是整件事:本轮还能继续");
    }

    [TestMethod]
    public async Task TeamPlan_ApprovedStartsTheTeamWithCommandPermissions()
    {
        using var context = new TestPluginContext();
        var requests = new List<SubagentRequest>();
        await using var orchestrator = new SubagentOrchestrator((request, _) =>
        {
            lock (requests) requests.Add(request);
            return Task.FromResult(SubagentExecutionResult.FromText("ok"));
        }, CancellationToken.None);
        await using var toolbox = new AgentToolbox(context)
        {
            Subagents = orchestrator,
            Approval = ApprovalMode.Ask,
            ApprovalHandler = _ => Task.FromResult(true)
        };
        await using CommandJobRunScope run = toolbox.BeginRun("o", "r");
        AIFunction teamRun = toolbox.CreateTools(ChatMode.Agent, runScope: run).OfType<AIFunction>().Single(f => f.Name == "team_run");

        string result = (await teamRun.InvokeAsync(TeamArguments()))?.ToString() ?? "";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (orchestrator.HasPendingResults && (await orchestrator.WaitForCompletionAsync(timeout.Token)).Count == 0) { }

        StringAssert.Contains(result, "started");
        Assert.IsFalse(requests.Single(r => r.Prompt.Contains("## Your task #t1", StringComparison.Ordinal)).AllowCommands);
        Assert.IsTrue(requests.Single(r => r.Prompt.Contains("## Your task #t2", StringComparison.Ordinal)).AllowCommands);
    }

    [TestMethod]
    public void BridgeTeamLine_ShowsPhaseProgressAndBusyMembers()
    {
        var loc = new Loc("zh-Hans");
        var team = new TeamBoardSnapshot("team-1", "goal", "running",
            [new("scout", "researcher", null, false, 0), new("fixer", "engineer", null, true, 1)],
            [
                new("t1", "梳理 nginx 配置", "scout", "completed", [], null, 1, null, "a", "ok", "", TimeSpan.FromSeconds(3)),
                new("t2", "修正 upstream 端口并 reload nginx 服务", "fixer", "running", ["t1"], null, 1, null, "b", "", "", TimeSpan.FromSeconds(1))
            ], [], TimeSpan.FromSeconds(4));

        string line = BridgeAgentRunner.TeamProgressLine(team, loc);

        StringAssert.Contains(line, "团队");
        StringAssert.Contains(line, "执行中");
        StringAssert.Contains(line, "1/2");
        StringAssert.Contains(line, "fixer: 修正 upstream 端口并 relo…", "标题截到 20 个字符");
        Assert.DoesNotContain("scout:", line, "只列在跑的成员");
    }
}
