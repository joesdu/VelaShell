using System.Text.Json;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Chat;

namespace VelaShell.Plugin.Ai.Core.Runtime;

/// <summary>
/// Boundary adapter for the existing AgentExecution ledger. The model loop remains unchanged;
/// hosts use this port for lifecycle, observation, deduplication and safe resume data.
/// </summary>
/// <remarks>
/// This adapter is assembly-internal because the existing execution engine is intentionally
/// internal. Hosts consume <see cref="IAgentExecutionPort"/> instead of depending on this type.
/// </remarks>
internal sealed class AgentExecutionPortAdapter : IAgentExecutionPort, IDisposable
{
    private readonly AgentExecution _execution;
    private readonly Dictionary<string, AIFunction> _tools;
    private readonly Lock _gate = new();
    private readonly List<AgentRuntimeEvent> _events = [];
    private AgentPauseReason? _pauseReason;
    private long _nextSequence;
    private bool _disposed;

    public AgentExecutionPortAdapter(AgentExecution execution, IEnumerable<AIFunction> tools)
    {
        _execution = execution ?? throw new ArgumentNullException(nameof(execution));
        ArgumentNullException.ThrowIfNull(tools);
        _tools = tools.GroupBy(tool => tool.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        _execution.EventRecorded += OnExistingEvent;
    }

    public string RunId => _execution.RunId;

    public AgentRunStatus Status
    {
        get { lock (_gate) return field; }

        private set;
    } = AgentRunStatus.Created;

    public AgentExecutionSnapshot Snapshot
    {
        get
        {
            IReadOnlyList<AgentMessage> messages = [.. RunCheckpointStore.Capture(_execution.CompletedMessages).Select(ToAgentMessage)];
            return new(RunId, Status, messages, Events);
        }
    }

    public IReadOnlyList<AgentRuntimeEvent> Events
    {
        get { lock (_gate) return [.. _events]; }
    }

    public event Action<AgentRuntimeEvent>? EventObserved;

    public void BeginStep(AgentStepInput step)
    {
        ArgumentNullException.ThrowIfNull(step);
        EnsureAvailable();
        _execution.BeginStep(step.Messages.Select(ToChatMessage));
        lock (_gate) Status = AgentRunStatus.Running;
    }

    public async ValueTask<AgentToolOutcome> InvokeAsync(AgentToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        EnsureAvailable();
        if (!_tools.TryGetValue(call.ToolName, out AIFunction? function))
        {
            return new(call.CallId, call.ToolName, AgentToolOutcomeStatus.UnknownTool,
                Error: $"Tool '{call.ToolName}' is not available to this run.");
        }

        Dictionary<string, object?> arguments;
        try
        {
            arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(call.ArgumentsJson)
                ?.ToDictionary(item => item.Key, item => (object?)item.Value, StringComparer.Ordinal)
                ?? [];
        }
        catch (JsonException ex)
        {
            return new(call.CallId, call.ToolName, AgentToolOutcomeStatus.InvalidArguments,
                Error: $"Arguments are not a JSON object: {ex.Message}");
        }

        long previousSequence;
        lock (_gate) previousSequence = _nextSequence;
        var context = new FunctionInvocationContext
        {
            Function = function,
            Arguments = [with(arguments)],
            CallContent = new FunctionCallContent(call.CallId, call.ToolName, arguments)
        };

        try
        {
            object? result = await _execution.InvokeAsync(context, cancellationToken).ConfigureAwait(false);
            AgentRuntimeEvent? completed = LastEventAfter(previousSequence, call.CallId);
            AgentToolOutcomeStatus status = completed?.Outcome == "invalid_arguments"
                ? AgentToolOutcomeStatus.InvalidArguments
                : AgentToolOutcomeStatus.Succeeded;
            bool reused = !HasNewEvent(previousSequence, AgentRuntimeEventKind.ToolCallStarted, call.CallId);
            return new(call.CallId, call.ToolName, status, ToText(result), reused,
                Error: status == AgentToolOutcomeStatus.InvalidArguments ? "The tool did not run." : null);
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                Status = AgentRunStatus.Cancelled;
                _pauseReason = AgentPauseReason.Cancelled;
            }
            AgentCancellationDisposition disposition = call.ToolName == "run_local_command"
                ? AgentCancellationDisposition.LocalOperationStopped
                : AgentCancellationDisposition.OutcomeUnknown;
            return new(call.CallId, call.ToolName, AgentToolOutcomeStatus.Cancelled,
                Cancellation: disposition, PauseReason: AgentPauseReason.Cancelled,
                Error: disposition == AgentCancellationDisposition.OutcomeUnknown
                    ? "The operation was cancelled; its side effects are unknown."
                    : "The local operation was cancelled and stopped.");
        }
        catch (AgentPausedException ex)
        {
            AgentPauseReason reason = ToPauseReason(ex.ReasonKey, call.CallId);
            lock (_gate)
            {
                Status = AgentRunStatus.Paused;
                _pauseReason = reason;
            }
            return new(call.CallId, call.ToolName, AgentToolOutcomeStatus.Paused,
                PauseReason: reason, Error: ex.Message);
        }
        catch (Exception ex)
        {
            AgentPauseReason reason = LastEventAfter(previousSequence, call.CallId)?.Outcome == "CallIdArgumentsChanged"
                ? AgentPauseReason.CallIdentityConflict
                : AgentPauseReason.ToolFailure;
            lock (_gate)
            {
                Status = AgentRunStatus.Paused;
                _pauseReason = reason;
            }
            return new(call.CallId, call.ToolName, AgentToolOutcomeStatus.Failed,
                PauseReason: reason, Error: ex.Message);
        }
    }

    public void CompleteStep(AgentMessage assistantMessage)
    {
        ArgumentNullException.ThrowIfNull(assistantMessage);
        EnsureAvailable();
        _execution.CompleteStep(new ChatResponse([ToChatMessage(assistantMessage)]));
        lock (_gate) Status = AgentRunStatus.Running;
    }

    public void RecordNotification(AgentMessage notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        EnsureAvailable();
        _execution.RecordNotification(ToChatMessage(notification));
    }

    public void ContinueBatch()
    {
        EnsureAvailable();
        _execution.ContinueBatch();
    }

    public void Pause(AgentPauseReason reason, string? summary = null)
    {
        lock (_gate)
        {
            _pauseReason = reason;
            Status = reason == AgentPauseReason.Cancelled ? AgentRunStatus.Cancelled : AgentRunStatus.Paused;
        }
        Publish(new AgentRuntimeEvent(NextSequence(), RunId, DateTimeOffset.UtcNow,
            AgentRuntimeEventKind.RunPaused, 0, OutcomeFor(reason), Summary: summary));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _execution.EventRecorded -= OnExistingEvent;
    }

    private void EnsureAvailable()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pauseReason is { } reason)
                throw new InvalidOperationException($"Agent run is paused: {reason}.");
        }
    }

    private void OnExistingEvent(AgentRunEvent item)
    {
        AgentRuntimeEvent mapped = new(item.Sequence, item.RunId, item.Timestamp, MapKind(item.Kind), item.BatchStep,
            item.CallId, item.ToolName, item.Outcome, item.DurationMilliseconds, item.Summary,
            item.Kind == AgentRunEventKind.ToolCallCancelled ? CancellationFor(item.ToolName) : null);
        lock (_gate)
        {
            _nextSequence = Math.Max(_nextSequence, mapped.Sequence);
            _events.Add(mapped);
            if (mapped.Kind == AgentRuntimeEventKind.RunPaused) Status = AgentRunStatus.Paused;
            else if (mapped.Kind == AgentRuntimeEventKind.StepStarted) Status = AgentRunStatus.Running;
        }
        Publish(mapped, alreadyRecorded: true);
    }

    private void Publish(AgentRuntimeEvent item, bool alreadyRecorded = false)
    {
        if (!alreadyRecorded)
        {
            lock (_gate)
            {
                _events.Add(item);
                _nextSequence = Math.Max(_nextSequence, item.Sequence);
            }
        }
        try { EventObserved?.Invoke(item); }
        catch (Exception) { }
    }

    private long NextSequence() => Interlocked.Increment(ref _nextSequence);

    private AgentRuntimeEvent? LastEventAfter(long sequence, string callId)
    {
        lock (_gate) return _events.LastOrDefault(item => item.Sequence > sequence && item.CallId == callId);
    }

    private bool HasNewEvent(long sequence, AgentRuntimeEventKind kind, string callId)
    {
        lock (_gate) return _events.Any(item => item.Sequence > sequence && item.Kind == kind && item.CallId == callId);
    }

    private static AgentRuntimeEventKind MapKind(AgentRunEventKind kind) => kind switch
    {
        AgentRunEventKind.StepStarted => AgentRuntimeEventKind.StepStarted,
        AgentRunEventKind.ToolCallStarted => AgentRuntimeEventKind.ToolCallStarted,
        AgentRunEventKind.ToolCallCompleted => AgentRuntimeEventKind.ToolCallCompleted,
        AgentRunEventKind.ToolCallFailed => AgentRuntimeEventKind.ToolCallFailed,
        AgentRunEventKind.ToolCallCancelled => AgentRuntimeEventKind.ToolCallCancelled,
        AgentRunEventKind.StepCompleted => AgentRuntimeEventKind.StepCompleted,
        AgentRunEventKind.NotificationRecorded => AgentRuntimeEventKind.NotificationRecorded,
        AgentRunEventKind.BackgroundWaitStarted => AgentRuntimeEventKind.BackgroundWaitStarted,
        AgentRunEventKind.BackgroundWaitCompleted => AgentRuntimeEventKind.BackgroundWaitCompleted,
        AgentRunEventKind.BatchContinued => AgentRuntimeEventKind.BatchContinued,
        AgentRunEventKind.RunPaused => AgentRuntimeEventKind.RunPaused,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static AgentPauseReason ToPauseReason(string key, string callId) => key switch
    {
        "ExecutionDenied" => AgentPauseReason.PolicyDenied,
        "TaskUnknown" => AgentPauseReason.UnknownSideEffect,
        "NoProgress" => AgentPauseReason.NoProgress,
        "CallIdArgumentsChanged" => AgentPauseReason.CallIdentityConflict,
        _ => AgentPauseReason.ToolFailure
    };

    private static string OutcomeFor(AgentPauseReason reason) => reason switch
    {
        AgentPauseReason.PolicyDenied => "ExecutionDenied",
        AgentPauseReason.UnknownSideEffect => "TaskUnknown",
        AgentPauseReason.CallIdentityConflict => "CallIdArgumentsChanged",
        AgentPauseReason.NoProgress => "NoProgress",
        AgentPauseReason.Cancelled => "Cancelled",
        _ => "ToolFailure"
    };

    private static AgentCancellationDisposition? CancellationFor(string? toolName)
        => toolName == "run_local_command"
            ? AgentCancellationDisposition.LocalOperationStopped
            : AgentCancellationDisposition.OutcomeUnknown;

    private static string? ToText(object? value) => value switch
    {
        null => null,
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        JsonElement element => element.GetRawText(),
        _ => value.ToString()
    };

    private static AgentMessage ToAgentMessage(RunCheckpointMessage message)
        => new(ParseRole(message.Role), message.Text,
            message.Calls?.Select(call => new AgentToolCall(call.Id, call.Name, call.Arguments)).ToArray(),
            message.Results?.Select(result => new AgentToolResult(result.Id, result.Text)).ToArray());

    private static AgentMessageRole ParseRole(string value)
        => Enum.TryParse(value, ignoreCase: true, out AgentMessageRole role) ? role : AgentMessageRole.User;

    private static ChatMessage ToChatMessage(AgentMessage message)
    {
        List<AIContent> contents = [];
        if (!string.IsNullOrEmpty(message.Text)) contents.Add(new TextContent(message.Text));
        if (message.Calls is { Count: > 0 })
        {
            contents.AddRange(message.Calls.Select(call => new FunctionCallContent(call.CallId, call.ToolName, ParseArguments(call.ArgumentsJson))));
        }
        if (message.Results is { Count: > 0 })
        {
            contents.AddRange(message.Results.Select(result => (AIContent)new FunctionResultContent(result.CallId, result.Text)));
        }
        ChatRole role = message.Role switch
        {
            AgentMessageRole.System => ChatRole.System,
            AgentMessageRole.Assistant => ChatRole.Assistant,
            AgentMessageRole.Tool => ChatRole.Tool,
            _ => ChatRole.User
        };
        return contents.Count == 0 ? new ChatMessage(role, message.Text ?? string.Empty) : new ChatMessage(role, contents);
    }

    private static Dictionary<string, object?> ParseArguments(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
                ?.ToDictionary(item => item.Key, item => (object?)item.Value, StringComparer.Ordinal) ?? [];
        }
        catch (JsonException)
        {
            return new(StringComparer.Ordinal) { ["arguments"] = json };
        }
    }
}
