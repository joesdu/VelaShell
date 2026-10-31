using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Infrastructure.Persistence;

namespace VelaShell.Infrastructure.Tests;

/// <summary>
/// 传输队列落在真正的 SonnetDB 上:集合随建库创建,关库重开之后批次原样读回。
/// </summary>
[TestClass]
[TestCategory("Persistence")]
public sealed class TransferQueuePersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"velashell_tq_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ABatchRoundTripsThroughAReopenedDatabase()
    {
        var origin = new TransferQueueOrigin(Guid.NewGuid(), "备份机");
        List<TransferQueueItem> items =
        [
            .. Enumerable.Range(0, TransferQueueJournal.ChunkSize + 3)
                .Select(i => new TransferQueueItem(
                    i % 2 == 0 ? TransferType.Upload : TransferType.Download,
                    Path.Combine(_directory, $"本地 {i}.bin"),
                    $"/srv/备份/{i}.bin")),
        ];
        Guid id;
        using (var engine = new SonnetDbEngine(Path.Combine(_directory, "sonnetdb")))
        {
            var journal = new TransferQueueJournal(new SonnetDbAppDataStore(engine), TimeSpan.FromHours(1));
            id = journal.Begin(origin, items);
            journal.Settle(id, 1);
            await journal.FlushAsync();
        }

        using (var reopened = new SonnetDbEngine(Path.Combine(_directory, "sonnetdb")))
        {
            var journal = new TransferQueueJournal(new SonnetDbAppDataStore(reopened), TimeSpan.FromHours(1));
            PausedTransferBatch batch = (await journal.LoadAsync()).Single();

            Assert.AreEqual(id, batch.BatchId);
            Assert.AreEqual(origin, batch.Origin);
            Assert.AreSequenceEqual(
                [.. items.Where((_, i) => i != 1)], [.. batch.Items], "方向、路径(含中文与空格)与先后都原样读回,落定的那一项不在");

            journal.Drop(id);
            await journal.FlushAsync();
            Assert.IsEmpty(await new SonnetDbAppDataStore(reopened).GetAllAsync<TransferQueueChunk>(TransferQueueJournal.Collection));
        }
    }

    /// <summary>
    /// 库里有一行读不出来(写了一半的 JSON、字段类型对不上):其余的批次照常还原,坏的那几行在下次落盘时删掉 ——
    /// 以前 GetAllAsync 碰到第一行坏的就整次抛出,所有暂停的批次每次启动都还原不出来。
    /// </summary>
    [TestMethod]
    public async Task AnUnreadableRow_DoesNotBlockRestoringTheOthers_AndIsCleanedUp()
    {
        var origin = new TransferQueueOrigin(Guid.NewGuid(), "备份机");
        string halfWritten = TransferQueueJournal.DocumentId(Guid.NewGuid(), 0);
        Guid id;
        using (var engine = new SonnetDbEngine(Path.Combine(_directory, "sonnetdb")))
        {
            var journal = new TransferQueueJournal(new SonnetDbAppDataStore(engine), TimeSpan.FromHours(1));
            id = journal.Begin(origin, [new TransferQueueItem(TransferType.Upload, Path.Combine(_directory, "a.bin"), "/srv/a.bin")]);
            await journal.FlushAsync();
            await engine.WithCollectionAsync<object?>(SonnetDbEngine.TransferQueueCollection, store =>
            {
                store.Upsert(halfWritten, "{\"batchId\":\"" + Guid.NewGuid() + "\",\"chunk\":\"zero\"}");
                store.Upsert("leftover", "{\"items\":42}");
                return null;
            });
        }

        using (var reopened = new SonnetDbEngine(Path.Combine(_directory, "sonnetdb")))
        {
            var data = new SonnetDbAppDataStore(reopened);
            var journal = new TransferQueueJournal(data, TimeSpan.FromHours(1));
            PausedTransferBatch batch = (await journal.LoadAsync()).Single();
            await journal.FlushAsync();

            Assert.AreEqual(id, batch.BatchId);
            Assert.AreEqual("/srv/a.bin", batch.Items.Single().RemotePath);
            (List<TransferQueueChunk> left, List<string> unreadable) = await data.GetAllReadableAsync<TransferQueueChunk>(TransferQueueJournal.Collection);
            Assert.IsEmpty(unreadable, "坏的那几行删掉了");
            Assert.AreEqual(id, left.Single().BatchId);
        }
    }
}
