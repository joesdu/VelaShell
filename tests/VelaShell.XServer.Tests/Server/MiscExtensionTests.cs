using System.Text;
using VelaShell.XServer.Host;
using VelaShell.XServer.Server;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>XTEST、XINERAMA、RANDR 的运行时布局、MIT-SCREEN-SAVER、DPMS、X-Resource、Generic Event。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class MiscExtensionTests
{
    private static async Task<byte> MajorAsync(XTestClient c, string name)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(name);
        XMessage q = await c.RequestAsync(98, 0, b => b.U16((ushort)bytes.Length).U16(0).Bytes(bytes).Pad());
        Assert.AreEqual(1, q.Bytes[8], $"{name} 应当存在");
        return q.Bytes[9];
    }

    private static async Task<uint> MapTopAsync(XTestClient c, RecordingHost host, uint eventMask)
    {
        uint id = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(id).U32(c.RootWindow).I16(10).I16(20).U16(50).U16(40).U16(0).U16(1).U32(0)
            .U32(0x800).U32(eventMask));
        await c.SendAsync(8, 0, b => b.U32(id));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
        return id;
    }

    [TestMethod]
    public async Task XTEST伪造按键与绝对移动投递给窗口()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte major = await MajorAsync(c, "XTEST");
        XMessage version = await c.RequestAsync(major, 0, b => b.U8(2).U8(0).U16(2));
        Assert.AreEqual(2, version.Bytes[1]);

        uint top = await MapTopAsync(c, host, 0x1 | 0x40);   // KeyPress | PointerMotion
        server.FocusTopLevel(top);
        await c.SyncAsync();

        // FakeInput(MotionNotify, 绝对, 根坐标 (30, 35)) → 窗口内 (20, 15)。
        await c.SendAsync(major, 2, b => b.U8(6).U8(0).U16(0).U32(0).U32(0).U32(0).U32(0).I16(30).I16(35)
            .U32(0).U16(0).U8(0).U8(0));
        XMessage motion = await c.NextEventAsync(6);
        Assert.AreEqual(20, motion.I16(24));
        Assert.AreEqual(15, motion.I16(26));

        await c.SendAsync(major, 2, b => b.U8(2).U8(38).U16(0).U32(0).U32(0).U32(0).U32(0).I16(0).I16(0)
            .U32(0).U16(0).U8(0).U8(0));
        XMessage key = await c.NextEventAsync(2);
        Assert.AreEqual(38, key.Bytes[1], "keycode");
    }

    [TestMethod]
    public async Task 运行时换成两台显示器_XINERAMA与RANDR都看得到且发通知()
    {
        await using X11Server server = new(new XServerOptions { ScreenWidth = 1920, ScreenHeight = 1080 });
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte xinerama = await MajorAsync(c, "XINERAMA");
        byte randr = await MajorAsync(c, "RANDR");
        await c.SendAsync(randr, 4, b => b.U32(c.RootWindow).U16(1 | 2).U16(0));   // SelectInput:ScreenChange | CrtcChange
        await c.SendAsync(2, 0, b => b.U32(c.RootWindow).U32(0x800).U32(0x20000));  // 根窗口 StructureNotify
        await c.SyncAsync();

        server.SetScreenLayout(3840, 1080,
        [
            new XMonitor(0, 0, 1920, 1080) { Name = "DP-1" },
            new XMonitor(1920, 0, 1920, 1080) { Name = "HDMI-1", Primary = true },
        ]);

        XMessage configure = await c.NextEventAsync(22);
        Assert.AreEqual(3840, configure.U16(20), "根窗口变宽");
        XMessage q = await c.RequestAsync(98, 0, b => b.U16(5).U16(0).Bytes(Encoding.Latin1.GetBytes("RANDR")).Pad());
        byte randrEvent = q.Bytes[10];
        XMessage screenChange = await c.NextEventAsync(randrEvent);
        Assert.AreEqual(3840, screenChange.U16(24));
        await c.NextEventAsync((byte)(randrEvent + 1));   // CrtcChange

        XMessage screens = await c.RequestAsync(xinerama, 5);
        Assert.AreEqual(2u, screens.U32(8));
        Assert.AreEqual(1920, screens.I16(32), "主显示器排第一");

        XMessage monitors = await c.RequestAsync(randr, 42, b => b.U32(c.RootWindow).U8(1).U8(0).U8(0).U8(0));
        Assert.AreEqual(2u, monitors.U32(12));
        Assert.AreEqual(0, monitors.Bytes[36], "第一台不是主显示器");
        Assert.AreEqual(1, monitors.Bytes[36 + 28], "第二台是主显示器");
    }

    [TestMethod]
    public async Task 屏保报出空闲时间且输入会归零()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte saver = await MajorAsync(c, "MIT-SCREEN-SAVER");
        await c.SendAsync(107, 0, b => b.I16(600).I16(60).U8(1).U8(1));   // SetScreenSaver
        XMessage core = await c.RequestAsync(108, 0);
        Assert.AreEqual(600, core.U16(8));

        await Task.Delay(120);
        XMessage before = await c.RequestAsync(saver, 1, b => b.U32(c.RootWindow));
        Assert.AreEqual(0, before.Bytes[1], "state = Off");
        Assert.IsTrue(before.U32(16) >= 100, $"空闲 {before.U32(16)} ms");

        server.Key(38, true);
        server.Key(38, false);
        XMessage after = await c.RequestAsync(saver, 1, b => b.U32(c.RootWindow));
        Assert.IsTrue(after.U32(16) < 100, $"输入后空闲应归零,实际 {after.U32(16)} ms");
    }

    [TestMethod]
    public async Task DPMS的超时与开关往返()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte dpms = await MajorAsync(c, "DPMS");
        await c.SendAsync(dpms, 3, b => b.U16(60).U16(120).U16(180).U16(0));
        await c.SendAsync(dpms, 4);
        XMessage timeouts = await c.RequestAsync(dpms, 2);
        Assert.AreEqual(120, timeouts.U16(10));
        XMessage info = await c.RequestAsync(dpms, 7);
        Assert.AreEqual(1, info.Bytes[10], "已启用");
        XMessage bad = await c.RequestAsync(dpms, 3, b => b.U16(300).U16(120).U16(180).U16(0));
        Assert.IsTrue(bad.IsError, "standby > suspend 是 BadValue");
    }

    [TestMethod]
    public async Task XRes列出客户端与各类资源数()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte xres = await MajorAsync(c, "X-Resource");
        uint pixmap = c.NewId();
        await c.SendAsync(53, 24, b => b.U32(pixmap).U32(c.RootWindow).U16(10).U16(10));

        XMessage clients = await c.RequestAsync(xres, 1);
        Assert.AreEqual(1u, clients.U32(8));
        Assert.AreEqual(c.ResourceBase, clients.U32(32));

        XMessage bytes = await c.RequestAsync(xres, 3, b => b.U32(c.ResourceBase));
        Assert.AreEqual(400u, bytes.U32(8), "10×10、32 bpp");

        XMessage resources = await c.RequestAsync(xres, 2, b => b.U32(c.ResourceBase));
        Assert.AreEqual(1u, resources.U32(8), "一类资源:PIXMAP");
        Assert.AreEqual(1u, resources.U32(36));
    }

    [TestMethod]
    public async Task GenericEvent扩展报1点0()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte ge = await MajorAsync(c, "Generic Event Extension");
        XMessage v = await c.RequestAsync(ge, 0, b => b.U16(1).U16(0));
        Assert.AreEqual(1, v.U16(8));
        Assert.AreEqual(0, v.U16(10));
    }
}
