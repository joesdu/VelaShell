using System.Collections.Concurrent;
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
/// <param name="Output">完整输出文本。</param>
/// <param name="ToolsUsed">使用过的工具及调用次数。</param>
/// <param name="Errors">执行期间产生的错误或权限拒绝信息。</param>
/// <param name="Elapsed">从排队到结束或当前时刻的耗时。</param>
/// <param name="ModelId">本任务选择的模型配置标识。</param>
/// <param name="TaskPrompt">创建任务时的请求，用于运行卡片的任务摘要。</param>
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
    string TaskPrompt = "");

/// <summary>
/// 管理当前 Agent 委托出去的受限子任务。子任务使用父模型或显式指定的已配置模型，
/// 继承父轮次授权，默认只读，不能递归创建子 Agent。
/// </summary>
public sealed class SubagentOrchestrator(
    Func<SubagentRequest, CancellationToken, Task<SubagentExecutionResult>> runner,
    CancellationToken parentCancellation,
    TimeSpan? timeout = null) : IAsyncDisposable
{
    private const int MaxConcurrent = 4;
    private const int MaxTotal = 8;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);
    private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly AsyncLocal<Entry?> _currentEntry = new();
    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;
    private int _started;
    private int _disposed;

    private sealed class Entry(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task<SubagentExecutionResult>? Task { get; set; }
        public string Status { get; set; } = "queued";
        public string? Role { get; set; }
        public string? ModelId { get; set; }
        public string TaskPrompt { get; init; } = "";
        public bool AllowWrites { get; init; }
        public SubagentExecutionResult? Result { get; set; }
        public string? Error { get; set; }
        public Dictionary<string, int> LiveTools { get; } = new(StringComparer.Ordinal);
        public List<string> LiveErrors { get; } = [];
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? FinishedAt { get; set; }
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

    /// <summary>未指定模型时使用的父模型配置标识。</summary>
    public string? DefaultModelId { get; set; }

    /// <summary>可选模型的配置标识与显示名称，不包含凭据或端点。</summary>
    public IReadOnlyDictionary<string, string> ModelChoices { get; init; } = new Dictionary<string, string>();

    /// <summary>子任务状态或工具使用记录发生变化时触发。</summary>
    public event Action<IReadOnlyList<SubagentSnapshot>>? SnapshotsChanged;

    /// <summary>启动一个受限子任务并立即返回任务 id；文件写入须显式开启。</summary>
    public string Start(string prompt, string? role, string? modelId = null, bool allowWrites = false)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return "Subagent service is shutting down.";
        }
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return "A non-empty subagent prompt is required.";
        }
        if (Interlocked.Increment(ref _started) > MaxTotal)
        {
            Interlocked.Decrement(ref _started);
            return $"The maximum of {MaxTotal} subagents per parent turn has been reached.";
        }

        string id = $"sub-{Guid.NewGuid():N}"[..12];
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(parentCancellation);
        var entry = new Entry(cancellation)
        {
            TaskPrompt = prompt,
            AllowWrites = allowWrites,
            Role = role,
            ModelId = string.IsNullOrWhiteSpace(modelId) ? DefaultModelId : modelId.Trim()
        };
        _entries[id] = entry;
        entry.Task = RunAsync(entry, prompt, role);
        NotifyChanged();
        return $"Started subagent {id}. Use subagent_status or subagent_wait with this id.";
    }

    /// <summary>取得子任务当前状态和已完成结果。</summary>
    public string Status(string id)
    {
        if (!_entries.TryGetValue(id, out Entry? entry))
        {
            return $"Unknown subagent id '{id}'.";
        }
        return Format(id, entry);
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
            return $"Subagent {id} is still running. Use subagent_status to check it.";
        }
        return Format(id, entry);
    }

    /// <summary>返回给运行状态 UI 的结构化快照。</summary>
    public IReadOnlyList<SubagentSnapshot> Snapshots()
        => [.. _entries.Select(pair => Snapshot(pair.Key, pair.Value)).OrderBy(item => item.SubagentId)];

    /// <summary>记录当前子任务的工具调用，供运行状态 UI 显示实时计数。</summary>
    public void RecordToolUsage(string name, object? result)
    {
        Entry? entry = _currentEntry.Value;
        if (entry is null)
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
        entry.Cancellation.Cancel();
        return $"Cancellation requested for subagent {id}.";
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
            timeout.CancelAfter(_timeout);
            _currentEntry.Value = entry;
            entry.Result = await runner(new SubagentRequest(prompt.Trim(), role, entry.ModelId, entry.AllowWrites), timeout.Token)
                .ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            entry.Status = "completed";
            NotifyChanged();
            return entry.Result;
        }
        catch (OperationCanceledException) when (entry.Cancellation.IsCancellationRequested)
        {
            entry.Status = "cancelled";
            entry.Error = "The subagent was cancelled.";
            NotifyChanged();
            return new SubagentExecutionResult("", [], [entry.Error]);
        }
        catch (OperationCanceledException)
        {
            entry.Status = "timed_out";
            entry.Error = $"The subagent exceeded {_timeout.TotalSeconds:0.###} seconds.";
            NotifyChanged();
            return new SubagentExecutionResult("", [], [entry.Error]);
        }
        catch (Exception ex)
        {
            entry.Status = "failed";
            entry.Error = ex.GetBaseException().Message;
            NotifyChanged();
            return new SubagentExecutionResult("", [], [entry.Error]);
        }
        finally
        {
            entry.FinishedAt = DateTimeOffset.UtcNow;
            _currentEntry.Value = null;
            if (acquired)
            {
                _slots.Release();
            }
            NotifyChanged();
        }

    }

    private static string Format(string id, Entry entry)
    {
        SubagentSnapshot snapshot = Snapshot(id, entry);
        return JsonSerializer.Serialize(new
        {
            subagentId = snapshot.SubagentId,
            role = snapshot.Role,
            modelId = snapshot.ModelId,
            allowWrites = entry.AllowWrites,
            status = snapshot.Status,
            summary = snapshot.Summary,
            output = snapshot.Output,
            toolsUsed = snapshot.ToolsUsed.Select(tool => new { name = tool.Name, count = tool.Count }),
            errors = snapshot.Errors,
            elapsedMs = (long)snapshot.Elapsed.TotalMilliseconds
        });
    }

    private static SubagentSnapshot Snapshot(string id, Entry entry)
    {
        TimeSpan elapsed = (entry.FinishedAt ?? DateTimeOffset.UtcNow) - entry.StartedAt;
        IReadOnlyList<SubagentToolUsage> liveTools;
        IReadOnlyList<string> liveErrors;
        lock (entry.LiveTools)
        {
            liveTools = [.. entry.LiveTools.Select(pair => new SubagentToolUsage(pair.Key, pair.Value))];
            liveErrors = [.. entry.LiveErrors];
        }
        IReadOnlyList<SubagentToolUsage> tools = entry.Result?.ToolsUsed is { Count: > 0 } resultTools
            ? resultTools
            : liveTools;
        IReadOnlyList<string> errors = entry.Result?.Errors is { } resultErrors
            ? [.. liveErrors, .. resultErrors]
            : liveErrors;
        return new(
            id,
            entry.Role ?? "analysis",
            entry.Status,
            Summary(entry.Result?.Output ?? entry.Error),
            entry.Result?.Output ?? "",
            tools,
            entry.Error is { } statusError ? [.. errors, statusError] : errors,
            elapsed,
            entry.ModelId,
            entry.TaskPrompt);
    }

    private void NotifyChanged()
    {
        SnapshotsChanged?.Invoke(Snapshots());
    }

    internal static class SubagentToolTracker
    {
        public static IReadOnlyList<AITool> Wrap(
            IEnumerable<AITool> tools,
            Action<string, object?> record)
            => [.. tools.Select(tool => tool is AIFunction function
                ? new CountingFunction(function, record)
                : tool)];

        private sealed class CountingFunction(AIFunction inner, Action<string, object?> record)
            : DelegatingAIFunction(inner)
        {
            protected override async ValueTask<object?> InvokeCoreAsync(
                AIFunctionArguments arguments,
                CancellationToken cancellationToken)
            {
                try
                {
                    object? result = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
                    record(Name, result);
                    return result;
                }
                catch (Exception ex)
                {
                    record(Name, ex);
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
