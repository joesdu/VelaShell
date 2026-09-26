using System.Buffers;
using System.Globalization;
using VelaShell.Core.Ssh;
using VelaShell.Ssh.Sftp;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// <see cref="ISftpClientWrapper" /> 的 VelaShell.Ssh 实现。
/// </summary>
/// <param name="connect">在主连接上开 SFTP 的工厂。</param>
public sealed class VelaSftpClientWrapper(Func<CancellationToken, ValueTask<SftpFileSystem>> connect)
    : ISftpClientWrapper
{
    /// <summary>传输每次搬运的块大小。</summary>
    /// <remarks>
    /// 只是**本地**的搬运粒度,与 SFTP 报文大小无关 —— 后者由
    /// <see cref="SftpFileSystem.BlockSize" /> 按服务端能力协商。
    /// </remarks>
    private const int CopyChunkSize = 256 * 1024;

    private readonly Func<CancellationToken, ValueTask<SftpFileSystem>> _connect =
        connect ?? throw new ArgumentNullException(nameof(connect));

    private SftpFileSystem? _fs;
    private bool _disposed;

    /// <inheritdoc />
    public bool IsConnected => !_disposed && _fs is not null;

    /// <inheritdoc />
    /// <remarks>SFTP 复用主连接的通道,这里没有自己的建链超时;保留只为满足契约。</remarks>
    public TimeSpan ConnectionTimeout
    {
        get { ObjectDisposedException.ThrowIf(_disposed, this); return field; }
        set { ObjectDisposedException.ThrowIf(_disposed, this); field = value; }
    } = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public string WorkingDirectory
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return field;
        }
        private set;
    } = "/";

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// 流水线写入的应答顺序不保证与偏移顺序一致,所以中断时**文件长度只代表
    /// 「已确认的最高偏移」**,它之前可能留着读作 0 的空洞。续传前必须回退
    /// 一整个在途写入窗口,使起点之前的数据可信。
    /// </para>
    /// <para>
    /// 窗口大小**按当前会话实测**(在途请求数 × 协商出的块大小),不再写死 2 MB ——
    /// 写死的那个值只对某一个底层库的某一组默认参数成立,换了库或换了服务端就是错的,
    /// 而错的方向是「回退得不够」,也就是续传出一个坏文件。
    /// </para>
    /// <para>
    /// 更好的做法是用库的 <c>SftpFileStream.DurableLength</c>(已**连续**确认的偏移),
    /// 那能精确到字节、完全不用回退。但那要改 <see cref="ISftpClientWrapper" /> 的契约
    /// 与 <c>SftpService</c> 的续传流程,不在这次换库的范围里 —— 记在 feature-plan。
    /// </para>
    /// </remarks>
    public long ResumeSafetyMargin =>
        _fs is { } fs ? (long)SftpOptions.Default.MaxInFlight * fs.BlockSize : 64L * 32 * 1024;

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_fs is not null)
        {
            return;
        }

        try
        {
            _fs = await _connect(cancellationToken).ConfigureAwait(false);

            // 来自 REALPATH ".",比拼 /home/{user} 靠谱 —— 用户的家目录未必在那儿。
            WorkingDirectory = _fs.WorkingDirectory;
        }
        catch (Exception ex) when (SshInterop.Translate(ex, cancellationToken) is { } translated)
        {
            throw translated;
        }
    }

    /// <inheritdoc />
    public Task<IEnumerable<SftpEntry>> ListDirectoryAsync(string path, CancellationToken ct) =>
        GuardedAsync(async () =>
        {
            List<SftpEntry> entries = [];

            // 库这一侧已经做完了「不跟随地列、再并发补上链接目标」:
            // 直接跟随会让「这是个链接」这件事彻底消失,于是删一个指向目录的链接
            // 会变成递归删除目标目录里的东西。
            await foreach (SftpDirectoryEntry entry in
                EnsureConnected().EnumerateDirectoryAsync(path, ct).ConfigureAwait(false))
            {
                entries.Add(MapEntry(entry));
            }
            return (IEnumerable<SftpEntry>)entries;
        }, ct);

    /// <inheritdoc />
    public Task UploadAsync(Stream input, string path, Action<ulong>? uploadCallback = null,
        CancellationToken ct = default) =>
        UploadAsync(input, path, 0, uploadCallback, ct);

    /// <inheritdoc />
    public Task UploadAsync(Stream input, string path, long resumeOffset,
        Action<ulong>? uploadCallback = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return GuardedAsync(async () =>
        {
            SftpFileSystem fs = EnsureConnected();

            await using SftpFileStream remote = resumeOffset > 0
                ? await fs.OpenAppendAsync(path, resumeOffset, cancellationToken: ct).ConfigureAwait(false)
                : await fs.OpenWriteAsync(path, cancellationToken: ct).ConfigureAwait(false);

            if (resumeOffset > 0)
            {
                input.Seek(resumeOffset, SeekOrigin.Begin);
            }

            await CopyAsync(input, remote, resumeOffset, uploadCallback, ct).ConfigureAwait(false);

            // **必须显式冲一次再关。** 流水线写入在 Flush 之前还有在途请求,
            // 不等它们落地就关,表现是「上传显示完成,远端文件尾部却缺字节」。
            await remote.FlushAsync(ct).ConfigureAwait(false);
        }, ct);
    }

    /// <inheritdoc />
    public Task DownloadAsync(string path, Stream output, Action<ulong>? downloadCallback = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);

        return GuardedAsync(async () =>
        {
            await using SftpFileStream remote =
                await EnsureConnected().OpenReadAsync(path, ct).ConfigureAwait(false);

            await CopyAsync(remote, output, 0, downloadCallback, ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
        }, ct);
    }

    /// <summary>按块搬运并报告**累计**字节数(与契约一致,不是增量)。</summary>
    private static async Task CopyAsync(
        Stream source, Stream destination, long startOffset, Action<ulong>? progress, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyChunkSize);
        try
        {
            long transferred = startOffset;
            while (true)
            {
                int read = await source.ReadAsync(buffer.AsMemory(0, CopyChunkSize), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                transferred += read;
                progress?.Invoke((ulong)transferred);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <inheritdoc />
    public Task DeleteFileAsync(string path, CancellationToken ct = default) =>
        GuardedAsync(async () => await EnsureConnected().DeleteFileAsync(path, ct).ConfigureAwait(false), ct);

    /// <inheritdoc />
    /// <remarks>
    /// 删的是**空目录**。递归由 <c>SftpService</c> 自己做(它要逐条报进度、
    /// 还要判断「指向目录的链接」当叶子处理),所以这里不需要递归删。
    /// </remarks>
    public Task DeleteDirectoryAsync(string path, CancellationToken ct = default) =>
        GuardedAsync(async () => await EnsureConnected().DeleteDirectoryAsync(path, ct).ConfigureAwait(false), ct);

    /// <inheritdoc />
    public Task CreateDirectoryAsync(string path, CancellationToken ct = default) =>
        GuardedAsync(async () =>
            await EnsureConnected().CreateDirectoryAsync(path, cancellationToken: ct).ConfigureAwait(false), ct);

    /// <inheritdoc />
    public Task RenameFileAsync(string oldPath, string newPath, CancellationToken ct = default) =>
        GuardedAsync(async () =>
            await EnsureConnected().RenameAsync(oldPath, newPath, overwrite: false, ct).ConfigureAwait(false), ct);

    /// <inheritdoc />
    /// <remarks>
    /// 这一条以前是**假的** —— 上一版底层库没暴露 posix-rename,实现直接转调普通重命名,
    /// 于是「普通 RENAME 被拒、POSIX 变体能过」的那些服务端照样失败。
    /// 现在真的发 <c>posix-rename@openssh.com</c>(服务端支持时),它同时还是原子覆盖。
    /// </remarks>
    public Task PosixRenameFileAsync(string oldPath, string newPath, CancellationToken ct = default) =>
        GuardedAsync(async () =>
        {
            SftpFileSystem fs = EnsureConnected();

            // 能力要先问:不支持时退回普通重命名,而不是抛一个用户看不懂的"不支持"。
            await fs.RenameAsync(oldPath, newPath, fs.Capabilities.HasPosixRename, ct).ConfigureAwait(false);
        }, ct);

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string path, CancellationToken ct = default) =>
        GuardedAsync(async () => await EnsureConnected().ExistsAsync(path, ct).ConfigureAwait(false), ct);

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="mode" /> 按契约是「把三个八进制数字写成十进制」(755、644),
    /// 所以要按 8 进制解回去,不能直接当数值用。
    /// </remarks>
    public Task ChangePermissionsAsync(string path, short mode, CancellationToken ct = default) =>
        GuardedAsync(async () =>
        {
            uint permissions = Convert.ToUInt32(mode.ToString(CultureInfo.InvariantCulture), 8);
            await EnsureConnected().SetPermissionsAsync(path, permissions, ct).ConfigureAwait(false);
        }, ct);

    /// <inheritdoc />
    /// <remarks>
    /// 库那一侧会**先把当前的 atime 取回来**再一并写回 —— SFTP 的 atime 与 mtime
    /// 共用一个标志位,只给一个会把另一个抹成 1970 年。
    /// </remarks>
    public Task SetLastWriteTimeAsync(string path, DateTimeOffset lastWriteTime, CancellationToken ct = default) =>
        GuardedAsync(async () =>
            await EnsureConnected().SetLastWriteTimeAsync(path, lastWriteTime, ct).ConfigureAwait(false), ct);

    /// <inheritdoc />
    /// <remarks>
    /// 返回的 <see cref="SftpFileStream" /> <b>本来就可 Seek</b>,不需要像上一版那样
    /// 显式打开两个互相独立的开关(<c>Seekable</c> + <c>CacheLength</c>)——
    /// 那两个开关缺一个,症状就是「上传一半取消后再传同一个文件必报错」。
    /// </remarks>
    public Task<Stream> OpenAsync(string path, FileMode mode, FileAccess access, CancellationToken ct = default) =>
        GuardedAsync<Stream>(async () =>
        {
            SftpFileSystem fs = EnsureConnected();
            bool canRead = access is FileAccess.Read or FileAccess.ReadWrite;
            bool canWrite = access is FileAccess.Write or FileAccess.ReadWrite;

            if (mode == FileMode.Append)
            {
                long end = await GetFileSizeAsync(path, ct).ConfigureAwait(false);
                return await fs.OpenAppendAsync(path, Math.Max(0, end), cancellationToken: ct).ConfigureAwait(false);
            }

            SftpOpenModes open = (canRead ? SftpOpenModes.Read : SftpOpenModes.None)
                                | (canWrite ? SftpOpenModes.Write : SftpOpenModes.None);

            open |= mode switch
            {
                FileMode.CreateNew => SftpOpenModes.Create | SftpOpenModes.Exclusive,
                // Create / Truncate 都要求截断旧内容,否则新内容比旧文件短时会残留旧尾部。
                FileMode.Create => SftpOpenModes.Create | SftpOpenModes.Truncate,
                FileMode.Truncate => SftpOpenModes.Truncate,
                FileMode.OpenOrCreate => SftpOpenModes.Create,
                _ => SftpOpenModes.None,
            };

            return await fs.OpenAsync(
                path, open, cancellationToken: ct)
                .ConfigureAwait(false);
        }, ct);

    /// <inheritdoc />
    public Task<long> GetFileSizeAsync(string path, CancellationToken ct = default) =>
        GuardedAsync(async () =>
        {
            try
            {
                SftpFileAttributes attributes =
                    await EnsureConnected().GetAttributesAsync(path, ct).ConfigureAwait(false);
                return attributes.HasSize ? (long)attributes.Size : -1L;
            }
            catch (SftpException ex) when (ex.IsNotFound)
            {
                // 契约:不存在返回 -1。
                return -1L;
            }
        }, ct);

    /// <inheritdoc />
    /// <remarks>
    /// 先 lstat(不跟随):只有这样才看得出路径本身是不是链接。是链接再补跟随的 stat
    /// 与 readlink,于是 <see cref="SftpEntry.IsDirectory" /> 仍描述链接指向的对象,
    /// 而删除/复制据 <see cref="SftpEntry.IsSymbolicLink" /> 不沿链接递归。
    /// 非链接仍是一次往返。
    /// </remarks>
    public Task<SftpEntry?> GetEntryAsync(string path, CancellationToken ct = default) =>
        GuardedAsync(async () =>
        {
            SftpFileSystem fs = EnsureConnected();

            SftpFileAttributes link;
            try
            {
                link = await fs.GetLinkAttributesAsync(path, ct).ConfigureAwait(false);
            }
            catch (SftpException ex) when (ex.IsNotFound)
            {
                return null;
            }

            if (!link.IsSymbolicLink)
            {
                return MapEntry(path, link, isSymbolicLink: false, linkTarget: null);
            }

            string? target = null;
            try
            {
                target = await fs.ReadSymbolicLinkAsync(path, ct).ConfigureAwait(false);
            }
            catch (SftpException)
            {
                // 读不到目标文本(权限、服务端不支持 readlink)不影响条目本身。
            }

            try
            {
                SftpFileAttributes resolved = await fs.GetAttributesAsync(path, ct).ConfigureAwait(false);
                return MapEntry(path, resolved, isSymbolicLink: true, target);
            }
            catch (SftpException)
            {
                // 断链:保留链接自身的属性,IsDirectory 为 false。
                // **不能返回 null** —— 链接本身是存在的,删除它不能先报"找不到"。
                return MapEntry(path, link, isSymbolicLink: true, target);
            }
        }, ct);

    /// <inheritdoc />
    /// <remarks>
    /// 参数顺序按人话来(先在哪建、再指向哪)。OpenSSH 服务端把 SSH_FXP_SYMLINK 的两个参数
    /// 实现反了(bugzilla #861),库那一侧已经按 OpenSSH 的顺序发包 —— 这里**不要**再对调一次。
    /// </remarks>
    public Task CreateSymbolicLinkAsync(string linkPath, string targetPath, CancellationToken ct = default) =>
        GuardedAsync(async () =>
            await EnsureConnected().CreateSymbolicLinkAsync(linkPath, targetPath, ct).ConfigureAwait(false), ct);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        SftpFileSystem? target = Interlocked.Exchange(ref _fs, null);
        if (target is not null)
        {
            await DisposeQuietlyAsync(target).ConfigureAwait(false);
        }
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------ 映射

    internal static SftpEntry MapEntry(SftpDirectoryEntry entry) =>
        MapEntry(entry.FullPath, entry.Attributes, entry.IsSymbolicLink, entry.LinkTarget, entry.Name);

    internal static SftpEntry MapEntry(
        string fullPath, SftpFileAttributes attributes, bool isSymbolicLink, string? linkTarget, string? name = null)
    {
        // PermissionBits 已经去掉了高位的文件类型（0xF000）——
        // 直接用 Permissions 在这九位上结果一样，但读起来像是在碰类型位。
        uint permissions = attributes.PermissionBits;

        return new SftpEntry
        {
            Name = name ?? Path.GetFileName(fullPath),
            FullName = fullPath,
            Length = (long)attributes.Size,
            IsDirectory = attributes.IsDirectory,
            IsSymbolicLink = isSymbolicLink,
            LinkTarget = linkTarget,

            // 必须 LocalDateTime 而非 .DateTime:后者把 DateTimeOffset 的偏移剥掉,留下
            // "UTC 墙钟数 + Kind=Unspecified" —— 文件浏览器显示成 +0 时区,下载保留时间戳
            // (File.SetLastWriteTime 按本地解读)还会再错一次时差。
            LastWriteTime = attributes.LastWriteTime.LocalDateTime,
            UserId = (int)attributes.UserId,
            GroupId = (int)attributes.GroupId,

            OwnerCanRead = (permissions & 0b100_000_000) != 0,
            OwnerCanWrite = (permissions & 0b010_000_000) != 0,
            OwnerCanExecute = (permissions & 0b001_000_000) != 0,
            GroupCanRead = (permissions & 0b000_100_000) != 0,
            GroupCanWrite = (permissions & 0b000_010_000) != 0,
            GroupCanExecute = (permissions & 0b000_001_000) != 0,
            OthersCanRead = (permissions & 0b000_000_100) != 0,
            OthersCanWrite = (permissions & 0b000_000_010) != 0,
            OthersCanExecute = (permissions & 0b000_000_001) != 0,
        };
    }

    // ------------------------------------------------------------ 管道

    private SftpFileSystem EnsureConnected()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _fs ?? throw new InvalidOperationException("Not connected.");
    }

    /// <summary>
    /// 统一异常翻译:释放竞态导致的 NRE 归一为 <see cref="ObjectDisposedException" />,
    /// 其余库异常经 <see cref="SshInterop.Translate" /> 翻译为 Core 中立异常。
    /// </summary>
    private async Task<T> GuardedAsync<T>(Func<Task<T>> operation, CancellationToken ct = default)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (NullReferenceException) when (IsTornDown())
        {
            throw new ObjectDisposedException(nameof(VelaSftpClientWrapper));
        }
        catch (Exception ex) when (SshInterop.Translate(ex, ct) is { } translated)
        {
            throw translated;
        }
    }

    private async Task GuardedAsync(Func<Task> operation, CancellationToken ct = default) =>
        await GuardedAsync(async () =>
        {
            await operation().ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);

    private bool IsTornDown()
    {
        if (_disposed)
        {
            return true;
        }
        try { return _fs is null; } catch { return true; }
    }

    private static async ValueTask DisposeQuietlyAsync(SftpFileSystem fs)
    {
        try
        {
            await fs.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 释放路径不抛:通道可能已经随连接一起没了。
        }
    }
}
