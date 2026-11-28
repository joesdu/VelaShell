using System.Collections.Immutable;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Chat.Runtime;

namespace VelaShell.Plugin.Ai.Tests;

[TestClass]
public sealed class AgentRunSnapshotTests
{
    private static AgentRunEvent Event(long sequence, AgentRunEventKind kind, int step,
        DateTimeOffset timestamp, string? callId = null, string? toolName = null,
        string? outcome = null, int? durationMilliseconds = null, string? summary = null)
        => new(sequence, "run", timestamp, kind, step, callId, toolName, outcome, durationMilliseconds, summary);

    [TestMethod]
    public void Projection_AggregatesLifecycleMetricsAndCurrentState()
    {
        var projector = new AgentRunSnapshotProjector("run");
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        projector.Apply(
        [
            Event(1, AgentRunEventKind.StepStarted, 2, start),
            Event(2, AgentRunEventKind.ToolCallStarted, 2, start.AddSeconds(1), "call-1", "read"),
            Event(3, AgentRunEventKind.ToolCallCompleted, 2, start.AddSeconds(3), "call-1", "read", "succeeded", 2000),
            Event(4, AgentRunEventKind.BackgroundWaitStarted, 2, start.AddSeconds(4), summary: "transfer"),
            Event(5, AgentRunEventKind.BackgroundWaitCompleted, 2, start.AddSeconds(7), outcome: "completed", durationMilliseconds: 3000),
            Event(6, AgentRunEventKind.RunPaused, 2, start.AddSeconds(8), outcome: "NoProgress"),
        ]);

        AgentRunSnapshot snapshot = projector.Snapshot;
        Assert.AreEqual(AgentProjectionStatus.Paused, snapshot.Status);
        Assert.AreEqual(2, snapshot.CurrentStep);
        Assert.IsNull(snapshot.ActiveTool);
        Assert.IsNull(snapshot.ActiveWait);
        Assert.AreEqual(6L, snapshot.LastSequence);
        Assert.AreEqual(TimeSpan.FromSeconds(8), snapshot.Elapsed);
        Assert.AreEqual(1, snapshot.Metrics.ToolCalls);
        Assert.AreEqual(1, snapshot.Metrics.CompletedToolCalls);
        Assert.AreEqual(1, snapshot.Metrics.SuccessfulToolCalls);
        Assert.AreEqual(0, snapshot.Metrics.FailedToolCalls);
        Assert.AreEqual(0, snapshot.Metrics.CancelledToolCalls);
        Assert.AreEqual(1, snapshot.Metrics.BackgroundWaits);
        Assert.AreEqual(1, snapshot.Metrics.CompletedBackgroundWaits);
        Assert.AreEqual(1, snapshot.Metrics.Pauses);
    }

    [TestMethod]
    public void Projection_ExportsRedactedTelemetry()
    {
        var projector = new AgentRunSnapshotProjector("run");
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        projector.Apply(
        [
            Event(1, AgentRunEventKind.StepStarted, 0, start),
            Event(2, AgentRunEventKind.ToolCallStarted, 0, start.AddSeconds(1), "call-1", "read"),
            Event(3, AgentRunEventKind.ToolCallCompleted, 0, start.AddSeconds(2), "call-1", "read", "succeeded"),
            Event(4, AgentRunEventKind.RunCompleted, 0, start.AddSeconds(3), outcome: "verified")
        ]);

        AgentRunTelemetry telemetry = projector.Snapshot.ToTelemetry();
        Assert.AreEqual("run", telemetry.RunId);
        Assert.AreEqual("Completed", telemetry.Status);
        Assert.AreEqual(1, telemetry.ToolCalls);
        Assert.AreEqual(1, telemetry.SuccessfulToolCalls);
        Assert.AreEqual(3000L, telemetry.ElapsedMilliseconds);
        Assert.AreEqual(4L, telemetry.LastSequence);
    }

    [TestMethod]
    public void Projection_BoundsRecentEvents_AndKeepsImmutableCopies()
    {
        var projector = new AgentRunSnapshotProjector("run", recentEventCapacity: 2);
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        projector.Apply(
        [
            Event(1, AgentRunEventKind.StepStarted, 1, start),
            Event(2, AgentRunEventKind.ToolCallStarted, 1, start.AddSeconds(1), "call-1", "read"),
            Event(3, AgentRunEventKind.ToolCallCompleted, 1, start.AddSeconds(2), "call-1", "read", "succeeded"),
        ]);

        ImmutableArray<AgentRunEvent> first = projector.Snapshot.RecentEvents;
        ImmutableArray<AgentRunEvent> second = projector.Snapshot.RecentEvents;
        Assert.HasCount(2, first);
        Assert.AreEqual(2L, first[0].Sequence);
        Assert.AreEqual(3L, first[1].Sequence);
        Assert.AreNotEqual(first, second);
    }

    [TestMethod]
    public void Projection_SortsBulkEvents_AndIgnoresLateOrDuplicateSequences()
    {
        var projector = new AgentRunSnapshotProjector("run");
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        projector.Apply(
        [
            Event(3, AgentRunEventKind.ToolCallCompleted, 1, start.AddSeconds(2), "call-1", "read", "succeeded"),
            Event(1, AgentRunEventKind.StepStarted, 1, start),
            Event(2, AgentRunEventKind.ToolCallStarted, 1, start.AddSeconds(1), "call-1", "read"),
        ]);
        projector.Apply(Event(2, AgentRunEventKind.RunPaused, 1, start.AddSeconds(3), outcome: "late"));

        AgentRunSnapshot snapshot = projector.Snapshot;
        Assert.AreEqual(AgentProjectionStatus.Running, snapshot.Status);
        Assert.AreEqual(3L, snapshot.LastSequence);
        Assert.AreEqual(1, snapshot.Metrics.ToolCalls);
        Assert.AreEqual(1, snapshot.Metrics.CompletedToolCalls);
        Assert.AreSequenceEqual([1, 2, 3], snapshot.RecentEvents.Select(item => item.Sequence));
    }
}
