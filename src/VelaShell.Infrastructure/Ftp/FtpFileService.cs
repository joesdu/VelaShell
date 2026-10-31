using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentFTP;
using FluentFTP.Exceptions;
using FluentFTP.Proxy.AsyncProxy;
using VelaShell.Core.Ftp;
using VelaShell.Core.Models;
using VelaShell.Core.Net;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Net;
using CoreDataMode = VelaShell.Core.Models.FtpDataConnectionMode;
using CoreEncryption = VelaShell.Core.Models.FtpEncryptionMode;

namespace VelaShell.Infrastructure.Ftp;

/// <summary>
/// FTP / FTPS 的文件服务:同时实现 <see cref="ISftpService" />(文件操作,与协议无关的那套契约)
/// 与 <see cref="IFtpSessionService" />(会话生命周期)。
/// <para>
/// 接缝选在 <see cref="ISftpService" /> 而不是 <c>ISftpClientWrapper</c>,原因有二
/// (详见 docs/FTP客户端可行性调研.md §一):
/// 其一,上层真正消费的 <see cref="RemoteFileInfo" /> 里权限/属主/属组都是**字符串**,
/// 正是 FTP 的 LIST/MLSD 能给的,不必绕 UID/GID 与 SSH exec 查表;
/// 其二,<c>ISftpClientWrapper.OpenAsync</c> 硬性要求返回**可 Seek** 的流,
/// 而 FluentFTP 的数据流 <c>CanSeek == false</c>、<c>Seek()</c> 直接抛异常,根本满足不了。
/// </para>
/// </summary>
public sealed class FtpFileService(IProxyResolver? proxyResolver = null) : ISftpService, IFtpSessionService, RemoteRelay.IFtpSourceRejectionSignal
{
    private readonly ConcurrentDictionary<Guid, FtpConnectionPool> _sessions = new();

    /// <inheritdoc />
    public event EventHandler<FtpSessionStateChange>? SessionStateChanged;

    /// <inheritdoc />
    public async Task<Guid> OpenSessionAsync(FtpConnectionInfo info, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        var probe = new CertificateProbe(info.Settings.TrustedCertificateThumbprint);
        var routeProbe = new RouteProbe();
        var pool = new FtpConnectionPool(info, token => CreateConnectedClientAsync(info, probe, proxyResolver, routeProbe, token));
        try
        {
            // 第一次租借即完成 TCP 连接、TLS 握手与登录 —— 把失败暴露在「打开会话」这一步,
            // 而不是等用户点开目录才炸。
            using FtpConnectionPool.Lease lease = await pool.RentAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await pool.DisposeAsync().ConfigureAwait(false);
            // 代理配错了(显式开了代理但 host/port 不完整、或凭据超长):这条错误本身
            // 已经说清楚了,别让下面的 Translate 把它翻成笼统的“连接丢失”。
            if (ex is ProxyMisconfiguredException)
            {
                throw;
            }
            // 证书没过校验时,FluentFTP 抛出来的是一个笼统的连接失败;这里换成带指纹的专用异常,
            // 上层才能弹出「是否信任该证书」并把指纹写回配置。
            if (probe.Failure is { } failure)
            {
                throw new VelaFtpCertificateException(
                    $"The server certificate for {info.Host} is not trusted ({failure.PolicyErrors}).",
                    failure.Thumbprint, failure.Subject, failure.Issuer, failure.ExpiresOn, failure.PolicyErrors, ex);
            }
            throw WithProxyContext(FluentFtpInterop.Translate(ex, "connect"), info, routeProbe.Used);
        }
        var sessionId = Guid.NewGuid();
        _sessions[sessionId] = pool;
        SessionStateChanged?.Invoke(this, new(sessionId, FtpSessionState.Connected));
        return sessionId;
    }

    /// <inheritdoc />
    public bool OwnsSession(Guid sessionId) => _sessions.ContainsKey(sessionId);

    /// <inheritdoc />
    public async Task CloseSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (_sessions.TryRemove(sessionId, out FtpConnectionPool? pool))
        {
            await pool.DisposeAsync().ConfigureAwait(false);
            SessionStateChanged?.Invoke(this, new(sessionId, FtpSessionState.Closed));
        }
    }

    /// <inheritdoc />
    public async Task<List<RemoteFileInfo>> ListDirectoryAsync(Guid sessionId, string path, CancellationToken cancellationToken = default)
    {
        using FtpConnectionPool.Lease lease = await RentAsync(sessionId, cancellationToken).ConfigureAwait(false);
        try
        {
            FtpListItem[] items = await lease.Client.GetListing(NormalizePath(path), cancellationToken).ConfigureAwait(false);
            var result = new List<RemoteFileInfo>(items.Length);
            foreach (FtpListItem item in items)
            {
                if (item is not null)
                {
                    result.Add(await MapAsync(lease.Client, item, cancellationToken).ConfigureAwait(false));
                }
            }
            return result;
        }
        catch (Exception ex)
        {
            throw Fault(sessionId, ex, "list directory");
        }
    }

    /// <inheritdoc />
    public async Task UploadFileAsync(Guid sessionId,
        string localPath,
        string remotePath,
        IProgress<TransferProgress>? progress = null,
        long resumeOffset = 0,
        CancellationToken cancellationToken = default)
    {
        string fileName = Path.GetFileName(localPath);
        long totalBytes = new FileInfo(localPath).Length;
        // 调用方给的偏移只决定「要不要试着续」(ISftpService 的契约):此刻核实远端那半截确实是本地文件的开头才续,
        // 远端没有可续的半截就整份重传,对不上抛 VelaSftpResumeMismatchException 交回同名冲突策略。
        if (resumeOffset > 0)
        {
            await using FileStream local = File.OpenRead(localPath);
            resumeOffset = await ResolveUploadResumeAsync(sessionId, remotePath, local, totalBytes, cancellationToken).ConfigureAwait(false);
        }
        ReadCountingStream? attempt = null;
        await RunTransferAsync(sessionId, "upload", async client =>
        {
            attempt = null;
            await using FileStream file = File.OpenRead(localPath);
            // 每一次尝试各数各的:只有这一次真从源里读走过字节,才说明数据已经上了数据连接。
            var source = attempt = new ReadCountingStream(file);
            // 续传交给 FluentFTP:Resume 模式下它自己 SIZE 远端、再把本地流 Seek 到同一偏移(起点上面已经核实过)。
            // 不需要 SFTP 那套「回退一个在途写入窗口」—— FTP 单条数据连接顺序写,没有乱序空洞。
            FtpStatus status = await client.UploadStream(
                source,
                NormalizePath(remotePath),
                resumeOffset > 0 ? FtpRemoteExists.Resume : FtpRemoteExists.Overwrite,
                createRemoteDir: false,
                progress: MapProgress(progress, fileName, totalBytes),
                token: cancellationToken).ConfigureAwait(false);
            if (status == FtpStatus.Failed)
            {
                throw new VelaFtpOperationException($"FTP upload of {fileName} failed.");
            }
        }, () => attempt?.BytesRead > 0, replayable: true, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 与 <see cref="UploadFileAsync" /> 同一条 STOR 路径,只是源换成了调用方的流。源往往是另一条连接上的
    /// 远端流(FTP 的数据流不可 Seek),所以包一层报出已知长度的外壳交给 FluentFTP ——
    /// 它要长度来算进度,又不能去 Seek 一条网络流。时间戳沿用本服务上传的既有口径:不设。
    /// <para>
    /// 续传与 <see cref="UploadFileAsync" /> 同一口径:先核实远端那半截的末尾与源流同一段一致,
    /// 再交给 FluentFTP 的 Resume 模式:它自己 SIZE 远端、再把源流 Seek 到同一偏移 ——
    /// 所以续传时源必须可 Seek,直接交它本身,不包外壳。
    /// </para>
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
        // createMode 不用:FTP 的 STOR 没有建文件时的权限位(权限由服务端的 umask 定),接口说明里写了后端可以忽略它。
        ArgumentNullException.ThrowIfNull(source);
        string fileName = GetRemoteFileName(remotePath);
        if (resumeOffset > 0)
        {
            if (!source.CanSeek)
            {
                throw new ArgumentException("Resuming a streamed upload needs a seekable source.", nameof(source));
            }
            resumeOffset = await ResolveUploadResumeAsync(sessionId, remotePath, source, length, cancellationToken).ConfigureAwait(false);
            if (resumeOffset > 0)
            {
                ReadCountingStream? attempt = null;
                await RunTransferAsync(sessionId, "upload", async client =>
                {
                    // 不包外壳的理由见上:Resume 要 Seek 源流。计数这层照样把 Seek 交给它。
                    var counted = attempt = new ReadCountingStream(source);
                    FtpStatus resumed = await client.UploadStream(
                        counted,
                        NormalizePath(remotePath),
                        FtpRemoteExists.Resume,
                        createRemoteDir: false,
                        progress: MapProgress(progress, fileName, length),
                        token: cancellationToken).ConfigureAwait(false);
                    if (resumed == FtpStatus.Failed)
                    {
                        throw new VelaFtpOperationException($"FTP upload of {fileName} failed.");
                    }
                }, () => attempt?.BytesRead > 0, replayable: true, cancellationToken).ConfigureAwait(false);
                return;
            }
            // 远端没有可续的半截:整份重传。核实时源流被定位过,从头读。
            source.Seek(0, SeekOrigin.Begin);
        }
        var input = new KnownLengthStream(source, length);
        await RunTransferAsync(sessionId, "upload", async client =>
        {
            // 服务器以「忙」顶回来时 RunTransferAsync 会重试一次,而源流可能已经被读走一截 ——
            // 照原样再传,落下的就是一个少了开头的文件。能倒回去就倒回去。
            // 倒不回去的源(replayable: false)读走过字节之后 RunTransferAsync 不会再进来,下面的报错只是兜底。
            if (input.BytesRead > 0)
            {
                if (!source.CanSeek)
                {
                    throw new VelaFtpOperationException($"FTP upload of {fileName} was interrupted and the source stream cannot be replayed.");
                }
                source.Position = 0;
                input.ResetCount();
            }
            FtpStatus status = await client.UploadStream(
                input,
                NormalizePath(remotePath),
                FtpRemoteExists.Overwrite,
                createRemoteDir: false,
                progress: MapProgress(progress, fileName, length),
                token: cancellationToken).ConfigureAwait(false);
            if (status == FtpStatus.Failed)
            {
                throw new VelaFtpOperationException($"FTP upload of {fileName} failed.");
            }
        }, () => input.BytesRead > 0, replayable: source.CanSeek, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DownloadFileAsync(Guid sessionId,
        string remotePath,
        string localPath,
        IProgress<TransferProgress>? progress = null,
        long resumeOffset = 0,
        CancellationToken cancellationToken = default)
    {
        string fileName = GetRemoteFileName(remotePath);
        // 以此刻本地那半截的实际长度为准,并核实它的末尾确实是远端文件同一段(理由同上传侧);
        // 没有可续的就整份重下(覆盖本地残留)。
        if (resumeOffset > 0)
        {
            resumeOffset = await ResolveDownloadResumeAsync(sessionId, remotePath, localPath, cancellationToken).ConfigureAwait(false);
        }
        long written = 0;
        await RunTransferAsync(sessionId, "download", async client =>
        {
            written = 0;
            long totalBytes = await client.GetFileSize(NormalizePath(remotePath), -1, cancellationToken).ConfigureAwait(false);
            // 续传不用 FileMode.Append:它追加在「文件此刻的末尾」,而 RunTransferAsync 被「忙」顶回来时会把这一段再跑一遍 ——
            // 上一次已经写下的那截还在,同一个 REST 偏移再下一遍,同一段字节就写了两遍(文件比远端长、内容是坏的)。
            // 每一次尝试都截回本次调用的起点、从那里写,这一段跑几遍落下的都是同一份。
            await using FileStream target = resumeOffset > 0
                ? new FileStream(localPath, FileMode.Open, FileAccess.Write, FileShare.None)
                : new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None);
            if (resumeOffset > 0)
            {
                target.SetLength(resumeOffset);
                target.Seek(resumeOffset, SeekOrigin.Begin);
            }
            try
            {
                bool ok = await client.DownloadStream(
                    target,
                    NormalizePath(remotePath),
                    resumeOffset,
                    MapProgress(progress, fileName, totalBytes),
                    cancellationToken).ConfigureAwait(false);
                if (!ok)
                {
                    throw new VelaFtpOperationException($"FTP download of {fileName} failed.");
                }
            }
            finally
            {
                // Position 含还在写缓冲里的字节:只要这一次往本地落过东西就算「动过字节」。
                written = target.Position - resumeOffset;
            }
        }, () => written > 0, replayable: true, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DownloadStreamAsync(Guid sessionId, string remotePath, Stream destination,
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("Destination stream must be writable.", nameof(destination));
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        string path = NormalizePath(remotePath);
        long start = destination.CanSeek ? destination.Position : 0;
        var budget = new DownloadBudget();
        var output = new DownloadDestination(destination, progress, GetRemoteFileName(path));
        for (int attempt = 0; ; attempt++)
        {
            FtpConnectionPool.Lease? lease = null;
            bool failed = false;
            long expected = -1;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                lease = await RentAsync(sessionId, cancellationToken).ConfigureAwait(false);
                AsyncFtpClient client = lease.Client;
                ((IBoundedDownloadClient)client).DownloadBudget = budget;
                expected = await GetBinaryFileSizeAsync(client, path, cancellationToken).ConfigureAwait(false);
                output.ExpectedLength = expected;
                // 55.1 (f051d642): progress != null makes DownloadFileInternal stop at SIZE,
                // hiding excess data. Read to EOF instead; our sink supplies progress and length checks.
                client.LastReplies.Clear();
                bool ok = await client.DownloadStream(output, path, token: cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                output.ThrowIfFailed(); // the library can swallow an IOException from the destination
                // SIZE 不可用时 expected = -1:跳过长度核对,由下面的 226/250 完成回复把关。
                // 以前这里只在「长短相同」时才算通过,于是不支持 SIZE 的服务器上一份完整的下载也被判失败,
                // 白重试满设置里的次数。
                if (expected >= 0 && output.BytesWritten != expected)
                    throw new RemoteTransferLengthException(path, expected, output.BytesWritten);
                // DownloadFileInternal already consumes the final reply, including its earlySuccess path.
                // bool alone accepts a null code and preliminary replies. Never issue a second GetReply.
                // f051d642: Execute sets LastCommandExecuted; ProcessGetReply copies it to Command
                // (MaskCommand only masks USER/PASS). IOException earlySuccess adds a suffix.
                // Noop=false and Navigate=Manual are pinned before Connect below, so no daemon
                // or auto-restore PWD/CWD can replace this reply. Command is library attribution,
                // not a server-supplied transfer identifier; length and completion code remain required.
                FtpReply reply = client.LastReply;
                if (!ok || reply.Code is not ("226" or "250") || reply.Command?.StartsWith("RETR ", StringComparison.Ordinal) != true)
                    throw new VelaFtpOperationException(Strings.Format("FtpSvc_ReadCompletionFailed", reply.Code, reply.Message));
                return;
            }
            catch (Exception ex)
            {
                failed = true;
                lease?.Invalidate();
                output.ThrowIfFailed();
                cancellationToken.ThrowIfCancellationRequested();
                // All outer busy retries AND internal REST resumes share one RETR budget (3 total).
                // Non-seekable plugin sinks remain supported, but cannot replay already delivered bytes.
                if (attempt < MaxBusyRetries && budget.Opens < DownloadBudget.MaxOpens
                    && FluentFtpInterop.IsConcurrencyRejection(ex)
                    && (output.BytesWritten == 0 || destination.CanSeek)
                    && LimitSessionToSingleConnection(sessionId))
                {
                    if (output.BytesWritten > 0)
                    {
                        destination.Position = start;
                        destination.SetLength(start);
                    }
                    output.ResetCount();
                    continue;
                }
                if (ex is RemoteTransferLengthException) throw;
                // A reset can make FluentFTP throw while closing the data stream instead of
                // returning false. Preserve the short-transfer diagnosis for a 426 reply too.
                if (expected >= 0 && output.BytesWritten != expected
                    && HasAbortedTransferReply(ex))
                {
                    throw new RemoteTransferLengthException(path, expected, output.BytesWritten);
                }

                throw Fault(sessionId, ex, "download");
            }
            finally
            {
                if (lease is not null)
                {
                    ((IBoundedDownloadClient)lease.Client).DownloadBudget = null;
                    try { lease.Dispose(); }
                    catch (Exception cleanup) when (failed && cleanup is not OutOfMemoryException)
                    {
                        // Failed data/completion/local writes remain the primary error, even if disposal fails.
                    }
                }
            }
        }
    }

    private static bool HasAbortedTransferReply(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is FtpCommandException { CompletionCode: "426" }) return true;
        }
        return false;
    }

    /// <summary>续传前比对的尾部字节数:与 SftpService 同一口径 —— 够识别「同名不同文件」,又只值一次往返。</summary>
    private const int ResumeVerifyBytes = 64 * 1024;

    /// <summary>
    /// 核实一次上传的续传起点。
    /// <para>
    /// 调用方手里那个偏移是更早之前探到的,中间隔着冲突对话框、传输队列,甚至一次重启 ——
    /// 源或远端完全可能已经变了,照着「远端更短」就追加,落下的是「旧开头 + 新尾巴」,行还显示完成。
    /// 所以这里以此刻的远端长度为准,读回远端那半截的最后一段,与源的同一段逐字节比对(SFTP 侧同一个做法)。
    /// FTP 单条数据连接顺序写,没有乱序空洞,不必像 SFTP 那样回退一个在途窗口。
    /// </para>
    /// </summary>
    /// <returns>核实过的续传偏移;0 = 远端没有可续的半截(不在、为空、或已不短于源),应整份重传。</returns>
    private async Task<long> ResolveUploadResumeAsync(Guid sessionId, string remotePath, Stream source, long sourceLength,
        CancellationToken cancellationToken)
    {
        string path = NormalizePath(remotePath);
        long remoteLength = await GetRemoteBinarySizeAsync(sessionId, path, cancellationToken).ConfigureAwait(false);
        if (remoteLength <= 0 || remoteLength >= sourceLength)
        {
            return 0;
        }
        int window = (int)Math.Min(ResumeVerifyBytes, remoteLength);
        // 这一段正好读到远端文件末尾:读完照常收 226,连接留着接着传。
        byte[]? remote = await ReadRemoteWindowAsync(sessionId, path, remoteLength - window, window, remoteLength, cancellationToken)
            .ConfigureAwait(false);
        byte[] local = new byte[window];
        source.Seek(remoteLength - window, SeekOrigin.Begin);
        int read = await source.ReadAtLeastAsync(local, window, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (remote is null || read != window || !remote.AsSpan().SequenceEqual(local))
        {
            throw new VelaSftpResumeMismatchException(Strings.Format("SftpSvc_ResumeUploadMismatch", remotePath));
        }
        return remoteLength;
    }

    /// <summary>
    /// 核实一次下载的续传起点,理由同 <see cref="ResolveUploadResumeAsync" />:以此刻本地那半截的实际长度为准,
    /// 读回远端文件同一段,与本地那半截的最后一段逐字节比对。
    /// </summary>
    /// <remarks>
    /// 这一段在远端文件中间,读够就得放手:那条连接上还欠着没读完的数据和一条完成回复,只能作废,
    /// 接下来的下载重新登录一次 —— 只在续传时多这一次,换来的是不往一个已经被替换掉的文件后面接尾巴。
    /// 远端问不出长度(SIZE 不可用)时没有可信的起点,整份重下。
    /// </remarks>
    /// <returns>核实过的续传偏移;0 = 应整份重下(覆盖本地残留)。</returns>
    private async Task<long> ResolveDownloadResumeAsync(Guid sessionId, string remotePath, string localPath,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(localPath);
        if (!info.Exists || info.Length <= 0)
        {
            return 0;
        }
        long localLength = info.Length;
        string path = NormalizePath(remotePath);
        long remoteLength = await GetRemoteBinarySizeAsync(sessionId, path, cancellationToken).ConfigureAwait(false);
        if (localLength >= remoteLength)
        {
            return 0;
        }
        int window = (int)Math.Min(ResumeVerifyBytes, localLength);
        byte[]? remote = await ReadRemoteWindowAsync(sessionId, path, localLength - window, window, remoteLength, cancellationToken)
            .ConfigureAwait(false);
        byte[] local = new byte[window];
        int read;
        await using (var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            stream.Seek(localLength - window, SeekOrigin.Begin);
            read = await stream.ReadAtLeastAsync(local, window, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        }
        if (remote is null || read != window || !remote.AsSpan().SequenceEqual(local))
        {
            throw new VelaSftpResumeMismatchException(Strings.Format("SftpSvc_ResumeDownloadMismatch", remotePath));
        }
        return localLength;
    }

    /// <summary>远端文件此刻的二进制长度(SIZE);不在或问不出时为 -1。</summary>
    private async Task<long> GetRemoteBinarySizeAsync(Guid sessionId, string path, CancellationToken cancellationToken)
    {
        using FtpConnectionPool.Lease lease = await RentAsync(sessionId, cancellationToken).ConfigureAwait(false);
        try
        {
            return await GetBinaryFileSizeAsync(lease.Client, path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Fault(sessionId, ex, "resume check");
        }
    }

    /// <summary>
    /// 读远端文件从 <paramref name="start" /> 起的 <paramref name="count" /> 个字节(REST + RETR)。
    /// 读不满(远端在这期间变短了)返回 null。
    /// </summary>
    /// <remarks>
    /// 收尾交给 <see cref="LeasedStream" />:这一段正好到文件末尾(<paramref name="fileLength" /> = start + count)时
    /// 它照常收 226、连接回池;否则读够就放手,它把这条连接作废(数据连接上还有没读的字节,不能复用)。
    /// </remarks>
    private async Task<byte[]?> ReadRemoteWindowAsync(Guid sessionId, string path, long start, int count, long fileLength,
        CancellationToken cancellationToken)
    {
        FtpConnectionPool.Lease lease = await RentAsync(sessionId, cancellationToken).ConfigureAwait(false);
        Stream stream;
        try
        {
            Stream inner = await lease.Client
                .OpenRead(path, FtpDataType.Binary, start, fileLength, token: cancellationToken)
                .ConfigureAwait(false);
            // 完成回复由 LeasedStream 自己收(与 OpenReadOnceAsync 同一个做法)。
            if (inner is FtpDataStream data)
            {
                data.ControlConnection = null!;
            }
            stream = new LeasedStream(inner, lease, path, fileLength - start, cancellationToken);
        }
        catch (Exception ex)
        {
            lease.Invalidate();
            try { lease.Dispose(); }
            catch (Exception cleanup) when (cleanup is not OutOfMemoryException)
            {
                // Preserve the original open failure.
            }
            cancellationToken.ThrowIfCancellationRequested();
            throw Fault(sessionId, ex, "resume check");
        }
        byte[] window = new byte[count];
        int read;
        try
        {
            await using (stream.ConfigureAwait(false))
            {
                read = await stream.ReadAtLeastAsync(window, count, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (RemoteTransferLengthException)
        {
            // 远端在 SIZE 与 RETR 之间变短了:起点不可信。
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Fault(sessionId, ex, "resume check");
        }
        return read == count ? window : null;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid sessionId, string remotePath, IProgress<SftpDeleteProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using FtpConnectionPool.Lease lease = await RentAsync(sessionId, cancellationToken).ConfigureAwait(false);
        AsyncFtpClient client = lease.Client;
        string path = NormalizePath(remotePath);
        try
        {
            FtpListItem? item = await client.GetObjectInfo(path, false, cancellationToken).ConfigureAwait(false);
            if (item is null || item.Type != FtpObjectType.Directory)
            {
                await client.DeleteFile(path, cancellationToken).ConfigureAwait(false);
                progress?.Report(new(1, 1, path));
                return;
            }
            // 递归删:先数一遍再删,才能给出「已删 n / 共 m」的确定进度(与 SFTP 侧的语义一致)。
            List<string> files = [];
            List<string> directories = [];
            await CollectAsync(client, path, files, directories, cancellationToken).ConfigureAwait(false);
            int total = files.Count + directories.Count + 1;
            int done = 0;
            foreach (string file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await client.DeleteFile(file, cancellationToken).ConfigureAwait(false);
                progress?.Report(new(++done, total, file));
            }
            // 子目录按深度倒序删,保证先空后删。
            foreach (string directory in directories.OrderByDescending(static d => d.Count(static c => c == '/')))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await client.DeleteDirectory(directory, cancellationToken).ConfigureAwait(false);
                progress?.Report(new(++done, total, directory));
            }
            await client.DeleteDirectory(path, cancellationToken).ConfigureAwait(false);
            progress?.Report(new(++done, total, path));
        }
        catch (Exception ex)
        {
            throw Fault(sessionId, ex, "delete");
        }
    }

    /// <inheritdoc />
    public async Task CreateDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        using FtpConnectionPool.Lease lease = await RentAsync(sessionId, cancellationToken).ConfigureAwait(false);
        try
        {
            await lease.Client.CreateDirectory(NormalizePath(remotePath), true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Fault(sessionId, ex, "create directory");
        }
    }

    /// <inheritdoc />
    public async Task CreateFileAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        using FtpConnectionPool.Lease lease = await RentAsync(sessionId, cancellationToken).ConfigureAwait(false);
        try
        {
            using var empty = new MemoryStream([]);
            await lease.Client.UploadStream(empty, NormalizePath(remotePath), FtpRemoteExists.Overwrite,
                createRemoteDir: false, token: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Fault(sessionId, ex, "create file");
        }
    }

    /// <inheritdoc />
    public Task EnsureDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) =>
        CreateDirectoryAsync(sessionId, remotePath, cancellationToken);

    /// <inheritdoc />
    public async Task RenameAsync(Guid sessionId, string oldPath, string newPath, CancellationToken cancellationToken = default)
    {
        using FtpConnectionPool.Lease lease = await RentAsync(sessionId, cancellationToken).ConfigureAwait(false);
        try
        {
            await lease.Client.Rename(NormalizePath(oldPath), NormalizePath(newPath), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Fault(sessionId, ex, "rename");
        }
    }

    /// <inheritdoc />
    public async Task CopyAsync(Guid sessionId, string sourcePath, string destPath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        // FTP 没有服务端复制命令(FXP 是站点间传输,不是同站复制),只能下行再上行。
        // 走临时文件而非内存:远端文件可能远大于可用内存。
        string temp = Path.Combine(Path.GetTempPath(), $"vela-ftp-copy-{Guid.NewGuid():N}");
        try
        {
            await DownloadFileAsync(sessionId, sourcePath, temp, progress, 0, cancellationToken).ConfigureAwait(false);
            await UploadFileAsync(sessionId, temp, destPath, progress, 0, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (IOException)
            {
                // 临时文件删不掉不影响结果。
            }
        }
    }

    /// <inheritdoc />
    public async Task SetPermissionsAsync(Guid sessionId, string remotePath, short octalMode, CancellationToken cancellationToken = default)
    {
        using FtpConnectionPool.Lease lease = await RentAsync(sessionId, cancellationToken).ConfigureAwait(false);
        try
        {
            // SITE CHMOD 是可选命令,很多服务器(尤其 Windows/IIS 系)不实现 —— 失败会被翻译成
            // VelaFtpOperationException,由上层提示,而不是静默当成功。
            await lease.Client.Chmod(NormalizePath(remotePath), octalMode, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Fault(sessionId, ex, "chmod");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// FTP 没有改属主的标准命令(<c>SITE CHOWN</c> 不在任何 RFC 里,各家服务器要么没有、要么只给 root),
    /// 如实报不支持。属性弹窗靠 <see cref="GetOwnerChoicesAsync" /> 返回 null 把属主/属组显示成只读,
    /// 正常走不到这里。
    /// </remarks>
    public Task SetOwnerAsync(Guid sessionId, string remotePath, string? owner, string? group, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("FTP has no standard command for changing file ownership."));

    /// <inheritdoc />
    public Task<RemoteOwnerChoices?> GetOwnerChoicesAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult<RemoteOwnerChoices?>(null);

    /// <inheritdoc />
    /// <remarks>
    /// 走 FluentFTP 的 <c>GetChecksum</c>:服务器通告了 <c>HASH</c>(draft-bryan-ftpext-hash)且支持 SHA-256 时用它,
    /// 否则试 <c>XSHA256</c>。指定了 SHA-256 就不会退而求其次去用 MD5 / CRC —— 服务器只有那些时库抛
    /// <see cref="FtpHashUnsupportedException" />,这里翻译成 <see cref="NotSupportedException" />。
    /// 单个文件失败(不存在、没权限)只让它回退;连接断了照常上抛,不能把掉线当成「这个文件算不出来」。
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, string?>> ComputeSha256Async(Guid sessionId, IReadOnlyList<string> remotePaths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remotePaths);
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (remotePaths.Count == 0)
        {
            return result;
        }
        using FtpConnectionPool.Lease lease = await RentAsync(sessionId, cancellationToken).ConfigureAwait(false);
        foreach (string path in remotePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                FtpHash hash = await lease.Client
                    .GetChecksum(NormalizePath(path), FtpHashAlgorithm.SHA256, cancellationToken)
                    .ConfigureAwait(false);
                result[path] = hash.IsValid && hash.Algorithm == FtpHashAlgorithm.SHA256 && hash.Value is { Length: 64 } value
                    ? value.ToLowerInvariant()
                    : null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (FtpHashUnsupportedException ex)
            {
                throw new NotSupportedException($"This FTP server does not offer SHA-256 (HASH / XSHA256): {ex.Message}", ex);
            }
            catch (FtpCommandException ex) when (ex.CompletionCode is "500" or "501" or "502" or "504")
            {
                throw new NotSupportedException($"This FTP server rejected the SHA-256 command: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                Exception translated = Fault(sessionId, ex, "hash");
                if (FluentFtpInterop.IsConnectionLost(translated))
                {
                    throw translated;
                }
                result[path] = null;
            }
        }
        return result;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 走 <c>MFMT</c>(draft-somers-ftp-mfxx,时间按 UTC 发送)。它不在 RFC 959 里,服务器没通告或回
    /// 「不认识 / 未实现」时抛 <see cref="NotSupportedException" /> —— 目录同步据此如实提示
    /// 「这台服务器记不住上传文件的修改时间」,而不是假装对齐了。
    /// </remarks>
    public async Task SetLastWriteTimeAsync(Guid sessionId, string remotePath, DateTime lastWriteTimeUtc, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        DateTime utc = lastWriteTimeUtc.Kind == DateTimeKind.Utc ? lastWriteTimeUtc : lastWriteTimeUtc.ToUniversalTime();
        using FtpConnectionPool.Lease lease = await RentAsync(sessionId, cancellationToken).ConfigureAwait(false);
        FtpReply reply;
        try
        {
            // 不用 AsyncFtpClient.SetModifiedTime:它按 TimeConversion 配置换算时区,而 MFMT 的时间
            // 规定就是 UTC;自己拼命令,发出去的值才不会因为某个配置项被悄悄挪几个小时。
            reply = await lease.Client
                .Execute($"MFMT {utc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)} {NormalizePath(remotePath)}", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Fault(sessionId, ex, "mfmt");
        }
        if (!reply.Success)
        {
            throw reply.Code is "500" or "501" or "502" or "504"
                ? new NotSupportedException($"This FTP server does not support MFMT: {reply.Message}")
                : new VelaFtpOperationException($"FTP MFMT failed: {reply.Message}");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// FTP 标准里没有建链接的命令,只有 ProFTPD(mod_site_misc)等少数服务器实现了非标准的
    /// <c>SITE SYMLINK &lt;目标&gt; &lt;链接&gt;</c>。服务器回「不认识 / 未实现」时抛
    /// <see cref="NotSupportedException" />,让界面如实说"这台服务器不支持",而不是报一个笼统的失败。
    /// 这条命令以空格分隔参数、没有转义手段,含空格的路径表达不了,同样直说不支持。
    /// </remarks>
    public async Task CreateSymbolicLinkAsync(Guid sessionId, string linkPath, string targetPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        if (linkPath.Contains(' ') || targetPath.Contains(' '))
        {
            throw new NotSupportedException("FTP SITE SYMLINK cannot express paths containing spaces.");
        }
        using FtpConnectionPool.Lease lease = await RentAsync(sessionId, cancellationToken).ConfigureAwait(false);
        FtpReply reply;
        try
        {
            reply = await lease.Client
                .Execute($"SITE SYMLINK {targetPath.Replace('\\', '/')} {NormalizePath(linkPath)}", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Fault(sessionId, ex, "symlink");
        }
        if (!reply.Success)
        {
            throw reply.Code is "500" or "501" or "502" or "504"
                ? new NotSupportedException($"This FTP server does not support SITE SYMLINK: {reply.Message}")
                : new VelaFtpOperationException($"FTP symlink failed: {reply.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<RemoteFileInfo> GetFileInfoAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        using FtpConnectionPool.Lease lease = await RentAsync(sessionId, cancellationToken).ConfigureAwait(false);
        string path = NormalizePath(remotePath);
        try
        {
            FtpListItem? item = await lease.Client.GetObjectInfo(path, true, cancellationToken).ConfigureAwait(false)
                                ?? throw new VelaFtpPathNotFoundException($"FTP path not found: {path}");
            RemoteFileInfo mapped = await MapAsync(lease.Client, item, cancellationToken).ConfigureAwait(false);
            if (!mapped.IsDirectory)
            {
                // LIST/MLST 的大小不保证与 Binary RETR 同口径。仅单文件 stat 补 SIZE,
                // 不给目录列表逐个查询;未知时用 0 沿用上层「非正长不做精确校验」的策略。
                long length = await GetBinaryFileSizeAsync(lease.Client, path, cancellationToken).ConfigureAwait(false);
                return Map(item, size: Math.Max(0, length));
            }
            return mapped;
        }
        catch (Exception ex)
        {
            throw Fault(sessionId, ex, "stat");
        }
    }

    /// <inheritdoc />
    /// <inheritdoc />
    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) =>
        OpenReadOnceAsync(sessionId, remotePath, cancellationToken);

    private async Task<Stream> OpenReadOnceAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken)
    {
        string path = NormalizePath(remotePath);
        for (int attempt = 0; ; attempt++)
        {
            FtpConnectionPool.Lease lease = await RentAsync(sessionId, cancellationToken).ConfigureAwait(false);
            try
            {
                long length = await GetBinaryFileSizeAsync(lease.Client, path, cancellationToken).ConfigureAwait(false);
                // Query binary SIZE on this lease; -1 avoids the library querying again for empty files.
                Stream inner = await lease.Client
                    .OpenRead(path, FtpDataType.Binary, 0L, length > 0 ? length : -1L, token: cancellationToken)
                    .ConfigureAwait(false);
                // LeasedStream alone consumes the final reply, including after read failure.
                if (inner is FtpDataStream data)
                {
                    data.ControlConnection = null!;
                }
                return new LeasedStream(inner, lease, remotePath, length, cancellationToken);
            }
            catch (RejectedDataConnectionException) when (attempt < MaxBusyRetries && LimitSessionToSingleConnection(sessionId))
            {
                // 数据连接一个字节都没给就被关掉:服务器大概在拒绝并发。收成单连接再开一次,
                // 这一次会排队等前一个传输结束,而不是立刻又撞上同一个上限。
                lease.Invalidate();
                try { lease.Dispose(); }
                catch (Exception cleanup) when (cleanup is not OutOfMemoryException)
                {
                    // Preserve the original failure.
                }
                continue;
            }
            catch (Exception ex)
            {
                lease.Invalidate();
                try { lease.Dispose(); }
                catch (Exception cleanup) when (cleanup is not OutOfMemoryException)
                {
                    // Preserve the original open failure.
                }
                cancellationToken.ThrowIfCancellationRequested();
                // No bytes have escaped to a caller yet. Release the failed lease before
                // queueing behind the active transfer on a server allowing only one RETR.
                // 两种拒绝都算:明确说「忙」的,和数据连接刚建起来就被复位、一个字节都没给的。
                // 这里的 receivedAnyBytes: false 是事实:流还没交出去,数据连接上一个字节都还没读过。
                if (attempt < MaxBusyRetries
                    && (FluentFtpInterop.IsConcurrencyRejection(ex)
                        || FluentFtpInterop.LooksLikeRejectedDataConnection(ex, receivedAnyBytes: false))
                    && LimitSessionToSingleConnection(sessionId))
                {
                    continue;
                }
                throw Fault(sessionId, ex, "open");
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        using FtpConnectionPool.Lease lease = await RentAsync(sessionId, cancellationToken).ConfigureAwait(false);
        string path = NormalizePath(remotePath);
        try
        {
            return await lease.Client.FileExists(path, cancellationToken).ConfigureAwait(false) ||
                   await lease.Client.DirectoryExists(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Fault(sessionId, ex, "exists");
        }
    }

    /// <inheritdoc />
    /// <remarks>FTP 没有查文件系统用量的标准命令:一律查不到。</remarks>
    public Task<RemoteSpaceInfo?> GetSpaceAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) =>
        Task.FromResult<RemoteSpaceInfo?>(null);

    /// <inheritdoc />
    /// <remarks>FTP 没有展开 ~用户名 的命令:一律展开不了。</remarks>
    public Task<string?> ExpandPathAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    /// <inheritdoc />
    public async Task<string> GetWorkingDirectoryAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        using FtpConnectionPool.Lease lease = await RentAsync(sessionId, cancellationToken).ConfigureAwait(false);
        try
        {
            string path = await lease.Client.GetWorkingDirectory(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(path) ? "/" : path;
        }
        catch (Exception ex)
        {
            throw Fault(sessionId, ex, "pwd");
        }
    }

    /// <summary>释放全部 FTP 会话与其连接。</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (Guid sessionId in _sessions.Keys.ToArray())
        {
            await CloseSessionAsync(sessionId).ConfigureAwait(false);
        }
    }

    // ---- 内部实现 ---------------------------------------------------------

    /// <summary>
    /// 跑一次传输。服务器以「忙 / 连接数超限 / 一次只能传一个」拒绝时,把该会话就地收成
    /// 单连接并**重试一次** —— 这一次会排队等那条唯一的连接空下来,于是批量传输自然变成串行。
    /// </summary>
    /// <remarks>
    /// 收紧是**整条会话**的一次性动作,而重试是**每个传输**各自的:一批并发传输往往同时撞上
    /// 这个拒绝,只有跑得最快的那个能把上限从 4 收到 1,其余的看到的是"已经是 1 了"——
    /// 所以重试的条件不能是"我收紧成功了",而是"这次拒绝是并发导致的,且会话还在"。
    /// 收紧之后重试会在池的闸门上排队,同一时刻只剩一个传输在跑,自然不会再撞。
    /// <para>
    /// 与 <see cref="FtpConnectionPool" /> 里那条自适应是两个场景:池管的是"第二条**连接**开不出来",
    /// 这里管的是"连接开得出来,但服务器不让同时跑第二个**传输**"(数据通道被拒)。
    /// </para>
    /// </remarks>
    /// <summary>
    /// 跑一次传输。服务器以「忙 / 连接数超限 / 一次只能传一个」拒绝时,把该会话就地收成
    /// 单连接并**重试一次** —— 这一次会排队等那条唯一的连接空下来,于是批量传输自然变成串行。
    /// <para>
    /// 拒绝有两种形态:明确回 <c>421/425/450</c> 或「busy」字样(措辞可见),
    /// 以及数据连接刚建起来就被复位、一个字节都没读到(措辞不可见,只能按事实判断)。
    /// 后者是批量传输里 `actual=0` 成片的真正来源,以前认不出来,于是每次重试都在撞同一个上限。
    /// </para>
    /// <para>
    /// 后一种只能按事实认,所以事实必须是真的:<paramref name="movedBytes" /> 由调用点如实报「这一次尝试动没动过字节」。
    /// 传到一半断掉(复位、426)不是「被拒」,是一次断线 —— 原样交给上层的重试策略,由它按目标此刻的长度续传;
    /// 在这里把整段再跑一遍,续传下载会把同一段字节追加两遍,只能顺序读的源则只能改报一个「无法重放」的永久失败。
    /// 租连接本身失败(主机不可达)同样不算:那时数据连接还没开过,不能让每次上层重试都白等三轮连接超时。
    /// </para>
    /// </summary>
    /// <param name="sessionId">会话。</param>
    /// <param name="operation">出错时报的操作名。</param>
    /// <param name="body">一次尝试;每次重试都整段再跑一遍。</param>
    /// <param name="movedBytes">这一次尝试有没有读走源 / 写下目标的字节(每次尝试开始时由 <paramref name="body" /> 自己清零)。</param>
    /// <param name="replayable">
    /// 动过字节之后还能不能整段再跑一遍(本地文件、可 Seek 的源、每次截回起点的下载)。
    /// 只影响「明说了忙」的那一种拒绝;按事实认的那一种动过字节就一律不算。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task RunTransferAsync(
        Guid sessionId,
        string operation,
        Func<AsyncFtpClient, Task> body,
        Func<bool> movedBytes,
        bool replayable,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            bool rented = false;
            try
            {
                using FtpConnectionPool.Lease lease = await RentAsync(sessionId, cancellationToken).ConfigureAwait(false);
                rented = true;
                await body(lease.Client).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (
                attempt < MaxBusyRetries
                && !cancellationToken.IsCancellationRequested
                && IsRetryableRejection(ex, rented, movedBytes(), replayable)
                && LimitSessionToSingleConnection(sessionId))
            {
                // 落到下一轮循环重试:这一次会排队等那条唯一的连接空下来。
            }
            catch (Exception ex)
            {
                throw Fault(sessionId, ex, operation);
            }
        }
    }

    /// <summary>这次失败算不算一次可以排队重来的「拒绝」(口径见 <see cref="RunTransferAsync" /> 的说明)。</summary>
    private static bool IsRetryableRejection(Exception ex, bool rented, bool movedBytes, bool replayable)
    {
        if (FluentFtpInterop.IsConcurrencyRejection(ex))
        {
            return !movedBytes || replayable;
        }
        return rented && FluentFtpInterop.LooksLikeRejectedDataConnection(ex, receivedAnyBytes: movedBytes);
    }

    /// <summary>
    /// 单个传输被"忙/超限"顶回来后允许重试的次数。给 2 次是留一手:
    /// 收紧生效前抢跑的那几个传输可能连着撞两回,再多就该如实报错了。
    /// </summary>
    private const int MaxBusyRetries = 2;

    /// <summary>把会话收成单连接;会话还在就返回 true(上限本来就是 1 也算数,见调用点注释)。</summary>
    private bool LimitSessionToSingleConnection(Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out FtpConnectionPool? pool))
        {
            return false;
        }
        pool.LimitToSingleConnection();
        return true;
    }

    /// <inheritdoc />
    void RemoteRelay.IFtpSourceRejectionSignal.LimitToSingleConnection(Guid sessionId) =>
        LimitSessionToSingleConnection(sessionId);

    private async Task<FtpConnectionPool.Lease> RentAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(sessionId, out FtpConnectionPool? pool))
        {
            throw new VelaFtpConnectionException($"FTP session {sessionId} is not open.");
        }
        try
        {
            return await pool.RentAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 租不到连接(服务器没了 / 重连失败)同样是一次「会话掉线」,要上报出去,
            // 否则界面上的状态圆点会一直停在绿色。
            throw Fault(sessionId, ex, "connect");
        }
    }

    /// <summary>
    /// 把库异常翻译成中立异常;若属于连接级失败,顺带把该会话标记为已失效并广播出去
    /// —— 资源管理器树的状态圆点据此自动从「活跃」变「离线」。
    /// </summary>
    private Exception Fault(Guid sessionId, Exception ex, string operation)
    {
        Exception translated = FluentFtpInterop.Translate(ex, operation);
        if (FluentFtpInterop.IsConnectionLost(translated))
        {
            SessionStateChanged?.Invoke(this, new(sessionId, FtpSessionState.Faulted));
        }
        return translated;
    }

    private static async Task<AsyncFtpClient> CreateConnectedClientAsync(FtpConnectionInfo info, CertificateProbe probe, IProxyResolver? proxyResolver, RouteProbe routeProbe, CancellationToken cancellationToken)
    {
        ProxyRoute route = proxyResolver?.Resolve(info.Host, info.Port) ?? ProxyRoute.Direct;
        routeProbe.Used = route;
        var config = new FtpConfig
        {
            EncryptionMode = MapEncryption(info.Settings.EncryptionMode),
            // 主动模式要求服务器反向连入本机,经代理无法到达;走代理时强制被动。
            DataConnectionType = route.Kind != ProxyKind.None
                || info.Settings.DataConnectionMode == CoreDataMode.Passive
                ? FtpDataConnectionType.AutoPassive
                : FtpDataConnectionType.AutoActive,
            // 绝不无条件信任证书:校验逻辑在 ValidateCertificate 里,未信任的指纹要能一路冒泡到 UI。
            ValidateAnyCertificate = false,
            ConnectTimeout = 15_000,
            ReadTimeout = 30_000,
            DataConnectionConnectTimeout = 15_000,
            DataConnectionReadTimeout = 30_000,
            // 55.1/f051d642: NoopInterval=0 is clamped to 1000, NOT disabled.
            // Connect starts the daemon only when Noop=true. Pin the existing false default
            // before connecting, rather than racing a live daemon when a lease is rented.
            Noop = false,
            // DownloadFileInternal can issue PWD/CWD after RETR in SemiAuto mode, replacing
            // LastReply. Keep the existing Manual default explicit for completion attribution.
            Navigate = FtpNavigate.Manual,
        };
        AsyncFtpClient client = await CreateClientAsync(info, route, config, cancellationToken).ConfigureAwait(false);
        client.ValidateCertificate += (_, e) => probe.Validate(e);
        try
        {
            await client.Connect(cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 本次连接**实际用过**的代理路由,由 <see cref="CreateConnectedClientAsync" /> 在拨号前写入。
    /// </summary>
    /// <remarks>
    /// 失败之后重新 <c>Resolve</c> 一次是不可靠的:<c>system</c> 档跟随的是 OS **当前**代理,
    /// 两次调用之间用户可能刚把 Clash 的系统代理关掉 —— 那样报出来的 via 就不是真正失败的那一条,
    /// 反而把人往错方向带。
    /// </remarks>
    private sealed class RouteProbe
    {
        /// <summary>拨号用的路由;<c>Resolve</c> 就抛了(代理配错)时保持 null。</summary>
        public ProxyRoute? Used { get; set; }
    }

    /// <summary>
    /// 连接层失败的代理语境补全:
    /// 只包隧道层异常(<see cref="VelaFtpConnectionException" />,即代理拨号/握手/Socket/TLS 失败),
    /// 认证/权限/路径等应用层异常原样透出 —— 那些说明隧道本身是通的。
    /// 后缀是纯技术信息(host:port),不新增本地化键。
    /// </summary>
    private static Exception WithProxyContext(Exception translated, FtpConnectionInfo info, ProxyRoute? route)
    {
        if (translated is not VelaFtpConnectionException
            || route is null
            || route.Kind == ProxyKind.None)
        {
            return translated;
        }
        string via = $" (via {(route.Kind == ProxyKind.Socks5 ? "socks5" : "http")} {HostAddress.FormatEndPoint(route.Host, route.Port)} → {HostAddress.FormatEndPoint(info.Host, info.Port)})";
        // FTP :21 经 HTTP CONNECT 同样常被代理软件限制(只放行 80/443),直指换 SOCKS5/直连。
        string hint = route.Kind == ProxyKind.Http
            ? " If the proxy refuses CONNECT, switch Proxy to socks5 or none for TUN."
            : "";
        return new VelaFtpConnectionException(translated.Message + via + hint, translated);
    }

    /// <summary>
    /// 按统一代理路由实例化 FTP 客户端:直连用普通 <see cref="AsyncFtpClient" />,
    /// 走代理用 FluentFTP 的代理子类(控制与数据连接一并经代理)。
    /// 「不用代理做 DNS」时本地先解析成 IP 交给代理端。
    /// </summary>
    private static async Task<AsyncFtpClient> CreateClientAsync(
        FtpConnectionInfo info, ProxyRoute route, FtpConfig config, CancellationToken cancellationToken)
    {
        if (route.Kind == ProxyKind.None)
        {
            return new BoundedFtpClient(info.Host, info.EffectiveUsername, info.EffectivePassword, info.Port, config);
        }
        string ftpHost = route.ProxyDns
            ? info.Host
            : await LocalDnsResolver.ResolveAsync(info.Host, cancellationToken).ConfigureAwait(false);
        var profile = new FtpProxyProfile
        {
            ProxyHost = route.Host,
            ProxyPort = route.Port,
            ProxyCredentials = route.ToCredential(),
            FtpHost = ftpHost,
            FtpPort = info.Port,
            FtpCredentials = new NetworkCredential(info.EffectiveUsername, info.EffectivePassword),
        };
        AsyncFtpClient client = route.Kind == ProxyKind.Http
            ? new BoundedHttpFtpClient(profile)
            : new BoundedSocksFtpClient(profile);
        client.Config = config;
        return client;
    }

    private static FluentFTP.FtpEncryptionMode MapEncryption(CoreEncryption mode) =>
        mode switch
        {
            CoreEncryption.None => FluentFTP.FtpEncryptionMode.None,
            CoreEncryption.Explicit => FluentFTP.FtpEncryptionMode.Explicit,
            CoreEncryption.Implicit => FluentFTP.FtpEncryptionMode.Implicit,
            _ => FluentFTP.FtpEncryptionMode.Auto,
        };

    /// <summary>SIZE 取决于当前 TYPE;调用期间由同一租约独占控制连接。负值表示未知。</summary>
    private static async Task<long> GetBinaryFileSizeAsync(AsyncFtpClient client, string path, CancellationToken cancellationToken)
    {
        // 55.1 的 SetDataTypeAsync 是 protected;只在公开状态尚非 Binary 时发等价命令。
        // 成功后同步公开缓存,避免 OpenRead 内再次 TYPE;重连由库重置此状态。
        if (client.Status.CurrentDataType != FtpDataType.Binary)
        {
            FtpReply reply = await client.Execute("TYPE I", cancellationToken).ConfigureAwait(false);
            if (!reply.Success) throw new FtpCommandException(reply);
            client.Status.CurrentDataType = FtpDataType.Binary;
        }
        long length = await client.GetFileSize(path, -1, cancellationToken).ConfigureAwait(false);
        return length >= 0 ? length : -1;
    }

    /// <summary>把 FluentFTP 的进度回调翻译成 VelaShell 的传输进度快照。</summary>
    private static Progress<FtpProgress>? MapProgress(IProgress<TransferProgress>? progress, string fileName, long totalBytes) =>
        progress is null
            ? null
            : new Progress<FtpProgress>(p => progress.Report(new()
            {
                FileName = fileName,
                BytesTransferred = p.TransferredBytes,
                TotalBytes = totalBytes > 0 ? totalBytes : p.TransferredBytes,
                Percentage = p.Progress is >= 0 and <= 100 ? (int)p.Progress : 0,
                SpeedBytesPerSecond = p.TransferSpeed,
                EstimatedTimeRemaining = p.ETA,
            }));

    /// <summary>递归收集目录下的文件与子目录(不含目录自身)。</summary>
    private static async Task CollectAsync(AsyncFtpClient client, string path, List<string> files, List<string> directories, CancellationToken cancellationToken)
    {
        FtpListItem[] items = await client.GetListing(path, cancellationToken).ConfigureAwait(false);
        foreach (FtpListItem item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Type == FtpObjectType.Directory)
            {
                directories.Add(item.FullName);
                await CollectAsync(client, item.FullName, files, directories, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                files.Add(item.FullName);
            }
        }
    }

    /// <summary>
    /// <see cref="FtpListItem" /> → <see cref="RemoteFileInfo" />。
    /// 属主/属组直接取服务器给的**名字**(FTP 没有 UID/GID),因此整条绕开了 SFTP 那边
    /// 靠 SSH exec 跑 <c>getent passwd</c> 的身份翻译。服务器不给时留空,由界面自行留白。
    /// </summary>
    private static RemoteFileInfo Map(FtpListItem item, bool linkIsDirectory = false, long? size = null) =>
        new()
        {
            Name = item.Name ?? string.Empty,
            FullPath = item.FullName ?? string.Empty,
            Size = size ?? Math.Max(0, item.Size),
            IsDirectory = item.Type == FtpObjectType.Directory || (item.Type == FtpObjectType.Link && linkIsDirectory),
            IsSymbolicLink = item.Type == FtpObjectType.Link,
            LinkTarget = item.Type == FtpObjectType.Link && !string.IsNullOrEmpty(item.LinkTarget) ? item.LinkTarget : null,
            LastModified = item.Modified,
            Permissions = FormatPermissions(item),
            Owner = item.RawOwner ?? string.Empty,
            Group = item.RawGroup ?? string.Empty,
        };

    /// <summary>
    /// 带链接解析的 <see cref="Map" />:LIST 只说得出"这是个链接、指向哪里",说不出目标是不是目录
    /// (FluentFTP 不再提供解引用),于是对链接补一次 <c>DirectoryExists</c>(CWD 探测)。
    /// 只有链接才多这一趟往返,普通条目零开销。
    /// </summary>
    private static async Task<RemoteFileInfo> MapAsync(AsyncFtpClient client, FtpListItem item, CancellationToken cancellationToken) =>
        Map(item, item.Type == FtpObjectType.Link
                  && await client.DirectoryExists(item.FullName, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// 权限字符串:优先用服务器原样给的(Unix 风格 LIST 会给 <c>-rw-r--r--</c>),
    /// 去掉首位的类型字符;拿不到就用解析出的三组权限位拼,再拿不到就留空。
    /// </summary>
    private static string FormatPermissions(FtpListItem item)
    {
        string raw = item.RawPermissions ?? string.Empty;
        if (raw.Length >= 10)
        {
            return raw[1..10];
        }
        if (raw.Length == 9)
        {
            return raw;
        }
        if (item.OwnerPermissions == FtpPermission.None &&
            item.GroupPermissions == FtpPermission.None &&
            item.OthersPermissions == FtpPermission.None)
        {
            return string.Empty;
        }
        return string.Concat(
            Rwx(item.OwnerPermissions),
            Rwx(item.GroupPermissions),
            Rwx(item.OthersPermissions));
    }

    private static string Rwx(FtpPermission permission) =>
        string.Concat(
            permission.HasFlag(FtpPermission.Read) ? "r" : "-",
            permission.HasFlag(FtpPermission.Write) ? "w" : "-",
            permission.HasFlag(FtpPermission.Execute) ? "x" : "-");

    private static string NormalizePath(string path) =>
        string.IsNullOrWhiteSpace(path) ? "/" : path.Replace('\\', '/');

    private static string GetRemoteFileName(string remotePath)
    {
        string normalized = NormalizePath(remotePath).TrimEnd('/');
        int slash = normalized.LastIndexOf('/');
        return slash >= 0 && slash < normalized.Length - 1 ? normalized[(slash + 1)..] : normalized;
    }

    /// <summary>
    /// 服务器证书校验策略:指纹已被用户信任、或链路本身无误 → 放行;
    /// 否则记下证书信息并拒绝,由 <see cref="OpenSessionAsync" /> 换成带指纹的异常抛给上层。
    /// <para>
    /// 刻意**不**在这个同步事件里去弹 UI 等用户点确认 —— 那要把异步的对话框阻塞成同步,
    /// 极易死锁。改成「先拒绝 → 上层提示 → 记住指纹后重连」,流程干净且不阻塞。
    /// </para>
    /// </summary>
    private sealed class CertificateProbe(string? trustedThumbprint)
    {
        /// <summary>最近一次未通过校验的证书信息;没有失败时为 null。</summary>
        public CertificateFailure? Failure { get; private set; }

        public void Validate(FtpSslValidationEventArgs e)
        {
            string thumbprint = ComputeThumbprint(e.Certificate);
            if (e.PolicyErrors == SslPolicyErrors.None ||
                (trustedThumbprint is { Length: > 0 } trusted &&
                 string.Equals(trusted, thumbprint, StringComparison.OrdinalIgnoreCase)))
            {
                e.Accept = true;
                return;
            }
            Failure = new(
                thumbprint,
                e.Certificate?.Subject ?? string.Empty,
                e.Certificate?.Issuer ?? string.Empty,
                e.Certificate is X509Certificate2 cert ? cert.NotAfter : DateTimeOffset.MinValue,
                e.PolicyErrors.ToString());
            e.Accept = false;
        }

        private static string ComputeThumbprint(X509Certificate? certificate)
        {
            if (certificate is null)
            {
                return string.Empty;
            }
            try
            {
                return Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
            }
            catch (CryptographicException)
            {
                return string.Empty;
            }
        }
    }

    /// <summary>
    /// 数据连接在 RETR 之后、任何字节到达之前就被关闭。并发被拒时不少服务器就是这种形态
    /// (直接复位,不给 425/450),所以这不是「文件坏了」,而是「这次没轮到」——
    /// 上层据此把会话收成单连接后重试,而不是判成内容不符。
    /// </summary>
    public sealed class RejectedDataConnectionException(string message, Exception? innerException = null)
        : VelaFtpConnectionException(message, innerException);
    /// <summary>未通过校验的证书信息。</summary>
    private sealed record CertificateFailure(string Thumbprint, string Subject, string Issuer, DateTimeOffset ExpiresOn, string PolicyErrors);

    /// <summary>
    /// 绑定了一次连接租借的只读流:调用方释放流时连同把连接还回池子。
    /// FTP 的数据连接与流同生共死,不能像 SFTP 那样先还连接再慢慢读。
    /// </summary>
    private sealed class LeasedStream(Stream inner, FtpConnectionPool.Lease lease, string remotePath, long length, CancellationToken openToken) : Stream
    {
        private readonly Lock _closeSync = new();
        private readonly long _length = length;
        private readonly string _remotePath = remotePath;
        private Task? _closing;
        private long _bytesRead;
        private bool _eof;
        private bool _readFailed;

        public override bool CanRead => _closing is null && inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _bytesRead;
            set => throw new NotSupportedException("FTP data streams are sequential.");
        }

        public override void Flush() { }

        private int TrackRead(int read, int requested)
        {
            _bytesRead += read;
            if (requested > 0 && read == 0)
            {
                _eof = true;
                // SIZE 与实际读出量对不上是真问题,但这里只报给调用方「传输不完整」这一件事:
                // 内容完整性交给 226 最终回复;这个异常只为让上层给出可读的诊断与重试判断。
                if (_length > 0 && _bytesRead != _length)
                {
                    // 一个字节都没抓到就结束。两种可能:数据连接被服务器掐掉(并发被拒时不少服务器
                    // 直接复位、不给 425/450),或者服务器正常结束了数据流但文件确实比元数据短。
                    // 只有「读取过程中真的出现过错误」才判为前者并允许重开;正常 EOF 仍是长度不符。
                    if (_bytesRead == 0 && _readFailed)
                    {
                        // 根本没有数据到达,而且读取过程本身就失败了:数据连接被服务器掐掉
                        // (并发被拒时不少服务器直接复位,不给 425/450),不是文件内容不符。
                        throw new RemoteTransferLengthException(_remotePath, _length, 0, rejectedByServer: true);
                    }
                    if (_bytesRead == 0)
                    {
                        // 没有失败、也没有数据:服务器正常结束了数据流。文件确实比元数据短,重开无益。
                        throw new RemoteTransferLengthException(_remotePath, _length, 0, rejectedByServer: false);
                    }
                    throw LengthMismatch();
                }
            }
            return read;
        }

        /// <summary>长度不符:这是一次不完整的读取,不是「长度未知」—— 后者在 OpenReadAsync 里已按 -1 表达。</summary>
        private RemoteTransferLengthException LengthMismatch() =>
            new(_remotePath, _length, _bytesRead);

        public override int Read(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(_closing is not null, this);
            try
            {
                openToken.ThrowIfCancellationRequested();
                return TrackRead(inner.Read(buffer, offset, count), count);
            }
            catch (Exception ex)
            {
                _readFailed = true;
                lease.Invalidate();
                CloseOnceAsync().GetAwaiter().GetResult();
                if (ex is IOException or OperationCanceledException) throw;
                throw FluentFtpInterop.Translate(ex, "read");
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_closing is not null, this);
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(openToken, cancellationToken);
                linked.Token.ThrowIfCancellationRequested();
                int read = await inner.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                return TrackRead(read, buffer.Length);
            }
            catch (Exception ex)
            {
                _readFailed = true;
                lease.Invalidate();
                await CloseOnceAsync().ConfigureAwait(false);
                if (ex is IOException or OperationCanceledException) throw;
                throw FluentFtpInterop.Translate(ex, "read");
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException("FTP data streams are not seekable.");

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private Task CloseOnceAsync()
        {
            lock (_closeSync)
            {
                return _closing ??= CloseCoreAsync();
            }
        }

        private async Task CloseCoreAsync()
        {
            // 未读完不尝试排空/ABOR:直接丢弃控制连接,避免收尾盖掉调用方的失败。
            // 这里有两条不同的事实,不能混成一条:
            //  · 调用方把 SIZE 说有的字节都读走了(只是没探到 EOF):控制通道状态仍然可信,照常收 226;
            //  · 调用方在中途就放手了:数据通道还有没读的字节,不能排空也不能复用。
            // 长度只是信封:没读到 EOF、而且 SIZE 说还有没读的字节,才算调用方中途弃读。
            bool lengthSatisfied = _length > 0 && _bytesRead >= _length;
            bool abandoned = !_eof && !lengthSatisfied;
            bool discard = abandoned || _readFailed || openToken.IsCancellationRequested;
            if (discard) lease.Invalidate();
            // 沿用控制通道超时;即使调用方配置无限等待,收尾也最多等默认的 30 秒。
            int timeout = lease.Client.Config.ReadTimeout;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(openToken);
            deadline.CancelAfter(timeout > 0 ? timeout : 30_000);
            Exception? failure = null;
            bool verified = false;
            try
            {
                // 只关闭数据资源一次。绝不调用 FtpDataStream.CloseAsync 或 base.DisposeAsync。
                // FluentFTP 的读失败路径可能已经关闭数据 socket,不再二次关闭。
                if (inner is not FtpSocketStream { IsDisposed: true })
                {
                    await inner.DisposeAsync().AsTask().WaitAsync(deadline.Token).ConfigureAwait(false);
                }
                if (!discard)
                {
                    // 租约仍独占;GetReply 可能返回空 Code 而不抛超时,必须显式检查。
                    FtpReply reply = await lease.Client.GetReply(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
                    if (reply.Code is not ("226" or "250"))
                    {
                        throw new VelaFtpOperationException(Strings.Format("FtpSvc_ReadCompletionFailed", reply.Code, reply.Message));
                    }
                    verified = true;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                lease.Invalidate();
                if (!discard)
                {
                    failure = openToken.IsCancellationRequested
                        ? new OperationCanceledException(openToken)
                        : deadline.IsCancellationRequested
                            ? new VelaFtpConnectionException(Strings.Get("FtpSvc_ReadCompletionTimeout"), ex)
                            : FluentFtpInterop.Translate(ex, "read completion");
                }
            }
            finally
            {
                // 包括致命异常在内,只有明确验证完成的连接才有资格回到空闲池。
                if (!verified) lease.Invalidate();
                try
                {
                    lease.Dispose();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    if (!discard && failure is null) failure = FluentFtpInterop.Translate(ex, "read cleanup");
                }
            }
            if (failure is not null) throw failure;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) CloseOnceAsync().GetAwaiter().GetResult();
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            GC.SuppressFinalize(this);
            return new ValueTask(CloseOnceAsync());
        }
    }

    // 55.1 ResumeDownloadAsync has no retry limit and ignores Config.RetryAttempts.
    // Guard its public virtual OpenReadInternal hook, not log text or private reflection.
    // The budget is installed only for DownloadStreamAsync; legacy OpenRead is unchanged.
    private sealed class DownloadBudget
    {
        public const int MaxOpens = 3;
        public int Opens { get; private set; }
        public void Opening()
        {
            if (Opens >= MaxOpens)
                throw new VelaFtpOperationException(Strings.Format("FtpSvc_ReadCompletionFailed", "RETR", "retry-limit"));
            Opens++;
        }
    }

    private interface IBoundedDownloadClient
    {
        DownloadBudget? DownloadBudget { get; set; }
    }

    private sealed class BoundedFtpClient(string host, string user, string password, int port, FtpConfig config)
        : AsyncFtpClient(host, user, password, port, config), IBoundedDownloadClient
    {
        public DownloadBudget? DownloadBudget { get; set; }
        public override Task<Stream> OpenReadInternal(string path, FtpDataType type, long fileLen, long restart,
            bool ignoreStaleData, CancellationToken token = default)
        {
            DownloadBudget?.Opening();
            return base.OpenReadInternal(path, type, fileLen, restart, ignoreStaleData, token);
        }
    }

    private sealed class BoundedHttpFtpClient(FtpProxyProfile profile)
        : AsyncFtpClientHttp11Proxy(profile), IBoundedDownloadClient
    {
        public DownloadBudget? DownloadBudget { get; set; }
        public override Task<Stream> OpenReadInternal(string path, FtpDataType type, long fileLen, long restart,
            bool ignoreStaleData, CancellationToken token = default)
        {
            DownloadBudget?.Opening();
            return base.OpenReadInternal(path, type, fileLen, restart, ignoreStaleData, token);
        }
    }

    private sealed class BoundedSocksFtpClient(FtpProxyProfile profile)
        : AsyncFtpClientSocks5Proxy(profile), IBoundedDownloadClient
    {
        public DownloadBudget? DownloadBudget { get; set; }
        public override Task<Stream> OpenReadInternal(string path, FtpDataType type, long fileLen, long restart,
            bool ignoreStaleData, CancellationToken token = default)
        {
            DownloadBudget?.Opening();
            return base.OpenReadInternal(path, type, fileLen, restart, ignoreStaleData, token);
        }
    }

    /// <summary>Leave-open, sequential sink. Preserve local failures even when FluentFTP catches IOException.</summary>
    private sealed class DownloadDestination(Stream inner, IProgress<TransferProgress>? progress, string fileName) : Stream
    {
        private Exception? _failure;
        public long ExpectedLength { get; set; }
        public long BytesWritten { get; private set; }
        public void ResetCount() => BytesWritten = 0;
        public void ThrowIfFailed()
        {
            if (_failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_failure).Throw();
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ThrowIfFailed();
            try
            {
                await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                BytesWritten += buffer.Length;
                progress?.Report(new TransferProgress
                {
                    FileName = fileName,
                    BytesTransferred = BytesWritten,
                    TotalBytes = Math.Max(0, ExpectedLength),
                    Percentage = ExpectedLength > 0 ? (int)Math.Min(100, BytesWritten * 100.0 / ExpectedLength) : 0,
                    SpeedBytesPerSecond = 0,
                    EstimatedTimeRemaining = TimeSpan.Zero,
                });
            }
            catch (Exception ex)
            {
                _failure = ex;
                throw;
            }
        }
        public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            ThrowIfFailed();
            try { await inner.FlushAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { _failure = ex; throw; }
        }
    }

    /// <summary>
    /// 从流上传用的只读外壳:报出调用方给的长度、不可 Seek、记着已读字节数,且<b>不</b>释放内层流
    /// (流归调用方)。FluentFTP 拿长度算进度,而源往往是一条问不出长度的网络流。
    /// </summary>
    private sealed class KnownLengthStream(Stream inner, long length) : Stream
    {
        /// <summary>自上次 <see cref="ResetCount" /> 以来读出的字节数。</summary>
        public long BytesRead { get; private set; }

        /// <summary>源流倒回开头之后清零计数。</summary>
        public void ResetCount() => BytesRead = 0;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => BytesRead;
            set => throw new NotSupportedException("The relay source is sequential.");
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            BytesRead += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException("The relay source is not seekable.");

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// 只数读走了多少字节的透明外壳:长度、定位一律交给内层(FluentFTP 的 Resume 要 Seek 源流),
    /// 也<b>不</b>释放内层流。<see cref="RunTransferAsync" /> 据此分辨「一开就被拒」与「传到一半断掉」。
    /// </summary>
    private sealed class ReadCountingStream(Stream inner) : Stream
    {
        /// <summary>经这层读走的字节数。</summary>
        public long BytesRead { get; private set; }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            BytesRead += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
