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

    /// <summary>
    /// 服务端取 CLIPBOARD 时先要 TARGETS(xs_plan F15):像不认 TARGETS 的老属主那样拒绝它,返回随后的文本请求(UTF8_STRING)。
    /// 取 PRIMARY 不要 TARGETS,第一个就是文本请求。
    /// </summary>
    private static async Task<XMessage> TextRequestAsync(XTestClient c, Func<XMessage, bool>? match = null)
    {
        uint targets = await InternAsync(c, "TARGETS");
        bool Match(XMessage m) => !m.IsReply && !m.IsError && m.EventCode == SelectionRequest && (match?.Invoke(m) ?? true);
        XMessage request = await c.NextAsync(Match);
        if (request.U32(20) == targets)
        {
            await SendSelectionNotifyAsync(c, request, 0);
            request = await c.NextAsync(Match);
        }
        return request;
    }

    /// <summary>X 端属主回一个目标:把数据写到服务端给的属性上,再发 SelectionNotify。</summary>
    private static async Task AnswerAsync(XTestClient c, XMessage request, uint type, byte[] data)
    {
        await ChangePropertyAsync(c, request.U32(12), request.U32(24), type, data);
        await SendSelectionNotifyAsync(c, request, request.U32(24));
    }

    /// <summary>ATOM 列表(TARGETS 的回答),小端。</summary>
    private static async Task AnswerTargetsAsync(XTestClient c, XMessage request, params uint[] atoms)
    {
        await c.SendAsync(18, 0, b =>
        {
            b.U32(request.U32(12)).U32(request.U32(24)).U32(4).U8(32).U8(0).U8(0).U8(0).U32((uint)atoms.Length);   // type ATOM(4)、format 32
            foreach (uint atom in atoms)
            {
                b.U32(atom);
            }
        });
        await SendSelectionNotifyAsync(c, request, request.U32(24));
    }

    [TestMethod]
    public async Task 宿主的文本X客户端能以UTF8与TARGETS取到()
    {
        await using X11Server server = new(new X11ServerOptions { ClipboardFollowsFocus = false });   // 这里只看传输;跟着焦点走见下面单独的用例
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
    public async Task TEXT目标在Latin1装不下时回UTF8_STRING_X端回COMPOUND_TEXT也照样解码()
    {
        using RecordingHost host = new();
        await using X11Server server = new(new X11ServerOptions { ClipboardFollowsFocus = false }, host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint clipboard = await InternAsync(c, "CLIPBOARD");
        uint utf8 = await InternAsync(c, "UTF8_STRING");
        uint text = await InternAsync(c, "TEXT");
        uint prop = await InternAsync(c, "MY_PROP");
        uint window = await CreateWindowAsync(c);
        async Task<(uint Type, byte[] Data)> ConvertTextAsync()
        {
            await c.SendAsync(24, 0, b => b.U32(window).U32(clipboard).U32(text).U32(prop).U32(0));
            await c.NextEventAsync(SelectionNotify);
            XMessage value = await c.RequestAsync(20, 1, b => b.U32(window).U32(prop).U32(0).U32(0).U32(1000));
            return (value.U32(8), value.Bytes[32..(32 + (int)value.U32(16))]);
        }

        server.SetClipboardText("中文 café");
        while ((await c.RequestAsync(23, 0, b => b.U32(clipboard))).U32(8) == 0)
        {
        }
        (uint type, byte[] data) = await ConvertTextAsync();
        Assert.AreEqual(utf8, type, "原先按 Latin-1 有损转换,汉字成了「?」");
        Assert.AreEqual("中文 café", Encoding.UTF8.GetString(data));

        server.SetClipboardText("café");
        await c.SyncAsync();
        (type, data) = await ConvertTextAsync();
        Assert.AreEqual(31u, type, "Latin-1 装得下:照旧回 STRING,老程序认得");
        Assert.AreEqual("café", Encoding.Latin1.GetString(data));

        // X 端的属主回 COMPOUND_TEXT:按类型解码后交给宿主。
        uint compound = await InternAsync(c, "COMPOUND_TEXT");
        uint owner = await CreateWindowAsync(c);
        await c.SendAsync(22, 0, b => b.U32(owner).U32(clipboard).U32(0));
        XMessage request = await TextRequestAsync(c);
        byte[] encoded = [(byte)'x', 0x1B, (byte)'%', (byte)'G', .. Encoding.UTF8.GetBytes("文本"), 0x1B, (byte)'%', (byte)'@'];
        await ChangePropertyAsync(c, request.U32(12), request.U32(24), compound, encoded);
        await SendSelectionNotifyAsync(c, request, request.U32(24));
        await host.WaitForAsync(() => host.Clipboard == "x文本");
    }

    [TestMethod]
    public async Task ConvertSelection的property是不存在的原子时回BadAtom_不往窗口上写()
    {
        await using X11Server server = new(new X11ServerOptions { ClipboardFollowsFocus = false });
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint clipboard = await InternAsync(c, "CLIPBOARD");
        uint utf8 = await InternAsync(c, "UTF8_STRING");
        server.SetClipboardText("x");
        while ((await c.RequestAsync(23, 0, b => b.U32(clipboard))).U32(8) == 0)
        {
        }

        const uint bogus = 0x00ABCDEF;
        await c.SendAsync(24, 0, b => b.U32(c.RootWindow).U32(clipboard).U32(utf8).U32(bogus).U32(0));
        XMessage error = await c.NextAsync(m => m.IsError);
        Assert.AreEqual(5, error.Detail, "BadAtom");
        Assert.AreEqual(bogus, error.U32(4));
        XMessage list = await c.RequestAsync(21, 0, b => b.U32(c.RootWindow));   // ListProperties
        uint[] atoms = [.. Enumerable.Range(0, list.U16(8)).Select(i => list.U32(32 + (i * 4)))];
        CollectionAssert.DoesNotContain(atoms, bogus, "原先服务端拿它当属性名写到根窗口上,之后 xprop -root 收到 BadAtom");
    }

    [TestMethod]
    public async Task 宿主的大文本按INCR分块交给X客户端_超过上限的文本当场拒绝()
    {
        await using X11Server server = new(new X11ServerOptions { ClipboardFollowsFocus = false });
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint clipboard = await InternAsync(c, "CLIPBOARD");
        uint utf8 = await InternAsync(c, "UTF8_STRING");
        uint incr = await InternAsync(c, "INCR");
        uint prop = await InternAsync(c, "PASTED");
        uint window = await CreateWindowAsync(c);
        await c.SendAsync(2, 0, b => b.U32(window).U32(0x800).U32(0x400000));   // PropertyChangeMask

        string big = string.Concat(Enumerable.Range(0, 70_000).Select(i => $"{i % 10}中"));   // 约 280 KB(UTF-8)
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => server.SetClipboardText(new string('x', X11Server.MaxClipboardBytes + 1)));
        server.SetClipboardText(big);
        while ((await c.RequestAsync(23, 0, b => b.U32(clipboard))).U32(8) == 0)
        {
        }
        await c.SendAsync(24, 0, b => b.U32(window).U32(clipboard).U32(utf8).U32(prop).U32(0));
        await c.NextEventAsync(SelectionNotify);
        // 第一次取:类型 INCR、值是总长的下限;取的同时删掉(delete = 1)表示「准备好了」。
        XMessage head = await c.RequestAsync(20, 1, b => b.U32(window).U32(prop).U32(0).U32(0).U32(1));
        Assert.AreEqual(incr, head.U32(8), "原先整份写成一个属性");
        Assert.AreEqual((uint)Encoding.UTF8.GetByteCount(big), head.U32(32));

        List<byte> received = [];
        int chunks = 0;
        while (true)
        {
            await c.NextAsync(m => !m.IsReply && !m.IsError && m.EventCode == PropertyNotify && m.U32(8) == prop && m.Bytes[16] == 0);
            XMessage chunk = await c.RequestAsync(20, 1, b => b.U32(window).U32(prop).U32(0).U32(0).U32(1_000_000));
            Assert.AreEqual(utf8, chunk.U32(8), "每块的类型是真正的类型");
            int length = (int)chunk.U32(16);
            if (length == 0)
            {
                break;   // 空的一块:结束
            }
            Assert.IsLessThanOrEqualTo(X11Server.IncrChunkBytes, length);
            received.AddRange(chunk.Bytes.AsSpan(32, length).ToArray());
            chunks++;
        }
        Assert.IsGreaterThan(1, chunks);
        Assert.AreEqual(big, Encoding.UTF8.GetString([.. received]));
    }

    [TestMethod]
    public async Task 服务端当属主支持MULTIPLE与COMPOUND_TEXT_转换不了的那一对写回None()
    {
        await using X11Server server = new(new X11ServerOptions { ClipboardFollowsFocus = false });
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint clipboard = await InternAsync(c, "CLIPBOARD");
        uint utf8 = await InternAsync(c, "UTF8_STRING"), compound = await InternAsync(c, "COMPOUND_TEXT");
        uint targets = await InternAsync(c, "TARGETS"), multiple = await InternAsync(c, "MULTIPLE"), pair = await InternAsync(c, "ATOM_PAIR");
        uint list = await InternAsync(c, "MY_LIST"), first = await InternAsync(c, "MY_FIRST"), second = await InternAsync(c, "MY_SECOND"),
            third = await InternAsync(c, "MY_THIRD"), bogus = await InternAsync(c, "image/png");
        uint window = await CreateWindowAsync(c);
        server.SetClipboardText("a 中文");
        while ((await c.RequestAsync(23, 0, b => b.U32(clipboard))).U32(8) == 0)
        {
        }
        async Task<XMessage> GetAsync(uint property) => await c.RequestAsync(20, 0, b => b.U32(window).U32(property).U32(0).U32(0).U32(1000));

        await c.SendAsync(24, 0, b => b.U32(window).U32(clipboard).U32(targets).U32(list).U32(0));
        await c.NextEventAsync(SelectionNotify);
        XMessage offered = await GetAsync(list);
        uint[] atoms = [.. Enumerable.Range(0, (int)offered.U32(16)).Select(i => offered.U32(32 + (i * 4)))];
        CollectionAssert.Contains(atoms, multiple, "ICCCM §2.6.2 要求属主支持 MULTIPLE");
        CollectionAssert.Contains(atoms, compound);

        // MULTIPLE:三对 —— UTF8_STRING、不支持的 image/png、COMPOUND_TEXT。
        await c.SendAsync(18, 0, b => b.U32(window).U32(list).U32(pair).U8(32).U8(0).U8(0).U8(0).U32(6)
            .U32(utf8).U32(first).U32(bogus).U32(second).U32(compound).U32(third));
        await c.SendAsync(24, 0, b => b.U32(window).U32(clipboard).U32(multiple).U32(list).U32(0));
        XMessage notify = await c.NextEventAsync(SelectionNotify);
        Assert.AreEqual(list, notify.U32(20), "回的就是放对的那个属性");
        XMessage one = await GetAsync(first);
        Assert.AreEqual("a 中文", Encoding.UTF8.GetString(one.Bytes, 32, (int)one.U32(16)));
        XMessage three = await GetAsync(third);
        Assert.AreEqual(compound, three.U32(8));
        Assert.AreEqual("a 中文", Protocol.XText.DecodeCompoundText(three.Bytes.AsSpan(32, (int)three.U32(16))));
        XMessage pairs = await GetAsync(list);
        Assert.AreEqual(0u, pairs.U32(32 + 12), "转换不了的那一对,属性换成 None");
        Assert.AreEqual(first, pairs.U32(32 + 4));
        Assert.AreEqual(0u, (await GetAsync(second)).U32(8), "没写");
    }

    [TestMethod]
    public async Task 从X端取选区每个选区各取各的_属主不回时过了时限放弃()
    {
        using RecordingHost host = new();
        await using X11Server server = new(new X11ServerOptions { ClipboardFollowsFocus = false, SyncPrimary = true }, host);
        server.FetchStepTimeout = TimeSpan.FromMilliseconds(200);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint clipboard = await InternAsync(c, "CLIPBOARD");
        uint utf8 = await InternAsync(c, "UTF8_STRING");
        uint window = await CreateWindowAsync(c);

        // 同时占 PRIMARY 与 CLIPBOARD:两次取各用各的属性,先回的那个不被后来的冲掉(原先只有一个槽)。
        await c.SendAsync(22, 0, b => b.U32(window).U32(1).U32(0));
        await c.SendAsync(22, 0, b => b.U32(window).U32(clipboard).U32(0));
        XMessage primaryRequest = await c.NextAsync(m => m.EventCode == SelectionRequest && m.U32(16) == 1);
        XMessage clipboardRequest = await TextRequestAsync(c, m => m.U32(16) == clipboard);
        Assert.AreNotEqual(primaryRequest.U32(24), clipboardRequest.U32(24), "两个选区取到不同的属性上");
        await ChangePropertyAsync(c, primaryRequest.U32(12), primaryRequest.U32(24), utf8, Encoding.UTF8.GetBytes("选中的"));
        await SendSelectionNotifyAsync(c, primaryRequest, primaryRequest.U32(24));
        await host.WaitForAsync(() => host.Clipboard == "选中的");   // 原先 CLIPBOARD 那次把 PRIMARY 的冲掉了,这条回应被丢掉
        await ChangePropertyAsync(c, clipboardRequest.U32(12), clipboardRequest.U32(24), utf8, Encoding.UTF8.GetBytes("复制的文本"));
        await SendSelectionNotifyAsync(c, clipboardRequest, clipboardRequest.U32(24));
        await host.WaitForAsync(() => host.Clipboard == "复制的文本");

        // 属主收到 SelectionRequest 却迟迟不回:过了时限这次作废,之后迟到的回应不再交给宿主。
        uint late = await CreateWindowAsync(c);
        await c.SendAsync(22, 0, b => b.U32(late).U32(clipboard).U32(0));
        XMessage stalled = await c.NextAsync(m => m.EventCode == SelectionRequest && m.U32(8) == late);
        await Task.Delay(600);
        await ChangePropertyAsync(c, stalled.U32(12), stalled.U32(24), utf8, Encoding.UTF8.GetBytes("迟到的"));
        await SendSelectionNotifyAsync(c, stalled, stalled.U32(24));
        await c.SyncAsync();
        await Task.Delay(100);
        Assert.AreEqual("复制的文本", host.Clipboard, "原先没有时限,一直等着");
    }

    [TestMethod]
    public async Task X客户端复制的文本交给宿主_写回来不抢选区()
    {
        using RecordingHost host = new();
        await using X11Server server = new(new X11ServerOptions { ClipboardFollowsFocus = false }, host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint clipboard = await InternAsync(c, "CLIPBOARD");
        uint utf8 = await InternAsync(c, "UTF8_STRING");
        uint window = await CreateWindowAsync(c);

        await c.SendAsync(22, 0, b => b.U32(window).U32(clipboard).U32(0));   // SetSelectionOwner
        XMessage targetsRequest = await c.NextEventAsync(SelectionRequest);
        Assert.AreEqual(window, targetsRequest.U32(8), "owner");
        Assert.AreEqual(await InternAsync(c, "TARGETS"), targetsRequest.U32(20), "先要 TARGETS");
        await AnswerTargetsAsync(c, targetsRequest, await InternAsync(c, "TARGETS"), utf8);
        XMessage request = await c.NextEventAsync(SelectionRequest);
        Assert.AreEqual(utf8, request.U32(20), "TARGETS 里有 UTF8_STRING:要它");

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
    public async Task X端复制之后属主退出_服务端替宿主接管_别的X程序照样粘贴得到()
    {
        using RecordingHost host = new();
        await using X11Server server = new(new X11ServerOptions { ClipboardFollowsFocus = false }, host);
        XTestClient copier = await XTestClient.ConnectAsync(server);
        await using XTestClient paster = await XTestClient.ConnectAsync(server);
        uint clipboard = await InternAsync(copier, "CLIPBOARD");
        uint utf8 = await InternAsync(copier, "UTF8_STRING");
        uint window = await CreateWindowAsync(copier);

        await copier.SendAsync(22, 0, b => b.U32(window).U32(clipboard).U32(0));
        XMessage request = await TextRequestAsync(copier);
        uint property = request.U32(24);
        await ChangePropertyAsync(copier, request.U32(12), property, utf8, Encoding.UTF8.GetBytes("复制的文本"));
        await SendSelectionNotifyAsync(copier, request, property);
        await host.WaitForAsync(() => host.Clipboard == "复制的文本");

        await copier.DisposeAsync();   // 复制的程序退出
        uint requestor = await CreateWindowAsync(paster);
        uint prop = await InternAsync(paster, "PASTED");
        for (int i = 0; i < 50; i++)
        {
            XMessage owner = await paster.RequestAsync(23, 0, b => b.U32(clipboard));
            if (owner.U32(8) != 0)
            {
                break;
            }
            await Task.Delay(20);
        }
        await paster.SendAsync(24, 0, b => b.U32(requestor).U32(clipboard).U32(utf8).U32(prop).U32(0));   // ConvertSelection
        XMessage notify = await paster.NextEventAsync(SelectionNotify);
        Assert.AreEqual(prop, notify.U32(20), "有人回应了");
        XMessage value = await paster.RequestAsync(20, 1, b => b.U32(requestor).U32(prop).U32(0).U32(0).U32(1000));
        Assert.AreEqual("复制的文本", Encoding.UTF8.GetString(value.Bytes, 32, (int)value.U32(16)));
    }

    [TestMethod]
    public async Task 大文本走INCR分块()
    {
        using RecordingHost host = new();
        await using X11Server server = new(new X11ServerOptions { ClipboardFollowsFocus = false }, host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint clipboard = await InternAsync(c, "CLIPBOARD");
        uint utf8 = await InternAsync(c, "UTF8_STRING");
        uint incr = await InternAsync(c, "INCR");
        uint window = await CreateWindowAsync(c);

        await c.SendAsync(22, 0, b => b.U32(window).U32(clipboard).U32(0));
        XMessage request = await TextRequestAsync(c);
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
        Assert.IsGreaterThan(0, at);
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
    public async Task 换DPI只替换RESOURCE_MANAGER里的Xft几项_用户xrdb进去的资源留着()
    {
        await using X11Server server = new(new X11ServerOptions { Dpi = 96 });
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint resources = await InternAsync(c, "RESOURCE_MANAGER");
        // xrdb -merge 的结果:用户的资源,连同一条带续行的与一条旧的 Xft.dpi。
        byte[] text = Encoding.Latin1.GetBytes("XTerm*background:\tblack\nEmacs.font:\tfixed\\\n-misc\nXft.dpi:\t120\n! 注释: 留着\n*faceName:\tMonospace\n");
        await c.SendAsync(18, 0, b => b.U32(c.RootWindow).U32(resources).U32(31).U8(8).U8(0).U8(0).U8(0).U32((uint)text.Length).Bytes(text).Pad());
        await c.SyncAsync();

        server.SetDisplayScale(144);
        await c.SyncAsync();
        XMessage value = await c.RequestAsync(20, 0, b => b.U32(c.RootWindow).U32(resources).U32(0).U32(0).U32(100000));
        string merged = Encoding.Latin1.GetString(value.Bytes, 32, (int)value.U32(16));
        StringAssert.Contains(merged, "XTerm*background:\tblack\n", "原先换一次 DPI 整份覆盖,用户的资源全丢");
        StringAssert.Contains(merged, "Emacs.font:\tfixed\\\n-misc\n", "续行跟着它那一条走");
        StringAssert.Contains(merged, "*faceName:\tMonospace\n");
        StringAssert.Contains(merged, "Xft.dpi:\t144\n");
        Assert.DoesNotContain("Xft.dpi:\t120", merged, "服务端管的那几项换成新值,不留重复的旧值");
        Assert.AreEqual(1, merged.Split("Xft.antialias:").Length - 1);
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
    [TestMethod]
    public async Task 剪贴板跟着键盘焦点走_别的会话读不到宿主的文本_后台会话的复制不进系统剪贴板()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient a = await XTestClient.ConnectAsync(server, label: "joe@a:22");
        await using XTestClient xclip = await XTestClient.ConnectAsync(server, label: "joe@a:22");   // 同一个 SSH 会话里的 xclip
        await using XTestClient b = await XTestClient.ConnectAsync(server, label: "joe@b:22");
        uint clipboard = await InternAsync(a, "CLIPBOARD");
        uint utf8 = await InternAsync(a, "UTF8_STRING");

        async Task<uint> MapAsync(XTestClient c)
        {
            uint id = c.NewId();
            await c.SendAsync(1, 0, w => w.U32(id).U32(c.RootWindow).I16(0).I16(0).U16(40).U16(30).U16(0).U16(1).U32(0).U32(0));
            await c.SendAsync(8, 0, w => w.U32(id));
            await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
            return id;
        }
        uint windowA = await MapAsync(a);
        uint windowB = await MapAsync(b);
        server.FocusTopLevel(host.Mapped[windowA]);   // 用户在用会话 A 的程序

        server.SetClipboardText("本机复制的密码");
        while ((await a.RequestAsync(23, 0, w => w.U32(clipboard))).U32(8) == 0)
        {
        }

        async Task<uint> ConvertAsync(XTestClient c)
        {
            uint requestor = await CreateWindowAsync(c);
            uint property = await InternAsync(c, "PASTED");
            await c.SendAsync(24, 0, w => w.U32(requestor).U32(clipboard).U32(utf8).U32(property).U32(0));
            return (await c.NextEventAsync(SelectionNotify)).U32(20);
        }
        Assert.AreNotEqual(0u, await ConvertAsync(xclip), "同一个会话(连接名相同)读得到");
        Assert.AreEqual(0u, await ConvertAsync(b), "别的会话读不到:原先点一下任意 X 窗口,所有会话都读得到");

        // 后台会话 B 抢 CLIPBOARD:不去取它的内容,系统剪贴板不被改写。
        await b.SendAsync(22, 0, w => w.U32(windowB).U32(clipboard).U32(0));
        await Assert.ThrowsAsync<OperationCanceledException>(() => b.NextEventAsync(SelectionRequest, timeoutMs: 200));
        Assert.IsNull(host.Clipboard);

        // 焦点所在的会话 A 复制:照常交给宿主。
        await a.SendAsync(22, 0, w => w.U32(windowA).U32(clipboard).U32(0));
        XMessage request = await TextRequestAsync(a);
        await ChangePropertyAsync(a, request.U32(12), request.U32(24), utf8, Encoding.UTF8.GetBytes("从 A 复制"));
        await SendSelectionNotifyAsync(a, request, request.U32(24));
        await host.WaitForAsync(() => host.Clipboard == "从 A 复制");
    }
    [TestMethod]
    public async Task 宿主接管剪贴板的XFIXES通知只发给焦点所在的会话_别的会话不知道何时有新内容()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient a = await XTestClient.ConnectAsync(server, label: "joe@a:22");
        await using XTestClient b = await XTestClient.ConnectAsync(server, label: "joe@b:22");
        uint clipboard = await InternAsync(a, "CLIPBOARD");
        XMessage xfixes = await b.RequestAsync(98, 0, w => w.U16(6).U16(0).Bytes(Encoding.Latin1.GetBytes("XFIXES")).Pad());
        byte major = xfixes.Bytes[9], firstEvent = xfixes.Bytes[10];
        await a.SendAsync(major, 2, w => w.U32(a.RootWindow).U32(clipboard).U32(0x1));   // SelectSelectionInput:SetSelectionOwner
        await b.SendAsync(major, 2, w => w.U32(b.RootWindow).U32(clipboard).U32(0x1));
        await a.SyncAsync();
        await b.SyncAsync();

        uint windowA = a.NewId();
        await a.SendAsync(1, 0, w => w.U32(windowA).U32(a.RootWindow).I16(0).I16(0).U16(40).U16(30).U16(0).U16(1).U32(0).U32(0));
        await a.SendAsync(8, 0, w => w.U32(windowA));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(windowA));
        server.FocusTopLevel(host.Mapped[windowA]);

        server.SetClipboardText("本机复制的第一段");
        await a.NextEventAsync(firstEvent);
        server.SetClipboardText("本机复制的第二段");   // 服务端本来就是属主:GetSelectionOwner 看不出变化
        XMessage second = await a.NextEventAsync(firstEvent);
        Assert.AreEqual(clipboard, second.U32(12), "焦点所在的会话照常收到");
        await b.SyncAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => b.NextEventAsync(firstEvent, timeoutMs: 200),
            "别的会话收不到:原先它据此精确得知宿主剪贴板何时有了新内容");
    }

    /// <summary>两个 SSH 会话(连接名不同)各连一个客户端,各映射一个顶层窗口。</summary>
    private static async Task<(XTestClient A, uint WindowA, XTestClient B, uint WindowB)> TwoSessionsAsync(X11Server server, RecordingHost host)
    {
        XTestClient a = await XTestClient.ConnectAsync(server, label: "joe@a:22");
        XTestClient b = await XTestClient.ConnectAsync(server, label: "joe@b:22");
        async Task<uint> MapAsync(XTestClient c)
        {
            uint id = c.NewId();
            await c.SendAsync(1, 0, w => w.U32(id).U32(c.RootWindow).I16(0).I16(0).U16(40).U16(30).U16(0).U16(1).U32(0).U32(0));
            await c.SendAsync(8, 0, w => w.U32(id));
            await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
            return id;
        }
        return (a, await MapAsync(a), b, await MapAsync(b));
    }

    /// <summary>X 客户端占有 CLIPBOARD,并回应服务端随之发来的 SelectionRequest(把文本交给宿主)。</summary>
    private static async Task CopyAsync(XTestClient c, uint window, uint clipboard, uint utf8, string text)
    {
        await c.SendAsync(22, 0, w => w.U32(window).U32(clipboard).U32(0));
        XMessage request = await TextRequestAsync(c);
        await ChangePropertyAsync(c, request.U32(12), request.U32(24), utf8, Encoding.UTF8.GetBytes(text));
        await SendSelectionNotifyAsync(c, request, request.U32(24));
    }

    [TestMethod]
    public async Task 剪贴板按会话隔离_别的会话看不到属主_收不到SelectionClear_也读不到另一个会话里的复制()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        (XTestClient a, uint windowA, XTestClient b, uint windowB) = await TwoSessionsAsync(server, host);
        await using XTestClient disposeA = a;
        await using XTestClient disposeB = b;
        uint clipboard = await InternAsync(a, "CLIPBOARD");
        uint utf8 = await InternAsync(a, "UTF8_STRING");

        server.FocusTopLevel(host.Mapped[windowA]);   // 用户在会话 A 里复制
        await CopyAsync(a, windowA, clipboard, utf8, "会话 A 的口令");
        await host.WaitForAsync(() => host.Clipboard == "会话 A 的口令");

        // 会话 B 不在焦点:它眼里 CLIPBOARD 没有属主(原先看得到 A 的窗口,还能直接向 A 要内容)。
        Assert.AreEqual(0u, (await b.RequestAsync(23, 0, w => w.U32(clipboard))).U32(8), "B 看不到 A 的属主");
        uint requestor = await CreateWindowAsync(b);
        uint pasted = await InternAsync(b, "PASTED");
        await b.SendAsync(24, 0, w => w.U32(requestor).U32(clipboard).U32(utf8).U32(pasted).U32(0));
        Assert.AreEqual(0u, (await b.NextEventAsync(SelectionNotify)).U32(20), "B 读不到 A 里的复制");
        await Assert.ThrowsAsync<OperationCanceledException>(() => a.NextEventAsync(SelectionRequest, timeoutMs: 200),
            "A 的属主根本没收到 B 的请求");

        // B 在后台占 CLIPBOARD:只改 B 那一份。A 收不到 SelectionClear,A 眼里属主仍是自己。
        await b.SendAsync(22, 0, w => w.U32(windowB).U32(clipboard).U32(0));
        await b.SyncAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => a.NextEventAsync(SelectionClear, timeoutMs: 200),
            "原先 A 收到 SelectionClear,从而知道别的会话在动剪贴板");
        Assert.AreEqual(windowA, (await a.RequestAsync(23, 0, w => w.U32(clipboard))).U32(8));
        Assert.AreEqual(windowB, (await b.RequestAsync(23, 0, w => w.U32(clipboard))).U32(8));

        // 宿主在 A 拿着焦点时有了新文本:只在 A 里接管,B 那一份不动、也不收 SelectionClear。
        server.SetClipboardText("本机复制的");
        await a.NextEventAsync(SelectionClear);
        await Assert.ThrowsAsync<OperationCanceledException>(() => b.NextEventAsync(SelectionClear, timeoutMs: 200));
        Assert.AreEqual(windowB, (await b.RequestAsync(23, 0, w => w.U32(clipboard))).U32(8), "B 看不出宿主何时有了新内容");
    }

    [TestMethod]
    public async Task 跨会话的复制粘贴经宿主的剪贴板中转_切到哪个会话就在那里占有最新的文本()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        (XTestClient a, uint windowA, XTestClient b, uint windowB) = await TwoSessionsAsync(server, host);
        await using XTestClient disposeA = a;
        await using XTestClient disposeB = b;
        uint clipboard = await InternAsync(a, "CLIPBOARD");
        uint utf8 = await InternAsync(a, "UTF8_STRING");

        server.FocusTopLevel(host.Mapped[windowA]);
        await CopyAsync(a, windowA, clipboard, utf8, "从 A 复制,到 B 粘贴");
        await host.WaitForAsync(() => host.Clipboard == "从 A 复制,到 B 粘贴");

        server.FocusTopLevel(host.Mapped[windowB]);   // 用户切到会话 B
        uint owner = 0;
        for (int i = 0; i < 50 && owner == 0; i++)
        {
            owner = (await b.RequestAsync(23, 0, w => w.U32(clipboard))).U32(8);
        }
        Assert.AreNotEqual(0u, owner, "服务端替宿主在 B 里占有了最新的文本");
        Assert.AreNotEqual(windowA, owner);
        uint requestor = await CreateWindowAsync(b);
        uint pasted = await InternAsync(b, "PASTED");
        await b.SendAsync(24, 0, w => w.U32(requestor).U32(clipboard).U32(utf8).U32(pasted).U32(0));
        Assert.AreEqual(pasted, (await b.NextEventAsync(SelectionNotify)).U32(20));
        XMessage value = await b.RequestAsync(20, 1, w => w.U32(requestor).U32(pasted).U32(0).U32(0).U32(1000));
        Assert.AreEqual("从 A 复制,到 B 粘贴", Encoding.UTF8.GetString(value.Bytes, 32, (int)value.U32(16)));

        // A 那一份不受影响:属主仍是 A 自己的窗口,也没收到 SelectionClear。
        Assert.AreEqual(windowA, (await a.RequestAsync(23, 0, w => w.U32(clipboard))).U32(8));
        await Assert.ThrowsAsync<OperationCanceledException>(() => a.NextEventAsync(SelectionClear, timeoutMs: 200));

        // B 在后台之后自己又复制了别的:切回 B 时不被宿主的旧文本盖掉(最近一次复制赢)。
        server.FocusTopLevel(host.Mapped[windowA]);
        await b.SendAsync(22, 0, w => w.U32(windowB).U32(clipboard).U32(0));
        await b.SyncAsync();
        server.FocusTopLevel(host.Mapped[windowB]);
        await b.SyncAsync();
        Assert.AreEqual(windowB, (await b.RequestAsync(23, 0, w => w.U32(clipboard))).U32(8));
    }

    [TestMethod]
    public async Task SetSelectionOwner的时间戳规则与当前有没有属主无关_未来的时间戳锁不住选区()
    {
        await using X11Server server = new();
        await using XTestClient attacker = await XTestClient.ConnectAsync(server);
        await using XTestClient victim = await XTestClient.ConnectAsync(server);
        uint selection = await InternAsync(attacker, "CLIPBOARD");
        uint evil = await CreateWindowAsync(attacker);
        uint good = await CreateWindowAsync(victim);
        async Task<uint> OwnerAsync() => (await victim.RequestAsync(23, 0, b => b.U32(selection))).U32(8);

        // 先放弃(没有属主了),再带一个远在未来的时间戳去占:原先「没属主时不查」,未来时间被接受。
        await attacker.SendAsync(22, 0, b => b.U32(0).U32(selection).U32(0));
        await attacker.SendAsync(22, 0, b => b.U32(evil).U32(selection).U32(0x7FFFFFF0));
        await attacker.SyncAsync();
        Assert.AreEqual(0u, await OwnerAsync(), "晚于服务端当前时间:无效");

        // 正常程序带真实时间去占:照常成功(原先之后所有人都被当成「早于当前属主」静默忽略)。
        await Task.Delay(20);
        await victim.SendAsync(22, 0, b => b.U32(good).U32(selection).U32(0));
        Assert.AreEqual(good, await OwnerAsync());

        // 属主放弃之后,最后一次换属主的时间还在:带着更早时间戳的请求照样无效。
        await victim.SendAsync(22, 0, b => b.U32(0).U32(selection).U32(0));
        await attacker.SendAsync(22, 0, b => b.U32(evil).U32(selection).U32(1));
        await attacker.SyncAsync();
        Assert.AreEqual(0u, await OwnerAsync(), "早于最后一次换属主的时间:无效");
    }

    /// <summary>PNG 文件头加几个字节:测试里当一张图片用(服务端不解码图片)。</summary>
    private static readonly byte[] FakePng = [0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 1, 2, 3];

    [TestMethod]
    public async Task X端复制带HTML与图片_按TARGETS逐个取回_一起交给宿主()
    {
        using RecordingHost host = new();
        await using X11Server server = new(new X11ServerOptions { ClipboardFollowsFocus = false }, host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint clipboard = await InternAsync(c, "CLIPBOARD");
        uint utf8 = await InternAsync(c, "UTF8_STRING");
        uint html = await InternAsync(c, "text/html"), png = await InternAsync(c, "image/png");
        uint window = await CreateWindowAsync(c);

        await c.SendAsync(22, 0, b => b.U32(window).U32(clipboard).U32(0));
        XMessage targets = await c.NextEventAsync(SelectionRequest);
        await AnswerTargetsAsync(c, targets, await InternAsync(c, "TARGETS"), utf8, html, png, await InternAsync(c, "SAVE_TARGETS"));
        List<uint> asked = [];
        foreach ((uint type, byte[] data) in (List<(uint, byte[])>)[(utf8, Encoding.UTF8.GetBytes("表格")), (html, Encoding.UTF8.GetBytes("<b>表格</b>")), (png, FakePng)])
        {
            XMessage request = await c.NextEventAsync(SelectionRequest);
            asked.Add(request.U32(20));
            await AnswerAsync(c, request, type, data);
        }
        CollectionAssert.AreEqual(new[] { utf8, html, png }, asked, "文本、HTML、图片依次要");
        await host.WaitForAsync(() => host.ClipboardContent is not null);
        XClipboardContent content = host.ClipboardContent!;
        Assert.AreEqual("表格", content.Text);
        Assert.AreEqual("<b>表格</b>", content.Html);
        CollectionAssert.AreEqual(FakePng, content.Png.ToArray());
        Assert.AreEqual("表格", host.Clipboard, "只认文本的宿主照样收到文本");
    }

    [TestMethod]
    public async Task 宿主给的图片与HTML_TARGETS只列有的格式_按目标取得到()
    {
        await using X11Server server = new(new X11ServerOptions { ClipboardFollowsFocus = false });
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint clipboard = await InternAsync(c, "CLIPBOARD");
        uint targets = await InternAsync(c, "TARGETS"), utf8 = await InternAsync(c, "UTF8_STRING");
        uint html = await InternAsync(c, "text/html"), png = await InternAsync(c, "image/png");
        uint prop = await InternAsync(c, "MY_PROP");
        uint window = await CreateWindowAsync(c);
        async Task<(uint Property, uint Type, byte[] Data)> ConvertAsync(uint target)
        {
            await c.SendAsync(24, 0, b => b.U32(window).U32(clipboard).U32(target).U32(prop).U32(0));
            uint property = (await c.NextEventAsync(SelectionNotify)).U32(20);
            XMessage value = await c.RequestAsync(20, 1, b => b.U32(window).U32(prop).U32(0).U32(0).U32(1000));
            return (property, value.U32(8), value.Bytes[32..(32 + (int)(value.U32(16) * (value.Bytes[1] / 8)))]);
        }

        server.SetClipboard(new XClipboardContent { Html = "<i>x</i>", Png = FakePng });
        while ((await c.RequestAsync(23, 0, b => b.U32(clipboard))).U32(8) == 0)
        {
        }
        (_, _, byte[] list) = await ConvertAsync(targets);
        uint[] atoms = [.. Enumerable.Range(0, list.Length / 4).Select(i => BitConverter.ToUInt32(list, i * 4))];
        CollectionAssert.Contains(atoms, html);
        CollectionAssert.Contains(atoms, png);
        CollectionAssert.DoesNotContain(atoms, utf8, "没有文本就不列文本的目标");
        (uint property, uint type, byte[] data) = await ConvertAsync(png);
        Assert.AreEqual(png, type);
        CollectionAssert.AreEqual(FakePng, data);
        Assert.AreEqual(0u, (await ConvertAsync(utf8)).Property, "没有文本:UTF8_STRING 拒绝");
        Assert.ThrowsExactly<ArgumentException>(() => server.SetClipboard(new XClipboardContent()));
    }

    [TestMethod]
    public async Task 宿主只写回交出去的文本也算回声_不抢走带图片的选区()
    {
        using RecordingHost host = new();
        await using X11Server server = new(new X11ServerOptions { ClipboardFollowsFocus = false }, host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint clipboard = await InternAsync(c, "CLIPBOARD");
        uint utf8 = await InternAsync(c, "UTF8_STRING"), png = await InternAsync(c, "image/png");
        uint window = await CreateWindowAsync(c);

        await c.SendAsync(22, 0, b => b.U32(window).U32(clipboard).U32(0));
        await AnswerTargetsAsync(c, await c.NextEventAsync(SelectionRequest), utf8, png);
        await AnswerAsync(c, await c.NextEventAsync(SelectionRequest), utf8, Encoding.UTF8.GetBytes("图注"));
        await AnswerAsync(c, await c.NextEventAsync(SelectionRequest), png, FakePng);
        await host.WaitForAsync(() => host.ClipboardContent is not null);

        server.SetClipboardText("图注");   // 只认文本的宿主写回来的只有文本
        Assert.AreEqual(window, (await c.RequestAsync(23, 0, b => b.U32(clipboard))).U32(8), "X 程序仍是属主,图片没丢");
    }

    [TestMethod]
    public async Task 剪贴板管理器_SAVE_TARGETS在内容取完之后才回答_属主退出后别的程序照样粘贴到()
    {
        using RecordingHost host = new();
        await using X11Server server = new(new X11ServerOptions { ClipboardFollowsFocus = false }, host);
        XTestClient copier = await XTestClient.ConnectAsync(server);
        await using XTestClient paster = await XTestClient.ConnectAsync(server);
        uint clipboard = await InternAsync(copier, "CLIPBOARD");
        uint manager = await InternAsync(copier, "CLIPBOARD_MANAGER"), save = await InternAsync(copier, "SAVE_TARGETS");
        uint utf8 = await InternAsync(copier, "UTF8_STRING");
        uint window = await CreateWindowAsync(copier);
        Assert.AreNotEqual(0u, (await copier.RequestAsync(23, 0, b => b.U32(manager))).U32(8), "服务端占着 CLIPBOARD_MANAGER");

        // 复制,还没把内容交出去就请管理器保存(GTK 退出时就是这样):管理器等内容取完才回答。
        await copier.SendAsync(22, 0, b => b.U32(window).U32(clipboard).U32(0));
        XMessage request = await TextRequestAsync(copier);
        uint saveProperty = await InternAsync(copier, "SAVE_PROP");
        await copier.SendAsync(24, 0, b => b.U32(window).U32(manager).U32(save).U32(saveProperty).U32(0));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => copier.NextEventAsync(SelectionNotify, timeoutMs: 200),
            "内容还没取完:不回答(规范:属主收到回答就退出,不等传完)");
        await AnswerAsync(copier, request, utf8, Encoding.UTF8.GetBytes("退出前复制的"));
        XMessage saved = await copier.NextAsync(m => !m.IsReply && !m.IsError && m.EventCode == SelectionNotify && m.U32(12) == manager);
        Assert.AreEqual(save, saved.U32(16));
        Assert.AreEqual(saveProperty, saved.U32(20), "保存成功");

        await copier.DisposeAsync();   // 复制的程序退出
        uint requestor = await CreateWindowAsync(paster);
        uint pasted = await InternAsync(paster, "PASTED");
        while ((await paster.RequestAsync(23, 0, b => b.U32(clipboard))).U32(8) == 0)
        {
            await Task.Delay(20);
        }
        await paster.SendAsync(24, 0, b => b.U32(requestor).U32(clipboard).U32(utf8).U32(pasted).U32(0));
        Assert.AreEqual(pasted, (await paster.NextEventAsync(SelectionNotify)).U32(20));
        XMessage value = await paster.RequestAsync(20, 1, b => b.U32(requestor).U32(pasted).U32(0).U32(0).U32(1000));
        Assert.AreEqual("退出前复制的", Encoding.UTF8.GetString(value.Bytes, 32, (int)value.U32(16)));
    }
}
