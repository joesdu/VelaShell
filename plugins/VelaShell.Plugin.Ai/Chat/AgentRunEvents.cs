namespace VelaShell.Plugin.Ai.Chat;

/// <summary>运行时可观测事件的种类。事件只描述状态变化，不携带提示词、参数或工具输出。</summary>
internal enum AgentRunEventKind
{
    StepStarted,
    ToolCallStarted,
    ToolCallCompleted,
    ToolCallFailed,
    ToolCallCancelled,
    StepCompleted,
    NotificationRecorded,
    BackgroundWaitStarted,
    BackgroundWaitCompleted,
    PlanStarted,
    PlanAccepted,
    VerificationStarted,
    VerificationCompleted,
    RunCompleted,
    RunFailed,
    RunCancelled,
    BatchContinued,
    RunPaused
}

/// <summary>
/// Agent 运行事件。它是桌面、桥接和未来独立宿主共享的最小观测契约。
/// <paramref name="Summary" /> 只能放短的、已脱敏的状态说明；不要把模型上下文写进事件。
/// </summary>
internal sealed record AgentRunEvent(
    long Sequence,
    string RunId,
    DateTimeOffset Timestamp,
    AgentRunEventKind Kind,
    int BatchStep,
    string? CallId = null,
    string? ToolName = null,
    string? Outcome = null,
    int? DurationMilliseconds = null,
    string? Summary = null);

/// <summary>
/// 有界的运行事件日志。事件订阅者故障不能影响 Agent，日志满了只淘汰最早事件。
/// </summary>
internal sealed class AgentRunEventLog
{
    internal const int DefaultCapacity = 512;

    private readonly Lock _gate = new();
    private readonly int _capacity;
    private readonly List<AgentRunEvent> _events;
    private long _nextSequence;

    internal AgentRunEventLog(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _events = [with(capacity)];
    }

    internal event Action<AgentRunEvent>? Appended;

    internal IReadOnlyList<AgentRunEvent> Snapshot
    {
        get
        {
            lock (_gate) return [.. _events];
        }
    }

    internal AgentRunEvent Append(string runId, AgentRunEventKind kind, int batchStep,
        string? callId = null, string? toolName = null, string? outcome = null,
        int? durationMilliseconds = null, string? summary = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        AgentRunEvent item;
        lock (_gate)
        {
            item = new(++_nextSequence, runId, DateTimeOffset.UtcNow, kind, batchStep,
                callId, toolName, outcome, durationMilliseconds, Clip(summary));
            if (_events.Count == _capacity) _events.RemoveAt(0);
            _events.Add(item);
        }

        try { Appended?.Invoke(item); }
        catch (Exception) { }
        return item;
    }

    private static string? Clip(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Length <= 256 ? value : value[..256] + "…";
}
