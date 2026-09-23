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
/// 读：stdout；读到 0 表示对端发了 EOF 或通道关了。写：stdin，受对端窗口限制 ——
/// 窗口不够时写会等，不会丢数据。
/// </para>
/// </remarks>
public sealed class SshChannelStream : Stream
{
    private readonly SshChannel _channel;
    private readonly bool _ownsChannel;
    private readonly IAsyncDisposable? _owner;
    private int _disposed;

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
        if (result.IsCompleted)
        {
            throw new IOException($"通道 {_channel.LocalId} 已经关闭，写不进去了。");
        }
    }

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>写入时已经交给通道了，没有本地缓冲要冲。</summary>
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>没有本地缓冲，空操作。</summary>
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
            try
            {
                await _channel.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 释放路径不抛。
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
