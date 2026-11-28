using VelaShell.Plugin.Ai.Chat;

namespace VelaShell.Plugin.Ai.Core.Runtime;

/// <summary>Adapts the durable Chat checkpoint store without exposing Chat or disk details to hosts.</summary>
/// <summary>Adapts the plugin checkpoint store to the standalone AgentCore persistence contract.</summary>
public sealed class RunCheckpointStoreAdapter : IAgentRunCheckpointStore
{
    private readonly RunCheckpointStore _store;

    /// <summary>Creates an adapter over the existing durable checkpoint store.</summary>
    public RunCheckpointStoreAdapter(RunCheckpointStore store)
        => _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>Persists a host-neutral checkpoint using the existing store.</summary>
    public ValueTask SaveAsync(AgentRunCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _store.Save(ToCheckpoint(checkpoint));
        return ValueTask.CompletedTask;
    }

    /// <summary>Loads a checkpoint by conversation id, or returns <see langword="null"/> when absent.</summary>
    public ValueTask<AgentRunCheckpoint?> LoadAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_store.Load(conversationId) is { } checkpoint ? FromCheckpoint(checkpoint) : null);
    }

    /// <summary>Deletes the checkpoint for a conversation.</summary>
    public ValueTask DeleteAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _store.Delete(conversationId);
        return ValueTask.CompletedTask;
    }

    /// <summary>Restores transcript messages and marks unknown or pending side effects for verification.</summary>
    public AgentResumePlan Restore(AgentRunCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        RunCheckpoint source = ToCheckpoint(checkpoint);
        (List<Microsoft.Extensions.AI.ChatMessage> messages, string earlierSteps) = RunCheckpointStore.Restore(source);
        bool unknown = messages.SelectMany(message => message.Contents)
            .OfType<Microsoft.Extensions.AI.FunctionResultContent>()
            .Any(result => result.Result?.ToString()?.Contains("outcome is unknown", StringComparison.OrdinalIgnoreCase) == true);
        bool pending = RunCheckpointStore.PendingBackgroundCommands(source).Count > 0;
        return checkpoint with
        {
            Messages = [.. RunCheckpointStore.Capture(messages).Select(ToAgentMessage)],
            ProgressSummary = earlierSteps,
        } is AgentRunCheckpoint restored
            ? new AgentResumePlan(restored, restored.Messages, earlierSteps, unknown || pending)
            : throw new InvalidOperationException("Unable to create resume plan.");
    }

    private static RunCheckpoint ToCheckpoint(AgentRunCheckpoint checkpoint)
        => new(checkpoint.Descriptor.ConversationId, checkpoint.Descriptor.RunId, checkpoint.Descriptor.Task,
            checkpoint.Descriptor.StartedAt, DateTimeOffset.UtcNow, ToStatus(checkpoint.Status),
            [.. checkpoint.Messages.Select(ToCheckpointMessage)], checkpoint.ProgressSummary,
            checkpoint.Descriptor.ModelLabel, checkpoint.Events?.Select(ToCheckpointEvent).ToArray());

    private static AgentRunCheckpoint FromCheckpoint(RunCheckpoint checkpoint)
        => new(new(checkpoint.ConversationId, checkpoint.RunId, checkpoint.Task, checkpoint.StartedAt, checkpoint.ModelLabel),
            ParseStatus(checkpoint.Status), [.. checkpoint.Steps.Select(ToAgentMessage)], checkpoint.ProgressSummary,
            checkpoint.Events?.Select(ToRuntimeEvent).ToArray());

    private static RunCheckpointMessage ToCheckpointMessage(AgentMessage message)
        => new(message.Role.ToString().ToLowerInvariant(), message.Text,
            message.Calls?.Select(call => new RunCheckpointCall(call.CallId, call.ToolName, call.ArgumentsJson)).ToArray(),
            message.Results?.Select(result => new RunCheckpointResult(result.CallId, result.Text)).ToArray());

    private static AgentMessage ToAgentMessage(RunCheckpointMessage message)
        => new(ParseRole(message.Role), message.Text,
            message.Calls?.Select(call => new AgentToolCall(call.Id, call.Name, call.Arguments)).ToArray(),
            message.Results?.Select(result => new AgentToolResult(result.Id, result.Text)).ToArray());

    private static RunCheckpointEvent ToCheckpointEvent(AgentRuntimeEvent item)
        => new(item.Sequence, item.Timestamp, item.Kind.ToString(), item.BatchStep, item.CallId, item.ToolName,
            item.Outcome, item.DurationMilliseconds, item.Summary);

    private static AgentRuntimeEvent ToRuntimeEvent(RunCheckpointEvent item)
        => new(item.Sequence, item.RunId, item.Timestamp, ParseKind(item.Kind), item.BatchStep,
            item.CallId, item.ToolName, item.Outcome, item.DurationMilliseconds, item.Summary);

    private static string ToStatus(AgentRunStatus status) => status.ToString().ToLowerInvariant();

    private static AgentRunStatus ParseStatus(string status)
        => Enum.TryParse(status, true, out AgentRunStatus parsed) ? parsed : AgentRunStatus.Paused;

    private static AgentMessageRole ParseRole(string role)
        => Enum.TryParse(role, true, out AgentMessageRole parsed) ? parsed : AgentMessageRole.User;

    private static AgentRuntimeEventKind ParseKind(string kind)
        => Enum.TryParse(kind, true, out AgentRuntimeEventKind parsed) ? parsed : AgentRuntimeEventKind.RunPaused;
}

