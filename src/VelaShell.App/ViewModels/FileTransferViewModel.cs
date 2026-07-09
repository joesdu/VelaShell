using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using ReactiveUI;

namespace VelaShell.App.ViewModels;

public class FileTransferViewModel : ReactiveObject
{
    private readonly ITransferManager _transferManager;
    private bool _isPanelVisible;
    private IDisposable? _autoHide;
    private bool _isPointerOver;
    private bool _hidePending;

    // Current batch (a folder/multi-file download or upload): the number of files still to finish
    // and the token source that cancels whatever remains, including the file in flight.
    private CancellationTokenSource? _batchCts;
    private int _batchRemaining;

    public FileTransferViewModel(ITransferManager? transferManager)
    {
        _transferManager = transferManager!;

        Transfers = new ObservableCollection<TransferItemViewModel>();
        Transfers.CollectionChanged += OnTransfersChanged;

        CancelTransferCommand = ReactiveCommand.Create<Guid>(CancelTransfer);
        RetryTransferCommand = ReactiveCommand.Create<Guid>(RetryTransfer);
        ClearCompletedCommand = ReactiveCommand.Create(ClearCompleted);
        CancelAllCommand = ReactiveCommand.Create(CancelAll);
        HidePanelCommand = ReactiveCommand.Create(() => { IsPanelVisible = false; });
    }

    public ObservableCollection<TransferItemViewModel> Transfers { get; }

    /// <summary>Count of in-flight tasks (in progress or queued).</summary>
    public int ActiveCount => Transfers.Count(t => t.IsActive);

    /// <summary>Whether a cancellable batch of transfers is currently running.</summary>
    public bool IsBatchActive => _batchCts is not null;

    /// <summary>Header badge (design 9Ralg): during a batch this is the number of files still to
    /// transfer (counting down), otherwise the number of in-flight single transfers. Fixes the
    /// badge previously being stuck at "1" because files are transferred one at a time.</summary>
    public int PendingCount => IsBatchActive ? _batchRemaining : ActiveCount;

    /// <summary>Begins a cancellable batch of <paramref name="totalFiles"/> transfers. The header
    /// then shows the remaining count and a cancel control that trips <paramref name="cts"/>.</summary>
    public void BeginBatch(int totalFiles, CancellationTokenSource cts)
    {
        _batchCts = cts;
        _batchRemaining = totalFiles;
        this.RaisePropertyChanged(nameof(IsBatchActive));
        this.RaisePropertyChanged(nameof(PendingCount));
    }

    /// <summary>Marks one file of the current batch as finished, decrementing the remaining count.</summary>
    public void NotifyBatchItemSettled()
    {
        if (_batchRemaining > 0)
            _batchRemaining--;
        this.RaisePropertyChanged(nameof(PendingCount));
    }

    /// <summary>Ends the current batch; the toast reverts to its idle badge and resumes auto-hide.</summary>
    public void EndBatch()
    {
        _batchCts = null;
        _batchRemaining = 0;
        this.RaisePropertyChanged(nameof(IsBatchActive));
        this.RaisePropertyChanged(nameof(PendingCount));

        // Re-evaluate auto-hide now that the batch (which suppressed it) is done.
        NotifyTaskSettled();
    }

    private void CancelAll()
    {
        // Cancel() runs the transfer's cancellation callbacks inline; guard so a misbehaving
        // callback can never crash the app from the cancel button.
        try
        {
            _batchCts?.Cancel();
        }
        catch
        {
            // Best-effort: the batch is already tearing down.
        }

        // Reflect the cancellation immediately; the running file unwinds as its stream closes.
        foreach (var item in Transfers.Where(t => t.IsActive).ToList())
            item.Status = TransferStatus.Cancelled;
    }

    /// <summary>
    /// The toast exists only while it has content and wasn't manually collapsed (spec §9):
    /// a new task fades it in, closing (x) only hides it — tasks keep running.
    /// </summary>
    public bool IsPanelVisible
    {
        get => _isPanelVisible;
        set => this.RaiseAndSetIfChanged(ref _isPanelVisible, value);
    }

    public ReactiveCommand<Unit, Unit> HidePanelCommand { get; }

    /// <summary>Reopens the transfer toast (from the toolbar "transfer history" button) so past and
    /// active transfers can be reviewed. Cancels any pending auto-hide and keeps it up until the
    /// user collapses it with the x.</summary>
    public void ShowPanel()
    {
        _autoHide?.Dispose();
        _autoHide = null;
        _hidePending = false;
        IsPanelVisible = true;
    }

    private void OnTransfersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        this.RaisePropertyChanged(nameof(ActiveCount));
        this.RaisePropertyChanged(nameof(PendingCount));
        if (Transfers.Count > 0)
            IsPanelVisible = true;
        else
            IsPanelVisible = false;
    }

    /// <summary>Call when a task finishes; once nothing is active the toast lingers ~3s
    /// showing the completed state, then fades out (spec §9.4). While the pointer is inside
    /// the toast the countdown is held — it only starts (or restarts) after the pointer leaves.</summary>
    public void NotifyTaskSettled()
    {
        this.RaisePropertyChanged(nameof(ActiveCount));
        this.RaisePropertyChanged(nameof(PendingCount));
        _autoHide?.Dispose();
        _autoHide = null;

        // Keep the toast up while more files in the batch are pending, or while any single
        // transfer is still in flight.
        if (ActiveCount > 0 || IsBatchActive)
        {
            _hidePending = false;
            return;
        }

        _hidePending = true;
        if (!_isPointerOver)
            ScheduleAutoHide();
    }

    /// <summary>Called by the view on pointer enter/leave: entering pauses any pending
    /// auto-hide so the user can inspect results; leaving resumes the 3s countdown.</summary>
    public void SetPointerOver(bool isOver)
    {
        _isPointerOver = isOver;

        if (isOver)
        {
            _autoHide?.Dispose();
            _autoHide = null;
            return;
        }

        if (_hidePending && ActiveCount == 0)
            ScheduleAutoHide();
    }

    private void ScheduleAutoHide()
    {
        _autoHide = Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            if (ActiveCount == 0 && !_isPointerOver)
            {
                IsPanelVisible = false;
                _hidePending = false;
            }
        }, TimeSpan.FromSeconds(3));
    }

    public ReactiveCommand<Guid, Unit> CancelTransferCommand { get; }
    public ReactiveCommand<Guid, Unit> RetryTransferCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearCompletedCommand { get; }

    /// <summary>Cancels every remaining file in the current batch, aborting the one in flight
    /// and skipping the rest (spec §9: 取消剩余传输).</summary>
    public ReactiveCommand<Unit, Unit> CancelAllCommand { get; }

    public void AddTransfer(TransferTask task)
    {
        var item = new TransferItemViewModel(task);
        // New tasks appear at the top so active uploads are visible without scrolling.
        Transfers.Insert(0, item);
    }

    public TransferItemViewModel? FindTransfer(Guid transferId)
    {
        return Transfers.FirstOrDefault(t => t.Id == transferId);
    }

    private void CancelTransfer(Guid transferId)
    {
        var item = FindTransfer(transferId);
        if (item == null) return;

        item.Status = TransferStatus.Cancelled;
        _transferManager?.CancelTransferAsync(transferId);
    }

    private void RetryTransfer(Guid transferId)
    {
        var item = FindTransfer(transferId);
        if (item == null || item.Status != TransferStatus.Failed) return;

        item.Status = TransferStatus.Queued;
    }

    private void ClearCompleted()
    {
        var completed = Transfers.Where(t =>
            t.Status == TransferStatus.Completed ||
            t.Status == TransferStatus.Cancelled).ToList();

        foreach (var item in completed)
        {
            Transfers.Remove(item);
        }
    }
}
