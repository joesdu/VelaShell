using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using VelaShell.Services.XServer;
using VelaShell.Ssh.Transport;
using VelaShell.Tests.TestSupport;
using VelaShell.XServer;

namespace VelaShell.Tests.XServer;

/// <summary>
/// X 程序的托盘图标(F12)画成宿主的托盘图标:停靠进来时应用的托盘图标集合里多一个(悬停提示是图标的名字),
/// 原有的托盘图标(VelaShell 自己的)不动;图标窗口没了就收掉。
/// </summary>
[TestClass]
[TestCategory("XServer")]
public sealed class XTrayIconsUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(XTrayIconsUiTests).Assembly);

    [TestMethod]
    public async Task DockedIcon_BecomesAnAppTrayIcon_NextToExistingOnes_AndGoesAwayWithTheWindow() => await _session.RunOnUiAsync(async () =>
    {
        Application app = Application.Current!;
        TrayIcon own = new() { ToolTipText = "VelaShell" };
        TrayIcon.SetIcons(app, [own]);
        try
        {
            AvaloniaXServerHost host = new();
            await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "", SystemTray = true }, host);
            await host.AttachAsync(server, CancellationToken.None);
            (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
            Task serve = server.ServeAsync(serverSide, isLocal: true);
            XTestWire wire = await XTestWire.ConnectAsync(client);

            uint trayAtom = await wire.InternAsync("_NET_SYSTEM_TRAY_S0");
            uint owner = XTestWire.U32(await wire.RequestAsync(23, 0, b => b.U32(trayAtom)), 8);
            uint icon = wire.IdBase | 1;
            await wire.SendAsync(1, 24, b => b.U32(icon).U32(wire.Root).I16(0).I16(0).U16(10).U16(10).U16(0).U16(1).U32(0).U32(0));
            uint netName = await wire.InternAsync("_NET_WM_NAME"), utf8 = await wire.InternAsync("UTF8_STRING");
            byte[] title = "备份工具"u8.ToArray();
            await wire.SendAsync(18, 0, b => b.U32(icon).U32(netName).U32(utf8).U8(8).Zero(3).U32((uint)title.Length).Bytes(title).Pad());
            uint opcode = await wire.InternAsync("_NET_SYSTEM_TRAY_OPCODE");
            await wire.SendAsync(25, 0, b => b.U32(owner).U32(0)
                .U8(33).U8(32).U16(0).U32(icon).U32(opcode).U32(0).U32(0).U32(icon).U32(0).U32(0));

            await XTestWire.WaitForAsync(() => host.TrayIconCount == 1 ? host : null);
            TrayIcons icons = TrayIcon.GetIcons(app)!;
            Assert.HasCount(2, icons);
            Assert.AreSame(own, icons[0], "VelaShell 自己的托盘图标还在");
            Assert.AreEqual("备份工具", icons[1].ToolTipText);
            Assert.IsNotNull(icons[1].Menu, "菜单:单击 / 右键菜单");

            await wire.SendAsync(4, 0, b => b.U32(icon));   // DestroyWindow
            await XTestWire.WaitForAsync(() => host.TrayIconCount == 0 ? host : null);
            Assert.AreSame(own, TrayIcon.GetIcons(app)!.Single(), "只收掉 X 程序的那个");

            host.Detach();
            client.Dispose();
            await serve.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            TrayIcon.SetIcons(app, []);
            own.Dispose();
        }
    });
}
