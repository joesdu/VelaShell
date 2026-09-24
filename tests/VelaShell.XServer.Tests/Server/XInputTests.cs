using System.Text;
using VelaShell.XServer.Server;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>XInputExtension:设备查询、XI2 事件选择与投递、XI2 抓取、原始事件。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class XInputTests
{
    private const byte GenericEvent = 35;

    private static async Task<byte> XiAsync(XTestClient c)
    {
        XMessage q = await c.RequestAsync(98, 0, b => b.U16(15).U16(0).Bytes(Encoding.Latin1.GetBytes("XInputExtension")).Pad());
        Assert.AreEqual(1, q.Bytes[8]);
        XMessage v = await c.RequestAsync(q.Bytes[9], 47, b => b.U16(2).U16(2));
        Assert.AreEqual(2, v.U16(8));
        Assert.AreEqual(2, v.U16(10));
        return q.Bytes[9];
    }

    private static Task<ushort> SelectAsync(XTestClient c, byte xi, uint window, ushort device, uint mask) =>
        c.SendAsync(xi, 46, b => b.U32(window).U16(1).U16(0).U16(device).U16(1)
            .U8((byte)mask).U8((byte)(mask >> 8)).U8((byte)(mask >> 16)).U8((byte)(mask >> 24)));

    private static Task<XMessage> NextXiAsync(XTestClient c, byte xi, int evtype, int timeoutMs = 5000) =>
        c.NextAsync(m => !m.IsReply && !m.IsError && m.EventCode == GenericEvent && m.Bytes[1] == xi && m.U16(8) == evtype, timeoutMs);

    private static async Task<uint> MapTopAsync(XTestClient c, RecordingHost host)
    {
        uint id = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(id).U32(c.RootWindow).I16(10).I16(20).U16(60).U16(40).U16(0).U16(1).U32(0).U32(0));
        await c.SendAsync(8, 0, b => b.U32(id));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
        return id;
    }

    [TestMethod]
    public async Task XIQueryDevice列出两个主设备与两个从设备()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(c);
        XMessage all = await c.RequestAsync(xi, 48, b => b.U16(0).U16(0));
        Assert.AreEqual(4, all.U16(8));
        Assert.AreEqual(2, all.U16(32), "第一个是主指针");
        Assert.AreEqual(1, all.U16(34), "use = MasterPointer");
        XMessage masters = await c.RequestAsync(xi, 48, b => b.U16(1).U16(0));
        Assert.AreEqual(2, masters.U16(8));
        XMessage bad = await c.RequestAsync(xi, 48, b => b.U16(42).U16(0));
        Assert.IsTrue(bad.IsError);
    }

    [TestMethod]
    public async Task XI2按键事件带主设备号与键码_核心事件照样给选了核心的客户端()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        await using XTestClient core = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(c);
        uint top = await MapTopAsync(c, host);
        await SelectAsync(c, xi, top, 1, 1u << 2);                             // XIAllMasterDevices:KeyPress
        await core.SendAsync(2, 0, b => b.U32(top).U32(0x800).U32(0x1));      // 另一个客户端:核心 KeyPress
        await core.SyncAsync();
        server.FocusTopLevel(top);
        server.Key(38, true);

        XMessage e = await NextXiAsync(c, xi, 2);
        Assert.AreEqual(3, e.U16(10), "deviceid = 主键盘");
        Assert.AreEqual(38u, e.U32(16), "detail = 键码");
        Assert.AreEqual(top, e.U32(24), "event 窗口");
        Assert.AreEqual(5, e.U16(52), "sourceid = 从键盘");
        XMessage coreEvent = await core.NextEventAsync(2);
        Assert.AreEqual(38, coreEvent.Bytes[1]);
    }

    [TestMethod]
    public async Task XI2按下按钮形成隐式抓取_移出窗口仍收到移动()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(c);
        uint top = await MapTopAsync(c, host);
        await SelectAsync(c, xi, top, 1, (1u << 4) | (1u << 5) | (1u << 6));
        await c.SyncAsync();
        server.PointerButton(top, 5, 5, 1, pressed: true);
        XMessage press = await NextXiAsync(c, xi, 4);
        Assert.AreEqual(1u, press.U32(16), "button 1");
        Assert.AreEqual(5 << 16, (int)press.U32(40), "event_x(FP1616)");

        server.PointerMotion(top, 200, 5);   // 出了 60 宽的窗口
        XMessage motion = await c.NextAsync(m => m.EventCode == GenericEvent && m.U16(8) == 6 && (int)m.U32(40) == 200 << 16);
        Assert.AreEqual(top, motion.U32(24), "隐式抓取期间事件仍发到按下的窗口");
        Assert.AreEqual(1, motion.Bytes[80] >> 1 & 1, "buttons 掩码里按钮 1 按着");
    }

    [TestMethod]
    public async Task XIGrabDevice抓住键盘后按键都发给抓取窗口()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(c);
        uint top = await MapTopAsync(c, host);
        XMessage status = await c.RequestAsync(xi, 51, b => b.U32(top).U32(0).U32(0).U16(3).U8(1).U8(1).U8(0).U8(0).U16(1)
            .U8(1 << 2).U8(0).U8(0).U8(0));
        Assert.AreEqual(0, status.Bytes[8], "GrabSuccess");
        server.FocusTopLevel(0);   // 焦点不在它身上也照样收到
        server.Key(24, true);
        XMessage e = await NextXiAsync(c, xi, 2);
        Assert.AreEqual(top, e.U32(24));
        await c.SendAsync(xi, 52, b => b.U32(0).U16(3).U16(0));
        await c.SyncAsync();
    }

    [TestMethod]
    public async Task 根窗口上选的原始移动带增量()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(c);
        uint top = await MapTopAsync(c, host);
        await SelectAsync(c, xi, c.RootWindow, 0, 1u << 17);   // XIAllDevices:RawMotion
        await c.SyncAsync();
        server.PointerMotion(top, 1, 1);
        await NextXiAsync(c, xi, 17);   // 从初始位置移过来的那一下
        server.PointerMotion(top, 4, 6);
        XMessage raw = await NextXiAsync(c, xi, 17);
        Assert.AreEqual(1, raw.U16(22), "valuators_len");
        Assert.AreEqual(3, (int)raw.U32(36), "dx 的整数部分");
        Assert.AreEqual(5, (int)raw.U32(44), "dy 的整数部分");
    }

    /// <summary>XIChangeHierarchy 的 AddMaster:一条 HIERARCHYCHANGE。</summary>
    private static Task<ushort> AddMasterAsync(XTestClient c, byte xi, string name)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(name);
        int padded = (bytes.Length + 3) & ~3;
        return c.SendAsync(xi, 43, b => b.U8(1).U8(0).U8(0).U8(0)
            .U16(1).U16((ushort)((8 + padded) / 4)).U16((ushort)bytes.Length).U8(1).U8(1).Bytes(bytes).Pad());
    }

    [TestMethod]
    public async Task AddMaster新建一对主设备_发HierarchyChanged_QueryDevice列得出()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(c);
        await SelectAsync(c, xi, c.RootWindow, 0, 1u << 11);   // XIAllDevices:HierarchyChanged
        await AddMasterAsync(c, xi, "second");

        XMessage changed = await NextXiAsync(c, xi, 11);
        Assert.AreEqual(1u, changed.U32(16) & 1, "flags 含 MasterAdded");
        Assert.AreEqual(6, changed.U16(20), "num_info:原来四个 + 新的一对");

        XMessage all = await c.RequestAsync(xi, 48, b => b.U16(0).U16(0));
        Assert.AreEqual(6, all.U16(8));
        string names = Encoding.Latin1.GetString(all.Bytes);
        StringAssert.Contains(names, "second pointer");
        StringAssert.Contains(names, "second keyboard");
    }

    [TestMethod]
    public async Task AttachSlave之后事件以新主设备报_DetachSlave之后只报从设备且没有核心事件()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(c);
        uint top = await MapTopAsync(c, host);
        await AddMasterAsync(c, xi, "second");   // 主指针 6、主键盘 7
        await c.SendAsync(xi, 43, b => b.U8(1).U8(0).U8(0).U8(0).U16(3).U16(2).U16(4).U16(6));   // AttachSlave 4 → 6
        await SelectAsync(c, xi, top, 0, 1u << 6);                                                 // XIAllDevices:Motion
        await c.SyncAsync();

        server.PointerMotion(top, 3, 3);
        XMessage attached = await NextXiAsync(c, xi, 6);
        Assert.AreEqual(6, attached.U16(10), "deviceid = 新的主指针");

        await c.SendAsync(xi, 43, b => b.U8(1).U8(0).U8(0).U8(0).U16(4).U16(2).U16(4).U16(0));    // DetachSlave 4
        await c.SendAsync(2, 0, b => b.U32(top).U32(0x800).U32(0x40));                             // 同时选核心 PointerMotion
        await c.SyncAsync();
        server.PointerMotion(top, 5, 5);
        XMessage floating = await NextXiAsync(c, xi, 6);
        Assert.AreEqual(4, floating.U16(10), "浮动:deviceid = 从设备本身");
        await c.SyncAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => c.NextEventAsync(6, timeoutMs: 150), "浮动的从设备不产生核心事件");
    }

    [TestMethod]
    public async Task RemoveMaster让从设备浮动_虚拟核心设备不能删除()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(c);
        await AddMasterAsync(c, xi, "second");
        await c.SendAsync(xi, 43, b => b.U8(1).U8(0).U8(0).U8(0).U16(3).U16(2).U16(5).U16(7));    // 从键盘挂到 7
        await c.SendAsync(xi, 43, b => b.U8(1).U8(0).U8(0).U8(0).U16(2).U16(3).U16(6).U8(1).U8(0).U16(0).U16(0));   // RemoveMaster 6,Float
        XMessage keyboard = await c.RequestAsync(xi, 48, b => b.U16(5).U16(0));
        Assert.AreEqual(5, keyboard.U16(34), "use = FloatingSlave");
        Assert.AreEqual(0, keyboard.U16(36), "attachment = 0");

        XMessage error = await c.RequestAsync(xi, 43, b => b.U8(1).U8(0).U8(0).U8(0).U16(2).U16(3).U16(2).U8(1).U8(0).U16(0).U16(0));
        Assert.IsTrue(error.IsError, "删虚拟核心指针:BadDevice");
    }
}
