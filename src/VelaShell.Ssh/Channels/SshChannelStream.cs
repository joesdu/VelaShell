// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §5.2  CHANNEL_DATA 与窗口
//   RFC 4254 §5.3  CHANNEL_EOF / CHANNEL_CLOSE
//   行为规格:      velashell-docs/zh/ssh/spec/09-dialing.md §5.1

using System.Buffers;
using System.IO.Pipelines;

namespace VelaShell.Ssh.Channels;

/// <summary>把一条通道当成一条双向字节流。</summary>
/// <remarks>
/// <para>
/// 它存在的直接理由是跳板：跳板拨号器要交出一个 <see cref="Stream"/>（拨号层的通用形态），
/// 而到目标的那条 <c>direct-tcpip</c> 通道正是那条流。别的只认 <see cref="Stream"/> 的 API
/// （<c>SslStream</c>、HTTP 客户端的连接回调）同样用得上它。
/// </para>
/// <para>
/// <b>只有异步读写</b>（架构原则 1）：同步版本只能靠阻塞线程等网络往返来实现。
/// 读：stdout；读到 0 表示对端发了 EOF 或通道正常关了 —— 连接异常断开时读会抛出连接的故障，
/// 不会假装成 EOF（velashell-docs/zh/ssh/spec/05 §4.4）。写：stdin，受对端窗口限制 ——
/// 窗口不够时写会等，不会丢数据。
/// </para>
/// </remarks>
public sealed class SshChannelStream : Stream
{
    private readonly SshChannel _channel;
    private readonly bool _ownsChannel;
    private readonly IAsyncDisposable? _owner;
    private int _disposed;

    /// <summary>经这条流写进 stdin 的累计字节数（<see cref="FlushAsync(CancellationToken)"/> 等的目标）。</summary>
    private long _written;

    /// <summary>把通道包成流。</summary>
    /// <param name="channel">通道。</param>
    /// <param name="ownsChannel">释放流时是否一并关掉通道。</param>
    public SshChannelStream(SshChannel channel, bool ownsChannel = true)
        : this(channel, ownsChannel, owner: null)
    {
    }

    /// <param name="channel">通道。</param>
    /// <param name="ownsChannel">释放流时是否一并关掉通道。</param>
    /// <param name="owner">释放流时最后再释放的东西（跳板拨号器用它挂住跳板连接）。</param>
    internal SshChannelStream(SshChannel channel, bool ownsChannel, IAsyncDisposable? owner)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _ownsChannel = ownsChannel;
        _owner = owner;
    }

    /// <summary>底层通道。</summary>
    public SshChannel Channel => _channel;

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException("通道是一条流，没有长度。");

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException("通道是一条流，没有位置。");
        set => throw new NotSupportedException("通道是一条流，没有位置。");
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (buffer.IsEmpty)
        {
            return 0;
        }

        PipeReader reader = _channel.StandardOutput;
        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> data = result.Buffer;

            if (!data.IsEmpty)
            {
                int take = (int)Math.Min(buffer.Length, data.Length);
                data.Slice(0, take).CopyTo(buffer.Span);
                reader.AdvanceTo(data.GetPosition(take));
                return take;
            }

            reader.AdvanceTo(data.Start, data.End);

            if (result.IsCompleted || result.IsCanceled)
            {
                return 0;
            }
        }
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        FlushResult result = await _channel.StandardInput.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref _written, buffer.Length);
        if (result.IsCompleted)
        {
            throw new IOException($"通道 {_channel.LocalId} 已经关闭，写不进去了。");
        }
    }

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>等写进来的字节全部交给会话发送（离开本地的 stdin 管道）。</summary>
    /// <exception cref="IOException">通道先关了，还有字节没发出去。</exception>
    /// <remarks>
    /// 写入只是进了 stdin 管道；对端窗口不够时，它们会在管道里一直等。
    /// 曾经这里是空操作，而管道里压着的那一截在释放时被丢掉 —— 调用方「写完、冲刷、关闭」
    /// 照着 <see cref="Stream"/> 的约定做了，最后一段数据却没有到对端。
    /// </remarks>
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return _channel.WaitStandardInputSentAsync(Interlocked.Read(ref _written), cancellationToken).AsTask();
    }

    /// <summary>空操作 —— <b>不等</b>数据交出去；要等请用 <see cref="FlushAsync(CancellationToken)"/>。</summary>
    /// <remarks>
    /// 冲刷要等对端的窗口，同步地等只能阻塞线程等网络往返（架构原则 1）。
    /// 不抛异常是因为不少包装器（<c>StreamWriter</c> 的同步释放之类）会无条件调它。
    /// </remarks>
    public override void Flush()
    {
    }

    /// <summary>不支持 —— 用 <see cref="ReadAsync(Memory{byte}, CancellationToken)"/>（架构原则 1）。</summary>
    /// <exception cref="NotSupportedException">总是。</exception>
    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("SshChannelStream 只支持异步读，请用 ReadAsync。");

    /// <summary>不支持 —— 用 <see cref="WriteAsync(ReadOnlyMemory{byte}, CancellationToken)"/>（架构原则 1）。</summary>
    /// <exception cref="NotSupportedException">总是。</exception>
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("SshChannelStream 只支持异步写，请用 WriteAsync。");

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    /// <remarks>先发 EOF、关通道，再释放挂在它上面的东西（跳板连接）。</remarks>
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await ReleaseAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask ReleaseAsync()
    {
        if (_ownsChannel)
        {
            // ⚠️ **先把 stdin 里压着的冲出去、发 EOF，再关通道。**直接关的话，
            //    通道一进入关闭状态泵就不再发，窗口没轮到的那一截（最多一整个管道）就丢了 ——
            //    跳板、direct-tcpip 隧道上「写完就关」的最后一段数据到不了对端。
            //    有时限：半死的链路上等不到窗口，就只能丢。
            using (CancellationTokenSource deadline = new(SshChannel.DisposeTimeout))
            {
                try
                {
                    await _channel.SendEofAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 释放路径不抛。
                }
            }

            try
            {
                await _channel.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 同上。
            }
        }

        if (_owner is not null)
        {
            try
            {
                await _owner.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 同上。
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>同步释放不阻塞：收尾交给后台（见 <see cref="DisposeAsync"/>）。</remarks>
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _ = DisposeInBackgroundAsync();
        }
        base.Dispose(disposing);
    }

    private async Task DisposeInBackgroundAsync()
    {
        try
        {
            await ReleaseAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 没有人能接这个异常了。
        }
    }
}
