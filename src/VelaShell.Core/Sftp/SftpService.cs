using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;

namespace VelaShell.Core.Sftp;

/// <summary>
/// 基于 SSH 会话的 SFTP 文件操作服务:按会话缓存并复用 SFTP 客户端,提供目录浏览、
/// 上传/下载、删除、重命名、权限设置等远端文件系统操作。
/// </summary>
public class SftpService : ISftpService
{
    private readonly ISshConnectionService _connectionService;
    private readonly Func<SshSession, ISftpClientWrapper>? _sftpClientFactory;
    private readonly ISettingsService? _settingsService;
    private readonly ConcurrentDictionary<Guid, ISftpClientWrapper> _sftpClients = new();

    /// <summary>
    /// 传输专用的 SFTP 客户端:同一条 SSH 连接上的<b>第二条</b> SFTP 通道(见 <see cref="GetTransferClientAsync" />)。
    /// </summary>
    private readonly ConcurrentDictionary<Guid, ISftpClientWrapper> _transferClients = new();

    /// <summary>开不出第二条 SFTP 通道的会话(服务端 MaxSessions 太小之类):传输回到浏览用的那一条上。</summary>
    private readonly ConcurrentDictionary<Guid, bool> _singleChannelSessions = new();

    /// <summary>
    /// 上一次被打断的上传留下的精确续传点(库交出来的 DurableLength),按会话与远端路径记;
    /// 下一次续传这个路径时用它,而不是按远端长度盲退一个在途窗口(见 <see cref="ResolveUploadResumeAsync(ISftpClientWrapper, string, Stream, long, long?, CancellationToken)" />)。
    /// </summary>
    private readonly ConcurrentDictionary<(Guid Session, string Path), long> _durableUploads = new();

    /// <summary>属主/属组的数字 id → 名称翻译(按会话缓存,见 RemoteIdentityResolver)。</summary>
    private readonly RemoteIdentityResolver _identities;

    /// <summary>SFTP 客户端创建的按会话单飞闸(见 GetOrCreateSftpClientAsync)。</summary>
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _clientGates = new();

    /// <summary>
    /// 构造服务,并把 SFTP 通道的生命周期挂到 SSH 会话的生命周期上。
    /// </summary>
    /// <remarks>
    /// SFTP 不是一条独立的连接:它是**主 SSH 连接上的一个通道**
    /// (见 DI 注册处的 <c>OpenSftpClientAsync</c> —— 刻意复用主连接,
    /// 不在主连接之外偷偷另开一条)。既然如此,SSH 一断,这边缓存的客户端就已经是个
    /// 死物,只是还占着字典与非托管句柄。
    /// <para>
    /// 因此清理在这里订阅 <see cref="ISshConnectionService.SessionDisconnected" />,
    /// 而不是靠每个断开入口自己记得调一次 <see cref="CloseSessionAsync" /> ——
    /// 断开的入口不止一个(标签断开、关标签、隧道面板、退出应用),
    /// 靠约定去维持这个不变量,迟早会漏掉一个。
    /// </para>
    /// </remarks>
    public SftpService(
        ISshConnectionService connectionService,
        Func<SshSession, ISftpClientWrapper>? sftpClientFactory = null,
        ISettingsService? settingsService = null)
    {
        _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
        _sftpClientFactory = sftpClientFactory;
        _settingsService = settingsService;
        _identities = new(connectionService);
        _connectionService.SessionDisconnected += OnSshSessionDisconnected;
    }

    /// <summary>
    /// SSH 会话断了 —— 连它上面那条 SFTP 通道一起收掉。
    /// </summary>
    /// <remarks>
    /// 即发即忘:这是断开路径上的清理,拖慢它没有意义,失败也没有可补救的动作
    /// (要关的东西已经跟着连接一起没了)。<see cref="CloseSessionAsync" /> 内部
    /// 对已死的客户端本就是尽力而为。
    /// </remarks>
    private void OnSshSessionDisconnected(SshSession session) =>
        _ = CloseSessionAsync(session.SessionId);

    /// <summary>列出指定会话下远端目录的条目(自动剔除 "." 与 ".." )。</summary>
    public async Task<List<RemoteFileInfo>> ListDirectoryAsync(Guid sessionId, string path, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        IEnumerable<SftpEntry> files = await client.ListDirectoryAsync(path, cancellationToken).ConfigureAwait(false);

        // 属主/属组名要查远端 passwd 库(SFTP 只报数字 id):每会话查一次;表里没有的(只开了 SFTP 的账号整张表都没有)
        // 经 SFTP 扩展补上,还查不到就回退数字。
        SftpEntry[] entries = [.. files.Where(f => f.Name is not "." and not "..")];
        RemoteIdentityMap identities = await _identities.FillAsync(
            sessionId, client, entries.Select(f => f.UserId), entries.Select(f => f.GroupId), cancellationToken).ConfigureAwait(false);
        return [.. entries.Select(f => MapToRemoteFileInfo(f, identities))];
    }

    /// <summary>将本地文件上传到远端路径,可选限速与进度回报,支持取消。</summary>
    /// <summary>
    /// 上传的几种形状只差几个参数:要保留时间戳、要落盘或要指定新建权限位的,带着这些要求上传(关闭之前按同一个句柄做完);
    /// 其余按有没有续传起点选重载。
    /// </summary>
    private static Task UploadAsync(ISftpClientWrapper client, Stream input, string remotePath, long resumeOffset,
        DateTimeOffset? lastWriteTime, bool fsync, Action<ulong>? onBytes, uint? createMode,
        CancellationToken cancellationToken) =>
        lastWriteTime is not null || fsync || createMode is not null
            ? client.UploadAsync(input, remotePath, new RemoteUploadOptions(resumeOffset, lastWriteTime, fsync, createMode), onBytes, cancellationToken)
            : resumeOffset > 0
                ? client.UploadAsync(input, remotePath, resumeOffset, onBytes, cancellationToken)
                : client.UploadAsync(input, remotePath, onBytes, cancellationToken);

    /// <summary>
    /// 上传的进度上报:字节全部确认的那一拍绕过节流;任务成功仍由上传调用(含 fsync / CLOSE)决定。
    /// 满格只报一次:确认阶段报过了,成功收尾就不再补一帧;确认阶段里 Flush 的定时采样与最后一次写回调
    /// 也可能各报一遍满格,只放第一帧 —— 把满格当「完成」看的下游(插件桥)不该收到好几次完成。
    /// </summary>
    private sealed class UploadProgress(TransferProgressThrottle reporter, long totalBytes)
    {
        private int _fullReported;

        /// <summary>交给上传调用的确认字节回调;没有进度接收方时为 null。</summary>
        public Action<ulong>? OnBytes => reporter.IsEnabled ? OnConfirmed : null;

        /// <summary>上传成功后收尾。节流会丢弃最后一个时间片内的上报,确认阶段没报到满格的话进度条会停在 99%。</summary>
        public void Complete()
        {
            if (Interlocked.Exchange(ref _fullReported, 1) == 0)
            {
                reporter.ReportFinal(totalBytes);
            }
        }

        private void OnConfirmed(ulong bytes)
        {
            if ((long)bytes != totalBytes)
            {
                reporter.Report((long)bytes);
            }
            else if (Interlocked.Exchange(ref _fullReported, 1) == 0)
            {
                reporter.ReportFinal(totalBytes);
            }
        }
    }

    /// <summary>将本地文件上传到远端路径,可选限速与进度回报,支持断点续传(resumeOffset > 0 时追加上传)。</summary>
    public async Task UploadFileAsync(Guid sessionId,
        string localPath,
        string remotePath,
        IProgress<TransferProgress>? progress = null,
        long resumeOffset = 0,
        CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetTransferClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var fileInfo = new FileInfo(localPath);
        long totalBytes = fileInfo.Length;
        string fileName = Path.GetFileName(localPath);
        (long uploadBps, _, bool preserveTimestamps, bool fsync) = await GetTransferTuningAsync().ConfigureAwait(false);

        // 以此刻的远端状态重新核实续传起点;核实不通过会抛错,核实为"无可续"则整份重传。
        long? durable = TakeDurableHint(sessionId, remotePath);
        if (resumeOffset > 0)
        {
            resumeOffset = await ResolveUploadResumeAsync(client, remotePath, localPath, totalBytes, durable, cancellationToken).ConfigureAwait(false);
        }

        // 起点核实完才开始计时、才知道从哪儿算速度(见 TransferProgressThrottle 的 startBytes)。
        var reporter = new UploadProgress(new TransferProgressThrottle(progress, fileName, totalBytes, resumeOffset), totalBytes);
        Action<ulong>? onBytes = reporter.OnBytes;

        // 续传与全新上传只差一个偏移量参数,其余(限速包装、收尾上报)完全一致。
        Stream source = OpenLocalRead(localPath);
        Stream fileStream = uploadBps > 0 ? new ThrottledStream(source, uploadBps) : source;
        try
        {
            try
            {
                // 保留时间戳(设置 → 文件传输,scp -p 语义):把远端 mtime 设回本地源文件的 mtime
                // (下载方向的对等实现见 DownloadFileAsync)。关闭之前用同一个句柄设,一次往返;
                // 尽力而为 —— 个别服务器禁 setstat,不能让一次时间戳设置失败把已完成的上传标成失败。
                await UploadAsync(client, fileStream, remotePath, resumeOffset,
                    preserveTimestamps ? new DateTimeOffset(fileInfo.LastWriteTimeUtc) : null, fsync, onBytes, createMode: null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                RememberDurable(sessionId, remotePath, failure);
                throw;
            }

            reporter.Complete();
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is not OperationCanceledException)
        {
            // 兜底:取消后连接被撕掉之类的场景可能先冒出 IO 错误,统一归一为取消。
            // 正常路径不会走到这里 —— 底层库自己就响应 CancellationToken 并抛 OperationCanceledException。
            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            await fileStream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 把一条顺序读的流写成远端文件(覆盖)。与 <see cref="UploadFileAsync" /> 同一条写入路径
    /// (底层库的并发写窗口、上传限速、收尾补报 100%、尽力而为的时间戳),只是源从本地文件换成了调用方给的流。
    /// </summary>
    /// <remarks>
    /// 续传与 <see cref="UploadFileAsync" /> 同一套核实(此刻的远端长度、回退一个在途写入窗口、比对尾部),
    /// 只是比对的另一方是源流 —— 所以续传时源必须可 Seek(SFTP 的读流可以,FTP 的数据流不行)。
    /// </remarks>
    public async Task UploadStreamAsync(Guid sessionId,
        Stream source,
        string remotePath,
        long length,
        DateTime? lastWriteTime = null,
        IProgress<TransferProgress>? progress = null,
        long resumeOffset = 0,
        uint? createMode = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (resumeOffset > 0 && !source.CanSeek)
        {
            throw new ArgumentException("Resuming a streamed upload needs a seekable source.", nameof(source));
        }
        ISftpClientWrapper client = await GetTransferClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        (long uploadBps, _, bool preserveTimestamps, bool fsync) = await GetTransferTuningAsync().ConfigureAwait(false);

        long? durable = TakeDurableHint(sessionId, remotePath);
        if (resumeOffset > 0)
        {
            resumeOffset = await ResolveUploadResumeAsync(client, remotePath, source, length, durable, cancellationToken).ConfigureAwait(false);
            // 核实时两边都被定位过;整份重传要从头读(续传由下面的 UploadAsync 自己定位到起点)。
            if (resumeOffset == 0)
            {
                source.Seek(0, SeekOrigin.Begin);
            }
        }
        var reporter = new UploadProgress(new TransferProgressThrottle(progress, GetUnixFileName(remotePath), length, resumeOffset), length);
        Action<ulong>? onBytes = reporter.OnBytes;

        // 限速包装刻意不释放:它自己不持有任何资源,而释放它会连带关掉调用方的源流 ——
        // 流归调用方(契约如此),它还要在 await using 里自己收尾。
        Stream input = uploadBps > 0 ? new ThrottledStream(source, uploadBps) : source;
        try
        {
            DateTimeOffset? keepTime = preserveTimestamps && lastWriteTime is { } mtime && mtime != default
                ? new DateTimeOffset(mtime.Kind == DateTimeKind.Utc ? mtime : mtime.ToUniversalTime())
                : null;
            try
            {
                await UploadAsync(client, input, remotePath, resumeOffset, keepTime, fsync, onBytes, createMode, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                RememberDurable(sessionId, remotePath, failure);
                throw;
            }
            reporter.Complete();
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is not OperationCanceledException)
        {
            // 兜底,理由同 UploadFileAsync。
            throw new OperationCanceledException(cancellationToken);
        }
    }

    /// <summary>将远端文件下载到本地路径,可选限速与进度回报;按设置可保留文件修改时间戳。</summary>
    public async Task DownloadFileAsync(Guid sessionId,
        string remotePath,
        string localPath,
        IProgress<TransferProgress>? progress = null,
        long resumeOffset = 0,
        CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetTransferClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        string fileName = GetUnixFileName(remotePath);
        RemoteFileInfo fileInfo = await GetFileInfoAsync(sessionId, remotePath, cancellationToken).ConfigureAwait(false);
        long totalBytes = fileInfo.Size;
        (_, long downloadBps, bool preserveTimestamps, _) = await GetTransferTuningAsync().ConfigureAwait(false);

        // 以此刻本地残留文件的实际长度重新核实续传起点(理由同上传侧)。
        if (resumeOffset > 0)
        {
            resumeOffset = await ResolveDownloadResumeAsync(client, remotePath, localPath, totalBytes, cancellationToken).ConfigureAwait(false);
        }
        var reporter = new TransferProgressThrottle(progress, fileName, totalBytes, resumeOffset);

        if (resumeOffset > 0)
        {
            // 断点续传下载:从核实过的起点续写。
            // 不能用 FileMode.Append —— 它追加在"文件实际末尾",而续传起点已经回退过一个在途
            // 写入窗口,两者对不上就会在文件里留下空隙。这里显式截断到起点再定位过去,
            // 顺便丢掉起点之后那段可能含空洞的可疑残留。
            await using var localStream = new FileStream(localPath, FileMode.Open, FileAccess.Write, FileShare.None,
                LocalStreamBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            localStream.SetLength(resumeOffset);
            localStream.Seek(resumeOffset, SeekOrigin.Begin);
            // 限速与整份下载同一个做法:包住本地写流。续传这条路是自己搬字节、不经 client.DownloadAsync,
            // 原先漏了这一层,开着带宽限制时一续传就不限速了。包装流不持有资源,不必单独释放(本地流由上面的 await using 关)。
            Stream sink = downloadBps > 0 ? new ThrottledStream(localStream, downloadBps) : localStream;
            // 从偏移处分块拷贝下载远端内容。远端流用 await using:同步 Dispose 会阻塞在网络关闭上。
            await using Stream remoteStream = await client.OpenAsync(remotePath, FileMode.Open, FileAccess.Read, cancellationToken).ConfigureAwait(false);
            remoteStream.Seek(resumeOffset, SeekOrigin.Begin);

            // 32KB 一次往返对高延迟链路太小;续传路径是自己搬字节,块大些能显著减少往返次数。
            // 缓冲走池:256KB 超过 85KB 的大对象堆阈值,每次续传新开一个就是往 LOH 里丢一块
            // (LOH 不压缩,累积成碎片);池的最大桶是 1MB,这个尺寸正好落在池内。
            byte[] buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
            try
            {
                long bytesRead = 0;
                int read;
                while ((read = await remoteStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await sink.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    bytesRead += read;
                    reporter.Report(resumeOffset + bytesRead);
                }
                reporter.ReportFinal(resumeOffset + bytesRead);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        else
        {
            Stream sink = OpenLocalWrite(localPath);
            Stream fileStream = downloadBps > 0 ? new ThrottledStream(sink, downloadBps) : sink;
            Action<ulong>? onBytes = reporter.IsEnabled ? bytes => reporter.Report((long)bytes) : null;

            try
            {
                await client.DownloadAsync(remotePath, fileStream, onBytes, cancellationToken).ConfigureAwait(false);
                reporter.ReportFinal(totalBytes);
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is not OperationCanceledException)
            {
                // 兜底,理由同 UploadFileAsync。
                throw new OperationCanceledException(cancellationToken);
            }
            finally
            {
                await fileStream.DisposeAsync().ConfigureAwait(false);
            }
        }

        // 保留文件时间戳(设置 → 文件传输):下载完成后把远端修改时间写到本地副本。
        if (preserveTimestamps && fileInfo.LastModified != default)
        {
            try
            {
                File.SetLastWriteTime(localPath, fileInfo.LastModified);
            }
            catch
            {
                // 时间戳只是尽力而为。
            }
        }
    }

    /// <summary>将远端文件写入调用方提供的流,保留限速与实际字节统计。</summary>
    public async Task DownloadStreamAsync(Guid sessionId, string remotePath, Stream destination,
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("Destination stream must be writable.", nameof(destination));
        ISftpClientWrapper client = await GetTransferClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        RemoteFileInfo info = await GetFileInfoAsync(sessionId, remotePath, cancellationToken).ConfigureAwait(false);
        (_, long downloadBps, bool preserveTimestamps, _) = await GetTransferTuningAsync().ConfigureAwait(false);
        var reporter = new TransferProgressThrottle(progress, GetUnixFileName(remotePath), info.Size);
        await using Stream remote = await client.OpenAsync(remotePath, FileMode.Open, FileAccess.Read, cancellationToken).ConfigureAwait(false);
        Stream sink = downloadBps > 0 ? new ThrottledStream(destination, downloadBps) : destination;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        long transferred = 0;
        try
        {
            int read;
            while ((read = await remote.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await sink.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                transferred += read;
                reporter.Report(transferred);
            }
            reporter.ReportFinal(transferred);
            // 调用方持有的临时文件句柄上应用时间戳;绝不按最终目标路径写元数据。
            if (preserveTimestamps && destination is FileStream file && info.LastModified != default)
            {
                // Flush buffered writes before setting mtime, otherwise a later flush resets it.
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                try { File.SetLastWriteTimeUtc(file.SafeFileHandle, info.LastModified.ToUniversalTime()); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            // ThrottledStream 无独立资源,释放会连带关闭调用方拥有的流。
        }
    }

    /// <summary>删除远端文件或目录;目录按深度优先递归删除子项,并逐条回报删除进度。</summary>
    public async Task DeleteAsync(Guid sessionId, string remotePath, IProgress<SftpDeleteProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        // Deletion abandons this upload identity even if remote cleanup later fails.
        // A future upload must verify the remote file rather than reuse a stale hint.
        _ = TakeDurableHint(sessionId, remotePath);
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);

        // 一次 stat 同时回答"存在吗"和"是不是目录";旧实现是 Exists + 列举整个父目录两趟。
        SftpEntry entry = await client.GetEntryAsync(remotePath, cancellationToken).ConfigureAwait(false)
                          ?? throw new FileNotFoundException($"Remote path not found: {remotePath}");
        // 链接一律当叶子删(只删链接本身):沿链接递归,删掉的会是链接**指向的**那棵树。
        bool isDirectory = IsTraversableDirectory(entry);

        // 目录树先试一条 rm -rf(#474):SFTP 递归的往返次数与文件数成正比,
        // 一次 exec 则是一次往返。试不成(没有 exec 通道、非 Unix 主机、命令退非零码)
        // 就照旧走下面的 SFTP 递归 —— 回退路径会把真正的失败原因带出来。
        // 退出码 0 也再 stat 一次:rm -f 删一个不存在的路径同样退 0,路径的字节一旦与 SFTP 看到的不是同一串
        // (见 CanPassPathToCommand),「删成功」就是一句空话。目录还在就接着走 SFTP 递归,而不是报成功。
        if (isDirectory
            && await TryDeleteDirectoryByCommandAsync(sessionId, remotePath, progress, cancellationToken).ConfigureAwait(false)
            && await client.GetEntryAsync(remotePath, cancellationToken).ConfigureAwait(false) is null)
        {
            return;
        }
        int total = await CountEntriesAsync(client, remotePath, isDirectory, cancellationToken).ConfigureAwait(false);

        // 先发出一个 "0 / total" 的进度点,使 UI 能立即切换到确定型进度。
        progress?.Report(new(0, total, remotePath));
        var counter = new DeleteCounter();
        await DeleteEntryAsync(client, remotePath, isDirectory, total, counter, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>在远端创建指定路径的目录。</summary>
    public async Task CreateDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await client.CreateDirectoryAsync(remotePath, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>在远端创建一个空文件。</summary>
    public async Task CreateFileAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        using var empty = new MemoryStream();
        await client.UploadAsync(empty, remotePath, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>幂等地确保远端目录存在:已存在直接返回,否则创建。</summary>
    public async Task EnsureDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);

        // 先 Exists 探测再创建。与直觉相反,SFTP 的 MKDIR 对"已存在的目录"会回错误而抛
        // SftpException(并非无声成功):重复上传同一文件夹树(与服务端冲突的常见场景)时,
        // 每个已存在子目录都会甩出一条首发异常,上千文件的文件夹重传即刷出上百条异常噪声。
        // ExistsAsync 走 GetAttributes(不存在返回 null、零异常控制流),先探测即可对"已存在"
        // 零异常返回;仅真正缺失的目录才 CreateDirectory(此时创建必然成功,同样不产生异常)。
        // 代价是新建目录多一次 stat 往返,但目录数远少于文件数,相对整批传输可忽略。
        if (await client.ExistsAsync(remotePath, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        try
        {
            await client.CreateDirectoryAsync(remotePath, cancellationToken).ConfigureAwait(false);
        }
        catch (VelaSshClientException)
        {
            // 探测与创建之间被他方(并发上传/别的客户端)抢先创建的竞态:目录现已存在即幂等
            // 成功;其余失败(权限/父目录缺失)照常抛出。
            if (!await client.ExistsAsync(remotePath, cancellationToken).ConfigureAwait(false))
            {
                throw;
            }
        }
    }

    /// <summary>重命名或移动远端文件/目录;普通 rename 被服务器拒绝时回退到 posix-rename 扩展。</summary>
    public async Task RenameAsync(Guid sessionId, string oldPath, string newPath, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        try
        {
            await client.RenameFileAsync(oldPath, newPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is VelaSftpOperationException or NotSupportedException)
        {
            // 部分 SFTP 服务器以 SSH_FX_BAD_MESSAGE(表现为"bad message")拒绝普通的 SSH_FXP_RENAME,
            // 跨目录移动时常见。改用被广泛支持的 posix-rename@openssh.com 扩展重试;若该路径也不可用,
            // 则抛出原本更具信息量的错误。
            //
            // ⚠️ 目标已存在时**不回退**:posix-rename 的语义是原子覆盖,而普通 rename 失败的
            // 最常见原因恰恰就是「目标已存在」。回退过去等于把用户「改名撞了同名文件」
            // 变成静默覆盖 —— 数据就这么没了。上一版底层库的 posix-rename 是假的(转调普通
            // rename),所以这个坑以前没现形。
            bool targetExists;
            try
            {
                targetExists = await client.ExistsAsync(newPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception probe) when (probe is not OperationCanceledException)
            {
                // 连存在与否都问不出来,就别冒覆盖的险。
                targetExists = true;
            }
            if (targetExists)
            {
                throw;
            }
            try
            {
                await client.PosixRenameFileAsync(oldPath, newPath, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                throw ex;
            }
        }
    }

    /// <inheritdoc />
    public async Task CommitUploadAsync(Guid sessionId, string temporaryPath, string destinationPath, bool overwrite,
        CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (overwrite)
        {
            // 没有原子替换就不必先带模式:下面的替换照样拒绝,拒绝原因也该是「不支持」,而不是带模式时的别的失败。
            if (client.SupportsAtomicReplace)
            {
                await CarryOverReplacedModeAsync(client, temporaryPath, destinationPath, cancellationToken).ConfigureAwait(false);
            }
            await client.AtomicReplaceFileAsync(temporaryPath, destinationPath, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // 不走 RenameAsync 的探测后回退:探测与 POSIX rename 间可能出现新目标。
            await client.RenameFileAsync(temporaryPath, destinationPath, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 覆盖提交之前,把被替换的普通文件原有的属主与权限位带到临时文件上。posix-rename 换进来的是临时文件那个 inode,
    /// 它按 0644 新建:不带过去的话,覆盖一个 0600 的密钥文件就成了 0644,0755 的脚本丢掉执行位(旧的就地覆盖写不改这些)。
    /// <list type="bullet">
    ///   <item>目标不存在(新文件)或不是普通文件:不动,与新建上传同一口径 —— 被替换的是链接本身,目录本来就换不了;</item>
    ///   <item>属主尽力而为:普通用户改不了别人的属主,服务端拒绝就留着上传者。setuid / setgid 只在属主属组对上之后才带,
    ///   否则等于凭空造出一个以上传者身份运行的 setuid 程序;属主先改、权限后改,非 root 改属主时内核会清掉这两位;</item>
    ///   <item>只许收窄,不许放宽:目标存在却读不到它的模式,或改不了临时文件的模式而它比目标宽,就拒绝提交、目标原样不动 ——
    ///   宁可这次覆盖失败,也不让一个 0600 的文件悄悄变成 0644。</item>
    /// </list>
    /// </summary>
    private static async Task CarryOverReplacedModeAsync(ISftpClientWrapper client, string temporaryPath, string destinationPath,
        CancellationToken cancellationToken)
    {
        SftpFileMode? target = await client.GetLinkModeAsync(destinationPath, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return;
        }
        if (target.Mode is not { } mode)
        {
            throw new NotSupportedException("The server did not report the permissions of the file being replaced; refusing to replace it with default permissions.");
        }
        if (!target.IsRegularFile)
        {
            return;
        }

        bool owned = false;
        if (target is { UserId: { } uid, GroupId: { } gid })
        {
            try
            {
                await client.ChangeOwnerAsync(temporaryPath, uid, gid, cancellationToken).ConfigureAwait(false);
                owned = true;
            }
            catch (VelaSftpOperationException ex) when (ex is not VelaSftpTransferInterruptedException)
            {
                // 属主尽力而为:服务端明确拒绝(不是 root 就改不了别人的属主)时留着上传者。
            }
        }

        // 0777 与粘滞位(01777)照带;setuid / setgid(06000)只在属主属组对上之后才带。
        uint wanted = mode & (owned ? 0xFFFu : 0x3FFu);
        try
        {
            await client.SetModeAsync(temporaryPath, wanted, cancellationToken).ConfigureAwait(false);
        }
        catch (VelaSftpOperationException ex) when (ex is not VelaSftpTransferInterruptedException)
        {
            // 服务端不许改模式:临时文件不比目标宽(只会少几位,比如丢了执行位)才照样提交,否则原样拒绝。
            SftpFileMode? temporary = await client.GetLinkModeAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            if (temporary?.PermissionBits is not { } bits || (bits & ~wanted) != 0)
            {
                throw;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 与提交时 <see cref="CarryOverReplacedModeAsync" /> 同一套判断(lstat,不跟随链接):目标是链接、目录就不管(换掉的是链接本身,
    /// 与新建上传同一口径);读到模式就只取 0777;是普通文件却没报模式,按最严的 0600 建 —— 提交时那一步会因为读不到模式拒绝,
    /// 临时文件随后被删,写入期间也不该比 0600 宽。
    /// </remarks>
    public async Task<uint?> GetUploadCreateModeAsync(Guid sessionId, string destinationPath, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        SftpFileMode? target = await client.GetLinkModeAsync(destinationPath, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return null;
        }
        if (target.Mode is not { } mode)
        {
            return OwnerOnlyMode;
        }
        return target.IsRegularFile ? mode & 0x1FFu : null;
    }

    /// <summary>读不到被替换文件的模式时临时文件按它新建:只有属主读写。</summary>
    private const uint OwnerOnlyMode = 0x180; // 0600

    /// <inheritdoc />
    public async Task CheckUploadCommitAsync(Guid sessionId, string destinationPath, bool overwrite,
        CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (overwrite)
        {
            if (!client.SupportsAtomicReplace)
            {
                throw new NotSupportedException("The server does not support atomic POSIX replacement.");
            }
            return;
        }
        bool exists;
        try
        {
            exists = await client.ExistsAsync(destinationPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 预检只负责提前拒绝:问不出来(没权限 stat、通道出错)就不拦,由提交按服务端的应答定。
            return;
        }
        if (exists)
        {
            throw new VelaSftpOperationException("The upload destination already exists.");
        }
    }

    /// <summary>
    /// 将远端文件或目录复制到同一服务器的另一路径。
    /// 单个文件经由临时本地文件复制(不在内存中缓冲大文件)。
    /// 目录则递归遍历并做循环检测。
    /// </summary>
    public async Task CopyAsync(Guid sessionId,
        string sourcePath,
        string destPath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetTransferClientAsync(sessionId, cancellationToken).ConfigureAwait(false);

        // 通过 stat 判断源是否为目录(旧实现名为 stat 实为列举整个父目录)。
        SftpEntry? entry = await client.GetEntryAsync(sourcePath, cancellationToken).ConfigureAwait(false);

        // 复制一个链接得到的是一个链接(cp -P 口径):同一台服务器上目标文本依旧有效,
        // 也不会因为链接指回祖先目录而无限展开。
        if (entry is { IsSymbolicLink: true, LinkTarget: { Length: > 0 } linkTarget })
        {
            await client.CreateSymbolicLinkAsync(destPath, linkTarget, cancellationToken).ConfigureAwait(false);
            return;
        }
        bool isDir = entry is { IsDirectory: true };

        if (!isDir)
        {
            await CopySingleFileAsync(sessionId, sourcePath, destPath, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await CopyDirectoryAsync(sessionId, sourcePath, destPath, [with(StringComparer.Ordinal)], 0, progress, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 复制单个远端文件:服务端支持 <c>copy-data</c> 时在服务端内复制(数据不出服务器);
    /// 否则经由临时本地文件下载再上传,绝不在内存中缓冲整个文件(复用 DownloadFileAsync/UploadFileAsync 以进行限速与取消)。
    /// </summary>
    private async Task CopySingleFileAsync(
        Guid sessionId,
        string sourcePath,
        string destPath,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (await TryCopyOnServerAsync(sessionId, sourcePath, destPath, progress, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "VelaShell", "copy");
        Directory.CreateDirectory(tempDir);
        string tempPath = Path.Combine(tempDir, Guid.NewGuid().ToString("N"));

        try
        {
            // 远端下载 → 临时文件
            await DownloadFileAsync(sessionId, sourcePath, tempPath, progress, cancellationToken: cancellationToken).ConfigureAwait(false);

            // 临时文件上传 → 远端目标
            await UploadFileAsync(sessionId, tempPath, destPath, progress, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* 尽力而为 */ }
        }
    }

    /// <summary>
    /// 同一台服务器上在服务端内复制(SFTP 的 <c>copy-data</c>):省掉双倍的网络流量与本机的临时文件,
    /// 几 GB 的文件从几分钟变成几秒。服务端不支持时返回 false,由调用方走下载再上传。
    /// </summary>
    /// <remarks>
    /// 进度照常按字节报(库按段报,每段默认 64 MiB)。保留时间戳(设置 → 文件传输)时把目标的修改时间设成源的 ——
    /// 下载再上传那条路本来就是这个结果,服务端内复制不该悄悄换成「现在」。尽力而为,设不上不算复制失败。
    /// </remarks>
    private async Task<bool> TryCopyOnServerAsync(
        Guid sessionId,
        string sourcePath,
        string destPath,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        ISftpClientWrapper client = await GetTransferClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!client.SupportsServerCopy)
        {
            return false;
        }

        SftpEntry? source = await client.GetEntryAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        long total = source?.Length ?? 0;
        var reporter = new TransferProgressThrottle(progress, GetUnixFileName(destPath), total);
        Action<ulong>? onBytes = reporter.IsEnabled ? bytes => reporter.Report((long)bytes) : null;

        await client.CopyOnServerAsync(sourcePath, destPath, onBytes, cancellationToken).ConfigureAwait(false);
        reporter.ReportFinal(total);

        (_, _, bool preserveTimestamps, _) = await GetTransferTuningAsync().ConfigureAwait(false);
        if (preserveTimestamps && source is { LastWriteTime: var mtime } && mtime != default)
        {
            try
            {
                await client.SetLastWriteTimeAsync(destPath, new DateTimeOffset(mtime), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 时间戳只是尽力而为(理由同 UploadFileAsync)。
            }
        }
        return true;
    }

    /// <summary>
    /// 递归复制远端目录,通过已访问路径集合做循环检测,
    /// 并设置深度上限以防止符号链接环导致的栈溢出。
    /// </summary>
    private async Task CopyDirectoryAsync(
        Guid sessionId,
        string sourcePath,
        string destPath,
        HashSet<string> visited,
        int depth,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        const int maxDepth = 64;
        if (depth > maxDepth)
        {
            throw new InvalidOperationException($"Copy depth exceeded {maxDepth} levels — possible symlink cycle at {sourcePath}");
        }

        string canonical = sourcePath.TrimEnd('/');
        if (!visited.Add(canonical))
        {
            throw new InvalidOperationException($"Cycle detected copying {sourcePath} — a directory contains a link to itself.");
        }

        ISftpClientWrapper client = await GetTransferClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await EnsureDirectoryAsync(sessionId, destPath, cancellationToken).ConfigureAwait(false);

        IEnumerable<SftpEntry> children = await client.ListDirectoryAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        foreach (SftpEntry child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (child.Name is "." or "..") continue;

            string childSource = CombineUnixPath(sourcePath, child.Name);
            string childDest = CombineUnixPath(destPath, child.Name);

            // 子项里的链接原样重建为链接,不展开(理由同 CopyAsync 顶层)。
            // 读不到目标文本的链接才退回旧行为按内容复制,由上面的环检测与深度上限兜底。
            if (child is { IsSymbolicLink: true, LinkTarget: { Length: > 0 } linkTarget })
            {
                await client.CreateSymbolicLinkAsync(childDest, linkTarget, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (child.IsDirectory)
            {
                await CopyDirectoryAsync(sessionId, childSource, childDest, visited, depth + 1, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await CopySingleFileAsync(sessionId, childSource, childDest, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>以三位八进制模式(000-777)设置远端文件/目录的权限。</summary>
    public async Task SetPermissionsAsync(Guid sessionId, string remotePath, short octalMode, CancellationToken cancellationToken = default)
    {
        if (octalMode < 0 || octalMode > 777 || octalMode % 10 > 7 || (octalMode / 10) % 10 > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(octalMode), octalMode, @"Mode must be three octal digits (000-777).");
        }
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await client.ChangePermissionsAsync(remotePath, octalMode, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// 改远端条目的属主/属组(SFTP setstat 的 uid/gid)。SFTP 只认数字 id,名称按会话缓存的 passwd/group 表翻译
    /// (与列目录显示名称是同一张表),表里没有的名字才当数字读;两样都不是就报错,不发请求。
    /// </summary>
    public async Task SetOwnerAsync(Guid sessionId, string remotePath, string? owner, string? group, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        if (owner is null && group is null)
        {
            return;
        }
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        RemoteIdentityMap identities = await _identities.GetAsync(sessionId).ConfigureAwait(false);
        int? uid = null;
        int? gid = null;

        // 不带 paramName:ArgumentException 会把「(Parameter 'owner')」拼进 Message,而错误条显示的就是 Message。
        if (owner is not null)
        {
            string text = owner.Trim();
            uid = text.Length > 0 && identities.TryResolveUser(text, out int resolved)
                ? resolved
                : throw new ArgumentException(Strings.Format("SftpSvc_UnknownUser", text));
        }
        if (group is not null)
        {
            string text = group.Trim();
            gid = text.Length > 0 && identities.TryResolveGroup(text, out int resolved)
                ? resolved
                : throw new ArgumentException(Strings.Format("SftpSvc_UnknownGroup", text));
        }
        await client.ChangeOwnerAsync(remotePath, uid, gid, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 可选的用户名与组名取自同一张查表缓存,不额外往返。查不到表时两张列表都是空的 ——
    /// SFTP 照样改得了属主,只是只能填数字 id,所以返回空列表而不是 null。
    /// </summary>
    public async Task<RemoteOwnerChoices?> GetOwnerChoicesAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        RemoteIdentityMap identities = await _identities.GetAsync(sessionId).WaitAsync(cancellationToken).ConfigureAwait(false);
        return new(
            [.. identities.UserIds.Keys.Order(StringComparer.Ordinal)],
            [.. identities.GroupIds.Keys.Order(StringComparer.Ordinal)]);
    }

    /// <summary>设置远端条目的修改时间(SFTP setstat,与上传收尾的「保留时间戳」同一条路)。</summary>
    public async Task SetLastWriteTimeAsync(Guid sessionId, string remotePath, DateTime lastWriteTimeUtc, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        DateTime utc = lastWriteTimeUtc.Kind == DateTimeKind.Utc ? lastWriteTimeUtc : lastWriteTimeUtc.ToUniversalTime();
        await client.SetLastWriteTimeAsync(remotePath, new DateTimeOffset(utc), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 经 SSH exec 通道在服务器上跑 <c>sha256sum</c>(或 <c>shasum -a 256</c>)批量算摘要。
    /// exec 被禁(<c>ForceCommand internal-sftp</c> 的纯 SFTP 账号)、主机不是 POSIX、两个工具都没有时抛
    /// <see cref="NotSupportedException" />;命令与输出的细节见 <see cref="RemoteSha256" />。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string?>> ComputeSha256Async(Guid sessionId, IReadOnlyList<string> remotePaths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remotePaths);
        if (remotePaths.Count == 0)
        {
            return new Dictionary<string, string?>();
        }
        ISshClientWrapper client = _connectionService.GetClient(sessionId)
                                   ?? throw new NotSupportedException("This session has no SSH exec channel.");
        // 字节与 SFTP 看到的不是同一串的路径(非 UTF-8 会话里的中文名、UTF-8 会话里解不开的字节)不进命令行:
        // 进去了 sha256sum 要么找不到,要么算的是一个 UTF-8 字节的同名文件。它们记「没算出来」(null),由调用方退回读流比对。
        // 解析仍按全部请求的路径来:没进命令行的那些在输出里不会出现,照样落成 null。
        List<string> passable = [.. remotePaths.Where(path => CanPassPathToCommand(sessionId, path))];
        if (passable.Count == 0)
        {
            var none = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (string path in remotePaths)
            {
                none[path] = null;
            }
            return none;
        }
        RemoteCommandResult result;
        try
        {
            result = await client.RunCommandDetailedAsync(RemoteSha256.BuildCommand(passable), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new NotSupportedException($"Remote SHA-256 is unavailable: {ex.Message}", ex);
        }
        return RemoteSha256.Parse(result.StandardOutput, result.StandardError, result.ExitCode, remotePaths);
    }

    /// <summary>在远端创建符号链接;目标文本原样写入(ln -s 语义)。</summary>
    public async Task CreateSymbolicLinkAsync(Guid sessionId, string linkPath, string targetPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await client.CreateSymbolicLinkAsync(linkPath, targetPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>打开远端文件的只读流(顺序读取,调用方负责释放)。</summary>
    public async Task<Stream> OpenReadAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetTransferClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return await client.OpenAsync(remotePath, FileMode.Open, FileAccess.Read, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>获取远端指定路径文件/目录的详细信息;路径不存在时抛出 <see cref="FileNotFoundException" />。</summary>
    public async Task<RemoteFileInfo> GetFileInfoAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);

        // 一次 stat 即可。旧实现是列举整个父目录再从中挑一条:父目录上万条时代价极高,
        // 而且每次下载都会先走这里 —— 批量传 N 个文件就是 N 次全目录列举。
        SftpEntry file = await client.GetEntryAsync(remotePath, cancellationToken).ConfigureAwait(false)
                         ?? throw new FileNotFoundException($"File not found: {remotePath}");
        RemoteIdentityMap identities = await _identities.GetAsync(sessionId).ConfigureAwait(false);
        return MapToRemoteFileInfo(file, identities);
    }

    /// <summary>判断远端路径是否存在。</summary>
    public async Task<bool> ExistsAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return await client.ExistsAsync(remotePath, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RemoteSpaceInfo?> GetSpaceAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return await client.GetSpaceAsync(remotePath, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string?> ExpandPathAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return await client.ExpandPathAsync(remotePath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>返回该会话 SFTP 客户端的当前工作目录。</summary>
    public async Task<string> GetWorkingDirectoryAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return client.WorkingDirectory;
    }

    /// <summary>关闭并清理指定会话缓存的 SFTP 客户端与单飞闸,断开连接并释放资源。</summary>
    public async Task CloseSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (_clientGates.TryRemove(sessionId, out SemaphoreSlim? gate))
        {
            gate.Dispose();
        }

        // 在下面的早退之前丢弃:即使本会话从未建过 SFTP 客户端,查表缓存也可能已存在。
        _identities.Invalidate(sessionId);
        _singleChannelSessions.TryRemove(sessionId, out _);
        foreach ((Guid Session, string Path) key in _durableUploads.Keys.Where(k => k.Session == sessionId))
        {
            _durableUploads.TryRemove(key, out _);
        }

        // 这里原先要 Task.Run 把释放甩到线程池 —— 因为那时释放是同步阻塞的,
        // 在调用线程上做会卡住关标签页这个动作。现在释放本身就是异步的,直接 await。
        if (_transferClients.TryRemove(sessionId, out ISftpClientWrapper? transfer))
        {
            await DisposeQuietlyAsync(transfer).ConfigureAwait(false);
        }
        if (_sftpClients.TryRemove(sessionId, out ISftpClientWrapper? client))
        {
            await DisposeQuietlyAsync(client).ConfigureAwait(false);
        }
    }

    /// <summary>尽力拆解一个 SFTP 客户端;标签页已经不在了,失败没有补救动作。</summary>
    private static async ValueTask DisposeQuietlyAsync(ISftpClientWrapper client)
    {
        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // 尽力拆解。
        }
    }

    /// <summary>断开并释放所有缓存的 SFTP 客户端,尽力清理全部会话资源。</summary>
    public async ValueTask DisposeAsync()
    {
        // 先退订:连接服务比本服务活得久时,悬着的委托会把已释放的实例一直吊在内存里。
        _connectionService.SessionDisconnected -= OnSshSessionDisconnected;
        foreach (KeyValuePair<Guid, ISftpClientWrapper> kvp in _transferClients)
        {
            await DisposeQuietlyAsync(kvp.Value).ConfigureAwait(false);
        }
        _transferClients.Clear();
        foreach (KeyValuePair<Guid, ISftpClientWrapper> kvp in _sftpClients)
        {
            await DisposeQuietlyAsync(kvp.Value).ConfigureAwait(false);
        }
        _sftpClients.Clear();
        GC.SuppressFinalize(this);
    }

    /// <summary>大文件传输用的本地流缓冲区(1MB):把 GB 级文件的系统调用次数压到千级。</summary>
    private const int LocalStreamBufferSize = 1024 * 1024;

    /// <summary>续传前比对的尾部字节数:够识别"同名不同文件",又只值一次往返。</summary>
    private const int ResumeVerifyBytes = 64 * 1024;

    /// <summary>
    /// 核实一次上传的续传起点。
    /// <para>
    /// 调用方手里那个偏移是更早之前探测远端大小得到的,从探测到真正开始写之间隔着冲突对话框
    /// 和传输队列,远端文件完全可能已经变了 —— 直接照着旧偏移追加会静默产出损坏文件。
    /// 所以它只用来决定"要不要核实"(调用方的 <c>resumeOffset &gt; 0</c>),不传进来:
    /// 这里一律以"此刻的远端长度"为准,并比对尾部字节确认远端那半截确实是本地文件的前缀。
    /// </para>
    /// </summary>
    /// <returns>经核实的续传偏移量;返回 0 表示没有可续的半截,应整份重传。</returns>
    private static async Task<long> ResolveUploadResumeAsync(ISftpClientWrapper client,
        string remotePath,
        string localPath,
        long localLength,
        long? durable,
        CancellationToken cancellationToken)
    {
        await using Stream local = OpenLocalRead(localPath);
        return await ResolveUploadResumeAsync(client, remotePath, local, localLength, durable, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 同上,只是源换成了调用方给的可 Seek 的流(双栏远程之间的中转:源是另一台机器上的文件)。
    /// 源流由调用方负责释放;这里会移动它的位置,调用方按返回值重新定位。
    /// </summary>
    /// <remarks>
    /// <c>durable</c> 是上一次被打断时库交出来的精确续传点;给了就从它续(不超过此刻的远端长度),不必盲退一个在途窗口。
    /// </remarks>
    private static async Task<long> ResolveUploadResumeAsync(ISftpClientWrapper client,
        string remotePath,
        Stream source,
        long sourceLength,
        long? durable,
        CancellationToken cancellationToken)
    {
        long localLength = sourceLength;
        long remoteLength = await client.GetFileSizeAsync(remotePath, cancellationToken).ConfigureAwait(false);

        // 远端不存在/为空,或已不短于本地:都不构成"传了一半",退化为整份重传(覆盖写)。
        if (remoteLength <= 0 || remoteLength >= localLength)
        {
            return 0;
        }

        // 上一次被打断时记下了精确续传点(从开头起连续确认落盘的字节数):从它续,一个字节都不用重传。
        // 否则只能回退一整个在途写入窗口:文件长度只是"已确认的最高偏移",它之前可能还留着未落盘的空洞
        // (见 ISftpClientWrapper.ResumeSafetyMargin)。不回退的话尾部比对会落在已写入的那段上
        // 顺利通过,却从一个带洞的位置接着传 —— 那正是"续传出来的文件是坏的"的成因。
        long candidate = durable is { } exact
            ? Math.Min(exact, remoteLength)
            : remoteLength - client.ResumeSafetyMargin;
        if (candidate <= 0)
        {
            return 0;
        }
        await using Stream remote = await client.OpenAsync(remotePath, FileMode.Open, FileAccess.Read, cancellationToken).ConfigureAwait(false);
        if (!await TailMatchesAsync(remote, source, candidate, cancellationToken).ConfigureAwait(false))
        {
            throw new VelaSftpResumeMismatchException(Strings.Format("SftpSvc_ResumeUploadMismatch", remotePath));
        }
        return candidate;
    }

    /// <summary>取走这个路径上一次被打断时记下的续传点(只用一次:之后的上传各记各的)。</summary>
    private long? TakeDurableHint(Guid sessionId, string remotePath) =>
        _durableUploads.TryRemove((sessionId, remotePath), out long durable) ? durable : null;

    /// <summary>记下这次被打断的上传精确落盘到了哪里,供下一次续传用。</summary>
    private void RememberDurable(Guid sessionId, string remotePath, Exception failure)
    {
        // 源先失败时主异常不再被目标收尾异常替换,但目标的精确确认水位仍要留给续传。
        long? durable = VelaSftpUploadFailureDetails.Get(failure)?.DurableLength
            ?? (failure as VelaSftpTransferInterruptedException)?.DurableLength;
        if (durable is { } length)
        {
            _durableUploads[(sessionId, remotePath)] = length;
        }
    }

    /// <summary>
    /// 核实一次下载的续传起点,理由同 <see cref="ResolveUploadResumeAsync(ISftpClientWrapper, string, string, long, long?, CancellationToken)" />:
    /// 以"此刻本地文件的实际长度"为准,并比对尾部确认本地那半截确实是远端文件的前缀。
    /// </summary>
    /// <returns>经核实的续传偏移量;返回 0 表示应整份重下(覆盖本地残留)。</returns>
    private static async Task<long> ResolveDownloadResumeAsync(ISftpClientWrapper client,
        string remotePath,
        string localPath,
        long remoteLength,
        CancellationToken cancellationToken)
    {
        var local = new FileInfo(localPath);
        if (!local.Exists)
        {
            return 0;
        }
        long localLength = local.Length;
        if (localLength <= 0 || localLength >= remoteLength)
        {
            return 0;
        }

        // 同样回退在途写入窗口:下载侧的本地文件也是底层库并发写出来的,中断后尾部一样可能有空洞。
        long candidate = localLength - client.ResumeSafetyMargin;
        if (candidate <= 0)
        {
            return 0;
        }
        await using Stream remoteStream = await client.OpenAsync(remotePath, FileMode.Open, FileAccess.Read, cancellationToken).ConfigureAwait(false);
        await using Stream localStream = OpenLocalRead(localPath);
        if (!await TailMatchesAsync(remoteStream, localStream, candidate, cancellationToken).ConfigureAwait(false))
        {
            throw new VelaSftpResumeMismatchException(Strings.Format("SftpSvc_ResumeDownloadMismatch", remotePath));
        }
        return candidate;
    }

    /// <summary>
    /// 比对两个流在 <c>[offset - N, offset)</c> 区间的内容是否一致(N 最多
    /// <see cref="ResumeVerifyBytes" />)。两个流都会被定位,调用方不应依赖其原位置。
    /// </summary>
    private static async Task<bool> TailMatchesAsync(Stream first, Stream second, long offset, CancellationToken cancellationToken)
    {
        // 契约兜底:不可 Seek 的流会让下面的定位抛出底层库的裸 NotSupportedException,
        // 排查时完全看不出是"打开选项没开 Seekable"。这里提前把话说清楚。
        // (见 ISftpClientWrapper.OpenAsync 的实现必须返回可 Seek 的流。)
        if (!first.CanSeek || !second.CanSeek)
        {
            throw new InvalidOperationException(
                "Resume verification requires seekable streams; ISftpClientWrapper.OpenAsync must return a stream with CanSeek == true.");
        }
        int length = (int)Math.Min(ResumeVerifyBytes, offset);
        if (length <= 0)
        {
            return true;
        }
        long start = offset - length;
        byte[] firstBuffer = ArrayPool<byte>.Shared.Rent(length);
        byte[] secondBuffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            first.Seek(start, SeekOrigin.Begin);
            await first.ReadExactlyAsync(firstBuffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            second.Seek(start, SeekOrigin.Begin);
            await second.ReadExactlyAsync(secondBuffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            return firstBuffer.AsSpan(0, length).SequenceEqual(secondBuffer.AsSpan(0, length));
        }
        catch (EndOfStreamException)
        {
            // 说明某一侧在核实期间又变短了 —— 同样属于"不可信的续传起点"。
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(firstBuffer);
            ArrayPool<byte>.Shared.Return(secondBuffer);
        }
    }

    /// <summary>
    /// 打开本地文件供上传读取。
    /// <para>
    /// 必须用 <see cref="FileOptions.Asynchronous" />:<c>File.OpenRead</c> 返回的是同步句柄,
    /// 其上的每次 <c>ReadAsync</c> 都会真正阻塞一个线程池线程。GB 级文件意味着几十万次这样的
    /// 阻塞读,线程池只能靠每秒注入一两个线程来补偿,表现就是传输跑一阵后长时间停顿再恢复。
    /// <see cref="FileOptions.SequentialScan" /> 则让系统预读策略匹配顺序整文件读取。
    /// </para>
    /// </summary>
    private static FileStream OpenLocalRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, LocalStreamBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    /// <summary>打开本地文件供下载写入(截断已有内容);异步句柄的理由同 <see cref="OpenLocalRead" />。</summary>
    private static FileStream OpenLocalWrite(string path) =>
        new(path, FileMode.Create, FileAccess.Write, FileShare.None, LocalStreamBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    /// <summary>带宽限制(设置 → 文件传输):返回字节/秒,0 = 不限速。</summary>
    private async Task<(long UploadBps, long DownloadBps, bool PreserveTimestamps, bool FsyncAfterUpload)> GetTransferTuningAsync()
    {
        if (_settingsService is null)
        {
            return (0, 0, true, false);
        }
        try
        {
            TransferOptions t = (await _settingsService.GetSnapshotAsync().ConfigureAwait(false)).Transfer;
            long up = t.BandwidthLimitEnabled ? (long)Math.Max(0, t.UploadLimitMBps) * 1024 * 1024 : 0;
            long down = t.BandwidthLimitEnabled ? (long)Math.Max(0, t.DownloadLimitMBps) * 1024 * 1024 : 0;
            return (up, down, t.PreserveTimestamps, t.FsyncAfterUpload);
        }
        catch
        {
            return (0, 0, true, false);
        }
    }

    /// <summary>
    /// 目录树删除的快路径(#474):在 SSH 的 exec 通道上跑一条 <c>rm -rf</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只在**真的能跑命令**的会话上成立。独立 SFTP 配置、FTP、S3 等协议走的是另外的
    /// <c>ISftpService</c> 实现或没有 <see cref="ISshClientWrapper" />,
    /// <see cref="ISshConnectionService.GetClient" /> 返回 null,这里直接返回 false。
    /// </para>
    /// <para>
    /// 退非零码即判失败并回退 —— 包括"删了一半卡在某个只读文件上"这种半成功:
    /// 剩下的由 SFTP 递归接着删,删不动时抛出的是那条路径上的真实错误,
    /// 而不是一句没有上下文的 "rm: exit 1"。
    /// </para>
    /// <para>
    /// 进度只报一个不确定态的起点:远端一条命令跑完才返回,中途无从计数。
    /// </para>
    /// </remarks>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="remotePath">要删除的远端目录的绝对路径。</param>
    /// <param name="progress">删除进度回报。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>true = 已经删完,调用方不必再走 SFTP 递归。</returns>
    private async Task<bool> TryDeleteDirectoryByCommandAsync(Guid sessionId,
        string remotePath,
        IProgress<SftpDeleteProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!IsCommandDeletablePath(remotePath)
            || !CanPassPathToCommand(sessionId, remotePath)
            || !await IsRecursiveDeleteCommandEnabledAsync().ConfigureAwait(false))
        {
            return false;
        }
        ISshClientWrapper? ssh = _connectionService.GetClient(sessionId);
        if (ssh is null)
        {
            return false;
        }
        try
        {
            // 0 / 0 = 不确定态:这条路上没有逐条进度,界面据此显示转圈而不是一根不动的进度条。
            progress?.Report(new(0, 0, remotePath));
            RemoteCommandResult result = await ssh
                .RunCommandDetailedAsync($"rm -rf -- {QuoteForShell(remotePath)}", cancellationToken)
                .ConfigureAwait(false);
            return result.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            // 取消要如实往上抛:调用方(文件浏览器)据此重新列目录,
            // 而不是把它当成"快路径不可用"再默默用 SFTP 把剩下的删完。
            throw;
        }
        catch
        {
            // 没有 exec 通道、通道中途断开、非 Unix 主机:回退 SFTP 递归。
            return false;
        }
    }

    /// <summary>设置里是否允许用 <c>rm -rf</c> 删目录;读不到设置时按默认(允许)处理。</summary>
    private async Task<bool> IsRecursiveDeleteCommandEnabledAsync()
    {
        if (_settingsService is null)
        {
            return true;
        }
        try
        {
            return (await _settingsService.GetSnapshotAsync().ConfigureAwait(false)).Transfer.UseRecursiveDeleteCommand;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// 这条路径能不能交给 <c>rm -rf</c>:必须是绝对路径,且不能是根。
    /// </summary>
    /// <remarks>
    /// 根的那一刀挡的是 <c>rm -rf -- /</c>。相对路径同样拒绝:exec 通道的工作目录是登录目录,
    /// 与文件浏览器当前所在的目录没有任何关系 —— 同一个相对路径在两条通道上指的不是一个东西。
    /// (文件浏览器本就只传绝对路径,这里是不依赖调用方自觉的那道闸。)
    /// </remarks>
    private static bool IsCommandDeletablePath(string remotePath) =>
        remotePath.StartsWith('/') && remotePath.TrimEnd('/').Length > 0;

    /// <summary>exec 命令行固定按 UTF-8 编码;编不出(落单代理项)就抛,不悄悄换成 EF BF BD。</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// 这条 SFTP 路径能不能原样写进 exec 命令:命令行到远端是 UTF-8 字节,只有那串字节与 SFTP 发给服务器的
    /// (按会话的文件名编码,见 <see cref="RemoteFileNameEncoding" />)一模一样,两条通道指的才是同一个文件。
    /// </summary>
    /// <remarks>
    /// <para>
    /// GBK / Big5 / Shift_JIS 会话里 SFTP 按会话字符集解名字,<c>/data/报表</c> 在服务器上是 GBK 字节;塞进 UTF-8 的 exec 命令,
    /// <c>rm -rf</c> 拿到的是另一串字节 —— 多半不存在(<c>rm -f</c> 照样退 0,删除「成功」了个寂寞),迁移过的混编码服务器上
    /// 还可能正好有一个 UTF-8 字节的同名目录,删掉的是它(复查 file-panes F2)。纯 ASCII 的路径在这几种字符集里字节相同,照走快路径。
    /// </para>
    /// <para>
    /// UTF-8 会话里 SFTP 把解不开的字节记成落单代理项 U+DC80–DCFF,编进 UTF-8 命令行就成了 EF BF BD,同样不是那个文件。
    /// </para>
    /// </remarks>
    private bool CanPassPathToCommand(Guid sessionId, string remotePath)
    {
        byte[] utf8;
        try
        {
            utf8 = StrictUtf8.GetBytes(remotePath);
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
        return RemoteFileNameEncoding.Resolve(_connectionService.GetSession(sessionId)?.ConnectionInfo.FileNameEncoding) is not { } names
               || names.GetBytes(remotePath).AsSpan().SequenceEqual(utf8);
    }

    /// <summary>
    /// 把路径包成一个 POSIX shell 单引号字符串:单引号内除了单引号自身之外一切都是字面量,
    /// 于是空格、<c>$</c>、<c>;</c>、换行、通配符全部失去特殊含义。路径里的每个单引号
    /// 按 <c>'\''</c> 的老办法拼接(收尾、转义一个、再开头)。
    /// </summary>
    private static string QuoteForShell(string value) =>
        $"'{value.Replace("'", @"'\''", StringComparison.Ordinal)}'";

    /// <summary>异步递归的已删除计数(async 方法不允许 ref 参数)。</summary>
    private sealed class DeleteCounter
    {
        public int Deleted;
    }

    /// <summary>
    /// 深度优先删除:先移除目录的子项再移除目录自身,因为 SFTP 的 <c>rmdir</c> 仅对空目录成功。
    /// 每移除一个条目回报一次进度。
    /// </summary>
    private static async Task<int> CountEntriesAsync(ISftpClientWrapper client, string path, bool isDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!isDirectory)
        {
            return 1;
        }
        int total = 1;
        IEnumerable<SftpEntry> children = await client.ListDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
        foreach (SftpEntry child in children)
        {
            if (child.Name is "." or "..")
            {
                continue;
            }
            total += await CountEntriesAsync(
                client, child.FullName, await ShouldDescendAsync(client, child, cancellationToken).ConfigureAwait(false), cancellationToken)
                .ConfigureAwait(false);
        }
        return total;
    }

    private static async Task DeleteEntryAsync(ISftpClientWrapper client,
        string path,
        bool isDirectory,
        int total,
        DeleteCounter counter,
        IProgress<SftpDeleteProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isDirectory)
        {
            IEnumerable<SftpEntry> children = await client.ListDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
            foreach (SftpEntry child in children)
            {
                if (child.Name is "." or "..")
                {
                    continue;
                }
                bool descend = await ShouldDescendAsync(client, child, cancellationToken).ConfigureAwait(false);
                await DeleteEntryAsync(client, child.FullName, descend, total, counter, progress, cancellationToken).ConfigureAwait(false);
            }
            await client.DeleteDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // 链接也走这里:SSH_FXP_REMOVE 删的是链接本身,不碰目标。
            await client.DeleteFileAsync(path, cancellationToken).ConfigureAwait(false);
        }
        counter.Deleted++;
        progress?.Report(new(counter.Deleted, total, path));
    }

    /// <summary>
    /// 递归删除/计数时是否要进入该条目:真目录才进,指向目录的链接当叶子
    /// —— 链接可以指回祖先(无限递归),更要命的是进去删掉的是链接目标里的东西。
    /// </summary>
    private static bool IsTraversableDirectory(SftpEntry entry) => entry.IsDirectory && !entry.IsSymbolicLink;

    /// <summary>
    /// 列目录列出来的子项要不要进去:列表说它是真目录,还要用不跟随链接的 stat(<see cref="ISftpClientWrapper.GetEntryAsync" />)
    /// 再确认一次。
    /// </summary>
    /// <remarks>
    /// 「是不是真目录」在列表里只是 READDIR 的说法,而 SFTP 草案没规定那里的属性按 lstat 还是 stat 给。
    /// 某个服务端给的是跟随之后的属性的话,指向目录的链接看起来就是一个真目录 —— 递归删除会进到链接目标里去删,
    /// 链接指回祖先时计数还会无限递归。一个目录多一次往返,换不删错东西;文件不受影响。
    /// </remarks>
    private static async Task<bool> ShouldDescendAsync(ISftpClientWrapper client, SftpEntry child, CancellationToken cancellationToken)
    {
        if (!IsTraversableDirectory(child))
        {
            return false;
        }

        SftpEntry? confirmed = await client.GetEntryAsync(child.FullName, cancellationToken).ConfigureAwait(false);
        return confirmed is not null && IsTraversableDirectory(confirmed);
    }

    private async Task<ISftpClientWrapper> GetOrCreateSftpClientAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (TryGetUsableClient(sessionId, out ISftpClientWrapper? existingClient))
        {
            return existingClient;
        }

        // 单飞:面板加载与文件操作可能并发首次触达同一会话,不加闸时会各自握手出
        // 一条 SFTP 连接(SSH 握手 × N),后到者还会把先到者从字典里顶掉造成泄漏。
        SemaphoreSlim gate = _clientGates.GetOrAdd(sessionId, static _ => new(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetUsableClient(sessionId, out existingClient))
            {
                return existingClient;
            }
            SshSession? session = _connectionService.GetSession(sessionId) ?? throw new RemoteSessionUnavailableException($"Session {sessionId} not found");
            if (session.Status != SessionStatus.Connected)
            {
                throw new RemoteSessionUnavailableException($"Session {sessionId} is not connected");
            }
            if (_sftpClientFactory == null)
            {
                throw new InvalidOperationException("SFTP client factory not configured");
            }
            ISftpClientWrapper client = _sftpClientFactory(session);
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);

            // 换掉的那个(SFTP 通道已经死了)释放掉:它的通道、句柄表都还挂在连接上。
            if (_sftpClients.TryGetValue(sessionId, out ISftpClientWrapper? stale) && !ReferenceEquals(stale, client))
            {
                await DisposeQuietlyAsync(stale).ConfigureAwait(false);
            }
            _sftpClients[sessionId] = client;
            return client;
        }
        finally
        {
            try
            {
                gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // CloseSessionAsync 与首次创建赛跑时会把闸释放掉;创建结果本身已无所谓。
            }
        }
    }

    /// <summary>传输(上传、下载、远端复制、读流)用的 SFTP 客户端。</summary>
    /// <remarks>
    /// <para>
    /// <b>传输与浏览分开两条 SFTP 通道</b>(同一条 SSH 连接上开第二个 session 通道)。sftp-server 按到达顺序处理请求:
    /// 只有一条通道时,上传大文件约 16 MiB 的 WRITE 在途,这时点开一个目录,STAT / READDIR 就排在那 16 MiB 后面 ——
    /// 10 Mbit/s 上行时是十几秒的界面卡顿。WinSCP、FileZilla 也是传输单开连接。
    /// </para>
    /// <para>
    /// 开不出第二条(服务端 <c>MaxSessions</c> 太小、管理上禁止)就退回浏览用的那一条,并记住这个会话,之后不再试;
    /// 传输照常,只是又会挡住浏览。会话关闭时一并清掉。
    /// </para>
    /// </remarks>
    private async Task<ISftpClientWrapper> GetTransferClientAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (_singleChannelSessions.ContainsKey(sessionId))
        {
            return await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        if (TryGetUsableClient(_transferClients, sessionId, out ISftpClientWrapper? existing))
        {
            return existing;
        }

        // 先确保浏览那一条在:会话状态、工厂这些前置条件由它报;传输那一条开不成时也有地方退。
        ISftpClientWrapper interactive = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);

        SemaphoreSlim gate = _clientGates.GetOrAdd(sessionId, static _ => new(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetUsableClient(_transferClients, sessionId, out existing))
            {
                return existing;
            }

            SshSession session = _connectionService.GetSession(sessionId)
                ?? throw new RemoteSessionUnavailableException($"Session {sessionId} not found");
            ISftpClientWrapper client = _sftpClientFactory!(session);
            try
            {
                await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                await DisposeQuietlyAsync(client).ConfigureAwait(false);
                _singleChannelSessions[sessionId] = true;
                return interactive;
            }

            if (_transferClients.TryGetValue(sessionId, out ISftpClientWrapper? stale) && !ReferenceEquals(stale, client))
            {
                await DisposeQuietlyAsync(stale).ConfigureAwait(false);
            }
            _transferClients[sessionId] = client;
            return client;
        }
        finally
        {
            try
            {
                gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // CloseSessionAsync 与创建赛跑时会把闸释放掉;创建结果本身已无所谓。
            }
        }
    }

    private bool TryGetUsableClient(Guid sessionId, [NotNullWhen(true)] out ISftpClientWrapper? client) =>
        TryGetUsableClient(_sftpClients, sessionId, out client);

    private static bool TryGetUsableClient(
        ConcurrentDictionary<Guid, ISftpClientWrapper> clients, Guid sessionId, [NotNullWhen(true)] out ISftpClientWrapper? client)
    {
        if (clients.TryGetValue(sessionId, out client))
        {
            try
            {
                return client.IsConnected;
            }
            catch (ObjectDisposedException)
            {
                // 会话关闭把客户端释放了,当作缺失重建。
            }
        }
        client = null;
        return false;
    }

    private static RemoteFileInfo MapToRemoteFileInfo(SftpEntry file, RemoteIdentityMap identities)
    {
        return new()
        {
            Name = file.Name,
            FullPath = file.FullName,
            Size = file.Length,
            Permissions = FormatPermissions(file),
            IsDirectory = file.IsDirectory,
            IsSymbolicLink = file.IsSymbolicLink,
            LinkTarget = file.LinkTarget,
            LastModified = file.LastWriteTime,
            Owner = identities.UserName(file.UserId),
            Group = identities.GroupName(file.GroupId)
        };
    }

    private static string CombineUnixPath(string directory, string name) =>
        directory == "/" ? "/" + name : directory.TrimEnd('/') + "/" + name;

    private static string GetUnixFileName(string remotePath)
    {
        int lastSlash = remotePath.LastIndexOf('/');
        return lastSlash >= 0 ? remotePath[(lastSlash + 1)..] : remotePath;
    }

    private static string FormatPermissions(SftpEntry file)
    {
        // 类型位与 ls -l 一致:链接标 l(其后的 rwx 取自目标 —— chmod 本来就作用在目标上)。
        string perms = file.IsSymbolicLink ? "l" : file.IsDirectory ? "d" : "-";
        perms += file.OwnerCanRead ? "r" : "-";
        perms += file.OwnerCanWrite ? "w" : "-";
        perms += file.OwnerCanExecute ? "x" : "-";
        perms += file.GroupCanRead ? "r" : "-";
        perms += file.GroupCanWrite ? "w" : "-";
        perms += file.GroupCanExecute ? "x" : "-";
        perms += file.OthersCanRead ? "r" : "-";
        perms += file.OthersCanWrite ? "w" : "-";
        perms += file.OthersCanExecute ? "x" : "-";
        return perms;
    }
}
