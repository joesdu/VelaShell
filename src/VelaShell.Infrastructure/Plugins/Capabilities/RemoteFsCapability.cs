using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.RemoteFs;
using VelaShell.PluginSdk.Storage;

namespace VelaShell.Infrastructure.Plugins.Capabilities;

/// <summary>
/// <see cref="IRemoteFsApi" /> 的桥接实现:复用宿主既有会话的 SFTP 通道(不重复认证)。
/// 进度回调做 ≥100ms 节流 —— 插件流量不允许灌爆调用方的调度器
/// (吸取大文件传输卡顿的历史教训)。
/// </summary>
internal sealed partial class RemoteFsCapability(ISftpService sftp, ISshConnectionService connections,
    CancellationToken lifetime = default, string? pluginId = null, PluginPermissionGate? uploadPermissions = null,
    IPluginStorage? storage = null) : IRemoteFsApi, IDisposable
{
    // 链接两个字段(SDK 2.1.3)随条目交给插件:进程内直接带,隔离模式 fs/list、fs/stat 的 JSON 自动带上 ——
    // 插件(含 AI 插件)经 IRemoteFsApi 看得到链接,删除、递归时才知道只处理链接本身。
    private static RemoteFileEntry Map(RemoteFileInfo info) => new(
        info.Name, info.FullPath, info.IsDirectory, info.Size,
        new DateTimeOffset(DateTime.SpecifyKind(info.LastModified, DateTimeKind.Utc), TimeSpan.Zero),
        info.Permissions, info.Owner, info.Group)
    {
        IsSymbolicLink = info.IsSymbolicLink,
        LinkTarget = info.IsSymbolicLink ? info.LinkTarget : null
    };

    /// <summary>解析并校验会话 id;无效或未连接时抛 <see cref="PluginSessionNotFoundException" />。</summary>
    private Guid Resolve(string sessionId)
    {
        if (!Guid.TryParse(sessionId, out Guid id)
            || connections.GetSession(id) is not { Status: SessionStatus.Connected })
        {
            throw new PluginSessionNotFoundException(sessionId);
        }
        return id;
    }

    /// <summary>
    /// 把宿主进度转成 SDK 进度并节流:至少间隔 100ms 才转发一次。满格那一帧(插件拿它当「完成」)不从这里转发,
    /// 由 <see cref="TransferAsync" /> 在传输任务成功之后补发一次 —— 上传在最后一批确认到齐时就报满格,
    /// 那时 fsync / CLOSE 还没做、仍可能失败;收尾时还会再报一遍。原样转发,插件就会收到两次「完成」,
    /// 而且早于真正的结果。
    /// </summary>
    /// <remarks>
    /// 节流与扣住满格在回调线程上同步做;转发照旧经 <see cref="Progress{T}" /> 投递到调用方的同步上下文,
    /// 补发的完成帧也走这同一条投递,排在之前那些帧的后面。
    /// </remarks>
    private sealed class ProgressRelay(IProgress<RemoteTransferProgress> target) : IProgress<TransferProgress>
    {
        private readonly IProgress<RemoteTransferProgress> _marshaled = new Progress<RemoteTransferProgress>(target.Report);
        private long _lastTicks;
        private long _totalBytes = -1;

        public void Report(TransferProgress value)
        {
            Interlocked.Exchange(ref _totalBytes, value.TotalBytes);
            if (value.TotalBytes > 0 && value.BytesTransferred >= value.TotalBytes)
            {
                return;
            }
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastTicks) < 100)
            {
                return;
            }
            Interlocked.Exchange(ref _lastTicks, now);
            _marshaled.Report(new(value.BytesTransferred, value.TotalBytes));
        }

        /// <summary>传输成功:补发唯一的一帧满格。总量未知(没收到过进度,或总量为 0)时不补。</summary>
        public void Complete()
        {
            long total = Interlocked.Read(ref _totalBytes);
            if (total > 0)
            {
                _marshaled.Report(new(total, total));
            }
        }
    }

    /// <summary>跑一次带进度的传输;完成帧以任务结果为准,见 <see cref="ProgressRelay" />。</summary>
    private static async Task TransferAsync(
        IProgress<RemoteTransferProgress>? progress, Func<IProgress<TransferProgress>?, Task> transfer)
    {
        ProgressRelay? relay = progress is null ? null : new(progress);
        await transfer(relay).ConfigureAwait(false);
        relay?.Complete();
    }

    public async Task<IReadOnlyList<RemoteFileEntry>> ListDirectoryAsync(string sessionId, string path,
        CancellationToken cancellationToken = default)
    {
        List<RemoteFileInfo> entries = await sftp.ListDirectoryAsync(Resolve(sessionId), path, cancellationToken).ConfigureAwait(false);
        return [.. entries.Select(Map)];
    }

    public async Task<RemoteFileEntry?> StatAsync(string sessionId, string path, CancellationToken cancellationToken = default)
    {
        try
        {
            return Map(await sftp.GetFileInfoAsync(Resolve(sessionId), path, cancellationToken).ConfigureAwait(false));
        }
        catch (FileNotFoundException)
        {
            return null; // 语义约定:不存在返回 null,不以异常判存在。
        }
    }

    public Task<bool> ExistsAsync(string sessionId, string path, CancellationToken cancellationToken = default)
        => sftp.ExistsAsync(Resolve(sessionId), path, cancellationToken);

    public Task<string> GetWorkingDirectoryAsync(string sessionId, CancellationToken cancellationToken = default)
        => sftp.GetWorkingDirectoryAsync(Resolve(sessionId), cancellationToken);

    public Task DownloadFileAsync(string sessionId, string remotePath, string localPath,
        IProgress<RemoteTransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Guid id = Resolve(sessionId);
        return TransferAsync(progress, relay => sftp.DownloadFileAsync(id, remotePath, localPath, relay, 0, cancellationToken));
    }

    public Task UploadFileAsync(string sessionId, string localPath, string remotePath,
        IProgress<RemoteTransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Guid id = Resolve(sessionId);
        return TransferAsync(progress, relay => sftp.UploadFileAsync(id, localPath, remotePath, relay, 0, cancellationToken));
    }

    public Task<Stream> OpenReadAsync(string sessionId, string remotePath, CancellationToken cancellationToken = default)
        => sftp.OpenReadAsync(Resolve(sessionId), remotePath, cancellationToken);

    public async Task<byte[]> ReadAllBytesAsync(string sessionId, string remotePath, int maxBytes = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        Guid id = Resolve(sessionId);
        RemoteFileInfo info = await sftp.GetFileInfoAsync(id, remotePath, cancellationToken).ConfigureAwait(false);
        if (info.Size > maxBytes)
        {
            throw new InvalidOperationException(
                $"Remote file '{remotePath}' is {info.Size} bytes (limit {maxBytes}); use OpenReadAsync/DownloadFileAsync for large files.");
        }
        await using Stream stream = await sftp.OpenReadAsync(id, remotePath, cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream(capacity: (int)Math.Min(info.Size, maxBytes));
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    public async Task WriteAllBytesAsync(string sessionId, string remotePath, ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        Guid id = Resolve(sessionId);
        string tmp = Path.Combine(Path.GetTempPath(), $"velashell-plugin-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(tmp, content.ToArray(), cancellationToken).ConfigureAwait(false);
            await sftp.UploadFileAsync(id, tmp, remotePath, null, 0, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    public Task DeleteAsync(string sessionId, string remotePath, CancellationToken cancellationToken = default)
        => sftp.DeleteAsync(Resolve(sessionId), remotePath, null, cancellationToken);

    public Task CreateDirectoryAsync(string sessionId, string remotePath, CancellationToken cancellationToken = default)
        => sftp.CreateDirectoryAsync(Resolve(sessionId), remotePath, cancellationToken);

    public Task EnsureDirectoryAsync(string sessionId, string remotePath, CancellationToken cancellationToken = default)
        => sftp.EnsureDirectoryAsync(Resolve(sessionId), remotePath, cancellationToken);

    public Task RenameAsync(string sessionId, string oldPath, string newPath, CancellationToken cancellationToken = default)
        => sftp.RenameAsync(Resolve(sessionId), oldPath, newPath, cancellationToken);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // 临时文件清理尽力而为。
        }
    }
}
