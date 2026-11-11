using VelaShell.Plugin.Ai.Agent;

namespace VelaShell.Plugin.Ai.Tests;

[TestClass]
public sealed class SubagentOrchestratorTests
{
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
        Assert.AreEqual(1, models.Count(m => m == "replacement"));
        Assert.AreEqual(1, models.Count(m => m == "explicit"));
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

        string[] ids = Enumerable.Range(0, 5)
            .Select(_ => orchestrator.Start("wait", "timeout")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)[2]
                .TrimEnd('.'))
            .ToArray();

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
        orchestrator.SnapshotsChanged += snapshots => changes.Add(snapshots);

        string started = orchestrator.Start("inspect", "reader");
        string id = started.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2].TrimEnd('.');
        await running.Task;

        Assert.IsTrue(changes.SelectMany(snapshot => snapshot)
            .Any(snapshot => snapshot.SubagentId == id && snapshot.Status == "running"));

        release.SetResult();
        await orchestrator.WaitAsync(id, 5, CancellationToken.None);

        Assert.IsTrue(changes.SelectMany(snapshot => snapshot)
            .Any(snapshot => snapshot.SubagentId == id
                && snapshot.Status == "completed"
                && snapshot.ToolsUsed.Any(tool => tool.Name == "read_local_file")));
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
}
