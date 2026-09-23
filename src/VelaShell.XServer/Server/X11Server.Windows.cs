// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 「CreateWindow」「ChangeWindowAttributes」「GetWindowAttributes」
//   「DestroyWindow」「DestroySubwindows」「ChangeSaveSet」「ReparentWindow」「MapWindow」「MapSubwindows」
//   「UnmapWindow」「UnmapSubwindows」「ConfigureWindow」(含 stack-mode 与 SubstructureRedirect 的改道)
//   「CirculateWindow」「GetGeometry」「QueryTree」「TranslateCoordinates」;
//   第 10 节「Events」里 CreateNotify / DestroyNotify / MapNotify / MapRequest / UnmapNotify / ReparentNotify /
//   ConfigureNotify / ConfigureRequest / CirculateNotify 的字段

using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer.Server;

public sealed partial class X11Server
{
    private void CreateWindow(XClient c, XRequestReader r)
    {
        byte depth = r.Data;
        uint id = r.U32();
        XWindow parent = Window(r.U32());
        short x = r.I16(), y = r.I16();
        ushort width = r.U16(), height = r.U16(), border = r.U16(), cls = r.U16();
        uint visual = r.U32();
        uint mask = r.U32();

        if (width == 0 || height == 0)
        {
            throw new XProtocolError(XErrorCode.Value, 0);
        }
        if (cls > 2)
        {
            throw new XProtocolError(XErrorCode.Value, cls);
        }
        if (cls == 0)
        {
            cls = parent.Class;
        }
        if (cls == 1 && parent.IsInputOnly)
        {
            throw new XProtocolError(XErrorCode.Match);
        }

        XWindow window = new(id, c, parent)
        {
            X = x,
            Y = y,
            Width = width,
            Height = height,
            BorderWidth = border,
            Class = cls,
            BorderPixel = parent.BorderPixel,
            Colormap = parent.Colormap,
        };

        if (cls == 2)
        {
            if (depth != 0 || border != 0)
            {
                throw new XProtocolError(XErrorCode.Match);
            }
            window.Depth = 0;
            window.Visual = visual == 0 ? parent.Visual : visual;
        }
        else
        {
            window.Depth = depth == 0 ? parent.Depth : depth;
            window.Visual = visual == 0 ? parent.Visual : visual;
            bool ok = (window.Depth == 24 && window.Visual == RootVisualId) || (window.Depth == 32 && window.Visual == ArgbVisualId);
            if (!ok)
            {
                throw new XProtocolError(XErrorCode.Match);
            }
        }

        ApplyWindowAttributes(c, window, mask, r);
        AddResource(c, window);
        parent.Children.Add(window);
        InvalidateVisibility();

        DeliverToSelectors(parent, XEventMask.SubstructureNotify, client =>
            client.Event(XEventCode.CreateNotify, 0, w => w
                .U32(parent.Id).U32(window.Id).I16(x).I16(y).U16(width).U16(height).U16(border)
                .Bool(window.OverrideRedirect)));
    }

    private void ChangeWindowAttributes(XClient c, XRequestReader r)
    {
        XWindow window = Window(r.U32());
        uint mask = r.U32();
        ApplyWindowAttributes(c, window, mask, r);
        if ((mask & (uint)XWindowAttrMask.Cursor) != 0)
        {
            UpdateCursor();
        }
    }

    private void ApplyWindowAttributes(XClient c, XWindow window, uint mask, XRequestReader r)
    {
        const uint inputOnlyAllowed = (uint)(XWindowAttrMask.WinGravity | XWindowAttrMask.OverrideRedirect
            | XWindowAttrMask.EventMask | XWindowAttrMask.DoNotPropagateMask | XWindowAttrMask.Cursor);
        if (window.IsInputOnly && (mask & ~inputOnlyAllowed) != 0)
        {
            throw new XProtocolError(XErrorCode.Match);
        }

        for (int bit = 0; bit < 15; bit++)
        {
            if ((mask & (1u << bit)) == 0)
            {
                continue;
            }
            uint v = r.U32();
            switch ((XWindowAttrMask)(1u << bit))
            {
                case XWindowAttrMask.BackgroundPixmap:
                    window.BackgroundPixmap = v;
                    window.BackgroundPixel = null;
                    window.BackgroundTile = v > 1 ? Lookup<XPixmap>(v) ?? throw new XProtocolError(XErrorCode.Pixmap, v) : null;
                    break;
                case XWindowAttrMask.BackgroundPixel:
                    window.BackgroundPixmap = XWindow.BackgroundPixmapNone;
                    window.BackgroundTile = null;
                    window.BackgroundPixel = v;
                    break;
                case XWindowAttrMask.BorderPixmap:
                    window.BorderTile = v == 0 ? window.Parent?.BorderTile : Lookup<XPixmap>(v) ?? throw new XProtocolError(XErrorCode.Pixmap, v);
                    break;
                case XWindowAttrMask.BorderPixel:
                    window.BorderTile = null;
                    window.BorderPixel = v;
                    break;
                case XWindowAttrMask.BitGravity:
                    window.BitGravity = (byte)v;
                    break;
                case XWindowAttrMask.WinGravity:
                    window.WinGravity = (byte)v;
                    break;
                case XWindowAttrMask.BackingStore:
                    window.BackingStore = (byte)v;
                    break;
                case XWindowAttrMask.BackingPlanes:
                    window.BackingPlanes = v;
                    break;
                case XWindowAttrMask.BackingPixel:
                    window.BackingPixel = v;
                    break;
                case XWindowAttrMask.OverrideRedirect:
                    window.OverrideRedirect = v != 0;
                    break;
                case XWindowAttrMask.SaveUnder:
                    window.SaveUnder = v != 0;
                    break;
                case XWindowAttrMask.EventMask:
                    SelectEvents(c, window, v);
                    break;
                case XWindowAttrMask.DoNotPropagateMask:
                    window.DoNotPropagateMask = (ushort)v;
                    break;
                case XWindowAttrMask.Colormap:
                    window.Colormap = v == 0 ? window.Parent?.Colormap ?? DefaultColormapId : v;
                    break;
                case XWindowAttrMask.Cursor:
                    window.Cursor = v == 0 ? null : Lookup<XCursor>(v) ?? throw new XProtocolError(XErrorCode.Cursor, v);
                    break;
            }
        }
    }

    /// <summary>设置某客户端在窗口上选的事件。三种「独占」事件同一时间只能有一个客户端选(否则 BadAccess)。</summary>
    private static void SelectEvents(XClient c, XWindow window, uint mask)
    {
        if ((mask & ~(uint)XEventMask.AllValid) != 0)
        {
            throw new XProtocolError(XErrorCode.Value, mask);
        }
        foreach (XEventMask exclusive in (XEventMask[])[XEventMask.SubstructureRedirect, XEventMask.ResizeRedirect, XEventMask.ButtonPress])
        {
            if ((mask & (uint)exclusive) == 0)
            {
                continue;
            }
            foreach ((XClient other, uint selected) in window.EventSelections)
            {
                if (!ReferenceEquals(other, c) && (selected & (uint)exclusive) != 0)
                {
                    throw new XProtocolError(XErrorCode.Access);
                }
            }
        }
        if (mask == 0)
        {
            window.EventSelections.Remove(c);
        }
        else
        {
            window.EventSelections[c] = mask;
        }
    }

    private void GetWindowAttributes(XClient c, XRequestReader r)
    {
        XWindow w0 = Window(r.U32());
        uint yours = w0.EventSelections.GetValueOrDefault(c);
        c.Reply(w0.BackingStore, w => w
            .U32(w0.Visual).U16(w0.Class).U8(w0.BitGravity).U8(w0.WinGravity)
            .U32(w0.BackingPlanes).U32(w0.BackingPixel)
            .Bool(w0.SaveUnder).Bool(true).U8(w0.MapState).Bool(w0.OverrideRedirect)
            .U32(w0.Colormap).U32(w0.AllEventMasks).U32(yours).U16(w0.DoNotPropagateMask).Zero(2));
    }

    // ------------------------------------------------------------------ 销毁

    private void DestroyWindowRequest(XClient c, XRequestReader r) => DestroyWindow(Window(r.U32()));

    private void DestroySubwindows(XRequestReader r)
    {
        XWindow window = Window(r.U32());
        for (int i = window.Children.Count - 1; i >= 0; i--)
        {
            if (i < window.Children.Count)
            {
                DestroyWindow(window.Children[i]);
            }
        }
    }

    internal void DestroyWindow(XWindow window)
    {
        if (window.IsRoot || window.Id == SelectionWindowId || !_resources.ContainsKey(window.Id))
        {
            return;
        }
        if (window.Mapped)
        {
            UnmapWindow(window);
        }
        DestroyTree(window);
        window.Parent?.Children.Remove(window);
        InvalidateVisibility();
        UpdatePointerWindow();
    }

    /// <summary>从最深的后代开始逐个发 DestroyNotify 并释放(协议规定:先下级后上级)。</summary>
    private void DestroyTree(XWindow window)
    {
        foreach (XWindow child in window.Children.ToArray())
        {
            DestroyTree(child);
        }
        DeliverStructure(window, XEventCode.DestroyNotify, 0, w => w.U32(window.Id));
        _resources.Remove(window.Id);
        CleanupXFixes(null, window);
        CleanupDamage(null, window);
        CleanupCompositeDbe(null, window);
        CleanupPresent(null, window);
        CleanupRandR(null, window);
        CleanupEwmh(window);
        foreach (var (atom, owner) in _selections.ToArray())
        {
            if (ReferenceEquals(owner.Window, window))
            {
                _selections.Remove(atom);
                NotifySelectionChange(atom, 1, 0, owner.Time);
            }
        }
        if (ReferenceEquals(_focus, window))
        {
            RevertFocus(window);
        }
        if (ReferenceEquals(_pointerGrab?.Window, window))
        {
            _pointerGrab = null;
        }
        if (ReferenceEquals(_keyboardGrab?.Window, window))
        {
            _keyboardGrab = null;
        }
        if (_topLevelHandles.Remove(window, out Host.XTopLevelWindow? handle))
        {
            handle.IsMapped = false;
        }
        _damage.Remove(window);
        window.Buffer = null;
        foreach (XClient client in _clients.Values)
        {
            client.SaveSet.Remove(window.Id);
        }
    }

    /// <summary>
    /// 只记录不生效:save-set 的作用是「窗口管理器断开时把被它重新 reparent 过的窗口还回去」,
    /// 而我们的窗口管理器是宿主本身,不会断开。
    /// </summary>
    private void ChangeSaveSet(XClient c, XRequestReader r)
    {
        byte mode = r.Data;
        XWindow window = Window(r.U32());
        if (mode == 0)
        {
            c.SaveSet.Add(window.Id);
        }
        else
        {
            c.SaveSet.Remove(window.Id);
        }
    }

    // ------------------------------------------------------------------ 映射

    private void MapWindowRequest(XClient c, XRequestReader r) => MapWindow(c, Window(r.U32()));

    internal void MapWindow(XClient? requester, XWindow window)
    {
        if (window.Mapped || window.IsRoot)
        {
            return;
        }
        if (!window.OverrideRedirect && window.Parent is { } parent
            && RedirectClient(parent, XEventMask.SubstructureRedirect) is { } wm && !ReferenceEquals(wm, requester))
        {
            wm.Event(XEventCode.MapRequest, 0, w => w.U32(parent.Id).U32(window.Id));
            return;
        }

        window.Mapped = true;
        InvalidateVisibility();
        DeliverStructure(window, XEventCode.MapNotify, 0, w => w.U32(window.Id).Bool(window.OverrideRedirect));

        if (!window.IsViewable)
        {
            return;
        }
        if (window.IsTopLevel)
        {
            window.Buffer ??= new Drawing.PixelBuffer(window.Width, window.Height, window.Depth == 32 ? (byte)32 : (byte)24);
            window.Buffer.Resize(window.Width, window.Height);
            Host.XTopLevelWindow handle = HandleFor(window);
            RefreshHandle(window, handle);
            handle.IsMapped = true;
            ExposeWindowTree(window, new Drawing.Region(window.Buffer.Bounds));
            _host.TopLevelMapped(handle);
            OnTopLevelMappedEwmh(window);
        }
        else if (window.TopLevel is { } top)
        {
            ExposeWindowTree(top, VisibleOuter(window));
        }
        UpdatePointerWindow();
    }

    private void MapSubwindows(XClient c, XRequestReader r)
    {
        XWindow window = Window(r.U32());
        for (int i = window.Children.Count - 1; i >= 0; i--)
        {
            MapWindow(c, window.Children[i]);
        }
    }

    private void UnmapWindowRequest(XRequestReader r) => UnmapWindow(Window(r.U32()));

    internal void UnmapWindow(XWindow window, bool fromConfigure = false)
    {
        if (!window.Mapped || window.IsRoot)
        {
            return;
        }
        bool wasViewable = window.IsViewable;
        Drawing.Region old = wasViewable ? VisibleOuter(window) : new Drawing.Region();
        window.Mapped = false;
        InvalidateVisibility();
        DeliverStructure(window, XEventCode.UnmapNotify, 0, w => w.U32(window.Id).Bool(fromConfigure));

        if (window.IsTopLevel)
        {
            if (_topLevelHandles.TryGetValue(window, out Host.XTopLevelWindow? handle))
            {
                handle.IsMapped = false;
                _host.TopLevelUnmapped(handle);
                OnTopLevelUnmappedEwmh(window);
            }
        }
        else if (wasViewable && window.TopLevel is { } top)
        {
            ExposeWindowTree(top, old);
        }
        if (_focus is { } focus && (ReferenceEquals(focus, window) || focus.IsDescendantOf(window)))
        {
            RevertFocus(focus);
        }
        UpdatePointerWindow();
    }

    private void UnmapSubwindows(XRequestReader r)
    {
        XWindow window = Window(r.U32());
        foreach (XWindow child in window.Children.ToArray())
        {
            UnmapWindow(child);
        }
    }

    // ------------------------------------------------------------------ 配置

    private void ConfigureWindowRequest(XClient c, XRequestReader r)
    {
        XWindow window = Window(r.U32());
        ushort mask = r.U16();
        r.Skip(2);
        int x = window.X, y = window.Y, width = window.Width, height = window.Height, border = window.BorderWidth;
        XWindow? sibling = null;
        int stackMode = -1;
        for (int bit = 0; bit < 7; bit++)
        {
            if ((mask & (1 << bit)) == 0)
            {
                continue;
            }
            uint v = r.U32();
            switch ((XConfigMask)(1 << bit))
            {
                case XConfigMask.X: x = (short)v; break;
                case XConfigMask.Y: y = (short)v; break;
                case XConfigMask.Width: width = (ushort)v; break;
                case XConfigMask.Height: height = (ushort)v; break;
                case XConfigMask.BorderWidth: border = (ushort)v; break;
                case XConfigMask.Sibling: sibling = Window(v); break;
                case XConfigMask.StackMode: stackMode = (byte)v; break;
            }
        }
        if (width == 0 || height == 0)
        {
            throw new XProtocolError(XErrorCode.Value, 0);
        }
        if (sibling is not null && (stackMode < 0 || !ReferenceEquals(sibling.Parent, window.Parent) || ReferenceEquals(sibling, window)))
        {
            throw new XProtocolError(XErrorCode.Match);
        }
        if (window.IsInputOnly && (mask & (ushort)XConfigMask.BorderWidth) != 0 && border != 0)
        {
            throw new XProtocolError(XErrorCode.Match);
        }

        if (!window.OverrideRedirect && window.Parent is { } parent
            && RedirectClient(parent, XEventMask.SubstructureRedirect) is { } wm && !ReferenceEquals(wm, c))
        {
            wm.Event(XEventCode.ConfigureRequest, (byte)Math.Max(0, stackMode), w => w
                .U32(parent.Id).U32(window.Id).U32(sibling?.Id ?? 0)
                .I16(x).I16(y).U16((ushort)width).U16((ushort)height).U16((ushort)border).U16(mask));
            return;
        }

        ConfigureWindow(window, x, y, width, height, border, sibling, stackMode);
    }

    /// <summary>真正改几何与堆叠,发 ConfigureNotify、重画露出的部分、通知宿主。</summary>
    internal void ConfigureWindow(XWindow window, int x, int y, int width, int height, int border, XWindow? sibling, int stackMode)
    {
        if (window.IsRoot)
        {
            return;
        }
        bool viewable = window.IsViewable;
        Drawing.Region old = viewable && !window.IsTopLevel ? VisibleOuter(window) : new Drawing.Region();
        bool resized = width != window.Width || height != window.Height;
        bool moved = x != window.X || y != window.Y;

        window.X = x;
        window.Y = y;
        window.Width = width;
        window.Height = height;
        window.BorderWidth = border;
        if (resized)
        {
            ResizeBackBuffer(window);
        }
        if ((resized || moved) && _presentContexts.Count != 0)
        {
            NotifyPresentConfigure(window);
        }
        if (stackMode >= 0 && window.Parent is { } parent)
        {
            Restack(parent, window, sibling, stackMode);
            if (window.IsTopLevel && window.Mapped)
            {
                UpdateClientLists();
            }
        }
        InvalidateVisibility();

        XWindow? above = window.Parent is { } p && p.Children.IndexOf(window) is var i and > 0 ? p.Children[i - 1] : null;
        DeliverStructure(window, XEventCode.ConfigureNotify, 0, w => w
            .U32(window.Id).U32(above?.Id ?? 0).I16(x).I16(y).U16((ushort)width).U16((ushort)height)
            .U16((ushort)border).Bool(window.OverrideRedirect));

        if (!viewable)
        {
            return;
        }
        if (window.IsTopLevel)
        {
            if (resized && window.Buffer is { } buffer)
            {
                buffer.Resize(width, height);
                // bit-gravity 默认 Forget:整窗重画(并发 Expose)。NorthWest 时只画新露出的部分。
                Drawing.Region exposed = new(buffer.Bounds);
                ExposeWindowTree(window, exposed);
            }
            if ((resized || moved) && _topLevelHandles.TryGetValue(window, out Host.XTopLevelWindow? handle))
            {
                RefreshHandle(window, handle);
                _host.TopLevelChanged(handle);
            }
        }
        else if (window.TopLevel is { } top)
        {
            ExposeWindowTree(top, old.Union(VisibleOuter(window)));
        }
        UpdatePointerWindow();
    }

    /// <summary>stack-mode:0 Above,1 Below,2 TopIf,3 BottomIf,4 Opposite。后三种按遮挡判断太贵,按 Above / Below 近似。</summary>
    private static void Restack(XWindow parent, XWindow window, XWindow? sibling, int stackMode)
    {
        List<XWindow> list = parent.Children;
        list.Remove(window);
        bool above = stackMode is 0 or 2 or 4;
        if (sibling is null)
        {
            if (above)
            {
                list.Add(window);
            }
            else
            {
                list.Insert(0, window);
            }
            return;
        }
        int index = list.IndexOf(sibling);
        list.Insert(above ? index + 1 : index, window);
    }

    private void CirculateWindow(XRequestReader r)
    {
        byte direction = r.Data;
        XWindow window = Window(r.U32());
        List<XWindow> mapped = [.. window.Children.Where(w => w.Mapped)];
        if (mapped.Count < 2)
        {
            return;
        }
        XWindow moving = direction == 0 ? mapped[0] : mapped[^1];
        window.Children.Remove(moving);
        if (direction == 0)
        {
            window.Children.Add(moving);
        }
        else
        {
            window.Children.Insert(0, moving);
        }
        InvalidateVisibility();
        DeliverStructure(moving, XEventCode.CirculateNotify, 0, w => w.U32(moving.Id).U32(0).Zero(4).U8(direction == 0 ? (byte)0 : (byte)1));
        if (moving.TopLevel is { } top && !moving.IsTopLevel)
        {
            ExposeWindowTree(top, VisibleOuter(moving));
        }
    }

    private void ReparentWindow(XRequestReader r)
    {
        XWindow window = Window(r.U32());
        XWindow parent = Window(r.U32());
        short x = r.I16(), y = r.I16();
        if (window.IsRoot || ReferenceEquals(parent, window) || parent.IsDescendantOf(window))
        {
            throw new XProtocolError(XErrorCode.Match);
        }
        if (!window.IsInputOnly && parent.IsInputOnly)
        {
            throw new XProtocolError(XErrorCode.Match);
        }
        bool wasMapped = window.Mapped;
        if (wasMapped)
        {
            UnmapWindow(window);
        }
        XWindow oldParent = window.Parent!;
        bool wasTopLevel = window.IsTopLevel;
        oldParent.Children.Remove(window);
        parent.Children.Add(window);
        window.Parent = parent;
        window.X = x;
        window.Y = y;
        InvalidateVisibility();
        if (wasTopLevel && !window.IsTopLevel)
        {
            if (_topLevelHandles.Remove(window, out Host.XTopLevelWindow? handle))
            {
                handle.IsMapped = false;
            }
            window.Buffer = null;
        }

        void Body(XWriter w) => w.U32(window.Id).U32(parent.Id).I16(x).I16(y).Bool(window.OverrideRedirect);
        DeliverToSelectors(window, XEventMask.StructureNotify, c => c.Event(XEventCode.ReparentNotify, 0, w => { w.U32(window.Id); Body(w); }));
        DeliverToSelectors(oldParent, XEventMask.SubstructureNotify, c => c.Event(XEventCode.ReparentNotify, 0, w => { w.U32(oldParent.Id); Body(w); }));
        DeliverToSelectors(parent, XEventMask.SubstructureNotify, c => c.Event(XEventCode.ReparentNotify, 0, w => { w.U32(parent.Id); Body(w); }));

        if (wasMapped)
        {
            MapWindow(null, window);
        }
    }

    // ------------------------------------------------------------------ 查询

    private void GetGeometry(XClient c, XRequestReader r)
    {
        uint id = r.U32();
        switch (Lookup<XResource>(id))
        {
            case XWindow w0:
                c.Reply(w0.Depth, w => w.U32(Root.Id).I16(w0.X).I16(w0.Y).U16((ushort)w0.Width).U16((ushort)w0.Height)
                    .U16((ushort)w0.BorderWidth).Zero(10));
                break;
            case XPixmap p:
                c.Reply(p.Depth, w => w.U32(Root.Id).I16(0).I16(0).U16((ushort)p.Width).U16((ushort)p.Height).U16(0).Zero(10));
                break;
            default:
                throw new XProtocolError(XErrorCode.Drawable, id);
        }
    }

    private void QueryTree(XClient c, XRequestReader r)
    {
        XWindow window = Window(r.U32());
        c.Reply(0, w =>
        {
            w.U32(Root.Id).U32(window.Parent?.Id ?? 0).U16((ushort)window.Children.Count).Zero(14);
            foreach (XWindow child in window.Children)
            {
                w.U32(child.Id);
            }
        });
    }

    private void TranslateCoordinates(XClient c, XRequestReader r)
    {
        XWindow src = Window(r.U32());
        XWindow dst = Window(r.U32());
        short sx = r.I16(), sy = r.I16();
        (int ax, int ay) = src.AbsoluteInner();
        (int bx, int by) = dst.AbsoluteInner();
        int dx = ax + sx - bx, dy = ay + sy - by;
        uint child = 0;
        for (int i = dst.Children.Count - 1; i >= 0; i--)
        {
            XWindow ch = dst.Children[i];
            if (ch.Mapped && dx >= ch.X && dy >= ch.Y
                && dx < ch.X + ch.Width + (2 * ch.BorderWidth) && dy < ch.Y + ch.Height + (2 * ch.BorderWidth))
            {
                child = ch.Id;
                break;
            }
        }
        c.Reply(1, w => w.U32(child).I16(dx).I16(dy).Zero(16));
    }
}
