using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using Avalonia.Threading;

namespace VelaShell.Tests.TestSupport;

/// <summary>
/// headless UI 用例里直接对着内置 X 服务端说话的一条最小 X 连接(小端):发请求,回复与事件各进一个队列。
/// 等待一律经 <see cref="WaitForAsync{T}" />,边等边跑 UI 线程上排着的活(宿主的回调都切到 UI 线程)。
/// </summary>
internal sealed class XTestWire
{
    private readonly Stream _stream;
    private readonly ConcurrentQueue<byte[]> _replies = new();

    private XTestWire(Stream stream) => _stream = stream;

    /// <summary>这条连接的资源 ID 基址。</summary>
    public uint IdBase { get; private set; }

    /// <summary>根窗口。</summary>
    public uint Root { get; private set; }

    /// <summary>收到的事件(按到达顺序)。</summary>
    public ConcurrentQueue<byte[]> Events { get; } = new();

    /// <summary>建立连接(不带授权)并开始在后台读。</summary>
    public static async Task<XTestWire> ConnectAsync(Stream stream)
    {
        XTestWire wire = new(stream);
        await stream.WriteAsync(new byte[] { (byte)'l', 0, 11, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        await stream.FlushAsync();
        byte[] head = new byte[8];
        await stream.ReadExactlyAsync(head);
        Assert.AreEqual(1, head[0], "连接建立成功");
        byte[] rest = new byte[BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(6)) * 4];
        await stream.ReadExactlyAsync(rest);
        byte[] setup = [.. head, .. rest];
        int vendor = BinaryPrimitives.ReadUInt16LittleEndian(setup.AsSpan(24));
        wire.Root = BinaryPrimitives.ReadUInt32LittleEndian(setup.AsSpan(40 + ((vendor + 3) & ~3) + (setup[29] * 8)));
        wire.IdBase = BinaryPrimitives.ReadUInt32LittleEndian(setup.AsSpan(12));
        _ = wire.ReadAsync();
        return wire;
    }

    /// <summary>读一个小端的 32 位字段。</summary>
    public static uint U32(byte[] message, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(offset));

    /// <summary>等 <paramref name="probe" /> 给出值,其间跑 UI 线程上排着的活;5 秒等不到抛 <see cref="TimeoutException" />。</summary>
    public static async Task<T> WaitForAsync<T>(Func<T?> probe) where T : class
    {
        for (int i = 0; i < 250; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (probe() is { } value)
            {
                return value;
            }
            await Task.Delay(20);
        }
        throw new TimeoutException("等不到预期的状态");
    }

    private async Task ReadAsync()
    {
        try
        {
            while (true)
            {
                byte[] message = new byte[32];
                await _stream.ReadExactlyAsync(message);
                if (message[0] == 1 || (message[0] & 0x7F) == 35)
                {
                    byte[] extra = new byte[BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(4)) * 4];
                    await _stream.ReadExactlyAsync(extra);
                    message = [.. message, .. extra];
                }
                (message[0] == 1 ? _replies : Events).Enqueue(message);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or EndOfStreamException)
        {
        }
    }

    /// <summary>发一条请求(长度字段按内容算)。</summary>
    public async Task SendAsync(byte opcode, byte data, Action<Body> body)
    {
        Body b = new();
        body(b);
        byte[] payload = b.ToArray();
        byte[] request = new byte[4 + payload.Length];
        request[0] = opcode;
        request[1] = data;
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(2), (ushort)(request.Length / 4));
        payload.CopyTo(request, 4);
        await _stream.WriteAsync(request);
        await _stream.FlushAsync();
    }

    /// <summary>发一条有回复的请求,等它的回复。</summary>
    public async Task<byte[]> RequestAsync(byte opcode, byte data, Action<Body> body)
    {
        await SendAsync(opcode, data, body);
        return await WaitForAsync(() => _replies.TryDequeue(out byte[]? reply) ? reply : null);
    }

    /// <summary>一个往返(GetInputFocus):之前的请求都执行完了。</summary>
    public Task<byte[]> SyncAsync() => RequestAsync(43, 0, _ => { });

    /// <summary>InternAtom。</summary>
    public async Task<uint> InternAsync(string name)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(name);
        return U32(await RequestAsync(16, 0, b => b.U16((ushort)bytes.Length).U16(0).Bytes(bytes).Pad()), 8);
    }

    /// <summary>等一条给定类型的 ClientMessage(之前的事件丢掉)。</summary>
    public Task<byte[]> NextClientMessageAsync(uint type) => WaitForAsync(() =>
    {
        while (Events.TryDequeue(out byte[]? e))
        {
            if ((e[0] & 0x7F) == 33 && U32(e, 8) == type)
            {
                return e;
            }
        }
        return null;
    });

    /// <summary>请求体的小端写入器。</summary>
    internal sealed class Body
    {
        private readonly List<byte> _bytes = [];

        public Body U8(byte v) { _bytes.Add(v); return this; }

        public Body U16(ushort v) { _bytes.Add((byte)v); _bytes.Add((byte)(v >> 8)); return this; }

        public Body I16(short v) => U16(unchecked((ushort)v));

        public Body U32(uint v) => U16((ushort)v).U16((ushort)(v >> 16));

        public Body Zero(int n) { _bytes.AddRange(new byte[n]); return this; }

        public Body Bytes(byte[] data) { _bytes.AddRange(data); return this; }

        public Body Pad() { while (_bytes.Count % 4 != 0) { _bytes.Add(0); } return this; }

        public byte[] ToArray() => [.. _bytes];
    }
}
