using Renci.SshNet;
using Renci.SshNet.Common;
using VelaShell.Core.Ssh;

namespace VelaShell.Infrastructure.Ssh;

public sealed class SshClientWrapper(SshClient client) : ISshClientWrapper
{
    private readonly SshClient _client = client ?? throw new ArgumentNullException(nameof(client));
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

    public IShellStreamWrapper CreateShellStream(
        string terminalName,
        uint columns,
        uint rows,
        uint width,
        uint height,
        int bufferSize,
        IDictionary<TerminalModes, uint>? terminalModeValues = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ShellStream shellStream = _client.CreateShellStream(terminalName,
            columns,
            rows,
            width,
            height,
            bufferSize,
            terminalModeValues);
        return new ShellStreamWrapper(shellStream);
    }

    public Task<string> RunCommandAsync(string commandText, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // SSH.NET SshCommand is synchronous; run it off the caller's thread.
        return Task.Run(() =>
        {
            using SshCommand command = _client.CreateCommand(commandText);
            command.CommandTimeout = TimeSpan.FromSeconds(10);
            return command.Execute();
        }, cancellationToken);
    }

    public void AddForwardedPort(ForwardedPort port)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _client.AddForwardedPort(port);
    }

    public void RemoveForwardedPort(ForwardedPort port)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _client.RemoveForwardedPort(port);
    }

    public void Dispose()
    {
        Dispose(true);
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
