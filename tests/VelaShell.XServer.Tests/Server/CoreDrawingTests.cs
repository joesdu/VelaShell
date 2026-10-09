using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>核心绘图请求经协议走一遍:PolyLine 的闭合、CopyPlane、GC 的参数校验、CopyArea 的 GraphicsExposure。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class CoreDrawingTests
{
    private static async Task<(uint Window, XTopLevelWindow Handle)> MapWindowAsync(XTestClient c, RecordingHost host, int width = 64, int height = 48)
    {
        uint id = c.NewId();
        await c.SendAsync(1, 24, b => b.U32(id).U32(c.RootWindow).I16(0).I16(0).U16((ushort)width).U16((ushort)height)
            .U16(0).U16(1).U32(0).U32(0x2).U32(0x000000));
        await c.SendAsync(8, 0, b => b.U32(id));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
        return (id, host.Mapped[id]);
    }

    /// <summary>CreateGC:按掩码位从低到高给值。</summary>
    private static async Task<uint> CreateGcAsync(XTestClient c, uint drawable, params (uint Bit, uint Value)[] values)
    {
        uint gc = c.NewId();
        uint mask = 0;
        foreach ((uint bit, _) in values)
        {
            mask |= bit;
        }
        await c.SendAsync(55, 0, b =>
        {
            b.U32(gc).U32(drawable).U32(mask);
            foreach ((_, uint value) in values.OrderBy(v => v.Bit))
            {
                b.U32(value);
            }
        });
        return gc;
    }

    private const uint GcFunction = 0x1, GcForeground = 0x4, GcBackground = 0x8, GcLineWidth = 0x10;

    private static Task<ushort> PolyLineAsync(XTestClient c, uint drawable, uint gc, params (short X, short Y)[] points) =>
        c.SendAsync(65, 0, b =>
        {
            b.U32(drawable).U32(gc);
            foreach ((short x, short y) in points)
            {
                b.I16(x).I16(y);
            }
        });

    private static uint Pixel(XTopLevelWindow handle, int x, int y)
    {
        (uint[] px, int w, _) = RecordingHost.Snapshot(handle);
        return px[(y * w) + x] & 0xFFFFFF;
    }

    [TestMethod]
    public async Task PolyLine首尾重合时当闭合路径_细线起点只画一次_宽线首尾按接头连起来()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint window, XTopLevelWindow handle) = await MapWindowAsync(c, host);

        // GXxor 的细线三角形:起点原先被第一段画一次、最后一段的终点又画一次,两次抵消。
        uint xor = await CreateGcAsync(c, window, (GcFunction, 6), (GcForeground, 0xFFFFFF));
        await PolyLineAsync(c, window, xor, (5, 5), (20, 5), (20, 20), (5, 5));
        await c.SyncAsync();
        Assert.AreEqual(0xFFFFFFu, Pixel(handle, 5, 5), "起点只画一次");
        Assert.AreEqual(0xFFFFFFu, Pixel(handle, 20, 5));

        // lw = 6 的方框(五个点,首尾重合):首尾两段在 (30,10) 按 Miter 连起来,角是方的;原先是两个 Butt 端帽,角上缺一块。
        uint wide = await CreateGcAsync(c, window, (GcForeground, 0x00FF00), (GcLineWidth, 6));
        await PolyLineAsync(c, window, wide, (30, 10), (50, 10), (50, 30), (30, 30), (30, 10));
        await c.SyncAsync();
        Assert.AreEqual(0x00FF00u, Pixel(handle, 27, 7), "起点 / 终点处的角");
        Assert.AreEqual(0x00FF00u, Pixel(handle, 52, 7), "中间的角");
    }

    [TestMethod]
    public async Task PolyArc首尾相接的弧在一条请求里连成路径_细弧接点只画一次_宽弧按接头连起来()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint window, XTopLevelWindow handle) = await MapWindowAsync(c, host);
        Task<ushort> PolyArcAsync(uint gc, params (short X, short Y, ushort W, ushort H, short A1, short A2)[] arcs) =>
            c.SendAsync(68, 0, b =>
            {
                b.U32(window).U32(gc);
                foreach ((short x, short y, ushort w, ushort h, short a1, short a2) in arcs)
                {
                    b.I16(x).I16(y).U16(w).U16(h).I16(a1).I16(a2);
                }
            });

        // GXxor 的细弧:同一个圆的两段四分之一弧在 (30,10) 相接,接点原先两条弧各画一次、互相抵消。
        uint xor = await CreateGcAsync(c, window, (GcFunction, 6), (GcForeground, 0xFFFFFF));
        await PolyArcAsync(xor, (10, 10, 40, 40, 0, 90 * 64), (10, 10, 40, 40, 90 * 64, 90 * 64));
        await c.SyncAsync();
        Assert.AreEqual(0xFFFFFFu, Pixel(handle, 30, 10), "接点只画一次");

        // lw = 8 的两条弧在 (20,10) 拐成直角:Miter 把缺口补成方角;原先两个 Butt 端帽,(17,13) 空着。
        uint wide = await CreateGcAsync(c, window, (GcForeground, 0x00FF00), (GcLineWidth, 8));
        await PolyArcAsync(wide, (0, 10, 40, 40, 0, 90 * 64), (-20, -10, 40, 40, 0, 90 * 64));
        await c.SyncAsync();
        Assert.AreEqual(0x00FF00u, Pixel(handle, 17, 13), "接头");
    }

    private static async Task<byte> ErrorOfAsync(XTestClient c, ushort sequence) =>
        (await c.NextAsync(m => m.IsError && m.Sequence == sequence)).Detail;

    [TestMethod]
    public async Task GC的枚举值越界回BadValue_出错的ChangeGC与SetDashes不改任何值()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint window, XTopLevelWindow handle) = await MapWindowAsync(c, host);
        uint gc = await CreateGcAsync(c, window, (GcForeground, 0xFF0000));

        // function 16 原先被 & 0xF 截成 GXclear;line-style / cap / join / fill-style / fill-rule / subwindow-mode / arc-mode 越界原样收下。
        foreach ((uint bit, uint value) in new (uint, uint)[] { (0x1, 16), (0x20, 3), (0x40, 4), (0x80, 3), (0x100, 4), (0x200, 2), (0x8000, 2), (0x10000, 2), (0x400000, 2) })
        {
            ushort seq = await c.SendAsync(56, 0, b => b.U32(gc).U32(bit).U32(value));   // ChangeGC
            Assert.AreEqual(2, await ErrorOfAsync(c, seq), $"掩码 0x{bit:x} = {value}:BadValue");
        }

        // 前景改成绿、同时给一个不存在的平铺像素图:BadPixmap,前景也不该变。
        ushort bad = await c.SendAsync(56, 0, b => b.U32(gc).U32(GcForeground | 0x400).U32(0x00FF00).U32(0x0BADBAD));
        Assert.AreEqual(4, await ErrorOfAsync(c, bad), "BadPixmap");
        await FillRectAsync(c, window, gc, 0, 0, 2, 2);
        await c.SyncAsync();
        Assert.AreEqual(0xFF0000u, Pixel(handle, 0, 0), "出错的 ChangeGC 不产生效果");

        // OnOffDash、默认的 [4, 4]:SetDashes(offset 2, [0]) 出错 —— dash-offset 也不能改(改了的话第 2 个像素就落在空白里)。
        await c.SendAsync(56, 0, b => b.U32(gc).U32(0x20).U32(1));
        bad = await c.SendAsync(58, 0, b => b.U32(gc).U16(2).U16(1).U8(0).U8(0).U8(0).U8(0));
        Assert.AreEqual(2, await ErrorOfAsync(c, bad), "dash 为 0:BadValue");
        await PolyLineAsync(c, window, gc, (0, 10), (7, 10));
        await c.SyncAsync();
        Assert.AreEqual(0xFF0000u, Pixel(handle, 2, 10), "dash-offset 仍是 0");
        Assert.AreEqual(0x000000u, Pixel(handle, 4, 10));
    }

    [TestMethod]
    public async Task PutImage位图格式的left_pad不小于32回BadMatch()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint window, _) = await MapWindowAsync(c, host);
        uint gc = await CreateGcAsync(c, window, (GcForeground, 0xFF0000));
        // 1×1 的 XYBitmap、left-pad 32:每行 33 位 → 补齐到 8 字节。
        ushort seq = await c.SendAsync(72, 0, b => b.U32(window).U32(gc).U16(1).U16(1).I16(0).I16(0).U8(32).U8(1).U16(0).U32(0).U32(1));
        Assert.AreEqual(8, await ErrorOfAsync(c, seq), "BadMatch");
    }

    private static Task<ushort> CopyAreaAsync(XTestClient c, uint src, uint dst, uint gc, short sx, short sy, short dx, short dy, ushort width, ushort height) =>
        c.SendAsync(62, 0, b => b.U32(src).U32(dst).U32(gc).I16(sx).I16(sy).I16(dx).I16(dy).U16(width).U16(height));

    private static async Task<uint> PixmapPixelAsync(XTestClient c, uint pixmap, short x, short y) =>
        (await c.RequestAsync(73, 2, b => b.U32(pixmap).I16(x).I16(y).U16(1).U16(1).U32(0xFFFFFFFF))).U32(32) & 0xFFFFFF;

    [TestMethod]
    public async Task CopyArea不拷被子窗口挡住与不可见的源_改发GraphicsExposure_IncludeInferiors时连子窗口一起拷()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint window, XTopLevelWindow handle) = await MapWindowAsync(c, host);
        // 顶层左上 20×10 画红;(5, 0) 一个 5×10、背景蓝的子窗口。
        await FillRectAsync(c, window, await CreateGcAsync(c, window, (GcForeground, 0xFF0000)), 0, 0, 20, 10);
        uint child = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(child).U32(window).I16(5).I16(0).U16(5).U16(10).U16(0).U16(1).U32(0).U32(0x2).U32(0x0000FF));
        await c.SendAsync(8, 0, b => b.U32(child));
        uint pixmap = await PixmapAsync(c, window, 24, 20, 10);
        uint green = await CreateGcAsync(c, pixmap, (GcForeground, 0x00FF00));

        // ClipByChildren(默认):子窗口盖住的那一块拷不到 —— 像素图上保持原样,并报 GraphicsExposure。原先拷到的是子窗口的蓝。
        await FillRectAsync(c, pixmap, green, 0, 0, 20, 10);
        await CopyAreaAsync(c, window, pixmap, green, 0, 0, 0, 0, 20, 10);
        XMessage exposure = await c.NextEventAsync(13);
        Assert.AreEqual("5,0 5×10", $"{exposure.U16(8)},{exposure.U16(10)} {exposure.U16(12)}×{exposure.U16(14)}");
        Assert.AreEqual(0xFF0000u, await PixmapPixelAsync(c, pixmap, 2, 2));
        Assert.AreEqual(0x00FF00u, await PixmapPixelAsync(c, pixmap, 7, 2), "被子窗口挡住的不拷");

        // IncludeInferiors:连子窗口的内容一起拷,都拿得到 —— NoExposure。
        uint inferiors = await CreateGcAsync(c, window, (0x8000, 1));
        await CopyAreaAsync(c, window, pixmap, inferiors, 0, 0, 0, 0, 20, 10);
        Assert.AreEqual(XEventCodeNoExposure, (await c.NextAsync(m => !m.IsError && !m.IsReply && m.EventCode is 13 or 14)).EventCode);
        Assert.AreEqual(0x0000FFu, await PixmapPixelAsync(c, pixmap, 7, 2));

        // 源窗口没映射:整块都拿不到。
        uint hidden = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(hidden).U32(window).I16(30).I16(30).U16(8).U16(8).U16(0).U16(1).U32(0).U32(0));
        await CopyAreaAsync(c, hidden, pixmap, green, 0, 0, 0, 0, 8, 8);
        XMessage unmapped = await c.NextAsync(m => !m.IsError && !m.IsReply && m.EventCode is 13 or 14);
        Assert.AreEqual(13, unmapped.EventCode, "GraphicsExposure 而不是 NoExposure");

        // 目标是窗口:源拿不到的那一块先用目标窗口的背景(黑)铺上。
        await FillRectAsync(c, window, await CreateGcAsync(c, window, (GcForeground, 0xFFFFFF)), 0, 30, 20, 10);
        uint small = await PixmapAsync(c, window, 24, 10, 10);
        await CopyAreaAsync(c, small, window, green, 5, 0, 0, 30, 10, 10);   // 源只有第 5–9 列
        await c.SyncAsync();
        Assert.AreEqual(0x000000u, Pixel(handle, 7, 32), "拿不到的部分铺上背景");
        Assert.AreEqual(0xFFFFFFu, Pixel(handle, 12, 32), "请求范围之外不动");
    }

    private const byte XEventCodeNoExposure = 14;

    [TestMethod]
    public async Task GetImage的XYPixmap按平面掩码逐平面给出_高位平面在前_LSBFirst()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint window, _) = await MapWindowAsync(c, host);
        uint pixmap = await PixmapAsync(c, window, 24, 11, 2);
        // (0,0) 与 (9,1) 是 0x800001(第 23 与第 0 位),其余 0。
        uint gc = await CreateGcAsync(c, pixmap, (GcForeground, 0x800001));
        await FillRectAsync(c, pixmap, gc, 0, 0, 1, 1);
        await FillRectAsync(c, pixmap, gc, 9, 1, 1, 1);
        // 平面掩码只要第 23 位与第 0 位:两张位图,每行 11 位补齐到 4 字节、两行 —— 每张 8 字节。
        XMessage image = await c.RequestAsync(73, 1, b => b.U32(pixmap).I16(0).I16(0).U16(11).U16(2).U32(0x800001));
        Assert.AreEqual(16u * 1 / 4, image.U32(4), "回复长度 = 16 字节");
        byte[] data = image.Bytes[32..48];
        byte[] plane = [0x01, 0, 0, 0, 0x00, 0x02, 0, 0];   // (0,0) 在第 0 行最低位;(9,1) 在第 1 行第 2 个字节的第 1 位
        CollectionAssert.AreEqual(plane, data[..8], "第 23 位平面(高位在前)");
        CollectionAssert.AreEqual(plane, data[8..], "第 0 位平面");
    }

    private static async Task<uint> PixmapAsync(XTestClient c, uint drawable, byte depth, ushort width, ushort height)
    {
        uint pixmap = c.NewId();
        await c.SendAsync(53, depth, b => b.U32(pixmap).U32(drawable).U16(width).U16(height));
        return pixmap;
    }

    private static Task<ushort> FillRectAsync(XTestClient c, uint drawable, uint gc, short x, short y, ushort width, ushort height) =>
        c.SendAsync(70, 0, b => b.U32(drawable).U32(gc).I16(x).I16(y).U16(width).U16(height));

    private static Task<ushort> CopyPlaneAsync(XTestClient c, uint src, uint dst, uint gc, short sx, short sy, short dx, short dy,
        ushort width, ushort height, uint plane) =>
        c.SendAsync(63, 0, b => b.U32(src).U32(dst).U32(gc).I16(sx).I16(sy).I16(dx).I16(dy).U16(width).U16(height).U32(plane));

    [TestMethod]
    public async Task CopyPlane按位平面贴前景背景_源拿不到的部分发GraphicsExposure_位平面超出源深度回BadValue()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint window, XTopLevelWindow handle) = await MapWindowAsync(c, host);

        // 20×10 的 24 位像素图:左半 0x000100(第 8 位)、右半 0。
        uint source = await PixmapAsync(c, window, 24, 20, 10);
        await FillRectAsync(c, source, await CreateGcAsync(c, source, (GcForeground, 0x000100)), 0, 0, 10, 10);
        uint gc = await CreateGcAsync(c, window, (GcForeground, 0xFF0000), (GcBackground, 0x0000FF));

        // 从 (5, 0) 起拷 20×10:源只有 15 列拿得到,目标 (15..19, 0..9) 那一块要客户端补画。
        await CopyPlaneAsync(c, source, window, gc, 5, 0, 0, 20, 20, 10, 0x100);
        XMessage exposure = await c.NextEventAsync(13);   // GraphicsExposure
        Assert.AreEqual(window, exposure.U32(4));
        Assert.AreEqual("15,20 5×10", $"{exposure.U16(8)},{exposure.U16(10)} {exposure.U16(12)}×{exposure.U16(14)}");
        Assert.AreEqual(63, exposure.Bytes[20], "major-opcode = CopyPlane");
        Assert.AreEqual(0xFF0000u, Pixel(handle, 4, 25), "位为 1:前景");
        Assert.AreEqual(0x0000FFu, Pixel(handle, 5, 25), "位为 0:背景");
        Assert.AreEqual(0x000000u, Pixel(handle, 15, 25), "源之外不画");

        // 8 位的源没有第 8 位平面。
        uint shallow = await PixmapAsync(c, window, 8, 4, 4);
        ushort bad = await CopyPlaneAsync(c, shallow, window, gc, 0, 0, 0, 0, 4, 4, 0x100);
        XMessage error = await c.NextAsync(m => m.IsError && m.Sequence == bad);
        Assert.AreEqual(2, error.Detail, "BadValue");
    }
}
