using Renci.SshNet;
using VelaShell.Core.Ssh;

namespace VelaShell.Infrastructure.Ssh;

public class ShellStreamWrapper(ShellStream stream) : IShellStreamWrapper
{
    private readonly ShellStream _stream = stream ?? throw new ArgumentNullException(nameof(stream));

    public bool DataAvailable => _stream.DataAvailable;

    public bool CanRead => _stream.CanRead;

    public bool CanWrite => _stream.CanWrite;

    public string? Expect(string regex, TimeSpan timeout) => _stream.Expect(regex, timeout);

    public void WriteLine(string line) => _stream.WriteLine(line);

    public Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _stream.ReadAsync(buffer, offset, count, cancellationToken);

    public Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _stream.WriteAsync(buffer, offset, count, cancellationToken);

    public void Flush() => _stream.Flush();

    public void Resize(int columns, int rows)
    {
        if (columns <= 0 || rows <= 0)
        {
            return;
        }
        // SSH.NET sends a "window-change" channel request to the server.
        _stream.ChangeWindowSize((uint)columns, (uint)rows, 0, 0);
    }

    public void Dispose() => _stream.Dispose();
}
