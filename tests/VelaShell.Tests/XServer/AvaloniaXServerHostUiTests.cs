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
using VelaShell.XServer.Host;
using VelaShell.XServer.Server;

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
        Assert.AreEqual(StandardCursorType.Ibeam, XInputMap.Cursor(152));
        Assert.AreEqual(StandardCursorType.None, XInputMap.Cursor(-2));
        Assert.AreEqual(StandardCursorType.Arrow, XInputMap.Cursor(-1));
    }

    [TestMethod]
    public void WindowsKeymap_MapsCharactersAndDeadKeysToKeysyms()
    {
        Assert.AreEqual('a', WindowsKeymap.Keysym('a'));
        Assert.AreEqual(0xe4u, WindowsKeymap.Keysym('ä'), "Latin-1 字符就是它自己");
        Assert.AreEqual(0x0100_20ACu, WindowsKeymap.Keysym('€'), "其余用 Unicode 键值");
        Assert.AreEqual(0xfe52u, WindowsKeymap.DeadKeysym('^'), "dead_circumflex");
        Assert.AreEqual(0xfe51u, WindowsKeymap.DeadKeysym('´'), "dead_acute");
        Assert.AreEqual('@', WindowsKeymap.DeadKeysym('@'), "不认识的死键按普通字符给");
    }

    /// <summary>按当前布局算键位表:不打字符的键(退格、Tab、回车、Ctrl、Shift)与布局无关,永远是标准键值。</summary>
    [TestMethod]
    public void WindowsKeymap_KeepsNonCharacterKeysFixed()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("只在 Windows 上按系统布局推键位表");
            return;
        }
        (uint[] main, uint[] intl) = WindowsKeymap.Build(WindowsKeymap.CurrentLayout());
        uint At(byte keycode, int column) => main[((keycode - WindowsKeymap.FirstKeycode) * 2) + column];
        Assert.AreEqual(0xff08u, At(XKeycodes.BackSpace, 0));
        Assert.AreEqual(0xfe20u, At(XKeycodes.Tab, 1), "Shift+Tab = ISO_Left_Tab");
        Assert.AreEqual(0xff0du, At(XKeycodes.Return, 0));
        Assert.AreEqual(0xffe1u, At(XKeycodes.ShiftLeft, 0));
        Assert.AreNotEqual(0u, At(XKeycodes.A, 0), "字母键在任何布局下都打得出字符");
        Assert.HasCount(2, intl);
    }

    [TestMethod]
    public async Task MappedWindow_BecomesNativeWindow_AndCloseButtonDisconnectsClient() => await _session.Dispatch(async () =>
    {
        AvaloniaXServerHost host = new();
        await using X11Server server = new(new XServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        Task serve = server.ServeAsync(serverSide);

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
