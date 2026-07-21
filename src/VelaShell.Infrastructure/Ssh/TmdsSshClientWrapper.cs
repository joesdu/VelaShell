using Tmds.Ssh;
using VelaShell.Core.Ssh;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// <see cref="ISshClientWrapper" /> 的 Tmds.Ssh 实现。
/// </summary>
public sealed class TmdsSshClientWrapper(SshClientSettings settings) : ISshClientWrapper
{
    private readonly SshClientSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private SshClient? _client;
    private bool _disposed;

    internal SshClient? InnerClient => _client;

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

    /// <summary>
    /// Tmds.Ssh 的 SshClientSettings.ConnectTimeout 对应 SSH 连接超时,默认 10 秒。
    /// </summary>
    public TimeSpan ConnectionTimeout
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return field;
        }
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            field = value;
            _settings.ConnectTimeout = value;
        }
    } = settings.ConnectTimeout;

    /// <summary>
    /// Tmds.Ssh 的 SshClient.Disconnected 令牌,底层连接丢失时取消。
    /// </summary>
    public CancellationToken Disconnected => _client?.Disconnected ?? CancellationToken.None;

    // ---- Connection methods ----
    /// <summary>
    /// 连接到远程 SSH 服务器,成功后 _client 被赋值,失败时抛出 SshConnectionException。
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="VelaSshConnectionException"></exception>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_client is not null) return;
        SshClient? client;
        try
        {
            client = new SshClient(_settings);
        }
        catch (ArgumentException argEx)
        {
            throw new VelaSshConnectionException(
                $"SSH client configuration is invalid: {argEx.Message}", argEx);
        }
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException argEx)
        {
            SafeDisposeClient(client);
            throw new VelaSshConnectionException(
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

    /// <summary>
    /// 断开当前连接,释放 _client,不抛出异常。
    /// </summary>
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
            var options = new ExecuteOptions
            {
                AllocateTerminal = true,
                TerminalType = terminalName,
                TerminalWidth = (int)columns,
                TerminalHeight = (int)rows,
            };
            RemoteProcess process = await _client
                .ExecuteShellAsync(options, cancellationToken)
                .ConfigureAwait(false);
            return new ShellStreamWrapper(process);
        }
        catch (Exception ex) when (TmdsSshInterop.Translate(ex, cancellationToken) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>
    /// 在当前连接上异步执行命令,返回标准输出。Tmds.Ssh 的 ExecuteAsync 只返回标准输出,标准错误被忽略。
    /// </summary>
    /// <param name="commandText"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="ObjectDisposedException"></exception>
    public async Task<string> RunCommandAsync(string commandText, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_client is null) throw new InvalidOperationException("Not connected.");
        try
        {
            using RemoteProcess process = await _client
                .ExecuteAsync(commandText, cancellationToken)
                .ConfigureAwait(false);

            using var reader = new StreamReader(
                process.ReadAsStream(StderrHandler.Ignore));
            string result = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.GetExitCodeAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex) when (ex is SshConnectionException && IsTornDown())
        {
            throw new ObjectDisposedException(nameof(TmdsSshClientWrapper), ex);
        }
        catch (Exception ex) when (TmdsSshInterop.Translate(ex, cancellationToken) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>
    /// 在当前连接上异步启动端口转发,返回 <see cref="IPortForwardHandle" />。Tmds.Ssh 的 ForwardToRemoteAsync 只支持远程转发,本地转发被忽略。
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
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

    /// <summary>
    /// 释放当前连接,并将 _client 置 null,不抛出异常。
    /// </summary>
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
    private static void SafeDisposeClient(SshClient? client)
    {
        try { client?.Dispose(); } catch { }
    }

    private static void SafeDisposeClient(ref SshClient? client)
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
