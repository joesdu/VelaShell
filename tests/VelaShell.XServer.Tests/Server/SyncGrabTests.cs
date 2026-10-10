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

    /// <summary>
    /// 指针同时被指针抓取与键盘抓取冻着:键盘抓取解除时指针照样冻着(原先每个设备只记一个冻结者,后冻的键盘抓取把指针抓取换掉,
    /// 它一解除指针就提前解冻了);同一个客户端冻了两次,一个 AsyncPointer 全放开。
    /// </summary>
    [TestMethod]
    public async Task 设备被两个抓取冻着_解除一个仍冻着_同一客户端的一个AllowEvents全放开()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host);
        server.InjectPointerMotion(host.Mapped[top], 1, 1);
        await c.RequestAsync(26, 0, b => b.U32(top).U16(0x40).U8(Synchronous).U8(Asynchronous).U32(0).U32(0).U32(0));   // 指针抓取冻指针
        await c.RequestAsync(31, 0, b => b.U32(top).U32(0).U8(Synchronous).U8(Asynchronous).U16(0));                     // 键盘抓取也冻指针
        await DrainAsync(c, MotionNotify);

        server.InjectPointerMotion(host.Mapped[top], 10, 10);
        await c.SendAsync(32, 0, b => b.U32(0));   // UngrabKeyboard
        Assert.IsEmpty(await DrainAsync(c, MotionNotify), "指针抓取还冻着指针");

        await c.RequestAsync(31, 0, b => b.U32(top).U32(0).U8(Synchronous).U8(Asynchronous).U16(0));   // 再冻一次
        await c.SendAsync(35, 0, b => b.U32(0));   // AsyncPointer:两次都是这个客户端冻的,一起放开
        Assert.HasCount(1, await DrainAsync(c, MotionNotify));
    }

    /// <summary>
    /// WarpPointer:src-window 与源矩形生效(原先读了就丢)、不存在的 src-window 回 BadWindow;结果夹在根窗口里、有 confine-to 时夹在那个窗口里;
    /// 指针冻着时 Warp 排在冻结的事件后面(原先越过排着的移动先到)。
    /// </summary>
    [TestMethod]
    public async Task WarpPointer按源矩形生效_夹在根窗口与confine_to里_冻结时排在队列里()
    {
        using RecordingHost host = new();
        await using X11Server server = new(new X11ServerOptions { ScreenWidth = 800, ScreenHeight = 600 }, host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host, eventMask: 0x40);   // 在 (0, 0),100 × 80
        server.InjectPointerMotion(host.Mapped[top], 50, 40);
        await DrainAsync(c, MotionNotify);
        async Task<(int X, int Y)> PointerAsync()
        {
            XMessage q = await c.RequestAsync(38, 0, b => b.U32(c.RootWindow));
            return (q.I16(16), q.I16(18));
        }
        Task<ushort> WarpAsync(uint src, short sx, short sy, ushort sw, ushort sh, uint dst, short dx, short dy) =>
            c.SendAsync(41, 0, b => b.U32(src).U32(dst).I16(sx).I16(sy).U16(sw).U16(sh).I16(dx).I16(dy));

        await WarpAsync(top, 0, 0, 10, 10, top, 5, 5);   // 指针 (50, 40) 不在源矩形 (0, 0, 10, 10) 里:不挪
        Assert.AreEqual((50, 40), await PointerAsync(), "源矩形之外不挪");
        await WarpAsync(top, 40, 30, 0, 0, top, 5, 5);   // 宽高为 0:换成窗口剩下的部分,指针在里面
        Assert.AreEqual((5, 5), await PointerAsync());
        XMessage bad = await c.RequestAsync(41, 0, b => b.U32(0x12345).U32(0).I16(0).I16(0).U16(0).U16(0).I16(0).I16(0));
        Assert.AreEqual(3, bad.Detail, "不存在的 src-window:BadWindow");

        await WarpAsync(0, 0, 0, 0, 0, c.RootWindow, -50, 9000);
        Assert.AreEqual((0, 599), await PointerAsync(), "夹在根窗口里(原先 −50 撞上「指针离开」)");

        await DrainAsync(c, MotionNotify);   // 前面几次 Warp 的移动
        // 带 confine-to 的指针抓取(同步):Warp 夹在 confine-to 里,而且排在冻结的移动后面。
        await c.RequestAsync(26, 0, b => b.U32(top).U16(0x40).U8(Synchronous).U8(Asynchronous).U32(top).U32(0).U32(0));
        server.InjectPointerMotion(host.Mapped[top], 20, 20);
        await WarpAsync(0, 0, 0, 0, 0, c.RootWindow, 500, 500);
        Assert.IsEmpty(await DrainAsync(c, MotionNotify), "冻着:Warp 也排队");
        await c.SendAsync(35, 0, b => b.U32(0));   // AsyncPointer
        List<XMessage> moves = await DrainAsync(c, MotionNotify);
        Assert.HasCount(2, moves);
        Assert.AreEqual(20, moves[0].I16(24), "先是排着的移动");
        Assert.AreEqual(99, moves[1].I16(24), "再是 Warp:夹在 confine-to 的右边缘");
        Assert.AreEqual(79, moves[1].I16(26));
    }

    /// <summary>
    /// 指针交给宿主(xs_plan F8):抓着指针的客户端 Warp 才告诉宿主挪系统光标(根坐标),没抓着的不报;带 confine-to 的抓取开始时报那个窗口的内区,
    /// 解除时报 null。
    /// </summary>
    [TestMethod]
    public async Task 抓着指针的客户端Warp才交给宿主_confine_to的范围开始与解除都报()
    {
        using RecordingHost host = new();
        await using X11Server server = new(new X11ServerOptions { ScreenWidth = 800, ScreenHeight = 600 }, host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        await using XTestClient other = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host);   // 在 (0, 0),100 × 80
        Task<ushort> WarpAsync(XTestClient client, short x, short y) =>
            client.SendAsync(41, 0, b => b.U32(0).U32(client.RootWindow).I16(0).I16(0).U16(0).U16(0).I16(x).I16(y));

        await WarpAsync(c, 300, 200);
        await c.SyncAsync();
        Assert.IsEmpty(host.Warps, "没抓着指针:不挪用户的鼠标");

        await c.RequestAsync(26, 0, b => b.U32(top).U16(0x40).U8(Asynchronous).U8(Asynchronous).U32(top).U32(0).U32(0));   // 带 confine-to
        await host.WaitForAsync(() => !host.Confinements.IsEmpty);
        Assert.AreEqual(new XRect(0, 0, 100, 80), host.Confinements.Last(), "confine-to 窗口的内区(根坐标)");
        await WarpAsync(c, 30, 20);
        await host.WaitForAsync(() => !host.Warps.IsEmpty);
        Assert.AreEqual((30, 20), host.Warps.Last());
        await WarpAsync(other, 60, 60);
        await other.SyncAsync();
        Assert.HasCount(1, host.Warps, "别的客户端挪:不报");

        await c.SendAsync(27, 0, b => b.U32(0));   // UngrabPointer
        await host.WaitForAsync(() => host.Confinements.Count >= 2);
        Assert.IsNull(host.Confinements.Last(), "解除");
    }

    /// <summary>重放的按下按事件之前的状态报:按钮 1 的位、Shift 自己的位都不在 state 里(原先重放时已经带上了)。</summary>
    [TestMethod]
    public async Task 重放的按下按事件之前的状态报_不带这次按下的按钮与修饰位()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient a = await XTestClient.ConnectAsync(server);
        await using XTestClient b = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(a, host);
        await b.SendAsync(2, 0, w => w.U32(top).U32(0x800).U32(0x1 | 0x4));   // B:KeyPress | ButtonPress
        await b.SyncAsync();
        await a.SendAsync(28, 0, w => w.U32(top).U16(0x4).U8(Synchronous).U8(Asynchronous).U32(0).U32(0).U8(1).U8(0).U16(0x8000));
        await a.SendAsync(33, 0, w => w.U32(top).U16(0x8000).U8(XKeycodes.ShiftLeft).U8(Asynchronous).U8(Synchronous).Pad());   // GrabKey(Shift_L)
        await a.SyncAsync();
        server.FocusTopLevel(host.Mapped[top]);

        server.InjectPointerButton(host.Mapped[top], 5, 5, 1, pressed: true);
        Assert.HasCount(1, await DrainAsync(a, ButtonPress));
        await a.SendAsync(35, 2, w => w.U32(0));   // ReplayPointer
        XMessage button = (await DrainAsync(b, ButtonPress)).Single();
        Assert.AreEqual(0, button.U16(28), "重放的 ButtonPress:state 里没有 Button1");
        server.InjectPointerButton(host.Mapped[top], 5, 5, 1, pressed: false);

        server.InjectKey(XKeycodes.ShiftLeft, pressed: true);
        Assert.HasCount(1, await DrainAsync(a, KeyPress));
        await a.SendAsync(35, 5, w => w.U32(0));   // ReplayKeyboard
        XMessage key = (await DrainAsync(b, KeyPress)).Single();
        Assert.AreEqual(0, key.U16(28), "重放的 Shift 按下:state 里没有 Shift");
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

    private static Task<XMessage> GrabPointerAsync(XTestClient c, uint window, uint confineTo = 0) =>
        c.RequestAsync(26, 0, b => b.U32(window).U16(0).U8(Asynchronous).U8(Asynchronous).U32(confineTo).U32(0).U32(0));

    private static Task<XMessage> GrabKeyboardAsync(XTestClient c, uint window) =>
        c.RequestAsync(31, 0, b => b.U32(window).U32(0).U8(Asynchronous).U8(Asynchronous).U16(0));

    [TestMethod]
    public async Task 抓取窗口变得不可见时抓取自动解除_confine_to不可见时回GrabNotViewable()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient a = await XTestClient.ConnectAsync(server);
        await using XTestClient b = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(a, host);
        uint child = a.NewId();
        await a.SendAsync(1, 0, x => x.U32(child).U32(top).I16(10).I16(10).U16(20).U16(20).U16(0).U16(1).U32(0).U32(0));
        await a.SendAsync(8, 0, x => x.U32(child));

        Assert.AreEqual(0, (await GrabPointerAsync(a, child)).Detail);
        Assert.AreEqual(0, (await GrabKeyboardAsync(a, child)).Detail);
        Assert.AreEqual(1, (await GrabPointerAsync(b, b.RootWindow)).Detail, "AlreadyGrabbed");
        Assert.AreEqual(1, (await GrabKeyboardAsync(b, b.RootWindow)).Detail, "AlreadyGrabbed");

        await a.SendAsync(10, 0, x => x.U32(top));   // UnmapWindow:子窗口随之不可见
        await a.SyncAsync();
        Assert.AreEqual(0, (await GrabPointerAsync(b, b.RootWindow)).Detail, "指针抓取随之解除");
        Assert.AreEqual(0, (await GrabKeyboardAsync(b, b.RootWindow)).Detail, "键盘抓取随之解除");

        await b.SendAsync(27, 0, x => x.U32(0));   // UngrabPointer
        Assert.AreEqual(3, (await GrabPointerAsync(b, b.RootWindow, confineTo: child)).Detail, "confine-to 不可见:GrabNotViewable");
    }

    /// <summary>收齐这段时间的 FocusIn / FocusOut / KeymapNotify,写成「in/out 窗口名 detail mode」与「keymap」。</summary>
    private static async Task<List<string>> FocusTraceAsync(XTestClient c, Dictionary<uint, string> names)
    {
        string[] details = ["Ancestor", "Virtual", "Inferior", "Nonlinear", "NonlinearVirtual", "Pointer", "PointerRoot", "None"];
        List<string> trace = [];
        foreach (XMessage m in await DrainAsync(c, 9, 10, 11))
        {
            trace.Add(m.EventCode == 11
                ? "keymap"
                : $"{(m.EventCode == 9 ? "in" : "out")} {names[m.U32(4)]} {details[m.Detail]}{(m.Bytes[8] == 0 ? "" : " mode" + m.Bytes[8])}");
        }
        return trace;
    }

    private static void AssertTrace(string[] expected, List<string> actual) =>
        Assert.AreSequenceEqual(expected, actual, string.Join(" | ", actual));

    [TestMethod]
    public async Task 焦点事件按上下级关系给detail_中间的窗口发虚拟事件_FocusIn之后跟KeymapNotify()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        const uint focusChange = 0x200000, keymapState = 0x4000;
        uint top = await MapTopAsync(c, host, focusChange);
        uint other = await MapTopAsync(c, host, focusChange);
        uint child = c.NewId(), grandchild = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(child).U32(top).I16(0).I16(0).U16(50).U16(50).U16(0).U16(1).U32(0).U32(0x800).U32(focusChange));
        await c.SendAsync(1, 0, b => b.U32(grandchild).U32(child).I16(0).I16(0).U16(20).U16(20).U16(0).U16(1).U32(0).U32(0x800).U32(focusChange | keymapState));
        await c.SendAsync(8, 0, b => b.U32(child));
        await c.SendAsync(8, 0, b => b.U32(grandchild));
        Dictionary<uint, string> names = new() { [top] = "T", [other] = "O", [child] = "C", [grandchild] = "G" };
        await DrainAsync(c, 9, 10, 11);

        Task SetFocusAsync(uint window) => c.SendAsync(42, 2, b => b.U32(window).U32(0));   // SetInputFocus,revert-to Parent
        // PointerRoot → G。指针在 (0, 0),落在最后映射的 O 里:旧焦点是 PointerRoot 时,从指针所在的窗口往上(连根)先发 Pointer;
        // 然后 G 的根往下到 G 之前是 NonlinearVirtual,G 本身 Nonlinear。
        await SetFocusAsync(grandchild);
        AssertTrace(["out O Pointer", "in T NonlinearVirtual", "in C NonlinearVirtual", "in G Nonlinear", "keymap"], await FocusTraceAsync(c, names));

        await SetFocusAsync(child);        // G 是 C 的下级
        AssertTrace(["out G Ancestor", "in C Inferior"], await FocusTraceAsync(c, names));

        await SetFocusAsync(grandchild);   // G 是 C 的下级,反过来
        AssertTrace(["out C Inferior", "in G Ancestor", "keymap"], await FocusTraceAsync(c, names));

        await SetFocusAsync(other);        // 共同祖先是根:两边的中间窗口都是 NonlinearVirtual
        AssertTrace(["out G Nonlinear", "out C NonlinearVirtual", "out T NonlinearVirtual", "in O Nonlinear"], await FocusTraceAsync(c, names));

        // 键盘抓取激活 / 解除:就像焦点从 O 移到抓取窗口 C、再移回来,mode 是 Grab(1)/ Ungrab(2)。
        await c.RequestAsync(31, 0, b => b.U32(child).U32(0).U8(Asynchronous).U8(Asynchronous).U16(0));
        AssertTrace(["out O Nonlinear mode1", "in T NonlinearVirtual mode1", "in C Nonlinear mode1"], await FocusTraceAsync(c, names));
        await c.SendAsync(32, 0, b => b.U32(0));   // UngrabKeyboard
        AssertTrace(["out C Nonlinear mode2", "out T NonlinearVirtual mode2", "in O Nonlinear mode2"], await FocusTraceAsync(c, names));
    }

    [TestMethod]
    public async Task 两个设备都冻着时排着的事件放行后按原来的先后_排队有上限()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host);
        XTopLevelWindow handle = host.Mapped[top];
        await c.RequestAsync(31, 0, b => b.U32(top).U32(0).U8(Asynchronous).U8(Asynchronous).U16(0));                        // GrabKeyboard
        await c.RequestAsync(26, 0, b => b.U32(top).U16(0x40).U8(Synchronous).U8(Synchronous).U32(0).U32(0).U32(0));        // GrabPointer:两个都冻上

        server.InjectKey(XKeycodes.A, pressed: true);
        server.InjectPointerMotion(handle, 10, 10);
        server.InjectKey(XKeycodes.A, pressed: false);
        Assert.IsEmpty(await DrainAsync(c, KeyPress, KeyRelease, MotionNotify), "冻着:一条都不发");

        await c.SendAsync(35, 6, b => b.U32(0));   // AllowEvents AsyncBoth
        List<XMessage> events = await DrainAsync(c, KeyPress, KeyRelease, MotionNotify);
        Assert.AreSequenceEqual([KeyPress, MotionNotify, KeyRelease], events.Select(e => e.EventCode).ToArray(), "按键、移动、松开:按到达的先后,不是两个设备各走各的");

        // 再冻上,宿主不停地移动:排着的事件有上限。
        await c.RequestAsync(26, 0, b => b.U32(top).U16(0x40).U8(Synchronous).U8(Asynchronous).U32(0).U32(0).U32(0));
        for (int i = 0; i < X11Server.MaxFrozenInput + 500; i++)
        {
            server.InjectPointerMotion(handle, i % 90, 5);
        }
        Assert.AreEqual(X11Server.MaxFrozenInput, await server.InvokeAsync(() => server.FrozenInputCount));
    }

    /// <summary>
    /// 抓取请求看时间戳与冻结者(协议「GrabPointer」「GrabKeyboard」「UngrabPointer」「AllowEvents」):设备被别的客户端冻着回 Frozen,
    /// 时间戳晚于当前服务端时间回 InvalidTime;早于上次抓取时间的 UngrabPointer / AllowEvents 不生效。原先一律不看。
    /// </summary>
    [TestMethod]
    public async Task 设备被别的客户端冻着时回Frozen_时间戳不对回InvalidTime_过期的Ungrab与AllowEvents不生效()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient a = await XTestClient.ConnectAsync(server);
        await using XTestClient b = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(a, host);
        server.InjectPointerMotion(host.Mapped[top], 1, 1);
        await Task.Delay(20);   // 服务端时间走过几毫秒:下面的时间戳 1 一定早于抓取时间

        // A 的指针抓取把键盘也冻上;B 抓键盘:Frozen(原先照样成功,还把冻结者换成了自己)。
        XMessage grab = await a.RequestAsync(26, 0, w => w.U32(top).U16(0x40).U8(Synchronous).U8(Synchronous).U32(0).U32(0).U32(0));
        Assert.AreEqual(0, grab.Bytes[1]);
        XMessage frozen = await b.RequestAsync(31, 0, w => w.U32(top).U32(0).U8(Asynchronous).U8(Asynchronous).U16(0));
        Assert.AreEqual(4, frozen.Bytes[1], "Frozen");

        // 晚于当前服务端时间:InvalidTime。
        XMessage future = await a.RequestAsync(31, 0, w => w.U32(top).U32(0x7FFF_0000).U8(Asynchronous).U8(Asynchronous).U16(0));
        Assert.AreEqual(2, future.Bytes[1], "InvalidTime");

        // 早于这次抓取的 AllowEvents 与 UngrabPointer 不生效:指针还冻着、抓取还在。
        server.InjectPointerMotion(host.Mapped[top], 10, 10);
        await a.SendAsync(35, 0, w => w.U32(1));   // AllowEvents AsyncPointer,时间戳 1
        Assert.IsEmpty(await DrainAsync(a, MotionNotify), "过期的 AllowEvents 不放行");
        await a.SendAsync(27, 0, w => w.U32(1));   // UngrabPointer,时间戳 1
        await a.SyncAsync();
        XMessage still = await b.RequestAsync(26, 0, w => w.U32(top).U16(0x40).U8(Asynchronous).U8(Asynchronous).U32(0).U32(0).U32(0));
        Assert.AreEqual(1, still.Bytes[1], "A 的抓取还在:AlreadyGrabbed");

        await a.SendAsync(35, 6, w => w.U32(0));   // AsyncBoth,CurrentTime
        await a.NextEventAsync(MotionNotify);   // 当前时间的 AllowEvents 照常放行(排着的事件由后续工作项回放,等它到,不按 80 毫秒的窗口数)
        Assert.IsEmpty(await DrainAsync(a, MotionNotify), "只排着一个移动");
        await a.SendAsync(27, 0, w => w.U32(0));
        await a.SyncAsync();   // A 与 B 是两条连接:先确认 A 的 UngrabPointer 执行过,B 的请求才不会抢在它前面
        XMessage after = await b.RequestAsync(26, 0, w => w.U32(top).U16(0x40).U8(Asynchronous).U8(Asynchronous).U32(0).U32(0).U32(0));
        Assert.AreEqual(0, after.Bytes[1], "当前时间的 UngrabPointer 照常解除");
    }

    /// <summary>
    /// 冻结的队列被按键塞满:新来的按下丢掉(连同它的松开),松开一律留着 —— 原先连松开也丢,解冻之后 a 一直按着。
    /// </summary>
    [TestMethod]
    public async Task 冻结队列满时丢新来的按下_松开一律留着_解冻后键不卡住()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host);
        server.FocusTopLevel(host.Mapped[top]);
        XMessage grab = await c.RequestAsync(31, 0, b => b.U32(top).U32(0).U8(Asynchronous).U8(Synchronous).U16(0));
        Assert.AreEqual(0, grab.Bytes[1]);

        server.InjectKey(XKeycodes.A, pressed: true);
        for (int i = 0; i < (X11Server.MaxFrozenInput / 2) + 100; i++)
        {
            server.InjectKey(XKeycodes.S, pressed: true);
            server.InjectKey(XKeycodes.S, pressed: false);
        }
        server.InjectKey(XKeycodes.D, pressed: true);    // 队列满了:丢掉
        server.InjectKey(XKeycodes.A, pressed: false);   // 松开:留着
        server.InjectKey(XKeycodes.D, pressed: false);   // 它的按下丢了:一并丢掉
        int queued = await server.InvokeAsync(() => server.FrozenInputCount);
        Assert.AreEqual(X11Server.MaxFrozenInput + 2, queued, "满了之后只多出按下已经排进去的松开:正好填满时的那个 s 与 a");

        await c.SendAsync(35, 3, b => b.U32(0));   // AllowEvents AsyncKeyboard
        for (int i = 0; i < 200 && await server.InvokeAsync(() => server.FrozenInputCount) > 0; i++)
        {
            await Task.Delay(10);
        }
        XMessage keymap = await c.RequestAsync(44, 0);   // QueryKeymap
        Assert.IsFalse(keymap.Bytes[8..40].Any(b => b != 0), "解冻之后没有键一直按着");
    }

    [TestMethod]
    public async Task 抓取期间Enter与Leave只报给抓取方_激活与解除时发Grab与Ungrab模式的crossing()
    {
        const byte EnterNotify = 7, LeaveNotify = 8;
        const uint enterLeave = 0x10 | 0x20;
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient a = await XTestClient.ConnectAsync(server);
        await using XTestClient b = await XTestClient.ConnectAsync(server);

        async Task<uint> MapAtAsync(short x)
        {
            uint id = a.NewId();
            await a.SendAsync(1, 0, w => w.U32(id).U32(a.RootWindow).I16(x).I16(0).U16(100).U16(80).U16(0).U16(1).U32(0).U32(0x800).U32(enterLeave));
            await a.SendAsync(8, 0, w => w.U32(id));
            await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
            return id;
        }
        uint t = await MapAtAsync(0), u = await MapAtAsync(300);
        await b.SendAsync(2, 0, w => w.U32(u).U32(0x800).U32(enterLeave));   // B 也在 U 上选了 Enter / Leave
        server.InjectPointerMotion(host.Mapped[t], 5, 5);
        await DrainAsync(a, EnterNotify, LeaveNotify);
        await DrainAsync(b, EnterNotify, LeaveNotify);

        // A 在 U 上抓指针(owner-events False,抓取掩码 Enter | Leave):就像指针从 T 瞬移到 U,mode = Grab;只报抓取窗口 U 上的。
        await a.RequestAsync(26, 0, w => w.U32(u).U16((ushort)enterLeave).U8(Asynchronous).U8(Asynchronous).U32(0).U32(0).U32(0));
        List<XMessage> grabbed = await DrainAsync(a, EnterNotify, LeaveNotify);
        Assert.HasCount(1, grabbed, "只有抓取窗口上的那一个");
        Assert.AreEqual(EnterNotify, grabbed[0].EventCode);
        Assert.AreEqual(u, grabbed[0].U32(12));
        Assert.AreEqual(1, grabbed[0].Bytes[30], "mode = Grab");

        server.InjectPointerMotion(host.Mapped[u], 5, 5);   // 真的挪进 U:照常的 crossing,但只报给抓取方
        Assert.AreEqual(EnterNotify, (await DrainAsync(a, EnterNotify, LeaveNotify)).Single().EventCode);
        Assert.IsEmpty(await DrainAsync(b, EnterNotify, LeaveNotify), "抓取期间别的客户端收不到");

        server.InjectPointerMotion(host.Mapped[t], 5, 5);
        await DrainAsync(a, EnterNotify, LeaveNotify);
        await a.SendAsync(27, 0, w => w.U32(0));             // UngrabPointer:就像指针从 U 瞬移回 T,mode = Ungrab,照常报给所有人
        List<XMessage> released = await DrainAsync(b, EnterNotify, LeaveNotify);
        Assert.HasCount(1, released);
        Assert.AreEqual(LeaveNotify, released[0].EventCode);
        Assert.AreEqual(2, released[0].Bytes[30], "mode = Ungrab");
    }

    [TestMethod]
    public async Task 自动抓取也发Grab与Ungrab模式的crossing_在A里按下拖到B松开_B收到Ungrab的Enter()
    {
        const byte EnterNotify = 7, LeaveNotify = 8;
        const uint enterLeave = 0x10 | 0x20, buttons = 0x4 | 0x8;
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient a = await XTestClient.ConnectAsync(server);
        await using XTestClient b = await XTestClient.ConnectAsync(server);

        async Task<uint> MapAtAsync(XTestClient c, short x, uint mask)
        {
            uint id = c.NewId();
            await c.SendAsync(1, 0, w => w.U32(id).U32(c.RootWindow).I16(x).I16(0).U16(100).U16(80).U16(0).U16(1).U32(0).U32(0x800).U32(mask));
            await c.SendAsync(8, 0, w => w.U32(id));
            await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
            return id;
        }
        uint t = await MapAtAsync(a, 0, enterLeave | buttons);
        uint u = await MapAtAsync(b, 300, enterLeave);
        uint child = a.NewId();   // T 里的子窗口:只选 Enter / Leave,按下的事件传到 T,自动抓取的窗口是 T
        await a.SendAsync(1, 0, w => w.U32(child).U32(t).I16(10).I16(10).U16(30).U16(30).U16(0).U16(1).U32(0).U32(0x800).U32(enterLeave));
        await a.SendAsync(8, 0, w => w.U32(child));
        server.InjectPointerMotion(host.Mapped[t], 20, 20);
        await DrainAsync(a, EnterNotify, LeaveNotify);
        await DrainAsync(b, EnterNotify, LeaveNotify);

        // 激活:就像指针从子窗口瞬移到 T,mode = Grab,在 ButtonPress 之前;只报抓取窗口 T 上的。
        server.InjectPointerButton(host.Mapped[t], 20, 20, 1, pressed: true);
        List<XMessage> pressed = await DrainAsync(a, EnterNotify, LeaveNotify, ButtonPress);
        CollectionAssert.AreEqual(new[] { EnterNotify, ButtonPress }, pressed.Select(m => m.EventCode).ToArray());
        Assert.AreEqual(t, pressed[0].U32(12));
        Assert.AreEqual(1, pressed[0].Bytes[30], "mode = Grab");

        server.InjectPointerMotion(host.Mapped[u], 5, 5);   // 按着拖进 B 的窗口:抓取期间 B 收不到 Enter
        await DrainAsync(a, EnterNotify, LeaveNotify);     // 抓取方照常收到离开 T 的 Normal crossing
        Assert.IsEmpty(await DrainAsync(b, EnterNotify, LeaveNotify));

        // 解除:ButtonRelease 之后,就像指针从 T 瞬移到它实际所在的 U,mode = Ungrab,照常报给所有人。
        server.InjectPointerButton(host.Mapped[u], 5, 5, 1, pressed: false);
        List<XMessage> released = await DrainAsync(b, EnterNotify, LeaveNotify);
        Assert.HasCount(1, released, "原先自动抓取解除时不发,B 一直不知道指针在它里面");
        Assert.AreEqual(EnterNotify, released[0].EventCode);
        Assert.AreEqual(u, released[0].U32(12));
        Assert.AreEqual(2, released[0].Bytes[30], "mode = Ungrab");
        List<XMessage> owner = await DrainAsync(a, ButtonRelease, LeaveNotify);
        CollectionAssert.AreEqual(new[] { ButtonRelease, LeaveNotify }, owner.Select(m => m.EventCode).ToArray(), "Ungrab 的 crossing 在 ButtonRelease 之后");
    }

    [TestMethod]
    public async Task 键盘被抓着且owner_events为True时按键照常按焦点报告_不是按指针所在的窗口()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);

        async Task<uint> MapAtAsync(short x)
        {
            uint id = c.NewId();
            await c.SendAsync(1, 0, b => b.U32(id).U32(c.RootWindow).I16(x).I16(0).U16(100).U16(80).U16(0).U16(1).U32(0).U32(0x800).U32(0x1));   // KeyPress
            await c.SendAsync(8, 0, b => b.U32(id));
            await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
            return id;
        }
        uint focused = await MapAtAsync(0), pointed = await MapAtAsync(300);
        server.FocusTopLevel(host.Mapped[focused]);
        server.InjectPointerMotion(host.Mapped[pointed], 5, 5);   // 指针在另一个窗口里
        await c.RequestAsync(31, 1, b => b.U32(c.RootWindow).U32(0).U8(Asynchronous).U8(Asynchronous).U16(0));   // GrabKeyboard,owner-events True

        server.InjectKey(XKeycodes.A, pressed: true);
        XMessage key = await c.NextEventAsync(KeyPress);
        Assert.AreEqual(focused, key.U32(12), "event = 焦点窗口");
    }
}
