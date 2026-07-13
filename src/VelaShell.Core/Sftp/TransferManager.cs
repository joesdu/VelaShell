using System.Collections.Concurrent;
using System.Threading.Channels;
using VelaShell.Core.Models;

namespace VelaShell.Core.Sftp;

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

    public TransferManager() : this(null) { }

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

    public IReadOnlyList<TransferTask> ActiveTransfers => [.. _allTransfers.Values.Where(t => t.Status == TransferStatus.InProgress)];

    public IReadOnlyList<TransferTask> QueuedTransfers => [.. _allTransfers.Values.Where(t => t.Status == TransferStatus.Queued)];

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

    public TransferTask? GetTransfer(Guid transferId) => _allTransfers.GetValueOrDefault(transferId);

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
