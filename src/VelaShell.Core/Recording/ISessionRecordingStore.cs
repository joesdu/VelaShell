namespace VelaShell.Core.Recording;

/// <summary>一次会话录制的元数据(文档集合 recordings,Id 为文档键)。</summary>
public class SessionRecording
{
    /// <summary>录制唯一标识(文档键)。</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>会话展示名(标签标题,如服务器名)。</summary>
    public string SessionLabel { get; set; } = string.Empty;

    /// <summary>录制开始时刻(UTC)。</summary>
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>null = 录制中(或应用异常退出未收尾)。</summary>
    public DateTime? EndedAtUtc { get; set; }

    /// <summary>录制输出的累计字节数。</summary>
    public long ByteSize { get; set; }

    /// <summary>录制包含的数据块数量。</summary>
    public int ChunkCount { get; set; }

    /// <summary>录制时长(毫秒);以最后一块输出的偏移为准。</summary>
    public long DurationMs { get; set; }
}

/// <summary>一块录制数据:相对录制开始的毫秒偏移 + 原始终端输出字节。</summary>
/// <param name="OffsetMs">相对录制开始时刻的毫秒偏移。</param>
/// <param name="Data">该时刻的原始终端输出字节。</param>
public sealed record RecordingChunk(long OffsetMs, byte[] Data);

/// <summary>
/// 会话录制存储(设置 → 安全审计 → 会话录制):
/// 元数据存文档集合,输出块存 SonnetDB 时序 measurement(时间 = 开始时刻 + 偏移)。
/// </summary>
public interface ISessionRecordingStore
{
    /// <summary>写入/更新录制元数据(开始时创建,结束与周期刷新时更新)。</summary>
    Task SaveRecordingAsync(SessionRecording recording, CancellationToken cancellationToken = default);

    /// <summary>追加一块输出数据。</summary>
    Task AppendChunkAsync(Guid recordingId, DateTime startedAtUtc, long offsetMs, byte[] data, CancellationToken cancellationToken = default);

    /// <summary>全部录制,按开始时间倒序。</summary>
    Task<List<SessionRecording>> ListRecordingsAsync(CancellationToken cancellationToken = default);

    /// <summary>某次录制的全部数据块,按偏移升序(回放输入)。</summary>
    Task<List<RecordingChunk>> GetChunksAsync(Guid recordingId, CancellationToken cancellationToken = default);

    /// <summary>删除一次录制(元数据 + 数据块)。</summary>
    Task DeleteRecordingAsync(Guid recordingId, CancellationToken cancellationToken = default);

    /// <summary>清理超过保留天数的录制(随会话日志保留天数,启动时调用)。</summary>
    Task CleanupExpiredAsync(int retentionDays, CancellationToken cancellationToken = default);
}
