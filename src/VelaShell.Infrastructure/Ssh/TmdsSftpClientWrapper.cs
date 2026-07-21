using VelaShell.Core.Ssh;

namespace VelaShell.Infrastructure.Ssh;

public sealed class TmdsSftpClientWrapper : ISftpClientWrapper
{
    private readonly Func<Task<Tmds.Ssh.SftpClient>> _clientFactory;
    private Tmds.Ssh.SftpClient? _client;
    private bool _disposed;
    private TimeSpan _connectionTimeout = TimeSpan.FromSeconds(10);
    private string _workingDirectory = "/";

    public TmdsSftpClientWrapper(Func<Task<Tmds.Ssh.SftpClient>> clientFactory)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    }

    public bool IsConnected => !_disposed && _client is not null;

    public TimeSpan ConnectionTimeout
    {
        get { ObjectDisposedException.ThrowIf(_disposed, this); return _connectionTimeout; }
        set { ObjectDisposedException.ThrowIf(_disposed, this); _connectionTimeout = value; }
    }

    public string WorkingDirectory
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _workingDirectory;
        }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_client is not null) return;
        try
        {
            _client = await _clientFactory().ConfigureAwait(false);
            _workingDirectory = _client.WorkingDirectory.Path;
        }
        catch (Exception ex) when (TmdsSshInterop.Translate(ex, cancellationToken) is { } translated) { throw translated; }
    }

    public void Disconnect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _client?.Dispose(); _client = null;
    }

    public Task<IEnumerable<SftpEntry>> ListDirectoryAsync(string path, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync(() => ListEntriesAsync(EnsureClient(), path, ct), ct);
    }

    public Task UploadAsync(Stream input, string path, Action<ulong>? cb = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync(async () => await EnsureClient()
            .UploadFileAsync(input, path, overwrite: true, progress: ToProgress(cb), cancellationToken: ct)
            .ConfigureAwait(false), ct);
    }

    public Task DownloadAsync(string path, Stream output, Action<ulong>? cb = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync(async () => await EnsureClient()
            .DownloadFileAsync(path, output, ToProgress(cb), ct)
            .ConfigureAwait(false), ct);
    }

    public Task DeleteFileAsync(string path, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync(async () => await EnsureClient().DeleteFileAsync(path, ct).ConfigureAwait(false), ct);
    }

    public Task DeleteDirectoryAsync(string path, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync(async () => await EnsureClient().DeleteDirectoryAsync(path, true, null, ct).ConfigureAwait(false), ct);
    }

    public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync(async () => await EnsureClient().CreateDirectoryAsync(path, cancellationToken: ct).ConfigureAwait(false), ct);
    }

    public Task RenameFileAsync(string o, string n, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync(async () => await EnsureClient().RenameAsync(o, n, ct).ConfigureAwait(false), ct);
    }

    // Tmds.Ssh 未单独暴露 posix-rename 扩展;RenameAsync 即其现有的重命名路径。
    public Task PosixRenameFileAsync(string o, string n, CancellationToken ct = default) => RenameFileAsync(o, n, ct);

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync(async () =>
        {
            // Tmds.Ssh 的 GetAttributesAsync 对不存在的路径返回 null 而非抛异常
            // (SSH.NET 是抛 SftpPathNotFoundException):判空即存在性,零异常控制流。
            Tmds.Ssh.FileEntryAttributes? attrs = await EnsureClient()
                .GetAttributesAsync(path, true, null, ct).ConfigureAwait(false);
            return attrs is not null;
        }, ct);
    }

    public Task ChangePermissionsAsync(string path, short mode, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync(async () =>
        {
            var unixMode = (System.IO.UnixFileMode)Convert.ToInt32(mode.ToString(), 8);
            var perms = Tmds.Ssh.UnixFilePermissionsExtensions.ToUnixFilePermissions(unixMode);
            await EnsureClient().SetAttributesAsync(path, permissions: perms, cancellationToken: ct).ConfigureAwait(false);
        }, ct);
    }

    public Task<Stream> OpenAsync(string path, System.IO.FileMode mode, System.IO.FileAccess access, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync<Stream>(async () =>
        {
            Tmds.Ssh.SftpClient c = EnsureClient();
            Tmds.Ssh.SftpFile? file = mode switch
            {
                System.IO.FileMode.CreateNew => await c.CreateNewFileAsync(path, access, new Tmds.Ssh.FileOpenOptions(), ct).ConfigureAwait(false),
                // Create/Truncate 语义要求截断旧内容,否则新内容比旧文件短时会残留旧尾部数据
                System.IO.FileMode.Create => await c.OpenOrCreateFileAsync(path, access, new Tmds.Ssh.FileOpenOptions { OpenMode = Tmds.Ssh.OpenMode.Truncate }, ct).ConfigureAwait(false),
                System.IO.FileMode.Truncate => await c.OpenFileAsync(path, access, new Tmds.Ssh.FileOpenOptions { OpenMode = Tmds.Ssh.OpenMode.Truncate }, ct).ConfigureAwait(false),
                System.IO.FileMode.Append => await c.OpenOrCreateFileAsync(path, access, new Tmds.Ssh.FileOpenOptions { OpenMode = Tmds.Ssh.OpenMode.Append }, ct).ConfigureAwait(false),
                System.IO.FileMode.OpenOrCreate => await c.OpenOrCreateFileAsync(path, access, new Tmds.Ssh.FileOpenOptions(), ct).ConfigureAwait(false),
                _ => await c.OpenFileAsync(path, access, new Tmds.Ssh.FileOpenOptions(), ct).ConfigureAwait(false),
            };
            // Tmds.Ssh 对不存在的文件返回 null 而非抛异常
            return file ?? throw new SftpPathNotFoundException($"File not found: {path}");
        }, ct);
    }

    public Task<long> GetFileSizeAsync(string path, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync(async () =>
        {
            // 不存在返回 null → 契约 -1,零异常控制流。
            Tmds.Ssh.FileEntryAttributes? attrs = await EnsureClient()
                .GetAttributesAsync(path, true, null, ct).ConfigureAwait(false);
            return attrs?.Length ?? -1L;
        }, ct);
    }

    public Task UploadAsync(Stream input, string path, long resumeOffset, Action<ulong>? cb = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync(async () =>
        {
            Tmds.Ssh.SftpClient c = EnsureClient();
            if (resumeOffset > 0)
            {
                input.Seek(resumeOffset, SeekOrigin.Begin);
                using Tmds.Ssh.SftpFile r = await c.OpenFileAsync(path, System.IO.FileAccess.Write, new Tmds.Ssh.FileOpenOptions(), ct).ConfigureAwait(false)
                    ?? throw new SftpPathNotFoundException($"File not found: {path}");
                r.Seek(0, SeekOrigin.End);
                byte[] b = new byte[32 * 1024]; long t = 0; int n;
                while ((n = await input.ReadAsync(b, ct).ConfigureAwait(false)) > 0)
                { await r.WriteAsync(b.AsMemory(0, n), ct).ConfigureAwait(false); t += n; cb?.Invoke((ulong)(resumeOffset + t)); }
            }
            else
            { await c.UploadFileAsync(input, path, overwrite: true, progress: ToProgress(cb), cancellationToken: ct).ConfigureAwait(false); }
        }, ct);
    }

    public void Dispose() { if (_disposed) return; _disposed = true; _client?.Dispose(); _client = null; GC.SuppressFinalize(this); }

    private Tmds.Ssh.SftpClient EnsureClient() => _client ?? throw new InvalidOperationException("Not connected.");

    private static async Task<IEnumerable<SftpEntry>> ListEntriesAsync(Tmds.Ssh.SftpClient client, string dir, CancellationToken ct)
    {
        var entries = new List<SftpEntry>();
        await foreach ((string Path, Tmds.Ssh.FileEntryAttributes Attributes) result in Tmds.Ssh.SftpDirectoryExtensions.GetDirectoryEntriesAsync(
            client, dir, new Tmds.Ssh.EnumerationOptions())
            .WithCancellation(ct).ConfigureAwait(false))
        {
            entries.Add(MapEntry(result.Path, result.Attributes));
        }
        return entries;
    }

    private static SftpEntry MapEntry(string fullPath, Tmds.Ssh.FileEntryAttributes attrs)
    {
        string fn = System.IO.Path.GetFileName(fullPath);
        Tmds.Ssh.UnixFilePermissions p = attrs.Permissions;
        return new SftpEntry
        {
            Name = fn,
            FullName = fullPath,
            Length = attrs.Length,
            IsDirectory = attrs.FileType == Tmds.Ssh.UnixFileType.Directory,
            LastWriteTime = attrs.LastWriteTime.DateTime,
            UserId = attrs.Uid,
            GroupId = attrs.Gid,
            OwnerCanRead = (p & Tmds.Ssh.UnixFilePermissions.UserRead) != 0,
            OwnerCanWrite = (p & Tmds.Ssh.UnixFilePermissions.UserWrite) != 0,
            OwnerCanExecute = (p & Tmds.Ssh.UnixFilePermissions.UserExecute) != 0,
            GroupCanRead = (p & Tmds.Ssh.UnixFilePermissions.GroupRead) != 0,
            GroupCanWrite = (p & Tmds.Ssh.UnixFilePermissions.GroupWrite) != 0,
            GroupCanExecute = (p & Tmds.Ssh.UnixFilePermissions.GroupExecute) != 0,
            OthersCanRead = (p & Tmds.Ssh.UnixFilePermissions.OtherRead) != 0,
            OthersCanWrite = (p & Tmds.Ssh.UnixFilePermissions.OtherWrite) != 0,
            OthersCanExecute = (p & Tmds.Ssh.UnixFilePermissions.OtherExecute) != 0,
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
    private sealed class ProgressAdapter(Action<ulong> callback) : Tmds.Ssh.SftpProgressHandler
    {
        protected override void DataTransferred(int index, long bytesTransferred, long offset) => callback((ulong)offset);
    }
}
