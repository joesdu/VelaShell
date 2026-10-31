using VelaShell.Core.Models;
using VelaShell.Core.Sftp;

namespace VelaShell.ViewModels;

/// <summary>
/// 一次批量中转里某个源文件的元数据缓存:同一批的自动重试、以及同一批里同一条源路径的重复项,
/// 都复用第一次 stat 的结果,不再回源端问。
/// </summary>
/// <remarks>
/// <para>
/// 为什么值得缓存:<b>FTP 的一条连接同一时刻只能跑一条命令</b>。源栏若是 FTP,中转时源端每一次 <c>SIZE</c>
/// 都要与正在下载的那条数据连接抢同一条控制连接 —— 文件多、重试多的时候,这些往返把一个本来就在慢速链路上的
/// 中转拖得更慢,而它带来的信息(大小与修改时间)在一次批量里根本不会变。
/// </para>
/// <para>
/// 只缓存<b>成功</b>的 stat:失败(源不在了、权限、临时网络错误)照旧每一轮都再试,免得一次瞬时失败被当成定论。
/// </para>
/// <para>
/// 眼下<b>没接上</b>:没有任何代码构造它或 <see cref="StreamedRelaySource" />,中转的每一次尝试照旧在源端 stat 一次
/// (RemoteRelay 的 <c>knownSource</c> 参数也只有测试在传)。上面说的是设计意图,不是现状。
/// </para>
/// </remarks>
public sealed class RelaySourceCache
{
    private readonly Lock _sync = new();
    private readonly Dictionary<(Guid Session, string Path), StreamedRelaySource> _sources = [];

    /// <summary>取该源文件的缓存条目;没有就建一个并记下。</summary>
    /// <param name="source">源端文件服务。</param>
    /// <param name="sessionId">源会话。</param>
    /// <param name="path">源文件的远端路径。</param>
    public StreamedRelaySource For(ISftpService source, Guid sessionId, string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (_sync)
        {
            if (!_sources.TryGetValue((sessionId, path), out StreamedRelaySource? entry))
            {
                entry = new StreamedRelaySource(source, sessionId, path);
                _sources[(sessionId, path)] = entry;
            }
            return entry;
        }
    }
}

/// <summary>流管道中转的源端:一个源文件,以及它第一次被问到的那份元数据。</summary>
public sealed class StreamedRelaySource(ISftpService service, Guid sessionId, string path)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RelaySourceSnapshot? _snapshot;

    /// <summary>源端文件服务。</summary>
    public ISftpService Service { get; } = service;

    /// <summary>源会话。</summary>
    public Guid SessionId { get; } = sessionId;

    /// <summary>源文件的远端路径。</summary>
    public string Path { get; } = path;

    /// <summary>源文件的元数据;已经问过的直接给缓存的那份。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<RelaySourceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _snapshot) is { } cached)
        {
            return cached;
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_snapshot is not null)
            {
                return _snapshot;
            }
            RemoteFileInfo info = await Service.GetFileInfoAsync(SessionId, Path, cancellationToken).ConfigureAwait(false);
            var snapshot = RelaySourceSnapshot.From(info);
            Volatile.Write(ref _snapshot, snapshot);
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }
}
