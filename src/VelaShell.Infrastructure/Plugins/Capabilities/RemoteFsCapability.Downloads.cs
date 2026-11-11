using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using VelaShell.Core.Models;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.RemoteFs;

namespace VelaShell.Infrastructure.Plugins.Capabilities;

internal sealed partial class RemoteFsCapability
{
    // Windows 走 NTFS 句柄钉住的目录链,Linux / macOS(x64、arm64)走固定目录 fd 的提交;其它平台照旧拒绝,不退回按路径的检查。
    public bool SupportsDownloadOperations => OperatingSystem.IsWindows() || PosixDownloadTarget.IsSupported || DownloadTargetFactory is not null;

    /// <summary>
    /// 测试开关:注入落盘目标,驱动真实目标难以复现的收尾分支(与 <see cref="UseDiskSnapshot" /> 同一用意)。
    /// 生产代码从不设置;为 null 时 Windows 用固定本地 NTFS 目标,Linux / macOS 用 <see cref="PosixDownloadTarget" />。
    /// </summary>
    internal Func<string, bool, IDownloadTarget>? DownloadTargetFactory { get; init; }

    /// <summary>下载落盘目标:独占创建临时文件,校验后原子提交;未提交的临时文件随释放清除,清不掉时释放抛出。</summary>
    internal interface IDownloadTarget : IAsyncDisposable
    {
        /// <summary>独占创建临时文件并交出写入流;流归目标所有。</summary>
        FileStream CreateTemporary();

        /// <summary>把临时文件原子提交为最终目标;不覆盖时目标已存在必须拒绝。</summary>
        void Commit(bool overwrite);
    }

    public Task<RemoteTransferSnapshot> StartDownloadAsync(string sessionId, string remotePath, string localPath,
        RemoteDownloadOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OperationId);
        if (options.OperationId.Length > 256) throw new ArgumentException("Operation id is too long.", nameof(options));
        if (options.Timeout is { } timeout && (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(options));
        string local = Path.GetFullPath(localPath);
        string remote = NormalizeDestination(remotePath);
        lock (_transfersGate)
        {
            ObjectDisposedException.ThrowIf(_transfersDisposed || lifetime.IsCancellationRequested, this);
            if (_receipts.TryGetValue(options.OperationId, out StartReceipt? previous))
            {
                if (previous.Operation._snapshot.Direction != RemoteTransferDirection.Download || previous.Session != sessionId || previous.Local != local || previous.Remote != remote || !Equals(previous.Options, options))
                    throw new InvalidOperationException("Operation id was already used with different parameters or direction.");
                return Task.FromResult(ReadAuthorized(previous.Operation));
            }
            if (!SupportsDownloadOperations) throw new PlatformNotSupportedException("Safe background download commit is unavailable on this platform.");
            Guid session = Resolve(sessionId);
            ThrowIfTransferQuotaReached("download");
            var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            if (options.Timeout is { } budget) stop.CancelAfter(budget);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var op = new UploadOperation(new()
            {
                TransferId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                OperationId = options.OperationId,
                SessionId = sessionId,
                LocalPath = local,
                RemotePath = remote,
                Direction = RemoteTransferDirection.Download,
                Phase = RemoteTransferPhase.Approval,
                State = RemoteTransferState.Queued,
                Generation = 1,
                Sequence = 1,
                TotalBytes = -1,
                CreatedAt = now,
                UpdatedAt = now,
                DestinationCommitConfirmedAbsent = true,
            }, session, null, stop);
            if (uploadPermissions is not null && !string.IsNullOrWhiteSpace(pluginId))
                op._downloadPermission = uploadPermissions.CreateDownloadLease(pluginId);
            if (!_disconnectSubscribed)
            {
                connections.SessionDisconnected += OnTransferSessionDisconnected;
                _disconnectSubscribed = true;
            }
            _transfers.Add(op._snapshot.TransferId, op);
            _receipts.Add(options.OperationId, new(sessionId, local, remote, options, op));
            op._worker = Task.Run(() => RunDownloadAsync(op, session, options), CancellationToken.None);
            _ = op._worker.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return Task.FromResult(Read(op));
        }
    }

    private async Task RunDownloadAsync(UploadOperation op, Guid session, RemoteDownloadOptions options)
    {
        bool slot = false, committed = false;
        Task? downloadWrite = null;
        IDownloadTarget? target = null;
        // 速率按真正开始传输之后的耗时算:审批对话框与排队等待不算进去。
        var elapsed = new Stopwatch();
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(op._stop.Token, op._downloadPermission?.Revocation ?? CancellationToken.None);
        CancellationToken ct = execution.Token;
        using var heartbeatStop = new CancellationTokenSource();
        Task heartbeat = TransferHeartbeatAsync(op, heartbeatStop.Token);
        try
        {
            // 与上传同理:后台流程还没跑到这里就被撤回或取消的,保留 CancelRequested,连远端源也不去 stat。
            Update(op, s => s with { State = s.CancellationRequested ? s.State : RemoteTransferState.Starting, Phase = RemoteTransferPhase.Approval });
            if (uploadPermissions is null || op._downloadPermission is null || string.IsNullOrWhiteSpace(pluginId))
                throw new PluginPermissionDeniedException("A trusted download approval is required.");
            ct.ThrowIfCancellationRequested();
            // Pin and verify the whole local directory chain before approval, without creating content.
            target = DownloadTargetFactory?.Invoke(Read(op).LocalPath, options.Overwrite)
                ?? (OperatingSystem.IsWindows() ? WindowsDownloadTarget.Open(Read(op).LocalPath, options.Overwrite) : OpenPosixDownloadTarget(op, options.Overwrite));
            RemoteFileInfo source = await sftp.GetFileInfoAsync(session, Read(op).RemotePath, ct).ConfigureAwait(false);
            ValidateDownloadSource(source);
            Update(op, s => s with { TotalBytes = source.Size });
            SshSession connection = connections.GetSession(session) ?? throw new PluginSessionNotFoundException(Read(op).SessionId);
            string label = $"{connection.ConnectionInfo.Username}@{connection.ConnectionInfo.Host}:{connection.ConnectionInfo.Port}";
            bool autoApprove = await AutoApproveTransferAsync(options.ApprovalContext, RemoteTransferDirection.Download, ct).ConfigureAwait(false);
            // 批准生效与阶段推进在授权闸的同一把锁里(见 EnterTransferPhase):已批准、在等传输槽的下载报 Transfer,
            // 插件停止时按「Approval 才撤」的口径不会再把它当成还在等确认的撤掉;条件撤回在同一把任务锁里判先后。
            if (!await uploadPermissions.CheckDownloadAsync(op._downloadPermission, pluginId, label, Read(op).RemotePath,
                Read(op).LocalPath, options.Overwrite, source.Size, ct, () => EnterTransferPhase(op), autoApprove).ConfigureAwait(false))
            {
                throw new PluginPermissionDeniedException("The user denied this download.");
            }

            ConfirmApprovedOrCancelled(op, ct);
            await _uploadSlots.WaitAsync(ct).ConfigureAwait(false);
            slot = true;
            ct.ThrowIfCancellationRequested();
            DemandSnapshotAccess(op);
            Resolve(Read(op).SessionId);
            RemoteFileInfo before = await sftp.GetFileInfoAsync(session, Read(op).RemotePath, ct).ConfigureAwait(false);
            if (!SameDownloadSource(source, before)) throw new SourceChangedException();
            FileStream sink = target.CreateTemporary();
            Update(op, s => s with { State = RemoteTransferState.Running });
            elapsed.Start();
            downloadWrite = sftp.DownloadStreamAsync(session, Read(op).RemotePath, sink, new UploadProgressSink(op), ct);
            await downloadWrite.WaitAsync(ct).ConfigureAwait(false);
            Update(op, s => s with { Phase = RemoteTransferPhase.Verification, TransferredBytes = sink.Length, SpeedBytesPerSecond = null, EstimatedTimeRemaining = null });
            if (sink.Length != source.Size) throw new InvalidDataException("Downloaded length does not match the remote source.");
            RemoteFileInfo after = await sftp.GetFileInfoAsync(session, Read(op).RemotePath, ct).ConfigureAwait(false);
            if (!SameDownloadSource(source, after)) throw new SourceChangedException();
            // Use the existing digest capability when available. Pure SFTP accounts need no exec;
            // there is no mandatory second full-file download and this is not a remote snapshot.
            IReadOnlyDictionary<string, string?>? hashes = null;
            try { hashes = await sftp.ComputeSha256Async(session, [Read(op).RemotePath], ct).ConfigureAwait(false); }
            catch (NotSupportedException) { }
            if (hashes is not null && hashes.TryGetValue(Read(op).RemotePath, out string? digest) && digest is { Length: 64 })
            {
                byte[]? expected = null;
                try { expected = Convert.FromHexString(digest); } catch (FormatException) { }
                if (expected is not null)
                {
                    sink.Position = 0;
                    byte[] actual = await SHA256.HashDataAsync(sink, ct).ConfigureAwait(false);
                    if (!CryptographicOperations.FixedTimeEquals(expected, actual)) throw new InvalidDataException("Downloaded content verification failed.");
                }
            }
            await sink.FlushAsync(ct).ConfigureAwait(false);
            sink.Flush(flushToDisk: true);
            ct.ThrowIfCancellationRequested();
            Resolve(Read(op).SessionId);
            DemandSnapshotAccess(op);
            Update(op, s => s with { Phase = RemoteTransferPhase.Commit });
            ct.ThrowIfCancellationRequested();
            DemandSnapshotAccess(op);
            // The synchronous NTFS rename has a definite native return. A cancellation racing it
            // may lose to commit; success then remains success, not a promise to roll it back.
            target.Commit(options.Overwrite);
            committed = true;
            await target.DisposeAsync().ConfigureAwait(false);
            target = null;
            Update(op, s => s with
            {
                State = RemoteTransferState.Succeeded,
                CompletedAt = DateTimeOffset.UtcNow,
                RemoteStopConfirmed = true,
                DestinationCommitConfirmedAbsent = false,
                TransferredBytes = s.TotalBytes,
                SpeedBytesPerSecond = elapsed.Elapsed.TotalSeconds > 0 ? s.TotalBytes / elapsed.Elapsed.TotalSeconds : null,
                EstimatedTimeRemaining = TimeSpan.Zero,
            });
        }
        catch (Exception ex)
        {
            // 与上传同理:取消、到期、断线、撤权只打断了等待,后端可能还在往临时文件里写。先给它一段有界的宽限期停下来,
            // 停下了才关掉并删掉临时文件、确认停写;宽限期内仍没停(后端不理取消),照旧尽快发布未确认的结果,
            // 流、目录钉与共享槽保留到它真正退出。
            if (downloadWrite is { IsCompleted: false })
                await Task.WhenAny(downloadWrite, Task.Delay(StopGracePeriod)).ConfigureAwait(false);
            bool stopped = downloadWrite is null || downloadWrite.IsCompleted;
            bool cleaned = stopped;
            if (stopped && target is not null)
            {
                try { await target.DisposeAsync().ConfigureAwait(false); }
                catch { cleaned = false; }
                target = null;
            }
            // 后端自己的失败原因比「等待被打断」有用。
            Exception cause = downloadWrite is { IsFaulted: true } ? downloadWrite.Exception!.InnerException ?? ex : ex;
            // 撤权优先于其它停止原因;断线、停用不再被推断成到期或用户取消。确认停写、临时文件清掉了的断线记
            // Failed / session-disconnected 而不是 Cancelled:本地目标确认没被提交,重连后重新下载是安全的(与上传同一口径,见 FailUpload)。
            // 批准生效之前就撤下的一律按用户取消收尾(理由同 FailUpload)。
            bool withdrawn = WithdrawnBeforeApproval(op);
            bool revoked = op.PermissionRevoked && !withdrawn;
            bool denied = ex is PluginPermissionDeniedException && !withdrawn;
            StopReason reason = withdrawn ? StopReason.User : revoked ? StopReason.None : ReadStopReason(op);
            RemoteTransferState state = committed ? RemoteTransferState.Unknown : reason == StopReason.Timeout ? RemoteTransferState.TimedOut
                : !cleaned ? RemoteTransferState.Unknown : revoked || denied ? RemoteTransferState.Failed
                : reason is StopReason.User or StopReason.Disposed ? RemoteTransferState.Cancelled : RemoteTransferState.Failed;
            string code = revoked ? "permission-revoked" : denied ? "permission-denied"
                : committed ? "commit-unknown" : !cleaned ? "cleanup-unknown"
                : StopCode(reason) ?? (ex is SourceChangedException ? "source-changed" : ex is NotSupportedException ? "not-supported" : "download-failed");
            Update(op, s => s with
            {
                State = state,
                CompletedAt = DateTimeOffset.UtcNow,
                RemoteStopConfirmed = cleaned,
                DestinationCommitConfirmedAbsent = !committed,
                Resumable = false,
                SpeedBytesPerSecond = null,
                EstimatedTimeRemaining = null,
                ErrorCode = code,
                ErrorMessage = committed ? "The destination was committed but final completion could not be confirmed; do not retry automatically."
                    : revoked ? "Download authorization was revoked."
                    : denied ? "A trusted host download approval was not granted."
                    : !cleaned ? "The download did not confirm it stopped and cleaned up; the destination was not committed; do not retry automatically."
                    : StopText(reason) is { } stop ? stop + "; the destination was not committed."
                    : ex is SourceChangedException ? "The remote source changed; the destination was not committed."
                    : "Download did not complete; the destination was not committed: " + DescribeCause(cause),
            });
            LogTransferEnd(op, state, code, cause);
        }
        finally
        {
            heartbeatStop.Cancel();
            await heartbeat.ConfigureAwait(false);
            if (downloadWrite is not null) { try { await downloadWrite.ConfigureAwait(false); } catch { } }
            try
            {
                if (target is not null) await target.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                Update(op, s => s with
                {
                    State = RemoteTransferState.Unknown,
                    RemoteStopConfirmed = false,
                    ErrorCode = "cleanup-unknown",
                    ErrorMessage = "The task temporary cleanup could not be confirmed."
                });
            }
            finally
            {
                if (slot) _uploadSlots.Release();
                op._downloadPermission?._active = false;
                lock (op._gate) { op._stopDisposed = true; op._stop.Dispose(); }
            }
        }
    }

    private static void ValidateDownloadSource(RemoteFileInfo source)
    {
        if (source.IsDirectory || source.IsSymbolicLink || source.Size < 0)
            throw new NotSupportedException("Download source must be a regular file with a known length.");
    }

    private static bool SameDownloadSource(RemoteFileInfo left, RemoteFileInfo right)
        => !right.IsDirectory && !right.IsSymbolicLink && left.Size == right.Size && left.LastModified == right.LastModified;

    /// <summary>
    /// Windows local NTFS only: every ancestor is held (with traverse access, so the share mode is enforced) without delete
    /// sharing, so no ancestor can be renamed, moved or deleted while the download is in flight. The exclusively-created
    /// temporary is verified by handle to live in the pinned parent and is renamed relative to the pinned parent handle,
    /// or deleted by its handle; final paths are never opened for writing.
    /// Unsupported filesystems/platforms fail closed, rather than substituting path-based preflight checks.
    /// </summary>
    private sealed partial class WindowsDownloadTarget : IDownloadTarget
    {
        private const string TemporaryPrefix = ".vela-download-";
        private readonly List<SafeFileHandle> _directories = [];
        private readonly string _destination;

        /// <summary>按句柄核验过的父目录最终路径(最后钉住的那一级)。</summary>
        private string _parent = "";
        private FileStream? _stream;
        private bool _committed;
        private WindowsDownloadTarget(string destination) => _destination = destination;

        internal static WindowsDownloadTarget Open(string destination, bool overwrite)
        {
            if (!OperatingSystem.IsWindows() || destination.StartsWith(@"\\", StringComparison.Ordinal) || destination.IndexOf(':', 2) >= 0)
                throw new PlatformNotSupportedException("Safe download requires an ordinary local NTFS path.");
            string root = Path.GetPathRoot(destination) ?? throw new IOException("Destination must be absolute.");
            var drive = new DriveInfo(root);
            if (drive.DriveType != DriveType.Fixed || !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                throw new PlatformNotSupportedException("Safe download currently requires a fixed local NTFS volume.");
            string name = Path.GetFileName(destination);
            if (string.IsNullOrWhiteSpace(name) || name.EndsWith('.') || name.EndsWith(' ')) throw new IOException("Invalid destination name.");
            var target = new WindowsDownloadTarget(destination);
            try
            {
                string parent = Path.GetDirectoryName(destination) ?? throw new IOException("Destination must name a file.");
                string current = root;
                target.PinDirectory(current);
                foreach (string part in parent[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (part.EndsWith('.') || part.EndsWith(' ')) throw new IOException("Ambiguous directory name.");
                    current = Path.Combine(current, part);
                    target.PinDirectory(current);
                }
                if (File.Exists(destination) || Directory.Exists(destination))
                {
                    FileAttributes attributes = File.GetAttributes(destination);
                    if ((attributes & FileAttributes.Directory) != 0 || IsLink(destination, attributes))
                        throw new IOException("The destination must not be a directory or link.");
                    if (!overwrite) throw new IOException("The destination already exists.");
                }
                SweepOrphans(target._parent);
                return target;
            }
            catch { foreach (SafeFileHandle handle in target._directories) handle.Dispose(); throw; }
        }

        private void PinDirectory(string path)
        {
            // 遍历 + 读属性(FILE_TRAVERSE | FILE_READ_ATTRIBUTES):只读属性的句柄不参与共享检查,共享模式形同虚设,
            // 带上遍历权限才算数。共享读写、不共享删除:祖先改不了名、挪不走、删不掉;在目录里建临时文件、
            // 提交改名时打开目标目录(要读写共享)照常。
            SafeFileHandle handle = CreateFileW(path, 0xA0, 3, 0, 3, 0x02200000, 0);
            if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Cannot lock the destination directory chain.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error())); }
            try
            {
                string? actual = FinalPath(handle);
                if (actual is null || !string.Equals(Path.TrimEndingDirectorySeparator(actual), Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase)
                    || IsLink(handle))
                {
                    throw new IOException("Destination directory identity cannot be verified.");
                }

                _directories.Add(handle);
                _parent = Path.TrimEndingDirectorySeparator(actual);
            }
            catch { handle.Dispose(); throw; }
        }

        /// <summary>
        /// 清掉崩溃留下的临时文件:宿主崩在下载中途时没有关闭即删,`.vela-download-` 加 48 位十六进制的文件会一直留在用户选的目录里。
        /// 只删能以「可删除、不共享」打开的 —— 活着的临时文件被它的 worker 以不共享方式占着,打不开,不会被误删;
        /// 不跟随链接(删的是链接本身),目录打不开;名字对不上的一律不碰。尽力而为,任何失败都只是跳过。
        /// </summary>
        private static void SweepOrphans(string directory)
        {
            try
            {
                foreach (string path in Directory.EnumerateFiles(directory, TemporaryPrefix + "*"))
                {
                    // 与 CreateTemporary 起的名字逐字对得上才算:前缀 + 48 位大写十六进制。
                    string name = Path.GetFileName(path);
                    if (name.Length != TemporaryPrefix.Length + 48 || !name.StartsWith(TemporaryPrefix, StringComparison.Ordinal)
                        || !name[TemporaryPrefix.Length..].All(char.IsAsciiHexDigitUpper))
                    {
                        continue;
                    }

                    using SafeFileHandle orphan = CreateFileW(path, 0x00010000, 0, 0, 3, 0x00200000, 0);
                    if (!orphan.IsInvalid) MarkForDeletion(orphan);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        public FileStream CreateTemporary()
        {
            string name = TemporaryPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            const uint fileFlagOverlapped = 0x40000000;
            SafeFileHandle handle = CreateFileW(Path.Combine(Path.GetDirectoryName(_destination)!, name), 0xC0010000, 0, 0, 1, fileFlagOverlapped, 0);
            if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Cannot exclusively create the download temporary file."); }
            // 祖先都钉着、路径换不了;仍按句柄核一次它确实建在钉住的那个目录里,不在就删掉自己建的这个、拒绝。
            if (!string.Equals(FinalPath(handle), Path.Combine(_parent, name), StringComparison.OrdinalIgnoreCase))
            {
                MarkForDeletion(handle);
                handle.Dispose();
                throw new IOException("The download temporary file was not created in the locked destination directory.");
            }
            _stream = new FileStream(handle, FileAccess.ReadWrite, 128 * 1024, isAsync: true);
            return _stream;
        }

        public void Commit(bool overwrite)
        {
            if (_stream is null) throw new InvalidOperationException();
            // 相对钉住的父目录句柄改名、只给末级名字:不再按完整路径重新解析一遍目标目录。
            // 走 NtSetInformationFile:Win32 的 SetFileInformationByHandle 把名字当 DOS 路径再转一遍,带 RootDirectory 时报参数错误(87)。
            // FileNameLength excludes the UTF-16 terminator.
            byte[] name = Encoding.Unicode.GetBytes(Path.GetFileName(_destination) + '\0');
            int root = IntPtr.Size, offset = IntPtr.Size == 8 ? 20 : 12;
            SafeFileHandle parent = _directories[^1];
            nint info = Marshal.AllocHGlobal(offset + name.Length);
            bool referenced = false;
            try
            {
                parent.DangerousAddRef(ref referenced);
                for (int i = 0; i < offset; i++) Marshal.WriteByte(info, i, 0);
                Marshal.WriteByte(info, 0, overwrite ? (byte)1 : (byte)0);
                Marshal.WriteIntPtr(info, root, parent.DangerousGetHandle());
                Marshal.WriteInt32(info, offset - 4, name.Length - sizeof(char));
                Marshal.Copy(name, 0, info + offset, name.Length);
                // FileRenameInformation(10)。即便句柄是重叠方式打开的,这个调用也同步返回最终结果。
                int status = NtSetInformationFile(_stream.SafeFileHandle, out _, info, (uint)(offset + name.Length), 10);
                if (status < 0)
                    throw new IOException("Atomic download commit was rejected.", new System.ComponentModel.Win32Exception(RtlNtStatusToDosError(status)));
                _committed = true;
            }
            finally
            {
                if (referenced) parent.DangerousRelease();
                Marshal.FreeHGlobal(info);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Exception? cleanupFailure = null;
            try
            {
                if (_stream is not null && !_stream.SafeFileHandle.IsClosed)
                {
                    if (!_committed && !MarkForDeletion(_stream.SafeFileHandle))
                        cleanupFailure = new IOException("The task temporary cleanup could not be confirmed.");
                    await _stream.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally { foreach (SafeFileHandle handle in _directories) handle.Dispose(); }
            if (cleanupFailure is not null) throw cleanupFailure;
        }

        /// <summary>按句柄标记删除(FileDispositionInfo):关掉最后一个句柄时删掉的就是这个对象,不按路径再找一遍。</summary>
        private static bool MarkForDeletion(SafeFileHandle handle)
        {
            nint disposition = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(disposition, 1);
                return SetFileInformationByHandle(handle, 4, disposition, 4);
            }
            finally { Marshal.FreeHGlobal(disposition); }
        }

        /// <summary>句柄的最终路径(去掉 <c>\\?\</c> 前缀);查不出来为 null。</summary>
        private static string? FinalPath(SafeFileHandle handle)
        {
            char[] buffer = new char[32768];
            uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0 || length >= buffer.Length) return null;
            string actual = new(buffer, 0, (int)length);
            return actual.StartsWith(@"\\?\", StringComparison.Ordinal) ? actual[4..] : actual;
        }

        [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, nint information, uint size);

        /// <summary>IO_STATUS_BLOCK:Status 与 Pointer 共用第一个指针宽的槽。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct IoStatusBlock
        {
            public nint Status;
            public nint Information;
        }

        [LibraryImport("ntdll.dll")]
        private static partial int NtSetInformationFile(SafeFileHandle file, out IoStatusBlock status, nint information, uint length, int informationClass);

        [LibraryImport("ntdll.dll")]
        private static partial int RtlNtStatusToDosError(int status);
    }
}
