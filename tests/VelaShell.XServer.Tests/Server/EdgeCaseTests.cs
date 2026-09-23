using System.Text;
using VelaShell.XServer.Server;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>协议里容易漏的细节:移动提示的节流、Enter / Leave 的 child、根窗口上的 Damage、极端参数不拖垮服务端。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class EdgeCaseTests
{
    private const byte MotionNotify = 6, EnterNotify = 7;

    private static async Task<uint> MapTopAsync(XTestClient c, RecordingHost host, short x, short y, uint eventMask)
    {
        uint id = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(id).U32(c.RootWindow).I16(x).I16(y).U16(60).U16(40).U16(0).U16(1).U32(0)
            .U32(0x800).U32(eventMask));
        await c.SendAsync(8, 0, b => b.U32(id));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
        return id;
    }

    /// <summary>把已经到了的某种事件数一遍(先 Sync,保证之前的请求与注入都执行完了)。</summary>
    private static async Task<List<XMessage>> DrainAsync(XTestClient c, byte code)
    {
        await c.SyncAsync();
        List<XMessage> events = [];
        while (true)
        {
            try
            {
                events.Add(await c.NextEventAsync(code, timeoutMs: 50));
            }
            catch (OperationCanceledException)
            {
                return events;
            }
        }
    }

    [TestMethod]
    public async Task PointerMotionHint每轮只发一条提示_QueryPointer之后再给一条()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host, 0, 0, 0x40 | 0x80);   // PointerMotion | PointerMotionHint
        server.PointerMotion(top, 1, 1);
        server.PointerMotion(top, 2, 2);
        server.PointerMotion(top, 3, 3);
        List<XMessage> hints = await DrainAsync(c, MotionNotify);
        Assert.HasCount(1, hints, "同一轮里只发一条");
        Assert.AreEqual(1, hints[0].Bytes[1], "detail = Hint");

        server.PointerMotion(top, 4, 4);
        Assert.IsEmpty(await DrainAsync(c, MotionNotify), "客户端还没来问位置:不再发");

        await c.RequestAsync(38, 0, b => b.U32(top));   // QueryPointer
        server.PointerMotion(top, 5, 5);
        server.PointerMotion(top, 6, 6);
        Assert.HasCount(1, await DrainAsync(c, MotionNotify), "问过位置之后再给一条");
    }

    [TestMethod]
    public async Task 指针直接进入子窗口时父窗口的EnterNotify带child()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host, 0, 0, 0x10);   // EnterWindow
        uint child = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(child).U32(top).I16(20).I16(10).U16(20).U16(20).U16(0).U16(1).U32(0).U32(0));
        await c.SendAsync(8, 0, b => b.U32(child));
        server.PointerMotion(top, 100, 100);   // 先移到窗口外
        await DrainAsync(c, EnterNotify);      // 映射时指针可能已在窗口里:那条 Enter 不算

        server.PointerMotion(top, 25, 15);   // 从窗口外直接落进子窗口
        XMessage enter = await c.NextAsync(m => !m.IsReply && !m.IsError && m.EventCode == EnterNotify && m.U32(12) == top);
        Assert.AreEqual(child, enter.U32(16), "child = 通往指针所在窗口的那个子窗口");
    }

    [TestMethod]
    public async Task 根窗口上的Damage收到顶层窗口里画的内容_坐标换到根坐标()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte[] name = Encoding.Latin1.GetBytes("DAMAGE");
        XMessage q = await c.RequestAsync(98, 0, b => b.U16((ushort)name.Length).U16(0).Bytes(name).Pad());
        (byte damage, byte damageEvent) = (q.Bytes[9], q.Bytes[10]);
        await c.RequestAsync(damage, 0, b => b.U32(1).U32(1));
        uint top = await MapTopAsync(c, host, 10, 20, 0);
        uint gc = c.NewId();
        await c.SendAsync(55, 0, b => b.U32(gc).U32(top).U32(0x4).U32(0xFF0000));
        await c.SyncAsync();

        uint id = c.NewId();
        await c.SendAsync(damage, 1, b => b.U32(id).U32(c.RootWindow).U8(0).U8(0).U8(0).U8(0));   // RawRectangles
        await c.SendAsync(70, 0, b => b.U32(top).U32(gc).I16(5).I16(6).U16(7).U16(8));
        XMessage e = await c.NextAsync(m => !m.IsReply && !m.IsError && m.EventCode == damageEvent && m.U32(4) == c.RootWindow);
        Assert.AreEqual((15, 26, 7, 8), (e.I16(16), e.I16(18), e.U16(20), e.U16(22)));
    }

    [TestMethod]
    public async Task 巨大的PolyFillArc只扫可画的行_很快完成()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host, 0, 0, 0);
        uint gc = c.NewId();
        await c.SendAsync(55, 0, b => b.U32(gc).U32(top).U32(0x4).U32(0xFF0000));
        // 以前:65535 行 × 二十万条边,一个请求就能把执行线程占住几十秒。
        await c.SendAsync(71, 0, b => b.U32(top).U32(gc).I16(-32000).I16(-32000).U16(65535).U16(65535).I16(0).I16(360 * 64));
        Task sync = c.SyncAsync();
        Assert.AreSame(sync, await Task.WhenAny(sync, Task.Delay(TimeSpan.FromSeconds(3))), "三秒内完成");
        (uint[] pixels, int width, _) = RecordingHost.Snapshot(host.Mapped[top]);
        Assert.AreEqual(0xFF0000u, pixels[(20 * width) + 30] & 0xFFFFFF, "整个窗口都在圆里");
    }

    [TestMethod]
    public async Task RotateProperties列表里有重复的名字是BadMatch()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint window = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(window).U32(c.RootWindow).I16(0).I16(0).U16(10).U16(10).U16(0).U16(1).U32(0).U32(0));
        await c.SendAsync(18, 0, b => b.U32(window).U32(39).U32(31).U8(8).U8(0).U8(0).U8(0).U32(1).U8((byte)'a').Pad());   // WM_NAME
        XMessage error = await c.RequestAsync(114, 0, b => b.U32(window).U16(2).I16(1).U32(39).U32(39));
        Assert.IsTrue(error.IsError);
        Assert.AreEqual(8, error.Bytes[1], "BadMatch");
    }
}
