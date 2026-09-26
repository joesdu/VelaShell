using System.Text;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>与宿主的剪贴板互通:宿主 → X(服务端当属主)与 X → 宿主(服务端当请求方,含 INCR)。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class ClipboardTests
{
    private const byte SelectionRequest = 30, SelectionNotify = 31, PropertyNotify = 28, SelectionClear = 29;

    private static async Task<uint> InternAsync(XTestClient c, string name)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(name);
        XMessage m = await c.RequestAsync(16, 0, b => b.U16((ushort)bytes.Length).U16(0).Bytes(bytes).Pad());
        return m.U32(8);
    }

    private static async Task<uint> CreateWindowAsync(XTestClient c)
    {
        uint id = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(id).U32(c.RootWindow).I16(0).I16(0).U16(1).U16(1).U16(0).U16(2).U32(0).U32(0));
        return id;
    }

    private static Task<ushort> ChangePropertyAsync(XTestClient c, uint window, uint property, uint type, byte[] data) =>
        c.SendAsync(18, 0, b => b.U32(window).U32(property).U32(type).U8(8).U8(0).U8(0).U8(0).U32((uint)data.Length).Bytes(data).Pad());

    private static Task<ushort> SendSelectionNotifyAsync(XTestClient c, XMessage request, uint property) =>
        c.SendAsync(25, 0, b => b.U32(request.U32(12)).U32(0)
            .U8(SelectionNotify).U8(0).U16(0).U32(request.U32(4)).U32(request.U32(12)).U32(request.U32(16))
            .U32(request.U32(20)).U32(property).U32(0).U32(0));

    [TestMethod]
    public async Task 宿主的文本X客户端能以UTF8与TARGETS取到()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint clipboard = await InternAsync(c, "CLIPBOARD");
        uint utf8 = await InternAsync(c, "UTF8_STRING");
        uint targets = await InternAsync(c, "TARGETS");
        uint prop = await InternAsync(c, "MY_PROP");
        uint window = await CreateWindowAsync(c);

        server.SetClipboardText("héllo 世界");
        XMessage owner;
        do
        {
            owner = await c.RequestAsync(23, 0, b => b.U32(clipboard));
        }
        while (owner.U32(8) == 0);

        await c.SendAsync(24, 0, b => b.U32(window).U32(clipboard).U32(utf8).U32(prop).U32(0));
        XMessage notify = await c.NextEventAsync(SelectionNotify);
        Assert.AreEqual(prop, notify.U32(20));
        XMessage value = await c.RequestAsync(20, 1, b => b.U32(window).U32(prop).U32(0).U32(0).U32(1000));
        Assert.AreEqual(utf8, value.U32(8));
        Assert.AreEqual("héllo 世界", Encoding.UTF8.GetString(value.Bytes, 32, (int)value.U32(16)));

        await c.SendAsync(24, 0, b => b.U32(window).U32(clipboard).U32(targets).U32(prop).U32(0));
        await c.NextEventAsync(SelectionNotify);
        XMessage list = await c.RequestAsync(20, 1, b => b.U32(window).U32(prop).U32(0).U32(0).U32(1000));
        uint[] atoms = [.. Enumerable.Range(0, (int)list.U32(16)).Select(i => list.U32(32 + (i * 4)))];
        CollectionAssert.Contains(atoms, utf8);
        CollectionAssert.Contains(atoms, 31u, "STRING");
    }

    [TestMethod]
    public async Task X客户端复制的文本交给宿主_写回来不抢选区()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint clipboard = await InternAsync(c, "CLIPBOARD");
        uint utf8 = await InternAsync(c, "UTF8_STRING");
        uint window = await CreateWindowAsync(c);

        await c.SendAsync(22, 0, b => b.U32(window).U32(clipboard).U32(0));   // SetSelectionOwner
        XMessage request = await c.NextEventAsync(SelectionRequest);
        Assert.AreEqual(window, request.U32(8), "owner");
        Assert.AreEqual(utf8, request.U32(20), "先要 UTF8_STRING");

        uint property = request.U32(24);
        await ChangePropertyAsync(c, request.U32(12), property, utf8, Encoding.UTF8.GetBytes("来自 X"));
        await SendSelectionNotifyAsync(c, request, property);
        await host.WaitForAsync(() => host.Clipboard == "来自 X");

        server.SetClipboardText("来自 X");   // 宿主把同一段文本写回来
        XMessage owner = await c.RequestAsync(23, 0, b => b.U32(clipboard));
        Assert.AreEqual(window, owner.U32(8), "不应抢走 X 客户端的选区");

        server.SetClipboardText("宿主的新文本");
        XMessage clear = await c.NextEventAsync(SelectionClear);
        Assert.AreEqual(window, clear.U32(8));
    }

    [TestMethod]
    public async Task 大文本走INCR分块()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint clipboard = await InternAsync(c, "CLIPBOARD");
        uint utf8 = await InternAsync(c, "UTF8_STRING");
        uint incr = await InternAsync(c, "INCR");
        uint window = await CreateWindowAsync(c);

        await c.SendAsync(22, 0, b => b.U32(window).U32(clipboard).U32(0));
        XMessage request = await c.NextEventAsync(SelectionRequest);
        uint requestor = request.U32(12), property = request.U32(24);

        await c.SendAsync(2, 0, b => b.U32(requestor).U32(0x800).U32(0x400000));   // PropertyChangeMask
        await c.SendAsync(18, 0, b => b.U32(requestor).U32(property).U32(incr).U8(32).U8(0).U8(0).U8(0).U32(1).U32(9));
        await SendSelectionNotifyAsync(c, request, property);

        string[] chunks = ["abc", "defg", "hi", ""];
        foreach (string chunk in chunks)
        {
            XMessage deleted = await c.NextAsync(m => !m.IsReply && !m.IsError && m.EventCode == PropertyNotify && m.Bytes[16] == 1);
            Assert.AreEqual(property, deleted.U32(8));
            await ChangePropertyAsync(c, requestor, property, utf8, Encoding.UTF8.GetBytes(chunk));
        }
        await host.WaitForAsync(() => host.Clipboard == "abcdefghi");
    }

    [TestMethod]
    public async Task 服务端当XSETTINGS管理器发布DPI_且管理器选区转换不出剪贴板内容()
    {
        await using X11Server server = new(new X11ServerOptions { Dpi = 144 });
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint selection = await InternAsync(c, "_XSETTINGS_S0");
        uint settings = await InternAsync(c, "_XSETTINGS_SETTINGS");
        XMessage owner = await c.RequestAsync(23, 0, b => b.U32(selection));
        uint manager = owner.U32(8);
        Assert.AreNotEqual(0u, manager);

        XMessage prop = await c.RequestAsync(20, 0, b => b.U32(manager).U32(settings).U32(0).U32(0).U32(1000));
        Assert.AreEqual(settings, prop.U32(8), "类型也是 _XSETTINGS_SETTINGS");
        byte[] data = prop.Bytes[32..(32 + (int)prop.U32(16))];
        string text = Encoding.ASCII.GetString(data);
        int at = text.IndexOf("Xft/DPI", StringComparison.Ordinal);
        Assert.IsTrue(at > 0);
        // 名字(7 字节补到 8)之后是 last-change-serial,再之后才是值。
        Assert.AreEqual(144 * 1024, BitConverter.ToInt32(data, at + 8 + 4));

        server.SetClipboardText("secret");
        uint window = await CreateWindowAsync(c);
        uint utf8 = await InternAsync(c, "UTF8_STRING");
        await c.SendAsync(24, 0, b => b.U32(window).U32(selection).U32(utf8).U32(utf8).U32(0));
        XMessage notify = await c.NextEventAsync(SelectionNotify);
        Assert.AreEqual(0u, notify.U32(20), "管理器选区不给剪贴板内容");
    }

    [TestMethod]
    public async Task 关掉互通后不取也不占()
    {
        using RecordingHost host = new();
        await using X11Server server = new(new X11ServerOptions { SyncClipboard = false }, host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint clipboard = await InternAsync(c, "CLIPBOARD");
        uint window = await CreateWindowAsync(c);

        server.SetClipboardText("x");
        await c.SendAsync(22, 0, b => b.U32(window).U32(clipboard).U32(0));
        await c.SyncAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => c.NextEventAsync(SelectionRequest, timeoutMs: 200));
        Assert.IsNull(host.Clipboard);
    }
}
