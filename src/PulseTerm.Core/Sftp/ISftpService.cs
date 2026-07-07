using PulseTerm.Core.Models;

namespace PulseTerm.Core.Sftp;

public interface ISftpService : IAsyncDisposable
{
    Task<List<RemoteFileInfo>> ListDirectoryAsync(Guid sessionId, string path, CancellationToken cancellationToken = default);
    Task UploadFileAsync(Guid sessionId, string localPath, string remotePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
    Task DownloadFileAsync(Guid sessionId, string remotePath, string localPath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default);
    Task CreateDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default);

    /// <summary>Creates an empty file at the given remote path.</summary>
    Task CreateFileAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default);

    /// <summary>Renames or moves a remote entry (SFTP rename doubles as move).</summary>
    Task RenameAsync(Guid sessionId, string oldPath, string newPath, CancellationToken cancellationToken = default);
    Task<RemoteFileInfo> GetFileInfoAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default);

    /// <summary>The session's SFTP working directory (the account's home directory right after
    /// login), used to open the browser there instead of the filesystem root.</summary>
    Task<string> GetWorkingDirectoryAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>Closes and disposes the SFTP channel for a session (called when its SSH tab is
    /// closed) so it no longer holds a live connection or accepts operations. No-op if the session
    /// has no SFTP channel open.</summary>
    Task CloseSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
