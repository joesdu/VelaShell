using VelaShell.Core.Models;

namespace VelaShell.Core.Sftp;

/// <summary>Progress of a (possibly recursive) delete: how many entries are deleted so far, the
/// total entries expected to be deleted, and the path most recently deleted.</summary>
public readonly record struct SftpDeleteProgress(int DeletedCount, int TotalCount, string CurrentPath)
{
    /// <summary>Delete progress percentage in [0, 100].</summary>
    public int Percentage => TotalCount <= 0 ? 0 : (int)Math.Clamp((double)DeletedCount * 100 / TotalCount, 0, 100);
}

public interface ISftpService : IAsyncDisposable
{
    Task<List<RemoteFileInfo>> ListDirectoryAsync(Guid sessionId, string path, CancellationToken cancellationToken = default);
    Task UploadFileAsync(Guid sessionId, string localPath, string remotePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
    Task DownloadFileAsync(Guid sessionId, string remotePath, string localPath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Deletes a file, or a directory and its entire contents (recursively). Reports one
    /// progress tick per removed entry so the UI can show progress for large/slow folder deletes.</summary>
    Task DeleteAsync(Guid sessionId, string remotePath, IProgress<SftpDeleteProgress>? progress = null, CancellationToken cancellationToken = default);
    Task CreateDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default);

    /// <summary>Creates an empty file at the given remote path.</summary>
    Task CreateFileAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default);

    /// <summary>Creates a directory if it does not already exist (idempotent). Used when uploading
    /// a folder tree so re-creating an existing subdirectory is not an error.</summary>
    Task EnsureDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default);

    /// <summary>Renames or moves a remote entry (SFTP rename doubles as move).</summary>
    Task RenameAsync(Guid sessionId, string oldPath, string newPath, CancellationToken cancellationToken = default);

    /// <summary>Changes a remote entry's permissions (chmod). <paramref name="octalMode"/> is
    /// three octal digits written as a decimal number (e.g. 755, 644), matching `chmod` notation.</summary>
    Task SetPermissionsAsync(Guid sessionId, string remotePath, short octalMode, CancellationToken cancellationToken = default);
    Task<RemoteFileInfo> GetFileInfoAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default);

    /// <summary>Whether a remote path exists (file or directory). Used for upload conflict
    /// detection before overwriting a remote file.</summary>
    Task<bool> ExistsAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default);

    /// <summary>The session's SFTP working directory (the account's home directory right after
    /// login), used to open the browser there instead of the filesystem root.</summary>
    Task<string> GetWorkingDirectoryAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>Closes and disposes the SFTP channel for a session (called when its SSH tab is
    /// closed) so it no longer holds a live connection or accepts operations. No-op if the session
    /// has no SFTP channel open.</summary>
    Task CloseSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
