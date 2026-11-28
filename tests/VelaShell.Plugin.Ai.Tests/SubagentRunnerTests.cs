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

    [TestMethod]
    public async Task ModelSelectionSpansAllProvidersWithTheParentProviderFirst()
    {
        using var context = new TestPluginContext();
        ResolvedModel first = Model();
        ResolvedModel parent = Model();
        var child = new AiModelConfig { Model = "child" };
        var image = new AiModelConfig { Model = "gpt-image-test" };
        parent.Provider.Models.AddRange([child, image]);
        var settings = new AiSettings { Providers = [first.Provider, parent.Provider], ActiveModelId = parent.Id };
        var store = new AiSettingsStore(context);
        await store.SaveAsync(settings);
        Dictionary<string, string> choices = SubagentRunner.ModelChoices(settings, parent.Provider.Id);
        // 所有供应商的聊天模型都可选,主控那一家排在最前(也是替补顺序);图片模型不在内
        Assert.AreSequenceEqual([parent.Id, child.Id, first.Id], [.. choices.Keys]);
        (ResolvedModel selected, _) = await SubagentRunner.ResolveFromSettingsAsync(store, child.Id, parent.Id, CancellationToken.None);
        Assert.AreEqual(child.Id, selected.Id);
        (ResolvedModel foreign, _) = await SubagentRunner.ResolveFromSettingsAsync(store, first.Id, parent.Id, CancellationToken.None);
        Assert.AreEqual(first.Provider.Id, foreign.Provider.Id, "别家供应商的模型按它自己的配置解析");
        await Assert.ThrowsAsync<InvalidOperationException>(() => SubagentRunner.ResolveFromSettingsAsync(
            store, image.Id, parent.Id, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => SubagentRunner.ResolveFromSettingsAsync(
            store, "deleted", parent.Id, CancellationToken.None));
    }

    /// <summary>编排器工厂(ai-plan §12.2 的 1d):子任务由这个执行器跑、结果是否自动送回可配,候选三项一起配上。</summary>
    [TestMethod]
    public async Task CreateOrchestrator_RunsOnThisRunner_AndUseCandidatesSetsAllThree()
    {
        using var context = new TestPluginContext();
        ResolvedModel first = Model();
        ResolvedModel parent = Model();
        var settings = new AiSettings { Providers = [first.Provider, parent.Provider] };
        var client = new Scripted([Text("factory report"), End(ChatFinishReason.Stop)]);
        var toolbox = new AgentToolbox(context);
        CommandJobRunScope run = toolbox.BeginRun("test", "turn");
        AgentToolbox template = toolbox.CreateSubagentToolbox();
        var runner = new SubagentRunner(
            (request, _) => Task.FromResult((settings.FindModel(request.ModelId!)!, default(ProviderCredential))),
            (_, _) => client, () => template, run, "en");
        SubagentOrchestrator orchestrator = runner.CreateOrchestrator(CancellationToken.None, deliversResults: false);
        await using (toolbox)
        await using (run)
        await using (template)
        await using (orchestrator)
        {
            Assert.IsFalse(orchestrator.DeliversResults);
            SubagentRunner.UseCandidates(orchestrator, settings, parent.Provider.Id, parent.Id);
            Assert.AreEqual(parent.Id, orchestrator.DefaultModelId);
            Assert.AreSequenceEqual([parent.Id, first.Id], [.. orchestrator.ModelChoices.Keys], "主控那一家在前");
            StringAssert.Contains(orchestrator.ModelCatalog, "\"parent\":true");

            orchestrator.Start("task", "r");
            using var result = JsonDocument.Parse(await orchestrator.WaitAsync(orchestrator.Snapshots()[0].SubagentId, 30, CancellationToken.None));
            Assert.AreEqual("factory report", result.RootElement.GetProperty("output").GetString(), "子任务由这个执行器跑");
            Assert.AreEqual(parent.Id, result.RootElement.GetProperty("modelId").GetString());
        }
    }

    [TestMethod]
    public void CannotUseModel_CoversModelAndCredentialFailuresOnly()
    {
        Assert.IsTrue(SubagentRunner.CannotUseModel(ModelNotFound("x")));
        Assert.IsTrue(SubagentRunner.CannotUseModel(new HttpRequestException("bad key", null, System.Net.HttpStatusCode.Unauthorized)));
        Assert.IsTrue(SubagentRunner.CannotUseModel(new HttpRequestException("forbidden", null, System.Net.HttpStatusCode.Forbidden)));
        Assert.IsTrue(SubagentRunner.CannotUseModel(new HttpRequestException("quota", null, System.Net.HttpStatusCode.TooManyRequests)));
        Assert.IsFalse(SubagentRunner.CannotUseModel(new HttpRequestException("bad input", null, System.Net.HttpStatusCode.BadRequest)));
        Assert.IsFalse(SubagentRunner.CannotUseModel(new HttpRequestException("down", null, System.Net.HttpStatusCode.InternalServerError)));
        Assert.IsFalse(SubagentRunner.CannotUseModel(new OperationCanceledException()));
    }

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

    /// <summary>Codex 用 ChatGPT 账号时真实回过的那句:这个型号在这个账号上用不了。</summary>
    private static HttpRequestException ModelNotFound(string model) => new(
        $"HTTP 400 (invalid_request_error: model_not_found) The '{model}' model is not supported when using Codex with a ChatGPT account.",
        null, System.Net.HttpStatusCode.BadRequest);

    /// <summary>每次请求都回「模型用不了」的假客户端。</summary>
    private sealed class Rejecting(string model) : IChatClient
    {
        public int Requests;

        /// <summary>第一次请求先正常调一个工具,之后才回「模型用不了」。</summary>
        public bool CallFirst { get; set; }

        /// <summary>改回别的错误(比如另一家的凭据被拒);为空时回「模型用不了」。</summary>
        public Exception? Error { get; set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            int request = Interlocked.Increment(ref Requests);
            await Task.CompletedTask;
            if (CallFirst && request == 1)
            {
                yield return Call("c1", "list_sessions");
                yield return End(ChatFinishReason.ToolCalls);
                yield break;
            }
            throw Error ?? ModelNotFound(model);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>同一供应商下三个模型:parent(父模型)、bad(这个账号用不了)、spare(备用)。</summary>
    private static async Task<(SubagentOrchestrator Orchestrator, Rejecting Bad, Scripted Good, AgentToolbox Toolbox, CommandJobRunScope Run, AgentToolbox Template)>
        FallbackSetupAsync(TestPluginContext context, string parentModel, params IReadOnlyList<ChatResponseUpdate>[] goodScript)
    {
        ResolvedModel parent = Model();
        parent.Provider.Models[0].Id = "parent";
        parent.Provider.Models[0].Model = parentModel;
        parent.Provider.Models.Add(new AiModelConfig { Id = "bad", Model = "gpt-5.4" });
        parent.Provider.Models.Add(new AiModelConfig { Id = "spare", Model = "gpt-5.2" });
        var settings = new AiSettings { Providers = [parent.Provider] };
        var bad = new Rejecting("gpt-5.4");
        var good = new Scripted(goodScript);
        var toolbox = new AgentToolbox(context);
        CommandJobRunScope run = toolbox.BeginRun("test", "turn");
        AgentToolbox template = toolbox.CreateSubagentToolbox();
        var runner = new SubagentRunner(
            (request, _) => Task.FromResult((settings.FindModel(request.ModelId!)!, default(ProviderCredential))),
            (model, _) => model.Model == "gpt-5.4" || model.Model == parentModel && parentModel == "gpt-5.4" ? bad : good,
            () => template, run, "en");
        SubagentOrchestrator? owner = null;
        owner = new SubagentOrchestrator((request, token) => runner.RunAsync(owner!, request, token), CancellationToken.None)
        {
            DefaultModelId = "parent",
            ModelChoices = new Dictionary<string, string> { ["parent"] = "parent", ["bad"] = "bad", ["spare"] = "spare" }
        };
        return (owner, bad, good, toolbox, run, template);
    }

    /// <summary>
    /// 主模型给子任务选的型号在这个账号上用不了:改用父模型跑完,卡片与结果写明换过;
    /// 同一轮再选它的子任务直接用父模型,不再撞一遍。
    /// </summary>
    [TestMethod]
    public async Task UnavailableModel_FallsBackToTheParentAndIsSkippedForTheRestOfTheTurn()
    {
        using var context = new TestPluginContext();
        (SubagentOrchestrator orchestrator, Rejecting bad, Scripted good, AgentToolbox toolbox, CommandJobRunScope run, AgentToolbox template) =
            await FallbackSetupAsync(context, "gpt-5.3", [Text("first report"), End(ChatFinishReason.Stop)], [Text("second report"), End(ChatFinishReason.Stop)]);
        await using (toolbox)
        await using (run)
        await using (template)
        await using (orchestrator)
        {
            orchestrator.Start("task one", "r", modelId: "bad");
            string first = orchestrator.Snapshots()[0].SubagentId;
            using var one = JsonDocument.Parse(await orchestrator.WaitAsync(first, 30, CancellationToken.None));
            orchestrator.Start("task two", "r", modelId: "bad");
            string second = orchestrator.Snapshots()[1].SubagentId;
            using var two = JsonDocument.Parse(await orchestrator.WaitAsync(second, 30, CancellationToken.None));

            Assert.AreEqual("completed", one.RootElement.GetProperty("status").GetString(), one.RootElement.GetRawText());
            Assert.AreEqual("first report", one.RootElement.GetProperty("output").GetString());
            Assert.AreEqual("parent", one.RootElement.GetProperty("modelId").GetString(), "卡片显示实际用的模型");
            Assert.Contains(e => e!.Contains("Model bad could not be used", StringComparison.Ordinal) && e.Contains("switched to model parent", StringComparison.Ordinal),
                one.RootElement.GetProperty("errors").EnumerateArray().Select(e => e.GetString()));
            Assert.AreEqual("completed", two.RootElement.GetProperty("status").GetString());
            Assert.AreEqual(1, bad.Requests, "同一轮第二次选到它,不再发请求去撞");
            Assert.HasCount(2, good.Requests);
        }
    }

    /// <summary>
    /// 分给另一家供应商的成员,那一家的 Key 被拒(401):还没输出、没调工具,就改用父模型跑完,结果写明换过。
    /// 跨供应商后「只配了地址、Key 早过期」很常见,不该让这个成员整个失败。
    /// </summary>
    [TestMethod]
    public async Task ForeignProviderCredentialFailure_FallsBackToTheParent()
    {
        using var context = new TestPluginContext();
        ResolvedModel parent = Model();
        parent.Provider.Models[0].Id = "parent";
        ResolvedModel foreign = Model();
        foreign.Provider.Name = "elsewhere";
        foreign.Provider.Models[0].Id = "foreign";
        foreign.Provider.Models[0].Model = "foreign-model";
        var settings = new AiSettings { Providers = [parent.Provider, foreign.Provider] };
        var rejected = new Rejecting("foreign-model")
        {
            Error = new HttpRequestException("HTTP 401 invalid api key", null, System.Net.HttpStatusCode.Unauthorized)
        };
        var good = new Scripted([Text("parent report"), End(ChatFinishReason.Stop)]);
        var toolbox = new AgentToolbox(context);
        CommandJobRunScope run = toolbox.BeginRun("test", "turn");
        AgentToolbox template = toolbox.CreateSubagentToolbox();
        var runner = new SubagentRunner(
            (request, _) => Task.FromResult((settings.FindModel(request.ModelId!)!, default(ProviderCredential))),
            (model, _) => model.Provider.Id == foreign.Provider.Id ? rejected : good,
            () => template, run, "en");
        SubagentOrchestrator? owner = null;
        owner = new SubagentOrchestrator((request, token) => runner.RunAsync(owner!, request, token), CancellationToken.None)
        {
            DefaultModelId = "parent",
            ModelChoices = SubagentRunner.ModelChoices(settings, parent.Provider.Id)
        };
        await using (toolbox)
        await using (run)
        await using (template)
        await using (owner)
        {
            Assert.IsTrue(owner.ModelChoices.ContainsKey("foreign"), "别家供应商的模型在候选里");
            owner.Start("task", "r", modelId: "foreign");
            string id = owner.Snapshots()[0].SubagentId;
            using var result = JsonDocument.Parse(await owner.WaitAsync(id, 30, CancellationToken.None));

            Assert.AreEqual("completed", result.RootElement.GetProperty("status").GetString(), result.RootElement.GetRawText());
            Assert.AreEqual("parent report", result.RootElement.GetProperty("output").GetString());
            Assert.AreEqual("parent", result.RootElement.GetProperty("modelId").GetString());
            Assert.Contains(e => e!.Contains("Model foreign could not be used", StringComparison.Ordinal),
                result.RootElement.GetProperty("errors").EnumerateArray().Select(e => e.GetString()));
            Assert.AreEqual(1, rejected.Requests);
        }
    }

    /// <summary>父模型自己用不了时,换到候选里的其它聊天模型(委派本来就允许选它)。</summary>
    [TestMethod]
    public async Task UnavailableParent_FallsBackToAnotherCandidate()
    {
        using var context = new TestPluginContext();
        (SubagentOrchestrator orchestrator, Rejecting bad, Scripted good, AgentToolbox toolbox, CommandJobRunScope run, AgentToolbox template) =
            await FallbackSetupAsync(context, "gpt-5.4", [Text("spare report"), End(ChatFinishReason.Stop)]);
        await using (toolbox)
        await using (run)
        await using (template)
        await using (orchestrator)
        {
            orchestrator.Start("task", "r");
            string id = orchestrator.Snapshots()[0].SubagentId;
            using var result = JsonDocument.Parse(await orchestrator.WaitAsync(id, 30, CancellationToken.None));

            Assert.AreEqual("completed", result.RootElement.GetProperty("status").GetString(), result.RootElement.GetRawText());
            Assert.AreEqual("spare", result.RootElement.GetProperty("modelId").GetString());
            Assert.AreEqual(2, bad.Requests, "父模型与 bad 都是 gpt-5.4,各试一次后换到 spare");
        }
    }

    /// <summary>已经调过工具之后才失败:不换模型重跑(副作用可能做两遍),照常报错。</summary>
    [TestMethod]
    public async Task UnavailableAfterToolCalls_IsNotRetriedOnAnotherModel()
    {
        using var context = new TestPluginContext();
        context.FakeSessions.AddConnected();
        (SubagentOrchestrator orchestrator, Rejecting bad, Scripted good, AgentToolbox toolbox, CommandJobRunScope run, AgentToolbox template) =
            await FallbackSetupAsync(context, "gpt-5.3", [Text("must not run"), End(ChatFinishReason.Stop)]);
        bad.CallFirst = true;
        await using (toolbox)
        await using (run)
        await using (template)
        await using (orchestrator)
        {
            orchestrator.Start("task", "r", modelId: "bad");
            string id = orchestrator.Snapshots()[0].SubagentId;
            using var result = JsonDocument.Parse(await orchestrator.WaitAsync(id, 30, CancellationToken.None));

            Assert.AreEqual("failed", result.RootElement.GetProperty("status").GetString(), result.RootElement.GetRawText());
            Assert.IsEmpty(good.Requests, "调过工具之后不换模型重跑");
        }
    }

    /// <summary>只认点名模型的 400 / 404;普通参数错、裸 404 不算。主模型那条路按 403 同样处理:换 Key / 换站、只冷这个模型。</summary>
    [TestMethod]
    public void ModelUnavailableDetection_OnlyMatchesModelSpecificErrors()
    {
        Assert.IsTrue(Ui.TransientFailure.IsModelUnavailable(ModelNotFound("gpt-5.4")));
        Assert.IsTrue(Ui.TransientFailure.IsModelUnavailable(new InvalidOperationException("wrapped", ModelNotFound("x"))));
        Assert.IsTrue(Ui.TransientFailure.IsModelUnavailable(new HttpRequestException(
            "not_found_error: model: claude-x does not exist", null, System.Net.HttpStatusCode.NotFound)));
        Assert.IsFalse(Ui.TransientFailure.IsModelUnavailable(new HttpRequestException(
            "Not Found", null, System.Net.HttpStatusCode.NotFound)), "裸 404 多半是 Base URL 填错,不是模型问题");
        Assert.IsFalse(Ui.TransientFailure.IsModelUnavailable(new HttpRequestException(
            "invalid_request_error: max_tokens is too large", null, System.Net.HttpStatusCode.BadRequest)), "普通参数错不算");
        Assert.IsFalse(Ui.TransientFailure.IsModelUnavailable(new HttpRequestException(
            "model_not_found", null, System.Net.HttpStatusCode.TooManyRequests)), "只认 400 / 404");
        Assert.IsTrue(Ui.TransientFailure.IsWorthSwitching(ModelNotFound("gpt-5.4")), "换一站可能就好");
        Assert.IsTrue(Ui.TransientFailure.IsApiKeyFailure(ModelNotFound("gpt-5.4")), "按 403 同样只冷这把 Key 用在这个模型上");
        Assert.IsFalse(Ui.TransientFailure.IsKeyRejected(ModelNotFound("gpt-5.4")), "不冷整把 Key");
        Assert.AreEqual(Ui.TransientFailure.SwitchModel, Ui.TransientFailure.SwitchReason(ModelNotFound("gpt-5.4")).Reason);
        Assert.IsFalse(Ui.TransientFailure.IsWorthSwitching(new HttpRequestException(
            "invalid_request_error: bad parameter", null, System.Net.HttpStatusCode.BadRequest)), "普通 400 照旧不换站");
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
