using VelaShell.Core.Models;

namespace VelaShell.Core.Sftp;

/// <summary>
/// 为单个文档所属的会话串行化 SFTP 操作,而不改变终端侧边栏所用共享 SFTP 服务的行为。
/// </summary>
public sealed class SerializedSftpService : ISftpService
{
    private readonly ISftpService _inner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lifecycleSync = new();
    private Task? _closeTask;
    private bool _closing;

    /// <summary>创建绑定到唯一一个文档所属 SFTP 会话的串行器。</summary>
    public SerializedSftpService(ISftpService inner, Guid sessionId)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        SessionId = sessionId;
    }

    /// <summary>该文档作用域服务唯一接受的会话 ID。</summary>
    public Guid SessionId { get; }

    /// <summary>列举远端目录的串行化透传。</summary>
    public Task<List<RemoteFileInfo>> ListDirectoryAsync(Guid sessionId, string path, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.ListDirectoryAsync(sessionId, path, token), cancellationToken);

    /// <summary>向远端主机上传文件的串行化透传。</summary>
    public Task UploadFileAsync(Guid sessionId, string localPath, string remotePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default, long resumeOffset = 0) => ExecuteAsync(sessionId, token => _inner.UploadFileAsync(sessionId, localPath, remotePath, progress, token, resumeOffset), cancellationToken);

    /// <summary>从远端主机下载文件的串行化透传。</summary>
    public Task DownloadFileAsync(Guid sessionId, string remotePath, string localPath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default, long resumeOffset = 0) => ExecuteAsync(sessionId, token => _inner.DownloadFileAsync(sessionId, remotePath, localPath, progress, token, resumeOffset), cancellationToken);

    /// <summary>删除远端文件或目录的串行化透传。</summary>
    public Task DeleteAsync(Guid sessionId, string remotePath, IProgress<SftpDeleteProgress>? progress = null, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.DeleteAsync(sessionId, remotePath, progress, token), cancellationToken);

    /// <summary>创建远端目录的串行化透传。</summary>
    public Task CreateDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.CreateDirectoryAsync(sessionId, remotePath, token), cancellationToken);

    /// <summary>创建远端文件的串行化透传。</summary>
    public Task CreateFileAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.CreateFileAsync(sessionId, remotePath, token), cancellationToken);

    /// <summary>确保远端目录存在的串行化透传。</summary>
    public Task EnsureDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.EnsureDirectoryAsync(sessionId, remotePath, token), cancellationToken);

    /// <summary>重命名远端条目的串行化透传。</summary>
    public Task RenameAsync(Guid sessionId, string oldPath, string newPath, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.RenameAsync(sessionId, oldPath, newPath, token), cancellationToken);

    /// <summary>复制远端条目的串行化透传。</summary>
    public Task CopyAsync(Guid sessionId, string sourcePath, string destPath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.CopyAsync(sessionId, sourcePath, destPath, progress, token), cancellationToken);

    /// <summary>设置远端文件权限的串行化透传。</summary>
    public Task SetPermissionsAsync(Guid sessionId, string remotePath, short octalMode, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.SetPermissionsAsync(sessionId, remotePath, octalMode, token), cancellationToken);

    /// <summary>获取远端文件元数据的串行化透传。</summary>
    public Task<RemoteFileInfo> GetFileInfoAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.GetFileInfoAsync(sessionId, remotePath, token), cancellationToken);

    /// <summary>检查远端路径是否存在的串行化透传。</summary>
    public Task<bool> ExistsAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.ExistsAsync(sessionId, remotePath, token), cancellationToken);

    /// <summary>获取远端工作目录的串行化透传。</summary>
    public Task<string> GetWorkingDirectoryAsync(Guid sessionId, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.GetWorkingDirectoryAsync(sessionId, token), cancellationToken);

    /// <summary>关闭所绑定 SFTP 会话的串行化透传。</summary>
    public Task CloseSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        ValidateSession(sessionId);
        return CloseAsync(cancellationToken);
    }

    /// <summary>拒绝新任务,排空当前操作,并关闭所绑定的会话一次。</summary>
    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleSync)
        {
            _closing = true;
            _closeTask ??= CloseCoreAsync();
            return _closeTask.WaitAsync(cancellationToken);
        }
    }

    /// <summary>关闭会话并释放内部信号量。</summary>
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
