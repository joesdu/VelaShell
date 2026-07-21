using VelaShell.Core.Ssh;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// <see cref="ISshClientWrapper" /> 的 Tmds.Ssh 实现。
/// </summary>
public sealed class TmdsSshClientWrapper : ISshClientWrapper
{
    private readonly Tmds.Ssh.SshClientSettings _settings;
    private Tmds.Ssh.SshClient? _client;
    private TimeSpan _connectionTimeout;
    private bool _disposed;

    public TmdsSshClientWrapper(Tmds.Ssh.SshClientSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _connectionTimeout = settings.ConnectTimeout;
    }

    internal Tmds.Ssh.SshClient? InnerClient => _client;

    /// <summary>
    /// Tmds.Ssh 的 SshClient 无 IsConnected 属性:_client 仅在连接成功后被赋值,
    /// 再结合 Disconnected 令牌(底层连接丢失时取消)即可如实反映断线。
    /// </summary>
    public bool IsConnected
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _client is { } client && !client.Disconnected.IsCancellationRequested;
        }
    }

    public TimeSpan ConnectionTimeout
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _connectionTimeout;
        }
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _connectionTimeout = value;
            _settings.ConnectTimeout = value;
        }
    }

    public CancellationToken Disconnected => _client?.Disconnected ?? CancellationToken.None;

    // ---- Connection methods ----

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_client is not null) return;
        Tmds.Ssh.SshClient? client = null;
        try
        {
            client = new Tmds.Ssh.SshClient(_settings);
        }
        catch (ArgumentException argEx)
        {
            throw new SshConnectionException(
                $"SSH client configuration is invalid: {argEx.Message}", argEx);
        }
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException argEx)
        {
            SafeDisposeClient(client);
            throw new SshConnectionException(
                $"SSH connection rejected by Tmds.Ssh with invalid argument: {argEx.Message}", argEx);
        }
        catch (Exception ex)
        {
            SafeDisposeClient(client);
            if (TmdsSshInterop.Translate(ex, cancellationToken) is { } translated) throw translated;
            throw;
        }
        _client = client;
    }

    public void Disconnect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SafeDisposeClient(ref _client);
    }

    /// <summary>
    /// 在当前连接上异步打开交互式 shell。Tmds.Ssh 的 pty-req 只接受字符行列数:
    /// 像素尺寸(width/height)、bufferSize 与 terminalModeValues 无对应 API,被忽略。
    /// </summary>
    public async Task<IShellStreamWrapper> CreateShellStreamAsync(
        string terminalName,
        uint columns,
        uint rows,
        uint width,
        uint height,
        int bufferSize,
        IReadOnlyDictionary<TerminalMode, uint>? terminalModeValues = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_client is null) throw new InvalidOperationException("Not connected.");
        try
        {
            var options = new Tmds.Ssh.ExecuteOptions
            {
                AllocateTerminal = true,
                TerminalType = terminalName,
                TerminalWidth = (int)columns,
                TerminalHeight = (int)rows,
            };
            Tmds.Ssh.RemoteProcess process = await _client
                .ExecuteShellAsync(options, cancellationToken)
                .ConfigureAwait(false);
            return new ShellStreamWrapper(process);
        }
        catch (Exception ex) when (TmdsSshInterop.Translate(ex, cancellationToken) is { } translated)
        {
            throw translated;
        }
    }

    public async Task<string> RunCommandAsync(string commandText, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_client is null) throw new InvalidOperationException("Not connected.");
        try
        {
            using Tmds.Ssh.RemoteProcess process = await _client
                .ExecuteAsync(commandText, cancellationToken)
                .ConfigureAwait(false);

            using var reader = new StreamReader(
                process.ReadAsStream(Tmds.Ssh.StderrHandler.Ignore));
            string result = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.GetExitCodeAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex) when (ex is Tmds.Ssh.SshConnectionException && IsTornDown())
        {
            throw new ObjectDisposedException(nameof(TmdsSshClientWrapper), ex);
        }
        catch (Exception ex) when (TmdsSshInterop.Translate(ex, cancellationToken) is { } translated)
        {
            throw translated;
        }
    }

    public async Task<IPortForwardHandle> StartPortForwardAsync(PortForwardRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_client is null) throw new InvalidOperationException("Not connected.");
        try
        {
            return await TmdsSshPortForwardHandle.CreateAsync(_client, request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (TmdsSshInterop.Translate(ex, cancellationToken) is { } translated)
        {
            throw translated;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SafeDisposeClient(ref _client);
    }

    /// <summary>
    /// 安全释放 Tmds.Ssh.SshClient:通道关闭时 Dispose 可能抛出 SshChannelClosedException,
    /// 视为正常清理噪声,吞掉即可。
    /// </summary>
    private static void SafeDisposeClient(Tmds.Ssh.SshClient? client)
    {
        try { client?.Dispose(); } catch { }
    }

    private static void SafeDisposeClient(ref Tmds.Ssh.SshClient? client)
    {
        try { client?.Dispose(); } catch { }
        client = null;
    }

    private bool IsTornDown()
    {
        if (_disposed) return true;
        try { return _client is null; } catch { return true; }
    }
}
