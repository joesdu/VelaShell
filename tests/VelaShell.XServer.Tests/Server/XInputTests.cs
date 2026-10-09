using System.Text;
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
        await c.SyncAsync();                                                   // 两个客户端的请求都执行完了,再注入
        server.FocusTopLevel(host.Mapped[top]);
        server.InjectKey(38, true);

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
        server.InjectPointerButton(host.Mapped[top], 5, 5, 1, pressed: true);
        XMessage press = await NextXiAsync(c, xi, 4);
        Assert.AreEqual(1u, press.U32(16), "button 1");
        Assert.AreEqual(5 << 16, (int)press.U32(40), "event_x(FP1616)");

        server.InjectPointerMotion(host.Mapped[top], 200, 5);   // 出了 60 宽的窗口
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
        server.FocusTopLevel(null);   // 焦点不在它身上也照样收到
        server.InjectKey(24, true);
        XMessage e = await NextXiAsync(c, xi, 2);
        Assert.AreEqual(top, e.U32(24));
        await c.SendAsync(xi, 52, b => b.U32(0).U16(3).U16(0));
        await c.SyncAsync();
    }

    [TestMethod]
    public async Task 根窗口上选的原始移动报设备的绝对位置_Warp不发也不挪设备位置()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(c);
        uint top = await MapTopAsync(c, host);   // 在根坐标 (10, 20)
        await SelectAsync(c, xi, c.RootWindow, 1, 1u << 17);   // XIAllMasterDevices:RawMotion(XIAllDevices 的话主、从设备各一份)
        await c.SyncAsync();
        server.InjectPointerMotion(host.Mapped[top], 1, 1);
        await NextXiAsync(c, xi, 17);
        server.InjectPointerMotion(host.Mapped[top], 4, 6);
        XMessage raw = await NextXiAsync(c, xi, 17);
        Assert.AreEqual(1, raw.U16(22), "valuators_len");
        Assert.AreEqual(14, (int)raw.U32(36), "轴声明的是 Abs X:报设备的位置(整数部分)");
        Assert.AreEqual(26, (int)raw.U32(44), "Abs Y");

        // 程序 Warp 回窗口中心:服务端合成的,不是设备运动 —— 不发原始移动。
        await c.SendAsync(41, 0, b => b.U32(0).U32(top).I16(0).I16(0).U16(0).U16(0).I16(30).I16(20));   // WarpPointer
        await c.SyncAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => NextXiAsync(c, xi, 17, timeoutMs: 100), "原先 Warp 也发一条反向的增量");

        // 用户再挪 +2:原始值是设备的位置(没被 Warp 改过),客户端拿前后两次的差得到真实的 +2。
        server.InjectPointerMotion(host.Mapped[top], 6, 6);
        XMessage next = await NextXiAsync(c, xi, 17);
        Assert.AreEqual(16, (int)next.U32(36));
        Assert.AreEqual(26, (int)next.U32(44));
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
        Assert.Contains("second pointer", names);
        Assert.Contains("second keyboard", names);
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

        server.InjectPointerMotion(host.Mapped[top], 3, 3);
        XMessage fromSlave = await NextXiAsync(c, xi, 6);
        Assert.AreEqual(4, fromSlave.U16(10), "XIAllDevices:从设备那份");
        XMessage attached = await NextXiAsync(c, xi, 6);
        Assert.AreEqual(6, attached.U16(10), "主设备那份:deviceid = 新的主指针");

        await c.SendAsync(xi, 43, b => b.U8(1).U8(0).U8(0).U8(0).U16(4).U16(2).U16(4).U16(0));    // DetachSlave 4
        await c.SendAsync(2, 0, b => b.U32(top).U32(0x800).U32(0x40));                             // 同时选核心 PointerMotion
        await c.SyncAsync();
        server.InjectPointerMotion(host.Mapped[top], 5, 5);
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

    [TestMethod]
    public async Task AddMaster分完设备ID回BadAlloc_错误里带已生效的条数_之前的改动照样生效()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(c);
        await SelectAsync(c, xi, c.RootWindow, 0, 1u << 11);   // HierarchyChanged

        // 一条请求里 255 个 AddMaster:设备 ID 是 CARD8(XI 1.x),6–255 只容得下 125 对。
        XMessage error = await c.RequestAsync(xi, 43, b =>
        {
            b.U8(255).U8(0).U8(0).U8(0);
            for (int i = 0; i < 255; i++)
            {
                b.U16(1).U16(3).U16(1).U8(1).U8(1).Bytes("m"u8.ToArray()).Pad();
            }
        });
        Assert.IsTrue(error.IsError);
        Assert.AreEqual(11, error.Bytes[1], "BadAlloc");
        Assert.AreEqual(125u, error.U32(4), "bad value = 已经生效的条数");

        XMessage changed = await NextXiAsync(c, xi, 11);
        Assert.AreEqual(254, changed.U16(20), "num_info:原来四个 + 125 对");
        XMessage all = await c.RequestAsync(xi, 48, b => b.U16(0).U16(0));
        Assert.AreEqual(254, all.U16(8));
    }

    [TestMethod]
    public async Task XI2按钮事件的buttons是事件之前的按钮状态()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(c);
        uint top = await MapTopAsync(c, host);
        await SelectAsync(c, xi, top, 0, (1u << 4) | (1u << 5));   // ButtonPress | ButtonRelease
        await c.SyncAsync();

        server.InjectPointerButton(host.Mapped[top], 3, 3, 1, pressed: true);
        XMessage press = await NextXiAsync(c, xi, 4);
        Assert.AreEqual(0, press.Bytes[80] & 0x02, "按下:按钮 1 在事件之前还没按着");
        server.InjectPointerButton(host.Mapped[top], 3, 3, 1, pressed: false);
        XMessage release = await NextXiAsync(c, xi, 5);
        Assert.AreEqual(0x02, release.Bytes[80] & 0x02, "松开:事件之前还按着");
    }

    /// <summary>XIPassiveGrabDevice(在根窗口上抓按钮或按键,掩码 1 个单位为 0)。</summary>
    private static Task<XMessage> PassiveGrabAsync(XTestClient c, byte xi, uint detail, byte grabType, params uint[] modifiers) =>
        c.RequestAsync(xi, 54, b =>
        {
            b.U32(0).U32(c.RootWindow).U32(0).U32(detail).U16(2).U16((ushort)modifiers.Length).U16(1)
                .U8(grabType).U8(1).U8(1).U8(0).U16(0).U32(0);
            foreach (uint m in modifiers)
            {
                b.U32(m);
            }
        });

    [TestMethod]
    public async Task XI2被动抓取与别的客户端冲突的修饰组合回报为AlreadyGrabbed()
    {
        await using X11Server server = new();
        await using XTestClient a = await XTestClient.ConnectAsync(server);
        await using XTestClient b = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(a);
        await XiAsync(b);
        XMessage first = await PassiveGrabAsync(a, xi, 1, 0, 0);
        Assert.AreEqual(0, first.U16(8), "a 的抓取都成");

        XMessage second = await PassiveGrabAsync(b, xi, 1, 0, 0, 4);
        Assert.AreEqual(1, second.U16(8), "b 的 0 号组合与 a 冲突");
        Assert.AreEqual(0u, second.U32(32));
        Assert.AreEqual(1, second.Bytes[36], "AlreadyGrabbed");

        // AnyButton(0)与 AnyModifier 跟谁都冲突。
        XMessage any = await PassiveGrabAsync(b, xi, 0, 0, 0x80000000);
        Assert.AreEqual(1, any.U16(8));
    }

    /// <summary>
    /// 选 XIAllDevices 的客户端,从设备与主设备的事件各收一份(按键与原始按键都是);核心的 do-not-propagate 不截断 XI2 的传播。
    /// </summary>
    [TestMethod]
    public async Task 选XIAllDevices的主从设备各收一份_核心的do_not_propagate不截断XI2()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        await using XTestClient core = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(c);
        uint top = await MapTopAsync(c, host);
        uint child = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(child).U32(top).I16(0).I16(0).U16(30).U16(30).U16(0).U16(1).U32(0).U32(0x1000).U32(0x1));   // do-not-propagate:KeyPress
        await c.SendAsync(8, 0, b => b.U32(child));
        await SelectAsync(c, xi, top, 0, 1u << 2);                  // XIAllDevices:KeyPress
        await SelectAsync(c, xi, c.RootWindow, 0, 1u << 13);        // XIAllDevices:RawKeyPress
        await core.SendAsync(2, 0, b => b.U32(top).U32(0x800).U32(0x1));
        await core.SyncAsync();
        await c.SyncAsync();
        server.FocusTopLevel(host.Mapped[top]);
        server.InjectPointerMotion(host.Mapped[top], 5, 5);       // 指针在子窗口里:按键从子窗口往上传
        server.InjectKey(XKeycodes.A, pressed: true);

        XMessage first = await NextXiAsync(c, xi, 2), second = await NextXiAsync(c, xi, 2);
        CollectionAssert.AreEquivalent(new ushort[] { 5, 3 }, new[] { first.U16(10), second.U16(10) }, "从键盘、主键盘各一份(原先只有主设备那份)");
        Assert.AreEqual(top, first.U32(24), "do-not-propagate 只截断核心事件,XI2 照样传到顶层");
        XMessage raw1 = await NextXiAsync(c, xi, 13), raw2 = await NextXiAsync(c, xi, 13);
        CollectionAssert.AreEquivalent(new ushort[] { 5, 3 }, new[] { raw1.U16(10), raw2.U16(10) }, "原始事件也是各一份");
        await Assert.ThrowsAsync<OperationCanceledException>(() => core.NextEventAsync(2, timeoutMs: 100), "核心事件被子窗口的 do-not-propagate 截断");
    }

    /// <summary>
    /// XISetFocus 接受 PointerRoot(原先 BadWindow);窗口不可见时焦点退到父窗口(XI 2.2:等于 RevertToParent,原先退到 PointerRoot);
    /// XIUngrabDevice 校验设备;Enter 等被动抓取的 detail、TouchBegin 的 grab_mode 按规范校验;XIQueryPointer 重置移动提示。
    /// </summary>
    [TestMethod]
    public async Task XISetFocus接受PointerRoot并按RevertToParent退回_XIUngrabDevice校验设备_XIQueryPointer重置移动提示()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(c);
        uint top = await MapTopAsync(c, host);
        uint child = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(child).U32(top).I16(0).I16(0).U16(20).U16(20).U16(0).U16(1).U32(0).U32(0));
        await c.SendAsync(8, 0, b => b.U32(child));

        ushort pointerRoot = await c.SendAsync(xi, 49, b => b.U32(1).U32(0).U16(3).U16(0));   // XISetFocus(PointerRoot)
        await c.SyncAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => c.NextAsync(m => m.IsError && m.Sequence == pointerRoot, timeoutMs: 100));
        await c.SendAsync(xi, 49, b => b.U32(child).U32(0).U16(3).U16(0));
        await c.SendAsync(10, 0, b => b.U32(child));                                          // UnmapWindow(child)
        XMessage focus = await c.RequestAsync(xi, 50, b => b.U16(3).U16(0));
        Assert.AreEqual(top, focus.U32(8), "退到父窗口");

        XMessage badDevice = await c.RequestAsync(xi, 52, b => b.U32(0).U16(42).U16(0));
        Assert.IsTrue(badDevice.IsError, "XIUngrabDevice 对不存在的设备回 BadDevice");
        XMessage enterDetail = await c.RequestAsync(xi, 54, b => b.U32(0).U32(top).U32(0).U32(1).U16(2).U16(1).U16(1)
            .U8(2).U8(1).U8(1).U8(0).U16(0).U32(0).U32(0));
        Assert.AreEqual(2, enterDetail.Detail, "Enter 类被动抓取的 detail 必须是 0");
        XMessage touchMode = await c.RequestAsync(xi, 54, b => b.U32(0).U32(top).U32(0).U32(0).U16(2).U16(1).U16(1)
            .U8(4).U8(1).U8(1).U8(0).U16(0).U32(0).U32(0));
        Assert.AreEqual(2, touchMode.Detail, "TouchBegin 的 grab_mode 必须是 Touch");

        // PointerMotionHint:一条提示之后,XIQueryPointer 问过位置才再给一条(同核心 QueryPointer)。
        await c.SendAsync(2, 0, b => b.U32(top).U32(0x800).U32(0x40 | 0x80));
        await c.SyncAsync();
        server.InjectPointerMotion(host.Mapped[top], 30, 30);
        Assert.AreEqual(1, (await c.NextEventAsync(6)).Detail, "Hint");
        server.InjectPointerMotion(host.Mapped[top], 31, 31);
        await c.SyncAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => c.NextEventAsync(6, timeoutMs: 100));
        await c.RequestAsync(xi, 40, b => b.U32(top).U16(2).U16(0));                         // XIQueryPointer
        server.InjectPointerMotion(host.Mapped[top], 32, 32);
        Assert.AreEqual(1, (await c.NextEventAsync(6)).Detail, "问过位置之后再给一条提示");
    }

    /// <summary>
    /// 指针离开所有顶层之后位置留着最后一次的:QueryPointer / XIQueryPointer 报它(child 为 None),按键事件的 root-x 也是它 ——
    /// 原先报 (0, 0),按键事件的 root-x 是 −1。
    /// </summary>
    [TestMethod]
    public async Task 指针离开所有顶层之后位置留着最后一次的()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(c);
        uint top = await MapTopAsync(c, host);   // 在根坐标 (10, 20)
        await c.SendAsync(2, 0, b => b.U32(top).U32(0x800).U32(0x1));
        await c.SyncAsync();
        server.FocusTopLevel(host.Mapped[top]);
        server.InjectPointerMotion(host.Mapped[top], 30, 15);
        server.InjectPointerLeave();

        XMessage core = await c.RequestAsync(38, 0, b => b.U32(c.RootWindow));
        Assert.AreEqual(40, core.I16(16), "root-x");
        Assert.AreEqual(35, core.I16(18), "root-y");
        Assert.AreEqual(0u, core.U32(12), "child = None:指针不在任何顶层里");
        XMessage xi2 = await c.RequestAsync(xi, 40, b => b.U32(c.RootWindow).U16(2).U16(0));
        Assert.AreEqual(40, (int)xi2.U32(16) >> 16);

        server.InjectKey(XKeycodes.A, pressed: true);
        XMessage key = await c.NextEventAsync(2);
        Assert.AreEqual(40, key.I16(20), "按键事件的 root-x");
    }

    /// <summary>根窗口变大:指针设备的 Abs X / Abs Y 范围跟着变,发 XI_DeviceChanged(reason DeviceChange)带上新的类。原先从不发。</summary>
    [TestMethod]
    public async Task 根窗口尺寸变了发XI_DeviceChanged_轴的范围跟着变()
    {
        await using X11Server server = new(new X11ServerOptions { ScreenWidth = 1920, ScreenHeight = 1080 });
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(c);
        await SelectAsync(c, xi, c.RootWindow, 0, 1u << 1);   // XIAllDevices:DeviceChanged
        await c.SyncAsync();
        server.SetScreenLayout(2560, 1440);

        List<XMessage> changed = [await NextXiAsync(c, xi, 1), await NextXiAsync(c, xi, 1)];
        CollectionAssert.AreEquivalent(new ushort[] { 2, 4 }, changed.Select(e => e.U16(10)).ToArray(), "主指针与从指针各一条");
        XMessage e = changed[0];
        Assert.AreEqual(7, e.U16(16), "num_classes:按钮、Abs X / Y、两个滚动轴、两个 ScrollClass");
        Assert.AreEqual(2, e.Bytes[20], "reason = DeviceChange");
        // 类从第 32 字节起:按钮类 12 个四字节,之后是 Abs X 的轴类(max 的整数部分在轴类的第 20 字节)。
        Assert.AreEqual(2, e.U16(80), "第二个类是轴");
        Assert.AreEqual(2559, (int)e.U32(100), "Abs X 的最大值 = 新的根窗口宽 − 1");
    }

    /// <summary>XIQueryDevice(一个设备)回复里的类:(type, 类在回复里的起点)。</summary>
    private static List<(ushort Type, int Offset)> Classes(XMessage reply)
    {
        int nameLength = reply.U16(40), classes = reply.U16(38);
        int at = 44 + ((nameLength + 3) & ~3);
        List<(ushort, int)> found = [];
        for (int i = 0; i < classes; i++)
        {
            found.Add((reply.U16(at), at));
            at += reply.U16(at + 2) * 4;
        }
        return found;
    }

    /// <summary>FP3232 → double。</summary>
    private static double Fp3232(XMessage m, int offset) => (int)m.U32(offset) + (m.U32(offset + 4) / 4294967296.0);

    [TestMethod]
    public async Task 指针设备有两个滚动轴与两个ScrollClass()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(c);
        XMessage slave = await c.RequestAsync(xi, 48, b => b.U16(4).U16(0));
        List<(ushort Type, int Offset)> classes = Classes(slave);
        CollectionAssert.AreEqual(new ushort[] { 1, 2, 2, 2, 2, 3, 3 }, classes.Select(k => k.Type).ToArray(), "按钮、四个轴、两个 ScrollClass");
        (ushort, ushort, uint, double)[] scroll = [.. classes.Where(k => k.Type == 3)
            .Select(k => (slave.U16(k.Offset + 6), slave.U16(k.Offset + 8), slave.U32(k.Offset + 12), Fp3232(slave, k.Offset + 16)))];
        CollectionAssert.AreEqual(new (ushort, ushort, uint, double)[] { (2, 2, 2u, 1.0), (3, 1, 2u, 1.0) }, scroll,
            "轴 2 水平、轴 3 垂直,Preferred,一格 = 1.0");
        int vertical = classes[4].Offset;
        Assert.AreEqual(0, slave.Bytes[vertical + 40], "滚动轴是相对模式");
    }

    [TestMethod]
    public async Task 平滑滚动_XI2经滚动轴收到累计值_攒够一格模拟成按钮_XI2那份带PointerEmulated_核心只收按钮()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        await using XTestClient core = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(c);
        uint top = await MapTopAsync(c, host);
        await SelectAsync(c, xi, top, 1, (1u << 4) | (1u << 5) | (1u << 6));   // ButtonPress、ButtonRelease、Motion
        await core.SendAsync(2, 0, b => b.U32(top).U32(0x800).U32(0x4 | 0x8));
        await core.SyncAsync();
        await c.SyncAsync();
        XTopLevelWindow window = host.Mapped[top];

        server.InjectScroll(window, 5, 5, 0, 0.5);
        server.InjectScroll(window, 5, 5, 0, 0.5);

        Task<XMessage> ScrollMotionAsync() => c.NextAsync(m => !m.IsReply && !m.IsError && m.EventCode == GenericEvent && m.Bytes[1] == xi
            && m.U16(8) == 6 && m.U32(84) == 0xB);
        XMessage first = await ScrollMotionAsync(), second = await ScrollMotionAsync();
        Assert.AreEqual(0.5, Fp3232(first, 104), "轴 3 的累计值");
        Assert.AreEqual(1.0, Fp3232(second, 104));
        Assert.AreEqual(0u, first.U32(56) & (1u << 16), "滚动轴的事件本身不是模拟的");
        XMessage press = await NextXiAsync(c, xi, 4);
        Assert.AreEqual(5u, press.U32(16), "攒够一格:模拟一次按钮 5(向下)");
        Assert.AreEqual(1u << 16, press.U32(56) & (1u << 16), "PointerEmulated");
        XMessage corePress = await core.NextEventAsync(4);
        Assert.AreEqual(5, corePress.Bytes[1], "核心客户端照样收到按钮 5");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => server.InjectScroll(window, 5, 5, double.NaN, 0));
    }

    [TestMethod]
    public async Task 滚轮按钮反过来模拟成滚动轴_带PointerEmulated()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(c);
        uint top = await MapTopAsync(c, host);
        await SelectAsync(c, xi, top, 1, (1u << 4) | (1u << 6));
        await c.SyncAsync();
        XTopLevelWindow window = host.Mapped[top];

        server.InjectPointerButton(window, 5, 5, 4, pressed: true);
        server.InjectPointerButton(window, 5, 5, 4, pressed: false);

        XMessage motion = await c.NextAsync(m => !m.IsReply && !m.IsError && m.EventCode == GenericEvent && m.Bytes[1] == xi
            && m.U16(8) == 6 && m.U32(84) == 0xB);
        Assert.AreEqual(-1.0, Fp3232(motion, 104), "按钮 4 = 向上一格");
        Assert.AreEqual(1u << 16, motion.U32(56) & (1u << 16), "模拟出来的滚动带 PointerEmulated");
        XMessage press = await NextXiAsync(c, xi, 4);
        Assert.AreEqual(4u, press.U32(16));
        Assert.AreEqual(0u, press.U32(56) & (1u << 16), "真的按钮不带");
    }

    [TestMethod]
    public async Task XI2被动抓取的detail与修饰位按规范校验()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(c);
        XMessage keycode = await PassiveGrabAsync(c, xi, 5, 1, 0);
        Assert.IsTrue(keycode.IsError, "键码 5 不合法");
        Assert.AreEqual(2, keycode.Detail, "BadValue");
        XMessage huge = await PassiveGrabAsync(c, xi, 70000, 0, 0);
        Assert.AreEqual(2, huge.Detail, "detail 超过 255");
        XMessage modifier = await PassiveGrabAsync(c, xi, 1, 0, 0x100);
        Assert.AreEqual(2, modifier.Detail, "核心修饰位之外的位");
    }

    /// <summary>
    /// 核心被动抓取:AnyModifier 的抓取与别人的具体组合冲突(原先只比完全相同的组合);Ungrab 掉 Shift 那一个组合,其余组合照样有效
    /// (原先 Ungrab 不拆分);核心的 UngrabKey(AnyKey, AnyModifier)不动同一客户端的 XI2 被动抓取(原先一起删);修饰与事件掩码校验。
    /// </summary>
    [TestMethod]
    public async Task 核心被动抓取按组合查冲突_Ungrab拆分AnyModifier抓取_核心Ungrab不动XI2的被动抓取()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient a = await XTestClient.ConnectAsync(server);
        await using XTestClient b = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(a);
        uint top = await MapTopAsync(a, host);
        await b.SendAsync(2, 0, w => w.U32(top).U32(0x800).U32(0x1));            // B 在顶层上选 KeyPress
        await a.SendAsync(33, 0, w => w.U32(top).U16(0x8000).U8(38).U8(1).U8(1).Pad());   // A:GrabKey(a, AnyModifier)
        await a.SyncAsync();

        XMessage conflict = await b.RequestAsync(33, 0, w => w.U32(top).U16(0x1).U8(38).U8(1).U8(1).Pad());
        Assert.AreEqual(10, conflict.Detail, "Shift+a 落在 A 的 AnyModifier 里:BadAccess");

        await a.SendAsync(34, 38, w => w.U32(top).U16(0x1).Pad());   // A:UngrabKey(a, Shift)
        await a.SyncAsync();
        server.FocusTopLevel(host.Mapped[top]);
        await b.SyncAsync();
        server.InjectKey(XKeycodes.ShiftLeft, pressed: true);
        server.InjectKey(XKeycodes.A, pressed: true);
        server.InjectKey(XKeycodes.A, pressed: false);
        server.InjectKey(XKeycodes.ShiftLeft, pressed: false);
        XMessage shifted = await b.NextAsync(m => !m.IsReply && !m.IsError && m.EventCode == 2 && m.Detail == XKeycodes.A);
        Assert.AreEqual(1, shifted.U16(28) & 0xFF, "Shift+a 不再被抓,照常给 B");
        server.InjectKey(XKeycodes.A, pressed: true);
        server.InjectKey(XKeycodes.A, pressed: false);
        await a.NextAsync(m => !m.IsReply && !m.IsError && m.EventCode == 2 && m.Detail == XKeycodes.A);   // 不带修饰的 a 仍被 A 抓

        // A 的 XI2 被动抓取(s,不带修饰);核心 UngrabKey(AnyKey, AnyModifier)只删核心的抓取。
        XMessage xiGrab = await a.RequestAsync(xi, 54, w => w.U32(0).U32(top).U32(0).U32(XKeycodes.S).U16(3).U16(1).U16(1)
            .U8(1).U8(1).U8(1).U8(0).U16(0).U8(1 << 2).U8(0).U8(0).U8(0).U32(0));
        Assert.AreEqual(0, xiGrab.U16(8));
        await a.SendAsync(34, 0, w => w.U32(top).U16(0x8000).Pad());
        await a.SyncAsync();
        server.InjectKey(XKeycodes.S, pressed: true);
        XMessage xiPress = await NextXiAsync(a, xi, 2);
        Assert.AreEqual(XKeycodes.S, xiPress.U32(16), "XI2 的被动抓取还在");
        server.InjectKey(XKeycodes.S, pressed: false);

        XMessage badModifiers = await a.RequestAsync(33, 0, w => w.U32(top).U16(0x100).U8(40).U8(1).U8(1).Pad());
        Assert.AreEqual(2, badModifiers.Detail, "修饰组合里有核心修饰位之外的位:BadValue");
        XMessage badMask = await a.RequestAsync(28, 0, w => w.U32(top).U16(0x1).U8(1).U8(1).U32(0).U32(0).U8(1).U8(0).U16(0));
        Assert.AreEqual(2, badMask.Detail, "SETofPOINTEREVENT 之外的位:BadValue");
    }

    [TestMethod]
    public async Task 一个客户端在一个窗口上的XI2被动抓取有上限()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(c);
        uint[] all = [.. Enumerable.Range(0, 256).Select(m => (uint)m)];
        XMessage? error = null;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (uint button = 1; button <= 20 && error is null; button++)
        {
            XMessage m = await PassiveGrabAsync(c, xi, button, 0, all);
            error = m.IsError ? m : null;
        }
        Assert.IsNotNull(error, "16 × 256 = 4096 个之后应当回 BadAlloc");
        Assert.AreEqual(11, error.Detail);
        Assert.IsLessThan(5_000, watch.ElapsedMilliseconds);
    }

    private static Task<ushort> XiChangePropertyAsync(XTestClient c, byte xi, byte mode, byte format, uint property, uint type, byte[] values) =>
        c.SendAsync(xi, 57, b => b.U16(2).U8(mode).U8(format).U32(property).U32(type).U32((uint)(values.Length / (format / 8))).Bytes(values).Pad());

    [TestMethod]
    public async Task XI设备属性按核心属性的规则校验_按本机序存放_变化发XI_PropertyEvent()
    {
        await using X11Server server = new();
        await using XTestClient big = await XTestClient.ConnectAsync(server, bigEndian: true);
        await using XTestClient little = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(big);
        await XiAsync(little);
        const uint property = 1, integer = 19;   // PRIMARY 当属性名用;INTEGER

        // little 在根窗口上选 XI_PropertyEvent(第 12 位)。
        await SelectAsync(little, xi, little.RootWindow, 0, 1u << 12);
        await little.SyncAsync();

        // 大端客户端写一个 32 位值,小端客户端读到的是同一个数。
        await XiChangePropertyAsync(big, xi, 0, 32, property, integer, [0x11, 0x22, 0x33, 0x44]);
        XMessage created = await NextXiAsync(little, xi, 12);
        Assert.AreEqual(property, created.U32(16));
        Assert.AreEqual(1, created.Bytes[20], "PropertyCreated");
        XMessage read = await little.RequestAsync(xi, 59, b => b.U16(2).U8(0).U8(0).U32(property).U32(0).U32(0).U32(1));
        Assert.AreEqual(0x11223344u, read.U32(32));

        // Append 的类型不一样:BadMatch。
        await XiChangePropertyAsync(little, xi, 2, 8, property, integer, [1]);
        XMessage mismatch = await little.NextAsync(m => m.IsError);
        Assert.AreEqual(8, mismatch.Detail, "BadMatch");

        // 声称的个数比带的数据多:BadLength(原先截断了事)。
        XMessage length = await little.RequestAsync(xi, 57, b => b.U16(2).U8(0).U8(8).U32(property).U32(integer).U32(100).U32(0));
        Assert.AreEqual(16, length.Detail, "BadLength");

        await little.SendAsync(xi, 58, b => b.U16(2).U16(0).U32(property));   // XIDeleteProperty
        XMessage deleted = await NextXiAsync(little, xi, 12);
        Assert.AreEqual(0, deleted.Bytes[20], "PropertyDeleted");
    }
    [TestMethod]
    public async Task XISelectEvents按设备分开存_分两次选不互相覆盖_一次请求给两个设备各选各的()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(c);
        uint window = await MapTopAsync(c, host);
        XTopLevelWindow handle = host.Mapped[window];

        // 先给 XIAllMasterDevices 选 RawMotion,再给 XIAllDevices 选 HierarchyChanged(原先后一次把前一次的主设备掩码盖掉)。
        await SelectAsync(c, xi, c.RootWindow, 1, 1u << 17);
        await SelectAsync(c, xi, c.RootWindow, 0, 1u << 11);
        await c.SyncAsync();
        server.InjectPointerMotion(handle, 5, 5);
        await NextXiAsync(c, xi, 17);

        XMessage selected = await c.RequestAsync(xi, 60, b => b.U32(c.RootWindow));   // XIGetSelectedEvents
        Assert.AreEqual(2, selected.U16(8), "两份:XIAllDevices 与 XIAllMasterDevices");
        Assert.AreEqual(0, selected.U16(32));
        Assert.AreEqual(1u << 11, selected.U32(36));
        Assert.AreEqual(1, selected.U16(44));
        Assert.AreEqual(1u << 17, selected.U32(48));

        // 一次请求:主指针(2)选 Motion,主键盘(3)选 KeyPress —— 原先后一个把前一个盖掉,指针事件全丢。
        await c.SendAsync(xi, 46, b => b.U32(window).U16(2).U16(0)
            .U16(2).U16(1).U32(1u << 6)
            .U16(3).U16(1).U32(1u << 2));
        await c.SyncAsync();
        server.InjectPointerMotion(handle, 9, 9);
        XMessage motion = await NextXiAsync(c, xi, 6);
        Assert.AreEqual(2, motion.U16(10), "deviceid = 主指针");
        server.FocusTopLevel(handle);
        server.InjectKey(38, pressed: true);
        await NextXiAsync(c, xi, 2);
    }
    [TestMethod]
    public async Task BreakGrabs解除抓取与冻结_放开GrabServer_把浮动的从设备挂回去()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient a = await XTestClient.ConnectAsync(server);
        await using XTestClient b = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(a);

        async Task<uint> MapAtAsync(XTestClient c, short x)
        {
            uint id = c.NewId();
            await c.SendAsync(1, 0, w => w.U32(id).U32(c.RootWindow).I16(x).I16(0).U16(100).U16(80).U16(0).U16(1).U32(0).U32(0x800).U32(0x4));   // ButtonPress
            await c.SendAsync(8, 0, w => w.U32(id));
            await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
            return id;
        }
        uint t = await MapAtAsync(a, 0), u = await MapAtAsync(b, 300);

        // 卡住的样子:A 同步抓着指针(设备冻结)、抓着服务器,还把从指针浮动了;之后 A 再也不说话(远端进程被 SIGSTOP)。
        await a.SendAsync(xi, 43, w => w.U8(1).U8(0).U8(0).U8(0).U16(4).U16(2).U16(4).U16(0));   // DetachSlave 4
        await a.RequestAsync(26, 0, w => w.U32(t).U16(0x4).U8(0).U8(1).U32(0).U32(0).U32(0));    // GrabPointer,pointer_mode = Synchronous
        await a.SendAsync(36, 0);                                                                   // GrabServer
        await a.SyncAsync();
        ushort pending = await b.SendAsync(43, 0);                                                  // B 的请求被 GrabServer 挂住

        server.BreakGrabs();
        XMessage reply = await b.NextAsync(m => m.IsReply && m.Sequence == pending);
        Assert.IsTrue(reply.IsReply, "GrabServer 放开了");
        XMessage slave = await b.RequestAsync(xi, 48, w => w.U16(4).U16(0));                       // XIQueryDevice 4
        Assert.AreEqual(2, slave.U16(36), "从指针挂回虚拟核心指针");

        server.InjectPointerButton(host.Mapped[u], 5, 5, 1, pressed: true);
        XMessage press = await b.NextEventAsync(4);
        Assert.AreEqual(u, press.U32(12), "抓取与冻结都解除了,按下照常报给 B 的窗口");
    }
    [TestMethod]
    public async Task RestrictForwardedClients_SSH转发来的连接看不到XTEST_收不到原始按键_不能改设备层级()
    {
        using RecordingHost host = new();
        await using X11Server server = new(new X11ServerOptions { RestrictForwardedClients = true }, host);
        await using XTestClient local = await XTestClient.ConnectAsync(server);
        await using XTestClient forwarded = await XTestClient.ConnectAsync(server, label: "joe@remote:22");   // 经 ServeAuthenticatedAsync

        static async Task<bool> HasXTestAsync(XTestClient c) =>
            (await c.RequestAsync(98, 0, b => b.U16(5).U16(0).Bytes(Encoding.Latin1.GetBytes("XTEST")).Pad())).Bytes[8] == 1;
        Assert.IsTrue(await HasXTestAsync(local));
        Assert.IsFalse(await HasXTestAsync(forwarded), "伪造的输入与真实键盘无从区分");

        byte xi = await XiAsync(forwarded);
        byte xiLocal = await XiAsync(local);
        await SelectAsync(forwarded, xi, forwarded.RootWindow, 0, 1u << 13);   // RawKeyPress
        await SelectAsync(local, xiLocal, local.RootWindow, 0, 1u << 13);
        await forwarded.SyncAsync();
        await local.SyncAsync();
        server.InjectKey(38, pressed: true);
        await NextXiAsync(local, xiLocal, 13);
        await Assert.ThrowsAsync<OperationCanceledException>(() => NextXiAsync(forwarded, xi, 13, timeoutMs: 200), "不抢焦点就能记下所有按键");

        await forwarded.SendAsync(xi, 43, b => b.U8(1).U8(0).U8(0).U8(0).U16(4).U16(2).U16(4).U16(0));   // DetachSlave 4
        Assert.AreEqual(10, (await forwarded.NextAsync(m => m.IsError)).Detail, "BadAccess");
    }
}
