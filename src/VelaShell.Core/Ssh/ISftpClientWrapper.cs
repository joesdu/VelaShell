namespace VelaShell.Core.Ssh;

/// <summary>
/// SFTP 客户端的库中立抽象(参见 <see cref="ISshClientWrapper" /> 的隔离说明):
/// 目录条目以 <see cref="SftpEntry" /> 返回,失败以 SshClientException 层级抛出,
/// 不暴露任何具体 SSH 库的类型。
/// </summary>
public interface ISftpClientWrapper : IDisposable
{
    /// <summary>
    /// 指示 SFTP 会话当前是否已连接。
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// 建立连接时使用的超时时长。
    /// </summary>
    TimeSpan ConnectionTimeout { get; set; }

    /// <summary>
    /// 当前远端工作目录。
    /// </summary>
    string WorkingDirectory { get; }

    /// <summary>
    /// 同步建立 SFTP 连接。
    /// </summary>
    void Connect();

    /// <summary>
    /// 异步建立 SFTP 连接。
    /// </summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 断开 SFTP 连接。
    /// </summary>
    void Disconnect();

    /// <summary>
    /// 列出指定远端目录下的条目。
    /// </summary>
    IEnumerable<SftpEntry> ListDirectory(string path);

    /// <summary>
    /// 异步列出指定远端目录下的条目。
    /// </summary>
    Task<IEnumerable<SftpEntry>> ListDirectoryAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// 将输入流的内容上传到指定远端路径。
    /// </summary>
    void UploadFile(Stream input, string path, bool canOverride = true);

    /// <summary>
    /// 异步将输入流的内容上传到指定远端路径,并可通过回调报告上传进度。
    /// </summary>
    Task UploadAsync(Stream input, string path, Action<ulong>? uploadCallback = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 将指定远端路径的文件下载到输出流。
    /// </summary>
    void DownloadFile(string path, Stream output);

    /// <summary>
    /// 异步将指定远端路径的文件下载到输出流,并可通过回调报告下载进度。
    /// </summary>
    Task DownloadAsync(string path, Stream output, Action<ulong>? downloadCallback = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除指定远端文件。
    /// </summary>
    void DeleteFile(string path);

    /// <summary>
    /// 删除指定远端目录。
    /// </summary>
    void DeleteDirectory(string path);

    /// <summary>
    /// 创建指定远端目录。
    /// </summary>
    void CreateDirectory(string path);

    /// <summary>
    /// 重命名或移动远端条目。
    /// </summary>
    void RenameFile(string oldPath, string newPath);

    /// <summary>
    /// Renames/moves a remote entry using the <c>posix-rename@openssh.com</c> extension.
    /// Some servers reject the plain SSH_FXP_RENAME (SSH_FX_BAD_MESSAGE) — notably for cross-directory
    /// moves — but accept the POSIX variant.
    /// </summary>
    void PosixRenameFile(string oldPath, string newPath);

    /// <summary>
    /// 判断指定远端路径是否存在。
    /// </summary>
    bool Exists(string path);

    /// <summary>
    /// Changes a remote entry's permissions. <paramref name="mode" /> uses the convention of
    /// three octal digits written as a decimal number (e.g. 755, 644).
    /// </summary>
    void ChangePermissions(string path, short mode);

    /// <summary>
    /// Opens a remote file for read or write. Use <see cref="FileMode.Append"/> with
    /// <see cref="FileAccess.Write"/> for resume-capable uploads.
    /// </summary>
    Stream Open(string path, FileMode mode, FileAccess access);

    /// <summary>
    /// Gets the size in bytes of a remote file, or -1 if it does not exist.
    /// </summary>
    long GetFileSize(string path);

    /// <summary>
    /// Uploads a stream to a remote path, seeking to <paramref name="resumeOffset"/> bytes
    /// before writing. Used for breakpoint-resume uploads.
    /// </summary>
    Task UploadAsync(Stream input, string path, long resumeOffset, Action<ulong>? uploadCallback = null, CancellationToken cancellationToken = default);
}
