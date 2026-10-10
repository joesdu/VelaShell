using System.Collections.Concurrent;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>
/// 一条请求的代价有上限(工作量预算),宿主读像素不陪着一条慢请求等:服务端与宿主界面在同一个进程里,
/// 执行线程执行一条请求期间一直持着像素锁。
/// </summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class WorkBudgetTests
{
    private const byte BadAlloc = 11;

    private static async Task<(uint Pixmap, uint Gc)> PixmapWithGcAsync(XTestClient c, ushort size, uint foreground)
    {
        uint pixmap = c.NewId();
        await c.SendAsync(53, 24, b => b.U32(pixmap).U32(c.RootWindow).U16(size).U16(size));
        uint gc = c.NewId();
        await c.SendAsync(55, 0, b => b.U32(gc).U32(pixmap).U32(0x4).U32(foreground));   // GCForeground
        return (pixmap, gc);
    }

    private static async Task<uint> PixelAsync(XTestClient c, uint drawable, short x, short y)
    {
        XMessage image = await c.RequestAsync(73, 2, b => b.U32(drawable).I16(x).I16(y).U16(1).U16(1).U32(0xFFFFFFFF));
        Assert.IsTrue(image.IsReply, "GetImage");
        return image.U32(32);
    }

    [TestMethod]
    public async Task 超出工作量预算的绘图请求回BadAlloc_之后的请求照常执行()
    {
        await using X11Server server = new() { RequestWorkBudget = 10_000 };
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint pixmap, uint gc) = await PixmapWithGcAsync(c, 200, 0xFF0000);

        // 200 × 200 = 4 万个像素,超出 1 万的预算。
        XMessage big = await c.RequestAsync(70, 0, b => b.U32(pixmap).U32(gc).I16(0).I16(0).U16(200).U16(200));
        Assert.IsTrue(big.IsError, "PolyFillRectangle 应当回错误");
        Assert.AreEqual(BadAlloc, big.Detail);

        // 下一条请求有自己的一份预算。
        await c.SendAsync(70, 0, b => b.U32(pixmap).U32(gc).I16(10).I16(10).U16(20).U16(20));
        Assert.AreEqual(0xFF0000u, await PixelAsync(c, pixmap, 15, 15));
    }

    [TestMethod]
    public async Task 超出工作量预算的RENDER合成回BadAlloc()
    {
        await using X11Server server = new() { RequestWorkBudget = 10_000 };
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte[] name = "RENDER"u8.ToArray();
        XMessage ext = await c.RequestAsync(98, 0, b => b.U16((ushort)name.Length).U16(0).Bytes(name).Pad());
        byte render = ext.Bytes[9];
        (uint pixmap, _) = await PixmapWithGcAsync(c, 200, 0);

        // 深度 24 的 picture 格式。
        XMessage formats = await c.RequestAsync(render, 1);   // QueryPictFormats
        uint format = 0;
        int count = (int)formats.U32(8);
        for (int i = 0; i < count; i++)
        {
            int at = 32 + (i * 28);
            if (formats.Bytes[at + 5] == 24)
            {
                format = formats.U32(at);
                break;
            }
        }
        Assert.AreNotEqual(0u, format, "应当有深度 24 的格式");
        uint picture = c.NewId();
        await c.SendAsync(render, 4, b => b.U32(picture).U32(pixmap).U32(format).U32(0));   // CreatePicture
        uint solid = c.NewId();
        await c.SendAsync(render, 33, b => b.U32(solid).U16(0xFFFF).U16(0).U16(0).U16(0xFFFF));   // CreateSolidFill

        XMessage composite = await c.RequestAsync(render, 8, b => b.U8(3).Pad().U32(solid).U32(0).U32(picture)
            .I16(0).I16(0).I16(0).I16(0).I16(0).I16(0).U16(200).U16(200));
        Assert.IsTrue(composite.IsError, "Composite 应当回错误");
        Assert.AreEqual(BadAlloc, composite.Detail);
    }

    [TestMethod]
    public async Task 几万条落在窗口外的长线与高矩形_按看得见的部分花工作量()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint pixmap, uint gc) = await PixmapWithGcAsync(c, 10, 0x00FF00);
        const int count = 16000;

        // PolySegment:每条 65535 长,几乎全在 10 × 10 的像素图之外。原先每条逐像素走完,一个请求就是几十秒。
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await c.SendAsync(66, 0, b =>
        {
            b.U32(pixmap).U32(gc);
            for (int i = 0; i < count; i++)
            {
                b.I16(-32768).I16((short)(i % 20)).I16(32767).I16((short)(i % 20));
            }
        });
        // PolyFillRectangle:(3, −32768, 1, 65535) 这样的高矩形,原先逐行走六万多行。
        await c.SendAsync(70, 0, b =>
        {
            b.U32(pixmap).U32(gc);
            for (int i = 0; i < count; i++)
            {
                b.I16(3).I16(-32768).U16(1).U16(65535);
            }
        });
        Assert.AreEqual(0x00FF00u, await PixelAsync(c, pixmap, 3, 9));
        Assert.IsLessThan(10_000, watch.ElapsedMilliseconds, "两条请求都应当很快执行完");
    }

    [TestMethod]
    public async Task 线宽65535的圆帽折线_圆的顶点数封顶_很快画完()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint pixmap = c.NewId();
        await c.SendAsync(53, 24, b => b.U32(pixmap).U32(c.RootWindow).U16(10).U16(10));
        uint gc = c.NewId();
        // Foreground(bit 2)、LineWidth(bit 4)、CapStyle(bit 6,Round = 2)。
        await c.SendAsync(55, 0, b => b.U32(gc).U32(pixmap).U32(0x4 | 0x10 | 0x40).U32(0x0000FF).U32(65535).U32(2));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        // 1000 个点的折线:每个接头一个半径 32767 的圆。原先每个圆按半径 × 4 取 13 万个顶点,一条请求要分配十几 GB。
        XMessage? error = null;
        ushort seq = await c.SendAsync(65, 0, b =>
        {
            b.U32(pixmap).U32(gc);
            for (int i = 0; i < 1000; i++)
            {
                b.I16((short)(i % 2 == 0 ? 0 : 9)).I16((short)(i % 3));
            }
        });
        await c.SyncAsync();
        try
        {
            error = await c.NextAsync(m => m.IsError && m.Sequence == seq, timeoutMs: 100);
        }
        catch (OperationCanceledException)
        {
        }
        Assert.IsNull(error, "不应当耗尽工作量预算");
        Assert.AreEqual(0x0000FFu, await PixelAsync(c, pixmap, 5, 5));
        Assert.IsLessThan(10_000, watch.ElapsedMilliseconds);
    }

    private static async Task<XTopLevelWindow> MapTopAsync(XTestClient c, RecordingHost host)
    {
        uint id = c.NewId();
        await c.SendAsync(1, 24, b => b.U32(id).U32(c.RootWindow).I16(0).I16(0).U16(60).U16(40).U16(0).U16(1).U32(0)
            .U32(0x2).U32(0));
        await c.SendAsync(8, 0, b => b.U32(id));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
        return host.Mapped[id];
    }

    [TestMethod]
    public async Task 执行线程被一项慢工作占着时_限时读像素返回Busy而不是一直等()
    {
        using RecordingHost host = new();
        await using X11Server server = new(null, host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        XTopLevelWindow window = await MapTopAsync(c, host);

        using ManualResetEventSlim holding = new();
        using ManualResetEventSlim release = new();
        server.Post(null, () =>
        {
            holding.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        });
        Assert.IsTrue(holding.Wait(TimeSpan.FromSeconds(5)), "执行线程应当已经拿着像素锁");

        bool called = false;
        Assert.AreEqual(XPixelReadResult.Busy, window.TryReadPixels((_, _, _) => called = true, TimeSpan.FromMilliseconds(30)));
        Assert.IsFalse(called);

        release.Set();
        Assert.AreEqual(XPixelReadResult.Read, window.TryReadPixels((_, w, h) => called = w == 60 && h == 40, TimeSpan.FromSeconds(5)));
        Assert.IsTrue(called);
    }

    [TestMethod]
    public async Task 持锁很久的工作项记一行日志()
    {
        ConcurrentQueue<string> log = new();
        await using X11Server server = new(new X11ServerOptions { Log = log.Enqueue });
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        server.Post(null, () => Thread.Sleep(300));
        await c.SyncAsync();
        Assert.IsNotEmpty(await ServerLog.WaitForAsync(() => log, line => line.Contains("slow work item", StringComparison.Ordinal)), string.Join('\n', log));
    }
}
