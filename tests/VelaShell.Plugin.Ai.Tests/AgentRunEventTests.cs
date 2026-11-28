using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Chat;

namespace VelaShell.Plugin.Ai.Tests;

[TestClass]
public sealed class AgentRunEventTests
{
    [TestMethod]
    public void EventLog_IsBounded_AndSequencesRemainMonotonic()
    {
        var log = new AgentRunEventLog(capacity: 2);
        List<AgentRunEvent> observed = [];
        log.Appended += observed.Add;

        log.Append("run", AgentRunEventKind.StepStarted, 1, summary: "first");
        log.Append("run", AgentRunEventKind.StepCompleted, 1, summary: "second");
        log.Append("run", AgentRunEventKind.BatchContinued, 0, summary: "third");

        Assert.HasCount(2, log.Snapshot);
        Assert.AreEqual(2L, log.Snapshot[0].Sequence);
        Assert.AreEqual(3L, log.Snapshot[1].Sequence);
        Assert.AreSequenceEqual([1L, 2L, 3L], observed.Select(e => e.Sequence));
        RunCheckpointEvent[] checkpointEvents = [.. RunCheckpointStore.CaptureEvents(log.Snapshot)];
        Assert.AreEqual("StepCompleted", checkpointEvents[0].Kind);
        Assert.AreEqual("BatchContinued", checkpointEvents[1].Kind);
    }

    [TestMethod]
    public async Task AgentExecution_EmitsLifecycleAndToolOutcomeEvents()
    {
        var log = new AgentRunEventLog();
        var execution = new AgentExecution("run", eventLog: log);
        AIFunction function = AIFunctionFactory.Create(() => "ok", "read");
        var context = new FunctionInvocationContext
        {
            Function = function,
            Arguments = [],
            CallContent = new("call-1", "read", new Dictionary<string, object?>())
        };

        execution.BeginStep([new(ChatRole.User, "inspect")]);
        Assert.AreEqual("ok", (await execution.InvokeAsync(context, default))?.ToString());
        execution.CompleteStep(new ChatResponse([new ChatMessage(ChatRole.Assistant, "done")]));
        execution.ContinueBatch();
        execution.RecordNotification(new ChatMessage(ChatRole.User, "background result"));

        AgentRunEvent[] events = [.. log.Snapshot];
        Assert.Contains(e => e.Kind == AgentRunEventKind.StepStarted, events);
        AgentRunEvent[] startedEvents = [.. events.Where(e => e.Kind == AgentRunEventKind.ToolCallStarted)];
        AgentRunEvent[] completedEvents = [.. events.Where(e => e.Kind == AgentRunEventKind.ToolCallCompleted)];
        Assert.HasCount(1, startedEvents);
        Assert.HasCount(1, completedEvents);
        AgentRunEvent started = startedEvents[0];
        AgentRunEvent completed = completedEvents[0];
        Assert.AreEqual("call-1", started.CallId);
        Assert.AreEqual("read", started.ToolName);
        Assert.AreEqual("succeeded", completed.Outcome);
        Assert.Contains(e => e.Kind == AgentRunEventKind.StepCompleted, events);
        Assert.Contains(e => e.Kind == AgentRunEventKind.BatchContinued, events);
        Assert.Contains(e => e.Kind == AgentRunEventKind.NotificationRecorded, events);
    }

    [TestMethod]
    public void EventSubscriberFailure_DoesNotBreakEventRecording()
    {
        var log = new AgentRunEventLog();
        log.Appended += _ => throw new InvalidOperationException("observer failure");

        log.Append("run", AgentRunEventKind.RunPaused, 1, outcome: "NoProgress");

        Assert.HasCount(1, log.Snapshot);
        Assert.AreEqual("NoProgress", log.Snapshot[0].Outcome);
    }
}
