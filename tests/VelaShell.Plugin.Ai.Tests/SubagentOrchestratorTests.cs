using VelaShell.Plugin.Ai.Agent;

namespace VelaShell.Plugin.Ai.Tests;

[TestClass]
public sealed class SubagentOrchestratorTests
{
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
}
