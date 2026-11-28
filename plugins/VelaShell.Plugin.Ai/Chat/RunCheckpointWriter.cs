using VelaShell.Plugin.Ai.Core.Runtime;

namespace VelaShell.Plugin.Ai.Chat;

/// <summary>
/// 一轮运行的检查点写手:有进展就排一次延迟写(合并 1.5 秒内的多次进展),
/// 收尾时按结局删掉或补写最终一份。文件读写在线程池上做,不占 UI 线程。聊天面板与 IM 桥接共用。
/// </summary>
/// <remarks>
/// 写与删串在同一把锁上:定时写入已经过了「没关闭」那道检查、还没落盘时这一轮正常结束删掉了检查点,
/// 那次写入就会把文件重新写出来,下次打开会话冒出一张不该有的续跑卡。所以落盘前在锁里再确认一次。
/// </remarks>
internal sealed class RunCheckpointWriter : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1.5);

    private readonly RunCheckpointStore _store;
    private readonly AgentExecution _execution;
    private readonly string _conversationId;
    private readonly string _runId;
    private readonly string _task;
    private readonly string _modelLabel;
    private readonly Func<string> _summary;
    private readonly Func<AgentOrchestrationState?>? _orchestration;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly Lock _gate = new();
    private readonly Lock _writeGate = new();
    private Timer? _timer;
    private bool _closed;

    public RunCheckpointWriter(RunCheckpointStore store, AgentExecution execution, string conversationId,
        string runId, string task, string modelLabel, Func<string> summary,
        Func<AgentOrchestrationState?>? orchestration = null)
    {
        _store = store;
        _execution = execution;
        _conversationId = conversationId;
        _runId = runId;
        _task = task;
        _modelLabel = modelLabel;
        _summary = summary;
        _orchestration = orchestration;
        _execution.Progressed += Schedule;
    }

    private void Schedule()
    {
        lock (_gate)
        {
            if (_closed || _timer is not null) return;
            _timer = new Timer(_ =>
            {
                lock (_gate)
                {
                    _timer?.Dispose();
                    _timer = null;
                }
                Write("running", final: false);
            }, null, Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Write(string status, bool final)
    {
        lock (_writeGate)
        {
            // 定时写入在关闭之后一律作废(收尾那一份由 Keep 自己写)
            if (!final && Volatile.Read(ref _closed)) return;
            try
            {
                IReadOnlyList<RunCheckpointMessage> steps = RunCheckpointStore.Capture(_execution.CompletedMessages);
                if (steps.Count == 0) return;
                IReadOnlyList<RunCheckpointEvent> events = RunCheckpointStore.CaptureEvents(_execution.Events.Snapshot);
                _store.Save(new RunCheckpoint(_conversationId, _runId, _task, _startedAt, DateTimeOffset.UtcNow,
                    status, steps, _summary(), _modelLabel, events, _orchestration?.Invoke()));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // 写不了盘只是这一轮断了之后续不上;不该连累这一轮本身
            }
        }
    }

    /// <summary>中断了(停下 / 出错):补写最终一份并留着,返回它;没有可续的步骤时返回 null。</summary>
    public RunCheckpoint? Keep()
    {
        Close();
        Write("interrupted", final: true);
        return _store.Load(_conversationId) is { RunId: var id } checkpoint && id == _runId ? checkpoint : null;
    }

    /// <summary>跑完了,或一步都没走:检查点作废。</summary>
    public void Discard()
    {
        Close();
        lock (_writeGate) _store.Delete(_conversationId);
    }

    private void Close()
    {
        lock (_gate)
        {
            Volatile.Write(ref _closed, true);
            _timer?.Dispose();
            _timer = null;
        }
        _execution.Progressed -= Schedule;
    }

    public void Dispose() => Close();
}
