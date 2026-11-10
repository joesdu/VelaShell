using System.Collections.Concurrent;
namespace VelaShell.Plugin.Ai.Agent;

/// <summary>
/// 管理当前 Agent 委托出去的只读子任务。子任务使用父轮次已经验证过的模型和凭据，
/// 但不继承工具循环，因此不能递归创建子 Agent。
/// </summary>
public sealed class SubagentOrchestrator(
    Func<string, CancellationToken, Task<string>> runner,
    CancellationToken parentCancellation) : IAsyncDisposable
{
    private const int MaxConcurrent = 4;
    private const int MaxTotal = 8;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);
    private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int _started;
    private int _disposed;

    private sealed class Entry(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task<string>? Task { get; set; }
        public string Status { get; set; } = "queued";
        public string? Result { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>启动一个受限只读子任务并立即返回任务 id。</summary>
    public string Start(string prompt, string? role)
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
        CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(parentCancellation);
        var entry = new Entry(cancellation);
        _entries[id] = entry;
        entry.Task = RunAsync(id, entry, prompt, role);
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
        Task<string> task = entry.Task ?? Task.FromResult("Subagent was not scheduled.");
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

    private async Task<string> RunAsync(string id, Entry entry, string prompt, string? role)
    {
        bool acquired = false;
        try
        {
            await _slots.WaitAsync(entry.Cancellation.Token).ConfigureAwait(false);
            acquired = true;
            entry.Status = "running";
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(entry.Cancellation.Token);
            timeout.CancelAfter(DefaultTimeout);
            string instruction = string.IsNullOrWhiteSpace(role)
                ? prompt.Trim()
                : $"You are a specialist subagent focused on {role.Trim()}.\n\n{prompt.Trim()}";
            entry.Result = await runner(instruction, timeout.Token).ConfigureAwait(false);
            entry.Status = "completed";
            return entry.Result;
        }
        catch (OperationCanceledException) when (entry.Cancellation.IsCancellationRequested)
        {
            entry.Status = "cancelled";
            entry.Error = "The subagent was cancelled.";
            return entry.Error;
        }
        catch (OperationCanceledException)
        {
            entry.Status = "timed_out";
            entry.Error = $"The subagent exceeded {DefaultTimeout.TotalSeconds:0} seconds.";
            return entry.Error;
        }
        catch (Exception ex)
        {
            entry.Status = "failed";
            entry.Error = ex.GetBaseException().Message;
            return entry.Error;
        }
        finally
        {
            if (acquired)
            {
                _slots.Release();
            }
        }
    }

    private static string Format(string id, Entry entry)
        => entry.Status is "completed"
            ? $"Subagent {id} completed:\n{entry.Result}"
            : $"Subagent {id}: {entry.Status}"
              + (string.IsNullOrWhiteSpace(entry.Error) ? "" : $"\n{entry.Error}");

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
        Task[] tasks = _entries.Values.Select(e => e.Task).OfType<Task>().ToArray();
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
