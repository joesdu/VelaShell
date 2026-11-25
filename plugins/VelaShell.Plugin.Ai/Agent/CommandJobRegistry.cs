using System.Runtime.CompilerServices;
using VelaShell.PluginSdk.RemoteExec;

namespace VelaShell.Plugin.Ai.Agent;

/// <summary>本地管理状态;取消通道不等于已证实远端进程停止。</summary>
public enum CommandJobState
{
    /// <summary>已排队,尚未开通道。</summary>
    Starting,
    /// <summary>等待可信审批或认证。</summary>
    AwaitingUser,
    /// <summary>前台执行通道已启动。</summary>
    Running,
    /// <summary>已确认退出码为零。</summary>
    Succeeded,
    /// <summary>已确认执行失败或不支持。</summary>
    Failed,
    /// <summary>排队任务在启动前已取消。</summary>
    Cancelled,
    /// <summary>执行死线已到;不保证远端停止。</summary>
    TimedOut,
    /// <summary>通道失败或取消后无法确认远端结果。</summary>
    Unknown
}

/// <summary>带单调游标与原始流标识的一段有界输出。</summary>
/// <param name="Cursor">此条输出的单调序号。</param>
/// <param name="Stream">stdout、stderr 或提权 PTY 的 combined。</param>
/// <param name="Text">不超过单条上限的原文。</param>
public sealed record CommandJobOutput(long Cursor, string Stream, string Text);

/// <summary>不可变、有界的任务快照。游标指向下一条输出,过期部分以 OutputTruncated 标记。</summary>
public sealed record CommandJobSnapshot(
    string TaskId, string SessionId, CommandJobState State, int? ExitCode,
    DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt,
    bool CancellationRequested, bool RemoteStopConfirmed, bool Monitoring,
    IReadOnlyList<CommandJobOutput> Output, long NextCursor, long EarliestCursor,
    bool OutputTruncated, bool HasMoreOutput, string? Error)
{
    /// <summary>本地托管执行已经收尾,不代表未知任务的远端副作用已完成。</summary>
    public bool IsTerminal => State is CommandJobState.Succeeded or CommandJobState.Failed
        or CommandJobState.Cancelled or CommandJobState.TimedOut or CommandJobState.Unknown;
}

/// <summary>
/// 应用存活期间的 exec 任务注册表。不解析 shell、不接管脱离 exec 通道的进程。
/// 同一插件上下文的工具箱共用四个槽,避免多对话分别占满宿主额度。
/// </summary>
public sealed class CommandJobRegistry(IRemoteExecApi remoteExec, object? schedulerKey = null,
    CancellationToken lifetimeToken = default) : IAsyncDisposable
{
    private const int MaxJobsPerRun = 128;
    private const int MaxOutputChars = 32 * 1024;
    private const int MaxEntryChars = 4096;
    private const int MaxOutputEntries = 256;
    private static readonly ConditionalWeakTable<object, SemaphoreSlim> Schedulers = [];
    private readonly SemaphoreSlim _streamSlots = Schedulers.GetValue(schedulerKey ?? remoteExec,
        static _ => new SemaphoreSlim(IRemoteExecApi.MaxConcurrentStreams));
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Job> _jobs = [with(StringComparer.Ordinal)];
    private readonly HashSet<CommandJobRunScope> _runs = [];
    private bool _disposed;

    /// <summary>创建所有者隔离的轮次,并绑定查询和实际启动前的会话授权检查。</summary>
    /// <remarks>
    /// <c>denialEndsRun</c> 决定拒绝是否封住整个轮次。MCP 的轮次跨越整条客户端连接,传 false:
    /// 拒绝只作为那一次调用的结果返回。
    /// </remarks>
    public CommandJobRunScope BeginRun(string ownerId, string runId,
        Func<string, CancellationToken, Task<bool>>? authorize = null, bool denialEndsRun = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runs.Any(r => r.OwnerId == ownerId && r.RunId == runId))
            {
                throw new InvalidOperationException("This owner/run is already registered.");
            }
            var scope = new CommandJobRunScope(this, ownerId, runId, authorize, denialEndsRun, cancellationToken, lifetimeToken);
            _runs.Add(scope);
            return scope;
        }
    }

    /// <summary>登记普通前台命令并进入共享流调度队列;返回不含目标信息的随机任务 id。</summary>
    public string Start(CommandJobRunScope scope, string sessionId, string command,
        TimeSpan? executionTimeout = null, bool monitoring = false)
        => StartCore(scope, sessionId, command, executionTimeout, monitoring,
            (output, _, token) => remoteExec.StreamAsync(sessionId, command,
                new ExecStreamOptions { Timeout = executionTimeout, IncludeStandardError = true }, output, token));

    /// <summary>仅通过专用宿主契约执行整条 root 命令,不回退普通执行。</summary>
    public string StartSudo(CommandJobRunScope scope, string sessionId, string command,
        TimeSpan? executionTimeout = null, bool monitoring = false)
        => StartCore(scope, sessionId, command, executionTimeout, monitoring, async (output, state, token) =>
        {
            SudoExecResult result = await remoteExec.StreamSudoAsync(sessionId, command,
                new SudoExecOptions { Timeout = executionTimeout }, new SudoOutput(output, state), token).ConfigureAwait(false);
            if (result.Outcome == SudoExecOutcome.AuthenticationCancelled)
            {
                scope.MarkExecutionDenied();
            }
            return result.Outcome switch
            {
                SudoExecOutcome.Exited when result.ExitCode is { } code => new ExecStreamResult(code, result.OutputChunks),
                SudoExecOutcome.AuthenticationCancelled => throw new JobOutcomeException(CommandJobState.Failed,
                    "The user denied root execution or cancelled authentication. The command did not start. Do not retry.", true),
                SudoExecOutcome.AuthenticationFailed => throw new JobOutcomeException(CommandJobState.Failed,
                    "Sudo authentication failed. The command did not start. Do not automatically retry.", true),
                SudoExecOutcome.NotSupported => throw new JobOutcomeException(CommandJobState.Failed,
                    "Trusted sudo execution is not supported. The command did not start; no ordinary-exec fallback was attempted.", true),
                _ => throw new JobOutcomeException(CommandJobState.Unknown,
                    "Sudo command outcome is unknown or its exit code is missing. Do not replay this operation.", false)
            };
        }, mergedOutput: true);

    private sealed class SudoOutput(IProgress<ExecOutput> output, Action<CommandJobState> state) : IProgress<SudoExecEvent>
    {
        public void Report(SudoExecEvent value)
        {
            if (value.Kind == SudoExecEventKind.Output && value.Text is { } text)
            {
                output.Report(new ExecOutput(ExecStream.StandardOutput, text));
            }
            else if (value.Kind == SudoExecEventKind.State)
            {
                state(value.State == SudoExecState.Running ? CommandJobState.Running : CommandJobState.AwaitingUser);
            }
        }
    }

    /// <summary>执行器给出的确定结局(状态、说明、进程是否已确认停止),不再按通用异常推断。</summary>
    internal sealed class JobOutcomeException(CommandJobState state, string message, bool confirmed) : Exception(message)
    {
        public CommandJobState State { get; } = state;
        public bool Confirmed { get; } = confirmed;
    }

    /// <summary>任务列表里本机任务的会话标识(不是任何远端会话)。</summary>
    public const string LocalSessionId = "local";

    /// <summary>
    /// 接管一个已经过审批、已启动的本机进程:与远端命令共用任务 id、task_status / task_wait / task_cancel、
    /// 输出缓冲与完成通知。不占远端流的并发槽(进程已在跑,排队等槽只会让管道写满把它卡住),
    /// 授权改用调用方给的工作区检查。
    /// </summary>
    /// <param name="scope">所属轮次。</param>
    /// <param name="command">原始命令(只用于登记,不再执行)。</param>
    /// <param name="executionTimeout">执行死线;由 <paramref name="execute" /> 自行施行。</param>
    /// <param name="execute">驱动进程直到结束;取消 / 超时时须自行终止进程并以 <see cref="JobOutcomeException" /> 报告结局。</param>
    /// <param name="authorize">读取任务时重新确认工作区授权;撤销后输出不再交出。</param>
    internal string StartLocal(CommandJobRunScope scope, string command, TimeSpan? executionTimeout,
        Func<IProgress<ExecOutput>, CancellationToken, Task<ExecStreamResult>> execute,
        Func<CancellationToken, Task<bool>> authorize)
        => StartCore(scope, LocalSessionId, command, executionTimeout, monitoring: false,
            (output, _, token) => execute(output, token), preStarted: true, localAuthorize: authorize);

    // 提权使用同一调度、所有权、输出和清理路径;认证秘密不进入注册表。
    internal string StartCore(CommandJobRunScope scope, string sessionId, string command,
        TimeSpan? executionTimeout, bool monitoring,
        Func<IProgress<ExecOutput>, Action<CommandJobState>, CancellationToken, Task<ExecStreamResult>> execute,
        bool mergedOutput = false, bool preStarted = false, Func<CancellationToken, Task<bool>>? localAuthorize = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (executionTimeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(executionTimeout));
        }
        Job job;
        lock (_gate)
        {
            ValidateScope(scope);
            scope.Token.ThrowIfCancellationRequested();
            if (_jobs.Values.Count(j => ReferenceEquals(j.Scope, scope)) >= MaxJobsPerRun)
            {
                // 额度满时回收已收尾、终态已交付的任务;否则活一整条连接的 MCP 轮次跑满 128 条就再也执行不了。
                // Unknown 一类留着:它们是本轮的停止边界,回收了 HasUnknownOutcome 就会悄悄放行。
                foreach (Job done in _jobs.Values.Where(j => ReferenceEquals(j.Scope, scope) && j.Reclaimable).ToArray())
                {
                    _jobs.Remove(done.Id);
                    done.Cancel.Dispose();
                }
                if (_jobs.Values.Count(j => ReferenceEquals(j.Scope, scope)) >= MaxJobsPerRun)
                {
                    throw new InvalidOperationException("The command task budget for this run is exhausted.");
                }
            }
            job = new Job(scope, sessionId, command, monitoring, mergedOutput)
            {
                PreStarted = preStarted,
                LocalAuthorize = localAuthorize
            };
            _jobs.Add(job.Id, job);
            // 先登记再执行;同步完成的替身和真实异步实现走同一路径,无 Task.Run 包 I/O。
            job.Execution = ExecuteAsync(job, execute);
        }
        return job.Id;
    }

    /// <summary>验证所有者与当前授权,读取游标之后保留的输出。</summary>
    public async Task<CommandJobSnapshot> ReadAsync(CommandJobRunScope scope, string taskId,
        long cursor = 0, CancellationToken cancellationToken = default)
    {
        Job job = Find(scope, taskId);
        await AuthorizeAsync(job, cancellationToken).ConfigureAwait(false);
        return job.Deliver(cursor);
    }

    /// <summary>
    /// 给界面实时展示用的只读快照。与 <see cref="ReadAsync" /> 不同:不消费终态的完成通知
    /// (那是留给模型的),授权已撤销时只交出不含输出的占位快照,也不因此取消任务。
    /// </summary>
    internal async Task<CommandJobSnapshot> PeekAsync(CommandJobRunScope scope, string taskId,
        long cursor = 0, CancellationToken cancellationToken = default)
    {
        Job job = Find(scope, taskId);
        if (!await IsAuthorizedAsync(job, cancellationToken).ConfigureAwait(false))
        {
            return job.Withheld();
        }
        return job.Snapshot(cursor);
    }

    /// <summary>只限本地等待窗口,不会改变远端执行死线。</summary>
    public async Task<CommandJobSnapshot> WaitAsync(CommandJobRunScope scope, string taskId, TimeSpan wait,
        long cursor = 0, CancellationToken cancellationToken = default)
    {
        Job job = Find(scope, taskId);
        await AuthorizeAsync(job, cancellationToken).ConfigureAwait(false);
        try
        {
            await job.Completion.Task.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 短等结束仍持有任务和独立 CTS;不是执行超时。
        }
        await AuthorizeAsync(job, cancellationToken).ConfigureAwait(false);
        return job.Deliver(cursor);
    }

    /// <summary>工具已返回运行中回执;允许终态投递为新的本地完成通知。</summary>
    public void MarkBackground(CommandJobRunScope scope, string taskId)
    {
        Job job = Find(scope, taskId);
        lock (job.Gate)
        {
            job.Background = true;
        }
    }

    /// <summary>取消不重新验证会话范围,授权撤销也不能阻挡资源清理。</summary>
    public async Task CancelAsync(CommandJobRunScope scope, string taskId)
    {
        Job job = Find(scope, taskId);
        lock (job.Gate)
        {
            job.StopRequested = true;
        }
        await job.Cancel.CancelAsync().ConfigureAwait(false);
        // 不等可能失联的远端,也不返回撤权后的输出、退出码或目标元数据。
    }

    private Job Find(CommandJobRunScope scope, string taskId)
    {
        lock (_gate)
        {
            ValidateScope(scope);
            if (!_jobs.TryGetValue(taskId, out Job? job) || !ReferenceEquals(job.Scope, scope))
            {
                // 不泄漏其它所有者是否拥有这个 id。
                throw new KeyNotFoundException("No command task with this id belongs to this run.");
            }
            return job;
        }
    }

    private void ValidateScope(CommandJobRunScope scope)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_runs.Contains(scope) || scope.IsDisposed)
        {
            throw new InvalidOperationException("The command run scope is no longer active.");
        }
    }

    /// <summary>本机任务按工作区授权,远端任务按会话授权;都没有设检查时视为已授权。</summary>
    private static async Task<bool> IsAuthorizedAsync(Job job, CancellationToken cancellationToken)
    {
        if (job.LocalAuthorize is { } local)
        {
            return await local(cancellationToken).ConfigureAwait(false);
        }
        return job.Scope._authorize is not { } authorize
            || await authorize(job.SessionId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task AuthorizeAsync(Job job, CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(job, cancellationToken).ConfigureAwait(false))
        {
            await job.Cancel.CancelAsync().ConfigureAwait(false);
            throw new UnauthorizedAccessException("Authorization for this task's session was revoked; output is withheld.");
        }
    }

    private async Task ExecuteAsync(Job job,
        Func<IProgress<ExecOutput>, Action<CommandJobState>, CancellationToken, Task<ExecStreamResult>> execute)
    {
        bool slot = false;
        bool started = false;
        try
        {
            // 本机任务进来时进程已经在跑(审批与工作区授权在启动前已核过):不能再排队等槽或做启动前检查,
            // 那样一旦被挡下,执行器根本没机会接手,已启动的进程就没人收拾了。执行器必须立刻接管。
            if (!job.PreStarted)
            {
                await _streamSlots.WaitAsync(job.Cancel.Token).ConfigureAwait(false);
                slot = true;
                job.Cancel.Token.ThrowIfCancellationRequested();
                await AuthorizeAsync(job, job.Cancel.Token).ConfigureAwait(false);
                job.Cancel.Token.ThrowIfCancellationRequested();
                if (job.Scope.HasDeniedExecution)
                    throw new UnauthorizedAccessException("Execution was denied in this run; queued commands cannot start.");
            }
            lock (job.Gate)
            {
                job.Status = new RunningState();
                job.StartedAt = DateTimeOffset.UtcNow;
                job.CommandStarted = !job.MergedOutput;
            }
            started = true;
            ExecStreamResult result = await execute(new DirectOutput(job), state =>
            {
                lock (job.Gate)
                {
                    job.Status = state switch
                    {
                        CommandJobState.Running => new RunningState(),
                        CommandJobState.AwaitingUser => new AwaitingUserState(),
                        _ => throw new ArgumentOutOfRangeException(nameof(state))
                    };
                    if (state == CommandJobState.Running)
                    {
                        job.CommandStarted = true;
                    }
                }
            }, job.Cancel.Token).ConfigureAwait(false);
            lock (job.Gate)
            {
                // 退出码证实受监控的前台进程已退出;不对其自行脱离的子进程作保证。
                job.Status = new ExitedState(result.ExitCode);
            }
        }
        catch (OperationCanceledException) when (job.Cancel.IsCancellationRequested)
        {
            lock (job.Gate)
            {
                // 与超时分支同一判据:提权命令还停在审批/认证里就取消,用户命令没有启动过。
                // 对显式监控任务点名取消是结束它的正常方式,不是需要暂停整轮的未知结果。
                bool unknown = job.CommandStarted && !(job.Monitoring && job.StopRequested);
                bool remoteStopConfirmed = !started;
                string? error = unknown ? "Exec channel cancelled; remote process termination is not confirmed."
                    : !started ? null
                    : job.CommandStarted ? "Monitoring task stopped on request; remote process termination is not confirmed."
                    : "Cancelled before the command reported starting.";
                job.Status = unknown
                    ? new UnknownState(error)
                    : new CancelledState(remoteStopConfirmed, error);
            }
        }
        catch (JobOutcomeException ex)
        {
            lock (job.Gate)
            {
                job.Status = ex.State switch
                {
                    CommandJobState.Failed => new FailedState(ex.Confirmed, ex.Message),
                    CommandJobState.Cancelled => new CancelledState(ex.Confirmed, ex.Message),
                    CommandJobState.TimedOut => new TimedOutState(job.CommandStarted, ex.Confirmed, ex.Message),
                    CommandJobState.Unknown => new UnknownState(ex.Message),
                    _ => throw new ArgumentOutOfRangeException(nameof(ex.State))
                };
            }
        }
        catch (TimeoutException)
        {
            lock (job.Gate)
            {
                job.Status = new TimedOutState(job.CommandStarted, false,
                    "Execution deadline expired; remote process termination is not confirmed.");
            }
        }
        catch (Exception ex)
        {
            lock (job.Gate)
            {
                job.Status = started && ex is not (NotSupportedException or UnauthorizedAccessException)
                    ? new UnknownState(ex.Message)
                    : new FailedState(false, ex.Message);
            }
        }
        finally
        {
            if (slot)
            {
                _streamSlots.Release();
            }
            lock (job.Gate)
            {
                job.CompletedAt = DateTimeOffset.UtcNow;
            }
            job.Completion.TrySetResult();
        }
    }

    internal bool HasActiveJobs(CommandJobRunScope scope)
    {
        lock (_gate)
        {
            return _jobs.Values.Any(j => ReferenceEquals(j.Scope, scope) && j.NeedsNotification);
        }
    }

    internal bool HasUnknownOutcome(CommandJobRunScope scope)
    {
        lock (_gate)
        {
            return _jobs.Values.Any(j => ReferenceEquals(j.Scope, scope) && j.HasUnknownOutcome);
        }
    }

    internal bool HasMonitoringJobs(CommandJobRunScope scope)
    {
        lock (_gate)
        {
            return _jobs.Values.Any(j => ReferenceEquals(j.Scope, scope) && j.Monitoring && !j.Completion.Task.IsCompleted);
        }
    }

    internal async Task<IReadOnlyList<CommandJobSnapshot>> WaitForCompletionAsync(CommandJobRunScope scope,
        CancellationToken cancellationToken)
    {
        // 只有"已经没有待通知的任务"才返回空表;醒来却一条都交不出时接着等,
        // 否则调用方会把空表当成全部完成,收尾时连同其余仍在跑的任务一起取消。
        while (true)
        {
            Job[] jobs;
            lock (_gate)
            {
                ValidateScope(scope);
                jobs = [.. _jobs.Values.Where(j => ReferenceEquals(j.Scope, scope) && j.NeedsNotification)];
            }
            if (jobs.Length == 0)
            {
                return [];
            }
            await Task.WhenAny(jobs.Select(j => j.Completion.Task)).WaitAsync(cancellationToken).ConfigureAwait(false);
            var snapshots = new List<CommandJobSnapshot>();
            foreach (Job job in jobs.Where(j => j.Completion.Task.IsCompleted))
            {
                bool authorized = true;
                try
                {
                    await AuthorizeAsync(job, cancellationToken).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException)
                {
                    authorized = false;
                }
                lock (job.Gate)
                {
                    if (!job.Notified)
                    {
                        job.Notified = true;
                        // 自动续接和手动查询遵守同一范围:撤销后不能借完成通知读旧输出,
                        // 但"这条任务的结果不再提供"本身必须交出去,按 Unknown 停下这一轮。
                        snapshots.Add(authorized ? job.Snapshot(0) : job.Withheld());
                    }
                }
            }
            if (snapshots.Count > 0)
            {
                return snapshots;
            }
        }
    }

    internal Task WaitForMonitoringEndAsync(CommandJobRunScope scope, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ValidateScope(scope);
            return Task.WhenAll(_jobs.Values.Where(j => ReferenceEquals(j.Scope, scope) && j.Monitoring)
                .Select(j => j.Completion.Task)).WaitAsync(cancellationToken);
        }
    }

    internal async Task CancelRunAsync(CommandJobRunScope scope)
    {
        await scope._cancelSource.CancelAsync().ConfigureAwait(false);
        Job[] jobs;
        lock (_gate)
        {
            jobs = [.. _jobs.Values.Where(j => ReferenceEquals(j.Scope, scope))];
        }
        await Task.WhenAll(jobs.Select(j => j.Execution)).ConfigureAwait(false);
    }

    internal async ValueTask DisposeRunAsync(CommandJobRunScope scope)
    {
        await CancelRunAsync(scope).ConfigureAwait(false);
        lock (_gate)
        {
            foreach (Job job in _jobs.Values.Where(j => ReferenceEquals(j.Scope, scope)).ToArray())
            {
                _jobs.Remove(job.Id);
                job.Cancel.Dispose();
            }
            _runs.Remove(scope);
        }
    }

    /// <summary>取消并等待托管 I/O 收尾后释放本地资源。</summary>
    public async ValueTask DisposeAsync()
    {
        CommandJobRunScope[] runs;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            runs = [.. _runs];
        }
        foreach (CommandJobRunScope run in runs)
        {
            await run.DisposeAsync().ConfigureAwait(false);
        }
    }

    private readonly union JobExecutionState(StartingState, AwaitingUserState, RunningState,
        ExitedState, FailedState, CancelledState, TimedOutState, UnknownState);
    private readonly record struct StartingState;
    private readonly record struct AwaitingUserState;
    private readonly record struct RunningState;
    private readonly record struct ExitedState(int ExitCode);
    private readonly record struct FailedState(bool RemoteStopConfirmed, string Error);
    private readonly record struct CancelledState(bool RemoteStopConfirmed, string? Error);
    private readonly record struct TimedOutState(bool CommandStarted, bool RemoteStopConfirmed, string Error);
    private readonly record struct UnknownState(string? Error);

    private sealed class DirectOutput(Job job) : IProgress<ExecOutput>
    {
        public void Report(ExecOutput value) => job.Append(value);
    }

    private sealed class Job(CommandJobRunScope scope, string sessionId, string command, bool monitoring, bool mergedOutput)
    {
        public readonly object Gate = new();
        public readonly string Id = Guid.NewGuid().ToString("N");
        public readonly CommandJobRunScope Scope = scope;
        public readonly string SessionId = sessionId;
        public readonly string Command = command;
        public readonly bool Monitoring = monitoring;
        public readonly bool MergedOutput = mergedOutput;
        /// <summary>本机任务:进程在登记前已启动,跳过排队、等槽与启动前授权(那些已在启动前做过)。</summary>
        public bool PreStarted { get; init; }
        /// <summary>本机任务的授权检查(工作区仍被允许);为空时按会话授权。</summary>
        public Func<CancellationToken, Task<bool>>? LocalAuthorize { get; init; }
        public bool CommandStarted;
        public readonly DateTimeOffset CreatedAt = DateTimeOffset.UtcNow;
        public readonly CancellationTokenSource Cancel = CancellationTokenSource.CreateLinkedTokenSource(scope.Token);
        public readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Execution = Task.CompletedTask;
        public JobExecutionState Status = new StartingState();
        public DateTimeOffset? StartedAt;
        public DateTimeOffset? CompletedAt;
        public bool Background;
        public bool Notified;
        /// <summary>经 task_cancel 点名取消,而不是随整轮停止或撤权被连带取消。</summary>
        public bool StopRequested;
        private readonly Queue<CommandJobOutput> _output = new();
        private int _characters;
        private long _nextCursor;
        private bool _truncated;

        public bool HasUnknownOutcome
        {
            get
            {
                lock (Gate)
                {
                    return Status is UnknownState
                        || Status is TimedOutState { CommandStarted: true, RemoteStopConfirmed: false };
                }
            }
        }

        public bool NeedsNotification
        {
            get
            {
                lock (Gate)
                {
                    return Background && !Monitoring && !Notified;
                }
            }
        }

        /// <summary>已收尾、终态已交付,且不承载 Unknown 停止边界;任务额度满时可以回收。</summary>
        public bool Reclaimable
        {
            get
            {
                lock (Gate)
                {
                    return Completion.Task.IsCompleted && Notified && !HasUnknownOutcome;
                }
            }
        }

        public void Append(ExecOutput value)
        {
            lock (Gate)
            {
                // SDK 2.0.9 已按行回调,提权输出则是最长 8192 字节的原始分片;超长的一段按序拆成多条,
                // 不丢后半段,也不因此误报截断。\r 进度按原文保留,代理对不拆开。
                string text = value.Line ?? "";
                string stream = MergedOutput ? "combined" : value.Stream == ExecStream.StandardError ? "stderr" : "stdout";
                int offset = 0;
                do
                {
                    int length = Math.Min(MaxEntryChars, text.Length - offset);
                    if (offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1]))
                    {
                        length--;
                    }
                    Enqueue(stream, text.Substring(offset, length));
                    offset += length;
                }
                while (offset < text.Length);
            }
        }

        private void Enqueue(string stream, string text)
        {
            int cost = Math.Max(1, text.Length);
            while ((_characters + cost > MaxOutputChars || _output.Count >= MaxOutputEntries)
                && _output.TryDequeue(out CommandJobOutput? old))
            {
                _characters -= Math.Max(1, old.Text.Length);
                _truncated = true;
            }
            _output.Enqueue(new CommandJobOutput(_nextCursor++, stream, text));
            _characters += cost;
        }

        /// <summary>交给模型的快照;终态一旦经查询或等待交付,就不再另发一次完成通知。</summary>
        public CommandJobSnapshot Deliver(long cursor)
        {
            lock (Gate)
            {
                CommandJobSnapshot snapshot = Snapshot(cursor);
                if (snapshot.IsTerminal)
                {
                    Notified = true;
                }
                return snapshot;
            }
        }

        /// <summary>会话授权已撤销:只交出任务 id 与"结果不予提供",不带输出、退出码与目标。</summary>
        public CommandJobSnapshot Withheld() => new(Id, "", CommandJobState.Unknown, null, CreatedAt, null, null,
            Cancel.IsCancellationRequested, false, Monitoring, [], 0, 0, false, false,
            "Authorization for this task's session was revoked; its output and final state are withheld. Do not rerun it automatically.");

        public CommandJobSnapshot Snapshot(long cursor)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(cursor);
            lock (Gate)
            {
                long earliest = _output.TryPeek(out CommandJobOutput? first) ? first.Cursor : _nextCursor;
                CommandJobOutput[] output = [.. _output.Where(o => o.Cursor >= cursor)];
                (CommandJobState state, int? exitCode, bool remoteStopConfirmed, string? error) = Status switch
                {
                    StartingState => (CommandJobState.Starting, (int?)null, false, (string?)null),
                    AwaitingUserState => (CommandJobState.AwaitingUser, null, false, null),
                    RunningState => (CommandJobState.Running, null, false, null),
                    ExitedState(var code) => (code == 0 ? CommandJobState.Succeeded : CommandJobState.Failed, code, true, null),
                    FailedState(var confirmed, var message) => (CommandJobState.Failed, null, confirmed, message),
                    CancelledState(var confirmed, var message) => (CommandJobState.Cancelled, null, confirmed, message),
                    TimedOutState(_, var confirmed, var message) => (CommandJobState.TimedOut, null, confirmed, message),
                    UnknownState(var message) => (CommandJobState.Unknown, null, false, message)
                };
                return new CommandJobSnapshot(Id, SessionId, state, exitCode, CreatedAt, StartedAt, CompletedAt,
                    Cancel.IsCancellationRequested, remoteStopConfirmed, Monitoring, output,
                    _nextCursor, earliest, _truncated || cursor < earliest, false, error);
            }
        }
    }
}

/// <summary>一轮执行的所有者能力令牌。调用者必须异步释放,Stop 也取消其所有排队和运行任务。</summary>
public sealed class CommandJobRunScope : IAsyncDisposable
{
    private readonly CommandJobRegistry _registry;
    private readonly bool _denialEndsRun;
    private int _disposed;
    private int _executionDenied;
    internal readonly CancellationTokenSource _cancelSource;
    internal readonly Func<string, CancellationToken, Task<bool>>? _authorize;
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    internal CancellationToken Token => _cancelSource.Token;
    /// <summary>稳定的入口或对话所有者,不交由模型指定。</summary>
    public string OwnerId { get; }
    /// <summary>入口生成的唯一轮次代际。</summary>
    public string RunId { get; }
    /// <summary>仍有尚未消费完成通知的有限后台任务。</summary>
    public bool HasActiveJobs => !IsDisposed && _registry.HasActiveJobs(this);
    /// <summary>本轮存在 Unknown 或已启动且未确认停止的 TimedOut,不得自动推进或重放。</summary>
    public bool HasUnknownOutcome => (!IsDisposed && _registry.HasUnknownOutcome(this))
        || Transfers?.HasUnknownOutcome == true;
    /// <summary>与本轮绑定的独立上传；模型 Stop 不取消已批准的宿主传输。</summary>
    public FileTransferRunScope? Transfers { get; internal set; }
    /// <summary>本轮审批或认证被拒绝,不得自动推进任何后续工具。拒绝不封轮次的(MCP)恒为 false。</summary>
    public bool HasDeniedExecution => Volatile.Read(ref _executionDenied) != 0
        || (_denialEndsRun && Transfers?.HasDeniedExecution == true);
    internal void MarkExecutionDenied()
    {
        if (_denialEndsRun)
        {
            Interlocked.Exchange(ref _executionDenied, 1);
            // 拒绝是本轮的停止边界:之前发起、还排着等宿主确认的上传 / 下载一并撤掉,确认框不再一个个弹
            _ = Transfers?.CancelUnapprovedAsync();
        }
    }

    /// <summary>入口的停止(面板「停止」、IM 的 /stop 与轮次超时、对外 MCP 连接收尾)。</summary>
    /// <remarks>
    /// 只挂在入口传进来的令牌上:<see cref="_cancelSource" /> 在正常收尾时也会被取消(见 DisposeAsync),
    /// 那时还在等宿主确认的上传是用户随后要去点的,不能撤。
    /// </remarks>
    private readonly CancellationTokenRegistration _stopRegistration;

    private void OnStopped() => _ = Transfers?.CancelUnapprovedAsync();
    /// <summary>仍有不应自动请求模型的显式无限监控任务。</summary>
    public bool HasMonitoringJobs => !IsDisposed && _registry.HasMonitoringJobs(this);

    internal CommandJobRunScope(CommandJobRegistry registry, string ownerId, string runId,
        Func<string, CancellationToken, Task<bool>>? authorize, bool denialEndsRun,
        CancellationToken cancellationToken, CancellationToken lifetimeToken)
    {
        _registry = registry;
        _denialEndsRun = denialEndsRun;
        OwnerId = ownerId;
        RunId = runId;
        _authorize = authorize;
        _cancelSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeToken);
        _stopRegistration = cancellationToken.Register(static state => ((CommandJobRunScope)state!).OnStopped(), this);
    }

    /// <summary>首个有限后台任务结束时唤醒,原子消费此刻所有已完成且未投递的快照。</summary>
    public Task<IReadOnlyList<CommandJobSnapshot>> WaitForCompletionAsync(CancellationToken cancellationToken = default)
        => _registry.WaitForCompletionAsync(this, cancellationToken);

    /// <summary>仅本地等待显式监控任务结束,不投递模型完成通知。</summary>
    public Task WaitForMonitoringEndAsync(CancellationToken cancellationToken = default)
        => _registry.WaitForMonitoringEndAsync(this, cancellationToken);

    /// <summary>停止本轮,等待排队及运行任务的本地收尾;不受会话范围撤销影响。</summary>
    public Task CancelAsync() => _registry.CancelRunAsync(this);

    /// <summary>界面读取本轮某个任务的实时快照(不消费模型的完成通知),见 <see cref="CommandJobRegistry.PeekAsync" />。</summary>
    internal Task<CommandJobSnapshot> PeekAsync(string taskId, long cursor = 0, CancellationToken cancellationToken = default)
        => _registry.PeekAsync(this, taskId, cursor, cancellationToken);

    /// <summary>界面上的「停止」:请求取消本轮的单个任务,与模型调用 task_cancel 同一条路径。</summary>
    internal Task CancelJobAsync(string taskId) => _registry.CancelAsync(this, taskId);

    /// <summary>取消并等待托管 I/O 收尾后释放本地资源。</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        // 轮次收尾之后入口令牌再被取消(面板换会话、关面板)与这一轮已无关:排着的确认留给用户
        await _stopRegistration.DisposeAsync().ConfigureAwait(false);
        await _registry.DisposeRunAsync(this).ConfigureAwait(false);
        if (Transfers is { } transfers) await transfers.DisposeAsync().ConfigureAwait(false);
        _cancelSource.Dispose();
    }
}
