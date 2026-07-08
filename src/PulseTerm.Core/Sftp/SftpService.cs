using System.Collections.Concurrent;
using System.Diagnostics;
using PulseTerm.Core.Models;
using PulseTerm.Core.Ssh;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace PulseTerm.Core.Sftp;

public class SftpService : ISftpService
{
    private readonly ISshConnectionService _connectionService;
    private readonly ConcurrentDictionary<Guid, ISftpClientWrapper> _sftpClients = new();
    private readonly Func<SshSession, ISftpClientWrapper>? _sftpClientFactory;

    public SftpService(ISshConnectionService connectionService, Func<SshSession, ISftpClientWrapper>? sftpClientFactory = null)
    {
        _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
        _sftpClientFactory = sftpClientFactory;
    }

    public async Task<List<RemoteFileInfo>> ListDirectoryAsync(Guid sessionId, string path, CancellationToken cancellationToken = default)
    {
        var client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var files = await client.ListDirectoryAsync(path, cancellationToken).ConfigureAwait(false);

        return files
            .Where(f => f.Name != "." && f.Name != "..")
            .Select(MapToRemoteFileInfo)
            .ToList();
    }

    public async Task UploadFileAsync(Guid sessionId, string localPath, string remotePath,
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var fileInfo = new FileInfo(localPath);
        var totalBytes = fileInfo.Length;
        var fileName = Path.GetFileName(localPath);

        var stopwatch = Stopwatch.StartNew();

        await using var fileStream = File.OpenRead(localPath);

        // NOTE: SSH.NET invokes this progress callback on a detached thread-pool thread
        // (ThreadPool.QueueUserWorkItem), so throwing from it would be an unhandled exception that
        // crashes the process. To cancel the in-flight file we instead dispose our own stream, which
        // makes the worker's read fail; we normalise that into a clean cancellation below.
        using (cancellationToken.Register(() => SafeDispose(fileStream)))
        {
            try
            {
                await client.UploadAsync(fileStream, remotePath, bytesTransferred =>
                {
                    ReportProgress(progress, fileName, (long)bytesTransferred, totalBytes, stopwatch);
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is not OperationCanceledException)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
    }

    public async Task DownloadFileAsync(Guid sessionId, string remotePath, string localPath,
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var fileName = GetUnixFileName(remotePath);

        var fileInfo = await GetFileInfoAsync(sessionId, remotePath, cancellationToken).ConfigureAwait(false);
        var totalBytes = fileInfo.Size;

        var stopwatch = Stopwatch.StartNew();

        await using var fileStream = File.Create(localPath);

        // See UploadFileAsync: the callback runs on a detached thread-pool thread, so we cancel by
        // disposing our own stream (failing the worker's write) rather than throwing from it.
        using (cancellationToken.Register(() => SafeDispose(fileStream)))
        {
            try
            {
                await client.DownloadAsync(remotePath, fileStream, bytesTransferred =>
                {
                    ReportProgress(progress, fileName, (long)bytesTransferred, totalBytes, stopwatch);
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is not OperationCanceledException)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
    }

    public async Task DeleteAsync(Guid sessionId, string remotePath, IProgress<SftpDeleteProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await Task.Run(() =>
        {
            if (!client.Exists(remotePath))
            {
                throw new FileNotFoundException($"Remote path not found: {remotePath}");
            }

            var parentDir = GetUnixParentDirectory(remotePath);
            var name = GetUnixFileName(remotePath);
            var entry = client.ListDirectory(parentDir).FirstOrDefault(f => f.Name == name);
            bool isDirectory = entry != null && entry.IsDirectory;
            int total = CountEntries(client, remotePath, isDirectory, cancellationToken);

            // Emit a "0 / total" tick so the UI can immediately switch to determinate progress.
            progress?.Report(new SftpDeleteProgress(0, total, remotePath));

            int deleted = 0;
            DeleteEntry(client, remotePath, isDirectory, total, ref deleted, progress, cancellationToken);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Depth-first delete: a directory's children are removed before the directory itself,
    /// since SFTP <c>rmdir</c> only succeeds on empty directories. Reports one tick per removed entry.</summary>
    private static int CountEntries(ISftpClientWrapper client, string path, bool isDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!isDirectory)
            return 1;

        var total = 1; // include the directory itself
        foreach (var child in client.ListDirectory(path))
        {
            if (child.Name == "." || child.Name == "..")
                continue;

            total += CountEntries(client, child.FullName, child.IsDirectory, cancellationToken);
        }

        return total;
    }

    private static void DeleteEntry(ISftpClientWrapper client, string path, bool isDirectory,
        int total, ref int deleted, IProgress<SftpDeleteProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (isDirectory)
        {
            foreach (var child in client.ListDirectory(path))
            {
                if (child.Name == "." || child.Name == "..")
                    continue;
                DeleteEntry(client, child.FullName, child.IsDirectory, total, ref deleted, progress, cancellationToken);
            }

            client.DeleteDirectory(path);
        }
        else
        {
            client.DeleteFile(path);
        }

        deleted++;
        progress?.Report(new SftpDeleteProgress(deleted, total, path));
    }

    public async Task CreateDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        var client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await Task.Run(() =>
        {
            client.CreateDirectory(remotePath);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateFileAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        var client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        using var empty = new MemoryStream();
        await client.UploadAsync(empty, remotePath, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task EnsureDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        var client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await Task.Run(() =>
        {
            if (!client.Exists(remotePath))
                client.CreateDirectory(remotePath);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task RenameAsync(Guid sessionId, string oldPath, string newPath, CancellationToken cancellationToken = default)
    {
        var client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await Task.Run(() =>
        {
            try
            {
                client.RenameFile(oldPath, newPath);
            }
            catch (Exception ex) when (ex is SftpException or NotSupportedException)
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
        if (octalMode < 0 || octalMode > 777 || octalMode % 10 > 7 || octalMode / 10 % 10 > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(octalMode), octalMode, "Mode must be three octal digits (000-777).");
        }

        var client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await Task.Run(() =>
        {
            client.ChangePermissions(remotePath, octalMode);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteFileInfo> GetFileInfoAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        var client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var parentDir = GetUnixParentDirectory(remotePath);
        var fileName = GetUnixFileName(remotePath);

        var files = await client.ListDirectoryAsync(parentDir, cancellationToken).ConfigureAwait(false);
        var file = files.FirstOrDefault(f => f.Name == fileName);

        if (file == null)
        {
            throw new FileNotFoundException($"File not found: {remotePath}");
        }

        return MapToRemoteFileInfo(file);
    }

    public async Task<string> GetWorkingDirectoryAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return client.WorkingDirectory;
    }

    public async Task CloseSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sftpClients.TryRemove(sessionId, out var client))
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
        foreach (var kvp in _sftpClients)
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
    }

    private async Task<ISftpClientWrapper> GetOrCreateSftpClientAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (_sftpClients.TryGetValue(sessionId, out var existingClient))
        {
            if (existingClient.IsConnected)
            {
                return existingClient;
            }
        }

        var session = _connectionService.GetSession(sessionId);
        if (session == null)
        {
            throw new InvalidOperationException($"Session {sessionId} not found");
        }

        if (session.Status != SessionStatus.Connected)
        {
            throw new InvalidOperationException($"Session {sessionId} is not connected");
        }

        if (_sftpClientFactory == null)
        {
            throw new InvalidOperationException("SFTP client factory not configured");
        }

        var client = _sftpClientFactory(session);
        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);

        _sftpClients[sessionId] = client;
        return client;
    }

    private static RemoteFileInfo MapToRemoteFileInfo(ISftpFile file)
    {
        return new RemoteFileInfo
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
        if (progress == null) return;

        var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
        var speed = elapsedSeconds > 0 ? bytesTransferred / elapsedSeconds : 0;
        var remainingBytes = totalBytes - bytesTransferred;
        var estimatedTimeRemaining = speed > 0
            ? TimeSpan.FromSeconds(remainingBytes / speed)
            : TimeSpan.Zero;

        var transferProgress = new TransferProgress
        {
            FileName = fileName,
            BytesTransferred = bytesTransferred,
            TotalBytes = totalBytes,
            Percentage = totalBytes > 0 ? (int)(bytesTransferred * 100 / totalBytes) : 0,
            SpeedBytesPerSecond = speed,
            EstimatedTimeRemaining = estimatedTimeRemaining
        };

        progress.Report(transferProgress);
    }

    /// <summary>Disposes a stream to abort an in-flight transfer on cancellation. Runs from a
    /// <see cref="CancellationToken"/> callback, so it must never throw — otherwise
    /// <see cref="CancellationTokenSource.Cancel()"/> would surface an aggregated exception to
    /// whoever pressed cancel.</summary>
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
        var lastSlash = remotePath.LastIndexOf('/');
        return lastSlash > 0 ? remotePath[..lastSlash] : "/";
    }

    private static string GetUnixFileName(string remotePath)
    {
        var lastSlash = remotePath.LastIndexOf('/');
        return lastSlash >= 0 ? remotePath[(lastSlash + 1)..] : remotePath;
    }

    private static string FormatPermissions(ISftpFile file)
    {
        var perms = file.IsDirectory ? "d" : "-";

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
