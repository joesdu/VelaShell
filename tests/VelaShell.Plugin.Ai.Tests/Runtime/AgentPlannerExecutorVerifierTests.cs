using VelaShell.Plugin.Ai.Chat.Runtime;
using VelaShell.Plugin.Ai.Core.Runtime;

namespace VelaShell.Plugin.Ai.Tests;

[TestClass]
public sealed class AgentPlannerExecutorVerifierTests
{
    [TestMethod]
    public async Task PlanAndExecute_RunsOrderedSteps_RetriesWithinMaxAttempts_AndVerifies()
    {
        AgentPlan plan = new("deliver", [],
        [
            new AgentPlanStep("inspect", "inspect", maxAttempts: 1),
            new AgentPlanStep("change", "change", maxAttempts: 2)
        ], []);
        var planner = new StubPlanner(plan);
        var executor = new RecordingExecutor();
        var verifier = new RecordingVerifier(AgentVerificationResult.Pass(["verified"]));
        var execution = new RecordingExecutionPort();
        var session = new AgentRuntimeSession("run-1");
        var request = new AgentPlanningRequest(
            new AgentRunDescriptor("conversation", "run-1", "deliver", DateTimeOffset.UtcNow), "deliver");

        AgentVerificationResult result = await session.PlanAndExecuteAsync(
            request, planner, executor, verifier, execution);

        Assert.IsTrue(result.Passed);
        Assert.AreSequenceEqual(["inspect", "change", "change"], executor.StepIds);
        Assert.HasCount(3, execution.BegunSteps);
        Assert.HasCount(2, verifier.Context!.StepResults);
        Assert.AreEqual(AgentOrchestrationPhase.Completed, session.State.Phase);
        Assert.HasCount(2, session.State.CompletedSteps);
    }

    [TestMethod]
    public async Task PlanAndExecute_CancellationTransitionsTheRunToCancelled()
    {
        AgentPlan plan = new("deliver", [], [new AgentPlanStep("main", "main")], []);
        var execution = new RecordingExecutionPort();
        var session = new AgentRuntimeSession("run-cancel");
        var request = new AgentPlanningRequest(
            new AgentRunDescriptor("conversation", "run-cancel", "deliver", DateTimeOffset.UtcNow), "deliver");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        bool cancelled = false;
        try
        {
            await session.PlanAndExecuteAsync(request, new StubPlanner(plan), new RecordingExecutor(),
                new RecordingVerifier(AgentVerificationResult.Pass()), execution, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        Assert.IsTrue(cancelled);
        Assert.AreEqual(AgentOrchestrationPhase.Cancelled, session.State.Phase);
        Assert.AreEqual(AgentRunStatus.Cancelled, execution.Status);
    }

    [TestMethod]
    public async Task PlanAndExecute_PausesWhenVerifierRejectsCompletedPlan()
    {
        AgentPlan plan = new("deliver", ["verified"],
            [new AgentPlanStep("main", "main")], []);
        var execution = new RecordingExecutionPort();
        var session = new AgentRuntimeSession("run-2");
        var verifier = new RecordingVerifier(AgentVerificationResult.Fail(
            [new AgentVerificationIssue("evidence", "required evidence is missing")]));
        var request = new AgentPlanningRequest(
            new AgentRunDescriptor("conversation", "run-2", "deliver", DateTimeOffset.UtcNow), "deliver");

        AgentVerificationResult result = await session.PlanAndExecuteAsync(
            request, new StubPlanner(plan), new RecordingExecutor(), verifier, execution);

        Assert.IsFalse(result.Passed);
        Assert.AreEqual(AgentOrchestrationPhase.Paused, session.State.Phase);
        Assert.AreEqual("VerificationFailed", session.State.PauseReason);
        Assert.HasCount(1, verifier.Context!.StepResults);
    }

    private sealed class StubPlanner(AgentPlan plan) : IAgentPlanner
    {
        public ValueTask<AgentPlan> PlanAsync(AgentPlanningRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(plan);
    }

    private sealed class RecordingExecutor : IAgentExecutor
    {
        private readonly Dictionary<string, int> _attempts = [with(StringComparer.Ordinal)];
        internal List<string> StepIds { get; } = [];

        public ValueTask<AgentStepExecutionResult> ExecuteAsync(AgentStepExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            StepIds.Add(context.Step.Id);
            int attempt = _attempts.TryGetValue(context.Step.Id, out int previous) ? previous + 1 : 1;
            _attempts[context.Step.Id] = attempt;
            if (context.Step.Id == "change" && attempt == 1)
                return ValueTask.FromResult(AgentStepExecutionResult.Failure("retryable"));
            return ValueTask.FromResult(AgentStepExecutionResult.Success(
                new AgentMessage(AgentMessageRole.Assistant, context.Step.Objective)));
        }
    }

    private sealed class RecordingVerifier(AgentVerificationResult result) : IAgentVerifier
    {
        internal AgentVerificationContext? Context { get; private set; }

        public ValueTask<AgentVerificationResult> VerifyAsync(AgentVerificationContext context,
            CancellationToken cancellationToken = default)
        {
            Context = context;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RecordingExecutionPort : IAgentExecutionPort
    {
        internal List<AgentStepInput> BegunSteps { get; } = [];

        public string RunId => "run-1";
        public AgentRunStatus Status { get; private set; } = AgentRunStatus.Created;
        public AgentExecutionSnapshot Snapshot => new(RunId, Status, [], []);
        public IReadOnlyList<AgentRuntimeEvent> Events => [];
        event Action<AgentRuntimeEvent>? IAgentExecutionPort.EventObserved
        {
            add { }
            remove { }
        }

        public void BeginStep(AgentStepInput step)
        {
            BegunSteps.Add(step);
            Status = AgentRunStatus.Running;
        }

        public ValueTask<AgentToolOutcome> InvokeAsync(AgentToolCall call,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentToolOutcome(call.CallId, call.ToolName,
                AgentToolOutcomeStatus.Succeeded));

        public void CompleteStep(AgentMessage assistantMessage)
            => Status = AgentRunStatus.Running;

        public void RecordNotification(AgentMessage notification) { }
        public void ContinueBatch() { }
        public void Pause(AgentPauseReason reason, string? summary = null)
            => Status = reason == AgentPauseReason.Cancelled ? AgentRunStatus.Cancelled : AgentRunStatus.Paused;
    }
}
