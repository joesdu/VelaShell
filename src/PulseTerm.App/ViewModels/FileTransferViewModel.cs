using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using PulseTerm.Core.Models;
using PulseTerm.Core.Sftp;
using ReactiveUI;

namespace PulseTerm.App.ViewModels;

public class FileTransferViewModel : ReactiveObject
{
    private readonly ITransferManager _transferManager;
    private bool _isPanelVisible;
    private IDisposable? _autoHide;

    public FileTransferViewModel(ITransferManager? transferManager)
    {
        _transferManager = transferManager!;

        Transfers = new ObservableCollection<TransferItemViewModel>();
        Transfers.CollectionChanged += OnTransfersChanged;

        CancelTransferCommand = ReactiveCommand.Create<Guid>(CancelTransfer);
        RetryTransferCommand = ReactiveCommand.Create<Guid>(RetryTransfer);
        ClearCompletedCommand = ReactiveCommand.Create(ClearCompleted);
        HidePanelCommand = ReactiveCommand.Create(() => { IsPanelVisible = false; });
    }

    public ObservableCollection<TransferItemViewModel> Transfers { get; }

    /// <summary>Count of in-flight tasks shown in the header badge (design 9Ralg).</summary>
    public int ActiveCount => Transfers.Count(t => t.IsActive);

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

    private void OnTransfersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        this.RaisePropertyChanged(nameof(ActiveCount));
        if (Transfers.Count > 0)
            IsPanelVisible = true;
        else
            IsPanelVisible = false;
    }

    /// <summary>Call when a task finishes; once nothing is active the toast lingers ~3s
    /// showing the completed state, then fades out (spec §9.4).</summary>
    public void NotifyTaskSettled()
    {
        this.RaisePropertyChanged(nameof(ActiveCount));
        _autoHide?.Dispose();
        if (ActiveCount > 0)
            return;

        _autoHide = Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            if (ActiveCount == 0)
                IsPanelVisible = false;
        }, TimeSpan.FromSeconds(3));
    }

    public ReactiveCommand<Guid, Unit> CancelTransferCommand { get; }
    public ReactiveCommand<Guid, Unit> RetryTransferCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearCompletedCommand { get; }

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
