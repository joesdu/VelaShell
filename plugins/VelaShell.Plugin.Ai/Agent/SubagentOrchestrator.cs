using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace VelaShell.Plugin.Ai.Agent;

/// <summary>提交给受限子 Agent 的任务请求。</summary>
/// <param name="Prompt">子 Agent 要执行的分析任务。</param>
/// <param name="Role">任务角色的显示名称；未指定时使用默认分析角色。</param>
/// <param name="ModelId">已配置模型的标识；未指定时沿用父模型。</param>
/// <param name="AllowWrites">显式允许继承父轮授权的文件修改工具，默认只读。</param>
public sealed record SubagentRequest(string Prompt, string? Role, string? ModelId = null, bool AllowWrites = false);

/// <summary>子 Agent 对某个工具的调用次数。</summary>
/// <param name="Name">工具名称。</param>
/// <param name="Count">调用次数。</param>
public sealed record SubagentToolUsage(string Name, int Count);

/// <summary>子 Agent 执行完成或失败后的结构化结果。</summary>
/// <param name="Output">子 Agent 输出的文本。</param>
/// <param name="ToolsUsed">子 Agent 使用过的工具及调用次数。</param>
/// <param name="Errors">执行期间产生的错误或权限拒绝信息。</param>
public sealed record SubagentExecutionResult(
    string Output,
    IReadOnlyList<SubagentToolUsage> ToolsUsed,
    IReadOnlyList<string> Errors)
{
    /// <summary>从纯文本输出创建没有工具和错误的结果。</summary>
    /// <param name="output">子 Agent 输出的文本。</param>
    public static SubagentExecutionResult FromText(string output)
        => new(output, [], []);
}

/// <summary>用于聊天 UI 和状态接口的子 Agent 运行快照。</summary>
/// <param name="SubagentId">子 Agent 标识。</param>
/// <param name="Role">子 Agent 角色。</param>
/// <param name="Status">排队、运行中、完成、失败、取消或超时状态。</param>
/// <param name="Summary">从最终输出提取的简短摘要。</param>
/// <param name="Output">完整输出文本；未结束或中途停下时是到目前为止收到的正文。</param>
/// <param name="ToolsUsed">使用过的工具及调用次数。</param>
/// <param name="Errors">执行期间产生的错误或权限拒绝信息。</param>
/// <param name="Elapsed">从排队到结束或当前时刻的耗时。</param>
/// <param name="ModelId">本任务选择的模型配置标识。</param>
/// <param name="TaskPrompt">创建任务时的请求，用于运行卡片的任务摘要。</param>
/// <param name="Activity">运行中此刻在做什么：正在调用的工具及其目标，或 <see cref="SubagentOrchestrator.WaitingForApproval" /> / <see cref="SubagentOrchestrator.WaitingForFileLock" />。</param>
/// <param name="InputTokens">供应商已报告的输入 token 累计。</param>
/// <param name="OutputTokens">供应商已报告的输出 token 累计。</param>
/// <param name="AllowWrites">本次委派是否开放了文件修改工具。</param>
public sealed record SubagentSnapshot(
    string SubagentId,
    string Role,
    string Status,
    string Summary,
    string Output,
    IReadOnlyList<SubagentToolUsage> ToolsUsed,
    IReadOnlyList<string> Errors,
    TimeSpan Elapsed,
    string? ModelId = null,
    string TaskPrompt = "",
    string Activity = "",
    long InputTokens = 0,
    long OutputTokens = 0,
    bool AllowWrites = false)
{
    /// <summary>已经落到终态（完成、失败、取消或超时）。</summary>
    public bool IsTerminal => Status is not ("queued" or "running");
}

/// <summary>
/// 管理当前 Agent 委托出去的受限子任务。子任务使用父模型或显式指定的已配置模型，
/// 继承父轮次授权，默认只读，不能递归创建子 Agent。
/// </summary>
/// <remarks>
/// 结果有两条路回到父 Agent：模型自己调 <c>subagent_wait</c> / <c>subagent_status</c> 取走，
/// 或由 <see cref="Chat.AgentRunCoordinator" /> 在父模型停下后等到终态、当作通知送回去
/// （见 <see cref="WaitForCompletionAsync" />）。每个终态结果只送一次。
/// </remarks>
public sealed class SubagentOrchestrator(
    Func<SubagentRequest, CancellationToken, Task<SubagentExecutionResult>> runner,
    CancellationToken parentCancellation,
    TimeSpan? timeout = null) : IAsyncDisposable
{
    /// <summary><see cref="SubagentSnapshot.Activity" /> 的取值：正在等用户审批。</summary>
    public const string WaitingForApproval = "waiting_approval";

    /// <summary><see cref="SubagentSnapshot.Activity" /> 的取值：正在排队等文件修改串行锁。</summary>
    public const string WaitingForFileLock = "waiting_file_lock";

    /// <summary>单个子任务执行期限的可选范围（秒）。</summary>
    public const int MinTimeoutSeconds = 30, MaxTimeoutSeconds = 600;

    private const int MaxConcurrent = 4;

    /// <summary>一个父轮最多委派多少个子任务(桌面与 IM 的默认值)。</summary>
    public const int DefaultMaxTotal = 8;

    /// <summary>交回父模型的正文上限；卡片与历史里仍是完整的。</summary>
    private const int MaxModelOutputChars = 24_000;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    /// <summary>当前异步流属于哪个子任务；工具包装、审批与文件锁据此找到条目。</summary>
    private static readonly AsyncLocal<Entry?> CurrentEntry = new();

    private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Lock _completionGate = new();
    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;
    private TaskCompletionSource _completionSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _started;
    private int _disposed;

    private sealed class Entry(SubagentOrchestrator owner, string id, int sequence, CancellationTokenSource cancellation)
    {
        public SubagentOrchestrator Owner { get; } = owner;
        public string Id { get; } = id;
        public int Sequence { get; } = sequence;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task<SubagentExecutionResult>? Task { get; set; }
        public string Status { get; set; } = "queued";
        public string? Role { get; set; }
        public string? ModelId { get; set; }
        public string TaskPrompt { get; init; } = "";
        public bool AllowWrites { get; init; }
        public TimeSpan Timeout { get; init; }
        public SubagentExecutionResult? Result { get; set; }
        public string? Error { get; set; }
        public Dictionary<string, int> LiveTools { get; } = new(StringComparer.Ordinal);
        public List<string> LiveErrors { get; } = [];
        public string LiveOutput { get; set; } = "";
        public string ToolActivity { get; set; } = "";
        public string WaitActivity { get; set; } = "";
        public RunClock? Clock { get; set; }
        public long InputTokens;
        public long OutputTokens;
        public bool Reported { get; set; }
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? FinishedAt { get; set; }
    }

    /// <summary>
    /// 可暂停的执行期限：等用户审批、排队等文件锁的时间不计入。
    /// 那两段子任务自己一步都没走，算进去的话用户晚点一下审批，任务就超时了。
    /// </summary>
    private sealed class RunClock(CancellationTokenSource cancellation, TimeSpan budget)
    {
        private readonly Lock _gate = new();
        private TimeSpan _remaining = budget;
        private long _resumedAt;
        private int _pauses;

        public void Start()
        {
            lock (_gate)
            {
                _resumedAt = Stopwatch.GetTimestamp();
                Arm(_remaining);
            }
        }

        public void Pause()
        {
            lock (_gate)
            {
                if (_pauses++ > 0) return;
                _remaining -= Stopwatch.GetElapsedTime(_resumedAt);
                // 已经用完就让它照常到期；否则停表
                if (_remaining > TimeSpan.Zero) Arm(System.Threading.Timeout.InfiniteTimeSpan);
            }
        }

        public void Resume()
        {
            lock (_gate)
            {
                if (--_pauses > 0) return;
                _resumedAt = Stopwatch.GetTimestamp();
                Arm(_remaining > TimeSpan.Zero ? _remaining : TimeSpan.Zero);
            }
        }

        private void Arm(TimeSpan delay)
        {
            try { cancellation.CancelAfter(delay); }
            catch (ObjectDisposedException) { } // 任务已经收尾，迟到的恢复不再有意义
        }
    }

    private sealed class TimeoutSuspension(Entry entry, string activity) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (entry.WaitActivity == activity) entry.WaitActivity = "";
            entry.Clock?.Resume();
            entry.Owner.NotifyChanged();
        }
    }

    /// <summary>使用纯文本委托函数创建子任务编排器。</summary>
    /// <param name="runner">执行子任务并返回文本结果的委托。</param>
    /// <param name="parentCancellation">父 Agent 轮次的取消令牌。</param>
    public SubagentOrchestrator(
        Func<string, CancellationToken, Task<string>> runner,
        CancellationToken parentCancellation)
        : this(async (request, token) =>
            SubagentExecutionResult.FromText(await runner(request.Prompt, token).ConfigureAwait(false)),
            parentCancellation)
    {
    }

    /// <summary>允许父轮把已发现的只读 MCP 工具注入后续子任务。</summary>
    public IReadOnlyList<AITool> ReadOnlyMcpTools { get; set; } = [];

    /// <summary>
    /// 这个编排器一共能启动多少个子任务。桌面与 IM 一轮一个编排器,用默认值;
    /// 对外 MCP 一条连接一个编排器,连接比一轮长得多,由入口另给上限。
    /// </summary>
    public int MaxTotal { get; init; } = DefaultMaxTotal;

    /// <summary>
    /// 结果会不会在终态后自动送回发起方(桌面与 IM 由 <see cref="Chat.AgentRunCoordinator" /> 送)。
    /// 对外 MCP 唤不醒外部模型,那边为 false:回执与工具说明改为让对方自己用 <c>subagent_wait</c> 取。
    /// </summary>
    public bool DeliversResults { get; init; } = true;

    /// <summary>还有子任务在排队或运行(对外 MCP 据此不回收仍在干活的连接)。</summary>
    public bool HasActive => _entries.Values.Any(entry => entry.FinishedAt is null);

    /// <summary>未指定模型时使用的父模型配置标识。</summary>
    public string? DefaultModelId { get; set; }

    /// <summary>可选模型的配置标识与显示名称，不包含凭据或端点。</summary>
    public IReadOnlyDictionary<string, string> ModelChoices { get; init; } = new Dictionary<string, string>();

    /// <summary>子任务状态或工具使用记录发生变化时触发。</summary>
    public event Action<IReadOnlyList<SubagentSnapshot>>? SnapshotsChanged;

    /// <summary>还有没交回父 Agent 的结果：仍在排队 / 运行，或已到终态但没人取走。</summary>
    public bool HasPendingResults => _entries.Values.Any(entry => !entry.Reported);

    /// <summary>当前异步流所属子任务的标签（<c>id · 角色</c>）；不在子任务里时为 null。审批卡用它标明来源。</summary>
    public static string? CurrentLabel
        => CurrentEntry.Value is { } entry ? $"{entry.Id} · {entry.Role ?? "analysis"}" : null;

    /// <summary>
    /// 当前异步流属于某个子任务时，暂停它的执行期限直到返回值被释放；不在子任务里时返回 null。
    /// </summary>
    /// <param name="activity"><see cref="WaitingForApproval" /> 或 <see cref="WaitingForFileLock" />，显示在运行卡片上。</param>
    public static IDisposable? SuspendCurrentTimeout(string activity)
    {
        if (CurrentEntry.Value is not { } entry)
        {
            return null;
        }
        entry.Clock?.Pause();
        entry.WaitActivity = activity;
        entry.Owner.NotifyChanged();
        return new TimeoutSuspension(entry, activity);
    }

    /// <summary>启动一个受限子任务并立即返回任务 id；文件写入须显式开启。</summary>
    /// <param name="prompt">交给子 Agent 的任务。</param>
    /// <param name="role">角色显示名。</param>
    /// <param name="modelId">已配置模型的标识；为空沿用父模型。</param>
    /// <param name="allowWrites">是否开放文件修改工具。</param>
    /// <param name="timeoutSeconds">
    /// 执行期限（秒），夹在 <see cref="MinTimeoutSeconds" /> 与 <see cref="MaxTimeoutSeconds" /> 之间；
    /// 不大于 0 时用默认值。等审批与等文件锁的时间不计入。
    /// </param>
    public string Start(string prompt, string? role, string? modelId = null, bool allowWrites = false,
        int timeoutSeconds = 0)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return "Subagent service is shutting down.";
        }
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return "A non-empty subagent prompt is required.";
        }
        int sequence = Interlocked.Increment(ref _started);
        if (sequence > MaxTotal)
        {
            Interlocked.Decrement(ref _started);
            return $"The maximum of {MaxTotal} subagents has been reached.";
        }

        string id = $"sub-{Guid.NewGuid():N}"[..12];
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(parentCancellation);
        var entry = new Entry(this, id, sequence, cancellation)
        {
            TaskPrompt = prompt,
            AllowWrites = allowWrites,
            Role = role,
            ModelId = string.IsNullOrWhiteSpace(modelId) ? DefaultModelId : modelId.Trim(),
            Timeout = timeoutSeconds > 0
                ? TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, MinTimeoutSeconds, MaxTimeoutSeconds))
                : _timeout
        };
        _entries[id] = entry;
        entry.Task = RunAsync(entry, prompt, role);
        NotifyChanged();
        return DeliversResults
            ? $"Started subagent {id}. Its result is delivered to you automatically when it finishes, so do not poll. "
              + "Call subagent_wait with this id only if you need the result before doing anything else."
            : $"Started subagent {id}. Results are NOT pushed to you: call subagent_wait with this id to collect the result.";
    }

    /// <summary>取得子任务当前状态和已完成结果。</summary>
    public string Status(string id)
    {
        if (!_entries.TryGetValue(id, out Entry? entry))
        {
            return $"Unknown subagent id '{id}'.";
        }
        return Format(entry);
    }

    /// <summary>等待一个子任务，超时只返回状态，不取消子任务。</summary>
    public async Task<string> WaitAsync(string id, int timeoutSeconds, CancellationToken cancellationToken)
    {
        if (!_entries.TryGetValue(id, out Entry? entry))
        {
            return $"Unknown subagent id '{id}'.";
        }
        Task<SubagentExecutionResult> task = entry.Task
            ?? Task.FromResult(SubagentExecutionResult.FromText("Subagent was not scheduled."));
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 120)), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 仍在运行：交回当前状态（耗时、工具计数会变），而不是一句每次都一样的话
        }
        return Format(entry);
    }

    /// <summary>
    /// 等到至少一个子任务落到终态，返回所有<b>还没交回父 Agent</b> 的终态结果并标记为已交回。
    /// 没有在途任务、也没有待交回的结果时立即返回空列表。
    /// </summary>
    /// <param name="cancellationToken">父轮的取消令牌；取消只结束等待，不取消子任务。</param>
    public async Task<IReadOnlyList<SubagentSnapshot>> WaitForCompletionAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task signal;
            lock (_completionGate)
            {
                List<SubagentSnapshot> finished = [];
                bool active = false;
                foreach (Entry entry in _entries.Values.OrderBy(e => e.Sequence))
                {
                    if (entry.FinishedAt is null)
                    {
                        active = true;
                    }
                    else if (!entry.Reported)
                    {
                        entry.Reported = true;
                        finished.Add(Snapshot(entry));
                    }
                }
                if (finished.Count > 0 || !active)
                {
                    return finished;
                }
                signal = _completionSignal.Task;
            }
            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>返回给运行状态 UI 的结构化快照，按创建顺序排列。</summary>
    public IReadOnlyList<SubagentSnapshot> Snapshots()
        => [.. _entries.Values.OrderBy(entry => entry.Sequence).Select(Snapshot)];

    /// <summary>记下当前子任务开始调用某个工具，供运行卡片显示"此刻在做什么"。</summary>
    /// <param name="name">工具名。</param>
    /// <param name="target">这次调用的目标（路径、URL、检索词等）；没有就留空。</param>
    public void BeginTool(string name, string target)
    {
        if (CurrentEntry.Value is not { } entry || !ReferenceEquals(entry.Owner, this))
        {
            return;
        }
        entry.ToolActivity = target.Length > 0 ? $"{name} · {target}" : name;
        NotifyChanged();
    }

    /// <summary>记录当前子任务的工具调用，供运行状态 UI 显示实时计数。</summary>
    public void RecordToolUsage(string name, object? result)
    {
        if (CurrentEntry.Value is not { } entry || !ReferenceEquals(entry.Owner, this))
        {
            return;
        }
        lock (entry.LiveTools)
        {
            entry.LiveTools[name] = entry.LiveTools.GetValueOrDefault(name) + 1;
            if (result is Exception exception)
            {
                entry.LiveErrors.Add($"{name}: {ClipError(exception.GetBaseException().Message)}");
            }
            else if (result is string text && IsDenialResult(text))
            {
                entry.LiveErrors.Add($"{name}: {ClipError(text)}");
            }
        }
        entry.ToolActivity = "";
        NotifyChanged();
    }

    /// <summary>
    /// 更新当前子任务到目前为止的正文。不发变更通知：卡片按固定间隔自己来取，
    /// 逐块通知会让流式输出把 UI 调度器打满。
    /// </summary>
    public void ReportOutput(string text)
    {
        if (CurrentEntry.Value is { } entry && ReferenceEquals(entry.Owner, this))
        {
            entry.LiveOutput = text;
        }
    }

    /// <summary>累计当前子任务的 token 用量（供应商已报告的部分）。</summary>
    public void RecordTokens(long inputTokens, long outputTokens)
    {
        if (CurrentEntry.Value is not { } entry || !ReferenceEquals(entry.Owner, this))
        {
            return;
        }
        Interlocked.Add(ref entry.InputTokens, inputTokens);
        Interlocked.Add(ref entry.OutputTokens, outputTokens);
        NotifyChanged();
    }

    /// <summary>
    /// 工具返回的是不是"被拒/无审批通道"那几句固定话术。只认开头:读到的文件或终端输出里
    /// 出现 "Permission denied" 之类再正常不过,按包含匹配会把整段内容当错误塞回父 Agent。
    /// </summary>
    /// <param name="text">工具的字符串结果。</param>
    public static bool IsDenialResult(string text)
        => text.StartsWith("The user DENIED ", StringComparison.Ordinal)
           || text.StartsWith("The host DENIED ", StringComparison.Ordinal)
           || text.StartsWith("No approval channel", StringComparison.Ordinal);

    private static string ClipError(string text) => text.Length <= 300 ? text : text[..300] + "…";

    /// <summary>请求取消一个子任务。</summary>
    public string Cancel(string id)
    {
        if (!_entries.TryGetValue(id, out Entry? entry))
        {
            return $"Unknown subagent id '{id}'.";
        }
        if (entry.FinishedAt is not null)
        {
            return $"Subagent {id} has already finished ({entry.Status}).";
        }
        try { entry.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        return $"Cancellation requested for subagent {id}. Changes it already made are not rolled back.";
    }

    private async Task<SubagentExecutionResult> RunAsync(Entry entry, string prompt, string? role)
    {
        bool acquired = false;
        try
        {
            await _slots.WaitAsync(entry.Cancellation.Token).ConfigureAwait(false);
            acquired = true;
            entry.Status = "running";
            NotifyChanged();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(entry.Cancellation.Token);
            var clock = new RunClock(timeout, entry.Timeout);
            entry.Clock = clock;
            clock.Start();
            CurrentEntry.Value = entry;
            entry.Result = await runner(new SubagentRequest(prompt.Trim(), role, entry.ModelId, entry.AllowWrites), timeout.Token)
                .ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            entry.Status = "completed";
            return entry.Result;
        }
        catch (OperationCanceledException) when (entry.Cancellation.IsCancellationRequested)
        {
            entry.Status = "cancelled";
            entry.Error = "The subagent was cancelled.";
            return new SubagentExecutionResult("", [], [entry.Error]);
        }
        catch (OperationCanceledException)
        {
            entry.Status = "timed_out";
            entry.Error = $"The subagent exceeded {entry.Timeout.TotalSeconds:0.###} seconds.";
            return new SubagentExecutionResult("", [], [entry.Error]);
        }
        catch (Exception ex)
        {
            entry.Status = "failed";
            entry.Error = ex.GetBaseException().Message;
            return new SubagentExecutionResult("", [], [entry.Error]);
        }
        finally
        {
            entry.Clock = null;
            entry.ToolActivity = "";
            entry.WaitActivity = "";
            CurrentEntry.Value = null;
            if (acquired)
            {
                _slots.Release();
            }
            TaskCompletionSource signal;
            lock (_completionGate)
            {
                entry.FinishedAt = DateTimeOffset.UtcNow;
                signal = _completionSignal;
                _completionSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            signal.TrySetResult();
            NotifyChanged();
        }
    }

    private string Format(Entry entry)
    {
        SubagentSnapshot snapshot;
        lock (_completionGate)
        {
            snapshot = Snapshot(entry);
            // 终态结果经工具交给了模型：协调器不必再当通知送一遍
            if (snapshot.IsTerminal && entry.FinishedAt is not null)
            {
                entry.Reported = true;
            }
        }
        return JsonSerializer.Serialize(ModelView(snapshot));
    }

    /// <summary>交给父模型的结构化结果：状态、正文、工具、错误与耗时。</summary>
    /// <param name="snapshot">子任务快照。</param>
    public static object ModelView(SubagentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        // 运行中不把半截正文喂给父模型；没跑完就停下的那几种，留下的半截就是全部可用的东西
        string output = snapshot.IsTerminal ? snapshot.Output : "";
        bool clipped = output.Length > MaxModelOutputChars;
        return new
        {
            subagentId = snapshot.SubagentId,
            role = snapshot.Role,
            modelId = snapshot.ModelId,
            allowWrites = snapshot.AllowWrites,
            status = snapshot.Status,
            activity = snapshot.Activity.Length > 0 ? snapshot.Activity : null,
            output = clipped ? output[..MaxModelOutputChars] : output,
            outputTruncated = clipped,
            outputIsPartial = snapshot.Status is not ("completed" or "queued" or "running") && output.Length > 0,
            toolsUsed = snapshot.ToolsUsed.Select(tool => new { name = tool.Name, count = tool.Count }),
            errors = snapshot.Errors,
            elapsedMs = (long)snapshot.Elapsed.TotalMilliseconds
        };
    }

    private static SubagentSnapshot Snapshot(Entry entry)
    {
        TimeSpan elapsed = (entry.FinishedAt ?? DateTimeOffset.UtcNow) - entry.StartedAt;
        IReadOnlyList<SubagentToolUsage> liveTools;
        IReadOnlyList<string> liveErrors;
        lock (entry.LiveTools)
        {
            liveTools = [.. entry.LiveTools.Select(pair => new SubagentToolUsage(pair.Key, pair.Value))];
            liveErrors = [.. entry.LiveErrors];
        }
        // 只有正常跑完才采用 runner 交回的那份；中途停下时它没有返回值，留下实时记的
        SubagentExecutionResult? result = entry.Status == "completed" ? entry.Result : null;
        IReadOnlyList<SubagentToolUsage> tools = result?.ToolsUsed is { Count: > 0 } resultTools
            ? resultTools
            : liveTools;
        IReadOnlyList<string> errors = result?.Errors is { } resultErrors
            ? [.. liveErrors, .. resultErrors]
            : liveErrors;
        string output = result?.Output ?? entry.LiveOutput;
        return new(
            entry.Id,
            entry.Role ?? "analysis",
            entry.Status,
            Summary(output.Length > 0 ? output : entry.Error),
            output,
            tools,
            entry.Error is { } statusError ? [.. errors, statusError] : errors,
            elapsed,
            entry.ModelId,
            entry.TaskPrompt,
            entry.WaitActivity.Length > 0 ? entry.WaitActivity : entry.ToolActivity,
            Interlocked.Read(ref entry.InputTokens),
            Interlocked.Read(ref entry.OutputTokens),
            entry.AllowWrites);
    }

    private void NotifyChanged()
    {
        SnapshotsChanged?.Invoke(Snapshots());
    }

    internal static class SubagentToolTracker
    {
        /// <summary>给子任务的工具套上计数与"此刻在做什么"的记录。</summary>
        public static IReadOnlyList<AITool> Wrap(IEnumerable<AITool> tools, SubagentOrchestrator owner)
            => [.. tools.Select(tool => tool is AIFunction function
                ? new CountingFunction(function, owner)
                : tool)];

        /// <summary>从参数里挑一个最能说明"这次调用冲着什么去"的值。</summary>
        internal static string Target(AIFunctionArguments arguments)
        {
            foreach (string key in (ReadOnlySpan<string>)["path", "url", "query", "pattern", "from", "sessionId"])
            {
                if (arguments.TryGetValue(key, out object? value)
                    && (value is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString() : value as string)
                        is { Length: > 0 } text)
                {
                    text = text.ReplaceLineEndings(" ").Trim();
                    return text.Length <= 80 ? text : "…" + text[^79..];
                }
            }
            return "";
        }

        private sealed class CountingFunction(AIFunction inner, SubagentOrchestrator owner)
            : DelegatingAIFunction(inner)
        {
            protected override async ValueTask<object?> InvokeCoreAsync(
                AIFunctionArguments arguments,
                CancellationToken cancellationToken)
            {
                owner.BeginTool(Name, Target(arguments));
                try
                {
                    object? result = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
                    owner.RecordToolUsage(Name, result);
                    return result;
                }
                catch (Exception ex)
                {
                    owner.RecordToolUsage(Name, ex);
                    throw;
                }
            }
        }
    }

    private static string Summary(string? output)
    {
        string text = output?.ReplaceLineEndings(" ").Trim() ?? "";
        return text.Length <= 160 ? text : text[..160] + "…";
    }

    /// <summary>取消并等待当前编排器中的全部子任务。</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        foreach (Entry entry in _entries.Values)
        {
            entry.Cancellation.Cancel();
        }
        Task[] tasks = [.. _entries.Values.Select(e => e.Task).OfType<Task>()];
        if (tasks.Length > 0)
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        foreach (Entry entry in _entries.Values)
        {
            entry.Cancellation.Dispose();
        }
        _slots.Dispose();
    }
}
