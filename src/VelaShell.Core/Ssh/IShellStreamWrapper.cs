namespace VelaShell.Core.Ssh;

public interface IShellStreamWrapper : IDisposable
{
    bool DataAvailable { get; }

    bool CanRead { get; }

    bool CanWrite { get; }

    string? Expect(string regex, TimeSpan timeout);
    void WriteLine(string line);
    Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken);
    Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken);
    void Flush();

    /// <summary>
    /// Sends an SSH window-change request so the remote PTY matches the local terminal size.
    /// Pixel dimensions are reported as 0 (character-cell sizing only).
    /// </summary>
    void Resize(int columns, int rows);
}
