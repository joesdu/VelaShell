using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using VelaShell.Services.XServer;
using VelaShell.Ssh.Transport;
using VelaShell.Views.XServer;
using VelaShell.XServer;

namespace VelaShell.Tests.XServer;

/// <summary>
/// 内置 X 服务端的 Avalonia 宿主:X 客户端映射一个窗口 → 出现一个原生窗口,尺寸、标题、像素都对;
/// 点关闭按钮 → 请客户端关(没声明 WM_DELETE_WINDOW 就断开它)→ 原生窗口随之收掉。
/// 客户端是手拼的最小 X 协议(小端),经内存双工流直接接进 <see cref="X11Server.ServeAsync" />。
/// </summary>
[TestClass]
[TestCategory("XServerHostUi")]
public sealed class AvaloniaXServerHostUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AvaloniaXServerHostUiTests).Assembly);

    [TestMethod]
    public void InputMap_TranslatesPhysicalKeysButtonsAndCursors()
    {
        Assert.AreEqual(XKeycodes.A, XInputMap.Keycode(PhysicalKey.A));
        Assert.AreEqual(XKeycodes.Return, XInputMap.Keycode(PhysicalKey.Enter));
        Assert.AreEqual(XKeycodes.ControlLeft, XInputMap.Keycode(PhysicalKey.ControlLeft));
        Assert.AreEqual(XKeycodes.Up, XInputMap.Keycode(PhysicalKey.ArrowUp));
        Assert.AreEqual(XKeycodes.KeypadEnter, XInputMap.Keycode(PhysicalKey.NumPadEnter));
        Assert.AreEqual(0, XInputMap.Keycode(PhysicalKey.None));
        Assert.AreEqual(3, XInputMap.Button(MouseButton.Right));
        Assert.AreEqual(8, XInputMap.Button(MouseButton.XButton1));
        Assert.AreEqual(StandardCursorType.Ibeam, XInputMap.Cursor(XCursorShape.Text));
        Assert.AreEqual(StandardCursorType.None, XInputMap.Cursor(XCursorShape.Hidden));
        Assert.AreEqual(StandardCursorType.Arrow, XInputMap.Cursor(XCursorShape.Arrow));
        Assert.AreEqual(StandardCursorType.BottomRightCorner, XInputMap.Cursor(XCursorShape.ResizeSouthEast));
    }

    [TestMethod]
    public void HostKeymap_MapsCharactersAndDeadKeysToKeysyms()
    {
        Assert.AreEqual('a', HostKeymap.Keysym('a'));
        Assert.AreEqual(0xe4u, HostKeymap.Keysym('ä'), "Latin-1 字符就是它自己");
        Assert.AreEqual(0x0100_20ACu, HostKeymap.Keysym('€'), "其余用 Unicode 键值");
        Assert.AreEqual(0u, HostKeymap.Keysym('\u0001'), "控制字符按「打不出字符」算");
        Assert.AreEqual(0xfe52u, HostKeymap.DeadKeysym('^'), "dead_circumflex");
        Assert.AreEqual(0xfe52u, HostKeymap.DeadKeysym('ˆ'), "macOS 的死键给 U+02C6");
        Assert.AreEqual(0xfe53u, HostKeymap.DeadKeysym('˜'), "macOS 的死键给 U+02DC");
        Assert.AreEqual(0xfe51u, HostKeymap.DeadKeysym('´'), "dead_acute");
        Assert.AreEqual('@', HostKeymap.DeadKeysym('@'), "不认识的死键按普通字符给");
    }

    /// <summary>四层排成核心列:没有第三、四层时每键两列;有时六列(XKB §17:组 1 第 1、2 级,组 2 照抄,组 1 第 3、4 级),缺的层照抄。</summary>
    [TestMethod]
    public void HostKeymap_AssemblesCoreColumns()
    {
        byte[] keycodes = [.. HostKeymap.Keycodes()];
        Assert.HasCount(HostKeymap.LastKeycode - HostKeymap.FirstKeycode + 2, keycodes);
        Assert.AreEqual(XKeycodes.IntlBackslash, keycodes[^1]);

        (uint, uint, uint, uint)[] plain = [.. keycodes.Select(k => k == XKeycodes.Q ? ('q', 'Q', 0u, 0u) : (1u, 0u, 0u, 0u))];
        HostKeymapResult two = HostKeymap.Assemble(plain);
        Assert.AreEqual(2, two.PerKeycode);
        Assert.IsFalse(two.HasAltGr);
        Assert.AreEqual('Q', two.Main[((XKeycodes.Q - HostKeymap.FirstKeycode) * 2) + 1]);
        Assert.AreEqual(1u, two.Main[1], "第二层缺的照抄第一层");

        plain[XKeycodes.Q - HostKeymap.FirstKeycode] = ('q', 'Q', '@', 0u);
        HostKeymapResult six = HostKeymap.Assemble(plain);
        Assert.IsTrue(six.HasAltGr);
        CollectionAssert.AreEqual(new uint[] { 'q', 'Q', 'q', 'Q', '@', '@' },
            six.Main.AsSpan((XKeycodes.Q - HostKeymap.FirstKeycode) * 6, 6).ToArray(), "第四层缺的照抄第三层");
        Assert.IsTrue(six.SameAs(HostKeymap.Assemble(plain)));
        Assert.IsFalse(six.SameAs(two));
    }

    /// <summary>设置里手选的布局用随程序带的表:德语 y / z 互换、AltGr 层(Q 上的 @、E 上的 €);美式没有 AltGr 层;表外的名字给 null。</summary>
    [TestMethod]
    public void HostKeymap_BuildsChosenLayoutsFromTheBundledTable()
    {
        HostKeymapResult de = HostKeymap.FromBundled("de")!;
        Assert.IsTrue(de.HasAltGr);
        static uint At(HostKeymapResult k, byte keycode, int column) => k.Main[((keycode - HostKeymap.FirstKeycode) * k.PerKeycode) + column];
        Assert.AreEqual('z', At(de, XKeycodes.Y, 0), "德语 QWERTZ:Y 的位置打 z");
        Assert.AreEqual('y', At(de, XKeycodes.Z, 0));
        Assert.AreEqual('@', At(de, XKeycodes.Q, 4), "AltGr+Q");
        Assert.AreEqual(0x20acu, At(de, XKeycodes.E, 4), "AltGr+E = EuroSign");
        Assert.AreEqual(0xff08u, At(de, XKeycodes.BackSpace, 0), "固定键不取表里的值");

        HostKeymapResult us = HostKeymap.FromBundled("us")!;
        Assert.IsFalse(us.HasAltGr, "美式布局没有 Level3 键");
        Assert.AreEqual('!', At(us, XKeycodes.D1, 1));

        Assert.IsNull(HostKeymap.FromBundled("xx"));

        Assert.AreEqual("de", de.Layout, "手选的布局名跟着键位表走");
        XKeymap keymap = de.ToXKeymap();
        Assert.AreEqual("de", keymap.Layout);
        Assert.IsTrue(keymap.AltGr, "有 AltGr 层:右 Alt 当 AltGr");
        Assert.AreEqual(6, keymap.KeysymsPerKeycode);
    }

    /// <summary>跟随系统时没有布局名:取随程序带的表里第一、二层最像的那个;认不出来按 us。</summary>
    [TestMethod]
    public void HostKeymap_GuessesTheLayoutNameWhenFollowingTheSystem()
    {
        byte[] keycodes = [.. HostKeymap.Keycodes()];
        List<(uint, uint, uint, uint)> Levels(string layout)
        {
            uint[] t = BundledKeymaps.Layouts[layout];
            return [.. keycodes.Select((k, i) => HostKeymap.Fixed(k) is { } f ? (f.Item1, f.Item2, 0u, 0u) : (t[i * 4], t[i * 4 + 1], t[i * 4 + 2], t[i * 4 + 3]))];
        }
        Assert.AreEqual("de", HostKeymap.Assemble(Levels("de")).Layout);
        Assert.AreEqual("fr", HostKeymap.Assemble(Levels("fr")).Layout);
        Assert.AreEqual("us", HostKeymap.Assemble(Levels("us")).Layout);
        Assert.AreEqual("us", HostKeymap.Assemble([.. keycodes.Select(_ => ((uint)'a', (uint)'A', 0u, 0u))]).Layout, "认不出来");
    }

    /// <summary>设置页下拉里的每个布局(「自动」除外)在随程序带的表里都有 —— 选了却没有数据就会退回系统布局,用户看不出来。</summary>
    [TestMethod]
    public void BundledKeymaps_CoverEveryLayoutOfferedInSettings()
    {
        string[] offered = [.. VelaShell.ViewModels.SettingsViewModel.BuildKeyboardLayouts().Select(c => c.Value).Where(v => v.Length != 0)];
        Assert.IsNotEmpty(offered);
        string[] missing = [.. offered.Where(v => !BundledKeymaps.Layouts.ContainsKey(v))];
        Assert.IsEmpty(missing, string.Join(", ", missing));
        Assert.IsTrue(BundledKeymaps.Layouts.Values.All(t => t.Length == HostKeymap.Keycodes().Count() * 4));
    }

    /// <summary>macOS 的虚拟键码表:要推导的键里,除了与布局无关的那几个,每个都有且互不相同。</summary>
    [TestMethod]
    public void MacKeymap_VirtualKeyTableCoversEveryCharacterKey()
    {
        int[] codes = [.. HostKeymap.Keycodes().Where(k => HostKeymap.Fixed(k) is null).Select(MacKeymap.VirtualKeyFor)];
        Assert.IsTrue(codes.All(c => c >= 0), "每个打字符的键都有 kVK_* 对应");
        Assert.HasCount(codes.Length, codes.Distinct());
        Assert.AreEqual(0x00, MacKeymap.VirtualKeyFor(XKeycodes.A), "kVK_ANSI_A");
        Assert.AreEqual(0x0A, MacKeymap.VirtualKeyFor(XKeycodes.IntlBackslash), "kVK_ISO_Section");
    }

    [TestMethod]
    public void LinuxKeymap_ParsesDisplayNumber()
    {
        Assert.AreEqual(0, LinuxKeymap.DisplayNumber(":0"));
        Assert.AreEqual(12, LinuxKeymap.DisplayNumber("localhost:12.0"));
        Assert.AreEqual(1, LinuxKeymap.DisplayNumber("unix:1"));
        Assert.AreEqual(-1, LinuxKeymap.DisplayNumber("wayland-0"));
        Assert.AreEqual(-1, LinuxKeymap.DisplayNumber(":x"));
    }

    /// <summary>
    /// 按当前系统的布局真的算一次(Windows / 有 X 显示的 Linux):不打字符的键(退格、Tab、回车、Ctrl、Shift)
    /// 与布局无关,永远是标准键值;字母键在任何布局下都打得出字符。macOS 的 TIS 接口只能在主线程上调
    /// (macOS 14 起在别的线程上调会断言失败、整个进程退出),测试线程不是主线程,所以 macOS 上不在这里真取。
    /// </summary>
    [TestMethod]
    public void HostKeymap_BuildsFromTheCurrentSystemLayout()
    {
        if (OperatingSystem.IsMacOS())
        {
            Assert.Inconclusive("macOS 的 TIS 接口只能在主线程上调,宿主在 UI 线程上调用");
            return;
        }
        HostKeymapResult? keymap = OperatingSystem.IsWindows() ? WindowsKeymap.Build(WindowsKeymap.CurrentLayout())
            : OperatingSystem.IsLinux() ? LinuxKeymap.Build(ownDisplay: -1)
            : null;
        if (keymap is null)
        {
            Assert.IsTrue(OperatingSystem.IsLinux(), "Windows 上总能按当前布局算出键位表");
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            {
                Assert.Inconclusive("没有 $DISPLAY:沿用服务端的 US 键位表");
                return;
            }
            // 桌面标配的库在最小化的容器 / CI 镜像里可能没有:那是环境不全,不是实现的错。库都在还取不到才是真失败。
            string[] missing = [.. new[] { "libxcb.so.1", "libxkbcommon.so.0", "libxkbcommon-x11.so.0" }
                .Where(name => !NativeLibrary.TryLoad(name, out _))];
            if (missing.Length > 0)
            {
                Assert.Inconclusive($"缺少 {string.Join("、", missing)}:沿用服务端的 US 键位表");
                return;
            }
            Assert.Fail("有 $DISPLAY、库也都在,却没按桌面布局算出键位表");
        }
        int per = keymap.PerKeycode;
        Assert.IsTrue(per is 2 or 6, "无 AltGr 两列;有 AltGr 按 XKB §17 的核心列序六列");
        uint At(byte keycode, int column) => keymap.Main[((keycode - HostKeymap.FirstKeycode) * per) + column];
        Assert.AreEqual(0xff08u, At(XKeycodes.BackSpace, 0));
        Assert.AreEqual(0xfe20u, At(XKeycodes.Tab, 1), "Shift+Tab = ISO_Left_Tab");
        Assert.AreEqual(0xff0du, At(XKeycodes.Return, 0));
        Assert.AreEqual(0xffe1u, At(XKeycodes.ShiftLeft, 0));
        Assert.AreNotEqual(0u, At(XKeycodes.A, 0), "字母键在任何布局下都打得出字符");
        Assert.AreNotEqual(0u, At(XKeycodes.D1, 1), "数字行的 Shift 层也有字符");
        Assert.HasCount(per, keymap.IntlBackslash);
        if (per == 6)
        {
            Assert.AreEqual(At(XKeycodes.A, 0), At(XKeycodes.A, 2), "组 2 照抄组 1");
        }
    }

    [TestMethod]
    public async Task MappedWindow_BecomesNativeWindow_AndCloseButtonDisconnectsClient() => await _session.Dispatch(async () =>
    {
        AvaloniaXServerHost host = new();
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        Task serve = server.ServeAsync(serverSide, isLocal: true);

        (uint idBase, uint root) = await HandshakeAsync(client);
        uint window = idBase | 1, gc = idBase | 2;
        // CreateWindow 60×40 于 (100,50),背景白;WM_NAME = "xtest";映射;用 GC 填一块红。
        await SendAsync(client, 1, 24, w => w.U32(window).U32(root).I16(100).I16(50).U16(60).U16(40).U16(0).U16(1).U32(0)
            .U32(0x2).U32(0xFFFFFF));
        byte[] title = Encoding.ASCII.GetBytes("xtest");
        await SendAsync(client, 18, 0, w => w.U32(window).U32(39).U32(31).U8(8).Zero(3).U32((uint)title.Length).Bytes(title).Pad());
        await SendAsync(client, 8, 0, w => w.U32(window));
        await SendAsync(client, 55, 0, w => w.U32(gc).U32(window).U32(0x4).U32(0xFF0000));
        await SendAsync(client, 70, 0, w => w.U32(window).U32(gc).I16(0).I16(0).U16(10).U16(10));

        XNativeWindow native = await WaitForAsync(() => host.Windows.FirstOrDefault());
        await WaitForAsync(() => native.Title == "xtest" ? native : null);
        Assert.AreEqual(new Size(60, 40), native.ClientSize, "headless 缩放为 1:物理像素 = DIP");
        Assert.AreEqual(window, native.Handle.Id);

        // 像素:左上角是填的红,右下是背景白。
        await WaitForAsync(() => Pixel(native, 5, 5) == 0xFF0000 ? native : null);
        Assert.AreEqual(0xFFFFFFu, Pixel(native, 50, 30));

        // 关闭按钮:客户端没声明 WM_DELETE_WINDOW → 服务端断开它 → 窗口随之收掉。
        native.Close();
        await WaitForAsync(() => host.Windows.Count == 0 ? native : null);
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
        host.Detach();
        return true;   // 带返回值的重载:无返回值的 async lambda 会变成从未被等待的 Task<Task>(AGENTS.md)
    }, CancellationToken.None);

    /// <summary>
    /// 原生窗口按 256 × 256 切块、只取损伤矩形:跨块的窗口、后来只改了右下角一小块、客户端改了尺寸(缓冲变大、块数变多)之后,
    /// 各块的像素都对,没改到的地方保持原样。
    /// </summary>
    [TestMethod]
    public async Task TiledSurface_CopiesOnlyDamage_AndFollowsResize() => await _session.Dispatch(async () =>
    {
        AvaloniaXServerHost host = new();
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        Task serve = server.ServeAsync(serverSide, isLocal: true);

        (uint idBase, uint root) = await HandshakeAsync(client);
        uint window = idBase | 1, red = idBase | 2, green = idBase | 3, blue = idBase | 4;
        // 300×270:横竖各跨两块。背景白,左上角填红。
        await SendAsync(client, 1, 24, w => w.U32(window).U32(root).I16(40).I16(40).U16(300).U16(270).U16(0).U16(1).U32(0)
            .U32(0x2).U32(0xFFFFFF));
        await SendAsync(client, 8, 0, w => w.U32(window));
        await SendAsync(client, 55, 0, w => w.U32(red).U32(window).U32(0x4).U32(0xFF0000));
        await SendAsync(client, 55, 0, w => w.U32(green).U32(window).U32(0x4).U32(0x00FF00));
        await SendAsync(client, 55, 0, w => w.U32(blue).U32(window).U32(0x4).U32(0x0000FF));
        await SendAsync(client, 70, 0, w => w.U32(window).U32(red).I16(0).I16(0).U16(10).U16(10));

        XNativeWindow native = await WaitForAsync(() => host.Windows.FirstOrDefault());
        await WaitForAsync(() => Pixel(native, 5, 5) == 0xFF0000 ? native : null);
        Assert.AreEqual(0xFFFFFFu, Pixel(native, 290, 260), "右下那块(第二行第二列)是背景");

        // 只改右下角一小块:它所在的块取到了,左上角的块保持原样。
        await SendAsync(client, 70, 0, w => w.U32(window).U32(green).I16(280).I16(260).U16(10).U16(10));
        await WaitForAsync(() => Pixel(native, 285, 265) == 0x00FF00 ? native : null);
        Assert.AreEqual(0xFF0000u, Pixel(native, 5, 5));
        Assert.AreEqual(0xFFFFFFu, Pixel(native, 150, 150));

        // 客户端把窗口改到 520×300(横向三块):缓冲变了,整窗重取;新露出来的地方画了蓝色。
        // (默认 bit-gravity 是 Forget:改尺寸时服务端按背景重画整窗,原来的红、绿都没了 —— 原生窗口要跟服务端的缓冲一致。)
        await SendAsync(client, 12, 0, w => w.U32(window).U16(0x4 | 0x8).U16(0).U32(520).U32(300));
        await SendAsync(client, 70, 0, w => w.U32(window).U32(blue).I16(510).I16(290).U16(10).U16(10));
        await WaitForAsync(() => native.ClientSize == new Size(520, 300) && Pixel(native, 515, 295) == 0x0000FF ? native : null);
        foreach ((int x, int y) in new[] { (5, 5), (285, 265), (400, 10), (10, 290), (515, 295) })
        {
            Assert.AreEqual(ServerPixel(native, x, y), Pixel(native, x, y), $"({x},{y}) 与服务端的缓冲一致");
        }

        await SendAsync(client, 4, 0, w => w.U32(window));   // DestroyWindow
        await WaitForAsync(() => host.Windows.Count == 0 ? native : null);
        client.Dispose();
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
        host.Detach();
        return true;
    }, CancellationToken.None);

    /// <summary>服务端缓冲里的像素(低 24 位)。</summary>
    private static uint ServerPixel(XNativeWindow window, int x, int y)
    {
        uint[] pixels = new uint[window.Handle.Snapshot.Width * window.Handle.Snapshot.Height];
        (int w, _) = window.Handle.CopyPixels(pixels);
        return pixels[(y * w) + x] & 0xFFFFFF;
    }

    private static uint Pixel(XNativeWindow window, int x, int y)
    {
        Dispatcher.UIThread.RunJobs();
        if (window.CaptureRenderedFrame() is not { } frame)
        {
            return 0;
        }
        using (frame)
        {
            uint[] pixel = new uint[1];
            GCHandle pin = GCHandle.Alloc(pixel, GCHandleType.Pinned);
            try
            {
                frame.CopyPixels(new PixelRect(x, y, 1, 1), pin.AddrOfPinnedObject(), 4, 4);
            }
            finally
            {
                pin.Free();
            }
            uint value = pixel[0];
            // 截图的像素格式随渲染后端:BGRA 小端读出来是 0xAARRGGBB,RGBA 要把 R、B 对调。
            if (frame.Format == Avalonia.Platform.PixelFormat.Rgba8888)
            {
                value = (value & 0xFF00FF00) | ((value & 0xFF) << 16) | ((value >> 16) & 0xFF);
            }
            return value & 0xFFFFFF;
        }
    }

    private static async Task<T> WaitForAsync<T>(Func<T?> probe) where T : class
    {
        for (int i = 0; i < 250; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (probe() is { } value)
            {
                return value;
            }
            await Task.Delay(20);
        }
        throw new TimeoutException("等不到预期的状态");
    }

    // ------------------------------------------------------------------ 最小的 X 客户端(小端)

    private static async Task<(uint IdBase, uint Root)> HandshakeAsync(Stream stream)
    {
        await stream.WriteAsync(new byte[] { (byte)'l', 0, 11, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        await stream.FlushAsync();
        byte[] head = new byte[8];
        await stream.ReadExactlyAsync(head);
        Assert.AreEqual(1, head[0], "连接建立成功");
        byte[] rest = new byte[BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(6)) * 4];
        await stream.ReadExactlyAsync(rest);
        byte[] reply = [.. head, .. rest];
        uint idBase = BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(12));
        int vendor = BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(24));
        int formats = reply[29];
        uint root = BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(40 + ((vendor + 3) & ~3) + (formats * 8)));
        _ = ReadAndDiscardAsync(stream);   // 事件与回复一概不看,只要别把管道堵住
        return (idBase, root);
    }

    private static async Task ReadAndDiscardAsync(Stream stream)
    {
        byte[] buffer = new byte[4096];
        try
        {
            while (await stream.ReadAsync(buffer) > 0)
            {
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private static async Task SendAsync(Stream stream, byte opcode, byte data, Action<Body> body)
    {
        Body b = new();
        body(b);
        byte[] payload = b.ToArray();
        byte[] request = new byte[4 + payload.Length];
        request[0] = opcode;
        request[1] = data;
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(2), (ushort)(request.Length / 4));
        payload.CopyTo(request, 4);
        await stream.WriteAsync(request);
        await stream.FlushAsync();
    }

    private sealed class Body
    {
        private readonly List<byte> _bytes = [];

        public Body U8(byte v) { _bytes.Add(v); return this; }

        public Body U16(ushort v) { _bytes.Add((byte)v); _bytes.Add((byte)(v >> 8)); return this; }

        public Body I16(short v) => U16(unchecked((ushort)v));

        public Body U32(uint v) => U16((ushort)v).U16((ushort)(v >> 16));

        public Body Zero(int n) { _bytes.AddRange(new byte[n]); return this; }

        public Body Bytes(byte[] data) { _bytes.AddRange(data); return this; }

        public Body Pad() { while (_bytes.Count % 4 != 0) { _bytes.Add(0); } return this; }

        public byte[] ToArray() => [.. _bytes];
    }
}
