namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 把 SSH 库的通道流(<c>SshChannel.AsStream()</c>)交给「只认 <see cref="Stream" />」的下游:
/// 补上同步的 <see cref="Read(byte[], int, int)" /> / <see cref="Write(byte[], int, int)" />,其余原样转发。
/// </summary>
/// <remarks>
/// <para>
/// 库的流只支持异步 —— 那是它的设计原则,同步重载直接抛。而宿主这一侧的契约
/// (<see cref="Core.Ssh.ISshClientWrapper.OpenUnixConnectionAsync" />、<c>OpenTcpConnectionAsync</c>)
/// 交出去的是一条普通 <see cref="Stream" />,下游是 <c>HttpClient</c> 的 <c>ConnectCallback</c>、
/// 插件协议的字节管子 —— 挡不住有人调同步的读写。
/// </para>
/// <para>
/// 「同步读一条网络流」本来就意味着阻塞当前线程 —— <c>NetworkStream.Read</c> 也是这样,
/// 这不是把异步包成同步。库内部的 <c>await</c> 一律 <c>ConfigureAwait(false)</c>,
/// 不回捕获的上下文,所以这里的等待不会自己堵死自己。
/// </para>
/// <para>
/// 通道本身怎么读、怎么写、怎么冲刷、怎么在释放时先发 EOF 再关,全在库的流里 —— 这里不重写一遍。
/// </para>
/// </remarks>
internal sealed class SyncCompatibleStream(Stream inner) : Stream
{
    private readonly Stream _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc />
    public override bool CanRead => _inner.CanRead;

    /// <inheritdoc />
    public override bool CanWrite => _inner.CanWrite;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.ReadAsync(buffer, offset, count, cancellationToken);

    /// <inheritdoc />
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.WriteAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.WriteAsync(buffer, offset, count, cancellationToken);

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    /// <inheritdoc />
    /// <remarks>见类型说明:同步读一条网络流本来就阻塞。</remarks>
    public override int Read(byte[] buffer, int offset, int count) =>
        _inner.ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    /// <remarks>理由同 <see cref="Read(byte[], int, int)" />。</remarks>
    public override void Write(byte[] buffer, int offset, int count) =>
        _inner.WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    /// <remarks>
    /// 不等数据交出去:冲刷要等对端的窗口,同步地等只能阻塞线程等网络往返。
    /// 要等请用 <see cref="FlushAsync(CancellationToken)" />。
    /// </remarks>
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override ValueTask DisposeAsync() => _inner.DisposeAsync();

    /// <inheritdoc />
    /// <remarks>
    /// <b>请用 <c>await using</c>,不要用 <c>using</c>。</b>关一条 SSH 通道要发 <c>CHANNEL_CLOSE</c>
    /// 并等对端应答,是货真价实的 I/O;这条同步路径<b>只发起关闭、不等它完成</b>
    /// (库的流自己会把收尾放到后台)—— 阻塞等会死锁(迁移期实测过)。
    /// </remarks>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
