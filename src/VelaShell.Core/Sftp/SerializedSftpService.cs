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
    private readonly Guid _sessionId;
    private Task? _closeTask;
    private bool _closing;

    /// <summary>Creates a serializer bound to exactly one document-owned SFTP session.</summary>
    public SerializedSftpService(ISftpService inner, Guid sessionId)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _sessionId = sessionId;
    }

    /// <summary>The only session ID accepted by this document-scoped service.</summary>
    public Guid SessionId => _sessionId;

    public Task<List<RemoteFileInfo>> ListDirectoryAsync(Guid sessionId, string path, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.ListDirectoryAsync(sessionId, path, token), cancellationToken);

    public Task UploadFileAsync(Guid sessionId, string localPath, string remotePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.UploadFileAsync(sessionId, localPath, remotePath, progress, token), cancellationToken);

    public Task DownloadFileAsync(Guid sessionId, string remotePath, string localPath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.DownloadFileAsync(sessionId, remotePath, localPath, progress, token), cancellationToken);

    public Task DeleteAsync(Guid sessionId, string remotePath, IProgress<SftpDeleteProgress>? progress = null, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.DeleteAsync(sessionId, remotePath, progress, token), cancellationToken);

    public Task CreateDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.CreateDirectoryAsync(sessionId, remotePath, token), cancellationToken);

    public Task CreateFileAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.CreateFileAsync(sessionId, remotePath, token), cancellationToken);

    public Task EnsureDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.EnsureDirectoryAsync(sessionId, remotePath, token), cancellationToken);

    public Task RenameAsync(Guid sessionId, string oldPath, string newPath, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.RenameAsync(sessionId, oldPath, newPath, token), cancellationToken);

    public Task SetPermissionsAsync(Guid sessionId, string remotePath, short octalMode, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.SetPermissionsAsync(sessionId, remotePath, octalMode, token), cancellationToken);

    public Task<RemoteFileInfo> GetFileInfoAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.GetFileInfoAsync(sessionId, remotePath, token), cancellationToken);

    public Task<bool> ExistsAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.ExistsAsync(sessionId, remotePath, token), cancellationToken);

    public Task<string> GetWorkingDirectoryAsync(Guid sessionId, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.GetWorkingDirectoryAsync(sessionId, token), cancellationToken);

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
            await _inner.CloseSessionAsync(_sessionId, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ValidateSession(Guid sessionId)
    {
        if (sessionId != _sessionId)
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
