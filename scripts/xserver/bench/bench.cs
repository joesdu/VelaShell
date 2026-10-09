#:project ../../../src/VelaShell.XServer/VelaShell.XServer.csproj
#:property TreatWarningsAsErrors=false

// VelaShell.XServer 的吞吐基准:服务端与一个手写的 X 客户端在同一进程里,经内存管道通信,
// 按典型负载各发一批请求(先预热一遍),最后一个往返(GetInputFocus)确认全部执行完,记总耗时。
//
//   dotnet run -c Release -p:SignAssembly=false scripts/xserver/bench/bench.cs   (仓库里没有签名密钥,Release 需关掉签名)
//
// 场景:核心填充、32 位 PutImage(小块与整窗)、Xft 式字形合成(a8 字形 + 纯色源 + Over)、ARGB 图像 Over 合成、
// RENDER 通用路径(线性渐变源、带缩放变换的双线性源、ARGB 源 + a8 遮罩)、GLX 单缓冲的小三角形(每个 Render 请求一个)、
// RENDER 多矩形填充、PolyArc(各自独立的宽弧、首尾相接的宽弧与细弧)、指针移动注入(窗口选了 PointerMotion)、请求往返延迟;
// 最后量整窗 PutImage 满载时宿主读像素(另一条线程每 16 毫秒读一次整窗)要等多久 —— 宿主 UI 线程卡不卡看的就是它;
// 再量四个窗口一起忙时宿主每帧逐个窗口读一遍(每个窗口拿一次像素锁)的总耗时。
// 数字只用来比较前后改动,不同机器之间不可比。

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using VelaShell.XServer;

BenchHost host = new();
await using X11Server server = new(host: host);
Pipe toServer = new(), toClient = new();
Stream serverSide = new Duplex(toServer.Reader, toClient.Writer);
Stream clientSide = new Duplex(toClient.Reader, toServer.Writer);
_ = server.ServeAsync(serverSide, isLocal: true);

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
// 线性渐变(三个色标,中间半透明):cairo / Qt 画按钮底色、标题栏。
uint gradient = c.NewId();
c.Request(render, 34, b => b.U32(gradient).U32(0).U32(0).U32(100 << 16).U32(100 << 16).U32(3)
    .U32(0).U32(0x8000).U32(0x10000)
    .U16(0xFFFF).U16(0).U16(0).U16(0xFFFF).U16(0).U16(0xFFFF).U16(0).U16(0x8000).U16(0).U16(0).U16(0xFFFF).U16(0xFFFF));
// 同一张 ARGB 像素图的另一个 picture:放大两倍、双线性(缩放预览、HiDPI 下的图标)。
uint scaledPicture = c.NewId();
c.Request(render, 4, b => b.U32(scaledPicture).U32(argbPixmap).U32(argb).U32(0));
c.Request(render, 28, b => b.U32(scaledPicture).U32(0x8000).U32(0).U32(0).U32(0).U32(0x8000).U32(0).U32(0).U32(0).U32(0x10000));
c.Request(render, 30, b => b.U32(scaledPicture).U16(8).U16(0).Bytes("bilinear"u8.ToArray()));
// 100×100 的 a8 遮罩:半透明的斜坡(圆角、阴影的形状遮罩)。
uint maskPixmap = c.NewId();
c.Request(53, 8, b => b.U32(maskPixmap).U32(window).U16(100).U16(100));
uint maskPicture = c.NewId();
c.Request(render, 4, b => b.U32(maskPicture).U32(maskPixmap).U32(a8).U32(0));
uint maskGc = c.NewId();
c.Request(55, 0, b => b.U32(maskGc).U32(maskPixmap).U32(0));
byte[] ramp = new byte[100 * 100];
for (int i = 0; i < ramp.Length; i++)
{
    ramp[i] = (byte)(i % 100 * 255 / 99);
}
c.Request(72, 2, b => b.U32(maskPixmap).U32(maskGc).U16(100).U16(100).I16(0).I16(0).U8(0).U8(8).U16(0).Bytes(ramp));
await c.SyncAsync();

byte[] image = new byte[200 * 100 * 4];
Random.Shared.NextBytes(image);
byte[] frame = new byte[800 * 600 * 4];
Random.Shared.NextBytes(frame);
byte[] frameRequest = Client.Encode(72, 2, b => b.U32(window).U32(gc).U16(800).U16(600).I16(0).I16(0).U8(0).U8(24).U16(0).Bytes(frame), big: true);
byte bigRequests = await c.QueryExtensionAsync("BIG-REQUESTS");
await c.RequestAsync(bigRequests, 0);
// GLX 间接渲染、单缓冲(FBConfig 0x102)绑在整个窗口上:每个 Render 请求画一个约 20 像素的小三角形(单缓冲的 GL 程序逐条画、画完就该看得见)。
byte glx = await c.QueryExtensionAsync("GLX");
uint glContext = c.NewId();
c.Request(glx, 24, b => b.U32(glContext).U32(0x102).U32(0).U32(0x8014).U32(0).U8(0).U8(0).U16(0));   // CreateNewContext
byte[] made = await c.RequestAsync(glx, 5, b => b.U32(window).U32(glContext).U32(0));                 // MakeCurrent
uint glTag = BinaryPrimitives.ReadUInt32LittleEndian(made.AsSpan(8));
byte[][] triangles = new byte[64][];
for (int k = 0; k < triangles.Length; k++)
{
    float x = -0.9f + (k % 8 * 0.22f), y = -0.9f + (k / 8 * 0.22f);
    triangles[k] = Client.Encode(glx, 1, b => GlCommands(b.U32(glTag),
        (8, [k % 3 == 0 ? 1f : 0f, k % 3 == 1 ? 1f : 0f, k % 3 == 2 ? 1f : 0f]),                   // Color3fv
        (4, [BitConverter.UInt32BitsToSingle(4)]),                                                  // Begin(TRIANGLES)
        (66, [x, y]), (66, [x + 0.05f, y]), (66, [x, y + 0.066f]),                                  // Vertex2fv
        (23, [])));                                                                                 // End
}
// FillRectangles:50 个 10×10 的矩形(cairo / Qt 清背景一个请求里常有几十个)。
Action<Client.Body> fillRects = b =>
{
    b.U8(1).U8(0).U8(0).U8(0).U32(picture).U16(0x8000).U16(0x4000).U16(0x2000).U16(0xFFFF);
    for (int k = 0; k < 50; k++)
    {
        b.I16((short)(k * 15 % 780)).I16((short)(k * 11 % 580)).U16(10).U16(10);
    }
};
// PolyArc:一个请求四条弧,事先编好 16 个位置。「互不相接」是四个各自独立的 60×60 整圆(每条弧单独成一串);
// 「四段拼整圆」是同一个 120×120 外接框的四段 90° 弧,首尾相接成一串闭合的路径。宽弧用 lw = 3 的 GC。
uint wideGc = c.NewId();
c.Request(55, 0, b => b.U32(wideGc).U32(window).U32(0x4 | 0x10).U32(0x993366).U32(3));
byte[][] separateArcs = new byte[16][], wideQuarters = new byte[16][], thinQuarters = new byte[16][];
for (int k = 0; k < 16; k++)
{
    short x = (short)(k % 4 * 170), y = (short)(k / 4 * 130);
    separateArcs[k] = Client.Encode(68, 0, b =>
    {
        b.U32(window).U32(wideGc);
        for (int a = 0; a < 4; a++)
        {
            b.I16((short)(x + (a * 30))).I16((short)(y + (a * 10))).U16(60).U16(60).I16(0).I16(360 * 64);
        }
    });
    foreach ((byte[][] target, uint arcGc) in new[] { (wideQuarters, wideGc), (thinQuarters, gc) })
    {
        target[k] = Client.Encode(68, 0, b =>
        {
            b.U32(window).U32(arcGc);
            for (int a = 0; a < 4; a++)
            {
                b.I16(x).I16(y).U16(120).U16(120).I16((short)(a * 90 * 64)).I16(90 * 64);
            }
        });
    }
}

Console.WriteLine($"{"场景",-34}{"次数",8}{"耗时 ms",10}{"每秒",14}{"CPU ms",10}{"分配 B/次",12}");
await RunAsync("PolyFillRectangle 50×50", 20_000, i =>
    c.Request(70, 0, b => b.U32(window).U32(gc).I16((short)(i % 700)).I16((short)(i % 500)).U16(50).U16(50)));
await RunAsync("PutImage 200×100 32 bpp", 2_000, i =>
    c.Request(72, 2, b => b.U32(window).U32(gc).U16(200).U16(100).I16((short)(i % 500)).I16((short)(i % 400)).U8(0).U8(24).U16(0).Bytes(image)));
await RunAsync("CompositeGlyphs8 ×1", 20_000, i =>
    c.Request(render, 23, b => b.U8(3).U8(0).U8(0).U8(0).U32(solid).U32(picture).U32(0).U32(glyphs).I16(0).I16(0)
        .U8(1).U8(0).U8(0).U8(0).I16((short)(i % 600)).I16((short)(20 + (i % 500))).Bytes("g"u8.ToArray()).U8(0).U8(0).U8(0)));
await RunAsync("CompositeGlyphs8 ×10(Xft 文字)", 20_000, i =>
    c.Request(render, 23, b => b.U8(3).U8(0).U8(0).U8(0).U32(solid).U32(picture).U32(0).U32(glyphs).I16(0).I16(0)
        .U8(10).U8(0).U8(0).U8(0).I16((short)(i % 600)).I16((short)(20 + (i % 500))).Bytes("gggggggggg"u8.ToArray()).U8(0).U8(0)));
await RunAsync("Composite ARGB 100×100 Over", 5_000, i =>
    c.Request(render, 8, b => b.U8(3).U8(0).U8(0).U8(0).U32(argbPicture).U32(0).U32(picture)
        .I16(0).I16(0).I16(0).I16(0).I16((short)(i % 700)).I16((short)(i % 500)).U16(100).U16(100)));
await RunAsync("Composite 线性渐变 100×100 Over", 5_000, i =>
    c.Request(render, 8, b => b.U8(3).U8(0).U8(0).U8(0).U32(gradient).U32(0).U32(picture)
        .I16(0).I16(0).I16(0).I16(0).I16((short)(i % 700)).I16((short)(i % 500)).U16(100).U16(100)));
await RunAsync("Composite 放大 2 倍双线性 100×100 Over", 5_000, i =>
    c.Request(render, 8, b => b.U8(3).U8(0).U8(0).U8(0).U32(scaledPicture).U32(0).U32(picture)
        .I16(0).I16(0).I16(0).I16(0).I16((short)(i % 700)).I16((short)(i % 500)).U16(100).U16(100)));
await RunAsync("Composite ARGB + a8 遮罩 100×100 Over", 5_000, i =>
    c.Request(render, 8, b => b.U8(3).U8(0).U8(0).U8(0).U32(argbPicture).U32(maskPicture).U32(picture)
        .I16(0).I16(0).I16(0).I16(0).I16((short)(i % 700)).I16((short)(i % 500)).U16(100).U16(100)));
await RunAsync("GLX 单缓冲 Render 小三角形", 5_000, i => c.Raw(triangles[i % triangles.Length]));
await RunAsync("PutImage 800×600 整窗(BIG-REQUESTS)", 1_000, _ => c.Raw(frameRequest));
await RunAsync("RenderFillRectangles ×50", 5_000, _ => c.Request(render, 26, fillRects));
await RunAsync("PolyArc 宽弧 ×4 互不相接", 5_000, i => c.Raw(separateArcs[i % separateArcs.Length]));
await RunAsync("PolyArc 宽弧 四段 90° 拼整圆", 5_000, i => c.Raw(wideQuarters[i % wideQuarters.Length]));
await RunAsync("PolyArc 细弧 四段 90° 拼整圆", 5_000, i => c.Raw(thinQuarters[i % thinQuarters.Length]));
XTopLevelWindow mapped = host.Mapped ?? throw new InvalidOperationException("窗口没映射");
await RunAsync("指针移动注入(选了 PointerMotion)", 50_000, i => server.InjectPointerMotion(mapped, i % 800, (i / 800) % 600));

// 指针在两个并排的子窗口之间来回:每次都是 Nonlinear 的 crossing(公共祖先、XI2 的 Enter / Leave);
// 按键注入:根窗口上登记着 200 个别的键的被动抓取(窗口管理器、快捷键程序都会这样登记),每次按下都要查一遍。
byte xi = await c.QueryExtensionAsync("XInputExtension");
await c.RequestAsync(xi, 47, b => b.U16(2).U16(2));   // XIQueryVersion
uint left = c.NewId(), right = c.NewId();
foreach ((uint child, short x) in new[] { (left, (short)0), (right, (short)400) })
{
    c.Request(1, 24, b => b.U32(child).U32(window).I16(x).I16(0).U16(400).U16(600).U16(0).U16(1).U32(0).U32(0x800).U32(0x10 | 0x20));
    c.Request(xi, 46, b => b.U32(child).U16(1).U16(0).U16(1).U16(1).U8(0x80).U8(0x01).U8(0).U8(0));   // XI2 Enter | Leave
    c.Request(8, 0, b => b.U32(child));
}
for (int k = 0; k < 200; k++)
{
    c.Request(33, 0, b => b.U32(c.Root).U16((ushort)(k % 8 == 0 ? 0x8000 : k % 8)).U8((byte)(100 + (k / 8))).U8(1).U8(1).U8(0).U16(0));   // GrabKey
}
await c.SyncAsync();
await RunAsync("指针在两个子窗口之间来回(crossing)", 20_000, i => server.InjectPointerMotion(mapped, i % 2 == 0 ? 100 : 500, 300));
await RunAsync("按键注入(根上 200 个被动抓取)", 50_000, i => server.InjectKey(38, pressed: i % 2 == 0));

long roundTripStart = GC.GetTotalAllocatedBytes(precise: true);
Stopwatch rt = Stopwatch.StartNew();
const int roundTrips = 5_000;
for (int i = 0; i < roundTrips; i++)
{
    await c.SyncAsync();
}
rt.Stop();
long roundTripAllocated = GC.GetTotalAllocatedBytes(precise: true) - roundTripStart;
Console.WriteLine($"{"往返 GetInputFocus(串行)",-34}{roundTrips,8}{rt.Elapsed.TotalMilliseconds,10:F0}{roundTrips / rt.Elapsed.TotalSeconds,14:F0}{"",10}{roundTripAllocated / roundTrips,12}");

// 整窗 PutImage 满载时,宿主每 16 毫秒读一次整窗像素:每次要等多久才拿到锁并读完。
XTopLevelWindow handle = mapped;
using CancellationTokenSource stop = new();
List<double> waits = [];
Thread reader = new(() =>
{
    uint[] copy = new uint[800 * 600];
    while (!stop.IsCancellationRequested)
    {
        long t0 = Stopwatch.GetTimestamp();
        handle.CopyPixels(copy);
        waits.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        Thread.Sleep(16);
    }
});
reader.Start();
await SendBatchAsync(2_000, _ => c.Raw(frameRequest));
stop.Cancel();
reader.Join();
waits.Sort();
Console.WriteLine($"宿主读整窗像素(整窗 PutImage 满载):{waits.Count} 次,中位 {waits[waits.Count / 2]:F2} ms,"
    + $"p99 {waits[(int)(waits.Count * 0.99)]:F2} ms,最长 {waits[^1]:F2} ms");

// 四个忙窗口(xs_plan API-P1):宿主每帧逐个窗口拿一次像素锁读整窗(XNativeWindow 的做法:TryReadPixels,最多等 8 毫秒,
// 拿不到就跳过这一帧)。先在空闲时量一遍(只有拷贝的开销),再在四个窗口轮流整窗 PutImage 时量:每帧的总耗时、读不到的次数。
// 满载时每帧比空闲时多出来的,就是四次等锁 —— 「一次锁内读多个窗口」的 API 最多能省下其中三次。
List<XTopLevelWindow> busy = [mapped];
List<byte[]> busyFrames = [frameRequest];
for (int k = 1; k < 4; k++)
{
    uint extra = c.NewId();
    c.Request(1, 24, b => b.U32(extra).U32(c.Root).I16((short)(40 * k)).I16((short)(40 * k)).U16(800).U16(600).U16(0).U16(1).U32(0).U32(0x2).U32(0xFFFFFF));
    c.Request(8, 0, b => b.U32(extra));
    await c.SyncAsync();
    busy.Add(host.Mapped!);
    busyFrames.Add(Client.Encode(72, 2, b => b.U32(extra).U32(gc).U16(800).U16(600).I16(0).I16(0).U8(0).U8(24).U16(0).Bytes(frame), big: true));
}
(List<double> Frames, int Busy) ReadFrames(Func<bool> keepGoing, int maxFrames)
{
    List<double> frames = [];
    int skipped = 0;
    uint[] copy = new uint[800 * 600];
    XPixelReader reader = (pixels, _, _) => pixels.CopyTo(copy);
    while (keepGoing() && frames.Count < maxFrames)
    {
        long t0 = Stopwatch.GetTimestamp();
        foreach (XTopLevelWindow w in busy)
        {
            if (w.TryReadPixels(reader, TimeSpan.FromMilliseconds(8)) == XPixelReadResult.Busy)
            {
                skipped++;
            }
        }
        frames.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        Thread.Sleep(16);
    }
    frames.Sort();
    return (frames, skipped);
}
string Describe((List<double> Frames, int Busy) r) =>
    $"{r.Frames.Count} 帧,每帧中位 {r.Frames[r.Frames.Count / 2]:F2} ms,p99 {r.Frames[(int)(r.Frames.Count * 0.99)]:F2} ms,"
    + $"最长 {r.Frames[^1]:F2} ms,读不到 {r.Busy} 次";
Console.WriteLine($"宿主每帧读四个窗口(空闲):{Describe(ReadFrames(() => true, 200))}");
using CancellationTokenSource stopBusy = new();
(List<double> Frames, int Busy) loaded = ([], 0);
Thread busyReader = new(() => loaded = ReadFrames(() => !stopBusy.IsCancellationRequested, int.MaxValue));
busyReader.Start();
await SendBatchAsync(2_000, i => c.Raw(busyFrames[i % busyFrames.Count]));
stopBusy.Cancel();
busyReader.Join();
Console.WriteLine($"宿主每帧读四个窗口(四个窗口轮流整窗 PutImage 满载):{Describe(loaded)}");

// GLX 渲染命令:每条 2 字节长度(含 4 字节头)、2 字节操作码,参数都是 4 字节。
static Client.Body GlCommands(Client.Body b, params (ushort Opcode, float[] Values)[] commands)
{
    foreach ((ushort opcode, float[] values) in commands)
    {
        b.U16((ushort)(4 + (values.Length * 4))).U16(opcode);
        foreach (float v in values)
        {
            b.U32(BitConverter.SingleToUInt32Bits(v));
        }
    }
    return b;
}

async Task RunAsync(string name, int count, Action<int> send)
{
    // 先不计时跑一遍:让分层 JIT 把热路径升到优化代码,量的是长期运行的服务端的稳态,不是冷启动。
    await SendBatchAsync(count, send);
    // CPU 时间(整个进程,含测试客户端;客户端那份前后一样,差出来的是服务端):吞吐被别的环节卡住时,省下的功夫在这一列看得出来。
    // 托管堆分配(整个进程,含测试客户端拼请求的那份;同一场景前后比较时客户端那份不变)。
    TimeSpan cpu = Process.GetCurrentProcess().TotalProcessorTime;
    long allocated = GC.GetTotalAllocatedBytes(precise: true);
    Stopwatch sw = Stopwatch.StartNew();
    await SendBatchAsync(count, send);
    sw.Stop();
    double cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds;
    long perRequest = (GC.GetTotalAllocatedBytes(precise: true) - allocated) / count;
    Console.WriteLine($"{name,-34}{count,8}{sw.Elapsed.TotalMilliseconds,10:F0}{count / sw.Elapsed.TotalSeconds,14:F0}{cpuMs,10:F0}{perRequest,12}");
}

async Task SendBatchAsync(int count, Action<int> send)
{
    await c.SyncAsync();
    for (int i = 0; i < count; i++)
    {
        send(i);
        if ((i & 255) == 255 || c.PendingBytes > (4 << 20))
        {
            await c.FlushAsync();
        }
    }
    await c.SyncAsync();
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

    /// <summary>攒着还没写出去的字节数(整窗大请求攒几条就该写了)。</summary>
    public long PendingBytes => _pending.Length;

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

    public ushort Request(byte opcode, byte data, Action<Body>? body = null, bool big = false)
    {
        _pending.Write(Encode(opcode, data, body, big));
        return ++_sequence;
    }

    /// <summary>发一条事先编好的请求(大请求每次现拼的话,量的就是这个测试客户端而不是服务端)。</summary>
    public ushort Raw(byte[] request)
    {
        _pending.Write(request);
        return ++_sequence;
    }

    public static byte[] Encode(byte opcode, byte data, Action<Body>? body = null, bool big = false)
    {
        MemoryStream request = new();
        Body b = new();
        body?.Invoke(b);
        byte[] payload = b.ToArray();
        Span<byte> head = stackalloc byte[8];
        head[0] = opcode;
        head[1] = data;
        if (big)
        {
            // BIG-REQUESTS:长度字段写 0,后跟 4 字节的真长度(含这 8 字节头)。
            BinaryPrimitives.WriteUInt16LittleEndian(head[2..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(head[4..], (uint)(2 + (payload.Length / 4)));
            request.Write(head);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(head[2..], (ushort)(1 + (payload.Length / 4)));
            request.Write(head[..4]);
        }
        request.Write(payload);
        return request.ToArray();
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

/// <summary>只记下映射出来的那个顶层窗口(宿主读像素的场景要用它)。</summary>
sealed class BenchHost : IX11ServerHost
{
    public XTopLevelWindow? Mapped { get; private set; }
    public void TopLevelMapped(XTopLevelWindow window) => Mapped = window;
    public void TopLevelUnmapped(XTopLevelWindow window) { }
    public void TopLevelChanged(XTopLevelWindow window, XTopLevelChanges changes) { }
    public void TopLevelDamaged(XTopLevelWindow window, IReadOnlyList<XRect> damage) { }
    public void CursorChanged(XTopLevelWindow? window, XCursor cursor) { }
    public void BellRequested(int volume) { }
    public void ClipboardChanged(string text) { }
}