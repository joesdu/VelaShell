using Renci.SshNet;
using VelaShell.Core.Ssh;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// <see cref="ISshClientWrapper" /> 的 SSH.NET 实现。库类型不越过此边界:终端模式/端口转发
/// 参数用 Core 的中立类型表达,SSH.NET 异常翻译为 Core 的 SshClientException 层级 ——
/// 更换底层库时重写本文件(与 <see cref="SftpClientWrapper" /> 等姐妹实现)即可。
/// </summary>
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
        try
        {
            _client.Connect();
        }
        catch (Exception ex) when (SshNetInterop.Translate(ex) is { } translated)
        {
            throw translated;
        }
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

    public IShellStreamWrapper CreateShellStream(
        string terminalName,
        uint columns,
        uint rows,
        uint width,
        uint height,
        int bufferSize,
        IReadOnlyDictionary<TerminalMode, uint>? terminalModeValues = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            ShellStream shellStream = _client.CreateShellStream(terminalName,
                columns,
                rows,
                width,
                height,
                bufferSize,
                SshNetInterop.MapTerminalModes(terminalModeValues));
            return new ShellStreamWrapper(shellStream);
        }
        catch (Exception ex) when (SshNetInterop.Translate(ex) is { } translated)
        {
            throw translated;
        }
    }

    public async Task<string> RunCommandAsync(string commandText, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            using SshCommand command = _client.CreateCommand(commandText);
            command.CommandTimeout = TimeSpan.FromSeconds(10);
            await command.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return command.Result;
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshConnectionException or NullReferenceException && IsTornDown())
        {
            // The session was disconnected/disposed while the command was in flight (e.g. its
            // tab closed mid-probe). Normalise to the "session is gone" signal callers already
            // handle, instead of surfacing SSH.NET's internal failure.
            throw new ObjectDisposedException(nameof(SshClientWrapper), ex);
        }
        catch (Exception ex) when (SshNetInterop.Translate(ex) is { } translated)
        {
            throw translated;
        }
    }

    public IPortForwardHandle StartPortForward(PortForwardRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            return new SshNetPortForwardHandle(_client, SshNetInterop.CreateForwardedPort(request));
        }
        catch (Exception ex) when (SshNetInterop.Translate(ex) is { } translated)
        {
            throw translated;
        }
    }

    public void Dispose()
    {
        Dispose(true);
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
