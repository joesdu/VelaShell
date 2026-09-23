#:project ../../../src/VelaShell.XServer/VelaShell.XServer.csproj
#:property TreatWarningsAsErrors=false

// VelaShell.XServer 的吞吐基准:服务端与一个手写的 X 客户端在同一进程里,经内存管道通信,
// 按典型负载各发一批请求,最后一个往返(GetInputFocus)确认全部执行完,记总耗时。
//
//   dotnet run -c Release -p:SignAssembly=false scripts/xserver/bench/bench.cs   (仓库里没有签名密钥,Release 需关掉签名)
//
// 场景:核心填充、32 位 PutImage、Xft 式字形合成(a8 字形 + 纯色源 + Over)、ARGB 图像 Over 合成、
// 指针移动注入(窗口选了 PointerMotion)、请求往返延迟。数字只用来比较前后改动,不同机器之间不可比。

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using VelaShell.XServer.Server;

await using X11Server server = new();
Pipe toServer = new(), toClient = new();
Stream serverSide = new Duplex(toServer.Reader, toClient.Writer);
Stream clientSide = new Duplex(toClient.Reader, toServer.Writer);
_ = server.ServeAsync(serverSide);

Client c = new(clientSide);
await c.HandshakeAsync();

// 800×600 的顶层窗口,选 PointerMotion,映射。
uint window = c.NewId();
c.Request(1, 24, b => b.U32(window).U32(c.Root).I16(0).I16(0).U16(800).U16(600).U16(0).U16(1).U32(0).U32(0x2 | 0x800).U32(0xFFFFFF).U32(0x40));
c.Request(8, 0, b => b.U32(window));
uint gc = c.NewId();
c.Request(55, 0, b => b.U32(gc).U32(window).U32(0x4).U32(0x336699));
await c.SyncAsync();

byte render = await c.QueryExtensionAsync("RENDER");
(uint argb, uint rgb, uint a8) = await c.FormatsAsync(render);
uint picture = c.NewId();
c.Request(render, 4, b => b.U32(picture).U32(window).U32(rgb).U32(0));
uint solid = c.NewId();
c.Request(render, 33, b => b.U32(solid).U16(0).U16(0).U16(0).U16(0xFFFF));
uint glyphs = c.NewId();
c.Request(render, 17, b => b.U32(glyphs).U32(a8));
// 一个 8×16 的 a8 字形:半透明到不透明的斜坡,行宽 8 字节(已 4 字节对齐)。
byte[] glyphBits = new byte[8 * 16];
for (int i = 0; i < glyphBits.Length; i++)
{
    glyphBits[i] = (byte)(i * 2);
}
c.Request(render, 20, b => b.U32(glyphs).U32(1).U32('g').U16(8).U16(16).I16(0).I16(12).I16(9).I16(0).Bytes(glyphBits));
uint argbPixmap = c.NewId();
c.Request(53, 32, b => b.U32(argbPixmap).U32(window).U16(100).U16(100));
uint argbPicture = c.NewId();
c.Request(render, 4, b => b.U32(argbPicture).U32(argbPixmap).U32(argb).U32(0));
c.Request(render, 26, b => b.U8(1).U8(0).U8(0).U8(0).U32(argbPicture).U16(0x8000).U16(0x4000).U16(0).U16(0x8000).I16(0).I16(0).U16(100).U16(100));
await c.SyncAsync();

byte[] image = new byte[200 * 100 * 4];
Random.Shared.NextBytes(image);

Console.WriteLine($"{"场景",-34}{"次数",8}{"耗时 ms",10}{"每秒",14}");
await RunAsync("PolyFillRectangle 50×50", 20_000, i =>
    c.Request(70, 0, b => b.U32(window).U32(gc).I16((short)(i % 700)).I16((short)(i % 500)).U16(50).U16(50)));
await RunAsync("PutImage 200×100 32 bpp", 2_000, i =>
    c.Request(72, 2, b => b.U32(window).U32(gc).U16(200).U16(100).I16((short)(i % 500)).I16((short)(i % 400)).U8(0).U8(24).U16(0).Bytes(image)));
await RunAsync("CompositeGlyphs8 ×10(Xft 文字)", 20_000, i =>
    c.Request(render, 23, b => b.U8(3).U8(0).U8(0).U8(0).U32(solid).U32(picture).U32(0).U32(glyphs).I16(0).I16(0)
        .U8(10).U8(0).U8(0).U8(0).I16((short)(i % 600)).I16((short)(20 + (i % 500))).Bytes("gggggggggg"u8.ToArray()).U8(0).U8(0)));
await RunAsync("Composite ARGB 100×100 Over", 5_000, i =>
    c.Request(render, 8, b => b.U8(3).U8(0).U8(0).U8(0).U32(argbPicture).U32(0).U32(picture)
        .I16(0).I16(0).I16(0).I16(0).I16((short)(i % 700)).I16((short)(i % 500)).U16(100).U16(100)));
await RunAsync("指针移动注入(选了 PointerMotion)", 50_000, i => server.PointerMotion(window, i % 800, (i / 800) % 600));

Stopwatch rt = Stopwatch.StartNew();
const int roundTrips = 5_000;
for (int i = 0; i < roundTrips; i++)
{
    await c.SyncAsync();
}
rt.Stop();
Console.WriteLine($"{"往返 GetInputFocus(串行)",-34}{roundTrips,8}{rt.Elapsed.TotalMilliseconds,10:F0}{roundTrips / rt.Elapsed.TotalSeconds,14:F0}");

async Task RunAsync(string name, int count, Action<int> send)
{
    await c.SyncAsync();
    Stopwatch sw = Stopwatch.StartNew();
    for (int i = 0; i < count; i++)
    {
        send(i);
        if ((i & 255) == 255)
        {
            await c.FlushAsync();
        }
    }
    await c.SyncAsync();
    sw.Stop();
    Console.WriteLine($"{name,-34}{count,8}{sw.Elapsed.TotalMilliseconds,10:F0}{count / sw.Elapsed.TotalSeconds,14:F0}");
}

/// <summary>一个最小的 X 客户端:小端、自己拼请求、只认得回复的序号。</summary>
sealed class Client(Stream stream)
{
    private readonly Stream _stream = stream;
    private readonly MemoryStream _pending = new();
    private readonly Dictionary<ushort, TaskCompletionSource<byte[]>> _waiting = [];
    private ushort _sequence;
    private uint _nextId = 1;

    public uint Root { get; private set; }

    private uint _base;

    public uint NewId() => _base | _nextId++;

    public async Task HandshakeAsync()
    {
        await _stream.WriteAsync(new byte[] { (byte)'l', 0, 11, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        byte[] head = new byte[8];
        await _stream.ReadExactlyAsync(head);
        byte[] rest = new byte[BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(6)) * 4];
        await _stream.ReadExactlyAsync(rest);
        _base = BinaryPrimitives.ReadUInt32LittleEndian(rest.AsSpan(4));
        int vendor = BinaryPrimitives.ReadUInt16LittleEndian(rest.AsSpan(16));
        int formats = rest[21];
        int screen = 32 + ((vendor + 3) & ~3) + (formats * 8);
        Root = BinaryPrimitives.ReadUInt32LittleEndian(rest.AsSpan(screen));
        _ = ReadLoopAsync();
    }

    public sealed class Body
    {
        private readonly List<byte> _bytes = [];
        public Body U8(byte v) { _bytes.Add(v); return this; }
        public Body U16(ushort v) { _bytes.Add((byte)v); _bytes.Add((byte)(v >> 8)); return this; }
        public Body I16(short v) => U16((ushort)v);
        public Body U32(uint v) { U16((ushort)v); return U16((ushort)(v >> 16)); }
        public Body Bytes(byte[] data) { _bytes.AddRange(data); return this; }
        public byte[] ToArray()
        {
            while (_bytes.Count % 4 != 0)
            {
                _bytes.Add(0);
            }
            return [.. _bytes];
        }
    }

    public ushort Request(byte opcode, byte data, Action<Body>? body = null)
    {
        Body b = new();
        body?.Invoke(b);
        byte[] payload = b.ToArray();
        int units = 1 + (payload.Length / 4);
        Span<byte> head = stackalloc byte[4];
        head[0] = opcode;
        head[1] = data;
        BinaryPrimitives.WriteUInt16LittleEndian(head[2..], (ushort)units);
        _pending.Write(head);
        _pending.Write(payload);
        return ++_sequence;
    }

    public async Task FlushAsync()
    {
        if (_pending.Length > 0)
        {
            await _stream.WriteAsync(_pending.GetBuffer().AsMemory(0, (int)_pending.Length));
            await _stream.FlushAsync();
            _pending.SetLength(0);
        }
    }

    public async Task<byte[]> RequestAsync(byte opcode, byte data, Action<Body>? body = null)
    {
        ushort seq = Request(opcode, data, body);
        TaskCompletionSource<byte[]> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waiting)
        {
            _waiting[seq] = tcs;
        }
        await FlushAsync();
        return await tcs.Task;
    }

    public Task SyncAsync() => RequestAsync(43, 0);

    public async Task<byte> QueryExtensionAsync(string name)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(name);
        byte[] reply = await RequestAsync(98, 0, b => b.U16((ushort)bytes.Length).U16(0).Bytes(bytes));
        return reply[9];
    }

    public async Task<(uint Argb, uint Rgb, uint A8)> FormatsAsync(byte render)
    {
        byte[] f = await RequestAsync(render, 1);
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(8));
        uint argb = 0, rgb = 0, a8 = 0;
        for (int i = 0; i < count; i++)
        {
            int o = 32 + (i * 28);
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(o));
            byte depth = f[o + 5];
            ushort alpha = BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(o + 22)), red = BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(o + 10));
            if (depth == 32 && alpha == 0xFF) argb = id;
            if (depth == 24 && red == 0xFF) rgb = id;
            if (depth == 8 && alpha == 0xFF && red == 0) a8 = id;
        }
        return (argb, rgb, a8);
    }

    private async Task ReadLoopAsync()
    {
        byte[] head = new byte[32];
        try
        {
            while (true)
            {
                await _stream.ReadExactlyAsync(head);
                byte[] message = head;
                if (head[0] == 1 || (head[0] & 0x7F) == 35)
                {
                    uint extra = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
                    message = new byte[32 + (extra * 4)];
                    head.CopyTo(message, 0);
                    await _stream.ReadExactlyAsync(message.AsMemory(32));
                }
                if (head[0] == 0)
                {
                    Console.WriteLine($"  !! 错误 {head[1]}(操作码 {head[10]}.{BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(8))})");
                }
                if (head[0] is 0 or 1)
                {
                    ushort seq = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(2));
                    TaskCompletionSource<byte[]>? tcs;
                    lock (_waiting)
                    {
                        _waiting.Remove(seq, out tcs);
                    }
                    tcs?.TrySetResult(message.ToArray());
                }
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException)
        {
        }
    }
}

sealed class Duplex(PipeReader reader, PipeWriter writer) : Stream
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
}
