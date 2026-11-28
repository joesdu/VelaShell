using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Agent.Policy;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Core.Runtime;

namespace VelaShell.Plugin.Ai.Chat.Runtime;

/// <summary>
/// Composes one host-independent Agent runtime for every entry point.
/// The desktop panel and Bridge only supply lifecycle, approval and persistence adapters.
/// </summary>
internal static class AgentRuntimeFactory
{
    internal static AgentRuntimeBundle Create(
        string runId,
        CommandJobRunScope runScope,
        ApprovalMode approvalMode,
        Func<ApprovalRequest, Task<bool>>? approvalHandler,
        Func<bool>? hasUnknownOutcome = null,
        Func<bool>? hasDeniedExecution = null,
        Action? markExecutionDenied = null,
        AgentToolbox? toolbox = null,
        IAgentPlanner? planner = null,
        IAgentExecutor? executor = null,
        IAgentVerifier? verifier = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        ArgumentNullException.ThrowIfNull(runScope);

        var events = new AgentRunEventLog();
        var snapshot = new AgentRunSnapshotProjector(runId);
        snapshot.AttachTo(events);
        var execution = new AgentExecution(runId, hasUnknownOutcome, hasDeniedExecution, events, snapshot);
        var coordinator = new AgentRunCoordinator(runScope, execution);
        var orchestration = new AgentRuntimeSession(runId, events);
        var policy = new AgentPolicyAdapter(approvalMode, approvalHandler,
            hasDeniedExecution, hasUnknownOutcome, markExecutionDenied ?? runScope.MarkExecutionDenied);
        var bundle = new AgentRuntimeBundle(execution, coordinator, orchestration, snapshot, policy, events,
            planner ?? new DefaultAgentPlanner(), executor ?? new DefaultAgentExecutor(),
            verifier ?? new DefaultAgentVerifier());
        toolbox?.PolicyGate = policy;
        return bundle;
    }
}

/// <summary>Shared runtime components owned by one Agent turn.</summary>
internal sealed class AgentRuntimeBundle : IDisposable
{
    private bool _planStarted;
    private bool _disposed;

    internal AgentRuntimeBundle(AgentExecution execution, AgentRunCoordinator coordinator,
        AgentRuntimeSession orchestration, AgentRunSnapshotProjector snapshot,
        AgentPolicyAdapter policy, AgentRunEventLog events,
        IAgentPlanner planner, IAgentExecutor executor, IAgentVerifier verifier)
    {
        Execution = execution;
        Coordinator = coordinator;
        Orchestration = orchestration;
        SnapshotProjector = snapshot;
        Policy = policy;
        Events = events;
        Planner = planner ?? throw new ArgumentNullException(nameof(planner));
        Executor = executor ?? throw new ArgumentNullException(nameof(executor));
        Verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
    }

    internal AgentExecution Execution { get; }
    internal AgentRunCoordinator Coordinator { get; }
    internal AgentRuntimeSession Orchestration { get; }
    internal AgentRunSnapshotProjector SnapshotProjector { get; }
    internal AgentPolicyAdapter Policy { get; }
    internal AgentRunEventLog Events { get; }
    internal IAgentPlanner Planner { get; }
    internal IAgentExecutor Executor { get; }
    internal IAgentVerifier Verifier { get; }

    internal AgentRunSnapshot Snapshot => SnapshotProjector.Snapshot;

    internal IAgentExecutionPort CreateExecutionPort(IEnumerable<AIFunction> tools)
        => new AgentExecutionPortAdapter(Execution, tools);

    /// <summary>Opt-in multi-step entry point; existing hosts may keep using the implicit path below.</summary>
    internal async ValueTask<AgentVerificationResult> PlanAndExecuteAsync(
        AgentPlanningRequest request, IEnumerable<AIFunction> tools,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(tools);
        if (!string.Equals(request.Descriptor.RunId, Execution.RunId, StringComparison.Ordinal))
            throw new ArgumentException("The planning request belongs to a different run.", nameof(request));

        using var port = new AgentExecutionPortAdapter(Execution, tools);
        return await Orchestration.PlanAndExecuteAsync(request, Planner, Executor, Verifier,
            port, cancellationToken).ConfigureAwait(false);
    }

    internal void BeginImplicitPlan(string goal)
    {
        if (_planStarted) return;
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        Orchestration.StartPlanning();
        Orchestration.AcceptPlan(new AgentPlan(goal,
            ["Complete the requested operation and provide an accurate response."],
            [new AgentPlanStep("main", goal, risk: "adaptive", maxAttempts: 1)],
            ["Stop when the request is complete, blocked, cancelled, or requires user input."]));
        Orchestration.BeginCurrentStep();
        _planStarted = true;
    }

    internal void CompleteWithVerification(IEnumerable<string>? evidence = null)
    {
        if (!_planStarted || Orchestration.State.Phase != AgentOrchestrationPhase.Executing) return;
        Orchestration.CompleteCurrentStep();
        Orchestration.BeginVerification();
        Orchestration.ApplyVerification(AgentVerificationResult.Pass(evidence));
    }

    internal void MarkCancelled()
    {
        if (!_planStarted || IsTerminal) return;
        Orchestration.Cancel();
    }

    internal void MarkFailure(string reason, bool pause)
    {
        if (!_planStarted || IsTerminal) return;
        if (pause) Orchestration.Pause(reason);
        else Orchestration.Fail(reason);
    }

    private bool IsTerminal => Orchestration.State.Phase is AgentOrchestrationPhase.Completed
        or AgentOrchestrationPhase.Failed or AgentOrchestrationPhase.Cancelled;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SnapshotProjector.DetachFrom(Events);
        SnapshotProjector.Dispose();
    }
}
