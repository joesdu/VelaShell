using System.Text.Json;
using VelaShell.Plugin.Ai.Agent;

namespace VelaShell.Plugin.Ai.Tests;

[TestClass]
public sealed class SubagentOrchestratorTests
{
    [TestMethod]
    public async Task AllowWrites_IsExplicitAndReported()
    {
        var requests = new List<SubagentRequest>();
        await using var orchestrator = new SubagentOrchestrator((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(SubagentExecutionResult.FromText("done"));
        }, CancellationToken.None);
        orchestrator.Start("read", null);
        orchestrator.Start("edit", null, allowWrites: true);
        foreach (SubagentSnapshot snapshot in orchestrator.Snapshots())
            await orchestrator.WaitAsync(snapshot.SubagentId, 5, CancellationToken.None);
        Assert.IsFalse(requests.Single(r => r.Prompt == "read").AllowWrites);
        Assert.IsTrue(requests.Single(r => r.Prompt == "edit").AllowWrites);
        string id = orchestrator.Snapshots().Single(s => s.TaskPrompt == "edit").SubagentId;
        Assert.Contains("\"allowWrites\":true", orchestrator.Status(id));
    }

    [TestMethod]
    public async Task CancellationAfterWrite_PreservesUsageAndDoesNotReportCompletion()
    {
        using var cancellation = new CancellationTokenSource();
        SubagentOrchestrator? owner = null;
        owner = new SubagentOrchestrator((_, _) =>
        {
            owner!.RecordToolUsage("write_local_file", "Wrote 3 bytes.");
            cancellation.Cancel();
            return Task.FromResult(SubagentExecutionResult.FromText("changed"));
        }, cancellation.Token);
        await using SubagentOrchestrator orchestrator = owner;
        orchestrator.Start("edit", null, allowWrites: true);
        SubagentSnapshot snapshot = orchestrator.Snapshots().Single();
        string result = await orchestrator.WaitAsync(snapshot.SubagentId, 5, CancellationToken.None);
        Assert.Contains("\"status\":\"cancelled\"", result);
        Assert.Contains("\"name\":\"write_local_file\"", result);
    }

    [TestMethod]
    public async Task DefaultModelChange_DoesNotRerouteQueuedTasks()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var models = new System.Collections.Concurrent.ConcurrentBag<string?>();
        await using var orchestrator = new SubagentOrchestrator(async (request, token) =>
        {
            await release.Task.WaitAsync(token);
            models.Add(request.ModelId);
            return SubagentExecutionResult.FromText("done");
        }, CancellationToken.None)
        { DefaultModelId = "original" };
        for (int i = 0; i < 5; i++) orchestrator.Start("inspect", null);
        orchestrator.DefaultModelId = "replacement";
        orchestrator.Start("inspect", null);
        orchestrator.Start("inspect", null, "explicit");
        release.SetResult();
        await Task.WhenAll(orchestrator.Snapshots().Select(s =>
            orchestrator.WaitAsync(s.SubagentId, 5, CancellationToken.None)));
        Assert.AreEqual(5, models.Count(m => m == "original"));
        Assert.ContainsSingle(m => m == "replacement", models);
        Assert.ContainsSingle(m => m == "explicit", models);
        Assert.IsTrue(orchestrator.Snapshots().All(s => s.Status == "completed"));
    }

    [TestMethod]
    public async Task StartAndWait_ReturnsCompletedResult()
    {
        await using var orchestrator = new SubagentOrchestrator(
            (prompt, _) => Task.FromResult($"analysis: {prompt}"),
            CancellationToken.None);

        string started = orchestrator.Start("inspect the configuration", "configuration");
        string id = started.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2].TrimEnd('.');

        string result = await orchestrator.WaitAsync(id, 5, CancellationToken.None);

        Assert.Contains("completed", result);
        Assert.Contains("analysis:", result);
    }

    [TestMethod]
    public async Task ParentCancellation_CancelsRunningSubagent()
    {
        using var parent = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var orchestrator = new SubagentOrchestrator(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "unreachable";
        }, parent.Token);

        string response = orchestrator.Start("wait", null);
        string id = response.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2].TrimEnd('.');
        await started.Task;
        parent.Cancel();

        string result = await orchestrator.WaitAsync(id, 5, CancellationToken.None);

        Assert.Contains("cancelled", result);
    }

    [TestMethod]
    public async Task CompletedResult_ReportsStructuredToolUsage()
    {
        await using var orchestrator = new SubagentOrchestrator(
            async (request, _) => new SubagentExecutionResult(
                $"analysis: {request.Prompt}",
                [new SubagentToolUsage("read_local_file", 3)],
                []),
            CancellationToken.None);

        string started = orchestrator.Start("inspect files", "workspace");
        string id = started.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2].TrimEnd('.');
        string result = await orchestrator.WaitAsync(id, 5, CancellationToken.None);

        Assert.Contains("\"status\":\"completed\"", result);
        Assert.Contains("\"role\":\"workspace\"", result);
        Assert.Contains("\"name\":\"read_local_file\"", result);
        Assert.Contains("\"count\":3", result);
    }

    [TestMethod]
    public async Task Timeout_ReleasesConcurrencySlot()
    {
        await using var orchestrator = new SubagentOrchestrator(
            async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return SubagentExecutionResult.FromText("unreachable");
            },
            CancellationToken.None,
            TimeSpan.FromMilliseconds(30));

        string[] ids = [.. Enumerable.Range(0, 5)
            .Select(_ => orchestrator.Start("wait", "timeout")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)[2]
                .TrimEnd('.'))];

        string[] results = await Task.WhenAll(ids.Select(id => orchestrator.WaitAsync(id, 5, CancellationToken.None)));

        Assert.IsTrue(results.All(result => result.Contains("\"status\":\"timed_out\"")));
    }

    [TestMethod]
    public async Task SnapshotsChanged_ReportsRunningAndCompletedStates()
    {
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<IReadOnlyList<SubagentSnapshot>> changes = [];
        await using var orchestrator = new SubagentOrchestrator(async (_, token) =>
        {
            running.SetResult();
            await release.Task.WaitAsync(token);
            return new SubagentExecutionResult("done", [new SubagentToolUsage("read_local_file", 1)], []);
        }, CancellationToken.None);
        orchestrator.SnapshotsChanged += changes.Add;

        string started = orchestrator.Start("inspect", "reader");
        string id = started.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2].TrimEnd('.');
        await running.Task;

        Assert.Contains(snapshot => snapshot.SubagentId == id && snapshot.Status == "running", changes.SelectMany(snapshot => snapshot));

        release.SetResult();
        await orchestrator.WaitAsync(id, 5, CancellationToken.None);

        Assert.Contains(snapshot => snapshot.SubagentId == id
                && snapshot.Status == "completed"
                && snapshot.ToolsUsed.Any(tool => tool.Name == "read_local_file"), changes.SelectMany(snapshot => snapshot));
    }

    [TestMethod]
    public async Task RecordToolUsage_IsVisibleBeforeCompletion()
    {
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SubagentOrchestrator? owner = null;
        owner = new SubagentOrchestrator(async (_, token) =>
        {
            owner!.RecordToolUsage("read_local_file", "ok");
            running.SetResult();
            await release.Task.WaitAsync(token);
            return SubagentExecutionResult.FromText("done");
        }, CancellationToken.None);
        await using SubagentOrchestrator orchestrator = owner;

        string started = orchestrator.Start("inspect", null);
        string id = started.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2].TrimEnd('.');
        await running.Task;

        string status = orchestrator.Status(id);
        Assert.Contains("\"name\":\"read_local_file\"", status);
        Assert.Contains("\"count\":1", status);

        release.SetResult();
        await orchestrator.WaitAsync(id, 5, CancellationToken.None);
    }

    [TestMethod]
    public async Task WaitForCompletion_DeliversEachTerminalResultOnce()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var orchestrator = new SubagentOrchestrator(async (request, token) =>
        {
            if (request.Prompt == "slow") await release.Task.WaitAsync(token);
            return SubagentExecutionResult.FromText($"report: {request.Prompt}");
        }, CancellationToken.None);
        Assert.IsFalse(orchestrator.HasPendingResults);
        Assert.IsEmpty(await orchestrator.WaitForCompletionAsync(CancellationToken.None), "没有任务时不该挂住");

        orchestrator.Start("fast", null);
        orchestrator.Start("slow", null);
        Assert.IsTrue(orchestrator.HasPendingResults);
        IReadOnlyList<SubagentSnapshot> first = await orchestrator.WaitForCompletionAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("fast", first.Single().TaskPrompt);
        Assert.AreEqual("report: fast", first.Single().Output);
        Assert.IsTrue(orchestrator.HasPendingResults, "还有一个在跑");

        Task<IReadOnlyList<SubagentSnapshot>> pending = orchestrator.WaitForCompletionAsync(CancellationToken.None);
        Assert.IsFalse(pending.IsCompleted, "已交回的结果不能再唤醒一次等待");
        release.SetResult();
        Assert.AreEqual("slow", (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Single().TaskPrompt);
        Assert.IsFalse(orchestrator.HasPendingResults);
        Assert.IsEmpty(await orchestrator.WaitForCompletionAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task ResultCollectedByTheModel_IsNotDeliveredAgain()
    {
        await using var orchestrator = new SubagentOrchestrator(
            (prompt, _) => Task.FromResult("done"), CancellationToken.None);
        orchestrator.Start("inspect", null);
        string id = orchestrator.Snapshots().Single().SubagentId;

        Assert.Contains("\"status\":\"completed\"", await orchestrator.WaitAsync(id, 5, CancellationToken.None));

        Assert.IsFalse(orchestrator.HasPendingResults);
        Assert.IsEmpty(await orchestrator.WaitForCompletionAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task WaitOnARunningSubagent_ReturnsItsLiveStatus()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SubagentOrchestrator? owner = null;
        owner = new SubagentOrchestrator(async (_, token) =>
        {
            owner!.BeginTool("read_local_file", "src/app.cs");
            owner.ReportOutput("half a sentence");
            await release.Task.WaitAsync(token);
            return SubagentExecutionResult.FromText("done");
        }, CancellationToken.None);
        await using SubagentOrchestrator orchestrator = owner;
        orchestrator.Start("inspect", null);
        string id = orchestrator.Snapshots().Single().SubagentId;

        using var running = JsonDocument.Parse(await orchestrator.WaitAsync(id, 1, CancellationToken.None));

        Assert.AreEqual("running", running.RootElement.GetProperty("status").GetString());
        Assert.AreEqual("read_local_file · src/app.cs", running.RootElement.GetProperty("activity").GetString());
        Assert.AreEqual("", running.RootElement.GetProperty("output").GetString(), "运行中的半截正文不交给父模型");
        Assert.AreEqual("half a sentence", orchestrator.Snapshots().Single().Output, "卡片上看得到实时正文");
        Assert.IsTrue(orchestrator.HasPendingResults, "没到终态的查询不算已交回");
        release.SetResult();
        await orchestrator.WaitAsync(id, 5, CancellationToken.None);
    }

    [TestMethod]
    public async Task TimeSpentWaitingForApproval_DoesNotCountAgainstTheTimeout()
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var approve = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var orchestrator = new SubagentOrchestrator(async (_, token) =>
        {
            using (SubagentOrchestrator.SuspendCurrentTimeout(SubagentOrchestrator.WaitingForApproval))
            {
                waiting.SetResult();
                await approve.Task.WaitAsync(token);
            }
            return SubagentExecutionResult.FromText("written");
        }, CancellationToken.None, TimeSpan.FromMilliseconds(200));
        orchestrator.Start("edit", null, allowWrites: true);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 远超执行期限:用户迟迟不点,任务不该因此超时
        await Task.Delay(800);
        SubagentSnapshot paused = orchestrator.Snapshots().Single();
        Assert.AreEqual("running", paused.Status);
        Assert.AreEqual(SubagentOrchestrator.WaitingForApproval, paused.Activity);

        approve.SetResult();
        string result = await orchestrator.WaitAsync(paused.SubagentId, 5, CancellationToken.None);
        Assert.Contains("\"status\":\"completed\"", result);
        Assert.AreEqual("", orchestrator.Snapshots().Single().Activity);
    }

    [TestMethod]
    public async Task TimeoutResumesAfterTheWait_AndKeepsWhatWasWritten()
    {
        SubagentOrchestrator? owner = null;
        owner = new SubagentOrchestrator(async (_, token) =>
        {
            using (SubagentOrchestrator.SuspendCurrentTimeout(SubagentOrchestrator.WaitingForFileLock))
            {
                await Task.Delay(300, token);
            }
            owner!.ReportOutput("found two issues so far");
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return SubagentExecutionResult.FromText("unreachable");
        }, CancellationToken.None, TimeSpan.FromMilliseconds(150));
        await using SubagentOrchestrator orchestrator = owner;
        orchestrator.Start("inspect", null);
        string id = orchestrator.Snapshots().Single().SubagentId;

        using var result = JsonDocument.Parse(await orchestrator.WaitAsync(id, 10, CancellationToken.None));

        Assert.AreEqual("timed_out", result.RootElement.GetProperty("status").GetString());
        Assert.AreEqual("found two issues so far", result.RootElement.GetProperty("output").GetString());
        Assert.IsTrue(result.RootElement.GetProperty("outputIsPartial").GetBoolean());
    }

    [TestMethod]
    public async Task PerTaskTimeout_IsClampedAndOverridesTheDefault()
    {
        await using var orchestrator = new SubagentOrchestrator(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return SubagentExecutionResult.FromText("unreachable");
        }, CancellationToken.None, TimeSpan.FromMilliseconds(30));
        orchestrator.Start("default", null);
        orchestrator.Start("longer", null, timeoutSeconds: 1);
        string[] ids = [.. orchestrator.Snapshots().Select(s => s.SubagentId)];

        Assert.Contains("\"status\":\"timed_out\"", await orchestrator.WaitAsync(ids[0], 5, CancellationToken.None));
        // 请求 1 秒被夹到下限 30 秒:默认那个已经超时,它还在跑
        using var longer = JsonDocument.Parse(await orchestrator.WaitAsync(ids[1], 1, CancellationToken.None));
        Assert.AreEqual("running", longer.RootElement.GetProperty("status").GetString());
        Assert.Contains("already finished", orchestrator.Cancel(ids[0]));
        Assert.Contains("Cancellation requested", orchestrator.Cancel(ids[1]));
        Assert.Contains("\"status\":\"cancelled\"", await orchestrator.WaitAsync(ids[1], 5, CancellationToken.None));
    }

    [TestMethod]
    public async Task Snapshots_KeepCreationOrder()
    {
        await using var orchestrator = new SubagentOrchestrator(
            (prompt, _) => Task.FromResult(prompt), CancellationToken.None);
        string[] prompts = ["first", "second", "third", "fourth", "fifth", "sixth"];
        foreach (string prompt in prompts) orchestrator.Start(prompt, null);

        Assert.AreSequenceEqual(prompts, [.. orchestrator.Snapshots().Select(s => s.TaskPrompt)]);
    }

    [TestMethod]
    public void IsDenialResult_MatchesOnlyToolDenialSentences()
    {
        Assert.IsTrue(SubagentOrchestrator.IsDenialResult("The user DENIED this local edit. Do not retry."));
        Assert.IsTrue(SubagentOrchestrator.IsDenialResult("The host DENIED this connection (x)."));
        Assert.IsTrue(SubagentOrchestrator.IsDenialResult("No approval channel available; the call was not executed."));
        // 读到的文件/终端内容里出现 denied 很常见,不能当成错误
        Assert.IsFalse(SubagentOrchestrator.IsDenialResult("sshd[1]: Permission denied (publickey)."));
        Assert.IsFalse(SubagentOrchestrator.IsDenialResult("log line\nThe user DENIED nothing"));
    }
}
