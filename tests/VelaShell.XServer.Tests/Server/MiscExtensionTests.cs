using System.Text;
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
        server.FocusTopLevel(host.Mapped[top]);
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
        await using X11Server server = new(new X11ServerOptions { ScreenWidth = 1920, ScreenHeight = 1080 });
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
        Assert.IsGreaterThanOrEqualTo(100u, before.U32(16), $"空闲 {before.U32(16)} ms");

        server.InjectKey(38, true);
        server.InjectKey(38, false);
        XMessage after = await c.RequestAsync(saver, 1, b => b.U32(c.RootWindow));
        Assert.IsLessThan(100u, after.U32(16), $"输入后空闲应归零,实际 {after.U32(16)} ms");
    }

    /// <summary>用户在宿主的本机界面里打字:宿主报一声 NoteUserActivity,远端看到的空闲时间同样归零(不产生任何输入事件)。</summary>
    [TestMethod]
    public async Task 宿主报的本机活动让空闲时间归零()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte saver = await MajorAsync(c, "MIT-SCREEN-SAVER");
        await Task.Delay(120);
        XMessage before = await c.RequestAsync(saver, 1, b => b.U32(c.RootWindow));
        Assert.IsGreaterThanOrEqualTo(100u, before.U32(16), $"空闲 {before.U32(16)} ms");

        server.NoteUserActivity();
        server.NoteUserActivity();   // 连着报只排一个工作项
        XMessage after = await c.RequestAsync(saver, 1, b => b.U32(c.RootWindow));
        Assert.IsLessThan(100u, after.U32(16), $"本机有动静之后空闲应归零,实际 {after.U32(16)} ms");
    }

    /// <summary>
    /// 屏保交给宿主(xs_plan F9):Suspend 每个客户端各自计数,任何一个挂着就告诉宿主「挂起」,都恢复(或挂着的客户端断开)才报「恢复」;
    /// 别的客户端恢复不了别人挂起的。ForceScreenSaver(Reset) 隔一会儿告诉宿主一次。
    /// </summary>
    [TestMethod]
    public async Task 屏保挂起按客户端计数交给宿主_断开即作废_Reset隔一会儿报一次()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        XTestClient player = await XTestClient.ConnectAsync(server);
        await using XTestClient other = await XTestClient.ConnectAsync(server);
        byte saver = await MajorAsync(player, "MIT-SCREEN-SAVER");
        Task<ushort> SuspendAsync(XTestClient c, bool suspend) => c.SendAsync(saver, 5, b => b.U32(suspend ? 1u : 0u));

        await SuspendAsync(player, true);
        await SuspendAsync(player, true);   // 嵌套两次
        await player.SyncAsync();
        await host.WaitForAsync(() => !host.SaverSuspensions.IsEmpty);
        CollectionAssert.AreEqual(new[] { true }, host.SaverSuspensions.ToArray(), "第一次挂起报一次");
        await SuspendAsync(other, false);    // 别的客户端恢复不了
        await SuspendAsync(player, false);   // 还剩一层
        await other.SyncAsync();
        await player.SyncAsync();
        Assert.HasCount(1, host.SaverSuspensions);
        await player.DisposeAsync();         // 挂着的客户端断开:作废
        await host.WaitForAsync(() => host.SaverSuspensions.Count >= 2);
        CollectionAssert.AreEqual(new[] { true, false }, host.SaverSuspensions.ToArray());

        await other.SendAsync(115, 0);       // ForceScreenSaver(Reset)
        await other.SendAsync(115, 0);
        await other.SyncAsync();
        await host.WaitForAsync(() => host.SaverResets >= 1);
        Assert.AreEqual(1, host.SaverResets, "连着的 Reset 只报一次");
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

    /// <summary>XTEST FakeInput:type、detail、延迟(毫秒),其余为 0。</summary>
    private static Task<ushort> FakeInputAsync(XTestClient c, byte xtest, byte type, byte detail, uint delay) =>
        c.SendAsync(xtest, 2, b => b.U8(type).U8(detail).U16(0).U32(delay).U32(0).U32(0).U32(0).I16(0).I16(0).U32(0).U32(0));

    [TestMethod]
    public async Task XTEST的延迟到点之前这个客户端的请求不处理_按下与松开不会乱序把键卡住()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte xtest = await MajorAsync(c, "XTEST");
        const byte a = 38;

        // 按下延迟 1 秒、松开立即:旧的实现先执行松开(那时还没按下)、再按下,键就一直按着。
        // 延迟要远大于下面那 50 毫秒:原先是 150,只留 100 毫秒余量,macOS runner 忙起来
        // Task.Delay(50) 就能睡过头,查的时候延迟已经到点,「延迟期间不处理」那条就红了。
        await FakeInputAsync(c, xtest, 2, a, delay: 1000);
        await FakeInputAsync(c, xtest, 3, a, delay: 0);
        Task<XMessage> keymap = c.RequestAsync(44, 0);   // QueryKeymap:要等延迟到点、两条都做完才处理
        await Task.Delay(50);
        Assert.IsFalse(keymap.IsCompleted, "延迟期间这个客户端后面的请求不处理");
        XMessage keys = await keymap;
        Assert.AreEqual(0, keys.Bytes[8 + (a / 8)] & (1 << (a % 8)), "按下、松开按发出的顺序生效,键没被卡住");

        await Task.Delay(300);
        keys = await c.RequestAsync(44, 0);
        Assert.AreEqual(0, keys.Bytes[8 + (a / 8)] & (1 << (a % 8)), "过一会儿再看也没卡住");
    }

    [TestMethod]
    public async Task XTEST的延迟与Present的NotifyMSC在客户端断开时取消_NotifyMSC有上限()
    {
        await using X11Server server = new();
        XTestClient c = await XTestClient.ConnectAsync(server);
        byte xtest = await MajorAsync(c, "XTEST");
        byte present = await MajorAsync(c, "Present");
        uint window = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(window).U32(c.RootWindow).I16(0).I16(0).U16(10).U16(10).U16(0).U16(1).U32(0).U32(0));

        // NotifyMSC 目标 MSC 在几十天之后:每条一个计时器。
        ushort last = await c.SendManyAsync(Enumerable.Range(0, X11Server.MaxPendingPresents + 1).Select<int, (byte, byte, Action<XTestClient.Body>?)>(i =>
            (present, 2, b => b.U32(window).U32((uint)i).U32(0).U32(100_000_000).U32(0).U32(0).U32(0).U32(0).U32(0))));
        XMessage refused = await c.NextAsync(m => m.IsError && m.Sequence == last);
        Assert.AreEqual(11, refused.Detail, "超过上限:BadAlloc");
        await FakeInputAsync(c, xtest, 2, 38, delay: int.MaxValue);   // 挂一个 24 天的延迟
        // 延迟挂着时这个客户端之后的请求都暂存,没法用一次往返确认 FakeInput 已经执行;读端把它排进执行线程之前,
        // InvokeAsync 可能先排进去(负载重时见过):轮询到它执行了为止。
        using (CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5)))
        {
            while (await server.InvokeAsync(() => server.PendingFakeInputDelays) == 0)
            {
                await Task.Delay(10, timeout.Token);
            }
        }
        Assert.AreEqual((1, X11Server.MaxPendingPresents), await server.InvokeAsync(() => (server.PendingFakeInputDelays, server.PendingPresents)));

        Task serving = c.ServerTask;
        await c.DisposeAsync();
        await serving.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual((0, 0), await server.InvokeAsync(() => (server.PendingFakeInputDelays, server.PendingPresents)), "断开时计时器一并取消");
    }

    [TestMethod]
    public async Task QueryClientIds每个客户端只回一次_GetXIDList一次最多给上限个()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        await using XTestClient other = await XTestClient.ConnectAsync(server);
        byte xres = await MajorAsync(c, "X-Resource");
        // 1000 条 spec,每条都是 client = 0(全部客户端)、mask = ClientXIDMask:旧的实现每条都展开一遍。
        XMessage ids = await c.RequestAsync(xres, 4, b =>
        {
            b.U32(1000);
            for (int i = 0; i < 1000; i++)
            {
                b.U32(0).U32(1);
            }
        });
        Assert.AreEqual(2u, ids.U32(8), "两个客户端,各回一次");

        byte xcmisc = await MajorAsync(c, "XC-MISC");
        XMessage list = await c.RequestAsync(xcmisc, 2, b => b.U32(uint.MaxValue));
        Assert.AreEqual(X11Server.MaxXidListCount, list.U32(8), "给的可以比要的少(XC-MISC 规范)");
    }
}
