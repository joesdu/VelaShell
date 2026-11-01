using VelaShell.Core.Models;

namespace VelaShell.Core.Sftp;

/// <summary>
/// 删除(可能递归)的进度:已删除的条目数、预计删除的总条目数,以及最近一次被删除的路径。
/// </summary>
/// <param name="DeletedCount">已删除的条目数。</param>
/// <param name="TotalCount">预计删除的总条目数。</param>
/// <param name="CurrentPath">最近一次被删除的路径。</param>
// ReSharper disable once NotAccessedPositionalProperty.Global
public readonly record struct SftpDeleteProgress(int DeletedCount, int TotalCount, string CurrentPath)
{
    /// <summary>删除进度百分比,取值范围 [0, 100]。</summary>
    public int Percentage => TotalCount <= 0 ? 0 : (int)Math.Clamp(((double)DeletedCount * 100) / TotalCount, 0, 100);
}

/// <summary>
/// 改属主/属组时可选的用户名与组名(远端 passwd/group 库里查到的),各按序数排好。
/// 列表为空不代表改不了 —— 查不到表(纯 SFTP 账号没有 exec 通道)时照样可以直接填数字 id。
/// </summary>
/// <param name="Users">可选的用户名。</param>
/// <param name="Groups">可选的组名。</param>
public sealed record RemoteOwnerChoices(IReadOnlyList<string> Users, IReadOnlyList<string> Groups);

/// <summary>远端某个目录所在文件系统的用量(SFTP 的 <c>statvfs@openssh.com</c>)。</summary>
/// <param name="TotalBytes">总容量。</param>
/// <param name="AvailableBytes">登录用户还能写多少(上传前预检看这个,不含只留给 root 的那部分)。</param>
/// <param name="IsReadOnly">挂载成只读的。</param>
public sealed record RemoteSpaceInfo(ulong TotalBytes, ulong AvailableBytes, bool IsReadOnly);

/// <summary>
/// 基于已有 SSH 会话的 SFTP 文件操作:目录列举、上传/下载、删除、创建、重命名、权限与元数据查询,以会话 id 为键。
/// </summary>
public interface ISftpService : IAsyncDisposable
{
    /// <summary>列举远端目录的条目。</summary>
    Task<List<RemoteFileInfo>> ListDirectoryAsync(Guid sessionId, string path, CancellationToken cancellationToken = default);

    /// <summary>将本地文件上传到给定远端路径,并报告传输进度。</summary>
    Task UploadFileAsync(Guid sessionId, string localPath, string remotePath, IProgress<TransferProgress>? progress = null, long resumeOffset = 0, CancellationToken cancellationToken = default);

    /// <summary>
    /// 把一条<b>顺序读</b>的流写成远端文件(覆盖),并报告传输进度。流由调用方打开、调用方释放。
    /// <para>
    /// 双栏远程文档里的跨会话传输靠它:源端 <see cref="OpenReadAsync" /> 读出来的流直接喂给目标端,
    /// 字节只在内存里过一下,不落本地磁盘。<paramref name="length" /> 是源文件的字节数,
    /// 只用于进度的分母(流本身可能不可 Seek,问不出长度);给 0 时进度退化成只报已传字节。
    /// </para>
    /// <para>
    /// <paramref name="lastWriteTime" /> 不为 null 时,按「保留时间戳」设置把目标的修改时间对齐源文件
    /// (与 <see cref="UploadFileAsync" /> 同一口径,尽力而为)。限速按上传方向计。
    /// 后端没有这种能力(插件协议)时抛 <see cref="NotSupportedException" />。
    /// </para>
    /// <para>
    /// <paramref name="resumeOffset" /> &gt; 0 表示断点续传,语义与 <see cref="UploadFileAsync" /> 相同:
    /// 那个数只决定「要不要试着续」,真正的起点由实现按目标此刻的状态重新核实(可能回退、可能整份重传),
    /// 核实不通过抛 <c>VelaSftpResumeMismatchException</c>;服务器根本续不了(FTP 不认 REST / APPE)抛它的派生类
    /// <c>VelaSftpResumeUnsupportedException</c>,下载续传同样如此,两种情形目标都还没被碰过。续传要能在源里来回定位,
    /// 所以此时 <paramref name="source" /> <b>必须可 Seek</b>,否则抛 <see cref="ArgumentException" />;
    /// 实现返回时源的位置不做保证。
    /// </para>
    /// <para>
    /// <paramref name="createMode" /> 不为 null 时,目标由这次上传新建的话就按这个权限位建(只取 0777,服务端照常再减 umask;
    /// 已存在的文件不改模式)。覆盖上传的临时文件靠它一建出来就不比被替换的文件宽(见 <see cref="GetUploadCreateModeAsync" />)。
    /// 没有权限位概念的后端(FTP、插件协议)忽略它。
    /// </para>
    /// </summary>
    Task UploadStreamAsync(Guid sessionId, Stream source, string remotePath, long length, DateTime? lastWriteTime = null, IProgress<TransferProgress>? progress = null, long resumeOffset = 0, uint? createMode = null, CancellationToken cancellationToken = default);

    /// <summary>将远端文件下载到给定本地路径,并报告传输进度。</summary>
    Task DownloadFileAsync(Guid sessionId, string remotePath, string localPath, IProgress<TransferProgress>? progress = null, long resumeOffset = 0, CancellationToken cancellationToken = default);

    /// <summary>将远端文件流式下载到调用方已打开的目标流。实现不得关闭该流。</summary>
    Task DownloadStreamAsync(Guid sessionId, string remotePath, Stream destination,
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This backend cannot download to an opened stream.");

    /// <summary>
    /// 删除文件,或递归删除目录及其全部内容。每移除一个条目回报一次进度,
    /// 以便界面对大/慢文件夹的删除展示进度。
    /// </summary>
    Task DeleteAsync(Guid sessionId, string remotePath, IProgress<SftpDeleteProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>在给定远端路径创建新目录。</summary>
    Task CreateDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default);

    /// <summary>在给定远端路径创建一个空文件。</summary>
    Task CreateFileAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 若目录不存在则创建(幂等)。上传文件夹树时使用,以便重建已存在的子目录不会报错。
    /// </summary>
    Task EnsureDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default);

    /// <summary>重命名或移动远端条目(SFTP 的 rename 兼具 move 语义)。</summary>
    Task RenameAsync(Guid sessionId, string oldPath, string newPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 提交已关闭并校验的上传临时文件。无覆盖只走标准无替换 rename;覆盖只走 POSIX 原子替换,不删除目标、不回退,
    /// 并把被替换的普通文件原有的权限与属主带到新文件上(带不过去又会放宽权限时拒绝提交)。
    /// </summary>
    Task CommitUploadAsync(Guid sessionId, string temporaryPath, string destinationPath, bool overwrite,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This backend cannot guarantee atomic upload commit.");

    /// <summary>
    /// 上传开始写之前的提交预检:<see cref="CommitUploadAsync" /> 注定拒绝时提前拒绝,不白传一整份字节 ——
    /// 覆盖而服务端没有原子替换抛 <see cref="NotSupportedException" />,不覆盖而目标已存在抛 <c>VelaSftpOperationException</c>。
    /// 只是提示:查不出来就不拦,预检之后才出现的目标照样由提交拒绝,提交仍是唯一的权威。
    /// </summary>
    Task CheckUploadCommitAsync(Guid sessionId, string destinationPath, bool overwrite,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This backend cannot guarantee atomic upload commit.");

    /// <summary>
    /// 覆盖上传开写之前:临时文件该按什么权限位新建(交给 <see cref="UploadStreamAsync" /> 的 <c>createMode</c>)——
    /// 被替换的普通文件此刻的 0777 那几位;读不到它的模式时按 0600(提交时 <see cref="CommitUploadAsync" /> 反正会拒绝)。
    /// 目标不存在、不是普通文件、或后端没有权限位时为 null(按服务端默认新建,与新建上传一个口径)。
    /// </summary>
    /// <remarks>
    /// 以前临时文件一律按 0644(减 umask)新建,要到提交前才把原文件的权限位带过去:覆盖一个 0600 的密钥文件时,
    /// 写入的那段时间里同机的其他用户读得到新内容。setuid / setgid / 粘滞位不在建文件时带(属主还没对上),仍由提交补上。
    /// 只是收窄写入期间的暴露面,提交时照旧按那一刻的目标重新核对、补齐属主与全部权限位。
    /// </remarks>
    Task<uint?> GetUploadCreateModeAsync(Guid sessionId, string destinationPath, CancellationToken cancellationToken = default)
        => Task.FromResult<uint?>(null);

    /// <summary>
    /// 将远端文件或目录树复制到同一服务器的另一路径。单个文件采用先下载到本地临时文件再上传的方式;
    /// 目录则逐文件递归复制。按文件回报传输进度。
    /// </summary>
    Task CopyAsync(Guid sessionId, string sourcePath, string destPath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 修改远端条目的权限(chmod)。<paramref name="octalMode" /> 是三位八进制数字,
    /// 以十进制数书写(如 755、644),与 `chmod` 记法一致。
    /// </summary>
    Task SetPermissionsAsync(Guid sessionId, string remotePath, short octalMode, CancellationToken cancellationToken = default);

    /// <summary>
    /// 修改远端条目的属主与属组(chown)。<paramref name="owner" /> / <paramref name="group" /> 填用户名 / 组名
    /// 或十进制数字 id,为 null 表示这一项不改,两个都为 null 时什么都不做。
    /// 名称按远端的 passwd/group 库翻成数字 id,翻不出来抛 <see cref="ArgumentException" />。
    /// 符号链接改的是它指向的对象(与 <see cref="SetPermissionsAsync" /> 一致)。
    /// 后端没有这种能力(FTP、插件协议)时抛 <see cref="NotSupportedException" />。
    /// </summary>
    Task SetOwnerAsync(Guid sessionId, string remotePath, string? owner, string? group, CancellationToken cancellationToken = default);

    /// <summary>
    /// 改属主/属组时可选的用户名与组名。后端改不了属主(FTP、插件协议)时返回 null ——
    /// 属性弹窗据此把属主/属组显示成只读,而不是让用户填完再报「不支持」。
    /// </summary>
    Task<RemoteOwnerChoices?> GetOwnerChoicesAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 把远端文件的修改时间设为 <paramref name="lastWriteTimeUtc" />(UTC)。目录同步靠它在上传后
    /// 把远端 mtime 对齐本地源文件,否则下一次比较会把刚传上去的文件判成「远端较新」。
    /// 后端没有这种能力(插件协议、不支持 <c>MFMT</c> 的 FTP 服务器)时抛 <see cref="NotSupportedException" />。
    /// </summary>
    Task SetLastWriteTimeAsync(Guid sessionId, string remotePath, DateTime lastWriteTimeUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// 在服务器端批量算文件的 SHA-256(小写十六进制),不经网络传内容。返回 请求的路径 → 摘要;
    /// 单个文件算不出来(不存在、没权限)时值为 null。整个后端做不到(没有 exec 通道 / 没有 <c>sha256sum</c>、
    /// FTP 服务器不提供 <c>HASH</c> / <c>XSHA256</c>、插件协议)时抛 <see cref="NotSupportedException" />,
    /// 调用方据此回退到大小与修改时间比较。
    /// </summary>
    Task<IReadOnlyDictionary<string, string?>> ComputeSha256Async(Guid sessionId, IReadOnlyList<string> remotePaths, CancellationToken cancellationToken = default);

    /// <summary>
    /// 在 <paramref name="linkPath" /> 创建指向 <paramref name="targetPath" /> 的符号链接。
    /// <paramref name="targetPath" /> 原样写入(相对路径按链接所在目录解析,与 <c>ln -s</c> 一致)。
    /// 后端没有这种能力(插件协议、不支持 <c>SITE SYMLINK</c> 的 FTP 服务器)时抛
    /// <see cref="NotSupportedException" />,而不是静默成功。
    /// </summary>
    Task CreateSymbolicLinkAsync(Guid sessionId, string linkPath, string targetPath, CancellationToken cancellationToken = default);

    /// <summary>获取单个远端文件或目录的元数据。</summary>
    Task<RemoteFileInfo> GetFileInfoAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 打开远端文件的只读流(顺序读取,调用方负责释放)。适合边读边处理的大文件
    /// (日志、导出),不经本地临时文件中转。
    /// </summary>
    Task<Stream> OpenReadAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 远端路径是否存在(文件或目录)。用于在覆盖远端文件前进行上传冲突检测。
    /// </summary>
    Task<bool> ExistsAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 会话的 SFTP 工作目录(登录后即为账户的 home 目录),用于在该处打开浏览器而非文件系统根目录。
    /// </summary>
    Task<string> GetWorkingDirectoryAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 远端路径所在文件系统的用量;查不到(服务端不支持 <c>statvfs@openssh.com</c>、FTP、插件协议)时为 <see langword="null" />。
    /// </summary>
    /// <remarks>查不到不是错误:文件面板只是不显示剩余空间、上传前不预检。</remarks>
    Task<RemoteSpaceInfo?> GetSpaceAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 请服务端展开 <c>~用户名</c> 这类本地展开不了的路径(要读远端的 passwd),交回绝对路径;
    /// 展开不了(服务端没有相应的扩展、用户不存在、FTP、插件协议)时为 <see langword="null" />。
    /// </summary>
    Task<string?> ExpandPathAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 关闭并释放某个会话的 SFTP 通道(在其 SSH 标签页关闭时调用),使其不再持有活动连接或接受操作。
    /// 若会话未打开 SFTP 通道则为空操作。
    /// </summary>
    Task CloseSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
