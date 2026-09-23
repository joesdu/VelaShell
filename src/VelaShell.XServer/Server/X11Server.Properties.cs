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
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer.Server;

public sealed partial class X11Server
{
    private readonly Dictionary<string, uint> _atomsByName = new(StringComparer.Ordinal);
    private readonly List<string> _atomNames = [];
    private readonly Dictionary<uint, (XWindow Window, XClient? Client, uint Time)> _selections = [];

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
        uint atom = onlyIfExists ? _atomsByName.GetValueOrDefault(name) : Intern(name);
        c.Reply(0, w => w.U32(atom).Zero(20));
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
        byte[] data = ToNativeOrder(r.Bytes((int)byteCount), format, c.BigEndian);

        if (mode != 0 && window.Properties.TryGetValue(property, out XProperty? existing))
        {
            if (existing.Type != type || existing.Format != format)
            {
                throw new XProtocolError(XErrorCode.Match);
            }
            data = mode == 1 ? [.. data, .. existing.Data] : [.. existing.Data, .. data];
        }
        window.Properties[property] = new XProperty(type, format, data);
        SendPropertyNotify(window, property, deleted: false);
        OnTopLevelPropertyChanged(window, property);
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
        if (window.Properties.Remove(property))
        {
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
        byte[] value = prop.Data.AsSpan((int)i, (int)l).ToArray();
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
            SendPropertyNotify(window, property, deleted: true);
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
        for (int i = 0; i < count; i++)
        {
            atoms[i] = r.U32();
            CheckAtom(atoms[i]);
            if (!window.Properties.ContainsKey(atoms[i]))
            {
                throw new XProtocolError(XErrorCode.Match, atoms[i]);
            }
        }
        if (count == 0 || delta % count == 0)
        {
            return;
        }
        XProperty[] values = [.. atoms.Select(a => window.Properties[a])];
        for (int i = 0; i < count; i++)
        {
            window.Properties[atoms[(((i + delta) % count) + count) % count]] = values[i];
        }
        foreach (uint a in atoms)
        {
            SendPropertyNotify(window, a, deleted: false);
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
        if (_selections.TryGetValue(selection, out var current))
        {
            // 时间早于当前属主的获取时间,或晚于服务端当前时间:忽略(协议规定)。
            if (unchecked((int)(time - current.Time)) < 0 || unchecked((int)(time - now)) > 0)
            {
                return;
            }
            if (!ReferenceEquals(current.Client, c) || owner is null)
            {
                XWindow old = current.Window;
                current.Client?.Event(XEventCode.SelectionClear, 0, w => w.U32(time).U32(old.Id).U32(selection));
            }
        }
        if (owner is null)
        {
            _selections.Remove(selection);
        }
        else
        {
            _selections[selection] = (owner, c, time);
        }
        NotifySelectionChange(selection, 0, ownerId, time);
        if (owner is not null)
        {
            OnClientTookSelection(c, owner, selection, time);
        }
    }

    private void GetSelectionOwner(XClient c, XRequestReader r)
    {
        uint selection = r.U32();
        CheckAtom(selection);
        uint owner = _selections.TryGetValue(selection, out var s) ? s.Window.Id : 0;
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
        if (_selections.TryGetValue(selection, out var owner))
        {
            if (owner.Client is null)
            {
                // 属主是服务端自己(宿主的剪贴板)。
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

    private void SendEventRequest(XClient c, XRequestReader r)
    {
        bool propagate = r.Data != 0;
        uint destination = r.U32();
        uint mask = r.U32();
        byte[] raw = r.Bytes(32);
        byte code = (byte)(raw[0] & 0x7F);
        if (code < 2)
        {
            // 0 是错误、1 是回复,都不是事件。
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
            // 发给服务端自己的请求窗口:这是选区属主回的 SelectionNotify。
            OnSelectionWindowEvent(raw, c.BigEndian);
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
            return;   // AllTemporary:没有 RetainTemporary 的资源,什么都不做
        }
        if (Lookup<XResource>(id) is not { Owner: { } owner })
        {
            throw new XProtocolError(XErrorCode.Value, id);
        }
        owner.Abort();
        DisconnectClient(owner);
    }
}
