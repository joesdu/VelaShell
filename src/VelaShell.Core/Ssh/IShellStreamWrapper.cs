namespace VelaShell.Core.Ssh;

/// <summary>
/// Library-neutral abstraction over an interactive SSH shell stream, decoupling
/// callers from the underlying SSH implementation.
/// </summary>
public interface IShellStreamWrapper : IDisposable
{
    /// <summary>Gets a value indicating whether unread data is currently buffered on the stream.</summary>
    bool DataAvailable { get; }

    /// <summary>Gets a value indicating whether the stream currently supports reading.</summary>
    bool CanRead { get; }

    /// <summary>Gets a value indicating whether the stream currently supports writing.</summary>
    bool CanWrite { get; }

    /// <summary>Waits for output matching the given regular expression, up to the specified timeout.</summary>
    /// <param name="regex">The regular expression to match against incoming output.</param>
    /// <param name="timeout">The maximum time to wait for a match.</param>
    /// <returns>The matched text, or <c>null</c> if the timeout elapses without a match.</returns>
    string? Expect(string regex, TimeSpan timeout);

    /// <summary>Writes a line of text to the shell, appending a line terminator.</summary>
    /// <param name="line">The text to send to the remote shell.</param>
    void WriteLine(string line);

    /// <summary>Asynchronously reads output bytes from the shell into the buffer.</summary>
    /// <param name="buffer">The buffer that receives the read bytes.</param>
    /// <param name="offset">The zero-based byte offset in <paramref name="buffer" /> at which to begin storing data.</param>
    /// <param name="count">The maximum number of bytes to read.</param>
    /// <param name="cancellationToken">A token to cancel the read operation.</param>
    /// <returns>The number of bytes actually read.</returns>
    Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken);

    /// <summary>Asynchronously writes bytes to the shell.</summary>
    /// <param name="buffer">The buffer containing the bytes to write.</param>
    /// <param name="offset">The zero-based byte offset in <paramref name="buffer" /> from which to begin writing.</param>
    /// <param name="count">The number of bytes to write.</param>
    /// <param name="cancellationToken">A token to cancel the write operation.</param>
    /// <returns>A task that completes when the write finishes.</returns>
    Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken);

    /// <summary>Flushes any buffered output to the remote shell.</summary>
    void Flush();

    /// <summary>
    /// Sends an SSH window-change request so the remote PTY matches the local terminal size.
    /// Pixel dimensions are reported as 0 (character-cell sizing only).
    /// </summary>
    void Resize(int columns, int rows);
}
