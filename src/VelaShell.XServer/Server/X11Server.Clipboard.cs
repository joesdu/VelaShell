// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   Inter-Client Communication Conventions Manual (ICCCM) 2.0 —— §2.2「Responsibilities of the Selection Owner」
//   (回应 SelectionRequest:写属性再发 SelectionNotify;property 为 None 的旧式请求用 target 当属性名)、
//   §2.4「Requesting a Selection」(作为请求方:ConvertSelection、读属性、删属性)、
//   §2.5「Large Data Transfers」(INCR 分块协议)、§2.6.2「Target Atoms」(TARGETS、TIMESTAMP、TEXT、STRING)、
//   §2.7.1「Text Properties」(TEXT 由属主挑 STRING / UTF8_STRING / COMPOUND_TEXT,取回的按类型解码)
//   X Window System Protocol —— 「SetSelectionOwner」「ConvertSelection」及 SelectionRequest / SelectionNotify 事件
//   freedesktop.org Clipboard Manager Specification —— CLIPBOARD_MANAGER 选区、SAVE_TARGETS(内容取完之前不回答)
//   剪贴板的富格式按 MIME 类型当目标(text/html、image/png;freedesktop 的惯例,ICCCM §2.6.2 允许属主自定目标)
//
//   与宿主的剪贴板互通:
//   · 宿主 → X:SetClipboardText 让服务端自己占有 CLIPBOARD(可选 PRIMARY),X 客户端来要时直接回;
//   · X → 宿主:X 客户端占有 CLIPBOARD(可选 PRIMARY)时,服务端以一个隐藏的 InputOnly 窗口为请求方
//     把内容要过来(支持 INCR),交给宿主的 ClipboardChanged。
//   · 开着 ClipboardFollowsFocus 时 PRIMARY / SECONDARY / CLIPBOARD 按会话隔离(每个会话各有各的属主);
//     两个方向都只与键盘焦点所在的会话来往,跨会话的复制粘贴经宿主的剪贴板中转。

using System.Buffers.Binary;
using System.Text;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    /// <summary>服务端作为选区请求方 / 属主时用的窗口(不映射、不挂进窗口树,客户端的 QueryTree 看不到)。</summary>
    private const uint SelectionWindowId = 0x43;

    private XWindow? _selectionWindow;

    /// <summary>
    /// 最新的剪贴板内容(宿主给的,或从 X 端取来交给宿主的);服务端占有选区时拿它回应(见 <see cref="SetHostClipboard" />)。
    /// 文本、HTML、PNG 各有各的目标(<see cref="ConvertHostSelection" />)。
    /// </summary>
    private XClipboardContent _hostContent = new();

    /// <summary>文本的 UTF-8 / Latin-1 / COMPOUND_TEXT 编码与 HTML 的 UTF-8:第一次有人要时编一次,之后各次 ConvertSelection 共用。</summary>
    private byte[]? _hostClipboardUtf8, _hostClipboardLatin1, _hostClipboardCompound, _hostHtmlUtf8;

    private void SetHostClipboard(XClipboardContent content)
    {
        _hostContent = content;
        _hostClipboardUtf8 = null;
        _hostClipboardLatin1 = null;
        _hostClipboardCompound = null;
        _hostHtmlUtf8 = null;
    }

    private string HostClipboardText => _hostContent.Text ?? "";

    private byte[] HostClipboardUtf8 => _hostClipboardUtf8 ??= Encoding.UTF8.GetBytes(HostClipboardText);

    private byte[] HostClipboardLatin1 => _hostClipboardLatin1 ??= Encoding.Latin1.GetBytes(HostClipboardText);

    /// <summary>最近一次交给宿主的内容 —— 宿主把它(或其中几种格式)写回来时不再抢选区(防回声)。</summary>
    private XClipboardContent? _lastDelivered;

    /// <summary>
    /// <paramref name="content" /> 是不是宿主把我们刚交给它的写了回来:它给的每种格式都与交出去的相同(宿主可能只写回其中几种,
    /// 比如只认文本的宿主只写回文本 —— 那也是回声,抢过来就把 X 那边的 HTML / 图片丢了)。
    /// </summary>
    private bool IsEcho(XClipboardContent content) =>
        _lastDelivered is { } last && !content.IsEmpty
        && (content.Text is null || content.Text == last.Text)
        && (content.Html is null || content.Html == last.Html)
        && (content.Png.IsEmpty || content.Png.Span.SequenceEqual(last.Png.Span));

    /// <summary>
    /// 进行中的「从 X 客户端取选区」,每个选区一份(CLIPBOARD 与 PRIMARY 同时变化时各取各的,原先只有一个槽,后来的把先来的冲掉)。
    /// </summary>
    private readonly Dictionary<uint, SelectionFetch> _fetches = [];

    /// <summary>取选区时每一步(等 SelectionNotify、等下一块 INCR)等属主的时限,过了就放弃(测试可以调短)。</summary>
    internal TimeSpan FetchStepTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>一次进行中的「从 X 客户端取选区」。</summary>
    private sealed class SelectionFetch(SelectionSlot slot, uint target, uint time, uint property)
    {
        /// <summary>取的是哪一份选区(按会话隔离时带着焦点所在的会话)。</summary>
        public SelectionSlot Slot { get; } = slot;

        public uint Selection => Slot.Atom;

        /// <summary>这一步在取的目标。</summary>
        public uint Target { get; set; } = target;

        /// <summary>这一步之后还要取的目标,按顺序(CLIPBOARD 先取 TARGETS,再按它排:文本、HTML、PNG)。</summary>
        public Queue<uint> Plan { get; } = new();

        /// <summary>取到的:目标 → (类型, 数据)。</summary>
        public Dictionary<uint, (uint Type, byte[] Data)> Results { get; } = [];

        /// <summary>文本的退路:UTF8_STRING 不给时再要 STRING(属主不回 TARGETS 时,或 TARGETS 里只列了 UTF8_STRING 却又不给)。</summary>
        public bool StringFallback { get; set; } = true;

        /// <summary>这次取完(或放弃)时要做的:剪贴板管理器在等它才回 SAVE_TARGETS。参数是取没取到东西。</summary>
        public List<Action<bool>> Finished { get; } = [];

        public uint Time { get; } = time;

        /// <summary>属主把内容写到服务端请求窗口上的这个属性(每个选区一个,互不干扰)。</summary>
        public uint Property { get; } = property;

        /// <summary>INCR 传输中累积的字节;不在 INCR 里为 null。</summary>
        public List<byte>? Incr { get; set; }

        public uint IncrType { get; set; }

        /// <summary>第几步:限时检查只认安排它时的那一步。</summary>
        public int Step { get; set; }
    }

    private XWindow SelectionWindow
    {
        get
        {
            if (_selectionWindow is null)
            {
                _selectionWindow = new XWindow(SelectionWindowId, null, Root)
                {
                    Class = 2,   // InputOnly
                    Width = 1,
                    Height = 1,
                    Visual = RootVisualId,
                };
                _resources[SelectionWindowId] = _selectionWindow;
            }
            return _selectionWindow;
        }
    }

    // ------------------------------------------------------------------ 会话与选区的作用域

    /// <summary>没有连接名的客户端(本机经 TCP / Unix 套接字连进来的程序)同属的那个会话;连接名不会是这个值。</summary>
    private const string LocalSession = "\0local";

    /// <summary>
    /// 客户端所属的会话:连接名相同的算一个(同一个 SSH 会话里的程序,见 <see cref="ServeAuthenticatedAsync(Stream, string?, CancellationToken)" />),
    /// 没有连接名的本机程序同属一个本机会话。
    /// </summary>
    private static string SessionOf(XClient client) => client.Label ?? LocalSession;

    /// <summary>
    /// 按会话隔离的选区:开着 <see cref="X11ServerOptions.ClipboardFollowsFocus" /> 时,PRIMARY、SECONDARY、CLIPBOARD 每个会话各有各的属主 ——
    /// 别的会话看不到属主变化、收不到因此发的 SelectionClear 与 XFIXES 通知,也读不到另一个会话里的复制(xs_plan WN-S11)。
    /// 跨会话的复制粘贴经宿主的剪贴板中转(见 <see cref="SyncFocusedSessionClipboard" />)。其余选区(窗口管理器、XSETTINGS、拖放……)照协议全显示共享。
    /// </summary>
    private bool IsIsolatedSelection(uint atom) =>
        _options.ClipboardFollowsFocus && (atom == XAtom.Primary || atom == XAtom.Secondary || atom == Intern("CLIPBOARD"));

    /// <summary>这个客户端眼里的那一份选区。</summary>
    private SelectionSlot SlotOf(uint atom, XClient client) => new(atom, IsIsolatedSelection(atom) ? SessionOf(client) : null);

    /// <summary>这一份选区的变化该让这个客户端知道吗:全显示共享的谁都知道,按会话隔离的只有那个会话的。</summary>
    private static bool InScope(XClient client, SelectionSlot slot) => slot.Scope is null || SessionOf(client) == slot.Scope;

    /// <summary>键盘焦点所在的会话(焦点是 PointerRoot 时看指针所在的顶层);没有 X 窗口有焦点时为 null。</summary>
    private string? FocusedSession()
    {
        XWindow? focused = _focus is null ? null : ReferenceEquals(_focus, Root) ? _pointerWindow : _focus;
        return focused?.TopLevel?.Owner is { } peer ? SessionOf(peer) : null;
    }

    /// <summary>
    /// 这个客户端此刻能不能与宿主的剪贴板来往(<see cref="X11ServerOptions.ClipboardFollowsFocus" />):它属于键盘焦点所在的会话
    /// (与焦点窗口的客户端连接名相同;本机程序同属一个会话)。
    /// </summary>
    private bool InFocusedSession(XClient client) => !_options.ClipboardFollowsFocus || FocusedSession() == SessionOf(client);

    // ------------------------------------------------------------------ 宿主 → X

    /// <summary>
    /// 剪贴板内容的逻辑时钟:宿主给了新文本、X 端的复制交给了宿主、哪个会话里的程序占有了同步的选区,各推进一格。
    /// 某个会话的那一份选区记着它最后一次变化时的钟点(<see cref="_slotStamps" />),比 <see cref="_clipboardStamp" /> 旧,
    /// 那个会话拿到焦点时服务端就替宿主在那里占有最新的文本 —— 「最近一次复制」赢,不管它发生在哪个会话或本机。
    /// </summary>
    private long _clipboardClock;

    /// <summary>最新的剪贴板文本(宿主给的,或从 X 端取来交给宿主的)对应的钟点;0 = 还没有过。</summary>
    private long _clipboardStamp;

    private readonly Dictionary<SelectionSlot, long> _slotStamps = [];

    /// <summary>宿主的剪贴板有了新内容(见 <see cref="SetClipboard" />):服务端替宿主在焦点所在的会话里占有 CLIPBOARD(有文本时连同 PRIMARY)。</summary>
    private void ApplyClipboardContent(XClipboardContent content)
    {
        if (!_options.SyncClipboard || content.IsEmpty || IsEcho(content))
        {
            return;   // 宿主把我们刚给的写回来了
        }
        SetHostClipboard(content);
        _clipboardStamp = ++_clipboardClock;
        SyncFocusedSessionClipboard();
    }

    /// <summary>
    /// 让焦点所在的会话看到最新的剪贴板:它那一份同步的选区比最新的文本旧,服务端就替宿主在那里占有。不隔离时(全显示一份)
    /// 不看焦点,有新文本就占。焦点换了、PointerRoot 下指针换了顶层、宿主给了新文本时调。
    /// </summary>
    private void SyncFocusedSessionClipboard()
    {
        if (!_options.SyncClipboard || _clipboardStamp == 0)
        {
            return;
        }
        string? session = _options.ClipboardFollowsFocus ? FocusedSession() : null;
        if (_options.ClipboardFollowsFocus && session is null)
        {
            return;   // 没有 X 窗口有焦点:等用户回到某个会话再交
        }
        foreach (uint atom in (uint[])[Intern("CLIPBOARD"), XAtom.Primary])
        {
            if (!IsSyncedSelection(atom))
            {
                continue;
            }
            SelectionSlot slot = new(atom, IsIsolatedSelection(atom) ? session : null);
            if (_slotStamps.GetValueOrDefault(slot) < _clipboardStamp)
            {
                TakeSelectionForHost(slot);
            }
        }
    }

    /// <summary>某个会话里的程序占有了一份同步的选区:它就是那个会话里最新的复制。</summary>
    private void NoteClientSelection(SelectionSlot slot)
    {
        if (IsSyncedSelection(slot.Atom))
        {
            _slotStamps[slot] = ++_clipboardClock;
        }
    }

    private void TakeSelectionForHost(SelectionSlot slot)
    {
        uint selection = slot.Atom;
        // 宿主的占有也是一次换属主:不早于最后一次换属主的时间(否则之后带着更早事件时间的客户端反而能抢回来),并推进它。
        uint now = Now;
        if (_selectionLastChange.TryGetValue(slot, out uint lastChange) && unchecked((int)(lastChange - now)) > 0)
        {
            now = lastChange;
        }
        _selectionLastChange[slot] = now;
        // 按会话隔离时,只有这个会话里原先的属主收到 SelectionClear、只有这个会话收到 XFIXES 通知:别的会话看不出宿主何时有了新内容。
        if (_selections.TryGetValue(slot, out (XWindow Window, XClient? Client, uint Time) current) && current.Client is { } previous)
        {
            XWindow old = current.Window;
            previous.Event(XEventCode.SelectionClear, 0, w => w.U32(now).U32(old.Id).U32(selection));
        }
        _selections[slot] = (SelectionWindow, null, now);
        _slotStamps[slot] = _clipboardStamp;
        NotifySelectionChange(selection, 0, SelectionWindowId, now, client => InScope(client, slot));
    }

    /// <summary>
    /// 同步的选区没了属主(属主 SetSelectionOwner(None)、属主窗口销毁、属主断开):服务端替宿主接管,内容是最新的剪贴板文本 ——
    /// 相当于剪贴板管理器。原先 X 程序复制之后一退出,别的 X 程序就再也粘贴不到,宿主手里明明还有这段文本(两道防回声都拦着它)。
    /// </summary>
    private void OnSelectionOwnerLost(SelectionSlot slot)
    {
        if (IsSyncedSelection(slot.Atom) && _clipboardStamp != 0 && !_selections.ContainsKey(slot))
        {
            TakeSelectionForHost(slot);
        }
    }

    private bool IsSyncedSelection(uint selection) =>
        _options.SyncClipboard && (selection == Intern("CLIPBOARD") || (_options.SyncPrimary && selection == XAtom.Primary));

    // ------------------------------------------------------------------ 服务端当属主

    /// <summary>服务端占有的选区被 ConvertSelection 了:按目标写属性,再发 SelectionNotify(ICCCM §2.2)。</summary>
    private void ServeSelection(XClient c, XWindow requestor, uint selection, uint target, uint property, uint time, uint ownerTime)
    {
        bool oldStyle = property == 0;
        if (oldStyle)
        {
            property = target;   // 旧式请求方(ICCCM §2.2)
        }
        if (selection == Intern("CLIPBOARD_MANAGER"))
        {
            if (target == Intern("SAVE_TARGETS") && !oldStyle)
            {
                SaveTargets(c, requestor, property, time);   // 回答等内容取完再发(剪贴板管理器规范)
                return;
            }
            if (target == Intern("TARGETS"))
            {
                WriteConverted(requestor, property, (XAtom.Atom, 32, AtomList([Intern("TARGETS"), Intern("SAVE_TARGETS"), Intern("TIMESTAMP")])));
            }
            else if (target == Intern("TIMESTAMP"))
            {
                WriteConverted(requestor, property, (XAtom.Integer, 32, AtomList([ownerTime])));
            }
            else
            {
                property = 0;
            }
            XClient managerReply = requestor.Owner is { Closed: false } o ? o : c;
            managerReply.Event(XEventCode.SelectionNotify, 0, w => w.U32(time).U32(requestor.Id).U32(selection).U32(target).U32(property));
            return;
        }
        if (selection != Intern("CLIPBOARD") && selection != XAtom.Primary)
        {
            property = 0;   // 服务端占有的其它选区(_XSETTINGS_S0 这类管理器选区)没有可转换的内容
        }
        else if (!InFocusedSession(c))
        {
            property = 0;   // 宿主的文本只给键盘焦点所在的会话:原先本机复制的密码在用户点一下任意 X 窗口后,所有会话的所有客户端都读得到
        }
        else if (target == Intern("MULTIPLE"))
        {
            if (oldStyle || !ServeMultiple(requestor, property, ownerTime))
            {
                property = 0;   // MULTIPLE 必须给属性(放目标与属性对的那个)
            }
        }
        else if (ConvertHostSelection(target, ownerTime) is { } value)
        {
            WriteConverted(requestor, property, value);
        }
        else
        {
            property = 0;   // 不支持的目标:拒绝
        }
        XClient to = requestor.Owner is { Closed: false } creator ? creator : c;
        to.Event(XEventCode.SelectionNotify, 0, w => w.U32(time).U32(requestor.Id).U32(selection).U32(target).U32(property));
    }

    /// <summary>32 位的一串值(原子、时间戳),按服务端的字节序(属性数据按小端存,发出时按客户端的字节序换)。</summary>
    private static byte[] AtomList(ReadOnlySpan<uint> values)
    {
        byte[] data = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i * 4), values[i]);
        }
        return data;
    }

    /// <summary>剪贴板的 HTML 与 PNG 图片的目标名(freedesktop 的惯例:MIME 类型当目标)。</summary>
    private const string HtmlTarget = "text/html", PngTarget = "image/png";

    /// <summary>宿主的内容按目标转换;不支持的目标(或这份内容里没有那种格式)为 null。</summary>
    private (uint Type, byte Format, byte[] Data)? ConvertHostSelection(uint target, uint ownerTime)
    {
        uint utf8 = Intern("UTF8_STRING");
        uint targets = Intern("TARGETS");
        uint timestamp = Intern("TIMESTAMP");
        uint text = Intern("TEXT");
        uint plainUtf8 = Intern("text/plain;charset=utf-8");
        uint compound = Intern("COMPOUND_TEXT");
        uint html = Intern(HtmlTarget), png = Intern(PngTarget);
        bool hasText = _hostContent.Text is not null;
        if (target == targets)
        {
            // ICCCM §2.6.2:属主必须支持 TARGETS、MULTIPLE、TIMESTAMP。原先不列 MULTIPLE 与 COMPOUND_TEXT。只列这份内容里有的格式。
            List<uint> atoms = [targets, Intern("MULTIPLE"), timestamp];
            if (hasText)
            {
                atoms.AddRange([utf8, plainUtf8, compound, XAtom.String, text]);
            }
            if (_hostContent.Html is not null)
            {
                atoms.Add(html);
            }
            if (!_hostContent.Png.IsEmpty)
            {
                atoms.Add(png);
            }
            return (XAtom.Atom, 32, AtomList([.. atoms]));
        }
        if (target == timestamp)
        {
            return (XAtom.Integer, 32, AtomList([ownerTime]));
        }
        if (target == html && _hostContent.Html is { } markup)
        {
            return (html, 8, _hostHtmlUtf8 ??= Encoding.UTF8.GetBytes(markup));
        }
        if (target == png && !_hostContent.Png.IsEmpty)
        {
            return (png, 8, _hostContent.Png.ToArray());
        }
        if (!hasText)
        {
            return null;
        }
        if (target == utf8 || target == plainUtf8)
        {
            return (target, 8, HostClipboardUtf8);
        }
        if (target == compound)
        {
            return (compound, 8, _hostClipboardCompound ??= XText.EncodeCompoundText(HostClipboardText));   // Motif / Xaw 要的
        }
        if (target == text && !IsLatin1(HostClipboardText))
        {
            // TEXT 由属主挑编码(ICCCM §2.6.2):Latin-1 装不下(中日韩)就回 UTF8_STRING —— 原先按 Latin-1 有损转换,汉字变成「?」。
            return (utf8, 8, HostClipboardUtf8);
        }
        if (target == XAtom.String || target == text)
        {
            return (XAtom.String, 8, HostClipboardLatin1);
        }
        return null;
    }

    /// <summary>转换结果写到请求方的属性上:大的分块交(ICCCM §2.5)。</summary>
    private void WriteConverted(XWindow requestor, uint property, (uint Type, byte Format, byte[] Data) value)
    {
        if (value.Format == 8 && value.Data.Length > IncrChunkBytes)
        {
            StartIncrTransfer(requestor, property, value.Type, value.Data);
            return;
        }
        StoreServerProperty(requestor, property, new XProperty(value.Type, value.Format, value.Data));
        SendPropertyNotify(requestor, property, deleted: false);
    }

    /// <summary>MULTIPLE 里最多看这么多对(真实的请求方一次要几个目标)。</summary>
    private const int MaxMultiplePairs = 64;

    /// <summary>
    /// ICCCM §2.6.2「MULTIPLE」:请求方在 <paramref name="property" /> 里放一串(目标, 属性)对(ATOM_PAIR);逐个转换写到各自的属性上,
    /// 转换不了的把那一对的属性换成 None 再写回去。属性不在或格式不对时整个拒绝。
    /// </summary>
    private bool ServeMultiple(XWindow requestor, uint property, uint ownerTime)
    {
        if (!requestor.Properties.TryGetValue(property, out XProperty? list) || list.Format != 32)
        {
            return false;
        }
        uint[] pairs = ReadCard32s(list, MaxMultiplePairs * 2);
        bool refused = false;
        for (int i = 0; i + 1 < pairs.Length; i += 2)
        {
            uint target = pairs[i], destination = pairs[i + 1];
            if (destination == 0)
            {
                continue;
            }
            // 属性名必须是存在的原子(同 ConvertSelection);MULTIPLE 不能套 MULTIPLE。
            if (target == Intern("MULTIPLE") || AtomName(destination) is null || ConvertHostSelection(target, ownerTime) is not { } value)
            {
                pairs[i + 1] = 0;
                refused = true;
                continue;
            }
            WriteConverted(requestor, destination, value);
        }
        if (refused)
        {
            SetProperty(requestor, property, list.Type, pairs);
        }
        return true;
    }

    /// <summary>
    /// 服务端当属主时,超过这么多字节的文本按 INCR 分块交,每块这么大。原先整份写成一个属性:绕过了单个属性的上限,
    /// 请求方一次取回几十 MB 又会撞上输出积压上限被断开。
    /// </summary>
    internal const int IncrChunkBytes = 256 * 1024;

    /// <summary>同时进行的 INCR 传输上限(多了丢掉最早的)与每一步等请求方的时限。</summary>
    private const int MaxOutgoingIncr = 32;

    internal TimeSpan IncrStepTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>一次服务端当属主的 INCR 传输:(请求窗口, 属性) → 进度。</summary>
    private readonly Dictionary<(XWindow Window, uint Property), OutgoingIncr> _outgoingIncr = [];

    private sealed class OutgoingIncr(uint type, byte[] data)
    {
        public uint Type { get; } = type;

        public byte[] Data { get; } = data;

        public int Offset { get; set; }

        /// <summary>最后那块空的已经写出:请求方删掉它就结束。</summary>
        public bool Finished { get; set; }

        /// <summary>第几步:限时检查只认安排它时的那一步。</summary>
        public int Step { get; set; }
    }

    /// <summary>
    /// ICCCM §2.5:属性先写成类型 INCR、值是总长的下限,发 SelectionNotify;请求方每删一次属性,就写下一块(类型是真正的类型),
    /// 最后写一块空的表示结束。块之间请求方迟迟不删,传输作废。
    /// </summary>
    private void StartIncrTransfer(XWindow requestor, uint property, uint type, byte[] data)
    {
        if (_outgoingIncr.Count >= MaxOutgoingIncr)
        {
            _outgoingIncr.Remove(_outgoingIncr.Keys.First());
        }
        OutgoingIncr transfer = new(type, data);
        _outgoingIncr[(requestor, property)] = transfer;
        byte[] size = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)data.Length);
        StoreServerProperty(requestor, property, new XProperty(Intern("INCR"), 32, size));
        SendPropertyNotify(requestor, property, deleted: false);
        ExpireIncrLater(requestor, property, transfer);
    }

    /// <summary>请求方删掉了属性(见 <see cref="SendPropertyNotify" />):是一次 INCR 传输在等的,就写下一块。</summary>
    private void OnIncrPropertyDeleted(XWindow window, uint property)
    {
        if (!_outgoingIncr.TryGetValue((window, property), out OutgoingIncr? transfer))
        {
            return;
        }
        if (transfer.Finished)
        {
            _outgoingIncr.Remove((window, property));
            return;
        }
        int length = Math.Min(IncrChunkBytes, transfer.Data.Length - transfer.Offset);
        byte[] chunk = transfer.Data.AsSpan(transfer.Offset, length).ToArray();
        transfer.Offset += length;
        transfer.Finished = length == 0;
        transfer.Step++;
        StoreServerProperty(window, property, new XProperty(transfer.Type, 8, chunk));
        SendPropertyNotify(window, property, deleted: false);
        ExpireIncrLater(window, property, transfer);
    }

    private void ExpireIncrLater(XWindow window, uint property, OutgoingIncr transfer)
    {
        int step = transfer.Step;
        _ = DelayThenPostAsync((uint)IncrStepTimeout.TotalMilliseconds, () =>
        {
            if (_outgoingIncr.TryGetValue((window, property), out OutgoingIncr? current) && ReferenceEquals(current, transfer) && current.Step == step)
            {
                _outgoingIncr.Remove((window, property));   // 请求方不再取了(或窗口已经没了)
            }
        }, _lifetime.Token);
    }

    // ------------------------------------------------------------------ 服务端当请求方

    /// <summary>
    /// X 客户端占有了同步的选区:把内容要过来。CLIPBOARD 先要 TARGETS,再按它列的要文本(UTF8_STRING,没有退到 COMPOUND_TEXT / STRING)、
    /// <c>text/html</c>、<c>image/png</c>;属主不回 TARGETS 时只要文本。PRIMARY 只要文本(UTF8_STRING,不给再退回 STRING)。
    /// </summary>
    private void OnClientTookSelection(XClient owner, XWindow ownerWindow, SelectionSlot slot, uint time)
    {
        uint selection = slot.Atom;
        if (!IsSyncedSelection(selection) || !InFocusedSession(owner))
        {
            return;   // 后台会话的复制不进系统剪贴板:否则远端程序可以反复改写本机剪贴板,用户往别处粘贴时中招
        }
        bool clipboard = selection == Intern("CLIPBOARD");
        SelectionFetch fetch = new(slot, clipboard ? Intern("TARGETS") : Intern("UTF8_STRING"), time,
            Intern("_VELASHELL_" + (AtomName(selection) ?? "SELECTION")));
        if (_fetches.Remove(selection, out SelectionFetch? replaced))
        {
            FinishFetch(replaced, delivered: false);   // 同一个选区又换了属主:旧的那次作废
        }
        _fetches[selection] = fetch;
        RequestFetch(fetch, owner, ownerWindow);
    }

    private void RequestFetch(SelectionFetch fetch, XClient owner, XWindow ownerWindow)
    {
        uint requestor = SelectionWindow.Id;
        owner.Event(XEventCode.SelectionRequest, 0, w => w
            .U32(fetch.Time).U32(ownerWindow.Id).U32(requestor).U32(fetch.Selection).U32(fetch.Target).U32(fetch.Property));
        ExpireFetchLater(fetch);
    }

    /// <summary>属主迟迟不回(卡死、不理 SelectionRequest、INCR 写到一半不写了):过了时限放弃这次,不一直占着。原先没有时限。</summary>
    private void ExpireFetchLater(SelectionFetch fetch)
    {
        int step = ++fetch.Step;
        _ = DelayThenPostAsync((uint)FetchStepTimeout.TotalMilliseconds, () =>
        {
            if (_fetches.TryGetValue(fetch.Selection, out SelectionFetch? current) && ReferenceEquals(current, fetch) && current.Step == step)
            {
                // 这一个目标等不到了:不再要后面的,已经取到的照样交出去(文本取到了、图片卡住,文本不该陪着丢)。
                fetch.Incr = null;
                fetch.Plan.Clear();
                NextFetch(fetch);
            }
        }, _lifetime.Token);
    }

    /// <summary>
    /// 这一个目标有了结果(<paramref name="data" /> 为 null 是属主不给):TARGETS 的结果排出要取的目标;UTF8_STRING 不给时退到 STRING;
    /// 其余记下。然后取下一个,都取完了交给宿主。
    /// </summary>
    private void OnFetched(SelectionFetch fetch, uint type, byte[]? data)
    {
        fetch.Incr = null;
        uint target = fetch.Target;
        uint utf8 = Intern("UTF8_STRING");
        if (target == Intern("TARGETS"))
        {
            PlanFetch(fetch, data is not null && type == XAtom.Atom ? ReadAtoms(data) : null);
        }
        else if (data is null)
        {
            if (target == utf8 && fetch.StringFallback && !fetch.Results.ContainsKey(XAtom.String))
            {
                fetch.Plan.Clear();
                fetch.Plan.Enqueue(XAtom.String);   // 属主不给 UTF8_STRING:退回 STRING(只要文本时才走到这)
            }
        }
        else if (data.Length <= MaxFetchBytes(target))
        {
            fetch.Results[target] = (type, data);
        }
        NextFetch(fetch);
    }

    /// <summary>
    /// 按属主的 TARGETS 排要取的目标:文本一个(UTF8_STRING,没有就 COMPOUND_TEXT,再没有就 STRING)、<c>text/html</c>、<c>image/png</c>。
    /// 属主不回 TARGETS(<paramref name="offered" /> 为 null)时只要文本,UTF8_STRING 不给再退 STRING。
    /// </summary>
    private void PlanFetch(SelectionFetch fetch, uint[]? offered)
    {
        uint utf8 = Intern("UTF8_STRING"), compound = Intern("COMPOUND_TEXT");
        fetch.Plan.Clear();
        if (offered is null)
        {
            fetch.Plan.Enqueue(utf8);
            return;
        }
        fetch.StringFallback = false;
        uint? text = offered.Contains(utf8) ? utf8 : offered.Contains(compound) ? compound : offered.Contains(XAtom.String) ? XAtom.String : null;
        if (text is { } t)
        {
            fetch.Plan.Enqueue(t);
        }
        foreach (uint extra in (uint[])[Intern(HtmlTarget), Intern(PngTarget)])
        {
            if (offered.Contains(extra))
            {
                fetch.Plan.Enqueue(extra);
            }
        }
    }

    /// <summary>一个目标至多收这么多字节:PNG 图片 <see cref="MaxClipboardImageBytes" />,其余 <see cref="MaxClipboardBytes" />。</summary>
    private int MaxFetchBytes(uint target) => target == Intern(PngTarget) ? MaxClipboardImageBytes : MaxClipboardBytes;

    private static uint[] ReadAtoms(byte[] data)
    {
        uint[] atoms = new uint[Math.Min(data.Length / 4, 1024)];
        for (int i = 0; i < atoms.Length; i++)
        {
            atoms[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i * 4));
        }
        return atoms;
    }

    /// <summary>取计划里的下一个目标;没有了(或者属主已经不在)就结束这次,把取到的交给宿主。</summary>
    private void NextFetch(SelectionFetch fetch)
    {
        DeleteSelectionProperty(fetch.Property);
        if (fetch.Plan.Count > 0 && _selections.TryGetValue(fetch.Slot, out (XWindow Window, XClient? Client, uint Time) owner) && owner.Client is { } client)
        {
            fetch.Target = fetch.Plan.Dequeue();
            RequestFetch(fetch, client, owner.Window);
            return;
        }
        if (_fetches.TryGetValue(fetch.Selection, out SelectionFetch? current) && ReferenceEquals(current, fetch))
        {
            _fetches.Remove(fetch.Selection);
        }
        FinishFetch(fetch, Deliver(fetch));
    }

    /// <summary>这次取结束了:等着它的剪贴板管理器请求(SAVE_TARGETS)现在回答。</summary>
    private static void FinishFetch(SelectionFetch fetch, bool delivered)
    {
        foreach (Action<bool> finished in fetch.Finished)
        {
            finished(delivered);
        }
        fetch.Finished.Clear();
    }

    /// <summary>某个客户端断开了:正在取的选区若已没了属主,不再要后面的目标,已经取到的照样交出去。</summary>
    private void DropOrphanedFetches()
    {
        foreach (SelectionFetch fetch in _fetches.Values.ToArray())
        {
            if (!_selections.ContainsKey(fetch.Slot))
            {
                fetch.Incr = null;
                fetch.Plan.Clear();
                NextFetch(fetch);
            }
        }
    }

    // ------------------------------------------------------------------ 剪贴板管理器(CLIPBOARD_MANAGER)

    /// <summary>
    /// 服务端当剪贴板管理器(freedesktop 的 Clipboard Manager Specification;开着剪贴板同步时):占有 CLIPBOARD_MANAGER,
    /// GTK 之类的程序退出前用 SAVE_TARGETS 请管理器把剪贴板接过去。服务端在程序复制时就把内容取过来了(<see cref="OnClientTookSelection" />),
    /// 属主一走就以宿主的身份接管 CLIPBOARD(<see cref="OnSelectionOwnerLost" />);这里要做的只是在内容取完之前不回答 ——
    /// 规范:属主收到 SAVE_TARGETS 的 SelectionNotify 就退出,不等 INCR 传完。原先没有管理器,程序复制一张大图马上退出,图就丢了。
    /// </summary>
    private void InitClipboardManager()
    {
        if (_options.SyncClipboard)
        {
            _selections[new SelectionSlot(Intern("CLIPBOARD_MANAGER"), null)] = (SelectionWindow, null, 0);
        }
    }

    /// <summary>
    /// SAVE_TARGETS:请求方的 CLIPBOARD 正在取 → 取完再回;已经取到、宿主手里就是它的 → 马上回成功;别的情况(后台会话的复制不进系统剪贴板、
    /// 请求方不是属主)回 None —— 规范:管理器存不了,程序照常退出。属性里列的目标不另外取:要存的就是服务端认得的那几种格式。
    /// </summary>
    private void SaveTargets(XClient c, XWindow requestor, uint property, uint time)
    {
        uint manager = Intern("CLIPBOARD_MANAGER"), save = Intern("SAVE_TARGETS");
        SelectionSlot slot = SlotOf(Intern("CLIPBOARD"), c);
        XClient to = requestor.Owner is { Closed: false } creator ? creator : c;
        void Reply(bool saved)
        {
            if (!to.Closed)
            {
                to.Event(XEventCode.SelectionNotify, 0, w => w.U32(time).U32(requestor.Id).U32(manager).U32(save).U32(saved ? property : 0));
            }
        }
        if (_fetches.TryGetValue(slot.Atom, out SelectionFetch? fetch) && fetch.Slot == slot)
        {
            fetch.Finished.Add(Reply);
            return;
        }
        bool owner = _selections.TryGetValue(slot, out (XWindow Window, XClient? Client, uint Time) current) && ReferenceEquals(current.Client, c);
        Reply(owner && _clipboardStamp != 0 && _slotStamps.GetValueOrDefault(slot) == _clipboardStamp && ReferenceEquals(_lastDelivered, _hostContent));
    }

    /// <summary>属主用 SendEvent 把 SelectionNotify 发到了我们的请求窗口。</summary>
    private void OnSelectionWindowEvent(byte[] raw, bool bigEndian)
    {
        if ((raw[0] & 0x7F) != XEventCode.SelectionNotify)
        {
            return;
        }
        uint selection = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(12)) : BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(12));
        uint property = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(20)) : BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(20));
        if (!_fetches.TryGetValue(selection, out SelectionFetch? fetch) || fetch.Incr is not null)
        {
            return;
        }
        if (property == 0 || property != fetch.Property || !SelectionWindow.Properties.TryGetValue(property, out XProperty? value))
        {
            OnFetched(fetch, 0, null);   // 属主不给这个目标
            return;
        }
        if (value.Type == Intern("INCR"))
        {
            // 大数据:删掉属性表示「准备好了」,属主随后一块块往里写(ICCCM §2.5)。
            fetch.Incr = [];
            DeleteSelectionProperty(property);
            ExpireFetchLater(fetch);
            return;
        }
        OnFetched(fetch, value.Type, value.Data.ToArray());
    }

    /// <summary>INCR 传输中:属主往请求窗口写了一块。空块表示结束。</summary>
    private void OnSelectionWindowProperty(uint property, bool deleted)
    {
        if (deleted || _fetches.Count == 0)
        {
            return;
        }
        SelectionFetch? fetch = null;
        foreach (SelectionFetch candidate in _fetches.Values)
        {
            if (candidate.Property == property && candidate.Incr is not null)
            {
                fetch = candidate;
            }
        }
        if (fetch is not { Incr: { } buffer })
        {
            return;
        }
        XProperty chunk = SelectionWindow.Properties[property];
        DeleteSelectionProperty(property);
        if (chunk.Data.Length == 0)
        {
            OnFetched(fetch, fetch.IncrType, [.. buffer]);
            return;
        }
        fetch.IncrType = chunk.Type;
        buffer.AddRange(chunk.Data);
        if (buffer.Count > MaxFetchBytes(fetch.Target))
        {
            // 太大:这个目标不要了,接着要下一个(属主写下一块时没人删属性,它自己会超时)。
            OnFetched(fetch, 0, null);
            return;
        }
        ExpireFetchLater(fetch);
    }

    private static bool IsLatin1(string text) => !text.AsSpan().ContainsAnyExceptInRange('\0', '\u00FF');

    private void DeleteSelectionProperty(uint property)
    {
        if (SelectionWindow.Properties.Remove(property, out XProperty? removed))
        {
            ReleaseProperty(removed);
            SendPropertyNotify(SelectionWindow, property, deleted: true);
        }
    }

    /// <summary>
    /// 从 X 端取来的内容交给宿主(取到一种格式也算;一种都没有返回 false)。它也就成了最新的剪贴板内容:复制它的那个会话已经有了,
    /// 别的会话拿到焦点时由服务端替宿主占有(按会话隔离时,跨会话的复制粘贴就是这样经宿主的剪贴板中转的);属主退出时服务端接管它。
    /// </summary>
    private bool Deliver(SelectionFetch fetch)
    {
        string? text = null, html = null;
        byte[] png = [];
        foreach ((uint target, (uint type, byte[] data)) in fetch.Results)
        {
            if (target == Intern(HtmlTarget))
            {
                html = DecodeHtml(data);
            }
            else if (target == Intern(PngTarget))
            {
                png = data;
            }
            else
            {
                // 文本按属主回的类型解码:STRING 是 Latin-1,COMPOUND_TEXT 解转义序列,其余按 UTF-8。
                text = XText.Decode(data, type == XAtom.String ? XTextEncoding.Latin1
                    : type == Intern("COMPOUND_TEXT") ? XTextEncoding.CompoundText
                    : XTextEncoding.Utf8);
            }
        }
        XClipboardContent content = new() { Text = text, Html = html, Png = png };
        if (content.IsEmpty)
        {
            return false;
        }
        _lastDelivered = content;
        SetHostClipboard(content);
        _clipboardStamp = ++_clipboardClock;
        _slotStamps[fetch.Slot] = _clipboardStamp;
        _host.ClipboardContentChanged(content);
        return true;
    }

    /// <summary><c>text/html</c> 的字节:带 UTF-16 的字节序标记时按 UTF-16 解(有的浏览器这样给),否则按 UTF-8。</summary>
    private static string DecodeHtml(byte[] data) =>
        data is [0xFF, 0xFE, ..] ? Encoding.Unicode.GetString(data, 2, data.Length - 2)
        : data is [0xFE, 0xFF, ..] ? Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2)
        : Encoding.UTF8.GetString(data);
}
