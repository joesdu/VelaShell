using System.Text;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>宿主接口:运行中改的配置(DPI / 缩放、键位表)、选项校验、窗口快照与变化、光标、响铃、句柄的归属。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class HostApiTests
{
    private static async Task<uint> InternAsync(XTestClient c, string name)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(name);
        XMessage m = await c.RequestAsync(16, 0, b => b.U16((ushort)bytes.Length).U16(0).Bytes(bytes).Pad());
        return m.U32(8);
    }

    private static async Task<string> RootStringAsync(XTestClient c, string property)
    {
        uint atom = await InternAsync(c, property);
        XMessage p = await c.RequestAsync(20, 0, b => b.U32(c.RootWindow).U32(atom).U32(0).U32(0).U32(1000));
        return Encoding.Latin1.GetString(p.Bytes, 32, (int)p.U32(16));
    }

    private static async Task<byte> MajorAsync(XTestClient c, string extension)
    {
        byte[] name = Encoding.Latin1.GetBytes(extension);
        XMessage q = await c.RequestAsync(98, 0, b => b.U16((ushort)name.Length).U16(0).Bytes(name).Pad());
        Assert.AreEqual(1, q.Bytes[8], $"{extension} 应当存在");
        return q.Bytes[9];
    }

    private static async Task<uint> MapTopAsync(XTestClient c, RecordingHost host)
    {
        uint id = c.NewId();
        await c.SendAsync(1, 24, b => b.U32(id).U32(c.RootWindow).I16(0).I16(0).U16(60).U16(40).U16(0).U16(1).U32(0)
            .U32(0x2).U32(0));
        await c.SendAsync(8, 0, b => b.U32(id));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
        return id;
    }

    private static Task<ushort> SetTitleAsync(XTestClient c, uint window, string title)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(title);
        return c.SendAsync(18, 0, b => b.U32(window).U32(39).U32(31).U8(8).U8(0).U8(0).U8(0).U32((uint)bytes.Length).Bytes(bytes).Pad());
    }

    [TestMethod]
    public async Task SetDisplayScale更新Xft_dpi与XSETTINGS的缩放()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        StringAssert.Contains(await RootStringAsync(c, "RESOURCE_MANAGER"), "Xft.dpi:\t96");
        await c.SendAsync(2, 0, b => b.U32(c.RootWindow).U32(0x800).U32(0x400000));   // PropertyChange
        await c.SyncAsync();

        server.SetDisplayScale(192, 2);
        XMessage notify = await c.NextEventAsync(28);
        Assert.AreEqual(c.RootWindow, notify.U32(4));
        StringAssert.Contains(await RootStringAsync(c, "RESOURCE_MANAGER"), "Xft.dpi:\t192");

        uint selection = await InternAsync(c, "_XSETTINGS_S0");
        uint settings = await InternAsync(c, "_XSETTINGS_SETTINGS");
        uint manager = (await c.RequestAsync(23, 0, b => b.U32(selection))).U32(8);
        XMessage prop = await c.RequestAsync(20, 0, b => b.U32(manager).U32(settings).U32(0).U32(0).U32(1000));
        byte[] data = prop.Bytes[32..(32 + (int)prop.U32(16))];
        int at = Encoding.ASCII.GetString(data).IndexOf("Gdk/WindowScalingFactor", StringComparison.Ordinal);
        Assert.IsTrue(at > 0);
        Assert.AreEqual(2, BitConverter.ToInt32(data, at + 24 + 4), "名字 23 字节补到 24,再跳过 last-change-serial");
    }

    [TestMethod]
    public async Task SetKeymap一次换掉键值与布局名_客户端只收到一轮MappingNotify()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        await c.SyncAsync();
        // 德语布局的一角:键码 29 是 z / Z(QWERTZ)。
        server.SetKeymap(new XKeymap("de").Map(29, 'z', 'Z'));
        XMessage mapping = await c.NextEventAsync(34);
        Assert.AreEqual(1, mapping.Bytes[4], "request = Keyboard");
        Assert.AreEqual(29, mapping.Bytes[5], "从列出的最小键码起");

        XMessage keys = await c.RequestAsync(101, 0, b => b.U8(29).U8(1).U16(0));   // GetKeyboardMapping
        Assert.AreEqual('z', keys.U32(32));
        StringAssert.Contains(await RootStringAsync(c, "_XKB_RULES_NAMES"), "\0de\0", "布局名跟着键位表一起到");
        await Assert.ThrowsAsync<OperationCanceledException>(() => c.NextEventAsync(34, timeoutMs: 300),
            "修饰键表没变:不该再有第二轮 MappingNotify");
    }

    [TestMethod]
    public async Task SetKeymap的AltGr把右Alt挪进Mod5()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        server.SetKeymap(new XKeymap("de", 6) { AltGr = true }.Map(24, 'q', 'Q', 'q', 'Q', '@', '@'));
        await c.NextEventAsync(34);
        XMessage modifiers = await c.RequestAsync(119, 0);   // GetModifierMapping
        int per = modifiers.Bytes[1];
        Assert.AreEqual(XKeycodes.AltRight, modifiers.Bytes[32 + (7 * per)], "Mod5 里有右 Alt");
        XMessage keys = await c.RequestAsync(101, 0, b => b.U8(XKeycodes.AltRight).U8(1).U16(0));
        Assert.AreEqual(0xfe03u, keys.U32(32), "ISO_Level3_Shift");
    }

    [TestMethod]
    public void 选项不合法时构造就抛异常()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => new X11Server(new X11ServerOptions { Dpi = 0 }));
        Assert.AreEqual("options", error.ParamName);
        Assert.Throws<ArgumentException>(() => new X11Server(new X11ServerOptions { ScreenWidth = 40_000 }));
        Assert.Throws<ArgumentException>(() => new X11Server(new X11ServerOptions { DisplayNumber = -1 }));
        Assert.Throws<ArgumentException>(() => new X11Server(new X11ServerOptions { Monitors = [new XMonitor(0, 0, 0, 10)] }));
    }

    [TestMethod]
    public async Task Display按实际监听的传输给出_没监听时为null()
    {
        await using X11Server server = new(new X11ServerOptions { DisplayNumber = 97, ListenTcp = false, UnixSocketPath = "" });
        Assert.AreEqual(97, server.DisplayNumber);
        Assert.IsNull(server.Display, "还没开始监听");
        await server.StartAsync();
        Assert.IsNull(server.Display, "TCP 与 Unix 套接字都关了:只能经 ServeAsync 喂流");
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.StartAsync());
    }

    [TestMethod]
    public async Task 宿主方法当场校验参数_别的服务端的窗口不收()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using X11Server other = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        XTopLevelWindow window = host.Mapped[await MapTopAsync(c, host)];

        Assert.Throws<ArgumentOutOfRangeException>(() => server.ResizeTopLevel(window, 0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => server.InjectPointerButton(window, 0, 0, 0, pressed: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => server.SetTopLevelFrameExtents(window, new XFrameExtents(-1, 0, 0, 0)));
        Assert.Throws<ArgumentException>(() => other.MoveTopLevel(window, 0, 0));
    }

    [TestMethod]
    public async Task 快照整份替换_变化按组报告_没变不报()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host);
        XTopLevelWindow window = host.Mapped[top];
        XTopLevelSnapshot before = window.Snapshot;
        Assert.IsTrue(before.IsMapped);
        Assert.AreEqual(60, before.Width);

        await SetTitleAsync(c, top, "one");
        await host.WaitForAsync(() => window.Snapshot.Title == "one");
        Assert.AreEqual(XTopLevelChanges.Title, host.LastChanges);
        Assert.AreEqual("", before.Title, "旧快照不变");

        await SetTitleAsync(c, top, "one");   // 值没变:不该报
        await c.SendAsync(12, 0, b => b.U32(top).U16(0xC).U16(0).U32(80).U32(50));   // ConfigureWindow 宽高
        await host.WaitForAsync(() => window.Snapshot.Width == 80);
        Assert.AreEqual(XTopLevelChanges.Geometry, host.LastChanges);
        Assert.AreEqual(1, host.Log.Count(e => e.Contains(" Title ", StringComparison.Ordinal)), "同样的标题再设一遍不算变化");
    }

    [TestMethod]
    public async Task 位图光标连图像交给宿主_XFIXES起的名字推出形状()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host);

        uint pixmap = c.NewId(), cursor = c.NewId();
        await c.SendAsync(53, 1, b => b.U32(pixmap).U32(top).U16(16).U16(16));   // CreatePixmap,深度 1,全 0
        await c.SendAsync(93, 0, b => b.U32(cursor).U32(pixmap).U32(0)              // CreateCursor,没有 mask
            .U16(0xFFFF).U16(0).U16(0).U16(0).U16(0).U16(0xFFFF).U16(3).U16(4));
        await c.SendAsync(2, 0, b => b.U32(top).U32(0x4000).U32(cursor));           // ChangeWindowAttributes:cursor
        server.InjectPointerMotion(host.Mapped[top], 5, 5);
        await host.WaitForAsync(() => host.Cursor?.Image is not null);
        XCursorImage image = host.Cursor!.Image!;
        Assert.AreEqual((16, 16, 3, 4), (image.Width, image.Height, image.HotspotX, image.HotspotY));
        Assert.AreEqual(0xFF0000FFu, image.Pixels[0], "source 为 0 的像素用背景色(蓝),不透明");
        Assert.AreEqual(XCursorShape.Arrow, host.Cursor.Shape, "没起名字:形状推不出来");

        byte xfixes = await MajorAsync(c, "XFIXES");
        byte[] name = Encoding.Latin1.GetBytes("text");
        await c.SendAsync(xfixes, 23, b => b.U32(cursor).U16((ushort)name.Length).U16(0).Bytes(name).Pad());   // SetCursorName
        await host.WaitForAsync(() => host.Cursor?.Shape == XCursorShape.Text);
        Assert.AreSame(image, host.Cursor!.Image, "图像不变");
    }

    [TestMethod]
    public async Task 响铃按协议从基准音量换算()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        await c.SendAsync(104, 0);                                  // 0 → 基准 50
        await c.SendAsync(104, 100);                                // 100 → 100
        await c.SendAsync(104, unchecked((byte)(sbyte)-50));        // −50 → 50 − 25
        await host.WaitForAsync(() => host.Log.Count(e => e.StartsWith("bell", StringComparison.Ordinal)) == 3);
        CollectionAssert.AreEqual((string[])["bell 50", "bell 100", "bell 25"], host.Log.Where(e => e.StartsWith("bell", StringComparison.Ordinal)).ToArray());

        XMessage error = await c.RequestAsync(104, 101);
        Assert.IsTrue(error.IsError, "−100…100 以外是 BadValue");
        Assert.AreEqual(2, error.Bytes[1]);
    }
}
