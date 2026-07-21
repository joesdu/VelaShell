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
    /// 以读或写方式打开远端文件。
    /// <para>
    /// **实现必须返回可 Seek 的流**(<see cref="Stream.CanSeek" /> 为 <c>true</c>):
    /// 续传起点校验与断点续传下载都要按绝对偏移定位。部分 SSH 库默认打开的是不可定位的流,
    /// 需要显式开启相应选项。断点续传上传请改用带 <c>resumeOffset</c> 的
    /// <see cref="UploadAsync(Stream, string, long, Action{ulong}, CancellationToken)" /> 重载,
    /// 不要用 <see cref="FileMode.Append" /> 自行拼接。
    /// </para>
    /// </summary>
    Task<Stream> OpenAsync(string path, FileMode mode, FileAccess access, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取远端文件的字节大小;若文件不存在则返回 -1。
    /// </summary>
    Task<long> GetFileSizeAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// 直接 stat 单个远端条目,不存在时返回 <c>null</c>。
    /// <para>
    /// 用于替代"列举父目录再从中挑一条"的做法 —— 后者在父目录条目很多时代价极高
    /// (批量传输会退化成每个文件一次全目录列举)。跟随符号链接,与
    /// <see cref="ListDirectoryAsync" /> 的默认枚举语义保持一致。
    /// </para>
    /// </summary>
    Task<SftpEntry?> GetEntryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// 将流上传到远端路径,写入前先定位到 <paramref name="resumeOffset"/> 字节处。
    /// 用于断点续传上传。
    /// </summary>
    Task UploadAsync(Stream input, string path, long resumeOffset, Action<ulong>? uploadCallback = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 断点续传前必须从"当前文件长度"回退的字节数。
    /// <para>
    /// 底层库为了吞吐会同时投递多个写缓冲区,它们的完成顺序不保证与偏移顺序一致。
    /// 传输中途断开时,文件长度只代表"已确认的最高偏移",并不代表它之前的每个字节都写进去了 ——
    /// 中间可能留有读作 0 的空洞。因此不能直接从文件长度处续传:必须回退一整个在途写入窗口,
    /// 使续传起点之前的数据可信。
    /// </para>
    /// <para>返回 0 表示该实现保证写入是连续的,无需回退。</para>
    /// </summary>
    long ResumeSafetyMargin { get; }
}
