using System.Text;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>
/// 单窗口(rootful)模式(F13):服务端不当窗口管理器,远端的窗口管理器接手;宿主只拿到一整块屏幕(<see cref="X11Server.Screen" />),
/// 屏幕是根窗口的背景加上按堆叠次序拼起来的顶层(边框、形状);指针按根坐标注入;缩放屏幕窗口就是改屏幕尺寸。
/// </summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class RootfulTests
{
    private const byte ButtonPress = 4, MapRequest = 20, ConfigureNotify = 22;

    private static async Task<uint> InternAsync(XTestClient c, string name)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(name);
        return (await c.RequestAsync(16, 0, b => b.U16((ushort)bytes.Length).U16(0).Bytes(bytes).Pad())).U32(8);
    }

    /// <summary>建一个顶层:背景色、边框宽与边框色、选的事件。</summary>
    private static async Task<uint> CreateTopAsync(XTestClient c, int x, int y, int width, int height, uint background,
        int border = 0, uint borderPixel = 0, uint events = 0)
    {
        uint id = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(id).U32(c.RootWindow).I16((short)x).I16((short)y).U16((ushort)width).U16((ushort)height)
            .U16((ushort)border).U16(1).U32(0).U32(0x2 | 0x8 | 0x800).U32(background).U32(borderPixel).U32(events));
        return id;
    }

    private static uint Pixel(XTopLevelWindow screen, int x, int y)
    {
        uint value = 0;
        Assert.IsTrue(screen.ReadPixels((pixels, width, _) => value = pixels[(y * width) + x] & 0xFFFFFF));
        return value;
    }

    [TestMethod]
    public async Task 单窗口模式下服务端不当窗口管理器_远端的窗口管理器接手_宿主只拿到屏幕()
    {
        await using (X11Server rootless = new())
        {
            Assert.IsNull(rootless.Screen, "默认 rootless:没有屏幕句柄");
        }

        using RecordingHost host = new();
        await using X11Server server = new(new X11ServerOptions { Rootful = true, ScreenWidth = 320, ScreenHeight = 200 }, host);
        XTopLevelWindow screen = server.Screen!;
        Assert.AreEqual((0, 0, 320, 200, true), (screen.Snapshot.X, screen.Snapshot.Y, screen.Snapshot.Width, screen.Snapshot.Height, screen.Snapshot.IsMapped));

        await using XTestClient wm = await XTestClient.ConnectAsync(server);
        uint wmS0 = await InternAsync(wm, "WM_S0");
        Assert.AreEqual(0u, (await wm.RequestAsync(23, 0, b => b.U32(wmS0))).U32(8), "WM_S0 没人占:窗口管理器起得来");
        uint check = await InternAsync(wm, "_NET_SUPPORTING_WM_CHECK");
        Assert.AreEqual(0u, (await wm.RequestAsync(20, 0, b => b.U32(wm.RootWindow).U32(check).U32(0).U32(0).U32(1))).U32(8), "没有 _NET_SUPPORTING_WM_CHECK");
        uint xsettings = await InternAsync(wm, "_XSETTINGS_S0");
        Assert.AreEqual(0u, (await wm.RequestAsync(23, 0, b => b.U32(xsettings))).U32(8), "XSETTINGS 归远端桌面的设置守护进程");
        await wm.SendAsync(2, 0, b => b.U32(wm.RootWindow).U32(0x800).U32(0x180000));   // SubstructureRedirect | SubstructureNotify
        await wm.SyncAsync();

        await using XTestClient app = await XTestClient.ConnectAsync(server);
        uint window = await CreateTopAsync(app, 10, 10, 50, 40, 0xFF0000);
        await app.SendAsync(8, 0, b => b.U32(window));   // MapWindow:转成给窗口管理器的 MapRequest
        XMessage request = await wm.NextEventAsync(MapRequest);
        Assert.AreEqual(window, request.U32(8));
        Assert.AreEqual(0, (await app.RequestAsync(3, 0, b => b.U32(window))).Bytes[26], "窗口管理器照办之前还没映射");

        await wm.SendAsync(8, 0, b => b.U32(window));
        await wm.SyncAsync();
        Assert.AreEqual(2, (await app.RequestAsync(3, 0, b => b.U32(window))).Bytes[26]);
        Assert.IsTrue(host.Mapped.IsEmpty, "顶层拼进屏幕,不单独交给宿主");
        Assert.AreEqual(0xFF0000u, Pixel(screen, 20, 20));
    }

    [TestMethod]
    public async Task 屏幕是根窗口的背景加上按堆叠次序拼起来的顶层_边框照画_GetImage读得到_宿主收到屏幕的损伤()
    {
        using RecordingHost host = new();
        await using X11Server server = new(new X11ServerOptions { Rootful = true, ScreenWidth = 200, ScreenHeight = 100 }, host);
        XTopLevelWindow screen = server.Screen!;
        await using XTestClient c = await XTestClient.ConnectAsync(server);

        await c.SendAsync(2, 0, b => b.U32(c.RootWindow).U32(0x2).U32(0x00FF00));   // 根窗口背景:绿(xsetroot -solid)
        uint a = await CreateTopAsync(c, 10, 10, 40, 30, 0xFF0000, border: 2, borderPixel: 0x0000FF);
        uint b2 = await CreateTopAsync(c, 30, 20, 40, 30, 0xFFFF00);
        await c.SendAsync(8, 0, b => b.U32(a));
        await c.SendAsync(8, 0, b => b.U32(b2));
        await c.SyncAsync();
        Assert.AreEqual(0x00FF00u, Pixel(screen, 0, 0), "背景");
        Assert.AreEqual(0x0000FFu, Pixel(screen, 11, 11), "边框");
        Assert.AreEqual(0xFF0000u, Pixel(screen, 15, 15), "A 的内区从 (12, 12) 起");
        Assert.AreEqual(0xFFFF00u, Pixel(screen, 35, 25), "B 在上面");
        await host.WaitForAsync(() => !host.DamageRects.IsEmpty);
        Assert.IsTrue(host.DamageRects.Any(r => r.Contains(35, 25)), "宿主收到屏幕坐标的损伤");

        await c.SendAsync(12, 0, b => b.U32(a).U16(0x40).U16(0).U32(0));   // ConfigureWindow stack-mode Above:把 A 抬上来
        await c.SyncAsync();
        Assert.AreEqual(0xFF0000u, Pixel(screen, 35, 25), "重叠处换成 A");
        Assert.AreEqual(0xFFFF00u, Pixel(screen, 60, 40), "B 没被挡住的部分不变");

        XMessage image = await c.RequestAsync(73, 2, b => b.U32(c.RootWindow).I16(0).I16(0).U16(200).U16(100).U32(0xFFFFFFFF));
        uint At(int x, int y) => BitConverter.ToUInt32(image.Bytes, 32 + (((y * 200) + x) * 4)) & 0xFFFFFF;
        Assert.AreEqual((0x00FF00u, 0xFF0000u, 0xFFFF00u), (At(0, 0), At(35, 25), At(60, 40)), "GetImage 读根窗口拿到的就是屏幕");

        await c.SendAsync(10, 0, b => b.U32(a));   // UnmapWindow
        await c.SyncAsync();
        Assert.AreEqual(0xFFFF00u, Pixel(screen, 35, 25));
        Assert.AreEqual(0x00FF00u, Pixel(screen, 15, 15), "A 拿走之后露出背景");
    }

    [TestMethod]
    public async Task 指针按根坐标注入_光标在屏幕上_缩放屏幕窗口就是改屏幕尺寸_其余窗口管理动作不起作用()
    {
        using RecordingHost host = new();
        await using X11Server server = new(new X11ServerOptions { Rootful = true, ScreenWidth = 200, ScreenHeight = 100 }, host);
        XTopLevelWindow screen = server.Screen!;
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint window = await CreateTopAsync(c, 50, 20, 40, 30, 0x123456, events: 0x4);   // ButtonPress
        await c.SendAsync(8, 0, b => b.U32(window));
        await c.SendAsync(2, 0, b => b.U32(c.RootWindow).U32(0x800).U32(0x20000));   // 根窗口:StructureNotify
        await c.SyncAsync();

        server.InjectPointerMotion(screen, 60, 30);
        server.InjectPointerButton(screen, 60, 30, 1, pressed: true);
        server.InjectPointerButton(screen, 60, 30, 1, pressed: false);
        XMessage press = await c.NextEventAsync(ButtonPress);
        Assert.AreEqual(window, press.U32(12));
        Assert.AreEqual((60, 30, 10, 10), (press.I16(20), press.I16(22), press.I16(24), press.I16(26)));
        await host.WaitForAsync(() => host.CursorWindow is not null);
        Assert.AreSame(screen, host.CursorWindow, "光标报在屏幕句柄上");

        server.MoveTopLevel(screen, 300, 300);
        server.CloseTopLevel(screen);
        server.FocusTopLevel(null);
        server.SetTopLevelStates(screen, XWindowStates.Maximized);
        server.ResizeTopLevel(screen, 300, 150);
        XMessage configure = await c.NextEventAsync(ConfigureNotify);
        Assert.AreEqual((c.RootWindow, 300, 150), (configure.U32(8), configure.U16(20), configure.U16(22)));
        await host.WaitForAsync(() => screen.Snapshot.Width == 300);
        (int width, int height) = (0, 0);
        screen.ReadPixels((_, w, h) => (width, height) = (w, h));
        Assert.AreEqual((300, 150, 0, 0), (width, height, screen.Snapshot.X, screen.Snapshot.Y));
        Assert.AreEqual(0x123456u, Pixel(screen, 60, 30), "整屏重拼过");
        Assert.AreEqual(2, (await c.RequestAsync(3, 0, b => b.U32(window))).Bytes[26], "关闭 / 移动屏幕不动任何 X 窗口");
    }
}
