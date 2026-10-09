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
//   ICCCM 2.0 —— §4.1.2.3 WM_NORMAL_HINTS(含基准尺寸、宽高比、win_gravity、USPosition / PPosition)、§4.1.2.4 WM_HINTS(含 initial_state、
//   icon_pixmap / icon_mask、window_group)、§4.1.3.1 WM_STATE、§4.1.4 WM_CHANGE_STATE(IconicState)
//   Motif Window Manager hints(_MOTIF_WM_HINTS 的 flags / functions / decorations 三个字段,EWMH 附录所引)
//
//   rootless 下宿主就是窗口管理器:服务端维护 EWMH 要求窗口管理器维护的属性(客户端列表、活动窗口、WM_STATE、
//   _NET_FRAME_EXTENTS……),把客户端经根窗口 ClientMessage 提出的请求翻成 XWindowManagerRequest 交给宿主,
//   并把 ICCCM / EWMH 提示解析进 XTopLevelWindow。

using System.Buffers.Binary;
using System.Text;
using VelaShell.XServer.Drawing;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

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
    private uint _netWmNameAtom, _wmProtocolsAtom, _wmDeleteWindowAtom, _wmTakeFocusAtom, _motifHintsAtom, _netWmOpacityAtom,
        _gtkFrameExtentsAtom, _netWmPidAtom, _wmClientMachineAtom, _wmRoleAtom, _netWmIconAtom, _utf8StringAtom, _compoundTextAtom,
        _netWmStrutAtom, _netWmStrutPartialAtom;

    /// <summary>宿主给每个顶层设的外框尺寸(_NET_FRAME_EXTENTS):左、右、上、下。</summary>
    private readonly Dictionary<XWindow, XFrameExtents> _frameExtents = [];

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
        _wmTakeFocusAtom = Intern("WM_TAKE_FOCUS");
        _motifHintsAtom = Intern("_MOTIF_WM_HINTS");
        _netWmOpacityAtom = Intern("_NET_WM_WINDOW_OPACITY");
        _gtkFrameExtentsAtom = Intern("_GTK_FRAME_EXTENTS");
        _netWmPidAtom = Intern("_NET_WM_PID");
        _wmClientMachineAtom = Intern("WM_CLIENT_MACHINE");
        _wmRoleAtom = Intern("WM_WINDOW_ROLE");
        _netWmIconAtom = Intern("_NET_WM_ICON");
        _utf8StringAtom = Intern("UTF8_STRING");
        _compoundTextAtom = Intern("COMPOUND_TEXT");
        _netWmStrutAtom = Intern("_NET_WM_STRUT");
        _netWmStrutPartialAtom = Intern("_NET_WM_STRUT_PARTIAL");
        _handleProperties =
        [
            XAtom.WmName, XAtom.WmClass, XAtom.WmTransientFor, XAtom.WmHints, XAtom.WmNormalHints,
            .. ((string[])["_NET_WM_NAME", "WM_PROTOCOLS", "_NET_WM_WINDOW_TYPE", "_NET_WM_STATE", "_MOTIF_WM_HINTS",
                "_NET_WM_ICON", "_NET_WM_WINDOW_OPACITY", "_GTK_FRAME_EXTENTS", "_NET_WM_PID", "WM_CLIENT_MACHINE",
                "WM_WINDOW_ROLE", "_NET_WM_STRUT", "_NET_WM_STRUT_PARTIAL"]).Select(Intern),
        ];
        if (Rootful)
        {
            return;   // 单窗口模式:窗口管理器是远端的,WM_S0、_NET_SUPPORTED、客户端列表这些归它
        }
        XWindow check = SelectionWindow;   // 服务端自己的隐藏窗口兼作 _NET_SUPPORTING_WM_CHECK 窗口
        List<string> supported =
        [
            "_NET_SUPPORTED", "_NET_SUPPORTING_WM_CHECK", "_NET_CLIENT_LIST", "_NET_CLIENT_LIST_STACKING",
            "_NET_NUMBER_OF_DESKTOPS", "_NET_DESKTOP_GEOMETRY", "_NET_DESKTOP_VIEWPORT", "_NET_CURRENT_DESKTOP",
            "_NET_ACTIVE_WINDOW", "_NET_WORKAREA", "_NET_CLOSE_WINDOW", "_NET_MOVERESIZE_WINDOW", "_NET_WM_MOVERESIZE",
            "_NET_REQUEST_FRAME_EXTENTS", "_NET_FRAME_EXTENTS", "_NET_WM_NAME", "_NET_WM_DESKTOP", "_NET_WM_WINDOW_TYPE",
            "_NET_WM_STATE", "_NET_WM_ICON", "_NET_WM_PID", "_NET_WM_WINDOW_OPACITY", "_NET_WM_PING",
            "_NET_WM_SYNC_REQUEST", "_NET_WM_SYNC_REQUEST_COUNTER",
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
        // ICCCM §2.8:窗口管理器占有 WM_S0(屏幕 0 的管理器选区)。与根窗口的 SubstructureRedirect 一起,远端误跑的窗口管理器
        // 一看就知道已经有窗口管理器了(真实桌面上就是这样)。
        _selections[new SelectionSlot(Intern("WM_S0"), null)] = (check, null, 0);
        SetProperty(Root, Intern("_NET_NUMBER_OF_DESKTOPS"), cardinal, [1]);
        SetProperty(Root, Intern("_NET_CURRENT_DESKTOP"), cardinal, [0]);
        SetProperty(Root, Intern("_NET_DESKTOP_VIEWPORT"), cardinal, [0, 0]);
        SetProperty(Root, Intern("_NET_ACTIVE_WINDOW"), window, [0]);
        SetProperty(Root, Intern("_NET_CLIENT_LIST"), window, []);
        SetProperty(Root, Intern("_NET_CLIENT_LIST_STACKING"), window, []);
        UpdateDesktopGeometry();
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
        StoreServerProperty(window, property, new XProperty(type, 32, data));
        SendPropertyNotify(window, property, deleted: false);
    }

    /// <summary>_NET_WM_STATE、_NET_WM_WINDOW_TYPE 这类原子列表最多看这么多个(认识的状态与类型总共十几个)。</summary>
    private const int MaxHintAtoms = 64;

    /// <summary>交给宿主的标题最多这么多个字符;类名、机器名、角色最多 <see cref="MaxHostNameChars" /> 个。</summary>
    internal const int MaxHostTitleChars = 4096, MaxHostNameChars = 256;

    /// <summary>
    /// 交给宿主的字符串(标题、类名、机器名、角色):只解码够 <paramref name="maxChars" /> 个字符的那几个字节(属性能有 32 MB,
    /// 快照每次几何刷新都要重建),去掉控制字符与双向排版控制符 —— 后者能在任务栏、标题栏里把标题倒着显示、伪造来源。
    /// </summary>
    internal static string HostText(ReadOnlySpan<byte> data, bool utf8, int maxChars) =>
        HostText(data, utf8 ? XTextEncoding.Utf8 : XTextEncoding.Latin1, maxChars);

    /// <summary>
    /// TEXT 类属性(WM_NAME、WM_CLIENT_MACHINE、剪贴板)按类型解码(ICCCM §2.7.1):UTF8_STRING、COMPOUND_TEXT,其余按 STRING(Latin-1)。
    /// 原先一律按 Latin-1,类型是 UTF8_STRING / COMPOUND_TEXT 的标题成了乱码。
    /// </summary>
    private XTextEncoding TextEncodingOf(uint type) =>
        type == _utf8StringAtom ? XTextEncoding.Utf8 : type == _compoundTextAtom ? XTextEncoding.CompoundText : XTextEncoding.Latin1;

    /// <summary>同 <see cref="HostText(ReadOnlySpan{byte}, bool, int)" />,按 <paramref name="encoding" /> 解码(COMPOUND_TEXT 的转义序列也算在字节预算里,多给一些)。</summary>
    internal static string HostText(ReadOnlySpan<byte> data, XTextEncoding encoding, int maxChars)
    {
        int maxBytes = encoding switch
        {
            XTextEncoding.Latin1 => maxChars,
            XTextEncoding.Utf8 => maxChars * 4,
            _ => (maxChars * 4) + 1024,
        };
        string text = XText.Decode(data.Length > maxBytes ? data[..maxBytes] : data, encoding);
        if (text.Length > maxChars)
        {
            text = text[..(char.IsHighSurrogate(text[maxChars - 1]) ? maxChars - 1 : maxChars)];
        }
        return text.AsSpan().ContainsAny(DisallowedHostChars) ? string.Concat(text.Where(ch => !DisallowedHostChars.Contains(ch))) : text;
    }

    /// <summary>C0 / C1 控制字符,以及双向排版控制符(LRM / RLM / ALM、LRE…RLO、LRI…PDI)。</summary>
    private static readonly System.Buffers.SearchValues<char> DisallowedHostChars = System.Buffers.SearchValues.Create(
        string.Concat(Enumerable.Range(0, 0x20).Concat(Enumerable.Range(0x7F, 0x21)).Select(c => (char)c))
        + "؜‎‏‪‫‬‭‮⁦⁧⁨⁩");

    /// <summary>32 位属性的前 <paramref name="max" /> 个值(提示类属性只看头几个:客户端可以把它们设成 32 MB,快照每次几何刷新都要读一遍)。</summary>
    private static uint[] ReadCard32s(XProperty? property, int max = int.MaxValue)
    {
        if (property is not { Format: 32 })
        {
            return [];
        }
        uint[] values = new uint[Math.Min(property.Data.Length / 4, max)];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadUInt32LittleEndian(property.Data[(i * 4)..]);
        }
        return values;
    }

    private void UpdateDesktopGeometry()
    {
        if (Rootful)
        {
            return;   // 单窗口模式:_NET_DESKTOP_GEOMETRY、_NET_WORKAREA 归远端的窗口管理器
        }
        uint w = (uint)Root.Width, h = (uint)Root.Height;
        SetProperty(Root, Intern("_NET_DESKTOP_GEOMETRY"), XAtom.Cardinal, [w, h]);
        XRect area = WorkArea();
        SetProperty(Root, Intern("_NET_WORKAREA"), XAtom.Cardinal, [(uint)area.X, (uint)area.Y, (uint)area.Width, (uint)area.Height]);
    }

    /// <summary>
    /// EWMH「_NET_WORKAREA」:整个根窗口扣掉各台显示器在虚拟桌面边缘上让出来的部分(<see cref="XMonitor.WorkArea" />;
    /// 相当于任务栏、Dock 占的 strut)。原先恒为整个根窗口,菜单、最大化、对话框落到任务栏后面。扣完为空时退回整个根窗口。
    /// </summary>
    private XRect WorkArea()
    {
        int width = Root.Width, height = Root.Height;
        int left = 0, top = 0, right = 0, bottom = 0;
        foreach (XMonitor m in _monitors)
        {
            if (m.WorkArea is not { } area)
            {
                continue;
            }
            if (m.X == 0)
            {
                left = Math.Max(left, area.X - m.X);
            }
            if (m.Y == 0)
            {
                top = Math.Max(top, area.Y - m.Y);
            }
            if (m.X + m.Width == width)
            {
                right = Math.Max(right, m.X + m.Width - area.Right);
            }
            if (m.Y + m.Height == height)
            {
                bottom = Math.Max(bottom, m.Y + m.Height - area.Bottom);
            }
        }
        XRect result = new(left, top, width - left - right, height - top - bottom);
        return result.IsEmpty ? new XRect(0, 0, width, height) : result;
    }

    // ------------------------------------------------------------------ 窗口管理器维护的属性

    /// <summary>顶层映射了:WM_STATE = Normal、_NET_WM_DESKTOP = 0、_NET_FRAME_EXTENTS,并更新客户端列表。</summary>
    private void OnTopLevelMappedEwmh(XWindow top)
    {
        if (top.OverrideRedirect || top.IsInputOnly)
        {
            return;   // 菜单、提示框之类不归窗口管理器管(ICCCM §4.1.10);InputOnly 的顶层看不见,也不进客户端列表
        }
        XWindowStates states = ReadNetWmStates(top);
        if ((states & XWindowStates.Hidden) == 0 && StartsIconic(top))
        {
            // ICCCM §4.1.4:从 Withdrawn 映射时按 WM_HINTS 的 initial_state 进 IconicState(xterm -iconic、Tk 的 wm iconify 后再映射)。
            // 原先一律 Normal;写成 _NET_WM_STATE_HIDDEN,宿主看快照的状态就把原生窗口最小化显示。
            WriteStates(top, states | XWindowStates.Hidden);
            states |= XWindowStates.Hidden;
        }
        uint hidden = (states & XWindowStates.Hidden) != 0 ? 3u : 1u;
        SetProperty(top, _wmStateAtom, _wmStateAtom, [hidden, 0]);
        SetProperty(top, Intern("_NET_WM_DESKTOP"), XAtom.Cardinal, [0]);
        WriteFrameExtents(top);
        if (!_clientListOrder.Contains(top))
        {
            _clientListOrder.Add(top);
        }
        UpdateClientLists();
    }

    /// <summary>
    /// 顶层取消映射(Withdrawn):WM_STATE 写 Withdrawn,删掉 _NET_WM_STATE 与 _NET_WM_DESKTOP(EWMH:窗口管理器在窗口 withdraw 时删它们)——
    /// 原先留着,重新映射时带着过期的 Hidden / Focused 又被最小化、画成活动外观。
    /// </summary>
    private void OnTopLevelUnmappedEwmh(XWindow top)
    {
        if (top.OverrideRedirect || top.IsInputOnly)
        {
            return;
        }
        SetProperty(top, _wmStateAtom, _wmStateAtom, [0, 0]);   // Withdrawn
        foreach (uint property in (uint[])[_netWmStateAtom, Intern("_NET_WM_DESKTOP")])
        {
            if (top.Properties.Remove(property, out XProperty? removed))
            {
                ReleaseProperty(removed);
                SendPropertyNotify(top, property, deleted: true);
                OnTopLevelPropertyChanged(top, property);
            }
        }
        _clientListOrder.Remove(top);
        UpdateClientLists();
    }

    /// <summary>管着的顶层,按第一次映射的先后(_NET_CLIENT_LIST 的次序)。</summary>
    private readonly List<XWindow> _clientListOrder = [];

    /// <summary>
    /// _NET_CLIENT_LIST(按第一次映射的先后,EWMH §3.3;原先按 XID 排)与 _NET_CLIENT_LIST_STACKING(从下到上)。
    /// </summary>
    private void UpdateClientLists()
    {
        uint[] stacking = [.. Root.Children.Where(w => w is { Mapped: true, OverrideRedirect: false, IsInputOnly: false } && w.Owner is not null).Select(w => w.Id)];
        SetProperty(Root, Intern("_NET_CLIENT_LIST_STACKING"), XAtom.Window, stacking);
        SetProperty(Root, Intern("_NET_CLIENT_LIST"), XAtom.Window, [.. _clientListOrder.Where(w => w.Mapped && w.IsTopLevel).Select(w => w.Id)]);
    }

    /// <summary>当前的活动窗口(_NET_ACTIVE_WINDOW):带键盘焦点的、窗口管理器管着的顶层。</summary>
    private XWindow? _activeTopLevel;

    /// <summary>
    /// 键盘焦点换了:_NET_ACTIVE_WINDOW 与各顶层的 _NET_WM_STATE_FOCUSED 跟着变。焦点进了 override-redirect 的弹层(菜单抓键盘)
    /// 不算换了活动窗口 —— 原先活动窗口与 FOCUSED 被挪到弹层上,主窗口画成非活动的样子。
    /// </summary>
    private void UpdateActiveWindow(XWindow? _, XWindow? newFocus)   // 第一个参数是旧焦点:活动窗口另行记着(_activeTopLevel),用不着
    {
        XWindow? newTop = newFocus is { IsRoot: false } ? newFocus.TopLevel : null;
        if (newTop is { OverrideRedirect: true } or { IsInputOnly: true } || ReferenceEquals(_activeTopLevel, newTop))
        {
            return;
        }
        XWindow? oldTop = _activeTopLevel;
        _activeTopLevel = newTop;
        SetProperty(Root, Intern("_NET_ACTIVE_WINDOW"), XAtom.Window, [newTop?.Id ?? 0]);
        if (oldTop is { Mapped: true })
        {
            WriteStates(oldTop, ReadNetWmStates(oldTop) & ~XWindowStates.Focused);
        }
        if (newTop is { Mapped: true })
        {
            WriteStates(newTop, ReadNetWmStates(newTop) | XWindowStates.Focused);
        }
    }

    /// <summary>_NET_WM_STATE 属性解析成状态位。</summary>
    private XWindowStates ReadNetWmStates(XWindow top)
    {
        XWindowStates states = XWindowStates.None;
        foreach (uint atom in ReadCard32s(top.Properties.GetValueOrDefault(_netWmStateAtom), MaxHintAtoms))
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
        XFrameExtents e = _frameExtents.GetValueOrDefault(top);
        SetProperty(top, Intern("_NET_FRAME_EXTENTS"), XAtom.Cardinal, [(uint)e.Left, (uint)e.Right, (uint)e.Top, (uint)e.Bottom]);
    }

    // ------------------------------------------------------------------ 客户端经根窗口提出的请求

    /// <summary>发到根窗口的 ClientMessage:窗口管理器的请求。解析、能自己办的自己办,其余交给宿主。</summary>
    private void OnRootClientMessage(XClient sender, byte[] raw)
    {
        if (Rootful)
        {
            return;   // 单窗口模式:发给窗口管理器的请求由远端的窗口管理器照常收(它选了根窗口的 SubstructureRedirect)
        }
        bool be = sender.BigEndian;
        uint Read(int offset) => be ? BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(offset)) : BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offset));
        uint windowId = Read(4), type = Read(8);
        uint[] data = [Read(12), Read(16), Read(20), Read(24), Read(28)];
        if (type == _wmProtocolsAtom && data[0] == Intern("_NET_WM_PING"))
        {
            OnPong(data[1], data[2]);   // ping 的回应:窗口字段是根窗口,data[2] 才是它自己的窗口
            return;
        }
        if (Use<XWindow>(windowId) is not { IsTopLevel: true } top)
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
                    if (direction != XMoveResizeDirection.Cancel && ReferenceEquals(PointerGrab?.Client, sender))
                    {
                        // 只有发起拖动的那个程序(按下时的自动抓取归它)才能让服务端当按钮已经松开:原先任何客户端对任何顶层发一条、
                        // data[3] 给 0 或越界值,就清掉全局的按钮状态并解除别人的抓取,打断别的会话正在进行的拖动。
                        ReleaseButtonForWindowManager((int)data[3]);
                    }
                    _host.WindowManagerRequested(new XMoveResizeRequest(handle, direction, (int)data[3], (int)data[0], (int)data[1]));
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
                    XWindowStates current = ReadNetWmStates(top);
                    (XWindowStates add, XWindowStates remove) = data[0] switch
                    {
                        0 => (XWindowStates.None, changed),
                        1 => (changed, XWindowStates.None),
                        _ => (changed & ~current, changed & current),   // Toggle
                    };
                    if (add != XWindowStates.None || remove != XWindowStates.None)
                    {
                        _host.WindowManagerRequested(new XStateChangeRequest(handle, add, remove));
                    }
                    break;
                }
            case "_NET_ACTIVE_WINDOW":
                {
                    // EWMH §「_NET_ACTIVE_WINDOW」:data[0] 是来源(1 普通程序、2 分页器),data[1] 是引起它的那次用户操作的时间戳。
                    // 时间戳不早于用户最近一次在 X 里按键 / 按按钮才算用户引起的;CurrentTime 与过期的时间戳不算 —— 原先一律转给宿主激活,
                    // 任何 X 客户端都能在任意时刻把自己的窗口切到系统前台。
                    int source = (int)Math.Min(data[0], 2u);
                    uint time = data[1];
                    bool user = source == 2 || (time != 0 && _lastUserInputTime != 0 && unchecked((int)(time - _lastUserInputTime)) >= 0
                                                && unchecked((int)(time - Now)) <= 0);
                    _host.WindowManagerRequested(new XActivateRequest(handle) { Source = source, Timestamp = time, UserInitiated = user });
                    break;
                }
            case "_NET_CLOSE_WINDOW":
                _host.WindowManagerRequested(new XCloseRequest(handle));
                break;
            case "WM_CHANGE_STATE" when data[0] == 3:   // IconicState
                _host.WindowManagerRequested(new XMinimizeRequest(handle));
                break;
            case "_NET_REQUEST_FRAME_EXTENTS":
                WriteFrameExtents(top);
                break;
            case "_NET_MOVERESIZE_WINDOW":
                {
                    // data[0]:低 8 位重力,第 8–11 位表示 x / y / 宽 / 高各给没给。值是任意 32 位整数,而 X 的坐标是 16 位、
                    // 尺寸 1–32767:宽高越界的请求整个不理,坐标夹到 16 位 —— 原先原样交给 Configure,别的会话的客户端发一个
                    // 2³¹ 的宽度,GetGeometry 与 ConfigureNotify 截断成乱值、指针的根坐标溢出、宿主收到 2³¹ 大小的原生窗口。
                    // 给的坐标与 ConfigureRequest 一样指参考点(EWMH「_NET_MOVERESIZE_WINDOW」,ICCCM §4.1.2.3):映射着的窗口按重力
                    // (0 = 用窗口自己的 win_gravity)与宿主给的外框换算成 X 窗口的位置,外框而不是内容区对准它 —— 窗口管理器在这里就摆好了,
                    // 宿主照着摆即可;还没映射的只记下请求的位置,映射时由宿主摆(见 XTopLevelSnapshot.NeedsPlacement)。
                    uint flags = data[0];
                    bool move = (flags & (3 << 8)) != 0;
                    XGravity gravity = (flags & 0xFF) is >= 1 and <= 10 ? (XGravity)(flags & 0xFF) : HandleFor(top).Snapshot.WinGravity;
                    (int dx, int dy) = top.Mapped
                        ? XTopLevelSnapshot.GravityOffset(gravity, _frameExtents.GetValueOrDefault(top), top.BorderWidth)
                        : (0, 0);
                    int x = (flags & (1 << 8)) != 0 ? (int)Math.Clamp((long)(int)data[1] + dx, short.MinValue, short.MaxValue) : top.X;
                    int y = (flags & (1 << 9)) != 0 ? (int)Math.Clamp((long)(int)data[2] + dy, short.MinValue, short.MaxValue) : top.Y;
                    int w = (flags & (1 << 10)) != 0 ? (int)data[3] : top.Width;
                    int h = (flags & (1 << 11)) != 0 ? (int)data[4] : top.Height;
                    if (w is < 1 or > short.MaxValue || h is < 1 or > short.MaxValue)
                    {
                        break;
                    }
                    if (top.Buffer is not null && (w != top.Width || h != top.Height))
                    {
                        try
                        {
                            RequireBufferMemory(top, w, h);   // 缓冲要跟着变大:先核账(与 ConfigureWindow 一样)
                        }
                        catch (XProtocolError)
                        {
                            break;
                        }
                    }
                    if (move && (x != top.X || y != top.Y))
                    {
                        top.PositionRequested = !top.Mapped;
                    }
                    Configure(top, x, y, w, h, top.BorderWidth, null, -1);
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
            for (int physical = 1; physical <= 255; physical++)
            {
                if (MapButton(physical) == button)
                {
                    _physicalButtonsDown[physical >> 3] &= (byte)~(1 << (physical & 7));   // 客户端说的是生效的按钮号
                }
            }
        }
        else
        {
            Array.Clear(_buttonsDown);
            Array.Clear(_physicalButtonsDown);
            _buttons = 0;
        }
        if (PointerGrab is { ReleaseWhenButtonsUp: true } && _buttons == 0)
        {
            PointerGrab = null;
            UpdateCursor();
        }
    }

    // ------------------------------------------------------------------ 客户端的提示 → 宿主看到的快照

    /// <summary>把 ICCCM / EWMH / Motif 提示解析进窗口快照(<see cref="BuildSnapshot" /> 的一部分)。</summary>
    private XTopLevelSnapshot ReadWindowManagerHints(XWindow top, XTopLevelSnapshot snapshot, bool hasTransientFor)
    {
        Dictionary<uint, XProperty> props = top.Properties;
        XWindowStates states = ReadNetWmStates(top);

        XWindowType type = hasTransientFor ? XWindowType.Dialog : XWindowType.Normal;
        foreach (uint atom in ReadCard32s(props.GetValueOrDefault(_netWmTypeAtom), MaxHintAtoms))   // 按偏好顺序,第一个认识的为准
        {
            int index = Array.IndexOf(_typeAtoms, atom);
            if (index >= 0)
            {
                type = NetWmTypes[index].Type;
                break;
            }
        }

        uint[] motif = ReadCard32s(props.GetValueOrDefault(_motifHintsAtom), 5);
        uint[] hints = ReadCard32s(props.GetValueOrDefault(XAtom.WmHints), 9);
        uint hintFlags = hints.Length >= 1 ? hints[0] : 0;
        uint[] opacity = ReadCard32s(props.GetValueOrDefault(_netWmOpacityAtom), 1);
        uint[] extents = ReadCard32s(props.GetValueOrDefault(_gtkFrameExtentsAtom), 4);
        uint[] pid = ReadCard32s(props.GetValueOrDefault(_netWmPidAtom), 1);
        uint[] strut = ReadCard32s(props.GetValueOrDefault(_netWmStrutPartialAtom), 4) is { Length: 4 } partial
            ? partial
            : ReadCard32s(props.GetValueOrDefault(_netWmStrutAtom), 4);

        // _NET_WM_ICON 优先;没有时取 WM_HINTS 的 icon_pixmap / icon_mask(老程序只给那个)。
        XProperty? icon = props.GetValueOrDefault(_netWmIconAtom);
        XProperty? iconSource = icon ?? props.GetValueOrDefault(XAtom.WmHints);
        if (!ReferenceEquals(iconSource, top.ParsedIcons.Source))
        {
            // 图标动辄几百 KB:只在属性真的换了时重新解析,改标题之类的刷新不重复这份工作(各份快照共用同一个列表)。
            top.ParsedIcons = (iconSource, icon is not null ? ParseIcons(ReadCard32s(icon, MaxIconWords)) : IconFromPixmap(hints));
        }

        uint windowGroup = (hintFlags & WmHintWindowGroup) != 0 && hints.Length >= 9 ? hints[8] : 0;
        return ReadSizeHints(props, snapshot) with
        {
            States = states,
            WindowType = type,
            Decorated = motif.Length < 3 || (motif[0] & 2) == 0 || motif[2] != 0,
            Functions = motif.Length >= 2 && (motif[0] & 1) != 0 ? MotifFunctions(motif[1]) : XWindowFunctions.All,
            AcceptsFocus = hints.Length < 2 || (hintFlags & 1) == 0 || hints[1] != 0,
            Urgent = (hintFlags & 256) != 0 || (states & XWindowStates.DemandsAttention) != 0,
            WindowGroup = windowGroup != 0 && Lookup<XWindow>(windowGroup) is { IsTopLevel: true } leader ? HandleFor(leader) : null,
            Opacity = opacity.Length >= 1 ? opacity[0] / (double)uint.MaxValue : 1,
            ClientFrameExtents = extents.Length >= 4
                ? new XFrameExtents(HintSize(extents[0]), HintSize(extents[1]), HintSize(extents[2]), HintSize(extents[3]))
                : default,
            ProcessId = pid.Length >= 1 && pid[0] <= int.MaxValue ? (int)pid[0] : 0,
            ClientMachine = props.GetValueOrDefault(_wmClientMachineAtom) is { Format: 8 } machine ? HostText(machine.Data, TextEncodingOf(machine.Type), MaxHostNameChars) : "",
            Role = props.GetValueOrDefault(_wmRoleAtom) is { Format: 8 } role ? HostText(role.Data, utf8: false, MaxHostNameChars) : "",
            Icons = top.ParsedIcons.Icons,
            Strut = strut.Length == 4
                ? new XFrameExtents(HintSize(strut[0]), HintSize(strut[1]), HintSize(strut[2]), HintSize(strut[3]))
                : default,
        };
    }

    // WM_HINTS 的 flags(ICCCM §4.1.2.4)。
    private const uint WmHintState = 2, WmHintIconPixmap = 4, WmHintIconMask = 32, WmHintWindowGroup = 64;

    /// <summary>WM_HINTS 的 initial_state 是 IconicState(3):客户端要求一映射就最小化(<c>xterm -iconic</c>)。</summary>
    private static bool StartsIconic(XWindow top)
    {
        uint[] hints = ReadCard32s(top.Properties.GetValueOrDefault(XAtom.WmHints), 3);
        return hints.Length >= 3 && (hints[0] & WmHintState) != 0 && hints[2] == 3;
    }

    /// <summary>
    /// WM_NORMAL_HINTS(ICCCM §4.1.2.3):flags、4 个作废的字段、最小 / 最大尺寸、步长、最小 / 最大宽高比、基准尺寸、win_gravity。
    /// 只认 flags 里给了、而且属性里真有的字段(老程序的 WM_SIZE_HINTS 只有 15 个值,没有基准尺寸与重力);
    /// 数值是 INT32,一律夹进 X 的尺寸范围 —— 原先超过 int.MaxValue 的值强转成负的最小尺寸交给宿主。
    /// </summary>
    private static XTopLevelSnapshot ReadSizeHints(Dictionary<uint, XProperty> props, XTopLevelSnapshot snapshot)
    {
        uint[] size = ReadCard32s(props.GetValueOrDefault(XAtom.WmNormalHints), 18);
        uint flags = size.Length >= 1 ? size[0] : 0;
        bool Has(uint flag, int lastField) => (flags & flag) != 0 && size.Length > lastField;
        (int Width, int Height) Pair(uint flag, int first) => Has(flag, first + 1) ? (HintSize(size[first]), HintSize(size[first + 1])) : (0, 0);
        double Aspect(int first) => Has(128, first + 1) && (int)size[first] > 0 && (int)size[first + 1] > 0
            ? (int)size[first] / (double)(int)size[first + 1]
            : 0;

        (int minWidth, int minHeight) = Pair(16, 5);
        (int baseWidth, int baseHeight) = Pair(256, 15);
        (int maxWidth, int maxHeight) = Pair(32, 7);
        (int widthInc, int heightInc) = Pair(64, 9);
        uint gravity = Has(512, 17) ? size[17] : 1;
        return snapshot with
        {
            // 没给基准尺寸就按最小尺寸,反之亦然(ICCCM §4.1.2.3)。
            MinWidth = Has(16, 6) ? minWidth : baseWidth,
            MinHeight = Has(16, 6) ? minHeight : baseHeight,
            BaseWidth = Has(256, 16) ? baseWidth : minWidth,
            BaseHeight = Has(256, 16) ? baseHeight : minHeight,
            MaxWidth = maxWidth,
            MaxHeight = maxHeight,
            WidthIncrement = widthInc,
            HeightIncrement = heightInc,
            MinAspect = Aspect(11),
            MaxAspect = Aspect(13),
            WinGravity = gravity is >= 1 and <= 10 ? (XGravity)gravity : XGravity.NorthWest,
            UserPosition = (flags & 1) != 0,
            ProgramPosition = (flags & 4) != 0,
        };
    }

    /// <summary>提示里的尺寸(INT32):负数当 0,大于 X 的尺寸上限的夹到 32767。</summary>
    private static int HintSize(uint value) => Math.Clamp((int)value, 0, short.MaxValue);

    /// <summary>_MOTIF_WM_HINTS 的 functions:ALL(1)置位时其余位表示「除了这些」。</summary>
    private static XWindowFunctions MotifFunctions(uint functions)
    {
        XWindowFunctions listed = XWindowFunctions.None;
        foreach ((uint bit, XWindowFunctions function) in (ReadOnlySpan<(uint, XWindowFunctions)>)
                 [(2, XWindowFunctions.Resize), (4, XWindowFunctions.Move), (8, XWindowFunctions.Minimize),
                  (16, XWindowFunctions.Maximize), (32, XWindowFunctions.Close)])
        {
            if ((functions & bit) != 0)
            {
                listed |= function;
            }
        }
        return (functions & 1) != 0 ? XWindowFunctions.All & ~listed : listed;
    }

    /// <summary>图标像素图的边长上限:宿主的任务栏图标用不到更大的,也免得一个巨大的像素图每次换提示都整份转换一遍。</summary>
    private const int MaxIconPixmapSize = 256;

    /// <summary>
    /// WM_HINTS 的 icon_pixmap(深度 1 时按 ICCCM §4.1.2.4 用黑白两色:1 黑、0 白;根窗口深度时是 RGB)与 icon_mask(深度 1,0 处透明)
    /// 烙成一幅图标。没给、像素图不在、太大或掩码尺寸不符时为空列表。
    /// </summary>
    private IReadOnlyList<XWindowIcon> IconFromPixmap(uint[] hints)
    {
        uint flags = hints.Length >= 1 ? hints[0] : 0;
        if ((flags & WmHintIconPixmap) == 0 || hints.Length < 4
            || Lookup<XPixmap>(hints[3]) is not { Buffer: var source }
            || source.Width > MaxIconPixmapSize || source.Height > MaxIconPixmapSize)
        {
            return [];
        }
        PixelBuffer? mask = (flags & WmHintIconMask) != 0 && hints.Length >= 8 && Lookup<XPixmap>(hints[7]) is { Depth: 1, Buffer: var m }
                            && m.Width == source.Width && m.Height == source.Height
            ? m
            : null;
        uint[] pixels = new uint[source.Width * source.Height];
        for (int i = 0; i < pixels.Length; i++)
        {
            if (mask is not null && mask.Pixels[i] == 0)
            {
                continue;   // 透明
            }
            uint p = source.Pixels[i];
            pixels[i] = source.Depth switch
            {
                1 => p != 0 ? 0xFF000000 : 0xFFFFFFFF,
                32 => Unpremultiply(p),
                _ => 0xFF000000 | (p & 0xFFFFFF),
            };
        }
        return [new XWindowIcon(source.Width, source.Height, pixels)];
    }

    /// <summary>预乘的 ARGB → 非预乘(图标是非预乘的)。</summary>
    private static uint Unpremultiply(uint argb)
    {
        uint a = argb >> 24;
        if (a is 0 or 255)
        {
            return a == 0 ? 0 : argb;
        }
        uint Channel(int shift) => Math.Min(255u, ((((argb >> shift) & 0xFF) * 255) + (a / 2)) / a) << shift;
        return (a << 24) | Channel(16) | Channel(8) | Channel(0);
    }

    /// <summary>
    /// _NET_WM_ICON 只解析前这么多个值(4 MB):真实程序的图标合计几百 KB;属性能有 32 MB、可以一次追加几个字节,
    /// 每次变了都要整份重新解析,不设上限就是「追加 4 字节、解析 30 MB」的放大。
    /// </summary>
    private const int MaxIconWords = 1 << 20;

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

    private void CleanupEwmh(XWindow window)
    {
        _frameExtents.Remove(window);
        _clientListOrder.Remove(window);
        if (ReferenceEquals(_activeTopLevel, window))
        {
            _activeTopLevel = null;
        }
    }
}
