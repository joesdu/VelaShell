using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Threading;
using VelaShell.Services.XServer;
using VelaShell.Ssh.Transport;
using VelaShell.Tests.TestSupport;
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

    /// <summary>日文 JIS / 巴西 ABNT2 / 韩文键盘的键、F13–F24、多媒体键都有 X 键码;Ro、Yen 按系统布局推出的键值并进键位表。</summary>
    [TestMethod]
    public void InputMap_CoversInternationalFunctionAndMediaKeys_AndExtrasJoinTheKeymap()
    {
        Assert.AreEqual(XKeycodes.IntlRo, XInputMap.Keycode(PhysicalKey.IntlRo));
        Assert.AreEqual(XKeycodes.IntlYen, XInputMap.Keycode(PhysicalKey.IntlYen));
        Assert.AreEqual(XKeycodes.Henkan, XInputMap.Keycode(PhysicalKey.Convert));
        Assert.AreEqual(XKeycodes.F13, XInputMap.Keycode(PhysicalKey.F13));
        Assert.AreEqual(XKeycodes.F24, XInputMap.Keycode(PhysicalKey.F24));
        Assert.AreEqual(XKeycodes.AudioMute, XInputMap.Keycode(PhysicalKey.AudioVolumeMute));

        HostKeymapResult us = HostKeymap.FromBundled("us")!;
        Assert.IsEmpty(us.Extras, "手选布局:随程序带的表里没有 Ro / Yen,服务端沿用起步的 JIS 键值");
        // ABNT2:Ro 是 / ?;Yen 这台键盘没有(第一层 0 跳过)。
        HostKeymapResult abnt2 = HostKeymap.WithExtras(us, [('/', '?', 0, 0), (0, 0, 0, 0)]);
        Assert.HasCount(1, abnt2.Extras);
        Assert.AreEqual(XKeycodes.IntlRo, abnt2.Extras[0].Keycode);
        Assert.AreSequenceEqual(['/', '?'], abnt2.Extras[0].Columns);
        Assert.IsFalse(abnt2.SameAs(us), "多了额外的键就不是同一个结果");
        Assert.AreEqual(2, abnt2.ToXKeymap().KeysymsPerKeycode);
    }

    /// <summary>显示器多于服务端的上限(16 台)时只交主显示器与排在前面的几台 —— 原先整个列表交过去,服务端抛的异常落在 UI 线程上。</summary>
    [TestMethod]
    public void 显示器超过上限时只交主显示器与排在前面的几台()
    {
        (int Id, bool Primary)[] screens = [.. Enumerable.Range(0, 20).Select(i => (i, i == 18))];
        IReadOnlyList<(int Id, bool Primary)> limited = AvaloniaXServerHost.LimitScreens(screens, s => s.Primary);
        Assert.HasCount(X11Server.MaxMonitors, limited);
        Assert.AreEqual(18, limited[0].Id, "主显示器留着");
        Assert.AreEqual(14, limited[^1].Id, "其余按先后取够");
        Assert.HasCount(3, AvaloniaXServerHost.LimitScreens(screens[..3], s => s.Primary), "没超过就原样交");
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
        Assert.AreSequenceEqual(['q', 'Q', 'q', 'Q', '@', '@'], six.Main.AsSpan((XKeycodes.Q - HostKeymap.FirstKeycode) * 6, 6).ToArray(), "第四层缺的照抄第三层");
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
        var keymap = de.ToXKeymap();
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

    /// <summary>
    /// 桌面的键位表与锁定键(Linux 上要连桌面的 X 显示、拉一遍 XKB 表)在后台读,激活 X 窗口不等它;读的期间再激活的合成一次,
    /// 读完把键位表与锁定键交给服务端。原先每次激活都在 UI 线程上读两遍,<c>$DISPLAY</c> 慢时切一次窗口界面就卡一下。
    /// </summary>
    [TestMethod]
    public async Task DesktopKeyboard_IsReadInTheBackground_AndActivationsCoalesce() => await _session.RunOnUiAsync(async () =>
    {
        using ManualResetEventSlim gate = new();
        int reads = 0;
        AvaloniaXServerHost host = new()
        {
            DesktopKeyboardReader = _ =>
            {
                Interlocked.Increment(ref reads);
                gate.Wait(TimeSpan.FromSeconds(10));   // 慢的显示
                return new DesktopKeyboard(HostKeymap.FromBundled("de"), (CapsLock: false, NumLock: true));
            },
        };
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        _ = server.ServeAsync(serverSide, isLocal: true);
        (uint idBase, uint root) = await HandshakeAsync(client);
        await SendAsync(client, 1, 24, w => w.U32(idBase | 1).U32(root).I16(0).I16(0).U16(60).U16(40).U16(0).U16(1).U32(0).U32(0));
        await SendAsync(client, 8, 0, w => w.U32(idBase | 1));
        XNativeWindow native = await WaitForAsync(() => host.Windows.FirstOrDefault());
        for (int i = 0; i < 3; i++)
        {
            native.Activate();
            Dispatcher.UIThread.RunJobs();
        }
        Assert.IsLessThan(TimeSpan.FromSeconds(5), elapsed.Elapsed, "附着与激活都没等桌面的键盘读完");
        Assert.AreEqual(1, Volatile.Read(ref reads), "同一时刻只读一次");
        Assert.IsFalse(host.HasAltGr);

        gate.Set();
        await WaitForAsync(() => host.HasAltGr ? host : null);   // 德语布局有 AltGr 层:键位表交给了服务端
        await WaitForAsync(() => Volatile.Read(ref reads) == 2 ? host : null);   // 读的期间激活的合成了读完之后的一次
        host.Detach();
    });

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
    public async Task MappedWindow_BecomesNativeWindow_AndCloseButtonDisconnectsClient() => await _session.RunOnUiAsync(async () =>
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
    });

    /// <summary>
    /// 记住窗口位置(xs_plan F17):一个程序的窗口关掉时记下位置与尺寸,下次没给位置的同类窗口摆回去、尺寸也恢复;
    /// 同类窗口还开着时不摆过去(免得叠在一起),别的程序照常居中,客户端指定了尺寸(USSize)时不盖掉它。
    /// </summary>
    [TestMethod]
    public async Task ReopenedProgram_ComesBackWhereItWasClosed() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new();
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        _ = server.ServeAsync(serverSide, isLocal: true);
        (uint idBase, uint root) = await HandshakeAsync(client);
        uint next = 0;
        async Task<XNativeWindow> MapAsync(string wmClass, bool userSize = false)
        {
            uint window = idBase | ++next;
            await SendAsync(client, 1, 24, w => w.U32(window).U32(root).I16(0).I16(0).U16(60).U16(40).U16(0).U16(1).U32(0).U32(0));
            byte[] cls = Encoding.ASCII.GetBytes(wmClass);
            await SendAsync(client, 18, 0, w => w.U32(window).U32(67).U32(31).U8(8).Zero(3).U32((uint)cls.Length).Bytes(cls).Pad());   // WM_CLASS
            if (userSize)
            {
                await SendAsync(client, 18, 0, w => w.U32(window).U32(40).U32(41).U8(32).Zero(3).U32(18).U32(2).Zero(17 * 4));   // USSize
            }
            await SendAsync(client, 8, 0, w => w.U32(window));
            return await WaitForAsync(() => host.Windows.FirstOrDefault(n => n.Handle.Id == window && n.HasOpened && !n.Handle.Snapshot.NeedsPlacement),
                $"{wmClass} 显示出来并摆好");
        }
        (int ox, int oy) = host.RootOrigin;

        XNativeWindow first = await MapAsync("xterm\0XTerm\0");
        PixelPoint centered = first.Position;
        first.Position = new PixelPoint(ox + 37, oy + 29);   // 用户把它拖到一边、拉大
        server.ResizeTopLevel(first.Handle, 90, 70);
        await WaitForAsync(() => first.Handle.Snapshot.Width == 90 ? first : null, "拉大");
        await SendAsync(client, 10, 0, w => w.U32(first.Handle.Id));   // UnmapWindow:程序关掉了这个窗口
        await WaitForAsync(() => host.Windows.Count == 0 ? first : null, "收掉");

        XNativeWindow again = await MapAsync("xterm\0XTerm\0");
        Assert.AreEqual(new PixelPoint(ox + 37, oy + 29), again.Position, "回到上次关掉的位置");
        await WaitForAsync(() => again.Handle.Snapshot.Width == 90 ? again : null, $"尺寸恢复({again.Handle.Snapshot.Width})");
        Assert.AreEqual(70, again.Handle.Snapshot.Height, "尺寸也恢复");

        XNativeWindow second = await MapAsync("xterm\0XTerm\0");
        Assert.AreEqual(centered, second.Position, "同一个程序已经开着一个窗口:不叠过去");
        XNativeWindow other = await MapAsync("xclock\0XClock\0");
        Assert.AreEqual(centered, other.Position, "别的程序照常居中");

        await SendAsync(client, 10, 0, w => w.U32(second.Handle.Id));   // 后关的那个说了算:先关居中的,再关挪过的
        await SendAsync(client, 10, 0, w => w.U32(again.Handle.Id));
        await WaitForAsync(() => host.Windows.Count == 1 ? other : null);
        XNativeWindow sized = await MapAsync("xterm\0XTerm\0", userSize: true);
        Assert.AreEqual(new PixelPoint(ox + 37, oy + 29), sized.Position);
        await Task.Delay(100);
        Assert.AreEqual((60, 40), (sized.Handle.Snapshot.Width, sized.Handle.Snapshot.Height), "客户端指定了尺寸:不盖掉");
        host.Detach();
    });

    /// <summary>
    /// X 窗口截图(xs_plan F20):截到的是窗口此刻的像素(深度 24 的补成不透明);非矩形窗口形状以外是透明的。
    /// </summary>
    [TestMethod]
    public async Task Screenshot_CapturesTheWindowPixels_AndClearsOutsideTheShape() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new();
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        _ = server.ServeAsync(serverSide, isLocal: true);
        System.Collections.Concurrent.ConcurrentQueue<byte[]> replies = new();
        (uint idBase, uint root) = await HandshakeAsync(client, replies: replies);
        byte[] shapeName = Encoding.ASCII.GetBytes("SHAPE");
        await SendAsync(client, 98, 0, w => w.U16((ushort)shapeName.Length).U16(0).Bytes(shapeName).Pad());
        byte shape = (await WaitForAsync(() => replies.TryDequeue(out byte[]? r) ? r : null))[9];
        uint window = idBase | 1, gc = idBase | 2;
        await SendAsync(client, 1, 24, w => w.U32(window).U32(root).I16(10).I16(10).U16(60).U16(40).U16(0).U16(1).U32(0)
            .U32(0x2).U32(0xFFFFFF));
        await SendAsync(client, 8, 0, w => w.U32(window));
        await SendAsync(client, 55, 0, w => w.U32(gc).U32(window).U32(0x4).U32(0xFF0000));
        await SendAsync(client, 70, 0, w => w.U32(window).U32(gc).I16(0).I16(0).U16(10).U16(10));
        XNativeWindow native = await WaitForAsync(() => host.Windows.FirstOrDefault());
        await WaitForAsync(() => Pixel(native, 5, 5) == 0xFF0000 ? native : null, "画上了");

        static uint At(Avalonia.Media.Imaging.WriteableBitmap bitmap, int x, int y)
        {
            using Avalonia.Platform.ILockedFramebuffer frame = bitmap.Lock();
            return (uint)Marshal.ReadInt32(frame.Address, (y * frame.RowBytes) + (x * 4));
        }
        using (Avalonia.Media.Imaging.WriteableBitmap shot = WindowScreenshot.Capture(native.Handle)!)
        {
            Assert.AreEqual(new PixelSize(60, 40), shot.PixelSize);
            Assert.AreEqual(0xFFFF0000u, At(shot, 5, 5), "填的红,不透明");
            Assert.AreEqual(0xFFFFFFFFu, At(shot, 50, 30), "背景白");
        }

        // ShapeRectangles:边界形状只留左半边。
        await SendAsync(client, shape, 1, w => w.U8(0).U8(0).U8(0).U8(0).U32(window).I16(0).I16(0).I16(0).I16(0).U16(30).U16(40));
        await WaitForAsync(() => native.Handle.Snapshot.Shape is not null ? native : null, "形状生效");
        using (Avalonia.Media.Imaging.WriteableBitmap shot = WindowScreenshot.Capture(native.Handle)!)
        {
            Assert.AreEqual(0xFFFF0000u, At(shot, 5, 5));
            Assert.AreEqual(0u, At(shot, 50, 30), "形状以外透明");
        }
        host.Detach();
    });

    /// <summary>截图的默认文件名:标题里不能进文件名的字符换掉;没有标题用本地化的默认名。</summary>
    [TestMethod]
    public void ScreenshotFileName_IsSafe()
    {
        string name = WindowScreenshot.FileNameFor("a/b:c*d?\u0001");
        Assert.AreEqual(-1, name.IndexOfAny(Path.GetInvalidFileNameChars()), name);
        Assert.EndsWith(".png", name);
        Assert.IsGreaterThan(4, WindowScreenshot.FileNameFor("  ").Length, "空标题有默认名");
    }

    /// <summary>任务栏组名(xs_plan F17):按 WM_CLASS 的类名,只留 AppUserModelID 认的字符、不超过 128 个字符;没有类名的不归组。</summary>
    [TestMethod]
    [DataRow("XTerm", "VelaShell.X11.XTerm")]
    [DataRow("Gimp-2.10", "VelaShell.X11.Gimp-2.10")]
    [DataRow("My App/中文", "VelaShell.X11.My_App___")]
    [DataRow("", null)]
    public void TaskbarGroupId_FollowsTheClassName(string className, string? expected)
    {
        Assert.AreEqual(expected, TaskbarGroup.IdFor(new XTopLevelSnapshot { ClassName = className }));
        Assert.IsLessThanOrEqualTo(128, TaskbarGroup.IdFor(new XTopLevelSnapshot { ClassName = new string('a', 500) })!.Length);
    }

    /// <summary>
    /// 任务栏按 X 程序归组(xs_plan F17):进任务栏的窗口显示之前按类名归组,窗口收掉之前清掉;没有类名的、对话框(不进任务栏)不归组。
    /// </summary>
    [TestMethod]
    public async Task TaskbarButtons_AreGroupedByProgram() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new();
        List<(Avalonia.Controls.Window Window, string? Group)> calls = [];
        host.GroupWindow = (window, group) =>
        {
            calls.Add((window, group));
            return true;
        };
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        _ = server.ServeAsync(serverSide, isLocal: true);
        (uint idBase, uint root) = await HandshakeAsync(client);
        uint xterm = idBase | 1, anonymous = idBase | 2, dialog = idBase | 3;
        foreach (uint window in new[] { xterm, anonymous, dialog })
        {
            await SendAsync(client, 1, 24, w => w.U32(window).U32(root).I16(10).I16(10).U16(60).U16(40).U16(0).U16(1).U32(0).U32(0));
        }
        byte[] cls = Encoding.ASCII.GetBytes("xterm\0XTerm\0");
        await SendAsync(client, 18, 0, w => w.U32(xterm).U32(67).U32(31).U8(8).Zero(3).U32((uint)cls.Length).Bytes(cls).Pad());
        await SendAsync(client, 18, 0, w => w.U32(dialog).U32(67).U32(31).U8(8).Zero(3).U32((uint)cls.Length).Bytes(cls).Pad());
        await SendAsync(client, 18, 0, w => w.U32(dialog).U32(68).U32(33).U8(32).Zero(3).U32(1).U32(xterm));   // WM_TRANSIENT_FOR
        foreach (uint window in new[] { xterm, anonymous, dialog })
        {
            await SendAsync(client, 8, 0, w => w.U32(window));
        }
        await WaitForAsync(() => host.Windows.Count == 3 ? host : null, "三个窗口");
        XNativeWindow main = host.Windows.Single(w => w.Handle.Id == xterm);
        Assert.HasCount(1, calls, "只有进任务栏、有类名的那个归组");
        Assert.AreSame(main, calls[0].Window);
        Assert.AreEqual("VelaShell.X11.XTerm", calls[0].Group);

        await SendAsync(client, 10, 0, w => w.U32(xterm));
        await WaitForAsync(() => calls.Count == 2 ? calls : null, "收掉时清掉");
        Assert.AreSame(main, calls[1].Window);
        Assert.IsNull(calls[1].Group);
        host.Detach();
    });

    /// <summary>
    /// 来源标识(xs_plan F18):转发来的连接(有标签)的窗口标题前标出来源,远端把标题设成什么都盖不住;本机连接(没标签)不标;设置关掉也不标。
    /// 来源与标题各自包在双向隔离符里(来源里的 U+202E 倒不过后面的标题)。运行中改开关,开着的窗口当场改标题(原先只对之后打开的窗口生效)。
    /// </summary>
    [TestMethod]
    [DataRow("alice@build:22", true, "⁨alice@build:22⁩ — ⁨Windows Security⁩")]
    [DataRow("alice@build:22", false, "Windows Security")]
    [DataRow(null, true, "Windows Security")]
    public async Task ForwardedWindowTitle_ShowsTheSource(string? label, bool show, string expected) => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new();
        host.ShowWindowSource(show);
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        Task serve = label is null ? server.ServeAsync(serverSide, isLocal: true) : server.ServeAuthenticatedAsync(serverSide, label);

        (uint idBase, uint root) = await HandshakeAsync(client);
        uint window = idBase | 1;
        await SendAsync(client, 1, 24, w => w.U32(window).U32(root).I16(100).I16(50).U16(60).U16(40).U16(0).U16(1).U32(0).U32(0));
        byte[] title = Encoding.ASCII.GetBytes("Windows Security");
        await SendAsync(client, 18, 0, w => w.U32(window).U32(39).U32(31).U8(8).Zero(3).U32((uint)title.Length).Bytes(title).Pad());
        await SendAsync(client, 8, 0, w => w.U32(window));

        XNativeWindow native = await WaitForAsync(() => host.Windows.FirstOrDefault());
        await WaitForAsync(() => native.Title?.Contains("Windows Security", StringComparison.Ordinal) == true ? native : null);
        Assert.AreEqual(expected, native.Title);

        if (label is not null)
        {
            // 运行中改开关:开着的窗口当场改标题。
            host.ShowWindowSource(!show);
            await WaitForAsync(() => native.Title != expected ? native : null, "开关一改,标题跟着改");
            Assert.AreEqual(show ? "Windows Security" : $"⁨{label}⁩ — ⁨Windows Security⁩", native.Title);
        }

        native.CloseByHost();
        host.Detach();
        client.Dispose();
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
    });

    /// <summary>
    /// 原生窗口的尺寸变了而不是我们按服务端几何设的(Linux 上 Avalonia 的 X11 后端给的原因是 Unspecified 而不是 User):照样回报给服务端。
    /// </summary>
    [TestMethod]
    public async Task NativeResizeWithoutUserReason_IsReportedToTheServer() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new();
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        _ = server.ServeAsync(serverSide, isLocal: true);
        (uint idBase, uint root) = await HandshakeAsync(client);
        uint window = idBase | 1;
        await SendAsync(client, 1, 24, w => w.U32(window).U32(root).I16(10).I16(10).U16(60).U16(40).U16(0).U16(1).U32(0).U32(0));
        await SendAsync(client, 8, 0, w => w.U32(window));
        XNativeWindow native = await WaitForAsync(() => host.Windows.FirstOrDefault());
        await WaitForAsync(() => native.ClientSize.Width == 60 ? native : null);

        native.Width = 90;   // 不经服务端改了原生窗口的尺寸(相当于用户在 Linux 上拖了边框)
        await WaitForAsync(() => native.Handle.Snapshot.Width == 90 ? native : null);
        host.Detach();
    });

    /// <summary>
    /// 映射时照客户端的提示摆:映射前就设好的 <c>_NET_WM_STATE</c> 最大化、<c>WM_HINTS</c> 的 initial_state = Iconic(<c>xterm -iconic</c>)、
    /// 用户指定在 (0, 0) 的位置(USPosition,<c>xterm -geometry +0+0</c>)—— 原先都按普通窗口显示、(0, 0) 一律挪到屏幕中央。
    /// </summary>
    [TestMethod]
    public async Task MapHonoursInitialStatesAndUserPosition() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new();
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        Task serve = server.ServeAsync(serverSide, isLocal: true);
        System.Collections.Concurrent.ConcurrentQueue<byte[]> replies = new();
        (uint idBase, uint root) = await HandshakeAsync(client, replies);
        uint maximized = idBase | 1, iconic = idBase | 2, pinned = idBase | 3;
        foreach (uint id in (uint[])[maximized, iconic, pinned])
        {
            await SendAsync(client, 1, 24, w => w.U32(id).U32(root).I16(0).I16(0).U16(60).U16(40).U16(0).U16(1).U32(0).U32(0));
        }
        uint netWmState = await InternAsync(client, replies, "_NET_WM_STATE");
        uint vert = await InternAsync(client, replies, "_NET_WM_STATE_MAXIMIZED_VERT");
        uint horz = await InternAsync(client, replies, "_NET_WM_STATE_MAXIMIZED_HORZ");
        await SendAsync(client, 18, 0, w => w.U32(maximized).U32(netWmState).U32(4).U8(32).Zero(3).U32(2).U32(vert).U32(horz));
        await SendAsync(client, 18, 0, w => w.U32(iconic).U32(35).U32(35).U8(32).Zero(3).U32(9)              // WM_HINTS:StateHint,IconicState
            .U32(2).U32(0).U32(3).U32(0).U32(0).U32(0).U32(0).U32(0).U32(0));
        await SendAsync(client, 18, 0, w => w.U32(pinned).U32(40).U32(41).U8(32).Zero(3).U32(18).U32(1).Zero(17 * 4));   // USPosition
        foreach (uint id in (uint[])[maximized, iconic, pinned])
        {
            await SendAsync(client, 8, 0, w => w.U32(id));
        }

        XNativeWindow max = await WaitForAsync(() => host.Windows.FirstOrDefault(w => w.Handle.Id == maximized));
        XNativeWindow min = await WaitForAsync(() => host.Windows.FirstOrDefault(w => w.Handle.Id == iconic));
        XNativeWindow pin = await WaitForAsync(() => host.Windows.FirstOrDefault(w => w.Handle.Id == pinned));
        Assert.AreEqual(Avalonia.Controls.WindowState.Maximized, max.WindowState, "映射前设好的最大化");
        Assert.AreEqual(Avalonia.Controls.WindowState.Minimized, min.WindowState, "initial_state = IconicState");
        Assert.IsFalse(min.ShowActivated, "一映射就最小化的窗口不抢前台");
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
        Assert.AreEqual((0, 0), (pin.Handle.Snapshot.X, pin.Handle.Snapshot.Y), "USPosition 的 (0, 0) 不被挪到屏幕中央");

        XNativeWindow[] all = [.. host.Windows];
        host.Detach();
        await WaitForAsync(() => all.All(w => !w.IsVisible) ? all : null);
        client.Dispose();
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
    });

    /// <summary>InputOnly 的顶层(GTK 的 GtkInvisible):看不见,不开原生窗口 —— 原先宿主多出一个黑色的窗口。</summary>
    [TestMethod]
    public async Task InputOnlyTopLevel_GetsNoNativeWindow() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new();
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        Task serve = server.ServeAsync(serverSide, isLocal: true);
        (uint idBase, uint root) = await HandshakeAsync(client);
        uint invisible = idBase | 1, visible = idBase | 2;
        await SendAsync(client, 1, 0, w => w.U32(invisible).U32(root).I16(-100).I16(-100).U16(10).U16(10).U16(0).U16(2).U32(0).U32(0));
        await SendAsync(client, 8, 0, w => w.U32(invisible));
        await SendAsync(client, 1, 24, w => w.U32(visible).U32(root).I16(10).I16(10).U16(60).U16(40).U16(0).U16(1).U32(0).U32(0));
        await SendAsync(client, 8, 0, w => w.U32(visible));
        await WaitForAsync(() => host.Windows.FirstOrDefault(w => w.Handle.Id == visible));   // 之前映射的 InputOnly 那个也处理过了
        Assert.IsFalse(host.Windows.Any(w => w.Handle.Id == invisible));

        XNativeWindow[] all = [.. host.Windows];
        host.Detach();
        await WaitForAsync(() => all.All(w => !w.IsVisible) ? all : null);
        client.Dispose();
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
    });

    /// <summary>连接建立;之后收到的回复按到达顺序放进 <paramref name="replies" />(事件与错误读掉不留)。</summary>
    private static async Task<(uint IdBase, uint Root)> HandshakeAsync(Stream stream, System.Collections.Concurrent.ConcurrentQueue<byte[]> replies)
    {
        await stream.WriteAsync(new byte[] { (byte)'l', 0, 11, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        await stream.FlushAsync();
        byte[] head = new byte[8];
        await stream.ReadExactlyAsync(head);
        Assert.AreEqual(1, head[0], "连接建立成功");
        byte[] rest = new byte[BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(6)) * 4];
        await stream.ReadExactlyAsync(rest);
        byte[] reply = [.. head, .. rest];
        int vendor = BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(24));
        uint root = BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(40 + ((vendor + 3) & ~3) + (reply[29] * 8)));
        _ = ReadRepliesAsync();
        return (BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(12)), root);

        async Task ReadRepliesAsync()
        {
            try
            {
                while (true)
                {
                    byte[] message = new byte[32];
                    await stream.ReadExactlyAsync(message);
                    if (message[0] == 1 || (message[0] & 0x7F) == 35)
                    {
                        byte[] extra = new byte[BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(4)) * 4];
                        await stream.ReadExactlyAsync(extra);
                        if (message[0] == 1)
                        {
                            replies.Enqueue([.. message, .. extra]);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or EndOfStreamException)
            {
            }
        }
    }

    /// <summary>InternAtom,等它的回复(用 <see cref="HandshakeAsync(Stream, System.Collections.Concurrent.ConcurrentQueue{byte[]})" /> 建立的连接)。</summary>
    private static async Task<uint> InternAsync(Stream stream, System.Collections.Concurrent.ConcurrentQueue<byte[]> replies, string name)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(name);
        await SendAsync(stream, 16, 0, w => w.U16((ushort)bytes.Length).U16(0).Bytes(bytes).Pad());
        byte[] reply = await WaitForAsync(() => replies.TryDequeue(out byte[]? r) ? r : null);
        return BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(8));
    }

    /// <summary>
    /// 客户端给的位置像窗口管理器那样摆(ICCCM §4.1.2.3):外框对准请求的位置、摆好之后把 X 窗口的实际位置报回服务端;
    /// 用户指定的 (0, 0)(<c>xterm -geometry +0+0</c>,USPosition)照办,不再当成「没给位置」挪到屏幕中央;
    /// X 的边框不画,内容区对准内区(边框外沿 + 边框宽),与服务端算指针根坐标的方式一致。
    /// </summary>
    [TestMethod]
    public async Task ClientPosition_PlacesTheFrameByGravity_HonorsUserPositionAtOrigin_AndOffsetsTheBorder() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new();
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        _ = server.ServeAsync(serverSide, isLocal: true);
        (uint idBase, uint root) = await HandshakeAsync(client);
        uint origin = idBase | 1, bordered = idBase | 2;

        // (0, 0) 带 USPosition:用户指定的位置。
        await SendAsync(client, 1, 24, w => w.U32(origin).U32(root).I16(0).I16(0).U16(60).U16(40).U16(0).U16(1).U32(0).U32(0));
        await SendAsync(client, 18, 0, w => w.U32(origin).U32(40).U32(41).U8(32).Zero(3).U32(18).U32(1).Zero(17 * 4));   // WM_NORMAL_HINTS
        await SendAsync(client, 8, 0, w => w.U32(origin));
        // (100, 50)、边框宽 5、没有提示:按默认的 NorthWest 摆。
        await SendAsync(client, 1, 24, w => w.U32(bordered).U32(root).I16(100).I16(50).U16(60).U16(40).U16(5).U16(1).U32(0).U32(0));
        await SendAsync(client, 8, 0, w => w.U32(bordered));

        XNativeWindow first = await WaitForAsync(() => host.Windows.FirstOrDefault(w => w.Handle.Id == origin && !w.Handle.Snapshot.NeedsPlacement));
        (int ox, int oy) = host.RootOrigin;
        Assert.AreEqual(new PixelPoint(ox, oy), first.Position, "外框在用户指定的 (0, 0)");

        XNativeWindow second = await WaitForAsync(() => host.Windows.FirstOrDefault(w => w.Handle.Id == bordered && !w.Handle.Snapshot.NeedsPlacement));
        Assert.AreEqual(new PixelPoint(100 + ox, 50 + oy), second.Position, "外框左上角在请求的位置");
        XTopLevelSnapshot s = second.Handle.Snapshot;
        Assert.AreEqual((95, 45), (s.X, s.Y), "无装饰的外框直接包着内容区:X 窗口的边框外沿在内容区左上再退 5");
        host.Detach();
    });

    /// <summary>
    /// 用户最大化 / 还原只改原生窗口管的那几个状态:客户端映射前设的 SkipTaskbar 留着,窗口照样不进任务栏
    /// (原先整组覆盖,第一次最大化就把它清掉了)。客户端之后请求的 SkipTaskbar 之类也记进服务端。
    /// </summary>
    [TestMethod]
    public async Task MaximizingKeepsClientStates_AndClientRequestedStatesAreRecorded() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new();
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        _ = server.ServeAsync(serverSide, isLocal: true);
        System.Collections.Concurrent.ConcurrentQueue<byte[]> replies = new();
        (uint idBase, uint root) = await HandshakeAsync(client, replies: replies);
        uint state = await InternAsync(client, replies, "_NET_WM_STATE");
        uint skipTaskbar = await InternAsync(client, replies, "_NET_WM_STATE_SKIP_TASKBAR");
        uint sticky = await InternAsync(client, replies, "_NET_WM_STATE_STICKY");
        uint window = idBase | 1;
        await SendAsync(client, 1, 24, w => w.U32(window).U32(root).I16(10).I16(10).U16(60).U16(40).U16(0).U16(1).U32(0).U32(0));
        await SendAsync(client, 18, 0, w => w.U32(window).U32(state).U32(4).U8(32).Zero(3).U32(1).U32(skipTaskbar));
        await SendAsync(client, 8, 0, w => w.U32(window));
        XNativeWindow native = await WaitForAsync(() => host.Windows.FirstOrDefault());
        Assert.IsFalse(native.ShowInTaskbar);

        native.WindowState = Avalonia.Controls.WindowState.Maximized;
        await WaitForAsync(() => (native.Handle.Snapshot.States & XWindowStates.Maximized) != 0 ? native : null);
        Assert.AreNotEqual(XWindowStates.None, native.Handle.Snapshot.States & XWindowStates.SkipTaskbar, "SkipTaskbar 留着");
        Assert.IsFalse(native.ShowInTaskbar);

        // 映射之后客户端请求 _NET_WM_STATE add STICKY(根窗口 ClientMessage):记进服务端。
        await SendAsync(client, 25, 0, w => w.U32(root).U32(0x180000).U8(33).U8(32).U16(0).U32(window).U32(state)
            .U32(1).U32(sticky).U32(0).U32(1).U32(0));
        await WaitForAsync(() => (native.Handle.Snapshot.States & XWindowStates.Sticky) != 0 ? native : null);
        Assert.AreNotEqual(XWindowStates.None, native.Handle.Snapshot.States & XWindowStates.Maximized);
        host.Detach();
    });

    /// <summary>
    /// 半透明的窗口(<c>_NET_WM_WINDOW_OPACITY</c>)要透明底,否则只是和自己的底色混;图像光标每个窗口只留最近的几个,
    /// 多的释放(原先放在 ConditionalWeakTable 里,系统光标句柄一直不释放)。
    /// </summary>
    [TestMethod]
    public async Task OpacityNeedsATransparentWindow_AndImageCursorsAreBounded() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new();
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        _ = server.ServeAsync(serverSide, isLocal: true);
        System.Collections.Concurrent.ConcurrentQueue<byte[]> replies = new();
        (uint idBase, uint root) = await HandshakeAsync(client, replies: replies);
        uint opacity = await InternAsync(client, replies, "_NET_WM_WINDOW_OPACITY");
        uint window = idBase | 1;
        await SendAsync(client, 1, 24, w => w.U32(window).U32(root).I16(0).I16(0).U16(60).U16(40).U16(0).U16(1).U32(0).U32(0));
        await SendAsync(client, 8, 0, w => w.U32(window));
        XNativeWindow native = await WaitForAsync(() => host.Windows.FirstOrDefault());
        Assert.Contains(Avalonia.Controls.WindowTransparencyLevel.None, native.TransparencyLevelHint.ToArray());

        await SendAsync(client, 18, 0, w => w.U32(window).U32(opacity).U32(6).U8(32).Zero(3).U32(1).U32(0x80000000));   // CARDINAL 一半
        await WaitForAsync(() => native.TransparencyLevelHint.Contains(Avalonia.Controls.WindowTransparencyLevel.Transparent) ? native : null);
        Assert.AreEqual(0.5, native.Opacity, 0.01);

        for (int i = 0; i < 40; i++)
        {
            native.ApplyCursor(new XCursor(XCursorShape.Arrow, new XCursorImage(2, 2, 0, 0, new uint[] { (uint)i, 0, 0, 0 })));
        }
        Assert.IsLessThanOrEqualTo(16, native.ImageCursorCount, "图像光标有上限");
        Assert.IsNotNull(native.Cursor, "正显示的那个还在");
        host.Detach();
    });

    /// <summary>
    /// 停服后马上重启:UI 队列里还排着旧服务端的回调,新服务端的窗口 XID 又与旧的重合。旧回调既不能用旧句柄建原生窗口
    /// (之后的注入会把旧句柄交给新服务端,抛 ArgumentException),也不能按 XID 误关新服务端的窗口。
    /// </summary>
    [TestMethod]
    public async Task StaleCallbacksFromAStoppedServer_DoNotTouchTheNewServersWindows() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new();
        X11ServerOptions options = new() { ListenTcp = false, UnixSocketPath = "" };
        await using X11Server old = new(options, host);
        await host.AttachAsync(old, CancellationToken.None);
        (InMemoryDuplexStream oldSide, InMemoryDuplexStream oldClient) = InMemoryTransport.CreatePair();
        _ = old.ServeAsync(oldSide, isLocal: true);
        (uint oldBase, uint oldRoot) = await HandshakeAsync(oldClient);
        await SendAsync(oldClient, 1, 24, w => w.U32(oldBase | 1).U32(oldRoot).I16(10).I16(10).U16(60).U16(40).U16(0).U16(1).U32(0).U32(0));
        await SendAsync(oldClient, 8, 0, w => w.U32(oldBase | 1));
        XTopLevelWindow stale = (await WaitForAsync(() => host.Windows.FirstOrDefault())).Handle;
        host.Detach();

        await using X11Server current = new(options, host);
        await host.AttachAsync(current, CancellationToken.None);
        (InMemoryDuplexStream newSide, InMemoryDuplexStream newClient) = InMemoryTransport.CreatePair();
        _ = current.ServeAsync(newSide, isLocal: true);
        (uint newBase, uint newRoot) = await HandshakeAsync(newClient);
        await SendAsync(newClient, 1, 24, w => w.U32(newBase | 1).U32(newRoot).I16(10).I16(10).U16(60).U16(40).U16(0).U16(1).U32(0).U32(0));
        await SendAsync(newClient, 8, 0, w => w.U32(newBase | 1));
        XNativeWindow fresh = await WaitForAsync(() => host.Windows.FirstOrDefault(w => ReferenceEquals(w.Handle.Server, current)));
        Assert.AreEqual(stale.Id, fresh.Handle.Id, "两个服务端的第一个窗口 XID 相同");

        // 旧服务端收工时才交出来的回调。
        host.TopLevelUnmapped(stale);
        host.TopLevelMapped(stale);
        host.TopLevelChanged(stale, XTopLevelChanges.All);
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
        Assert.IsTrue(fresh.IsVisible, "新服务端的窗口没被旧回调关掉");
        Assert.IsFalse(host.Windows.Any(w => ReferenceEquals(w.Handle, stale)), "旧句柄没有原生窗口");
        host.Detach();
    });

    /// <summary>
    /// 最大化 / 全屏时尺寸由系统定:客户端自己改了尺寸,把原生窗口的尺寸推回给它 —— 原先不理,X 缓冲从此与原生窗口对不上。
    /// </summary>
    [TestMethod]
    public async Task ClientResizeWhileMaximized_IsPushedBackToTheNativeSize() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new();
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        _ = server.ServeAsync(serverSide, isLocal: true);
        System.Collections.Concurrent.ConcurrentQueue<byte[]> replies = new();
        (uint idBase, uint root) = await HandshakeAsync(client, replies: replies);
        uint window = idBase | 1;
        await SendAsync(client, 1, 24, w => w.U32(window).U32(root).I16(10).I16(10).U16(60).U16(40).U16(0).U16(1).U32(0).U32(0));
        await SendAsync(client, 8, 0, w => w.U32(window));
        XNativeWindow native = await WaitForAsync(() => host.Windows.FirstOrDefault(w => !w.Handle.Snapshot.NeedsPlacement));
        native.WindowState = Avalonia.Controls.WindowState.Maximized;
        Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(Avalonia.Controls.WindowState.Maximized, native.WindowState);
        (int width, int height) = ((int)native.ClientSize.Width, (int)native.ClientSize.Height);

        // 客户端自己改尺寸;随后一个有回复的请求回来了,服务端就已经照办了(快照此刻是客户端要的尺寸)。
        await SendAsync(client, 12, 0, w => w.U32(window).U16(0x4 | 0x8).U16(0).U32((uint)width + 37).U32((uint)height + 21));
        await InternAsync(client, replies, "WM_STATE");
        await WaitForAsync(() => native.Handle.Snapshot is { } s && s.Width == width && s.Height == height ? native : null);
        host.Detach();
    });

    /// <summary>
    /// 桌面类窗口(xfdesktop 一类,<c>_NET_WM_WINDOW_TYPE_DESKTOP</c>)不给原生窗口 —— 原先它是一个铺满虚拟桌面的无边框窗口,
    /// 一激活就挡住本机所有程序;映射之后才改成桌面类型的收掉,改回来的重新显示。
    /// </summary>
    [TestMethod]
    public async Task DesktopTypeWindow_GetsNoNativeWindow() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new();
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        _ = server.ServeAsync(serverSide, isLocal: true);
        System.Collections.Concurrent.ConcurrentQueue<byte[]> replies = new();
        (uint idBase, uint root) = await HandshakeAsync(client, replies: replies);
        uint type = await InternAsync(client, replies, "_NET_WM_WINDOW_TYPE");
        uint desktop = await InternAsync(client, replies, "_NET_WM_WINDOW_TYPE_DESKTOP");
        uint normal = await InternAsync(client, replies, "_NET_WM_WINDOW_TYPE_NORMAL");
        uint icons = idBase | 1, editor = idBase | 2;

        await SendAsync(client, 1, 24, w => w.U32(icons).U32(root).I16(0).I16(0).U16(800).U16(600).U16(0).U16(1).U32(0).U32(0));
        await SendAsync(client, 18, 0, w => w.U32(icons).U32(type).U32(4).U8(32).Zero(3).U32(1).U32(desktop));   // ATOM
        await SendAsync(client, 8, 0, w => w.U32(icons));
        await SendAsync(client, 1, 24, w => w.U32(editor).U32(root).I16(10).I16(10).U16(60).U16(40).U16(0).U16(1).U32(0).U32(0));
        await SendAsync(client, 8, 0, w => w.U32(editor));
        XNativeWindow shown = await WaitForAsync(() => host.Windows.FirstOrDefault(w => w.Handle.Id == editor));
        Assert.IsFalse(host.Windows.Any(w => w.Handle.Id == icons), "桌面窗口没有原生窗口");

        // 映射之后改成桌面:收掉;再改回普通:重新显示。
        await SendAsync(client, 18, 0, w => w.U32(editor).U32(type).U32(4).U8(32).Zero(3).U32(1).U32(desktop));
        await WaitForAsync(() => !shown.IsVisible ? shown : null);
        Assert.IsFalse(host.Windows.Any(w => w.Handle.Id == editor));
        await SendAsync(client, 18, 0, w => w.U32(editor).U32(type).U32(4).U8(32).Zero(3).U32(1).U32(normal));
        await WaitForAsync(() => host.Windows.FirstOrDefault(w => w.Handle.Id == editor && w.IsVisible));
        host.Detach();
    });

    /// <summary>
    /// owner 级联关闭(Avalonia 关 owner 时先问它拥有的窗口,有一个不肯 owner 就关不掉):
    /// 父窗口在 X 里取消映射、对话框还映射着 → 父窗口收掉,对话框不带 owner 重新显示;停服时一个都不留;
    /// 弹层的关闭不转给客户端(没有 WM_DELETE_WINDOW 的弹层原先一关就断开了整个程序)。
    /// </summary>
    [TestMethod]
    public async Task OwnerCascade_ReshowsStillMappedDialogs_LeavesNoGhosts_AndPopupCloseKeepsTheClient() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new();
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        Task serve = server.ServeAsync(serverSide, isLocal: true);
        (uint idBase, uint root) = await HandshakeAsync(client);
        uint parent = idBase | 1, dialog = idBase | 2, popup = idBase | 3;
        await SendAsync(client, 1, 24, w => w.U32(parent).U32(root).I16(10).I16(10).U16(80).U16(60).U16(0).U16(1).U32(0).U32(0));
        await SendAsync(client, 1, 24, w => w.U32(dialog).U32(root).I16(20).I16(20).U16(40).U16(30).U16(0).U16(1).U32(0).U32(0));
        await SendAsync(client, 18, 0, w => w.U32(dialog).U32(68).U32(33).U8(32).Zero(3).U32(1).U32(parent));   // WM_TRANSIENT_FOR
        await SendAsync(client, 8, 0, w => w.U32(parent));
        await SendAsync(client, 8, 0, w => w.U32(dialog));
        XNativeWindow parentNative = await WaitForAsync(() => host.Windows.FirstOrDefault(w => w.Handle.Id == parent));
        XNativeWindow dialogNative = await WaitForAsync(() => host.Windows.FirstOrDefault(w => w.Handle.Id == dialog));
        Assert.AreSame(parentNative, dialogNative.Owner, "对话框压在父窗口之上");

        // 父窗口取消映射、对话框还在:父窗口收掉,对话框重新显示(原先对话框拦下关闭,父窗口成了关不掉的空壳)。
        await SendAsync(client, 10, 0, w => w.U32(parent));
        await WaitForAsync(() => !parentNative.IsVisible ? parentNative : null);
        XNativeWindow reshown = await WaitForAsync(() => host.Windows.FirstOrDefault(w => w.Handle.Id == dialog && w.IsVisible));
        Assert.IsNull(reshown.Owner);

        // 弹层(override-redirect):原生窗口被关(Alt+F4)不转给客户端,客户端不被断开。
        await SendAsync(client, 1, 24, w => w.U32(popup).U32(root).I16(30).I16(30).U16(20).U16(20).U16(0).U16(1).U32(0).U32(0x200).U32(1));
        await SendAsync(client, 8, 0, w => w.U32(popup));
        XNativeWindow popupNative = await WaitForAsync(() => host.Windows.FirstOrDefault(w => w.Handle.Id == popup));
        popupNative.Close();
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
        Assert.IsFalse(serve.IsCompleted, "客户端没有被断开");
        Assert.IsTrue(popupNative.IsVisible);

        // 停服:一个原生窗口都不留。
        XNativeWindow[] all = [.. host.Windows];
        host.Detach();
        await WaitForAsync(() => all.All(w => !w.IsVisible) ? all : null);
    });

    /// <summary>
    /// 按钮按着的时候窗口失活(Alt+Tab、别的窗口抢走)或失去捕获:之后的松开不会再送到这个窗口,X 那边要替它松开 ——
    /// 否则那个按钮一直按着、自动抓取也一直不解除。之后真的松开时不再补一次。
    /// headless 平台不发 Deactivated / PointerCaptureLost,这里直接调那两个处理器都调的 <see cref="XNativeWindow.ReleaseHeldButtons" />。
    /// </summary>
    [TestMethod]
    public async Task ReleaseHeldButtons_ReleasesInX_AndTheLaterMouseUpIsNotRepeated() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new();
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        Task serve = server.ServeAsync(serverSide, isLocal: true);
        System.Collections.Concurrent.ConcurrentQueue<byte> events = new();
        (uint idBase, uint root) = await HandshakeAsync(client, events);
        uint window = idBase | 1;
        await SendAsync(client, 1, 24, w => w.U32(window).U32(root).I16(0).I16(0).U16(60).U16(40).U16(0).U16(1).U32(0)
            .U32(0x800).U32(0x4 | 0x8));                                                  // ButtonPress | ButtonRelease
        await SendAsync(client, 8, 0, w => w.U32(window));
        XNativeWindow native = await WaitForAsync(() => host.Windows.FirstOrDefault());
        native.Activate();

        native.MouseDown(new Point(5, 5), MouseButton.Left);
        await WaitForAsync(() => events.Contains((byte)4) ? native : null);           // ButtonPress

        native.ReleaseHeldButtons();
        await WaitForAsync(() => events.Count(e => e == 5) == 1 ? native : null);       // ButtonRelease
        native.MouseUp(new Point(5, 5), MouseButton.Left);                               // 之后真的松开:不再补一个
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
        Assert.ContainsSingle(e => e == 5, events, "松开只有一次");

        native.CloseByHost();
        host.Detach();
        client.Dispose();
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
    });

    /// <summary>
    /// 按住一个键:同一个键再来 KeyDown(系统的自动重复,中间没有 KeyUp)时告诉服务端这是重复 —— 核心客户端看到
    /// 「按下、松开、按下、松开」(X 的自动重复),而不是「按下、按下、松开」;修饰键的重复由服务端丢掉。
    /// </summary>
    [TestMethod]
    public async Task 按住键时重复的KeyDown按自动重复转交_修饰键不重复() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new();
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        Task serve = server.ServeAsync(serverSide, isLocal: true);
        System.Collections.Concurrent.ConcurrentQueue<byte> events = new();
        (uint idBase, uint root) = await HandshakeAsync(client, events);
        uint window = idBase | 1;
        await SendAsync(client, 1, 24, w => w.U32(window).U32(root).I16(0).I16(0).U16(60).U16(40).U16(0).U16(1).U32(0)
            .U32(0x800).U32(0x1 | 0x2));                                                // KeyPress | KeyRelease
        await SendAsync(client, 8, 0, w => w.U32(window));
        XNativeWindow native = await WaitForAsync(() => host.Windows.FirstOrDefault());
        native.Activate();
        server.FocusTopLevel(native.Handle);
        byte[] Keys() => [.. events.Where(e => e is 2 or 3)];

        native.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.None);
        native.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.None);                   // 自动重复
        native.KeyReleaseQwerty(PhysicalKey.A, RawInputModifiers.None);
        await WaitForAsync(() => Keys().Length >= 4 ? native : null);
        native.KeyPressQwerty(PhysicalKey.ShiftLeft, RawInputModifiers.Shift);
        native.KeyPressQwerty(PhysicalKey.ShiftLeft, RawInputModifiers.Shift);         // Windows 上按住 Shift 一直有 KeyDown
        native.KeyReleaseQwerty(PhysicalKey.ShiftLeft, RawInputModifiers.None);
        await WaitForAsync(() => Keys().Length >= 6 ? native : null);
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
        CollectionAssert.AreEqual(new byte[] { 2, 3, 2, 3, 2, 3 }, Keys());

        native.CloseByHost();
        host.Detach();
        client.Dispose();
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
    });

    /// <summary>
    /// 本机输入法(xs_plan F5):输入法上屏的字经 InjectText 输入给 X(借一个键码,客户端先收到 MappingNotify);
    /// 按键自己打出的字(KeyDown 之后的 TextInput)不重复输入;输入法在组字时的键(ImeProcessed)不转交;设置关了就不接输入法的字。
    /// </summary>
    [TestMethod]
    public async Task 本机输入法上屏的字输入给X_按键自己的字不重复_组字中的键不转交() => await _session.RunOnUiAsync(async () =>
    {
        // 不在后台读桌面的键盘(Linux 上默认会读):读完换键位表的那一次 MappingNotify 会混进这里数的事件。
        AvaloniaXServerHost host = new() { DesktopKeyboardReader = null };
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        Task serve = server.ServeAsync(serverSide, isLocal: true);
        System.Collections.Concurrent.ConcurrentQueue<byte> events = new();
        (uint idBase, uint root) = await HandshakeAsync(client, events);
        uint window = idBase | 1;
        await SendAsync(client, 1, 24, w => w.U32(window).U32(root).I16(0).I16(0).U16(60).U16(40).U16(0).U16(1).U32(0)
            .U32(0x800).U32(0x1 | 0x2));                                                // KeyPress | KeyRelease
        await SendAsync(client, 8, 0, w => w.U32(window));
        XNativeWindow native = await WaitForAsync(() => host.Windows.FirstOrDefault());
        native.Activate();
        server.FocusTopLevel(native.Handle);
        byte[] Seen() => [.. events.Where(e => e is 2 or 3 or 34)];
        async Task SettleAsync()
        {
            await Task.Delay(150);
            Dispatcher.UIThread.RunJobs();
        }

        // 普通的键:KeyDown 注入了键码,随后系统为它报的 TextInput 不再输入一遍。
        native.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.None);
        native.KeyTextInput("a");
        native.KeyReleaseQwerty(PhysicalKey.A, RawInputModifiers.None);
        await SettleAsync();
        CollectionAssert.AreEqual(new byte[] { 2, 3 }, Seen(), "a 只按了一次");

        // 输入法在组字:键归输入法,上屏的字经 InjectText 来 —— MappingNotify,然后按下、松开。
        native.KeyPress(Key.ImeProcessed, RawInputModifiers.None, PhysicalKey.N, null);
        native.KeyRelease(Key.ImeProcessed, RawInputModifiers.None, PhysicalKey.N, null);
        native.KeyTextInput("中");
        await SettleAsync();
        CollectionAssert.AreEqual(new byte[] { 2, 3, 34, 2, 3 }, Seen(), "组字的 N 不转交;上屏的字借键码输入");

        // 不出字的键(方向键)之后输入法上屏的字照样输入。
        native.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        native.KeyReleaseQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        native.KeyTextInput("中");
        await SettleAsync();
        CollectionAssert.AreEqual(new byte[] { 2, 3, 34, 2, 3, 2, 3, 2, 3 }, Seen(), "同一个字不再改键位表");

        // 按着 Shift 时输入法上屏的字(有的中文输入法按 Shift 把拼音原样上屏)照样输入:修饰键不出字,原先被当成 Shift 打出的字吞掉。
        native.KeyPressQwerty(PhysicalKey.ShiftLeft, RawInputModifiers.None);
        native.KeyTextInput("文");
        native.KeyReleaseQwerty(PhysicalKey.ShiftLeft, RawInputModifiers.Shift);
        await SettleAsync();
        CollectionAssert.AreEqual(new byte[] { 2, 3, 34, 2, 3, 2, 3, 2, 3, 2, 34, 2, 3, 3 }, Seen(), "Shift、上屏的字、Shift 松开");

        // 一个键报来两个字(Windows 上死键后跟拼不上的字母:「´」「x」):都是这个键的,原先第二个又输入了一遍。
        native.KeyPressQwerty(PhysicalKey.X, RawInputModifiers.None);
        native.KeyTextInput("´");
        native.KeyTextInput("x");
        native.KeyReleaseQwerty(PhysicalKey.X, RawInputModifiers.None);
        await SettleAsync();
        Assert.HasCount(16, Seen(), "只有 x 键的按下、松开");

        // 组着字切走:叠画的预编辑收掉(无头平台不发 Deactivated,直接调它的处理器)。
        TextInputMethodClientRequestedEventArgs request = new() { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
        native.RaiseEvent(request);
        request.Client!.SetPreeditText("ni");
        Assert.AreEqual("ni", native.Preedit);
        native.OnDeactivated();
        Assert.IsNull(native.Preedit, "窗口失活时清掉预编辑");

        // 设置关掉:窗口不接输入法的字。
        host.UseHostInputMethod(false);
        native.KeyTextInput("文");
        await SettleAsync();
        Assert.HasCount(16, Seen());

        native.CloseByHost();
        host.Detach();
        client.Dispose();
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
    });

    /// <summary>
    /// 本机输入法的 XIM 桥(F5 第二步):服务端报来接受输入的输入上下文 → 只交给它所在的那个窗口:候选框挪到它报的插入点;
    /// on-the-spot(程序自己画预编辑)时窗口不叠画;over-the-spot 时叠画、盖在插入点那一行;撤了就回到最后一次点击处。
    /// </summary>
    [TestMethod]
    public async Task XIM报来的插入点决定候选框位置_onthespot时不叠画预编辑() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new() { DesktopKeyboardReader = null };
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "", InputMethodName = "velashell" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        Task serve = server.ServeAsync(serverSide, isLocal: true);
        (uint idBase, uint root) = await HandshakeAsync(client);
        for (uint i = 1; i <= 2; i++)
        {
            uint id = idBase | i;
            await SendAsync(client, 1, 24, w => w.U32(id).U32(root).I16((short)(i * 100)).I16(0).U16(60).U16(40).U16(0).U16(1).U32(0).U32(0));
            await SendAsync(client, 8, 0, w => w.U32(id));
        }
        XNativeWindow first = await WaitForAsync(() => host.Windows.FirstOrDefault(w => w.Handle.Id == (idBase | 1)));
        XNativeWindow second = await WaitForAsync(() => host.Windows.FirstOrDefault(w => w.Handle.Id == (idBase | 2)));
        first.MouseDown(new Point(7, 9), MouseButton.Left);
        first.MouseUp(new Point(7, 9), MouseButton.Left);
        Rect clicked = first.ImeCursorRect;
        first.SetPreedit("ni", 2);

        host.InputMethodFocusChanged(new XInputMethodFocus(first.Handle, ClientDrawsPreedit: true, Cursor: new XRect(30, 20, 1, 16)));
        await WaitForAsync(() => first.HasReportedCursor ? first : null, "插入点交给了它所在的窗口");
        Assert.AreEqual(new Rect(30, 20, 1, 16), first.ImeCursorRect, "候选框跟着插入点(headless 的缩放是 1)");
        Assert.AreEqual("ni", first.Preedit);
        Assert.IsNull(first.OverlayPreedit, "on-the-spot:程序自己画,窗口不叠画");
        Assert.IsFalse(second.HasReportedCursor, "别的窗口不受影响");

        host.InputMethodFocusChanged(new XInputMethodFocus(first.Handle, ClientDrawsPreedit: false, Cursor: new XRect(30, 20, 1, 16)));
        await WaitForAsync(() => first.OverlayPreedit is not null ? first : null);
        Assert.AreEqual("ni", first.OverlayPreedit, "over-the-spot:程序不画,窗口叠画");

        host.InputMethodFocusChanged(null);
        await WaitForAsync(() => first.HasReportedCursor ? null : first);
        Assert.AreEqual(clicked, first.ImeCursorRect, "撤了:回到最后一次点击处");

        first.CloseByHost();
        second.CloseByHost();
        host.Detach();
        client.Dispose();
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
    });

    /// <summary>
    /// 从 X 程序往本机拖出来(F16 的另一半):手拼的 XDND 源在用户按着鼠标时把文字拖到根窗口的代理上 → 宿主在按着鼠标的那个窗口里发起本机拖放
    /// (这里换成桩:看交过去的数据,回「复制」)→ 先把结果交回服务端、再在 X 那边松开按钮 → 源发 XdndDrop,收到成功的 XdndFinished。
    /// 交出去的数据带「从 X 拖出来」的标记,拖回 X 窗口时 XDropTarget 不接。
    /// </summary>
    [TestMethod]
    public async Task X程序拖到窗口外_宿主发起本机拖放_放下之后X程序收到成功的XdndFinished() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new() { DesktopKeyboardReader = null };
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "", AcceptOutgoingDrags = true }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        Task serve = server.ServeAsync(serverSide, isLocal: true);
        System.Collections.Concurrent.ConcurrentQueue<byte[]> messages = new();
        (uint idBase, uint root) = await HandshakeAsync(client, messages: messages);
        ushort sequence = 0;
        async Task<byte[]> RequestAsync(byte opcode, byte data, Action<Body> body)
        {
            await SendAsync(client, opcode, data, body);
            ushort seq = ++sequence;
            return await WaitForAsync(() => messages.FirstOrDefault(m => m[0] == 1 && BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(2)) == seq));
        }
        async Task<uint> InternAsync(string name)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(name);
            return BinaryPrimitives.ReadUInt32LittleEndian((await RequestAsync(16, 0, w => w.U16((ushort)bytes.Length).U16(0).Bytes(bytes).Pad())).AsSpan(8));
        }
        uint U32(byte[] m, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(offset));

        uint window = idBase | 1, source = idBase | 2;
        await SendAsync(client, 1, 24, w => w.U32(window).U32(root).I16(300).I16(300).U16(60).U16(40).U16(0).U16(1).U32(0).U32(0x800).U32(0x4 | 0x8));
        sequence++;
        await SendAsync(client, 8, 0, w => w.U32(window));
        sequence++;
        XNativeWindow native = await WaitForAsync(() => host.Windows.FirstOrDefault());
        IDataTransfer? handedOver = null;
        native.StartNativeDrag = (_, data, effects) =>
        {
            handedOver = data;
            Assert.AreEqual(DragDropEffects.Copy, effects, "只给复制");
            return Task.FromResult(DragDropEffects.Copy);
        };
        native.Activate();
        native.MouseDown(new Point(5, 5), MouseButton.Left);   // 用户按着鼠标开始拖

        uint selection = await InternAsync("XdndSelection"), utf8 = await InternAsync("UTF8_STRING");
        uint enter = await InternAsync("XdndEnter"), position = await InternAsync("XdndPosition"), drop = await InternAsync("XdndDrop");
        uint finished = await InternAsync("XdndFinished"), proxyAtom = await InternAsync("XdndProxy");
        uint proxy = U32(await RequestAsync(20, 0, w => w.U32(root).U32(proxyAtom).U32(0).U32(0).U32(1)), 32);
        await SendAsync(client, 1, 0, w => w.U32(source).U32(root).I16(0).I16(0).U16(1).U16(1).U16(0).U16(2).U32(0).U32(0));
        sequence++;
        await SendAsync(client, 22, 0, w => w.U32(source).U32(selection).U32(0));
        sequence++;
        Task SendXdndAsync(uint type, uint l0, uint l1 = 0, uint l2 = 0, uint l3 = 0, uint l4 = 0)
        {
            sequence++;
            return SendAsync(client, 25, 0, w => w.U32(proxy).U32(0).U8(33).U8(32).U16(0).U32(root).U32(type).U32(l0).U32(l1).U32(l2).U32(l3).U32(l4));
        }
        await SendXdndAsync(enter, source, 5u << 24, utf8);
        await SendXdndAsync(position, source, 0, (10u << 16) | 10, 1);

        // 服务端要数据:写到请求方的属性上,发 SelectionNotify。
        byte[] request = await WaitForAsync(() => messages.FirstOrDefault(m => (m[0] & 0x7F) == 30));
        uint requestor = U32(request, 12), property = U32(request, 24);
        byte[] text = Encoding.UTF8.GetBytes("拖出来的字");
        await SendAsync(client, 18, 0, w => w.U32(requestor).U32(property).U32(utf8).U8(8).Zero(3).U32((uint)text.Length).Bytes(text).Pad());
        sequence++;
        await SendAsync(client, 25, 0, w => w.U32(requestor).U32(0).U8(31).U8(0).U16(0).U32(1).U32(requestor).U32(selection).U32(utf8).U32(property).Zero(8));
        sequence++;

        // 宿主发起了本机拖放(桩回「复制」),之后在 X 那边松开按钮。
        await WaitForAsync(() => handedOver);
        Assert.AreEqual("拖出来的字", handedOver!.TryGetText());
        Assert.IsTrue(XDragSource.IsFromX(handedOver), "带着标记:拖回 X 窗口时不接");
        await WaitForAsync(() => messages.FirstOrDefault(m => (m[0] & 0x7F) == 5), "X 那边收到了松开");
        Assert.IsFalse(native.HoldsButtons);

        await SendXdndAsync(drop, source, 0, 2);
        byte[] reply = await WaitForAsync(() => messages.FirstOrDefault(m => (m[0] & 0x7F) == 33 && U32(m, 8) == finished));
        Assert.AreEqual(1u, U32(reply, 16), "XdndFinished:接受了");

        native.CloseByHost();
        host.Detach();
        client.Dispose();
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
    });

    /// <summary>
    /// 指针交给宿主(xs_plan F8):X 窗口活动时,抓着指针的程序的 Warp 挪系统光标(根坐标加 RootOrigin)、confine-to 关住光标;
    /// 抓取解除时光标放开、之后的 Warp 不挪。
    /// </summary>
    [TestMethod]
    public async Task 抓着指针的程序Warp挪系统光标_confine_to关住光标_解除就放开() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new();
        List<(int X, int Y)> warps = [];
        List<PixelRect?> confines = [];
        host.WarpCursor = (x, y) => { warps.Add((x, y)); return true; };
        host.ConfineCursor = area => { confines.Add(area); return true; };
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        Task serve = server.ServeAsync(serverSide, isLocal: true);
        (uint idBase, uint root) = await HandshakeAsync(client, new System.Collections.Concurrent.ConcurrentQueue<byte>());
        uint window = idBase | 1;
        await SendAsync(client, 1, 24, w => w.U32(window).U32(root).I16(0).I16(0).U16(60).U16(40).U16(0).U16(1).U32(0).U32(0));
        await SendAsync(client, 8, 0, w => w.U32(window));
        XNativeWindow native = await WaitForAsync(() => host.Windows.FirstOrDefault());
        native.Activate();
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
        (int ox, int oy) = host.RootOrigin;

        await SendAsync(client, 26, 0, w => w.U32(window).U16(0x40).U8(1).U8(1).U32(window).U32(0).U32(0));   // GrabPointer,confine-to = 自己
        await SendAsync(client, 41, 0, w => w.U32(0).U32(window).I16(0).I16(0).U16(0).U16(0).I16(10).I16(12));   // WarpPointer 到窗口里的 (10, 12)
        await WaitForAsync(() => warps.Count > 0 ? native : null);
        XTopLevelSnapshot s = native.Handle.Snapshot;
        Assert.AreEqual((s.X + s.BorderWidth + 10 + ox, s.Y + s.BorderWidth + 12 + oy), warps[0], "根坐标加 RootOrigin");
        Assert.IsTrue(confines.Count > 0 && confines[^1] is { } area && area.Width > 0, "关在 confine-to 窗口里");

        // 抓取解除:光标放开;之后不抓着指针的 Warp 不挪光标。(用户切到本机窗口时同样放开 —— 无头平台不会让 X 窗口失活,这条路靠 OnWindowDeactivated。)
        await SendAsync(client, 27, 0, w => w.U32(0));   // UngrabPointer
        await WaitForAsync(() => confines.Count > 0 && confines[^1] is null ? native : null);
        await SendAsync(client, 41, 0, w => w.U32(0).U32(root).I16(0).I16(0).U16(0).U16(0).I16(30).I16(30));
        await Task.Delay(150);
        Dispatcher.UIThread.RunJobs();
        Assert.HasCount(1, warps, "不再抓着指针:不挪光标");

        native.CloseByHost();
        host.Detach();
        client.Dispose();
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
    });

    /// <summary>
    /// _NET_WM_SYNC_REQUEST(xs_plan F10):宿主改尺寸之后、客户端重画完之前画进来的像素先攒着不显示,客户端把计数器推上去才显示。
    /// </summary>
    [TestMethod]
    public async Task 改尺寸后客户端重画完之前的损伤先攒着_重画完才显示() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new();
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        Task serve = server.ServeAsync(serverSide, isLocal: true);
        System.Collections.Concurrent.ConcurrentQueue<byte[]> replies = new();
        (uint idBase, uint root) = await HandshakeAsync(client, replies: replies);
        byte[] syncName = Encoding.ASCII.GetBytes("SYNC");
        await SendAsync(client, 98, 0, w => w.U16((ushort)syncName.Length).U16(0).Bytes(syncName).Pad());
        byte sync = (await WaitForAsync(() => replies.TryDequeue(out byte[]? r) ? r : null))[9];   // 取走,后面的 InternAsync 按先后取回复
        uint protocols = await InternAsync(client, replies, "WM_PROTOCOLS");
        uint request = await InternAsync(client, replies, "_NET_WM_SYNC_REQUEST");
        uint counterProperty = await InternAsync(client, replies, "_NET_WM_SYNC_REQUEST_COUNTER");
        uint window = idBase | 1, counter = idBase | 2, gc = idBase | 3;
        await SendAsync(client, sync, 2, w => w.U32(counter).U32(0).U32(0));                                          // CreateCounter
        await SendAsync(client, 1, 24, w => w.U32(window).U32(root).I16(0).I16(0).U16(60).U16(40).U16(0).U16(1).U32(0).U32(0));
        await SendAsync(client, 18, 0, w => w.U32(window).U32(protocols).U32(4).U8(32).U8(0).U8(0).U8(0).U32(1).U32(request));
        await SendAsync(client, 18, 0, w => w.U32(window).U32(counterProperty).U32(6).U8(32).U8(0).U8(0).U8(0).U32(1).U32(counter));
        await SendAsync(client, 55, 0, w => w.U32(gc).U32(window).U32(0x4).U32(0xFF0000));                          // CreateGC,前景红
        await SendAsync(client, 8, 0, w => w.U32(window));
        XNativeWindow native = await WaitForAsync(() => host.Windows.FirstOrDefault());

        server.ResizeTopLevel(native.Handle, 80, 50);
        bool awaiting = false;
        for (int i = 0; i < 100 && !awaiting; i++)
        {
            awaiting = native.Handle.AwaitingRedraw;
            await Task.Delay(5);
        }
        Assert.IsTrue(awaiting, $"改尺寸之后在等客户端重画(快照 {native.Handle.Snapshot.Width}×{native.Handle.Snapshot.Height})");
        await SendAsync(client, 70, 0, w => w.U32(window).U32(gc).I16(0).I16(0).U16(80).U16(50));                    // 画了一半(PolyFillRectangle)
        bool held = false;
        for (int i = 0; i < 40 && !held; i++)
        {
            await Task.Delay(5);
            Dispatcher.UIThread.RunJobs();
            held = host.IsHoldingDamage(native.Handle);
        }
        Assert.IsTrue(held, $"损伤攒着(还在等:{native.Handle.AwaitingRedraw})");
        await SendAsync(client, sync, 3, w => w.U32(counter).U32(0).U32(1));                                           // 重画完:计数器推到 1
        await WaitForAsync(() => !host.IsHoldingDamage(native.Handle) && !native.Handle.AwaitingRedraw ? native : null);

        native.CloseByHost();
        host.Detach();
        client.Dispose();
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
    });

    /// <summary>帧时钟(xs_plan F25):服务端要帧时钟时宿主跟着合成器逐帧回调、每帧报一次;不要了就停。</summary>
    [TestMethod]
    public async Task 服务端要帧时钟时逐帧报帧_不要了就停() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new();
        int frames = 0;
        host.NotifyFrame = _ => frames++;
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        Task serve = server.ServeAsync(serverSide, isLocal: true);
        (uint idBase, uint root) = await HandshakeAsync(client);
        await SendAsync(client, 1, 24, w => w.U32(idBase | 1).U32(root).I16(0).I16(0).U16(60).U16(40).U16(0).U16(1).U32(0).U32(0));
        await SendAsync(client, 8, 0, w => w.U32(idBase | 1));
        await WaitForAsync(() => host.Windows.FirstOrDefault(w => w.IsVisible), "窗口显示出来");

        async Task TickAsync(int count)
        {
            for (int i = 0; i < count; i++)
            {
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(5);
            }
        }
        await TickAsync(3);
        Assert.AreEqual(0, frames, "没人要:不逐帧回调");

        // 慢的 CI 机器上几次强制的渲染节拍可能并成一帧(Ubuntu 上见过 5 拍只报了 2 帧):拍到报够为止,至多 40 拍。
        host.FrameClockWanted(true);
        Dispatcher.UIThread.RunJobs();
        for (int i = 0; i < 40 && frames < 3; i++)
        {
            await TickAsync(1);
        }
        Assert.IsGreaterThanOrEqualTo(3, frames, "要帧时钟时逐帧报帧");

        host.FrameClockWanted(false);
        Dispatcher.UIThread.RunJobs();
        await TickAsync(5);   // 已经挂上的那一次回调还会来,来了不报
        int stopped = frames;
        await TickAsync(10);
        Assert.AreEqual(stopped, frames, "不要了就停");

        host.Detach();
        client.Dispose();
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
    });

    /// <summary>屏保(xs_plan F9):X 程序挂起屏保时宿主抑制本机屏保,恢复时恢复;停 X Server 时一并恢复;Reset 重置本机空闲计时。</summary>
    [TestMethod]
    public async Task X程序挂起屏保时抑制本机屏保_停服时恢复_Reset重置空闲计时() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new();
        List<bool> inhibits = [];
        int resets = 0;
        host.InhibitIdle = on => { inhibits.Add(on); return true; };
        host.ResetIdle = () => { resets++; return true; };
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        Task serve = server.ServeAsync(serverSide, isLocal: true);
        System.Collections.Concurrent.ConcurrentQueue<byte[]> replies = new();
        await HandshakeAsync(client, replies: replies);
        byte[] name = Encoding.ASCII.GetBytes("MIT-SCREEN-SAVER");
        await SendAsync(client, 98, 0, w => w.U16((ushort)name.Length).U16(0).Bytes(name).Pad());                   // 序号 1
        byte saver = (await WaitForAsync(() => replies.FirstOrDefault(r => BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(2)) == 1)))[9];

        await SendAsync(client, saver, 5, w => w.U32(1));   // Suspend(True)
        await SendAsync(client, 115, 0, w => { });          // ForceScreenSaver(Reset)
        await WaitForAsync(() => inhibits.Count > 0 && resets > 0 ? inhibits : null);
        Assert.IsTrue(inhibits[^1], "挂起:抑制本机屏保");

        host.Detach();
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
        Assert.IsFalse(inhibits[^1], "停服:恢复");
        client.Dispose();
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
    });

    /// <summary>
    /// 平滑滚动(xs_plan F6):宿主把滚动增量原样交给服务端 —— 触控板的半格增量两次攒成一格,只认按钮的核心客户端收到一次按钮 4(向上)。
    /// </summary>
    [TestMethod]
    public async Task 滚动增量原样交给服务端_半格两次攒成一次按钮4() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new();
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        Task serve = server.ServeAsync(serverSide, isLocal: true);
        System.Collections.Concurrent.ConcurrentQueue<byte> events = new();
        (uint idBase, uint root) = await HandshakeAsync(client, events);
        uint window = idBase | 1;
        await SendAsync(client, 1, 24, w => w.U32(window).U32(root).I16(0).I16(0).U16(60).U16(40).U16(0).U16(1).U32(0)
            .U32(0x800).U32(0x4 | 0x8));                                                // ButtonPress | ButtonRelease
        await SendAsync(client, 8, 0, w => w.U32(window));
        XNativeWindow native = await WaitForAsync(() => host.Windows.FirstOrDefault());

        native.MouseWheel(new Point(10, 10), new Vector(0, 0.5), RawInputModifiers.None);
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
        Assert.IsEmpty(events.Where(e => e is 4 or 5), "半格:还没攒够");
        native.MouseWheel(new Point(10, 10), new Vector(0, 0.5), RawInputModifiers.None);
        await WaitForAsync(() => events.Count(e => e is 4 or 5) >= 2 ? native : null);
        CollectionAssert.AreEqual(new byte[] { 4, 5 }, events.Where(e => e is 4 or 5).ToArray(), "一次按下、一次松开");

        native.CloseByHost();
        host.Detach();
        client.Dispose();
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
    });

    /// <summary>
    /// 用户在 VelaShell 自己的窗口(终端之类)里打字:X 服务端的空闲时间同样归零 —— 远端程序经 MIT-SCREEN-SAVER 看到的不再只是 X 窗口里的输入。
    /// </summary>
    [TestMethod]
    public async Task 本机窗口里的按键让X服务端的空闲时间归零() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new();
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        Task serve = server.ServeAsync(serverSide, isLocal: true);
        System.Collections.Concurrent.ConcurrentQueue<byte[]> replies = new();
        (_, uint root) = await HandshakeAsync(client, replies: replies);
        static ushort Sequence(byte[] reply) => BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(2));

        byte[] name = Encoding.ASCII.GetBytes("MIT-SCREEN-SAVER");
        await SendAsync(client, 98, 0, w => w.U16((ushort)name.Length).U16(0).Bytes(name).Pad());                   // 序号 1
        byte saver = (await WaitForAsync(() => replies.FirstOrDefault(r => Sequence(r) == 1)))[9];
        Avalonia.Controls.Window local = new() { Width = 100, Height = 80 };
        local.Show();
        await Task.Delay(150);

        local.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.None);
        await SendAsync(client, saver, 1, w => w.U32(root));                                                         // QueryInfo,序号 2
        byte[] info = await WaitForAsync(() => replies.FirstOrDefault(r => Sequence(r) == 2));
        uint idle = BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(16));
        Assert.IsLessThan(100u, idle, $"本机窗口里刚按了键,空闲应归零,实际 {idle} ms");

        local.Close();
        host.Detach();
        client.Dispose();
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
    });

    /// <summary>
    /// 「每个 SSH 会话一个显示」时同时附着着几个宿主:本机窗口里的按键让每个服务端的空闲时间都归零。
    /// 原先只记着最后附着的那一个宿主,先开的会话的服务端一直以为用户走了。
    /// </summary>
    [TestMethod]
    public async Task 同时附着几个宿主时_本机按键让每个服务端的空闲时间都归零() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost firstHost = new(), secondHost = new();
        await using X11Server first = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, firstHost);
        await using X11Server second = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, secondHost);
        await firstHost.AttachAsync(first, CancellationToken.None);
        await secondHost.AttachAsync(second, CancellationToken.None);
        List<(InMemoryDuplexStream Client, Task Serve, System.Collections.Concurrent.ConcurrentQueue<byte[]> Replies, uint Root, byte Saver)> clients = [];
        static ushort Sequence(byte[] reply) => BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(2));
        foreach (X11Server server in (X11Server[])[first, second])
        {
            (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
            Task serve = server.ServeAsync(serverSide, isLocal: true);
            System.Collections.Concurrent.ConcurrentQueue<byte[]> replies = new();
            (_, uint root) = await HandshakeAsync(client, replies: replies);
            byte[] name = Encoding.ASCII.GetBytes("MIT-SCREEN-SAVER");
            await SendAsync(client, 98, 0, w => w.U16((ushort)name.Length).U16(0).Bytes(name).Pad());               // 序号 1
            byte saver = (await WaitForAsync(() => replies.FirstOrDefault(r => Sequence(r) == 1)))[9];
            clients.Add((client, serve, replies, root, saver));
        }
        Avalonia.Controls.Window local = new() { Width = 100, Height = 80 };
        local.Show();
        await Task.Delay(1000);   // 没归零的话空闲至少 1 秒;归零了的话只剩按键之后这一两次往返(CI 的 macOS 上曾到 174 毫秒)

        local.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.None);
        foreach ((InMemoryDuplexStream client, _, System.Collections.Concurrent.ConcurrentQueue<byte[]> replies, uint root, byte saver) in clients)
        {
            await SendAsync(client, saver, 1, w => w.U32(root));                                                     // QueryInfo,序号 2
            byte[] info = await WaitForAsync(() => replies.FirstOrDefault(r => Sequence(r) == 2));
            uint idle = BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(16));
            Assert.IsLessThan(600u, idle, $"本机窗口里刚按了键,每个服务端的空闲都应归零,实际 {idle} ms");
        }

        local.Close();
        firstHost.Detach();
        secondHost.Detach();
        foreach ((InMemoryDuplexStream client, Task serve, _, _, _) in clients)
        {
            client.Dispose();
            await serve.WaitAsync(TimeSpan.FromSeconds(5));
        }
    });

    /// <summary>
    /// macOS 上 Command 组合键收不到 KeyUp:Command 松开时把按着它时按下的键一并松开 —— 否则 X 那边以为 C 一直按着。
    /// 在别的系统上打开这个处理来测(macOS 才默认打开)。
    /// </summary>
    [TestMethod]
    public async Task Command组合键收不到KeyUp时_Command松开把它们一并松开() => await _session.RunOnUiAsync(async () =>
    {
        bool before = XNativeWindow.CommandKeyUpMayBeLost;
        XNativeWindow.CommandKeyUpMayBeLost = true;
        try
        {
            AvaloniaXServerHost host = new();
            await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
            await host.AttachAsync(server, CancellationToken.None);
            (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
            Task serve = server.ServeAsync(serverSide, isLocal: true);
            System.Collections.Concurrent.ConcurrentQueue<byte> events = new();
            (uint idBase, uint root) = await HandshakeAsync(client, events);
            uint window = idBase | 1;
            await SendAsync(client, 1, 24, w => w.U32(window).U32(root).I16(0).I16(0).U16(60).U16(40).U16(0).U16(1).U32(0)
                .U32(0x800).U32(0x1 | 0x2));
            await SendAsync(client, 8, 0, w => w.U32(window));
            XNativeWindow native = await WaitForAsync(() => host.Windows.FirstOrDefault());
            native.Activate();
            server.FocusTopLevel(native.Handle);
            byte[] Keys() => [.. events.Where(e => e is 2 or 3)];

            native.KeyPressQwerty(PhysicalKey.MetaLeft, RawInputModifiers.Meta);
            native.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Meta);       // Cmd+C:AppKit 不发它的 KeyUp
            native.KeyReleaseQwerty(PhysicalKey.MetaLeft, RawInputModifiers.None);
            await WaitForAsync(() => Keys().Length >= 4 ? native : null);
            CollectionAssert.AreEqual(new byte[] { 2, 2, 3, 3 }, Keys(), "Command 与 C 都松开了");

            native.CloseByHost();
            host.Detach();
            client.Dispose();
            await serve.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            XNativeWindow.CommandKeyUpMayBeLost = before;
        }
    });

    /// <summary>
    /// override-redirect 的弹出层只在用户正在用 X 窗口时才系统级置顶:用户在本机窗口里时映射上来的(远端程序画的假凭据框)不盖住本机程序,
    /// 用户回到某个 X 窗口时照常置顶(菜单要在最上面)。
    /// </summary>
    [TestMethod]
    public async Task OverrideRedirectPopup_IsTopmostOnlyWhileAnXWindowIsActive() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new();
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        Task serve = server.ServeAsync(serverSide, isLocal: true);
        (uint idBase, uint root) = await HandshakeAsync(client);
        uint normal = idBase | 1, popup = idBase | 2;
        await SendAsync(client, 1, 24, w => w.U32(popup).U32(root).I16(10).I16(10).U16(30).U16(20).U16(0).U16(1).U32(0)
            .U32(0x200).U32(1));                                                                       // override-redirect
        await SendAsync(client, 8, 0, w => w.U32(popup));
        XNativeWindow menu = await WaitForAsync(() => host.Windows.FirstOrDefault(w => w.Handle.Id == popup));
        Assert.IsFalse(menu.Topmost, "没有 X 窗口是活动的:原先一直系统级置顶,盖住所有本机程序");

        // 用户回到 X 窗口(映射一个普通窗口,它随之成为活动窗口)。
        await SendAsync(client, 1, 24, w => w.U32(normal).U32(root).I16(0).I16(0).U16(60).U16(40).U16(0).U16(1).U32(0).U32(0));
        await SendAsync(client, 8, 0, w => w.U32(normal));
        XNativeWindow main = await WaitForAsync(() => host.Windows.FirstOrDefault(w => w.Handle.Id == normal));
        main.Activate();
        await WaitForAsync(() => menu.Topmost ? menu : null);

        main.CloseByHost();
        menu.CloseByHost();
        host.Detach();
        client.Dispose();
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
    });

    /// <summary>
    /// 原生窗口按 256 × 256 切块、只取损伤矩形:跨块的窗口、后来只改了右下角一小块、客户端改了尺寸(缓冲变大、块数变多)之后,
    /// 各块的像素都对,没改到的地方保持原样。
    /// </summary>
    [TestMethod]
    public async Task TiledSurface_CopiesOnlyDamage_AndFollowsResize() => await _session.RunOnUiAsync(async () =>
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
    });

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
            var pin = GCHandle.Alloc(pixel, GCHandleType.Pinned);
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

    private static async Task<T> WaitForAsync<T>(Func<T?> probe, string? what = null) where T : class
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
        throw new TimeoutException(what is null ? "等不到预期的状态" : $"等不到预期的状态:{what}");
    }

    // ------------------------------------------------------------------ 最小的 X 客户端(小端)

    private static async Task<(uint IdBase, uint Root)> HandshakeAsync(Stream stream, System.Collections.Concurrent.ConcurrentQueue<byte>? events = null,
        System.Collections.Concurrent.ConcurrentQueue<byte[]>? replies = null, System.Collections.Concurrent.ConcurrentQueue<byte[]>? messages = null)
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
        // 不看的就读掉,只要别把管道堵住
        _ = messages is not null ? ReadMessagesAsync(stream, messages)
            : replies is not null ? ReadRepliesAsync(stream, replies) : events is null ? ReadAndDiscardAsync(stream) : ReadEventsAsync(stream, events);
        return (idBase, root);
    }

    /// <summary>回复与事件都整条记下(错误跳过)。</summary>
    private static async Task ReadMessagesAsync(Stream stream, System.Collections.Concurrent.ConcurrentQueue<byte[]> messages)
    {
        try
        {
            while (true)
            {
                byte[] head = new byte[32];
                await stream.ReadExactlyAsync(head);
                byte[] extra = [];
                if (head[0] == 1 || (head[0] & 0x7F) == 35)
                {
                    extra = new byte[BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4)) * 4];
                    await stream.ReadExactlyAsync(extra);
                }
                if (head[0] != 0)
                {
                    messages.Enqueue([.. head, .. extra]);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or EndOfStreamException)
        {
        }
    }

    /// <summary>记下收到的回复(整条,含额外长度);事件与错误跳过。</summary>
    private static async Task ReadRepliesAsync(Stream stream, System.Collections.Concurrent.ConcurrentQueue<byte[]> replies)
    {
        byte[] head = new byte[32];
        try
        {
            while (true)
            {
                await stream.ReadExactlyAsync(head);
                byte[] extra = [];
                if (head[0] == 1 || (head[0] & 0x7F) == 35)
                {
                    extra = new byte[BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4)) * 4];
                    await stream.ReadExactlyAsync(extra);
                }
                if (head[0] == 1)
                {
                    replies.Enqueue([.. head, .. extra]);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or EndOfStreamException)
        {
        }
    }

    /// <summary>记下收到的事件码(回复与错误跳过)。</summary>
    private static async Task ReadEventsAsync(Stream stream, System.Collections.Concurrent.ConcurrentQueue<byte> events)
    {
        byte[] head = new byte[32];
        try
        {
            while (true)
            {
                await stream.ReadExactlyAsync(head);
                if (head[0] == 1 || (head[0] & 0x7F) == 35)
                {
                    await stream.ReadExactlyAsync(new byte[BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4)) * 4]);
                }
                if (head[0] > 1)
                {
                    events.Enqueue((byte)(head[0] & 0x7F));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or EndOfStreamException)
        {
        }
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
