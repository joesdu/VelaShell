using Tmds.Ssh;
using VelaShell.Core.Ssh;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// Wraps a <see cref="SftpClient" /> to provide a common interface for SFTP operations, with exception translation and progress reporting.
/// </summary>
/// <param name="clientFactory"></param>
public sealed class TmdsSftpClientWrapper(Func<Task<SftpClient>> clientFactory) : ISftpClientWrapper
{
    private readonly Func<Task<SftpClient>> _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    private SftpClient? _client;
    private bool _disposed;

    /// <summary>
    /// Gets a value indicating whether the SFTP client is currently connected and not disposed.
    /// </summary>
    public bool IsConnected => !_disposed && _client is not null;

    /// <summary>
    /// Gets or sets the connection timeout for the SFTP client. The default value is 10 seconds.
    /// </summary>
    public TimeSpan ConnectionTimeout
    {
        get { ObjectDisposedException.ThrowIf(_disposed, this); return field; }
        set { ObjectDisposedException.ThrowIf(_disposed, this); field = value; }
    } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets the current working directory of the SFTP client. The default value is "/".
    /// </summary>
    public string WorkingDirectory
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return field;
        }

        private set;
    } = "/";

    /// <summary>
    /// Connects to the SFTP server using the provided client factory. If already connected, this method does nothing.
    /// If an exception occurs during connection, it is translated to a more meaningful exception using <see cref="TmdsSshInterop.Translate" />.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_client is not null) return;
        try
        {
            _client = await _clientFactory().ConfigureAwait(false);
            WorkingDirectory = _client.WorkingDirectory.Path;
        }
        catch (Exception ex) when (TmdsSshInterop.Translate(ex, cancellationToken) is { } translated) { throw translated; }
    }

    /// <summary>
    /// Disconnects from the SFTP server and disposes of the underlying client. If already disconnected or disposed, this method does nothing.
    /// </summary>
    public void Disconnect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _client?.Dispose(); _client = null;
    }

    /// <summary>
    /// Lists the entries in the specified directory on the SFTP server. If the client is not connected, an <see cref="InvalidOperationException" /> is thrown.
    /// </summary>
    /// <param name="path"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    public Task<IEnumerable<SftpEntry>> ListDirectoryAsync(string path, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync(() => ListEntriesAsync(EnsureClient(), path, ct), ct);
    }

    /// <summary>
    /// Uploads a file to the specified path on the SFTP server. If the client is not connected, an <see cref="InvalidOperationException" /> is thrown.
    /// </summary>
    /// <param name="input"></param>
    /// <param name="path"></param>
    /// <param name="cb"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    public Task UploadAsync(Stream input, string path, Action<ulong>? cb = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync(async () => await EnsureClient()
            .UploadFileAsync(input, path, overwrite: true, progress: ToProgress(cb), cancellationToken: ct)
            .ConfigureAwait(false), ct);
    }

    /// <summary>
    /// Downloads a file from the specified path on the SFTP server to the provided output stream. If the client is not connected, an <see cref="InvalidOperationException" /> is thrown.
    /// </summary>
    /// <param name="path"></param>
    /// <param name="output"></param>
    /// <param name="cb"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    public Task DownloadAsync(string path, Stream output, Action<ulong>? cb = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync(async () => await EnsureClient()
            .DownloadFileAsync(path, output, ToProgress(cb), ct)
            .ConfigureAwait(false), ct);
    }

    /// <summary>
    /// Deletes the file at the specified path on the SFTP server. If the client is not connected, an <see cref="InvalidOperationException" /> is thrown.
    /// </summary>
    /// <param name="path"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    public Task DeleteFileAsync(string path, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync(async () => await EnsureClient().DeleteFileAsync(path, ct).ConfigureAwait(false), ct);
    }

    /// <summary>
    /// Deletes the directory at the specified path on the SFTP server, including all its contents. If the client is not connected, an <see cref="InvalidOperationException" /> is thrown.
    /// </summary>
    /// <param name="path"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    public Task DeleteDirectoryAsync(string path, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync(async () => await EnsureClient().DeleteDirectoryAsync(path, true, null, ct).ConfigureAwait(false), ct);
    }

    /// <summary>
    /// Creates a directory at the specified path on the SFTP server. If the client is not connected, an <see cref="InvalidOperationException" /> is thrown.
    /// </summary>
    /// <param name="path"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync(async () => await EnsureClient().CreateDirectoryAsync(path, cancellationToken: ct).ConfigureAwait(false), ct);
    }

    /// <summary>
    /// Renames a file from the old path to the new path on the SFTP server. If the client is not connected, an <see cref="InvalidOperationException" /> is thrown.
    /// </summary>
    /// <param name="o"></param>
    /// <param name="n"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    public Task RenameFileAsync(string o, string n, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync(async () => await EnsureClient().RenameAsync(o, n, ct).ConfigureAwait(false), ct);
    }

    // Tmds.Ssh 未单独暴露 posix-rename 扩展;RenameAsync 即其现有的重命名路径。
    /// <summary>
    /// Renames a file from the old path to the new path on the SFTP server using POSIX semantics. If the client is not connected, an <see cref="InvalidOperationException" /> is thrown.
    /// </summary>
    /// <param name="o"></param>
    /// <param name="n"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    public Task PosixRenameFileAsync(string o, string n, CancellationToken ct = default) => RenameFileAsync(o, n, ct);

    /// <summary>
    /// Checks if a file or directory exists at the specified path on the SFTP server. If the client is not connected, an <see cref="InvalidOperationException" /> is thrown.
    /// </summary>
    /// <param name="path"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync(async () =>
        {
            // Tmds.Ssh 的 GetAttributesAsync 对不存在的路径返回 null 而非抛异常
            // (SSH.NET 是抛 SftpPathNotFoundException):判空即存在性,零异常控制流。
            FileEntryAttributes? attrs = await EnsureClient()
                .GetAttributesAsync(path, true, null, ct).ConfigureAwait(false);
            return attrs is not null;
        }, ct);
    }

    /// <summary>
    /// Changes the permissions of a file or directory at the specified path on the SFTP server.
    /// The mode is specified as a short integer representing the Unix file mode (e.g., 0o755).
    /// If the client is not connected, an <see cref="InvalidOperationException" /> is thrown.
    /// </summary>
    /// <param name="path"></param>
    /// <param name="mode"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    public Task ChangePermissionsAsync(string path, short mode, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync(async () =>
        {
            var unixMode = (UnixFileMode)Convert.ToInt32(mode.ToString(), 8);
            var perms = UnixFilePermissionsExtensions.ToUnixFilePermissions(unixMode);
            await EnsureClient().SetAttributesAsync(path, permissions: perms, cancellationToken: ct).ConfigureAwait(false);
        }, ct);
    }

    /// <summary>
    /// Opens a file at the specified path on the SFTP server with the given mode and access. If the client is not connected, an <see cref="InvalidOperationException" /> is thrown.
    /// </summary>
    /// <param name="path"></param>
    /// <param name="mode"></param>
    /// <param name="access"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    /// <exception cref="VelaSftpPathNotFoundException"></exception>
    public Task<Stream> OpenAsync(string path, FileMode mode, FileAccess access, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync<Stream>(async () =>
        {
            SftpClient c = EnsureClient();
            SftpFile? file = mode switch
            {
                FileMode.CreateNew => await c.CreateNewFileAsync(path, access, new FileOpenOptions(), ct).ConfigureAwait(false),
                // Create/Truncate 语义要求截断旧内容,否则新内容比旧文件短时会残留旧尾部数据
                FileMode.Create => await c.OpenOrCreateFileAsync(path, access, new FileOpenOptions { OpenMode = OpenMode.Truncate }, ct).ConfigureAwait(false),
                FileMode.Truncate => await c.OpenFileAsync(path, access, new FileOpenOptions { OpenMode = OpenMode.Truncate }, ct).ConfigureAwait(false),
                FileMode.Append => await c.OpenOrCreateFileAsync(path, access, new FileOpenOptions { OpenMode = OpenMode.Append }, ct).ConfigureAwait(false),
                FileMode.OpenOrCreate => await c.OpenOrCreateFileAsync(path, access, new FileOpenOptions(), ct).ConfigureAwait(false),
                _ => await c.OpenFileAsync(path, access, new FileOpenOptions(), ct).ConfigureAwait(false),
            };
            // Tmds.Ssh 对不存在的文件返回 null 而非抛异常
            return file ?? throw new VelaSftpPathNotFoundException($"File not found: {path}");
        }, ct);
    }

    /// <summary>
    /// Gets the size of a file at the specified path on the SFTP server. If the client is not connected, an <see cref="InvalidOperationException" /> is thrown.
    /// </summary>
    /// <param name="path"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    public Task<long> GetFileSizeAsync(string path, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync(async () =>
        {
            // 不存在返回 null → 契约 -1,零异常控制流。
            FileEntryAttributes? attrs = await EnsureClient()
                .GetAttributesAsync(path, true, null, ct).ConfigureAwait(false);
            return attrs?.Length ?? -1L;
        }, ct);
    }

    /// <summary>
    /// Uploads a file to the specified path on the SFTP server, resuming from the given offset. If the client is not connected, an <see cref="InvalidOperationException" /> is thrown.
    /// </summary>
    /// <param name="input"></param>
    /// <param name="path"></param>
    /// <param name="resumeOffset"></param>
    /// <param name="cb"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    /// <exception cref="VelaSftpPathNotFoundException"></exception>
    public Task UploadAsync(Stream input, string path, long resumeOffset, Action<ulong>? cb = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync(async () =>
        {
            SftpClient c = EnsureClient();
            if (resumeOffset > 0)
            {
                input.Seek(resumeOffset, SeekOrigin.Begin);
                using SftpFile r = await c.OpenFileAsync(path, FileAccess.Write, new FileOpenOptions(), ct).ConfigureAwait(false)
                    ?? throw new VelaSftpPathNotFoundException($"File not found: {path}");
                r.Seek(0, SeekOrigin.End);
                byte[] b = new byte[32 * 1024]; long t = 0; int n;
                while ((n = await input.ReadAsync(b, ct).ConfigureAwait(false)) > 0)
                {
                    await r.WriteAsync(b.AsMemory(0, n), ct).ConfigureAwait(false); t += n; cb?.Invoke((ulong)(resumeOffset + t));
                }
            }
            else
            {
                await c.UploadFileAsync(input, path, overwrite: true, progress: ToProgress(cb), cancellationToken: ct).ConfigureAwait(false);
            }
        }, ct);
    }

    /// <summary>
    /// Disposes the SFTP client and releases all resources.
    /// </summary>
    public void Dispose() { if (_disposed) return; _disposed = true; _client?.Dispose(); _client = null; GC.SuppressFinalize(this); }

    private SftpClient EnsureClient() => _client ?? throw new InvalidOperationException("Not connected.");

    private static async Task<IEnumerable<SftpEntry>> ListEntriesAsync(SftpClient client, string dir, CancellationToken ct)
    {
        var entries = new List<SftpEntry>();
        await foreach ((string Path, FileEntryAttributes Attributes) result in SftpDirectoryExtensions.GetDirectoryEntriesAsync(
            client, dir, new Tmds.Ssh.EnumerationOptions())
            .WithCancellation(ct).ConfigureAwait(false))
        {
            entries.Add(MapEntry(result.Path, result.Attributes));
        }
        return entries;
    }

    private static SftpEntry MapEntry(string fullPath, FileEntryAttributes attrs)
    {
        string fn = Path.GetFileName(fullPath);
        UnixFilePermissions p = attrs.Permissions;
        return new SftpEntry
        {
            Name = fn,
            FullName = fullPath,
            Length = attrs.Length,
            IsDirectory = attrs.FileType == UnixFileType.Directory,
            LastWriteTime = attrs.LastWriteTime.DateTime,
            UserId = attrs.Uid,
            GroupId = attrs.Gid,
            OwnerCanRead = (p & UnixFilePermissions.UserRead) != 0,
            OwnerCanWrite = (p & UnixFilePermissions.UserWrite) != 0,
            OwnerCanExecute = (p & UnixFilePermissions.UserExecute) != 0,
            GroupCanRead = (p & UnixFilePermissions.GroupRead) != 0,
            GroupCanWrite = (p & UnixFilePermissions.GroupWrite) != 0,
            GroupCanExecute = (p & UnixFilePermissions.GroupExecute) != 0,
            OthersCanRead = (p & UnixFilePermissions.OtherRead) != 0,
            OthersCanWrite = (p & UnixFilePermissions.OtherWrite) != 0,
            OthersCanExecute = (p & UnixFilePermissions.OtherExecute) != 0,
        };
    }

    /// <summary>
    /// 统一异常翻译:释放竞态导致的 NRE 归一为 ObjectDisposedException,
    /// 其余库异常经 <see cref="TmdsSshInterop.Translate" /> 翻译为 Core 中立异常。
    /// </summary>
    private async Task<T> GuardedAsync<T>(Func<Task<T>> op, CancellationToken ct = default)
    {
        try { return await op().ConfigureAwait(false); }
        catch (NullReferenceException) when (IsTornDown()) { throw new ObjectDisposedException(nameof(TmdsSftpClientWrapper)); }
        catch (Exception ex) when (TmdsSshInterop.Translate(ex, ct) is { } t) { throw t; }
    }

    private async Task GuardedAsync(Func<Task> op, CancellationToken ct = default)
    {
        await GuardedAsync(async () => { await op().ConfigureAwait(false); return true; }, ct).ConfigureAwait(false);
    }

    private bool IsTornDown() { if (_disposed) return true; try { return _client is null; } catch { return true; } }

    private static ProgressAdapter? ToProgress(Action<ulong>? cb) => cb is null ? null : new ProgressAdapter(cb);

    /// <summary>
    /// 把 Tmds.Ssh 的分块传输进度桥接为累计字节回调:
    /// offset 是每块传输完成后在远端(上传)/本地目标(下载)文件中的位置,即累计已传字节数。
    /// </summary>
    private sealed class ProgressAdapter(Action<ulong> callback) : SftpProgressHandler
    {
        protected override void DataTransferred(int index, long bytesTransferred, long offset) => callback((ulong)offset);
    }
}
