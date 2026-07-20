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

    /// <summary>以给定模式与访问权限打开远程文件用于读或写。</summary>
    public Stream Open(string path, FileMode mode, FileAccess access)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Guarded(() => _client.Open(path, mode, access));
    }

    /// <summary>获取远程文件的大小(字节);不存在则返回 -1。</summary>
    public long GetFileSize(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            SftpFileAttributes attrs = Guarded(() => _client.GetAttributes(path));
            return attrs?.Size ?? -1;
        }
        catch (SshClientException)
        {
            return -1;
        }
    }

    /// <summary>
    /// 从 <paramref name="resumeOffset" /> 处开始将流上传到远程路径。当 resumeOffset > 0 时,
    /// 以 FileMode.Append 打开远程文件。
    /// </summary>
    public Task UploadAsync(Stream input, string path, long resumeOffset, Action<ulong>? uploadCallback = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.Run(() => Guarded(() =>
        {
            using Stream remote = resumeOffset > 0
                ? _client.Open(path, FileMode.Append, FileAccess.Write)
                : _client.Create(path);
            // 将输入流定位到续传偏移处,只传输尾部数据。
            if (resumeOffset > 0)
            {
                input.Seek(resumeOffset, SeekOrigin.Begin);
            }
            // 复制并上报进度。
            byte[] buffer = new byte[32 * 1024];
            long totalRead = 0;
            int bytesRead;
            while ((bytesRead = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                remote.Write(buffer, 0, bytesRead);
                totalRead += bytesRead;
                uploadCallback?.Invoke((ulong)(resumeOffset + totalRead));
            }
        }), cancellationToken);
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
    /// 执行一次 SSH.NET 调用,并阻止库类型越过本包装器边界:SSH.NET 异常被翻译为 Core 的
    /// SshClientException 层级;当底层会话在调用中途被拆除(Disconnect/Dispose)时,库抛出的
    /// NullReferenceException(例如标签页关闭时仍在运行的目录列举)转变为调用方已处理的
    /// 常规"会话已释放"信号 <see cref="ObjectDisposedException" />。
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

    /// <summary>包装器已释放或客户端连接/会话丢失时返回 True。</summary>
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
            // 一旦底层客户端被释放,IsConnected 本身也会抛出异常。
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
