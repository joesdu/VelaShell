using System.Text;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>
/// 宿主拖进 X 窗口(F16):服务端替宿主扮演 XDND 的源 —— 按 XdndAware 找目标、Enter / Position / Status 的流控、
/// 放下之后目标经 XdndSelection 取数据、XdndFinished 之后数据丢掉;不接受就 XdndLeave;XdndProxy。
/// </summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class XdndTests
{
    private const byte ClientMessage = 33, SelectionNotify = 31;

    private sealed record Atoms(uint Aware, uint Enter, uint Position, uint Status, uint Drop, uint Leave, uint Finished,
        uint Selection, uint Copy, uint UriList, uint Utf8Text, uint Proxy);

    private static async Task<uint> InternAsync(XTestClient c, string name)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(name);
        XMessage m = await c.RequestAsync(16, 0, b => b.U16((ushort)bytes.Length).U16(0).Bytes(bytes).Pad());
        return m.U32(8);
    }

    private static async Task<Atoms> AtomsAsync(XTestClient c) => new(
        await InternAsync(c, "XdndAware"), await InternAsync(c, "XdndEnter"), await InternAsync(c, "XdndPosition"),
        await InternAsync(c, "XdndStatus"), await InternAsync(c, "XdndDrop"), await InternAsync(c, "XdndLeave"),
        await InternAsync(c, "XdndFinished"), await InternAsync(c, "XdndSelection"), await InternAsync(c, "XdndActionCopy"),
        await InternAsync(c, "text/uri-list"), await InternAsync(c, "text/plain;charset=utf-8"), await InternAsync(c, "XdndProxy"));

    /// <summary>一个 60×40 的顶层,在 (0, 0);<paramref name="aware" /> 时声明 XdndAware 第 5 版。</summary>
    private static async Task<(uint Window, XTopLevelWindow Handle)> TopLevelAsync(XTestClient c, RecordingHost host, Atoms atoms, bool aware)
    {
        uint window = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(window).U32(c.RootWindow).I16(0).I16(0).U16(60).U16(40).U16(0).U16(1).U32(0).U32(0));
        if (aware)
        {
            await SetAtomPropertyAsync(c, window, atoms.Aware, 4, 5);
        }
        await c.SendAsync(8, 0, b => b.U32(window));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(window));
        return (window, host.Mapped[window]);
    }

    private static Task<ushort> SetAtomPropertyAsync(XTestClient c, uint window, uint property, uint type, uint value) =>
        c.SendAsync(18, 0, b => b.U32(window).U32(property).U32(type).U8(32).U8(0).U8(0).U8(0).U32(1).U32(value));

    private static Task<XMessage> NextXdndAsync(XTestClient c, uint type, int timeoutMs = 5000) =>
        c.NextAsync(m => !m.IsReply && !m.IsError && m.EventCode == ClientMessage && m.U32(8) == type, timeoutMs);

    /// <summary>目标回给源窗口的 ClientMessage(SendEvent,掩码为空)。</summary>
    private static Task<ushort> ReplyAsync(XTestClient c, uint source, uint type, uint l0, uint l1 = 0, uint l2 = 0, uint l3 = 0, uint l4 = 0) =>
        c.SendAsync(25, 0, b => b.U32(source).U32(0)
            .U8(ClientMessage).U8(32).U16(0).U32(source).U32(type).U32(l0).U32(l1).U32(l2).U32(l3).U32(l4));

    [TestMethod]
    public async Task 拖进声明了XdndAware的窗口_等状态再报位置_接受之后放下_目标取得到数据_Finished之后丢掉()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        Atoms atoms = await AtomsAsync(c);
        (uint window, XTopLevelWindow handle) = await TopLevelAsync(c, host, atoms, aware: true);

        server.InjectDragOver(handle, 5, 5, ["text/uri-list", "text/plain;charset=utf-8"]);
        XMessage enter = await NextXdndAsync(c, atoms.Enter);
        uint source = enter.U32(12);
        Assert.AreEqual(window, enter.U32(4), "事件里的窗口是目标");
        Assert.AreEqual(5u, enter.U32(16) >> 24, "版本取两边的小者");
        Assert.AreEqual(0u, enter.U32(16) & 1, "不到三种类型:不必读 XdndTypeList");
        Assert.AreEqual((atoms.UriList, atoms.Utf8Text, 0u), (enter.U32(20), enter.U32(24), enter.U32(28)));
        XMessage position = await NextXdndAsync(c, atoms.Position);
        Assert.AreEqual((source, 0x0005_0005u, atoms.Copy), (position.U32(12), position.U32(20), position.U32(28)));
        Assert.IsFalse(server.IsDragAccepted, "目标还没表态");

        // 等 XdndStatus 期间又动了:先记下,不再发 —— 状态回来之后补一条最新的位置。
        server.InjectDragOver(handle, 6, 7, ["text/uri-list", "text/plain;charset=utf-8"]);
        await c.SyncAsync();
        await ReplyAsync(c, source, atoms.Status, window, 1, l4: atoms.Copy);
        XMessage next = await NextXdndAsync(c, atoms.Position);
        Assert.AreEqual(0x0006_0007u, next.U32(20), "补发的是最新的位置");
        for (int i = 0; i < 100 && !server.IsDragAccepted; i++)
        {
            await Task.Delay(10);
        }
        Assert.IsTrue(server.IsDragAccepted);
        await ReplyAsync(c, source, atoms.Status, window, 1, l4: atoms.Copy);   // 回补发的那条位置
        await c.SyncAsync();   // 状态先到服务端,再松手

        byte[] uris = Encoding.UTF8.GetBytes("file:///tmp/drop/a.txt\r\n");
        server.InjectDrop(handle, 6, 7, new Dictionary<string, ReadOnlyMemory<byte>>
        {
            ["text/uri-list"] = uris,
            ["text/plain;charset=utf-8"] = Encoding.UTF8.GetBytes("/tmp/drop/a.txt"),
        });
        // 松手时先按最后的位置报一次,等最后一条 XdndStatus,再放下。
        await NextXdndAsync(c, atoms.Position);
        await ReplyAsync(c, source, atoms.Status, window, 1, l4: atoms.Copy);
        XMessage drop = await NextXdndAsync(c, atoms.Drop);
        Assert.AreEqual(source, drop.U32(12));
        uint time = drop.U32(20);

        uint property = await InternAsync(c, "DROPPED");
        await c.SendAsync(24, 0, b => b.U32(window).U32(atoms.Selection).U32(atoms.UriList).U32(property).U32(time));
        XMessage notify = await c.NextEventAsync(SelectionNotify);
        Assert.AreEqual(property, notify.U32(20));
        XMessage value = await c.RequestAsync(20, 1, b => b.U32(window).U32(property).U32(0).U32(0).U32(1000));
        Assert.AreEqual(atoms.UriList, value.U32(8));
        CollectionAssert.AreEqual(uris, value.Bytes.AsSpan(32, (int)value.U32(16)).ToArray());

        // 用完了:XdndFinished 之后数据丢掉,再要就是 None。
        await ReplyAsync(c, source, atoms.Finished, window, 1, l2: atoms.Copy);
        await c.SendAsync(24, 0, b => b.U32(window).U32(atoms.Selection).U32(atoms.UriList).U32(property).U32(time));
        Assert.AreEqual(0u, (await c.NextEventAsync(SelectionNotify)).U32(20));
    }

    [TestMethod]
    public async Task 目标最后说不接受_松手发XdndLeave而不是XdndDrop()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        Atoms atoms = await AtomsAsync(c);
        (uint window, XTopLevelWindow handle) = await TopLevelAsync(c, host, atoms, aware: true);

        server.InjectDragOver(handle, 5, 5, ["text/plain;charset=utf-8"]);
        uint source = (await NextXdndAsync(c, atoms.Enter)).U32(12);
        await NextXdndAsync(c, atoms.Position);
        await ReplyAsync(c, source, atoms.Status, window, 0);   // 不接受
        await c.SyncAsync();

        server.InjectDrop(handle, 5, 5, new Dictionary<string, ReadOnlyMemory<byte>> { ["text/plain;charset=utf-8"] = "x"u8.ToArray() });
        await NextXdndAsync(c, atoms.Position);
        await ReplyAsync(c, source, atoms.Status, window, 0);
        await NextXdndAsync(c, atoms.Leave);
        await c.SyncAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => NextXdndAsync(c, atoms.Drop, timeoutMs: 300));
    }

    [TestMethod]
    public async Task 没声明XdndAware的窗口不发消息_目标设了XdndProxy时消息投给代理()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        await using XTestClient proxyClient = await XTestClient.ConnectAsync(server);
        Atoms atoms = await AtomsAsync(c);
        (uint plain, XTopLevelWindow plainHandle) = await TopLevelAsync(c, host, atoms, aware: false);

        server.InjectDragOver(plainHandle, 5, 5, ["text/plain;charset=utf-8"]);
        await c.SyncAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => NextXdndAsync(c, atoms.Enter, timeoutMs: 300));
        Assert.IsFalse(server.IsDragAccepted);
        server.InjectDragLeave();

        // 目标窗口指向另一个客户端的代理窗口(代理自己的 XdndProxy 指向自己,XdndAware 设在代理上):消息投给代理的客户端,窗口字段仍是目标。
        uint proxy = proxyClient.NewId();
        await proxyClient.SendAsync(1, 0, b => b.U32(proxy).U32(proxyClient.RootWindow).I16(0).I16(0).U16(1).U16(1).U16(0).U16(2).U32(0).U32(0));
        await SetAtomPropertyAsync(proxyClient, proxy, atoms.Proxy, 33, proxy);
        await SetAtomPropertyAsync(proxyClient, proxy, atoms.Aware, 4, 4);
        await proxyClient.SyncAsync();
        await SetAtomPropertyAsync(c, plain, atoms.Proxy, 33, proxy);
        await c.SyncAsync();

        server.InjectDragOver(plainHandle, 5, 5, ["text/plain;charset=utf-8"]);
        XMessage enter = await NextXdndAsync(proxyClient, atoms.Enter);
        Assert.AreEqual(plain, enter.U32(4), "事件里的窗口是指针所在的那个,不是代理");
        Assert.AreEqual(4u, enter.U32(16) >> 24, "版本按代理上声明的");
    }
}
