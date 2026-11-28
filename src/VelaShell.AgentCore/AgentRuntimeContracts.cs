using System.Collections.ObjectModel;

namespace VelaShell.Plugin.Ai.Core.Runtime;

/// <summary>Lifecycle status exposed to any Agent host.</summary>
public enum AgentRunStatus
{
    /// <summary>The run exists but has not begun execution.</summary>
    Created,
    /// <summary>The run is processing a step or waiting for its work.</summary>
    Running,
    /// <summary>Further execution is blocked pending a decision or state check.</summary>
    Paused,
    /// <summary>Execution has been cancelled; external side effects may still need verification.</summary>
    Cancelled,
    /// <summary>The host has finished the run normally.</summary>
    Completed
}

/// <summary>Reasons which stop a run before another side effect may be started.</summary>
public enum AgentPauseReason
{
    /// <summary>Policy or the approval decision prevented execution.</summary>
    PolicyDenied,
    /// <summary>A previous operation may already have taken effect; inspect its state before retrying.</summary>
    UnknownSideEffect,
    /// <summary>A call identifier was reused for a different invocation.</summary>
    CallIdentityConflict,
    /// <summary>Further steps appear to repeat work without advancing the task.</summary>
    NoProgress,
    /// <summary>A tool or execution step failed and cannot continue normally.</summary>
    ToolFailure,
    /// <summary>A cancellation interrupted execution.</summary>
    Cancelled,
    /// <summary>The hosting application stopped the run.</summary>
    HostStopped
}

/// <summary>What the host knows about the underlying operation after cancellation.</summary>
public enum AgentCancellationDisposition
{
    /// <summary>Cancellation was requested, without confirmation that work stopped.</summary>
    Requested,
    /// <summary>The local operation stopped; effects already applied are not rolled back.</summary>
    LocalOperationStopped,
    /// <summary>The caller cannot determine whether the external operation completed.</summary>
    OutcomeUnknown
}

/// <summary>Result category used to decide how a caller should handle a tool invocation.</summary>
public enum AgentToolOutcomeStatus
{
    /// <summary>The execution port returned a normal tool result.</summary>
    Succeeded,
    /// <summary>The arguments were rejected before the requested work could run.</summary>
    InvalidArguments,
    /// <summary>Execution reported an error rather than a normal result.</summary>
    Failed,
    /// <summary>Execution was cancelled; consult the cancellation disposition before retrying.</summary>
    Cancelled,
    /// <summary>The run stopped at an execution gate; consult its pause reason.</summary>
    Paused,
    /// <summary>No tool with the requested name is available to this run.</summary>
    UnknownTool
}

/// <summary>Milestones exposed by the execution port, distinct from planner phase transitions.</summary>
public enum AgentRuntimeEventKind
{
    /// <summary>The execution port began a step using its supplied messages.</summary>
    StepStarted,
    /// <summary>A tool invocation began; not evidence that its side effects completed.</summary>
    ToolCallStarted,
    /// <summary>A tool call returned an outcome that can be matched by call ID.</summary>
    ToolCallCompleted,
    /// <summary>An invocation ended in an execution error.</summary>
    ToolCallFailed,
    /// <summary>An invocation was interrupted by cancellation.</summary>
    ToolCallCancelled,
    /// <summary>The assistant response for a step was recorded.</summary>
    StepCompleted,
    /// <summary>A host notification was added to the run transcript.</summary>
    NotificationRecorded,
    /// <summary>The run started waiting for outstanding background work.</summary>
    BackgroundWaitStarted,
    /// <summary>A wait for background work ended; this is not the result of the tool itself.</summary>
    BackgroundWaitCompleted,
    /// <summary>The host continued the same run into another batch.</summary>
    BatchContinued,
    /// <summary>Execution stopped pending intervention or a state check.</summary>
    RunPaused,
    /// <summary>The run reached normal completion.</summary>
    RunCompleted
}

/// <summary>Origin of a message in a provider-independent transcript.</summary>
public enum AgentMessageRole
{
    /// <summary>Instructions supplied by the host.</summary>
    System,
    /// <summary>User input or a notification presented in the user role.</summary>
    User,
    /// <summary>A model reply, possibly including requests for tool calls.</summary>
    Assistant,
    /// <summary>One or more results returned to the model for earlier calls.</summary>
    Tool
}

/// <summary>A tool call as it crosses the host boundary. Arguments are canonical JSON.</summary>
public sealed record AgentToolCall
{
    /// <summary>Describes a requested invocation without dispatching it or parsing its arguments.</summary>
    /// <param name="callId">Invocation identity used to pair results and detect repeated calls.</param>
    /// <param name="toolName">Name used by the host to resolve a registered tool.</param>
    /// <param name="argumentsJson">Serialized argument object; validation occurs at the execution boundary.</param>
    public AgentToolCall(string callId, string toolName, string argumentsJson)
    {
        ArgumentException.ThrowIfNullOrEmpty(callId);
        ArgumentException.ThrowIfNullOrEmpty(toolName);
        ArgumentNullException.ThrowIfNull(argumentsJson);
        CallId = callId;
        ToolName = toolName;
        ArgumentsJson = argumentsJson;
    }

    /// <summary>Identifier shared by this request and its eventual result.</summary>
    public string CallId { get; }
    /// <summary>Name resolved against the tools available to this run.</summary>
    public string ToolName { get; }
    /// <summary>Argument payload for execution; not appropriate for telemetry.</summary>
    public string ArgumentsJson { get; }
}

/// <summary>A tool reply as it appears in the transcript.</summary>
/// <param name="CallId">Identifier of the invocation being answered.</param>
/// <param name="Text">Result content provided to the model; may contain sensitive output.</param>
public sealed record AgentToolResult(string CallId, string Text);

/// <summary>A provider-neutral transcript message used for checkpoints and resume.</summary>
/// <param name="Role">Source of the message.</param>
/// <param name="Text">Optional prose, separate from structured calls and results.</param>
/// <param name="Calls">Invocations requested by the assistant, in transcript order.</param>
/// <param name="Results">Replies linked to earlier invocations by call ID.</param>
public sealed record AgentMessage(
    AgentMessageRole Role,
    string? Text = null,
    IReadOnlyList<AgentToolCall>? Calls = null,
    IReadOnlyList<AgentToolResult>? Results = null);

/// <summary>Transcript supplied to the execution port at the start of a single step.</summary>
/// <param name="Messages">Messages in the order the executor should receive them.</param>
public sealed record AgentStepInput(IReadOnlyList<AgentMessage> Messages)
{
    /// <summary>Materializes a sequence so step execution does not enumerate it again.</summary>
    public AgentStepInput(IEnumerable<AgentMessage> messages) : this([.. messages]) => ArgumentNullException.ThrowIfNull(messages);
}

/// <summary>Identification and request metadata shared by planning, execution and persistence.</summary>
/// <param name="ConversationId">Conversation that owns the turn and its checkpoint.</param>
/// <param name="RunId">Identity of this particular run within the conversation.</param>
/// <param name="Task">User objective supplied to the run; not a telemetry label.</param>
/// <param name="StartedAt">Time the host began the run.</param>
/// <param name="ModelLabel">Optional display name, not a credential or routing key.</param>
public sealed record AgentRunDescriptor(
    string ConversationId,
    string RunId,
    string Task,
    DateTimeOffset StartedAt,
    string ModelLabel = "");

/// <summary>A lifecycle observation for one run; keep transcript content out of diagnostic fields.</summary>
/// <param name="Sequence">Ordering key within the run's event stream.</param>
/// <param name="RunId">Run to which the observation belongs.</param>
/// <param name="Timestamp">When the event was recorded.</param>
/// <param name="Kind">Lifecycle milestone that occurred.</param>
/// <param name="BatchStep">Execution step within the batch, not the index of a plan step.</param>
/// <param name="CallId">Invocation being observed, if this is a tool event.</param>
/// <param name="ToolName">Registered tool name, where applicable.</param>
/// <param name="Outcome">Short classification of the result, not tool output.</param>
/// <param name="DurationMilliseconds">Measured duration when available.</param>
/// <param name="Summary">Optional diagnostic description suitable for observers.</param>
/// <param name="Cancellation">What is known about an interrupted operation.</param>
public sealed record AgentRuntimeEvent(
    long Sequence,
    string RunId,
    DateTimeOffset Timestamp,
    AgentRuntimeEventKind Kind,
    int BatchStep,
    string? CallId = null,
    string? ToolName = null,
    string? Outcome = null,
    int? DurationMilliseconds = null,
    string? Summary = null,
    AgentCancellationDisposition? Cancellation = null);

/// <summary>Execution result for one invocation, including whether it is safe to assume work stopped.</summary>
/// <param name="CallId">Identity of the call being answered.</param>
/// <param name="ToolName">Tool named by the request.</param>
/// <param name="Status">Result category reported by the execution port.</param>
/// <param name="ResultText">Optional result for the transcript, not telemetry.</param>
/// <param name="Reused">Whether a previous result was returned without starting a new invocation.</param>
/// <param name="PauseReason">Reason the execution port stopped accepting work, when applicable.</param>
/// <param name="Cancellation">Known effect of cancellation on the underlying operation.</param>
/// <param name="Error">Optional detail explaining a rejected or unsuccessful invocation.</param>
public sealed record AgentToolOutcome(
    string CallId,
    string ToolName,
    AgentToolOutcomeStatus Status,
    string? ResultText = null,
    bool Reused = false,
    AgentPauseReason? PauseReason = null,
    AgentCancellationDisposition? Cancellation = null,
    string? Error = null);

/// <summary>Point-in-time view of the execution port for verification or persistence.</summary>
/// <param name="RunId">Run whose execution was captured.</param>
/// <param name="Status">Execution lifecycle status at capture.</param>
/// <param name="Messages">Captured transcript, potentially including private arguments and results.</param>
/// <param name="Events">Execution observations available at capture.</param>
public sealed record AgentExecutionSnapshot(
    string RunId,
    AgentRunStatus Status,
    IReadOnlyList<AgentMessage> Messages,
    IReadOnlyList<AgentRuntimeEvent> Events);

/// <summary>Portable recovery data for one run, distinct from its display-only telemetry.</summary>
/// <param name="Descriptor">Identity and original task needed to place the run in context.</param>
/// <param name="Status">Execution status at the time of the checkpoint.</param>
/// <param name="Messages">Saved messages and tool interactions used to reconstruct context.</param>
/// <param name="ProgressSummary">Compact account of work already done.</param>
/// <param name="Events">Optional diagnostic event history retained by the store.</param>
public sealed record AgentRunCheckpoint(
    AgentRunDescriptor Descriptor,
    AgentRunStatus Status,
    IReadOnlyList<AgentMessage> Messages,
    string ProgressSummary = "",
    IReadOnlyList<AgentRuntimeEvent>? Events = null);

/// <summary>Context prepared for resumption; restoring a checkpoint does not execute any tools.</summary>
/// <param name="Checkpoint">Recovery record associated with the restored run.</param>
/// <param name="Messages">Recent messages prepared for the next model request.</param>
/// <param name="EarlierSteps">Account of older work omitted from the verbatim context.</param>
/// <param name="RequiresStateVerification">Whether pending or unresolved work must be checked before repeating operations.</param>
public sealed record AgentResumePlan(
    AgentRunCheckpoint Checkpoint,
    IReadOnlyList<AgentMessage> Messages,
    string EarlierSteps,
    bool RequiresStateVerification);

/// <summary>Execution port shared by desktop, Bridge, MCP and future application hosts.</summary>
public interface IAgentExecutionPort
{
    /// <summary>Identity of the run to which all calls through this port belong.</summary>
    string RunId { get; }
    /// <summary>Current execution status, distinct from the plan's orchestration phase.</summary>
    AgentRunStatus Status { get; }
    /// <summary>Captures execution state and evidence for a verifier or checkpoint writer.</summary>
    AgentExecutionSnapshot Snapshot { get; }
    /// <summary>Execution events currently retained for this run.</summary>
    IReadOnlyList<AgentRuntimeEvent> Events { get; }
    /// <summary>Notifies subscribers of new execution events without requiring a snapshot poll.</summary>
    event Action<AgentRuntimeEvent>? EventObserved;

    /// <summary>Starts a step with the supplied input transcript.</summary>
    void BeginStep(AgentStepInput step);
    /// <summary>Dispatches a named tool call through the execution ledger and returns its outcome.</summary>
    ValueTask<AgentToolOutcome> InvokeAsync(AgentToolCall call, CancellationToken cancellationToken = default);
    /// <summary>Records the assistant response that closes the current step, not the entire run.</summary>
    void CompleteStep(AgentMessage assistantMessage);
    /// <summary>Adds a host or background notification to the execution transcript.</summary>
    void RecordNotification(AgentMessage notification);
    /// <summary>Opens another batch of steps in the same run rather than creating a new run identity.</summary>
    void ContinueBatch();
    /// <summary>Stops further execution and records the reason and optional diagnostic summary.</summary>
    void Pause(AgentPauseReason reason, string? summary = null);
}

/// <summary>Persistence port. Implementations decide where checkpoint data is stored.</summary>
public interface IAgentRunCheckpointStore
{
    /// <summary>Persists the supplied recovery data for its conversation.</summary>
    ValueTask SaveAsync(AgentRunCheckpoint checkpoint, CancellationToken cancellationToken = default);
    /// <summary>Loads recovery data for the conversation, or returns null if none is available.</summary>
    ValueTask<AgentRunCheckpoint?> LoadAsync(string conversationId, CancellationToken cancellationToken = default);
    /// <summary>Removes the saved checkpoint, not the conversation history.</summary>
    ValueTask DeleteAsync(string conversationId, CancellationToken cancellationToken = default);
    /// <summary>Reconstructs context and identifies unresolved work without reissuing tool calls.</summary>
    AgentResumePlan Restore(AgentRunCheckpoint checkpoint);
}

/// <summary>Host lifecycle and observation boundary; it deliberately contains no UI or prompt policy.</summary>
public interface IAgentRunHost : IAsyncDisposable
{
    /// <summary>Identity and original request for the run owned by this host.</summary>
    AgentRunDescriptor Descriptor { get; }
    /// <summary>Port through which the host records steps and dispatches tool calls.</summary>
    IAgentExecutionPort Execution { get; }
    /// <summary>Lifecycle state reported to callers of this host.</summary>
    AgentRunStatus Status { get; }
    /// <summary>Raised when a new runtime observation is available.</summary>
    event Action<AgentRuntimeEvent>? EventObserved;

    /// <summary>Saves the current run's recovery data using the host's persistence policy.</summary>
    ValueTask SaveCheckpointAsync(CancellationToken cancellationToken = default);
    /// <summary>Prepares recovery context for this conversation, or returns null if none exists.</summary>
    ValueTask<AgentResumePlan?> LoadResumeAsync(CancellationToken cancellationToken = default);
    /// <summary>Suspends the run with a reason the host can surface to its user.</summary>
    ValueTask PauseAsync(AgentPauseReason reason, string? summary = null, CancellationToken cancellationToken = default);
    /// <summary>Cancels the run, distinguishing a stop request from a known final state of the operation.</summary>
    ValueTask CancelAsync(AgentCancellationDisposition disposition = AgentCancellationDisposition.Requested,
        CancellationToken cancellationToken = default);
    /// <summary>Signals normal completion of the hosted run.</summary>
    ValueTask CompleteAsync(CancellationToken cancellationToken = default);
    /// <summary>Observes lifecycle events asynchronously; the token controls observation.</summary>
    IAsyncEnumerable<AgentRuntimeEvent> ObserveAsync(CancellationToken cancellationToken = default);
}

internal static class AgentRuntimeReadOnly
{
    internal static IReadOnlyList<T> Copy<T>(IEnumerable<T> values) =>
        new ReadOnlyCollection<T>([.. values]);
}
