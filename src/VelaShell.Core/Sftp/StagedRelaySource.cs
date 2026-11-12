using System.Security.Cryptography;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.Core.Sftp;

/// <summary>
/// A fully downloaded, seekable relay snapshot. Reuse within one run's upload retries,
/// then dispose; it neither owns nor closes either remote session.
/// </summary>
public sealed class StagedRelaySource : IAsyncDisposable
{
    private readonly CacheStream _cache;
    private readonly string _sourcePath;
    private readonly DateTime? _mtime;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    private StagedRelaySource(CacheStream cache, string sourcePath, DateTime? mtime)
    {
        _cache = cache;
        _sourcePath = sourcePath;
        _mtime = mtime;
    }

    /// <summary>Stat and download the entire source, validating it before any target can be written.</summary>
    public static Task<StagedRelaySource> PrepareAsync(ISftpService source, Guid sourceSessionId,
        string sourcePath, CancellationToken cancellationToken = default) =>
        PrepareCoreAsync(source, sourceSessionId, sourcePath, CreateStorage, cancellationToken);

    // Per-call factory allows deterministic local I/O fault tests without a global test hook.
    internal static async Task<StagedRelaySource> PrepareCoreAsync(ISftpService source, Guid sourceSessionId,
        string sourcePath, Func<long, Stream> createStorage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        RemoteFileInfo info = await source.GetFileInfoAsync(sourceSessionId, sourcePath, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        Stream storage;
        try { storage = createStorage(info.Size); }
        catch (Exception ex) when (IsLocalFailure(ex)) { throw new RelayCacheException("create", ex); }
        var cache = new CacheStream(storage, info.Size, sourcePath);
        try
        {
            await source.DownloadStreamAsync(sourceSessionId, sourcePath, cache,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await cache.SealAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new StagedRelaySource(cache, sourcePath, info.LastModified == default ? null : info.LastModified);
        }
        catch
        {
            await cache.ReleaseAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Upload the immutable snapshot. Each attempt resets its position; the target service
    /// remains responsible for tail verification, safe resume and durable acknowledgement tracking.
    /// Calls on one snapshot are serialized. Target failures leave the snapshot available for retry.
    /// </summary>
    public async Task UploadAsync(ISftpService target, Guid targetSessionId, string targetPath,
        IProgress<TransferProgress>? progress = null, Action? writing = null, long resumeOffset = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _cache.Position = 0;
            long length = _cache.Length;
            using var input = new RemoteRelay.CheckedReadStream(_cache, length, _sourcePath, requireFullConsumption: resumeOffset <= 0);
            RemoteRelay.CompletionProgress? completion = progress is null ? null : new RemoteRelay.CompletionProgress(progress);
            cancellationToken.ThrowIfCancellationRequested();
            writing?.Invoke();
            await target.UploadStreamAsync(targetSessionId, input, targetPath, length, _mtime,
                completion, resumeOffset, cancellationToken: cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await input.VerifyCompleteAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            completion?.Complete();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Clear the owned memory or close/delete the temporary file. Never mask a transfer failure.</summary>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await _cache.ReleaseAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private static Stream CreateStorage(long length)
    {
        string home = UserPathResolver.Home;
        if (!Path.IsPathFullyQualified(home)) throw new IOException();
        string directory = Path.Combine(home, ".velashell", "temp");
        // Windows inherits the user's directory ACL. Unix applies 0700 only to newly
        // created directories; never chmod an existing home, .velashell or temp directory.
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        SweepStale(directory);
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            BufferSize = 1, // no extra managed FileStream buffer retaining payload after disposal
            Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose,
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var stream = new FileStream(Path.Combine(directory, $"velashell-relay-{Guid.NewGuid():N}.tmp"), options);
        try
        {
            // Unix DeleteOnClose runs in managed disposal, not on process termination.
            // Unlink before writing payload; the open handle keeps the snapshot seekable.
            if (!OperatingSystem.IsWindows()) File.Delete(stream.Name);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Reclaim old relay files left by previous versions or interrupted cleanup, without touching live snapshots.</summary>
    public static int SweepStale(string? directory = null, DateTime? utcNow = null)
    {
        directory ??= Path.Combine(UserPathResolver.Home, ".velashell", "temp");
        DateTime cutoff = (utcNow ?? DateTime.UtcNow).AddDays(-1);
        int removed = 0;
        try
        {
            foreach (string path in Directory.EnumerateFiles(directory, "velashell-relay-*.tmp"))
            {
                string name = Path.GetFileName(path);
                if (name.Length != "velashell-relay-".Length + 32 + ".tmp".Length
                    || !Guid.TryParseExact(name.AsSpan("velashell-relay-".Length, 32), "N", out _))
                {
                    continue;
                }

                try
                {
                    var file = new FileInfo(path);
                    if (file.LinkTarget is not null || (file.Attributes & FileAttributes.ReparsePoint) != 0
                        || file.LastWriteTimeUtc >= cutoff)
                    {
                        continue;
                    }
                    // Read/write avoids blocking on a Unix FIFO; non-seekable entries are not caches.
                    // Delete sharing permits unlink while retaining the exclusive read/write lease.
                    using var orphan = new FileStream(path, new FileStreamOptions
                    {
                        Mode = FileMode.Open,
                        Access = FileAccess.ReadWrite,
                        Share = FileShare.Delete,
                    });
                    if (!orphan.CanSeek) continue;
                    File.Delete(path);
                    removed++;
                }
                catch (Exception ex) when (IsLocalFailure(ex)) { }
            }
        }
        catch (Exception ex) when (IsLocalFailure(ex)) { }
        return removed;
    }

    private static bool IsLocalFailure(Exception ex) => ex is IOException or UnauthorizedAccessException
        or System.Security.SecurityException or ObjectDisposedException;

    // Owns storage, but Dispose from a backend must not close it. Only ReleaseAsync owns cleanup.
    // During download it is append-only; after sealing all writes are rejected, even through
    // a destination reference retained by the source service.
    private sealed class CacheStream(Stream storage, long expectedLength, string path) : Stream
    {
        private bool _ready;
        private bool _released;
        private long _written;
        private RelayCacheException? _failure;

        private T Local<T>(string operation, Func<T> action)
        {
            if (_failure is not null) throw _failure;
            try { return action(); }
            catch (Exception ex) when (IsLocalFailure(ex)) { throw Remember(operation, ex); }
        }

        private RelayCacheException Remember(string operation, Exception ex) =>
            _failure ??= new RelayCacheException(operation, ex);

        public override bool CanRead => _ready && !_released;
        public override bool CanSeek => _ready && !_released;
        public override bool CanWrite => !_ready && !_released;
        public override long Length => Local("length", () => storage.Length);
        public override long Position
        {
            get => Local("seek", () => storage.Position);
            set => Seek(value, SeekOrigin.Begin);
        }

        private void BeforeWrite(int count)
        {
            ObjectDisposedException.ThrowIf(_released, this);
            if (_ready) throw new NotSupportedException();
            if (_failure is not null) throw _failure;
            if (expectedLength > 0 && count > expectedLength - _written)
                throw new RemoteTransferLengthException(path, expectedLength, _written + count);
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            BeforeWrite(buffer.Length);
            try { storage.Write(buffer); }
            catch (Exception ex) when (IsLocalFailure(ex)) { throw Remember("write", ex); }
            _written += buffer.Length;
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeWrite(buffer.Length);
            try { await storage.WriteAsync(buffer, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (IsLocalFailure(ex)) { throw Remember("write", ex); }
            _written += buffer.Length;
        }

        public async Task SealAsync(CancellationToken cancellationToken)
        {
            if (_failure is not null) throw _failure;
            if (expectedLength > 0 && _written != expectedLength)
                throw new RemoteTransferLengthException(path, expectedLength, _written);
            await FlushAsync(cancellationToken).ConfigureAwait(false);
            if (Length != _written) throw new RemoteTransferLengthException(path, _written, Length);
            _ready = true;
            Position = 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (!CanRead) throw new NotSupportedException();
            if (_failure is not null) throw _failure;
            try { return storage.Read(buffer); }
            catch (Exception ex) when (IsLocalFailure(ex)) { throw Remember("read", ex); }
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CanRead) throw new NotSupportedException();
            if (_failure is not null) throw _failure;
            try { return await storage.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (IsLocalFailure(ex)) { throw Remember("read", ex); }
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            if (!CanSeek) throw new NotSupportedException();
            return Local("seek", () => storage.Seek(offset, origin));
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() => Local("flush", () => { storage.Flush(); return true; });
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (_failure is not null) throw _failure;
            try { await storage.FlushAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (IsLocalFailure(ex)) { throw Remember("flush", ex); }
        }

        public async ValueTask ReleaseAsync()
        {
            if (_released) return;
            _released = true;
            if (storage is MemoryStream memory && memory.TryGetBuffer(out ArraySegment<byte> buffer))
                CryptographicOperations.ZeroMemory(buffer.Array);
            try { await storage.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Never replace a source/target failure (including cancellation) in a finally.
                System.Diagnostics.Trace.WriteLine($"[VelaShell] Relay cache cleanup failed: {ex.GetType().Name}");
            }
        }
    }
}

/// <summary>A permanent local staging I/O failure, not a retryable remote transport failure.</summary>
/// <remarks>Create a cache failure without exposing local paths or raw exception messages.</remarks>
public sealed class RelayCacheException(string operation, Exception innerException) : IOException(Strings.Get("Relay_CacheFailed"), innerException)
{

    /// <summary>Nonlocalized operation identifier suitable for structured diagnostics.</summary>
    public string Operation { get; } = operation;
}
