using System.Buffers.Binary;
using System.Text;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>
/// X 程序往本机拖出来(F16 的另一半):根窗口的 XdndProxy 指向服务端的代理窗口(XDND 第 4 版起「拖到根窗口」的约定),X 程序当源、
/// 服务端当目标 —— 接受(只给复制)、用 XdndPosition 的时间戳取 XdndSelection 的数据交给宿主、按宿主交回的结果回 XdndFinished。
/// 测试客户端逐字节扮演 XDND 的源。
/// </summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class OutgoingDragTests
{
    private const byte ClientMessage = 33, SelectionRequest = 30, SelectionNotify = 31;

    private sealed record Atoms(uint Proxy, uint Aware, uint Enter, uint Position, uint Status, uint Drop, uint Leave, uint Finished,
        uint Selection, uint Copy, uint Move, uint UriList, uint Utf8, uint TypeList, uint Png);

    private static async Task<uint> InternAsync(XTestClient c, string name)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(name);
        XMessage m = await c.RequestAsync(16, 0, b => b.U16((ushort)bytes.Length).U16(0).Bytes(bytes).Pad());
        return m.U32(8);
    }

    private static async Task<Atoms> AtomsAsync(XTestClient c) => new(
        await InternAsync(c, "XdndProxy"), await InternAsync(c, "XdndAware"), await InternAsync(c, "XdndEnter"),
        await InternAsync(c, "XdndPosition"), await InternAsync(c, "XdndStatus"), await InternAsync(c, "XdndDrop"),
        await InternAsync(c, "XdndLeave"), await InternAsync(c, "XdndFinished"), await InternAsync(c, "XdndSelection"),
        await InternAsync(c, "XdndActionCopy"), await InternAsync(c, "XdndActionMove"), await InternAsync(c, "text/uri-list"),
        await InternAsync(c, "UTF8_STRING"), await InternAsync(c, "XdndTypeList"), await InternAsync(c, "image/png"));

    private static async Task<uint?> WindowPropertyAsync(XTestClient c, uint window, uint property)
    {
        XMessage m = await c.RequestAsync(20, 0, b => b.U32(window).U32(property).U32(0).U32(0).U32(1));
        return m.U32(16) == 0 ? null : m.U32(32);
    }

    /// <summary>源:一个 InputOnly 窗口,占住 XdndSelection(拖开始时源都这么做)。</summary>
    private static async Task<uint> SourceAsync(XTestClient c, Atoms atoms)
    {
        uint source = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(source).U32(c.RootWindow).I16(0).I16(0).U16(1).U16(1).U16(0).U16(2).U32(0).U32(0));
        await c.SendAsync(22, 0, b => b.U32(source).U32(atoms.Selection).U32(0));
        return source;
    }

    /// <summary>源发给目标的 XDND 消息:投给代理(根窗口的 XdndProxy),事件里的窗口是指针所在的根窗口。</summary>
    private static Task<ushort> SendXdndAsync(XTestClient c, uint proxy, uint type, uint l0, uint l1 = 0, uint l2 = 0, uint l3 = 0, uint l4 = 0) =>
        c.SendAsync(25, 0, b => b.U32(proxy).U32(0).U8(ClientMessage).U8(32).U16(0).U32(c.RootWindow).U32(type).U32(l0).U32(l1).U32(l2).U32(l3).U32(l4));

    private static Task<XMessage> NextXdndAsync(XTestClient c, uint type, int timeoutMs = 5000) =>
        c.NextAsync(m => !m.IsReply && !m.IsError && m.EventCode == ClientMessage && m.U32(8) == type, timeoutMs);

    /// <summary>源回答服务端的 SelectionRequest:把数据写到请求方的属性上,再发 SelectionNotify。</summary>
    private static async Task AnswerAsync(XTestClient c, XMessage request, uint type, byte[] data)
    {
        uint time = request.U32(4), requestor = request.U32(12), selection = request.U32(16), target = request.U32(20), property = request.U32(24);
        await c.SendAsync(18, 0, b => b.U32(requestor).U32(property).U32(type).U8(8).U8(0).U8(0).U8(0).U32((uint)data.Length).Bytes(data));
        await c.SendAsync(25, 0, b => b.U32(requestor).U32(0).U8(SelectionNotify).U8(0).U16(0)
            .U32(time).U32(requestor).U32(selection).U32(target).U32(property).U32(0).U32(0));
    }

    /// <summary>开着拖出的服务端、源客户端(带连接名)、代理窗口、源窗口;源已进入并报了一次位置,服务端回了接受、开始取数据。</summary>
    private static async Task<(XTestClient Source, Atoms Atoms, uint Proxy, uint Window)> EnterAsync(X11Server server, uint[] types,
        string label = "user@host:22")
    {
        XTestClient c = await XTestClient.ConnectAsync(server, label: label);
        Atoms atoms = await AtomsAsync(c);
        uint proxy = await WindowPropertyAsync(c, c.RootWindow, atoms.Proxy) ?? throw new AssertFailedException("根窗口没有 XdndProxy");
        uint window = await SourceAsync(c, atoms);
        uint flags = (5u << 24) | (types.Length > 3 ? 1u : 0u);
        if (types.Length > 3)
        {
            byte[] list = new byte[types.Length * 4];
            for (int i = 0; i < types.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(list.AsSpan(i * 4), types[i]);
            }
            await c.SendAsync(18, 0, b => b.U32(window).U32(atoms.TypeList).U32(4).U8(32).U8(0).U8(0).U8(0).U32((uint)types.Length).Bytes(list));
        }
        await SendXdndAsync(c, proxy, atoms.Enter, window, flags, types.ElementAtOrDefault(0), types.ElementAtOrDefault(1), types.ElementAtOrDefault(2));
        await SendXdndAsync(c, proxy, atoms.Position, window, 0, (100u << 16) | 200, 1234, atoms.Move);
        return (c, atoms, proxy, window);
    }

    [TestMethod]
    public async Task 根窗口的XdndProxy指向代理_代理指向自己并声明XdndAware_没开时与单窗口模式下都没有()
    {
        await using X11Server server = new(new X11ServerOptions { AcceptOutgoingDrags = true });
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        Atoms atoms = await AtomsAsync(c);
        uint proxy = (await WindowPropertyAsync(c, c.RootWindow, atoms.Proxy))!.Value;
        Assert.AreEqual(proxy, await WindowPropertyAsync(c, proxy, atoms.Proxy), "代理窗口的 XdndProxy 指向它自己");
        Assert.AreEqual(5u, await WindowPropertyAsync(c, proxy, atoms.Aware), "XdndAware 第 5 版设在代理上");
        Assert.IsNull(await WindowPropertyAsync(c, c.RootWindow, atoms.Aware), "根窗口本身不声明 XdndAware");

        foreach (X11ServerOptions options in new[] { new X11ServerOptions(), new X11ServerOptions { AcceptOutgoingDrags = true, Rootful = true } })
        {
            await using X11Server other = new(options);
            await using XTestClient oc = await XTestClient.ConnectAsync(other);
            Assert.IsNull(await WindowPropertyAsync(oc, oc.RootWindow, await InternAsync(oc, "XdndProxy")));
        }
    }

    [TestMethod]
    public async Task 拖到X窗口以外_接受并只给复制_取来URI与文字交给宿主_宿主说放下了就回Finished成功()
    {
        using RecordingHost host = new();
        await using X11Server server = new(new X11ServerOptions { AcceptOutgoingDrags = true }, host);
        await using XTestClient probe = await XTestClient.ConnectAsync(server);
        Atoms a = await AtomsAsync(probe);
        (XTestClient c, Atoms atoms, uint proxy, uint window) = await EnterAsync(server, [a.UriList, a.Utf8, a.Png]);
        await using XTestClient _ = c;

        // 服务端用 XdndPosition 的时间戳要数据:先 URI 列表,再文字。
        XMessage request = await c.NextEventAsync(SelectionRequest);
        Assert.AreEqual((1234u, window, atoms.Selection, atoms.UriList), (request.U32(4), request.U32(8), request.U32(16), request.U32(20)));
        XMessage status = await NextXdndAsync(c, atoms.Status);
        Assert.AreEqual(window, status.U32(4), "投给源窗口");
        Assert.AreEqual((c.RootWindow, 1u, atoms.Copy), (status.U32(12), status.U32(16) & 1, status.U32(28)),
            "l0 是根窗口(指针所在的那个)、接受、动作只给复制(源要的是移动)");

        await AnswerAsync(c, request, atoms.UriList, Encoding.UTF8.GetBytes("file:///home/u/a.txt\r\n# 注释\r\n\r\nfile:///home/u/b%20c.txt\r\n"));
        request = await c.NextEventAsync(SelectionRequest);
        Assert.AreEqual(atoms.Utf8, request.U32(20));
        await AnswerAsync(c, request, atoms.Utf8, Encoding.UTF8.GetBytes("中文"));

        await host.WaitForAsync(() => !host.OutgoingDrags.IsEmpty);
        Assert.IsTrue(host.OutgoingDrags.TryPeek(out XOutgoingDrag? drag));
        CollectionAssert.AreEqual(new[] { "file:///home/u/a.txt", "file:///home/u/b%20c.txt" }, drag.Uris.ToArray());
        Assert.AreEqual("中文", drag.Text);
        Assert.AreEqual("user@host:22", drag.ClientLabel);
        Assert.AreEqual((100, 200), (drag.RootX, drag.RootY));

        // 指针还在动:照常回接受。宿主的本机拖放放下了 → 宿主先交回结果,再注入松开的按钮 → 源发 XdndDrop。
        await SendXdndAsync(c, proxy, atoms.Position, window, 0, (101u << 16) | 200, 1240, atoms.Copy);
        Assert.AreEqual(1u, (await NextXdndAsync(c, atoms.Status)).U32(16) & 1);
        server.CompleteOutgoingDrag(drag, dropped: true);
        await SendXdndAsync(c, proxy, atoms.Drop, window, 0, 1300);
        XMessage finished = await NextXdndAsync(c, atoms.Finished);
        Assert.AreEqual((c.RootWindow, 1u, atoms.Copy), (finished.U32(12), finished.U32(16), finished.U32(20)), "接受了、做的是复制");
        await c.SyncAsync();
        Assert.IsTrue(host.OutgoingDragsEnded.IsEmpty, "宿主自己交回了结果:不再报结束");
    }

    [TestMethod]
    public async Task 宿主还没交回结果就离开或松手_报宿主结束_松手回Finished失败()
    {
        using RecordingHost host = new();
        await using X11Server server = new(new X11ServerOptions { AcceptOutgoingDrags = true }, host);
        await using XTestClient probe = await XTestClient.ConnectAsync(server);
        Atoms a = await AtomsAsync(probe);

        // 一:指针回到 X 窗口里,源发 XdndLeave。
        (XTestClient c, Atoms atoms, uint proxy, uint window) = await EnterAsync(server, [a.Utf8]);
        await using (c)
        {
            await AnswerAsync(c, await c.NextEventAsync(SelectionRequest), atoms.Utf8, "hello"u8.ToArray());
            await host.WaitForAsync(() => host.OutgoingDrags.Count == 1);
            await SendXdndAsync(c, proxy, atoms.Leave, window);
            await host.WaitForAsync(() => host.OutgoingDragsEnded.Count == 1);

            // 二:本机拖放还没开始用户就松了手(源发 XdndDrop):回「没接受」,报宿主结束。
            await SendXdndAsync(c, proxy, atoms.Enter, window, 5u << 24, atoms.Utf8);
            await SendXdndAsync(c, proxy, atoms.Position, window, 0, (5u << 16) | 5, 2000, atoms.Copy);
            await AnswerAsync(c, await c.NextEventAsync(SelectionRequest), atoms.Utf8, "again"u8.ToArray());
            await host.WaitForAsync(() => host.OutgoingDrags.Count == 2);
            await SendXdndAsync(c, proxy, atoms.Drop, window, 0, 2100);
            XMessage finished = await NextXdndAsync(c, atoms.Finished);
            Assert.AreEqual((0u, 0u), (finished.U32(16), finished.U32(20)), "没接受、动作 None");
            await host.WaitForAsync(() => host.OutgoingDragsEnded.Count == 2);
        }
    }

    [TestMethod]
    public async Task 宿主说取消了_之后的位置不接受_松手回Finished失败_旧的结果不影响新的一次()
    {
        using RecordingHost host = new();
        await using X11Server server = new(new X11ServerOptions { AcceptOutgoingDrags = true }, host);
        await using XTestClient probe = await XTestClient.ConnectAsync(server);
        Atoms a = await AtomsAsync(probe);
        (XTestClient c, Atoms atoms, uint proxy, uint window) = await EnterAsync(server, [a.UriList]);
        await using XTestClient _ = c;
        await AnswerAsync(c, await c.NextEventAsync(SelectionRequest), atoms.UriList, "file:///tmp/x\r\n"u8.ToArray());
        await host.WaitForAsync(() => host.OutgoingDrags.Count == 1);
        host.OutgoingDrags.TryPeek(out XOutgoingDrag? first);

        server.CompleteOutgoingDrag(first!, dropped: false);
        await NextXdndAsync(c, atoms.Status);   // 第一条位置的回答
        await SendXdndAsync(c, proxy, atoms.Position, window, 0, (7u << 16) | 7, 3000, atoms.Copy);
        Assert.AreEqual(0u, (await NextXdndAsync(c, atoms.Status)).U32(16) & 1, "宿主取消了:不再接受");
        await SendXdndAsync(c, proxy, atoms.Drop, window, 0, 3100);
        Assert.AreEqual(0u, (await NextXdndAsync(c, atoms.Finished)).U32(16));
        await c.SyncAsync();
        Assert.IsTrue(host.OutgoingDragsEnded.IsEmpty, "宿主交回过结果:不报结束");

        // 新的一次:旧对象交回的结果不算数。
        await SendXdndAsync(c, proxy, atoms.Enter, window, 5u << 24, atoms.UriList);
        await SendXdndAsync(c, proxy, atoms.Position, window, 0, (8u << 16) | 8, 4000, atoms.Copy);
        await AnswerAsync(c, await c.NextEventAsync(SelectionRequest), atoms.UriList, "file:///tmp/y\r\n"u8.ToArray());
        await host.WaitForAsync(() => host.OutgoingDrags.Count == 2);
        server.CompleteOutgoingDrag(first!, dropped: true);
        await SendXdndAsync(c, proxy, atoms.Drop, window, 0, 4100);
        XMessage finished = await NextXdndAsync(c, atoms.Finished);
        Assert.AreEqual(0u, finished.U32(16), "旧的那次的结果不算到新的一次头上");
        await host.WaitForAsync(() => host.OutgoingDragsEnded.Count == 1);
    }

    /// <summary>根窗口 (x, y) 处的子窗口(TranslateCoordinates 根 → 根 的 child)。</summary>
    private static async Task<uint> ChildAtAsync(XTestClient c, short x, short y) =>
        (await c.RequestAsync(40, 0, b => b.U32(c.RootWindow).U32(c.RootWindow).I16(x).I16(y))).U32(8);

    [TestMethod]
    public async Task X程序占了XdndSelection_空白处垫上接手窗口_拖动结束之后撤掉()
    {
        await using X11Server server = new(new X11ServerOptions { AcceptOutgoingDrags = true });
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        Atoms atoms = await AtomsAsync(c);
        uint proxy = (await WindowPropertyAsync(c, c.RootWindow, atoms.Proxy))!.Value;
        Assert.AreEqual(0u, await ChildAtAsync(c, 300, 300), "平时空白处没有窗口");

        // 开始拖:占 XdndSelection、抓指针(源都这么做)。空白处的子窗口就是接手窗口,带 WM_STATE(Java 只对这样的顶层找 XdndAware)。
        uint source = await SourceAsync(c, atoms);
        XMessage grab = await c.RequestAsync(26, 0, b => b.U32(c.RootWindow).U16(0x0040).U8(1).U8(1).U32(0).U32(0).U32(0));
        Assert.AreEqual(0, grab.Detail, "GrabSuccess");
        Assert.AreEqual(proxy, await ChildAtAsync(c, 300, 300));
        Assert.IsNotNull(await WindowPropertyAsync(c, proxy, await InternAsync(c, "WM_STATE")));
        uint topLevel = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(topLevel).U32(c.RootWindow).I16(10).I16(10).U16(50).U16(50).U16(0).U16(1).U32(0).U32(0));
        await c.SendAsync(8, 0, b => b.U32(topLevel));
        Assert.AreEqual(topLevel, await ChildAtAsync(c, 20, 20), "真的窗口照样在它上面");
        XMessage tree = await c.RequestAsync(15, 0, b => b.U32(c.RootWindow));
        _ = source;

        // 还抓着指针:过了一秒多还垫着。放开指针:一秒内撤掉。
        await Task.Delay(1300);
        Assert.AreEqual(proxy, await ChildAtAsync(c, 300, 300), "还在拖");
        await c.SendAsync(27, 0, b => b.U32(0));
        for (int i = 0; i < 40 && await ChildAtAsync(c, 300, 300) != 0; i++)
        {
            await Task.Delay(100);
        }
        Assert.AreEqual(0u, await ChildAtAsync(c, 300, 300), "拖动结束:撤掉了");
        Assert.IsTrue(tree.U16(16) >= 2, "垫着时 QueryTree 列得出它");
    }

    [TestMethod]
    public async Task 超过三种类型读XdndTypeList_都不认的不接受_源窗口销毁时报宿主结束()
    {
        using RecordingHost host = new();
        await using X11Server server = new(new X11ServerOptions { AcceptOutgoingDrags = true }, host);
        await using XTestClient probe = await XTestClient.ConnectAsync(server);
        Atoms a = await AtomsAsync(probe);
        uint t1 = await InternAsync(probe, "application/x-a"), t2 = await InternAsync(probe, "application/x-b");

        // 只有不认的类型:不取数据、回不接受。
        (XTestClient c, Atoms atoms, uint proxy, uint window) = await EnterAsync(server, [t1, t2]);
        await using (c)
        {
            Assert.AreEqual(0u, (await NextXdndAsync(c, atoms.Status)).U32(16) & 1);
            await Assert.ThrowsAsync<OperationCanceledException>(() => c.NextEventAsync(SelectionRequest, timeoutMs: 200));
        }

        // 四种类型(第四种才是 URI 列表):从源窗口的 XdndTypeList 读全。
        (XTestClient d, Atoms datoms, _, uint dwindow) = await EnterAsync(server, [t1, t2, a.Png, a.UriList]);
        await using (d)
        {
            XMessage request = await d.NextEventAsync(SelectionRequest);
            Assert.AreEqual(datoms.UriList, request.U32(20));
            await AnswerAsync(d, request, datoms.UriList, "file:///srv/z\r\n"u8.ToArray());
            await host.WaitForAsync(() => host.OutgoingDrags.Count == 1);
            await d.SendAsync(4, 0, b => b.U32(dwindow));   // 源程序把源窗口销毁了(崩溃、退出)
            await host.WaitForAsync(() => host.OutgoingDragsEnded.Count == 1);
        }
    }
}
