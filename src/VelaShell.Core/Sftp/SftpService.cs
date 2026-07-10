using System.Collections.Concurrent;
using System.Diagnostics;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;

namespace VelaShell.Core.Sftp;

public class SftpService(
    ISshConnectionService connectionService,
    Func<SshSession, ISftpClientWrapper>? sftpClientFactory = null,
    ISettingsService? settingsService = null) : ISftpService
{
    private readonly ISshConnectionService _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
    private readonly ConcurrentDictionary<Guid, ISftpClientWrapper> _sftpClients = new();

    public async Task<List<RemoteFileInfo>> ListDirectoryAsync(Guid sessionId, string path, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        IEnumerable<SftpEntry> files = await client.ListDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
        return [.. files.Where(f => f.Name is not "." and not "..").Select(MapToRemoteFileInfo)];
    }

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

    public async Task CreateDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await Task.Run(() => { client.CreateDirectory(remotePath); }, cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateFileAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        using var empty = new MemoryStream();
        await client.UploadAsync(empty, remotePath, null, cancellationToken).ConfigureAwait(false);
    }

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

    public async Task SetPermissionsAsync(Guid sessionId, string remotePath, short octalMode, CancellationToken cancellationToken = default)
    {
        if (octalMode < 0 || octalMode > 777 || octalMode % 10 > 7 || (octalMode / 10) % 10 > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(octalMode), octalMode, @"Mode must be three octal digits (000-777).");
        }
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await Task.Run(() => { client.ChangePermissions(remotePath, octalMode); }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteFileInfo> GetFileInfoAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        string parentDir = GetUnixParentDirectory(remotePath);
        string fileName = GetUnixFileName(remotePath);
        IEnumerable<SftpEntry> files = await client.ListDirectoryAsync(parentDir, cancellationToken).ConfigureAwait(false);
        SftpEntry? file = files.FirstOrDefault(f => f.Name == fileName);
        if (file == null)
        {
            throw new FileNotFoundException($"File not found: {remotePath}");
        }
        return MapToRemoteFileInfo(file);
    }

    public async Task<bool> ExistsAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => client.Exists(remotePath), cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> GetWorkingDirectoryAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return client.WorkingDirectory;
    }

    public async Task CloseSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
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
        if (_sftpClients.TryGetValue(sessionId, out ISftpClientWrapper? existingClient))
        {
            if (existingClient.IsConnected)
            {
                return existingClient;
            }
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

    private static RemoteFileInfo MapToRemoteFileInfo(SftpEntry file)
    {
        return new()
        {
            Name = file.Name,
            FullPath = file.FullName,
            Size = file.Length,
            Permissions = FormatPermissions(file),
            IsDirectory = file.IsDirectory,
            LastModified = file.LastWriteTime,
            Owner = file.UserId.ToString(),
            Group = file.GroupId.ToString()
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
    private static void SafeDispose(IDisposable stream)
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
