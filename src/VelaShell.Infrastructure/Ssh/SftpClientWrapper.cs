using Renci.SshNet;
using Renci.SshNet.Sftp;
using VelaShell.Core.Ssh;

namespace VelaShell.Infrastructure.Ssh;

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
        _client.Connect();
    }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _client.ConnectAsync(cancellationToken);
    }

    public void Disconnect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _client.Disconnect();
    }

    public IEnumerable<ISftpFile> ListDirectory(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Guarded(() => _client.ListDirectory(path));
    }

    public Task<IEnumerable<ISftpFile>> ListDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.Run(() => Guarded(() => _client.ListDirectory(path)), cancellationToken);
    }

    public void UploadFile(Stream input, string path, bool canOverride = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _client.UploadFile(input, path, canOverride);
    }

    public Task UploadAsync(Stream input, string path, Action<ulong>? uploadCallback = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.Run(() => Guarded(() => _client.UploadFile(input, path, true, uploadCallback)), cancellationToken);
    }

    public void DownloadFile(string path, Stream output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _client.DownloadFile(path, output);
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

    /// <summary>
    /// Runs an SSH.NET call, normalising the NullReferenceException the library throws when the
    /// underlying session is torn down (Disconnect/Dispose) while the call is in flight — e.g. a
    /// directory listing still running as its tab is closed. Translated to
    /// <see cref="ObjectDisposedException" /> so callers see the ordinary "session is gone" signal
    /// they already handle instead of an opaque crash from inside Renci.SshNet.
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
