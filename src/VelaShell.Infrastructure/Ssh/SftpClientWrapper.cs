using Renci.SshNet;
using Renci.SshNet.Sftp;
using VelaShell.Core.Ssh;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// <see cref="ISftpClientWrapper" /> 的 SSH.NET 实现。库类型不越过此边界:目录条目映射为
/// 中立的 <see cref="SftpEntry" />,SSH.NET 异常在 <see cref="Guarded{T}" /> 中翻译为
/// Core 的 SshClientException 层级 —— 更换底层库时重写本文件即可。
/// </summary>
public sealed class SftpClientWrapper(SftpClient client) : ISftpClientWrapper
{
    private readonly SftpClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private bool _disposed;

    /// <summary>获取底层 SFTP 会话是否处于已连接状态。</summary>
    public bool IsConnected
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _client.IsConnected;
        }
    }

    /// <summary>获取或设置建立连接及执行操作时使用的超时时间。</summary>
    public TimeSpan ConnectionTimeout
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _client.ConnectionInfo.Timeout;
        }
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _client.ConnectionInfo.Timeout = value;
        }
    }

    /// <summary>获取当前会话的远程工作目录。</summary>
    public string WorkingDirectory
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _client.WorkingDirectory;
        }
    }

    /// <summary>同步建立 SFTP 连接。</summary>
    public void Connect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Guarded(_client.Connect);
    }

    /// <summary>异步建立 SFTP 连接。</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (SshNetInterop.Translate(ex) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>断开当前 SFTP 连接。</summary>
    public void Disconnect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _client.Disconnect();
    }

    /// <summary>同步列出指定远程目录下的条目。</summary>
    public IEnumerable<SftpEntry> ListDirectory(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Guarded(() => _client.ListDirectory(path).Select(MapEntry).ToList());
    }

    /// <summary>异步列出指定远程目录下的条目。</summary>
    public Task<IEnumerable<SftpEntry>> ListDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.Run(() => Guarded<IEnumerable<SftpEntry>>(() => [.. _client.ListDirectory(path).Select(MapEntry)]), cancellationToken);
    }

    /// <summary>同步将输入流上传到指定远程路径。</summary>
    public void UploadFile(Stream input, string path, bool canOverride = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Guarded(() => _client.UploadFile(input, path, canOverride));
    }

    /// <summary>异步将输入流上传到指定远程路径,并可通过回调报告已上传字节数。</summary>
    public Task UploadAsync(Stream input, string path, Action<ulong>? uploadCallback = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.Run(() => Guarded(() => _client.UploadFile(input, path, true, uploadCallback)), cancellationToken);
    }

    /// <summary>同步将指定远程文件下载到输出流。</summary>
    public void DownloadFile(string path, Stream output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Guarded(() => _client.DownloadFile(path, output));
    }

    /// <summary>异步将指定远程文件下载到输出流,并可通过回调报告已下载字节数。</summary>
    public Task DownloadAsync(string path, Stream output, Action<ulong>? downloadCallback = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.Run(() => Guarded(() => _client.DownloadFile(path, output, downloadCallback)), cancellationToken);
    }

    /// <summary>删除指定的远程文件。</summary>
    public void DeleteFile(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Guarded(() => _client.DeleteFile(path));
    }

    /// <summary>删除指定的远程目录。</summary>
    public void DeleteDirectory(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Guarded(() => _client.DeleteDirectory(path));
    }

    /// <summary>在指定远程路径创建目录。</summary>
    public void CreateDirectory(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Guarded(() => _client.CreateDirectory(path));
    }

    /// <summary>将远程文件从旧路径重命名为新路径。</summary>
    public void RenameFile(string oldPath, string newPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Guarded(() => _client.RenameFile(oldPath, newPath));
    }

    /// <summary>使用 POSIX 语义(可原子覆盖目标)将远程文件重命名。</summary>
    public void PosixRenameFile(string oldPath, string newPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Guarded(() => _client.RenameFile(oldPath, newPath, true));
    }

    /// <summary>判断指定远程路径是否存在。</summary>
    public bool Exists(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Guarded(() => _client.Exists(path));
    }

    /// <summary>修改指定远程路径的权限位(八进制模式)。</summary>
    public void ChangePermissions(string path, short mode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Guarded(() => _client.ChangePermissions(path, mode));
    }

    /// <summary>释放包装器并关闭底层 SFTP 客户端。</summary>
    public void Dispose() => Dispose(true);

    private static SftpEntry MapEntry(ISftpFile file)
    {
        return new()
        {
            Name = file.Name,
            FullName = file.FullName,
            Length = file.Length,
            IsDirectory = file.IsDirectory,
            LastWriteTime = file.LastWriteTime,
            UserId = file.UserId,
            GroupId = file.GroupId,
            OwnerCanRead = file.OwnerCanRead,
            OwnerCanWrite = file.OwnerCanWrite,
            OwnerCanExecute = file.OwnerCanExecute,
            GroupCanRead = file.GroupCanRead,
            GroupCanWrite = file.GroupCanWrite,
            GroupCanExecute = file.GroupCanExecute,
            OthersCanRead = file.OthersCanRead,
            OthersCanWrite = file.OthersCanWrite,
            OthersCanExecute = file.OthersCanExecute
        };
    }

    /// <summary>
    /// Runs an SSH.NET call and keeps library types from leaking past this wrapper:
    /// SSH.NET exceptions are translated to the Core SshClientException hierarchy, and the
    /// NullReferenceException the library throws when the underlying session is torn down
    /// (Disconnect/Dispose) mid-call — e.g. a directory listing still running as its tab is
    /// closed — becomes <see cref="ObjectDisposedException" />, the ordinary "session is gone"
    /// signal callers already handle.
    /// </summary>
    private T Guarded<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (NullReferenceException ex) when (IsTornDown())
        {
            throw new ObjectDisposedException(nameof(SftpClientWrapper), ex);
        }
        catch (Exception ex) when (SshNetInterop.Translate(ex) is { } translated)
        {
            throw translated;
        }
    }

    private void Guarded(Action operation)
    {
        Guarded(() =>
        {
            operation();
            return true;
        });
    }

    /// <summary>True when the wrapper was disposed or the client lost its connection/session.</summary>
    private bool IsTornDown()
    {
        if (_disposed)
        {
            return true;
        }
        try
        {
            return !_client.IsConnected;
        }
        catch
        {
            // IsConnected itself throws once the client is disposed underneath us.
            return true;
        }
    }

    private void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }
        if (disposing)
        {
            _client.Dispose();
        }
        _disposed = true;
    }
}
