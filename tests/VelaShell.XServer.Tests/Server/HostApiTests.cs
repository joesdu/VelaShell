using System.Text;
using VelaShell.XServer.Server;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>宿主在运行中改的东西:DPI / 缩放、键盘布局。</summary>
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

    [TestMethod]
    public async Task SetDisplayScale更新Xft_dpi与XSETTINGS的缩放()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        StringAssert.Contains(await RootStringAsync(c, "RESOURCE_MANAGER"), "Xft.dpi:\t96");
        await c.SendAsync(2, 0, b => b.U32(c.RootWindow).U32(0x800).U32(0x400000));   // PropertyChange
        await c.SyncAsync();

        server.SetDisplayScale(192, 2);
        XMessage notify = await c.NextEventAsync(28);
        Assert.AreEqual(c.RootWindow, notify.U32(4));
        StringAssert.Contains(await RootStringAsync(c, "RESOURCE_MANAGER"), "Xft.dpi:\t192");

        uint selection = await InternAsync(c, "_XSETTINGS_S0");
        uint settings = await InternAsync(c, "_XSETTINGS_SETTINGS");
        uint manager = (await c.RequestAsync(23, 0, b => b.U32(selection))).U32(8);
        XMessage prop = await c.RequestAsync(20, 0, b => b.U32(manager).U32(settings).U32(0).U32(0).U32(1000));
        byte[] data = prop.Bytes[32..(32 + (int)prop.U32(16))];
        int at = Encoding.ASCII.GetString(data).IndexOf("Gdk/WindowScalingFactor", StringComparison.Ordinal);
        Assert.IsTrue(at > 0);
        Assert.AreEqual(2, BitConverter.ToInt32(data, at + 24 + 4), "名字 23 字节补到 24,再跳过 last-change-serial");
    }

    [TestMethod]
    public async Task SetKeyboardMapping发MappingNotify_核心与布局名都跟着变()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        await c.SyncAsync();
        // 德语布局的一角:键码 29 是 z / Z(QWERTZ)。
        server.SetKeyboardMapping(29, 2, ['z', 'Z'], layout: "de");
        XMessage mapping = await c.NextEventAsync(34);
        Assert.AreEqual(1, mapping.Bytes[4], "request = Keyboard");
        Assert.AreEqual(29, mapping.Bytes[5]);

        XMessage keys = await c.RequestAsync(101, 0, b => b.U8(29).U8(1).U16(0));   // GetKeyboardMapping
        Assert.AreEqual('z', keys.U32(32));
        StringAssert.Contains(await RootStringAsync(c, "_XKB_RULES_NAMES"), "\0de\0");
    }
}
