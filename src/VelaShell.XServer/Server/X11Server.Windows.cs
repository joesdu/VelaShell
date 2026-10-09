// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 「CreateWindow」「ChangeWindowAttributes」「GetWindowAttributes」
//   「DestroyWindow」「DestroySubwindows」「ChangeSaveSet」「ReparentWindow」「MapWindow」「MapSubwindows」
//   「UnmapWindow」「UnmapSubwindows」「ConfigureWindow」(含 stack-mode、SubstructureRedirect 与 ResizeRedirect 的改道)
//   「CirculateWindow」「GetGeometry」「QueryTree」「TranslateCoordinates」;
//   「CreateWindow」的 win-gravity(父窗口改尺寸时子窗口怎么挪);第 10 节「Events」里 CreateNotify / DestroyNotify / MapNotify /
//   MapRequest / UnmapNotify / ReparentNotify / GravityNotify / ResizeRequest / CirculateRequest / VisibilityNotify /
//   ConfigureNotify / ConfigureRequest / CirculateNotify 的字段

using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    /// <summary>
    /// 窗口嵌套层数上限(顶层为 1)。协议没有规定上限,但逐层往上走的可见区域计算每条绘图请求都要做一遍,
    /// 无限嵌套就能把执行线程拖住;真实程序里嵌套最深的 Xt / Motif 也不过几十层。超出回 BadAlloc(协议允许任何请求回 Alloc)。
    /// </summary>
    internal const int MaxWindowDepth = 256;

    /// <summary>每个客户端同时拥有的窗口数上限。每个窗口都带着几张事件 / 属性 / 抓取表,不设上限一个客户端就能把内存吃光。</summary>
    internal const int MaxWindowsPerClient = 32768;

    private void CreateWindow(XClient c, XRequestReader r)
    {
        byte depth = r.Data;
        uint id = r.U32();
        XWindow parent = Window(r.U32());
        short x = r.I16(), y = r.I16();
        ushort width = r.U16(), height = r.U16(), border = r.U16(), cls = r.U16();
        if (IsServerWindow(parent))
        {
            throw new XProtocolError(XErrorCode.Match);   // 服务端自己的窗口不在窗口树里,下面建的窗口没有着落
        }
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
        if (c.WindowCount >= MaxWindowsPerClient || parent.Level >= MaxWindowDepth)
        {
            throw new XProtocolError(XErrorCode.Alloc);
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
        c.WindowCount++;
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
        if (IsServerWindow(window) && (mask & ~(uint)XWindowAttrMask.EventMask) != 0)
        {
            throw new XProtocolError(XErrorCode.Access);   // 只许选事件(GTK 在 WM 检查窗口上选 StructureNotify)
        }
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
                    window.Cursor = v == 0 ? null : Lookup<XCursorResource>(v) ?? throw new XProtocolError(XErrorCode.Cursor, v);
                    break;
            }
        }
    }

    /// <summary>设置某客户端在窗口上选的事件。三种「独占」事件同一时间只能有一个客户端选(否则 BadAccess)。</summary>
    private void SelectEvents(XClient c, XWindow window, uint mask)
    {
        SelectEventsCore(c, window, mask, Rootful);
        if ((mask & (uint)XEventMask.VisibilityChange) != 0 && _visibilityWatchers.Add(window))
        {
            window.VisibilityState = VisibilityOf(window);   // 起点:之后状态变了才报
        }
    }

    private static void SelectEventsCore(XClient c, XWindow window, uint mask, bool rootful)
    {
        if ((mask & ~(uint)XEventMask.AllValid) != 0)
        {
            throw new XProtocolError(XErrorCode.Value, mask);
        }
        if (window.IsRoot && !rootful && (mask & (uint)XEventMask.SubstructureRedirect) != 0)
        {
            // 窗口管理器是服务端(宿主)自己:根窗口的 SubstructureRedirect 一直有人占着,与真实桌面上已有窗口管理器时一样回 BadAccess。
            // 原先谁都选得上 —— 远端误跑 openbox / xfwm4,所有会话的新窗口都变成发给它的 MapRequest、被它套进自己的外框。
            throw new XProtocolError(XErrorCode.Access);
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

    // ------------------------------------------------------------------ 可见性(VisibilityNotify)

    private const byte Unobscured = 0, PartiallyObscured = 1, FullyObscured = 2, NotViewable = 255;

    /// <summary>有客户端选了 VisibilityChange 的窗口(选择撤掉、窗口销毁后在下一次检查时去掉)。</summary>
    private readonly HashSet<XWindow> _visibilityWatchers = [];

    /// <summary>上一次检查可见性时的可见性代号:树没变就不必再算。</summary>
    private int _visibilityCheckedGeneration = -1;

    /// <summary>
    /// 协议「VisibilityNotify」:窗口(不算它的子窗口)在不可见、完全露出、部分被挡、完全被挡之间变了,就报给选了 VisibilityChange 的客户端;
    /// InputOnly 窗口不报。在引起它的结构事件之后、这个窗口的 Expose 之前发 —— 所以重画(<see cref="ExposeWindowTree" />)之前先查一次,
    /// 结构变化收尾时再查一次(只挡住、不露出的变化没有 Expose)。原先从不发:xterm、mpv 选了它也收不到。
    /// 顶层各有自己的缓冲与原生窗口,宿主里谁挡着谁这边不知道,顶层映射着就算完全露出。
    /// </summary>
    private void UpdateVisibility()
    {
        if (_visibilityWatchers.Count == 0 || _visibilityCheckedGeneration == _visibilityGeneration)
        {
            return;
        }
        _visibilityCheckedGeneration = _visibilityGeneration;
        foreach (XWindow window in _visibilityWatchers.ToArray())
        {
            if (!_resources.ContainsKey(window.Id) || !window.EventSelections.Values.Any(m => (m & (uint)XEventMask.VisibilityChange) != 0))
            {
                _visibilityWatchers.Remove(window);
                continue;
            }
            byte state = VisibilityOf(window);
            if (state == window.VisibilityState)
            {
                continue;
            }
            window.VisibilityState = state;
            if (state != NotViewable)
            {
                DeliverToSelectors(window, XEventMask.VisibilityChange, c => c.Event(XEventCode.VisibilityNotify, 0, w => w.U32(window.Id).U8(state)));
            }
        }
    }

    /// <summary>窗口自己(不算子窗口)现在的可见状态:外框(连同边界形状)里露在外面的部分与整个外框比。</summary>
    private static byte VisibilityOf(XWindow window)
    {
        if (!window.IsViewable || window.IsInputOnly)
        {
            return NotViewable;
        }
        if (window.IsTopLevel)
        {
            return Unobscured;
        }
        Drawing.Region visible = VisibleOuter(window);
        if (visible.IsEmpty)
        {
            return FullyObscured;
        }
        return ShapedOuter(window).Subtract(visible).IsEmpty ? Unobscured : PartiallyObscured;
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

    private void DestroyWindow(XClient c, XRequestReader r) => Destroy(Window(r.U32()));

    /// <summary>协议「DestroySubwindows」:对每个子窗口做一次 DestroyWindow,按堆叠次序从下到上(原先从上到下)。</summary>
    private void DestroySubwindows(XRequestReader r)
    {
        XWindow window = Window(r.U32());
        foreach (XWindow child in window.Children.ToArray())   // Children 从下到上排
        {
            Destroy(child);
        }
    }

    internal void Destroy(XWindow window)
    {
        if (window.Owner is null || !_resources.ContainsKey(window.Id))   // 根窗口与服务端自己的窗口不能销毁
        {
            return;
        }
        if (window.Mapped)
        {
            Unmap(window);
        }
        DestroyTree(window);
        window.Parent?.Children.Remove(window);
        InvalidateVisibility();
        UpdateVisibility();
        UpdatePointerWindow();
    }

    /// <summary>
    /// 从最深的后代开始逐个发 DestroyNotify 并释放(协议规定:先下级后上级)。显式栈做后序遍历、不递归 ——
    /// 客户端断开时的清理也走这里,递归的话一棵够深的树就能把执行线程的栈压爆、整个进程退出。
    /// 顺序与递归写法相同:兄弟按堆叠顺序从下到上,每个子窗口的子表在轮到它时才取。
    /// </summary>
    private void DestroyTree(XWindow window)
    {
        Stack<(XWindow Window, XWindow[] Children, int Next)> pending = new();
        pending.Push((window, [.. window.Children], 0));
        while (pending.TryPop(out (XWindow Window, XWindow[] Children, int Next) item))
        {
            if (item.Next < item.Children.Length)
            {
                pending.Push(item with { Next = item.Next + 1 });
                XWindow child = item.Children[item.Next];
                pending.Push((child, [.. child.Children], 0));
                continue;
            }
            DestroyOne(item.Window);
        }
    }

    /// <summary>销毁一个窗口本身(它的后代已经销毁过了)。</summary>
    private void DestroyOne(XWindow window)
    {
        DeliverStructure(window, XEventCode.DestroyNotify, 0, w => w.U32(window.Id));
        RemoveResource(window.Id);
        if (window.Owner is { } creator)
        {
            creator.WindowCount--;
        }
        foreach (Extension extension in _extensionList)
        {
            extension.WindowDestroyed?.Invoke(window);
        }
        CleanupEwmh(window);
        CleanupSystemTray(window);
        foreach ((SelectionSlot slot, (XWindow Window, XClient? Client, uint Time) owner) in _selections.ToArray())
        {
            if (ReferenceEquals(owner.Window, window))
            {
                _selections.Remove(slot);
                NotifySelectionChange(slot.Atom, 1, 0, owner.Time, client => InScope(client, slot));
                OnSelectionOwnerLost(slot);
            }
        }
        if (ReferenceEquals(_focus, window))
        {
            RevertFocus(window);
        }
        if (ReferenceEquals(PointerGrab?.Window, window))
        {
            PointerGrab = null;
        }
        if (ReferenceEquals(KeyboardGrab?.Window, window))
        {
            KeyboardGrab = null;
        }
        if (_topLevelHandles.Remove(window, out XTopLevelWindow? handle))
        {
            RetireHandle(handle);
        }
        _damage.Remove(window);
        window.Buffer = null;
        SyncBufferCharge(window);
        foreach (XClient client in _clients.Values)
        {
            client.SaveSet.Remove(window.Id);
        }
        // 属性随窗口而去:写它们的客户端的账退还(最后做 —— 上面的清理还可能读它们)。
        foreach (XProperty property in window.Properties.Values)
        {
            ReleaseProperty(property);
        }
        window.Properties.Clear();
    }

    /// <summary>
    /// save-set:客户端断开时(见 <see cref="ProcessSaveSet" />)把挂在它的窗口下面的这些窗口还回去。用它的是 reparent 别人窗口的程序 ——
    /// XEmbed 的嵌入方(托盘、插件宿主)、窗口管理器。窗口不能是这个客户端自己建的(BadMatch)。
    /// </summary>
    private void ChangeSaveSet(XClient c, XRequestReader r)
    {
        byte mode = r.Data;
        XWindow window = Window(r.U32());
        ChangeSaveSet(c, window, mode == 0, toRoot: false, map: true);
    }

    /// <summary>核心与 XFIXES 的 ChangeSaveSet 共用;<paramref name="toRoot" /> 与 <paramref name="map" /> 是 XFIXES 的 target / map。</summary>
    internal static void ChangeSaveSet(XClient c, XWindow window, bool insert, bool toRoot, bool map)
    {
        if (ReferenceEquals(window.Owner, c) || window.IsRoot)
        {
            throw new XProtocolError(XErrorCode.Match);
        }
        if (insert)
        {
            c.SaveSet[window.Id] = (toRoot, map);
        }
        else
        {
            c.SaveSet.Remove(window.Id);
        }
    }

    /// <summary>
    /// 协议第 10 节「Connection Close」:save-set 里的每个窗口,若在这个客户端建的某个窗口之下,就 reparent 到最近的一个祖先,
    /// 使它不再在这个客户端建的任何窗口之下(根坐标不变;XFIXES 可以指定挂到根窗口);没映射的补映射(XFIXES 可以指定不补)。
    /// 在销毁资源之前做,不论 close-down mode。原先只登记不生效:嵌入方(或远端窗口管理器)一退出,外框被销毁,别人的窗口跟着被销毁。
    /// </summary>
    private void ProcessSaveSet(XClient client)
    {
        foreach ((uint id, (bool toRoot, bool map)) in client.SaveSet.ToArray())
        {
            if (Lookup<XWindow>(id) is not { Parent: not null } window)
            {
                continue;
            }
            XWindow? outermost = null;   // 这个客户端建的、最靠近根的那个祖先
            for (XWindow? w = window.Parent; w is { IsRoot: false }; w = w.Parent)
            {
                if (ReferenceEquals(w.Owner, client))
                {
                    outermost = w;
                }
            }
            if (outermost?.Parent is { } keep)
            {
                XWindow target = toRoot ? Root : keep;
                (int ax, int ay) = window.Parent.AbsoluteInner();
                (int tx, int ty) = target.AbsoluteInner();
                Reparent(window, target, (short)Math.Clamp(ax + window.X - tx, short.MinValue, short.MaxValue),
                    (short)Math.Clamp(ay + window.Y - ty, short.MinValue, short.MaxValue), requester: null);
            }
            if (map && !window.Mapped)
            {
                Map(null, window);
            }
        }
        client.SaveSet.Clear();
    }

    // ------------------------------------------------------------------ 映射

    private void MapWindow(XClient c, XRequestReader r)
    {
        XWindow window = Window(r.U32());
        if (!IsServerWindow(window))
        {
            Map(c, window);   // 服务端自己的窗口(剪贴板桥、WM 检查窗口)映射了,宿主就会多出一个原生窗口
        }
    }

    /// <summary>
    /// 服务端自己的窗口(<see cref="SelectionWindowId" /> 这类,根窗口之外 Owner 为 null 的):客户端不能映射、改几何、改属性(事件选择除外)、
    /// reparent 或在它下面建窗口,销毁时跳过 —— 原先 ReparentWindow(0x43, 自己的窗口) 再销毁自己的窗口,剪贴板桥接、XSETTINGS、
    /// _NET_SUPPORTING_WM_CHECK 就一起没了,整个显示(所有会话)直到重启。
    /// </summary>
    private static bool IsServerWindow(XWindow window) => window.Owner is null && !window.IsRoot;

    internal void Map(XClient? requester, XWindow window)
    {
        if (window.Mapped && requester is not null && window.IsTopLevel && !window.OverrideRedirect
            && _topLevelHandles.TryGetValue(window, out XTopLevelWindow? iconic) && (iconic.Snapshot.States & XWindowStates.Hidden) != 0)
        {
            // ICCCM §4.1.4:从 IconicState 回到 NormalState,客户端 map 窗口(Tk 的 wm deiconify、Emacs 的 make-frame-visible、
            // xdotool windowmap)。最小化时窗口在 X 里仍映射着,这一下原先是空操作;转成「去掉 Hidden」请宿主还原。
            _host.WindowManagerRequested(new XStateChangeRequest(iconic, XWindowStates.None, XWindowStates.Hidden));
            return;
        }
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
        if (requester is not null && window.IsTopLevel && !window.IsInputOnly)
        {
            RequireBufferMemory(window, window.Width, window.Height);   // 映射时才建缓冲:先核账(xs_plan X-2)
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
            // InputOnly 的顶层(GTK 的 GtkInvisible 之类)看不见、不能画:不建像素缓冲。快照带着 InputOnly,宿主据此不开原生窗口 ——
            // 原先给它建 24 位缓冲,宿主多出一个黑色的原生窗口。
            if (!window.IsInputOnly)
            {
                if (ReleaseNamedWindowPixmaps(window))
                {
                    window.Buffer = null;   // 旧缓冲归 NameWindowPixmap 的像素图;映射时整窗重画,换一块新的
                }
                window.Buffer ??= new Drawing.PixelBuffer(window.Width, window.Height, window.Depth == 32 ? (byte)32 : (byte)24);
                window.Buffer.Resize(window.Width, window.Height);
                SyncBufferCharge(window);
            }
            XTopLevelWindow handle = HandleFor(window);
            RefreshSnapshot(window, handle);
            SetMapped(handle, true);
            if (window.Buffer is { } buffer)
            {
                ExposeWindowTree(window, new Drawing.Region(buffer.Bounds));
            }
            if (IsTrayEmbedder(window))
            {
                _host.SystemTrayIconAdded(handle, TrayIconTitle(window));   // 托盘图标:不当普通顶层窗口交给宿主
            }
            else if (!Rootful)   // 单窗口模式:顶层拼进屏幕,不单独交给宿主;外框、客户端列表归远端的窗口管理器
            {
                _host.TopLevelMapped(handle);
                OnTopLevelMappedEwmh(window);
            }
        }
        else
        {
            ExposeWindowTree(window, VisibleOuter(window));   // 映射只露出它自己与它的下级
        }
        UpdateVisibility();
        UpdatePointerWindow();
    }

    private void MapSubwindows(XClient c, XRequestReader r)
    {
        XWindow window = Window(r.U32());
        for (int i = window.Children.Count - 1; i >= 0; i--)
        {
            Map(c, window.Children[i]);
        }
    }

    private void UnmapWindow(XRequestReader r)
    {
        XWindow window = Window(r.U32());
        if (!IsServerWindow(window))
        {
            Unmap(window);
        }
    }

    internal void Unmap(XWindow window, bool fromConfigure = false)
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
            if (_topLevelHandles.TryGetValue(window, out XTopLevelWindow? handle))
            {
                SetMapped(handle, false);
                if (IsTrayEmbedder(window))
                {
                    _host.SystemTrayIconRemoved(handle);
                }
                else if (!Rootful)
                {
                    _host.TopLevelUnmapped(handle);
                    OnTopLevelUnmappedEwmh(window);
                }
            }
        }
        else if (wasViewable)
        {
            ExposeWindowTree(window.Parent!, old);   // 露出来的是父窗口、下面的兄弟及其子树:都在父窗口这棵子树里
        }
        if (_focus is { } focus && (ReferenceEquals(focus, window) || focus.IsDescendantOf(window)))
        {
            RevertFocus(focus);
        }
        ReleaseUnviewableGrabs();
        UpdateVisibility();
        UpdatePointerWindow();
    }

    private void UnmapSubwindows(XRequestReader r)
    {
        XWindow window = Window(r.U32());
        foreach (XWindow child in window.Children.ToArray())
        {
            Unmap(child);
        }
    }

    // ------------------------------------------------------------------ 配置

    private void ConfigureWindow(XClient c, XRequestReader r)
    {
        XWindow window = Window(r.U32());
        if (IsServerWindow(window))
        {
            throw new XProtocolError(XErrorCode.Match);
        }
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
                case XConfigMask.StackMode:
                    // Above、Below、TopIf、BottomIf、Opposite 之外的值:BadValue(原先截成一个字节照用)。
                    stackMode = v <= 4 ? (int)v : throw new XProtocolError(XErrorCode.Value, v);
                    break;
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
        if ((width != window.Width || height != window.Height)
            && RedirectClient(window, XEventMask.ResizeRedirect) is { } resizer && !ReferenceEquals(resizer, c))
        {
            // 协议「ConfigureWindow」:别的客户端在这个窗口上选了 ResizeRedirect,改尺寸就变成发给它的 ResizeRequest,
            // 尺寸保持现值,其余(位置、边框、堆叠)照常处理。原先 ResizeRedirect 选得上却不生效。
            resizer.Event(XEventCode.ResizeRequest, 0, w => w.U32(window.Id).U16((ushort)width).U16((ushort)height));
            (width, height) = (window.Width, window.Height);
        }

        if (window.IsTopLevel && window.Buffer is not null && (width != window.Width || height != window.Height))
        {
            RequireBufferMemory(window, width, height);   // 缓冲要跟着变大:先核账(xs_plan X-2)
        }
        if (window.IsTopLevel && (x != window.X || y != window.Y))
        {
            window.PositionRequested = true;   // 客户端自己给的位置:宿主按重力摆外框(ICCCM §4.1.5,见 XTopLevelSnapshot.NeedsPlacement)
        }
        Configure(window, x, y, width, height, border, sibling, stackMode);
        if (stackMode == 0 && sibling is null && window.IsTopLevel && window.Mapped && !window.OverrideRedirect
            && _topLevelHandles.TryGetValue(window, out XTopLevelWindow? handle))
        {
            // 客户端把顶层抬到最上面(XRaiseWindow、XMapRaised、Java 的 toFront):原生窗口的次序归宿主管,请它照办。
            _host.WindowManagerRequested(new XRaiseRequest(handle));
        }
    }

    /// <summary>真正改几何与堆叠,发 ConfigureNotify、重画露出的部分、通知宿主。</summary>
    internal void Configure(XWindow window, int x, int y, int width, int height, int border, XWindow? sibling, int stackMode)
    {
        if (window.IsRoot)
        {
            return;
        }
        bool viewable = window.IsViewable;
        Drawing.Region old = viewable && !window.IsTopLevel ? VisibleOuter(window) : new Drawing.Region();
        bool resized = width != window.Width || height != window.Height;
        bool moved = x != window.X || y != window.Y;
        (int dx, int dy, int dw, int dh) = (x - window.X, y - window.Y, width - window.Width, height - window.Height);
        bool rebordered = border != window.BorderWidth;

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
        if (resized && window.Children.Count != 0)
        {
            ApplyWinGravity(window, dx, dy, dw, dh);   // GravityNotify 在 ConfigureNotify 之后
        }

        if (!viewable)
        {
            return;
        }
        if (window.IsTopLevel)
        {
            if (resized && window.Buffer is { } buffer)
            {
                if (ReleaseNamedWindowPixmaps(window))
                {
                    buffer = window.Buffer = buffer.Clone();   // 旧缓冲归 NameWindowPixmap 的像素图,保持原来的尺寸与内容
                }
                buffer.Resize(width, height);
                SyncBufferCharge(window);
                // bit-gravity 默认 Forget:整窗重画(并发 Expose)。NorthWest 时只画新露出的部分。
                Drawing.Region exposed = new(buffer.Bounds);
                ExposeWindowTree(window, exposed);
            }
            if (resized || moved || rebordered)
            {
                RefreshTopLevel(window);
            }
        }
        else if (window.TopLevel is { } top)
        {
            ExposeWindowTree(top, old.Union(VisibleOuter(window)));
        }
        UpdateVisibility();
        UpdatePointerWindow();
    }

    /// <summary>
    /// 协议「ConfigureWindow」的 win-gravity:父窗口的内区尺寸真的变了,子窗口按各自的重力在父窗口里挪 —— (dw, dh) 是尺寸的变化,
    /// North 挪 (dw/2, 0)、SouthEast 挪 (dw, dh) 之类;Static 抵消父窗口位置的变化,在根窗口里不动;Unmap 位置同 NorthWest、
    /// 但取消映射(UnmapNotify 的 from-configure 为真)。挪了的发 GravityNotify。原先子窗口一律不动。
    /// </summary>
    private void ApplyWinGravity(XWindow window, int dx, int dy, int dw, int dh)
    {
        foreach (XWindow child in window.Children.ToArray())
        {
            if (child.WinGravity == 0)
            {
                Unmap(child, fromConfigure: true);
                continue;
            }
            (int ox, int oy) = child.WinGravity switch
            {
                2 => (dw / 2, 0),
                3 => (dw, 0),
                4 => (0, dh / 2),
                5 => (dw / 2, dh / 2),
                6 => (dw, dh / 2),
                7 => (0, dh),
                8 => (dw / 2, dh),
                9 => (dw, dh),
                10 => (-dx, -dy),
                _ => (0, 0),   // NorthWest
            };
            if (ox == 0 && oy == 0)
            {
                continue;
            }
            child.X = Math.Clamp(child.X + ox, short.MinValue, short.MaxValue);
            child.Y = Math.Clamp(child.Y + oy, short.MinValue, short.MaxValue);
            short cx = (short)child.X, cy = (short)child.Y;
            DeliverStructure(child, XEventCode.GravityNotify, 0, w => w.U32(child.Id).I16(cx).I16(cy));
        }
        InvalidateVisibility();
    }

    /// <summary>
    /// stack-mode(协议「ConfigureWindow」):0 Above、1 Below 放到兄弟的上 / 下面(没给兄弟就是最上 / 最下);
    /// 2 TopIf「兄弟挡着它就放到最上」、3 BottomIf「它挡着兄弟就放到最下」、4 Opposite 两样都看(没给兄弟就是任何一个兄弟)。
    /// 遮挡按新几何的外框矩形算(不看 SHAPE);原先后三种按 Above / Below 近似,不看遮挡。
    /// </summary>
    private static void Restack(XWindow parent, XWindow window, XWindow? sibling, int stackMode)
    {
        List<XWindow> list = parent.Children;
        if (stackMode >= 2)
        {
            int own = list.IndexOf(window);
            int other = sibling is null ? -1 : list.IndexOf(sibling);
            bool visible = window.Mapped && !window.IsInputOnly;
            bool occluded = sibling is null
                ? OverlapsAny(window, list, own + 1, list.Count, othersOcclude: true)
                : other > own && sibling.Mapped && !sibling.IsInputOnly && Overlap(window, sibling);
            bool occludes = visible && (sibling is null
                ? OverlapsAny(window, list, 0, own, othersOcclude: false)
                : other < own && sibling.Mapped && Overlap(window, sibling));
            bool? toTop = stackMode switch
            {
                2 => occluded ? true : null,
                3 => occludes ? false : null,
                _ => occluded ? true : occludes ? false : null,
            };
            if (toTop is not { } top)
            {
                return;
            }
            (stackMode, sibling) = (top ? 0 : 1, null);
        }
        list.Remove(window);
        bool above = stackMode == 0;
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

    /// <summary>
    /// 协议「CirculateWindow」:RaiseLowest 把被别的子窗口挡着的最低的已映射子窗口抬到最上,LowerHighest 把挡着别的子窗口的
    /// 最高的已映射子窗口压到最下;没有这样的子窗口什么也不做。真要挪、而别的客户端在这个窗口上选了 SubstructureRedirect 时,
    /// 发 CirculateRequest 给它、不再处理;否则挪了发 CirculateNotify,原先被挡住的部分重画。
    /// 原先不改道、不看遮挡(总挪最低 / 最高的那个),压下去之后让出来的兄弟留着旧像素。
    /// </summary>
    private void CirculateWindow(XClient c, XRequestReader r)
    {
        byte direction = r.Data;
        if (direction > 1)
        {
            throw new XProtocolError(XErrorCode.Value, direction);
        }
        XWindow window = Window(r.U32());
        XWindow? moving = direction == 0 ? LowestOccluded(window.Children) : HighestOccluding(window.Children);
        if (moving is null)
        {
            return;
        }
        // 附录 B:CirculateRequest 是 parent、window、4 字节不用,place 在第 16 字节(Top 0、Bottom 1);CirculateNotify 同样把 place 放在第 16 字节。
        if (RedirectClient(window, XEventMask.SubstructureRedirect) is { } wm && !ReferenceEquals(wm, c))
        {
            wm.Event(XEventCode.CirculateRequest, 0, w => w.U32(window.Id).U32(moving.Id).U32(0).U8(direction));
            return;
        }
        Drawing.Region old = moving.IsViewable && !moving.IsTopLevel ? VisibleOuter(moving) : new Drawing.Region();
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
        DeliverStructure(moving, XEventCode.CirculateNotify, 0, w => w.U32(moving.Id).U32(0).U8(direction));
        if (window.IsRoot)
        {
            UpdateClientLists();
        }
        else if (moving.TopLevel is { } top && moving.IsViewable)
        {
            ExposeWindowTree(top, old.Union(VisibleOuter(moving)));   // 抬上来的露出被挡的部分;压下去的让出来的兄弟重画
        }
        UpdateVisibility();
        UpdatePointerWindow();
    }

    /// <summary>被它上面的某个已映射兄弟挡着的、最低的已映射子窗口(<paramref name="children" /> 从下到上)。</summary>
    private static XWindow? LowestOccluded(List<XWindow> children)
    {
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i].Mapped && OverlapsAny(children[i], children, i + 1, children.Count, othersOcclude: true))
            {
                return children[i];
            }
        }
        return null;
    }

    /// <summary>挡着它下面的某个已映射兄弟的、最高的已映射子窗口。</summary>
    private static XWindow? HighestOccluding(List<XWindow> children)
    {
        for (int i = children.Count - 1; i >= 0; i--)
        {
            if (children[i].Mapped && !children[i].IsInputOnly && OverlapsAny(children[i], children, 0, i, othersOcclude: false))
            {
                return children[i];
            }
        }
        return null;
    }

    /// <summary>
    /// <paramref name="window" /> 与 [<paramref name="from" />, <paramref name="to" />) 之间的已映射兄弟的外框(含边框)有没有重叠。
    /// 遮挡按外框矩形算(不看 SHAPE);InputOnly 窗口看不见,不算挡住别人 —— <paramref name="othersOcclude" /> 时那些兄弟是挡的一方,要求它们不是 InputOnly。
    /// </summary>
    private static bool OverlapsAny(XWindow window, List<XWindow> siblings, int from, int to, bool othersOcclude)
    {
        for (int i = from; i < to; i++)
        {
            XWindow other = siblings[i];
            if (other.Mapped && !(othersOcclude && other.IsInputOnly) && Overlap(window, other))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>两个兄弟窗口的外框(含边框,父窗口坐标)相交。</summary>
    private static bool Overlap(XWindow a, XWindow b) => !OuterInParent(a).Intersect(OuterInParent(b)).IsEmpty;

    private static XRect OuterInParent(XWindow w) => new(w.X, w.Y, w.Width + (2 * w.BorderWidth), w.Height + (2 * w.BorderWidth));

    private void ReparentWindow(XClient c, XRequestReader r)
    {
        XWindow window = Window(r.U32());
        XWindow parent = Window(r.U32());
        short x = r.I16(), y = r.I16();
        if (window.Owner is null || IsServerWindow(parent) || ReferenceEquals(parent, window) || parent.IsDescendantOf(window))
        {
            throw new XProtocolError(XErrorCode.Match);
        }
        if (!window.IsInputOnly && parent.IsInputOnly)
        {
            throw new XProtocolError(XErrorCode.Match);
        }
        if (parent.Level + 1 + window.SubtreeHeight() > MaxWindowDepth)
        {
            throw new XProtocolError(XErrorCode.Alloc);   // 挪过去整棵子树就超过嵌套上限了
        }
        if (window.Mapped && parent.IsRoot && !window.IsInputOnly)
        {
            RequireBufferMemory(window, window.Width, window.Height);   // 重新映射成顶层要建缓冲:先核账,免得挪到一半才回 Alloc
        }
        Reparent(window, parent, x, y, c);
    }

    /// <summary>
    /// ReparentWindow 的执行部分(参数已核对);save-set 收尾也用它(<paramref name="requester" /> 为 null)。
    /// 原先映射着的窗口挪完自动 MapWindow,就像 <paramref name="requester" /> 自己发的一样(协议「ReparentWindow」):
    /// 发起 reparent 的正是在新父窗口上选了 SubstructureRedirect 的窗口管理器时直接映射,不给它自己发 MapRequest。
    /// </summary>
    private void Reparent(XWindow window, XWindow parent, short x, short y, XClient? requester)
    {
        bool wasMapped = window.Mapped;
        if (wasMapped)
        {
            Unmap(window);
        }
        XWindow oldParent = window.Parent!;
        bool wasTopLevel = window.IsTopLevel;
        oldParent.Children.Remove(window);
        parent.Children.Add(window);
        window.Parent = parent;
        window.X = x;
        window.Y = y;
        window.PositionRequested = true;   // 挪到根窗口下成了顶层:位置是客户端给的,宿主按重力摆
        InvalidateVisibility();
        if (wasTopLevel && !window.IsTopLevel)
        {
            if (_topLevelHandles.Remove(window, out XTopLevelWindow? handle))
            {
                RetireHandle(handle);
            }
            ReleaseNamedWindowPixmaps(window);
            window.Buffer = null;
            SyncBufferCharge(window);
        }

        void Body(XWriter w) => w.U32(window.Id).U32(parent.Id).I16(x).I16(y).Bool(window.OverrideRedirect);
        DeliverToSelectors(window, XEventMask.StructureNotify, c => c.Event(XEventCode.ReparentNotify, 0, w => { w.U32(window.Id); Body(w); }));
        DeliverToSelectors(oldParent, XEventMask.SubstructureNotify, c => c.Event(XEventCode.ReparentNotify, 0, w => { w.U32(oldParent.Id); Body(w); }));
        DeliverToSelectors(parent, XEventMask.SubstructureNotify, c => c.Event(XEventCode.ReparentNotify, 0, w => { w.U32(parent.Id); Body(w); }));

        if (wasMapped)
        {
            Map(requester, window);
        }
        OnTrayIconReparented(window, parent);
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
