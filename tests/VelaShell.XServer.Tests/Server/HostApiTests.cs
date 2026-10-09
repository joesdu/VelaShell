using System.Text;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>宿主接口:运行中改的配置(DPI / 缩放、键位表)、选项校验、窗口快照与变化、光标、响铃、句柄的归属。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class HostApiTests
{
    private static async Task<uint> InternAsync(XTestClient c, string name)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(name);
        XMessage m = await c.RequestAsync(16, 0, b => b.U16((ushort)bytes.Length).U16(0).Bytes(bytes).Pad());
        return m.U32(8);
    }

    private static async Task<string> RootStringAsync(XTestClient c, string property)
    {
        uint atom = await InternAsync(c, property);
        XMessage p = await c.RequestAsync(20, 0, b => b.U32(c.RootWindow).U32(atom).U32(0).U32(0).U32(1000));
        return Encoding.Latin1.GetString(p.Bytes, 32, (int)p.U32(16));
    }

    private static async Task<byte> MajorAsync(XTestClient c, string extension)
    {
        byte[] name = Encoding.Latin1.GetBytes(extension);
        XMessage q = await c.RequestAsync(98, 0, b => b.U16((ushort)name.Length).U16(0).Bytes(name).Pad());
        Assert.AreEqual(1, q.Bytes[8], $"{extension} 应当存在");
        return q.Bytes[9];
    }

    private static async Task<uint> MapTopAsync(XTestClient c, RecordingHost host)
    {
        uint id = c.NewId();
        await c.SendAsync(1, 24, b => b.U32(id).U32(c.RootWindow).I16(0).I16(0).U16(60).U16(40).U16(0).U16(1).U32(0)
            .U32(0x2).U32(0));
        await c.SendAsync(8, 0, b => b.U32(id));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
        return id;
    }

    private static Task<ushort> SetTitleAsync(XTestClient c, uint window, string title)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(title);
        return c.SendAsync(18, 0, b => b.U32(window).U32(39).U32(31).U8(8).U8(0).U8(0).U8(0).U32((uint)bytes.Length).Bytes(bytes).Pad());
    }

    [TestMethod]
    public async Task SetDisplayScale更新Xft_dpi与XSETTINGS的缩放()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        Assert.Contains("Xft.dpi:\t96", await RootStringAsync(c, "RESOURCE_MANAGER"));
        await c.SendAsync(2, 0, b => b.U32(c.RootWindow).U32(0x800).U32(0x400000));   // PropertyChange
        await c.SyncAsync();

        server.SetDisplayScale(192, 2);
        XMessage notify = await c.NextEventAsync(28);
        Assert.AreEqual(c.RootWindow, notify.U32(4));
        Assert.Contains("Xft.dpi:\t192", await RootStringAsync(c, "RESOURCE_MANAGER"));

        uint selection = await InternAsync(c, "_XSETTINGS_S0");
        uint settings = await InternAsync(c, "_XSETTINGS_SETTINGS");
        uint manager = (await c.RequestAsync(23, 0, b => b.U32(selection))).U32(8);
        XMessage prop = await c.RequestAsync(20, 0, b => b.U32(manager).U32(settings).U32(0).U32(0).U32(1000));
        byte[] data = prop.Bytes[32..(32 + (int)prop.U32(16))];
        int at = Encoding.ASCII.GetString(data).IndexOf("Gdk/WindowScalingFactor", StringComparison.Ordinal);
        Assert.IsGreaterThan(0, at);
        Assert.AreEqual(2, BitConverter.ToInt32(data, at + 24 + 4), "名字 23 字节补到 24,再跳过 last-change-serial");
    }

    [TestMethod]
    public async Task SetKeymap一次换掉键值与布局名_客户端只收到一轮MappingNotify()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        await c.SyncAsync();
        // 德语布局的一角:键码 29 是 z / Z(QWERTZ)。
        server.SetKeymap(new XKeymap("de").Map(29, 'z', 'Z'));
        XMessage mapping = await c.NextEventAsync(34);
        Assert.AreEqual(1, mapping.Bytes[4], "request = Keyboard");
        Assert.AreEqual(29, mapping.Bytes[5], "从列出的最小键码起");

        XMessage keys = await c.RequestAsync(101, 0, b => b.U8(29).U8(1).U16(0));   // GetKeyboardMapping
        Assert.AreEqual('z', keys.U32(32));
        Assert.Contains("\0de\0", await RootStringAsync(c, "_XKB_RULES_NAMES"), "布局名跟着键位表一起到");
        await Assert.ThrowsAsync<OperationCanceledException>(() => c.NextEventAsync(34, timeoutMs: 300),
            "修饰键表没变:不该再有第二轮 MappingNotify");
    }

    [TestMethod]
    public async Task SetKeymap的AltGr把右Alt挪进Mod5()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        server.SetKeymap(new XKeymap("de", 6) { AltGr = true }.Map(24, 'q', 'Q', 'q', 'Q', '@', '@'));
        await c.NextEventAsync(34);
        XMessage modifiers = await c.RequestAsync(119, 0);   // GetModifierMapping
        int per = modifiers.Bytes[1];
        Assert.AreEqual(XKeycodes.AltRight, modifiers.Bytes[32 + (7 * per)], "Mod5 里有右 Alt");
        XMessage keys = await c.RequestAsync(101, 0, b => b.U8(XKeycodes.AltRight).U8(1).U16(0));
        Assert.AreEqual(0xfe03u, keys.U32(32), "ISO_Level3_Shift");
    }

    [TestMethod]
    public void 选项不合法时构造就抛异常()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => new X11Server(new X11ServerOptions { Dpi = 0 }));
        Assert.AreEqual("options", error.ParamName);
        Assert.Throws<ArgumentException>(() => new X11Server(new X11ServerOptions { ScreenWidth = 40_000 }));
        Assert.Throws<ArgumentException>(() => new X11Server(new X11ServerOptions { DisplayNumber = -1 }));
        Assert.Throws<ArgumentException>(() => new X11Server(new X11ServerOptions { Monitors = [new XMonitor(0, 0, 0, 10)] }));
    }

    [TestMethod]
    public async Task 显示器要落在根窗口里()
    {
        // 伸出根窗口的显示器:构造与运行中换布局都当场拒绝(原先照收,RANDR / XINERAMA 报出根窗口外的 CRTC)。
        Assert.Throws<ArgumentException>(() => new X11Server(new X11ServerOptions
        {
            ScreenWidth = 1920,
            ScreenHeight = 1080,
            Monitors = [new XMonitor(1000, 0, 1920, 1080)],
        }));
        await using X11Server server = new();
        Assert.Throws<ArgumentException>(() => server.SetScreenLayout(1920, 1080, [new XMonitor(-10, 0, 800, 600)]));
        Assert.Throws<ArgumentException>(() => server.SetScreenLayout(1920, 1080, [new XMonitor(0, 0, 800, 600) { WidthMillimeters = -1 }]));
        server.SetScreenLayout(3840, 1080, [new XMonitor(0, 0, 1920, 1080), new XMonitor(1920, 0, 1920, 1080)]);
    }

    [TestMethod]
    public async Task Display按实际监听的传输给出_没监听时为null()
    {
        await using X11Server server = new(new X11ServerOptions { DisplayNumber = 97, ListenTcp = false, UnixSocketPath = "" });
        Assert.AreEqual(97, server.DisplayNumber);
        Assert.IsNull(server.Display, "还没开始监听");
        await server.StartAsync();
        Assert.IsNull(server.Display, "TCP 与 Unix 套接字都关了:只能经 ServeAsync 喂流");
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.StartAsync());
    }

    [TestMethod]
    public async Task 宿主方法当场校验参数_别的服务端的窗口不收()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using X11Server other = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        XTopLevelWindow window = host.Mapped[await MapTopAsync(c, host)];

        Assert.Throws<ArgumentOutOfRangeException>(() => server.ResizeTopLevel(window, 0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => server.InjectPointerButton(window, 0, 0, 0, pressed: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => server.SetTopLevelFrameExtents(window, new XFrameExtents(-1, 0, 0, 0)));
        Assert.Throws<ArgumentException>(() => other.MoveTopLevel(window, 0, 0));
    }

    [TestMethod]
    public async Task 句柄带着发出它的服务端_窗口销毁或服务端收工之后不再活着()
    {
        using RecordingHost host = new();
        X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        XTopLevelWindow first = host.Mapped[await MapTopAsync(c, host)];
        XTopLevelWindow second = host.Mapped[await MapTopAsync(c, host)];
        Assert.AreSame(server, first.Server);
        Assert.IsTrue(first.IsAlive);

        await c.SendAsync(10, 0, b => b.U32(first.Id));   // 只是取消映射:句柄照样活着
        await host.WaitForAsync(() => !first.Snapshot.IsMapped);
        Assert.IsTrue(first.IsAlive);
        await c.SendAsync(4, 0, b => b.U32(first.Id));    // DestroyWindow
        await c.SyncAsync();
        Assert.IsFalse(first.IsAlive);

        Assert.IsTrue(second.IsAlive);
        await server.DisposeAsync();
        Assert.IsFalse(second.IsAlive, "服务端收工:句柄都不再活着");
    }

    [TestMethod]
    public async Task 取消映射之后缓冲还在_销毁或reparent走之后才读不到()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host);
        XTopLevelWindow window = host.Mapped[top];
        static void ignore(ReadOnlySpan<uint> _1, int _2, int _3) { }

        await c.SendAsync(10, 0, b => b.U32(top));   // UnmapWindow
        await host.WaitForAsync(() => !window.Snapshot.IsMapped);
        Assert.IsTrue(window.ReadPixels(ignore), "文档:取消映射不算没有缓冲");
        Assert.AreEqual((60, 40), window.CopyPixels(new uint[60 * 40]));

        uint parent = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(parent).U32(c.RootWindow).I16(0).I16(0).U16(10).U16(10).U16(0).U16(1).U32(0).U32(0));
        await c.SendAsync(7, 0, b => b.U32(top).U32(parent).I16(0).I16(0));   // ReparentWindow:不再是顶层
        await c.SyncAsync();
        Assert.IsFalse(window.ReadPixels(ignore));
        Assert.AreEqual(XPixelReadResult.NoBuffer, window.TryReadPixels(ignore, TimeSpan.FromSeconds(1)));
        Assert.AreEqual((0, 0), window.CopyPixels(new uint[1]));
    }

    [TestMethod]
    public async Task ChangeTopLevelStates只改给的那几个状态_客户端设的其余状态保留()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint id = c.NewId();
        await c.SendAsync(1, 24, b => b.U32(id).U32(c.RootWindow).I16(0).I16(0).U16(60).U16(40).U16(0).U16(1).U32(0).U32(0));
        uint state = await InternAsync(c, "_NET_WM_STATE");
        await SetCard32sAsync(c, id, state, 4, await InternAsync(c, "_NET_WM_STATE_SKIP_TASKBAR"), await InternAsync(c, "_NET_WM_STATE_STICKY"));
        await c.SendAsync(8, 0, b => b.U32(id));   // 映射前设好:不进任务栏、所有工作区
        await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
        XTopLevelWindow window = host.Mapped[id];
        Assert.AreEqual(XWindowStates.SkipTaskbar | XWindowStates.Sticky, window.Snapshot.States);

        server.ChangeTopLevelStates(window, XWindowStates.Maximized, XWindowStates.Hidden | XWindowStates.Fullscreen);   // 用户点了最大化
        await host.WaitForAsync(() => (window.Snapshot.States & XWindowStates.Maximized) != 0);
        Assert.AreEqual(XWindowStates.SkipTaskbar | XWindowStates.Sticky | XWindowStates.Maximized, window.Snapshot.States);

        server.ChangeTopLevelStates(window, XWindowStates.None, XWindowStates.Sticky);
        await host.WaitForAsync(() => (window.Snapshot.States & XWindowStates.Sticky) == 0);
        Assert.AreEqual(XWindowStates.SkipTaskbar | XWindowStates.Maximized, window.Snapshot.States);

        Assert.Throws<ArgumentException>(() => server.ChangeTopLevelStates(window, XWindowStates.Above, XWindowStates.Above));
        server.SetTopLevelStates(window, XWindowStates.Hidden);   // 整组覆盖
        await host.WaitForAsync(() => window.Snapshot.States == XWindowStates.Hidden);
    }

    [TestMethod]
    public async Task 第二个DisposeAsync等第一个收完_Completion在收完时完成()
    {
        using RecordingHost host = new();
        X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        await MapTopAsync(c, host);
        Assert.IsFalse(server.Completion.IsCompleted);

        Task first = server.DisposeAsync().AsTask();
        Task second = server.DisposeAsync().AsTask();
        await second.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(first.IsCompleted, "后来的调用等第一次收完才返回(原先立即返回)");
        Assert.IsTrue(server.Completion.IsCompletedSuccessfully);
        await server.DisposeAsync();   // 收完之后再调:立即返回
    }

    [TestMethod]
    public async Task StartAsync与DisposeAsync同时跑_不留下监听与套接字文件()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"vxs-race-{Guid.NewGuid():N}"[..20]);
        Directory.CreateDirectory(directory);
        try
        {
            for (int i = 0; i < 20; i++)
            {
                string path = Path.Combine(directory, $"X{i}");
                X11Server server = new(new X11ServerOptions { DisplayNumber = 90, ListenTcp = false, UnixSocketPath = path });
                using Barrier barrier = new(2);
                var start = Task.Run(() => { barrier.SignalAndWait(); return server.StartAsync(); });
                var dispose = Task.Run(async () => { barrier.SignalAndWait(); await server.DisposeAsync(); });
                await dispose;
                try
                {
                    await start;
                }
                catch (ObjectDisposedException)
                {
                    // 收工先到:不开了
                }
                Assert.IsFalse(File.Exists(path), $"第 {i} 次:套接字文件没人收");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task Serve一族与注入坐标的参数当场核对()
    {
        using RecordingHost host = new();
        X11Server server = new(host: host);
        await using (XTestClient c = await XTestClient.ConnectAsync(server))
        {
            XTopLevelWindow window = host.Mapped[await MapTopAsync(c, host)];
            Assert.Throws<ArgumentOutOfRangeException>(() => server.InjectPointerMotion(window, 40_000, 0), "与 MoveTopLevel 一样按 16 位核对");
            Assert.Throws<ArgumentOutOfRangeException>(() => server.InjectPointerButton(window, 0, -40_000, 1, pressed: true));
            Assert.Throws<ArgumentNullException>(() => server.ServeAsync(null!, isLocal: true), "当场抛,不放进任务里");
            Assert.Throws<ArgumentNullException>(() => server.ServeAuthenticatedAsync(null!, "label"));
        }
        await server.DisposeAsync();
        using MemoryStream stream = new();
        Assert.Throws<ObjectDisposedException>(() => server.ServeAsync(stream, isLocal: true));
        Assert.Throws<ObjectDisposedException>(() => server.ServeAuthenticatedAsync(stream));
    }

    [TestMethod]
    public async Task 窗口伸出根窗口左上时_注入的指针不被当成离开()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = c.NewId();
        await c.SendAsync(1, 24, b => b.U32(top).U32(c.RootWindow).I16(-40).I16(-30).U16(100).U16(100).U16(0).U16(1).U32(0)
            .U32(0x800).U32(0x40));   // PointerMotion
        await c.SendAsync(8, 0, b => b.U32(top));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(top));

        // 内区 (10, 50) 的根坐标是 (−30, 20):原先负的根坐标当成「指针离开」,移动事件全落到根窗口上。
        server.InjectPointerMotion(host.Mapped[top], 10, 50);
        XMessage motion = await c.NextEventAsync(6);
        Assert.AreEqual(top, motion.U32(12), "MotionNotify 的事件窗口是它");
    }

    [TestMethod]
    public async Task 快照整份替换_变化按组报告_没变不报()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host);
        XTopLevelWindow window = host.Mapped[top];
        XTopLevelSnapshot before = window.Snapshot;
        Assert.IsTrue(before.IsMapped);
        Assert.AreEqual(60, before.Width);

        await SetTitleAsync(c, top, "one");
        // 快照先换、回调随后才由 DeferredHost 交给宿主:等回调到了再看报的是哪一组。
        await host.WaitForAsync(() => window.Snapshot.Title == "one" && host.LastChanges == XTopLevelChanges.Title);
        Assert.AreEqual("", before.Title, "旧快照不变");

        await SetTitleAsync(c, top, "one");   // 值没变:不该报
        await c.SendAsync(12, 0, b => b.U32(top).U16(0xC).U16(0).U32(80).U32(50));   // ConfigureWindow 宽高
        await host.WaitForAsync(() => window.Snapshot.Width == 80 && host.LastChanges == XTopLevelChanges.Geometry);
        Assert.ContainsSingle(e => e.Contains(" Title ", StringComparison.Ordinal), host.Log, "同样的标题再设一遍不算变化");
    }

    [TestMethod]
    public async Task 交给宿主的标题去掉控制字符与双向排版控制符_过长的截断()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host);
        XTopLevelWindow window = host.Mapped[top];

        await SetTitleAsync(c, top, "a\u001Bb\u0007c\u0085d");
        await host.WaitForAsync(() => window.Snapshot.Title == "abcd");

        // _NET_WM_NAME(UTF-8):RLO 能把任务栏里的标题倒着显示。
        uint netWmName = await InternAsync(c, "_NET_WM_NAME"), utf8String = await InternAsync(c, "UTF8_STRING");
        byte[] spoof = Encoding.UTF8.GetBytes("invoice‮txt.exe");
        await c.SendAsync(18, 0, b => b.U32(top).U32(netWmName).U32(utf8String).U8(8).U8(0).U8(0).U8(0).U32((uint)spoof.Length).Bytes(spoof).Pad());
        await host.WaitForAsync(() => window.Snapshot.Title == "invoicetxt.exe");

        byte[] huge = Encoding.UTF8.GetBytes(new string('x', 20000));
        await c.SendAsync(18, 0, b => b.U32(top).U32(netWmName).U32(utf8String).U8(8).U8(0).U8(0).U8(0).U32((uint)huge.Length).Bytes(huge).Pad());
        await host.WaitForAsync(() => window.Snapshot.Title.Length == 4096);
    }

    /// <summary>ChangeProperty(Replace)写一串 32 位值。</summary>
    private static Task<ushort> SetCard32sAsync(XTestClient c, uint window, uint property, uint type, params uint[] values) =>
        c.SendAsync(18, 0, b =>
        {
            b.U32(window).U32(property).U32(type).U8(32).U8(0).U8(0).U8(0).U32((uint)values.Length);
            foreach (uint v in values)
            {
                b.U32(v);
            }
        });

    [TestMethod]
    public async Task 快照带上ICCCM的位置提示_重力_基准尺寸_宽高比_窗口组_边框宽_输入形状与STRUT()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host);
        XTopLevelWindow window = host.Mapped[top];
        Assert.AreEqual(XGravity.NorthWest, window.Snapshot.WinGravity, "没给 win_gravity:NorthWest");
        Assert.IsFalse(window.Snapshot.UserPosition);

        uint leader = c.NewId();   // 组长:一个不映射的顶层
        await c.SendAsync(1, 0, b => b.U32(leader).U32(c.RootWindow).I16(0).I16(0).U16(1).U16(1).U16(0).U16(1).U32(0).U32(0));
        byte[] cls = Encoding.Latin1.GetBytes("inst\0Cls\0");
        await c.SendAsync(18, 0, b => b.U32(top).U32(67).U32(31).U8(8).U8(0).U8(0).U8(0).U32((uint)cls.Length).Bytes(cls).Pad());   // WM_CLASS
        // WM_NORMAL_HINTS:USPosition | PPosition | PAspect | PBaseSize | PWinGravity;宽高比 4:3–16:9,基准 20×10,SouthEast。
        await SetCard32sAsync(c, top, 40, 41, 1 | 4 | 128 | 256 | 512, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4, 3, 16, 9, 20, 10, 9);
        await SetCard32sAsync(c, top, 35, 35, 64, 0, 0, 0, 0, 0, 0, 0, leader);   // WM_HINTS:WindowGroupHint
        await SetCard32sAsync(c, top, await InternAsync(c, "_NET_WM_STRUT_PARTIAL"), 6, 0, 0, 30, 0, 0, 0, 0, 0, 0, 100, 0, 0);
        await c.SendAsync(12, 0, b => b.U32(top).U16(0x10).U16(0).U32(3));   // ConfigureWindow:border-width 3
        byte shape = await MajorAsync(c, "SHAPE");
        await c.SendAsync(shape, 1, b => b.U8(0).U8(2).U8(0).U8(0).U32(top).I16(0).I16(0).I16(0).I16(0).U16(10).U16(10));   // 输入形状 10×10

        await host.WaitForAsync(() => window.Snapshot is { InputShape: not null, BorderWidth: 3, Strut.Top: 30, WindowGroup: not null });
        XTopLevelSnapshot s = window.Snapshot;
        Assert.AreEqual(("inst", "Cls"), (s.InstanceName, s.ClassName));
        Assert.IsTrue(s.UserPosition && s.ProgramPosition);
        Assert.AreEqual(XGravity.SouthEast, s.WinGravity);
        Assert.AreEqual((20, 10), (s.BaseWidth, s.BaseHeight));
        Assert.AreEqual(4 / 3.0, s.MinAspect, 1e-9);
        Assert.AreEqual(16 / 9.0, s.MaxAspect, 1e-9);
        Assert.AreEqual(leader, s.WindowGroup!.Id);
        Assert.AreEqual(new XFrameExtents(0, 0, 30, 0), s.Strut);
        Assert.AreSequenceEqual([new XRect(0, 0, 10, 10)], s.InputShape!.ToArray());
        Assert.IsNull(s.Shape, "边界形状没设");
    }

    [TestMethod]
    public void 按重力套外框_外框的参考点落在请求的位置()
    {
        XFrameExtents frame = new(4, 6, 30, 8);
        XTopLevelSnapshot s = new() { X = 100, Y = 50, BorderWidth = 2 };
        // NorthWest:外框左上角在 (100, 50),内容区在 (104, 80),X 窗口(边框外沿)再退一个边框宽。
        Assert.AreEqual((102, 78), s.PlaceInFrame(frame));
        // SouthEast:外框右下角落在 X 窗口(含边框)的右下角 (100 + w + 4, 50 + h + 4)。
        Assert.AreEqual((96, 44), (s with { WinGravity = XGravity.SouthEast }).PlaceInFrame(frame));
        // Center:左右各宽 4、6,中心对齐时内容区往左挪一个像素。
        Assert.AreEqual((99, 61), (s with { WinGravity = XGravity.Center }).PlaceInFrame(frame));
        Assert.AreEqual((100, 50), (s with { WinGravity = XGravity.Static }).PlaceInFrame(frame), "Static:内容区不动");
        Assert.AreEqual((98, 48), s.PlaceInFrame(default), "无装饰的外框直接包着内容区:X 的边框不画");
    }

    [TestMethod]
    public async Task 客户端给的位置要宿主摆_宿主报回之后不再要_客户端再挪又要()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host);
        XTopLevelWindow window = host.Mapped[top];
        Assert.IsTrue(window.Snapshot.NeedsPlacement, "建窗口时给的位置还没摆过");
        await c.SendAsync(2, 0, b => b.U32(top).U32(0x800).U32(0x20000));   // StructureNotify
        await c.SyncAsync();

        server.MoveTopLevel(window, 8, 31);   // 宿主按重力摆好了:外框在 (0, 0),内容区在 (8, 31)
        await c.NextEventAsync(22);           // 真实的 ConfigureNotify
        XMessage configure = await c.NextEventAsync(22);
        Assert.AreEqual(0x80, configure.Kind & 0x80, "ICCCM §4.1.5:再补一条合成的 ConfigureNotify");
        Assert.AreEqual(8, configure.I16(16));
        Assert.AreEqual(31, configure.I16(18));
        Assert.IsFalse(window.Snapshot.NeedsPlacement);

        await c.SendAsync(12, 0, b => b.U32(top).U16(0x3).U16(0).U32(8).U32(31));   // 挪到它已经在的地方:不算新请求
        await c.SyncAsync();
        Assert.IsFalse(window.Snapshot.NeedsPlacement);
        await c.SendAsync(12, 0, b => b.U32(top).U16(0x3).U16(0).U32(200).U32(100));
        // 快照先换、回调在执行线程放锁之后才交给宿主:等到宿主收到这一次的变化(只有几何)再算完。
        await host.WaitForAsync(() => window.Snapshot is { X: 200, NeedsPlacement: true }
            && host.Log.Any(e => e.StartsWith($"changed {top:x} Geometry ", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task NET_MOVERESIZE_WINDOW按重力让外框对准请求的位置()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host);
        XTopLevelWindow window = host.Mapped[top];
        server.SetTopLevelFrameExtents(window, new XFrameExtents(4, 6, 30, 8));
        uint moveResize = await InternAsync(c, "_NET_MOVERESIZE_WINDOW");
        Task Send(uint flags, int x, int y) => c.SendAsync(25, 0, b => b.U32(c.RootWindow).U32(0x180000)
            .U8(33).U8(32).U16(0).U32(top).U32(moveResize).U32(flags).U32(unchecked((uint)x)).U32(unchecked((uint)y)).U32(0).U32(0));

        // wmctrl -e 0,0,0,-1,-1:重力 0 用窗口自己的(NorthWest),外框左上角到 (0, 0),内容区在系统边框里面。
        await Send(0x300, 0, 0);
        await host.WaitForAsync(() => window.Snapshot.X == 4);
        Assert.AreEqual(30, window.Snapshot.Y);
        Assert.IsFalse(window.Snapshot.NeedsPlacement, "窗口管理器已经摆好,宿主照着摆即可");

        await Send(0x300 | 10, 50, 60);   // Static:内容区就在请求的位置
        await host.WaitForAsync(() => window.Snapshot.X == 50);
        Assert.AreEqual(60, window.Snapshot.Y);
    }

    [TestMethod]
    public async Task 位图光标连图像交给宿主_XFIXES起的名字推出形状()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host);

        uint pixmap = c.NewId(), cursor = c.NewId();
        await c.SendAsync(53, 1, b => b.U32(pixmap).U32(top).U16(16).U16(16));   // CreatePixmap,深度 1,全 0
        await c.SendAsync(93, 0, b => b.U32(cursor).U32(pixmap).U32(0)              // CreateCursor,没有 mask
            .U16(0xFFFF).U16(0).U16(0).U16(0).U16(0).U16(0xFFFF).U16(3).U16(4));
        await c.SendAsync(2, 0, b => b.U32(top).U32(0x4000).U32(cursor));           // ChangeWindowAttributes:cursor
        await c.SyncAsync();
        server.InjectPointerMotion(host.Mapped[top], 5, 5);
        await host.WaitForAsync(() => host.Cursor?.Image is not null);
        XCursorImage image = host.Cursor!.Image!;
        Assert.AreEqual((16, 16, 3, 4), (image.Width, image.Height, image.HotspotX, image.HotspotY));
        Assert.AreEqual(0xFF0000FFu, image.Pixels.Span[0], "source 为 0 的像素用背景色(蓝),不透明");
        Assert.AreEqual(XCursorShape.Arrow, host.Cursor.Shape, "没起名字:形状推不出来");

        byte xfixes = await MajorAsync(c, "XFIXES");
        byte[] name = Encoding.Latin1.GetBytes("text");
        await c.SendAsync(xfixes, 23, b => b.U32(cursor).U16((ushort)name.Length).U16(0).Bytes(name).Pad());   // SetCursorName
        await host.WaitForAsync(() => host.Cursor?.Shape == XCursorShape.Text);
        Assert.AreSame(image, host.Cursor!.Image, "图像不变");
    }

    /// <summary>建一个选了 ButtonPress 的顶层并映射。</summary>
    private static async Task<uint> MapClickableAsync(XTestClient c, RecordingHost host, short x, short y)
    {
        uint id = c.NewId();
        await c.SendAsync(1, 24, b => b.U32(id).U32(c.RootWindow).I16(x).I16(y).U16(100).U16(100).U16(0).U16(1).U32(0)
            .U32(0x800).U32(0x4 | 0x8));   // CWEventMask:ButtonPress | ButtonRelease
        await c.SendAsync(8, 0, b => b.U32(id));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
        return id;
    }

    [TestMethod]
    public async Task 两个顶层重叠时_指针按宿主给的那个原生窗口命中_激活时X里也抬上来()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint a = await MapClickableAsync(c, host, 0, 0);
        uint b = await MapClickableAsync(c, host, 50, 50);   // 后建的 B 在 X 的堆叠里压在 A 上面

        // 用户在 A 的原生窗口里、两窗重叠的地方点了一下:原先按 X 的堆叠落到看不见的 B 上。
        server.InjectPointerButton(host.Mapped[a], 60, 60, 1, pressed: true);
        XMessage press = await c.NextEventAsync(4);
        Assert.AreEqual(a, press.U32(12), "ButtonPress 的事件窗口是 A");
        server.InjectPointerButton(host.Mapped[a], 60, 60, 1, pressed: false);
        await c.NextEventAsync(5);

        // 宿主激活 A:X 里 A 也抬到 B 上面。
        server.FocusTopLevel(host.Mapped[a]);
        await c.SyncAsync();
        XMessage tree = await c.RequestAsync(15, 0, w => w.U32(c.RootWindow));
        List<uint> children = [.. Enumerable.Range(0, tree.U16(16)).Select(i => tree.U32(32 + (4 * i)))];
        Assert.IsGreaterThan(children.IndexOf(b), children.IndexOf(a), "A 在 B 之上");
    }

    [TestMethod]
    public async Task NET_WM_MOVERESIZE之后宿主补的松开不再投递()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapClickableAsync(c, host, 0, 0);
        XTopLevelWindow window = host.Mapped[top];
        server.InjectPointerButton(window, 10, 10, 1, pressed: true);
        await c.NextEventAsync(4);

        // GTK 的自绘标题栏被按下:请窗口管理器拖动,按钮交给了它。
        uint moveResize = await InternAsync(c, "_NET_WM_MOVERESIZE");
        await c.SendAsync(25, 0, b => b.U32(c.RootWindow).U32(0x180000).U8(33).U8(32).U16(0).U32(top).U32(moveResize)
            .U32(10).U32(10).U32(8).U32(1).U32(1));   // Move,按钮 1
        await host.WaitForAsync(() => host.Requests.OfType<XMoveResizeRequest>().Any());

        server.InjectPointerButton(window, 30, 30, 1, pressed: false);   // 宿主的拖动循环结束后补的松开
        await c.SyncAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => c.NextEventAsync(5, timeoutMs: 300), "按钮已经交给窗口管理器:不再有孤立的 ButtonRelease");

        server.InjectPointerButton(window, 10, 10, 1, pressed: true);   // 之后的点击照常成对
        server.InjectPointerButton(window, 10, 10, 1, pressed: false);
        await c.NextEventAsync(4);
        await c.NextEventAsync(5);
    }

    [TestMethod]
    public async Task 最小化的顶层不再接住指针_客户端抬高顶层时请宿主照办()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint a = await MapClickableAsync(c, host, 0, 0);
        uint b = await MapClickableAsync(c, host, 50, 50);

        // 指针在 A 的原生窗口里,但落在 A 之外、B 之内的地方(拖动时捕获着指针):全局找,B 最小化了就不算。
        server.InjectPointerMotion(host.Mapped[a], 120, 120);
        await c.SyncAsync();
        Assert.AreEqual(b, (await c.RequestAsync(38, 0, w => w.U32(c.RootWindow))).U32(12), "QueryPointer 的 child 是 B");
        server.SetTopLevelStates(host.Mapped[b], XWindowStates.Hidden);
        server.InjectPointerMotion(host.Mapped[a], 121, 121);
        await c.SyncAsync();
        Assert.AreEqual(0u, (await c.RequestAsync(38, 0, w => w.U32(c.RootWindow))).U32(12), "最小化的 B 不再接住指针");

        // XRaiseWindow(ConfigureWindow stack-mode = Above,没给 sibling)对顶层:宿主收到 XRaiseRequest。
        await c.SendAsync(12, 0, w => w.U32(a).U16(0x40).U16(0).U32(0));
        await host.WaitForAsync(() => host.Requests.Any(r => r is XRaiseRequest raise && raise.Window.Id == a));
    }

    [TestMethod]
    public async Task 最小化之后客户端MapWindow还原_请宿主去掉Hidden()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host);
        server.SetTopLevelStates(host.Mapped[top], XWindowStates.Hidden);   // 用户在宿主里最小化了它
        await c.SyncAsync();

        await c.SendAsync(8, 0, b => b.U32(top));   // Tk 的 wm deiconify:MapWindow
        await host.WaitForAsync(() => host.Requests.Any(r => r is XStateChangeRequest { Remove: XWindowStates.Hidden } s && s.Window.Id == top));
    }

    [TestMethod]
    public async Task 同一批里的窗口变化合并_映射了又取消的抵消()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host);

        // 3000 次改标题:原先每次一个 TopLevelChanged。
        await c.SendManyAsync(Enumerable.Range(0, 3000).Select<int, (byte, byte, Action<XTestClient.Body>?)>(i =>
        {
            byte[] title = Encoding.Latin1.GetBytes($"t{i}");
            return (18, 0, b => b.U32(top).U32(39).U32(31).U8(8).U8(0).U8(0).U8(0).U32((uint)title.Length).Bytes(title).Pad());
        }));
        await host.WaitForAsync(() => host.Mapped[top].Snapshot.Title == "t2999");
        await c.SyncAsync();
        int changes = host.Log.Count(e => e.StartsWith($"changed {top:x}", StringComparison.Ordinal));
        Assert.IsLessThan(300, changes, $"3000 次改标题交给宿主 {changes} 次");

        // 2000 对映射 / 取消映射:同一批里成对抵消,宿主几乎不必建、关原生窗口。
        uint other = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(other).U32(c.RootWindow).I16(0).I16(0).U16(20).U16(20).U16(0).U16(1).U32(0).U32(0));
        await c.SendManyAsync(Enumerable.Range(0, 4000).Select<int, (byte, byte, Action<XTestClient.Body>?)>(i =>
            (i % 2 == 0 ? (byte)8 : (byte)10, 0, b => b.U32(other))));
        await c.SyncAsync();
        await Task.Delay(50);
        int maps = host.Log.Count(e => e.StartsWith($"mapped {other:x}", StringComparison.Ordinal));
        Assert.IsLessThan(200, maps, $"2000 次映射交给宿主 {maps} 次");
        Assert.IsFalse(host.Mapped.ContainsKey(other), "最后是取消映射");
    }

    [TestMethod]
    public async Task 成批的响铃合并成一次_之后按时间节流()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        await c.SendManyAsync(Enumerable.Range(0, 5000).Select<int, (byte, byte, Action<XTestClient.Body>?)>(_ => (104, 0, null)));
        await c.SyncAsync();
        await Task.Delay(50);
        int delivered = host.Log.Count(e => e.StartsWith("bell", StringComparison.Ordinal));
        Assert.IsTrue(delivered is >= 1 and <= 3, $"5000 次响铃交给宿主 {delivered} 次");
    }

    [TestMethod]
    public async Task 响铃按协议从基准音量换算()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        int bells = 0;
        foreach (byte percent in (byte[])[0, 100, unchecked((byte)(sbyte)-50)])   // 0 → 基准 50;100 → 100;−50 → 50 − 25
        {
            await c.SendAsync(104, percent);
            await host.WaitForAsync(() => host.Log.Count(e => e.StartsWith("bell", StringComparison.Ordinal)) == bells + 1);
            bells++;
            await Task.Delay(150);   // 两次响铃之间至少隔 100 毫秒(节流)
        }
        Assert.AreSequenceEqual(["bell 50", "bell 100", "bell 25"], host.Log.Where(e => e.StartsWith("bell", StringComparison.Ordinal)).ToArray());

        XMessage error = await c.RequestAsync(104, 101);
        Assert.IsTrue(error.IsError, "−100…100 以外是 BadValue");
        Assert.AreEqual(2, error.Bytes[1]);
    }

    /// <summary>
    /// _NET_WM_SYNC_REQUEST(xs_plan F10):宿主改尺寸前先发同步请求(序号从 1 起,第一次把计数器设成 0),句柄的 AwaitingRedraw 为真;
    /// 客户端把计数器推到这个序号就报 TopLevelRedrawn;不推的过了时限也报。没声明的窗口照旧,不等。
    /// </summary>
    [TestMethod]
    public async Task 改尺寸前发同步请求_客户端推了计数器才算重画完_不推的到点也报()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        server.RedrawSyncTimeout = TimeSpan.FromMilliseconds(150);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte sync = await MajorAsync(c, "SYNC");
        uint counter = c.NewId();
        await c.SendAsync(sync, 2, b => b.U32(counter).I32(0).U32(42));   // CreateCounter,初值 42
        uint top = await MapTopAsync(c, host);
        uint protocols = await InternAsync(c, "WM_PROTOCOLS"), request = await InternAsync(c, "_NET_WM_SYNC_REQUEST");
        uint counterProperty = await InternAsync(c, "_NET_WM_SYNC_REQUEST_COUNTER");
        await c.SendAsync(18, 0, b => b.U32(top).U32(protocols).U32(4).U8(32).U8(0).U8(0).U8(0).U32(1).U32(request));
        await c.SendAsync(18, 0, b => b.U32(top).U32(counterProperty).U32(6).U8(32).U8(0).U8(0).U8(0).U32(1).U32(counter));   // CARDINAL
        await c.SendAsync(2, 0, b => b.U32(top).U32(0x800).U32(0x20000));   // StructureNotify
        await c.SyncAsync();
        XTopLevelWindow handle = host.Mapped[top];

        server.ResizeTopLevel(handle, 80, 50);
        XMessage message = await c.NextAsync(m => !m.IsReply && !m.IsError && (m.EventCode & 0x7F) is 33 or 22);
        Assert.AreEqual(33, message.EventCode & 0x7F, "同步请求先于 ConfigureNotify");
        Assert.AreEqual(request, message.U32(12));
        Assert.AreEqual(1u, message.U32(20), "序号的低 32 位");
        Assert.AreEqual(0u, message.U32(24), "高 32 位");
        Assert.IsTrue(handle.AwaitingRedraw);
        XMessage value = await c.RequestAsync(sync, 5, b => b.U32(counter));   // QueryCounter
        Assert.AreEqual(0u, value.U32(12), "第一次用时窗口管理器把计数器设成 0");

        await c.SendAsync(sync, 3, b => b.U32(counter).I32(0).U32(1));   // SetCounter 1:重画完了
        await host.WaitForAsync(() => !host.Redrawn.IsEmpty);
        Assert.IsFalse(handle.AwaitingRedraw);

        server.ResizeTopLevel(handle, 90, 60);   // 这次不推
        await host.WaitForAsync(() => host.Redrawn.Count >= 2, timeoutMs: 3000);
        Assert.IsFalse(handle.AwaitingRedraw, "到点不再等");
    }
}
