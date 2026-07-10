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

    public bool IsConnected
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _client.IsConnected;
        }
    }

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

    public string WorkingDirectory
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _client.WorkingDirectory;
        }
    }

    public void Connect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Guarded(_client.Connect);
    }

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

    public void Disconnect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _client.Disconnect();
    }

    public IEnumerable<SftpEntry> ListDirectory(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Guarded(() => _client.ListDirectory(path).Select(MapEntry).ToList());
    }

    public Task<IEnumerable<SftpEntry>> ListDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.Run(() => Guarded<IEnumerable<SftpEntry>>(() => [.. _client.ListDirectory(path).Select(MapEntry)]), cancellationToken);
    }

    public void UploadFile(Stream input, string path, bool canOverride = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Guarded(() => _client.UploadFile(input, path, canOverride));
    }

    public Task UploadAsync(Stream input, string path, Action<ulong>? uploadCallback = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.Run(() => Guarded(() => _client.UploadFile(input, path, true, uploadCallback)), cancellationToken);
    }

    public void DownloadFile(string path, Stream output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Guarded(() => _client.DownloadFile(path, output));
    }

    public Task DownloadAsync(string path, Stream output, Action<ulong>? downloadCallback = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.Run(() => Guarded(() => _client.DownloadFile(path, output, downloadCallback)), cancellationToken);
    }

    public void DeleteFile(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Guarded(() => _client.DeleteFile(path));
    }

    public void DeleteDirectory(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Guarded(() => _client.DeleteDirectory(path));
    }

    public void CreateDirectory(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Guarded(() => _client.CreateDirectory(path));
    }

    public void RenameFile(string oldPath, string newPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Guarded(() => _client.RenameFile(oldPath, newPath));
    }

    public void PosixRenameFile(string oldPath, string newPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Guarded(() => _client.RenameFile(oldPath, newPath, true));
    }

    public bool Exists(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Guarded(() => _client.Exists(path));
    }

    public void ChangePermissions(string path, short mode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Guarded(() => _client.ChangePermissions(path, mode));
    }

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
