using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Chat;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 团队模式:引擎按依赖调度、上游报告自动交给下游、审查不过返工一次、失败让下游受阻、
/// 成员之间的消息投递,以及全部落定后只送回<b>一份</b>团队报告。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class TeamEngineTests
{
    /// <summary>假的子任务运行器:按任务提示词里的 "#id" 决定交回什么,并记下每次收到的请求。</summary>
    private sealed class FakeRunner(Func<SubagentRequest, int, string> reply)
    {
        public ConcurrentQueue<SubagentRequest> Requests { get; } = new();
        private readonly ConcurrentDictionary<string, int> _attempts = new();

        public Task<SubagentExecutionResult> RunAsync(SubagentRequest request, CancellationToken token)
        {
            Requests.Enqueue(request);
            string key = request.Prompt.Split('\n').First(l => l.StartsWith("## Your task #", StringComparison.Ordinal));
            int attempt = _attempts.AddOrUpdate(key, 1, (_, n) => n + 1);
            string output = reply(request, attempt);
            if (output == "THROW") throw new InvalidOperationException("boom");
            return Task.FromResult(new SubagentExecutionResult(output, [], []));
        }
    }

    private static TeamMemberSpec Member(string name, string role) => new(name, role);

    private static TeamTaskSpec Work(string id, string assignee, string[]? dependsOn = null, string? verifies = null)
        => new(id, $"title {id}", $"do {id}", assignee, dependsOn, verifies);

    private static async Task<IReadOnlyList<SubagentSnapshot>> WaitForReportAsync(SubagentOrchestrator orchestrator)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            IReadOnlyList<SubagentSnapshot> reports = await orchestrator.WaitForCompletionAsync(timeout.Token);
            if (reports.Count > 0 || !orchestrator.HasPendingResults) return reports;
        }
    }

    private static JsonElement Report(SubagentSnapshot snapshot) => JsonDocument.Parse(snapshot.Output).RootElement;

    [TestMethod]
    public async Task Dependencies_FlowUpstreamReportsDownstream_AndDeliverOneReport()
    {
        var runner = new FakeRunner((request, _) => request.Prompt.Contains("## Your task #t1", StringComparison.Ordinal)
            ? "nginx upstream points at port 8081"
            : "changed upstream to 8080");
        await using var orchestrator = new SubagentOrchestrator(runner.RunAsync, CancellationToken.None);

        string started = orchestrator.EnsureTeam().Run("fix the proxy",
            [Member("scout", "researcher"), Member("fixer", "engineer")],
            [Work("t1", "scout"), Work("t2", "fixer", ["t1"])]);
        IReadOnlyList<SubagentSnapshot> reports = await WaitForReportAsync(orchestrator);

        StringAssert.Contains(started, "started");
        Assert.HasCount(1, reports, "团队只送回一份汇总报告,不逐个送子任务结果");
        Assert.AreEqual("team", reports[0].Role);
        Assert.AreEqual("completed", reports[0].Status);
        SubagentRequest second = runner.Requests.Single(r => r.Prompt.Contains("## Your task #t2", StringComparison.Ordinal));
        StringAssert.Contains(second.Prompt, "<upstream-results>");
        StringAssert.Contains(second.Prompt, "nginx upstream points at port 8081");
        StringAssert.Contains(second.Instructions!, "engineer");
        Assert.AreEqual(TeamEngine.MemberMaxSteps, second.MaxSteps);
        Assert.IsTrue(second.ExtraTools!.OfType<AIFunction>().Any(t => t.Name == "team_message"));
        JsonElement report = Report(reports[0]);
        Assert.AreEqual("completed", report.GetProperty("phase").GetString());
        Assert.AreEqual(2, report.GetProperty("tasks").GetArrayLength());
        Assert.IsFalse(orchestrator.HasPendingResults);
    }

    /// <summary>真实运行器是异步的:成员并行跑、依赖到齐才开跑,结果仍只汇成一份。</summary>
    [TestMethod]
    public async Task AsyncRunner_RunsIndependentTasksInParallelAndWaitsForDependencies()
    {
        int running = 0, peak = 0;
        var runner = new FakeRunner((request, _) => request.Prompt.Contains("## Your task #t3", StringComparison.Ordinal) ? "merged" : "part");
        await using var orchestrator = new SubagentOrchestrator(async (request, token) =>
        {
            int now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            try
            {
                await System.Threading.Tasks.Task.Delay(50, token);
                return await runner.RunAsync(request, token);
            }
            finally { Interlocked.Decrement(ref running); }
        }, CancellationToken.None);

        orchestrator.EnsureTeam().Run("goal", [Member("a", "researcher"), Member("b", "researcher"), Member("c", "writer")],
            [Work("t1", "a"), Work("t2", "b"), Work("t3", "c", ["t1", "t2"])]);
        IReadOnlyList<SubagentSnapshot> reports = await WaitForReportAsync(orchestrator);

        Assert.HasCount(1, reports);
        Assert.AreEqual("completed", reports[0].Status);
        Assert.AreEqual(2, peak, "t1 与 t2 互不依赖,应同时在跑;t3 等它们都完成");
        SubagentRequest merge = runner.Requests.Single(r => r.Prompt.Contains("## Your task #t3", StringComparison.Ordinal));
        StringAssert.Contains(merge.Prompt, "#t1");
        StringAssert.Contains(merge.Prompt, "#t2");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }

    [TestMethod]
    public async Task FailedReview_SendsTheTaskBackOnceWithFeedback()
    {
        var runner = new FakeRunner((request, attempt) =>
            request.Prompt.Contains("## Your task #review", StringComparison.Ordinal)
                ? attempt == 1 ? "missing reload of nginx\nVERDICT: FAIL" : "looks right now\nVERDICT: PASS"
                : attempt == 1 ? "edited config" : "edited config and reloaded");
        await using var orchestrator = new SubagentOrchestrator(runner.RunAsync, CancellationToken.None);

        orchestrator.EnsureTeam().Run("change config safely",
            [Member("fixer", "engineer"), Member("checker", "reviewer")],
            [Work("fix", "fixer"), Work("review", "checker", verifies: "fix")]);
        IReadOnlyList<SubagentSnapshot> reports = await WaitForReportAsync(orchestrator);

        SubagentRequest[] fixes = [.. runner.Requests.Where(r => r.Prompt.Contains("## Your task #fix", StringComparison.Ordinal))];
        Assert.HasCount(2, fixes);
        StringAssert.Contains(fixes[1].Prompt, "<review-feedback>");
        StringAssert.Contains(fixes[1].Prompt, "missing reload of nginx");
        SubagentRequest[] reviews = [.. runner.Requests.Where(r => r.Prompt.Contains("## Your task #review", StringComparison.Ordinal))];
        Assert.HasCount(2, reviews);
        StringAssert.Contains(reviews[1].Prompt, "edited config and reloaded", "第二次审查看到的是返工后的报告");
        StringAssert.Contains(reviews[0].Prompt, "VERDICT: PASS");
        JsonElement review = Report(reports[0]).GetProperty("tasks").EnumerateArray().Single(t => t.GetProperty("id").GetString() == "review");
        Assert.AreEqual("PASS", review.GetProperty("verdict").GetString());
        Assert.AreEqual("completed", reports[0].Status);
    }

    /// <summary>
    /// 看板上的消息带着它说的是哪个任务;上游报告交给下游记一条「交付」(挂在上游任务上),
    /// 审查结论记一条「审查」(挂在审查任务上);任务记下开跑时距团队开跑多久,看板带上主控用的模型 ——
    /// 团队控制台的数据流、时间轴与通信记录靠的就是这几样。
    /// </summary>
    [TestMethod]
    public async Task Board_RecordsHandoffsReviewsTaskLinksAndStartOffsets()
    {
        var runner = new FakeRunner((request, attempt) =>
            request.Prompt.Contains("## Your task #review", StringComparison.Ordinal)
                ? attempt == 1 ? "no reload\nVERDICT: FAIL" : "fine\nVERDICT: PASS"
                : "did it");
        await using var orchestrator = new SubagentOrchestrator(runner.RunAsync, CancellationToken.None) { DefaultModelId = "lead-model" };

        orchestrator.EnsureTeam().Run("change config safely",
            [Member("scout", "researcher"), Member("fixer", "engineer"), Member("checker", "reviewer")],
            [Work("look", "scout"), Work("fix", "fixer", ["look"]), Work("review", "checker", verifies: "fix")]);
        await WaitForReportAsync(orchestrator);
        TeamBoardSnapshot board = orchestrator.Team!.Snapshot()!;

        Assert.AreEqual("lead-model", board.LeadModelId);
        Assert.IsTrue(board.Tasks.All(t => t.StartOffset >= TimeSpan.Zero), "每个开跑过的任务都记了开跑时刻");
        Assert.IsTrue(board.Members.All(m => m.ModelId == "lead-model"), "没指定模型的成员沿用主控的");

        TeamMessageInfo[] handoffs = [.. board.Messages.Where(m => m.Kind == "handoff")];
        Assert.IsTrue(handoffs.Any(m => (m.From, m.To, m.TaskId) == ("scout", "fixer", "look")), "调研报告交给了修改的人");
        Assert.IsTrue(handoffs.Any(m => (m.From, m.To, m.TaskId) == ("fixer", "checker", "fix")), "修改报告交给了审查的人");
        Assert.IsFalse(handoffs.Any(m => m.From == m.To));

        TeamMessageInfo[] reviews = [.. board.Messages.Where(m => m.Kind == "review")];
        Assert.HasCount(2, reviews, "第一次不通过(返工)+ 第二次通过,各一条");
        Assert.IsTrue(reviews.All(m => (m.From, m.To, m.TaskId) == ("checker", "fixer", "review")));
        StringAssert.Contains(reviews[0].Text, "sent back for rework");
        StringAssert.Contains(reviews[1].Text, "passed review");

        Assert.IsTrue(board.Messages.Where(m => m.Kind is "assign" or "report").All(m => m.TaskId is not null), "分派与汇报都挂着任务");
        Assert.HasCount(2, board.Messages.Where(m => m.Kind == "assign" && m.TaskId == "fix").ToArray(), "返工再分派一次");
    }

    [TestMethod]
    public async Task ReviewThatKeepsFailing_StopsAfterOneRework()
    {
        var runner = new FakeRunner((request, _) =>
            request.Prompt.Contains("## Your task #review", StringComparison.Ordinal) ? "still wrong\nVERDICT: FAIL" : "attempt");
        await using var orchestrator = new SubagentOrchestrator(runner.RunAsync, CancellationToken.None);

        orchestrator.EnsureTeam().Run("goal", [Member("a", "engineer"), Member("b", "reviewer")],
            [Work("fix", "a"), Work("review", "b", verifies: "fix")]);
        IReadOnlyList<SubagentSnapshot> reports = await WaitForReportAsync(orchestrator);

        Assert.AreEqual(1 + TeamEngine.MaxReworks,
            runner.Requests.Count(r => r.Prompt.Contains("## Your task #fix", StringComparison.Ordinal)));
        JsonElement review = Report(reports[0]).GetProperty("tasks").EnumerateArray().Single(t => t.GetProperty("id").GetString() == "review");
        Assert.AreEqual("FAIL", review.GetProperty("verdict").GetString());
    }

    [TestMethod]
    public async Task FailedTask_BlocksItsDependents()
    {
        var runner = new FakeRunner((request, _) => request.Prompt.Contains("## Your task #t1", StringComparison.Ordinal) ? "THROW" : "ok");
        await using var orchestrator = new SubagentOrchestrator(runner.RunAsync, CancellationToken.None);

        orchestrator.EnsureTeam().Run("goal", [Member("a", "researcher"), Member("b", "engineer")],
            [Work("t1", "a"), Work("t2", "b", ["t1"]), Work("t3", "b")]);
        IReadOnlyList<SubagentSnapshot> reports = await WaitForReportAsync(orchestrator);

        Assert.AreEqual("failed", reports[0].Status);
        Dictionary<string, string> status = Report(reports[0]).GetProperty("tasks").EnumerateArray()
            .ToDictionary(t => t.GetProperty("id").GetString()!, t => t.GetProperty("status").GetString()!);
        Assert.AreEqual("failed", status["t1"]);
        Assert.AreEqual("blocked", status["t2"]);
        Assert.AreEqual("completed", status["t3"], "不依赖失败任务的照常完成");
        Assert.IsFalse(runner.Requests.Any(r => r.Prompt.Contains("## Your task #t2", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Messages_ReachTeammatesInboxesAndTheLead()
    {
        TeamEngine? team = null;
        var reachedB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner((request, _) =>
        {
            if (request.Prompt.Contains("## Your task #ta", StringComparison.Ordinal))
            {
                team!.Message("a", "b", "config lives in /etc/app.conf");
                team.Message("a", "lead", "found a second problem in cron");
                return "a done";
            }
            // b 依赖 a:开跑时 a 发来的消息已经在任务说明里
            if (request.Prompt.Contains("config lives in /etc/app.conf", StringComparison.Ordinal)) reachedB.TrySetResult();
            return "b done";
        });
        await using var orchestrator = new SubagentOrchestrator(runner.RunAsync, CancellationToken.None);
        team = orchestrator.EnsureTeam();

        team.Run("goal", [Member("a", "researcher"), Member("b", "engineer")], [Work("ta", "a"), Work("tb", "b", ["ta"])]);
        IReadOnlyList<SubagentSnapshot> reports = await WaitForReportAsync(orchestrator);

        await reachedB.Task.WaitAsync(TimeSpan.FromSeconds(5));
        JsonElement toLead = Report(reports[0]).GetProperty("messagesToLead");
        Assert.AreEqual("found a second problem in cron", toLead[0].GetProperty("text").GetString());
        TeamBoardSnapshot board = team.Snapshot()!;
        Assert.IsTrue(board.Messages.Any(m => m.Kind == "peer" && m.From == "a" && m.To == "b"));
        Assert.IsTrue(board.Messages.Any(m => m.Kind == "report" && m.To == "lead"));
    }

    [TestMethod]
    public void Run_RejectsBrokenPlansWithoutStartingAnything()
    {
        var runner = new FakeRunner((_, _) => "x");
        var orchestrator = new SubagentOrchestrator(runner.RunAsync, CancellationToken.None);
        TeamEngine team = orchestrator.EnsureTeam();

        StringAssert.Contains(team.Run("g", [Member("a", "r")], [Work("t1", "ghost")]), "unknown member");
        StringAssert.Contains(team.Run("g", [Member("a", "r")], [Work("t1", "a", ["t2"]), Work("t2", "a", ["t1"])]), "cycle");
        StringAssert.Contains(team.Run("g", [Member("a", "r"), Member("A", "r")], [Work("t1", "a")]), "Duplicate member");
        StringAssert.Contains(team.Run("g", [Member("lead", "r")], [Work("t1", "lead")]), "reserved");
        StringAssert.Contains(team.Run("g", [Member("a", "r")], [Work("t1", "a", verifies: "t1")]), "itself");
        Assert.IsTrue(runner.Requests.IsEmpty);
        Assert.IsFalse(team.IsActive);
    }

    /// <summary>
    /// 正常跑完的团队,这一轮收尾释放编排器之后看板仍是「已完成」——
    /// 释放时的 Shutdown 曾无条件置位取消,卡片最后一次刷新就把已完成改判成了已取消。
    /// </summary>
    [TestMethod]
    public async Task FinishedTeam_StaysCompletedAfterTheOrchestratorIsDisposed()
    {
        var runner = new FakeRunner((_, _) => "ok");
        var orchestrator = new SubagentOrchestrator(runner.RunAsync, CancellationToken.None);
        TeamEngine team = orchestrator.EnsureTeam();
        team.Run("goal", [Member("a", "researcher")], [Work("t1", "a")]);
        await WaitForReportAsync(orchestrator);

        await orchestrator.DisposeAsync();

        Assert.AreEqual("completed", team.Snapshot()!.Phase);
    }

    /// <summary>还在跑时被释放(用户停下这一轮):看板记为已取消,任务不再开始。</summary>
    [TestMethod]
    public async Task RunningTeam_IsCancelledWhenTheOrchestratorIsDisposed()
    {
        var gate = new TaskCompletionSource<SubagentExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var orchestrator = new SubagentOrchestrator(async (_, token) =>
        {
            await using (token.Register(() => gate.TrySetCanceled(token)))
                return await gate.Task;
        }, CancellationToken.None);
        TeamEngine team = orchestrator.EnsureTeam();
        team.Run("goal", [Member("a", "researcher"), Member("b", "engineer")], [Work("t1", "a"), Work("t2", "b", ["t1"])]);

        await orchestrator.DisposeAsync();

        TeamBoardSnapshot board = team.Snapshot()!;
        Assert.AreEqual("cancelled", board.Phase);
        Assert.IsTrue(board.Tasks.All(t => t.Status is "cancelled" or "blocked"));
    }

    /// <summary>
    /// 团队模式由主模型自适应启用:提示词明说不必等用户点名,并给出用与不用的判据;
    /// 只在这一轮真有 team_run 时才附加(工具被停用、对外 MCP 时不提)。
    /// </summary>
    [TestMethod]
    public void TeamGuidance_IsAdaptiveAndOnlyAddedWhenTeamRunIsAvailable()
    {
        AIFunction teamRun = AIFunctionFactory.Create(() => "", "team_run");
        AIFunction delegate_ = AIFunctionFactory.Create(() => "", "delegate_subagent");

        Assert.IsTrue(SubagentRunner.HasTeam([delegate_, teamRun]));
        Assert.IsFalse(SubagentRunner.HasTeam([delegate_]));
        Assert.IsFalse(SubagentRunner.HasTeam(null));
        StringAssert.Contains(SubagentRunner.TeamGuidance, "without waiting for the user to ask for a team");
        StringAssert.Contains(SubagentRunner.TeamGuidance, "Do NOT form a team");
        Assert.DoesNotContain("team_run", SubagentRunner.DelegationGuidance, "停用团队工具时委派说明里不能再提它");
    }

    [TestMethod]
    public void ParseVerdict_TakesTheLastOneAndTreatsMissingAsFail()
    {
        Assert.AreEqual("PASS", TeamEngine.ParseVerdict("first VERDICT: FAIL\nfixed\nverdict: pass"));
        Assert.AreEqual("FAIL", TeamEngine.ParseVerdict("looks fine to me"));
    }

    [TestMethod]
    public async Task InboxClient_CarriesDeliveredMessagesIntoEveryLaterStep()
    {
        var pending = new Queue<string>(["<team-message>\nhello\n</team-message>"]);
        using var inbox = new InboxChatClient(new EchoClient(), () => pending.Count > 0 ? [pending.Dequeue()] : []);
        List<ChatMessage> step = [new(ChatRole.User, "task"), new(ChatRole.Assistant, [new FunctionCallContent("c1", "read")]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "out")])];

        List<ChatMessage> first = inbox.Compose(step);
        List<ChatMessage> second = inbox.Compose(step);

        StringAssert.Contains(first[^1].Text, "hello");
        StringAssert.Contains(second[^1].Text, "hello", "循环下一步整份重发上下文,送达过的消息要跟着重发");
        Assert.AreEqual(1, inbox.DeliveredCount);
        await Task.CompletedTask;
    }

    private sealed class EchoClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new ChatResponse([]));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => AsyncEnumerable.Empty<ChatResponseUpdate>();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
