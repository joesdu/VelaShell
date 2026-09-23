using System.Text;
using VelaShell.XServer.Host;
using VelaShell.XServer.Server;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>DAMAGE、Composite、DOUBLE-BUFFER、SYNC、Present。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class SyncCompositeTests
{
    private static async Task<(byte Major, byte Event, byte Error)> ExtAsync(XTestClient c, string name)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(name);
        XMessage q = await c.RequestAsync(98, 0, b => b.U16((ushort)bytes.Length).U16(0).Bytes(bytes).Pad());
        Assert.AreEqual(1, q.Bytes[8], $"{name} 应当存在");
        return (q.Bytes[9], q.Bytes[10], q.Bytes[11]);
    }

    private static async Task<uint> MapTopAsync(XTestClient c, RecordingHost host, uint background = 0xFFFFFF)
    {
        uint id = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(id).U32(c.RootWindow).I16(0).I16(0).U16(40).U16(30).U16(0).U16(1).U32(0)
            .U32(0x2).U32(background));
        await c.SendAsync(8, 0, b => b.U32(id));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
        return id;
    }

    private static async Task<uint> GcAsync(XTestClient c, uint drawable, uint foreground)
    {
        uint gc = c.NewId();
        await c.SendAsync(55, 0, b => b.U32(gc).U32(drawable).U32(0x4).U32(foreground));
        return gc;
    }

    private static Task<ushort> FillAsync(XTestClient c, uint drawable, uint gc, short x, short y, ushort w, ushort h) =>
        c.SendAsync(70, 0, b => b.U32(drawable).U32(gc).I16(x).I16(y).U16(w).U16(h));

    [TestMethod]
    public async Task DAMAGE的NonEmpty只报一次_Subtract后再报()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte damage, byte damageEvent, _) = await ExtAsync(c, "DAMAGE");
        await c.RequestAsync(damage, 0, b => b.U32(1).U32(1));
        uint pixmap = c.NewId();
        await c.SendAsync(53, 24, b => b.U32(pixmap).U32(c.RootWindow).U16(50).U16(50));
        uint gc = await GcAsync(c, pixmap, 0xFF0000);
        uint id = c.NewId();
        await c.SendAsync(damage, 1, b => b.U32(id).U32(pixmap).U8(3).U8(0).U8(0).U8(0));   // NonEmpty

        await FillAsync(c, pixmap, gc, 5, 6, 10, 10);
        XMessage first = await c.NextEventAsync(damageEvent);
        Assert.AreEqual(3, first.Bytes[1] & 0x7F, "level = NonEmpty");
        Assert.AreEqual(5, first.I16(16), "area.x");
        await FillAsync(c, pixmap, gc, 20, 20, 5, 5);
        await c.SyncAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => c.NextEventAsync(damageEvent, timeoutMs: 150));

        await c.SendAsync(damage, 3, b => b.U32(id).U32(0).U32(0));   // Subtract(None):清空
        await FillAsync(c, pixmap, gc, 1, 1, 2, 2);
        XMessage again = await c.NextEventAsync(damageEvent);
        Assert.AreEqual(1, again.I16(16));
    }

    [TestMethod]
    public async Task DAMAGE在窗口上用窗口坐标报RawRectangles()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte damage, byte damageEvent, _) = await ExtAsync(c, "DAMAGE");
        uint top = await MapTopAsync(c, host);
        uint child = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(child).U32(top).I16(10).I16(10).U16(20).U16(15).U16(0).U16(1).U32(0).U32(0));
        await c.SendAsync(8, 0, b => b.U32(child));
        uint id = c.NewId();
        await c.SendAsync(damage, 1, b => b.U32(id).U32(child).U8(0).U8(0).U8(0).U8(0));   // Raw
        await c.SyncAsync();
        while (await Drain(c, damageEvent)) { }   // 映射时的背景绘制

        uint gc = await GcAsync(c, child, 0x00FF00);
        await FillAsync(c, child, gc, 2, 3, 4, 5);
        XMessage e = await c.NextEventAsync(damageEvent);
        Assert.AreEqual(child, e.U32(4));
        Assert.AreEqual(2, e.I16(16));
        Assert.AreEqual(3, e.I16(18));
        Assert.AreEqual(4, e.U16(20));

        static async Task<bool> Drain(XTestClient c, byte code)
        {
            try
            {
                await c.NextEventAsync(code, timeoutMs: 100);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }

    [TestMethod]
    public async Task Composite的NameWindowPixmap与顶层共享像素()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte composite, _, _) = await ExtAsync(c, "Composite");
        XMessage v = await c.RequestAsync(composite, 0, b => b.U32(0).U32(4));
        Assert.AreEqual(4u, v.U32(12));
        uint top = await MapTopAsync(c, host, 0x123456);
        uint pixmap = c.NewId();
        await c.SendAsync(composite, 6, b => b.U32(top).U32(pixmap));
        uint gc = await GcAsync(c, top, 0xABCDEF);
        await FillAsync(c, top, gc, 0, 0, 1, 1);
        // GetImage(ZPixmap) 像素图的 (0,0):应当看到刚画进窗口的颜色。
        XMessage image = await c.RequestAsync(73, 2, b => b.U32(pixmap).I16(0).I16(0).U16(1).U16(1).U32(0xFFFFFFFF));
        Assert.AreEqual(0xABCDEFu, image.U32(32) & 0xFFFFFF);
    }

    [TestMethod]
    public async Task DBE后缓冲画好后SwapBuffers才出现在窗口上()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte dbe, _, _) = await ExtAsync(c, "DOUBLE-BUFFER");
        uint top = await MapTopAsync(c, host, 0xFFFFFF);
        XTopLevelWindow handle = host.Mapped[top];
        uint back = c.NewId();
        await c.SendAsync(dbe, 1, b => b.U32(top).U32(back).U8(1).U8(0).U8(0).U8(0));
        uint gc = await GcAsync(c, back, 0xFF0000);
        await FillAsync(c, back, gc, 0, 0, 40, 30);
        await c.SyncAsync();
        Assert.AreEqual(0xFFFFFFu, RecordingHost.Snapshot(handle).Pixels[0] & 0xFFFFFF, "交换前窗口不变");

        await c.SendAsync(dbe, 3, b => b.U32(1).U32(top).U8(1).U8(0).U8(0).U8(0));   // Background
        await c.SyncAsync();
        Assert.AreEqual(0xFF0000u, RecordingHost.Snapshot(handle).Pixels[0] & 0xFFFFFF, "交换后窗口是后缓冲的内容");

        XMessage attrs = await c.RequestAsync(dbe, 7, b => b.U32(back));
        Assert.AreEqual(top, attrs.U32(8));
    }

    [TestMethod]
    public async Task SYNC的Await挡住后续请求_计数器变了才放行()
    {
        await using X11Server server = new();
        await using XTestClient waiter = await XTestClient.ConnectAsync(server);
        await using XTestClient setter = await XTestClient.ConnectAsync(server);
        (byte sync, byte syncEvent, _) = await ExtAsync(waiter, "SYNC");
        XMessage init = await waiter.RequestAsync(sync, 0, b => b.U8(3).U8(1).U16(0));
        Assert.AreEqual(3, init.Bytes[8]);

        uint counter = setter.NewId();
        await setter.SendAsync(sync, 2, b => b.U32(counter).I32(0).U32(0));   // CreateCounter = 0
        await setter.SyncAsync();

        // Await(counter ≥ 5)
        await waiter.SendAsync(sync, 7, b => b.U32(counter).U32(0).I32(0).U32(5).U32(2).I32(0).U32(0));
        Task<XMessage> blocked = waiter.RequestAsync(43, 0);   // GetInputFocus:Await 期间不该有回复
        await Task.Delay(150);
        Assert.IsFalse(blocked.IsCompleted, "Await 期间后续请求应当被挡住");

        await setter.SendAsync(sync, 3, b => b.U32(counter).I32(0).U32(7));   // SetCounter = 7
        await blocked.WaitAsync(TimeSpan.FromSeconds(5));
        XMessage notify = await waiter.NextEventAsync(syncEvent);
        Assert.AreEqual(counter, notify.U32(4));
        Assert.AreEqual(7u, notify.U32(20), "counter_value 低 32 位");
    }

    [TestMethod]
    public async Task IDLETIME上的负向跨越报警器在用户输入时触发()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte sync, byte syncEvent, _) = await ExtAsync(c, "SYNC");
        uint top = await MapTopAsync(c, host);

        // ListSystemCounters:每项 = counter、resolution(8 字节)、名字长度、名字,整项按 4 字节补齐。
        XMessage list = await c.RequestAsync(sync, 1);
        uint idle = 0;
        for (int i = 0, offset = 32; i < (int)list.U32(8); i++)
        {
            int length = list.U16(offset + 12);
            if (Encoding.Latin1.GetString(list.Bytes, offset + 14, length) == "IDLETIME")
            {
                idle = list.U32(offset);
            }
            offset += (14 + length + 3) & ~3;
        }
        Assert.AreNotEqual(0u, idle, "有 IDLETIME 计数器");

        // 空闲超过 50 毫秒之后又有了输入:IDLETIME 从 50 以上掉回 0,NegativeTransition(1)成立。
        uint alarm = c.NewId();
        await c.SendAsync(sync, 9, b => b.U32(alarm).U32(1 | 2 | 4 | 8).U32(idle).U32(0).I32(0).U32(50).U32(1));
        await c.SyncAsync();
        await Task.Delay(150);
        server.PointerMotion(top, 3, 3);
        XMessage fired = await c.NextEventAsync((byte)(syncEvent + 1));
        Assert.AreEqual(alarm, fired.U32(4));
    }

    [TestMethod]
    public async Task SYNC报警器触发后按delta推进_栅栏可查询()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte sync, byte syncEvent, _) = await ExtAsync(c, "SYNC");
        uint counter = c.NewId(), alarm = c.NewId();
        await c.SendAsync(sync, 2, b => b.U32(counter).I32(0).U32(0));
        // CreateAlarm:counter、value-type Absolute、value 10、PositiveComparison、delta 10
        await c.SendAsync(sync, 9, b => b.U32(alarm).U32(1 | 2 | 4 | 8 | 16)
            .U32(counter).U32(0).I32(0).U32(10).U32(2).I32(0).U32(10));
        await c.SendAsync(sync, 3, b => b.U32(counter).I32(0).U32(12));
        XMessage fired = await c.NextEventAsync((byte)(syncEvent + 1));
        Assert.AreEqual(alarm, fired.U32(4));
        XMessage query = await c.RequestAsync(sync, 10, b => b.U32(alarm));
        Assert.AreEqual(20u, query.U32(20), "等待值推进到 20");

        uint fence = c.NewId();
        await c.SendAsync(sync, 14, b => b.U32(c.RootWindow).U32(fence).U8(0).U8(0).U8(0).U8(0));
        Assert.AreEqual(0, (await c.RequestAsync(sync, 18, b => b.U32(fence))).Bytes[8]);
        await c.SendAsync(sync, 15, b => b.U32(fence));
        Assert.AreEqual(1, (await c.RequestAsync(sync, 18, b => b.U32(fence))).Bytes[8]);
    }

    [TestMethod]
    public async Task SYNC的IDLETIME报警器到点触发()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte sync, byte syncEvent, _) = await ExtAsync(c, "SYNC");
        XMessage list = await c.RequestAsync(sync, 1);
        Assert.AreEqual(2u, list.U32(8));
        uint idle = 0;
        int offset = 32;
        for (int i = 0; i < 2; i++)
        {
            uint id = list.U32(offset);
            int nameLength = list.U16(offset + 12);
            string name = Encoding.Latin1.GetString(list.Bytes, offset + 14, nameLength);
            if (name == "IDLETIME")
            {
                idle = id;
            }
            offset += (14 + nameLength + 3) & ~3;
        }
        Assert.AreNotEqual(0u, idle);

        server.Key(38, true);
        server.Key(38, false);
        await c.SyncAsync();
        uint alarm = c.NewId();
        // 空闲 ≥ 当前 + 200 ms 时触发(Relative)。
        await c.SendAsync(sync, 9, b => b.U32(alarm).U32(1 | 2 | 4 | 8 | 16)
            .U32(idle).U32(1).I32(0).U32(200).U32(2).I32(0).U32(0));
        XMessage fired = await c.NextEventAsync((byte)(syncEvent + 1), timeoutMs: 3000);
        Assert.AreEqual(alarm, fired.U32(4));
    }

    [TestMethod]
    public async Task Present把像素图拷到窗口并报完成与空闲()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        _ = await ExtAsync(c, "Generic Event Extension");
        (byte present, _, _) = await ExtAsync(c, "Present");
        uint top = await MapTopAsync(c, host);
        uint pixmap = c.NewId();
        await c.SendAsync(53, 24, b => b.U32(pixmap).U32(top).U16(40).U16(30));
        uint gc = await GcAsync(c, pixmap, 0x0000FF);
        await FillAsync(c, pixmap, gc, 0, 0, 40, 30);
        uint eid = c.NewId();
        await c.SendAsync(present, 3, b => b.U32(eid).U32(top).U32(2 | 4));

        await c.SendAsync(present, 1, b => b.U32(top).U32(pixmap).U32(77).U32(0).U32(0).I16(0).I16(0)
            .U32(0).U32(0).U32(0).U32(0).U32(0).U32(0).U32(0).U32(0).U32(0).U32(0).U32(0));
        XMessage complete = await c.NextAsync(m => !m.IsReply && !m.IsError && m.EventCode == 35 && m.U16(8) == 1);
        Assert.AreEqual(present, complete.Bytes[1], "GenericEvent 的扩展号");
        Assert.AreEqual(77u, complete.U32(20), "serial");
        XMessage idle = await c.NextAsync(m => !m.IsReply && !m.IsError && m.EventCode == 35 && m.U16(8) == 2);
        Assert.AreEqual(pixmap, idle.U32(24));
        Assert.AreEqual(0x0000FFu, RecordingHost.Snapshot(host.Mapped[top]).Pixels[0] & 0xFFFFFF);
    }
}
