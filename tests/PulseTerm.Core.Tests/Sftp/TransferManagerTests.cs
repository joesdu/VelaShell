using PulseTerm.Core.Models;
using PulseTerm.Core.Sftp;

namespace PulseTerm.Core.Tests.Sftp;

[TestClass]
[TestCategory("Sftp")]
public class TransferManagerTests
{
    [TestMethod]
    public async Task QueueTransferAsync_AddsTransferToQueue()
    {
        using var manager = new TransferManager(SlowExecutor());
        var task = CreateTransferTask(TransferType.Upload);

        await manager.QueueTransferAsync(task);
        await Task.Delay(20);

        var retrieved = manager.GetTransfer(task.Id);
        Assert.IsNotNull(retrieved);
        Assert.AreEqual(task.Id, retrieved!.Id);
    }

    [TestMethod]
    public async Task QueueTransferAsync_RespectsMaxConcurrentLimit()
    {
        using var manager = new TransferManager(SlowExecutor()) { MaxConcurrentTransfers = 3 };
        var tasks = Enumerable.Range(0, 5)
            .Select(_ => CreateTransferTask(TransferType.Download))
            .ToList();

        foreach (var task in tasks)
        {
            await manager.QueueTransferAsync(task);
        }

        await Task.Delay(100);

        var activeCount = manager.ActiveTransfers.Count;
        var queuedCount = manager.QueuedTransfers.Count;
        var totalInSystem = activeCount + queuedCount;

        Assert.IsTrue(activeCount <= 3, "because max concurrent is 3");
        Assert.IsTrue(totalInSystem > 0, "because transfers should still be in progress");
    }

    [TestMethod]
    public async Task QueueTransferAsync_WithConcurrentLimit_OnlyThreeRunSimultaneously()
    {
        using var manager = new TransferManager(SlowExecutor()) { MaxConcurrentTransfers = 3 };
        var tasks = new List<TransferTask>();

        for (int i = 0; i < 5; i++)
        {
            var task = CreateTransferTask(TransferType.Upload);
            tasks.Add(task);
            await manager.QueueTransferAsync(task);
        }

        await Task.Delay(200);

        Assert.IsTrue(manager.ActiveTransfers.Count <= 3);
    }

    [TestMethod]
    public async Task CancelTransferAsync_CancelsSpecificTransfer()
    {
        using var manager = new TransferManager();
        var task = CreateTransferTask(TransferType.Upload);

        await manager.QueueTransferAsync(task);
        await manager.CancelTransferAsync(task.Id);

        var cancelledTask = manager.GetTransfer(task.Id);
        Assert.AreEqual(TransferStatus.Cancelled, cancelledTask?.Status);
    }

    [TestMethod]
    public async Task GetTransfer_ReturnsCorrectTransferTask()
    {
        using var manager = new TransferManager();
        var task = CreateTransferTask(TransferType.Download);

        await manager.QueueTransferAsync(task);
        var retrieved = manager.GetTransfer(task.Id);

        Assert.IsNotNull(retrieved);
        Assert.AreEqual(task.Id, retrieved!.Id);
        Assert.AreEqual(task.LocalPath, retrieved.LocalPath);
        Assert.AreEqual(task.RemotePath, retrieved.RemotePath);
    }

    [TestMethod]
    public void GetTransfer_WhenNotFound_ReturnsNull()
    {
        using var manager = new TransferManager();
        var nonExistentId = Guid.NewGuid();

        var result = manager.GetTransfer(nonExistentId);

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task ActiveTransfers_ReflectsCurrentlyRunningTransfers()
    {
        using var manager = new TransferManager(SlowExecutor()) { MaxConcurrentTransfers = 2 };

        var task1 = CreateTransferTask(TransferType.Upload);
        var task2 = CreateTransferTask(TransferType.Download);
        var task3 = CreateTransferTask(TransferType.Upload);

        await manager.QueueTransferAsync(task1);
        await manager.QueueTransferAsync(task2);
        await manager.QueueTransferAsync(task3);

        await Task.Delay(100);

        Assert.IsTrue(manager.ActiveTransfers.Count <= 2);
    }

    [TestMethod]
    public void MaxConcurrentTransfers_DefaultsToThree()
    {
        using var manager = new TransferManager();

        Assert.AreEqual(3, manager.MaxConcurrentTransfers);
    }

    [TestMethod]
    public void MaxConcurrentTransfers_CanBeChanged()
    {
        using var manager = new TransferManager();

        manager.MaxConcurrentTransfers = 5;

        Assert.AreEqual(5, manager.MaxConcurrentTransfers);
    }

    private static TransferExecutor SlowExecutor(int delayMs = 2000)
    {
        return async (task, progress, ct) =>
        {
            await Task.Delay(delayMs, ct);
        };
    }

    private static TransferTask CreateTransferTask(TransferType type)
    {
        return new TransferTask
        {
            Id = Guid.NewGuid(),
            Type = type,
            LocalPath = $"/local/path/file_{Guid.NewGuid()}.txt",
            RemotePath = $"/remote/path/file_{Guid.NewGuid()}.txt",
            Status = TransferStatus.Queued
        };
    }
}
