using System.Text;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>窗口映射、Expose、绘图、文字、输入 —— 画出来的像素与收到的事件都直接断言。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class WindowAndDrawingTests
{
    private const uint ExposureMask = 0x8000, KeyPressMask = 0x1, ButtonPressMask = 0x4, StructureNotifyMask = 0x20000;

    /// <summary>建一个顶层窗口(背景色 + 事件掩码)、映射、等宿主看到它。</summary>
    private static async Task<(uint Window, XTopLevelWindow Handle)> MapWindowAsync(
        XTestClient c, RecordingHost host, uint background, uint eventMask, int width = 64, int height = 48)
    {
        uint id = c.NewId();
        // CreateWindow:value-mask = background-pixel(0x2) | event-mask(0x800)
        await c.SendAsync(1, 24, b => b.U32(id).U32(c.RootWindow).I16(20).I16(30).U16((ushort)width).U16((ushort)height)
            .U16(0).U16(1).U32(0).U32(0x802).U32(background).U32(eventMask));
        await c.SendAsync(8, 0, b => b.U32(id));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
        return (id, host.Mapped[id]);
    }

    private static async Task<uint> CreateGcAsync(XTestClient c, uint drawable, uint foreground)
    {
        uint gc = c.NewId();
        await c.SendAsync(55, 0, b => b.U32(gc).U32(drawable).U32(0x4).U32(foreground));   // foreground
        return gc;
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task 映射顶层窗口会画背景并发Expose(bool bigEndian)
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server, bigEndian);

        (uint id, XTopLevelWindow handle) = await MapWindowAsync(c, host, 0x336699, ExposureMask | StructureNotifyMask);
        Assert.AreEqual(64, handle.Snapshot.Width);
        Assert.AreEqual(20, handle.Snapshot.X);

        XMessage map = await c.NextEventAsync(19);   // MapNotify
        Assert.AreEqual(id, map.U32(8));
        XMessage expose = await c.NextEventAsync(12);
        Assert.AreEqual(id, expose.U32(4));
        Assert.AreEqual(64, expose.U16(12));
        Assert.AreEqual(0, expose.U16(16), "count");

        (uint[] pixels, _, _) = RecordingHost.Snapshot(handle);
        Assert.AreEqual(0x336699u, pixels[0]);
        Assert.AreEqual(0x336699u, pixels[^1]);
    }

    [TestMethod]
    public async Task 子窗口按堆叠裁剪背景且有边框()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint top, XTopLevelWindow handle) = await MapWindowAsync(c, host, 0x000000, 0);

        uint child = c.NewId();
        // 位置 (10,10)、内区 20×20、边框 2(border-pixel 0x8 = 红)、背景白。
        await c.SendAsync(1, 24, b => b.U32(child).U32(top).I16(10).I16(10).U16(20).U16(20).U16(2).U16(1).U32(0)
            .U32(0x2 | 0x8).U32(0xFFFFFF).U32(0xFF0000));
        await c.SendAsync(8, 0, b => b.U32(child));
        await c.SyncAsync();

        (uint[] px, int w, _) = RecordingHost.Snapshot(handle);
        Assert.AreEqual(0xFF0000u, px[(10 * w) + 10], "边框左上角");
        Assert.AreEqual(0xFFFFFFu, px[(12 * w) + 12], "内区左上角(10+2)");
        Assert.AreEqual(0x000000u, px[(40 * w) + 40], "父窗口背景");
    }

    [TestMethod]
    public async Task 填矩形与CopyArea画到像素上并回NoExposure()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint win, XTopLevelWindow handle) = await MapWindowAsync(c, host, 0, 0);
        uint gc = await CreateGcAsync(c, win, 0x00FF00);

        await c.SendAsync(70, 0, b => b.U32(win).U32(gc).I16(2).I16(3).U16(5).U16(4));        // PolyFillRectangle
        await c.SendAsync(62, 0, b => b.U32(win).U32(win).U32(gc).I16(2).I16(3).I16(30).I16(20).U16(5).U16(4));   // CopyArea
        XMessage noExpose = await c.NextEventAsync(14);
        Assert.AreEqual(win, noExpose.U32(4));

        (uint[] px, int w, _) = RecordingHost.Snapshot(handle);
        Assert.AreEqual(0x00FF00u, px[(3 * w) + 2]);
        Assert.AreEqual(0x00FF00u, px[(6 * w) + 6], "矩形右下角 (2+5−1, 3+4−1)");
        Assert.AreEqual(0u, px[(7 * w) + 7], "矩形外");
        Assert.AreEqual(0x00FF00u, px[(20 * w) + 30], "拷过去的左上角");
        Assert.AreEqual(0x00FF00u, px[(23 * w) + 34]);
    }

    [TestMethod]
    public async Task GXxor画两遍等于没画()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint win, XTopLevelWindow handle) = await MapWindowAsync(c, host, 0x123456, 0);
        uint gc = c.NewId();
        // function = GXxor(6)、foreground、line-width 3(宽线走多边形并集,不能重复着色)
        await c.SendAsync(55, 0, b => b.U32(gc).U32(win).U32(0x1 | 0x4 | 0x10).U32(6).U32(0xFFFFFF).U32(3));
        for (int i = 0; i < 2; i++)
        {
            await c.SendAsync(65, 0, b => b.U32(win).U32(gc).I16(5).I16(5).I16(50).I16(40).I16(5).I16(40));   // PolyLine
        }
        await c.SyncAsync();
        (uint[] px, _, _) = RecordingHost.Snapshot(handle);
        Assert.IsTrue(px.All(p => p == 0x123456u), "画两遍 XOR 后应当完全复原");
    }

    [TestMethod]
    public async Task ImageText8用内置fixed字体画出字形()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint win, XTopLevelWindow handle) = await MapWindowAsync(c, host, 0x000000, 0);

        uint font = c.NewId();
        await c.SendAsync(45, 0, b => b.U32(font).U16(5).U16(0).Bytes(Encoding.Latin1.GetBytes("fixed")));
        uint gc = c.NewId();
        await c.SendAsync(55, 0, b => b.U32(gc).U32(win).U32(0x4 | 0x8 | 0x4000).U32(0xFFFFFF).U32(0x0000FF).U32(font));
        await c.SendAsync(76, 2, b => b.U32(win).U32(gc).I16(4).I16(20).Bytes(Encoding.Latin1.GetBytes("Hi")));
        await c.SyncAsync();

        (uint[] px, int w, _) = RecordingHost.Snapshot(handle);
        // 字形行框:x 4..16(两个 6 像素宽的字),y 20−11 .. 20+2。背景填成蓝,字形点是白。
        int white = 0, blue = 0;
        for (int y = 9; y < 22; y++)
        {
            for (int x = 4; x < 16; x++)
            {
                if (px[(y * w) + x] == 0xFFFFFFu) white++;
                if (px[(y * w) + x] == 0x0000FFu) blue++;
            }
        }
        Assert.IsGreaterThan(10, white, "应当有字形像素");
        Assert.IsGreaterThan(white, blue, "行框其余部分是背景色");
        Assert.AreEqual(0u, px[(20 * w) + 30], "行框外不动");
    }

    [TestMethod]
    public async Task QueryFont返回fixed的度量与每个字符的CHARINFO()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint font = c.NewId();
        await c.SendAsync(45, 0, b => b.U32(font).U16(5).U16(0).Bytes(Encoding.Latin1.GetBytes("fixed")));
        XMessage reply = await c.RequestAsync(47, 0, b => b.U32(font));
        Assert.IsTrue(reply.IsReply);
        Assert.AreEqual(6, reply.I16(28), "max-bounds(从 24 起)的 character-width = 6");
        Assert.AreEqual(11, reply.I16(52), "font-ascent");
        Assert.AreEqual(2, reply.I16(54), "font-descent");
        uint charInfos = reply.U32(56);
        Assert.AreEqual(256u - reply.U16(40), charInfos, "单字节字体:min..255 每个一条");
    }

    [TestMethod]
    public async Task ListFonts按通配符匹配()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        string pattern = "-misc-fixed-medium-r-*-*-13-*-*-*-*-*-iso10646-1";
        XMessage reply = await c.RequestAsync(49, 0, b => b.U16(10).U16((ushort)pattern.Length).Bytes(Encoding.Latin1.GetBytes(pattern)));
        Assert.AreEqual(1, reply.U16(8));
        string text = Encoding.Latin1.GetString(reply.Bytes, 33, reply.Bytes[32]);
        Assert.AreEqual("-misc-fixed-medium-r-semicondensed--13-120-75-75-c-60-iso10646-1", text);
    }

    [TestMethod]
    public async Task 宿主注入的点击与按键按键码投递并带正确坐标()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint win, _) = await MapWindowAsync(c, host, 0, ButtonPressMask | KeyPressMask);

        server.InjectPointerButton(host.Mapped[win], 7, 9, 1, pressed: true);
        XMessage press = await c.NextEventAsync(4);
        Assert.AreEqual(1, press.Detail, "button 1");
        Assert.AreEqual(win, press.U32(12), "event window");
        Assert.AreEqual(27, press.I16(20), "root-x = 20 + 7");
        Assert.AreEqual(7, press.I16(24), "event-x");
        Assert.AreEqual(9, press.I16(26), "event-y");
        server.InjectPointerButton(host.Mapped[win], 7, 9, 1, pressed: false);

        server.FocusTopLevel(host.Mapped[win]);
        server.InjectKey(XKeycodes.A, pressed: true);
        XMessage key = await c.NextEventAsync(2);
        Assert.AreEqual(XKeycodes.A, key.Detail);

        XMessage map = await c.RequestAsync(101, 0, b => b.U8(XKeycodes.A).U8(1).U16(0));
        Assert.AreEqual('a', map.U32(32));
        Assert.AreEqual('A', map.U32(36));
    }

    [TestMethod]
    public async Task 宿主缩放窗口时客户端收到ConfigureNotify与Expose()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint win, XTopLevelWindow handle) = await MapWindowAsync(c, host, 0xABCDEF, ExposureMask | StructureNotifyMask);
        await c.NextEventAsync(12);

        server.ResizeTopLevel(host.Mapped[win], 120, 90);
        XMessage configure = await c.NextEventAsync(22);
        Assert.AreEqual(120, configure.U16(20));
        Assert.AreEqual(90, configure.U16(22));
        await c.NextEventAsync(12);
        (uint[] px, int w, int h) = RecordingHost.Snapshot(handle);
        Assert.AreEqual((120, 90), (w, h));
        Assert.AreEqual(0xABCDEFu, px[^1]);
    }

    [TestMethod]
    public async Task 关闭按钮对声明了WM_DELETE_WINDOW的窗口发ClientMessage()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint win, _) = await MapWindowAsync(c, host, 0, 0);

        uint protocols = (await c.RequestAsync(16, 0, b => b.U16(12).U16(0).Bytes(Encoding.Latin1.GetBytes("WM_PROTOCOLS")))).U32(8);
        uint delete = (await c.RequestAsync(16, 0, b => b.U16(16).U16(0).Bytes(Encoding.Latin1.GetBytes("WM_DELETE_WINDOW")))).U32(8);
        await c.SendAsync(18, 0, b => b.U32(win).U32(protocols).U32(4).U8(32).U8(0).U8(0).U8(0).U32(1).U32(delete));
        await c.SyncAsync();

        server.CloseTopLevel(host.Mapped[win]);
        XMessage message = await c.NextEventAsync(33);
        Assert.IsTrue((message.Kind & 0x80) != 0, "SendEvent 合成的事件带 sent 位");
        Assert.AreEqual(protocols, message.U32(8));
        Assert.AreEqual(delete, message.U32(12));
    }

    [TestMethod]
    public async Task 标题变化通知宿主()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint win, XTopLevelWindow handle) = await MapWindowAsync(c, host, 0, 0);
        byte[] title = Encoding.Latin1.GetBytes("xterm");
        await c.SendAsync(18, 0, b => b.U32(win).U32(39).U32(31).U8(8).U8(0).U8(0).U8(0).U32((uint)title.Length).Bytes(title));
        await host.WaitForAsync(() => handle.Snapshot.Title == "xterm");
    }
}
