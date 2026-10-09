// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   freedesktop 的 XDND 协议,第 5 版 —— 「Atoms and Properties」(XdndAware、XdndSelection、XdndTypeList、XdndProxy、
//   XdndActionCopy)、「Client Messages」(XdndEnter / XdndPosition / XdndStatus / XdndLeave / XdndDrop / XdndFinished 的字段)、
//   「Theory」(等 XdndStatus 再发下一条 XdndPosition、松手时等最后一条 XdndStatus、收不到就 XdndLeave)
//   X Window System Protocol, X Version 11 —— 「SendEvent」(事件掩码为空时发给建窗口的客户端)、「ConvertSelection」
//   ICCCM §2(选区转换:TARGETS、TIMESTAMP)

using System.Buffers.Binary;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

/// <summary>
/// 宿主那边的拖放(本机的文件、文本)拖进 X 窗口:服务端替宿主扮演 XDND 的源 —— 源窗口是服务端自己的选区窗口,
/// 它占住 XdndSelection,按指针所在的顶层找声明了 XdndAware 的目标,发 XdndEnter / XdndPosition,等目标回 XdndStatus,
/// 松手时发 XdndDrop,目标经 ConvertSelection 取宿主交来的数据,用完回 XdndFinished。X 程序之间的拖放只靠核心协议,不经过这里。
/// </summary>
public sealed partial class X11Server
{
    /// <summary>服务端这一侧说的 XDND 版本(协议的当前版本)。目标声明的版本更低时取两者的小者;低于 3 的不当目标(XdndAware 从第 3 版起)。</summary>
    internal const int XdndVersion = 5;

    /// <summary>松手时最后一条 XdndStatus 还没回来,最多等这么久(之后发 XdndLeave,协议:「within a reasonable amount of time」)。</summary>
    internal TimeSpan XdndStatusTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>放下之后目标一直不回 XdndFinished,数据最多留这么久(协议:「The source must also be prepared to throw out extremely old data」)。</summary>
    internal TimeSpan XdndDataLifetime { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>进行中的这一次拖放;没有为 null。</summary>
    private XdndDrag? _drag;

    /// <summary>放下了、等目标取数据的那一份(目标回 XdndFinished、下一次拖放开始或放久了就丢掉)。</summary>
    private XdndDrop? _dropped;

    /// <summary>宿主读的反馈:当前目标说会接受(宿主据此显示「可以放」的光标)。在执行线程上写,宿主任意线程上读。</summary>
    private volatile bool _dragAccepted;

    private int _xdndEpoch;

    /// <summary>一次拖放:类型、当前目标、流控状态。</summary>
    private sealed class XdndDrag(uint[] types, int epoch)
    {
        public uint[] Types { get; } = types;

        public int Epoch { get; } = epoch;

        /// <summary>指针所在、声明了 XdndAware 的窗口(消息里写的窗口);没有为 null。</summary>
        public XWindow? Target { get; set; }

        /// <summary>消息实际投给的窗口:目标设了 XdndProxy 时是代理窗口,否则就是目标。</summary>
        public XWindow? Recipient { get; set; }

        public int Version { get; set; }

        /// <summary>发了 XdndPosition、还没等到 XdndStatus。</summary>
        public bool AwaitingStatus { get; set; }

        /// <summary>等 XdndStatus 期间又动了:回来之后要补发的位置(根坐标)。</summary>
        public (int X, int Y)? PendingPosition { get; set; }

        public bool Accepted { get; set; }

        /// <summary>松手了、等最后一条 XdndStatus:等到之后按它放下或离开。</summary>
        public XdndDrop? PendingDrop { get; set; }
    }

    /// <summary>放下的那一份:数据(类型原子 → 字节)、交给的目标、选区的时间。</summary>
    private sealed record XdndDrop(Dictionary<uint, byte[]> Data, uint[] Types, uint Time)
    {
        public XWindow? Target { get; set; }
    }

    // ================================================================== 宿主的动作(见 X11Server.cs 的公开方法)

    /// <summary>拖着东西在顶层 <paramref name="top" /> 的内区 (x, y) 上:没开始就开始,换了窗口就离开旧的、进入新的,然后报位置。</summary>
    private void ApplyDragOver(XWindow top, int x, int y, IReadOnlyList<string> types)
    {
        uint[] atoms = [.. types.Select(Intern)];
        if (_drag is null || !_drag.Types.AsSpan().SequenceEqual(atoms))
        {
            EndDrag(sendLeave: true);
            BeginDrag(atoms);
        }
        XdndDrag drag = _drag!;
        int rootX = top.X + top.BorderWidth + x, rootY = top.Y + top.BorderWidth + y;
        (XWindow? target, XWindow? recipient, int version) = XdndTargetAt(top, rootX, rootY);
        if (!ReferenceEquals(target, drag.Target))
        {
            SendXdndLeave(drag);
            drag.Target = target;
            drag.Recipient = recipient;
            drag.Version = version;
            drag.AwaitingStatus = false;
            drag.PendingPosition = null;
            SetDragAccepted(drag, false);
            if (target is not null)
            {
                SendXdndEnter(drag);
            }
        }
        if (drag.Target is null)
        {
            return;
        }
        if (drag.AwaitingStatus)
        {
            drag.PendingPosition = (rootX, rootY);   // 协议:等 XdndStatus 期间动了先记下,回来再补一条
            return;
        }
        SendXdndPosition(drag, rootX, rootY);
    }

    /// <summary>拖着的东西离开了这个顶层(或宿主那边取消了拖放)。</summary>
    private void ApplyDragLeave() => EndDrag(sendLeave: true);

    /// <summary>在顶层 <paramref name="top" /> 的内区 (x, y) 上松手:先按最后的位置更新一次,再放下(或等最后一条 XdndStatus)。</summary>
    private void ApplyDrop(XWindow top, int x, int y, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> data)
    {
        ApplyDragOver(top, x, y, [.. data.Keys]);
        XdndDrag drag = _drag!;
        Dictionary<uint, byte[]> converted = [];
        foreach ((string type, ReadOnlyMemory<byte> bytes) in data)
        {
            converted[Intern(type)] = bytes.ToArray();
        }
        XdndDrop drop = new(converted, drag.Types, Now);
        if (drag.Target is null)
        {
            EndDrag(sendLeave: false);   // 放在了不接受拖放的地方
            return;
        }
        if (drag.AwaitingStatus)
        {
            // 协议:松手时最后一条 XdndStatus 还没回来就等它;等不到就离开。
            drag.PendingDrop = drop;
            int epoch = drag.Epoch;
            _ = DelayThenPostAsync((uint)XdndStatusTimeout.TotalMilliseconds, () =>
            {
                if (_drag is { } stillWaiting && stillWaiting.Epoch == epoch && stillWaiting.PendingDrop is not null)
                {
                    EndDrag(sendLeave: true);
                }
            }, _lifetime.Token);
            return;
        }
        FinishDrop(drag, drop);
    }

    // ================================================================== 源这一侧

    private void BeginDrag(uint[] types)
    {
        _dropped = null;
        XdndDrag drag = new(types, ++_xdndEpoch);
        _drag = drag;
        uint now = Now;
        uint selection = Intern("XdndSelection");
        SelectionSlot slot = new(selection, null);
        if (_selections.TryGetValue(slot, out (XWindow Window, XClient? Client, uint Time) current) && current.Client is { } previous)
        {
            XWindow old = current.Window;
            previous.Event(XEventCode.SelectionClear, 0, w => w.U32(now).U32(old.Id).U32(selection));
        }
        _selections[slot] = (SelectionWindow, null, now);
        // 多于三种类型时目标要从源窗口的 XdndTypeList 读全(协议「XdndTypeList」);少于等于三种时也写上,无害。
        byte[] list = new byte[types.Length * 4];
        for (int i = 0; i < types.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(list.AsSpan(i * 4), types[i]);
        }
        StoreServerProperty(SelectionWindow, Intern("XdndTypeList"), new XProperty(XAtom.Atom, 32, list));
    }

    /// <summary>结束这一次拖放(不动已经放下、等目标取的那一份)。</summary>
    private void EndDrag(bool sendLeave)
    {
        if (_drag is not { } drag)
        {
            return;
        }
        if (sendLeave)
        {
            SendXdndLeave(drag);
        }
        _drag = null;
        SetDragAccepted(drag, false);
    }

    private void FinishDrop(XdndDrag drag, XdndDrop drop)
    {
        if (!drag.Accepted || drag.Target is null)
        {
            EndDrag(sendLeave: true);   // 协议:最后一条 XdndStatus 不接受就发 XdndLeave
            return;
        }
        drop.Target = drag.Target;
        _dropped = drop;
        SendXdndMessage(drag, "XdndDrop", SelectionWindowId, 0, drop.Time, 0, 0);
        _drag = null;
        SetDragAccepted(drag, false);
        // 目标一直不回 XdndFinished:数据不能一直留着。
        _ = DelayThenPostAsync((uint)XdndDataLifetime.TotalMilliseconds, () =>
        {
            if (ReferenceEquals(_dropped, drop))
            {
                _dropped = null;
            }
        }, _lifetime.Token);
    }

    private void SetDragAccepted(XdndDrag drag, bool accepted)
    {
        drag.Accepted = accepted;
        _dragAccepted = accepted && ReferenceEquals(_drag, drag);
    }

    /// <summary>
    /// 指针所在的接受拖放的窗口:从指针下最深的窗口往上找第一个设了 XdndAware(版本 ≥ 3)的;它设了有效的 XdndProxy 时,
    /// 消息投给代理(协议「XdndProxy」:代理窗口的 XdndProxy 要指向它自己,否则当作崩溃留下的残留、不理)。
    /// </summary>
    private (XWindow? Target, XWindow? Recipient, int Version) XdndTargetAt(XWindow top, int rootX, int rootY)
    {
        (int rx, int ry) = Root.AbsoluteInner();
        XWindow deepest = Hits(top, rx, ry, rootX, rootY)
            ? Descend(top, rx + top.X + top.BorderWidth, ry + top.Y + top.BorderWidth, rootX, rootY)
            : top;
        uint aware = Intern("XdndAware");
        for (XWindow? w = deepest; w is not null && !w.IsRoot; w = w.Parent)
        {
            XWindow recipient = XdndProxyOf(w) ?? w;
            if (recipient.Properties.TryGetValue(aware, out XProperty? property)
                && property is { Type: XAtom.Atom, Format: 32, Length: >= 4 })
            {
                int version = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(property.Data), XdndVersion);
                return version >= 3 ? (w, recipient, version) : (null, null, 0);
            }
        }
        return (null, null, 0);
    }

    private XWindow? XdndProxyOf(XWindow window)
    {
        uint proxyAtom = Intern("XdndProxy");
        if (window.Properties.TryGetValue(proxyAtom, out XProperty? property) && property is { Type: XAtom.Window, Format: 32, Length: >= 4 }
            && _resources.GetValueOrDefault(BinaryPrimitives.ReadUInt32LittleEndian(property.Data)) is XWindow proxy
            && proxy.Properties.TryGetValue(proxyAtom, out XProperty? self) && self is { Format: 32, Length: >= 4 }
            && BinaryPrimitives.ReadUInt32LittleEndian(self.Data) == proxy.Id)
        {
            return proxy;
        }
        return null;
    }

    private void SendXdndEnter(XdndDrag drag)
    {
        uint[] types = drag.Types;
        uint flags = ((uint)drag.Version << 24) | (types.Length > 3 ? 1u : 0u);
        SendXdndMessage(drag, "XdndEnter", SelectionWindowId, flags,
            types.Length > 0 ? types[0] : 0, types.Length > 1 ? types[1] : 0, types.Length > 2 ? types[2] : 0);
    }

    private void SendXdndPosition(XdndDrag drag, int rootX, int rootY)
    {
        uint position = ((uint)(ushort)rootX << 16) | (ushort)rootY;
        SendXdndMessage(drag, "XdndPosition", SelectionWindowId, 0, position, Now, Intern("XdndActionCopy"));
        drag.AwaitingStatus = true;
    }

    private void SendXdndLeave(XdndDrag drag)
    {
        if (drag.Target is not null)
        {
            SendXdndMessage(drag, "XdndLeave", SelectionWindowId, 0, 0, 0, 0);
        }
    }

    /// <summary>发给目标的 ClientMessage(格式 32):事件里的窗口是目标,投给它(或它的代理)的建窗口的客户端(SendEvent 掩码为空的语义)。</summary>
    private void SendXdndMessage(XdndDrag drag, string type, uint l0, uint l1, uint l2, uint l3, uint l4)
    {
        if (drag.Target is not { } target || drag.Recipient is not { } recipient
            || !_resources.ContainsKey(target.Id) || recipient.Owner is not { Closed: false } owner)
        {
            return;   // 目标已经没了(协议:收方崩溃时不许跟着出错)
        }
        uint atom = Intern(type);
        owner.Event(XEventCode.ClientMessage, 32, w => w.U32(target.Id).U32(atom).U32(l0).U32(l1).U32(l2).U32(l3).U32(l4), sent: true);
    }

    // ================================================================== 目标发回来的

    /// <summary>
    /// 发给服务端选区窗口的 ClientMessage:是 XdndStatus / XdndFinished 就处理并返回 true(别的交回原来的路径)。
    /// <paramref name="raw" /> 是发送方字节序的 32 字节事件。
    /// </summary>
    private bool OnXdndClientMessage(byte[] raw, bool bigEndian)
    {
        if ((raw[0] & 0x7F) != XEventCode.ClientMessage || raw[1] != 32)
        {
            return false;
        }
        uint Read(int offset) => bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(offset)) : BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offset));
        uint type = Read(8);
        uint l0 = Read(12), l1 = Read(16), l4 = Read(28);
        if (type == Intern("XdndStatus"))
        {
            // 协议:l0 是目标窗口 —— XdndLeave 之后才到的旧 XdndStatus 据此丢掉。
            if (_drag is { } drag && drag.Target?.Id == l0)
            {
                drag.AwaitingStatus = false;
                SetDragAccepted(drag, (l1 & 1) != 0 && l4 != 0);
                if (drag.PendingDrop is { } drop)
                {
                    FinishDrop(drag, drop);
                }
                else if (drag.PendingPosition is { } position)
                {
                    drag.PendingPosition = null;
                    SendXdndPosition(drag, position.X, position.Y);
                }
            }
            return true;
        }
        if (type == Intern("XdndFinished"))
        {
            if (_dropped is { Target: { } target } && target.Id == l0)
            {
                _dropped = null;   // 目标用完了,数据可以丢了
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// 目标要 XdndSelection 的内容(拖着时看一眼、放下之后取):TARGETS、TIMESTAMP 与宿主交来的各个类型。还没放下时没有数据,
    /// 只答得出 TARGETS(协议允许拖着时就看数据;宿主的文件要等放下才传到远端,这时给不出来)。
    /// </summary>
    private void ServeXdndSelection(XClient c, XWindow requestor, uint selection, uint target, uint property, uint time, uint ownerTime)
    {
        if (property == 0)
        {
            property = target;   // 旧式请求方(ICCCM §2.2)
        }
        uint[] types = _dropped?.Types ?? _drag?.Types ?? [];
        if (target == Intern("TARGETS"))
        {
            uint[] atoms = [Intern("TARGETS"), Intern("TIMESTAMP"), .. types];
            byte[] list = new byte[atoms.Length * 4];
            for (int i = 0; i < atoms.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(list.AsSpan(i * 4), atoms[i]);
            }
            WriteConverted(requestor, property, (XAtom.Atom, 32, list));
        }
        else if (target == Intern("TIMESTAMP"))
        {
            byte[] stamp = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(stamp, ownerTime);
            WriteConverted(requestor, property, (XAtom.Integer, 32, stamp));
        }
        else if (_dropped?.Data.TryGetValue(target, out byte[]? bytes) == true)
        {
            // 类型就是目标原子本身(MIME 类型、UTF8_STRING);STRING / TEXT 按 ICCCM 回 STRING / UTF8_STRING。
            uint type = target == XAtom.String ? XAtom.String : target == Intern("TEXT") ? Intern("UTF8_STRING") : target;
            WriteConverted(requestor, property, (type, 8, bytes));
        }
        else
        {
            property = 0;
        }
        XClient to = requestor.Owner is { Closed: false } creator ? creator : c;
        to.Event(XEventCode.SelectionNotify, 0, w => w.U32(time).U32(requestor.Id).U32(selection).U32(target).U32(property));
    }

    /// <summary>XdndSelection 的属主是服务端自己(宿主的拖放)吗。</summary>
    private bool IsHostXdndSelection(uint selection) => selection == Intern("XdndSelection");
}
