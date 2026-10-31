using System.Diagnostics;
using VelaShell.Core.Data;
using VelaShell.Core.Models;

namespace VelaShell.Core.Sftp;

/// <summary>
/// 传输队列持久化:文件面板发起的上传 / 下载批次里还没落定的项,记在 SonnetDB 的一个文档集合里,
/// 断线或重启之后还原成「已暂停」,由用户点「继续」时再连、再传。
/// </summary>
/// <remarks>
/// <para>
/// <b>记什么</b>:批次开始时整批登记;每一项落定(传完、跳过、永久失败)就从里面拿掉;
/// 用户取消或放弃整批删掉。剩下的 —— 还在排队的、正在传的、因断线停下的 —— 就是下次要接着传的。
/// 永久失败的项不留:换一条连接也传不过去,失败原因面板与传输日志里都有。
/// </para>
/// <para>
/// <b>不记什么</b>:同会话复制与双栏中转(后者要两条连接同时在才能接着传)、目录同步(计划是在预览里逐条确认的,
/// 重启之后应当重新比较,而不是照一份可能已过时的计划去写)、远程编辑的自动回传、AI 插件发起的传输
/// (它们有自己的任务登记、审批租约与回执,宿主明确不承诺跨重启恢复)。
/// </para>
/// <para>
/// <b>写得多勤</b>:改动先记在内存里,攒 <see cref="FlushDelay" /> 再落盘一次,只重写变过的段
/// (见 <see cref="TransferQueueChunk" />)。最坏情况是进程在这段时间里被杀掉:刚传完的几个文件下次还在队里,
/// 恢复时目标已经完整,按续传探测不会当成半截,整份重传一遍 —— 多花点流量,不会写坏。
/// </para>
/// <para>
/// 没有存储(单元测试、无数据库的宿主)时只在内存里记账:本次运行内的暂停 / 继续照常可用。
/// </para>
/// </remarks>
public sealed class TransferQueueJournal
{
    /// <summary>文档集合名(SonnetDbEngine 在建库时一并创建,与 <c>recordings</c> 同一种形状)。</summary>
    public const string Collection = "transfer_queue";

    /// <summary>一份文档最多装多少项。</summary>
    public const int ChunkSize = 200;

    private const string UploadTag = "upload";
    private const string DownloadTag = "download";

    private readonly IAppDataStore? _store;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, BatchState> _batches = [];
    private readonly HashSet<(Guid Batch, int Chunk)> _dirty = [];
    // 读不出来、Id 也认不出是哪一批哪一段的文档:下次落盘时按 Id 删掉。
    private readonly HashSet<string> _orphans = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private bool _flushScheduled;

    /// <summary>以给定的存储构造;<paramref name="store" /> 为 null 时只在内存里记账。</summary>
    /// <param name="store">SonnetDB 文档存储。</param>
    /// <param name="flushDelay">改动攒多久落一次盘;null = 默认 1 秒。</param>
    public TransferQueueJournal(IAppDataStore? store, TimeSpan? flushDelay = null)
    {
        _store = store;
        FlushDelay = flushDelay ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>改动攒多久落一次盘。</summary>
    public TimeSpan FlushDelay { get; }

    /// <summary>这一类传输记不记进队列(只有上传与下载)。</summary>
    public static bool CanPersist(TransferType type) => type is TransferType.Upload or TransferType.Download;

    /// <summary>登记一批传输,返回它在队列里的 Id。项的序号就是它在 <paramref name="items" /> 里的位置。</summary>
    /// <param name="origin">发起它的连接。</param>
    /// <param name="items">要传的项(只能是上传或下载)。</param>
    public Guid Begin(TransferQueueOrigin origin, IReadOnlyList<TransferQueueItem> items)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(items);
        var id = Guid.NewGuid();
        var slots = new TransferQueueItem?[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            if (!CanPersist(items[i].Type))
            {
                throw new ArgumentException($"Transfer type {items[i].Type} is not persisted.", nameof(items));
            }
            slots[i] = items[i];
        }
        lock (_gate)
        {
            var state = new BatchState(origin, DateTime.UtcNow, slots);
            _batches[id] = state;
            for (int chunk = 0; chunk < state.ChunkCount; chunk++)
            {
                _dirty.Add((id, chunk));
            }
        }
        ScheduleFlush();
        return id;
    }

    /// <summary>一项落定(传完、跳过、永久失败):从队列里拿掉。</summary>
    public void Settle(Guid batchId, int index)
    {
        lock (_gate)
        {
            if (!_batches.TryGetValue(batchId, out BatchState? state)
                || (uint)index >= (uint)state.Items.Length
                || state.Items[index] is null)
            {
                return;
            }
            state.Items[index] = null;
            state.Remaining--;
            _dirty.Add((batchId, index / ChunkSize));
        }
        ScheduleFlush();
    }

    /// <summary>整批作废(用户取消、放弃,或已经交给新的一批接着传)。</summary>
    public void Drop(Guid batchId)
    {
        lock (_gate)
        {
            if (!_batches.Remove(batchId, out BatchState? state))
            {
                return;
            }
            for (int chunk = 0; chunk < state.ChunkCount; chunk++)
            {
                _dirty.Add((batchId, chunk));
            }
        }
        ScheduleFlush();
    }

    /// <summary>某一批此刻还没落定的项;批次不在或已经全部落定时返回 null。</summary>
    public PausedTransferBatch? Snapshot(Guid batchId)
    {
        lock (_gate)
        {
            return _batches.TryGetValue(batchId, out BatchState? state) && state.Remaining > 0
                ? state.ToPaused(batchId)
                : null;
        }
    }

    /// <summary>
    /// 读出上次运行留下的批次(启动时调一次),登记进本次的账里,好让「继续」与「放弃」照常作用于它们。
    /// </summary>
    /// <remarks>
    /// 读不出的文档(坏 JSON、字段类型对不上、认不出的方向、空路径)跳过,其余批次照常还原;
    /// 整份读不出来的文档下次落盘时删掉(记一笔日志),一项都不剩的批次顺手删掉。
    /// 逐行容错要存储支持(<see cref="ITolerantAppDataStore" />,SonnetDB 实现了);不支持的存储里有一行坏的,整次读取照旧失败。
    /// </remarks>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按开始时间排的批次。</returns>
    public async Task<IReadOnlyList<PausedTransferBatch>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_store is null)
        {
            return [];
        }
        List<TransferQueueChunk>? chunks;
        List<string> unreadable = [];
        if (_store is ITolerantAppDataStore tolerant)
        {
            // 一行坏的(崩溃时写了一半、或者以后的版本改了字段类型)不能让所有暂停的批次都还原不出来 —— 而且每次启动都这样。
            (chunks, unreadable) = await tolerant.GetAllReadableAsync<TransferQueueChunk>(Collection, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            chunks = await _store.GetAllAsync<TransferQueueChunk>(Collection, cancellationToken).ConfigureAwait(false);
        }
        var restored = new List<PausedTransferBatch>();
        lock (_gate)
        {
            foreach (string id in unreadable)
            {
                Trace.WriteLine($"[VelaShell] Transfer queue document {id} is unreadable; discarding it.");
                // 认得出是哪一批哪一段的,交给那一段的落盘逻辑(批次还原了就按现状重写,没还原就删);认不出的按 Id 删。
                if (TryParseDocumentId(id, out Guid batch, out int chunk))
                {
                    _dirty.Add((batch, chunk));
                }
                else
                {
                    _orphans.Add(id);
                }
            }
            foreach (IGrouping<Guid, TransferQueueChunk> group in (chunks ?? []).Where(c => c.BatchId != Guid.Empty).GroupBy(c => c.BatchId))
            {
                if (_batches.ContainsKey(group.Key))
                {
                    continue;
                }
                TransferQueueChunk head = group.First();
                var entries = new SortedDictionary<int, TransferQueueItem>();
                foreach (TransferQueueChunkEntry entry in group.SelectMany(c => c.Items ?? []))
                {
                    if (entry.Index >= 0 && TryParse(entry, out TransferQueueItem item))
                    {
                        entries.TryAdd(entry.Index, item);
                    }
                }
                int length = Math.Max(
                    entries.Count == 0 ? 0 : entries.Keys.Max() + 1,
                    (group.Max(c => c.Chunk) + 1) * ChunkSize);
                var slots = new TransferQueueItem?[length];
                foreach ((int index, TransferQueueItem item) in entries)
                {
                    slots[index] = item;
                }
                var state = new BatchState(new(head.ProfileId, head.ServerName ?? string.Empty), head.CreatedUtc, slots);
                if (state.Remaining == 0)
                {
                    foreach (TransferQueueChunk chunk in group)
                    {
                        _dirty.Add((group.Key, chunk.Chunk));
                    }
                    continue;
                }
                // 坏掉的段(一项都没读出来)也记脏:下次落盘时按现状重写或删掉。
                foreach (TransferQueueChunk chunk in group.Where(c => (c.Items ?? []).Count == 0))
                {
                    _dirty.Add((group.Key, chunk.Chunk));
                }
                _batches[group.Key] = state;
                restored.Add(state.ToPaused(group.Key));
            }
        }
        ScheduleFlush();
        return [.. restored.OrderBy(b => b.CreatedUtc)];
    }

    /// <summary>把攒着的改动落盘。写失败只记一笔日志 —— 队列写不进去不能连累传输本身。</summary>
    public async Task FlushAsync()
    {
        if (_store is null)
        {
            lock (_gate)
            {
                _dirty.Clear();
                _orphans.Clear();
                _flushScheduled = false;
            }
            return;
        }
        await _flushLock.WaitAsync().ConfigureAwait(false);
        try
        {
            List<(string Id, TransferQueueChunk? Document)> work;
            lock (_gate)
            {
                _flushScheduled = false;
                work = [.. _dirty.Select(key => (DocumentId(key.Batch, key.Chunk), BuildChunk(key.Batch, key.Chunk)))];
                work.AddRange(_orphans.Select(static id => (id, (TransferQueueChunk?)null)));
                _dirty.Clear();
                _orphans.Clear();
            }
            foreach ((string id, TransferQueueChunk? document) in work)
            {
                try
                {
                    if (document is null)
                    {
                        await _store.DeleteAsync(Collection, id).ConfigureAwait(false);
                    }
                    else
                    {
                        await _store.UpsertAsync(Collection, id, document).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Trace.WriteLine($"[VelaShell] Transfer queue write failed for {id}: {ex.Message}");
                }
            }
        }
        finally
        {
            _flushLock.Release();
        }
    }

    /// <summary>文档 Id:批次 + 段号。</summary>
    public static string DocumentId(Guid batchId, int chunk) => $"{batchId:N}-{chunk:D4}";

    /// <summary><see cref="DocumentId" /> 的反解;不是这个格式的 Id 返回 false。</summary>
    private static bool TryParseDocumentId(string id, out Guid batchId, out int chunk)
    {
        int dash = id.LastIndexOf('-');
        chunk = 0;
        batchId = Guid.Empty;
        // 段号再大也得乘得出起始序号(BuildChunk 按 chunk * ChunkSize 取段),乘不出的当认不出。
        return dash > 0
               && Guid.TryParseExact(id.AsSpan(0, dash), "N", out batchId)
               && int.TryParse(id.AsSpan(dash + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out chunk)
               && chunk < int.MaxValue / ChunkSize;
    }

    private void ScheduleFlush()
    {
        if (_store is null)
        {
            return;
        }
        lock (_gate)
        {
            if (_flushScheduled)
            {
                return;
            }
            _flushScheduled = true;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(FlushDelay).ConfigureAwait(false);
                await FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Trace.WriteLine($"[VelaShell] Transfer queue flush failed: {ex.Message}");
            }
        });
    }

    /// <summary>某一段此刻该写成什么;批次不在或这一段已经全部落定时返回 null(删掉这份文档)。</summary>
    private TransferQueueChunk? BuildChunk(Guid batchId, int chunk)
    {
        if (!_batches.TryGetValue(batchId, out BatchState? state))
        {
            return null;
        }
        var items = new List<TransferQueueChunkEntry>();
        int end = Math.Min(state.Items.Length, (chunk + 1) * ChunkSize);
        for (int i = chunk * ChunkSize; i < end; i++)
        {
            if (state.Items[i] is { } item)
            {
                items.Add(new()
                {
                    Index = i,
                    Direction = item.Type == TransferType.Upload ? UploadTag : DownloadTag,
                    LocalPath = item.LocalPath,
                    RemotePath = item.RemotePath,
                });
            }
        }
        return items.Count == 0
            ? null
            : new()
            {
                BatchId = batchId,
                Chunk = chunk,
                ProfileId = state.Origin.ProfileId,
                ServerName = state.Origin.ServerName,
                CreatedUtc = state.CreatedUtc,
                Items = items,
            };
    }

    private static bool TryParse(TransferQueueChunkEntry entry, out TransferQueueItem item)
    {
        TransferType? type = entry.Direction switch
        {
            UploadTag => TransferType.Upload,
            DownloadTag => TransferType.Download,
            _ => null,
        };
        if (type is null || string.IsNullOrWhiteSpace(entry.LocalPath) || string.IsNullOrWhiteSpace(entry.RemotePath))
        {
            item = default;
            return false;
        }
        item = new(type.Value, entry.LocalPath, entry.RemotePath);
        return true;
    }

    /// <summary>一批的账:每个槽位一项,落定了就置空。</summary>
    private sealed class BatchState
    {
        public BatchState(TransferQueueOrigin origin, DateTime createdUtc, TransferQueueItem?[] items)
        {
            Origin = origin;
            CreatedUtc = createdUtc;
            Items = items;
            Remaining = items.Count(static i => i is not null);
        }

        public TransferQueueOrigin Origin { get; }

        public DateTime CreatedUtc { get; }

        public TransferQueueItem?[] Items { get; }

        public int Remaining { get; set; }

        public int ChunkCount => (Items.Length + ChunkSize - 1) / ChunkSize;

        public PausedTransferBatch ToPaused(Guid id) =>
            new(id, Origin, CreatedUtc, [.. Items.Where(static i => i is not null).Select(static i => i!.Value)]);
    }
}
