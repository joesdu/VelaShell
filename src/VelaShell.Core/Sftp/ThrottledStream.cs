namespace VelaShell.Core.Sftp;

/// <summary>
/// 带宽限制用的包装流(设置 → 文件传输 → 带宽限制):对读/写按字节速率整形。
/// 上传时包装本地读流(限读),下载时包装本地写流(限写),对 SSH.NET 完全透明。
/// 采用简单的滑动窗口:每消耗满一个配额窗口就 sleep 到窗口结束。
/// </summary>
public sealed class ThrottledStream(Stream inner, long bytesPerSecond) : Stream
{
    private readonly long _bytesPerSecond = Math.Max(1, bytesPerSecond);
    private readonly Stream _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private long _windowBytes;
    private long _windowStartTicks = Environment.TickCount64;

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => _inner.CanWrite;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    private void Throttle(int justTransferred)
    {
        _windowBytes += justTransferred;
        if (_windowBytes < _bytesPerSecond)
        {
            return;
        }
        long elapsed = Environment.TickCount64 - _windowStartTicks;
        long expectedMs = (_windowBytes * 1000) / _bytesPerSecond;
        if (expectedMs > elapsed)
        {
            Thread.Sleep((int)Math.Min(expectedMs - elapsed, 1000));
        }
        _windowBytes = 0;
        _windowStartTicks = Environment.TickCount64;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = _inner.Read(buffer, offset, count);
        Throttle(n);
        return n;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        int n = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        Throttle(n);
        return n;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
        Throttle(count);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await _inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        Throttle(count);
    }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
