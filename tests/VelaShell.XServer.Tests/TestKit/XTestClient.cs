using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Text;
using System.Threading.Channels;
using VelaShell.XServer.Server;

namespace VelaShell.XServer.Tests.TestKit;

/// <summary>内存里的一对双工流:一端给服务端,一端给测试客户端。</summary>
internal static class DuplexPair
{
    public static (Stream Server, Stream Client) Create()
    {
        Pipe toServer = new(), toClient = new();
        return (new DuplexStream(toServer.Reader, toClient.Writer), new DuplexStream(toClient.Reader, toServer.Writer));
    }

    private sealed class DuplexStream(PipeReader reader, PipeWriter writer) : Stream
    {
        private readonly Stream _in = reader.AsStream();
        private readonly Stream _out = writer.AsStream();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _out.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _out.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => _in.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _in.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => _out.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _out.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // 先放掉挂着的读,再完成 reader:在有读挂起时直接 Complete,那个读永远不会返回。
                reader.CancelPendingRead();
                _in.Dispose();
                _out.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

/// <summary>服务端发来的一条消息(回复 / 事件 / 错误)。</summary>
internal sealed record XMessage(byte[] Bytes, bool BigEndian)
{
    public byte Kind => Bytes[0];
    public bool IsError => Kind == 0;
    public bool IsReply => Kind == 1;
    public byte EventCode => (byte)(Kind & 0x7F);
    public byte Detail => Bytes[1];
    public ushort Sequence => U16(2);

    public ushort U16(int offset) => BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(Bytes.AsSpan(offset)) : BinaryPrimitives.ReadUInt16LittleEndian(Bytes.AsSpan(offset));
    public short I16(int offset) => (short)U16(offset);
    public uint U32(int offset) => BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(Bytes.AsSpan(offset)) : BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(offset));
}

/// <summary>
/// 最小的 X 协议客户端:逐字节拼请求、按序号收回复。只给测试用 —— 它故意不做任何「聪明」的事,
/// 这样测试断言的是服务端的字节,而不是某个客户端库的解释。
/// </summary>
internal sealed class XTestClient : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly Channel<XMessage> _incoming = Channel.CreateUnbounded<XMessage>();
    private readonly List<XMessage> _backlog = [];
    private readonly Task _serverTask;

    /// <summary>服务端为这个连接跑的 <c>ServeAsync</c>:服务端主动断开时它结束。</summary>
    public Task ServerTask => _serverTask;
    private readonly Task _readTask;
    private ushort _sequence;

    private XTestClient(Stream stream, Task serverTask, bool bigEndian, byte[] setupReply)
    {
        _stream = stream;
        _serverTask = serverTask;
        BigEndian = bigEndian;
        SetupReply = setupReply;
        _readTask = ReadLoopAsync();
    }

    public bool BigEndian { get; }

    public byte[] SetupReply { get; }

    public uint ResourceBase => ReadSetup32(12);

    public uint RootWindow => ReadSetup32(SetupScreenOffset);

    private int _nextId = 1;

    public uint NewId() => ResourceBase | (uint)_nextId++;

    private uint ReadSetup32(int offset) => BigEndian
        ? BinaryPrimitives.ReadUInt32BigEndian(SetupReply.AsSpan(offset))
        : BinaryPrimitives.ReadUInt32LittleEndian(SetupReply.AsSpan(offset));

    /// <summary>第一块 SCREEN 在建立回复里的偏移:40 字节定长 + vendor(补齐)+ 8 × FORMAT 数。</summary>
    private int SetupScreenOffset
    {
        get
        {
            int vendorLength = BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(SetupReply.AsSpan(24)) : BinaryPrimitives.ReadUInt16LittleEndian(SetupReply.AsSpan(24));
            int formats = SetupReply[29];
            return 40 + ((vendorLength + 3) & ~3) + (8 * formats);
        }
    }

    /// <summary>连上服务端(内存双工),走完连接建立。建立失败时返回的 SetupReply[0] 为 0。</summary>
    public static async Task<XTestClient> ConnectAsync(X11Server server, bool bigEndian = false, bool isLocal = true,
        string authName = "", byte[]? authData = null)
    {
        (Stream serverSide, Stream clientSide) = DuplexPair.Create();
        Task serverTask = server.ServeAsync(serverSide, isLocal);

        byte[] name = Encoding.ASCII.GetBytes(authName);
        authData ??= [];
        List<byte> hello = [bigEndian ? (byte)'B' : (byte)'l', 0];
        hello.AddRange(U16Bytes(11, bigEndian));
        hello.AddRange(U16Bytes(0, bigEndian));
        hello.AddRange(U16Bytes((ushort)name.Length, bigEndian));
        hello.AddRange(U16Bytes((ushort)authData.Length, bigEndian));
        hello.AddRange([0, 0]);
        hello.AddRange(Pad(name));
        hello.AddRange(Pad(authData));
        await clientSide.WriteAsync(hello.ToArray());
        await clientSide.FlushAsync();

        byte[] head = new byte[8];
        await clientSide.ReadExactlyAsync(head);
        int extra = (bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(6)) : BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(6))) * 4;
        byte[] reply = new byte[8 + extra];
        head.CopyTo(reply, 0);
        await clientSide.ReadExactlyAsync(reply.AsMemory(8));
        return new XTestClient(clientSide, serverTask, bigEndian, reply);
    }

    private static byte[] U16Bytes(ushort v, bool be)
    {
        byte[] b = new byte[2];
        if (be) BinaryPrimitives.WriteUInt16BigEndian(b, v); else BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        return b;
    }

    private static byte[] Pad(byte[] data) => [.. data, .. new byte[((data.Length + 3) & ~3) - data.Length]];

    // ------------------------------------------------------------------ 请求

    /// <summary>请求正文的组装器(按客户端字节序)。</summary>
    public sealed class Body(bool bigEndian)
    {
        private readonly List<byte> _bytes = [];
        public Body U8(byte v) { _bytes.Add(v); return this; }
        public Body U16(ushort v) { _bytes.AddRange(U16Bytes(v, bigEndian)); return this; }
        public Body I16(short v) => U16(unchecked((ushort)v));
        public Body I32(int v) => U32(unchecked((uint)v));
        public Body U32(uint v)
        {
            byte[] b = new byte[4];
            if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(b, v); else BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            _bytes.AddRange(b);
            return this;
        }
        public Body Bytes(byte[] data) { _bytes.AddRange(data); return this; }
        public Body Pad() { while (_bytes.Count % 4 != 0) _bytes.Add(0); return this; }
        public byte[] ToArray() => [.. _bytes];
    }

    /// <summary>发一条请求,返回它的序号。</summary>
    public async Task<ushort> SendAsync(byte opcode, byte data, Action<Body>? body = null, bool bigRequest = false)
    {
        Body b = new(BigEndian);
        body?.Invoke(b);
        b.Pad();
        byte[] payload = b.ToArray();
        List<byte> request = [opcode, data];
        if (bigRequest)
        {
            request.AddRange([0, 0]);
            Body len = new(BigEndian);
            len.U32((uint)((payload.Length + 8) / 4));
            request.AddRange(len.ToArray());
        }
        else
        {
            request.AddRange(U16Bytes((ushort)((payload.Length + 4) / 4), BigEndian));
        }
        request.AddRange(payload);
        await _stream.WriteAsync(request.ToArray());
        await _stream.FlushAsync();
        return ++_sequence;
    }

    /// <summary>发请求并等它的回复(或错误)。</summary>
    public async Task<XMessage> RequestAsync(byte opcode, byte data, Action<Body>? body = null)
    {
        ushort seq = await SendAsync(opcode, data, body);
        return await NextAsync(m => (m.IsReply || m.IsError) && m.Sequence == seq);
    }

    /// <summary>等下一条满足条件的消息;之前收到的不满足条件的消息留着给后续的 NextAsync。</summary>
    public async Task<XMessage> NextAsync(Func<XMessage, bool> match, int timeoutMs = 5000)
    {
        int index = _backlog.FindIndex(m => match(m));
        if (index >= 0)
        {
            XMessage found = _backlog[index];
            _backlog.RemoveAt(index);
            return found;
        }
        using CancellationTokenSource cts = new(timeoutMs);
        while (true)
        {
            XMessage m = await _incoming.Reader.ReadAsync(cts.Token);
            if (match(m))
            {
                return m;
            }
            _backlog.Add(m);
        }
    }

    public Task<XMessage> NextEventAsync(byte code, int timeoutMs = 5000) =>
        NextAsync(m => !m.IsReply && !m.IsError && m.EventCode == code, timeoutMs);

    /// <summary>等一个往返(GetInputFocus),保证之前的请求都执行完了。</summary>
    public Task SyncAsync() => RequestAsync(43, 0);

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                byte[] head = new byte[32];
                await _stream.ReadExactlyAsync(head);
                // 回复与 GenericEvent(35)在 32 字节之后还有「长度 × 4」字节。
                if (head[0] == 1 || (head[0] & 0x7F) == 35)
                {
                    uint extra = BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4)) : BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
                    byte[] full = new byte[32 + (extra * 4)];
                    head.CopyTo(full, 0);
                    await _stream.ReadExactlyAsync(full.AsMemory(32));
                    head = full;
                }
                await _incoming.Writer.WriteAsync(new XMessage(head, BigEndian));
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException or OperationCanceledException)
        {
            _incoming.Writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        try
        {
            await _serverTask.WaitAsync(TimeSpan.FromSeconds(2));
            await _readTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // 收尾阶段的异常不关心。
        }
    }
}
