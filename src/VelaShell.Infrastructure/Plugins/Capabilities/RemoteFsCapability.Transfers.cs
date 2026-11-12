using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.RemoteFs;
using VelaShell.Ssh.Diagnostics;
using RemoteUploadOptions = VelaShell.PluginSdk.RemoteFs.RemoteUploadOptions;

namespace VelaShell.Infrastructure.Plugins.Capabilities;

internal sealed partial class RemoteFsCapability
{
    public bool SupportsTransferOperations => true;
    internal bool UseDiskSnapshot { get; init; } = !OperatingSystem.IsWindows();

    /// <summary>
    /// 等待被取消、到期、断线或撤权打断后,给仍在跑的后端任务真正停下的有界宽限期:宽限期内停下了,才能确认停写、
    /// 清掉临时文件;仍没停才记 Unknown。测试可调短(与 <see cref="UseDiskSnapshot" /> 同一用意)。
    /// </summary>
    /// <remarks>
    /// 定 30 秒(原先 5 秒):SFTP 上传被取消时,文件流关闭前要先排空已经发出、还没确认的写入,再等 <c>CLOSE</c> 的应答,
    /// SSH 库给这两步各 30 秒(<c>SftpOptions.CloseTimeout</c>)。慢链路上排空一窗在途写入常要十几秒,5 秒的宽限期
    /// 一到就只能记「结果未知」、不敢清临时文件 —— 后端其实随后就合作地停了。取 30 秒盖住排空这一步;连 <c>CLOSE</c>
    /// 也等满的链路已经接近断开,仍按未确认记 Unknown,后端真停下后照样清掉没提交过的临时文件。
    /// 宽限期只在停止之后才开始算,正常传输不受影响;停止原因与终态的口径不变。
    /// </remarks>
    internal TimeSpan StopGracePeriod { get; init; } = TimeSpan.FromSeconds(30);

    private const int MaxRecords = 1024;
    private const int MaxActiveUploads = 32;
    private readonly Lock _transfersGate = new();
    private readonly Dictionary<string, UploadOperation> _transfers = [with(StringComparer.Ordinal)];
    private readonly Dictionary<string, StartReceipt> _receipts = [with(StringComparer.Ordinal)];
    private readonly SemaphoreSlim _uploadSlots = new(4);
    private bool _transfersDisposed;
    private bool _disconnectSubscribed;

    private sealed class SourceChangedException() : IOException("The approved source changed during upload.");
    private sealed record StartReceipt(string Session, string Local, string Remote, object Options, UploadOperation Operation);

    /// <summary>
    /// 任务被要求停下的原因,先到者为准。死线、断线、插件停用与用户取消取消的是同一个停止令牌,
    /// 终态必须按登记的原因分类,不能从「令牌被取消了」倒推成到期或用户取消。
    /// </summary>
    private enum StopReason { None, User, Timeout, Disconnected, Disposed }

    private sealed class UploadOperation(RemoteTransferSnapshot snapshot, Guid session, FileStream? source, CancellationTokenSource stop)
    {
        internal readonly object _gate = new();
        internal RemoteTransferSnapshot _snapshot = snapshot;

        /// <summary>
        /// 解析后的会话。快照里的 SessionId 是插件给的原样字符串(大写、N 格式都解析得了),
        /// 断线回调按它比对的话,换个写法启动的任务永远收不到断线通知。
        /// </summary>
        internal readonly Guid _session = session;
        internal readonly FileStream _source = source!;
        internal readonly CancellationTokenSource _stop = stop;
        internal TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task _worker = Task.CompletedTask;
        internal readonly DateTime _sourceModified = source is null ? default : File.GetLastWriteTimeUtc(source.SafeFileHandle);
        internal readonly long _sourceLength = source?.Length ?? -1;
        internal readonly long _registeredTicks = Stopwatch.GetTimestamp();
        internal long _lastProgress;
        internal bool _stopDisposed;
        internal StopReason _stopReason;
        internal UploadPermissionLease? _permission;
        internal DownloadPermissionLease? _downloadPermission;
        internal bool PermissionRevoked => _permission?.IsRevoked == true || _downloadPermission?.IsRevoked == true;

        /// <summary>
        /// 这一代的宿主批准已经生效:与阶段移出 <see cref="RemoteTransferPhase.Approval" /> 在同一步登记(见 <see cref="EnterTransferPhase" />)。
        /// 由 <see cref="_gate" /> 保护;一旦为 true 就不会变回去。
        /// </summary>
        internal bool _approvalEffective;

        /// <summary>
        /// 批准生效之前、收尾之前登记过取消意图(条件撤回,或批准前的 <see cref="CancelTransferAsync" />)。
        /// 由 <see cref="_gate" /> 保护。与 <see cref="_approvalEffective" /> 互斥:登记它时一并登记取消意图,批准随后就推进不了。
        /// </summary>
        internal bool _cancelledBeforeApproval;
    }

    internal int ActiveTransfers
    {
        get { lock (_transfersGate) return _transfers.Values.Count(op => !Read(op).IsTerminal || !op._worker.IsCompleted); }
    }

    public Task<RemoteTransferSnapshot> StartUploadAsync(string sessionId, string localPath, string remotePath,
        RemoteUploadOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OperationId);
        if (options.OperationId.Length > 256) throw new ArgumentException("Operation id is too long.", nameof(options));
        if (options.Timeout is { } timeout && (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(options), "Timeout must be positive and supported by the host timer.");
        string local = Path.GetFullPath(localPath);
        string remote = NormalizeDestination(remotePath);
        Guid session;
        lock (_transfersGate)
        {
            if (FindUploadReceipt(sessionId, local, remote, options) is { } previous) return Task.FromResult(previous);
            session = Resolve(sessionId);
            ThrowIfTransferQuotaReached("upload");
        }
        // 解析、打开与核验源都是阻塞 I/O(没有写端的 FIFO 能让 open 一直等下去),一律放在账本锁外:
        // 锁里只做回执查重与登记,同插件的查询、取消、断线回调与停用不会陪着卡住。
        string resolved;
        FileStream source;
        try
        {
            resolved = ResolveUploadSource(local);
            RejectLinks(resolved);
            RejectSpecialSource(resolved);
            source = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (AsSourceRejection(ex) is { } rejected)
        {
            throw rejected;
        }
        try
        {
            RejectLinks(resolved);
            VerifyOpenedSource(source.SafeFileHandle, resolved);
            // 打开前按路径查过类型,打开后再按句柄核一次:两次之间路径被换成设备也拦得住。
            if (!OperatingSystem.IsWindows() && source.SafeFileHandle.Type != FileHandleType.RegularFile)
                throw new NotSupportedException("Only regular files can be uploaded.");
            lock (_transfersGate)
            {
                // 锁外打开源的这段时间里,同一 OperationId 的并发启动可能已经先登记:沿用它的回执,本次打开的句柄作废。
                if (FindUploadReceipt(sessionId, local, remote, options) is { } raced)
                {
                    source.Dispose();
                    return Task.FromResult(raced);
                }
                // 会话也可能在这段时间里断开,而断线回调只停已登记的任务:登记前再查一次(只是查表,不阻塞)。
                Resolve(sessionId);
                ThrowIfTransferQuotaReached("upload");
                var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                if (options.Timeout is { } budget) stop.CancelAfter(budget);
                DateTimeOffset now = DateTimeOffset.UtcNow;
                var op = new UploadOperation(new()
                {
                    TransferId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                    OperationId = options.OperationId,
                    SessionId = sessionId,
                    LocalPath = resolved,
                    RemotePath = remote,
                    State = RemoteTransferState.Queued,
                    Generation = 1,
                    Sequence = 1,
                    TotalBytes = source.Length,
                    CreatedAt = now,
                    UpdatedAt = now,
                    DestinationCommitConfirmedAbsent = true,
                    Phase = RemoteTransferPhase.Approval,
                }, session, source, stop);
                if (uploadPermissions is not null && !string.IsNullOrWhiteSpace(pluginId))
                    op._permission = uploadPermissions.CreateUploadLease(pluginId);
                if (!_disconnectSubscribed)
                {
                    connections.SessionDisconnected += OnTransferSessionDisconnected;
                    _disconnectSubscribed = true;
                }
                _transfers.Add(op._snapshot.TransferId, op);
                _receipts.Add(options.OperationId, new(sessionId, local, remote, options, op));
                // 请求令牌只控制回执等待,绝不链接到传输寿命。
                op._worker = Task.Run(() => RunUploadAsync(op, session, options), CancellationToken.None);
                _ = op._worker.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return Task.FromResult(Read(op));
            }
        }
        catch (Exception ex) when (AsSourceRejection(ex) is { } rejected)
        {
            // 打开之后、登记之前文件没了(核验时按路径再走一遍才发现):同样什么都没登记。
            source.Dispose();
            throw rejected;
        }
        catch { source.Dispose(); throw; }
    }

    private const int SharingViolationHResult = unchecked((int)0x80070020);   // ERROR_SHARING_VIOLATION
    private const int LockViolationHResult = unchecked((int)0x80070021);      // ERROR_LOCK_VIOLATION

    /// <summary>
    /// 登记之前、源文件这一侧的明确拒绝 → <see cref="PluginTransferRejectedException" />(SDK 2.1.3):源不存在或已删除
    /// 记 <see cref="RemoteTransferRejectReason.SourceNotFound" />,被别的程序以拒读共享打开或锁住记
    /// <see cref="RemoteTransferRejectReason.SourceInUse" />。其余照旧是原来的类型 —— 链接、特殊文件、核验不过
    /// (<see cref="NotSupportedException" /> / <see cref="IOException" />)、没有权限(<see cref="UnauthorizedAccessException" />)。
    /// </summary>
    /// <remarks>
    /// 分开报的理由在调用方:这几种原先都是 <see cref="IOException" />,插件按约定把它当成「回执可能丢了」,
    /// 用同一个 OperationId 重取 —— 撞上同一个错误之后只能记成「结果未知」,整轮不敢再动。
    /// 新类型等于宿主保证什么都没开始;消息里不带路径(会转给模型)。
    /// </remarks>
    private static PluginTransferRejectedException? AsSourceRejection(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => new(RemoteTransferRejectReason.SourceNotFound,
            "The local source file does not exist (it may have been moved or deleted).", ex),
        IOException { HResult: SharingViolationHResult or LockViolationHResult } => new(RemoteTransferRejectReason.SourceInUse,
            "The local source file is locked or open in another program without read sharing.", ex),
        _ => null
    };

    /// <summary>
    /// 同一 OperationId 已登记过时按幂等规则重取回执(方向或参数不符一律拒绝,绝不重新打开源);没有回执时为 null。
    /// 调用方须持有 <see cref="_transfersGate" />。
    /// </summary>
    private RemoteTransferSnapshot? FindUploadReceipt(string sessionId, string local, string remote, RemoteUploadOptions options)
    {
        ObjectDisposedException.ThrowIf(_transfersDisposed || lifetime.IsCancellationRequested, this);
        if (!_receipts.TryGetValue(options.OperationId, out StartReceipt? previous)) return null;
        if (previous.Operation._snapshot.Direction != RemoteTransferDirection.Upload || previous.Session != sessionId || previous.Local != local || previous.Remote != remote || !Equals(previous.Options, options))
            throw new InvalidOperationException("Operation id was already used with different parameters.");
        return ReadAuthorized(previous.Operation);
    }

    /// <summary>
    /// 回执与活动任务额度(上传、下载共用);调用方须持有 <see cref="_transfersGate" />。额度满是登记之前的明确拒绝,
    /// 抛 <see cref="PluginTransferRejectedException" />(<see cref="RemoteTransferRejectReason.QuotaExceeded" />);
    /// 它派生自 <see cref="InvalidOperationException" />,与先前抛的类型兼容。
    /// </summary>
    private void ThrowIfTransferQuotaReached(string queue)
    {
        if (_receipts.Count >= MaxRecords)
        {
            throw new PluginTransferRejectedException(RemoteTransferRejectReason.QuotaExceeded,
                "Transfer operation receipt quota reached for this plugin lifetime.");
        }

        if (_transfers.Values.Count(op => !op._worker.IsCompleted) >= MaxActiveUploads)
        {
            throw new PluginTransferRejectedException(RemoteTransferRejectReason.QuotaExceeded,
                $"Active {queue} queue quota reached for this plugin; wait for running transfers to finish.");
        }
    }

    public Task<RemoteTransferSnapshot> GetTransferAsync(string transferId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ReadAuthorized(FindTransfer(transferId)));
    }

    public async Task<RemoteTransferSnapshot> WaitTransferAsync(string transferId, long afterSequence, TimeSpan waitTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        if (waitTimeout < TimeSpan.Zero || waitTimeout > TimeSpan.FromSeconds(IRemoteFsApi.MaxTransferWaitSeconds))
            throw new ArgumentOutOfRangeException(nameof(waitTimeout));
        cancellationToken.ThrowIfCancellationRequested();
        UploadOperation op = FindTransfer(transferId);
        Task changed;
        lock (op._gate)
        {
            if (op._snapshot.Sequence > afterSequence || op._snapshot.IsTerminal || waitTimeout == TimeSpan.Zero) return ReadAuthorized(op);
            changed = op._changed.Task;
        }
        try { await changed.WaitAsync(waitTimeout, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { }
        return ReadAuthorized(FindTransfer(transferId));
    }

    public Task<RemoteTransferSnapshot> CancelTransferAsync(string transferId, long expectedGeneration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UploadOperation op = FindTransfer(transferId, allowRevokedCleanup: true);
        lock (op._gate)
        {
            if (expectedGeneration <= 0 || expectedGeneration != op._snapshot.Generation)
                throw new InvalidOperationException("Transfer generation does not match.");
            if ((!op._snapshot.IsTerminal || !op._snapshot.RemoteStopConfirmed && !op._worker.IsCompleted)
                && !op._snapshot.CancellationRequested)
            {
                // 批准还没生效就取消的,与条件撤回同一口径(SDK:批准前的 CancelTransferAsync 也算撤下):这一代不会再被批准,以 Cancelled 收尾。
                if (!op._snapshot.IsTerminal && !op._approvalEffective) op._cancelledBeforeApproval = true;
                Update(op, s => s with { State = s.IsTerminal ? s.State : RemoteTransferState.CancelRequested, CancellationRequested = true });
                RequestStop(op, StopReason.User);
            }
            return Task.FromResult(CleanupView(op));
        }
    }

    /// <summary>宿主实现了条件撤回(<see cref="WithdrawPendingTransferAsync" />),上传与下载同一口径。</summary>
    public bool SupportsTransferWithdraw => true;

    /// <summary>
    /// 条件撤回:只在这一代的批准还没生效时撤下。判定与撤下都在任务锁里一步完成 —— 批准生效(<see cref="EnterTransferPhase" />)
    /// 也在同一把锁里推进阶段,两边谁先拿到锁谁赢,没有「插件读快照、再发取消」之间那一个往返的空当。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只取任务锁,不碰授权闸:批准生效的那一步是「闸 → 任务」的嵌套(闸里登记批准,经 onApproved 回调在任务锁里推进阶段),
    /// 任务锁就是两边共同的串行点,这里不引入新的锁序。
    /// </para>
    /// <para>
    /// 结论按 SDK 的判定顺序:批准已生效 → PastApproval(什么都不改);否则批准之前登记过取消 → Withdrawn;否则已经收尾 → AlreadyEnded;
    /// 否则本次登记取消意图并停下这一代 → Withdrawn。撤下的这一代:排在确认闸后面的等闸随停止令牌取消、不再弹框,已经弹出的确认框随令牌关掉,
    /// 随后到达的批准在闸里看到令牌已取消、或在任务锁里看到取消意图而作废(<see cref="ConfirmApprovedOrCancelled" />);
    /// 一个字节都不读写,以 Cancelled 收尾、RemoteStopConfirmed 为 true(收尾分类见 <see cref="FailUpload" />)。
    /// </para>
    /// <para>
    /// 两个标志都只在任务锁里改,且互斥,所以同一代重复调用总得到同一结论(回执丢了原样再调一次即可)。
    /// 无效或不属于本插件的 id 抛 <see cref="FileNotFoundException" />(能力实例按插件独立);撤权之后与取消一样仍可用于清理,快照按撤权口径脱敏。
    /// </para>
    /// </remarks>
    public Task<RemoteTransferWithdrawResult> WithdrawPendingTransferAsync(string transferId, long expectedGeneration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UploadOperation op = FindTransfer(transferId, allowRevokedCleanup: true);
        lock (op._gate)
        {
            if (expectedGeneration <= 0 || expectedGeneration != op._snapshot.Generation)
                throw new InvalidOperationException("Transfer generation does not match.");
            RemoteTransferWithdrawOutcome outcome;
            if (op._approvalEffective)
            {
                outcome = RemoteTransferWithdrawOutcome.PastApproval;
            }
            else if (op._cancelledBeforeApproval)
            {
                outcome = RemoteTransferWithdrawOutcome.Withdrawn;
            }
            else if (op._snapshot.IsTerminal)
            {
                outcome = RemoteTransferWithdrawOutcome.AlreadyEnded;
            }
            else
            {
                op._cancelledBeforeApproval = true;
                Update(op, s => s with { State = RemoteTransferState.CancelRequested, CancellationRequested = true });
                RequestStop(op, StopReason.User);
                outcome = RemoteTransferWithdrawOutcome.Withdrawn;
            }
            return Task.FromResult(new RemoteTransferWithdrawResult { Outcome = outcome, Snapshot = CleanupView(op) });
        }
    }

    /// <summary>
    /// 取消与条件撤回交还的快照:撤权后只交还清理所需的状态,路径、会话与失败说明(其中可能带着路径)一律不给。调用方须持有任务锁。
    /// </summary>
    private static RemoteTransferSnapshot CleanupView(UploadOperation op)
        => op.PermissionRevoked ? op._snapshot with { LocalPath = "", RemotePath = "", SessionId = "", ErrorMessage = null } : op._snapshot;

    /// <summary>这一代是在批准生效之前被撤下的(条件撤回或批准前的取消)。</summary>
    private static bool WithdrawnBeforeApproval(UploadOperation op)
    {
        lock (op._gate) return op._cancelledBeforeApproval;
    }

    public Task<RemoteTransferSnapshot> ResumeTransferAsync(string transferId, RemoteTransferResumeOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OperationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.TargetSessionId);
        UploadOperation op = FindTransfer(transferId);
        if (options.ExpectedGeneration <= 0 || options.ExpectedGeneration != Read(op).Generation)
            throw new InvalidOperationException("Transfer generation does not match.");
        lock (_transfersGate)
        {
            if (_receipts.ContainsKey(options.OperationId))
                throw new InvalidOperationException("Operation id was already used for an upload.");
        }
        // 连接接口尚未暴露认证后的服务器密钥、账户与命名空间身份。
        // 地址或会话 id 都不能证明重连后的恢复检查点仍属于同一远端。
        throw new NotSupportedException("This host cannot verify the server identity and remote namespace required for safe resume.");
    }

    private static RemoteTransferSnapshot ReadAuthorized(UploadOperation op)
    {
        lock (op._gate) { DemandSnapshotAccess(op); return op._snapshot; }
    }

    private static void DemandSnapshotAccess(UploadOperation op)
    {
        if (op.PermissionRevoked) throw new PluginPermissionDeniedException("Transfer authorization was revoked.");
    }

    private UploadOperation FindTransfer(string transferId, bool allowRevokedCleanup = false)
    {
        UploadOperation? op;
        lock (_transfersGate)
        {
            if (transferId is null || !_transfers.TryGetValue(transferId, out op))
                throw new FileNotFoundException("Transfer not found.");
        }
        // 归属边界是每插件独立的能力实例;断线后仍可查保守终态,撤销后不能泄漏路径。
        if (!allowRevokedCleanup) DemandSnapshotAccess(op);
        return op;
    }

    private static string NormalizeDestination(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!path.StartsWith('/') || path.EndsWith('/') || path.Contains('\0'))
            throw new ArgumentException("Upload destination must be an absolute file path.", nameof(path));
        var parts = new List<string>();
        foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (parts.Count == 0) throw new ArgumentException("Destination escapes root."); parts.RemoveAt(parts.Count - 1); }
            else
            {
                parts.Add(part);
            }
        }
        if (parts.Count == 0) throw new ArgumentException("Destination must name a file.", nameof(path));
        return "/" + string.Join('/', parts);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial uint GetFinalPathNameByHandle(SafeFileHandle handle,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] char[] path, uint length, uint flags);

    internal static string ResolveUploadSource(string path)
    {
        path = Path.GetFullPath(path);
        string parent = Path.GetDirectoryName(path) ?? throw new IOException("The upload source must name a file.");
        int links = 0;
        return Path.Combine(ResolveUploadDirectory(parent, ref links), Path.GetFileName(path));
    }

    private static string ResolveUploadDirectory(string path, ref int links)
    {
        string root = Path.GetPathRoot(path) ?? throw new IOException("The upload source must be absolute.");
        string current = root;
        foreach (string part in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { current = Path.GetDirectoryName(current) ?? root; continue; }
            var directory = new DirectoryInfo(Path.Combine(current, part));
            if (directory.LinkTarget is { } target)
            {
                if (++links > 40) throw new IOException("Too many links in the upload source path.");
                // 链接内容中的 .. 必须在前面的链接已展开后处理，不能先 GetFullPath 折叠。
                current = ResolveUploadDirectory(Path.IsPathRooted(target) ? target : Path.Combine(current, target), ref links);
            }
            else
            {
                // 目录不在单独报:那是「源找不到」(启动前被拒,见 AsSourceRejection),不是核验失败。
                if (!directory.Exists)
                    throw new DirectoryNotFoundException("The upload source directory does not exist.");
                if (IsLink(directory.FullName, directory.Attributes))
                    throw new IOException("Cannot verify the upload source directory.");
                current = directory.FullName;
            }
        }
        return current;
    }

    /// <summary>
    /// 只接受普通文件,而且在打开之前就拒:没有写端的 FIFO 会让 open 一直阻塞,设备与套接字读不出一份确定的内容,
    /// 有的设备一打开就有副作用 —— 何况这一步还在用户审批之前。Windows 没有这类会卡住 open 的对象,
    /// 设备与管道由打开后的路径核验拒绝。
    /// </summary>
    private static void RejectSpecialSource(string path)
    {
        const uint typeMask = 0xF000, regularFile = 0x8000;   // S_IFMT、S_IFREG
        if (ReadUnixFileMode(path) is { } mode && (mode & typeMask) != regularFile)
            throw new NotSupportedException("Only regular files can be uploaded.");
    }

    /// <summary>
    /// 不跟随链接地取路径的 st_mode。Linux 用 statx(结构各架构一致:stx_mask 在 0、stx_mode 在 28),
    /// macOS 用 64 位 inode 的 lstat(st_mode 在 4;x64 上符号名是 lstat$INODE64)。
    /// 查不了(不存在、没权限、内核或 seccomp 不给 statx、libc 太旧)时为 null:交给正式打开报出原本的错误,
    /// 打开之后还有按句柄的类型复核。
    /// </summary>
    private static uint? ReadUnixFileMode(string path)
    {
        byte[] buffer = new byte[512];   // struct statx 256 字节;macOS 的 struct stat 144 字节
        try
        {
            if (OperatingSystem.IsLinux())
            {
                const int atFdCwd = -100, atSymlinkNoFollow = 0x100;
                const uint statxType = 0x1;
                return LinuxStatX(atFdCwd, path, atSymlinkNoFollow, statxType, buffer) == 0 && (BitConverter.ToUInt32(buffer, 0) & statxType) != 0
                    ? BitConverter.ToUInt16(buffer, 28)
                    : null;
            }
            if (OperatingSystem.IsMacOS())
            {
                int result = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? DarwinLStatInode64(path, buffer) : DarwinLStat(path, buffer);
                return result == 0 ? BitConverter.ToUInt16(buffer, 4) : null;
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { }
        return null;
    }

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LinuxStatX(int directory, string path, int flags, uint mask, [Out] byte[] buffer);

    [LibraryImport("libc", EntryPoint = "lstat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int DarwinLStat(string path, [Out] byte[] buffer);

    [LibraryImport("libc", EntryPoint = "lstat$INODE64", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int DarwinLStatInode64(string path, [Out] byte[] buffer);

    private static void RejectLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if (IsLink(current, File.GetAttributes(current)))
                throw new NotSupportedException("Linked source paths are not supported for stable uploads.");
        }
    }

    /// <summary>
    /// 路径上这一项是不是链接。Windows 上只有名字代理(name surrogate)类的重解析点 —— 符号链接、目录联接、卷挂载点 ——
    /// 会把名字解析重定向到别处;OneDrive 的云文件占位符(IO_REPARSE_TAG_CLOUD_*,「已知文件夹移动」把桌面、文档整个放在里面)、
    /// 重复数据删除这类重解析点只是文件系统过滤器存数据的方式,不改路径解析,不能因此拒绝。
    /// 打开之后按句柄核验最终路径才是真正的防线;标记读不出来按链接算。Unix 上 .NET 只给符号链接报 ReparsePoint,一律算链接。
    /// </summary>
    private static bool IsLink(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) == 0) return false;
        if (!OperatingSystem.IsWindows()) return true;
        // 只要读属性:不参与共享检查、不触发云文件下载;OPEN_REPARSE_POINT 让句柄描述这一项本身,而不是它指向的东西。
        using SafeFileHandle handle = OpenAttributesHandle(path, 0x80, 7, 0, 3, 0x02200000, 0);
        return handle.IsInvalid || IsLink(handle);
    }

    /// <summary>按以 FILE_FLAG_OPEN_REPARSE_POINT 打开的句柄判断(见上);查不了标记按链接算。</summary>
    private static bool IsLink(SafeFileHandle handle)
        => !GetFileInformationByHandleEx(handle, 9, out FileAttributeTagInfo info, (uint)Marshal.SizeOf<FileAttributeTagInfo>())
           || (info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0 && IsNameSurrogate(info.ReparseTag);

    /// <summary>重解析标记是不是名字代理(winnt.h 的 IsReparseTagNameSurrogate,第 29 位)。</summary>
    internal static bool IsNameSurrogate(uint reparseTag) => (reparseTag & 0x20000000) != 0;

    /// <summary>GetFileInformationByHandleEx 的 FileAttributeTagInfo(9)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle OpenAttributesHandle(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileAttributeTagInfo information, uint size);

    private static RemoteTransferSnapshot Read(UploadOperation op) { lock (op._gate) return op._snapshot; }
    private static void Update(UploadOperation op, Func<RemoteTransferSnapshot, RemoteTransferSnapshot> update)
    {
        lock (op._gate)
        {
            op._snapshot = update(op._snapshot) with { Sequence = op._snapshot.Sequence + 1, UpdatedAt = DateTimeOffset.UtcNow };
            TaskCompletionSource changed = op._changed;
            op._changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            changed.TrySetResult();
        }
    }

    private sealed class UploadProgressSink(UploadOperation op) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value)
        {
            long now = Environment.TickCount64;
            lock (op._gate)
            {
                if (op._snapshot.IsTerminal || now - op._lastProgress < 100) return;
                op._lastProgress = now;
                Update(op, s => ApplyTransferProgress(s, value));
            }
        }
    }

    internal static RemoteTransferSnapshot ApplyTransferProgress(RemoteTransferSnapshot snapshot, TransferProgress value) => snapshot with
    {
        TransferredBytes = snapshot.Direction == RemoteTransferDirection.Download || snapshot.TotalBytes < 0
            ? Math.Max(snapshot.TransferredBytes, value.BytesTransferred)
            : Math.Clamp(value.BytesTransferred, snapshot.TransferredBytes, snapshot.TotalBytes),
        SpeedBytesPerSecond = value.SpeedBytesPerSecond > 0 ? value.SpeedBytesPerSecond : null,
        EstimatedTimeRemaining = value.SpeedBytesPerSecond > 0 ? value.EstimatedTimeRemaining : null,
    };

    private async Task RunUploadAsync(UploadOperation op, Guid session, RemoteUploadOptions options)
    {
        // 临时文件是目标同目录下的一个短名字:接在目标名后面要多出 61 字节,目标名一过约 194 字节就超过 NAME_MAX(255 字节)。
        // 64 位随机:同一目录里撞名的概率可以忽略,别人也猜不到名字、抢先占住。
        string destination = Read(op).RemotePath;
        string temporary = destination[..destination.LastIndexOf('/')] + "/.vela-up-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        bool slot = false, writing = false, committing = false, commitRejected = false, refused = false;
        Task? remoteWrite = null;
        Task? commitTask = null;
        UploadSnapshot? diskSnapshot = null;
        // 速率按真正开始传输之后的耗时算:审批对话框、排队与快照准备都不算进去。
        var elapsed = new Stopwatch();
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(op._stop.Token, op._permission?.Revocation ?? CancellationToken.None);
        CancellationToken ct = execution.Token;
        // SFTP 没有撤回:提交请求一发出,服务端照样改名。停止只打断这边的等待,提交本身在停止之后再等一个宽限期的应答,
        // 应答赶上了就按事实收尾,赶不上才是 Unknown。登记在 commitWait 之后声明,先于它释放。
        using var commitWait = new CancellationTokenSource();
        using CancellationTokenRegistration commitGrace = ct.Register(() => commitWait.CancelAfter(StopGracePeriod));
        using var heartbeatStop = new CancellationTokenSource();
        Task heartbeat = TransferHeartbeatAsync(op, heartbeatStop.Token);
        try
        {
            // 阶段与下载同一口径:等宿主确认时是 Approval,批准之后才进 Transfer。插件靠它分辨"还没批准"的任务 ——
            // 用户停止或拒绝时撤掉的只是这些,排队的确认框不再一个个弹(已批准的照常传完)。
            // 回执刚发出、后台流程还没跑到这里就被撤回或取消的,不把 CancelRequested 改回 Starting,也不再往下走。
            Update(op, s => s with { State = s.CancellationRequested ? s.State : RemoteTransferState.Starting, Phase = RemoteTransferPhase.Approval });
            if (uploadPermissions is null || op._permission is null || string.IsNullOrWhiteSpace(pluginId))
                throw new PluginPermissionDeniedException("A trusted upload approval is required.");
            ct.ThrowIfCancellationRequested();
            SshSession target = connections.GetSession(session) ?? throw new PluginSessionNotFoundException(Read(op).SessionId);
            string label = $"{target.ConnectionInfo.Username}@{target.ConnectionInfo.Host}:{target.ConnectionInfo.Port}";
            bool autoApprove = await AutoApproveTransferAsync(options.ApprovalContext, RemoteTransferDirection.Upload, ct).ConfigureAwait(false);
            bool approved = await uploadPermissions.CheckUploadAsync(op._permission, pluginId, label, Read(op).LocalPath,
                Read(op).RemotePath, options.Overwrite, op._sourceLength, ct, () => EnterTransferPhase(op), autoApprove).ConfigureAwait(false);
            if (!approved) throw new PluginPermissionDeniedException("The user denied this upload.");
            ConfirmApprovedOrCancelled(op, ct);
            ValidateSource(op);
            await _uploadSlots.WaitAsync(ct).ConfigureAwait(false);
            slot = true;
            ct.ThrowIfCancellationRequested();
            Resolve(Read(op).SessionId);
            Update(op, s => s with { State = RemoteTransferState.Starting });
            // 提交预检:注定被拒的提交(要覆盖而服务端没有 posix-rename、不覆盖而目标已存在)在写之前就拒,
            // 不白做一份快照、白传一整份字节。只是提示,提交仍是权威;拒绝与提交时的拒绝同一个口径。
            try
            {
                await sftp.CheckUploadCommitAsync(session, destination, options.Overwrite, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsDefiniteCommitRejection(ex))
            {
                refused = true;
                throw;
            }
            Stream uploadSource = op._source;
            if (UseDiskSnapshot)
            {
                diskSnapshot = await CreateUploadSnapshotAsync(op, ct).ConfigureAwait(false);
                uploadSource = diskSnapshot.Stream;
            }
            ValidateSource(op);
            byte[] hash = await SHA256.HashDataAsync(uploadSource, ct).ConfigureAwait(false);
            uploadSource.Position = 0;
            ct.ThrowIfCancellationRequested();
            // 覆盖上传:临时文件一建出来就按被替换文件的权限位(0777 那几位),写入期间不比它宽 —— 以前按 0644(减 umask)新建、
            // 提交前才收窄,覆盖一个 0600 的文件时,写入的那段时间里同机的其他用户读得到新内容。提交时照旧按那一刻的目标补齐属主与全部位。
            uint? createMode = options.Overwrite ? await ReadCreateModeAsync(session, destination, ct).ConfigureAwait(false) : null;
            Update(op, s => s with { State = RemoteTransferState.Running });
            elapsed.Start();
            writing = true;
            remoteWrite = sftp.UploadStreamAsync(session, uploadSource, temporary, op._sourceLength,
                progress: new UploadProgressSink(op), createMode: createMode, cancellationToken: ct);
            await remoteWrite.WaitAsync(ct).ConfigureAwait(false);
            Update(op, s => s with { Phase = RemoteTransferPhase.Verification, TransferredBytes = s.TotalBytes, SpeedBytesPerSecond = null, EstimatedTimeRemaining = null });
            // 优先服务端摘要;纯 SFTP 账号无 exec 时流式回读校验,不把满字节误报为已提交。
            byte[]? actual = null;
            try
            {
                IReadOnlyDictionary<string, string?> hashes = await sftp.ComputeSha256Async(session, [temporary], ct).ConfigureAwait(false);
                if (hashes is not null && hashes.TryGetValue(temporary, out string? text) && text is { Length: 64 })
                    actual = Convert.FromHexString(text);
            }
            catch (NotSupportedException) { }
            catch (FormatException) { }
            if (actual is null)
            {
                await using Stream remote = await sftp.OpenReadAsync(session, temporary, ct).ConfigureAwait(false);
                actual = await SHA256.HashDataAsync(remote, ct).ConfigureAwait(false);
            }
            if (!CryptographicOperations.FixedTimeEquals(hash, actual)) throw new InvalidDataException("Uploaded content verification failed.");
            ct.ThrowIfCancellationRequested();
            Resolve(Read(op).SessionId);
            ValidateSource(op);
            DemandSnapshotAccess(op);
            ct.ThrowIfCancellationRequested();
            Update(op, s => s with { Phase = RemoteTransferPhase.Commit });
            committing = true;
            commitTask = sftp.CommitUploadAsync(session, temporary, Read(op).RemotePath, options.Overwrite, commitWait.Token);
            await commitTask.WaitAsync(ct).ConfigureAwait(false);
            Succeed();
        }
        catch (Exception ex)
        {
            // 取消、到期、断线、撤权只打断了等待,后端任务可能还在跑:先给它一段有界的宽限期真正停下来,再定终态。
            Task? backend = commitTask ?? remoteWrite;
            if (backend is { IsCompleted: false })
                await Task.WhenAny(backend, Task.Delay(StopGracePeriod)).ConfigureAwait(false);
            // 提交应答赶在宽限期内到了:成功就是成功,取消不宣称回滚。
            if (commitTask is { IsCompletedSuccessfully: true })
            {
                Succeed();
            }
            else
            {
                // 后端自己的失败原因比「等待被打断」有用;同步抛出的提交失败就是 ex 本身。
                Exception cause = backend is { IsFaulted: true } ? backend.Exception!.InnerException ?? ex : ex;
                // 只有收不到应答的提交才是 Unknown;根本没发出去或服务端明确拒绝,目标就确认没被提交。
                commitRejected = refused || committing && (commitTask is null or { IsFaulted: true }) && IsDefiniteCommitRejection(cause);
                FailUpload(op, ex, cause, writeStopped: remoteWrite is not { IsCompleted: false },
                    commitUnknown: committing && !commitRejected, commitRejected);
            }
        }
        finally
        {
            try
            {
                heartbeatStop.Cancel();
                await heartbeat.ConfigureAwait(false);
            }
            finally
            {
                // 宽限期内没停下的后端:继续保留稳定源句柄,直到它真正结束;状态保持 Unknown,不冒充已停止。
                if (remoteWrite is not null) { try { await remoteWrite.ConfigureAwait(false); } catch { } }
                if (commitTask is not null) { try { await commitTask.ConfigureAwait(false); } catch { } }
                try
                {
                    if (diskSnapshot is not null) await diskSnapshot.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    try { await op._source.DisposeAsync().ConfigureAwait(false); }
                    finally
                    {
                        try { if (slot) _uploadSlots.Release(); }
                        finally
                        {
                            op._permission?._active = false;
                            lock (op._gate)
                            {
                                op._stopDisposed = true;
                                op._stop.Dispose();
                            }
                        }
                    }
                }
            }
            // 写入与提交到这里都已真正结束(上面等过了)。提交从没发出、或被服务端明确拒绝(迟于宽限期的拒绝也算),
            // 临时文件就只是残留:不论发布时有没有赶上宽限期都尽力清掉,已发布的终态不改写。
            // 提交可能已经发生(改名成功、应答被放弃、通道中断)就绝不删,也不开放恢复。
            if (commitTask is { IsFaulted: true, Exception.InnerException: { } late } && IsDefiniteCommitRejection(late)) commitRejected = true;
            if (writing && (!committing || commitRejected))
            {
                Task? cleanupTask = null;
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    cleanupTask = sftp.DeleteAsync(session, temporary, cancellationToken: cleanup.Token);
                    await cleanupTask.WaitAsync(cleanup.Token).ConfigureAwait(false);
                }
                catch
                {
                    if (cleanupTask is not null)
                    {
                        _ = cleanupTask.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    }
                }
            }
        }

        void Succeed() => Update(op, s => s with
        {
            State = RemoteTransferState.Succeeded,
            TransferredBytes = s.TotalBytes,
            CompletedAt = DateTimeOffset.UtcNow,
            RemoteStopConfirmed = true,
            DestinationCommitConfirmedAbsent = false,
            SpeedBytesPerSecond = elapsed.Elapsed.TotalSeconds > 0 ? s.TotalBytes / elapsed.Elapsed.TotalSeconds : null,
            EstimatedTimeRemaining = TimeSpan.Zero,
        });
    }

    /// <summary>
    /// 上传没能成功收尾时按事实定终态:写入任务已经结束(失败也好、取消也好,后端都已关了句柄)就是确认停写,
    /// 没提交过的临时文件随后清理;提交只有收不到应答才是 Unknown。停止原因按登记的那个分类,
    /// 失败原因清洗后留给插件,异常收成一行、限长后写进宿主日志。
    /// </summary>
    private void FailUpload(UploadOperation op, Exception ex, Exception cause, bool writeStopped, bool commitUnknown, bool commitRejected)
    {
        // 批准生效之前就撤下的(条件撤回、批准前的取消)一律按用户取消收尾 —— 撤回回执已经告诉插件「撤下了、以 Cancelled 收尾」。
        // 同一拍里先一步定下、还没来得及发布的拒绝、断线、到期或撤权让位给它:那一代同样什么都没传,结论只能有一个。
        bool withdrawn = WithdrawnBeforeApproval(op);
        bool revoked = op.PermissionRevoked && !withdrawn;
        bool denied = ex is PluginPermissionDeniedException && !withdrawn;
        // 撤权优先于其它停止原因,与此前的口径一致:撤权后的到期不再记成到期。
        StopReason reason = withdrawn ? StopReason.User : revoked ? StopReason.None : ReadStopReason(op);
        bool stopped = writeStopped && !commitUnknown;
        // 断线记 Failed / session-disconnected,不记 Cancelled(#656 之前记的是取消):Cancelled 是「有人不要这次传输了」,
        // 插件与模型按约定不会再发起;断线不是谁的意愿。走到这一分支说明写入已确认停下、提交没发出或被明确拒绝,
        // 目标确认没被写入(DestinationCommitConfirmedAbsent),重连之后重新上传不会叠加副作用,当作普通失败交还调用方、
        // 由它决定要不要重试是安全的。提交发出后没等到应答的断线不在这里:那是 commit-unknown / Unknown,不得自动重传。
        RemoteTransferState state = reason == StopReason.Timeout ? RemoteTransferState.TimedOut
            : !stopped ? RemoteTransferState.Unknown
            : revoked || denied ? RemoteTransferState.Failed
            : reason is StopReason.User or StopReason.Disposed ? RemoteTransferState.Cancelled
            : RemoteTransferState.Failed;
        string code = revoked ? "permission-revoked"
            : denied ? "permission-denied"
            : commitUnknown ? "commit-unknown"
            : StopCode(reason)
              ?? (commitRejected ? cause is NotSupportedException ? "not-supported" : "commit-rejected"
                  : ex is SourceChangedException ? "source-changed"
                  : ex is InsufficientSnapshotSpaceException ? "insufficient-space" : "upload-failed");
        string message = revoked ? "Upload authorization was revoked."
            : denied ? "A trusted host upload approval was not granted."
            : commitUnknown ? "The destination commit could not be confirmed; do not retry automatically."
            : !stopped ? "The remote write did not confirm it stopped; do not retry automatically."
            : StopText(reason) is { } stop ? stop + "; the destination was not committed."
            : commitRejected ? "The destination commit was refused; the destination was not committed: " + DescribeCause(cause)
            : ex is SourceChangedException ? "The approved source changed; the destination was not committed."
            : ex is InsufficientSnapshotSpaceException ? ex.Message + " Nothing was uploaded; the destination was not committed."
            : ex is InvalidDataException ? "Uploaded content verification failed."
            : "Upload failed: " + DescribeCause(cause);
        Update(op, s => s with
        {
            State = state,
            RemoteStopConfirmed = stopped,
            DestinationCommitConfirmedAbsent = !commitUnknown,
            CompletedAt = DateTimeOffset.UtcNow,
            Resumable = false,
            ErrorCode = code,
            ErrorMessage = message,
        });
        LogTransferEnd(op, state, code, cause);
    }

    /// <summary>覆盖上传的临时文件按什么权限位新建;读不出来就按服务端默认(改动之前的样子)。</summary>
    /// <remarks>
    /// 只是收窄写入期间的暴露面,不是安全提交的一环:提交时 <c>CommitUploadAsync</c> 照旧重新读目标的模式,读不到就拒绝提交。
    /// 所以这里查不出来(没权限 stat、通道一时出错)不该让整次上传失败。
    /// </remarks>
    private async Task<uint?> ReadCreateModeAsync(Guid session, string destination, CancellationToken ct)
    {
        try
        {
            return await sftp.GetUploadCreateModeAsync(session, destination, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.WriteLine($"[RemoteFs:{pluginId}] create mode for the upload temporary unknown: {PeerText.Sanitize(ex.Message)}");
            return null;
        }
    }

    /// <summary>
    /// 提交被明确拒绝:请求根本没发出去(后端不支持,如要覆盖而服务端没有 posix-rename),或服务端回了失败状态
    /// (如不覆盖时普通 RENAME 因目标已存在被拒)。取消、断线、超时都等不到应答,目标是否已被替换无从确认;
    /// SFTP 通道中途断开报的是传输中断,同样没有应答。
    /// </summary>
    private static bool IsDefiniteCommitRejection(Exception ex)
        => ex is NotSupportedException or (VelaSftpOperationException and not VelaSftpTransferInterruptedException);

    /// <summary>
    /// 停止原因:显式登记的(用户取消、断线、停用)优先;令牌被取消却没登记的,只能来自寿命令牌(插件停用、宿主退出)
    /// 或死线。须在停止令牌释放之前读取。
    /// </summary>
    private StopReason ReadStopReason(UploadOperation op)
    {
        lock (op._gate)
        {
            if (op._stopReason != StopReason.None || !op._stop.IsCancellationRequested) return op._stopReason;
        }
        return lifetime.IsCancellationRequested ? StopReason.Disposed : StopReason.Timeout;
    }

    /// <summary>
    /// 确认停下、又不是授权问题时按停止原因给的错误码;没有停止原因(真失败)时为 null。
    /// session-disconnected 配的终态是 Failed 而不是 Cancelled,理由见 <see cref="FailUpload" />(下载同一口径)。
    /// </summary>
    private static string? StopCode(StopReason reason) => reason switch
    {
        StopReason.User => "cancelled",
        StopReason.Timeout => "timeout",
        StopReason.Disconnected => "session-disconnected",
        StopReason.Disposed => "plugin-stopped",
        _ => null,
    };

    private static string? StopText(StopReason reason) => reason switch
    {
        StopReason.User => "The transfer was cancelled",
        StopReason.Timeout => "The transfer deadline was reached",
        StopReason.Disconnected => "The session disconnected",
        StopReason.Disposed => "The plugin or host stopped",
        _ => null,
    };

    /// <summary>
    /// 失败原因进快照:只取异常消息,按不可信文本清洗(控制字符、双向控制符)并限长 —— 它会原样到达插件与模型;
    /// 异常类型、内层与堆栈只写进宿主日志。
    /// </summary>
    private static string DescribeCause(Exception ex) => PeerText.Sanitize(ex.Message);

    /// <summary>宿主日志里收尾原因的长度上限:堆栈与超长消息不能把一行日志撑成一屏。</summary>
    private const int LogCauseLimit = 1024;

    /// <summary>
    /// 非成功收尾写进宿主日志:插件拿到的说明只有清洗过的消息,异常本身(类型、内层与堆栈)留在这里 ——
    /// 同样收成一行、清洗并限长:原样拼进去,消息里的换行就能在日志里伪造出另一条记录。
    /// </summary>
    private void LogTransferEnd(UploadOperation op, RemoteTransferState state, string code, Exception cause)
    {
        RemoteTransferSnapshot snapshot = Read(op);
        // 各种换行(含 U+2028/U+2029,不少查看器把它们当换行)先换成空格,其余控制字符与双向控制符交给 PeerText 换成 ?
        string text = PeerText.Sanitize(cause.ToString().ReplaceLineEndings(" "), LogCauseLimit);
        Trace.WriteLine($"[RemoteFs:{pluginId}] {snapshot.Direction} {snapshot.TransferId} ended {state} ({code}): {text}");
    }

    private static async Task TransferHeartbeatAsync(UploadOperation op, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                lock (op._gate)
                {
                    if (op._snapshot.IsTerminal) return;
                    if (op._snapshot.State == RemoteTransferState.Starting
                        || op._snapshot.State == RemoteTransferState.Running && op._snapshot.TransferredBytes == op._snapshot.TotalBytes)
                    {
                        Update(op, s => s);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private static void RequestStop(UploadOperation op, StopReason reason)
    {
        lock (op._gate)
        {
            if (op._stopDisposed) return;
            // 先到者为准:死线已到或插件已停用时令牌早已取消,不改写成后来的原因。
            if (!op._stop.IsCancellationRequested && op._stopReason == StopReason.None) op._stopReason = reason;
            op._stop.Cancel();
        }
    }

    /// <summary>
    /// 批准生效的那一刻(授权闸登记批准的锁里,见 <see cref="PluginPermissionGate.CheckUploadAsync" /> 的 onApproved)
    /// 把任务从「等确认」推进到「已批准、排队等传输槽」:<see cref="RemoteTransferState.Queued" /> + <see cref="RemoteTransferPhase.Transfer" />。
    /// </summary>
    /// <remarks>
    /// 原先阶段要等批准的回执回到后台任务里才改(下载更晚,要等拿到传输槽),中间这段插件读到的仍是 Approval ——
    /// AI 插件按「Approval 才撤」的口径在停止时撤掉等确认的任务,读到旧阶段就把用户刚批准的那个也撤了
    /// (插件已改为撤前先 GetTransferAsync,但取到的还是 Approval,挡不住)。取消已经登记的不推进:
    /// 那一项随后在 <see cref="ConfirmApprovedOrCancelled" /> 里以 Cancelled 收尾。
    /// 这一步同时记下「这一代的批准已经生效」,条件撤回(<see cref="WithdrawPendingTransferAsync" />)在同一把锁里按它判 PastApproval。
    /// </remarks>
    private static void EnterTransferPhase(UploadOperation op)
    {
        lock (op._gate)
        {
            if (op._snapshot.IsTerminal || op._snapshot.CancellationRequested || op._snapshot.Phase != RemoteTransferPhase.Approval) return;
            op._approvalEffective = true;
            Update(op, s => s with { State = RemoteTransferState.Queued, Phase = RemoteTransferPhase.Transfer });
        }
    }

    /// <summary>
    /// 批准之后、碰源文件与传输槽之前,在任务自己的锁里定下「传还是不传」:已经要求取消的(取消与批准几乎同时到)
    /// 以 Cancelled 收尾、一个字节都不传;否则确保阶段已是 Transfer(授权闸没替我们推进时补上)。
    /// 取消请求(<see cref="CancelTransferAsync" />)与条件撤回(<see cref="WithdrawPendingTransferAsync" />)在同一把锁里登记意图并发出停止,
    /// 所以两边只会有一个先后:取消先到就一个字节都不传(不会「撤了还在传」);批准先到,插件此后读到的就已经是 Transfer 阶段,
    /// 条件撤回也只会答 PastApproval、什么都不改。插件「先读快照、再按代次取消」的老路仍有一个往返的空当,条件撤回没有。
    /// </summary>
    private static void ConfirmApprovedOrCancelled(UploadOperation op, CancellationToken ct)
    {
        lock (op._gate)
        {
            if (op._snapshot.CancellationRequested) throw new OperationCanceledException(ct);
            ct.ThrowIfCancellationRequested();
            EnterTransferPhase(op);
        }
    }

    private static void ValidateSource(UploadOperation op)
    {
        if (op._source.Length != op._sourceLength || File.GetLastWriteTimeUtc(op._source.SafeFileHandle) != op._sourceModified)
            throw new SourceChangedException();
    }

    private void OnTransferSessionDisconnected(SshSession session)
    {
        lock (_transfersGate)
        {
            foreach (UploadOperation op in _transfers.Values)
            {
                if (!op._worker.IsCompleted && op._session == session.SessionId)
                    RequestStop(op, StopReason.Disconnected);
            }
        }
    }

    public void Dispose()
    {
        lock (_transfersGate)
        {
            if (_transfersDisposed) return;
            _transfersDisposed = true;
            if (_disconnectSubscribed) connections.SessionDisconnected -= OnTransferSessionDisconnected;
            foreach (UploadOperation op in _transfers.Values)
            {
                if (!op._worker.IsCompleted)
                {
                    RequestStop(op, StopReason.Disposed);
                    _ = op._worker.ContinueWith(_ => { op._permission?.Dispose(); op._downloadPermission?.Dispose(); }, CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                else { op._permission?.Dispose(); op._downloadPermission?.Dispose(); }
            }
        }
    }
}
