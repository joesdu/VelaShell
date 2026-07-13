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

    /// <summary>获取底层 SSH 会话当前是否处于已连接状态。</summary>
    public bool IsConnected
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _client.IsConnected;
        }
    }

    /// <summary>获取或设置建立与维持连接时使用的超时时长。</summary>
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

    /// <summary>同步建立 SSH 连接,并将底层库异常翻译为 Core 层异常。</summary>
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

    /// <summary>异步建立 SSH 连接,并将底层库异常翻译为 Core 层异常。</summary>
    /// <param name="cancellationToken">用于取消连接操作的取消令牌。</param>
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

    /// <summary>断开当前 SSH 连接。</summary>
    public void Disconnect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _client.Disconnect();
    }

    /// <summary>创建带伪终端的交互式 Shell 流,并以 Core 中立类型表达终端模式。</summary>
    /// <param name="terminalName">远端 PTY 的终端类型名称(如 xterm)。</param>
    /// <param name="columns">终端的列数(字符宽度)。</param>
    /// <param name="rows">终端的行数(字符高度)。</param>
    /// <param name="width">终端的像素宽度。</param>
    /// <param name="height">终端的像素高度。</param>
    /// <param name="bufferSize">Shell 流的缓冲区大小。</param>
    /// <param name="terminalModeValues">可选的终端模式取值映射,为 null 时使用默认模式。</param>
    /// <returns>封装底层 Shell 流的 <see cref="IShellStreamWrapper" /> 实例。</returns>
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

    /// <summary>在远端执行单条命令并返回其标准输出结果;会话中途失效时归一化为已释放信号。</summary>
    /// <param name="commandText">要在远端执行的命令文本。</param>
    /// <param name="cancellationToken">用于取消命令执行的取消令牌。</param>
    /// <returns>命令执行完成后的输出结果。</returns>
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

    /// <summary>按请求启动端口转发,并返回可用于停止转发的句柄。</summary>
    /// <param name="request">描述端口转发方式与地址的请求。</param>
    /// <returns>控制该端口转发生命周期的 <see cref="IPortForwardHandle" /> 句柄。</returns>
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

    /// <summary>释放本包装器并销毁底层 SSH 客户端。</summary>
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
