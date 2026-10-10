// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   The Input Method Protocol(X Consortium Standard,Version 1.0)——
//     「Default Preconnection Convention」(根窗口的 XIM_SERVERS、选区 @server=名字、目标 LOCALES / TRANSPORT)、
//     「Basic Requests Packet Format」「Data Types」(STR / XIMATTR / XICATTR / XIMATTRIBUTE / XICATTRIBUTE 与值类型表)、
//     「Error Notification」「Connection Establishment」「Event Flow Control」「Encoding Negotiation」
//     「Query the supported extension protocol list」「Setting / Getting IM Values」「Creating / Destroying the IC」
//     「Setting / Getting IC Values」「Setting / Unsetting IC Focus」「Filtering Events」「Synchronizing with the IM Server」
//     「Sending a committed string」「Reset IC」「Preedit Callbacks」、附录 B「Transport List」、附录 C「Protocol Number」、
//     附录 D(2)「Transport Layer」的 X 传输
//   The XIM Transport Specification(Revision 0.1)——「X Transport」:_XIM_XCONNECT、_XIM_PROTOCOL / _XIM_MOREDATA、
//     传输版本表(这里回 major 0 / minor 2:单条 ClientMessage、多条 ClientMessage 与「属性 + ClientMessage」都认)、分界长度;
//     属性写在收方的通信窗口上、类型 STRING(两份文档的正文写法互相矛盾,以 Input Method Protocol 附录 D 的表 D.6 / D.10 为准),
//     通知属性的那条 _XIM_PROTOCOL 用格式 32(表 D.7 / D.11;表 1.8 写的格式 8 与它的 data.l 自相矛盾)
//   Xlib - C Language X Interface 第 13 章 —— XIMStyle 的各个位、XN* 属性名(只取名字与位值)
//
//   服务端扮演 XIM 服务端只为一件事:把宿主的输入法(本机的拼音、假名、韩文输入法)组好的字交给远端程序,而不必在远端装 fcitx / ibus。
//   按键全在程序那边按它自己的键位表解释:XIM_SET_EVENT_MASK 把转发掩码设成 0,程序不把按键经 XIM 转给我们 —— 宿主的输入法
//   在本机就吃掉了组字用的键,只有组好的字(XIM_COMMIT)与预编辑(XIM_PREEDIT_*)经 XIM 过去。按键要是经 SSH 绕一圈再回来,
//   每个键都多一个往返。

using System.Buffers.Binary;
using System.Text;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer.Server;

/// <summary>
/// 服务端扮演的 XIM 输入法服务端:占着 <c>@server=名字</c> 选区,程序经 X 传输(ClientMessage 与窗口属性)连过来,
/// 开输入法(XIM_OPEN)、建输入上下文(XIM_CREATE_IC)、报焦点与插入点;宿主的输入法上屏的字经 XIM_COMMIT 交给键盘焦点所在的
/// 那个输入上下文,预编辑经 XIM_PREEDIT_START / DRAW / DONE 让程序画在自己的输入框里(on-the-spot)。自成一体 ——
/// 只经服务端少数 internal 成员碰资源表、窗口属性与焦点。只在执行线程上用。
/// </summary>
internal sealed class XimServer
{
    /// <summary>协议号(附录 C)。</summary>
    private static class Op
    {
        public const byte Connect = 1, ConnectReply = 2, Disconnect = 3, DisconnectReply = 4;
        public const byte AuthRequired = 10, AuthNg = 14;
        public const byte Error = 20;
        public const byte Open = 30, OpenReply = 31, Close = 32, CloseReply = 33, TriggerNotify = 35, TriggerNotifyReply = 36;
        public const byte SetEventMask = 37, EncodingNegotiation = 38, EncodingNegotiationReply = 39;
        public const byte QueryExtension = 40, QueryExtensionReply = 41;
        public const byte SetImValues = 42, SetImValuesReply = 43, GetImValues = 44, GetImValuesReply = 45;
        public const byte CreateIc = 50, CreateIcReply = 51, DestroyIc = 52, DestroyIcReply = 53;
        public const byte SetIcValues = 54, SetIcValuesReply = 55, GetIcValues = 56, GetIcValuesReply = 57;
        public const byte SetIcFocus = 58, UnsetIcFocus = 59, ForwardEvent = 60, Sync = 61, SyncReply = 62;
        public const byte Commit = 63, ResetIc = 64, ResetIcReply = 65;
        public const byte StrConversionReply = 72, PreeditStart = 73, PreeditStartReply = 74, PreeditDraw = 75;
        public const byte PreeditCaretReply = 77, PreeditDone = 78;
    }

    /// <summary>XIM_ERROR 的错误码(「Error Notification」)。</summary>
    private const ushort BadAlloc = 1, BadStyle = 2, BadProtocol = 13;

    /// <summary>XIMStyle 的位(Xlib 第 13.5 节)。</summary>
    internal const uint PreeditCallbacks = 0x0002, PreeditPosition = 0x0004, PreeditNothing = 0x0008, PreeditNone = 0x0010,
        StatusCallbacks = 0x0200, StatusNothing = 0x0400, StatusNone = 0x0800;

    /// <summary>
    /// 支持的输入风格(XNQueryInputStyle),按偏好排:程序自己画预编辑(on-the-spot)、预编辑跟着插入点(over-the-spot,由宿主叠画)、
    /// 根窗口风格(宿主在最后一次点击处叠画)。不支持 Area(off-the-spot:要协商几何,宿主画不进程序的窗口)。
    /// 状态区一律不画:报 StatusCallbacks 的程序只是收不到状态。
    /// </summary>
    internal static readonly uint[] Styles =
    [
        PreeditCallbacks | StatusNothing,
        PreeditCallbacks | StatusCallbacks,
        PreeditPosition | StatusNothing,
        PreeditNothing | StatusNothing,
        PreeditNone | StatusNone,
    ];

    /// <summary>XIMFeedback(「Data Types」):预编辑里的字一律加下划线。</summary>
    private const uint FeedbackUnderline = 0x000002;

    /// <summary>分界长度(X 传输):不超过它的经一条 ClientMessage,超过的经属性。等于一条 ClientMessage 装得下的 20 字节。</summary>
    internal const int DividingSize = 20;

    /// <summary>同时连着的 XIM 连接上限(每个 XOpenIM 一个;真实程序一个显示连一个)。</summary>
    internal const int MaxConnections = 256;

    /// <summary>一条连接上的输入上下文上限(每个输入框一个)。</summary>
    internal const int MaxContexts = 1024;

    /// <summary>多条 ClientMessage 拼一个包时最多攒这么多字节;再多当作坏数据丢掉这条连接(XIM 的包长字段至多 256 KB)。</summary>
    internal const int MaxPacketBytes = 4 + (4 * ushort.MaxValue);

    /// <summary>服务端给通信窗口分的 ID 段(服务端自己的资源段:托盘的嵌入窗口之后)。</summary>
    internal const uint FirstWindowId = 0x120000, LastWindowId = 0x1FFFFF;

    /// <summary>服务端往程序的通信窗口写属性时轮流用的名字个数(一个还没读走就换下一个,免得两包接在一起)。</summary>
    private const int PropertyNames = 64;

    // ------------------------------------------------------------------ 属性表(XIM_OPEN_REPLY 里交给程序的编号)

    private const ushort ImQueryInputStyle = 0;

    /// <summary>
    /// 要特别处理的 IC 属性的编号(<see cref="IcAttributes" /> 的下标)。9–13 与 16(colorMap、stdColorMap、foreground、background、
    /// backgroundPixmap、cursor)只存着、问的时候原样回答,不单列。
    /// </summary>
    private const ushort IcInputStyle = 0, IcClientWindow = 1, IcFocusWindow = 2, IcFilterEvents = 3, IcPreeditAttributes = 4,
        IcStatusAttributes = 5, IcFontSet = 6, IcArea = 7, IcAreaNeeded = 8, IcSpotLocation = 14, IcLineSpace = 15, IcSeparator = 17,
        IcResetState = 18, IcPreeditState = 19;

    /// <summary>值的类型(「Data Types」的类型表)。</summary>
    private const ushort TypeSeparator = 0, TypeLong = 3, TypeWindow = 5, TypeStyles = 10, TypeRectangle = 11, TypePoint = 12,
        TypeFontSet = 13, TypePreeditState = 18, TypeResetState = 19, TypeNested = 0x7FFF;

    /// <summary>IC 属性:编号、名字(Xlib 的 XN* 名)、值类型。编号就是数组下标。</summary>
    private static readonly (string Name, ushort Type)[] IcAttributes =
    [
        ("inputStyle", TypeLong),
        ("clientWindow", TypeWindow),
        ("focusWindow", TypeWindow),
        ("filterEvents", TypeLong),
        ("preeditAttributes", TypeNested),
        ("statusAttributes", TypeNested),
        ("fontSet", TypeFontSet),
        ("area", TypeRectangle),
        ("areaNeeded", TypeRectangle),
        ("colorMap", TypeLong),
        ("stdColorMap", TypeLong),
        ("foreground", TypeLong),
        ("background", TypeLong),
        ("backgroundPixmap", TypeLong),
        ("spotLocation", TypePoint),
        ("lineSpace", TypeLong),
        ("cursor", TypeLong),
        ("separatorofNestedList", TypeSeparator),
        ("resetState", TypeResetState),
        ("preeditState", TypePreeditState),
    ];

    private readonly X11Server _server;
    private readonly Dictionary<XWindow, Connection> _byServerWindow = [];
    private readonly Dictionary<XWindow, Connection> _byClientWindow = [];
    private readonly uint[] _propertyNames = new uint[PropertyNames];
    private uint _nextWindowId = FirstWindowId;
    private int _nextPropertyName;
    private long _focusClock;

    /// <summary>此刻接受宿主输入的输入上下文(见 <see cref="Refresh" />);没有为 null。</summary>
    private InputContext? _active;

    /// <summary>最近一次报给宿主的(相同的不重复报)。</summary>
    private XInputMethodFocus? _reported;

    private readonly uint _xconnect, _protocol, _moreData;

    public XimServer(X11Server server, string name)
    {
        _server = server;
        Name = name;
        SelectionAtom = server.Intern("@server=" + name);
        // 程序问 LOCALES / TRANSPORT 之前先用「只查不建」的 InternAtom 看这两个原子在不在,不在就当没有输入法服务端
        // (真实的 Xlib 就是这样,互操作用例里 xterm 因此一直不连):服务端一开 XIM 就把它们建好。
        server.Intern("LOCALES");
        server.Intern("TRANSPORT");
        _xconnect = server.Intern("_XIM_XCONNECT");
        _protocol = server.Intern("_XIM_PROTOCOL");
        _moreData = server.Intern("_XIM_MOREDATA");
        ServerWindow = NewWindow(X11Server.XimServerWindowId);
    }

    /// <summary>输入法的名字(<c>XMODIFIERS=@im=名字</c>)。</summary>
    public string Name { get; }

    /// <summary>选区 <c>@server=名字</c> 的原子(根窗口 XIM_SERVERS 里列的就是它)。</summary>
    public uint SelectionAtom { get; }

    /// <summary>选区的属主窗口:程序把 _XIM_XCONNECT 发到这里。</summary>
    public XWindow ServerWindow { get; }

    /// <summary>连着的 XIM 连接数(测试看)。</summary>
    internal int ConnectionCount => _byServerWindow.Count;

    /// <summary>这是 XIM 的窗口吗(选区属主窗口或某条连接的服务端通信窗口):发给它的 ClientMessage、写在它上面的属性归这里。</summary>
    public bool OwnsWindow(XWindow window) => ReferenceEquals(window, ServerWindow) || _byServerWindow.ContainsKey(window);

    // ================================================================== 选区(连接之前的约定)

    /// <summary>LOCALES 的回答:<c>@locale=</c> 后面逗号分开的区域名(附录 B)。</summary>
    internal static string Locales { get; } = "@locale=" + string.Join(',', BuildLocales());

    /// <summary>TRANSPORT 的回答:只有 X 传输(附录 B「X Names」)。</summary>
    internal const string Transport = "@transport=X/";

    /// <summary>
    /// 支持的区域:宿主的输入法与程序的区域无关(组好的字按协商的编码交过去),所以尽量都认。程序拿自己的区域名(全名、
    /// 「语言_地区」、语言)逐个比对这个列表,列出全部语言代码与常见的「语言_地区」(带不带 UTF-8 编码名两种写法)就都对得上。
    /// </summary>
    private static IEnumerable<string> BuildLocales()
    {
        yield return "C";
        yield return "POSIX";
        yield return "C.UTF-8";
        string[] territories =
        [
            "zh_CN", "zh_TW", "zh_HK", "zh_SG", "ja_JP", "ko_KR", "en_US", "en_GB", "en_AU", "en_CA", "en_IN", "en_NZ", "en_SG",
            "de_DE", "de_AT", "de_CH", "fr_FR", "fr_CA", "fr_BE", "fr_CH", "es_ES", "es_MX", "es_AR", "it_IT", "pt_BR", "pt_PT",
            "ru_RU", "uk_UA", "pl_PL", "cs_CZ", "sk_SK", "hu_HU", "ro_RO", "bg_BG", "el_GR", "tr_TR", "nl_NL", "nl_BE", "sv_SE",
            "da_DK", "nb_NO", "nn_NO", "fi_FI", "et_EE", "lv_LV", "lt_LT", "hr_HR", "sl_SI", "sr_RS", "vi_VN", "th_TH", "id_ID",
            "ms_MY", "he_IL", "ar_SA", "ar_EG", "fa_IR", "hi_IN", "bn_IN", "ta_IN",
        ];
        foreach (string territory in territories)
        {
            yield return territory;
            yield return territory + ".UTF-8";
            yield return territory + ".utf8";
        }
        // ISO 639-1 的语言代码:区域名对不上上面的「语言_地区」时按语言比对。
        const string languages =
            "aa ab ae af ak am an ar as av ay az ba be bg bh bi bm bn bo br bs ca ce ch co cr cs cu cv cy da de dv dz ee el en eo es et eu " +
            "fa ff fi fj fo fr fy ga gd gl gn gu gv ha he hi ho hr ht hu hy hz ia id ie ig ii ik io is it iu ja jv ka kg ki kj kk kl km kn " +
            "ko kr ks ku kv kw ky la lb lg li ln lo lt lu lv mg mh mi mk ml mn mr ms mt my na nb nd ne ng nl nn no nr nv ny oc oj om or os " +
            "pa pi pl ps pt qu rm rn ro ru rw sa sc sd se sg si sk sl sm sn so sq sr ss st su sv sw ta te tg th ti tk tl tn to tr ts tt tw " +
            "ty ug uk ur uz ve vi vo wa wo xh yi yo za zh zu";
        foreach (string language in languages.Split(' '))
        {
            yield return language;
        }
    }

    // ================================================================== X 传输

    /// <summary>
    /// 发给 XIM 窗口的 ClientMessage(SendEvent):_XIM_XCONNECT 建连接,_XIM_MOREDATA / _XIM_PROTOCOL 送数据。
    /// <paramref name="raw" /> 是发送方字节序的 32 字节事件。不是这几种的照收、不理(返回前已经处理完)。
    /// </summary>
    public void OnClientMessage(XClient sender, XWindow target, byte[] raw, bool bigEndian)
    {
        if ((raw[0] & 0x7F) != XEventCode.ClientMessage)
        {
            return;
        }
        byte format = raw[1];
        uint type = Read32(raw, 8, bigEndian);
        if (ReferenceEquals(target, ServerWindow))
        {
            if (type == _xconnect && format == 32)
            {
                Accept(sender, Read32(raw, 12, bigEndian));
            }
            return;
        }
        if (!_byServerWindow.TryGetValue(target, out Connection? connection) || !ReferenceEquals(connection.Client, sender))
        {
            return;   // 别的客户端冒充这条连接发数据:不理
        }
        if (type == _moreData && format == 8)
        {
            if (connection.Pending.Count + 20 > MaxPacketBytes)
            {
                _server.Log($"xim: connection of client {sender.Index} sent an oversized packet; dropped");
                Drop(connection);
                return;
            }
            connection.Pending.AddRange(raw.AsSpan(12, 20));
        }
        else if (type == _protocol && format == 8)
        {
            connection.Pending.AddRange(raw.AsSpan(12, 20));
            byte[] data = [.. connection.Pending];
            connection.Pending.Clear();
            Receive(connection, data);
        }
        else if (type == _protocol && format == 32)
        {
            // 「属性 + ClientMessage」:程序把包写在我们的通信窗口上(表 D.6);也看一眼它自己的窗口(两份文档的正文是那样写的)。
            int length = (int)Math.Min(Read32(raw, 12, bigEndian), MaxPacketBytes);
            uint property = Read32(raw, 16, bigEndian);
            byte[]? data = _server.TakeServerProperty(connection.ServerWindow, property)
                           ?? _server.TakeServerProperty(connection.ClientWindow, property);
            if (data is not null)
            {
                Receive(connection, data.AsSpan(0, Math.Min(length, data.Length)));
            }
        }
    }

    private static uint Read32(byte[] raw, int offset, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(offset)) : BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offset));

    /// <summary>
    /// _XIM_XCONNECT:为这条连接建一个服务端的通信窗口,回一条 _XIM_XCONNECT(表 D.2:我们的窗口、传输版本 0.2、分界长度)。
    /// </summary>
    private void Accept(XClient client, uint clientWindowId)
    {
        if (_server.Lookup<XWindow>(clientWindowId) is not { } clientWindow || !ReferenceEquals(clientWindow.Owner, client)
            || _byClientWindow.ContainsKey(clientWindow))
        {
            return;   // 只认客户端自己的窗口;同一个窗口不连两次
        }
        if (_byServerWindow.Count >= MaxConnections || AllocateWindowId() is not { } id)
        {
            _server.Log($"xim: too many connections; client {client.Index} refused");
            return;
        }
        Connection connection = new(client, clientWindow, NewWindow(id));
        _byServerWindow[connection.ServerWindow] = connection;
        _byClientWindow[clientWindow] = connection;
        client.Event(XEventCode.ClientMessage, 32, w => w.U32(clientWindow.Id).U32(_xconnect)
            .U32(connection.ServerWindow.Id).U32(0).U32(2).U32(DividingSize).U32(0), sent: true);
        _server.Log($"xim connection from client#{client.Index}");
    }

    private uint? AllocateWindowId()
    {
        for (uint tries = 0; tries <= LastWindowId - FirstWindowId; tries++)
        {
            uint id = _nextWindowId;
            _nextWindowId = id >= LastWindowId ? FirstWindowId : id + 1;
            if (_server.Lookup<VelaShell.XServer.Resources.XResource>(id) is null)
            {
                return id;
            }
        }
        return null;
    }

    /// <summary>服务端自己的窗口(不映射、不挂进窗口树、客户端的 QueryTree 看不到),进资源表。</summary>
    private XWindow NewWindow(uint id)
    {
        XWindow window = new(id, null, _server.Root)
        {
            Class = 2,   // InputOnly
            Width = 1,
            Height = 1,
            Visual = X11Server.RootVisualId,
        };
        _server.AddServerWindow(window);
        return window;
    }

    /// <summary>
    /// 发一个包给程序:不超过 <see cref="DividingSize" /> 字节的经一条 _XIM_PROTOCOL(格式 8,不足 20 字节补 0);更长的写成程序通信窗口上的
    /// 一个属性(类型 STRING、格式 8),再发一条格式 32 的 _XIM_PROTOCOL 告诉它长度与属性名(程序读的时候删掉)。
    /// </summary>
    private void Send(Connection connection, byte opcode, Packet body)
    {
        if (connection.Closed || connection.Client.Closed)
        {
            return;
        }
        byte[] packet = body.Finish(opcode);
        XWindow window = connection.ClientWindow;
        if (packet.Length <= DividingSize)
        {
            connection.Client.Event(XEventCode.ClientMessage, 8, w =>
            {
                w.U32(window.Id).U32(_protocol).Bytes(packet);
                w.Zero(20 - packet.Length);
            }, sent: true);
            return;
        }
        uint property = PropertyName(window);
        _server.PutServerProperty(window, property, XAtom.String, packet);
        connection.Client.Event(XEventCode.ClientMessage, 32, w => w.U32(window.Id).U32(_protocol)
            .U32((uint)packet.Length).U32(property).U32(0).U32(0).U32(0), sent: true);
    }

    /// <summary>挑一个程序窗口上眼下没有的属性名(上一包还没读走时换下一个)。</summary>
    private uint PropertyName(XWindow window)
    {
        for (int i = 0; i < PropertyNames; i++)
        {
            int index = (_nextPropertyName + i) % PropertyNames;
            uint atom = _propertyNames[index];
            if (atom == 0)
            {
                atom = _propertyNames[index] = _server.Intern($"_VELASHELL_XIM_{index}");
            }
            if (!window.Properties.ContainsKey(atom))
            {
                _nextPropertyName = (index + 1) % PropertyNames;
                return atom;
            }
        }
        // 64 包都没读走:程序多半卡住了,覆盖最旧的那个。
        int oldest = _nextPropertyName;
        _nextPropertyName = (oldest + 1) % PropertyNames;
        return _propertyNames[oldest];
    }

    /// <summary>一段收到的数据里的包(通常一个;不足 20 字节的 ClientMessage 后面补的是 0,遇到主操作码 0 就停)。</summary>
    private void Receive(Connection connection, ReadOnlySpan<byte> data)
    {
        int offset = 0;
        while (!connection.Closed && data.Length - offset >= 4 && data[offset] != 0)
        {
            if (!connection.Connected && data[offset] == Op.Connect && data.Length - offset >= 5)
            {
                connection.BigEndian = data[offset + 4] == 0x42;   // 「byte order」:#x42 MSB first、#x6c LSB first
            }
            int length = 4 + (4 * (connection.BigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..])
                : BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 2)..])));
            if (length > data.Length - offset)
            {
                _server.Log($"xim: truncated packet (opcode {data[offset]}) from client {connection.Client.Index}");
                return;
            }
            byte major = data[offset];   // 次操作码:核心请求都不用(协议:不用时为 0)
            Reader body = new(data.Slice(offset + 4, length - 4).ToArray(), connection.BigEndian);
            offset += length;
            connection.LastOpcode = major;
            try
            {
                Handle(connection, major, body);
            }
            catch (ArgumentOutOfRangeException)
            {
                // 包里的长度字段对不上(读过了头):回 BadProtocol,接着收后面的。
                SendError(connection, null, null, BadProtocol);
            }
        }
    }

    // ================================================================== 包的处理

    private void Handle(Connection connection, byte major, Reader body)
    {
        if (!connection.Connected && major != Op.Connect)
        {
            SendError(connection, null, null, BadProtocol);   // 第一个包必须是 XIM_CONNECT
            return;
        }
        switch (major)
        {
            case Op.Connect:
                connection.Connected = true;
                // 不做认证(client-auth-protocol-names 一律不理):X 传输上的连接已经过了 X 自己的授权。
                Send(connection, Op.ConnectReply, new Packet(connection.BigEndian).U16(1).U16(0));
                break;
            case Op.Disconnect:
                foreach (InputMethod im in connection.Methods.Values.ToArray())
                {
                    CloseMethod(connection, im);
                }
                Send(connection, Op.DisconnectReply, new Packet(connection.BigEndian));
                connection.Connected = false;
                break;
            case Op.AuthRequired or Op.AuthNg:
                Send(connection, Op.AuthNg, new Packet(connection.BigEndian));
                break;
            case Op.Open:
                OpenMethod(connection, body);
                break;
            case Op.Close:
                if (Method(connection, body.U16()) is { } closing)
                {
                    CloseMethod(connection, closing);
                    Send(connection, Op.CloseReply, new Packet(connection.BigEndian).U16(closing.Id).U16(0));
                }
                break;
            case Op.EncodingNegotiation:
                NegotiateEncoding(connection, body);
                break;
            case Op.QueryExtension:
                if (Method(connection, body.U16()) is { } queried)
                {
                    Send(connection, Op.QueryExtensionReply, new Packet(connection.BigEndian).U16(queried.Id).U16(0));   // 没有扩展
                }
                break;
            case Op.GetImValues:
                GetImValues(connection, body);
                break;
            case Op.SetImValues:
                if (Method(connection, body.U16()) is { } set)
                {
                    Send(connection, Op.SetImValuesReply, new Packet(connection.BigEndian).U16(set.Id).U16(0));   // 没有可设的 IM 属性
                }
                break;
            case Op.CreateIc:
                CreateContext(connection, body);
                break;
            case Op.DestroyIc:
                DestroyContext(connection, body);
                break;
            case Op.SetIcValues:
                SetContextValues(connection, body);
                break;
            case Op.GetIcValues:
                GetContextValues(connection, body);
                break;
            case Op.SetIcFocus or Op.UnsetIcFocus:
                if (Context(connection, body) is { } focused)
                {
                    focused.Focused = major == Op.SetIcFocus;
                    focused.FocusSerial = ++_focusClock;
                    Refresh();
                }
                break;
            case Op.ForwardEvent:
                ForwardBack(connection, body);
                break;
            case Op.Sync:
                {
                    ushort im = body.U16(), ic = body.U16();
                    Send(connection, Op.SyncReply, new Packet(connection.BigEndian).U16(im).U16(ic));
                    break;
                }
            case Op.ResetIc:
                if (Context(connection, body) is { } reset)
                {
                    EndPreedit(reset);
                    // 没有交出去的预编辑:宿主的输入法自己还在组字,这里只把程序那边的显示收掉。
                    Send(connection, Op.ResetIcReply, new Packet(connection.BigEndian).U16(reset.Method.Id).U16(reset.Id).U16(0).Pad4());
                }
                break;
            case Op.TriggerNotify:
                {
                    ushort im = body.U16(), ic = body.U16();
                    Send(connection, Op.TriggerNotifyReply, new Packet(connection.BigEndian).U16(im).U16(ic));
                    break;
                }
            case Op.SyncReply or Op.PreeditStartReply or Op.PreeditCaretReply or Op.StrConversionReply:
                break;   // 程序对我们的回调的回答:不需要
            case Op.Error:
                _server.Log($"xim: client {connection.Client.Index} reported error {(body.Length >= 8 ? body.Peek16(6) : 0)}");
                break;
            default:
                SendError(connection, null, null, BadProtocol);
                break;
        }
    }

    /// <summary>XIM_OPEN:记下区域,回全部 IM / IC 属性(编号是这里分的,之后的请求按编号指名)。</summary>
    private void OpenMethod(Connection connection, Reader body)
    {
        string locale = body.Str();
        if (connection.Methods.Count >= MaxConnections || NextId(connection.Methods.Keys, connection.NextMethod) is not { } id)
        {
            SendError(connection, null, null, BadAlloc);
            return;
        }
        connection.NextMethod = id;
        InputMethod im = new(id, locale);
        connection.Methods[id] = im;
        _server.Log($"xim client#{connection.Client.Index} opened the input method (locale {locale})");

        Packet imAttributes = new(connection.BigEndian);
        Attr(imAttributes, ImQueryInputStyle, TypeStyles, "queryInputStyle");
        Packet icAttributes = new(connection.BigEndian);
        for (int i = 0; i < IcAttributes.Length; i++)
        {
            Attr(icAttributes, (ushort)i, IcAttributes[i].Type, IcAttributes[i].Name);
        }
        Packet reply = new Packet(connection.BigEndian).U16(id).U16((ushort)imAttributes.Length).Append(imAttributes)
            .U16((ushort)icAttributes.Length).U16(0).Append(icAttributes);
        Send(connection, Op.OpenReply, reply);

        static void Attr(Packet list, ushort id, ushort type, string name)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(name);
            list.U16(id).U16(type).U16((ushort)bytes.Length).Bytes(bytes).PadFrom(2 + bytes.Length);   // p = Pad(2+n)
        }
    }

    private void CloseMethod(Connection connection, InputMethod im)
    {
        foreach (InputContext ic in im.Contexts.Values.ToArray())
        {
            Forget(ic);
        }
        connection.Methods.Remove(im.Id);
        Refresh();
    }

    /// <summary>
    /// XIM_ENCODING_NEGOTIATION:按名字挑一个。COMPOUND_TEXT 是 Xlib 的默认、程序都认(服务端的编码器把 Latin-1 之外的字放进 UTF-8 段);
    /// 没有它时挑 UTF-8;都没有回 -1(按协议退回可移植字符编码,我们照旧按 COMPOUND_TEXT 编)。
    /// </summary>
    private void NegotiateEncoding(Connection connection, Reader body)
    {
        if (Method(connection, body.U16()) is not { } im)
        {
            return;
        }
        int length = body.U16();
        int end = body.Position + length;
        List<string> names = [];
        while (body.Position < end)
        {
            names.Add(body.Str());
        }
        int index = names.IndexOf("COMPOUND_TEXT");
        im.Utf8 = false;
        if (index < 0 && names.IndexOf("UTF-8") is var utf8 && utf8 >= 0)
        {
            index = utf8;
            im.Utf8 = true;
        }
        Send(connection, Op.EncodingNegotiationReply, new Packet(connection.BigEndian).U16(im.Id).U16(0).U16(unchecked((ushort)(short)index)).U16(0));
    }

    /// <summary>XIM_GET_IM_VALUES:只有 queryInputStyle。</summary>
    private void GetImValues(Connection connection, Reader body)
    {
        if (Method(connection, body.U16()) is not { } im)
        {
            return;
        }
        int count = body.U16() / 2;
        Packet values = new(connection.BigEndian);
        for (int i = 0; i < count; i++)
        {
            if (body.U16() == ImQueryInputStyle)
            {
                Packet styles = new Packet(connection.BigEndian).U16((ushort)Styles.Length).U16(0);
                foreach (uint style in Styles)
                {
                    styles.U32(style);
                }
                Value(values, ImQueryInputStyle, styles);
            }
        }
        Send(connection, Op.GetImValuesReply, new Packet(connection.BigEndian).U16(im.Id).U16((ushort)values.Length).Append(values));
    }

    /// <summary>XIMATTRIBUTE / XICATTRIBUTE:编号、值长、值、补齐。</summary>
    private static void Value(Packet list, ushort id, Packet value) =>
        list.U16(id).U16((ushort)value.Length).Append(value).PadFrom(value.Length);

    private void CreateContext(Connection connection, Reader body)
    {
        if (Method(connection, body.U16()) is not { } im)
        {
            return;
        }
        int length = body.U16();
        if (connection.Methods.Values.Sum(m => m.Contexts.Count) >= MaxContexts || NextId(im.Contexts.Keys, im.NextContext) is not { } id)
        {
            SendError(connection, im, null, BadAlloc);
            return;
        }
        InputContext ic = new(connection, im, id);
        ReadAttributes(ic, body, body.Position + length);
        if (!Styles.Contains(ic.Style))
        {
            _server.Log($"xim: client#{connection.Client.Index} asked for unsupported input style 0x{ic.Style:x4}");
            SendError(connection, im, null, BadStyle);
            return;
        }
        im.NextContext = id;
        im.Contexts[id] = ic;
        _server.Log($"xim client#{connection.Client.Index} created input context {id} (style 0x{ic.Style:x4})");
        Send(connection, Op.CreateIcReply, new Packet(connection.BigEndian).U16(im.Id).U16(id));
        // 不要程序把任何事件经 XIM 转过来(转发掩码与同步掩码都是 0):按键由程序自己按键位表解释,组字在宿主那边。
        Send(connection, Op.SetEventMask, new Packet(connection.BigEndian).U16(im.Id).U16(id).U32(0).U32(0));
        Refresh();
    }

    private void DestroyContext(Connection connection, Reader body)
    {
        if (Context(connection, body) is not { } ic)
        {
            return;
        }
        EndPreedit(ic);   // 协议:销毁之前可以先发预编辑的回调
        Forget(ic);
        Send(connection, Op.DestroyIcReply, new Packet(connection.BigEndian).U16(ic.Method.Id).U16(ic.Id));
        Refresh();
    }

    private void SetContextValues(Connection connection, Reader body)
    {
        if (Context(connection, body) is not { } ic)
        {
            return;
        }
        int length = body.U16();
        body.Skip(2);
        uint style = ic.Style;
        ReadAttributes(ic, body, body.Position + length);
        ic.Style = style;   // 输入风格只能在建的时候定(Xlib:XNInputStyle 只许在 XCreateIC 里给)
        Send(connection, Op.SetIcValuesReply, new Packet(connection.BigEndian).U16(ic.Method.Id).U16(ic.Id));
        Refresh();
    }

    /// <summary>读一串 XICATTRIBUTE(到 <paramref name="end" /> 为止);嵌套的 preeditAttributes / statusAttributes 读进去,遇到分隔符就结束那一层。</summary>
    private void ReadAttributes(InputContext ic, Reader body, int end)
    {
        while (body.Position + 4 <= end)
        {
            ushort id = body.U16();
            int length = body.U16();
            int start = body.Position;
            if (id == IcSeparator)
            {
                body.Seek(start + length + Packet.Pad(length));
                return;
            }
            switch (id)
            {
                case IcInputStyle:
                    ic.Style = body.U32();
                    break;
                case IcClientWindow:
                    ic.ClientWindow = _server.Lookup<XWindow>(body.U32());
                    break;
                case IcFocusWindow:
                    ic.FocusWindow = _server.Lookup<XWindow>(body.U32());
                    break;
                case IcPreeditAttributes or IcStatusAttributes:
                    ReadAttributes(ic, body, start + length);
                    break;
                case IcSpotLocation:
                    ic.Spot = (body.I16(), body.I16());
                    break;
                case IcArea:
                    ic.Area = new XRect(body.I16(), body.I16(), body.U16(), body.U16());
                    break;
                case IcLineSpace:
                    ic.LineSpace = (int)body.U32();
                    break;
                case IcFontSet:
                    ic.FontSet = body.Bytes(body.U16());
                    break;
                default:
                    if (length == 4 && id < IcAttributes.Length && IcAttributes[id].Type == TypeLong)
                    {
                        ic.Values[id] = body.U32();   // 颜色、光标这些:存着,问的时候原样回答
                    }
                    break;
            }
            body.Seek(start + length + Packet.Pad(length));
        }
    }

    /// <summary>XIM_GET_IC_VALUES:按问的顺序回答;嵌套的那一层到分隔符为止,回成一个嵌套属性。</summary>
    private void GetContextValues(Connection connection, Reader body)
    {
        if (Context(connection, body) is not { } ic)
        {
            return;
        }
        int count = body.U16() / 2;
        List<ushort> ids = [];
        for (int i = 0; i < count; i++)
        {
            ids.Add(body.U16());
        }
        Packet values = new(connection.BigEndian);
        for (int i = 0; i < ids.Count; i++)
        {
            ushort id = ids[i];
            if (id is IcPreeditAttributes or IcStatusAttributes)
            {
                Packet nested = new(connection.BigEndian);
                for (i++; i < ids.Count && ids[i] != IcSeparator; i++)
                {
                    WriteValue(nested, ic, ids[i]);
                }
                Value(values, id, nested);
                continue;
            }
            WriteValue(values, ic, id);
        }
        Send(connection, Op.GetIcValuesReply, new Packet(connection.BigEndian).U16(ic.Method.Id).U16(ic.Id).U16((ushort)values.Length).U16(0).Append(values));
    }

    private static void WriteValue(Packet list, InputContext ic, ushort id)
    {
        Packet value = new(list.BigEndian);
        switch (id)
        {
            case IcInputStyle:
                value.U32(ic.Style);
                break;
            case IcClientWindow:
                value.U32(ic.ClientWindow?.Id ?? 0);
                break;
            case IcFocusWindow:
                value.U32(ic.FocusWindow?.Id ?? 0);
                break;
            case IcFilterEvents:
                value.U32((uint)XEventMask.KeyPress);   // 程序照常选按键就够了:我们不要它转任何事件
                break;
            case IcSpotLocation:
                value.U16(unchecked((ushort)(ic.Spot?.X ?? 0))).U16(unchecked((ushort)(ic.Spot?.Y ?? 0)));
                break;
            case IcArea or IcAreaNeeded:
                XRect area = ic.Area ?? default;
                value.U16(unchecked((ushort)area.X)).U16(unchecked((ushort)area.Y)).U16((ushort)area.Width).U16((ushort)area.Height);
                break;
            case IcLineSpace:
                value.U32((uint)ic.LineSpace);
                break;
            case IcFontSet:
                value.U16((ushort)ic.FontSet.Length).Bytes(ic.FontSet).PadFrom(2 + ic.FontSet.Length);
                break;
            case IcPreeditState or IcResetState:
                value.U32(1);   // XIMPreeditEnable / XIMInitialState
                break;
            default:
                if (id >= IcAttributes.Length || IcAttributes[id].Type != TypeLong)
                {
                    return;   // 分隔符或不认识的编号:不回
                }
                value.U32(ic.Values.GetValueOrDefault(id));   // 颜色、光标这些:原样回答程序设过的
                break;
        }
        Value(list, id, value);
    }

    /// <summary>
    /// 程序转过来的事件(我们把转发掩码设成了 0,正常不会有;Xlib 在掩码到达之前转了的):不过滤,原样转回去让它自己处理,
    /// 要求同步的再回 XIM_SYNC_REPLY。
    /// </summary>
    private void ForwardBack(Connection connection, Reader body)
    {
        ushort im = body.U16(), ic = body.U16(), flag = body.U16(), serial = body.U16();
        byte[] xEvent = body.Bytes(32);
        Send(connection, Op.ForwardEvent, new Packet(connection.BigEndian).U16(im).U16(ic).U16(0).U16(serial).Bytes(xEvent));
        if ((flag & 1) != 0)
        {
            Send(connection, Op.SyncReply, new Packet(connection.BigEndian).U16(im).U16(ic));
        }
    }

    private void SendError(Connection connection, InputMethod? im, InputContext? ic, ushort code)
    {
        _server.Log($"xim: client#{connection.Client.Index} gets XIM_ERROR {code} (last packet: opcode {connection.LastOpcode})");
        ushort flag = (ushort)((im is not null ? 1 : 0) | (ic is not null ? 2 : 0));
        Send(connection, Op.Error, new Packet(connection.BigEndian).U16(im?.Id ?? 0).U16(ic?.Id ?? 0).U16(flag).U16(code).U16(0).U16(0));
    }

    private InputMethod? Method(Connection connection, ushort id)
    {
        if (connection.Methods.TryGetValue(id, out InputMethod? im))
        {
            return im;
        }
        SendError(connection, null, null, BadProtocol);
        return null;
    }

    /// <summary>包头之后的「input-method-ID、input-context-ID」指的输入上下文;不存在时回错误、返回 null。</summary>
    private InputContext? Context(Connection connection, Reader body)
    {
        if (Method(connection, body.U16()) is not { } im)
        {
            return null;
        }
        if (im.Contexts.TryGetValue(body.U16(), out InputContext? ic))
        {
            return ic;
        }
        SendError(connection, im, null, BadProtocol);
        return null;
    }

    private static ushort? NextId(IEnumerable<ushort> used, ushort after)
    {
        HashSet<ushort> taken = [.. used];
        ushort id = after;
        for (int i = 0; i < ushort.MaxValue; i++)
        {
            id = id == ushort.MaxValue ? (ushort)1 : (ushort)(id + 1);
            if (!taken.Contains(id))
            {
                return id;
            }
        }
        return null;
    }

    private void Forget(InputContext ic)
    {
        ic.Method.Contexts.Remove(ic.Id);
        if (ReferenceEquals(_active, ic))
        {
            _active = null;
        }
    }

    /// <summary>收掉一条连接(程序的通信窗口销毁了、程序断开了、数据坏了)。</summary>
    private void Drop(Connection connection)
    {
        connection.Closed = true;
        foreach (InputMethod im in connection.Methods.Values)
        {
            foreach (InputContext ic in im.Contexts.Values)
            {
                if (ReferenceEquals(_active, ic))
                {
                    _active = null;
                }
            }
        }
        connection.Methods.Clear();
        _byServerWindow.Remove(connection.ServerWindow);
        _byClientWindow.Remove(connection.ClientWindow);
        _server.RemoveServerWindow(connection.ServerWindow);
        Refresh();
    }

    /// <summary>
    /// 一个窗口销毁了:是某条连接的通信窗口就收掉那条连接(程序 XCloseIM 或退出时就是这样);是输入上下文的焦点 / 客户窗口就忘掉它。
    /// </summary>
    public void WindowDestroyed(XWindow window)
    {
        if (_byClientWindow.TryGetValue(window, out Connection? connection))
        {
            Drop(connection);
            return;
        }
        bool changed = false;
        foreach (Connection c in _byServerWindow.Values)
        {
            foreach (InputMethod im in c.Methods.Values)
            {
                foreach (InputContext ic in im.Contexts.Values)
                {
                    if (ReferenceEquals(ic.FocusWindow, window))
                    {
                        ic.FocusWindow = null;
                        changed = true;
                    }
                    if (ReferenceEquals(ic.ClientWindow, window))
                    {
                        ic.ClientWindow = null;
                        changed = true;
                    }
                }
            }
        }
        if (changed)
        {
            Refresh();
        }
    }

    // ================================================================== 焦点、上屏与预编辑

    /// <summary>
    /// 重新挑接受宿主输入的输入上下文:键盘焦点所在的顶层里、程序报了焦点(XIM_SET_IC_FOCUS)的那个(有几个时取最后报的)。
    /// 换了就把旧的那个的预编辑收掉;报给宿主的有变化就报(<see cref="IX11ServerHost.InputMethodFocusChanged" />)。
    /// X 的焦点变了、程序报 / 撤焦点、改了插入点、建 / 销毁输入上下文时调。
    /// </summary>
    public void Refresh()
    {
        InputContext? best = null;
        if (_server.KeyboardFocusTopLevel() is { } focusTop)
        {
            foreach (Connection connection in _byServerWindow.Values)
            {
                foreach (InputMethod im in connection.Methods.Values)
                {
                    foreach (InputContext ic in im.Contexts.Values)
                    {
                        if (ic.Focused && (ic.FocusWindow ?? ic.ClientWindow)?.TopLevel is { } top && ReferenceEquals(top, focusTop)
                            && (best is null || ic.FocusSerial > best.FocusSerial))
                        {
                            best = ic;
                        }
                    }
                }
            }
        }
        if (!ReferenceEquals(best, _active))
        {
            if (_active is { } previous)
            {
                EndPreedit(previous);
            }
            _active = best;
        }
        XInputMethodFocus? focus = best is null ? null : Describe(best);
        if (focus != _reported)
        {
            _reported = focus;
            _server.ReportInputMethodFocus(focus);
        }
    }

    /// <summary>此刻有接受宿主输入的输入上下文(测试看)。</summary>
    internal bool HasActiveContext => _active is not null;

    /// <summary>报给宿主的样子:所在的顶层、程序画不画预编辑、插入点(焦点窗口坐标里的基线位置换成顶层内区坐标里的一个竖条)。</summary>
    private XInputMethodFocus? Describe(InputContext ic)
    {
        if ((ic.FocusWindow ?? ic.ClientWindow) is not { } window || _server.LocateInTopLevel(window) is not { } at)
        {
            return null;
        }
        XRect? cursor = null;
        if (ic.Spot is { } spot)
        {
            // XNSpotLocation 是预编辑第一个字的基线起点。行高:程序给了 XNLineSpace 用它,否则按 DPI 估一行(96 dpi 时 16 像素)。
            int line = ic.LineSpace is > 0 and < 1024 ? ic.LineSpace : Math.Max(12, _server.ScreenDpi / 6);
            int ascent = line * 4 / 5;
            cursor = new XRect(at.X + spot.X, at.Y + spot.Y - ascent, 1, line);
        }
        return new XInputMethodFocus(at.Handle, (ic.Style & PreeditCallbacks) != 0, cursor);
    }

    /// <summary>
    /// 宿主的输入法上屏了一段字:交给接受输入的输入上下文(XIM_COMMIT,XLookupChars,按协商的编码)。没有接受输入的输入上下文时返回 false
    /// (调用方退回借键码输入)。<paramref name="text" /> 里不该有控制字符(调用方把换行、制表拆出去按键)。
    /// </summary>
    public bool Commit(string text)
    {
        if (_active is not { } ic || ic.Connection.Closed)
        {
            return false;
        }
        EndPreedit(ic);   // 先收掉程序那边的预编辑,再插入组好的字
        byte[] bytes = Encode(ic.Method, text);
        if (bytes.Length is 0 or > ushort.MaxValue)
        {
            return true;
        }
        Send(ic.Connection, Op.Commit, new Packet(ic.Connection.BigEndian).U16(ic.Method.Id).U16(ic.Id).U16(0x0002)   // XLookupChars,不要求同步
            .U16((ushort)bytes.Length).Bytes(bytes).PadFrom(bytes.Length));
        return true;
    }

    /// <summary>
    /// 宿主的预编辑变了:接受输入的输入上下文是 on-the-spot(XIMPreeditCallbacks)时经 XIM_PREEDIT_START / DRAW / DONE 交给程序画;
    /// 别的风格不交(宿主自己叠画)。<paramref name="text" /> 为空是组字结束或取消;<paramref name="caret" /> 是 UTF-16 下标。
    /// </summary>
    public void Preedit(string text, int caret)
    {
        if (_active is not { } ic || ic.Connection.Closed || (ic.Style & PreeditCallbacks) == 0)
        {
            return;
        }
        if (text.Length == 0)
        {
            EndPreedit(ic);
            return;
        }
        Connection connection = ic.Connection;
        if (!ic.PreeditShown)
        {
            Send(connection, Op.PreeditStart, new Packet(connection.BigEndian).U16(ic.Method.Id).U16(ic.Id));
            ic.PreeditShown = true;
            ic.PreeditLength = 0;
        }
        byte[] bytes = Encode(ic.Method, text);
        int characters = CountCharacters(text, text.Length);
        int caretCharacters = CountCharacters(text, Math.Clamp(caret, 0, text.Length));
        Packet draw = new Packet(connection.BigEndian).U16(ic.Method.Id).U16(ic.Id)
            .I32(caretCharacters).I32(0).I32(ic.PreeditLength).U32(0)
            .U16((ushort)Math.Min(bytes.Length, ushort.MaxValue)).Bytes(bytes.AsSpan(0, Math.Min(bytes.Length, ushort.MaxValue)))
            .PadFrom(2 + Math.Min(bytes.Length, ushort.MaxValue))
            .U16((ushort)Math.Min(characters * 4, ushort.MaxValue - 3)).U16(0);
        for (int i = 0; i < characters && i < (ushort.MaxValue - 3) / 4; i++)
        {
            draw.U32(FeedbackUnderline);
        }
        Send(connection, Op.PreeditDraw, draw);
        ic.PreeditLength = characters;
    }

    /// <summary>程序那边显示着预编辑时把它擦掉(DRAW:整段删去、没有新字)并结束(DONE)。</summary>
    private void EndPreedit(InputContext ic)
    {
        if (!ic.PreeditShown)
        {
            return;
        }
        ic.PreeditShown = false;
        Connection connection = ic.Connection;
        Send(connection, Op.PreeditDraw, new Packet(connection.BigEndian).U16(ic.Method.Id).U16(ic.Id)
            .I32(0).I32(0).I32(ic.PreeditLength).U32(0x3)   // no string | no feedback
            .U16(0).U16(0).U16(0).U16(0));
        Send(connection, Op.PreeditDone, new Packet(connection.BigEndian).U16(ic.Method.Id).U16(ic.Id));
        ic.PreeditLength = 0;
    }

    /// <summary>字数(XIMText 的长度按字算,不按字节):按 Unicode 码位数。</summary>
    private static int CountCharacters(string text, int end)
    {
        int count = 0;
        for (int i = 0; i < end; i++)
        {
            if (!char.IsLowSurrogate(text[i]) || i == 0 || !char.IsHighSurrogate(text[i - 1]))
            {
                count++;
            }
        }
        return count;
    }

    private static byte[] Encode(InputMethod im, string text) =>
        im.Utf8 ? Encoding.UTF8.GetBytes(text) : XText.EncodeCompoundText(text);

    // ================================================================== 状态

    private sealed class Connection(XClient client, XWindow clientWindow, XWindow serverWindow)
    {
        public XClient Client { get; } = client;

        /// <summary>程序的通信窗口(我们的包发到这里、长包写成它上面的属性)。</summary>
        public XWindow ClientWindow { get; } = clientWindow;

        /// <summary>这条连接的服务端通信窗口(程序的包发到这里)。</summary>
        public XWindow ServerWindow { get; } = serverWindow;

        /// <summary>XIM 包的字节序(XIM_CONNECT 的 byte order)。</summary>
        public bool BigEndian { get; set; }

        public bool Connected { get; set; }

        public bool Closed { get; set; }

        /// <summary>_XIM_MOREDATA 攒着的前几段。</summary>
        public List<byte> Pending { get; } = [];

        public Dictionary<ushort, InputMethod> Methods { get; } = [];

        public ushort NextMethod { get; set; }

        /// <summary>最后收到的包的主操作码(诊断日志用)。</summary>
        public byte LastOpcode { get; set; }
    }

    private sealed class InputMethod(ushort id, string locale)
    {
        public ushort Id { get; } = id;

        public string Locale { get; } = locale;

        /// <summary>协商成了 UTF-8(否则按 COMPOUND_TEXT 编)。</summary>
        public bool Utf8 { get; set; }

        public Dictionary<ushort, InputContext> Contexts { get; } = [];

        public ushort NextContext { get; set; }
    }

    private sealed class InputContext(Connection connection, InputMethod method, ushort id)
    {
        public Connection Connection { get; } = connection;

        public InputMethod Method { get; } = method;

        public ushort Id { get; } = id;

        public uint Style { get; set; }

        public XWindow? ClientWindow { get; set; }

        public XWindow? FocusWindow { get; set; }

        /// <summary>XNSpotLocation(焦点窗口坐标);程序没给为 null。</summary>
        public (short X, short Y)? Spot { get; set; }

        public XRect? Area { get; set; }

        public int LineSpace { get; set; }

        public byte[] FontSet { get; set; } = [];

        /// <summary>只存着、问的时候原样回答的那些(颜色、光标、背景图)。</summary>
        public Dictionary<ushort, uint> Values { get; } = [];

        public bool Focused { get; set; }

        /// <summary>最后一次报焦点的先后(同一个顶层里几个都报了焦点时取最后的)。</summary>
        public long FocusSerial { get; set; }

        /// <summary>程序那边正显示着预编辑(发过 PREEDIT_START、还没 DONE)。</summary>
        public bool PreeditShown { get; set; }

        /// <summary>程序那边预编辑的字数(下一次 DRAW 要替换掉的长度)。</summary>
        public int PreeditLength { get; set; }
    }

    // ================================================================== 线上编码

    /// <summary>拼一个包的正文(按连接的字节序);<see cref="Finish" /> 补上包头。</summary>
    private sealed class Packet(bool bigEndian)
    {
        private readonly List<byte> _bytes = [];

        public bool BigEndian { get; } = bigEndian;

        public int Length => _bytes.Count;

        public static int Pad(int n) => (4 - (n % 4)) % 4;

        public Packet U8(byte value)
        {
            _bytes.Add(value);
            return this;
        }

        public Packet U16(ushort value)
        {
            Span<byte> b = stackalloc byte[2];
            if (BigEndian)
            {
                BinaryPrimitives.WriteUInt16BigEndian(b, value);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(b, value);
            }
            _bytes.AddRange(b);
            return this;
        }

        public Packet U32(uint value)
        {
            Span<byte> b = stackalloc byte[4];
            if (BigEndian)
            {
                BinaryPrimitives.WriteUInt32BigEndian(b, value);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(b, value);
            }
            _bytes.AddRange(b);
            return this;
        }

        public Packet I32(int value) => U32(unchecked((uint)value));

        public Packet Bytes(ReadOnlySpan<byte> bytes)
        {
            _bytes.AddRange(bytes);
            return this;
        }

        public Packet Append(Packet other)
        {
            _bytes.AddRange(other._bytes);
            return this;
        }

        /// <summary>补 Pad(<paramref name="n" />) 个 0(协议里各处的 p = Pad(…))。</summary>
        public Packet PadFrom(int n)
        {
            for (int i = Pad(n); i > 0; i--)
            {
                _bytes.Add(0);
            }
            return this;
        }

        public Packet Pad4() => PadFrom(_bytes.Count);

        /// <summary>包头(主操作码、次操作码 0、正文长度 / 4)+ 正文。</summary>
        public byte[] Finish(byte major)
        {
            Pad4();
            byte[] packet = new byte[4 + _bytes.Count];
            packet[0] = major;
            if (BigEndian)
            {
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)(_bytes.Count / 4));
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)(_bytes.Count / 4));
            }
            _bytes.CopyTo(packet, 4);
            return packet;
        }
    }

    /// <summary>读一个包的正文(按连接的字节序)。读过头抛 <see cref="ArgumentOutOfRangeException" />。</summary>
    private sealed class Reader(byte[] data, bool bigEndian)
    {
        public int Position { get; private set; }

        public int Length => data.Length;

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || Position + count > data.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            ReadOnlySpan<byte> span = data.AsSpan(Position, count);
            Position += count;
            return span;
        }

        public ushort U16() => bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(Take(2)) : BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

        public short I16() => unchecked((short)U16());

        public uint U32() => bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(Take(4)) : BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

        public byte[] Bytes(int count) => Take(count).ToArray();

        public void Skip(int count) => Take(count);

        public void Seek(int position) => Position = Math.Clamp(position, 0, data.Length);

        public ushort Peek16(int offset) => bigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));

        /// <summary>STR:一字节长度、名字;在 XIM_OPEN 里后面还补到 4 字节(调用方不用管:之后不再读)。</summary>
        public string Str()
        {
            int length = Take(1)[0];
            return Encoding.Latin1.GetString(Take(length));
        }
    }
}
