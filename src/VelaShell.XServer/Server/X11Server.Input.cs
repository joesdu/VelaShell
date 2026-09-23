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
//   只实现异步抓取模式:SyncPointer / SyncKeyboard 与 AllowEvents 的冻结 / 放行按异步处理(架构 §10 记录)。

using VelaShell.XServer.Host;
using VelaShell.XServer.Input;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer.Server;

public sealed partial class X11Server
{
    private XWindow _pointerWindow;
    private int _pointerX;
    private int _pointerY;
    private ushort _buttons;
    private ushort _modifiers;
    private readonly byte[] _keysDown = new byte[32];

    /// <summary>键盘焦点:null = None;<see cref="Root" /> = PointerRoot;其余为具体窗口。</summary>
    private XWindow? _focus;
    private byte _focusRevertTo;
    private ActiveGrab? _pointerGrab;
    private ActiveGrab? _keyboardGrab;
    private int _cursorGlyph = int.MinValue;

    private ushort State => (ushort)(_modifiers | _buttons);

    // ================================================================== 宿主注入(任意线程)

    /// <summary>指针在顶层窗口里移动(内区坐标)。</summary>
    public void PointerMotion(uint topLevel, int x, int y) => Post(null, () =>
    {
        if (Lookup<XWindow>(topLevel) is { IsTopLevel: true } top)
        {
            MovePointer(top.X + top.BorderWidth + x, top.Y + top.BorderWidth + y);
        }
    });

    /// <summary>
    /// 按钮按下 / 松开。1 左、2 中、3 右;滚轮向上 4、向下 5(宿主应当为每格滚动注入一次按下 + 松开)。
    /// </summary>
    public void PointerButton(uint topLevel, int x, int y, int button, bool pressed) => Post(null, () =>
    {
        if (Lookup<XWindow>(topLevel) is not { IsTopLevel: true } top || button is < 1 or > 5)
        {
            return;
        }
        MovePointer(top.X + top.BorderWidth + x, top.Y + top.BorderWidth + y);
        ButtonEvent(button, pressed);
    });

    /// <summary>指针离开了所有顶层窗口(移到了宿主的其他窗口或桌面上)。</summary>
    public void PointerLeft() => Post(null, () => MovePointer(-1, -1));

    /// <summary>按键按下 / 松开(X 键码,见 <see cref="XKeycodes" />)。</summary>
    public void Key(byte keycode, bool pressed) => Post(null, () => KeyEvent(keycode, pressed));

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
        owner.Closed = true;
        owner.Output.Writer.TryComplete();
        DisconnectClient(owner);
    });

    // ================================================================== 指针

    private XWindow WindowAt(int rootX, int rootY)
    {
        XWindow current = Root;
        while (true)
        {
            (int ix, int iy) = current.AbsoluteInner();
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
                if (rootX >= x && rootY >= y && rootX < x + w && rootY < y + h)
                {
                    hit = child;
                    break;
                }
            }
            if (hit is null)
            {
                return current;
            }
            current = hit;
        }
    }

    private void MovePointer(int rootX, int rootY)
    {
        bool moved = rootX != _pointerX || rootY != _pointerY;
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

    /// <summary>重新算指针所在窗口;变了就发 Enter / Leave、更新光标。窗口树变化后也调它。</summary>
    internal void UpdatePointerWindow()
    {
        XWindow now = _pointerX < 0 ? Root : WindowAt(_pointerX, _pointerY);
        if (!ReferenceEquals(now, _pointerWindow))
        {
            XWindow old = _pointerWindow;
            _pointerWindow = now;
            GenerateCrossing(old, now);
        }
        UpdateCursor();
    }

    private void ButtonEvent(int button, bool pressed)
    {
        ushort bit = (ushort)(0x100 << (button - 1));
        if (pressed)
        {
            if (_pointerGrab is null && FindPassiveGrab(_pointerWindow, isButton: true, button) is { } passive)
            {
                _pointerGrab = new ActiveGrab
                {
                    Client = passive.Grab.Client,
                    Window = passive.Window,
                    OwnerEvents = passive.Grab.OwnerEvents,
                    EventMask = passive.Grab.EventMask,
                    Cursor = passive.Grab.Cursor,
                    ReleaseWhenButtonsUp = true,
                };
            }
            (XWindow Window, XClient Client, uint Mask)? delivered = DeliverDeviceEvent(
                XEventCode.ButtonPress, (byte)button, XEventMask.ButtonPress, _pointerWindow);
            if (_pointerGrab is null && delivered is { } d)
            {
                // 自动抓取:按下的那个窗口在所有按钮松开之前独占指针事件(协议「ButtonPress」)。
                _pointerGrab = new ActiveGrab
                {
                    Client = d.Client,
                    Window = d.Window,
                    OwnerEvents = (d.Mask & (uint)XEventMask.OwnerGrabButton) != 0,
                    EventMask = d.Mask,
                    ReleaseWhenButtonsUp = true,
                };
            }
            _buttons |= bit;
        }
        else
        {
            DeliverDeviceEvent(XEventCode.ButtonRelease, (byte)button, XEventMask.ButtonRelease, _pointerWindow);
            _buttons &= (ushort)~bit;
            if (_buttons == 0 && _pointerGrab is { ReleaseWhenButtonsUp: true })
            {
                _pointerGrab = null;
                UpdateCursor();
            }
        }
    }

    /// <summary>
    /// 投递一个设备事件:有主动抓取按抓取规则走,否则从源窗口向上传播到第一个有人选了它的窗口。
    /// 返回实际收到事件的(窗口, 客户端, 该客户端在那个窗口上的掩码),没人收时为 null。
    /// </summary>
    private (XWindow Window, XClient Client, uint Mask)? DeliverDeviceEvent(byte code, byte detail, XEventMask mask, XWindow source)
    {
        bool isKey = code is XEventCode.KeyPress or XEventCode.KeyRelease;
        ActiveGrab? grab = isKey ? _keyboardGrab : _pointerGrab;
        XWindow? stopAt = null;
        if (isKey && grab is null && _focus is { } focus && !ReferenceEquals(focus, Root))
        {
            stopAt = focus;
        }

        if (grab is not null)
        {
            if (grab.OwnerEvents && Propagate(source, mask, grab.Client, stopAt) is { } own)
            {
                SendDeviceEvent(own.Client, code, detail, own.Window, source, own.Mask);
                return own;
            }
            if (isKey || (grab.EventMask & (uint)mask) != 0)
            {
                SendDeviceEvent(grab.Client, code, detail, grab.Window, source, grab.EventMask);
                return (grab.Window, grab.Client, grab.EventMask);
            }
            return null;
        }

        return Propagate(source, mask, null, stopAt) is { } hit ? SendToAll(hit.Window) : null;

        (XWindow, XClient, uint)? SendToAll(XWindow window)
        {
            (XWindow, XClient, uint)? first = null;
            foreach ((XClient client, uint selected) in window.EventSelections)
            {
                if ((selected & (uint)mask) != 0 && !client.Closed)
                {
                    SendDeviceEvent(client, code, detail, window, source, selected);
                    first ??= (window, client, selected);
                }
            }
            return first;
        }
    }

    /// <summary>从源窗口向上找第一个(指定客户端)选了这类事件的窗口;碰上 do-not-propagate 或 <paramref name="stopAt" /> 就停。</summary>
    private static (XWindow Window, XClient Client, uint Mask)? Propagate(XWindow source, XEventMask mask, XClient? only, XWindow? stopAt)
    {
        for (XWindow? w = source; w is not null; w = w.Parent)
        {
            foreach ((XClient client, uint selected) in w.EventSelections)
            {
                if ((selected & (uint)mask) != 0 && !client.Closed && (only is null || ReferenceEquals(client, only)))
                {
                    return (w, client, selected);
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
        if (to.IsDescendantOf(from))
        {
            Crossing(XEventCode.LeaveNotify, from, inferior);
            foreach (XWindow w in PathBetween(to, from))
            {
                Crossing(XEventCode.EnterNotify, w, @virtual);
            }
            Crossing(XEventCode.EnterNotify, to, ancestor);
        }
        else if (from.IsDescendantOf(to))
        {
            Crossing(XEventCode.LeaveNotify, from, ancestor);
            foreach (XWindow w in Enumerable.Reverse(PathBetween(from, to)))
            {
                Crossing(XEventCode.LeaveNotify, w, @virtual);
            }
            Crossing(XEventCode.EnterNotify, to, inferior);
        }
        else
        {
            XWindow common = CommonAncestor(from, to);
            Crossing(XEventCode.LeaveNotify, from, nonlinear);
            foreach (XWindow w in Enumerable.Reverse(PathBetween(from, common)))
            {
                Crossing(XEventCode.LeaveNotify, w, nonlinearVirtual);
            }
            foreach (XWindow w in PathBetween(to, common))
            {
                Crossing(XEventCode.EnterNotify, w, nonlinearVirtual);
            }
            Crossing(XEventCode.EnterNotify, to, nonlinear);
        }
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

    private void Crossing(byte code, XWindow window, byte detail)
    {
        XEventMask mask = code == XEventCode.EnterNotify ? XEventMask.EnterWindow : XEventMask.LeaveWindow;
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
            .U32(time).U32(Root.Id).U32(window.Id).U32(0)
            .I16(px).I16(py).I16(px - ex).I16(py - ey).U16(state)
            .U8(0)                                          // mode:Normal
            .U8((byte)(0x02 | (focus ? 0x01 : 0)))));       // same-screen | focus
    }

    /// <summary>光标:抓取的光标优先,否则从指针所在窗口向上找第一个设了光标的窗口。</summary>
    private void UpdateCursor()
    {
        XCursor? cursor = _pointerGrab?.Cursor;
        for (XWindow? w = _pointerWindow; cursor is null && w is not null; w = w.Parent)
        {
            cursor = w.Cursor;
        }
        int glyph = cursor?.Glyph ?? -1;
        if (glyph == _cursorGlyph)
        {
            return;
        }
        _cursorGlyph = glyph;
        XTopLevelWindow? handle = _pointerWindow.TopLevel is { } top && _topLevelHandles.TryGetValue(top, out XTopLevelWindow? h) ? h : null;
        _host.CursorChanged(handle, glyph);
    }

    // ================================================================== 键盘

    private void KeyEvent(byte keycode, bool pressed)
    {
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

        XWindow? source = KeyboardSource();
        if (pressed && _keyboardGrab is null && source is not null && FindPassiveGrab(source, isButton: false, keycode) is { } passive)
        {
            _keyboardGrab = new ActiveGrab
            {
                Client = passive.Grab.Client,
                Window = passive.Window,
                OwnerEvents = passive.Grab.OwnerEvents,
                ReleaseWhenButtonsUp = true,   // 对键盘:这个键松开时解除
            };
            _passiveKeyGrabKey = keycode;
        }

        // 事件里的 state 是事件发生前的:修饰键状态在投递之后才更新。
        if (source is not null)
        {
            DeliverDeviceEvent(pressed ? XEventCode.KeyPress : XEventCode.KeyRelease, keycode,
                pressed ? XEventMask.KeyPress : XEventMask.KeyRelease, source);
        }

        // 更新修饰键状态(Lock 类按下翻转;其余按住生效)。
        ushort modBit = _keymap.ModifierBitOf(keycode);
        if (modBit != 0)
        {
            if (_keymap.IsLockingKey(keycode))
            {
                if (pressed && !wasDown)
                {
                    _modifiers ^= modBit;
                }
            }
            else if (pressed)
            {
                _modifiers |= modBit;
            }
            else
            {
                _modifiers &= (ushort)~modBit;
            }
        }

        if (!pressed && _keyboardGrab is { ReleaseWhenButtonsUp: true } && _passiveKeyGrabKey == keycode)
        {
            _keyboardGrab = null;
            _passiveKeyGrabKey = 0;
        }
    }

    private byte _passiveKeyGrabKey;

    /// <summary>按键事件的源窗口:焦点是 PointerRoot 时是指针所在窗口;指针在焦点窗口里面时是指针所在窗口;否则是焦点窗口。</summary>
    private XWindow? KeyboardSource()
    {
        if (_keyboardGrab is not null)
        {
            return _pointerWindow.IsViewable ? _pointerWindow : _keyboardGrab.Window;
        }
        return _focus switch
        {
            null => null,
            { } f when ReferenceEquals(f, Root) => _pointerWindow,
            { } f => ReferenceEquals(_pointerWindow, f) || _pointerWindow.IsDescendantOf(f) ? _pointerWindow : f,
        };
    }

    /// <summary>被动抓取:从根往下到源窗口,第一个匹配的生效(协议「GrabButton」「GrabKey」)。</summary>
    private (XWindow Window, PassiveGrab Grab)? FindPassiveGrab(XWindow source, bool isButton, int detail)
    {
        List<XWindow> chain = [];
        for (XWindow? w = source; w is not null; w = w.Parent)
        {
            chain.Add(w);
        }
        chain.Reverse();
        ushort mods = (ushort)(_modifiers & 0xFF);
        foreach (XWindow w in chain)
        {
            foreach (PassiveGrab grab in isButton ? w.ButtonGrabs : w.KeyGrabs)
            {
                if (grab.Matches(detail, mods) && !grab.Client.Closed)
                {
                    return (w, grab);
                }
            }
        }
        return null;
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
        const byte nonlinear = 3;
        if (old is { IsRoot: false })
        {
            DeliverToSelectors(old, XEventMask.FocusChange, c => c.Event(XEventCode.FocusOut, nonlinear, w => w.U32(old.Id).U8(0)));
        }
        if (focus is { IsRoot: false })
        {
            DeliverToSelectors(focus, XEventMask.FocusChange, c => c.Event(XEventCode.FocusIn, nonlinear, w => w.U32(focus.Id).U8(0)));
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
        r.U8();
        r.U8();
        uint confine = r.U32();
        uint cursorId = r.U32();
        r.U32();
        byte status;
        if (_pointerGrab is { } existing && !ReferenceEquals(existing.Client, c))
        {
            status = 1;   // AlreadyGrabbed
        }
        else if (!window.IsViewable)
        {
            status = 3;   // GrabNotViewable
        }
        else
        {
            _pointerGrab = new ActiveGrab
            {
                Client = c,
                Window = window,
                OwnerEvents = ownerEvents,
                EventMask = mask,
                Cursor = cursorId == 0 ? null : Lookup<XCursor>(cursorId),
            };
            _ = confine;
            status = 0;
            UpdateCursor();
        }
        c.Reply(status, w => w.Zero(24));
    }

    private void UngrabPointer(XClient c)
    {
        if (_pointerGrab is { } grab && ReferenceEquals(grab.Client, c))
        {
            _pointerGrab = null;
            UpdateCursor();
        }
    }

    private void ChangeActivePointerGrab(XClient c, XRequestReader r)
    {
        uint cursorId = r.U32();
        r.U32();
        ushort mask = r.U16();
        if (_pointerGrab is { } grab && ReferenceEquals(grab.Client, c))
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
        r.U8();
        r.U8();
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
            confine == 0 ? null : Lookup<XWindow>(confine), cursorId == 0 ? null : Lookup<XCursor>(cursorId)));
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
        byte status;
        if (_keyboardGrab is { } existing && !ReferenceEquals(existing.Client, c))
        {
            status = 1;
        }
        else if (!window.IsViewable)
        {
            status = 3;
        }
        else
        {
            _keyboardGrab = new ActiveGrab { Client = c, Window = window, OwnerEvents = ownerEvents };
            status = 0;
        }
        c.Reply(status, w => w.Zero(24));
    }

    private void UngrabKeyboard(XClient c)
    {
        if (_keyboardGrab is { } grab && ReferenceEquals(grab.Client, c))
        {
            _keyboardGrab = null;
        }
    }

    private void GrabKey(XClient c, XRequestReader r)
    {
        bool ownerEvents = r.Data != 0;
        XWindow window = Window(r.U32());
        ushort modifiers = r.U16();
        byte key = r.U8();
        if (key != 0 && (key < Keymap.MinKeycode))
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
        window.KeyGrabs.Add(new PassiveGrab(c, key, modifiers, ownerEvents, 0, null, null));
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

    // ================================================================== 查询

    private void QueryPointer(XClient c, XRequestReader r)
    {
        XWindow window = Window(r.U32());
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
