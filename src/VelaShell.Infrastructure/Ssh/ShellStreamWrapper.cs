using VelaShell.Core.Ssh;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 将 Tmds.Ssh 的 <see cref="Tmds.Ssh.RemoteProcess" /> 适配到 <see cref="IShellStreamWrapper" />。
/// </summary>
public class ShellStreamWrapper(Tmds.Ssh.RemoteProcess process) : IShellStreamWrapper
{
    private readonly Tmds.Ssh.RemoteProcess _process = process ?? throw new ArgumentNullException(nameof(process));
    private Stream? _outputStream;
    private volatile bool _disposed;
    private bool _readEof;

    /// <summary>读端发出 EOF 前始终保持可读(Stream ReadAsync 返回 0 表示 EOF)。</summary>
    public bool CanRead => !_disposed && !_readEof;

    /// <summary>RemoteProcess 存活期间均可写入。</summary>
    public bool CanWrite => !_disposed;

    /// <summary>当前是否有数据可读而不阻塞(对 Stream 式 IO 无意义)。</summary>
    public bool DataAvailable => false;

    /// <summary>不被调用。</summary>
    public string? Expect(string regex, TimeSpan timeout) =>
        throw new NotSupportedException("Expect is not supported. Use ReadAsync instead.");

    /// <summary>不被调用。</summary>
    public void WriteLine(string line) =>
        throw new NotSupportedException("WriteLine is not supported. Use WriteAsync instead.");

    /// <summary>从远程进程标准输出读原始字节。EOF 时返回 0 并设置 _readEof。</summary>
    public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_disposed || _readEof) return 0;
        try
        {
            _outputStream ??= _process.ReadAsStream(Tmds.Ssh.StderrHandler.Ignore);
            int bytesRead = await _outputStream
                .ReadAsync(buffer.AsMemory(offset, count), cancellationToken)
                .ConfigureAwait(false);
            if (bytesRead == 0) _readEof = true;
            return bytesRead;
        }
        catch (Tmds.Ssh.SshChannelClosedException) { _readEof = true; return 0; }
        catch (ObjectDisposedException) { _readEof = true; return 0; }
        catch (IOException) { _readEof = true; return 0; }
        catch (OperationCanceledException) { _readEof = true; return 0; }
    }

    /// <summary>向远程进程标准输入写入原始字节。通道已关闭时标记 _disposed 并抛出 ObjectDisposedException。</summary>
    public async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            await _process.WriteAsync(
                new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Tmds.Ssh.SshChannelClosedException)
        {
            _disposed = true;
            throw new ObjectDisposedException(nameof(ShellStreamWrapper));
        }
    }

    /// <summary>空操作(Tmds.Ssh RemoteProcess 通过异步写入自动保证送达)。</summary>
    public void Flush() { }

    /// <summary>发送 window-change 请求以调整远程终端尺寸。</summary>
    public void Resize(int columns, int rows)
    {
        if (_disposed || columns <= 0 || rows <= 0) return;
        try { _process.SetTerminalSize(columns, rows); } catch { }
    }

    /// <summary>释放底层 RemoteProcess。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _process.Dispose(); } catch { }
        GC.SuppressFinalize(this);
    }
}
