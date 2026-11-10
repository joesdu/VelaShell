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
}
