using VelaShell.Plugin.Ai.Core.Runtime;

namespace VelaShell.Plugin.Ai.Chat.Runtime;

/// <summary>
/// 宿主无关的 Agent 编排会话。桌面、Bridge、MCP 和未来 APP 可以共享同一套阶段与事件语义，
/// 而不需要直接依赖 UI 状态或模型客户端。
/// </summary>
internal sealed class AgentRuntimeSession
{
    private readonly Lock _gate = new();
    private AgentOrchestrationState _state;

    internal AgentRuntimeSession(string runId, AgentRunEventLog? events = null)
    {
        Events = events ?? new AgentRunEventLog();
        _state = AgentOrchestrationState.Create(runId);
    }

    internal AgentOrchestrationState State
    {
        get { lock (_gate) return _state; }
    }

    internal AgentRunEventLog Events { get; }

    internal event Action<AgentOrchestrationState>? StateChanged;

    internal AgentOrchestrationState StartPlanning()
        => Transition(state => state.StartPlanning(), AgentRunEventKind.PlanStarted, summary: "planning started");

    internal AgentOrchestrationState AcceptPlan(AgentPlan plan)
        => Transition(state => state.AcceptPlan(plan), AgentRunEventKind.PlanAccepted,
            summary: $"plan accepted with {plan.Steps.Count} steps");

    internal AgentOrchestrationState BeginCurrentStep()
    {
        AgentOrchestrationState state = Transition(current => current.BeginCurrentStep(), AgentRunEventKind.StepStarted);
        return state;
    }

    internal AgentOrchestrationState CompleteCurrentStep()
        => Transition(state => state.CompleteCurrentStep(), AgentRunEventKind.StepCompleted,
            outcome: "plan step completed");

    internal AgentOrchestrationState BeginVerification()
        => Transition(state => state.Phase == AgentOrchestrationPhase.Verifying
            ? state
            : throw new InvalidOperationException("Verification can begin only after all plan steps complete."),
            AgentRunEventKind.VerificationStarted);

    internal AgentOrchestrationState ApplyVerification(AgentVerificationResult result)
    {
        AgentOrchestrationState state = Transition(current => current.ApplyVerification(result),
            AgentRunEventKind.VerificationCompleted, result.Passed ? "passed" : "failed");
        if (state.Phase == AgentOrchestrationPhase.Completed)
            Events.Append(state.RunId, AgentRunEventKind.RunCompleted, state.CurrentStepIndex, outcome: "verified");
        return state;
    }

    /// <summary>
    /// Runs an injected planner, executor and verifier through every ordered plan step.
    /// The existing chat loop can continue using its implicit single-step path; hosts that
    /// opt into this method get explicit multi-step lifecycle semantics.
    /// </summary>
    internal async ValueTask<AgentVerificationResult> PlanAndExecuteAsync(
        AgentPlanningRequest request,
        IAgentPlanner planner,
        IAgentExecutor executor,
        IAgentVerifier verifier,
        IAgentExecutionPort execution,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(execution);

        try
        {
            StartPlanning();
            AgentPlan plan = await planner.PlanAsync(request, cancellationToken).ConfigureAwait(false);
            AcceptPlan(plan);
            List<AgentStepExecutionResult> stepResults = [];

            while (State.Phase == AgentOrchestrationPhase.Executing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AgentPlanStep step = State.Plan!.Steps[State.CurrentStepIndex];
                AgentStepInput input = new([.. request.Messages,
                new AgentMessage(AgentMessageRole.User, step.Objective)]);
                AgentStepExecutionResult? result = null;

                for (int attempt = 0; attempt < step.MaxAttempts; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AgentOrchestrationState state = BeginCurrentStep();
                    execution.BeginStep(input);
                    try
                    {
                        result = await executor.ExecuteAsync(
                            new AgentStepExecutionContext(request.Descriptor, plan, step,
                                state.CurrentAttempt, input, execution), cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        execution.Pause(AgentPauseReason.Cancelled, "The plan execution was cancelled.");
                        Cancel();
                        throw;
                    }
                    catch (Exception ex)
                    {
                        execution.Pause(AgentPauseReason.ToolFailure, ex.Message);
                        Pause("ExecutionFailed");
                        return AgentVerificationResult.Fail(
                            [new AgentVerificationIssue("ExecutionFailed", ex.Message)]);
                    }

                    if (result.Succeeded) break;
                }

                if (result is null || !result.Succeeded)
                {
                    string summary = result?.Summary ?? $"Plan step '{step.Id}' did not complete.";
                    execution.Pause(AgentPauseReason.ToolFailure, summary);
                    Pause("ExecutionFailed");
                    return AgentVerificationResult.Fail(
                        [new AgentVerificationIssue("MaxAttemptsExceeded", summary)]);
                }

                stepResults.Add(result);
                execution.CompleteStep(result.AssistantMessage ??
                    new AgentMessage(AgentMessageRole.Assistant, result.Summary ?? step.Objective));
                CompleteCurrentStep();
            }

            BeginVerification();
            AgentVerificationResult verification = await verifier.VerifyAsync(
                new AgentVerificationContext(request.Descriptor, plan, execution.Snapshot, stepResults),
                cancellationToken).ConfigureAwait(false);
            ApplyVerification(verification);
            return verification;
        }
        catch (OperationCanceledException)
        {
            if (State.Phase is not AgentOrchestrationPhase.Completed
                and not AgentOrchestrationPhase.Failed
                and not AgentOrchestrationPhase.Cancelled)
            {
                execution.Pause(AgentPauseReason.Cancelled, "The plan execution was cancelled.");
                Cancel();
            }
            throw;
        }
    }

    internal AgentOrchestrationState Pause(string reason)
        => Transition(state => state.Pause(reason), AgentRunEventKind.RunPaused, outcome: reason);

    internal AgentOrchestrationState Fail(string reason)
        => Transition(state => state.Fail(reason), AgentRunEventKind.RunFailed, outcome: reason);

    internal AgentOrchestrationState Cancel()
        => Transition(state => state.Cancel(), AgentRunEventKind.RunCancelled, outcome: "cancelled");

    private AgentOrchestrationState Transition(Func<AgentOrchestrationState, AgentOrchestrationState> transition,
        AgentRunEventKind eventKind, string? outcome = null, string? summary = null)
    {
        AgentOrchestrationState updated;
        lock (_gate) updated = _state = transition(_state);
        Events.Append(updated.RunId, eventKind, Math.Max(0, updated.CurrentStepIndex), outcome: outcome, summary: summary);
        try { StateChanged?.Invoke(updated); }
        catch (Exception) { }
        return updated;
    }
}
