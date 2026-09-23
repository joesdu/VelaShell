// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   Extended Window Manager Hints (EWMH) 1.5 —— §3「Root Window Properties」(_NET_SUPPORTED、_NET_CLIENT_LIST(_STACKING)、
//   _NET_NUMBER_OF_DESKTOPS、_NET_DESKTOP_GEOMETRY、_NET_DESKTOP_VIEWPORT、_NET_CURRENT_DESKTOP、_NET_ACTIVE_WINDOW、
//   _NET_WORKAREA、_NET_SUPPORTING_WM_CHECK)、§4「Other Root Window Messages」(_NET_CLOSE_WINDOW、
//   _NET_MOVERESIZE_WINDOW、_NET_WM_MOVERESIZE、_NET_REQUEST_FRAME_EXTENTS)、§5「Application Window Properties」
//   (_NET_WM_NAME、_NET_WM_DESKTOP、_NET_WM_WINDOW_TYPE、_NET_WM_STATE 及其 ClientMessage、_NET_WM_ICON、_NET_WM_PID、
//   _NET_WM_WINDOW_OPACITY)、§6「Window Manager Protocols」(_NET_FRAME_EXTENTS)
//   ICCCM 2.0 —— §4.1.2.3 WM_NORMAL_HINTS、§4.1.2.4 WM_HINTS、§4.1.3.1 WM_STATE、§4.1.4 WM_CHANGE_STATE(IconicState)
//   Motif Window Manager hints(_MOTIF_WM_HINTS 的 flags / decorations 两个字段,EWMH 附录所引)
//
//   rootless 下宿主就是窗口管理器:服务端维护 EWMH 要求窗口管理器维护的属性(客户端列表、活动窗口、WM_STATE、
//   _NET_FRAME_EXTENTS……),把客户端经根窗口 ClientMessage 提出的请求翻成 XWindowManagerRequest 交给宿主,
//   并把 ICCCM / EWMH 提示解析进 XTopLevelWindow。

using System.Buffers.Binary;
using System.Text;
using VelaShell.XServer.Host;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer.Server;

public sealed partial class X11Server
{
    private static readonly (string Atom, XWindowStates State)[] NetWmStates =
    [
        ("_NET_WM_STATE_MODAL", XWindowStates.Modal),
        ("_NET_WM_STATE_STICKY", XWindowStates.Sticky),
        ("_NET_WM_STATE_MAXIMIZED_VERT", XWindowStates.MaximizedVertical),
        ("_NET_WM_STATE_MAXIMIZED_HORZ", XWindowStates.MaximizedHorizontal),
        ("_NET_WM_STATE_SHADED", XWindowStates.Shaded),
        ("_NET_WM_STATE_SKIP_TASKBAR", XWindowStates.SkipTaskbar),
        ("_NET_WM_STATE_SKIP_PAGER", XWindowStates.SkipPager),
        ("_NET_WM_STATE_HIDDEN", XWindowStates.Hidden),
        ("_NET_WM_STATE_FULLSCREEN", XWindowStates.Fullscreen),
        ("_NET_WM_STATE_ABOVE", XWindowStates.Above),
        ("_NET_WM_STATE_BELOW", XWindowStates.Below),
        ("_NET_WM_STATE_DEMANDS_ATTENTION", XWindowStates.DemandsAttention),
        ("_NET_WM_STATE_FOCUSED", XWindowStates.Focused),
    ];

    private static readonly (string Atom, XWindowType Type)[] NetWmTypes =
    [
        ("_NET_WM_WINDOW_TYPE_NORMAL", XWindowType.Normal),
        ("_NET_WM_WINDOW_TYPE_DIALOG", XWindowType.Dialog),
        ("_NET_WM_WINDOW_TYPE_UTILITY", XWindowType.Utility),
        ("_NET_WM_WINDOW_TYPE_TOOLBAR", XWindowType.Toolbar),
        ("_NET_WM_WINDOW_TYPE_SPLASH", XWindowType.Splash),
        ("_NET_WM_WINDOW_TYPE_MENU", XWindowType.Menu),
        ("_NET_WM_WINDOW_TYPE_DROPDOWN_MENU", XWindowType.DropdownMenu),
        ("_NET_WM_WINDOW_TYPE_POPUP_MENU", XWindowType.PopupMenu),
        ("_NET_WM_WINDOW_TYPE_TOOLTIP", XWindowType.Tooltip),
        ("_NET_WM_WINDOW_TYPE_NOTIFICATION", XWindowType.Notification),
        ("_NET_WM_WINDOW_TYPE_COMBO", XWindowType.Combo),
        ("_NET_WM_WINDOW_TYPE_DND", XWindowType.Dnd),
        ("_NET_WM_WINDOW_TYPE_DOCK", XWindowType.Dock),
        ("_NET_WM_WINDOW_TYPE_DESKTOP", XWindowType.Desktop),
    ];

    /// <summary>会影响宿主看到的窗口快照的属性;别的属性(_NET_WM_USER_TIME 每次输入都改)变了不打扰宿主。</summary>
    private HashSet<uint>? _handleProperties;

    /// <summary>与 <see cref="NetWmStates" /> / <see cref="NetWmTypes" /> 一一对应的原子(初始化时算好,热路径上不再查字符串)。</summary>
    private uint[] _stateAtoms = [];
    private uint[] _typeAtoms = [];
    private uint _netWmStateAtom, _netWmTypeAtom, _wmStateAtom;

    // 刷新窗口快照时要读的属性的原子,初始化时算好。
    private uint _netWmNameAtom, _wmProtocolsAtom, _wmDeleteWindowAtom, _motifHintsAtom, _netWmOpacityAtom,
        _gtkFrameExtentsAtom, _netWmPidAtom, _wmClientMachineAtom, _wmRoleAtom, _netWmIconAtom;

    /// <summary>宿主给每个顶层设的外框尺寸(_NET_FRAME_EXTENTS):左、右、上、下。</summary>
    private readonly Dictionary<XWindow, (int Left, int Right, int Top, int Bottom)> _frameExtents = [];

    private void InitEwmh()
    {
        uint window = XAtom.Window, cardinal = XAtom.Cardinal, atom = XAtom.Atom;
        _stateAtoms = [.. NetWmStates.Select(s => Intern(s.Atom))];
        _typeAtoms = [.. NetWmTypes.Select(t => Intern(t.Atom))];
        _netWmStateAtom = Intern("_NET_WM_STATE");
        _netWmTypeAtom = Intern("_NET_WM_WINDOW_TYPE");
        _wmStateAtom = Intern("WM_STATE");
        _netWmNameAtom = Intern("_NET_WM_NAME");
        _wmProtocolsAtom = Intern("WM_PROTOCOLS");
        _wmDeleteWindowAtom = Intern("WM_DELETE_WINDOW");
        _motifHintsAtom = Intern("_MOTIF_WM_HINTS");
        _netWmOpacityAtom = Intern("_NET_WM_WINDOW_OPACITY");
        _gtkFrameExtentsAtom = Intern("_GTK_FRAME_EXTENTS");
        _netWmPidAtom = Intern("_NET_WM_PID");
        _wmClientMachineAtom = Intern("WM_CLIENT_MACHINE");
        _wmRoleAtom = Intern("WM_WINDOW_ROLE");
        _netWmIconAtom = Intern("_NET_WM_ICON");
        XWindow check = SelectionWindow;   // 服务端自己的隐藏窗口兼作 _NET_SUPPORTING_WM_CHECK 窗口
        List<string> supported =
        [
            "_NET_SUPPORTED", "_NET_SUPPORTING_WM_CHECK", "_NET_CLIENT_LIST", "_NET_CLIENT_LIST_STACKING",
            "_NET_NUMBER_OF_DESKTOPS", "_NET_DESKTOP_GEOMETRY", "_NET_DESKTOP_VIEWPORT", "_NET_CURRENT_DESKTOP",
            "_NET_ACTIVE_WINDOW", "_NET_WORKAREA", "_NET_CLOSE_WINDOW", "_NET_MOVERESIZE_WINDOW", "_NET_WM_MOVERESIZE",
            "_NET_REQUEST_FRAME_EXTENTS", "_NET_FRAME_EXTENTS", "_NET_WM_NAME", "_NET_WM_DESKTOP", "_NET_WM_WINDOW_TYPE",
            "_NET_WM_STATE", "_NET_WM_ICON", "_NET_WM_PID", "_NET_WM_WINDOW_OPACITY",
        ];
        supported.AddRange(NetWmStates.Select(s => s.Atom));
        supported.AddRange(NetWmTypes.Select(t => t.Atom));
        if (_options.ClientSideShadows)
        {
            supported.Add("_GTK_FRAME_EXTENTS");
        }
        SetProperty(Root, Intern("_NET_SUPPORTED"), atom, [.. supported.Select(Intern)]);
        SetProperty(Root, Intern("_NET_SUPPORTING_WM_CHECK"), window, [check.Id]);
        SetProperty(check, Intern("_NET_SUPPORTING_WM_CHECK"), window, [check.Id]);
        check.Properties[Intern("_NET_WM_NAME")] = new XProperty(Intern("UTF8_STRING"), 8, Encoding.UTF8.GetBytes(_options.WindowManagerName));
        SetProperty(Root, Intern("_NET_NUMBER_OF_DESKTOPS"), cardinal, [1]);
        SetProperty(Root, Intern("_NET_CURRENT_DESKTOP"), cardinal, [0]);
        SetProperty(Root, Intern("_NET_DESKTOP_VIEWPORT"), cardinal, [0, 0]);
        SetProperty(Root, Intern("_NET_ACTIVE_WINDOW"), window, [0]);
        SetProperty(Root, Intern("_NET_CLIENT_LIST"), window, []);
        SetProperty(Root, Intern("_NET_CLIENT_LIST_STACKING"), window, []);
        UpdateDesktopGeometry();

        _handleProperties =
        [
            XAtom.WmName, XAtom.WmClass, XAtom.WmTransientFor, XAtom.WmHints, XAtom.WmNormalHints,
            .. ((string[])["_NET_WM_NAME", "WM_PROTOCOLS", "_NET_WM_WINDOW_TYPE", "_NET_WM_STATE", "_MOTIF_WM_HINTS",
                "_NET_WM_ICON", "_NET_WM_WINDOW_OPACITY", "_GTK_FRAME_EXTENTS", "_NET_WM_PID", "WM_CLIENT_MACHINE",
                "WM_WINDOW_ROLE"]).Select(Intern),
        ];
    }

    /// <summary>这个属性变了要不要刷新宿主看到的快照。</summary>
    private bool AffectsHandle(uint property) => _handleProperties is null || _handleProperties.Contains(property);

    /// <summary>设一个 32 位属性(按本机序存)并发 PropertyNotify。</summary>
    private void SetProperty(XWindow window, uint property, uint type, uint[] values)
    {
        byte[] data = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i * 4), values[i]);
        }
        window.Properties[property] = new XProperty(type, 32, data);
        SendPropertyNotify(window, property, deleted: false);
    }

    private static uint[] ReadCard32s(XProperty? property)
    {
        if (property is not { Format: 32 })
        {
            return [];
        }
        uint[] values = new uint[property.Data.Length / 4];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadUInt32LittleEndian(property.Data.AsSpan(i * 4));
        }
        return values;
    }

    private void UpdateDesktopGeometry()
    {
        uint w = (uint)Root.Width, h = (uint)Root.Height;
        SetProperty(Root, Intern("_NET_DESKTOP_GEOMETRY"), XAtom.Cardinal, [w, h]);
        SetProperty(Root, Intern("_NET_WORKAREA"), XAtom.Cardinal, [0, 0, w, h]);
    }

    // ------------------------------------------------------------------ 窗口管理器维护的属性

    /// <summary>顶层映射了:WM_STATE = Normal、_NET_WM_DESKTOP = 0、_NET_FRAME_EXTENTS,并更新客户端列表。</summary>
    private void OnTopLevelMappedEwmh(XWindow top)
    {
        if (top.OverrideRedirect)
        {
            return;   // 菜单、提示框之类不归窗口管理器管(ICCCM §4.1.10)
        }
        uint hidden = (ReadHandleStates(top) & XWindowStates.Hidden) != 0 ? 3u : 1u;
        SetProperty(top, _wmStateAtom, _wmStateAtom, [hidden, 0]);
        SetProperty(top, Intern("_NET_WM_DESKTOP"), XAtom.Cardinal, [0]);
        WriteFrameExtents(top);
        UpdateClientLists();
    }

    private void OnTopLevelUnmappedEwmh(XWindow top)
    {
        if (top.OverrideRedirect)
        {
            return;
        }
        SetProperty(top, _wmStateAtom, _wmStateAtom, [0, 0]);   // Withdrawn
        UpdateClientLists();
    }

    /// <summary>_NET_CLIENT_LIST(映射顺序)与 _NET_CLIENT_LIST_STACKING(从下到上)。</summary>
    private void UpdateClientLists()
    {
        uint[] stacking = [.. Root.Children.Where(w => w is { Mapped: true, OverrideRedirect: false } && w.Owner is not null).Select(w => w.Id)];
        SetProperty(Root, Intern("_NET_CLIENT_LIST_STACKING"), XAtom.Window, stacking);
        SetProperty(Root, Intern("_NET_CLIENT_LIST"), XAtom.Window, [.. stacking.Order()]);
    }

    /// <summary>键盘焦点换了:_NET_ACTIVE_WINDOW 与各顶层的 _NET_WM_STATE_FOCUSED 跟着变。</summary>
    private void UpdateActiveWindow(XWindow? oldFocus, XWindow? newFocus)
    {
        XWindow? oldTop = oldFocus is { IsRoot: false } ? oldFocus.TopLevel : null;
        XWindow? newTop = newFocus is { IsRoot: false } ? newFocus.TopLevel : null;
        if (ReferenceEquals(oldTop, newTop))
        {
            return;
        }
        SetProperty(Root, Intern("_NET_ACTIVE_WINDOW"), XAtom.Window, [newTop?.Id ?? 0]);
        if (oldTop is { Mapped: true })
        {
            WriteStates(oldTop, ReadHandleStates(oldTop) & ~XWindowStates.Focused);
        }
        if (newTop is { Mapped: true })
        {
            WriteStates(newTop, ReadHandleStates(newTop) | XWindowStates.Focused);
        }
    }

    private XWindowStates ReadHandleStates(XWindow top)
    {
        XWindowStates states = XWindowStates.None;
        foreach (uint atom in ReadCard32s(top.Properties.GetValueOrDefault(_netWmStateAtom)))
        {
            int index = Array.IndexOf(_stateAtoms, atom);
            if (index >= 0)
            {
                states |= NetWmStates[index].State;
            }
        }
        return states;
    }

    private void WriteStates(XWindow top, XWindowStates states)
    {
        List<uint> atoms = [];
        for (int i = 0; i < NetWmStates.Length; i++)
        {
            if ((states & NetWmStates[i].State) != 0)
            {
                atoms.Add(_stateAtoms[i]);
            }
        }
        SetProperty(top, _netWmStateAtom, XAtom.Atom, [.. atoms]);
        if (top.Mapped && !top.OverrideRedirect)
        {
            SetProperty(top, _wmStateAtom, _wmStateAtom, [(states & XWindowStates.Hidden) != 0 ? 3u : 1u, 0]);
        }
        OnTopLevelPropertyChanged(top, _netWmStateAtom);
    }

    private void WriteFrameExtents(XWindow top)
    {
        (int l, int r, int t, int b) = _frameExtents.GetValueOrDefault(top);
        SetProperty(top, Intern("_NET_FRAME_EXTENTS"), XAtom.Cardinal, [(uint)l, (uint)r, (uint)t, (uint)b]);
    }

    // ------------------------------------------------------------------ 宿主注入(任意线程)

    /// <summary>
    /// 宿主(窗口管理器)设定了窗口状态 —— 通常是照办了一个 <see cref="XStateChangeRequest" />,或用户点了原生窗口的最大化按钮。
    /// 服务端写 <c>_NET_WM_STATE</c> 与 <c>WM_STATE</c>,客户端据此更新外观。<see cref="XWindowStates.Focused" /> 由服务端按焦点维护,这里给的会被忽略。
    /// </summary>
    public void SetTopLevelStates(uint topLevel, XWindowStates states) => Post(null, () =>
    {
        if (Lookup<XWindow>(topLevel) is { IsTopLevel: true } top)
        {
            XWindowStates focused = ReadHandleStates(top) & XWindowStates.Focused;
            WriteStates(top, (states & ~XWindowStates.Focused) | focused);
        }
    });

    /// <summary>宿主给窗口加的装饰有多宽(<c>_NET_FRAME_EXTENTS</c>:左、右、上、下,像素)。客户端据此计算外框位置。</summary>
    public void SetFrameExtents(uint topLevel, int left, int right, int top, int bottom) => Post(null, () =>
    {
        if (Lookup<XWindow>(topLevel) is { IsTopLevel: true } window)
        {
            _frameExtents[window] = (Math.Max(0, left), Math.Max(0, right), Math.Max(0, top), Math.Max(0, bottom));
            WriteFrameExtents(window);
        }
    });

    // ------------------------------------------------------------------ 客户端经根窗口提出的请求

    /// <summary>发到根窗口的 ClientMessage:窗口管理器的请求。解析、能自己办的自己办,其余交给宿主。</summary>
    private void OnRootClientMessage(XClient sender, byte[] raw)
    {
        bool be = sender.BigEndian;
        uint Read(int offset) => be ? BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(offset)) : BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offset));
        uint windowId = Read(4), type = Read(8);
        uint[] data = [Read(12), Read(16), Read(20), Read(24), Read(28)];
        if (Lookup<XWindow>(windowId) is not { IsTopLevel: true } top)
        {
            return;
        }
        XTopLevelWindow handle = HandleFor(top);
        string? name = AtomName(type);
        switch (name)
        {
            case "_NET_WM_MOVERESIZE":
                {
                    var direction = (XMoveResizeDirection)Math.Min(data[2], 11u);
                    if (direction != XMoveResizeDirection.Cancel)
                    {
                        ReleaseButtonForWindowManager((int)data[3]);
                    }
                    _host.WindowManagerRequest(new XMoveResizeRequest(handle, direction, (int)data[3], (int)data[0], (int)data[1]));
                    break;
                }
            case "_NET_WM_STATE":
                {
                    XWindowStates changed = XWindowStates.None;
                    foreach (uint atom in (uint[])[data[1], data[2]])
                    {
                        int index = atom == 0 ? -1 : Array.IndexOf(_stateAtoms, atom);
                        if (index >= 0)
                        {
                            changed |= NetWmStates[index].State;
                        }
                    }
                    changed &= ~XWindowStates.Focused;
                    XWindowStates current = ReadHandleStates(top);
                    (XWindowStates add, XWindowStates remove) = data[0] switch
                    {
                        0 => (XWindowStates.None, changed),
                        1 => (changed, XWindowStates.None),
                        _ => (changed & ~current, changed & current),   // Toggle
                    };
                    if (add != XWindowStates.None || remove != XWindowStates.None)
                    {
                        _host.WindowManagerRequest(new XStateChangeRequest(handle, add, remove));
                    }
                    break;
                }
            case "_NET_ACTIVE_WINDOW":
                _host.WindowManagerRequest(new XActivateRequest(handle));
                break;
            case "_NET_CLOSE_WINDOW":
                _host.WindowManagerRequest(new XCloseRequest(handle));
                break;
            case "WM_CHANGE_STATE" when data[0] == 3:   // IconicState
                _host.WindowManagerRequest(new XMinimizeRequest(handle));
                break;
            case "_NET_REQUEST_FRAME_EXTENTS":
                WriteFrameExtents(top);
                break;
            case "_NET_MOVERESIZE_WINDOW":
                {
                    // data[0]:低 8 位重力,第 8–11 位表示 x / y / 宽 / 高各给没给
                    uint flags = data[0];
                    int x = (flags & (1 << 8)) != 0 ? (int)data[1] : top.X;
                    int y = (flags & (1 << 9)) != 0 ? (int)data[2] : top.Y;
                    int w = (flags & (1 << 10)) != 0 ? Math.Max(1, (int)data[3]) : top.Width;
                    int h = (flags & (1 << 11)) != 0 ? Math.Max(1, (int)data[4]) : top.Height;
                    ConfigureWindow(top, x, y, w, h, top.BorderWidth, null, -1);
                    break;
                }
        }
    }

    /// <summary>
    /// 窗口管理器接管了指针(开始拖动 / 缩放):客户端不会再收到这个按钮的松开,服务端当它已经松开,
    /// 并解除按下时形成的隐式抓取 —— 与真实的窗口管理器抓走指针时一致。
    /// </summary>
    private void ReleaseButtonForWindowManager(int button)
    {
        if (button is >= 1 and <= 255)
        {
            _buttonsDown[button >> 3] &= (byte)~(1 << (button & 7));
            if (button <= 5)
            {
                _buttons &= (ushort)~(0x100 << (button - 1));
            }
        }
        else
        {
            Array.Clear(_buttonsDown);
            _buttons = 0;
        }
        if (_pointerGrab is { ReleaseWhenButtonsUp: true } && _buttons == 0)
        {
            _pointerGrab = null;
            UpdateCursor();
        }
    }

    // ------------------------------------------------------------------ 客户端的提示 → 宿主看到的快照

    /// <summary>把 ICCCM / EWMH / Motif 提示解析进窗口快照(RefreshHandle 的一部分)。</summary>
    private void RefreshWindowManagerHints(XWindow top, XTopLevelWindow handle)
    {
        Dictionary<uint, XProperty> props = top.Properties;
        handle.HasAlpha = top.Depth == 32;
        handle.States = ReadHandleStates(top);

        uint[] types = ReadCard32s(props.GetValueOrDefault(_netWmTypeAtom));
        handle.WindowType = handle.TransientFor != 0 ? XWindowType.Dialog : XWindowType.Normal;
        foreach (uint atom in types)   // 按偏好顺序,第一个认识的为准
        {
            int index = Array.IndexOf(_typeAtoms, atom);
            if (index >= 0)
            {
                handle.WindowType = NetWmTypes[index].Type;
                break;
            }
        }

        uint[] motif = ReadCard32s(props.GetValueOrDefault(_motifHintsAtom));
        handle.Decorated = motif.Length < 3 || (motif[0] & 2) == 0 || motif[2] != 0;

        uint[] size = ReadCard32s(props.GetValueOrDefault(XAtom.WmNormalHints));
        if (size.Length >= 11)
        {
            uint flags = size[0];
            (handle.MinWidth, handle.MinHeight) = (flags & 16) != 0 ? ((int)size[5], (int)size[6]) : (0, 0);
            (handle.MaxWidth, handle.MaxHeight) = (flags & 32) != 0 ? ((int)size[7], (int)size[8]) : (0, 0);
            (handle.WidthIncrement, handle.HeightIncrement) = (flags & 64) != 0 ? ((int)size[9], (int)size[10]) : (0, 0);
        }

        uint[] hints = ReadCard32s(props.GetValueOrDefault(XAtom.WmHints));
        handle.AcceptsFocus = hints.Length < 2 || (hints[0] & 1) == 0 || hints[1] != 0;
        handle.Urgent = (hints.Length >= 1 && (hints[0] & 256) != 0) || (handle.States & XWindowStates.DemandsAttention) != 0;

        uint[] opacity = ReadCard32s(props.GetValueOrDefault(_netWmOpacityAtom));
        handle.Opacity = opacity.Length >= 1 ? opacity[0] / (double)uint.MaxValue : 1;

        uint[] extents = ReadCard32s(props.GetValueOrDefault(_gtkFrameExtentsAtom));
        handle.ClientFrameExtents = extents.Length >= 4 ? ((int)extents[0], (int)extents[1], (int)extents[2], (int)extents[3]) : default;

        uint[] pid = ReadCard32s(props.GetValueOrDefault(_netWmPidAtom));
        handle.ProcessId = pid.Length >= 1 ? (int)pid[0] : 0;
        handle.ClientMachine = props.GetValueOrDefault(_wmClientMachineAtom) is { Format: 8 } machine ? XWire.Latin1.GetString(machine.Data) : "";
        handle.Role = props.GetValueOrDefault(_wmRoleAtom) is { Format: 8 } role ? XWire.Latin1.GetString(role.Data) : "";

        XProperty? icon = props.GetValueOrDefault(_netWmIconAtom);
        if (!ReferenceEquals(icon, handle.IconSource))
        {
            // 图标动辄几百 KB:只在属性真的换了时重新解析,改标题之类的刷新不重复这份工作。
            handle.Icons = ParseIcons(ReadCard32s(icon));
            handle.IconSource = icon;
        }
    }

    /// <summary>_NET_WM_ICON:若干组(宽, 高, 宽 × 高 个 ARGB)。尺寸不合理的组丢弃,后面的不再解析。</summary>
    private static List<XWindowIcon> ParseIcons(uint[] data)
    {
        List<XWindowIcon> icons = [];
        int i = 0;
        while (i + 2 <= data.Length)
        {
            uint w = data[i], h = data[i + 1];
            if (w == 0 || h == 0 || w > 1024 || h > 1024 || i + 2 + (w * h) > data.Length)
            {
                break;
            }
            icons.Add(new XWindowIcon((int)w, (int)h, data.AsSpan(i + 2, (int)(w * h)).ToArray()));
            i += 2 + (int)(w * h);
        }
        return icons;
    }

    private void CleanupEwmh(XWindow window) => _frameExtents.Remove(window);
}
