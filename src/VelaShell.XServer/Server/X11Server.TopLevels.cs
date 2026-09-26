// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   ICCCM 2.0 —— §4.1.2.1 WM_NAME、§4.1.2.5 WM_CLASS、§4.1.2.6 WM_TRANSIENT_FOR、§4.1.2.7 WM_PROTOCOLS、
//   §4.1.5(窗口管理器移动顶层后发合成的 ConfigureNotify,根坐标)、§4.2.8(WM_DELETE_WINDOW)
//   EWMH 1.5 —— §5「Application Window Properties」(_NET_WM_NAME)
//   架构:velashell-docs/zh/xserver/design/architecture.md §6(rootless:宿主就是窗口管理器)
//
//   服务端与宿主之间关于顶层窗口的一切:给宿主的句柄与快照、快照的变化、损伤的交付,
//   以及宿主作为窗口管理器对顶层做的动作(焦点、移动、缩放、关闭、状态、外框)。

using System.Runtime.InteropServices;
using System.Text;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    // ------------------------------------------------------------------ 句柄与快照

    private XTopLevelWindow HandleFor(XWindow top)
    {
        if (!_topLevelHandles.TryGetValue(top, out XTopLevelWindow? handle))
        {
            handle = new XTopLevelWindow(top, _pixelGate);
            _topLevelHandles[top] = handle;
        }
        return handle;
    }

    /// <summary>从窗口与它的 ICCCM / EWMH 属性重建宿主看到的快照,返回比上一份变了哪几组。</summary>
    private XTopLevelChanges RefreshSnapshot(XWindow top, XTopLevelWindow handle)
    {
        XTopLevelSnapshot previous = handle.Snapshot;
        XTopLevelSnapshot next = BuildSnapshot(top, previous);
        handle.Snapshot = next;
        return Diff(previous, next);
    }

    /// <summary>顶层窗口的几何、形状或属性可能变了:刷新快照,真有变化且映射中时告诉宿主。</summary>
    private void RefreshTopLevel(XWindow top)
    {
        if (_topLevelHandles.TryGetValue(top, out XTopLevelWindow? handle)
            && RefreshSnapshot(top, handle) is var changes and not XTopLevelChanges.None
            && top.Mapped)
        {
            _host.TopLevelChanged(handle, changes);
        }
    }

    /// <summary>顶层窗口的属性变了:标题、类名、协议、提示可能跟着变。</summary>
    private void OnTopLevelPropertyChanged(XWindow window, uint property)
    {
        if (window.IsTopLevel && AffectsHandle(property))   // _NET_WM_USER_TIME 之类每次输入都改,与宿主无关
        {
            RefreshTopLevel(window);
        }
    }

    private static void SetMapped(XTopLevelWindow handle, bool mapped) => handle.Snapshot = handle.Snapshot with { IsMapped = mapped };

    private XTopLevelSnapshot BuildSnapshot(XWindow top, XTopLevelSnapshot previous)
    {
        Dictionary<uint, XProperty> props = top.Properties;
        string title = props.TryGetValue(_netWmNameAtom, out XProperty? utf8) && utf8.Format == 8
            ? Encoding.UTF8.GetString(utf8.Data)
            : props.TryGetValue(XAtom.WmName, out XProperty? name) && name.Format == 8
                ? XWire.Latin1.GetString(name.Data)
                : "";

        string className = "";
        if (props.TryGetValue(XAtom.WmClass, out XProperty? cls) && cls.Format == 8)
        {
            // WM_CLASS = "instance\0class\0"
            string[] parts = XWire.Latin1.GetString(cls.Data).Split('\0');
            className = parts.Length > 1 ? parts[1] : parts[0];
        }

        uint transientId = props.TryGetValue(XAtom.WmTransientFor, out XProperty? transient) && transient is { Format: 32, Data.Length: >= 4 }
            ? BitConverter.ToUInt32(transient.Data, 0)
            : 0;
        XTopLevelWindow? transientFor = transientId != 0 && Lookup<XWindow>(transientId) is { IsTopLevel: true } parent && !ReferenceEquals(parent, top)
            ? HandleFor(parent)
            : null;

        IReadOnlyList<XRect>? shape = top.BoundingShape is { } bounding
            ? [.. bounding.Clone().Intersect(new XRect(0, 0, top.Width, top.Height)).Rects]
            : null;
        if (shape is not null && previous.Shape is not null && shape.SequenceEqual(previous.Shape))
        {
            shape = previous.Shape;   // 形状没变就沿用上一份:宿主按引用判断要不要整窗重画
        }

        XTopLevelSnapshot snapshot = previous with
        {
            X = top.X,
            Y = top.Y,
            Width = top.Width,
            Height = top.Height,
            Title = title,
            ClassName = className,
            OverrideRedirect = top.OverrideRedirect,
            TransientFor = transientFor,
            SupportsDeleteWindow = SupportsDeleteWindow(top),
            HasAlpha = top.Depth == 32,
            Shape = shape,
        };
        return ReadWindowManagerHints(top, snapshot, hasTransientFor: transientId != 0);
    }

    /// <summary>两份快照之间哪几组字段不同;不属于前几组的字段一律算 <see cref="XTopLevelChanges.Hints" />。</summary>
    private static XTopLevelChanges Diff(XTopLevelSnapshot a, XTopLevelSnapshot b)
    {
        XTopLevelChanges changes = XTopLevelChanges.None;
        if (a.X != b.X || a.Y != b.Y || a.Width != b.Width || a.Height != b.Height)
        {
            changes |= XTopLevelChanges.Geometry;
        }
        if (a.Title != b.Title || a.ClassName != b.ClassName)
        {
            changes |= XTopLevelChanges.Title;
        }
        if (a.States != b.States)
        {
            changes |= XTopLevelChanges.States;
        }
        if (!ReferenceEquals(a.Icons, b.Icons))
        {
            changes |= XTopLevelChanges.Icons;
        }
        if (!ReferenceEquals(a.Shape, b.Shape))
        {
            changes |= XTopLevelChanges.Shape;
        }
        // 把已经比过的字段抹平之后整份比较:以后加的字段自动归进 Hints,不会漏报。
        XTopLevelSnapshot rest = a with
        {
            X = b.X,
            Y = b.Y,
            Width = b.Width,
            Height = b.Height,
            IsMapped = b.IsMapped,
            Title = b.Title,
            ClassName = b.ClassName,
            States = b.States,
            Icons = b.Icons,
            Shape = b.Shape,
        };
        if (rest != b)
        {
            changes |= XTopLevelChanges.Hints;
        }
        return changes;
    }

    /// <summary>客户端在 WM_PROTOCOLS 里声明了 WM_DELETE_WINDOW。</summary>
    private bool SupportsDeleteWindow(XWindow top) =>
        top.Properties.TryGetValue(_wmProtocolsAtom, out XProperty? p) && p.Format == 32
        && MemoryMarshal.Cast<byte, uint>(p.Data.AsSpan(0, p.Data.Length & ~3)).Contains(_wmDeleteWindowAtom);

    /// <summary>把这一批攒下的损伤一次性交给宿主(每个顶层合并成一组矩形)。</summary>
    private void FlushDamage()
    {
        if (_damage.Count == 0)
        {
            return;
        }
        KeyValuePair<XWindow, List<XRect>>[] batch = [.. _damage];
        _damage.Clear();
        foreach ((XWindow top, List<XRect> rects) in batch)
        {
            if (top.Mapped && _topLevelHandles.TryGetValue(top, out XTopLevelWindow? handle))
            {
                _host.TopLevelDamaged(handle, rects);
            }
        }
    }

    // ------------------------------------------------------------------ 宿主作为窗口管理器的动作(见 X11Server.cs 的公开方法)

    /// <summary>键盘焦点给 <paramref name="top" />;null = 所有顶层都失去焦点(焦点 None)。</summary>
    private void ApplyFocus(XWindow? top)
    {
        if (top is null)
        {
            SetFocus(null, 0);
            return;
        }
        if (top.IsViewable && (_focus is null || ReferenceEquals(_focus, Root) || !ReferenceEquals(_focus.TopLevel, top)))
        {
            // 与窗口管理器的做法一致:把焦点给顶层,revert-to PointerRoot。客户端之后可以自己把焦点挪到子窗口。
            SetFocus(top, 1);
        }
    }

    /// <summary>原生窗口被用户挪了:改位置,并按 ICCCM §4.1.5 发一条合成的 ConfigureNotify(根坐标)。</summary>
    private void ApplyMove(XWindow top, int x, int y)
    {
        if (top.X == x && top.Y == y)
        {
            return;
        }
        top.X = x;
        top.Y = y;
        if (_topLevelHandles.TryGetValue(top, out XTopLevelWindow? handle))
        {
            handle.Snapshot = handle.Snapshot with { X = x, Y = y };   // 宿主自己挪的,不再回报
        }
        DeliverToSelectors(top, XEventMask.StructureNotify, c => c.Event(XEventCode.ConfigureNotify, 0, w => w
            .U32(top.Id).U32(top.Id).U32(0).I16(x).I16(y).U16((ushort)top.Width).U16((ushort)top.Height)
            .U16((ushort)top.BorderWidth).Bool(top.OverrideRedirect), sent: true));
    }

    private void ApplyResize(XWindow top, int width, int height)
    {
        if (top.Width != width || top.Height != height)
        {
            Configure(top, top.X, top.Y, width, height, top.BorderWidth, null, -1);
        }
    }

    /// <summary>关闭:声明了 WM_DELETE_WINDOW 就发 ClientMessage 请它自己关(ICCCM §4.2.8),否则断开它的客户端。</summary>
    private void ApplyClose(XWindow top)
    {
        if (top.Owner is not { } owner)
        {
            return;
        }
        if (SupportsDeleteWindow(top))
        {
            uint time = Now;
            owner.Event(XEventCode.ClientMessage, 32, w => w.U32(top.Id).U32(_wmProtocolsAtom).U32(_wmDeleteWindowAtom).U32(time).Zero(12), sent: true);
            return;
        }
        owner.Abort();
        DisconnectClient(owner);
    }

    /// <summary>宿主设定的窗口状态写进 _NET_WM_STATE / WM_STATE;Focused 位由服务端按焦点维护,保留现值。</summary>
    private void ApplyStates(XWindow top, XWindowStates states)
    {
        XWindowStates focused = ReadNetWmStates(top) & XWindowStates.Focused;
        WriteStates(top, (states & ~XWindowStates.Focused) | focused);
    }

    private void ApplyFrameExtents(XWindow top, XFrameExtents extents)
    {
        _frameExtents[top] = extents;
        WriteFrameExtents(top);
    }
}
