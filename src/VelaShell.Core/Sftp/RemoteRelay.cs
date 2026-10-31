using System.Diagnostics;
using System.IO.Pipelines;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;

namespace VelaShell.Core.Sftp;

/// <summary>
/// 两个远端会话之间的文件中转:可定位源直接读取,顺序源以最多 4 MiB 内存缓冲,不落盘。
/// </summary>
/// <remarks>
/// <para>
/// SFTP / FTP 协议本身都没有「服务器到服务器」的复制原语(FTP 的 FXP 各家服务器几乎都禁了),
/// 所以数据一定要过本机。这里做到的是<b>不落盘</b>:不像同会话复制那样先整份下载到临时文件再上传,
/// 已知不超过 4 MiB 的顺序源先完整缓存并确认源关闭成功,其余顺序源以有界预读重叠两段传输。
/// </para>
/// <para>
/// 源与目标可以是不同的协议(SFTP、FTP、插件的文件协议两两组合),两端都只经 <see cref="ISftpService" />。
/// </para>
/// <para>
/// 支持断点续传,口径与上传相同:目标上前一次留下的半截,核实尾部与源一致后从那里接着写。
/// 核实要在源里来回定位,所以只有源可 Seek(SFTP)时才续;源回不了头时核实不了,按「对不上」交回同名冲突策略,
/// 而不是悄悄整份重传(那会不经询问覆盖一个可能与此无关的同名文件)。
/// </para>
/// </remarks>
public static class RemoteRelay
{
    /// <summary>把源会话上的一个文件搬到目标会话的给定路径(覆盖)。</summary>
    /// <param name="source">源端文件服务。</param>
    /// <param name="sourceSessionId">源会话。</param>
    /// <param name="sourcePath">源文件的远端路径。</param>
    /// <param name="target">目标端文件服务。</param>
    /// <param name="targetSessionId">目标会话。</param>
    /// <param name="targetPath">目标文件的远端路径。</param>
    /// <param name="progress">进度回报(以目标端写出的字节计)。</param>
    /// <param name="writing">
    /// 源端已经打开、即将开始写目标时回调一次。调用方据此区分「源都没打开就失败了」
    /// (目标原封未动,绝不能当半截文件清掉)与「写到一半失败了」。
    /// </param>
    /// <param name="resumeOffset">
    /// &gt; 0 表示目标已有前一次留下的半截,试着从那里接着传(真正的起点由目标端按此刻的状态核实,
    /// 见 <see cref="ISftpService.UploadStreamAsync" />)。源流不可 Seek(FTP 的数据流、多数插件协议)时核实不了,
    /// 抛 <see cref="VelaSftpResumeMismatchException" />,目标未被碰过。
    /// </param>
    /// <param name="knownSource">调用方已经问到的源元数据;给了就不在源端再 stat 一次(FTP 上那是一次抢控制连接的往返)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task CopyFileAsync(
        ISftpService source,
        Guid sourceSessionId,
        string sourcePath,
        ISftpService target,
        Guid targetSessionId,
        string targetPath,
        IProgress<TransferProgress>? progress = null,
        Action? writing = null,
        long resumeOffset = 0,
        RelaySourceSnapshot? knownSource = null,
        CancellationToken cancellationToken = default)
    {
        CompletionProgress? completion = await CopyFileCoreAsync(source, sourceSessionId, sourcePath, target, targetSessionId, targetPath,
            progress, writing, resumeOffset, requireFullConsumption: false, knownSource: knownSource,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        completion?.Complete();
    }

    /// <summary>
    /// 先把字节写到目标端同目录的临时文件,待源流完成确认后再提交到正式路径。
    /// <para>
    /// 只有目标后端能保证提交语义时才返回 <see langword="true" />。不支持时在打开源流前返回
    /// <see langword="false" />,让调用方选择兼容的回退策略。这样 FTP→SFTP 大文件不需要在本机
    /// 落一份完整缓存,而 SFTP 的 POSIX rename 仍保证正式文件不会暴露半截内容。
    /// </para>
    /// </summary>
    public static async Task<bool> TryCopyFileWithRemoteCommitAsync(
        ISftpService source,
        Guid sourceSessionId,
        string sourcePath,
        ISftpService target,
        Guid targetSessionId,
        string targetPath,
        IProgress<TransferProgress>? progress = null,
        Action? streamingStarted = null,
        RelaySourceSnapshot? knownSource = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        // Holding a source lease while renting a target lease from the same session
        // can deadlock a single-connection backend. Keep that case on the staged path.
        if (sourceSessionId == targetSessionId) return false;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await target.CheckUploadCommitAsync(targetSessionId, targetPath, overwrite: true, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            return false;
        }

        if (await target.ExistsAsync(targetSessionId, targetPath, cancellationToken).ConfigureAwait(false))
        {
            RemoteFileInfo destination = await target.GetFileInfoAsync(targetSessionId, targetPath, cancellationToken)
                .ConfigureAwait(false);
            // Legacy upload follows a link. Replacing that link changes semantics and
            // could create a readable temporary for a protected referent.
            if (destination.IsSymbolicLink) return false;
        }
        uint? createMode = await target.GetUploadCreateModeAsync(targetSessionId, targetPath, cancellationToken)
            .ConfigureAwait(false);
        streamingStarted?.Invoke();
        string temporaryPath = CreateTemporaryPath(targetPath);
        bool commitStarted = false;
        bool writing = false;
        try
        {
            // 源读端与目标写端在这里拆成两个循环(见 CopyFileCoreAsync 的 piped):
            // FTP 数据连接只负责尽快把字节吐进管道,SFTP 只负责从管道取走 —— 两边谁也不等谁。
            CompletionProgress? completion = await CopyFileCoreAsync(
                source, sourceSessionId, sourcePath, target, targetSessionId, temporaryPath,
                progress, () => writing = true, resumeOffset: 0, requireFullConsumption: true, createMode,
                piped: PipesSequentialSources, knownSource: knownSource, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            // Commit cannot be rolled back by cancellation. Bound the wait, but report
            // an ambiguous failure separately so callers never automatically replay it.
            using var commitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            commitStarted = true;
            try
            {
                await target.CommitUploadAsync(targetSessionId, temporaryPath, targetPath, overwrite: true,
                    commitTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                bool definiteRejection = ex is NotSupportedException
                    or (VelaSftpOperationException and not VelaSftpTransferInterruptedException);
                // A definite refusal means no commit was performed; delete the
                // staged file. Network/timeout failures remain ambiguous.
                if (definiteRejection)
                {
                    commitStarted = false;
                    throw;
                }
                throw new RemoteRelayCommitException(ex);
            }
            completion?.Complete();
            return true;
        }
        catch
        {
            if (writing && !commitStarted)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await target.DeleteAsync(targetSessionId, temporaryPath, cancellationToken: cleanup.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception cleanupFailure) when (cleanupFailure is not OutOfMemoryException)
                {
                    Trace.WriteLine(
                        $"[VelaShell] Relay temporary cleanup failed: {cleanupFailure.GetType().Name}");
                }
            }
            throw;
        }
    }

    private static async Task<CompletionProgress?> CopyFileCoreAsync(
        ISftpService source,
        Guid sourceSessionId,
        string sourcePath,
        ISftpService target,
        Guid targetSessionId,
        string targetPath,
        IProgress<TransferProgress>? progress,
        Action? writing,
        long resumeOffset,
        bool requireFullConsumption,
        uint? createMode = null,
        bool piped = false,
        RelaySourceSnapshot? knownSource = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        // 先 stat:进度要一个分母,「保留时间戳」要源文件的修改时间;顺带把"源已经不在了"
        // 挡在打开流之前,报出来的是那条路径找不到,而不是一条读到一半断掉的流。
        // 调用方已经问过(一次批量里的重试、同批的重复项)就给它的那份,不再回源端。
        RemoteFileInfo info = knownSource is { } known
            ? new RemoteFileInfo
            {
                Name = RemoteNameOf(sourcePath),
                FullPath = sourcePath,
                Size = known.Size,
                Permissions = string.Empty,
                IsDirectory = false,
                LastModified = known.LastModified,
                Owner = string.Empty,
                Group = string.Empty,
            }
            : await source.GetFileInfoAsync(sourceSessionId, sourcePath, cancellationToken).ConfigureAwait(false);
        Stream stream = await source.OpenReadAsync(sourceSessionId, sourcePath, cancellationToken).ConfigureAwait(false);
        return await CopyFromStreamAsync(info, stream, target, targetSessionId, targetPath,
            progress, writing, resumeOffset, requireFullConsumption, createMode, piped, cancellationToken).ConfigureAwait(false);
    }
    /// <summary>
    /// 让后端把一个会话的连接上限收成 1(「排队等前一个传输结束」)。本意是源端在交出任何字节之前就被掐断时,
    /// 中转先调它再重开一次源流,免得新连接撞上同一个并发上限。
    /// </summary>
    /// <remarks>
    /// 眼下<b>没接上</b>:中转从不调用它,也不会因此重开源流;FtpFileService 实现了它,但没有调用方。
    /// 源流在交出之前就被拒(425/450、数据连接一开就复位),由 FtpFileService.OpenReadAsync 自己收紧后有限重试。
    /// </remarks>
    public interface IFtpSourceRejectionSignal
    {
        /// <summary>把这个会话的连接上限收成 1(已经是 1 时是空操作)。</summary>
        void LimitToSingleConnection(Guid sessionId);
    }

    private static async Task<CompletionProgress?> CopyFromStreamAsync(
        RemoteFileInfo info,
        Stream stream,
        ISftpService target,
        Guid targetSessionId,
        string targetPath,
        IProgress<TransferProgress>? progress,
        Action? writing,
        long resumeOffset,
        bool requireFullConsumption,
        uint? createMode,
        bool piped = false,
        CancellationToken cancellationToken = default)
    {
        CompletionProgress? completion = progress is null ? null : new CompletionProgress(progress);
        BufferedRelayReadStream? buffered = null;
        if (piped && !stream.CanSeek)
        {
            return await CopyPipedAsync(info, stream, target, targetSessionId, targetPath,
                completion, writing, createMode, cancellationToken).ConfigureAwait(false);
        }
        CheckedReadStream? checkedSource = null;
        bool sourceClosed = false;
        async Task CloseSourceAsync()
        {
            // Mark before awaiting: a failed FTP final reply must not cause a
            // second close attempt, either here or in the exception cleanup.
            sourceClosed = true;
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        try
        {
            if (resumeOffset > 0 && !stream.CanSeek)
            {
                // FTP 的顺序流核实不了目标半截是否来自源,仍交回原有冲突策略,不能悄悄覆盖。
                throw new VelaSftpResumeMismatchException(Strings.Format("SftpSvc_ResumeUnverifiable", targetPath));
            }
            Stream uploadSource;
            // A fresh remote commit must consume every byte even if a future source
            // happens to expose seeking; do not inherit the legacy resume exemptions.
            if (requireFullConsumption || (!stream.CanSeek && resumeOffset == 0))
            {
                bool cacheEntireFile = info.Size is > 0 and <= BufferedRelayReadStream.MemoryBudget;
                buffered = new BufferedRelayReadStream(stream, info.Size, info.FullPath, cacheEntireFile, cancellationToken);
                await buffered.WaitUntilReadyAsync().ConfigureAwait(false);
                if (cacheEntireFile || buffered.IsEmpty)
                {
                    // Small files and genuinely empty unknown sources are safe to
                    // upload only after the source's final protocol reply succeeds.
                    await CloseSourceAsync().ConfigureAwait(false);
                }
                uploadSource = buffered;
            }
            else
            {
                checkedSource = new CheckedReadStream(stream, info.Size, info.FullPath);
                uploadSource = checkedSource;
            }
            cancellationToken.ThrowIfCancellationRequested();
            writing?.Invoke();
            await target.UploadStreamAsync(targetSessionId, uploadSource, targetPath, Math.Max(0, info.Size),
                info.LastModified == default ? null : info.LastModified, completion, resumeOffset, createMode,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (buffered is not null)
            {
                await buffered.VerifyCompleteAsync(cancellationToken).ConfigureAwait(false);
                await buffered.StopAsync().ConfigureAwait(false);
            }
            else
            {
                await checkedSource!.VerifyCompleteAsync(cancellationToken).ConfigureAwait(false);
            }
            if (!sourceClosed)
            {
                await CloseSourceAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // 目标或源读取已失败:源端仍须还连接,但收尾失败不能覆盖原始错误/取消。
            try
            {
                if (buffered is not null)
                {
                    await buffered.StopAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Trace.WriteLine($"[VelaShell] Relay buffer cleanup failed: {ex.GetType().Name}");
            }
            try
            {
                if (!sourceClosed)
                {
                    await CloseSourceAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Trace.WriteLine($"[VelaShell] Relay source cleanup failed: {ex.GetType().Name}");
            }
            throw;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (requireFullConsumption && completion is not null)
        {
            long bytes = buffered?.ConsumedBytes ?? checkedSource?.BytesRead ?? Math.Max(0, info.Size);
            completion.SetCompletion(info.Name, bytes);
        }
        return completion;
    }

    /// <summary>远端提交路径是否让顺序源走流管道(而不是先整份预读/缓存)。</summary>
    /// <remarks>
    /// 生产恒为 <see langword="true" />。只有验证"旧路径"的对抗用例把它关掉 —— 改动之前的行为不是被删掉,
    /// 而是两条路径都留着,能各自被指到。
    /// </remarks>
    public static bool PipesSequentialSources { get; set; } = true;

    private static async Task<CompletionProgress?> CopyPipedAsync(
        RemoteFileInfo info, Stream source, ISftpService target, Guid targetSessionId, string targetPath,
        CompletionProgress? completion, Action? writing, uint? createMode, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var pipe = new SourcePolledStream(info.FullPath, stop.Token);
        // The pump owns the source and writer, including their final protocol reply/completion.
        Task<long> pumping = PumpSourceAsync(source, info, pipe, stop.Token);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            writing?.Invoke();
            await target.UploadStreamAsync(targetSessionId, pipe, targetPath, Math.Max(0, info.Size),
                info.LastModified == default ? null : info.LastModified, completion, 0, createMode,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!pipe.EndOfStreamObserved)
            {
                throw new IOException("The upload returned before consuming the source to EOF.");
            }
            long produced = await pumping.ConfigureAwait(false);
            if (pipe.DeliveredBytes != produced)
            {
                throw new RemoteTransferLengthException(info.FullPath, produced, pipe.DeliveredBytes);
            }
            completion?.SetCompletion(info.Name, produced);
            return completion;
        }
        catch
        {
            // Cancellation interrupts both a pending source read and a backpressured flush.
            // Joining without a timeout ensures no task survives the attempt or races disposal.
            stop.Cancel();
            try
            {
                await pumping.ConfigureAwait(false);
            }
            catch (Exception cleanup) when (cleanup is not OutOfMemoryException)
            {
                Trace.WriteLine($"[VelaShell] Relay pump cleanup failed: {cleanup.GetType().Name}");
            }
            throw;
        }
    }

    private static async Task<long> PumpSourceAsync(
        Stream source, RemoteFileInfo info, SourcePolledStream sink, CancellationToken cancellationToken)
    {
        long produced = 0;
        Exception? failure = null;
        try
        {
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // A short read must reuse the unused segment instead of reserving
                    // another full block; byte backpressure alone cannot bound allocations.
                    Memory<byte> available = sink.Writer.GetMemory(1);
                    Memory<byte> block = available[..Math.Min(available.Length, BufferedRelayReadStream.BlockSize)];
                    int read = await source.ReadAsync(block, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    produced += read;
                    if (info.Size > 0 && produced > info.Size)
                        throw new RemoteTransferLengthException(info.FullPath, info.Size, produced);
                    sink.Writer.Advance(read);
                    FlushResult flushed = await sink.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    if (flushed.IsCanceled || flushed.IsCompleted)
                        throw new OperationCanceledException(cancellationToken);
                }
                if (info.Size > 0 && produced != info.Size)
                    throw new RemoteTransferLengthException(info.FullPath, info.Size, produced);
            }
            catch (Exception ex)
            {
                failure = ex;
                throw;
            }
            finally
            {
                try
                {
                    await source.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception) when (failure is not null)
                {
                    // Preserve a source read/length failure over a secondary final-reply failure.
                }
            }
            return produced;
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            await sink.Writer.CompleteAsync(failure).ConfigureAwait(false);
        }
    }
    private static string CreateTemporaryPath(string destinationPath)
    {
        int slash = destinationPath.LastIndexOf('/');
        string directory = slash >= 0 ? destinationPath[..(slash + 1)] : string.Empty;
        return $"{directory}.vela-relay-{Guid.NewGuid():N}";
    }

    /// <summary>远端路径的最后一段(调用方只给了元数据时补一个名字,错误与进度里要用)。</summary>
    private static string RemoteNameOf(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash >= 0 && slash + 1 < path.Length ? path[(slash + 1)..] : path;
    }

    internal sealed class CompletionProgress(IProgress<TransferProgress> inner) : IProgress<TransferProgress>
    {
        private readonly Lock _sync = new();
        private TransferProgress? _completion;
        private bool _succeeded;
        private bool _reported;

        public void Report(TransferProgress value)
        {
            lock (_sync)
            {
                if (_reported)
                {
                    return;
                }
                if (value.Percentage >= 100 || (value.TotalBytes > 0 && value.BytesTransferred >= value.TotalBytes))
                {
                    _completion = value;
                    if (!_succeeded)
                    {
                        // 字节确认仍用于重试起点记账,只是收尾成功前不显示完成。
                        inner.Report(new TransferProgress
                        {
                            FileName = value.FileName,
                            BytesTransferred = value.BytesTransferred,
                            TotalBytes = value.TotalBytes,
                            Percentage = 99,
                            SpeedBytesPerSecond = value.SpeedBytesPerSecond,
                            EstimatedTimeRemaining = value.EstimatedTimeRemaining,
                        });
                    }
                    ReportCompletion();
                }
                else
                {
                    inner.Report(value);
                }
            }
        }

        public void Complete()
        {
            lock (_sync)
            {
                _succeeded = true;
                ReportCompletion();
            }
        }

        public void SetCompletion(string fileName, long bytesTransferred)
        {
            lock (_sync)
            {
                _completion = new TransferProgress
                {
                    FileName = fileName,
                    BytesTransferred = bytesTransferred,
                    TotalBytes = bytesTransferred,
                    Percentage = 100,
                    SpeedBytesPerSecond = 0,
                    EstimatedTimeRemaining = TimeSpan.Zero,
                };
            }
        }

        private void ReportCompletion()
        {
            // FTP 目标通过 Progress<T> 异步上报,最后一帧也可能晚于上传方法返回。
            if (_succeeded && !_reported && _completion is { } value)
            {
                _reported = true;
                inner.Report(value);
            }
        }
    }

    // 不拥有 inner。按当前位置而非累计读取量校验,续传尾部比对的重复读取不算作额外传输。
    internal sealed class CheckedReadStream(Stream inner, long expectedLength, string path, bool requireFullConsumption = false) : Stream
    {
        private long _consumedPrefix;
        private bool _eof;

        public long BytesRead { get; private set; }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.CanSeek ? inner.Position : BytesRead;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            CheckRead(read, count);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            int read = inner.Read(buffer);
            CheckRead(read, buffer.Length);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            CheckRead(read, buffer.Length);
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            CheckRead(read, count);
            return read;
        }

        private void CheckRead(int read, int requested)
        {
            if (requested == 0)
            {
                return;
            }
            long position = inner.CanSeek ? inner.Position : BytesRead + read;
            if (read > 0)
            {
                if (requireFullConsumption && position - read <= _consumedPrefix)
                {
                    _consumedPrefix = Math.Max(_consumedPrefix, position);
                }
                BytesRead = position;
            }
            _eof = read == 0;
            if (expectedLength > 0 && (position > expectedLength || (_eof && position != expectedLength)))
            {
                throw LengthMismatch();
            }
        }

        public async Task VerifyCompleteAsync(CancellationToken cancellationToken)
        {
            if (expectedLength <= 0)
            {
                return;
            }
            if (BytesRead != expectedLength)
            {
                throw LengthMismatch();
            }
            // Staged, non-resumed uploads must consume the whole snapshot, not just
            // seek to the final byte. The original streaming path retains its contract.
            if (requireFullConsumption && _consumedPrefix != expectedLength)
            {
                throw new RemoteTransferLengthException(path, expectedLength, _consumedPrefix);
            }
            // 有的上传实现只读 length 个字节便返回,再探一次 EOF,避免源增长或 FTP 未读完就归池。
            if (!_eof)
            {
                if (inner.CanSeek)
                {
                    inner.Seek(BytesRead, SeekOrigin.Begin);
                }
                await ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false);
            }
        }

        private RemoteTransferLengthException LengthMismatch() => new(path, expectedLength, BytesRead);

        public override long Seek(long offset, SeekOrigin origin)
        {
            long position = inner.Seek(offset, origin);
            _eof = false;
            return position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>提交未确认;不得根据内层网络异常自动重放或清理正式目标。</summary>
public sealed class RemoteRelayCommitException(Exception innerException)
    : IOException(Strings.Get("SftpSvc_RelayCommitUnconfirmed"), innerException);

/// <summary>中转的可信源长度与实际读取量不一致;数值可安全写入重试诊断,不必记录带路径的消息。</summary>
/// <param name="path">源路径(消息里用)。</param>
/// <param name="expectedBytes">可信元数据给出的字节数。</param>
/// <param name="actualBytes">实际读取(或交给目标)的字节数。</param>
/// <param name="rejectedByServer">
/// 一个字节都没读到就结束:数据连接在 RETR 之后被服务器掐断(并发被拒时不少服务器直接复位,
/// 不给 425/450),与「文件内容变了」无关。眼下没有地方读它,也没有据此重开源流的逻辑(见 <see cref="RejectedByServer" />)。
/// </param>
/// <param name="message">
/// 可选的完整消息。字段与消息分开,是为了让只种了部分本地化资源的调用方(或一条尚未取走全部内容的源流)
/// 也能给出可读的诊断 —— 消息取不到不应该变成 NullReferenceException。
/// </param>
public sealed class RemoteTransferLengthException(
    string path, long expectedBytes, long actualBytes, bool rejectedByServer = false, string? message = null)
    : IOException(message ?? Strings.Format("SftpSvc_RelayLengthMismatch", path, expectedBytes, actualBytes))
{
    /// <summary>可信元数据给出的预期字节数。</summary>
    public long ExpectedBytes { get; } = expectedBytes;
    /// <summary>源流实际读取的字节数。</summary>
    public long ActualBytes { get; } = actualBytes;

    /// <summary>
    /// 一个字节都没读到就结束:数据连接在 RETR 之后被服务器掐断(并发被拒时不少服务器直接复位,
    /// 不给 425/450)。这类失败与「文件内容变了」无关。
    /// </summary>
    /// <remarks>
    /// 眼下恒为 false:唯一置 true 的地方(FTP 读流 LeasedStream 的长度核对)走不到 —— 读失败时流先被关掉,
    /// 不会再带着失败标记进到那里。也没有任何代码读它;重试分类不看它(本异常一律按永久失败算,见 TransferRetryPolicy)。
    /// </remarks>
    public bool RejectedByServer { get; } = rejectedByServer;
}
