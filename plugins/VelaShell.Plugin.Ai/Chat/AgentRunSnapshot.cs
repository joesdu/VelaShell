using System.Collections.Immutable;

namespace VelaShell.Plugin.Ai.Chat.Runtime;

/// <summary>当前运行的生命周期状态 inferred from the observed event stream.</summary>
internal enum AgentProjectionStatus
{
    Unknown,
    Running,
    Waiting,
    Completed,
    Paused,
    Failed,
    Cancelled
}

/// <summary>只读的运行计数与时间指标。</summary>
internal sealed record AgentRunMetrics(
    int ToolCalls,
    int CompletedToolCalls,
    int SuccessfulToolCalls,
    int FailedToolCalls,
    int CancelledToolCalls,
    int BackgroundWaits,
    int CompletedBackgroundWaits,
    int Pauses,
    TimeSpan Elapsed)
{
    internal int TotalToolCalls => ToolCalls;
    internal int Failures => FailedToolCalls;
    internal int Cancellations => CancelledToolCalls;
    internal int Waits => BackgroundWaits;
}

/// <summary>
/// 可供 UI、Bridge 和独立宿主读取的不可变运行投影。
/// 事件本身仍由 <see cref="AgentRunEventLog" /> 提供；此类型只保存脱敏事件和聚合指标。
/// </summary>
internal sealed record AgentRunSnapshot(
    string RunId,
    AgentProjectionStatus Status,
    int CurrentStep,
    string? ActiveTool,
    string? ActiveToolCallId,
    string? ActiveWait,
    AgentRunMetrics Metrics,
    long LastSequence,
    ImmutableArray<AgentRunEvent> RecentEvents)
{
    internal TimeSpan Elapsed => Metrics.Elapsed;
    internal bool IsTerminal => Status is AgentProjectionStatus.Completed or AgentProjectionStatus.Failed
        or AgentProjectionStatus.Cancelled or AgentProjectionStatus.Paused;

    internal AgentRunTelemetry ToTelemetry()
        => new(RunId, Status.ToString(), CurrentStep, Metrics.ToolCalls, Metrics.CompletedToolCalls,
            Metrics.SuccessfulToolCalls, Metrics.FailedToolCalls, Metrics.CancelledToolCalls,
            Metrics.BackgroundWaits, Metrics.CompletedBackgroundWaits, Metrics.Pauses,
            (long)Metrics.Elapsed.TotalMilliseconds, LastSequence);
}

/// <summary>
/// 以事件序列为唯一顺序源的线程安全运行投影器。
/// 单个事件以序列号递增方式应用；批量输入会先按序列号排序，迟到或重复事件会被忽略。
/// </summary>
internal sealed class AgentRunSnapshotProjector : IDisposable
{
    internal const int DefaultRecentEventCapacity = 64;

    private readonly Lock _gate = new();
    private readonly string _runId;
    private readonly int _recentEventCapacity;
    private readonly List<AgentRunEvent> _recentEvents;
    private readonly Dictionary<string, ActiveTool> _activeTools = [with(StringComparer.Ordinal)];
    private string? _activeWait;
    private AgentProjectionStatus _status;
    private int _currentStep;
    private int _toolCalls;
    private int _completedToolCalls;
    private int _successfulToolCalls;
    private int _failedToolCalls;
    private int _cancelledToolCalls;
    private int _backgroundWaits;
    private int _completedBackgroundWaits;
    private int _pauses;
    private long _lastSequence;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _lastEventAt;
    private bool _disposed;

    internal AgentRunSnapshotProjector(string runId, int recentEventCapacity = DefaultRecentEventCapacity)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        if (recentEventCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(recentEventCapacity));
        _runId = runId;
        _recentEventCapacity = recentEventCapacity;
        _recentEvents = [with(recentEventCapacity)];
    }

    internal AgentRunSnapshot Snapshot
    {
        get
        {
            lock (_gate) return CreateSnapshotLocked();
        }
    }

    internal static AgentRunSnapshotProjector Attach(
        AgentRunEventLog eventLog, int recentEventCapacity = DefaultRecentEventCapacity)
    {
        ArgumentNullException.ThrowIfNull(eventLog);
        IReadOnlyList<AgentRunEvent> events = eventLog.Snapshot;
        if (events.Count == 0) throw new ArgumentException("The event log is empty.", nameof(eventLog));
        var projector = new AgentRunSnapshotProjector(events[0].RunId, recentEventCapacity);
        projector.AttachTo(eventLog);
        return projector;
    }

    internal static AgentRunSnapshotProjector Attach(string runId, AgentRunEventLog eventLog,
        int recentEventCapacity = DefaultRecentEventCapacity)
    {
        ArgumentNullException.ThrowIfNull(eventLog);
        var projector = new AgentRunSnapshotProjector(runId, recentEventCapacity);
        projector.AttachTo(eventLog);
        return projector;
    }

    internal AgentRunSnapshot Apply(AgentRunEvent item)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            ApplyOrderedLocked(item);
            return CreateSnapshotLocked();
        }
    }

    internal AgentRunSnapshot Apply(IEnumerable<AgentRunEvent> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        ThrowIfDisposed();
        AgentRunEvent[] ordered = items.OrderBy(item => item.Sequence).ToArray();
        lock (_gate)
        {
            foreach (AgentRunEvent item in ordered) ApplyOrderedLocked(item);
            return CreateSnapshotLocked();
        }
    }

    internal void AttachTo(AgentRunEventLog eventLog)
    {
        ArgumentNullException.ThrowIfNull(eventLog);
        ThrowIfDisposed();
        eventLog.Appended += OnAppended;
        try
        {
            // Subscribe first so an append between Snapshot and subscription cannot be lost.
            Apply(eventLog.Snapshot);
        }
        catch
        {
            eventLog.Appended -= OnAppended;
            throw;
        }
    }

    internal void DetachFrom(AgentRunEventLog eventLog)
    {
        ArgumentNullException.ThrowIfNull(eventLog);
        eventLog.Appended -= OnAppended;
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
    }

    private void OnAppended(AgentRunEvent item)
    {
        try { Apply(item); }
        catch (ObjectDisposedException) { }
    }

    private void ApplyOrderedLocked(AgentRunEvent item)
    {
        if (!string.Equals(item.RunId, _runId, StringComparison.Ordinal))
            throw new ArgumentException("The event belongs to a different run.", nameof(item));
        if (item.Sequence <= _lastSequence) return;

        _startedAt ??= item.Timestamp;
        if (_lastEventAt is null || item.Timestamp > _lastEventAt) _lastEventAt = item.Timestamp;
        _lastSequence = item.Sequence;
        _currentStep = item.BatchStep;
        if (_recentEvents.Count == _recentEventCapacity) _recentEvents.RemoveAt(0);
        _recentEvents.Add(item);

        switch (item.Kind)
        {
            case AgentRunEventKind.StepStarted:
                _status = AgentProjectionStatus.Running;
                ClearCompletedWaitLocked();
                break;
            case AgentRunEventKind.ToolCallStarted:
                _status = AgentProjectionStatus.Running;
                _toolCalls++;
                if (!string.IsNullOrEmpty(item.CallId))
                    _activeTools[item.CallId] = new(item.CallId, item.ToolName, item.Sequence);
                break;
            case AgentRunEventKind.ToolCallCompleted:
                _completedToolCalls++;
                if (string.Equals(item.Outcome, "succeeded", StringComparison.Ordinal)) _successfulToolCalls++;
                else _failedToolCalls++;
                RemoveToolLocked(item.CallId);
                _status = AgentProjectionStatus.Running;
                break;
            case AgentRunEventKind.ToolCallFailed:
                _completedToolCalls++;
                _failedToolCalls++;
                RemoveToolLocked(item.CallId);
                _status = AgentProjectionStatus.Failed;
                break;
            case AgentRunEventKind.ToolCallCancelled:
                _completedToolCalls++;
                _cancelledToolCalls++;
                RemoveToolLocked(item.CallId);
                _status = AgentProjectionStatus.Cancelled;
                break;
            case AgentRunEventKind.StepCompleted:
                _status = AgentProjectionStatus.Completed;
                break;
            case AgentRunEventKind.NotificationRecorded:
            case AgentRunEventKind.BatchContinued:
                _status = AgentProjectionStatus.Running;
                break;
            case AgentRunEventKind.BackgroundWaitStarted:
                _backgroundWaits++;
                _activeWait = item.Summary;
                _status = AgentProjectionStatus.Waiting;
                break;
            case AgentRunEventKind.BackgroundWaitCompleted:
                _completedBackgroundWaits++;
                _activeWait = null;
                _status = string.Equals(item.Outcome, "cancelled", StringComparison.Ordinal)
                    ? AgentProjectionStatus.Cancelled : AgentProjectionStatus.Running;
                break;
            case AgentRunEventKind.RunPaused:
                _pauses++;
                _status = AgentProjectionStatus.Paused;
                break;
            default:
                // Keep the projection forward-compatible with additions to the shared event contract.
                switch (item.Kind.ToString())
                {
                    case "PlanStarted":
                    case "PlanAccepted":
                    case "VerificationStarted":
                    case "VerificationCompleted":
                        _status = AgentProjectionStatus.Running;
                        break;
                    case "RunCompleted":
                        _status = AgentProjectionStatus.Completed;
                        break;
                    case "RunFailed":
                        _status = AgentProjectionStatus.Failed;
                        break;
                    case "RunCancelled":
                        _status = AgentProjectionStatus.Cancelled;
                        break;
                }
                break;
        }
    }

    private void RemoveToolLocked(string? callId)
    {
        if (callId is not null) _activeTools.Remove(callId);
    }

    private void ClearCompletedWaitLocked()
    {
        if (_status is not AgentProjectionStatus.Waiting) _activeWait = null;
    }

    private AgentRunSnapshot CreateSnapshotLocked()
    {
        ActiveTool? activeTool = _activeTools.Values.OrderByDescending(tool => tool.Sequence).FirstOrDefault();
        TimeSpan elapsed = _startedAt is { } started && _lastEventAt is { } last && last >= started
            ? last - started : TimeSpan.Zero;
        var metrics = new AgentRunMetrics(_toolCalls, _completedToolCalls, _successfulToolCalls,
            _failedToolCalls, _cancelledToolCalls, _backgroundWaits, _completedBackgroundWaits, _pauses, elapsed);
        return new AgentRunSnapshot(_runId, _status, _currentStep, activeTool?.ToolName, activeTool?.CallId,
            _activeWait, metrics, _lastSequence, [.. _recentEvents]);
    }

    private void ThrowIfDisposed()
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AgentRunSnapshotProjector));
        }
    }

    private sealed record ActiveTool(string CallId, string? ToolName, long Sequence);
}
