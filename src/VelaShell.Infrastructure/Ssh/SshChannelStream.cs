using System.Buffers;
using System.IO.Pipelines;
using VelaShell.Ssh.Channels;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 把一条 <see cref="SshChannel" /> 适配成双工 <see cref="Stream" />。
/// </summary>
/// <remarks>
/// <para>
/// 库把通道的两端交成 <see cref="PipeReader" /> / <see cref="PipeWriter" /> —— 那是它内部
/// 表达背压的方式,也是它不开无界队列的原因。而宿主这一侧的契约
/// (<see cref="Core.Ssh.ISshClientWrapper.OpenUnixConnectionAsync" />、
/// <c>OpenTcpConnectionAsync</c>)收的是 <see cref="Stream" />,
/// 因为下游是 <c>HttpClient</c> 的 <c>ConnectCallback</c>、插件协议的字节管子这类只认 Stream 的东西。
/// </para>
/// <para>
/// <b>不用 <c>PipeReader.AsStream()</c> + <c>PipeWriter.AsStream()</c> 各包一个</b>:
/// 那样出来的是两个独立的流,合不成一个双工体,而且 <see cref="Dispose" /> 时谁去关通道也没了着落。
/// </para>
/// <para>
/// <b>写端不自动 Flush 就等于不发。</b><see cref="PipeWriter" /> 的写入只是进缓冲区,
/// 真正出网要 <c>FlushAsync</c>。请求-应答式的协议(HTTP over 隧道、docker.sock)
/// 在这里最容易挂死:请求写进去了却没冲出去,两端互相等对方。
/// </para>
/// </remarks>
internal sealed class SshChannelStream : Stream
{
    private readonly SshChannel _channel;
    private bool _readerCompleted;
    private bool _disposed;

    public SshChannelStream(SshChannel channel) =>
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));

    /// <inheritdoc />
    public override bool CanRead => !_disposed;

    /// <inheritdoc />
    public override bool CanWrite => !_disposed;

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
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (buffer.IsEmpty || _readerCompleted)
        {
            return 0;
        }

        PipeReader reader = _channel.StandardOutput;

        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> available = result.Buffer;

            if (!available.IsEmpty)
            {
                int copied = (int)Math.Min(buffer.Length, available.Length);
                available.Slice(0, copied).CopyTo(buffer.Span);

                // ⚠️ **没拷走的那部分留在管道里,不要自己存着。**
                //    AdvanceTo 一调,available 指向的内存就可能被回收 ——
                //    存下来的 ReadOnlySequence 会在下一次读时指向别人的数据,
                //    症状是隧道里偶发的乱字节,而且只在「一次读到的比调用方缓冲区大」时出现。
                reader.AdvanceTo(available.GetPosition(copied));
                return copied;
            }

            reader.AdvanceTo(available.Start, available.End);

            if (result.IsCompleted)
            {
                _readerCompleted = true;
                return 0;
            }

            if (result.IsCanceled)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    /// <inheritdoc />
    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (buffer.IsEmpty)
        {
            return;
        }

        // 写完立刻 Flush:见类型说明的第三条。
        _channel.StandardInput.Write(buffer.Span);
        FlushResult result = await _channel.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);

        if (result.IsCompleted)
        {
            throw new IOException("SSH 通道的写端已经关闭。");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>同步重载只是为了满足 <see cref="Stream" /> 的契约,能用异步的调用方都该用异步。</b>
    /// <para>
    /// 「同步读一条网络流」本来就意味着阻塞当前线程 —— <c>NetworkStream.Read</c> 也是这样,
    /// 这不是把异步包成同步。本类型内部的 <c>await</c> 一律 <c>ConfigureAwait(false)</c>,
    /// 不回捕获的上下文,所以这里的等待不会自己堵死自己。
    /// </para>
    /// </remarks>
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    /// <remarks>理由同 <see cref="Read(byte[], int, int)" />。</remarks>
    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public override Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override Task WriteAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override void Flush()
    {
        // WriteAsync 每次都已经 Flush 过了,这里没有东西可冲。
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// 向对端发写方向的 EOF(半关闭)。
    /// </summary>
    /// <remarks>
    /// 只读到 EOF 才收尾的协议(HTTP/1.0 的 <c>Connection: close</c>、<c>cat</c> 一类)
    /// 靠它结束。缺了它的症状是「传完了两端一起干等到超时」。
    /// </remarks>
    public async ValueTask SendEofAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }
        try
        {
            await _channel.StandardInput.CompleteAsync().ConfigureAwait(false);
            await _channel.SendEofAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 对端已经走了的话半关闭必然失败,拆链流程随后会收尾。
        }
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        try
        {
            await _channel.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 通道可能已经塌了 —— 释放路径不抛。
        }
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>请用 <c>await using</c>,不要用 <c>using</c>。</b>
    /// <para>
    /// 这条流最终交到**插件**手里(<c>IRemoteTunnelApi</c> 的签名是 <see cref="Stream" />),
    /// 而 <see cref="Stream" /> 的契约里有一个同步 <see cref="IDisposable.Dispose" /> ——
    /// 挡不住某个插件写 <c>using</c>。关一条 SSH 通道要发 <c>CHANNEL_CLOSE</c> 并等对端应答,
    /// 是货真价实的 I/O。
    /// </para>
    /// <para>
    /// 所以这条同步路径**只发起关闭、不等它完成**:阻塞等会死锁(迁移期实测过),
    /// 而在这里造一个假的同步更糟。<see cref="DisposeAsync" /> 才是完整的那条路 ——
    /// 宿主自己的代码一律走它。
    /// </para>
    /// </remarks>
    protected override void Dispose(bool disposing)
    {
        if (!disposing || _disposed)
        {
            return;
        }
        _ = DisposeAsync();
    }
}
