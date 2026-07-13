using SonnetDB.Model;
using SonnetDB.Sql.Execution;
using VelaShell.Core.Recording;

namespace VelaShell.Infrastructure.Persistence;

/// <summary>
/// 会话录制存储:元数据存文档集合 <c>recordings</c>(Id 为文档键),
/// 输出块存时序 measurement <c>session_recording_chunks</c>
/// (时间 = 录制开始时刻 + offset_ms,tag = recording_id)。
/// </summary>
public sealed class SonnetDbSessionRecordingStore(SonnetDbEngine engine) : ISessionRecordingStore
{
    private readonly SonnetDbEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    /// <summary>保存(新增或覆盖)一条会话录制的元数据文档。</summary>
    public async Task SaveRecordingAsync(SessionRecording recording, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recording);
        string json = SonnetDbJson.Serialize(recording);
        await _engine.WithCollectionAsync<object?>(SonnetDbEngine.RecordingsCollection, store =>
        {
            store.Upsert(DocId(recording.Id), json);
            return null;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 追加一个录制输出块:以「录制开始时刻 + <paramref name="offsetMs" />」为时间点、
    /// 录制 Id 为 tag,将 Base64 编码的数据写入时序 measurement。
    /// </summary>
    public Task AppendChunkAsync(Guid recordingId, DateTime startedAtUtc, long offsetMs, byte[] data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var tags = new Dictionary<string, string> { ["recording_id"] = recordingId.ToString("D") };
        var fields = new Dictionary<string, FieldValue>
        {
            ["offset_ms"] = FieldValue.FromLong(offsetMs),
            ["data"] = FieldValue.FromString(Convert.ToBase64String(data))
        };
        return _engine.WritePointAsync(SonnetDbEngine.RecordingChunksMeasurement,
            new DateTimeOffset(DateTime.SpecifyKind(startedAtUtc, DateTimeKind.Utc)).AddMilliseconds(offsetMs),
            tags, fields, cancellationToken);
    }

    /// <summary>列出所有会话录制元数据,按开始时间倒序排列。</summary>
    public async Task<List<SessionRecording>> ListRecordingsAsync(CancellationToken cancellationToken = default)
    {
        List<SessionRecording?> rows = await _engine.WithCollectionAsync(SonnetDbEngine.RecordingsCollection, store =>
                                               store.Scan().Select(row => SonnetDbJson.Deserialize<SessionRecording>(row.Json)).ToList(),
                                           cancellationToken).ConfigureAwait(false);
        return [.. rows.Where(r => r is not null).Cast<SessionRecording>().OrderByDescending(r => r.StartedAtUtc)];
    }

    /// <summary>
    /// 读取指定录制的全部输出块并按偏移量升序返回;单块 Base64 解码失败时跳过该块,不影响其余回放。
    /// </summary>
    public async Task<List<RecordingChunk>> GetChunksAsync(Guid recordingId, CancellationToken cancellationToken = default)
    {
        // SonnetDB 方言要求 ORDER BY time 时 SELECT 列表必须包含 time 列。
        SelectExecutionResult result = await _engine.QueryAsync(
                                           $"SELECT time, offset_ms, data FROM {SonnetDbEngine.RecordingChunksMeasurement} " +
                                           $"WHERE recording_id = '{recordingId:D}' ORDER BY time ASC LIMIT 1000000",
                                           cancellationToken).ConfigureAwait(false);
        int offsetIndex = IndexOf(result, "offset_ms");
        int dataIndex = IndexOf(result, "data");
        var chunks = new List<RecordingChunk>(result.Rows.Count);
        foreach (IReadOnlyList<object?> row in result.Rows)
        {
            if (offsetIndex >= row.Count || dataIndex >= row.Count)
            {
                continue;
            }
            long offset = Convert.ToInt64(row[offsetIndex] ?? 0L);
            string? base64 = row[dataIndex]?.ToString();
            if (string.IsNullOrEmpty(base64))
            {
                continue;
            }
            try
            {
                chunks.Add(new(offset, Convert.FromBase64String(base64)));
            }
            catch (FormatException)
            {
                // 单块损坏跳过,不影响其余回放。
            }
        }
        chunks.Sort((a, b) => a.OffsetMs.CompareTo(b.OffsetMs));
        return chunks;
    }

    /// <summary>
    /// 删除指定录制:先删元数据,再尽力删除其数据块;数据块删除失败时留作孤儿,
    /// 待后续清理的压缩重建路径统一回收。
    /// </summary>
    public async Task DeleteRecordingAsync(Guid recordingId, CancellationToken cancellationToken = default)
    {
        await DeleteMetadataAsync(recordingId, cancellationToken).ConfigureAwait(false);

        // 数据块尽力删除:SQL 方言不支持 DELETE 时留作不可见孤儿
        // (元数据已删,列表/回放均不可达);孤儿字节由 CleanupExpiredAsync
        // 的压缩重建路径在下次启动清理时统一回收。
        await TryDeleteChunksAsync(recordingId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 清理超过保留天数的过期录制;若数据块无法直接删除,则对存活录制执行压缩重建以回收孤儿字节。
    /// </summary>
    public async Task CleanupExpiredAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        if (retentionDays < 1)
        {
            return;
        }
        DateTime cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        List<SessionRecording> all = await ListRecordingsAsync(cancellationToken).ConfigureAwait(false);
        List<SessionRecording> expired = [.. all.Where(r => r.StartedAtUtc < cutoff)];
        if (expired.Count == 0)
        {
            return;
        }
        bool chunkDeleteFailed = false;
        foreach (SessionRecording recording in expired)
        {
            await DeleteMetadataAsync(recording.Id, cancellationToken).ConfigureAwait(false);
            if (!await TryDeleteChunksAsync(recording.Id, cancellationToken).ConfigureAwait(false))
            {
                chunkDeleteFailed = true;
            }
        }

        // SQL DELETE 不可用时,孤儿数据块会让磁盘只增不减:改走压缩重建 ——
        // 读出存活录制的数据块 → drop 重建 measurement → 回写,孤儿字节随 drop 回收。
        if (chunkDeleteFailed)
        {
            await CompactChunksAsync([.. all.Where(r => r.StartedAtUtc >= cutoff)], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 压缩重建数据块 measurement:仅在删除失败(方言不支持 DELETE)时执行。
    /// 存活数据先整体载入内存再重建,超过安全上限(256MB)时跳过本次压缩,
    /// 等保留窗口滚动、存活数据变小后的下次清理再试。
    /// </summary>
    private async Task CompactChunksAsync(List<SessionRecording> survivors, CancellationToken cancellationToken)
    {
        const long maxCompactBytes = 256L * 1024 * 1024;
        if (survivors.Sum(r => r.ByteSize) > maxCompactBytes)
        {
            return;
        }
        var kept = new List<(SessionRecording Recording, List<RecordingChunk> Chunks)>(survivors.Count);
        foreach (SessionRecording recording in survivors)
        {
            kept.Add((recording, await GetChunksAsync(recording.Id, cancellationToken).ConfigureAwait(false)));
        }
        await _engine.ResetMeasurementAsync(SonnetDbEngine.RecordingChunksMeasurement, cancellationToken).ConfigureAwait(false);
        foreach ((SessionRecording recording, List<RecordingChunk> chunks) in kept)
        {
            foreach (RecordingChunk chunk in chunks)
            {
                await AppendChunkAsync(recording.Id, recording.StartedAtUtc, chunk.OffsetMs, chunk.Data, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private Task<object?> DeleteMetadataAsync(Guid recordingId, CancellationToken cancellationToken) =>
        _engine.WithCollectionAsync<object?>(SonnetDbEngine.RecordingsCollection, store =>
        {
            store.Delete(DocId(recordingId));
            return null;
        }, cancellationToken);

    private Task<bool> TryDeleteChunksAsync(Guid recordingId, CancellationToken cancellationToken) =>
        _engine.TryExecuteAsync(
            $"DELETE FROM {SonnetDbEngine.RecordingChunksMeasurement} WHERE recording_id = '{recordingId:D}'",
            cancellationToken);

    private static string DocId(Guid id) => id.ToString("D");

    private static int IndexOf(SelectExecutionResult result, string column)
    {
        for (int i = 0; i < result.Columns.Count; i++)
        {
            if (string.Equals(result.Columns[i], column, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return int.MaxValue;
    }
}
