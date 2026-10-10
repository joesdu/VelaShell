using System.Text;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>
/// 系统托盘(F12):开着 <see cref="X11ServerOptions.SystemTray" /> 时服务端占住 _NET_SYSTEM_TRAY_S0;图标窗口按 XEmbed 嵌进服务端的嵌入窗口,
/// 宿主拿到的是嵌入窗口的句柄(不当普通顶层),读得到像素、点得进去;_XEMBED_INFO 管映射;图标没了宿主收到移除。
/// </summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class SystemTrayTests
{
    private const byte ClientMessage = 33, ButtonPress = 4, MapNotify = 19, UnmapNotify = 18;

    private static async Task<uint> InternAsync(XTestClient c, string name)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(name);
        XMessage m = await c.RequestAsync(16, 0, b => b.U16((ushort)bytes.Length).U16(0).Bytes(bytes).Pad());
        return m.U32(8);
    }

    private static async Task<uint> TrayOwnerAsync(XTestClient c) =>
        (await c.RequestAsync(23, 0, b => b.U32(InternAsync(c, "_NET_SYSTEM_TRAY_S0").Result))).U32(8);

    /// <summary>建一个 10×10 的图标窗口(没映射),选上结构与按钮事件,写 _XEMBED_INFO 与名字,再请求停靠。</summary>
    private static async Task<uint> DockAsync(XTestClient c, uint owner, uint flags, string name)
    {
        uint icon = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(icon).U32(c.RootWindow).I16(0).I16(0).U16(10).U16(10).U16(0).U16(1).U32(0)
            .U32(0x800).U32(0x20004));   // event-mask:StructureNotify | ButtonPress
        uint info = await InternAsync(c, "_XEMBED_INFO");
        await c.SendAsync(18, 0, b => b.U32(icon).U32(info).U32(info).U8(32).U8(0).U8(0).U8(0).U32(2).U32(0).U32(flags));
        uint netName = await InternAsync(c, "_NET_WM_NAME"), utf8 = await InternAsync(c, "UTF8_STRING");
        byte[] title = Encoding.UTF8.GetBytes(name);
        await c.SendAsync(18, 0, b => b.U32(icon).U32(netName).U32(utf8).U8(8).U8(0).U8(0).U8(0).U32((uint)title.Length).Bytes(title).Pad());
        uint opcode = await InternAsync(c, "_NET_SYSTEM_TRAY_OPCODE");
        await c.SendAsync(25, 0, b => b.U32(owner).U32(0)
            .U8(ClientMessage).U8(32).U16(0).U32(icon).U32(opcode).U32(0).U32(0).U32(icon).U32(0).U32(0));   // SYSTEM_TRAY_REQUEST_DOCK
        return icon;
    }

    [TestMethod]
    public async Task 没开托盘时没有托盘管理器_开着时占住选区并写托盘的属性()
    {
        await using (X11Server off = new())
        await using (XTestClient c = await XTestClient.ConnectAsync(off))
        {
            Assert.AreEqual(0u, await TrayOwnerAsync(c), "默认不当托盘:宿主不显示的话,关闭到托盘的程序就找不回来");
        }

        await using X11Server server = new(new X11ServerOptions { SystemTray = true });
        await using XTestClient client = await XTestClient.ConnectAsync(server);
        uint owner = await TrayOwnerAsync(client);
        Assert.AreNotEqual(0u, owner);
        uint orientation = await InternAsync(client, "_NET_SYSTEM_TRAY_ORIENTATION");
        XMessage value = await client.RequestAsync(20, 0, b => b.U32(owner).U32(orientation).U32(0).U32(0).U32(1));
        Assert.AreEqual((6u, 0u), (value.U32(8), value.U32(32)), "CARDINAL,水平");
        uint visual = await InternAsync(client, "_NET_SYSTEM_TRAY_VISUAL");
        Assert.AreEqual(32u, (await client.RequestAsync(20, 0, b => b.U32(owner).U32(visual).U32(0).U32(0).U32(1))).U32(8), "VISUALID");
    }

    [TestMethod]
    public async Task 停靠的图标嵌进嵌入窗口_宿主拿到句柄与名字_像素读得到_点得进去_图标销毁后收掉()
    {
        using RecordingHost host = new();
        await using X11Server server = new(new X11ServerOptions { SystemTray = true, SystemTrayIconSize = 24 }, host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint owner = await TrayOwnerAsync(c);
        uint icon = await DockAsync(c, owner, flags: 1, "备份工具");

        XMessage embedded = await c.NextAsync(m => !m.IsReply && !m.IsError && m.EventCode == ClientMessage);
        uint xembed = await InternAsync(c, "_XEMBED");
        Assert.AreEqual((icon, xembed, 0u), (embedded.U32(4), embedded.U32(8), embedded.U32(16)), "XEMBED_EMBEDDED_NOTIFY");
        uint embedder = embedded.U32(24);
        await c.NextEventAsync(MapNotify);   // XEMBED_MAPPED:嵌入方映射它

        await host.WaitForAsync(() => !host.TrayIcons.IsEmpty);
        (XTopLevelWindow handle, string title) = host.TrayIcons.Single();
        Assert.AreEqual(embedder, handle.Id);
        Assert.AreEqual("备份工具", title);
        Assert.IsFalse(host.Mapped.ContainsKey(embedder), "嵌入窗口不当普通顶层交给宿主");

        XMessage geometry = await c.RequestAsync(14, 0, b => b.U32(icon));
        Assert.AreEqual((0, 0, 24, 24), (geometry.I16(12), geometry.I16(14), geometry.U16(16), geometry.U16(18)), "撑满嵌入窗口");
        XMessage tree = await c.RequestAsync(15, 0, b => b.U32(icon));
        Assert.AreEqual(embedder, tree.U32(12), "父窗口是嵌入窗口");

        // 图标画红;宿主读得到。
        uint gc = c.NewId();
        await c.SendAsync(55, 0, b => b.U32(gc).U32(icon).U32(0x4).U32(0xFF0000));
        await c.SendAsync(70, 0, b => b.U32(icon).U32(gc).I16(0).I16(0).U16(24).U16(24));
        await c.SyncAsync();
        uint center = 0;
        handle.ReadPixels((pixels, width, _) => center = pixels[(12 * width) + 12]);
        Assert.AreEqual(0xFF0000u, center & 0xFFFFFF);

        // 宿主点托盘图标:按下送到图标窗口。
        server.InjectPointerButton(handle, 12, 12, 1, pressed: true);
        server.InjectPointerButton(handle, 12, 12, 1, pressed: false);
        XMessage press = await c.NextEventAsync(ButtonPress);
        Assert.AreEqual(icon, press.U32(12));

        // 程序把 XEMBED_MAPPED 去掉:嵌入方取消映射它。
        uint info = await InternAsync(c, "_XEMBED_INFO");
        await c.SendAsync(18, 0, b => b.U32(icon).U32(info).U32(info).U8(32).U8(0).U8(0).U8(0).U32(2).U32(0).U32(0));
        await c.NextEventAsync(UnmapNotify);

        await c.SendAsync(4, 0, b => b.U32(icon));   // DestroyWindow
        await host.WaitForAsync(() => !host.TrayIconsRemoved.IsEmpty);
        Assert.AreEqual(embedder, host.TrayIconsRemoved.Single().Id);
        Assert.IsTrue(host.TrayIcons.IsEmpty);
        Assert.IsFalse(handle.IsAlive, "嵌入窗口释放了");
    }

    [TestMethod]
    public async Task 程序退出时托盘图标收掉_程序把图标挪出去也收掉()
    {
        using RecordingHost host = new();
        await using X11Server server = new(new X11ServerOptions { SystemTray = true }, host);
        uint owner;
        await using (XTestClient c = await XTestClient.ConnectAsync(server))
        {
            owner = await TrayOwnerAsync(c);
            await DockAsync(c, owner, flags: 1, "a");
            await host.WaitForAsync(() => host.TrayIcons.Count == 1);
        }
        await host.WaitForAsync(() => host.TrayIconsRemoved.Count == 1);

        await using XTestClient second = await XTestClient.ConnectAsync(server);
        uint icon = await DockAsync(second, owner, flags: 1, "b");
        await host.WaitForAsync(() => host.TrayIcons.Count == 1);
        await second.SendAsync(7, 0, b => b.U32(icon).U32(second.RootWindow).I16(5).I16(5));   // ReparentWindow 回根窗口
        await host.WaitForAsync(() => host.TrayIconsRemoved.Count == 2);
        XMessage tree = await second.RequestAsync(15, 0, b => b.U32(icon));
        Assert.AreEqual(second.RootWindow, tree.U32(12), "图标照程序的意思留在根窗口下");
    }
}
