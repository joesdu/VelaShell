using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reactive;
using Avalonia.Threading;
using ReactiveUI;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;

namespace VelaShell.App.ViewModels;

public class FileTransferViewModel : ReactiveObject
{
    private readonly ITransferManager _transferManager;
    private IDisposable? _autoHide;

    // Current batch (a folder/multi-file download or upload): the number of files still to finish
    // and the token source that cancels whatever remains, including the file in flight.
    private CancellationTokenSource? _batchCts;
    private int _batchRemaining;
    private bool _hidePending;
    private bool _isPointerOver;

    // 准备阶段(上传/下载前的目录扫描):大文件夹的扫描可能持续数秒,期间面板立即弹出、
    // 徽标随发现的文件数递增,让用户知道处理已经开始(用户反馈)。
    private bool _isPreparing;
    private int _preparingCount;

    public FileTransferViewModel(ITransferManager? transferManager)
    {
        _transferManager = transferManager ?? throw new ArgumentNullException(nameof(transferManager));
        Transfers = [];
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

    /// <summary>
    /// Header badge (design 9Ralg): while preparing it counts up with the files
    /// discovered by the scan; during a batch it is the number of files still to transfer
    /// (counting down); otherwise the number of in-flight single transfers.
    /// </summary>
    public int PendingCount => IsPreparing
                                   ? _preparingCount
                                   : IsBatchActive
                                       ? _batchRemaining
                                       : ActiveCount;

    /// <summary>Whether an upload/download is still scanning directories to build its plan.</summary>
    public bool IsPreparing
    {
        get => _isPreparing;
        private set => this.RaiseAndSetIfChanged(ref _isPreparing, value);
    }

    /// <summary>准备阶段的状态行文案:随扫描进度动态刷新。</summary>
    public string PreparingText => $"正在扫描待传输文件… 已发现 {_preparingCount} 个";

    /// <summary>
    /// The toast exists only while it has content and wasn't manually collapsed (spec §9):
    /// a new task fades it in, closing (x) only hides it — tasks keep running.
    /// </summary>
    public bool IsPanelVisible
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ReactiveCommand<Unit, Unit> HidePanelCommand { get; }

    public ReactiveCommand<Guid, Unit> CancelTransferCommand { get; }

    public ReactiveCommand<Guid, Unit> RetryTransferCommand { get; }

    public ReactiveCommand<Unit, Unit> ClearCompletedCommand { get; }

    /// <summary>
    /// Cancels every remaining file in the current batch, aborting the one in flight
    /// and skipping the rest (spec §9: 取消剩余传输).
    /// </summary>
    public ReactiveCommand<Unit, Unit> CancelAllCommand { get; }

    /// <summary>
    /// Enters the preparing (directory-scan) state: the toast pops up immediately with
    /// a live file counter so picking a large folder no longer looks like nothing happened.
    /// Ended by <see cref="BeginBatch" /> (scan done, transfers starting) or <see cref="EndPreparing" />.
    /// </summary>
    public void BeginPreparing()
    {
        _preparingCount = 0;
        IsPreparing = true;
        this.RaisePropertyChanged(nameof(PendingCount));
        this.RaisePropertyChanged(nameof(PreparingText));

        // 面板立即可见;挂起中的自动隐藏作废(新一轮任务开始了)。
        _autoHide?.Dispose();
        _autoHide = null;
        _hidePending = false;
        IsPanelVisible = true;
    }

    /// <summary>Updates the live count of files discovered so far during the preparing scan.</summary>
    public void UpdatePreparingCount(int discovered)
    {
        if (!IsPreparing)
        {
            return;
        }
        _preparingCount = discovered;
        this.RaisePropertyChanged(nameof(PendingCount));
        this.RaisePropertyChanged(nameof(PreparingText));
    }

    /// <summary>
    /// Leaves the preparing state without starting a batch (empty plan, cancellation or
    /// error). No-op when <see cref="BeginBatch" /> already took over. Hides the toast again if
    /// the scan produced nothing to show.
    /// </summary>
    public void EndPreparing()
    {
        if (!IsPreparing)
        {
            return;
        }
        _preparingCount = 0;
        IsPreparing = false;
        this.RaisePropertyChanged(nameof(PendingCount));
        if (Transfers.Count == 0)
        {
            IsPanelVisible = false;
        }
        else
        {
            NotifyTaskSettled();
        }
    }

    /// <summary>
    /// Begins a cancellable batch of <paramref name="totalFiles" /> transfers. The header
    /// then shows the remaining count and a cancel control that trips <paramref name="cts" />.
    /// </summary>
    public void BeginBatch(int totalFiles, CancellationTokenSource cts)
    {
        // 准备阶段结束,徽标从"已发现"切换为"剩余"。
        _isPreparing = false;
        _preparingCount = 0;
        this.RaisePropertyChanged(nameof(IsPreparing));
        _batchCts = cts;
        _batchRemaining = totalFiles;
        this.RaisePropertyChanged(nameof(IsBatchActive));
        this.RaisePropertyChanged(nameof(PendingCount));
    }

    /// <summary>Marks one file of the current batch as finished, decrementing the remaining count.</summary>
    public void NotifyBatchItemSettled()
    {
        if (_batchRemaining > 0)
        {
            _batchRemaining--;
        }
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
        foreach (TransferItemViewModel item in Transfers.Where(t => t.IsActive).ToList())
        {
            item.Status = TransferStatus.Cancelled;
        }
    }

    /// <summary>
    /// Reopens the transfer toast (from the toolbar "transfer history" button) so past and
    /// active transfers can be reviewed. Cancels any pending auto-hide and keeps it up until the
    /// user collapses it with the x.
    /// </summary>
    public void ShowPanel()
    {
        _autoHide?.Dispose();
        _autoHide = null;
        _hidePending = false;
        IsPanelVisible = true;
    }

    /// <summary>
    /// 传输完成通知用的临时展开:面板可见,但不像 <see cref="ShowPanel" /> 那样锁定——
    /// 自动隐藏倒计时照常进行(指针悬停时照常暂停)。修复完成通知把面板钉死在界面上、
    /// 只能手动关闭的问题(用户反馈)。
    /// </summary>
    public void ShowPanelTransient()
    {
        IsPanelVisible = true;
        NotifyTaskSettled();
    }

    private void OnTransfersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        this.RaisePropertyChanged(nameof(ActiveCount));
        this.RaisePropertyChanged(nameof(PendingCount));
        if (Transfers.Count > 0)
        {
            IsPanelVisible = true;
        }
        else
        {
            IsPanelVisible = false;
        }
    }

    /// <summary>
    /// Call when a task finishes; once nothing is active the toast lingers ~3s
    /// showing the completed state, then fades out (spec §9.4). While the pointer is inside
    /// the toast the countdown is held — it only starts (or restarts) after the pointer leaves.
    /// </summary>
    public void NotifyTaskSettled()
    {
        this.RaisePropertyChanged(nameof(ActiveCount));
        this.RaisePropertyChanged(nameof(PendingCount));
        _autoHide?.Dispose();
        _autoHide = null;

        // Keep the toast up while more files in the batch are pending, while any single
        // transfer is still in flight, or while a scan is preparing the next batch.
        if (ActiveCount > 0 || IsBatchActive || IsPreparing)
        {
            _hidePending = false;
            return;
        }
        _hidePending = true;
        if (!_isPointerOver)
        {
            ScheduleAutoHide();
        }
    }

    /// <summary>
    /// Called by the view on pointer enter/leave: entering pauses any pending
    /// auto-hide so the user can inspect results; leaving resumes the 3s countdown.
    /// </summary>
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
        {
            ScheduleAutoHide();
        }
    }

    private void ScheduleAutoHide()
    {
        _autoHide = DispatcherTimer.RunOnce(() =>
        {
            if (ActiveCount != 0 || _isPointerOver)
            {
                return;
            }
            IsPanelVisible = false;
            _hidePending = false;
        }, TimeSpan.FromSeconds(3));
    }

    public void AddTransfer(TransferTask task)
    {
        var item = new TransferItemViewModel(task);
        // New tasks appear at the top so active uploads are visible without scrolling.
        Transfers.Insert(0, item);
    }

    public TransferItemViewModel? FindTransfer(Guid transferId) => Transfers.FirstOrDefault(t => t.Id == transferId);

    private void CancelTransfer(Guid transferId)
    {
        TransferItemViewModel? item = FindTransfer(transferId);
        if (item == null)
        {
            return;
        }
        item.Status = TransferStatus.Cancelled;
        _transferManager.CancelTransferAsync(transferId);
    }

    private void RetryTransfer(Guid transferId)
    {
        TransferItemViewModel? item = FindTransfer(transferId);
        if (item is not { Status: TransferStatus.Failed })
        {
            return;
        }
        item.Status = TransferStatus.Queued;
    }

    private void ClearCompleted()
    {
        var completed = Transfers.Where(t =>
            t.Status is TransferStatus.Completed or TransferStatus.Cancelled).ToList();
        foreach (TransferItemViewModel item in completed)
        {
            Transfers.Remove(item);
        }
    }
}
