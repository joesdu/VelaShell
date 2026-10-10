using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using VelaShell.Core.XServer;
using VelaShell.Services.XServer;
using VelaShell.Ssh.Transport;
using VelaShell.Tests.TestSupport;
using VelaShell.Views.XServer;
using VelaShell.XServer;

namespace VelaShell.Tests.XServer;

/// <summary>
/// 单窗口(rootful)模式(F13)的宿主一侧:附着时就开一个屏幕窗口(标题「X 桌面 :N」),X 程序的顶层拼在里面、不另开原生窗口;
/// 点击按屏幕坐标落到 X 窗口上;拖大屏幕窗口就是改屏幕尺寸;点关闭不直接关掉它(当作停 X Server,由主窗口确认)。
/// </summary>
[TestClass]
[TestCategory("XServer")]
public sealed class XScreenWindowUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(XScreenWindowUiTests).Assembly);

    [TestMethod]
    public async Task RootfulServer_ShowsOneScreenWindow_ThatComposesXWindows_TakesClicks_AndResizesTheScreen() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new();
        host.UseWindowMode(XServerWindowModes.Windowed);
        await using X11Server server = new(new X11ServerOptions
        {
            ListenTcp = false, UnixSocketPath = "", Rootful = true, ScreenWidth = 400, ScreenHeight = 300,
        }, host);
        await host.AttachAsync(server, CancellationToken.None);
        XNativeWindow screen = await XTestWire.WaitForAsync(() => host.Windows.SingleOrDefault());
        Assert.IsTrue(screen.IsScreen);
        Assert.AreSame(server.Screen, screen.Handle);
        StringAssert.Contains(screen.Title, ":" + server.DisplayNumber);
        Assert.AreEqual(WindowDecorations.Full, screen.WindowDecorations);

        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        Task serve = server.ServeAsync(serverSide, isLocal: true);
        XTestWire wire = await XTestWire.ConnectAsync(client);
        uint window = wire.IdBase | 1;
        // 顶层 (40, 30) 80×60,背景红,选 ButtonPress。
        await wire.SendAsync(1, 24, b => b.U32(window).U32(wire.Root).I16(40).I16(30).U16(80).U16(60).U16(0).U16(1).U32(0)
            .U32(0x2 | 0x800).U32(0xFF0000).U32(0x4));
        await wire.SendAsync(8, 0, b => b.U32(window));
        await wire.SyncAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.HasCount(1, host.Windows, "X 窗口拼在屏幕窗口里,不另开原生窗口");

        double scale = screen.RenderScaling;
        screen.MouseDown(new Point(50 / scale, 40 / scale), MouseButton.Left);
        screen.MouseUp(new Point(50 / scale, 40 / scale), MouseButton.Left);
        byte[] press = await XTestWire.WaitForAsync(() =>
        {
            while (wire.Events.TryDequeue(out byte[]? e))
            {
                if ((e[0] & 0x7F) == 4)
                {
                    return e;
                }
            }
            return null;
        });
        Assert.AreEqual(window, XTestWire.U32(press, 12));
        Assert.AreEqual((10, 10), (BitConverter.ToInt16(press, 24), BitConverter.ToInt16(press, 26)), "屏幕 (50, 40) 落在窗口的 (10, 10)");

        screen.Width = 500 / scale;
        screen.Height = 350 / scale;
        Dispatcher.UIThread.RunJobs();
        await XTestWire.WaitForAsync(() => server.Screen!.Snapshot.Width == 500 ? screen : null);
        Assert.AreEqual(350, server.Screen!.Snapshot.Height, "拖大屏幕窗口就是改屏幕尺寸");

        screen.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.IsTrue(screen.IsVisible, "关闭按钮不直接关掉 X 桌面(当作停 X Server,由主窗口确认)");

        host.Detach();
        Dispatcher.UIThread.RunJobs();
        Assert.IsEmpty(host.Windows);
        client.Dispose();
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
    });

    /// <summary>
    /// 按会话分出来的显示(F1)关掉屏幕窗口:不停整个 X Server,而是请求只收掉这个会话的显示(SessionDisplayCloseRequested);
    /// 没有程序连着时直接请求,有程序连着时先确认(带会话的来历与程序数),用户取消就什么都不做。
    /// </summary>
    [TestMethod]
    public async Task SessionScreenWindow_Close_AsksToCloseOnlyThatSessionsDisplay_ConfirmingWhenProgramsAreConnected() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new();
        host.UseWindowMode(XServerWindowModes.Windowed);
        host.UseSessionLabel("alice@a:22");
        int requested = 0;
        host.SessionDisplayCloseRequested += (_, _) => requested++;
        List<(string Label, int Clients)> asked = [];
        bool answer = false;
        host.ConfirmCloseSessionDisplay = (_, label, clients) =>
        {
            asked.Add((label, clients));
            return Task.FromResult(answer);
        };
        await using X11Server server = new(new X11ServerOptions
        {
            ListenTcp = false, UnixSocketPath = "", Rootful = true, ScreenWidth = 400, ScreenHeight = 300,
        }, host);
        await host.AttachAsync(server, CancellationToken.None);
        XNativeWindow screen = await XTestWire.WaitForAsync(() => host.Windows.SingleOrDefault());
        StringAssert.Contains(screen.Title, "alice@a:22", "屏幕窗口的标题写会话的来历");

        screen.Close();
        await XTestWire.WaitForAsync(() => requested == 1 ? screen : null);
        Assert.IsEmpty(asked, "没有程序连着:不问");
        Assert.IsTrue(screen.IsVisible, "窗口由 BuiltInLocalXServer 收掉这个显示时关,这里不自己关");

        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        Task serve = server.ServeAsync(serverSide, isLocal: true);
        XTestWire wire = await XTestWire.ConnectAsync(client);
        await wire.SyncAsync();

        screen.Close();
        await XTestWire.WaitForAsync(() => asked.Count == 1 ? screen : null);
        Assert.AreEqual(("alice@a:22", 1), asked[0]);
        Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(1, requested, "用户取消:不收");

        answer = true;
        screen.Close();
        await XTestWire.WaitForAsync(() => requested == 2 ? screen : null);
        Assert.HasCount(2, asked);

        host.Detach();
        Dispatcher.UIThread.RunJobs();
        client.Dispose();
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
    });
}
