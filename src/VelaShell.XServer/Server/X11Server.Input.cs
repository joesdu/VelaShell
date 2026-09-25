// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 第 10 节「Events」的「Input Device events」
//   (KeyPress / KeyRelease / ButtonPress / ButtonRelease / MotionNotify 的字段、state 是事件发生前的状态、
//   向上传播与 do-not-propagate、按钮按下的自动抓取)、「Pointer Window events」(EnterNotify / LeaveNotify 的
//   detail:Ancestor / Virtual / Inferior / Nonlinear / NonlinearVirtual)、「Input Focus events」;
//   「GrabPointer」「UngrabPointer」「GrabButton」「UngrabButton」「ChangeActivePointerGrab」「GrabKeyboard」
//   「UngrabKeyboard」「GrabKey」「UngrabKey」「QueryPointer」「WarpPointer」「SetInputFocus」「GetInputFocus」
//   「GetKeyboardMapping」「ChangeKeyboardMapping」「GetModifierMapping」「SetModifierMapping」
//   「GetKeyboardControl」「GetPointerMapping」;ICCCM §4.2.8(WM_DELETE_WINDOW)与 §4.1.5(合成 ConfigureNotify)
//
//   同步抓取(pointer-mode / keyboard-mode = Synchronous)与 AllowEvents 的冻结、放行、重放见 X11Server.SyncGrabs.cs。

using VelaShell.XServer.Host;
using VelaShell.XServer.Input;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer.Server;

public sealed partial class X11Server
{
    private XWindow _pointerWindow;

    /// <summary>PointerMotionHint 的轮次:按键 / 按钮变化、指针换窗口时推进(见 <see cref="XClient.MotionHint" />)。</summary>
    private uint _motionHintEpoch;
    private int _pointerX;
    private int _pointerY;
    private ushort _buttons;
    /// <summary>生效的修饰键 = 按着的(base)| 锁存的(latched)| 锁定的(locked)。</summary>
    private ushort _modifiers;
    private byte _baseMods;
    private byte _latchedMods;
    private byte _lockedMods;
    private readonly byte[] _keysDown = new byte[32];

    /// <summary>按着的按钮(1–255;XI2 的 buttons 掩码要全部按钮,核心 state 只有 1–5)。</summary>
    private readonly byte[] _buttonsDown = new byte[32];

    /// <summary>键盘焦点:null = None;<see cref="Root" /> = PointerRoot;其余为具体窗口。</summary>
    private XWindow? _focus;
    private byte _focusRevertTo;
    private int _cursorGlyph = int.MinValue;

    private ushort State => (ushort)(_modifiers | _buttons);

    // ================================================================== 宿主注入(任意线程)

    /// <summary>指针在顶层窗口里移动(内区坐标)。</summary>
    public void PointerMotion(uint topLevel, int x, int y) => Post(null, () =>
    {
        NoteUserActivity();
        if (Lookup<XWindow>(topLevel) is { IsTopLevel: true } top)
        {
            // 根坐标在注入的那一刻算好:指针冻着时事件排队,之后窗口可能挪了。
            int rootX = top.X + top.BorderWidth + x, rootY = top.Y + top.BorderWidth + y;
            ProcessPointerInput(() => MovePointer(rootX, rootY));
        }
    });

    /// <summary>
    /// 按钮按下 / 松开。1 左、2 中、3 右;滚轮向上 4、向下 5、向左 6、向右 7(宿主应当为每格滚动注入一次按下 + 松开);
    /// 8、9 是后退 / 前进侧键。
    /// </summary>
    public void PointerButton(uint topLevel, int x, int y, int button, bool pressed) => Post(null, () =>
    {
        NoteUserActivity();
        if (Lookup<XWindow>(topLevel) is not { IsTopLevel: true } top || button is < 1 or > 255)
        {
            return;
        }
        int rootX = top.X + top.BorderWidth + x, rootY = top.Y + top.BorderWidth + y;
        ProcessPointerInput(() =>
        {
            MovePointer(rootX, rootY);
            ButtonEvent(button, pressed);
        });
    });

    /// <summary>指针离开了所有顶层窗口(移到了宿主的其他窗口或桌面上)。</summary>
    public void PointerLeft() => Post(null, () => ProcessPointerInput(() => MovePointer(-1, -1)));

    /// <summary>按键按下 / 松开(X 键码,见 <see cref="XKeycodes" />)。</summary>
    public void Key(byte keycode, bool pressed) => Post(null, () =>
    {
        NoteUserActivity();
        ProcessKeyboardInput(() => KeyEvent(keycode, pressed));
    });

    /// <summary>
    /// 换键位表(宿主的键盘布局不是 US 时):从 <paramref name="firstKeycode" /> 起,每个键码 <paramref name="keysymsPerKeycode" /> 个键值
    /// (第 1 列无修饰、第 2 列 Shift,与核心协议 ChangeKeyboardMapping 相同)。XKB 描述随之重新推出,
    /// 客户端收到 MappingNotify 与 XKB 的 MapNotify。<paramref name="layout" /> 是布局名(如 <c>de</c>),给 setxkbmap 之类看。
    /// </summary>
    public void SetKeyboardMapping(byte firstKeycode, int keysymsPerKeycode, ReadOnlySpan<uint> keysyms, string? layout = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(firstKeycode, Keymap.MinKeycode);
        ArgumentOutOfRangeException.ThrowIfLessThan(keysymsPerKeycode, 1);
        if (keysyms.Length % keysymsPerKeycode != 0 || firstKeycode + (keysyms.Length / keysymsPerKeycode) - 1 > Keymap.MaxKeycode)
        {
            throw new ArgumentException("键值个数必须是每键码键值数的整数倍,且不超过键码 255。", nameof(keysyms));
        }
        uint[] copy = keysyms.ToArray();
        Post(null, () =>
        {
            _keymap.Change(firstKeycode, keysymsPerKeycode, copy);
            if (layout is not null)
            {
                _keyboardLayout = layout;
                InitXkbRulesNames();
            }
            byte count = (byte)(copy.Length / keysymsPerKeycode);
            foreach (XClient client in _clients.Values)
            {
                client.Event(XEventCode.MappingNotify, 0, w => w.U8(1).U8(firstKeycode).U8(count));
            }
            NotifyXkbMapChanged();
        });
    }

    /// <summary>
    /// 换修饰键表(与核心协议 SetModifierMapping 相同的布局:8 个修饰位,每位 <c>map.Length / 8</c> 个键码,0 为空位)。
    /// 宿主的键盘布局有 AltGr 层时用它把右 Alt(<see cref="Host.XKeycodes.AltRight" />,键值 ISO_Level3_Shift)从 Mod1 挪到 Mod5。
    /// 客户端收到 MappingNotify(Modifier)与 XKB 的 MapNotify。
    /// </summary>
    public void SetModifierMapping(ReadOnlySpan<byte> map)
    {
        if (map.Length == 0 || map.Length % 8 != 0 || map.Length > 8 * 255)
        {
            throw new ArgumentException("修饰键表是 8 个修饰位 × 每位若干键码。", nameof(map));
        }
        byte[] copy = map.ToArray();
        Post(null, () =>
        {
            _keymap.SetModifierMap(copy);
            NotifyXkbMapChanged();
            foreach (XClient client in _clients.Values)
            {
                client.Event(XEventCode.MappingNotify, 0, w => w.U8(0).U8(0).U8(0));
            }
        });
    }

    /// <summary>宿主让某个顶层窗口得到键盘焦点(用户点了它);0 = 所有顶层都失去焦点。</summary>
    public void FocusTopLevel(uint topLevel) => Post(null, () =>
    {
        if (topLevel == 0)
        {
            SetFocus(null, 0);
            return;
        }
        if (Lookup<XWindow>(topLevel) is { IsTopLevel: true, IsViewable: true } top
            && (_focus is null || ReferenceEquals(_focus, Root) || !ReferenceEquals(_focus.TopLevel, top)))
        {
            // 与窗口管理器的做法一致:把焦点给顶层,revert-to PointerRoot。客户端之后可以自己把焦点挪到子窗口。
            SetFocus(top, 1);
        }
    });

    /// <summary>用户移动了原生窗口:改位置,并按 ICCCM 发一条合成的 ConfigureNotify(根坐标)。</summary>
    public void MoveTopLevel(uint topLevel, int x, int y) => Post(null, () =>
    {
        if (Lookup<XWindow>(topLevel) is not { IsTopLevel: true } top || (top.X == x && top.Y == y))
        {
            return;
        }
        top.X = x;
        top.Y = y;
        if (_topLevelHandles.TryGetValue(top, out XTopLevelWindow? handle))
        {
            handle.X = x;
            handle.Y = y;
        }
        DeliverToSelectors(top, XEventMask.StructureNotify, c => c.Event(XEventCode.ConfigureNotify, 0, w => w
            .U32(top.Id).U32(top.Id).U32(0).I16(x).I16(y).U16((ushort)top.Width).U16((ushort)top.Height)
            .U16((ushort)top.BorderWidth).Bool(top.OverrideRedirect), sent: true));
    });

    /// <summary>用户缩放了原生窗口:改尺寸(客户端收到 ConfigureNotify 与 Expose,重画)。</summary>
    public void ResizeTopLevel(uint topLevel, int width, int height) => Post(null, () =>
    {
        if (Lookup<XWindow>(topLevel) is { IsTopLevel: true } top && width > 0 && height > 0
            && (top.Width != width || top.Height != height))
        {
            ConfigureWindow(top, top.X, top.Y, width, height, top.BorderWidth, null, -1);
        }
    });

    /// <summary>
    /// 用户点了原生窗口的关闭按钮:客户端声明了 WM_DELETE_WINDOW 就礼貌地请它自己关(ICCCM §4.2.8),
    /// 否则断开该客户端(与窗口管理器的 XKillClient 一致)。
    /// </summary>
    public void CloseTopLevel(uint topLevel) => Post(null, () =>
    {
        if (Lookup<XWindow>(topLevel) is not { IsTopLevel: true, Owner: { } owner } top)
        {
            return;
        }
        uint protocols = Intern("WM_PROTOCOLS");
        uint delete = Intern("WM_DELETE_WINDOW");
        if (_topLevelHandles.TryGetValue(top, out XTopLevelWindow? handle))
        {
            RefreshHandle(top, handle);
        }
        if (handle is { SupportsDeleteWindow: true })
        {
            uint time = Now;
            owner.Event(XEventCode.ClientMessage, 32, w => w.U32(top.Id).U32(protocols).U32(delete).U32(time).Zero(12), sent: true);
            return;
        }
        owner.Abort();
        DisconnectClient(owner);
    });

    // ================================================================== 指针

    private XWindow WindowAt(int rootX, int rootY)
    {
        XWindow current = Root;
        (int ix, int iy) = Root.AbsoluteInner();   // 往下走时逐层累加,不每层从头回溯到根
        while (true)
        {
            XWindow? hit = null;
            for (int i = current.Children.Count - 1; i >= 0; i--)
            {
                XWindow child = current.Children[i];
                if (!child.Mapped)
                {
                    continue;
                }
                int x = ix + child.X, y = iy + child.Y;
                int w = child.Width + (2 * child.BorderWidth), h = child.Height + (2 * child.BorderWidth);
                if (rootX >= x && rootY >= y && rootX < x + w && rootY < y + h
                    && (child.InputShape ?? child.BoundingShape) is var shape
                    && (shape is null || shape.Contains(rootX - x - child.BorderWidth, rootY - y - child.BorderWidth)))
                {
                    hit = child;
                    break;
                }
            }
            if (hit is null)
            {
                return current;
            }
            ix += hit.X + hit.BorderWidth;
            iy += hit.Y + hit.BorderWidth;
            current = hit;
        }
    }

    private void MovePointer(int rootX, int rootY)
    {
        bool moved = rootX != _pointerX || rootY != _pointerY;
        if (moved && rootX >= 0 && _pointerX >= 0)
        {
            SendRawEvent(XiRawMotion, 0, rootX - _pointerX, rootY - _pointerY);
        }
        _pointerX = rootX;
        _pointerY = rootY;
        UpdatePointerWindow();
        if (moved && rootX >= 0)
        {
            XEventMask mask = XEventMask.PointerMotion;   // 只选了 PointerMotionHint 的不算选了移动事件
            if (_buttons != 0)
            {
                mask |= XEventMask.ButtonMotion;
                for (int b = 0; b < 5; b++)
                {
                    if ((_buttons & (0x100 << b)) != 0)
                    {
                        mask |= (XEventMask)(0x100 << b);
                    }
                }
            }
            DeliverDeviceEvent(XEventCode.MotionNotify, 0, mask, _pointerWindow);
        }
    }

    /// <summary>
    /// 按钮按下的投递:被动抓取的激活(<paramref name="ignoreGrabsThrough" /> 及其以上的不看 —— ReplayPointer 用)、投递、
    /// 自动抓取、同步模式的冻结。<paramref name="replay" /> 时是重放:按钮状态与原始事件已经在第一次处理时记过了。
    /// </summary>
    private void PressButton(int button, XWindow? ignoreGrabsThrough, bool replay)
    {
        PassiveGrab? activated = null;
        if (PointerGrab is null && FindPassiveGrab(_pointerWindow, isButton: true, button, ignoreGrabsThrough) is { } passive)
        {
            activated = passive.Grab;
            PointerGrab = new ActiveGrab
            {
                Client = passive.Grab.Client,
                Window = passive.Window,
                OwnerEvents = passive.Grab.OwnerEvents,
                EventMask = passive.Grab.EventMask,
                Cursor = passive.Grab.Cursor,
                ReleaseWhenButtonsUp = true,
                Xi2 = passive.Grab.Xi2,
                Xi2Mask = passive.Grab.Xi2Mask,
            };
        }
        if (!replay)
        {
            _buttonsDown[button >> 3] |= (byte)(1 << (button & 7));
            SendRawEvent(XiRawButtonPress, (uint)button, 0, 0);
        }
        Delivery? delivered = DeliverDeviceEvent(XEventCode.ButtonPress, (byte)button, XEventMask.ButtonPress, _pointerWindow);
        if (PointerGrab is null && delivered is { } d)
        {
            // 自动抓取:按下的那个窗口在所有按钮松开之前独占指针事件(协议「ButtonPress」;XI2 同理,格式跟着收到的那种走)。
            PointerGrab = new ActiveGrab
            {
                Client = d.Client,
                Window = d.Window,
                OwnerEvents = (d.Mask & (uint)XEventMask.OwnerGrabButton) != 0,
                EventMask = d.Mask,
                ReleaseWhenButtonsUp = true,
                Xi2 = d.Xi2,
                Xi2Mask = d.Xi2Mask,
            };
        }
        if (activated is not null && PointerGrab is { } grab)
        {
            // 被动抓取以同步模式激活:事件已经交出去,设备此刻冻结;这一次按下可被 ReplayPointer 重放。
            ApplyGrabModes(grab, activated.PointerSync, activated.KeyboardSync);
            if (activated.PointerSync)
            {
                _pointerReplay = (button, grab.Window);
            }
        }
        else if (delivered is not null && PointerGrab is not null)
        {
            NotePointerEventReported(button, pressed: true);
        }
    }

    /// <summary>重新算指针所在窗口;变了就发 Enter / Leave、更新光标。窗口树变化后也调它。</summary>
    internal void UpdatePointerWindow()
    {
        XWindow now = _pointerX < 0 ? Root : WindowAt(_pointerX, _pointerY);
        if (!ReferenceEquals(now, _pointerWindow))
        {
            XWindow old = _pointerWindow;
            _pointerWindow = now;
            _motionHintEpoch++;
            GenerateCrossing(old, now);
        }
        UpdateCursor();
    }

    private void ButtonEvent(int button, bool pressed)
    {
        _motionHintEpoch++;   // 按钮状态变了:PointerMotionHint 的客户端可以再收一条提示
        // 只有按钮 1–5 在 state 里有位(协议 SETofKEYBUTMASK);6 以上(水平滚轮等)照样投递,但不进 state。
        ushort bit = button <= 5 ? (ushort)(0x100 << (button - 1)) : (ushort)0;
        if (pressed)
        {
            PressButton(button, ignoreGrabsThrough: null, replay: false);
            _buttons |= bit;
        }
        else
        {
            SendRawEvent(XiRawButtonRelease, (uint)button, 0, 0);
            Delivery? released = DeliverDeviceEvent(XEventCode.ButtonRelease, (byte)button, XEventMask.ButtonRelease, _pointerWindow);
            if (released is not null && PointerGrab is not null)
            {
                NotePointerEventReported(button, pressed: false);
            }
            _buttonsDown[button >> 3] &= (byte)~(1 << (button & 7));
            _buttons &= (ushort)~bit;
            if (_buttons == 0 && PointerGrab is { ReleaseWhenButtonsUp: true })
            {
                PointerGrab = null;
                UpdateCursor();
            }
        }
    }

    /// <summary>一次投递的落点:收到事件的窗口、客户端、核心掩码;经 XI2 收到时 <see cref="Xi2" /> 为真。</summary>
    private readonly record struct Delivery(XWindow Window, XClient Client, uint Mask, bool Xi2, ulong Xi2Mask, bool Xi2Slave);

    /// <summary>
    /// 投递一个设备事件:有主动抓取按抓取规则走,否则从源窗口向上传播到第一个有人选了它的窗口
    /// (核心事件掩码或 XI2 事件掩码都算)。在那个窗口上,选了核心事件的收核心事件,选了 XI2 的收 XI2 事件。
    /// XI2 的 evtype 与核心事件码相同(KeyPress 2 … Motion 6)。返回第一个收到事件的落点,没人收时为 null。
    /// </summary>
    private Delivery? DeliverDeviceEvent(byte code, byte detail, XEventMask mask, XWindow source)
    {
        bool isKey = code is XEventCode.KeyPress or XEventCode.KeyRelease;
        if (IsFloating(!isKey))
        {
            return DeliverFloating(code, detail, source);
        }
        ActiveGrab? grab = isKey ? KeyboardGrab : PointerGrab;
        XWindow? stopAt = null;
        if (isKey && grab is null && _focus is { } focus && !ReferenceEquals(focus, Root))
        {
            stopAt = focus;
        }

        if (grab is not null)
        {
            if (grab.OwnerEvents && Propagate(source, mask, code, grab.Client, stopAt) is { } own)
            {
                Send(own);
                return own;
            }
            if (grab.Xi2)
            {
                if ((grab.Xi2Mask & (1UL << code)) == 0)
                {
                    return null;
                }
                Delivery xi = new(grab.Window, grab.Client, 0, true, grab.Xi2Mask, false);
                Send(xi);
                return xi;
            }
            if (isKey || (grab.EventMask & (uint)mask) != 0)
            {
                SendDeviceEvent(grab.Client, code, detail, grab.Window, source, grab.EventMask);
                return new Delivery(grab.Window, grab.Client, grab.EventMask, false, 0, false);
            }
            return null;
        }

        return Propagate(source, mask, code, null, stopAt) is { } hit ? SendToAll(hit.Window) : null;

        void Send(Delivery d)
        {
            if (d.Xi2)
            {
                SendXi2DeviceEvent(d.Client, code, detail, d.Window, source, d.Xi2Slave);
            }
            else
            {
                SendDeviceEvent(d.Client, code, detail, d.Window, source, d.Mask);
            }
        }

        Delivery? SendToAll(XWindow window)
        {
            Delivery? first = null;
            foreach ((XClient client, uint selected) in window.EventSelections)
            {
                if ((selected & (uint)mask) != 0 && !client.Closed)
                {
                    SendDeviceEvent(client, code, detail, window, source, selected);
                    first ??= new Delivery(window, client, selected, false, 0, false);
                }
            }
            foreach ((XClient client, (ulong master, ulong slave)) in window.Xi2Selections)
            {
                if (((master | slave) & (1UL << code)) != 0 && !client.Closed)
                {
                    bool slaveOnly = (master & (1UL << code)) == 0;
                    SendXi2DeviceEvent(client, code, detail, window, source, slaveOnly);
                    first ??= new Delivery(window, client, 0, true, master | slave, slaveOnly);
                }
            }
            return first;
        }
    }

    /// <summary>
    /// 物理从设备浮动了(XIChangeHierarchy DetachSlave):不产生核心事件、不受主设备的抓取影响,
    /// 只以从设备的身份报 XI2 事件 —— 从源窗口向上找第一个为从设备(或 XIAllDevices)选了它的窗口。
    /// </summary>
    private Delivery? DeliverFloating(byte code, byte detail, XWindow source)
    {
        for (XWindow? w = source; w is not null; w = w.Parent)
        {
            Delivery? first = null;
            foreach ((XClient client, (_, ulong slave)) in w.Xi2Selections)
            {
                if ((slave & (1UL << code)) != 0 && !client.Closed)
                {
                    SendXi2DeviceEvent(client, code, detail, w, source, slave: true);
                    first ??= new Delivery(w, client, 0, true, slave, true);
                }
            }
            if (first is not null)
            {
                return first;
            }
        }
        return null;
    }

    /// <summary>
    /// 从源窗口向上找第一个(指定客户端)选了这类事件的窗口 —— 核心掩码或 XI2 的 <paramref name="evtype" /> 都算;
    /// 碰上 do-not-propagate 或 <paramref name="stopAt" /> 就停。
    /// </summary>
    private static Delivery? Propagate(XWindow source, XEventMask mask, int evtype, XClient? only, XWindow? stopAt)
    {
        for (XWindow? w = source; w is not null; w = w.Parent)
        {
            foreach ((XClient client, uint selected) in w.EventSelections)
            {
                if ((selected & (uint)mask) != 0 && !client.Closed && (only is null || ReferenceEquals(client, only)))
                {
                    return new Delivery(w, client, selected, false, 0, false);
                }
            }
            foreach ((XClient client, (ulong master, ulong slave)) in w.Xi2Selections)
            {
                if (((master | slave) & (1UL << evtype)) != 0 && !client.Closed && (only is null || ReferenceEquals(client, only)))
                {
                    return new Delivery(w, client, 0, true, master | slave, (master & (1UL << evtype)) == 0);
                }
            }
            if ((w.DoNotPropagateMask & (uint)mask) != 0 || ReferenceEquals(w, stopAt))
            {
                return null;
            }
        }
        return null;
    }

    private void SendDeviceEvent(XClient client, byte code, byte detail, XWindow eventWindow, XWindow source, uint clientMask)
    {
        (int ex, int ey) = eventWindow.AbsoluteInner();
        // child:事件窗口的、位于通往源窗口路径上的那个子窗口;源就是事件窗口时为 None。
        uint child = 0;
        for (XWindow? w = source; w is not null && !ReferenceEquals(w, eventWindow); w = w.Parent)
        {
            if (ReferenceEquals(w.Parent, eventWindow))
            {
                child = w.Id;
                break;
            }
        }
        if (code == XEventCode.MotionNotify && (clientMask & (uint)XEventMask.PointerMotionHint) != 0)
        {
            // 协议「MotionNotify」:选了 PointerMotionHint 的客户端,在按键 / 按钮状态变化、指针离开事件窗口、
            // 或者它发 QueryPointer / GetMotionEvents 之前,同一个事件窗口只收一条(detail = Hint)。
            if (ReferenceEquals(client.MotionHint.Window, eventWindow) && client.MotionHint.Epoch == _motionHintEpoch)
            {
                return;
            }
            client.MotionHint = (eventWindow, _motionHintEpoch);
            detail = 1;   // Hint
        }
        uint time = Now;
        ushort state = State;
        int px = _pointerX, py = _pointerY;
        client.Event(code, detail, w => w
            .U32(time).U32(Root.Id).U32(eventWindow.Id).U32(child)
            .I16(px).I16(py).I16(px - ex).I16(py - ey).U16(state).Bool(true));
    }

    /// <summary>
    /// Enter / Leave(协议「Pointer Window events」的五种 detail)。只发给选了 EnterWindow / LeaveWindow 的客户端,不传播。
    /// </summary>
    private void GenerateCrossing(XWindow from, XWindow to)
    {
        const byte ancestor = 0, @virtual = 1, inferior = 2, nonlinear = 3, nonlinearVirtual = 4;
        // Leave 的 child 指向指针原先所在的那一支,Enter 的指向指针现在所在的那一支(协议「EnterNotify / LeaveNotify」)。
        if (to.IsDescendantOf(from))
        {
            Crossing(XEventCode.LeaveNotify, from, inferior, to);
            foreach (XWindow w in PathBetween(to, from))
            {
                Crossing(XEventCode.EnterNotify, w, @virtual, to);
            }
            Crossing(XEventCode.EnterNotify, to, ancestor, to);
        }
        else if (from.IsDescendantOf(to))
        {
            Crossing(XEventCode.LeaveNotify, from, ancestor, from);
            foreach (XWindow w in Enumerable.Reverse(PathBetween(from, to)))
            {
                Crossing(XEventCode.LeaveNotify, w, @virtual, from);
            }
            Crossing(XEventCode.EnterNotify, to, inferior, from);
        }
        else
        {
            XWindow common = CommonAncestor(from, to);
            Crossing(XEventCode.LeaveNotify, from, nonlinear, from);
            foreach (XWindow w in Enumerable.Reverse(PathBetween(from, common)))
            {
                Crossing(XEventCode.LeaveNotify, w, nonlinearVirtual, from);
            }
            foreach (XWindow w in PathBetween(to, common))
            {
                Crossing(XEventCode.EnterNotify, w, nonlinearVirtual, to);
            }
            Crossing(XEventCode.EnterNotify, to, nonlinear, to);
        }
    }

    /// <summary><paramref name="window" /> 的哪个子窗口包含 <paramref name="pointerWindow" />(自己或后代);都不是时为 0(None)。</summary>
    private static uint ChildToward(XWindow window, XWindow pointerWindow)
    {
        for (XWindow? w = pointerWindow; w?.Parent is { } parent; w = parent)
        {
            if (ReferenceEquals(parent, window))
            {
                return w.Id;
            }
        }
        return 0;
    }

    /// <summary><paramref name="descendant" /> 与 <paramref name="ancestor" /> 之间的窗口(都不含),从上到下。</summary>
    private static List<XWindow> PathBetween(XWindow descendant, XWindow ancestor)
    {
        List<XWindow> path = [];
        for (XWindow? w = descendant.Parent; w is not null && !ReferenceEquals(w, ancestor); w = w.Parent)
        {
            path.Add(w);
        }
        path.Reverse();
        return path;
    }

    private static XWindow CommonAncestor(XWindow a, XWindow b)
    {
        HashSet<XWindow> chain = [];
        for (XWindow? w = a; w is not null; w = w.Parent)
        {
            chain.Add(w);
        }
        for (XWindow? w = b; w is not null; w = w.Parent)
        {
            if (chain.Contains(w))
            {
                return w;
            }
        }
        return a;
    }

    private void Crossing(byte code, XWindow window, byte detail, XWindow pointerWindow)
    {
        XEventMask mask = code == XEventCode.EnterNotify ? XEventMask.EnterWindow : XEventMask.LeaveWindow;
        bool xi2 = window.AnyXi2Selects(code == XEventCode.EnterNotify ? XiEnter : XiLeave);
        if (!xi2 && !window.AnySelects(mask))
        {
            return;
        }
        uint child = ChildToward(window, pointerWindow);
        if (xi2)
        {
            SendXi2Crossing(code == XEventCode.EnterNotify ? XiEnter : XiLeave, window, detail, child: child);
        }
        if (!window.AnySelects(mask))
        {
            return;
        }
        (int ex, int ey) = window.AbsoluteInner();
        uint time = Now;
        ushort state = State;
        int px = _pointerX, py = _pointerY;
        bool focus = _focus is { } f && (ReferenceEquals(f, window) || window.IsDescendantOf(f));
        DeliverToSelectors(window, mask, c => c.Event(code, detail, w => w
            .U32(time).U32(Root.Id).U32(window.Id).U32(child)
            .I16(px).I16(py).I16(px - ex).I16(py - ey).U16(state)
            .U8(0)                                          // mode:Normal
            .U8((byte)(0x02 | (focus ? 0x01 : 0)))));       // same-screen | focus
    }

    /// <summary>光标:抓取的光标优先,否则从指针所在窗口向上找第一个设了光标的窗口。</summary>
    private void UpdateCursor()
    {
        XCursor? cursor = CurrentCursor();
        int glyph = CursorHiddenAt(_pointerWindow) ? -2 : cursor?.Glyph ?? -1;
        if (glyph == _cursorGlyph)
        {
            return;
        }
        _cursorGlyph = glyph;
        NotifyCursorChange();
        XTopLevelWindow? handle = _pointerWindow.TopLevel is { } top && _topLevelHandles.TryGetValue(top, out XTopLevelWindow? h) ? h : null;
        _host.CursorChanged(handle, glyph);
    }

    // ================================================================== 键盘

    private void KeyEvent(byte keycode, bool pressed)
    {
        _motionHintEpoch++;   // 按键状态变了:同上
        if (keycode < Keymap.MinKeycode)
        {
            return;
        }

        bool wasDown = (_keysDown[keycode >> 3] & (1 << (keycode & 7))) != 0;
        if (pressed)
        {
            _keysDown[keycode >> 3] |= (byte)(1 << (keycode & 7));
        }
        else
        {
            _keysDown[keycode >> 3] &= (byte)~(1 << (keycode & 7));
        }

        SendRawEvent(pressed ? XiRawKeyPress : XiRawKeyRelease, keycode, 0, 0);
        // 事件里的 state 是事件发生前的:修饰键状态在投递之后才更新。
        if (pressed)
        {
            PressKey(keycode, ignoreGrabsThrough: null, replay: false);
        }
        else if (KeyboardSource() is { } source)
        {
            Delivery? released = DeliverDeviceEvent(XEventCode.KeyRelease, keycode, XEventMask.KeyRelease, source);
            if (released is not null && KeyboardGrab is not null)
            {
                NoteKeyboardEventReported(keycode, pressed: false);
            }
        }

        // 更新修饰键状态:Lock 类(Caps_Lock、Num_Lock)按下翻转锁定位;其余由「当前按着的键」重新算出,
        // 两个 Shift 同时按着、松开一个时 Shift 仍然生效。
        ushort modBit = _keymap.ModifierBitOf(keycode);
        if (modBit != 0)
        {
            if (_keymap.IsLockingKey(keycode) && pressed && !wasDown)
            {
                _lockedMods ^= (byte)modBit;
            }
            UpdateModifierState(keycode, pressed ? XEventCode.KeyPress : XEventCode.KeyRelease);
        }

        if (!pressed && KeyboardGrab is { ReleaseWhenButtonsUp: true } && _passiveKeyGrabKey == keycode)
        {
            KeyboardGrab = null;
            _passiveKeyGrabKey = 0;
        }
    }

    private byte _passiveKeyGrabKey;

    /// <summary>按键按下的投递:被动抓取的激活(<paramref name="ignoreGrabsThrough" /> 及其以上的不看)、投递、同步模式的冻结。</summary>
    private void PressKey(byte keycode, XWindow? ignoreGrabsThrough, bool replay)
    {
        XWindow? source = KeyboardSource();
        if (source is null)
        {
            return;
        }
        PassiveGrab? activated = null;
        if (KeyboardGrab is null && FindPassiveGrab(source, isButton: false, keycode, ignoreGrabsThrough) is { } passive)
        {
            activated = passive.Grab;
            KeyboardGrab = new ActiveGrab
            {
                Client = passive.Grab.Client,
                Window = passive.Window,
                OwnerEvents = passive.Grab.OwnerEvents,
                ReleaseWhenButtonsUp = true,   // 对键盘:这个键松开时解除
                Xi2 = passive.Grab.Xi2,
                Xi2Mask = passive.Grab.Xi2Mask,
            };
            _passiveKeyGrabKey = keycode;
        }
        Delivery? delivered = DeliverDeviceEvent(XEventCode.KeyPress, keycode, XEventMask.KeyPress, source);
        if (activated is not null && KeyboardGrab is { } grab)
        {
            ApplyGrabModes(grab, activated.PointerSync, activated.KeyboardSync);
            if (activated.KeyboardSync)
            {
                _keyboardReplay = (keycode, grab.Window);
            }
        }
        else if (!replay && delivered is not null && KeyboardGrab is not null)
        {
            NoteKeyboardEventReported(keycode, pressed: true);
        }
    }

    /// <summary>从按着的修饰键重新算 base,合成生效状态;变了就通知(XKB 的 StateNotify)。</summary>
    private void UpdateModifierState(byte keycode, byte eventType, byte requestMajor = 0, byte requestMinor = 0)
    {
        byte oldBase = _baseMods, oldLatched = _latchedMods, oldLocked = _lockedMods;
        ushort oldEffective = _modifiers;
        byte held = 0;
        for (int code = Keymap.MinKeycode; code <= Keymap.MaxKeycode; code++)
        {
            if ((_keysDown[code >> 3] & (1 << (code & 7))) != 0 && !_keymap.IsLockingKey((byte)code))
            {
                held |= (byte)_keymap.ModifierBitOf((byte)code);
            }
        }
        _baseMods = held;
        _modifiers = (ushort)(_baseMods | _latchedMods | _lockedMods);
        ushort changed = 0;
        changed |= (ushort)(_modifiers != oldEffective ? 1 : 0);
        changed |= (ushort)(_baseMods != oldBase ? 2 : 0);
        changed |= (ushort)(_latchedMods != oldLatched ? 4 : 0);
        changed |= (ushort)(_lockedMods != oldLocked ? 8 : 0);
        if (changed != 0)
        {
            NotifyXkbState(changed, keycode, eventType, requestMajor, requestMinor);
        }
    }

    /// <summary>按键事件的源窗口:焦点是 PointerRoot 时是指针所在窗口;指针在焦点窗口里面时是指针所在窗口;否则是焦点窗口。</summary>
    private XWindow? KeyboardSource()
    {
        if (KeyboardGrab is not null)
        {
            return _pointerWindow.IsViewable ? _pointerWindow : KeyboardGrab.Window;
        }
        return _focus switch
        {
            null => null,
            { } f when ReferenceEquals(f, Root) => _pointerWindow,
            { } f => ReferenceEquals(_pointerWindow, f) || _pointerWindow.IsDescendantOf(f) ? _pointerWindow : f,
        };
    }

    /// <summary>被动抓取:从根往下到源窗口,第一个匹配的生效(协议「GrabButton」「GrabKey」)。</summary>
    private (XWindow Window, PassiveGrab Grab)? FindPassiveGrab(XWindow source, bool isButton, int detail, XWindow? ignoreThrough = null)
    {
        // 从根往下找(协议:离根最近的那个被动抓取生效);递归回溯父链,不为每次按键分配链表。
        // ignoreThrough:它及其祖先上的被动抓取不看(Replay 重放时「不看抓取窗口及其以上」)。
        ushort mods = (ushort)(_modifiers & 0xFF);
        return Find(source);

        (XWindow Window, PassiveGrab Grab)? Find(XWindow w)
        {
            if (w.Parent is { } parent && Find(parent) is { } outer)
            {
                return outer;
            }
            if (ignoreThrough is not null && (ReferenceEquals(w, ignoreThrough) || ignoreThrough.IsDescendantOf(w)))
            {
                return null;
            }
            foreach (PassiveGrab grab in isButton ? w.ButtonGrabs : w.KeyGrabs)
            {
                if (grab.Matches(detail, mods) && !grab.Client.Closed)
                {
                    return (w, grab);
                }
            }
            return null;
        }
    }

    // ================================================================== 焦点

    private void SetFocus(XWindow? focus, byte revertTo)
    {
        XWindow? old = _focus;
        _focusRevertTo = revertTo;
        if (ReferenceEquals(old, focus))
        {
            return;
        }
        _focus = focus;
        UpdateActiveWindow(old, focus);
        const byte nonlinear = 3;
        if (old is { IsRoot: false })
        {
            DeliverToSelectors(old, XEventMask.FocusChange, c => c.Event(XEventCode.FocusOut, nonlinear, w => w.U32(old.Id).U8(0)));
            SendXi2Crossing(XiFocusOut, old, nonlinear);
        }
        if (focus is { IsRoot: false })
        {
            DeliverToSelectors(focus, XEventMask.FocusChange, c => c.Event(XEventCode.FocusIn, nonlinear, w => w.U32(focus.Id).U8(0)));
            SendXi2Crossing(XiFocusIn, focus, nonlinear);
        }
    }

    /// <summary>焦点窗口不可见了:按 revert-to 退回(None / PointerRoot / 最近的可见祖先)。</summary>
    private void RevertFocus(XWindow lost)
    {
        switch (_focusRevertTo)
        {
            case 1:
                SetFocus(Root, 1);
                break;
            case 2:
                XWindow? w = lost.Parent;
                while (w is not null && !w.IsViewable)
                {
                    w = w.Parent;
                }
                SetFocus(w is null || w.IsRoot ? null : w, 0);
                break;
            default:
                SetFocus(null, 0);
                break;
        }
    }

    private void SetInputFocusRequest(XRequestReader r)
    {
        byte revertTo = r.Data;
        uint id = r.U32();
        XWindow? focus = id switch
        {
            0 => null,
            1 => Root,
            _ => Window(id),
        };
        if (focus is { IsRoot: false, IsViewable: false })
        {
            throw new XProtocolError(XErrorCode.Match);
        }
        SetFocus(focus, revertTo);
    }

    private void GetInputFocus(XClient c)
    {
        uint id = _focus switch
        {
            null => 0,
            { } f when ReferenceEquals(f, Root) => 1,
            { } f => f.Id,
        };
        c.Reply(_focusRevertTo, w => w.U32(id).Zero(20));
    }

    // ================================================================== 抓取请求

    private void GrabPointer(XClient c, XRequestReader r)
    {
        bool ownerEvents = r.Data != 0;
        XWindow window = Window(r.U32());
        ushort mask = r.U16();
        (bool pointerSync, bool keyboardSync) = ReadGrabModes(r);
        uint confine = r.U32();
        uint cursorId = r.U32();
        r.U32();
        byte status;
        if (PointerGrab is { } existing && !ReferenceEquals(existing.Client, c))
        {
            status = 1;   // AlreadyGrabbed
        }
        else if (!window.IsViewable)
        {
            status = 3;   // GrabNotViewable
        }
        else
        {
            PointerGrab = new ActiveGrab
            {
                Client = c,
                Window = window,
                OwnerEvents = ownerEvents,
                EventMask = mask,
                Cursor = cursorId == 0 ? null : Lookup<XCursor>(cursorId),
            };
            _ = confine;
            ApplyGrabModes(PointerGrab, pointerSync, keyboardSync);
            status = 0;
            UpdateCursor();
        }
        c.Reply(status, w => w.Zero(24));
    }

    private void UngrabPointer(XClient c)
    {
        if (PointerGrab is { } grab && ReferenceEquals(grab.Client, c))
        {
            PointerGrab = null;
            UpdateCursor();
        }
    }

    private void ChangeActivePointerGrab(XClient c, XRequestReader r)
    {
        uint cursorId = r.U32();
        r.U32();
        ushort mask = r.U16();
        if (PointerGrab is { } grab && ReferenceEquals(grab.Client, c))
        {
            grab.EventMask = mask;
            grab.Cursor = cursorId == 0 ? null : Lookup<XCursor>(cursorId);
            UpdateCursor();
        }
    }

    private void GrabButton(XClient c, XRequestReader r)
    {
        bool ownerEvents = r.Data != 0;
        XWindow window = Window(r.U32());
        ushort mask = r.U16();
        (bool pointerSync, bool keyboardSync) = ReadGrabModes(r);
        uint confine = r.U32();
        uint cursorId = r.U32();
        byte button = r.U8();
        r.U8();
        ushort modifiers = r.U16();
        foreach (PassiveGrab g in window.ButtonGrabs)
        {
            if (!ReferenceEquals(g.Client, c) && g.Detail == button && g.Modifiers == modifiers)
            {
                throw new XProtocolError(XErrorCode.Access);
            }
        }
        window.ButtonGrabs.RemoveAll(g => ReferenceEquals(g.Client, c) && g.Detail == button && g.Modifiers == modifiers);
        window.ButtonGrabs.Add(new PassiveGrab(c, button, modifiers, ownerEvents, mask,
            confine == 0 ? null : Lookup<XWindow>(confine), cursorId == 0 ? null : Lookup<XCursor>(cursorId),
            PointerSync: pointerSync, KeyboardSync: keyboardSync));
    }

    private void UngrabButton(XClient c, XRequestReader r)
    {
        byte button = r.Data;
        XWindow window = Window(r.U32());
        ushort modifiers = r.U16();
        window.ButtonGrabs.RemoveAll(g => ReferenceEquals(g.Client, c)
                                          && (button == 0 || g.Detail == button)
                                          && (modifiers == 0x8000 || g.Modifiers == modifiers));
    }

    private void GrabKeyboard(XClient c, XRequestReader r)
    {
        bool ownerEvents = r.Data != 0;
        XWindow window = Window(r.U32());
        r.U32();   // time
        (bool pointerSync, bool keyboardSync) = ReadGrabModes(r);
        byte status;
        if (KeyboardGrab is { } existing && !ReferenceEquals(existing.Client, c))
        {
            status = 1;
        }
        else if (!window.IsViewable)
        {
            status = 3;
        }
        else
        {
            KeyboardGrab = new ActiveGrab { Client = c, Window = window, OwnerEvents = ownerEvents };
            ApplyGrabModes(KeyboardGrab, pointerSync, keyboardSync);
            status = 0;
        }
        c.Reply(status, w => w.Zero(24));
    }

    private void UngrabKeyboard(XClient c)
    {
        if (KeyboardGrab is { } grab && ReferenceEquals(grab.Client, c))
        {
            KeyboardGrab = null;
        }
    }

    private void GrabKey(XClient c, XRequestReader r)
    {
        bool ownerEvents = r.Data != 0;
        XWindow window = Window(r.U32());
        ushort modifiers = r.U16();
        byte key = r.U8();
        (bool pointerSync, bool keyboardSync) = ReadGrabModes(r);
        if (key is not 0 and < Keymap.MinKeycode)
        {
            throw new XProtocolError(XErrorCode.Value, key);
        }
        foreach (PassiveGrab g in window.KeyGrabs)
        {
            if (!ReferenceEquals(g.Client, c) && g.Detail == key && g.Modifiers == modifiers)
            {
                throw new XProtocolError(XErrorCode.Access);
            }
        }
        window.KeyGrabs.RemoveAll(g => ReferenceEquals(g.Client, c) && g.Detail == key && g.Modifiers == modifiers);
        window.KeyGrabs.Add(new PassiveGrab(c, key, modifiers, ownerEvents, 0, null, null,
            PointerSync: pointerSync, KeyboardSync: keyboardSync));
    }

    private void UngrabKey(XClient c, XRequestReader r)
    {
        byte key = r.Data;
        XWindow window = Window(r.U32());
        ushort modifiers = r.U16();
        window.KeyGrabs.RemoveAll(g => ReferenceEquals(g.Client, c)
                                       && (key == 0 || g.Detail == key)
                                       && (modifiers == 0x8000 || g.Modifiers == modifiers));
    }

    /// <summary>pointer-mode、keyboard-mode 两个字节:Synchronous 0、Asynchronous 1,别的值 BadValue。</summary>
    private static (bool PointerSync, bool KeyboardSync) ReadGrabModes(XRequestReader r)
    {
        byte pointerMode = r.U8(), keyboardMode = r.U8();
        if (pointerMode > 1)
        {
            throw new XProtocolError(XErrorCode.Value, pointerMode);
        }
        if (keyboardMode > 1)
        {
            throw new XProtocolError(XErrorCode.Value, keyboardMode);
        }
        return (pointerMode == 0, keyboardMode == 0);
    }

    // ================================================================== 查询

    private void QueryPointer(XClient c, XRequestReader r)
    {
        XWindow window = Window(r.U32());
        c.MotionHint = default;   // 客户端来问了位置:下一次移动再给它一条提示
        (int wx, int wy) = window.AbsoluteInner();
        uint child = 0;
        for (XWindow? w = _pointerWindow; w is not null; w = w.Parent)
        {
            if (ReferenceEquals(w.Parent, window))
            {
                child = w.Id;
                break;
            }
        }
        int px = Math.Max(0, _pointerX), py = Math.Max(0, _pointerY);
        ushort state = State;
        c.Reply(1, w => w.U32(Root.Id).U32(child).I16(px).I16(py).I16(px - wx).I16(py - wy).U16(state).Zero(6));
    }

    private void WarpPointer(XRequestReader r)
    {
        r.U32();
        uint dst = r.U32();
        r.I16();
        r.I16();
        r.U16();
        r.U16();
        short dx = r.I16(), dy = r.I16();
        if (dst == 0)
        {
            MovePointer(_pointerX + dx, _pointerY + dy);
            return;
        }
        (int x, int y) = Window(dst).AbsoluteInner();
        // 宿主的系统指针挪不动(那是用户的鼠标);这里只改服务端认为的指针位置。
        MovePointer(x + dx, y + dy);
    }

    private void GetKeyboardMapping(XClient c, XRequestReader r)
    {
        byte first = r.U8();
        byte count = r.U8();
        if (first < Keymap.MinKeycode || first + count - 1 > Keymap.MaxKeycode)
        {
            throw new XProtocolError(XErrorCode.Value, first);
        }
        int per = _keymap.KeysymsPerKeycode;
        c.Reply((byte)per, w =>
        {
            w.Zero(24);
            for (int k = 0; k < count; k++)
            {
                for (int col = 0; col < per; col++)
                {
                    w.U32(_keymap.Keysym((byte)(first + k), col));
                }
            }
        });
    }

    private void ChangeKeyboardMapping(XClient c, XRequestReader r)
    {
        byte count = r.Data;
        byte first = r.U8();
        byte per = r.U8();
        r.Skip(2);
        if (first < Keymap.MinKeycode || first + count - 1 > Keymap.MaxKeycode || per == 0)
        {
            throw new XProtocolError(XErrorCode.Value, first);
        }
        uint[] keysyms = new uint[count * per];
        for (int i = 0; i < keysyms.Length; i++)
        {
            keysyms[i] = r.U32();
        }
        _keymap.Change(first, per, keysyms);
        NotifyXkbMapChanged();
        _ = c;
        foreach (XClient client in _clients.Values)
        {
            client.Event(XEventCode.MappingNotify, 0, w => w.U8(1).U8(first).U8(count));
        }
    }

    private void GetModifierMapping(XClient c)
    {
        byte[] map = _keymap.ModifierMap;
        c.Reply((byte)_keymap.KeycodesPerModifier, w => w.Zero(24).Bytes(map));
    }

    private void SetModifierMapping(XClient c, XRequestReader r)
    {
        int per = r.Data;
        byte[] map = r.Bytes(per * 8);
        _keymap.SetModifierMap(map);
        NotifyXkbMapChanged();
        c.Reply(0, w => w.Zero(24));
        foreach (XClient client in _clients.Values)
        {
            client.Event(XEventCode.MappingNotify, 0, w => w.U8(0).U8(0).U8(0));
        }
    }

    private static void GetKeyboardControl(XClient c) =>
        c.Reply(1, w =>
        {
            w.U32(0).U8(0).U8(50).U16(400).U16(100).Zero(2);
            for (int i = 0; i < 32; i++)
            {
                w.U8(0xFF);
            }
        });

    private static void GetPointerMapping(XClient c) =>
        c.Reply(5, w => w.Zero(24).Bytes([1, 2, 3, 4, 5]).Pad4());
}
