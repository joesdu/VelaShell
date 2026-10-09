using System.Text;
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
    public async Task FreePixmap之后DamageDestroy照常成功()
    {
        // xeyes 用 Present 换帧:先 FreePixmap,再 DamageDestroy。提前销毁 Damage 会让后一条回 BadDamage、客户端退出。
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte damage, _, _) = await ExtAsync(c, "DAMAGE");
        await c.RequestAsync(damage, 0, b => b.U32(1).U32(1));
        uint pixmap = c.NewId();
        await c.SendAsync(53, 24, b => b.U32(pixmap).U32(c.RootWindow).U16(8).U16(8));
        uint id = c.NewId();
        await c.SendAsync(damage, 1, b => b.U32(id).U32(pixmap).U8(3).U8(0).U8(0).U8(0));
        await c.SendAsync(54, 0, b => b.U32(pixmap));        // FreePixmap
        await c.SendAsync(damage, 2, b => b.U32(id));         // DamageDestroy
        await c.SyncAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => c.NextAsync(m => m.IsError, timeoutMs: 150), "不该有 BadDamage");
    }

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
    public async Task NameWindowPixmap在窗口改尺寸后保持原来的尺寸与内容_往里画宿主收到损伤()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte composite, _, _) = await ExtAsync(c, "Composite");
        uint top = await MapTopAsync(c, host, 0x123456);
        uint pixmap = c.NewId();
        await c.SendAsync(composite, 6, b => b.U32(top).U32(pixmap));
        await c.SyncAsync();

        // 画进像素图就是画进了顶层(还共享着缓冲):宿主要收到损伤,不然原生窗口不重画。
        int before = host.Log.Count(e => e.StartsWith($"damaged {top:x}", StringComparison.Ordinal));
        await FillAsync(c, pixmap, await GcAsync(c, pixmap, 0xABCDEF), 0, 0, 4, 4);
        await host.WaitForAsync(() => host.Log.Count(e => e.StartsWith($"damaged {top:x}", StringComparison.Ordinal)) > before);
        Assert.AreEqual(0xABCDEFu, RecordingHost.Snapshot(host.Mapped[top]).Pixels[0] & 0xFFFFFF);

        // 窗口改成 60×50:像素图保持 40×30 与原来的内容(规范:窗口换一个新的像素图,旧的一直有效到释放)。
        await c.SendAsync(12, 0, b => b.U32(top).U16(0xC).U16(0).U32(60).U32(50));
        await FillAsync(c, top, await GcAsync(c, top, 0x00FF00), 0, 0, 60, 50);
        XMessage geometry = await c.RequestAsync(14, 0, b => b.U32(pixmap));
        Assert.AreEqual("40×30", $"{geometry.U16(16)}×{geometry.U16(18)}", "像素图的尺寸不跟着窗口变");
        XMessage image = await c.RequestAsync(73, 2, b => b.U32(pixmap).I16(0).I16(0).U16(1).U16(1).U32(0xFFFFFFFF));
        Assert.AreEqual(0xABCDEFu, image.U32(32) & 0xFFFFFF, "之后画进窗口的不进旧像素图");
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
    public async Task DBE的Background交换按背景像素图与ParentRelative铺_同一窗口列两次回BadMatch()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte dbe, _, _) = await ExtAsync(c, "DOUBLE-BUFFER");

        // 背景是 2×1 的像素图(红、蓝)的顶层,里面一个背景 ParentRelative 的子窗口。
        uint tile = c.NewId();
        await c.SendAsync(53, 24, b => b.U32(tile).U32(c.RootWindow).U16(2).U16(1));
        await FillAsync(c, tile, await GcAsync(c, tile, 0xFF0000), 0, 0, 1, 1);
        await FillAsync(c, tile, await GcAsync(c, tile, 0x0000FF), 1, 0, 1, 1);
        uint top = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(top).U32(c.RootWindow).I16(0).I16(0).U16(40).U16(30).U16(0).U16(1).U32(0).U32(0x1).U32(tile));
        await c.SendAsync(8, 0, b => b.U32(top));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(top));
        uint child = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(child).U32(top).I16(5).I16(3).U16(10).U16(10).U16(0).U16(1).U32(0).U32(0x1).U32(1));   // ParentRelative
        await c.SendAsync(8, 0, b => b.U32(child));

        uint topBack = c.NewId(), childBack = c.NewId();
        await c.SendAsync(dbe, 1, b => b.U32(top).U32(topBack).U8(1).U8(0).U8(0).U8(0));
        await c.SendAsync(dbe, 1, b => b.U32(child).U32(childBack).U8(1).U8(0).U8(0).U8(0));
        await c.SendAsync(dbe, 3, b => b.U32(2).U32(top).U8(1).U8(0).U8(0).U8(0).U32(child).U8(1).U8(0).U8(0).U8(0));   // 两个都 Background

        XMessage topImage = await c.RequestAsync(73, 2, b => b.U32(topBack).I16(0).I16(0).U16(2).U16(1).U32(0xFFFFFFFF));
        Assert.AreEqual((0xFF0000u, 0x0000FFu), (topImage.U32(32) & 0xFFFFFF, topImage.U32(36) & 0xFFFFFF), "按背景像素图平铺");
        // 子窗口在 (5,3):它的 (0,0) 对着顶层的 x = 5,平铺原点跟着顶层走 —— 是蓝的。
        XMessage childImage = await c.RequestAsync(73, 2, b => b.U32(childBack).I16(0).I16(0).U16(2).U16(1).U32(0xFFFFFFFF));
        Assert.AreEqual((0x0000FFu, 0xFF0000u), (childImage.U32(32) & 0xFFFFFF, childImage.U32(36) & 0xFFFFFF), "ParentRelative 用父窗口的背景");

        ushort twice = await c.SendAsync(dbe, 3, b => b.U32(2).U32(top).U8(1).U8(0).U8(0).U8(0).U32(top).U8(0).U8(0).U8(0).U8(0));
        XMessage error = await c.NextAsync(m => m.IsError && m.Sequence == twice);
        Assert.AreEqual(8, error.Detail, "同一窗口列两次:BadMatch");
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
    public async Task 被Await挂住的请求按字节计入背压_攒到上限就不再读()
    {
        await using X11Server server = new();
        await using XTestClient waiter = await XTestClient.ConnectAsync(server);
        await using XTestClient setter = await XTestClient.ConnectAsync(server);
        (byte sync, _, _) = await ExtAsync(waiter, "SYNC");
        await waiter.RequestAsync(sync, 0, b => b.U8(3).U8(1).U16(0));
        (byte bigRequests, _, _) = await ExtAsync(waiter, "BIG-REQUESTS");
        await waiter.RequestAsync(bigRequests, 0);   // BigReqEnable
        uint counter = setter.NewId();
        await setter.SendAsync(sync, 2, b => b.U32(counter).I32(0).U32(0));
        await setter.SyncAsync();
        await waiter.SendAsync(sync, 7, b => b.U32(counter).U32(0).I32(0).U32(5).U32(2).I32(0).U32(0));   // Await(counter ≥ 5)

        // 48 条 1 MB 的 NoOperation:只数条数(1024)的话全都读进来挂着;按字节算,攒到 32 MB 读端就停。
        byte[] payload = new byte[1 << 20];
        var flood = Task.Run(async () =>
        {
            for (int i = 0; i < 48; i++)
            {
                await waiter.SendAsync(127, 0, b => b.Bytes(payload), bigRequest: true);
            }
        });
        await Task.Delay(500);
        Assert.IsFalse(flood.IsCompleted, "挂住的请求攒到字节上限,读端不再读,客户端写不进去");

        await setter.SendAsync(sync, 3, b => b.U32(counter).I32(0).U32(7));   // 放行
        await flood.WaitAsync(TimeSpan.FromSeconds(10));
        XMessage focus = await waiter.RequestAsync(43, 0);
        Assert.IsTrue(focus.IsReply, "放行之后一条条照常执行完");
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
        server.InjectPointerMotion(host.Mapped[top], 3, 3);
        XMessage fired = await c.NextEventAsync((byte)(syncEvent + 1));
        Assert.AreEqual(alarm, fired.U32(4));
    }

    [TestMethod]
    public async Task 几千个报警器的建立与销毁是线性的_销毁的不再触发()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte sync, byte syncEvent, _) = await ExtAsync(c, "SYNC");
        uint counter = c.NewId();
        await c.SendAsync(sync, 2, b => b.U32(counter).I32(0).U32(0));
        const int count = 4000;
        uint[] alarms = [.. Enumerable.Range(0, count).Select(_ => c.NewId())];
        var watch = System.Diagnostics.Stopwatch.StartNew();
        // 等待值 100 万、PositiveComparison:一个都不会触发,但每建一个都要整轮求值一遍。原先每个报警器还要在列表里 Contains 一遍。
        await c.SendManyAsync(alarms.Select<uint, (byte, byte, Action<XTestClient.Body>?)>(id => (sync, 9, b => b.U32(id).U32(1 | 2 | 4 | 8 | 16)
            .U32(counter).U32(0).I32(0).U32(1_000_000).U32(2).I32(0).U32(1))));
        await c.SendManyAsync(alarms.Take(count - 1).Select<uint, (byte, byte, Action<XTestClient.Body>?)>(id => (sync, 11, b => b.U32(id))));
        await c.SyncAsync();
        Assert.IsLessThan(5_000, watch.ElapsedMilliseconds);

        await c.SendAsync(sync, 3, b => b.U32(counter).I32(0).U32(2_000_000));   // SetCounter:只剩最后一个报警器会触发
        XMessage fired = await c.NextAsync(m => !m.IsReply && !m.IsError && m.EventCode == syncEvent + 1 && m.Bytes[28] == 0);
        Assert.AreEqual(alarms[^1], fired.U32(4), "销毁了的报警器不触发");
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
    public async Task 比较型报警器一次跳过很远时按delta一步算到位_不因推进次数多而停用()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte sync, byte syncEvent, _) = await ExtAsync(c, "SYNC");
        uint counter = c.NewId(), alarm = c.NewId();
        await c.SendAsync(sync, 2, b => b.U32(counter).I32(0).U32(0));
        // PositiveComparison、等待值 10、delta 7;计数器一下跳到 10¹²(hi 232、lo 3567587328)。
        // 原先一次加 7、加到一百万次就把报警器停用;规范只在溢出时停用。
        await c.SendAsync(sync, 9, b => b.U32(alarm).U32(1 | 2 | 4 | 8 | 16).U32(counter).U32(0).I32(0).U32(10).U32(2).I32(0).U32(7));
        await c.SendAsync(sync, 3, b => b.U32(counter).I32(232).U32(3567587328));
        XMessage fired = await c.NextEventAsync((byte)(syncEvent + 1));
        Assert.AreEqual(alarm, fired.U32(4));

        XMessage query = await c.RequestAsync(sync, 10, b => b.U32(alarm));
        long wait = ((long)query.U32(16) << 32) | query.U32(20);
        const long target = 1_000_000_000_000L;
        Assert.AreEqual(10 + ((((target - 10) / 7) + 1) * 7), wait, "第一个大于计数器的 10 + 7k");
        Assert.AreEqual(0, query.Bytes[37], "仍是 Active");
    }

    [TestMethod]
    public async Task CreateAlarm不给test_type时默认是PositiveComparison()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte sync, byte syncEvent, _) = await ExtAsync(c, "SYNC");
        uint counter = c.NewId(), alarm = c.NewId();
        await c.SendAsync(sync, 2, b => b.U32(counter).I32(0).U32(5));
        // 只给 counter 与 value(5):计数器已经是 5。PositiveComparison 立即成立;原先默认 PositiveTransition,永远等不到。
        await c.SendAsync(sync, 9, b => b.U32(alarm).U32(1 | 4).U32(counter).I32(0).U32(5));
        XMessage fired = await c.NextEventAsync((byte)(syncEvent + 1));
        Assert.AreEqual(alarm, fired.U32(4));
        XMessage query = await c.RequestAsync(sync, 10, b => b.U32(alarm));
        Assert.AreEqual(2u, query.U32(24), "test-type = PositiveComparison");
    }

    [TestMethod]
    public async Task Relative的报警器保留value_type_只改value时仍按相对值解释()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte sync, byte syncEvent, _) = await ExtAsync(c, "SYNC");
        uint counter = c.NewId(), alarm = c.NewId();
        await c.SendAsync(sync, 2, b => b.U32(counter).I32(0).U32(100));
        // Relative(1)、value 10、PositiveComparison、delta 0:计数器 100 → 测试值 110。
        await c.SendAsync(sync, 9, b => b.U32(alarm).U32(1 | 2 | 4 | 8 | 16).U32(counter).U32(1).I32(0).U32(10).U32(2).I32(0).U32(0));
        XMessage query = await c.RequestAsync(sync, 10, b => b.U32(alarm));
        Assert.AreEqual((1u, 10u), (query.U32(12), query.U32(20)), "QueryAlarm 报客户端给的 value-type 与 value");

        // 只改 value(20):重新初始化时仍是 Relative —— 测试值 120;原先被当成绝对值 20,计数器 100 立即满足。
        await c.SendAsync(sync, 8, b => b.U32(alarm).U32(4).I32(0).U32(20));
        await c.SendAsync(sync, 3, b => b.U32(counter).I32(0).U32(115));
        await c.SyncAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => c.NextEventAsync((byte)(syncEvent + 1), timeoutMs: 150), "115 < 120,不触发");
        await c.SendAsync(sync, 3, b => b.U32(counter).I32(0).U32(120));
        XMessage fired = await c.NextEventAsync((byte)(syncEvent + 1));
        Assert.AreEqual(120u, fired.U32(20), "alarm-value 是测试值 120");

        // counter 为 None 时没有「相对于谁」:BadMatch。
        ushort bad = await c.SendAsync(sync, 9, b => b.U32(c.NewId()).U32(2).U32(1));
        Assert.AreEqual(8, (await c.NextAsync(m => m.IsError && m.Sequence == bad)).Detail);
    }

    /// <summary>Await 的一个 WAITCONDITION:counter、value-type Absolute、wait-value、test-type、event-threshold。</summary>
    private static XTestClient.Body Condition(XTestClient.Body b, uint counter, int waitValue, uint testType, int threshold) =>
        b.U32(counter).U32(0).I32(waitValue < 0 ? -1 : 0).I32(waitValue).U32(testType).I32(threshold < 0 ? -1 : 0).I32(threshold);

    [TestMethod]
    public async Task Await按event_threshold决定发不发CounterNotify_一开始就成立也查()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte sync, byte syncEvent, _) = await ExtAsync(c, "SYNC");
        uint a = c.NewId(), b2 = c.NewId();
        await c.SendAsync(sync, 2, b => b.U32(a).I32(0).U32(7));
        await c.SendAsync(sync, 2, b => b.U32(b2).I32(0).U32(0));

        // a ≥ 5 已经成立、差值 2:阈值 3 时不发,阈值 0 时发 —— 一开始就成立也要查(原先这时什么都不发)。
        await c.SendAsync(sync, 7, b => Condition(b, a, 5, 2, 3));
        await c.SyncAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => c.NextEventAsync(syncEvent, timeoutMs: 150), "差值 2 < 阈值 3");

        // 两个条件:a ≥ 5(阈值 0)成立;b ≥ 100 不成立,但差值 −100 不小于阈值 −1000,照样发。两条连着发,count 1、0。
        await c.SendAsync(sync, 7, b => Condition(Condition(b, a, 5, 2, 0), b2, 100, 2, -1000));
        XMessage first = await c.NextEventAsync(syncEvent);
        XMessage second = await c.NextEventAsync(syncEvent);
        Assert.AreEqual(a, first.U32(4));
        Assert.AreEqual(1, first.U16(28), "count:后面还有一条");
        Assert.AreEqual(b2, second.U32(4));
        Assert.AreEqual(0, second.U16(28));
    }

    [TestMethod]
    public async Task AlarmNotify报的是更新之后的状态_计数器销毁时报Inactive()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte sync, byte syncEvent, _) = await ExtAsync(c, "SYNC");
        uint counter = c.NewId(), once = c.NewId(), alarm = c.NewId();
        await c.SendAsync(sync, 2, b => b.U32(counter).I32(0).U32(0));
        // delta = 0 的 PositiveComparison:触发之后就停用 —— 事件里应当已经是 Inactive(1)。
        await c.SendAsync(sync, 9, b => b.U32(once).U32(1 | 4 | 8 | 16).U32(counter).I32(0).U32(5).U32(2).I32(0).U32(0));
        await c.SendAsync(sync, 3, b => b.U32(counter).I32(0).U32(5));
        XMessage fired = await c.NextAsync(m => !m.IsError && !m.IsReply && m.EventCode == syncEvent + 1 && m.U32(4) == once);
        Assert.AreEqual(1, fired.Bytes[28], "state = Inactive");

        // 计数器被销毁:挂着它的报警器进入 Inactive,并报一条 AlarmNotify。
        await c.SendAsync(sync, 9, b => b.U32(alarm).U32(1 | 4).U32(counter).I32(0).U32(1000));
        await c.SendAsync(sync, 6, b => b.U32(counter));
        XMessage gone = await c.NextAsync(m => !m.IsError && !m.IsReply && m.EventCode == syncEvent + 1 && m.U32(4) == alarm);
        Assert.AreEqual(1, gone.Bytes[28], "state = Inactive");
    }

    [TestMethod]
    public async Task Await列表为空回BadValue_AwaitFence列表为空与counter为None时不挂住_栅栏销毁时放行()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        await using XTestClient other = await XTestClient.ConnectAsync(server);
        (byte sync, _, _) = await ExtAsync(c, "SYNC");

        ushort empty = await c.SendAsync(sync, 7);
        Assert.AreEqual(2, (await c.NextAsync(m => m.IsError && m.Sequence == empty)).Detail, "Await 的 wait-list 为空:BadValue");
        await c.SendAsync(sync, 19);   // AwaitFence,空列表
        await c.SyncAsync().WaitAsync(TimeSpan.FromSeconds(2));
        // counter 为 None 的触发器永远为真(规范 TRIGGER)。
        await c.SendAsync(sync, 7, b => Condition(b, 0, 0, 2, 0));
        await c.SyncAsync().WaitAsync(TimeSpan.FromSeconds(2));

        // 别的客户端等着的栅栏被销毁:放行(规范 DestroyFence)。
        uint fence = c.NewId();
        await c.SendAsync(sync, 14, b => b.U32(c.RootWindow).U32(fence).U8(0).U8(0).U8(0).U8(0));
        await c.SyncAsync();
        await other.SendAsync(sync, 19, b => b.U32(fence));
        Task<XMessage> blocked = other.RequestAsync(43, 0);
        await Task.Delay(100);
        Assert.IsFalse(blocked.IsCompleted, "栅栏没触发时挡住");
        await c.SendAsync(sync, 17, b => b.U32(fence));   // DestroyFence
        await blocked.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task 窗口挪到另一个顶层之后它的Damage照常报_同一可绘对象上的Damage有上限()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte damage, byte damageEvent, _) = await ExtAsync(c, "DAMAGE");
        await c.RequestAsync(damage, 0, b => b.U32(1).U32(1));
        uint first = await MapTopAsync(c, host), second = await MapTopAsync(c, host);
        uint child = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(child).U32(first).I16(0).I16(0).U16(10).U16(10).U16(0).U16(1).U32(0).U32(0));
        await c.SendAsync(8, 0, b => b.U32(child));
        await c.SendAsync(damage, 1, b => b.U32(c.NewId()).U32(child).U8(0).U8(0).U8(0).U8(0));   // Raw
        uint gc = await GcAsync(c, first, 0xFF0000);
        await FillAsync(c, child, gc, 1, 1, 2, 2);
        Assert.AreEqual(child, (await c.NextEventAsync(damageEvent)).U32(4));

        // 挪进另一个顶层(按顶层建的索引要跟着变),再画。
        await c.SendAsync(7, 0, b => b.U32(child).U32(second).I16(5).I16(5));   // ReparentWindow
        await c.SendAsync(8, 0, b => b.U32(child));
        await c.SyncAsync();
        while (await DrainAsync(c, damageEvent)) { }
        await FillAsync(c, child, gc, 3, 3, 2, 2);
        XMessage moved = await c.NextEventAsync(damageEvent);
        Assert.AreEqual(child, moved.U32(4));
        Assert.AreEqual(3, moved.I16(16));

        ushort last = await c.SendManyAsync(Enumerable.Range(0, X11Server.MaxDamagePerDrawable).Select<int, (byte, byte, Action<XTestClient.Body>?)>(_ =>
            (damage, 1, b => b.U32(c.NewId()).U32(child).U8(3).U8(0).U8(0).U8(0))));
        Assert.AreEqual(11, (await c.NextAsync(m => m.IsError && m.Sequence == last)).Detail, "第 257 个:BadAlloc");

        static async Task<bool> DrainAsync(XTestClient c, byte code)
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
    public async Task DamageSubtract之后按级别逐块重报剩下的损伤()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte damage, byte damageEvent, _) = await ExtAsync(c, "DAMAGE");
        (byte xfixes, _, _) = await ExtAsync(c, "XFIXES");
        await c.RequestAsync(damage, 0, b => b.U32(1).U32(1));
        await c.RequestAsync(xfixes, 0, b => b.U32(5).U32(0));
        uint pixmap = c.NewId();
        await c.SendAsync(53, 24, b => b.U32(pixmap).U32(c.RootWindow).U16(50).U16(50));
        uint gc = await GcAsync(c, pixmap, 0xFF0000);
        uint id = c.NewId();
        await c.SendAsync(damage, 1, b => b.U32(id).U32(pixmap).U8(1).U8(0).U8(0).U8(0));   // DeltaRectangles
        await FillAsync(c, pixmap, gc, 0, 0, 2, 2);
        await FillAsync(c, pixmap, gc, 10, 10, 2, 2);
        await c.NextEventAsync(damageEvent);
        await c.NextEventAsync(damageEvent);

        // repair 与两块都不相交:剩下的两块应当各报一条(more 串起来),原先只报一条外接矩形 (0,0) 12×12。
        uint repair = c.NewId();
        await c.SendAsync(xfixes, 5, b => b.U32(repair).I16(30).I16(30).U16(1).U16(1));
        await c.SendAsync(damage, 3, b => b.U32(id).U32(repair).U32(0));
        XMessage first = await c.NextEventAsync(damageEvent);
        XMessage second = await c.NextEventAsync(damageEvent);
        Assert.AreEqual("0,0 2×2 more", $"{first.I16(16)},{first.I16(18)} {first.U16(20)}×{first.U16(22)} {((first.Bytes[1] & 0x80) != 0 ? "more" : "last")}");
        Assert.AreEqual("10,10 2×2 last", $"{second.I16(16)},{second.I16(18)} {second.U16(20)}×{second.U16(22)} {((second.Bytes[1] & 0x80) != 0 ? "more" : "last")}");
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

        server.InjectKey(38, true);
        server.InjectKey(38, false);
        await c.SyncAsync();
        uint alarm = c.NewId();
        // 空闲 ≥ 当前 + 200 ms 时触发(Relative)。
        await c.SendAsync(sync, 9, b => b.U32(alarm).U32(1 | 2 | 4 | 8 | 16)
            .U32(idle).U32(1).I32(0).U32(200).U32(2).I32(0).U32(0));
        XMessage fired = await c.NextEventAsync((byte)(syncEvent + 1), timeoutMs: 3000);
        Assert.AreEqual(alarm, fired.U32(4));
    }

    /// <summary>ListSystemCounters 里按名字找系统计数器。</summary>
    private static async Task<uint> SystemCounterAsync(XTestClient c, byte sync, string name)
    {
        XMessage list = await c.RequestAsync(sync, 1);
        for (int i = 0, offset = 32; i < (int)list.U32(8); i++)
        {
            int length = list.U16(offset + 12);
            if (Encoding.Latin1.GetString(list.Bytes, offset + 14, length) == name)
            {
                return list.U32(offset);
            }
            offset += (14 + length + 3) & ~3;
        }
        throw new AssertFailedException($"没有系统计数器 {name}");
    }

    [TestMethod]
    public async Task 系统计数器上已经越过的正向跨越不排计时器空转()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte sync, byte syncEvent, _) = await ExtAsync(c, "SYNC");
        uint serverTime = await SystemCounterAsync(c, sync, "SERVERTIME"), idle = await SystemCounterAsync(c, sync, "IDLETIME");

        // SERVERTIME 上的 PositiveTransition(0),等待值是一秒之前(Relative −1000):已经越过,永远不会再成立。
        uint past = c.NewId();
        await c.SendAsync(sync, 9, b => b.U32(past).U32(1 | 2 | 4 | 8).U32(serverTime).U32(1).I32(-1).U32(unchecked((uint)-1000)).U32(0));
        await c.SyncAsync();
        Assert.IsFalse(await server.InvokeAsync(() => server.SyncTimerPending), "不该每毫秒醒一次");

        // GNOME / KIdleTime 的用法:IDLETIME 越过 50 毫秒报一次,delta = 0。报过之后用户不动,同样不该空转。
        await c.SendAsync(sync, 11, b => b.U32(past));
        uint alarm = c.NewId();
        await c.SendAsync(sync, 9, b => b.U32(alarm).U32(1 | 2 | 4 | 8 | 16).U32(idle).U32(0).I32(0).U32(50).U32(0).I32(0).U32(0));
        XMessage fired = await c.NextAsync(m => !m.IsReply && !m.IsError && m.EventCode == syncEvent + 1 && m.U32(4) == alarm && m.Bytes[28] == 0);
        Assert.AreEqual(alarm, fired.U32(4));
        await c.SyncAsync();
        Assert.IsFalse(await server.InvokeAsync(() => server.SyncTimerPending), "触发之后等用户输入,不排计时器");
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

    /// <summary>PresentPixmap:窗口、像素图、serial、无 valid / update、偏移 0、无 crtc,之后是两个栅栏、选项与 target / divisor / remainder。</summary>
    private static Task<ushort> PresentPixmapAsync(XTestClient c, byte present, uint window, uint pixmap, uint serial,
        ulong targetMsc = 0, uint waitFence = 0, uint idleFence = 0, IEnumerable<(uint Window, uint Serial)>? notifies = null) =>
        c.SendAsync(present, 1, b =>
        {
            b.U32(window).U32(pixmap).U32(serial).U32(0).U32(0).I16(0).I16(0)
                .U32(0).U32(waitFence).U32(idleFence).U32(0).U32(0)
                .U32((uint)targetMsc).U32((uint)(targetMsc >> 32)).U32(0).U32(0).U32(0).U32(0);
            foreach ((uint w, uint s) in notifies ?? [])
            {
                b.U32(w).U32(s);
            }
        });

    private static Task<XMessage> PresentCompleteAsync(XTestClient c, uint serial, int timeoutMs = 5000) =>
        c.NextAsync(m => !m.IsReply && !m.IsError && m.EventCode == 35 && m.U16(8) == 1 && m.U32(20) == serial, timeoutMs);

    private static ulong CompleteMsc(XMessage complete) => complete.U32(32) | ((ulong)complete.U32(36) << 32);

    /// <summary>一张填满一种颜色的 40×30 像素图。</summary>
    private static async Task<uint> SolidPixmapAsync(XTestClient c, uint window, uint color)
    {
        uint pixmap = c.NewId();
        await c.SendAsync(53, 24, b => b.U32(pixmap).U32(window).U16(40).U16(30));
        await FillAsync(c, pixmap, await GcAsync(c, pixmap, color), 0, 0, 40, 30);
        return pixmap;
    }

    [TestMethod]
    public async Task Present的帧号跟着宿主报的帧走_有客户端等帧时才向宿主要帧时钟()
    {
        // xs_plan F25:宿主按 144 Hz 报帧,帧间隔取最短的那个(中间跳了一帧的那次不算);之后到了目标帧的呈现在报帧时当场做掉。
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        _ = await ExtAsync(c, "Generic Event Extension");
        (byte present, _, _) = await ExtAsync(c, "Present");
        uint top = await MapTopAsync(c, host);
        uint blue = await SolidPixmapAsync(c, top, 0x0000FF);
        await c.SendAsync(present, 3, b => b.U32(c.NewId()).U32(top).U32(2));

        long frame = System.Diagnostics.Stopwatch.Frequency / 144;
        await server.InvokeAsync(() =>
        {
            long start = server.ClockTicks - (42 * frame);
            for (int k = 0; k <= 41; k++)
            {
                if (k != 20)
                {
                    server.ApplyHostFrame(start + (k * frame));   // 第 20 帧没报:宿主跳了一帧,那次间隔是两帧
                }
            }
            return 0;
        });
        double hz = await server.InvokeAsync(() => System.Diagnostics.Stopwatch.Frequency / server.FrameIntervalTicks);
        Assert.AreEqual(144, hz, 1, "最短的间隔就是刷新周期");

        Assert.IsEmpty(host.FrameClockRequests, "没有客户端等帧:不要");
        await PresentPixmapAsync(c, present, top, blue, 1);
        ulong msc = CompleteMsc(await PresentCompleteAsync(c, 1));
        await PresentPixmapAsync(c, present, top, blue, 2, targetMsc: msc + 1000);   // 约 7 秒之后
        await host.WaitForAsync(() => host.FrameClockRequests.Contains(true));
        await server.InvokeAsync(() =>
        {
            server.ApplyHostFrame(server.ClockTicks + (1001 * frame));   // 宿主报来的这一帧已经过了目标帧
            return 0;
        });
        XMessage complete = await PresentCompleteAsync(c, 2, timeoutMs: 1000);
        Assert.IsGreaterThanOrEqualTo(msc + 1000, CompleteMsc(complete));
        await host.WaitForAsync(() => host.FrameClockRequests.LastOrDefault() == false);
    }

    [TestMethod]
    public async Task Present等到target_msc那一帧才呈现_像素图提前释放也照常呈现()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        _ = await ExtAsync(c, "Generic Event Extension");
        (byte present, _, _) = await ExtAsync(c, "Present");
        uint top = await MapTopAsync(c, host);
        uint blue = await SolidPixmapAsync(c, top, 0x0000FF), red = await SolidPixmapAsync(c, top, 0xFF0000);
        await c.SendAsync(present, 3, b => b.U32(c.NewId()).U32(top).U32(2));

        await PresentPixmapAsync(c, present, top, blue, 1);
        ulong msc = CompleteMsc(await PresentCompleteAsync(c, 1));

        // 30 帧(约 0.5 秒)之后再呈现红的:按 MSC 控帧的客户端靠这个不空转。
        await PresentPixmapAsync(c, present, top, red, 2, targetMsc: msc + 30);
        await c.SendAsync(54, 0, b => b.U32(red));   // FreePixmap:规范说呈现之前一直持有引用
        await c.SyncAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => PresentCompleteAsync(c, 2, timeoutMs: 150), "目标帧之前不报完成");
        Assert.AreEqual(0x0000FFu, RecordingHost.Snapshot(host.Mapped[top]).Pixels[0] & 0xFFFFFF, "目标帧之前窗口不变");

        XMessage complete = await PresentCompleteAsync(c, 2);
        Assert.IsGreaterThanOrEqualTo(msc + 30, CompleteMsc(complete), "完成时的 MSC 不早于目标");
        Assert.AreEqual(0xFF0000u, RecordingHost.Snapshot(host.Mapped[top]).Pixels[0] & 0xFFFFFF);
        Assert.AreEqual(0, await server.InvokeAsync(() => server.PendingPresents));
    }

    [TestMethod]
    public async Task Present等wait_fence触发才呈现_idle_fence无效回BadFence()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        _ = await ExtAsync(c, "Generic Event Extension");
        (byte present, _, _) = await ExtAsync(c, "Present");
        (byte sync, _, byte syncError) = await ExtAsync(c, "SYNC");
        uint top = await MapTopAsync(c, host);
        uint red = await SolidPixmapAsync(c, top, 0xFF0000);
        await c.SendAsync(present, 3, b => b.U32(c.NewId()).U32(top).U32(2));
        uint fence = c.NewId(), idle = c.NewId();
        await c.SendAsync(sync, 14, b => b.U32(top).U32(fence).U8(0).U8(0).U8(0).U8(0));   // CreateFence:未触发
        await c.SendAsync(sync, 14, b => b.U32(top).U32(idle).U8(0).U8(0).U8(0).U8(0));

        await PresentPixmapAsync(c, present, top, red, 5, waitFence: fence, idleFence: idle);
        await c.SyncAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => PresentCompleteAsync(c, 5, timeoutMs: 150), "栅栏没触发不呈现");
        Assert.AreEqual(0xFFFFFFu, RecordingHost.Snapshot(host.Mapped[top]).Pixels[0] & 0xFFFFFF);

        await c.SendAsync(sync, 15, b => b.U32(fence));   // TriggerFence
        await PresentCompleteAsync(c, 5);
        Assert.AreEqual(0xFF0000u, RecordingHost.Snapshot(host.Mapped[top]).Pixels[0] & 0xFFFFFF);
        Assert.AreEqual(1, (await c.RequestAsync(sync, 18, b => b.U32(idle))).Bytes[8], "呈现之后 idle-fence 触发");

        ushort bad = await PresentPixmapAsync(c, present, top, red, 6, idleFence: c.NewId());
        XMessage error = await c.NextAsync(m => m.IsError && m.Sequence == bad);
        Assert.AreEqual(syncError + 2, error.Detail, "idle-fence 不是栅栏:BadFence,而不是悄悄忽略");
        await Assert.ThrowsAsync<OperationCanceledException>(() => PresentCompleteAsync(c, 6, timeoutMs: 100), "出错的请求不呈现");
    }

    [TestMethod]
    public async Task 较晚的Present先呈现时排在前面的按Skip了结_notifies有上限()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        _ = await ExtAsync(c, "Generic Event Extension");
        (byte present, _, _) = await ExtAsync(c, "Present");
        uint top = await MapTopAsync(c, host);
        uint blue = await SolidPixmapAsync(c, top, 0x0000FF), red = await SolidPixmapAsync(c, top, 0xFF0000);
        await c.SendAsync(present, 3, b => b.U32(c.NewId()).U32(top).U32(2));

        await PresentPixmapAsync(c, present, top, red, 1, targetMsc: ulong.MaxValue / 2);   // 远在天边
        await PresentPixmapAsync(c, present, top, blue, 2);
        XMessage skipped = await PresentCompleteAsync(c, 1);
        Assert.AreEqual(2, skipped.Bytes[11], "mode = Skip");
        XMessage shown = await PresentCompleteAsync(c, 2);
        Assert.AreEqual(0, shown.Bytes[11], "mode = Copy");
        Assert.AreEqual(0x0000FFu, RecordingHost.Snapshot(host.Mapped[top]).Pixels[0] & 0xFFFFFF, "过时的那条不再盖上来");
        Assert.AreEqual(0, await server.InvokeAsync(() => server.PendingPresents));

        ushort tooMany = await PresentPixmapAsync(c, present, top, blue, 3,
            notifies: Enumerable.Repeat((top, 9u), X11Server.MaxPresentNotifies + 1));
        Assert.AreEqual(11, (await c.NextAsync(m => m.IsError && m.Sequence == tooMany)).Detail, "PRESENTNOTIFY 超过上限:BadAlloc");
    }

    [TestMethod]
    public async Task Present与DAMAGE的QueryVersion不高于客户端要的版本()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte present, _, _) = await ExtAsync(c, "Present");
        (byte damage, _, _) = await ExtAsync(c, "DAMAGE");
        XMessage p = await c.RequestAsync(present, 0, b => b.U32(1).U32(0));
        Assert.AreEqual((1u, 0u), (p.U32(8), p.U32(12)));
        p = await c.RequestAsync(present, 0, b => b.U32(1).U32(9));
        Assert.AreEqual((1u, 2u), (p.U32(8), p.U32(12)));
        XMessage d = await c.RequestAsync(damage, 0, b => b.U32(1).U32(0));
        Assert.AreEqual((1u, 0u), (d.U32(8), d.U32(12)));
    }
}
