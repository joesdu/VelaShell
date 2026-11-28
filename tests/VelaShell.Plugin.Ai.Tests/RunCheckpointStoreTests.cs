using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Core.Runtime;
using VelaShell.Plugin.Ai.Ui;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 断点续跑的检查点:落盘读回、截断、恢复时调用与结果成对,
/// 以及中断时没拿到结果的调用一律标成「结果未知」。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class RunCheckpointStoreTests
{
    private string _directory = "";

    [TestInitialize]
    public void Setup() => _directory = Directory.CreateTempSubdirectory("vela-checkpoint-").FullName;

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private static List<ChatMessage> Steps(int count, bool lastUnanswered = false)
    {
        List<ChatMessage> messages = [];
        for (int i = 0; i < count; i++)
        {
            messages.Add(new ChatMessage(ChatRole.Assistant,
                [new TextContent($"第 {i} 步"), new FunctionCallContent($"c{i}", "run_command",
                    new Dictionary<string, object?> { ["command"] = $"echo {i} > /tmp/{i}" })]));
            if (lastUnanswered && i == count - 1) break;
            messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"c{i}", $"输出 {i}")]));
        }
        return messages;
    }

    private static RunCheckpoint Checkpoint(IEnumerable<ChatMessage> messages, string summary = "")
        => new("conv-1", "run-1", "清理 /var/log", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "running",
            RunCheckpointStore.Capture(messages), summary);

    [TestMethod]
    public void SaveLoadDelete_RoundTrips()
    {
        var store = new RunCheckpointStore(_directory);
        RunCheckpoint saved = Checkpoint(Steps(3), "已完成前两步");

        store.Save(saved);
        RunCheckpoint? loaded = store.Load("conv-1");

        Assert.IsNotNull(loaded);
        Assert.AreEqual("清理 /var/log", loaded.Task);
        Assert.AreEqual("已完成前两步", loaded.ProgressSummary);
        Assert.AreEqual(3, loaded.ToolCalls);
        Assert.Contains("conv-1", store.ConversationIds().ToList());
        store.Delete("conv-1");
        Assert.IsNull(store.Load("conv-1"));
    }

    [TestMethod]
    public void Load_DropsCorruptFiles()
    {
        var store = new RunCheckpointStore(_directory);
        Directory.CreateDirectory(Path.Combine(_directory, "run-checkpoints"));
        string path = Path.Combine(_directory, "run-checkpoints", "conv-1.json");
        File.WriteAllText(path, "{ not json");

        Assert.IsNull(store.Load("conv-1"));
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public void Load_LegacyCheckpointWithoutEvents_RemainsCompatible()
    {
        var store = new RunCheckpointStore(_directory);
        Directory.CreateDirectory(Path.Combine(_directory, "run-checkpoints"));
        string path = Path.Combine(_directory, "run-checkpoints", "conv-1.json");
        File.WriteAllText(path, "{\"ConversationId\":\"conv-1\",\"RunId\":\"run-1\",\"Task\":\"task\",\"StartedAt\":\"2026-01-01T00:00:00Z\",\"UpdatedAt\":\"2026-01-01T00:00:00Z\",\"Status\":\"running\",\"Steps\":[{\"Role\":\"assistant\",\"Text\":\"done\"}]}");

        RunCheckpoint? loaded = store.Load("conv-1");

        Assert.IsNotNull(loaded);
        Assert.IsNull(loaded.Events);
        Assert.AreEqual("task", loaded.Task);
    }

    [TestMethod]
    public void SaveLoad_PreservesOrchestrationState()
    {
        var store = new RunCheckpointStore(_directory);
        AgentOrchestrationState state = AgentOrchestrationState.Create("run-1")
            .StartPlanning()
            .AcceptPlan(new AgentPlan("清理", [], [new AgentPlanStep("inspect", "检查")], []));

        store.Save(Checkpoint(Steps(1)) with { Orchestration = state });
        RunCheckpoint? loaded = store.Load("conv-1");

        Assert.IsNotNull(loaded?.Orchestration);
        Assert.AreEqual(AgentOrchestrationPhase.Executing, loaded.Orchestration.Phase);
        Assert.AreEqual("inspect", loaded.Orchestration.Plan!.Steps[0].Id);
    }

    [TestMethod]
    public void SaveLoad_PreservesDiagnosticEvents()
    {
        var store = new RunCheckpointStore(_directory);
        RunCheckpoint saved = Checkpoint(Steps(1)) with
        {
            Events = [new RunCheckpointEvent(7, DateTimeOffset.UtcNow, "ToolCallCompleted", 1,
                "call-1", "read_file", "succeeded", 12, "completed")]
        };

        store.Save(saved);
        RunCheckpoint? loaded = store.Load("conv-1");

        Assert.IsNotNull(loaded?.Events);
        Assert.HasCount(1, loaded.Events);
        Assert.AreEqual("ToolCallCompleted", loaded.Events[0].Kind);
        Assert.AreEqual("read_file", loaded.Events[0].ToolName);
    }

    [TestMethod]
    public void ConversationId_CannotEscapeTheDirectory()
    {
        var store = new RunCheckpointStore(_directory);
        store.Save(Checkpoint(Steps(1)) with { ConversationId = "../../evil" });

        Assert.IsTrue(File.Exists(Path.Combine(_directory, "run-checkpoints", "evil.json")));
    }

    [TestMethod]
    public void Capture_ClipsLargeOutputs()
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.Assistant, [new FunctionCallContent("c0", "read_file")]),
            new(ChatRole.Tool, [new FunctionResultContent("c0", new string('x', 100_000))])
        ];

        IReadOnlyList<RunCheckpointMessage> captured = RunCheckpointStore.Capture(messages);

        Assert.IsLessThan(RunCheckpointStore.MaxResultChars + 32, captured[1].Results![0].Text.Length);
    }

    [TestMethod]
    public void Restore_MarksUnansweredCallsAsUnknownAndKeepsPairs()
    {
        RunCheckpoint checkpoint = Checkpoint(Steps(3, lastUnanswered: true));

        (List<ChatMessage> messages, string earlier) = RunCheckpointStore.Restore(checkpoint);

        Assert.AreEqual("", earlier);
        var calls = messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.CallId).ToList();
        var results = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToList();
        Assert.AreSequenceEqual(calls, [.. results.Select(r => r.CallId)], SequenceOrder.InAnyOrder);
        Assert.AreEqual(RunCheckpointStore.UnknownOutcome, results.Single(r => r.CallId == "c2").Result);
        Assert.AreEqual("echo 1 > /tmp/1",
            messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Single(c => c.CallId == "c1").Arguments!["command"]!.ToString());
    }

    [TestMethod]
    public void Restore_LongRunsKeepRecentStepsVerbatimAndListEarlierCalls()
    {
        RunCheckpoint checkpoint = Checkpoint(Steps(40));

        (List<ChatMessage> messages, string earlier) = RunCheckpointStore.Restore(checkpoint);

        Assert.IsLessThanOrEqualTo(RunCheckpointStore.ResumeVerbatimMessages, messages.Count);
        Assert.AreEqual(ChatRole.Assistant, messages[0].Role, "从一步的开头恢复");
        Assert.Contains("run_command", earlier);
        Assert.Contains("/tmp/0", earlier);
    }

    [TestMethod]
    public void ResumePrompt_CarriesTaskSummaryAndSafetyRule()
    {
        string prompt = ChatPanelView.BuildResumePrompt(Checkpoint(Steps(2), "改过 nginx.conf"), "- run_command x\n");

        Assert.Contains("清理 /var/log", prompt);
        Assert.Contains("改过 nginx.conf", prompt);
        Assert.Contains("- run_command x", prompt);
        Assert.Contains("unknown outcome", prompt);
    }
}
