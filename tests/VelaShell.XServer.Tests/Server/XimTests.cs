using System.Buffers.Binary;
using System.Text;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>
/// 服务端当 XIM 输入法服务端(F5 第二步):按 The Input Method Protocol 的预连接约定被找到(XIM_SERVERS、@server= 选区、LOCALES / TRANSPORT),
/// 按 X 传输建连接、收发包(单条 ClientMessage、多条 ClientMessage、属性),开输入法、建输入上下文、报焦点与插入点,宿主的上屏与预编辑经
/// XIM_COMMIT / XIM_PREEDIT_* 交给程序。测试客户端逐字节扮演 Xlib 的那一侧。
/// </summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class XimTests
{
    private const byte ClientMessage = 33, SelectionNotify = 31, KeyPress = 2, MappingNotify = 34;

    // XIM 协议号(附录 C)
    private const byte XimConnect = 1, XimConnectReply = 2, XimError = 20, XimOpen = 30, XimOpenReply = 31, XimSetEventMask = 37,
        XimEncodingNegotiation = 38, XimEncodingNegotiationReply = 39, XimGetImValues = 44, XimGetImValuesReply = 45,
        XimCreateIc = 50, XimCreateIcReply = 51, XimSetIcValues = 54, XimSetIcValuesReply = 55, XimGetIcValues = 56, XimGetIcValuesReply = 57,
        XimSetIcFocus = 58, XimUnsetIcFocus = 59, XimCommit = 63, XimPreeditStart = 73, XimPreeditDraw = 75, XimPreeditDone = 78;

    private const uint PreeditCallbacks = 0x0002, PreeditPosition = 0x0004, PreeditNothing = 0x0008, StatusNothing = 0x0400;

    private static async Task<uint> InternAsync(XTestClient c, string name)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(name);
        XMessage m = await c.RequestAsync(16, 0, b => b.U16((ushort)bytes.Length).U16(0).Bytes(bytes).Pad());
        return m.U32(8);
    }

    private static async Task<byte[]> GetPropertyAsync(XTestClient c, uint window, uint property, uint type = 0, bool delete = false)
    {
        XMessage m = await c.RequestAsync(20, delete ? (byte)1 : (byte)0, b => b.U32(window).U32(property).U32(type).U32(0).U32(1 << 20));
        int units = (int)m.U32(16), format = m.Detail;
        return m.Bytes.AsSpan(32, units * Math.Max(1, format / 8)).ToArray();
    }

    /// <summary>一个 60×40、选了按键事件的顶层,在 (0, 0)。</summary>
    private static async Task<(uint Window, XTopLevelWindow Handle)> TopLevelAsync(XTestClient c, RecordingHost host)
    {
        uint window = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(window).U32(c.RootWindow).I16(0).I16(0).U16(60).U16(40).U16(0).U16(1).U32(0).U32(0x800).U32(1));
        await c.SendAsync(8, 0, b => b.U32(window));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(window));
        return (window, host.Mapped[window]);
    }

    private static X11ServerOptions Options => new() { InputMethodName = "velashell" };

    // ------------------------------------------------------------------ 扮演 Xlib 的 XIM 一侧

    /// <summary>程序这一侧的 XIM 连接:通信窗口、服务端的通信窗口,按 X 传输收发包。</summary>
    private sealed class XimClient
    {
        private readonly List<byte> _more = [];
        private int _propertySerial;

        private XimClient(XTestClient c, uint comm, uint ims, uint protocol, uint moreData)
        {
            C = c;
            Comm = comm;
            Ims = ims;
            Protocol = protocol;
            MoreData = moreData;
        }

        public XTestClient C { get; }

        public uint Comm { get; }

        public uint Ims { get; }

        public uint Protocol { get; }

        public uint MoreData { get; }

        public bool BigEndian => C.BigEndian;

        /// <summary>找到服务端(XIM_SERVERS → 选区属主)、建通信窗口、发 _XIM_XCONNECT,收它回的 _XIM_XCONNECT。</summary>
        public static async Task<XimClient> ConnectAsync(XTestClient c)
        {
            uint selection = await InternAsync(c, "@server=velashell");
            uint owner = (await c.RequestAsync(23, 0, b => b.U32(selection))).U32(8);
            Assert.AreNotEqual(0u, owner, "选区 @server=velashell 有属主");
            uint comm = c.NewId();
            await c.SendAsync(1, 0, b => b.U32(comm).U32(c.RootWindow).I16(0).I16(0).U16(1).U16(1).U16(0).U16(2).U32(0).U32(0));
            uint xconnect = await InternAsync(c, "_XIM_XCONNECT");
            await c.SendAsync(25, 0, b => b.U32(owner).U32(0).U8(ClientMessage).U8(32).U16(0).U32(owner).U32(xconnect)
                .U32(comm).U32(0).U32(0).U32(0).U32(0));
            XMessage reply = await c.NextAsync(m => !m.IsReply && !m.IsError && m.EventCode == ClientMessage && m.U32(8) == xconnect);
            Assert.AreEqual(comm, reply.U32(4), "回给程序的通信窗口");
            Assert.AreEqual((0u, 2u, 20u), (reply.U32(16), reply.U32(20), reply.U32(24)), "传输版本 0.2、分界长度 20");
            return new XimClient(c, comm, reply.U32(12), await InternAsync(c, "_XIM_PROTOCOL"), await InternAsync(c, "_XIM_MOREDATA"));
        }

        public enum Transport { Auto, MultiCm }

        public byte[] Packet(byte major, Action<XTestClient.Body>? body)
        {
            XTestClient.Body b = new(BigEndian);
            body?.Invoke(b);
            b.Pad();
            byte[] payload = b.ToArray();
            XTestClient.Body head = new(BigEndian);
            head.U8(major).U8(0).U16((ushort)(payload.Length / 4));
            return [.. head.ToArray(), .. payload];
        }

        /// <summary>发一个包:不超过 20 字节一条 _XIM_PROTOCOL;更长的写成服务端通信窗口上的属性(表 D.6)再通知,或拆成多条 ClientMessage。</summary>
        public async Task SendAsync(byte major, Action<XTestClient.Body>? body, Transport transport = Transport.Auto)
        {
            byte[] packet = Packet(major, body);
            if (packet.Length > 20 && transport == Transport.Auto)
            {
                uint property = await InternAsync(C, $"_client{_propertySerial++}");
                await C.SendAsync(18, 2, b => b.U32(Ims).U32(property).U32(31).U8(8).U8(0).U8(0).U8(0).U32((uint)packet.Length).Bytes(packet));
                await C.SendAsync(25, 0, b => b.U32(Ims).U32(0).U8(ClientMessage).U8(32).U16(0).U32(Ims).U32(Protocol)
                    .U32((uint)packet.Length).U32(property).U32(0).U32(0).U32(0));
                return;
            }
            for (int offset = 0; offset < packet.Length || offset == 0; offset += 20)
            {
                byte[] chunk = new byte[20];
                packet.AsSpan(offset, Math.Min(20, packet.Length - offset)).CopyTo(chunk);
                bool last = offset + 20 >= packet.Length;
                await C.SendAsync(25, 0, b => b.U32(Ims).U32(0).U8(ClientMessage).U8(8).U16(0).U32(Ims).U32(last ? Protocol : MoreData).Bytes(chunk));
            }
        }

        /// <summary>收下一个包(主操作码、正文)。</summary>
        public async Task<(byte Major, byte[] Body)> ReceiveAsync(int timeoutMs = 5000)
        {
            while (true)
            {
                XMessage m = await C.NextAsync(m => !m.IsReply && !m.IsError && m.EventCode == ClientMessage && m.U32(4) == Comm
                    && (m.U32(8) == Protocol || m.U32(8) == MoreData), timeoutMs);
                byte[] data;
                if (m.Detail == 32)
                {
                    int length = (int)m.U32(12);
                    data = (await GetPropertyAsync(C, Comm, m.U32(16), delete: true))[..length];
                }
                else if (m.U32(8) == MoreData)
                {
                    _more.AddRange(m.Bytes.AsSpan(12, 20));
                    continue;
                }
                else
                {
                    data = [.. _more, .. m.Bytes.AsSpan(12, 20)];
                    _more.Clear();
                }
                int bodyLength = 4 * (BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2)) : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2)));
                return (data[0], data.AsSpan(4, bodyLength).ToArray());
            }
        }

        /// <summary>收下一个包并断言它的主操作码。</summary>
        public async Task<byte[]> ExpectAsync(byte major, int timeoutMs = 5000)
        {
            (byte got, byte[] body) = await ReceiveAsync(timeoutMs);
            Assert.AreEqual(major, got, $"期望 XIM 包 {major},收到 {got}");
            return body;
        }

        public ushort U16(byte[] data, int offset) =>
            BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset)) : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));

        public uint U32(byte[] data, int offset) =>
            BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset)) : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));

        /// <summary>XIM_CONNECT → XIM_CONNECT_REPLY。</summary>
        public async Task HandshakeAsync()
        {
            await SendAsync(XimConnect, b => b.U8(BigEndian ? (byte)0x42 : (byte)0x6c).U8(0).U16(1).U16(0).U16(0));
            byte[] reply = await ExpectAsync(XimConnectReply);
            Assert.AreEqual((1, 0), (U16(reply, 0), U16(reply, 2)), "协议版本 1.0");
        }

        /// <summary>XIM_OPEN → XIM_OPEN_REPLY:返回 IM 编号与 IC 属性名 → 编号。</summary>
        public async Task<(ushort Im, Dictionary<string, (ushort Id, ushort Type)> IcAttributes, Dictionary<string, ushort> ImAttributes)> OpenAsync(string locale = "en_US.UTF-8")
        {
            byte[] name = Encoding.ASCII.GetBytes(locale);
            await SendAsync(XimOpen, b => b.U8((byte)name.Length).Bytes(name).Pad());
            byte[] reply = await ExpectAsync(XimOpenReply);
            ushort im = U16(reply, 0);
            int imLength = U16(reply, 2);
            Dictionary<string, ushort> imAttributes = [];
            int offset = 4;
            while (offset < 4 + imLength)
            {
                (string attr, ushort id, _, int size) = ReadAttr(reply, offset);
                imAttributes[attr] = id;
                offset += size;
            }
            int icLength = U16(reply, offset);
            offset += 4;
            int end = offset + icLength;
            Dictionary<string, (ushort, ushort)> icAttributes = [];
            while (offset < end)
            {
                (string attr, ushort id, ushort type, int size) = ReadAttr(reply, offset);
                icAttributes[attr] = (id, type);
                offset += size;
            }
            return (im, icAttributes, imAttributes);

            (string Name, ushort Id, ushort Type, int Size) ReadAttr(byte[] data, int at)
            {
                int n = U16(data, at + 4);
                int size = 6 + n + ((4 - ((2 + n) % 4)) % 4);
                return (Encoding.ASCII.GetString(data, at + 6, n), U16(data, at), U16(data, at + 2), size);
            }
        }
    }

    /// <summary>XICATTRIBUTE:编号、值长、值、补齐。</summary>
    private static XTestClient.Body Attribute(XTestClient.Body b, ushort id, byte[] value)
    {
        b.U16(id).U16((ushort)value.Length).Bytes(value);
        for (int i = value.Length; i % 4 != 0; i++)
        {
            b.U8(0);
        }
        return b;
    }

    private static byte[] U32Bytes(uint value, bool bigEndian)
    {
        byte[] b = new byte[4];
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(b, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        }
        return b;
    }

    private static byte[] PointBytes(short x, short y, bool bigEndian)
    {
        XTestClient.Body b = new(bigEndian);
        b.I16(x).I16(y);
        return b.ToArray();
    }

    /// <summary>建一个输入上下文(输入风格、客户窗口、焦点窗口,可选的嵌套预编辑属性),返回它的编号;顺带吃掉跟着来的 XIM_SET_EVENT_MASK。</summary>
    private static async Task<ushort> CreateIcAsync(XimClient x, ushort im, Dictionary<string, (ushort Id, ushort Type)> attrs, uint style, uint window,
        Action<XTestClient.Body>? preedit = null)
    {
        XTestClient.Body list = new(x.BigEndian);
        Attribute(list, attrs["inputStyle"].Id, U32Bytes(style, x.BigEndian));
        Attribute(list, attrs["clientWindow"].Id, U32Bytes(window, x.BigEndian));
        Attribute(list, attrs["focusWindow"].Id, U32Bytes(window, x.BigEndian));
        if (preedit is not null)
        {
            XTestClient.Body nested = new(x.BigEndian);
            preedit(nested);
            Attribute(list, attrs["preeditAttributes"].Id, nested.ToArray());
        }
        byte[] bytes = list.ToArray();
        await x.SendAsync(XimCreateIc, b => b.U16(im).U16((ushort)bytes.Length).Bytes(bytes));
        byte[] reply = await x.ExpectAsync(XimCreateIcReply);
        Assert.AreEqual(im, x.U16(reply, 0));
        ushort ic = x.U16(reply, 2);
        Assert.AreNotEqual((ushort)0, ic, "输入上下文的编号不为 0");
        byte[] mask = await x.ExpectAsync(XimSetEventMask);
        Assert.AreEqual((im, ic, 0u, 0u), (x.U16(mask, 0), x.U16(mask, 2), x.U32(mask, 4), x.U32(mask, 8)), "不要程序转任何事件过来");
        return ic;
    }

    // ================================================================== 用例

    [TestMethod]
    public async Task 预连接约定_XIM_SERVERS列出选区_LOCALES与TRANSPORT答得出_XCONNECT回服务端的通信窗口()
    {
        using RecordingHost host = new();
        await using X11Server server = new(Options, host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);

        uint servers = await InternAsync(c, "XIM_SERVERS");
        byte[] list = await GetPropertyAsync(c, c.RootWindow, servers, type: 4);
        uint selection = await InternAsync(c, "@server=velashell");
        Assert.IsTrue(Enumerable.Range(0, list.Length / 4).Any(i => BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(i * 4)) == selection),
            "XIM_SERVERS 里有 @server=velashell");

        uint requestor = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(requestor).U32(c.RootWindow).I16(0).I16(0).U16(1).U16(1).U16(0).U16(2).U32(0).U32(0));
        foreach ((string target, string expected) in new[] { ("LOCALES", "@locale="), ("TRANSPORT", "@transport=X/") })
        {
            uint atom = await InternAsync(c, target);
            await c.SendAsync(24, 0, b => b.U32(requestor).U32(selection).U32(atom).U32(atom).U32(0));
            XMessage notify = await c.NextEventAsync(SelectionNotify);
            Assert.AreEqual(atom, notify.U32(20), $"{target} 转换成功");
            string value = Encoding.Latin1.GetString(await GetPropertyAsync(c, requestor, atom, type: atom, delete: true));
            StringAssert.StartsWith(value, expected);
            if (target == "LOCALES")
            {
                string[] locales = value["@locale=".Length..].Split(',');
                CollectionAssert.IsSubsetOf(new[] { "C", "en", "zh", "ja", "ko", "zh_CN", "zh_CN.UTF-8", "en_US.UTF-8" }, locales);
            }
            else
            {
                Assert.AreEqual(expected, value);
            }
        }

        XimClient x = await XimClient.ConnectAsync(c);
        Assert.AreEqual(1, server.Xim!.ConnectionCount);
        Assert.AreNotEqual(x.Comm, x.Ims);
    }

    [TestMethod]
    public async Task OnTheSpot_焦点与插入点报给宿主_预编辑经回调画进程序_上屏走XIM_COMMIT_不改键位表()
    {
        using RecordingHost host = new();
        await using X11Server server = new(Options, host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint window, XTopLevelWindow handle) = await TopLevelAsync(c, host);
        server.FocusTopLevel(handle);
        XimClient x = await XimClient.ConnectAsync(c);
        await x.HandshakeAsync();
        (ushort im, var attrs, var imAttrs) = await x.OpenAsync();
        CollectionAssert.IsSubsetOf(new[] { "inputStyle", "clientWindow", "focusWindow", "filterEvents", "preeditAttributes", "statusAttributes",
            "spotLocation", "lineSpace", "fontSet", "separatorofNestedList" }, attrs.Keys.ToArray());
        Assert.AreEqual((ushort)0x7FFF, attrs["preeditAttributes"].Type, "嵌套属性的类型");

        // 编码协商:COMPOUND_TEXT 在列表里就挑它。
        byte[] names = [13, .. Encoding.ASCII.GetBytes("COMPOUND_TEXT"), 5, .. Encoding.ASCII.GetBytes("UTF-8")];
        await x.SendAsync(XimEncodingNegotiation, b => b.U16(im).U16((ushort)names.Length).Bytes(names).Pad().U16(0).U16(0));
        byte[] encoding = await x.ExpectAsync(XimEncodingNegotiationReply);
        Assert.AreEqual((im, (ushort)0, (ushort)0), (x.U16(encoding, 0), x.U16(encoding, 2), x.U16(encoding, 4)), "挑第 0 个(COMPOUND_TEXT)");

        // 支持的输入风格
        await x.SendAsync(XimGetImValues, b => b.U16(im).U16(2).U16(imAttrs["queryInputStyle"]).Pad());
        byte[] values = await x.ExpectAsync(XimGetImValuesReply);
        int count = x.U16(values, 8);
        uint[] styles = [.. Enumerable.Range(0, count).Select(i => x.U32(values, 12 + (i * 4)))];
        CollectionAssert.IsSubsetOf(new[] { PreeditCallbacks | StatusNothing, PreeditPosition | StatusNothing, PreeditNothing | StatusNothing }, styles);

        ushort ic = await CreateIcAsync(x, im, attrs, PreeditCallbacks | StatusNothing, window);
        Assert.IsNull(host.InputMethodFocus, "还没报焦点");
        await x.SendAsync(XimSetIcFocus, b => b.U16(im).U16(ic));
        await host.WaitForAsync(() => host.InputMethodFocus is not null);
        Assert.AreEqual(new XInputMethodFocus(handle, ClientDrawsPreedit: true, Cursor: null), host.InputMethodFocus, "没报插入点:Cursor 为 null");

        // 插入点(嵌套在 preeditAttributes 里):基线在 (10, 30),行高 20 → 竖条从 30 − 16 起、高 20。
        XTestClient.Body nested = new(x.BigEndian);
        Attribute(nested, attrs["spotLocation"].Id, PointBytes(10, 30, x.BigEndian));
        Attribute(nested, attrs["lineSpace"].Id, U32Bytes(20, x.BigEndian));
        byte[] nestedBytes = nested.ToArray();
        XTestClient.Body set = new(x.BigEndian);
        Attribute(set, attrs["preeditAttributes"].Id, nestedBytes);
        byte[] setBytes = set.ToArray();
        await x.SendAsync(XimSetIcValues, b => b.U16(im).U16(ic).U16((ushort)setBytes.Length).U16(0).Bytes(setBytes));
        await x.ExpectAsync(XimSetIcValuesReply);
        await host.WaitForAsync(() => host.InputMethodFocus?.Cursor is not null);
        Assert.AreEqual(new XRect(10, 14, 1, 20), host.InputMethodFocus!.Cursor);

        // 预编辑:START,再 DRAW(插入点在末尾、整段替换长度 0、每个字下划线)。
        server.InjectPreedit("ni", 2);
        await x.ExpectAsync(XimPreeditStart);
        byte[] draw = await x.ExpectAsync(XimPreeditDraw);
        Assert.AreEqual((2u, 0u, 0u, 0u), (x.U32(draw, 4), x.U32(draw, 8), x.U32(draw, 12), x.U32(draw, 16)), "caret、chg_first、chg_length、status");
        Assert.AreEqual("ni", Encoding.ASCII.GetString(draw, 22, x.U16(draw, 20)));
        Assert.AreEqual((8, 2u, 2u), (x.U16(draw, 24), x.U32(draw, 28), x.U32(draw, 32)), "两个字,各一个 XIMUnderline");
        server.InjectPreedit("nih", 3);
        draw = await x.ExpectAsync(XimPreeditDraw);
        Assert.AreEqual((3u, 0u, 2u), (x.U32(draw, 4), x.U32(draw, 8), x.U32(draw, 12)), "替换上一次的两个字");

        // 上屏:先擦掉预编辑(DRAW 删 3 个字、no string | no feedback)、DONE,再 COMMIT(XLookupChars,COMPOUND_TEXT 的 UTF-8 段)。
        server.InjectText("你好");
        draw = await x.ExpectAsync(XimPreeditDraw);
        Assert.AreEqual((0u, 3u, 3u), (x.U32(draw, 8), x.U32(draw, 12), x.U32(draw, 16)));
        await x.ExpectAsync(XimPreeditDone);
        byte[] commit = await x.ExpectAsync(XimCommit);
        Assert.AreEqual((im, ic, (ushort)0x0002), (x.U16(commit, 0), x.U16(commit, 2), x.U16(commit, 4)), "XLookupChars,不要求同步");
        byte[] expected = [0x1B, (byte)'%', (byte)'G', .. Encoding.UTF8.GetBytes("你好"), 0x1B, (byte)'%', (byte)'@'];
        CollectionAssert.AreEqual(expected, commit.AsSpan(8, x.U16(commit, 6)).ToArray());

        // 换行照常按回车键;前后的字各一次 COMMIT。整个过程不改键位表。
        server.InjectText("a\r\nb");
        Assert.AreEqual("a", Encoding.ASCII.GetString((await x.ExpectAsync(XimCommit)).AsSpan(8, 1)));
        XMessage key = await c.NextEventAsync(KeyPress);
        Assert.AreEqual(36, key.Detail, "回车键(US 键位表的 Return 是 36),CR LF 只按一次");
        Assert.AreEqual("b", Encoding.ASCII.GetString((await x.ExpectAsync(XimCommit)).AsSpan(8, 1)));
        await c.SyncAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => c.NextEventAsync(KeyPress, timeoutMs: 200), "只按了一次回车");
        await Assert.ThrowsAsync<OperationCanceledException>(() => c.NextEventAsync(MappingNotify, timeoutMs: 200), "没有借键码:键位表没动");
    }

    [TestMethod]
    public async Task OverTheSpot_宿主自己画预编辑_程序不收回调_上屏照样走XIM()
    {
        using RecordingHost host = new();
        await using X11Server server = new(Options, host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint window, XTopLevelWindow handle) = await TopLevelAsync(c, host);
        server.FocusTopLevel(handle);
        XimClient x = await XimClient.ConnectAsync(c);
        await x.HandshakeAsync();
        (ushort im, var attrs, _) = await x.OpenAsync();
        ushort ic = await CreateIcAsync(x, im, attrs, PreeditPosition | StatusNothing, window,
            preedit: n => Attribute(n, attrs["spotLocation"].Id, PointBytes(5, 20, x.BigEndian)));
        await x.SendAsync(XimSetIcFocus, b => b.U16(im).U16(ic));
        await host.WaitForAsync(() => host.InputMethodFocus is not null);
        XInputMethodFocus focus = host.InputMethodFocus!;
        Assert.IsFalse(focus.ClientDrawsPreedit);
        Assert.AreEqual(5, focus.Cursor!.Value.X);
        Assert.AreEqual(20, focus.Cursor.Value.Y + (focus.Cursor.Value.Height * 4 / 5), "竖条的上沿 = 基线 − 八成行高");

        server.InjectPreedit("ka", 2);
        server.InjectText("か");
        byte[] commit = await x.ExpectAsync(XimCommit);   // 前面没有 PREEDIT_START / DRAW
        Assert.AreEqual(ic, x.U16(commit, 2));
    }

    [TestMethod]
    public async Task 程序撤了焦点或焦点去了别的顶层_宿主收到null_上屏退回借键码()
    {
        using RecordingHost host = new();
        await using X11Server server = new(Options, host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint window, XTopLevelWindow handle) = await TopLevelAsync(c, host);
        (_, XTopLevelWindow other) = await TopLevelAsync(c, host);
        server.FocusTopLevel(handle);
        XimClient x = await XimClient.ConnectAsync(c);
        await x.HandshakeAsync();
        (ushort im, var attrs, _) = await x.OpenAsync();
        ushort ic = await CreateIcAsync(x, im, attrs, PreeditNothing | StatusNothing, window);
        await x.SendAsync(XimSetIcFocus, b => b.U16(im).U16(ic));
        await host.WaitForAsync(() => host.InputMethodFocus is not null);

        // X 的焦点去了另一个顶层(没有输入上下文):不再接受;回来又接受。
        server.FocusTopLevel(other);
        await host.WaitForAsync(() => host.InputMethodFocus is null);
        server.FocusTopLevel(handle);
        await host.WaitForAsync(() => host.InputMethodFocus is not null);

        await x.SendAsync(XimUnsetIcFocus, b => b.U16(im).U16(ic));
        await host.WaitForAsync(() => host.InputMethodFocus is null);
        server.InjectText("é");
        await c.NextEventAsync(MappingNotify);   // 借键码:为这个字改了一个空键码
        XMessage key = await c.NextEventAsync(KeyPress);
        Assert.AreEqual(window, key.U32(12), "按键送到焦点窗口");
        await Assert.ThrowsAsync<OperationCanceledException>(() => x.ReceiveAsync(timeoutMs: 200), "没有 XIM_COMMIT");
    }

    [TestMethod]
    public async Task 通信窗口销毁_连接收掉_服务端的通信窗口也没了()
    {
        using RecordingHost host = new();
        await using X11Server server = new(Options, host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint window, XTopLevelWindow handle) = await TopLevelAsync(c, host);
        server.FocusTopLevel(handle);
        XimClient x = await XimClient.ConnectAsync(c);
        await x.HandshakeAsync();
        (ushort im, var attrs, _) = await x.OpenAsync();
        ushort ic = await CreateIcAsync(x, im, attrs, PreeditCallbacks | StatusNothing, window);
        await x.SendAsync(XimSetIcFocus, b => b.U16(im).U16(ic));
        await host.WaitForAsync(() => host.InputMethodFocus is not null);

        await c.SendAsync(4, 0, b => b.U32(x.Comm));   // XCloseIM 之后 Xlib 就是这样销毁它的通信窗口
        await host.WaitForAsync(() => host.InputMethodFocus is null);
        Assert.AreEqual(0, server.Xim!.ConnectionCount);
        XMessage error = await c.RequestAsync(20, 0, b => b.U32(x.Ims).U32(1).U32(0).U32(0).U32(1));
        Assert.IsTrue(error.IsError, "服务端的通信窗口已经不在");
        Assert.AreEqual(3, error.Detail, "BadWindow");
    }

    [TestMethod]
    public async Task 不支持的输入风格回BadStyle_第一个包不是CONNECT回BadProtocol()
    {
        using RecordingHost host = new();
        await using X11Server server = new(Options, host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint window, _) = await TopLevelAsync(c, host);
        XimClient x = await XimClient.ConnectAsync(c);

        await x.SendAsync(XimOpen, b => b.U8(1).Bytes("C"u8.ToArray()).Pad());
        byte[] error = await x.ExpectAsync(XimError);
        Assert.AreEqual(13, x.U16(error, 6), "BadProtocol:还没 XIM_CONNECT");

        await x.HandshakeAsync();
        (ushort im, var attrs, _) = await x.OpenAsync();
        XTestClient.Body list = new(x.BigEndian);
        Attribute(list, attrs["inputStyle"].Id, U32Bytes(0x0101, x.BigEndian));   // PreeditArea | StatusArea:不支持
        Attribute(list, attrs["clientWindow"].Id, U32Bytes(window, x.BigEndian));
        byte[] bytes = list.ToArray();
        await x.SendAsync(XimCreateIc, b => b.U16(im).U16((ushort)bytes.Length).Bytes(bytes));
        error = await x.ExpectAsync(XimError);
        Assert.AreEqual((im, (ushort)1, (ushort)2), (x.U16(error, 0), x.U16(error, 4), x.U16(error, 6)), "IM 编号有效、BadStyle");
    }

    [TestMethod]
    public async Task 大端程序_多条ClientMessage拼成的包_GET_IC_VALUES答得出过滤掩码与嵌套的插入点()
    {
        using RecordingHost host = new();
        await using X11Server server = new(Options, host);
        await using XTestClient c = await XTestClient.ConnectAsync(server, bigEndian: true);
        (uint window, _) = await TopLevelAsync(c, host);
        XimClient x = await XimClient.ConnectAsync(c);
        await x.HandshakeAsync();
        (ushort im, var attrs, _) = await x.OpenAsync("ja_JP.UTF-8");

        // 拆成多条 ClientMessage(_XIM_MOREDATA … _XIM_PROTOCOL)发 XIM_CREATE_IC。
        XTestClient.Body list = new(x.BigEndian);
        Attribute(list, attrs["inputStyle"].Id, U32Bytes(PreeditPosition | StatusNothing, true));
        Attribute(list, attrs["clientWindow"].Id, U32Bytes(window, true));
        XTestClient.Body nested = new(true);
        Attribute(nested, attrs["spotLocation"].Id, PointBytes(-3, 41, true));
        Attribute(list, attrs["preeditAttributes"].Id, nested.ToArray());
        byte[] bytes = list.ToArray();
        await x.SendAsync(XimCreateIc, b => b.U16(im).U16((ushort)bytes.Length).Bytes(bytes), XimClient.Transport.MultiCm);
        ushort ic = x.U16(await x.ExpectAsync(XimCreateIcReply), 2);
        await x.ExpectAsync(XimSetEventMask);

        ushort filter = attrs["filterEvents"].Id, preedit = attrs["preeditAttributes"].Id, spot = attrs["spotLocation"].Id;
        ushort separator = attrs["separatorofNestedList"].Id;
        await x.SendAsync(XimGetIcValues, b => b.U16(im).U16(ic).U16(8).U16(filter).U16(preedit).U16(spot).U16(separator));
        byte[] reply = await x.ExpectAsync(XimGetIcValuesReply);
        Assert.AreEqual((filter, (ushort)4, 1u), (x.U16(reply, 8), x.U16(reply, 10), x.U32(reply, 12)), "filterEvents = KeyPressMask");
        Assert.AreEqual(preedit, x.U16(reply, 16));
        Assert.AreEqual((spot, (ushort)4), (x.U16(reply, 20), x.U16(reply, 22)), "嵌套里是 spotLocation");
        Assert.AreEqual(((short)-3, (short)41), ((short)x.U16(reply, 24), (short)x.U16(reply, 26)));
    }

    [TestMethod]
    public async Task XSETTINGS请GTK用XIM_没开输入法服务端时没有这一项_名字不合法构造就抛()
    {
        foreach (bool enabled in new[] { true, false })
        {
            await using X11Server server = new(enabled ? Options : new X11ServerOptions());
            await using XTestClient c = await XTestClient.ConnectAsync(server);
            uint selection = await InternAsync(c, "_XSETTINGS_S0");
            uint owner = (await c.RequestAsync(23, 0, b => b.U32(selection))).U32(8);
            byte[] settings = await GetPropertyAsync(c, owner, await InternAsync(c, "_XSETTINGS_SETTINGS"));
            Assert.AreEqual(enabled, Encoding.ASCII.GetString(settings).Contains("Gtk/IMModule", StringComparison.Ordinal));
            if (enabled)
            {
                Assert.IsTrue(Encoding.ASCII.GetString(settings).Contains("xim", StringComparison.Ordinal));
            }
            else
            {
                Assert.IsNull(server.Xim);
            }
        }
        Assert.ThrowsExactly<ArgumentException>(() => new X11Server(new X11ServerOptions { InputMethodName = "a b" }));
        Assert.ThrowsExactly<ArgumentException>(() => new X11Server(new X11ServerOptions { InputMethodName = "" }));
    }
}
