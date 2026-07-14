using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;

namespace VelaShell.Core.Sftp;

/// <summary>
/// 基于 SSH 会话的 SFTP 文件操作服务:按会话缓存并复用 SFTP 客户端,提供目录浏览、
/// 上传/下载、删除、重命名、权限设置等远端文件系统操作。
/// </summary>
public class SftpService(
    ISshConnectionService connectionService,
    Func<SshSession, ISftpClientWrapper>? sftpClientFactory = null,
    ISettingsService? settingsService = null) : ISftpService
{
    private readonly ISshConnectionService _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
    private readonly ConcurrentDictionary<Guid, ISftpClientWrapper> _sftpClients = new();

    /// <summary>属主/属组的数字 id → 名称翻译(按会话缓存,见 RemoteIdentityResolver)。</summary>
    private readonly RemoteIdentityResolver _identities = new(connectionService);

    /// <summary>SFTP 客户端创建的按会话单飞闸(见 GetOrCreateSftpClientAsync)。</summary>
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _clientGates = new();

    /// <summary>列出指定会话下远端目录的条目(自动剔除 "." 与 ".." )。</summary>
    public async Task<List<RemoteFileInfo>> ListDirectoryAsync(Guid sessionId, string path, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        IEnumerable<SftpEntry> files = await client.ListDirectoryAsync(path, cancellationToken).ConfigureAwait(false);

        // 属主/属组名要查远端 passwd 库(SFTP 只报数字 id):每会话查一次,查不到回退数字。
        RemoteIdentityMap identities = await _identities.GetAsync(sessionId).ConfigureAwait(false);
        return [.. files.Where(f => f.Name is not "." and not "..").Select(f => MapToRemoteFileInfo(f, identities))];
    }

    /// <summary>将本地文件上传到远端路径,可选限速与进度回报,支持取消。</summary>
    public async Task UploadFileAsync(Guid sessionId,
        string localPath,
        string remotePath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var fileInfo = new FileInfo(localPath);
        long totalBytes = fileInfo.Length;
        string fileName = Path.GetFileName(localPath);
        var stopwatch = Stopwatch.StartNew();
        (long uploadBps, _, _) = await GetTransferTuningAsync().ConfigureAwait(false);
        Stream fileStream = uploadBps > 0
                                ? new ThrottledStream(File.OpenRead(localPath), uploadBps)
                                : File.OpenRead(localPath);

        // NOTE: SSH.NET invokes this progress callback on a detached thread-pool thread
        // (ThreadPool.QueueUserWorkItem), so throwing from it would be an unhandled exception that
        // crashes the process. To cancel the in-flight file we instead dispose our own stream, which
        // makes the worker's read fail; we normalise that into a clean cancellation below.
        await using (cancellationToken.Register(() => SafeDispose(fileStream)))
        {
            try
            {
                await client.UploadAsync(fileStream, remotePath, bytesTransferred => { ReportProgress(progress, fileName, (long)bytesTransferred, totalBytes, stopwatch); }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is not OperationCanceledException)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            finally
            {
                await fileStream.DisposeAsync();
            }
        }
    }

    /// <summary>将远端文件下载到本地路径,可选限速与进度回报;按设置可保留文件修改时间戳。</summary>
    public async Task DownloadFileAsync(Guid sessionId,
        string remotePath,
        string localPath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        string fileName = GetUnixFileName(remotePath);
        RemoteFileInfo fileInfo = await GetFileInfoAsync(sessionId, remotePath, cancellationToken).ConfigureAwait(false);
        long totalBytes = fileInfo.Size;
        var stopwatch = Stopwatch.StartNew();
        (_, long downloadBps, bool preserveTimestamps) = await GetTransferTuningAsync().ConfigureAwait(false);
        Stream fileStream = downloadBps > 0
                                ? new ThrottledStream(File.Create(localPath), downloadBps)
                                : File.Create(localPath);

        // See UploadFileAsync: the callback runs on a detached thread-pool thread, so we cancel by
        // disposing our own stream (failing the worker's write) rather than throwing from it.
        await using (cancellationToken.Register(() => SafeDispose(fileStream)))
        {
            try
            {
                await client.DownloadAsync(remotePath, fileStream, bytesTransferred => { ReportProgress(progress, fileName, (long)bytesTransferred, totalBytes, stopwatch); }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is not OperationCanceledException)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            finally
            {
                await fileStream.DisposeAsync();
            }
        }

        // 保留文件时间戳(设置 → 文件传输):下载完成后把远端修改时间写到本地副本。
        if (preserveTimestamps && fileInfo.LastModified != default)
        {
            try
            {
                File.SetLastWriteTime(localPath, fileInfo.LastModified);
            }
            catch
            {
                // 时间戳只是尽力而为。
            }
        }
    }

    /// <summary>删除远端文件或目录;目录按深度优先递归删除子项,并逐条回报删除进度。</summary>
    public async Task DeleteAsync(Guid sessionId, string remotePath, IProgress<SftpDeleteProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await Task.Run(() =>
        {
            if (!client.Exists(remotePath))
            {
                throw new FileNotFoundException($"Remote path not found: {remotePath}");
            }
            string parentDir = GetUnixParentDirectory(remotePath);
            string name = GetUnixFileName(remotePath);
            SftpEntry? entry = client.ListDirectory(parentDir).FirstOrDefault(f => f.Name == name);
            bool isDirectory = entry is { IsDirectory: true };
            int total = CountEntries(client, remotePath, isDirectory, cancellationToken);

            // Emit a "0 / total" tick so the UI can immediately switch to determinate progress.
            progress?.Report(new(0, total, remotePath));
            int deleted = 0;
            DeleteEntry(client, remotePath, isDirectory, total, ref deleted, progress, cancellationToken);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>在远端创建指定路径的目录。</summary>
    public async Task CreateDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await Task.Run(() => { client.CreateDirectory(remotePath); }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>在远端创建一个空文件。</summary>
    public async Task CreateFileAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        using var empty = new MemoryStream();
        await client.UploadAsync(empty, remotePath, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>幂等地确保远端目录存在:直接创建,若因已存在而失败则视为成功。</summary>
    public async Task EnsureDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await Task.Run(() =>
        {
            // 直接创建而非先 Exists 探测:SSH.NET 的 Exists 对不存在的路径以内部异常实现,
            // 上传新文件夹树时会逐目录刷 SftpPathNotFoundException 并各多一次网络往返。
            // 创建失败且目录确实已存在 → 幂等成功;其余失败(权限/父目录缺失)照常抛出。
            try
            {
                client.CreateDirectory(remotePath);
            }
            catch (SshClientException) when (client.Exists(remotePath)) { }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>重命名或移动远端文件/目录;普通 rename 被服务器拒绝时回退到 posix-rename 扩展。</summary>
    public async Task RenameAsync(Guid sessionId, string oldPath, string newPath, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await Task.Run(() =>
        {
            try
            {
                client.RenameFile(oldPath, newPath);
            }
            catch (Exception ex) when (ex is SftpOperationException or NotSupportedException)
            {
                // Some SFTP servers reject the plain SSH_FXP_RENAME with SSH_FX_BAD_MESSAGE (surfaced
                // as "bad message") — commonly for cross-directory moves. Retry with the widely
                // supported posix-rename@openssh.com extension, but surface the original, more
                // meaningful error if that path is unavailable too.
                try
                {
                    client.PosixRenameFile(oldPath, newPath);
                }
                catch
                {
                    throw ex;
                }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>以三位八进制模式(000-777)设置远端文件/目录的权限。</summary>
    public async Task SetPermissionsAsync(Guid sessionId, string remotePath, short octalMode, CancellationToken cancellationToken = default)
    {
        if (octalMode < 0 || octalMode > 777 || octalMode % 10 > 7 || (octalMode / 10) % 10 > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(octalMode), octalMode, @"Mode must be three octal digits (000-777).");
        }
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await Task.Run(() => { client.ChangePermissions(remotePath, octalMode); }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>获取远端指定路径文件/目录的详细信息;路径不存在时抛出 <see cref="FileNotFoundException" />。</summary>
    public async Task<RemoteFileInfo> GetFileInfoAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        string parentDir = GetUnixParentDirectory(remotePath);
        string fileName = GetUnixFileName(remotePath);
        IEnumerable<SftpEntry> files = await client.ListDirectoryAsync(parentDir, cancellationToken).ConfigureAwait(false);
        SftpEntry? file = files.FirstOrDefault(f => f.Name == fileName) ?? throw new FileNotFoundException($"File not found: {remotePath}");
        RemoteIdentityMap identities = await _identities.GetAsync(sessionId).ConfigureAwait(false);
        return MapToRemoteFileInfo(file, identities);
    }

    /// <summary>判断远端路径是否存在。</summary>
    public async Task<bool> ExistsAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => client.Exists(remotePath), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>返回该会话 SFTP 客户端的当前工作目录。</summary>
    public async Task<string> GetWorkingDirectoryAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return client.WorkingDirectory;
    }

    /// <summary>关闭并清理指定会话缓存的 SFTP 客户端与单飞闸,断开连接并释放资源。</summary>
    public async Task CloseSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (_clientGates.TryRemove(sessionId, out SemaphoreSlim? gate))
        {
            gate.Dispose();
        }

        // 在下面的早退之前丢弃:即使本会话从未建过 SFTP 客户端,查表缓存也可能已存在。
        _identities.Invalidate(sessionId);
        if (!_sftpClients.TryRemove(sessionId, out ISftpClientWrapper? client))
        {
            return;
        }
        await Task.Run(() =>
        {
            try
            {
                if (client.IsConnected)
                {
                    client.Disconnect();
                }
                client.Dispose();
            }
            catch
            {
                // Best-effort teardown; the tab is already gone.
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>断开并释放所有缓存的 SFTP 客户端,尽力清理全部会话资源。</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (KeyValuePair<Guid, ISftpClientWrapper> kvp in _sftpClients)
        {
            try
            {
                if (kvp.Value.IsConnected)
                {
                    kvp.Value.Disconnect();
                }
                kvp.Value.Dispose();
            }
            catch
            {
                // Best-effort cleanup during disposal
            }
        }
        _sftpClients.Clear();
        await ValueTask.CompletedTask.ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>带宽限制(设置 → 文件传输):返回字节/秒,0 = 不限速。</summary>
    private async Task<(long UploadBps, long DownloadBps, bool PreserveTimestamps)> GetTransferTuningAsync()
    {
        if (settingsService is null)
        {
            return (0, 0, true);
        }
        try
        {
            TransferOptions t = (await settingsService.GetSettingsAsync().ConfigureAwait(false)).Transfer;
            long up = t.BandwidthLimitEnabled ? (long)Math.Max(0, t.UploadLimitMBps) * 1024 * 1024 : 0;
            long down = t.BandwidthLimitEnabled ? (long)Math.Max(0, t.DownloadLimitMBps) * 1024 * 1024 : 0;
            return (up, down, t.PreserveTimestamps);
        }
        catch
        {
            return (0, 0, true);
        }
    }

    /// <summary>
    /// Depth-first delete: a directory's children are removed before the directory itself,
    /// since SFTP <c>rmdir</c> only succeeds on empty directories. Reports one tick per removed entry.
    /// </summary>
    private static int CountEntries(ISftpClientWrapper client, string path, bool isDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!isDirectory)
        {
            return 1;
        }
        return 1 + client.ListDirectory(path).Where(child => child.Name is not ("." or "..")).Sum(child => CountEntries(client, child.FullName, child.IsDirectory, cancellationToken));
    }

    private static void DeleteEntry(ISftpClientWrapper client,
        string path,
        bool isDirectory,
        int total,
        ref int deleted,
        IProgress<SftpDeleteProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isDirectory)
        {
            foreach (SftpEntry child in client.ListDirectory(path))
            {
                if (child.Name is "." or "..")
                {
                    continue;
                }
                DeleteEntry(client, child.FullName, child.IsDirectory, total, ref deleted, progress, cancellationToken);
            }
            client.DeleteDirectory(path);
        }
        else
        {
            client.DeleteFile(path);
        }
        deleted++;
        progress?.Report(new(deleted, total, path));
    }

    private async Task<ISftpClientWrapper> GetOrCreateSftpClientAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (TryGetUsableClient(sessionId, out ISftpClientWrapper? existingClient))
        {
            return existingClient;
        }

        // 单飞:面板加载与文件操作可能并发首次触达同一会话,不加闸时会各自握手出
        // 一条 SFTP 连接(SSH 握手 × N),后到者还会把先到者从字典里顶掉造成泄漏。
        SemaphoreSlim gate = _clientGates.GetOrAdd(sessionId, static _ => new(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetUsableClient(sessionId, out existingClient))
            {
                return existingClient;
            }
            SshSession? session = _connectionService.GetSession(sessionId) ?? throw new InvalidOperationException($"Session {sessionId} not found");
            if (session.Status != SessionStatus.Connected)
            {
                throw new InvalidOperationException($"Session {sessionId} is not connected");
            }
            if (sftpClientFactory == null)
            {
                throw new InvalidOperationException("SFTP client factory not configured");
            }
            ISftpClientWrapper client = sftpClientFactory(session);
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            _sftpClients[sessionId] = client;
            return client;
        }
        finally
        {
            try
            {
                gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // CloseSessionAsync 与首次创建赛跑时会把闸释放掉;创建结果本身已无所谓。
            }
        }
    }

    private bool TryGetUsableClient(Guid sessionId, [NotNullWhen(true)] out ISftpClientWrapper? client)
    {
        if (_sftpClients.TryGetValue(sessionId, out client))
        {
            try
            {
                return client.IsConnected;
            }
            catch (ObjectDisposedException)
            {
                // 会话关闭把客户端释放了,当作缺失重建。
            }
        }
        client = null;
        return false;
    }

    private static RemoteFileInfo MapToRemoteFileInfo(SftpEntry file, RemoteIdentityMap identities)
    {
        return new()
        {
            Name = file.Name,
            FullPath = file.FullName,
            Size = file.Length,
            Permissions = FormatPermissions(file),
            IsDirectory = file.IsDirectory,
            LastModified = file.LastWriteTime,
            Owner = identities.UserName(file.UserId),
            Group = identities.GroupName(file.GroupId)
        };
    }

    private static void ReportProgress(IProgress<TransferProgress>? progress, string fileName, long bytesTransferred, long totalBytes, Stopwatch stopwatch)
    {
        if (progress == null)
        {
            return;
        }
        double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
        double speed = elapsedSeconds > 0 ? bytesTransferred / elapsedSeconds : 0;
        long remainingBytes = totalBytes - bytesTransferred;
        TimeSpan estimatedTimeRemaining = speed > 0
                                              ? TimeSpan.FromSeconds(remainingBytes / speed)
                                              : TimeSpan.Zero;
        var transferProgress = new TransferProgress
        {
            FileName = fileName,
            BytesTransferred = bytesTransferred,
            TotalBytes = totalBytes,
            Percentage = totalBytes > 0 ? (int)((bytesTransferred * 100) / totalBytes) : 0,
            SpeedBytesPerSecond = speed,
            EstimatedTimeRemaining = estimatedTimeRemaining
        };
        progress.Report(transferProgress);
    }

    /// <summary>
    /// Disposes a stream to abort an in-flight transfer on cancellation. Runs from a
    /// <see cref="CancellationToken" /> callback, so it must never throw — otherwise
    /// <see cref="CancellationTokenSource.Cancel()" /> would surface an aggregated exception to
    /// whoever pressed cancel.
    /// </summary>
    private static void SafeDispose(Stream stream)
    {
        try
        {
            stream.Dispose();
        }
        catch
        {
            // Best-effort: the transfer will still fail out and be reported as cancelled.
        }
    }

    private static string GetUnixParentDirectory(string remotePath)
    {
        int lastSlash = remotePath.LastIndexOf('/');
        return lastSlash > 0 ? remotePath[..lastSlash] : "/";
    }

    private static string GetUnixFileName(string remotePath)
    {
        int lastSlash = remotePath.LastIndexOf('/');
        return lastSlash >= 0 ? remotePath[(lastSlash + 1)..] : remotePath;
    }

    private static string FormatPermissions(SftpEntry file)
    {
        string perms = file.IsDirectory ? "d" : "-";
        perms += file.OwnerCanRead ? "r" : "-";
        perms += file.OwnerCanWrite ? "w" : "-";
        perms += file.OwnerCanExecute ? "x" : "-";
        perms += file.GroupCanRead ? "r" : "-";
        perms += file.GroupCanWrite ? "w" : "-";
        perms += file.GroupCanExecute ? "x" : "-";
        perms += file.OthersCanRead ? "r" : "-";
        perms += file.OthersCanWrite ? "w" : "-";
        perms += file.OthersCanExecute ? "x" : "-";
        return perms;
    }
}
