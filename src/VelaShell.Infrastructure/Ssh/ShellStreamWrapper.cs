using Renci.SshNet;
using VelaShell.Core.Ssh;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// Adapts an SSH.NET <see cref="ShellStream" /> to the <see cref="IShellStreamWrapper" /> abstraction.
/// </summary>
public class ShellStreamWrapper(ShellStream stream) : IShellStreamWrapper
{
    private readonly ShellStream _stream = stream ?? throw new ArgumentNullException(nameof(stream));

    /// <summary>Whether data is currently available to read without blocking.</summary>
    public bool DataAvailable => _stream.DataAvailable;

    /// <summary>Whether the underlying stream supports reading.</summary>
    public bool CanRead => _stream.CanRead;

    /// <summary>Whether the underlying stream supports writing.</summary>
    public bool CanWrite => _stream.CanWrite;

    /// <summary>Blocks until the given regular expression matches the incoming data or the timeout elapses.</summary>
    public string? Expect(string regex, TimeSpan timeout) => _stream.Expect(regex, timeout);

    /// <summary>Writes a line of text followed by a line terminator to the stream.</summary>
    public void WriteLine(string line) => _stream.WriteLine(line);

    /// <summary>Asynchronously reads a sequence of bytes from the stream into the given buffer.</summary>
    public Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _stream.ReadAsync(buffer, offset, count, cancellationToken);

    /// <summary>Asynchronously writes a sequence of bytes from the given buffer to the stream.</summary>
    public Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _stream.WriteAsync(buffer, offset, count, cancellationToken);

    /// <summary>Flushes any buffered data to the underlying stream.</summary>
    public void Flush() => _stream.Flush();

    /// <summary>Sends a window-change request to resize the remote terminal.</summary>
    public void Resize(int columns, int rows)
    {
        if (columns <= 0 || rows <= 0)
        {
            return;
        }
        // SSH.NET sends a "window-change" channel request to the server.
        _stream.ChangeWindowSize((uint)columns, (uint)rows, 0, 0);
    }

    /// <summary>Releases the underlying stream and suppresses finalization.</summary>
    public void Dispose()
    {
        _stream.Dispose();
        GC.SuppressFinalize(this);
    }
}
