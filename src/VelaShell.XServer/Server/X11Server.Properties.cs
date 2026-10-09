// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 「InternAtom」「GetAtomName」「ChangeProperty」「DeleteProperty」
//   「GetProperty」(long-offset / long-length / bytes-after 的算法)「ListProperties」「RotateProperties」
//   「SetSelectionOwner」「GetSelectionOwner」「ConvertSelection」「SendEvent」「KillClient」、
//   第 10 节「Events」里 PropertyNotify / SelectionClear / SelectionRequest / SelectionNotify 的字段
//   ICCCM §2(选区的约定)

using System.Buffers.Binary;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    /// <summary>
    /// 客户端经 InternAtom 能建的原子数上限。原子一旦建了就永不释放(协议如此),不设上限一个客户端就能把内存吃光;
    /// 真实程序建的是几百到几千个。服务端自己建的(预定义、EWMH、XKB 的名字……)不受限。
    /// </summary>
    internal const int MaxAtoms = 1 << 18;

    /// <summary>全部原子名合计的字节上限(一个名字可以到 65535 字节)。</summary>
    internal const long MaxAtomNameBytes = 16L * 1024 * 1024;

    /// <summary>一个属性的值最多这么多字节。ChangeProperty 的 Append / Prepend 能让它一直涨;_NET_WM_ICON 这种大户也不过几 MB。</summary>
    internal const long MaxPropertyBytes = 32L * 1024 * 1024;

    private readonly Dictionary<string, uint> _atomsByName = [with(StringComparer.Ordinal)];
    private readonly List<string> _atomNames = [];
    private long _atomNameBytes;
    /// <summary>
    /// 选区表的键:选区原子与它的作用域。作用域为 null 的是全显示共享的选区(协议的本义);开着
    /// <see cref="X11ServerOptions.ClipboardFollowsFocus" /> 时 PRIMARY、SECONDARY、CLIPBOARD 按会话各存一份(作用域是会话,见 <c>SessionOf</c>)。
    /// </summary>
    private readonly record struct SelectionSlot(uint Atom, string? Scope);

    private readonly Dictionary<SelectionSlot, (XWindow Window, XClient? Client, uint Time)> _selections = [];

    /// <summary>
    /// 每个选区最后一次换属主的时间(协议「SetSelectionOwner」的 last-change time)。属主窗口销毁、属主断开、属主放弃时记录整条删掉,
    /// 这个时间却不随之消失 —— 否则先放弃再带一个未来的时间戳去占,之后所有人带真实事件时间去占都被当成「早于当前属主」静默忽略。
    /// </summary>
    private readonly Dictionary<SelectionSlot, uint> _selectionLastChange = [];

    private void InitAtoms()
    {
        _atomNames.Add("");
        for (int i = 1; i < XAtom.PredefinedNames.Length; i++)
        {
            Intern(XAtom.PredefinedNames[i]);
        }
    }

    internal uint Intern(string name)
    {
        if (_atomsByName.TryGetValue(name, out uint atom))
        {
            return atom;
        }
        atom = (uint)_atomNames.Count;
        _atomNames.Add(name);
        _atomsByName[name] = atom;
        _atomNameBytes += name.Length;
        return atom;
    }

    internal string? AtomName(uint atom) => atom > 0 && atom < _atomNames.Count ? _atomNames[(int)atom] : null;

    private void CheckAtom(uint atom)
    {
        if (AtomName(atom) is null)
        {
            throw new XProtocolError(XErrorCode.Atom, atom);
        }
    }

    internal XWindow Window(uint id) => Lookup<XWindow>(id) ?? throw new XProtocolError(XErrorCode.Window, id);

    private void InternAtom(XClient c, XRequestReader r)
    {
        bool onlyIfExists = r.Data != 0;
        int length = r.U16();
        r.Skip(2);
        string name = r.String8(length);
        uint atom = onlyIfExists ? _atomsByName.GetValueOrDefault(name) : InternForClient(name);
        c.Reply(0, w => w.U32(atom).Zero(20));
    }

    /// <summary>
    /// 客户端要建的原子(InternAtom、XFIXES 的 SetCursorName):原子永不释放,超过 <see cref="MaxAtoms" /> 个或名字合计
    /// <see cref="MaxAtomNameBytes" /> 回 Alloc。服务端自己用的名字走 <see cref="Intern" />,不受限。
    /// </summary>
    private uint InternForClient(string name)
    {
        if (_atomsByName.TryGetValue(name, out uint atom))
        {
            return atom;
        }
        if (_atomNames.Count >= MaxAtoms || _atomNameBytes + name.Length > MaxAtomNameBytes)
        {
            throw new XProtocolError(XErrorCode.Alloc);
        }
        return Intern(name);
    }

    private void GetAtomName(XClient c, XRequestReader r)
    {
        uint atom = r.U32();
        string name = AtomName(atom) ?? throw new XProtocolError(XErrorCode.Atom, atom);
        byte[] bytes = XWire.Latin1.GetBytes(name);
        c.Reply(0, w => w.U16((ushort)bytes.Length).Zero(22).Bytes(bytes).Pad4());
    }

    // ------------------------------------------------------------------ 属性

    private void ChangeProperty(XClient c, XRequestReader r)
    {
        byte mode = r.Data;
        XWindow window = Window(r.U32());
        uint property = r.U32();
        uint type = r.U32();
        byte format = r.U8();
        r.Skip(3);
        uint count = r.U32();
        CheckAtom(property);
        CheckAtom(type);
        if (format is not (8 or 16 or 32))
        {
            throw new XProtocolError(XErrorCode.Value, format);
        }
        if (mode > 2)
        {
            throw new XProtocolError(XErrorCode.Value, mode);
        }
        long byteCount = count * (format / 8L);
        if (byteCount > r.Remaining)
        {
            throw new XProtocolError(XErrorCode.Length);
        }
        XProperty? existing = mode != 0 ? window.Properties.GetValueOrDefault(property) : null;
        if (existing is not null && (existing.Type != type || existing.Format != format))
        {
            throw new XProtocolError(XErrorCode.Match);
        }
        if (byteCount + (existing?.Data.Length ?? 0) > MaxPropertyBytes)
        {
            throw new XProtocolError(XErrorCode.Alloc);   // 先算再拼:不为一个注定超限的值分配
        }
        // 记账(xs_plan X-2):新值记在写它的客户端名下,旧值退还给当初写它的客户端;记不下回 Alloc,属性不变。
        XProperty? replaced = window.Properties.GetValueOrDefault(property);
        ReleaseProperty(replaced);
        try
        {
            ChargeMemory(c, byteCount + (existing?.Data.Length ?? 0));
        }
        catch (XProtocolError)
        {
            ChargeMemory(replaced?.ChargedTo, replaced?.Data.Length ?? 0, force: true);
            throw;
        }
        byte[] data = ToNativeOrder(r.Bytes((int)byteCount), format, c.BigEndian);
        // 追加接在留了余量的存储后面,只拷新字节(xs_plan WN-P1);前插少见,照旧整份拼一次。
        window.Properties[property] = existing is null ? new XProperty(type, format, data) { ChargedTo = c }
            : mode == 2 ? existing.Append(data, c)
            : new XProperty(type, format, [.. data, .. existing.Data]) { ChargedTo = c };
        SendPropertyNotify(window, property, deleted: false);
        OnTopLevelPropertyChanged(window, property);
    }

    /// <summary>服务端自己写的属性(EWMH、XSETTINGS、剪贴板的回应……):被替换掉的旧值退还写它的客户端的账。</summary>
    internal void StoreServerProperty(XWindow window, uint property, XProperty value)
    {
        ReleaseProperty(window.Properties.GetValueOrDefault(property));
        window.Properties[property] = value;
    }

    /// <summary>16 / 32 位属性一律按本机序(小端)存放,取出时再按取的人的字节序写出。</summary>
    private static byte[] ToNativeOrder(byte[] data, byte format, bool bigEndian)
    {
        if (!bigEndian || format == 8)
        {
            return data;
        }
        SwapUnits(data, format);
        return data;
    }

    private static void SwapUnits(byte[] data, byte format)
    {
        if (format == 16)
        {
            for (int i = 0; i + 1 < data.Length; i += 2)
            {
                (data[i], data[i + 1]) = (data[i + 1], data[i]);
            }
        }
        else if (format == 32)
        {
            for (int i = 0; i + 3 < data.Length; i += 4)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i), BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i)));
            }
        }
    }

    private void DeleteProperty(XRequestReader r)
    {
        XWindow window = Window(r.U32());
        uint property = r.U32();
        CheckAtom(property);
        if (window.Properties.Remove(property, out XProperty? removed))
        {
            ReleaseProperty(removed);
            SendPropertyNotify(window, property, deleted: true);
            OnTopLevelPropertyChanged(window, property);
        }
    }

    private void GetProperty(XClient c, XRequestReader r)
    {
        bool delete = r.Data != 0;
        XWindow window = Window(r.U32());
        uint property = r.U32();
        uint type = r.U32();
        uint longOffset = r.U32();
        uint longLength = r.U32();
        CheckAtom(property);
        if (type != 0)
        {
            CheckAtom(type);
        }

        if (!window.Properties.TryGetValue(property, out XProperty? prop))
        {
            c.Reply(0, w => w.U32(0).U32(0).U32(0).Zero(12));
            return;
        }
        if (type != 0 && type != prop.Type)
        {
            // 类型对不上:只报实际类型、格式与总长,不给值(协议规定)。
            c.Reply(prop.Format, w => w.U32(prop.Type).U32((uint)prop.Data.Length).U32(0).Zero(12));
            return;
        }

        long n = prop.Data.Length;
        long i = 4L * longOffset;
        long t = n - i;
        if (t < 0)
        {
            throw new XProtocolError(XErrorCode.Value, longOffset);
        }
        long l = Math.Min(t, 4L * longLength);
        long after = n - (i + l);
        byte[] value = prop.Data.Slice((int)i, (int)l).ToArray();
        if (c.BigEndian && prop.Format != 8)
        {
            // 本机序 → 大端:与 ToNativeOrder 互逆。
            if (prop.Format == 16)
            {
                SwapUnits(value, 16);
            }
            else
            {
                for (int k = 0; k + 3 < value.Length; k += 4)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(value.AsSpan(k), BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(k)));
                }
            }
        }
        uint units = (uint)(l / (prop.Format / 8));
        c.Reply(prop.Format, w => w.U32(prop.Type).U32((uint)after).U32(units).Zero(12).Bytes(value).Pad4());

        if (delete && after == 0)
        {
            window.Properties.Remove(property);
            ReleaseProperty(prop);
            SendPropertyNotify(window, property, deleted: true);
            OnTopLevelPropertyChanged(window, property);   // 与 DeleteProperty 一样:宿主那边的标题 / 提示跟着变
        }
    }

    private void ListProperties(XClient c, XRequestReader r)
    {
        XWindow window = Window(r.U32());
        uint[] atoms = [.. window.Properties.Keys];
        c.Reply(0, w =>
        {
            w.U16((ushort)atoms.Length).Zero(22);
            foreach (uint a in atoms)
            {
                w.U32(a);
            }
        });
    }

    private void RotateProperties(XRequestReader r)
    {
        XWindow window = Window(r.U32());
        int count = r.U16();
        int delta = r.I16();
        uint[] atoms = new uint[count];
        HashSet<uint> seen = [with(count)];   // 查重复:原先每个都往前 Array.IndexOf 一遍,65535 个名字就是二十亿次比较
        for (int i = 0; i < count; i++)
        {
            atoms[i] = r.U32();
            CheckAtom(atoms[i]);
            // 属性不存在,或者同一个名字在列表里出现不止一次:Match(协议「RotateProperties」)。
            if (!window.Properties.ContainsKey(atoms[i]) || !seen.Add(atoms[i]))
            {
                throw new XProtocolError(XErrorCode.Match, atoms[i]);
            }
        }
        if (count == 0 || delta % count == 0)
        {
            return;
        }
        var values = new XProperty[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = window.Properties[atoms[i]];
        }
        for (int i = 0; i < count; i++)
        {
            window.Properties[atoms[(((i + delta) % count) + count) % count]] = values[i];
        }
        foreach (uint a in atoms)
        {
            SendPropertyNotify(window, a, deleted: false);
            OnTopLevelPropertyChanged(window, a);
        }
    }

    private void SendPropertyNotify(XWindow window, uint atom, bool deleted)
    {
        uint time = Now;
        DeliverToSelectors(window, XEventMask.PropertyChange, c =>
            c.Event(XEventCode.PropertyNotify, 0, w => w.U32(window.Id).U32(atom).U32(time).U8(deleted ? (byte)1 : (byte)0)));
        if (ReferenceEquals(window, _selectionWindow))
        {
            OnSelectionWindowProperty(atom, deleted);
        }
        else if (deleted && _outgoingIncr.Count != 0)
        {
            OnIncrPropertyDeleted(window, atom);   // 服务端当剪贴板属主的 INCR 传输:请求方取走了一块
        }
    }

    // ------------------------------------------------------------------ 选区

    private void SetSelectionOwner(XClient c, XRequestReader r)
    {
        uint ownerId = r.U32();
        uint selection = r.U32();
        uint time = r.U32();
        CheckAtom(selection);
        XWindow? owner = ownerId == 0 ? null : Window(ownerId);
        uint now = Now;
        if (time == 0)
        {
            time = now;
        }
        SelectionSlot slot = SlotOf(selection, c);   // 按会话隔离的选区:只动这个客户端所在会话的那一份
        // 时间早于最后一次换属主的时间,或晚于服务端当前时间:忽略(协议规定)。两条都与当前有没有属主无关。
        if (unchecked((int)(time - now)) > 0
            || (_selectionLastChange.TryGetValue(slot, out uint lastChange) && unchecked((int)(time - lastChange)) < 0))
        {
            return;
        }
        _selectionLastChange[slot] = time;
        if (_selections.TryGetValue(slot, out (XWindow Window, XClient? Client, uint Time) current))
        {
            if (!ReferenceEquals(current.Client, c) || owner is null)
            {
                XWindow old = current.Window;
                current.Client?.Event(XEventCode.SelectionClear, 0, w => w.U32(time).U32(old.Id).U32(selection));
            }
        }
        if (owner is null)
        {
            _selections.Remove(slot);
        }
        else
        {
            _selections[slot] = (owner, c, time);
            NoteClientSelection(slot);
        }
        NotifySelectionChange(selection, 0, ownerId, time, client => InScope(client, slot));
        if (owner is null)
        {
            OnSelectionOwnerLost(slot);
        }
        else
        {
            OnClientTookSelection(c, owner, slot, time);
        }
    }

    private void GetSelectionOwner(XClient c, XRequestReader r)
    {
        uint selection = r.U32();
        CheckAtom(selection);
        uint owner = _selections.TryGetValue(SlotOf(selection, c), out (XWindow Window, XClient? Client, uint Time) s) ? s.Window.Id : 0;
        c.Reply(0, w => w.U32(owner).Zero(20));
    }

    private void ConvertSelection(XClient c, XRequestReader r)
    {
        XWindow requestor = Window(r.U32());
        uint selection = r.U32();
        uint target = r.U32();
        uint property = r.U32();
        uint time = r.U32();
        CheckAtom(selection);
        CheckAtom(target);
        if (property != 0)
        {
            // property 是 ATOM 或 None(协议「ConvertSelection」):不存在的原子回 BadAtom。原先不查,宿主占着剪贴板时服务端
            // 拿它当属性名写到请求方给的窗口上(可以是根窗口),之后 xprop -root 之类列属性的都收到 BadAtom。
            CheckAtom(property);
        }
        if (_selections.TryGetValue(SlotOf(selection, c), out (XWindow Window, XClient? Client, uint Time) owner))
        {
            if (owner.Client is null)
            {
                // 属主是服务端自己:宿主的拖放(XdndSelection),或宿主的剪贴板。
                if (IsHostXdndSelection(selection))
                {
                    ServeXdndSelection(c, requestor, selection, target, property, time, owner.Time);
                    return;
                }
                ServeSelection(c, requestor, selection, target, property, time, owner.Time);
                return;
            }
            owner.Client.Event(XEventCode.SelectionRequest, 0, w => w
                .U32(time).U32(owner.Window.Id).U32(requestor.Id).U32(selection).U32(target).U32(property));
            return;
        }
        // 没有属主:直接回一个 property = None 的 SelectionNotify。
        c.Event(XEventCode.SelectionNotify, 0, w => w.U32(time).U32(requestor.Id).U32(selection).U32(target).U32(0));
    }

    // ------------------------------------------------------------------ SendEvent

    /// <summary>
    /// SendEvent 能发的事件码:核心事件(2–34)与已登记扩展的事件(协议「SendEvent」:别的码一律 BadValue)。
    /// 0 是错误、1 是回复;35 GenericEvent 不行 —— 它的长度字段说后面还跟着几个 4 字节,SendEvent 只带 32 字节,
    /// 收到的客户端会按长度字段往后多读,从此整条协议流错位;没声明过 Generic Event 版本的客户端本来也不该收到它。
    /// </summary>
    private bool IsSendableEvent(byte code)
    {
        if (code is >= 2 and <= XEventCode.MappingNotify)
        {
            return true;
        }
        foreach (Extension extension in _extensionList)
        {
            if (extension.EventCount > 0 && code >= extension.FirstEvent && code < extension.FirstEvent + extension.EventCount)
            {
                return true;
            }
        }
        return false;
    }

    private void SendEvent(XClient c, XRequestReader r)
    {
        bool propagate = r.Data != 0;
        uint destination = r.U32();
        uint mask = r.U32();
        byte[] raw = r.Bytes(32);
        byte code = (byte)(raw[0] & 0x7F);
        if (!IsSendableEvent(code))
        {
            throw new XProtocolError(XErrorCode.Value, code);
        }

        XWindow? target = destination switch
        {
            0 => _pointerWindow,                                   // PointerWindow
            1 => FocusTargetForSendEvent(),                       // InputFocus
            _ => Window(destination),
        };
        if (target is null)
        {
            return;
        }
        if (ReferenceEquals(target, Root) && code == XEventCode.ClientMessage)
        {
            OnRootClientMessage(c, raw);   // 发给窗口管理器的请求;之后照常投递(没有别的客户端会重定向根窗口)
        }
        if (ReferenceEquals(target, _selectionWindow))
        {
            // 发给服务端自己的请求窗口:拖放的目标回的 XdndStatus / XdndFinished(它也是宿主拖放的源窗口),
            // 或者选区属主回的 SelectionNotify。
            if (!OnXdndClientMessage(raw, c.BigEndian) && !OnSystemTrayClientMessage(raw, c.BigEndian))
            {
                OnSelectionWindowEvent(raw, c.BigEndian);
            }
            return;
        }

        if (mask == 0)
        {
            // 空掩码:发给创建目标窗口的客户端(协议规定);那个客户端已经不在了就算了。
            if (target.Owner is { Closed: false } creator)
            {
                creator.Send(ReencodeEvent(raw, c.BigEndian, creator));
            }
            return;
        }

        for (XWindow? w = target; w is not null; w = w.Parent)
        {
            bool delivered = false;
            foreach ((XClient client, uint selected) in w.EventSelections)
            {
                if ((selected & mask) != 0)
                {
                    client.Send(ReencodeEvent(raw, c.BigEndian, client));
                    delivered = true;
                }
            }
            if (delivered || !propagate || (w.DoNotPropagateMask & mask) != 0)
            {
                return;
            }
        }
    }

    private XWindow? FocusTargetForSendEvent() => _focus switch
    {
        null => null,
        { } f when ReferenceEquals(f, Root) => _pointerWindow,   // PointerRoot
        { } f => _pointerWindow.IsDescendantOf(f) ? _pointerWindow : f,
    };

    /// <summary>
    /// SendEvent 带来的是发送方字节序的 32 字节;收方字节序不同时要按事件布局逐字段翻转,
    /// 序号换成收方的,事件码置 sent 位。
    /// </summary>
    private static byte[] ReencodeEvent(byte[] raw, bool fromBigEndian, XClient to)
    {
        byte[] e = (byte[])raw.Clone();
        e[0] |= XEventCode.SentFlag;
        byte code = (byte)(raw[0] & 0x7F);
        if (fromBigEndian != to.BigEndian)
        {
            foreach ((int offset, int size) in EventLayout(code, raw[1]))
            {
                if (size == 2)
                {
                    (e[offset], e[offset + 1]) = (e[offset + 1], e[offset]);
                }
                else if (size == 4)
                {
                    Array.Reverse(e, offset, 4);
                }
            }
        }
        if (code != XEventCode.KeymapNotify)
        {
            if (to.BigEndian)
            {
                BinaryPrimitives.WriteUInt16BigEndian(e.AsSpan(2), to.Sequence);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(2), to.Sequence);
            }
        }
        return e;
    }

    /// <summary>核心事件序号之后各多字节字段的 (偏移, 宽度)。未知事件(扩展)不翻转,原样转发。</summary>
    private static List<(int Offset, int Size)> EventLayout(byte code, byte detail)
    {
        switch (code)
        {
            case >= XEventCode.KeyPress and <= XEventCode.LeaveNotify:
                return [(4, 4), (8, 4), (12, 4), (16, 4), (20, 2), (22, 2), (24, 2), (26, 2), (28, 2)];
            case XEventCode.FocusIn or XEventCode.FocusOut:
                return [(4, 4)];
            case XEventCode.Expose:
                return [(4, 4), (8, 2), (10, 2), (12, 2), (14, 2), (16, 2)];
            case XEventCode.GraphicsExposure:
                return [(4, 4), (8, 2), (10, 2), (12, 2), (14, 2), (16, 2), (18, 2)];
            case XEventCode.NoExposure:
                return [(4, 4), (8, 2)];
            case XEventCode.VisibilityNotify or XEventCode.ResizeRequest:
                return code == XEventCode.ResizeRequest ? [(4, 4), (8, 2), (10, 2)] : [(4, 4)];
            case XEventCode.CreateNotify:
                return [(4, 4), (8, 4), (12, 2), (14, 2), (16, 2), (18, 2), (20, 2)];
            case XEventCode.DestroyNotify or XEventCode.UnmapNotify or XEventCode.MapNotify or XEventCode.MapRequest:
                return [(4, 4), (8, 4)];
            case XEventCode.ReparentNotify:
                return [(4, 4), (8, 4), (12, 4), (16, 2), (18, 2)];
            case XEventCode.ConfigureNotify:
                return [(4, 4), (8, 4), (12, 4), (16, 2), (18, 2), (20, 2), (22, 2), (24, 2)];
            case XEventCode.ConfigureRequest:
                return [(4, 4), (8, 4), (12, 4), (16, 2), (18, 2), (20, 2), (22, 2), (24, 2), (26, 2)];
            case XEventCode.GravityNotify:
                return [(4, 4), (8, 4), (12, 2), (14, 2)];
            case XEventCode.CirculateNotify or XEventCode.CirculateRequest:
                return [(4, 4), (8, 4), (12, 4)];
            case XEventCode.PropertyNotify:
                return [(4, 4), (8, 4), (12, 4)];
            case XEventCode.SelectionClear:
                return [(4, 4), (8, 4), (12, 4)];
            case XEventCode.SelectionRequest:
                return [(4, 4), (8, 4), (12, 4), (16, 4), (20, 4), (24, 4)];
            case XEventCode.SelectionNotify:
                return [(4, 4), (8, 4), (12, 4), (16, 4), (20, 4)];
            case XEventCode.ColormapNotify:
                return [(4, 4), (8, 4)];
            case XEventCode.ClientMessage:
                // detail 字节就是 format:决定 20 字节数据怎么翻。
                List<(int, int)> fields = [(4, 4), (8, 4)];
                if (detail == 16)
                {
                    for (int i = 12; i < 32; i += 2)
                    {
                        fields.Add((i, 2));
                    }
                }
                else if (detail == 32)
                {
                    for (int i = 12; i < 32; i += 4)
                    {
                        fields.Add((i, 4));
                    }
                }
                return fields;
            default:
                return [];
        }
    }

    private void KillClient(XRequestReader r)
    {
        uint id = r.U32();
        if (id == 0)
        {
            DestroyRetainedTemporaryClients();   // AllTemporary
            return;
        }
        if (Lookup<XResource>(id) is not { Owner: { } owner })
        {
            throw new XProtocolError(XErrorCode.Value, id);
        }
        KillClientOf(owner);   // 已经以 Retain 模式断开的:销毁它留下的全部资源
    }
}
