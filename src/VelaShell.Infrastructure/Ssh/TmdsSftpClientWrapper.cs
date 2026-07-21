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

    public async Task Connect() => await ConnectAsync(CancellationToken.None).ConfigureAwait(false);

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

    public IEnumerable<SftpEntry> ListDirectory(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.Run(() => ListEntriesAsync(EnsureClient(), path, CancellationToken.None))
            .GetAwaiter().GetResult();
    }

    public Task<IEnumerable<SftpEntry>> ListDirectoryAsync(string path, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ListEntriesAsync(EnsureClient(), path, ct);
    }

    public void UploadFile(Stream input, string path, bool canOverride = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // overwrite 交给库处理:目标已存在且不允许覆盖时抛 SftpException → 翻译为 SftpOperationException。
        Guarded(() => EnsureClient().UploadFileAsync(input, path, overwrite: canOverride).GetAwaiter().GetResult());
    }

    public async Task UploadAsync(Stream input, string path, Action<ulong>? cb = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await GuardedAsync(async () => await EnsureClient()
            .UploadFileAsync(input, path, overwrite: true, progress: ToProgress(cb), cancellationToken: ct)
            .ConfigureAwait(false), ct);
    }

    public void DownloadFile(string path, Stream output) { ObjectDisposedException.ThrowIf(_disposed, this); Guarded(() => EnsureClient().DownloadFileAsync(path, output).GetAwaiter().GetResult()); }

    public async Task DownloadAsync(string path, Stream output, Action<ulong>? cb = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await GuardedAsync(async () => await EnsureClient()
            .DownloadFileAsync(path, output, ToProgress(cb), ct)
            .ConfigureAwait(false), ct);
    }

    public void DeleteFile(string path) { ObjectDisposedException.ThrowIf(_disposed, this); Guarded(() => EnsureClient().DeleteFileAsync(path).GetAwaiter().GetResult()); }
    public void DeleteDirectory(string path) { ObjectDisposedException.ThrowIf(_disposed, this); Guarded(() => EnsureClient().DeleteDirectoryAsync(path, true).GetAwaiter().GetResult()); }
    public void CreateDirectory(string path) { ObjectDisposedException.ThrowIf(_disposed, this); Guarded(() => EnsureClient().CreateDirectoryAsync(path).GetAwaiter().GetResult()); }
    public void RenameFile(string o, string n) { ObjectDisposedException.ThrowIf(_disposed, this); Guarded(() => EnsureClient().RenameAsync(o, n).GetAwaiter().GetResult()); }
    public void PosixRenameFile(string o, string n) { ObjectDisposedException.ThrowIf(_disposed, this); Guarded(() => EnsureClient().RenameAsync(o, n).GetAwaiter().GetResult()); }

    public bool Exists(string path) { ObjectDisposedException.ThrowIf(_disposed, this); return Guarded(() => FileExistsSync(path)); }

    public void ChangePermissions(string path, short mode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Guarded(() =>
        {
            var unixMode = (System.IO.UnixFileMode)Convert.ToInt32(mode.ToString(), 8);
            var perms = Tmds.Ssh.UnixFilePermissionsExtensions.ToUnixFilePermissions(unixMode);
            EnsureClient().SetAttributesAsync(path, permissions: perms).GetAwaiter().GetResult();
        });
    }

    public System.IO.Stream Open(string path, System.IO.FileMode mode, System.IO.FileAccess access)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Guarded<System.IO.Stream>(() =>
        {
            var c = EnsureClient();
            Tmds.Ssh.SftpFile? file = mode switch
            {
                System.IO.FileMode.CreateNew => c.CreateNewFileAsync(path, access, new Tmds.Ssh.FileOpenOptions()).GetAwaiter().GetResult(),
                // Create/Truncate 语义要求截断旧内容,否则新内容比旧文件短时会残留旧尾部数据
                System.IO.FileMode.Create => c.OpenOrCreateFileAsync(path, access, new Tmds.Ssh.FileOpenOptions { OpenMode = Tmds.Ssh.OpenMode.Truncate }).GetAwaiter().GetResult(),
                System.IO.FileMode.Truncate => c.OpenFileAsync(path, access, new Tmds.Ssh.FileOpenOptions { OpenMode = Tmds.Ssh.OpenMode.Truncate }).GetAwaiter().GetResult(),
                System.IO.FileMode.Append => c.OpenOrCreateFileAsync(path, access, new Tmds.Ssh.FileOpenOptions { OpenMode = Tmds.Ssh.OpenMode.Append }).GetAwaiter().GetResult(),
                System.IO.FileMode.OpenOrCreate => c.OpenOrCreateFileAsync(path, access, new Tmds.Ssh.FileOpenOptions()).GetAwaiter().GetResult(),
                _ => c.OpenFileAsync(path, access, new Tmds.Ssh.FileOpenOptions()).GetAwaiter().GetResult(),
            };
            // Tmds.Ssh 对不存在的文件返回 null 而非抛异常
            return file ?? throw new SftpPathNotFoundException($"File not found: {path}");
        });
    }

    public long GetFileSize(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            return Guarded(() =>
            {
                var result = EnsureClient().GetAttributesAsync(path, true, null, CancellationToken.None).GetAwaiter().GetResult();
                return result.Length;
            });
        }
        catch (SftpPathNotFoundException) { return -1; }
    }

    public async Task UploadAsync(Stream input, string path, long resumeOffset, Action<ulong>? cb = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await GuardedAsync(async () =>
        {
            var c = EnsureClient();
            if (resumeOffset > 0)
            {
                input.Seek(resumeOffset, SeekOrigin.Begin);
                using var r = await c.OpenFileAsync(path, System.IO.FileAccess.Write, new Tmds.Ssh.FileOpenOptions(), ct).ConfigureAwait(false)
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

    private bool FileExistsSync(string path)
    {
        try { EnsureClient().GetAttributesAsync(path, true, null, CancellationToken.None).GetAwaiter().GetResult(); return true; }
        catch (Tmds.Ssh.SftpException) { return false; }
    }

    private static async Task<IEnumerable<SftpEntry>> ListEntriesAsync(Tmds.Ssh.SftpClient client, string dir, CancellationToken ct)
    {
        var entries = new List<SftpEntry>();
        await foreach (var result in Tmds.Ssh.SftpDirectoryExtensions.GetDirectoryEntriesAsync(
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
        var p = attrs.Permissions;
        return new SftpEntry
        {
            Name = fn, FullName = fullPath, Length = attrs.Length,
            IsDirectory = attrs.FileType == Tmds.Ssh.UnixFileType.Directory,
            LastWriteTime = attrs.LastWriteTime.DateTime,
            UserId = (int)attrs.Uid, GroupId = (int)attrs.Gid,
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

    private T Guarded<T>(Func<T> op) { try { return op(); } catch (NullReferenceException) when (IsTornDown()) { throw new ObjectDisposedException(nameof(TmdsSftpClientWrapper)); } catch (Exception ex) when (TmdsSshInterop.Translate(ex) is { } t) { throw t; } }
    private void Guarded(Action op) { Guarded(() => { op(); return true; }); }
    private async Task GuardedAsync(Func<Task> op, CancellationToken ct = default) { try { await op().ConfigureAwait(false); } catch (Exception ex) when (TmdsSshInterop.Translate(ex, ct) is { } t) { throw t; } }
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
