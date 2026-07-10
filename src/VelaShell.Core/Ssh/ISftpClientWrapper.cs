using Renci.SshNet.Sftp;

namespace VelaShell.Core.Ssh;

public interface ISftpClientWrapper : IDisposable
{
    bool IsConnected { get; }

    TimeSpan ConnectionTimeout { get; set; }

    string WorkingDirectory { get; }

    void Connect();
    Task ConnectAsync(CancellationToken cancellationToken);
    void Disconnect();

    IEnumerable<ISftpFile> ListDirectory(string path);
    Task<IEnumerable<ISftpFile>> ListDirectoryAsync(string path, CancellationToken cancellationToken);

    void UploadFile(Stream input, string path, bool canOverride = true);
    Task UploadAsync(Stream input, string path, Action<ulong>? uploadCallback = null, CancellationToken cancellationToken = default);

    void DownloadFile(string path, Stream output);
    Task DownloadAsync(string path, Stream output, Action<ulong>? downloadCallback = null, CancellationToken cancellationToken = default);

    void DeleteFile(string path);
    void DeleteDirectory(string path);
    void CreateDirectory(string path);
    void RenameFile(string oldPath, string newPath);

    /// <summary>
    /// Renames/moves a remote entry using the <c>posix-rename@openssh.com</c> extension.
    /// Some servers reject the plain SSH_FXP_RENAME (SSH_FX_BAD_MESSAGE) — notably for cross-directory
    /// moves — but accept the POSIX variant.
    /// </summary>
    void PosixRenameFile(string oldPath, string newPath);

    bool Exists(string path);

    /// <summary>
    /// Changes a remote entry's permissions. <paramref name="mode" /> uses SSH.NET's
    /// convention: three octal digits written as a decimal number (e.g. 755, 644).
    /// </summary>
    void ChangePermissions(string path, short mode);
}
