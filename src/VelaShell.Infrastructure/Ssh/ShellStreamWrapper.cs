using Renci.SshNet;
using VelaShell.Core.Ssh;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 将 SSH.NET 的 <see cref="ShellStream" /> 适配到 <see cref="IShellStreamWrapper" /> 抽象。
/// </summary>
public class ShellStreamWrapper(ShellStream stream) : IShellStreamWrapper
{
    private readonly ShellStream _stream = stream ?? throw new ArgumentNullException(nameof(stream));

    /// <summary>当前是否有数据可读而不阻塞。</summary>
    public bool DataAvailable => _stream.DataAvailable;

    /// <summary>底层流是否支持读取。</summary>
    public bool CanRead => _stream.CanRead;

    /// <summary>底层流是否支持写入。</summary>
    public bool CanWrite => _stream.CanWrite;

    /// <summary>阻塞直到给定正则表达式匹配到输入数据或超时。</summary>
    public string? Expect(string regex, TimeSpan timeout) => _stream.Expect(regex, timeout);

    /// <summary>向流写入一行文本并附带换行符。</summary>
    public void WriteLine(string line) => _stream.WriteLine(line);

    /// <summary>从流异步读取一段字节到给定缓冲区。</summary>
    public Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _stream.ReadAsync(buffer, offset, count, cancellationToken);

    /// <summary>从给定缓冲区向流异步写入一段字节。</summary>
    public Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _stream.WriteAsync(buffer, offset, count, cancellationToken);

    /// <summary>将任何缓冲数据刷新到底层流。</summary>
    public void Flush() => _stream.Flush();

    /// <summary>发送 window-change 请求以调整远程终端尺寸。</summary>
    public void Resize(int columns, int rows)
    {
        if (columns <= 0 || rows <= 0)
        {
            return;
        }
        // SSH.NET 向服务器发送 "window-change" 通道请求。
        _stream.ChangeWindowSize((uint)columns, (uint)rows, 0, 0);
    }

    /// <summary>释放底层流并禁止终结。</summary>
    public void Dispose()
    {
        _stream.Dispose();
        GC.SuppressFinalize(this);
    }
}
