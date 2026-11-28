namespace VelaShell.Plugin.Ai.Core.Runtime;

/// <summary>Host-neutral input supplied to a planner for one Agent run.</summary>
public sealed record AgentPlanningRequest
{
    /// <summary>Captures the task and conversation available before a plan is chosen.</summary>
    /// <param name="descriptor">Identifies the run and its conversation.</param>
    /// <param name="task">Nonblank request the planner should turn into steps.</param>
    /// <param name="messages">Conversation messages, copied in the supplied order; null means no history.</param>
    public AgentPlanningRequest(AgentRunDescriptor descriptor, string task,
        IReadOnlyList<AgentMessage>? messages = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (string.IsNullOrWhiteSpace(task)) throw new ArgumentException("A planning request requires a task.", nameof(task));
        Descriptor = descriptor;
        Task = task;
        Messages = messages is null ? [] : [.. messages];
    }

    /// <summary>Identifies the run for which the plan is being prepared.</summary>
    public AgentRunDescriptor Descriptor { get; }
    /// <summary>The requested task, supplied separately from the conversation history.</summary>
    public string Task { get; }
    /// <summary>Conversation history in caller-supplied order; empty when no messages were supplied.</summary>
    public IReadOnlyList<AgentMessage> Messages { get; }
}

/// <summary>Produces an auditable, ordered plan without performing side effects.</summary>
public interface IAgentPlanner
{
    /// <summary>Describes how to accomplish the task without executing any of its steps.</summary>
    /// <param name="request">The run, task, and ordered conversation history to plan from.</param>
    /// <param name="cancellationToken">Signals that planning should stop.</param>
    /// <returns>A plan whose steps are listed in execution order.</returns>
    ValueTask<AgentPlan> PlanAsync(AgentPlanningRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Context passed to an executor for one plan step.</summary>
public sealed record AgentStepExecutionContext
{
    /// <summary>Collects the inputs for one attempt without starting the step or invoking a tool.</summary>
    /// <param name="descriptor">Identifies the run in which the attempt takes place.</param>
    /// <param name="plan">The accepted plan containing the intended step.</param>
    /// <param name="step">The selected step; membership in the plan is not checked here.</param>
    /// <param name="attempt">One-based attempt number; not checked against the step's budget.</param>
    /// <param name="input">Messages to supply to this attempt.</param>
    /// <param name="execution">The run's port for operations such as invoking tools.</param>
    public AgentStepExecutionContext(AgentRunDescriptor descriptor, AgentPlan plan,
        AgentPlanStep step, int attempt, AgentStepInput input, IAgentExecutionPort execution)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempt);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(execution);
        Descriptor = descriptor;
        Plan = plan;
        Step = step;
        Attempt = attempt;
        Input = input;
        Execution = execution;
    }

    /// <summary>Identifies the run that owns this attempt.</summary>
    public AgentRunDescriptor Descriptor { get; }
    /// <summary>The overall plan against which the executor interprets the selected step.</summary>
    public AgentPlan Plan { get; }
    /// <summary>The step to execute, including its objective and verification items.</summary>
    public AgentPlanStep Step { get; }
    /// <summary>The attempt number for this step, starting at one rather than zero.</summary>
    public int Attempt { get; }
    /// <summary>The message input for this particular attempt.</summary>
    public AgentStepInput Input { get; }
    /// <summary>The live execution port, not a snapshot of earlier activity.</summary>
    public IAgentExecutionPort Execution { get; }
}

/// <summary>Host-neutral result returned after an executor handles one plan step.</summary>
public sealed record AgentStepExecutionResult
{
    /// <summary>Records the executor's outcome without inferring success from the message or tool results.</summary>
    /// <param name="succeeded">Whether the executor considers the step successful.</param>
    /// <param name="assistantMessage">Optional response produced by the step.</param>
    /// <param name="toolOutcomes">Tool results, copied in supplied order; null becomes an empty list.</param>
    /// <param name="summary">Optional account of the outcome, even when constructing a failed result directly.</param>
    public AgentStepExecutionResult(bool succeeded, AgentMessage? assistantMessage = null,
        IReadOnlyList<AgentToolOutcome>? toolOutcomes = null, string? summary = null)
    {
        Succeeded = succeeded;
        AssistantMessage = assistantMessage;
        ToolOutcomes = toolOutcomes is null ? [] : [.. toolOutcomes];
        Summary = summary;
    }

    /// <summary>Whether this step reported success, independently of the verifier's decision on the whole plan.</summary>
    public bool Succeeded { get; }
    /// <summary>The response produced by this step, or null if none was supplied.</summary>
    public AgentMessage? AssistantMessage { get; }
    /// <summary>Reported tool outcomes in the order supplied by the executor.</summary>
    public IReadOnlyList<AgentToolOutcome> ToolOutcomes { get; }
    /// <summary>An optional explanation of the step outcome, distinct from its assistant response.</summary>
    public string? Summary { get; }

    /// <summary>Reports a successful step, even if no message or tool results were produced.</summary>
    /// <param name="assistantMessage">Optional assistant response to retain.</param>
    /// <param name="toolOutcomes">Tool results to retain in enumeration order; null means none.</param>
    /// <param name="summary">Optional explanation of what the step accomplished.</param>
    /// <returns>A step result with <see cref="Succeeded"/> set to true.</returns>
    public static AgentStepExecutionResult Success(AgentMessage? assistantMessage = null,
        IEnumerable<AgentToolOutcome>? toolOutcomes = null, string? summary = null)
        => new(true, assistantMessage, toolOutcomes is null ? [] : [.. toolOutcomes], summary);

    /// <summary>Reports an unsuccessful step and preserves any tool results obtained before failure.</summary>
    /// <param name="summary">Nonblank explanation of why the step failed.</param>
    /// <param name="toolOutcomes">Tool results to retain in enumeration order; null means none.</param>
    /// <returns>A failed result with no assistant message.</returns>
    public static AgentStepExecutionResult Failure(string summary,
        IEnumerable<AgentToolOutcome>? toolOutcomes = null)
    {
        if (string.IsNullOrWhiteSpace(summary)) throw new ArgumentException("A failed step requires a summary.", nameof(summary));
        return new(false, null, toolOutcomes is null ? [] : [.. toolOutcomes], summary);
    }
}

/// <summary>Executes one already-planned step. Planning and verification remain separate concerns.</summary>
public interface IAgentExecutor
{
    /// <summary>Handles the selected attempt without advancing to the next step or verifying the whole plan.</summary>
    /// <param name="context">The selected step, attempt number, message input, and execution port.</param>
    /// <param name="cancellationToken">Signals that execution of this attempt should stop.</param>
    /// <returns>The executor's outcome for this attempt, including any reported tool results.</returns>
    ValueTask<AgentStepExecutionResult> ExecuteAsync(AgentStepExecutionContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Evidence supplied to a verifier after every planned step has completed.</summary>
public sealed record AgentVerificationContext
{
    /// <summary>Collects evidence for verification without deciding whether the plan passed.</summary>
    /// <param name="descriptor">Identifies the run under review.</param>
    /// <param name="plan">The plan whose steps and acceptance criteria are being assessed.</param>
    /// <param name="execution">Run status, messages, and events captured for review.</param>
    /// <param name="stepResults">Results to review, copied in supplied order; cannot be null but need not match the step count.</param>
    public AgentVerificationContext(AgentRunDescriptor descriptor, AgentPlan plan,
        AgentExecutionSnapshot execution, IReadOnlyList<AgentStepExecutionResult> stepResults)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(stepResults);
        Descriptor = descriptor;
        Plan = plan;
        Execution = execution;
        StepResults = [.. stepResults];
    }

    /// <summary>Identifies the run that produced the evidence.</summary>
    public AgentRunDescriptor Descriptor { get; }
    /// <summary>The accepted plan to compare with the reported outcomes.</summary>
    public AgentPlan Plan { get; }
    /// <summary>Recorded execution state for inspection rather than a port for issuing new operations.</summary>
    public AgentExecutionSnapshot Execution { get; }
    /// <summary>Step outcomes in the caller's order; the context does not sort or match them to step IDs.</summary>
    public IReadOnlyList<AgentStepExecutionResult> StepResults { get; }
}

/// <summary>Checks the completed plan independently of the executor.</summary>
public interface IAgentVerifier
{
    /// <summary>Assesses the completed plan from its recorded execution and step results.</summary>
    /// <param name="context">The accepted plan and evidence to review.</param>
    /// <param name="cancellationToken">Signals that verification should stop.</param>
    /// <returns>The verdict to apply to the run: a pass completes it and a failed check pauses it.</returns>
    ValueTask<AgentVerificationResult> VerifyAsync(AgentVerificationContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Default planner that preserves the existing implicit single-step behavior.</summary>
public sealed class DefaultAgentPlanner : IAgentPlanner
{
    /// <inheritdoc/>
    /// <remarks>After checking cancellation, uses the task as both the goal and the objective of a single
    /// <c>main</c> step with adaptive risk and one attempt. Conversation history does not affect this plan.</remarks>
    public ValueTask<AgentPlan> PlanAsync(AgentPlanningRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AgentPlan(request.Task,
            ["Complete the requested operation and provide an accurate response."],
            [new AgentPlanStep("main", request.Task, risk: "adaptive", maxAttempts: 1)],
            ["Stop when the request is complete, blocked, cancelled, or requires user input."]));
    }
}

/// <summary>Safe default for hosts that only need lifecycle and verification plumbing.</summary>
public sealed class DefaultAgentExecutor : IAgentExecutor
{
    /// <inheritdoc/>
    /// <remarks>After checking cancellation, returns the step objective as an assistant message and reports
    /// success without using the execution port. This does not demonstrate that the objective was achieved.</remarks>
    public ValueTask<AgentStepExecutionResult> ExecuteAsync(AgentStepExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(AgentStepExecutionResult.Success(
            new AgentMessage(AgentMessageRole.Assistant, context.Step.Objective),
            summary: "The planned step completed."));
    }
}

/// <summary>Default verifier that requires every planned step to report success.</summary>
public sealed class DefaultAgentVerifier : IAgentVerifier
{
    /// <inheritdoc/>
    /// <remarks>After checking cancellation, reports <c>StepIncomplete</c> if the result count differs from the
    /// step count or any result is unsuccessful. Otherwise it passes, without checking step identity or tool evidence.</remarks>
    public ValueTask<AgentVerificationResult> VerifyAsync(AgentVerificationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (context.StepResults.Count != context.Plan.Steps.Count || context.StepResults.Any(result => !result.Succeeded))
        {
            return ValueTask.FromResult(AgentVerificationResult.Fail(
                [new AgentVerificationIssue("StepIncomplete", "One or more planned steps did not complete.")],
                ["The default verifier requires one successful result for every planned step."]));
        }

        return ValueTask.FromResult(AgentVerificationResult.Pass(
            ["Every planned step reported success."]));
    }
}
