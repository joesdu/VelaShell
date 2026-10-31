using System.Collections.Concurrent;
using System.Text.Json;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;

namespace VelaShell.Core.Tests.Sftp;

/// <summary>
/// 传输队列持久化的账本:登记、落定、作废、分段落盘、跨重启读回。
/// </summary>
[TestClass]
[TestCategory("Sftp")]
public sealed class TransferQueueJournalTests
{
    private static readonly TransferQueueOrigin Origin = new(Guid.NewGuid(), "prod-01");

    private static List<TransferQueueItem> Items(int count, TransferType type = TransferType.Upload) =>
    [
        .. Enumerable.Range(0, count).Select(i => new TransferQueueItem(type, $"C:/data/f{i}.bin", $"/srv/f{i}.bin")),
    ];

    [TestMethod]
    public async Task ABatchSurvivesARestart_WithOnlyTheItemsNotYetSettled()
    {
        var store = new JsonStore();
        var before = new TransferQueueJournal(store, TimeSpan.FromHours(1));
        Guid id = before.Begin(Origin, Items(5));
        before.Settle(id, 0);
        before.Settle(id, 3);
        await before.FlushAsync();

        var after = new TransferQueueJournal(store, TimeSpan.FromHours(1));
        IReadOnlyList<PausedTransferBatch> restored = await after.LoadAsync();

        PausedTransferBatch batch = restored.Single();
        Assert.AreEqual(id, batch.BatchId);
        Assert.AreEqual(Origin, batch.Origin);
        Assert.AreSequenceEqual(
            ["/srv/f1.bin", "/srv/f2.bin", "/srv/f4.bin"], [.. batch.Items.Select(i => i.RemotePath)], "落定的项不再出现,剩下的保持原来的先后");
        Assert.IsTrue(batch.Items.All(i => i.Type == TransferType.Upload));
    }

    [TestMethod]
    public async Task DirectionIsStoredAsText_SoDownloadsNeverComeBackAsUploads()
    {
        var store = new JsonStore();
        var journal = new TransferQueueJournal(store, TimeSpan.FromHours(1));
        journal.Begin(Origin, Items(1, TransferType.Download));
        await journal.FlushAsync();

        Assert.Contains("\"direction\":\"download\"", store.RawDocuments().Single());
        PausedTransferBatch batch = (await new TransferQueueJournal(store).LoadAsync()).Single();
        Assert.AreEqual(TransferType.Download, batch.Items.Single().Type);
    }

    [TestMethod]
    public async Task LargeBatches_AreStoredInChunks_AndOnlyChangedChunksAreRewritten()
    {
        var store = new JsonStore();
        var journal = new TransferQueueJournal(store, TimeSpan.FromHours(1));
        Guid id = journal.Begin(Origin, Items(TransferQueueJournal.ChunkSize * 2 + 50));
        await journal.FlushAsync();
        Assert.AreEqual(3, store.Count, "450 项 = 3 段");

        store.ResetWrites();
        journal.Settle(id, 5);
        await journal.FlushAsync();
        Assert.AreEqual(1, store.Writes, "只重写第 0 段");

        for (int i = 0; i < TransferQueueJournal.ChunkSize; i++)
        {
            journal.Settle(id, i);
        }
        await journal.FlushAsync();
        // 第 0 段全部落定:留一份空段占位(读回时据此分得清「这一段传完了」与「这一段根本没写上」)。
        Assert.AreEqual(3, store.Count);
        PausedTransferBatch restored = (await new TransferQueueJournal(store, TimeSpan.FromHours(1)).LoadAsync()).Single();
        Assert.HasCount(TransferQueueJournal.ChunkSize + 50, restored.Items);

        for (int i = TransferQueueJournal.ChunkSize; i < TransferQueueJournal.ChunkSize * 2 + 50; i++)
        {
            journal.Settle(id, i);
        }
        await journal.FlushAsync();
        Assert.AreEqual(0, store.Count, "整批落定,占位的空段一起删掉");
    }

    /// <summary>
    /// 登记一大批之后第一次落盘没写全(写到一半进程被杀、或者某一段写失败了):读回时不能还原成一批悄悄少了几百个文件的「已暂停」——
    /// 以前缺的那几段与「已经传完」分不出来,那些文件永远不会再传、也不会再出现。没写全的整批不还原,剩下的段顺手删掉。
    /// </summary>
    [TestMethod]
    public async Task ABatchWhoseFirstFlushWasCutShort_IsNotRestoredWithSilentGaps()
    {
        var store = new JsonStore { FailUpsert = id => id.EndsWith("-0001", StringComparison.Ordinal) };
        var before = new TransferQueueJournal(store, TimeSpan.FromHours(1));
        before.Begin(Origin, Items(TransferQueueJournal.ChunkSize * 2 + 50));
        await before.FlushAsync();
        Assert.AreEqual(2, store.Count, "三段只写上了两段");

        // 进程到此为止(没有下一次落盘)。重启:
        store.FailUpsert = null;
        var after = new TransferQueueJournal(store, TimeSpan.FromHours(1));
        Assert.IsEmpty(await after.LoadAsync(), "少了一段的批次不还原");
        await after.FlushAsync();
        Assert.AreEqual(0, store.Count, "没写全的那一批剩下的段删掉");
    }

    /// <summary>某一段写失败了不是就此算了:记回去,下一次落盘照现状再写,整批读回来一项不少。</summary>
    [TestMethod]
    public async Task AFailedChunkWrite_IsRetriedOnTheNextFlush()
    {
        int failures = 1;
        var store = new JsonStore { FailUpsert = id => id.EndsWith("-0001", StringComparison.Ordinal) && Interlocked.Exchange(ref failures, 0) == 1 };
        var journal = new TransferQueueJournal(store, TimeSpan.FromHours(1));
        Guid id = journal.Begin(Origin, Items(TransferQueueJournal.ChunkSize * 2 + 50));
        await journal.FlushAsync();
        Assert.AreEqual(2, store.Count);

        await journal.FlushAsync();

        Assert.AreEqual(3, store.Count, "写失败的那一段在下一次落盘时补上");
        PausedTransferBatch restored = (await new TransferQueueJournal(store, TimeSpan.FromHours(1)).LoadAsync()).Single();
        Assert.AreEqual(id, restored.BatchId);
        Assert.HasCount(TransferQueueJournal.ChunkSize * 2 + 50, restored.Items);
    }

    /// <summary>
    /// 放弃 / 取消一大批,删到一半就没了下文(进程被杀、某一段删失败):剩下的那几段不能在重启后挂成一批「已暂停」——
    /// 那是用户已经不要的东西,点了「继续」就会把放弃了的文件又传一遍。
    /// </summary>
    [TestMethod]
    public async Task ADiscardedBatchCutShort_DoesNotComeBackPartlyPaused()
    {
        var store = new JsonStore();
        var before = new TransferQueueJournal(store, TimeSpan.FromHours(1));
        Guid id = before.Begin(Origin, Items(TransferQueueJournal.ChunkSize * 2 + 50));
        await before.FlushAsync();
        store.FailDelete = doc => doc.EndsWith("-0002", StringComparison.Ordinal);
        before.Drop(id);
        await before.FlushAsync();
        Assert.AreEqual(1, store.Count, "删到一半");

        store.FailDelete = null;
        var after = new TransferQueueJournal(store, TimeSpan.FromHours(1));
        Assert.IsEmpty(await after.LoadAsync(), "放弃了的批次不能挂回来");
        await after.FlushAsync();
        Assert.AreEqual(0, store.Count);
    }

    /// <summary>
    /// 交接(「继续」登记新的一批、撤掉旧的一批)落在同一次落盘里:先把新的写全再删旧的,
    /// 中途崩掉最多是两份都在,不会是新的没写全(整批不认)、旧的又已经删了。
    /// </summary>
    [TestMethod]
    public async Task AHandoffWritesTheNewBatchBeforeDeletingTheOldOne()
    {
        var store = new JsonStore();
        var journal = new TransferQueueJournal(store, TimeSpan.FromHours(1));
        Guid old = journal.Begin(Origin, Items(TransferQueueJournal.ChunkSize + 1));
        await journal.FlushAsync();
        store.Operations.Clear();

        Guid fresh = journal.Begin(Origin, Items(TransferQueueJournal.ChunkSize + 1));
        journal.Drop(old);
        await journal.FlushAsync();

        int lastUpsert = store.Operations.FindLastIndex(op => op.Upsert);
        int firstDelete = store.Operations.FindIndex(op => !op.Upsert);
        Assert.IsGreaterThan(lastUpsert, firstDelete, string.Join(", ", store.Operations));
        Assert.IsTrue(store.Operations.Where(op => op.Upsert).All(op => op.Id.StartsWith(fresh.ToString("N"), StringComparison.Ordinal)));
    }

    /// <summary>
    /// 旧版本写的文档不带段数(那时落定一段就删一段):缺的段照旧按「已经传完」还原,并按新格式整批重写一遍 ——
    /// 升级之后再重启,同一批照样一项不少地读回来,而不是被当成没写全。
    /// </summary>
    [TestMethod]
    public async Task LegacyDocumentsWithoutAChunkCount_StillRestore_AndAreRewrittenInTheNewFormat()
    {
        var store = new JsonStore();
        var batch = Guid.NewGuid();
        foreach (int chunk in new[] { 0, 2 })
        {
            int index = chunk * TransferQueueJournal.ChunkSize + 7;
            await store.UpsertAsync(TransferQueueJournal.Collection, TransferQueueJournal.DocumentId(batch, chunk), new TransferQueueChunk
            {
                BatchId = batch,
                Chunk = chunk,
                ProfileId = Origin.ProfileId,
                ServerName = Origin.ServerName,
                Items = [new() { Index = index, Direction = "download", LocalPath = $"C:/d/{index}", RemotePath = $"/r/{index}" }],
            });
        }

        var upgraded = new TransferQueueJournal(store, TimeSpan.FromHours(1));
        Assert.HasCount(2, (await upgraded.LoadAsync()).Single().Items, "第 1 段在旧版本里是传完删掉的");
        await upgraded.FlushAsync();
        Assert.AreEqual(3, store.Count, "按新格式整批重写,传完的第 1 段写成空段占位");

        PausedTransferBatch again = (await new TransferQueueJournal(store, TimeSpan.FromHours(1)).LoadAsync()).Single();
        Assert.AreSequenceEqual(["/r/7", $"/r/{2 * TransferQueueJournal.ChunkSize + 7}"], [.. again.Items.Select(i => i.RemotePath)]);
    }

    [TestMethod]
    public async Task DroppingABatch_RemovesEveryChunk()
    {
        var store = new JsonStore();
        var journal = new TransferQueueJournal(store, TimeSpan.FromHours(1));
        Guid id = journal.Begin(Origin, Items(TransferQueueJournal.ChunkSize + 1));
        await journal.FlushAsync();

        journal.Drop(id);
        await journal.FlushAsync();

        Assert.AreEqual(0, store.Count);
        Assert.IsNull(journal.Snapshot(id));
    }

    [TestMethod]
    public async Task ARestoredBatch_CanBeDropped_AndIsGoneAfterTheNextRestart()
    {
        var store = new JsonStore();
        var first = new TransferQueueJournal(store, TimeSpan.FromHours(1));
        first.Begin(Origin, Items(3));
        await first.FlushAsync();

        var second = new TransferQueueJournal(store, TimeSpan.FromHours(1));
        PausedTransferBatch restored = (await second.LoadAsync()).Single();
        Assert.AreEqual(3, second.Snapshot(restored.BatchId)?.Items.Count, "读回来的批次照样记在账上");
        second.Drop(restored.BatchId);
        await second.FlushAsync();

        Assert.IsEmpty(await new TransferQueueJournal(store).LoadAsync());
    }

    [TestMethod]
    public async Task UnreadableEntries_AreSkipped_AndEmptyBatchesCleanedUp()
    {
        var store = new JsonStore();
        var good = Guid.NewGuid();
        var hollow = Guid.NewGuid();
        await store.UpsertAsync(TransferQueueJournal.Collection, "good", new TransferQueueChunk
        {
            BatchId = good,
            ProfileId = Origin.ProfileId,
            ServerName = "prod-01",
            Items =
            [
                new() { Index = 0, Direction = "upload", LocalPath = "C:/a", RemotePath = "/a" },
                new() { Index = 1, Direction = "sideways", LocalPath = "C:/b", RemotePath = "/b" },
                new() { Index = 2, Direction = "download", LocalPath = "", RemotePath = "/c" },
            ],
        });
        await store.UpsertAsync(TransferQueueJournal.Collection, TransferQueueJournal.DocumentId(hollow, 0), new TransferQueueChunk
        {
            BatchId = hollow,
            Items = [new() { Index = 0, Direction = "?", LocalPath = "C:/x", RemotePath = "/x" }],
        });

        var journal = new TransferQueueJournal(store, TimeSpan.FromHours(1));
        IReadOnlyList<PausedTransferBatch> restored = await journal.LoadAsync();
        await journal.FlushAsync();

        Assert.AreEqual("/a", restored.Single().Items.Single().RemotePath);
        Assert.IsFalse(store.Contains(TransferQueueJournal.DocumentId(hollow, 0)), "一项都读不出的批次顺手删掉");
    }

    /// <summary>
    /// 一份整个读不出来的文档(崩溃时写了一半的 JSON,或者以后的版本改了字段类型)不能让其余暂停的批次都还原不出来 ——
    /// 以前整次读取直接抛出,什么都不还原,坏的那行与好的那些永远留在库里,每次启动都一样。
    /// 现在跳过它、照常还原其余的,并在下次落盘时把它删掉。
    /// </summary>
    [TestMethod]
    public async Task AnUndeserialisableDocument_DoesNotBlockTheOtherBatches_AndIsDeleted()
    {
        var store = new JsonStore();
        var before = new TransferQueueJournal(store, TimeSpan.FromHours(1));
        Guid good = before.Begin(Origin, Items(2));
        await before.FlushAsync();
        string halfWritten = TransferQueueJournal.DocumentId(Guid.NewGuid(), 0);
        store.PutRaw(TransferQueueJournal.Collection, halfWritten, "{\"batchId\":\"");
        store.PutRaw(TransferQueueJournal.Collection, "not-a-queue-id", "{\"chunk\":\"zero\",\"items\":[]}");

        var after = new TransferQueueJournal(store, TimeSpan.FromHours(1));
        IReadOnlyList<PausedTransferBatch> restored = await after.LoadAsync();
        await after.FlushAsync();

        Assert.AreEqual(good, restored.Single().BatchId);
        Assert.HasCount(2, restored.Single().Items);
        Assert.IsFalse(store.Contains(halfWritten), "读不出来的那份删掉,不再每次启动都撞上它");
        Assert.IsFalse(store.Contains("not-a-queue-id"));
        Assert.IsTrue(store.Contains(TransferQueueJournal.DocumentId(good, 0)), "好的那一批原样留着");
    }

    [TestMethod]
    public void OnlyUploadsAndDownloadsArePersisted()
    {
        var journal = new TransferQueueJournal(null);

        Assert.IsTrue(TransferQueueJournal.CanPersist(TransferType.Upload));
        Assert.IsTrue(TransferQueueJournal.CanPersist(TransferType.Download));
        Assert.IsFalse(TransferQueueJournal.CanPersist(TransferType.Relay), "中转要两条连接同时在才能接着传");
        Assert.IsFalse(TransferQueueJournal.CanPersist(TransferType.Copy));
        Assert.ThrowsExactly<ArgumentException>(() =>
            journal.Begin(Origin, [new TransferQueueItem(TransferType.Relay, "/src/a", "/dst/a")]));
    }

    [TestMethod]
    public async Task WithoutAStore_TheJournalStillKeepsTheBooksInMemory()
    {
        var journal = new TransferQueueJournal(null);
        Guid id = journal.Begin(Origin, Items(2));
        journal.Settle(id, 1);
        await journal.FlushAsync();

        Assert.AreEqual("/srv/f0.bin", journal.Snapshot(id)!.Items.Single().RemotePath);
        Assert.IsEmpty(await journal.LoadAsync());
    }

    [TestMethod]
    public void ABatchWhoseItemsAreAllSettled_HasNothingToResume()
    {
        var journal = new TransferQueueJournal(null);
        Guid id = journal.Begin(Origin, Items(2));
        journal.Settle(id, 0);
        journal.Settle(id, 1);
        journal.Settle(id, 1);

        Assert.IsNull(journal.Snapshot(id));
    }

    [TestMethod]
    public async Task ChangesAreFlushedByThemselves_AfterTheDelay()
    {
        var store = new JsonStore();
        var journal = new TransferQueueJournal(store, TimeSpan.FromMilliseconds(20));
        journal.Begin(Origin, Items(1));

        for (int i = 0; i < 200 && store.Count == 0; i++)
        {
            await Task.Delay(25);
        }

        Assert.AreEqual(1, store.Count, "不调 FlushAsync 也会在攒够延迟之后自己落盘");
    }

    /// <summary>
    /// 按 JSON 存取的内存文档库(与 SonnetDB 一样经过一次序列化,不会把对象引用原样交回;
    /// 读不出来的行在 <see cref="GetAllAsync{T}" /> 里同样整次抛出,在逐行容错的那一面里只交回 Id)。
    /// </summary>
    private sealed class JsonStore : IAppDataStore, ITolerantAppDataStore
    {
        private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private readonly ConcurrentDictionary<(string Collection, string Id), string> _docs = new();
        private int _writes;

        public int Count => _docs.Count;

        public int Writes => Volatile.Read(ref _writes);

        /// <summary>对哪些文档的写入抛 IOException(模拟写失败,或者写到这一份之前进程就没了)。</summary>
        public Func<string, bool>? FailUpsert { get; set; }

        /// <summary>对哪些文档的删除抛 IOException。</summary>
        public Func<string, bool>? FailDelete { get; set; }

        /// <summary>成功的写入 / 删除,按发生的先后。</summary>
        public List<(bool Upsert, string Id)> Operations { get; } = [];

        public void ResetWrites() => Interlocked.Exchange(ref _writes, 0);

        public bool Contains(string id) => _docs.Keys.Any(k => k.Id == id);

        public IEnumerable<string> RawDocuments() => _docs.Values;

        public Task<T?> GetAsync<T>(string collection, string id, CancellationToken cancellationToken = default) where T : class =>
            Task.FromResult(_docs.TryGetValue((collection, id), out string? json) ? JsonSerializer.Deserialize<T>(json, Json) : null);

        public Task<List<T>> GetAllAsync<T>(string collection, CancellationToken cancellationToken = default) where T : class =>
            Task.FromResult(_docs.Where(d => d.Key.Collection == collection)
                .Select(d => JsonSerializer.Deserialize<T>(d.Value, Json)!)
                .ToList());

        public Task<(List<T> Items, List<string> UnreadableIds)> GetAllReadableAsync<T>(string collection,
            CancellationToken cancellationToken = default) where T : class
        {
            var items = new List<T>();
            var unreadable = new List<string>();
            foreach (KeyValuePair<(string Collection, string Id), string> doc in _docs.Where(d => d.Key.Collection == collection))
            {
                try
                {
                    items.Add(JsonSerializer.Deserialize<T>(doc.Value, Json)!);
                }
                catch (JsonException)
                {
                    unreadable.Add(doc.Key.Id);
                }
            }
            return Task.FromResult((items, unreadable));
        }

        /// <summary>原样塞一份 JSON 进去(可以是坏的),模拟崩溃时写了一半、或者别的版本写下的文档。</summary>
        public void PutRaw(string collection, string id, string json) => _docs[(collection, id)] = json;

        public Task UpsertAsync<T>(string collection, string id, T value, CancellationToken cancellationToken = default) where T : class
        {
            if (FailUpsert?.Invoke(id) == true)
            {
                return Task.FromException(new IOException("injected write failure"));
            }
            _docs[(collection, id)] = JsonSerializer.Serialize(value, Json);
            Interlocked.Increment(ref _writes);
            lock (Operations)
            {
                Operations.Add((true, id));
            }
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string collection, string id, CancellationToken cancellationToken = default)
        {
            if (FailDelete?.Invoke(id) == true)
            {
                return Task.FromException(new IOException("injected delete failure"));
            }
            _docs.TryRemove((collection, id), out _);
            Interlocked.Increment(ref _writes);
            lock (Operations)
            {
                Operations.Add((false, id));
            }
            return Task.CompletedTask;
        }
    }
}
