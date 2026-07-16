using System.Threading.Channels;
using VelaShell.Core.ZModem.Abstractions;

namespace VelaShell.Core.Tests.ZModem;

/// <summary>
/// 测试用内存双工通道:写入本端即出现在对端的读队列,反之亦然。
/// 用于在无真实传输的情况下把 ZMODEM 发送方与接收方在进程内对接,或喂入预置字节序列。
/// </summary>
public sealed class InMemoryByteDuplex : IByteDuplex
{
    private readonly Channel<ReadOnlyMemory<byte>> _inbound;
    private readonly Channel<ReadOnlyMemory<byte>> _outbound;

    private InMemoryByteDuplex(
        Channel<ReadOnlyMemory<byte>> inbound,
        Channel<ReadOnlyMemory<byte>> outbound)
    {
        _inbound = inbound;
        _outbound = outbound;
    }

    /// <summary>创建一对相互连接的双工端点。</summary>
    public static (InMemoryByteDuplex A, InMemoryByteDuplex B) CreatePair()
    {
        var toA = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        var toB = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        var a = new InMemoryByteDuplex(toA, toB);
        var b = new InMemoryByteDuplex(toB, toA);
        return (a, b);
    }

    /// <summary>创建一个只读端点,预先灌入固定的入站字节(用于喂协议帧给解析器)。</summary>
    public static InMemoryByteDuplex FromInbound(IEnumerable<ReadOnlyMemory<byte>> chunks)
    {
        var inbound = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        foreach (ReadOnlyMemory<byte> chunk in chunks)
        {
            inbound.Writer.TryWrite(chunk);
        }
        inbound.Writer.TryComplete();
        var outbound = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        return new(inbound, outbound);
    }

    /// <summary>把本端已写出的全部出站字节拼接读出(供断言)。</summary>
    public async Task<byte[]> DrainOutboundAsync()
    {
        _outbound.Writer.TryComplete();
        var all = new List<byte>();
        await foreach (ReadOnlyMemory<byte> chunk in _outbound.Reader.ReadAllAsync())
        {
            all.AddRange(chunk.ToArray());
        }
        return [.. all];
    }

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

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        _outbound.Writer.TryWrite(data.ToArray());
        return ValueTask.CompletedTask;
    }

    public ValueTask FlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _inbound.Writer.TryComplete();
        _outbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
