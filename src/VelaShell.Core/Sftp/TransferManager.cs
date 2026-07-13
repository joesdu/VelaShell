using System.Collections.Concurrent;
using System.Threading.Channels;
using VelaShell.Core.Models;

namespace VelaShell.Core.Sftp;

/// <summary>SFTP 传输调度器:把上传/下载任务排入无界队列,按并发上限并行执行,并跟踪其状态与进度。</summary>
public class TransferManager : ITransferManager
{
    private readonly ConcurrentDictionary<Guid, TransferTask> _allTransfers = new();
    private readonly Channel<TransferTask> _channel;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly TransferExecutor? _executor;
    private readonly Lock _processorLock = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _transferCts = new();
    private SemaphoreSlim _concurrencySemaphore;
    private bool _disposed;
    private Task? _processorTask;

    /// <summary>创建不带执行委托的传输调度器(仅做状态/队列管理,不实际搬运数据)。</summary>
    public TransferManager() : this(null) { }

    /// <summary>创建传输调度器,并指定实际执行单个传输任务的 <paramref name="executor" /> 委托。</summary>
    /// <param name="executor">实际执行传输并上报进度的委托;为 <c>null</c> 时任务直接标记完成。</param>
    public TransferManager(TransferExecutor? executor)
    {
        _executor = executor;
        _concurrencySemaphore = new(MaxConcurrentTransfers, MaxConcurrentTransfers);
        _channel = Channel.CreateUnbounded<TransferTask>(new()
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    /// <summary>最大并发传输数(默认 3);只能在开始处理任务前修改,值须大于 0。</summary>
    public int MaxConcurrentTransfers
    {
        get;
        set
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), @"MaxConcurrentTransfers must be greater than 0");
            }
            if (_processorTask is not null)
            {
                throw new InvalidOperationException("Cannot change MaxConcurrentTransfers while transfers are being processed.");
            }
            field = value;
            _concurrencySemaphore.Dispose();
            _concurrencySemaphore = new(value, value);
        }
    } = 3;

    /// <summary>当前正在传输(<see cref="TransferStatus.InProgress" />)的任务快照。</summary>
    public IReadOnlyList<TransferTask> ActiveTransfers => [.. _allTransfers.Values.Where(t => t.Status == TransferStatus.InProgress)];

    /// <summary>当前处于排队(<see cref="TransferStatus.Queued" />)等待执行的任务快照。</summary>
    public IReadOnlyList<TransferTask> QueuedTransfers => [.. _allTransfers.Values.Where(t => t.Status == TransferStatus.Queued)];

    /// <summary>将传输任务加入队列并确保处理循环运行;任务即刻置为已排队状态。</summary>
    public Task QueueTransferAsync(TransferTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var taskCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        _transferCts[task.Id] = taskCts;
        _allTransfers[task.Id] = task;
        task.Status = TransferStatus.Queued;
        _channel.Writer.TryWrite(task);
        EnsureProcessorRunning();
        return Task.CompletedTask;
    }

    /// <summary>按标识取消指定传输:标记为已取消并触发其取消令牌。</summary>
    public Task CancelTransferAsync(Guid transferId, CancellationToken cancellationToken = default)
    {
        // ReSharper disable once InvertIf
        if (_allTransfers.TryGetValue(transferId, out TransferTask? task))
        {
            task.Status = TransferStatus.Cancelled;
            if (_transferCts.TryGetValue(transferId, out CancellationTokenSource? cts))
            {
                cts.Cancel();
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>按标识获取传输任务;不存在时返回 <c>null</c>。</summary>
    public TransferTask? GetTransfer(Guid transferId) => _allTransfers.GetValueOrDefault(transferId);

    /// <summary>释放调度器:结束队列、取消所有在途任务并释放相关资源。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _channel.Writer.TryComplete();
        _disposeCts.Cancel();
        _disposeCts.Dispose();
        _concurrencySemaphore.Dispose();
        foreach (CancellationTokenSource cts in _transferCts.Values)
        {
            cts.Dispose();
        }
        _transferCts.Clear();
        GC.SuppressFinalize(this);
    }

    private void EnsureProcessorRunning()
    {
        if (_processorTask is not null)
        {
            return;
        }
        lock (_processorLock)
        {
            _processorTask ??= Task.Run(() => ProcessTransferChannelAsync(_disposeCts.Token));
        }
    }

    private async Task ProcessTransferChannelAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (TransferTask task in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (task.Status == TransferStatus.Cancelled)
                {
                    continue;
                }
                await _concurrencySemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                _ = ExecuteTransferAsync(task, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ExecuteTransferAsync(TransferTask task, CancellationToken processorToken)
    {
        _transferCts.TryGetValue(task.Id, out CancellationTokenSource? taskCts);
        CancellationToken transferToken = taskCts?.Token ?? processorToken;
        try
        {
            if (task.Status == TransferStatus.Cancelled)
            {
                return;
            }
            task.Status = TransferStatus.InProgress;
            if (_executor is not null)
            {
                var progress = new Progress<TransferProgress>(p => task.Progress = p);
                await _executor(task, progress, transferToken).ConfigureAwait(false);
            }
            if (task.Status == TransferStatus.InProgress)
            {
                task.Status = TransferStatus.Completed;
            }
        }
        catch (OperationCanceledException)
        {
            task.Status = TransferStatus.Cancelled;
        }
        catch (Exception)
        {
            task.Status = TransferStatus.Failed;
        }
        finally
        {
            _concurrencySemaphore.Release();
            if (_transferCts.TryRemove(task.Id, out CancellationTokenSource? cts))
            {
                cts.Dispose();
            }
        }
    }
}
