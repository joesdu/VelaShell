using System.Threading.Channels;
using VelaShell.Core.Ssh;
using VelaShell.Core.ZModem.Abstractions;
using VelaShell.Core.ZModem.Diagnostics;

namespace VelaShell.Terminal.ZModem;

/// <summary>
/// 把 <see cref="IShellStreamWrapper" />(SSH / ConPTY / 未来串口 · Telnet)适配为 ZMODEM 引擎
/// 所需的 <see cref="IByteDuplex" />。入站字节由路由器从桥的读循环经 <see cref="Push" /> 喂入
/// (而非直接读传输,以复用桥已有的单一读循环);出站字节直写传输。
/// </summary>
public sealed class ShellStreamByteDuplex(IShellStreamWrapper shellStream) : IByteDuplex
{
    private readonly IShellStreamWrapper _shellStream =
        shellStream ?? throw new ArgumentNullException(nameof(shellStream));

    private readonly Channel<ReadOnlyMemory<byte>> _inbound =
        Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

    /// <summary>由路由器喂入一段截获的入站字节(读循环线程调用)。</summary>
    /// <param name="data">属于 ZMODEM 会话的入站字节。</param>
    public void Push(ReadOnlyMemory<byte> data)
    {
        if (!data.IsEmpty)
        {
            _inbound.Writer.TryWrite(data);
        }
    }

    /// <summary>标记入站结束(会话终止 / 传输关闭),使引擎的读取得到 EOF。</summary>
    public void CompleteInbound() => _inbound.Writer.TryComplete();

    /// <inheritdoc />
    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (await _inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)
                && _inbound.Reader.TryRead(out ReadOnlyMemory<byte> chunk))
            {
                return chunk;
            }
        }
        catch (ChannelClosedException)
        {
            // 归一化为 EOF。
        }
        return ReadOnlyMemory<byte>.Empty;
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (data.IsEmpty)
        {
            return;
        }
        if (!_shellStream.CanWrite)
        {
            // 静默丢弃出站帧 = 对端永远等不到我们的应答。这条日志能立刻把它揪出来。
            ZModemTrace.Log($"TX DROPPED ({data.Length}B): shellStream.CanWrite == false");
            return;
        }
        ZModemTrace.LogBytes("TX", data.Span);
        try
        {
            // IShellStreamWrapper 只接受 byte[]+offset+count;若底层是数组段则零拷贝复用。
            if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(data, out ArraySegment<byte> seg)
                && seg.Array is not null)
            {
                await _shellStream.WriteAsync(seg.Array, seg.Offset, seg.Count, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                byte[] copy = data.ToArray();
                await _shellStream.WriteAsync(copy, 0, copy.Length, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            ZModemTrace.Log($"TX FAILED: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        _shellStream.Flush();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _inbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
