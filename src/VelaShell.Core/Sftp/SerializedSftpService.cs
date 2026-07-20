using VelaShell.Core.Models;

namespace VelaShell.Core.Sftp;

/// <summary>
/// Serializes SFTP operations for one document-owned session without changing the behavior of the
/// shared SFTP service used by terminal side panels.
/// </summary>
public sealed class SerializedSftpService : ISftpService
{
    private readonly ISftpService _inner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lifecycleSync = new();
    private Task? _closeTask;
    private bool _closing;

    /// <summary>Creates a serializer bound to exactly one document-owned SFTP session.</summary>
    public SerializedSftpService(ISftpService inner, Guid sessionId)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        SessionId = sessionId;
    }

    /// <summary>The only session ID accepted by this document-scoped service.</summary>
    public Guid SessionId { get; }

    /// <summary>Serialized passthrough for listing a remote directory.</summary>
    public Task<List<RemoteFileInfo>> ListDirectoryAsync(Guid sessionId, string path, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.ListDirectoryAsync(sessionId, path, token), cancellationToken);

    /// <summary>Serialized passthrough for uploading a file to the remote host.</summary>
    public Task UploadFileAsync(Guid sessionId, string localPath, string remotePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default, long resumeOffset = 0) => ExecuteAsync(sessionId, token => _inner.UploadFileAsync(sessionId, localPath, remotePath, progress, token, resumeOffset), cancellationToken);

    /// <summary>Serialized passthrough for downloading a file from the remote host.</summary>
    public Task DownloadFileAsync(Guid sessionId, string remotePath, string localPath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default, long resumeOffset = 0) => ExecuteAsync(sessionId, token => _inner.DownloadFileAsync(sessionId, remotePath, localPath, progress, token, resumeOffset), cancellationToken);

    /// <summary>Serialized passthrough for deleting a remote file or directory.</summary>
    public Task DeleteAsync(Guid sessionId, string remotePath, IProgress<SftpDeleteProgress>? progress = null, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.DeleteAsync(sessionId, remotePath, progress, token), cancellationToken);

    /// <summary>Serialized passthrough for creating a remote directory.</summary>
    public Task CreateDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.CreateDirectoryAsync(sessionId, remotePath, token), cancellationToken);

    /// <summary>Serialized passthrough for creating a remote file.</summary>
    public Task CreateFileAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.CreateFileAsync(sessionId, remotePath, token), cancellationToken);

    /// <summary>Serialized passthrough for ensuring a remote directory exists.</summary>
    public Task EnsureDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.EnsureDirectoryAsync(sessionId, remotePath, token), cancellationToken);

    /// <summary>Serialized passthrough for renaming a remote entry.</summary>
    public Task RenameAsync(Guid sessionId, string oldPath, string newPath, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.RenameAsync(sessionId, oldPath, newPath, token), cancellationToken);

    /// <summary>Serialized passthrough for copying a remote entry.</summary>
    public Task CopyAsync(Guid sessionId, string sourcePath, string destPath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.CopyAsync(sessionId, sourcePath, destPath, progress, token), cancellationToken);

    /// <summary>Serialized passthrough for setting remote file permissions.</summary>
    public Task SetPermissionsAsync(Guid sessionId, string remotePath, short octalMode, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.SetPermissionsAsync(sessionId, remotePath, octalMode, token), cancellationToken);

    /// <summary>Serialized passthrough for retrieving remote file metadata.</summary>
    public Task<RemoteFileInfo> GetFileInfoAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.GetFileInfoAsync(sessionId, remotePath, token), cancellationToken);

    /// <summary>Serialized passthrough for checking if a remote path exists.</summary>
    public Task<bool> ExistsAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.ExistsAsync(sessionId, remotePath, token), cancellationToken);

    /// <summary>Serialized passthrough for getting the remote working directory.</summary>
    public Task<string> GetWorkingDirectoryAsync(Guid sessionId, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.GetWorkingDirectoryAsync(sessionId, token), cancellationToken);

    /// <summary>Serialized passthrough for closing the bound SFTP session.</summary>
    public Task CloseSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        ValidateSession(sessionId);
        return CloseAsync(cancellationToken);
    }

    /// <summary>Rejects new work, drains the current operation, and closes the bound session once.</summary>
    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleSync)
        {
            _closing = true;
            _closeTask ??= CloseCoreAsync();
            return _closeTask.WaitAsync(cancellationToken);
        }
    }

    /// <summary>Closes the session and disposes the internal semaphore.</summary>
    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private async Task<T> ExecuteAsync<T>(Guid sessionId, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        ValidateSession(sessionId);
        ThrowIfClosing();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfClosing();
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ExecuteAsync(Guid sessionId, Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        ValidateSession(sessionId);
        ThrowIfClosing();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfClosing();
            await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CloseCoreAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _inner.CloseSessionAsync(SessionId, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ValidateSession(Guid sessionId)
    {
        if (sessionId != SessionId)
        {
            throw new ArgumentException("The SFTP document service is bound to a different session.", nameof(sessionId));
        }
    }

    private void ThrowIfClosing()
    {
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
        }
    }
}
