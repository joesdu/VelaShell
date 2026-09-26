using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>同步抓取:冻结期间设备事件排队,AllowEvents 放行 / 单步 / 重放。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class SyncGrabTests
{
    private const byte KeyPress = 2, KeyRelease = 3, ButtonPress = 4, ButtonRelease = 5, MotionNotify = 6;
    private const byte Synchronous = 0, Asynchronous = 1;

    private static async Task<uint> MapTopAsync(XTestClient c, RecordingHost host, uint eventMask = 0)
    {
        uint id = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(id).U32(c.RootWindow).I16(0).I16(0).U16(100).U16(80).U16(0).U16(1).U32(0)
            .U32(0x800).U32(eventMask));
        await c.SendAsync(8, 0, b => b.U32(id));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
        return id;
    }

    private static async Task<List<XMessage>> DrainAsync(XTestClient c, params byte[] codes)
    {
        await c.SyncAsync();
        List<XMessage> events = [];
        while (true)
        {
            try
            {
                events.Add(await c.NextAsync(m => !m.IsReply && !m.IsError && codes.Contains(m.EventCode), timeoutMs: 80));
            }
            catch (OperationCanceledException)
            {
                return events;
            }
        }
    }

    [TestMethod]
    public async Task 同步GrabPointer冻住指针_AsyncPointer放行后按顺序送达()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host);
        server.InjectPointerMotion(host.Mapped[top], 1, 1);
        await DrainAsync(c, MotionNotify);

        // GrabPointer:owner-events False,事件掩码 PointerMotion,指针同步、键盘异步。
        XMessage grab = await c.RequestAsync(26, 0, b => b.U32(top).U16(0x40).U8(Synchronous).U8(Asynchronous).U32(0).U32(0).U32(0));
        Assert.AreEqual(0, grab.Bytes[1], "GrabSuccess");
        server.InjectPointerMotion(host.Mapped[top], 10, 10);
        server.InjectPointerMotion(host.Mapped[top], 20, 20);
        Assert.IsEmpty(await DrainAsync(c, MotionNotify), "冻着:移动排队,一条都不发");

        await c.SendAsync(35, 0, b => b.U32(0));   // AllowEvents AsyncPointer
        List<XMessage> moves = await DrainAsync(c, MotionNotify);
        Assert.HasCount(2, moves, "放行后两条按原顺序到");
        Assert.AreEqual(10, moves[0].I16(24), "event-x");
        Assert.AreEqual(20, moves[1].I16(24));
    }

    [TestMethod]
    public async Task 同步GrabButton激活后冻结_ReplayPointer把按下重放给下面的窗口()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient a = await XTestClient.ConnectAsync(server);
        await using XTestClient b = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(a, host);
        uint child = a.NewId();
        await a.SendAsync(1, 0, w => w.U32(child).U32(top).I16(10).I16(10).U16(50).U16(40).U16(0).U16(1).U32(0).U32(0));
        await a.SendAsync(8, 0, w => w.U32(child));
        await a.SyncAsync();
        await b.SendAsync(2, 0, w => w.U32(child).U32(0x800).U32(0x4 | 0x8));   // B 在子窗口上选 ButtonPress | ButtonRelease
        await b.SyncAsync();

        // A 在顶层上登记按钮 1 的同步被动抓取(任意修饰)。
        await a.SendAsync(28, 0, w => w.U32(top).U16(0x4 | 0x8).U8(Synchronous).U8(Asynchronous).U32(0).U32(0).U8(1).U8(0).U16(0x8000));
        await a.SyncAsync();

        server.InjectPointerButton(host.Mapped[top], 20, 20, 1, pressed: true);
        server.InjectPointerButton(host.Mapped[top], 20, 20, 1, pressed: false);
        List<XMessage> grabbed = await DrainAsync(a, ButtonPress, ButtonRelease);
        Assert.HasCount(1, grabbed, "A 只拿到按下;松开在冻结的队列里");
        Assert.AreEqual(ButtonPress, grabbed[0].EventCode);
        Assert.IsEmpty(await DrainAsync(b, ButtonPress, ButtonRelease), "被动抓取截走了按下");

        await a.SendAsync(35, 2, w => w.U32(0));   // AllowEvents ReplayPointer
        List<XMessage> replayed = await DrainAsync(b, ButtonPress, ButtonRelease);
        Assert.HasCount(2, replayed, "按下重放给 B,排着的松开随后也到 B");
        Assert.AreEqual(ButtonPress, replayed[0].EventCode);
        Assert.AreEqual(child, replayed[0].U32(12), "event 窗口是子窗口");
        Assert.AreEqual(ButtonRelease, replayed[1].EventCode);
        Assert.IsEmpty(await DrainAsync(a, ButtonPress, ButtonRelease), "A 的抓取已经解除");
    }

    [TestMethod]
    public async Task 同步GrabKeyboard_SyncKeyboard每次放行一个按键事件()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host);

        XMessage grab = await c.RequestAsync(31, 0, b => b.U32(top).U32(0).U8(Asynchronous).U8(Synchronous).U16(0));
        Assert.AreEqual(0, grab.Bytes[1], "GrabSuccess");
        server.InjectKey(38, pressed: true);
        server.InjectKey(38, pressed: false);
        server.InjectKey(39, pressed: true);
        Assert.IsEmpty(await DrainAsync(c, KeyPress, KeyRelease), "键盘冻着");

        await c.SendAsync(35, 4, b => b.U32(0));   // SyncKeyboard
        List<XMessage> one = await DrainAsync(c, KeyPress, KeyRelease);
        Assert.HasCount(1, one, "只放行到下一个按键事件为止");
        Assert.AreEqual(38, one[0].Bytes[1]);

        await c.SendAsync(35, 3, b => b.U32(0));   // AsyncKeyboard
        List<XMessage> rest = await DrainAsync(c, KeyPress, KeyRelease);
        Assert.HasCount(2, rest);
        Assert.AreEqual(KeyRelease, rest[0].EventCode);
        Assert.AreEqual(39, rest[1].Bytes[1]);
    }

    [TestMethod]
    public async Task UngrabPointer解冻_排着的事件照常送达()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host, eventMask: 0x40);
        server.InjectPointerMotion(host.Mapped[top], 1, 1);
        await DrainAsync(c, MotionNotify);

        await c.RequestAsync(26, 0, b => b.U32(top).U16(0x40).U8(Synchronous).U8(Asynchronous).U32(0).U32(0).U32(0));
        server.InjectPointerMotion(host.Mapped[top], 30, 30);
        Assert.IsEmpty(await DrainAsync(c, MotionNotify));
        await c.SendAsync(27, 0, b => b.U32(0));   // UngrabPointer
        Assert.HasCount(1, await DrainAsync(c, MotionNotify), "抓取解除,冻结随之解除,事件按普通选择送达");
    }
}
