using System.Text;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>窗口管理器的角色:EWMH 根属性、窗口提示解析、经根窗口 ClientMessage 提出的请求。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class EwmhTests
{
    private static async Task<uint> InternAsync(XTestClient c, string name)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(name);
        XMessage m = await c.RequestAsync(16, 0, b => b.U16((ushort)bytes.Length).U16(0).Bytes(bytes).Pad());
        return m.U32(8);
    }

    private static async Task<uint> CreateTopAsync(XTestClient c)
    {
        uint id = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(id).U32(c.RootWindow).I16(0).I16(0).U16(100).U16(80).U16(0).U16(1).U32(0).U32(0));
        return id;
    }

    private static Task<ushort> SetCard32Async(XTestClient c, uint window, uint property, uint type, params uint[] values) =>
        c.SendAsync(18, 0, b =>
        {
            b.U32(window).U32(property).U32(type).U8(32).U8(0).U8(0).U8(0).U32((uint)values.Length);
            foreach (uint v in values)
            {
                b.U32(v);
            }
        });

    private static Task<ushort> RootClientMessageAsync(XTestClient c, uint window, uint type, params uint[] data) =>
        c.SendAsync(25, 0, b =>
        {
            b.U32(c.RootWindow).U32(0x180000)   // SubstructureNotify | SubstructureRedirect
                .U8(33).U8(32).U16(0).U32(window).U32(type);
            for (int i = 0; i < 5; i++)
            {
                b.U32(i < data.Length ? data[i] : 0);
            }
        });

    [TestMethod]
    public async Task 根窗口有检查窗口与EWMH支持列表()
    {
        await using X11Server server = new(new X11ServerOptions { ScreenWidth = 1280, ScreenHeight = 720 });
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint check = await InternAsync(c, "_NET_SUPPORTING_WM_CHECK");
        XMessage p = await c.RequestAsync(20, 0, b => b.U32(c.RootWindow).U32(check).U32(0).U32(0).U32(1));
        uint checkWindow = p.U32(32);
        XMessage self = await c.RequestAsync(20, 0, b => b.U32(checkWindow).U32(check).U32(0).U32(0).U32(1));
        Assert.AreEqual(checkWindow, self.U32(32), "检查窗口上的属性指向它自己");

        uint supported = await InternAsync(c, "_NET_SUPPORTED");
        uint moveresize = await InternAsync(c, "_NET_WM_MOVERESIZE");
        XMessage list = await c.RequestAsync(20, 0, b => b.U32(c.RootWindow).U32(supported).U32(0).U32(0).U32(100));
        uint[] atoms = [.. Enumerable.Range(0, (int)list.U32(16)).Select(i => list.U32(32 + (i * 4)))];
        CollectionAssert.Contains(atoms, moveresize);

        uint workarea = await InternAsync(c, "_NET_WORKAREA");
        XMessage area = await c.RequestAsync(20, 0, b => b.U32(c.RootWindow).U32(workarea).U32(0).U32(0).U32(4));
        Assert.AreEqual(1280u, area.U32(40));
    }

    [TestMethod]
    public async Task NET_WORKAREA扣掉显示器在桌面边缘让出来的任务栏_不合法的工作区当场拒绝()
    {
        await using X11Server server = new(new X11ServerOptions { ScreenWidth = 1280, ScreenHeight = 720 });
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint workarea = await InternAsync(c, "_NET_WORKAREA");
        async Task<(uint, uint, uint, uint)> AreaAsync()
        {
            XMessage m = await c.RequestAsync(20, 0, b => b.U32(c.RootWindow).U32(workarea).U32(0).U32(0).U32(4));
            return (m.U32(32), m.U32(36), m.U32(40), m.U32(44));
        }

        // 左边一台 1920×1080、任务栏在底部 40 像素;右边一台 1280×1440、Dock 在右边 60 像素。
        server.SetScreenLayout(3200, 1440,
        [
            new XMonitor(0, 0, 1920, 1080) { WorkArea = new XRect(0, 0, 1920, 1040) },
            new XMonitor(1920, 0, 1280, 1440) { WorkArea = new XRect(1920, 0, 1220, 1440) },
        ]);
        await c.SyncAsync();
        Assert.AreEqual((0u, 0u, 3140u, 1440u), await AreaAsync(),
            "右边的 Dock 在桌面边缘上扣掉;左边那台的底边不是桌面的底边,它的任务栏扣不出来(EWMH 只有一个矩形)。原先恒为整个根窗口");

        server.SetScreenLayout(1920, 1080, [new XMonitor(0, 0, 1920, 1080) { WorkArea = new XRect(0, 30, 1920, 1010) }]);
        await c.SyncAsync();
        Assert.AreEqual((0u, 30u, 1920u, 1010u), await AreaAsync(), "顶部面板 30、底部任务栏 40");

        Assert.ThrowsExactly<ArgumentException>(() => server.SetScreenLayout(1920, 1080,
            [new XMonitor(0, 0, 1920, 1080) { WorkArea = new XRect(0, 0, 2000, 1080) }]), "工作区超出显示器");
    }

    [TestMethod]
    public async Task 映射后有WM_STATE与客户端列表_提示解析进窗口快照()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await CreateTopAsync(c);
        uint motif = await InternAsync(c, "_MOTIF_WM_HINTS");
        uint type = await InternAsync(c, "_NET_WM_WINDOW_TYPE");
        uint dialog = await InternAsync(c, "_NET_WM_WINDOW_TYPE_DIALOG");
        await SetCard32Async(c, top, motif, motif, 2, 0, 0, 0, 0);                       // 不要装饰
        await SetCard32Async(c, top, type, 4, dialog);                                   // ATOM
        await SetCard32Async(c, top, 40, 41, 16 | 32, 0, 0, 0, 0, 200, 150, 800, 600, 0, 0, 0, 0, 0, 0, 0, 0, 0);   // WM_NORMAL_HINTS
        await c.SendAsync(8, 0, b => b.U32(top));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(top));
        XTopLevelWindow handle = host.Mapped[top];
        Assert.IsFalse(handle.Snapshot.Decorated);
        Assert.AreEqual(XWindowType.Dialog, handle.Snapshot.WindowType);
        Assert.AreEqual(200, handle.Snapshot.MinWidth);
        Assert.AreEqual(600, handle.Snapshot.MaxHeight);

        uint wmState = await InternAsync(c, "WM_STATE");
        XMessage state = await c.RequestAsync(20, 0, b => b.U32(top).U32(wmState).U32(0).U32(0).U32(2));
        Assert.AreEqual(1u, state.U32(32), "WM_STATE = Normal");
        uint clients = await InternAsync(c, "_NET_CLIENT_LIST");
        XMessage list = await c.RequestAsync(20, 0, b => b.U32(c.RootWindow).U32(clients).U32(0).U32(0).U32(10));
        Assert.AreEqual(top, list.U32(32));
    }

    [TestMethod]
    public async Task WM_NORMAL_HINTS的基准尺寸_宽高比_重力_USPosition解析进快照_数值夹进X的范围()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await CreateTopAsync(c);
        // flags:USPosition | PMinSize | PMaxSize | PResizeInc | PAspect | PBaseSize | PWinGravity
        const uint flags = 1 | 2 | 16 | 32 | 64 | 128 | 256 | 512;
        await SetCard32Async(c, top, 40, 41, flags, 0, 0, 0, 0,
            unchecked((uint)-5), 0x80000000, 800, 100000, 6, 13,   // 最小尺寸是负数、最大高度超出 X 的范围、步长 6×13
            4, 3, 16, 9,                                                // 宽高比 4:3 – 16:9
            19, 4, 9);                                                  // 基准尺寸 19×4、重力 SouthEast
        await c.SendAsync(8, 0, b => b.U32(top));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(top));
        XTopLevelSnapshot s = host.Mapped[top].Snapshot;
        Assert.AreEqual(0, s.MinWidth, "负数当 0:原先强转成负的最小尺寸");
        Assert.AreEqual(0, s.MinHeight, "0x80000000 是负的 INT32");
        Assert.AreEqual(short.MaxValue, s.MaxHeight, "超大值夹到 32767");
        Assert.AreEqual((6, 13), (s.WidthIncrement, s.HeightIncrement));
        Assert.AreEqual((19, 4), (s.BaseWidth, s.BaseHeight), "xterm 按字符格缩放的基准");
        Assert.AreEqual(4 / 3.0, s.MinAspect, 1e-9);
        Assert.AreEqual(16 / 9.0, s.MaxAspect, 1e-9);
        Assert.AreEqual(XGravity.SouthEast, s.WinGravity);
        Assert.IsTrue(s.UserPosition, "xterm -geometry +0+0:宿主要照 (0, 0) 摆,不能当成没给位置");
        Assert.IsFalse(s.ProgramPosition);
        Assert.IsTrue(s.UserSize, "xterm -geometry 80x24:宿主不拿记住的尺寸盖掉它");

        // 只给基准尺寸、没给最小尺寸:最小尺寸按基准尺寸(ICCCM §4.1.2.3);老程序的 15 个值的 WM_SIZE_HINTS 没有重力也照样认。
        await SetCard32Async(c, top, 40, 41, 4 | 256, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 30, 20, 0);
        await host.WaitForAsync(() => host.Mapped[top].Snapshot.MinWidth == 30);
        s = host.Mapped[top].Snapshot;
        Assert.AreEqual((30, 20), (s.BaseWidth, s.MinHeight));
        Assert.IsTrue(s.ProgramPosition);
        Assert.IsFalse(s.UserSize);
        Assert.AreEqual(XGravity.NorthWest, s.WinGravity, "没给重力:NorthWest");
        await SetCard32Async(c, top, 40, 41, 16 | 64, 0, 0, 0, 0, 50, 40, 0, 0, 7, 7, 0, 0, 0, 0);   // 15 个值
        await host.WaitForAsync(() => host.Mapped[top].Snapshot.MinWidth == 50);
        Assert.AreEqual((50, 7), (host.Mapped[top].Snapshot.BaseWidth, host.Mapped[top].Snapshot.WidthIncrement), "没给基准尺寸:按最小尺寸");
    }

    [TestMethod]
    public async Task WM_HINTS的initial_state为Iconic时映射即最小化_icon_pixmap烙成图标_window_group与Motif的functions进快照()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint leader = await CreateTopAsync(c);   // 组长:不映射
        uint top = await CreateTopAsync(c);
        // 4×2 的图标:深度 1 的像素图,左半边 1(黑)、右半边 0(白);掩码只露出第一行。
        uint icon = c.NewId(), mask = c.NewId(), gc = c.NewId();
        await c.SendAsync(53, 1, b => b.U32(icon).U32(c.RootWindow).U16(4).U16(2));
        await c.SendAsync(53, 1, b => b.U32(mask).U32(c.RootWindow).U16(4).U16(2));
        await c.SendAsync(55, 0, b => b.U32(gc).U32(icon).U32(0));
        await c.SendAsync(72, 2, b => b.U32(icon).U32(gc).U16(4).U16(2).I16(0).I16(0).U8(0).U8(1).U16(0)   // PutImage ZPixmap
            .U32(0b0011).U32(0b0011));                                                                       // 每行补到 32 位,低位在前
        await c.SendAsync(72, 2, b => b.U32(mask).U32(gc).U16(4).U16(2).I16(0).I16(0).U8(0).U8(1).U16(0).U32(0b1111).U32(0));
        // WM_HINTS:StateHint | IconPixmapHint | IconMaskHint | WindowGroupHint,initial_state = IconicState(3)。
        await SetCard32Async(c, top, 35, 35, 2 | 4 | 32 | 64, 0, 3, icon, 0, 0, 0, mask, leader);
        uint motif = await InternAsync(c, "_MOTIF_WM_HINTS");
        await SetCard32Async(c, top, motif, motif, 1, 1 | 2 | 16, 0, 0, 0);   // functions:ALL 除了改尺寸与最大化
        await c.SendAsync(8, 0, b => b.U32(top));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(top));
        await host.WaitForAsync(() => (host.Mapped[top].Snapshot.States & XWindowStates.Hidden) != 0);

        uint wmState = await InternAsync(c, "WM_STATE");
        XMessage state = await c.RequestAsync(20, 0, b => b.U32(top).U32(wmState).U32(0).U32(0).U32(2));
        Assert.AreEqual(3u, state.U32(32), "WM_STATE = IconicState:原先一律 Normal,xterm -iconic 不生效");

        XTopLevelSnapshot s = host.Mapped[top].Snapshot;
        XWindowIcon only = s.Icons.Single();
        Assert.AreEqual((4, 2), (only.Width, only.Height));
        Assert.AreEqual(0xFF000000u, only.Pixels.Span[0], "1 → 黑");
        Assert.AreEqual(0xFFFFFFFFu, only.Pixels.Span[3], "0 → 白");
        Assert.AreEqual(0u, only.Pixels.Span[4], "掩码为 0 处透明");
        Assert.AreEqual(leader, s.WindowGroup?.Id);
        Assert.AreEqual(XWindowFunctions.Move | XWindowFunctions.Minimize | XWindowFunctions.Close, s.Functions);

        // 有 _NET_WM_ICON 时用它。
        uint netIcon = await InternAsync(c, "_NET_WM_ICON");
        await SetCard32Async(c, top, netIcon, 6, 1, 1, 0xFF112233);
        await host.WaitForAsync(() => host.Mapped[top].Snapshot.Icons is [{ Width: 1 }]);
    }

    [TestMethod]
    public async Task WM_NAME按属性类型解码_UTF8_STRING与COMPOUND_TEXT的标题不再是乱码()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await CreateTopAsync(c);
        await c.SendAsync(8, 0, b => b.U32(top));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(top));
        Task<ushort> SetNameAsync(uint type, byte[] bytes) =>
            c.SendAsync(18, 0, b => b.U32(top).U32(39).U32(type).U8(8).U8(0).U8(0).U8(0).U32((uint)bytes.Length).Bytes(bytes).Pad());

        uint utf8 = await InternAsync(c, "UTF8_STRING");
        await SetNameAsync(utf8, Encoding.UTF8.GetBytes("终端 — xterm"));
        await host.WaitForAsync(() => host.Mapped[top].Snapshot.Title == "终端 — xterm");

        // COMPOUND_TEXT:Latin-1 原样、UTF-8 段(ESC % G … ESC % @)、不认识的多字节字符集(GB2312 在 GR)每个字符换成 U+FFFD。
        uint compound = await InternAsync(c, "COMPOUND_TEXT");
        byte[] text = [(byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)' ', 0x1B, (byte)'%', (byte)'G', .. Encoding.UTF8.GetBytes("文件"),
            0x1B, (byte)'%', (byte)'@', (byte)' ', 0x1B, (byte)'$', (byte)')', (byte)'A', 0xD6, 0xD0, 0xCE, 0xC4];
        await SetNameAsync(compound, text);
        await host.WaitForAsync(() => host.Mapped[top].Snapshot.Title.StartsWith("caf", StringComparison.Ordinal));
        Assert.AreEqual("café 文件 \uFFFD\uFFFD", host.Mapped[top].Snapshot.Title, "原先一律按 Latin-1 解,成了乱码");

        Assert.AreEqual("a é 中文 b", Protocol.XText.DecodeCompoundText(Protocol.XText.EncodeCompoundText("a é 中文 b")), "编码再解码回到原文");

        await SetNameAsync(31, [(byte)'x', 0xE9]);   // STRING:Latin-1
        await host.WaitForAsync(() => host.Mapped[top].Snapshot.Title == "xé");
    }

    [TestMethod]
    public async Task InputOnly的顶层不建像素缓冲_快照标着InputOnly_不进客户端列表()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint invisible = c.NewId();   // GtkInvisible:InputOnly、在屏幕外
        await c.SendAsync(1, 0, b => b.U32(invisible).U32(c.RootWindow).I16(-100).I16(-100).U16(2000).U16(2000).U16(0).U16(2).U32(0).U32(0));
        await c.SendAsync(8, 0, b => b.U32(invisible));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(invisible));
        XTopLevelWindow handle = host.Mapped[invisible];
        Assert.IsTrue(handle.Snapshot.InputOnly, "宿主据此不开原生窗口(原先多出一个黑窗口)");
        Assert.IsFalse(handle.ReadPixels((_, _, _) => Assert.Fail("不该有像素")), "没有像素缓冲");
        Assert.IsLessThan(1L << 20, (await server.GetClientsAsync()).Single().MemoryBytes, "2000×2000 的缓冲(16 MB)也不记账");

        uint clients = await InternAsync(c, "_NET_CLIENT_LIST");
        XMessage list = await c.RequestAsync(20, 0, b => b.U32(c.RootWindow).U32(clients).U32(0).U32(0).U32(10));
        Assert.AreEqual(0u, list.U32(16), "看不见的窗口不进客户端列表");

        await c.SendAsync(12, 0, b => b.U32(invisible).U16(0x4).U16(0).U32(500));   // 改尺寸照常
        await c.SendAsync(10, 0, b => b.U32(invisible));                            // 取消映射
        await host.WaitForAsync(() => !host.Mapped.ContainsKey(invisible));
    }

    [TestMethod]
    public async Task NET_WM_STATE请求交给宿主_宿主设状态后写回属性()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await CreateTopAsync(c);
        await c.SendAsync(8, 0, b => b.U32(top));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(top));
        uint netWmState = await InternAsync(c, "_NET_WM_STATE");
        uint fullscreen = await InternAsync(c, "_NET_WM_STATE_FULLSCREEN");

        await RootClientMessageAsync(c, top, netWmState, 1, fullscreen, 0, 1);   // add
        await host.WaitForAsync(() => host.Requests.OfType<XStateChangeRequest>().Any());
        XStateChangeRequest request = host.Requests.OfType<XStateChangeRequest>().First();
        Assert.AreEqual(XWindowStates.Fullscreen, request.Add);

        server.SetTopLevelStates(host.Mapped[top], XWindowStates.Fullscreen);
        await c.SyncAsync();
        XMessage p = await c.RequestAsync(20, 0, b => b.U32(top).U32(netWmState).U32(0).U32(0).U32(10));
        uint[] atoms = [.. Enumerable.Range(0, (int)p.U32(16)).Select(i => p.U32(32 + (i * 4)))];
        CollectionAssert.Contains(atoms, fullscreen);
        await host.WaitForAsync(() => (host.Mapped[top].Snapshot.States & XWindowStates.Fullscreen) != 0);
    }

    [TestMethod]
    public async Task NET_WM_MOVERESIZE交给宿主且释放按着的按钮()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await CreateTopAsync(c);
        await c.SendAsync(2, 0, b => b.U32(top).U32(0x800).U32(0x4 | 0x8 | 0x40));   // ButtonPress | ButtonRelease | PointerMotion
        await c.SendAsync(8, 0, b => b.U32(top));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(top));
        server.InjectPointerButton(host.Mapped[top], 10, 10, 1, pressed: true);
        await c.NextEventAsync(4);

        uint moveresize = await InternAsync(c, "_NET_WM_MOVERESIZE");
        await RootClientMessageAsync(c, top, moveresize, 10, 10, 8, 1, 1);   // Move,按钮 1
        await host.WaitForAsync(() => host.Requests.OfType<XMoveResizeRequest>().Any());
        XMoveResizeRequest request = host.Requests.OfType<XMoveResizeRequest>().First();
        Assert.AreEqual(XMoveResizeDirection.Move, request.Direction);
        Assert.AreEqual(1, request.Button);

        // 指针被窗口管理器接管:按钮 1 不再算按着(QueryPointer 的 mask 里没有 Button1)。
        XMessage pointer = await c.RequestAsync(38, 0, b => b.U32(top));
        Assert.AreEqual(0, pointer.U16(24) & 0x100);
    }

    [TestMethod]
    public async Task 别的客户端发NET_WM_MOVERESIZE不清按钮状态_不解除拖动方的抓取()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient dragger = await XTestClient.ConnectAsync(server);
        await using XTestClient other = await XTestClient.ConnectAsync(server, label: "别的会话");
        uint top = await CreateTopAsync(dragger);
        await dragger.SendAsync(2, 0, b => b.U32(top).U32(0x800).U32(0x4 | 0x8));   // ButtonPress | ButtonRelease
        await dragger.SendAsync(8, 0, b => b.U32(top));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(top));
        uint theirs = await CreateTopAsync(other);
        await other.SendAsync(8, 0, b => b.U32(theirs));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(theirs));
        server.InjectPointerButton(host.Mapped[top], 10, 10, 1, pressed: true);   // 用户在 dragger 的窗口里按住左键拖选
        await dragger.NextEventAsync(4);

        uint moveresize = await InternAsync(other, "_NET_WM_MOVERESIZE");
        await RootClientMessageAsync(other, theirs, moveresize, 10, 10, 8, 0, 1);   // 按钮 0
        await RootClientMessageAsync(other, top, moveresize, 10, 10, 8, 999, 1);    // 越界的按钮,对别人的窗口
        await other.SyncAsync();

        XMessage pointer = await dragger.RequestAsync(38, 0, b => b.U32(top));   // QueryPointer
        Assert.AreEqual(0x100, pointer.U16(24) & 0x100, "按钮 1 仍按着:原先被别的客户端一条消息清掉");
        server.InjectPointerButton(host.Mapped[top], 300, 300, 1, pressed: false);   // 拖到窗口外松开
        XMessage release = await dragger.NextEventAsync(5);
        Assert.AreEqual(top, release.U32(12), "抓取还在:松开照样送到拖动方");
    }

    [TestMethod]
    public async Task NET_ACTIVE_WINDOW只有用户操作引起的才标成UserInitiated_CurrentTime与过期的时间戳不算()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint active = await InternAsync(c, "_NET_ACTIVE_WINDOW");
        uint top = await CreateTopAsync(c);
        await c.SendAsync(2, 0, b => b.U32(top).U32(0x800).U32(0x4));   // ButtonPress
        await c.SendAsync(8, 0, b => b.U32(top));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(top));

        async Task<XActivateRequest> ActivateAsync(uint source, uint time)
        {
            int before = host.Requests.OfType<XActivateRequest>().Count();
            await RootClientMessageAsync(c, top, active, source, time, 0);
            await host.WaitForAsync(() => host.Requests.OfType<XActivateRequest>().Count() > before);
            return host.Requests.OfType<XActivateRequest>().Last();
        }

        Assert.IsFalse((await ActivateAsync(1, 0)).UserInitiated, "CurrentTime 说明不了是用户引起的:原先一律激活");
        Assert.IsTrue((await ActivateAsync(2, 0)).UserInitiated, "分页器 / 任务栏直接代表用户");

        await Task.Delay(20);
        server.InjectPointerButton(host.Mapped[top], 5, 5, 1, pressed: true);   // 用户在程序里点了一下
        uint pressed = (await c.NextEventAsync(4)).U32(4);
        XActivateRequest byClick = await ActivateAsync(1, pressed);
        Assert.IsTrue(byClick.UserInitiated, "时间戳就是那次点击的:是它引起的");
        Assert.AreEqual(1, byClick.Source);
        Assert.AreEqual(pressed, byClick.Timestamp);
        Assert.IsFalse((await ActivateAsync(1, pressed - 10)).UserInitiated, "早于用户最近一次操作:过期的时间戳");
    }

    [TestMethod]
    public async Task 根窗口的SubstructureRedirect与WM_S0由服务端占着_远端误跑的窗口管理器知道已经有了()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        await c.SendAsync(2, 0, b => b.U32(c.RootWindow).U32(0x800).U32(0x100000));   // ChangeWindowAttributes:SubstructureRedirect
        Assert.AreEqual(10, (await c.NextAsync(m => m.IsError)).Detail, "BadAccess:与真实桌面上已有窗口管理器时一样");

        uint wmS0 = await InternAsync(c, "WM_S0");
        Assert.AreNotEqual(0u, (await c.RequestAsync(23, 0, b => b.U32(wmS0))).U32(8), "WM_S0 有属主");
        uint check = await InternAsync(c, "_NET_SUPPORTING_WM_CHECK");
        uint name = await InternAsync(c, "_NET_WM_NAME");
        uint checkWindow = (await c.RequestAsync(20, 0, b => b.U32(c.RootWindow).U32(check).U32(0).U32(0).U32(1))).U32(32);
        XMessage wmName = await c.RequestAsync(20, 0, b => b.U32(checkWindow).U32(name).U32(0).U32(0).U32(16));
        Assert.AreEqual("LG3D", Encoding.UTF8.GetString(wmName.Bytes, 32, (int)wmName.U32(16)), "Java 认得的「不套外框」的名字");
    }

    /// <summary>
    /// 合成管理器(xs_plan F11):打开 CompositingManager 时服务端占着 _NET_WM_CM_S0(工具包据此用 ARGB 视觉画透明窗口与圆角),
    /// 属主就是 _NET_SUPPORTING_WM_CHECK 窗口;默认没人占(与原先一样)。
    /// </summary>
    [TestMethod]
    public async Task 打开合成管理器时服务端占着_NET_WM_CM_S0_默认没人占()
    {
        await using (X11Server plain = new())
        {
            await using XTestClient c = await XTestClient.ConnectAsync(plain);
            uint cm = await InternAsync(c, "_NET_WM_CM_S0");
            Assert.AreEqual(0u, (await c.RequestAsync(23, 0, b => b.U32(cm))).U32(8), "默认没有合成管理器");
        }

        await using X11Server server = new(new X11ServerOptions { CompositingManager = true });
        await using XTestClient client = await XTestClient.ConnectAsync(server);
        uint selection = await InternAsync(client, "_NET_WM_CM_S0");
        uint owner = (await client.RequestAsync(23, 0, b => b.U32(selection))).U32(8);
        uint check = await InternAsync(client, "_NET_SUPPORTING_WM_CHECK");
        uint checkWindow = (await client.RequestAsync(20, 0, b => b.U32(client.RootWindow).U32(check).U32(0).U32(0).U32(1))).U32(32);
        Assert.AreEqual(checkWindow, owner, "属主是服务端自己的隐藏窗口");
    }

    [TestMethod]
    public async Task 嵌入方断开时save_set里的窗口还回根窗口并补映射_不跟着被销毁()
    {
        await using X11Server server = new();
        XTestClient embedder = await XTestClient.ConnectAsync(server);
        await using XTestClient embedded = await XTestClient.ConnectAsync(server);
        uint frame = await CreateTopAsync(embedder);
        await embedder.SendAsync(8, 0, b => b.U32(frame));
        uint plug = await CreateTopAsync(embedded);
        await embedded.SyncAsync();

        // XEmbed 的做法:嵌入方把别人的窗口 reparent 进自己的外框、放进 save-set。
        await embedder.SendAsync(7, 0, b => b.U32(plug).U32(frame).I16(5).I16(5));   // ReparentWindow
        await embedder.SendAsync(6, 0, b => b.U32(plug));                             // ChangeSaveSet(Insert)
        await embedder.SyncAsync();
        XMessage before = await embedded.RequestAsync(15, 0, b => b.U32(plug));      // QueryTree
        Assert.AreEqual(frame, before.U32(12));

        Task serving = embedder.ServerTask;
        await embedder.DisposeAsync();   // 嵌入方崩溃
        await serving.WaitAsync(TimeSpan.FromSeconds(3));

        XMessage after = await embedded.RequestAsync(15, 0, b => b.U32(plug));
        Assert.IsTrue(after.IsReply, "原先 save-set 不生效:外框被销毁,别人的窗口跟着被销毁");
        Assert.AreEqual(embedded.RootWindow, after.U32(12), "还回到最近的一个不是嵌入方建的祖先");
        XMessage attributes = await embedded.RequestAsync(3, 0, b => b.U32(plug));   // GetWindowAttributes
        Assert.AreNotEqual(0, attributes.Bytes[26], "补映射了(map-state 不是 Unmapped)");
    }

    [TestMethod]
    public async Task NET_CLIENT_LIST按映射先后_焦点进了弹层不换活动窗口_withdraw时删掉过期的状态()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint clients = await InternAsync(c, "_NET_CLIENT_LIST"), active = await InternAsync(c, "_NET_ACTIVE_WINDOW");
        uint netWmState = await InternAsync(c, "_NET_WM_STATE"), desktop = await InternAsync(c, "_NET_WM_DESKTOP");
        uint first = await CreateTopAsync(c), second = await CreateTopAsync(c);   // first 的 XID 小
        await c.SendAsync(8, 0, b => b.U32(second));
        await c.SendAsync(8, 0, b => b.U32(first));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(first) && host.Mapped.ContainsKey(second));
        XMessage list = await c.RequestAsync(20, 0, b => b.U32(c.RootWindow).U32(clients).U32(0).U32(0).U32(10));
        Assert.AreEqual((second, first), (list.U32(32), list.U32(36)), "按第一次映射的先后(原先按 XID 排)");

        // 弹出菜单抓了键盘焦点:活动窗口还是主窗口,主窗口仍是 FOCUSED。
        server.FocusTopLevel(host.Mapped[second]);
        uint popup = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(popup).U32(c.RootWindow).I16(0).I16(0).U16(20).U16(20).U16(0).U16(1).U32(0).U32(0x200).U32(1));
        await c.SendAsync(8, 0, b => b.U32(popup));
        await c.SendAsync(42, 1, b => b.U32(popup).U32(0));   // SetInputFocus 到弹层
        Assert.AreEqual(popup, (await c.RequestAsync(43, 0)).U32(8));
        Assert.AreEqual(second, (await c.RequestAsync(20, 0, b => b.U32(c.RootWindow).U32(active).U32(0).U32(0).U32(1))).U32(32),
            "原先活动窗口挪到弹层上,主窗口画成非活动的样子");
        Assert.IsTrue((host.Mapped[second].Snapshot.States & XWindowStates.Focused) != 0);

        // 最小化之后取消映射(withdraw):_NET_WM_STATE 与 _NET_WM_DESKTOP 删掉;重新映射不再带着过期的 Hidden。
        server.SetTopLevelStates(host.Mapped[first], XWindowStates.Hidden);
        await c.SyncAsync();
        await c.SendAsync(10, 0, b => b.U32(first));
        await host.WaitForAsync(() => !host.Mapped.ContainsKey(first));
        foreach (uint property in (uint[])[netWmState, desktop])
        {
            Assert.AreEqual(0u, (await c.RequestAsync(20, 0, b => b.U32(first).U32(property).U32(0).U32(0).U32(10))).U32(8), "属性删掉了");
        }
        await c.SendAsync(8, 0, b => b.U32(first));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(first));
        Assert.AreEqual(XWindowStates.None, host.Mapped[first].Snapshot.States & XWindowStates.Hidden, "原先重新映射又是最小化的");
        list = await c.RequestAsync(20, 0, b => b.U32(c.RootWindow).U32(clients).U32(0).U32(0).U32(10));
        Assert.AreEqual((second, first), (list.U32(32), list.U32(36)), "重新映射的排到后面");
    }

    [TestMethod]
    public async Task 宿主移动窗口发真实的ConfigureNotify_根窗口上的监听者也收到_再补一条合成的_不回报宿主()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        await using XTestClient watcher = await XTestClient.ConnectAsync(server);   // 任务栏、xdotool behave 之类
        await watcher.SendAsync(2, 0, b => b.U32(watcher.RootWindow).U32(0x800).U32(0x80000));   // SubstructureNotify
        await watcher.SyncAsync();
        uint top = await CreateTopAsync(c);
        await c.SendAsync(2, 0, b => b.U32(top).U32(0x800).U32(0x20000));   // StructureNotify
        await c.SendAsync(8, 0, b => b.U32(top));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(top));
        int logBefore = host.Log.Count;

        server.MoveTopLevel(host.Mapped[top], 300, 200);
        XMessage seen = await watcher.NextAsync(m => m.EventCode == 22 && m.U32(8) == top);
        Assert.AreEqual(watcher.RootWindow, seen.U32(4), "根窗口上选了 SubstructureNotify 的收到(原先只有合成的那条发给窗口自己)");
        Assert.AreEqual((300, 200), (seen.I16(16), seen.I16(18)));
        Assert.AreEqual(0, seen.Bytes[0] & 0x80, "真实事件");
        XMessage real = await c.NextEventAsync(22);
        XMessage synthetic = await c.NextEventAsync(22);
        Assert.AreEqual(0, real.Bytes[0] & 0x80);
        Assert.AreEqual(0x80, synthetic.Bytes[0] & 0x80, "ICCCM §4.1.5 的合成事件跟在后面");
        await c.SyncAsync();
        Assert.IsFalse(host.Log.Skip(logBefore).Any(e => e.StartsWith($"changed {top:x}", StringComparison.Ordinal)), "宿主自己挪的,不回报");
    }

    [TestMethod]
    public async Task 弹出菜单抓着指针时用户点别的X窗口_按下送到抓取方_菜单关得掉()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient menuApp = await XTestClient.ConnectAsync(server);
        await using XTestClient other = await XTestClient.ConnectAsync(server);
        uint main = await CreateTopAsync(menuApp);
        await menuApp.SendAsync(8, 0, b => b.U32(main));
        uint popup = menuApp.NewId();   // override-redirect 的菜单,选了按钮事件
        await menuApp.SendAsync(1, 0, b => b.U32(popup).U32(menuApp.RootWindow).I16(10).I16(10).U16(30).U16(30).U16(0).U16(1).U32(0)
            .U32(0x200 | 0x800).U32(1).U32(0x4 | 0x8));
        await menuApp.SendAsync(8, 0, b => b.U32(popup));
        uint elsewhere = await CreateTopAsync(other);
        await other.SendAsync(2, 0, b => b.U32(elsewhere).U32(0x800).U32(0x4));
        await other.SendAsync(8, 0, b => b.U32(elsewhere));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(popup) && host.Mapped.ContainsKey(elsewhere));
        // 菜单弹出时 GrabPointer(owner-events False,按钮按下 / 松开)。
        XMessage grab = await menuApp.RequestAsync(26, 0, b => b.U32(popup).U16(0x4 | 0x8).U8(1).U8(1).U32(0).U32(0).U32(0));
        Assert.AreEqual(0, grab.Detail, "GrabSuccess");

        // 用户点了另一个程序的 X 窗口:按下照协议送到抓取窗口(坐标在菜单外面),菜单据此收起;那个窗口收不到。
        server.InjectPointerButton(host.Mapped[elsewhere], 50, 50, 1, pressed: true);
        XMessage press = await menuApp.NextEventAsync(4);
        Assert.AreEqual(popup, press.U32(12), "抓取窗口");
        await other.SyncAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => other.NextEventAsync(4, timeoutMs: 100));
    }

    [TestMethod]
    public async Task 焦点给了顶层就更新活动窗口与FOCUSED状态()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await CreateTopAsync(c);
        await c.SendAsync(8, 0, b => b.U32(top));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(top));
        server.FocusTopLevel(host.Mapped[top]);
        await c.SyncAsync();
        uint active = await InternAsync(c, "_NET_ACTIVE_WINDOW");
        XMessage p = await c.RequestAsync(20, 0, b => b.U32(c.RootWindow).U32(active).U32(0).U32(0).U32(1));
        Assert.AreEqual(top, p.U32(32));
        await host.WaitForAsync(() => (host.Mapped[top].Snapshot.States & XWindowStates.Focused) != 0);
    }

    [TestMethod]
    public async Task FocusTopLevel按ICCCM的输入模型_不动override_redirect与不收输入的窗口_WM_TAKE_FOCUS发给声明了它的客户端()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint protocols = await InternAsync(c, "WM_PROTOCOLS"), takeFocus = await InternAsync(c, "WM_TAKE_FOCUS");
        const uint wmHints = 35, atom = 4;

        async Task<(uint Window, XTopLevelWindow Handle)> MapAsync(bool overrideRedirect, bool? input, bool takesFocus)
        {
            uint id = c.NewId();
            await c.SendAsync(1, 0, b => b.U32(id).U32(c.RootWindow).I16(0).I16(0).U16(40).U16(30).U16(0).U16(1).U32(0)
                .U32(0x200).U32(overrideRedirect ? 1u : 0));
            if (input is { } value)
            {
                await SetCard32Async(c, id, wmHints, wmHints, 1, value ? 1u : 0, 0, 0, 0, 0, 0, 0, 0);   // flags = InputHint
            }
            if (takesFocus)
            {
                await SetCard32Async(c, id, protocols, atom, takeFocus);
            }
            await c.SendAsync(8, 0, b => b.U32(id));
            await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
            return (id, host.Mapped[id]);
        }

        async Task<uint> FocusAfterAsync(XTopLevelWindow handle)
        {
            server.FocusTopLevel(handle);
            return (await c.RequestAsync(43, 0)).U32(8);   // GetInputFocus
        }

        (_, XTopLevelWindow popup) = await MapAsync(overrideRedirect: true, input: null, takesFocus: false);
        Assert.AreEqual(1u, await FocusAfterAsync(popup), "override-redirect:焦点不动(还是 PointerRoot)");

        (_, XTopLevelWindow noInput) = await MapAsync(overrideRedirect: false, input: false, takesFocus: false);
        Assert.AreEqual(1u, await FocusAfterAsync(noInput), "No Input:焦点不动");

        (uint globallyActive, XTopLevelWindow global) = await MapAsync(overrideRedirect: false, input: false, takesFocus: true);
        Assert.AreEqual(1u, await FocusAfterAsync(global), "Globally Active:窗口管理器不设焦点");
        XMessage message = await c.NextEventAsync(33);
        Assert.AreEqual(globallyActive, message.U32(4));
        Assert.AreEqual(takeFocus, message.U32(12), "data[0] = WM_TAKE_FOCUS");
        Assert.AreNotEqual(0u, message.U32(16), "data[1] 是有效的时间戳,不是 CurrentTime");

        (uint locallyActive, XTopLevelWindow local) = await MapAsync(overrideRedirect: false, input: true, takesFocus: true);
        Assert.AreEqual(locallyActive, await FocusAfterAsync(local), "Locally Active:设焦点");
        Assert.AreEqual(locallyActive, (await c.NextEventAsync(33)).U32(4), "同时发 WM_TAKE_FOCUS");
    }
    [TestMethod]
    public async Task SetInputFocus按时间戳_宿主换了焦点之后迟到的旧请求不生效_客户端挪焦点到别的顶层时请宿主激活()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint protocols = await InternAsync(c, "WM_PROTOCOLS"), takeFocus = await InternAsync(c, "WM_TAKE_FOCUS");
        const uint wmHints = 35, atom = 4;

        async Task<(uint Window, XTopLevelWindow Handle)> MapAsync(bool takesFocus)
        {
            uint id = c.NewId();
            await c.SendAsync(1, 0, b => b.U32(id).U32(c.RootWindow).I16(0).I16(0).U16(40).U16(30).U16(0).U16(1).U32(0).U32(0));
            await SetCard32Async(c, id, wmHints, wmHints, 1, 1, 0, 0, 0, 0, 0, 0, 0);   // input = True
            if (takesFocus)
            {
                await SetCard32Async(c, id, protocols, atom, takeFocus);
            }
            await c.SendAsync(8, 0, b => b.U32(id));
            await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
            return (id, host.Mapped[id]);
        }

        async Task<uint> FocusAsync() => (await c.RequestAsync(43, 0)).U32(8);   // GetInputFocus

        (uint a, XTopLevelWindow handleA) = await MapAsync(takesFocus: true);   // Locally Active(GTK3 这类)
        (uint b, XTopLevelWindow handleB) = await MapAsync(takesFocus: false);
        server.FocusTopLevel(handleA);   // 用户点了 A:服务端给 A 发 WM_TAKE_FOCUS
        uint stamp = (await c.NextEventAsync(33)).U32(16);
        await Task.Delay(20);
        server.FocusTopLevel(handleB);   // 紧接着点了 B
        Assert.AreEqual(b, await FocusAsync());

        // A 对 WM_TAKE_FOCUS 的回应经 SSH 才到:时间戳早于宿主换焦点的时间,不生效。
        await c.SendAsync(42, 1, x => x.U32(a).U32(stamp));
        Assert.AreEqual(b, await FocusAsync(), "原先不看时间戳,焦点被拉回 A,而宿主上亮着的是 B");
        Assert.IsFalse(host.Requests.OfType<XFocusRequest>().Any());

        await c.SendAsync(42, 3, x => x.U32(a).U32(0));   // revert-to 只有 None / PointerRoot / Parent
        Assert.AreEqual(2, (await c.NextAsync(m => m.IsError)).Detail, "BadValue");

        // 客户端用 CurrentTime 把焦点挪到另一个顶层:生效,并请宿主激活它的原生窗口。
        await c.SendAsync(42, 1, x => x.U32(a).U32(0));
        Assert.AreEqual(a, await FocusAsync());
        await host.WaitForAsync(() => host.Requests.OfType<XFocusRequest>().Any(r => r.Window.Id == a));
    }
    [TestMethod]
    public async Task 服务端自己的窗口不能被reparent_改几何_改属性_映射_也就不会被连带销毁()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint checkAtom = await InternAsync(c, "_NET_SUPPORTING_WM_CHECK");
        uint check = (await c.RequestAsync(20, 0, b => b.U32(c.RootWindow).U32(checkAtom).U32(0).U32(0).U32(1))).U32(32);
        uint mine = await CreateTopAsync(c);

        async Task<byte?> ErrorOfAsync(byte opcode, byte data, Action<XTestClient.Body> body)
        {
            ushort seq = await c.SendAsync(opcode, data, body);
            await c.SyncAsync();
            try
            {
                return (await c.NextAsync(m => m.IsError && m.Sequence == seq, 100)).Detail;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        Assert.AreEqual((byte?)8, await ErrorOfAsync(7, 0, b => b.U32(check).U32(mine).I16(0).I16(0)), "ReparentWindow:BadMatch");
        Assert.AreEqual((byte?)8, await ErrorOfAsync(12, 0, b => b.U32(check).U16(0x4).U16(0).U32(500)), "ConfigureWindow:BadMatch");
        Assert.AreEqual((byte?)10, await ErrorOfAsync(2, 0, b => b.U32(check).U32(0x2).U32(0xFF0000)), "改背景:BadAccess");
        Assert.IsNull(await ErrorOfAsync(2, 0, b => b.U32(check).U32(0x800).U32(0x20000)), "选 StructureNotify 照常可以");
        Assert.AreEqual((byte?)8, await ErrorOfAsync(1, 0, b => b.U32(c.NewId()).U32(check).I16(0).I16(0).U16(1).U16(1).U16(0).U16(2).U32(0).U32(0)),
            "在它下面建窗口:BadMatch");
        await c.SendAsync(8, 0, b => b.U32(check));   // MapWindow:不理会
        await c.SendAsync(4, 0, b => b.U32(mine));    // DestroyWindow 自己的窗口
        await c.SyncAsync();
        Assert.IsEmpty(host.Mapped, "宿主没有多出一个原生窗口");
        Assert.IsTrue((await c.RequestAsync(3, 0, b => b.U32(check))).IsReply, "还在");
    }
    [TestMethod]
    public async Task NET_MOVERESIZE_WINDOW的宽高越界整个不理_坐标夹到16位()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint moveResize = await InternAsync(c, "_NET_MOVERESIZE_WINDOW");
        uint top = await CreateTopAsync(c);
        await c.SendAsync(8, 0, b => b.U32(top));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(top));
        XTopLevelWindow handle = host.Mapped[top];

        // 别的会话的客户端:x = 0x7FFFFFF0、宽 = 0x7FFFFFFF —— 原先原样交给 Configure。
        await RootClientMessageAsync(c, top, moveResize, 0xF00, 0x7FFFFFF0, 0, 0x7FFFFFFF, 50);
        await c.SyncAsync();
        XMessage geometry = await c.RequestAsync(14, 0, b => b.U32(top));   // GetGeometry
        Assert.AreEqual(100, geometry.U16(16), "宽高越界:整个请求不理");
        Assert.AreEqual(0, geometry.I16(12));

        await RootClientMessageAsync(c, top, moveResize, 0x300, 0x7FFFFFF0, unchecked((uint)-100000), 0, 0);   // 只给 x / y
        await host.WaitForAsync(() => handle.Snapshot.X == short.MaxValue);
        Assert.AreEqual(short.MinValue, handle.Snapshot.Y, "坐标夹到 16 位");
    }
}
