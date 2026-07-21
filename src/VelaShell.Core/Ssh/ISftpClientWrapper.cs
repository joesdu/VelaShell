namespace VelaShell.Core.Ssh;

/// <summary>
/// SFTP 客户端的库中立抽象(参见 <see cref="ISshClientWrapper" /> 的隔离说明):
/// 目录条目以 <see cref="SftpEntry" /> 返回,失败以 SshClientException 层级抛出,
/// 不暴露任何具体 SSH 库的类型。底层库(Tmds.Ssh)原生全异步,本接口成员一律异步,
/// 不再提供同步阻塞变体。
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
    /// 异步建立 SFTP 连接。
    /// </summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 断开 SFTP 连接。
    /// </summary>
    void Disconnect();

    /// <summary>
    /// 异步列出指定远端目录下的条目。
    /// </summary>
    Task<IEnumerable<SftpEntry>> ListDirectoryAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// 异步将输入流的内容上传到指定远端路径,并可通过回调报告上传进度。
    /// </summary>
    Task UploadAsync(Stream input, string path, Action<ulong>? uploadCallback = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 异步将指定远端路径的文件下载到输出流,并可通过回调报告下载进度。
    /// </summary>
    Task DownloadAsync(string path, Stream output, Action<ulong>? downloadCallback = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除指定远端文件;文件不存在时抛出 SftpOperationException 由调用方甄别,
    /// 或由上层先行 <see cref="ExistsAsync" /> 检查。
    /// </summary>
    Task DeleteFileAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除指定远端目录。
    /// </summary>
    Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// 创建指定远端目录。
    /// </summary>
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// 重命名或移动远端条目。
    /// </summary>
    Task RenameFileAsync(string oldPath, string newPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 使用 <c>posix-rename@openssh.com</c> 扩展重命名/移动远端条目。
    /// 部分服务器会拒绝普通的 SSH_FXP_RENAME(SSH_FX_BAD_MESSAGE),尤其跨目录
    /// 移动时;但会接受 POSIX 变体。
    /// </summary>
    Task PosixRenameFileAsync(string oldPath, string newPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 判断指定远端路径是否存在。
    /// </summary>
    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// 修改远端条目的权限。<paramref name="mode" /> 采用将三个八进制数字写作十进制数的约定
    /// (例如 755、644)。
    /// </summary>
    Task ChangePermissionsAsync(string path, short mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// 以读或写方式打开远端文件。需支持断点续传的上传时,
    /// 请结合 <see cref="FileMode.Append"/> 与 <see cref="FileAccess.Write"/> 使用。
    /// </summary>
    Task<Stream> OpenAsync(string path, FileMode mode, FileAccess access, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取远端文件的字节大小;若文件不存在则返回 -1。
    /// </summary>
    Task<long> GetFileSizeAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// 将流上传到远端路径,写入前先定位到 <paramref name="resumeOffset"/> 字节处。
    /// 用于断点续传上传。
    /// </summary>
    Task UploadAsync(Stream input, string path, long resumeOffset, Action<ulong>? uploadCallback = null, CancellationToken cancellationToken = default);
}
