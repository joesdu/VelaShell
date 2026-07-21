using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

[TestClass]
public class FileTransferViewModelTests
{
    private readonly ITransferManager _transferManager;
    private readonly FileTransferViewModel _vm;

    public FileTransferViewModelTests()
    {
        _transferManager = Substitute.For<ITransferManager>();
        _transferManager.ActiveTransfers.Returns([]);
        _transferManager.QueuedTransfers.Returns([]);
        _vm = new(_transferManager);
    }

    private static TransferTask CreateTask(
        TransferType type = TransferType.Upload,
        TransferStatus status = TransferStatus.Queued,
        string remotePath = "/home/user/file.txt",
        string localPath = "/tmp/file.txt")
    {
        return new()
        {
            Id = Guid.NewGuid(),
            Type = type,
            RemotePath = remotePath,
            LocalPath = localPath,
            Status = status
        };
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void TransferAdded_AppearsInTransfersCollection()
    {
        // Arrange
        TransferTask task = CreateTask();

        // Act
        _vm.AddTransfer(task);

        // Assert
        Assert.HasCount(1, _vm.Transfers);
        Assert.AreEqual("file.txt", _vm.Transfers[0].FileName);
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void ProgressUpdate_ChangesTransferItemProgress()
    {
        // Arrange
        TransferTask task = CreateTask(status: TransferStatus.InProgress);
        _vm.AddTransfer(task);
        TransferItemViewModel item = _vm.Transfers[0];

        // Act
        var progress = new TransferProgress
        {
            FileName = "file.txt",
            BytesTransferred = 512_000,
            TotalBytes = 1_024_000,
            Percentage = 50,
            SpeedBytesPerSecond = 256_000,
            EstimatedTimeRemaining = TimeSpan.FromSeconds(2)
        };
        item.UpdateProgress(progress);

        // Assert
        Assert.AreEqual(50, item.Progress);
        Assert.AreEqual(512_000L, item.TransferredBytes);
        Assert.AreEqual(1_024_000L, item.TotalSize);
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void CancelTransfer_UpdatesStatusToCancelled()
    {
        // Arrange
        TransferTask task = CreateTask(status: TransferStatus.InProgress);
        _transferManager.CancelTransferAsync(task.Id, Arg.Any<CancellationToken>())
                        .Returns(Task.CompletedTask);
        _vm.AddTransfer(task);

        // Act
        _vm.CancelTransferCommand.Execute(task.Id).Subscribe();

        // Assert
        Assert.AreEqual(TransferStatus.Cancelled, _vm.Transfers[0].Status);
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void ClearCompleted_RemovesCompletedItemsFromList()
    {
        // Arrange
        TransferTask active = CreateTask(status: TransferStatus.InProgress);
        TransferTask completed1 = CreateTask(status: TransferStatus.Completed, remotePath: "/home/user/done1.txt");
        TransferTask completed2 = CreateTask(status: TransferStatus.Completed, remotePath: "/home/user/done2.txt");
        _vm.AddTransfer(active);
        _vm.AddTransfer(completed1);
        _vm.AddTransfer(completed2);
        Assert.HasCount(3, _vm.Transfers);

        // Act
        _vm.ClearCompletedCommand.Execute().Subscribe();

        // Assert
        Assert.HasCount(1, _vm.Transfers);
        Assert.AreEqual("file.txt", _vm.Transfers[0].FileName);
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    [DataRow(0, "0 B/s")]
    [DataRow(512, "512 B/s")]
    [DataRow(1_230, "1.2 KB/s")]
    [DataRow(3_670_016, "3.5 MB/s")]
    [DataRow(1_181_116_006, "1.1 GB/s")]
    public void SpeedFormatting_ReturnsHumanReadable(double bytesPerSecond, string expected) => Assert.AreEqual(expected, TransferItemViewModel.FormatSpeed(bytesPerSecond));

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void Direction_ShowsCorrectArrow()
    {
        // Arrange & Act
        TransferTask upload = CreateTask();
        TransferTask download = CreateTask(TransferType.Download);
        _vm.AddTransfer(upload);
        _vm.AddTransfer(download);

        // Assert
        Assert.AreEqual("↓", _vm.Transfers[0].Direction);
        Assert.AreEqual("↑", _vm.Transfers[1].Direction);
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void RetryTransfer_RequeuesFailedTransfer()
    {
        // Arrange
        TransferTask task = CreateTask(status: TransferStatus.Failed);
        _transferManager.QueueTransferAsync(Arg.Any<TransferTask>(), Arg.Any<CancellationToken>())
                        .Returns(Task.CompletedTask);
        _vm.AddTransfer(task);
        Assert.AreEqual(TransferStatus.Failed, _vm.Transfers[0].Status);

        // Act
        _vm.RetryTransferCommand.Execute(task.Id).Subscribe();

        // Assert
        Assert.AreEqual(TransferStatus.Queued, _vm.Transfers[0].Status);
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void TimeRemainingFormatting_ShowsReadableString()
    {
        // Arrange
        TransferTask task = CreateTask(status: TransferStatus.InProgress);
        _vm.AddTransfer(task);
        TransferItemViewModel item = _vm.Transfers[0];

        // Act
        var progress = new TransferProgress
        {
            FileName = "file.txt",
            BytesTransferred = 500_000,
            TotalBytes = 1_000_000,
            Percentage = 50,
            SpeedBytesPerSecond = 100_000,
            EstimatedTimeRemaining = TimeSpan.FromSeconds(65)
        };
        item.UpdateProgress(progress);

        // Assert
        Assert.AreEqual("1m 5s", item.TimeRemaining);
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void BeginBatch_ShowsRemainingCount_AndCountsDownAsFilesSettle()
    {
        using var cts = new CancellationTokenSource();
        _vm.BeginBatch(3, cts);
        Assert.IsTrue(_vm.IsBatchActive);
        Assert.AreEqual(3, _vm.PendingCount); // remaining count, not stuck at 1
        _vm.NotifyBatchItemSettled();
        Assert.AreEqual(2, _vm.PendingCount);
        _vm.NotifyBatchItemSettled();
        Assert.AreEqual(1, _vm.PendingCount);
        _vm.EndBatch();
        Assert.IsFalse(_vm.IsBatchActive);
        Assert.AreEqual(0, _vm.PendingCount); // falls back to (empty) active count
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void CancelAll_CancelsBatchToken_AndMarksActiveItemsCancelled()
    {
        using var cts = new CancellationTokenSource();
        TransferTask running = CreateTask(status: TransferStatus.InProgress);
        _vm.AddTransfer(running);
        _vm.BeginBatch(5, cts);
        _vm.CancelAllCommand.Execute().Subscribe();
        Assert.IsTrue(cts.IsCancellationRequested);
        Assert.AreEqual(TransferStatus.Cancelled, _vm.Transfers[0].Status);
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void ShowPanel_ReopensToast_ForReviewingHistory()
    {
        // A finished transfer leaves the toast collapsed but its history retained.
        _vm.AddTransfer(CreateTask(status: TransferStatus.Completed, remotePath: "/home/user/done.txt"));
        _vm.HidePanelCommand.Execute().Subscribe();
        Assert.IsFalse(_vm.IsPanelVisible);
        _vm.ShowPanel();
        Assert.IsTrue(_vm.IsPanelVisible);
        Assert.HasCount(1, _vm.Transfers); // past record still there to review
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void PendingCount_WithoutBatch_FallsBackToActiveCount()
    {
        _vm.AddTransfer(CreateTask(status: TransferStatus.InProgress));
        _vm.AddTransfer(CreateTask(status: TransferStatus.Completed, remotePath: "/home/user/done.txt"));
        Assert.IsFalse(_vm.IsBatchActive);
        Assert.AreEqual(1, _vm.PendingCount); // one in-flight single transfer
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void BeginPreparing_ShowsPanelImmediately_AndCountsDiscoveredFiles()
    {
        // 选择大文件夹后,扫描期间面板立即可见、徽标随发现数递增。
        Assert.IsFalse(_vm.IsPanelVisible);
        _vm.BeginPreparing();
        Assert.IsTrue(_vm.IsPreparing);
        Assert.IsTrue(_vm.IsPanelVisible);
        Assert.AreEqual(0, _vm.PendingCount);
        _vm.UpdatePreparingCount(1);
        Assert.AreEqual(1, _vm.PendingCount);
        _vm.UpdatePreparingCount(42);
        Assert.AreEqual(42, _vm.PendingCount);
        Assert.Contains("42", _vm.PreparingText);
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void BeginBatch_TakesOverFromPreparing_BadgeSwitchesToRemaining()
    {
        using var cts = new CancellationTokenSource();
        _vm.BeginPreparing();
        _vm.UpdatePreparingCount(7);
        _vm.BeginBatch(7, cts);
        Assert.IsFalse(_vm.IsPreparing);
        Assert.IsTrue(_vm.IsBatchActive);
        Assert.AreEqual(7, _vm.PendingCount); // 从"已发现"无缝切换为"剩余"
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void EndPreparing_WithNothingPlanned_HidesPanelAgain()
    {
        // 计划为空(全部冲突跳过/取消)时退出准备态,面板不残留。
        _vm.BeginPreparing();
        Assert.IsTrue(_vm.IsPanelVisible);
        _vm.EndPreparing();
        Assert.IsFalse(_vm.IsPreparing);
        Assert.IsFalse(_vm.IsPanelVisible);
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void UpdatePreparingCount_AfterPreparingEnded_IsIgnored()
    {
        using var cts = new CancellationTokenSource();
        _vm.BeginPreparing();
        _vm.BeginBatch(3, cts);
        _vm.UpdatePreparingCount(99); // 迟到的扫描回调不得污染"剩余"徽标
        Assert.AreEqual(3, _vm.PendingCount);
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void ShowPanelTransient_ShowsPanel_WithoutPinningIt()
    {
        // 完成通知展开面板,但自动隐藏倒计时照常进行(ShowPanel 则会钉住面板)。
        _vm.AddTransfer(CreateTask(status: TransferStatus.Completed, remotePath: "/home/user/done.txt"));
        _vm.HidePanelCommand.Execute().Subscribe();
        Assert.IsFalse(_vm.IsPanelVisible);
        _vm.ShowPanelTransient();
        Assert.IsTrue(_vm.IsPanelVisible);
        // 隐藏倒计时已排定:指针进入应暂停、离开应重启,而不是像 ShowPanel 那样清掉挂起状态。
        _vm.SetPointerOver(true);
        _vm.SetPointerOver(false);
        Assert.IsTrue(_vm.IsPanelVisible); // 3 秒倒计时尚未到期,面板仍在
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void MultipleTransfers_TrackedIndependently()
    {
        // Arrange
        TransferTask task1 = CreateTask(remotePath: "/home/user/alpha.zip");
        TransferTask task2 = CreateTask(remotePath: "/home/user/beta.tar.gz");

        // Act
        _vm.AddTransfer(task1);
        _vm.AddTransfer(task2);

        // Assert
        Assert.HasCount(2, _vm.Transfers);
        Assert.AreEqual("beta.tar.gz", _vm.Transfers[0].FileName);
        Assert.AreEqual("alpha.zip", _vm.Transfers[1].FileName);
    }
}
