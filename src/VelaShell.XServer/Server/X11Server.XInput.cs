// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Input Device Extension Protocol, Version 2.2(XI2)—— §2「Notations」(FP1616、FP3232、SETofEVENTMASK)、
//   §4「Devices」(主 / 从设备、XIAllDevices 0 / XIAllMasterDevices 1、设备类:Key 0、Button 1、Valuator 2)、
//   §6「Requests」(XIQueryPointer 40 … XIGetSelectedEvents 60、XIBarrierReleasePointer 61)、
//   §7「Events」(经 Generic Event Extension 发出:DeviceEvent —— KeyPress 2 / KeyRelease 3 / ButtonPress 4 /
//   ButtonRelease 5 / Motion 6;EnterLeave —— Enter 7 / Leave 8 / FocusIn 9 / FocusOut 10;
//   RawEvent —— RawKeyPress 13 … RawMotion 17,只发给在根窗口上选了它的客户端);§「Smooth scrolling」(XI 2.1:滚动轴、
//   ScrollClass、与按钮 4–7 的双向模拟、PointerEmulated)
//   xorgproto 的 XI2.h / XI2proto.h(XI2 的协议定义)—— 类的编号(Key 0、Button 1、Valuator 2、Scroll 3)、ScrollClass 的线上布局
//   (xXIScrollInfo)、XIScrollTypeVertical 1 / Horizontal 2、XIScrollFlagPreferred、XIPointerEmulated、XIModeRelative
//   X Input Device Extension Protocol, Version 1.5 —— GetExtensionVersion 1、ListInputDevices 2、OpenDevice 3、
//   CloseDevice 4、SelectExtensionEvent 6、GetSelectedExtensionEvents 7、GetDeviceFocus 20、SetDeviceFocus 21、
//   GetDeviceKeyMapping 24、GetDeviceModifierMapping 26、GetDeviceButtonMapping 28、QueryDeviceState 30、
//   DeviceBell 32、ListDeviceProperties 36、GetDeviceProperty 39;错误 BadDevice / BadEvent / BadMode / DeviceBusy / BadClass
//
//   设备:主指针 2、主键盘 3,各挂一个从设备(4、5)。宿主注入的输入都来自这两个从设备。
//   XIChangeHierarchy 可以加减主设备、挂上 / 摘下从设备(X11Server.XiHierarchy.cs)。
//   XI2 事件与核心事件走同一条传播路径(同一个窗口上,选了核心的收核心、选了 XI2 的收 XI2);抓取也可以是 XI2 的。
//   XI 1.x 的设备事件(DeviceKeyPress 之类)不产生 —— 现代客户端都走 XI2;XI 1.x 的请求只回答查询。

using VelaShell.XServer.Input;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    private const ushort XiMasterPointer = 2, XiMasterKeyboard = 3, XiSlavePointer = 4, XiSlaveKeyboard = 5;
    private const int XiEnter = 7, XiLeave = 8, XiFocusIn = 9, XiFocusOut = 10;
    private const int XiRawKeyPress = 13, XiRawKeyRelease = 14, XiRawButtonPress = 15, XiRawButtonRelease = 16, XiRawMotion = 17;

    /// <summary>滚动轴的编号(轴 0、1 是 Abs X / Abs Y)。</summary>
    private const ushort XiScrollAxisHorizontal = 2, XiScrollAxisVertical = 3;

    /// <summary>ScrollClass 的 Preferred 标志(XI2.h:XIScrollFlagPreferred)。</summary>
    private const uint XiScrollFlagPreferred = 1u << 1;

    /// <summary>DeviceEvent / RawEvent 的 PointerEmulated 标志(XI2.h:XIPointerEmulated):这个事件是从另一种事件模拟出来的。</summary>
    private const uint XiPointerEmulatedFlag = 1u << 16;

    /// <summary>两个滚动轴累计的滚动量(单位 = 一格);XIQueryDevice 的 ValuatorClass 与 Motion 事件里报的就是它。</summary>
    private (double X, double Y) _scrollValue;

    /// <summary>还没攒够一格、没模拟成按钮 4–7 的滚动量。</summary>
    private (double X, double Y) _scrollRemainder;

    /// <summary>正在投递的 Motion 事件带哪几个滚动轴(<see cref="SendXi2DeviceEvent" /> 据此写轴的掩码与值);平时都为假。</summary>
    private (bool Horizontal, bool Vertical) _xi2ScrollAxes;

    /// <summary>正在投递的指针事件额外带的 XI2 标志(模拟出来的事件带 PointerEmulated);平时为 0。</summary>
    private uint _xi2PointerFlags;
    private const int XiButtonCount = 9;

    private static readonly string[] XiButtonLabels =
    [
        "Button Left", "Button Middle", "Button Right", "Button Wheel Up", "Button Wheel Down",
        "Button Horiz Wheel Left", "Button Horiz Wheel Right", "Button Side", "Button Extra",
    ];

    /// <summary>设备属性(XIGetProperty / GetDeviceProperty):设备 → 原子 → 值。</summary>
    private readonly Dictionary<ushort, Dictionary<uint, XProperty>> _deviceProperties = [];

    private static XProtocolError BadDevice(uint id) => new((XErrorCode)XInputErrorBase, id);

    private static int Fp1616(int value) => value << 16;

    private void XInput(XClient c, XRequestReader r)
    {
        byte minor = r.Data;
        if (c.Untrusted && minor is 25 or 27 or 29 or 35 or 37 or 38 or 57 or 58)
        {
            // SECURITY:非受信客户端改设备的键位表、修饰键、按钮映射、设备属性,与核心的 ChangeKeyboardMapping 等一样回 Access。
            throw new XProtocolError(XErrorCode.Access);
        }
        switch (minor)
        {
            // ============================================================== XI 1.x
            case 1:   // GetExtensionVersion
                c.Reply(minor, w => w.U16(2).U16(2).Bool(true).Zero(19));
                break;
            case 2:   // ListInputDevices
                XiListInputDevices(c);
                break;
            case 3:   // OpenDevice:返回设备有哪些类,以及各类的事件基数
                {
                    byte id = r.U8();
                    if (!IsKnownDevice(id) || id is (byte)XiMasterPointer or (byte)XiMasterKeyboard)
                    {
                        throw BadDevice(id);   // 核心设备不能经 XI 1.x 打开(规范 OpenDevice)
                    }
                    byte[] classes = IsPointerDevice(id) ? [1, 2] : [0];
                    c.Reply(minor, w =>
                    {
                        w.U8((byte)classes.Length).Zero(23);
                        foreach (byte cls in classes)
                        {
                            w.U8(cls).U8(XInputEventBase);
                        }
                        w.Pad4();
                    });
                    break;
                }
            case 4:   // CloseDevice
            case 6:   // SelectExtensionEvent:XI 1.x 的设备事件不产生,接受选择即可
                break;
            case 7:   // GetSelectedExtensionEvents
                c.Reply(minor, w => w.U16(0).U16(0).Zero(20));
                break;
            case 20:  // GetDeviceFocus:键盘设备的焦点就是核心焦点
                {
                    uint focus = _focus switch { null => 0, { IsRoot: true } => 1, { } f => f.Id };
                    uint time = _lastFocusChangeTime;
                    c.Reply(minor, w => w.U32(focus).U32(time).U8(_focusRevertTo).Zero(15));
                    break;
                }
            case 21:  // SetDeviceFocus
                {
                    uint focusId = r.U32(), time = r.U32();
                    byte revertTo = r.U8();
                    if (revertTo > 2)
                    {
                        throw new XProtocolError(XErrorCode.Value, revertTo);
                    }
                    SetFocusFromClient(focusId switch { 0 => null, 1 => Root, _ => Window(focusId) }, revertTo, time);
                    break;
                }
            case 24:  // GetDeviceKeyMapping
                {
                    r.U8();
                    byte first = r.U8(), count = r.U8();
                    if (first < Keymap.MinKeycode || first + count - 1 > Keymap.MaxKeycode)
                    {
                        throw new XProtocolError(XErrorCode.Value, first);   // 同核心 GetKeyboardMapping(原先键码按字节回绕)
                    }
                    int per = _keymap.KeysymsPerKeycode;
                    c.Reply(minor, w =>
                    {
                        w.U8((byte)per).Zero(23);
                        for (int k = 0; k < count; k++)
                        {
                            for (int col = 0; col < per; col++)
                            {
                                w.U32(_keymap.Keysym((byte)(first + k), col));
                            }
                        }
                    });
                    break;
                }
            case 26:  // GetDeviceModifierMapping
                {
                    byte[] map = _keymap.ModifierMap;
                    c.Reply(minor, w => w.U8((byte)_keymap.KeycodesPerModifier).Zero(23).Bytes(map));
                    break;
                }
            case 28:  // GetDeviceButtonMapping:与核心 GetPointerMapping 同一份
                {
                    byte[] map = [.. _pointerMap];
                    c.Reply(minor, w => w.U8((byte)map.Length).Zero(23).Bytes(map).Pad4());
                    break;
                }
            case 30:  // QueryDeviceState
                {
                    byte id = r.U8();
                    if (!IsKnownDevice(id))
                    {
                        throw BadDevice(id);
                    }
                    c.Reply(minor, w =>
                    {
                        if (IsPointerDevice(id))
                        {
                            w.U8(2).Zero(23);
                            w.U8(1).U8(36).U8(XiButtonCount).Zero(1).Bytes(_buttonsDown);            // ButtonState
                            w.U8(2).U8(12).U8(2).U8(1).I32(_pointerX).I32(_pointerY);   // ValuatorState
                        }
                        else
                        {
                            w.U8(1).Zero(23);
                            w.U8(0).U8(36).U8(248).Zero(1).Bytes(_keysDown);                          // KeyState
                        }
                    });
                    break;
                }
            case 32:  // DeviceBell
                r.Skip(3);
                RingBell(r.I8());
                break;
            case 36:  // ListDeviceProperties
                {
                    byte id = r.U8();
                    uint[] atoms = [.. DeviceProperties(id).Keys];
                    c.Reply(minor, w =>
                    {
                        w.U16((ushort)atoms.Length).Zero(22);
                        foreach (uint atom in atoms)
                        {
                            w.U32(atom);
                        }
                    });
                    break;
                }
            case 39:  // GetDeviceProperty
                {
                    uint property = r.U32(), type = r.U32(), offset = r.U32(), length = r.U32();
                    byte id = r.U8();
                    XiReplyProperty(c, minor, id, property, type, offset, length, xi2: false);
                    break;
                }

            // ============================================================== XI 2.x
            case 40:  // XIQueryPointer
                {
                    XWindow window = Window(r.U32());
                    ushort id = r.U16();
                    if (!IsPointerDevice(id))
                    {
                        throw BadDevice(id);
                    }
                    c.MotionHint = default;   // 同核心 QueryPointer:客户端来问了位置,下一次移动再给它一条提示
                    (int wx, int wy) = window.AbsoluteInner();
                    uint child = ChildTowardPointer(window);
                    int px = _pointerX, py = _pointerY;
                    c.Reply(minor, w =>
                    {
                        w.U32(Root.Id).U32(child).I32(Fp1616(px)).I32(Fp1616(py)).I32(Fp1616(px - wx)).I32(Fp1616(py - wy))
                            .Bool(true).Zero(1).U16(1);
                        WriteXiModifiers(w);
                        WriteXiButtons(w, 1);
                    });
                    break;
                }
            case 41:  // XIWarpPointer
                {
                    uint src = r.U32(), dst = r.U32();
                    int srcX = r.I32() >> 16, srcY = r.I32() >> 16;
                    ushort srcW = r.U16(), srcH = r.U16();
                    int dstX = r.I32() >> 16, dstY = r.I32() >> 16;
                    ushort id = r.U16();
                    if (!IsPointerDevice(id))
                    {
                        throw BadDevice(id);   // 只能挪主指针或浮动的从指针
                    }
                    // 与核心 WarpPointer 同一条路(源矩形、夹在根窗口与 confine-to 里、冻结时排队)。
                    WarpPointerTo(c, src == 0 ? null : Window(src), srcX, srcY, srcW, srcH, dst == 0 ? null : Window(dst), dstX, dstY);
                    break;
                }
            case 42:  // XIChangeCursor:同核心的窗口光标
                {
                    XWindow window = Window(r.U32());
                    uint cursor = r.U32();
                    window.Cursor = cursor == 0 ? null : Use<XCursorResource>(cursor) ?? throw new XProtocolError(XErrorCode.Cursor, cursor);
                    UpdateCursor();
                    break;
                }
            case 43:  // XIChangeHierarchy
                if (IsRestricted(c))
                {
                    throw new XProtocolError(XErrorCode.Access);   // 能让物理输入设备失效(见 RestrictForwardedClients)
                }
                XiChangeHierarchy(r);
                break;
            case 44:  // XISetClientPointer:指针位置只有一份,接受即可(设备须是主设备)
                {
                    r.Skip(4);
                    ushort id = r.U16();
                    if (!IsMasterDevice(id))
                    {
                        throw BadDevice(id);
                    }
                    break;
                }
            case 45:  // XIGetClientPointer:物理指针挂着的那个主指针(浮动时退回虚拟核心指针)
                {
                    ushort attached = MasterOf(pointer: true);
                    ushort clientPointer = attached != 0 ? attached : XiMasterPointer;
                    c.Reply(minor, w => w.Bool(true).Zero(1).U16(clientPointer).Zero(20));
                    break;
                }
            case 46:  // XISelectEvents
                XiSelectEvents(c, r);
                break;
            case 47:  // XIQueryVersion:支持到 2.2
                {
                    ushort major = r.U16(), minorVersion = r.U16();
                    (ushort maj, ushort min) = major > 2 || (major == 2 && minorVersion >= 2) ? ((ushort)2, (ushort)2) : (major, minorVersion);
                    c.Reply(minor, w => w.U16(maj).U16(min).Zero(20));
                    break;
                }
            case 48:  // XIQueryDevice
                {
                    ushort id = r.U16();
                    ushort[] devices = id switch
                    {
                        0 => [.. _xiDevices.Keys],
                        1 => [.. _xiDevices.Values.Where(d => d.Master).Select(d => d.Id)],
                        _ when IsKnownDevice(id) => [id],
                        _ => throw BadDevice(id),
                    };
                    c.Reply(minor, w =>
                    {
                        w.U16((ushort)devices.Length).Zero(22);
                        foreach (ushort device in devices)
                        {
                            WriteXiDeviceInfo(w, device);
                        }
                    });
                    break;
                }
            case 49:  // XISetFocus
                {
                    // XI 2.2「XISetFocus」:窗口不可见了焦点退到第一个可见的祖先,等于核心的 RevertToParent(原先给的是 PointerRoot);
                    // PointerRoot 也接受(原先回 BadWindow)。
                    uint focusId = r.U32(), time = r.U32();
                    SetFocusFromClient(focusId switch { 0 => null, 1 => Root, _ => Window(focusId) }, 2, time);
                    break;
                }
            case 50:  // XIGetFocus
                {
                    uint focus = _focus switch { null => 0, { IsRoot: true } => Root.Id, { } f => f.Id };
                    c.Reply(minor, w => w.U32(focus).Zero(20));
                    break;
                }
            case 51:  // XIGrabDevice
                XiGrabDevice(c, r);
                break;
            case 52:  // XIUngrabDevice
                {
                    uint time = r.U32();
                    ushort id = r.U16();
                    if (!IsKnownDevice(id))
                    {
                        throw BadDevice(id);   // 原先不校验
                    }
                    if (IsPointerDevice(id))
                    {
                        if (ReferenceEquals(PointerGrab?.Client, c) && TimeAcceptable(ref time, _lastPointerGrabTime))
                        {
                            PointerGrab = null;
                            UpdateCursor();
                        }
                    }
                    else if (ReferenceEquals(KeyboardGrab?.Client, c) && TimeAcceptable(ref time, _lastKeyboardGrabTime))
                    {
                        KeyboardGrab = null;
                    }
                    break;
                }
            case 53:  // XIAllowEvents:换成核心 AllowEvents 的模式(按设备是指针还是键盘)
                {
                    uint time = r.U32();
                    ushort id = r.U16();
                    byte mode = r.U8();
                    if (!IsKnownDevice(id))
                    {
                        throw BadDevice(id);
                    }
                    byte core = CoreAllowMode(mode, IsPointerDevice(id));
                    if (core == byte.MaxValue)
                    {
                        break;   // AcceptTouch / RejectTouch:没有触摸设备
                    }
                    AllowEvents(c, core, time);
                    break;
                }
            case 61:  // XIBarrierReleasePointer:指针屏障不生效(XFIXES)
                break;
            case 54:  // XIPassiveGrabDevice
                XiPassiveGrab(c, r, grab: true);
                break;
            case 55:  // XIPassiveUngrabDevice
                XiPassiveGrab(c, r, grab: false);
                break;
            case 56:  // XIListProperties
                {
                    ushort id = r.U16();
                    uint[] atoms = [.. DeviceProperties(id).Keys];
                    c.Reply(minor, w =>
                    {
                        w.U16((ushort)atoms.Length).Zero(22);
                        foreach (uint atom in atoms)
                        {
                            w.U32(atom);
                        }
                    });
                    break;
                }
            case 57:  // XIChangeProperty
                {
                    ushort id = r.U16();
                    byte mode = r.U8(), format = r.U8();
                    uint property = r.U32(), type = r.U32(), count = r.U32();
                    XiChangeProperty(c, r, id, mode, format, property, type, count);
                    break;
                }
            case 58:  // XIDeleteProperty
                {
                    ushort id = r.U16();
                    r.Skip(2);
                    uint property = r.U32();
                    CheckAtom(property);
                    if (DeviceProperties(id).Remove(property, out XProperty? removed))
                    {
                        ReleaseProperty(removed);
                        SendXiPropertyEvent(id, property, what: 0);
                    }
                    break;
                }
            case 59:  // XIGetProperty
                {
                    ushort id = r.U16();
                    r.Skip(2);   // delete、pad
                    uint property = r.U32(), type = r.U32(), offset = r.U32(), length = r.U32();
                    XiReplyProperty(c, minor, id, property, type, offset, length, xi2: true);
                    break;
                }
            case 60:  // XIGetSelectedEvents
                {
                    XWindow window = Window(r.U32());
                    // 按设备回当初选的那几份(原先只记了合起来的主 / 从两个,回的设备号对不上)。
                    List<(ushort Device, ulong Mask)> masks = window.Xi2Selections.TryGetValue(c, out XiSelection? selected)
                        ? [.. selected.ByDevice.Select(p => (p.Key, p.Value))]
                        : [];
                    c.Reply(minor, w =>
                    {
                        w.U16((ushort)masks.Count).Zero(22);
                        foreach ((ushort device, ulong mask) in masks)
                        {
                            w.U16(device).U16(2);
                            for (int b = 0; b < 8; b++)
                            {
                                w.U8((byte)(mask >> (8 * b)));   // 字节数组,与客户端字节序无关
                            }
                        }
                    });
                    break;
                }
            default:
                throw new XProtocolError(XErrorCode.Request);
        }
    }

    // ------------------------------------------------------------------ 设备描述

    private void WriteXiDeviceInfo(XWriter w, ushort id)
    {
        XiDevice device = _xiDevices[id];
        byte[] nameBytes = XWire.Latin1.GetBytes(device.Name);
        bool pointer = device.Pointer;
        w.U16(id).U16(device.Use).U16(device.Attachment).U16(XiClassCount(pointer)).U16((ushort)nameBytes.Length)
            .Bool(device.Enabled).Zero(1).Bytes(nameBytes).Pad4();
        WriteXiClasses(w, pointer);
    }

    /// <summary>指针的类:按钮 + Abs X / Abs Y + 两个滚动轴的 ValuatorClass + 两个 ScrollClass;键盘只有按键。</summary>
    private static ushort XiClassCount(bool pointer) => pointer ? (ushort)7 : (ushort)1;

    /// <summary>
    /// XI_DeviceChanged(evtype 1,reason DeviceChange 2):指针设备的轴范围随根窗口的尺寸变了(Abs X / Abs Y 的最大值),
    /// 给在根窗口上为这个设备选了它的客户端发,带上全部的类。原先从不发,客户端按旧的范围换算轴值。
    /// </summary>
    private void SendXiDeviceChanged()
    {
        const int xiDeviceChanged = 1, deviceChange = 2;
        if (!Root.AnyXi2Selects(xiDeviceChanged))
        {
            return;
        }
        uint time = Now;
        foreach (XiDevice device in _xiDevices.Values.Where(d => d.Pointer).ToArray())
        {
            foreach ((XClient client, (ulong master, ulong slave)) in Root.Xi2Selections)
            {
                if (client.Closed || ((device.Master ? master : slave) & (1UL << xiDeviceChanged)) == 0)
                {
                    continue;
                }
                ushort id = device.Id;
                client.GenericEvent(XInputMajor, xiDeviceChanged, w =>
                {
                    w.U16(id).U32(time).U16(XiClassCount(pointer: true)).U16(id).U8(deviceChange).Zero(11);
                    WriteXiClasses(w, pointer: true);
                });
            }
        }
    }

    /// <summary>设备的类(XIQueryDevice 与 XI_DeviceChanged 共用):指针是按钮 + 两个轴,键盘是按键。</summary>
    private void WriteXiClasses(XWriter w, bool pointer)
    {
        ushort source = pointer ? XiSlavePointer : XiSlaveKeyboard;
        if (pointer)
        {
            // ButtonClass:type 1、len、sourceid、num_buttons、state(1 个 32 位)、labels
            w.U16(1).U16(3 + XiButtonCount).U16(source).U16(XiButtonCount);
            WriteButtonMask(w, 1);
            foreach (string label in XiButtonLabels)
            {
                w.U32(Intern(label));
            }
            WriteValuatorClass(w, source, 0, "Abs X", Root.Width, _pointerX);
            WriteValuatorClass(w, source, 1, "Abs Y", Root.Height, _pointerY);
            // 平滑滚动(XI 2.1「Smooth scrolling」):两个相对轴,值是累计的滚动量,一个单位 = 一格(increment 1.0)。
            WriteScrollValuatorClass(w, source, XiScrollAxisHorizontal, "Rel Horiz Scroll", _scrollValue.X);
            WriteScrollValuatorClass(w, source, XiScrollAxisVertical, "Rel Vert Scroll", _scrollValue.Y);
            // ScrollClass:type 3、len 6、sourceid、number、scroll_type(Vertical 1 / Horizontal 2)、pad、flags(Preferred)、increment(FP3232)
            w.U16(3).U16(6).U16(source).U16(XiScrollAxisHorizontal).U16(2).Zero(2).U32(XiScrollFlagPreferred).I32(1).U32(0);
            w.U16(3).U16(6).U16(source).U16(XiScrollAxisVertical).U16(1).Zero(2).U32(XiScrollFlagPreferred).I32(1).U32(0);
        }
        else
        {
            // KeyClass:type 0、len、sourceid、num_keys、keycodes
            const int keys = Keymap.MaxKeycode - Keymap.MinKeycode + 1;
            w.U16(0).U16(2 + keys).U16(source).U16(keys);
            for (int k = Keymap.MinKeycode; k <= Keymap.MaxKeycode; k++)
            {
                w.U32((uint)k);
            }
        }
    }

    private void WriteValuatorClass(XWriter w, ushort source, ushort number, string label, int max, int value) =>
        w.U16(2).U16(11).U16(source).U16(number).U32(Intern(label))
            .I32(0).U32(0)                  // min(FP3232)
            .I32(max - 1).U32(0)            // max
            .I32(value).U32(0)              // value
            .U32(1).U8(1).Zero(3);          // resolution、mode = Absolute

    /// <summary>滚动轴的 ValuatorClass:相对模式、没有范围(min = max = 0),value 是到目前为止累计的滚动量(客户端据此接着算增量)。</summary>
    private void WriteScrollValuatorClass(XWriter w, ushort source, ushort number, string label, double value)
    {
        w.U16(2).U16(11).U16(source).U16(number).U32(Intern(label))
            .I32(0).U32(0).I32(0).U32(0);   // min、max:没有
        WriteFp3232(w, value);
        w.U32(0).U8(0).Zero(3);             // resolution、mode = Relative
    }

    /// <summary>FP3232:整数部分 32 位有符号、小数部分 32 位无符号(XI2「Notations」)。</summary>
    private static void WriteFp3232(XWriter w, double value)
    {
        double integral = Math.Floor(value);
        w.I32((int)Math.Clamp(integral, int.MinValue, int.MaxValue)).U32((uint)Math.Min(uint.MaxValue, (value - integral) * 4294967296.0));
    }

    private void XiListInputDevices(XClient c)
    {
        // XI 1.x 的视角:核心指针(use 0)、核心键盘(use 1),再加两个从设备作为扩展设备。
        // XI 1.x 的 use:IsXPointer 0、IsXKeyboard 1、IsXExtensionKeyboard 3、IsXExtensionPointer 4。
        (byte Id, byte Use, string Name, bool Pointer)[] devices =
        [
            .. _xiDevices.Values.Select(d => ((byte)d.Id,
                d.Id == XiMasterPointer ? (byte)0 : d.Id == XiMasterKeyboard ? (byte)1 : d.Pointer ? (byte)4 : (byte)3,
                d.Name, d.Pointer)),
        ];
        uint mouse = Intern("MOUSE"), keyboard = Intern("KEYBOARD");
        c.Reply(2, w =>
        {
            w.U8((byte)devices.Length).Zero(23);
            foreach ((byte id, byte use, _, bool pointer) in devices)
            {
                w.U32(pointer ? mouse : keyboard).U8(id).U8(pointer ? (byte)2 : (byte)1).U8(use).U8(0);
            }
            foreach ((_, _, _, bool pointer) in devices)
            {
                if (pointer)
                {
                    w.U8(1).U8(4).U16(XiButtonCount);                                     // ButtonInfo
                    w.U8(2).U8(8 + 24).U8(2).U8(1).U32(0);                                 // ValuatorInfo:2 轴、Absolute
                    w.U32(1).I32(0).I32(Root.Width - 1).U32(1).I32(0).I32(Root.Height - 1);
                }
                else
                {
                    w.U8(0).U8(8).U8(Keymap.MinKeycode).U8(Keymap.MaxKeycode).U16(248).Zero(2);   // KeyInfo
                }
            }
            foreach ((_, _, string name, _) in devices)
            {
                byte[] bytes = XWire.Latin1.GetBytes(name);
                w.U8((byte)bytes.Length).Bytes(bytes);
            }
            w.Pad4();
        });
    }

    /// <summary>
    /// XIChangeProperty(XI 2.2「XIChangeProperty」,语义同核心 ChangeProperty):原子、格式、模式、长度逐项校验;
    /// Prepend / Append 的类型或格式与现值不同回 BadMatch;16 / 32 位值按本机序存;单个值不超过 <see cref="MaxPropertyBytes" />,
    /// 记在写它的客户端名下(设备属性是全局的,客户端断开也不释放 —— 原先既不设上限,追加还每次整份复制)。
    /// </summary>
    private void XiChangeProperty(XClient c, XRequestReader r, ushort id, byte mode, byte format, uint property, uint type, uint count)
    {
        if (format is not (8 or 16 or 32))
        {
            throw new XProtocolError(XErrorCode.Value, format);
        }
        if (mode > 2)
        {
            throw new XProtocolError(XErrorCode.Value, mode);
        }
        CheckAtom(property);
        CheckAtom(type);
        long byteCount = count * (format / 8L);
        if (byteCount > r.Remaining)
        {
            throw new XProtocolError(XErrorCode.Length);
        }
        Dictionary<uint, XProperty> props = DeviceProperties(id);
        XProperty? existing = mode != 0 ? props.GetValueOrDefault(property) : null;
        if (existing is not null && (existing.Type != type || existing.Format != format))
        {
            throw new XProtocolError(XErrorCode.Match);
        }
        long total = byteCount + (existing?.Length ?? 0);
        if (total > MaxPropertyBytes)
        {
            throw new XProtocolError(XErrorCode.Alloc);
        }
        XProperty? replaced = props.GetValueOrDefault(property);
        ReleaseProperty(replaced);
        try
        {
            ChargeMemory(c, total);
        }
        catch (XProtocolError)
        {
            ChargeMemory(replaced?.ChargedTo, replaced?.Length ?? 0, force: true);
            throw;
        }
        byte[] data = ToNativeOrder(r.Bytes((int)byteCount), format, c.BigEndian);
        props[property] = existing is null ? new XProperty(type, format, data) { ChargedTo = c }
            : mode == 2 ? existing.Append(data, c)
            : new XProperty(type, format, [.. data, .. existing.Data]) { ChargedTo = c };
        SendXiPropertyEvent(id, property, what: replaced is null ? (byte)1 : (byte)2);
    }

    /// <summary>
    /// 这个客户端看不到 XTEST、收不到原始按键、不能改设备层级:非受信客户端(SECURITY),或者受
    /// <see cref="X11ServerOptions.RestrictForwardedClients" /> 限制的(经 SSH 转发进来、且开了限制)。
    /// </summary>
    private bool IsRestricted(XClient client) => client.Untrusted || (_options.RestrictForwardedClients && client.Forwarded);

    /// <summary>XI_PropertyEvent(evtype 12):给在根窗口上选了它的客户端。what:0 删除、1 新建、2 修改。</summary>
    private void SendXiPropertyEvent(ushort id, uint property, byte what)
    {
        const int xiPropertyEvent = 12;
        if (!Root.AnyXi2Selects(xiPropertyEvent))
        {
            return;
        }
        uint time = Now;
        foreach ((XClient client, (ulong master, ulong slave)) in Root.Xi2Selections)
        {
            if (!client.Closed && ((master | slave) & (1UL << xiPropertyEvent)) != 0)
            {
                client.GenericEvent(XInputMajor, xiPropertyEvent, w => w.U16(id).U32(time).U32(property).U8(what).Zero(11));
            }
        }
    }

    /// <summary>设备被删掉:它的属性一并丢掉,退还写它们的客户端的账。</summary>
    private void DropDeviceProperties(ushort id)
    {
        if (_deviceProperties.Remove(id, out Dictionary<uint, XProperty>? props))
        {
            foreach (XProperty property in props.Values)
            {
                ReleaseProperty(property);
            }
        }
    }

    private Dictionary<uint, XProperty> DeviceProperties(ushort id)
    {
        if (!IsKnownDevice(id))
        {
            throw BadDevice(id);
        }
        if (!_deviceProperties.TryGetValue(id, out Dictionary<uint, XProperty>? props))
        {
            props = new Dictionary<uint, XProperty>
            {
                [Intern("Device Enabled")] = new XProperty(XAtom.Integer, 8, [1]),
            };
            _deviceProperties[id] = props;
        }
        return props;
    }

    private void XiReplyProperty(XClient c, byte minor, ushort id, uint property, uint type, uint offset, uint length, bool xi2)
    {
        Dictionary<uint, XProperty> props = DeviceProperties(id);
        if (!props.TryGetValue(property, out XProperty? value) || (type != 0 && type != value.Type))
        {
            uint actual = value?.Type ?? 0;
            byte format = value?.Format ?? 0;
            c.Reply(minor, w => w.U32(actual).U32(0).U32(0).U8(format).U8((byte)id).Zero(10));
            return;
        }
        long start = Math.Min(4L * offset, value.Data.Length);
        long count = Math.Min(4L * length, value.Data.Length - start);
        byte[] slice = value.Data.Slice((int)start, (int)count).ToArray();
        uint after = (uint)(value.Data.Length - start - count);
        uint items = (uint)(count / (value.Format / 8));
        c.Reply(minor, w =>
        {
            // XI2:type、bytes_after、num_items、format、pad 11;XI 1.x:同样的前四项,再 deviceid。
            w.U32(value.Type).U32(after).U32(items).U8(value.Format).U8(xi2 ? (byte)0 : (byte)id).Zero(10);
            w.Bytes(slice).Pad4();
        });
    }

    // ------------------------------------------------------------------ 事件选择与抓取

    private static ulong ReadXiMask(XRequestReader r, int units)
    {
        ulong mask = 0;
        for (int i = 0; i < units * 4; i++)
        {
            byte b = r.U8();
            if (i < 8)
            {
                mask |= (ulong)b << (8 * i);   // 掩码是字节数组:第 n 位 = 第 n/8 字节的第 n%8 位
            }
        }
        return mask;
    }

    private void XiSelectEvents(XClient c, XRequestReader r)
    {
        XWindow window = Window(r.U32());
        ushort count = r.U16();
        r.Skip(2);
        // 先读完、核对完再改(出错的请求不产生效果);同一个设备出现多次时后一份为准。
        List<(ushort Device, ulong Mask)> masks = [with(count)];
        for (int i = 0; i < count; i++)
        {
            ushort device = r.U16();
            ushort units = r.U16();
            ulong mask = ReadXiMask(r, units);
            if (device > 1 && !IsKnownDevice(device))
            {
                throw BadDevice(device);
            }
            if (c.Untrusted && window.Owner is null)
            {
                // SECURITY:非受信客户端在根窗口上只能听设备 / 层级 / 设备属性的变化 —— 选键盘、指针与原始事件就能记下所有窗口里的输入。
                mask &= XiAnyDeviceEvents;
            }
            masks.Add((device, mask));
        }
        XiSelection selection = window.Xi2Selections.GetValueOrDefault(c) ?? new XiSelection();
        foreach ((ushort device, ulong mask) in masks)
        {
            if (mask == 0)
            {
                selection.ByDevice.Remove(device);
            }
            else
            {
                selection.ByDevice[device] = mask;
            }
        }
        UpdateXiSelection(window, c, selection);
    }

    /// <summary>只由键盘产生的 evtype:KeyPress、KeyRelease、FocusIn、FocusOut、RawKeyPress、RawKeyRelease。</summary>
    private const ulong XiKeyboardEvents = (1UL << 2) | (1UL << 3) | (1UL << XiFocusIn) | (1UL << XiFocusOut) | (1UL << XiRawKeyPress) | (1UL << XiRawKeyRelease);

    /// <summary>两类设备都会产生的 evtype:DeviceChanged、HierarchyChanged、PropertyEvent。其余的只由指针产生。</summary>
    private const ulong XiAnyDeviceEvents = (1UL << 1) | (1UL << XiHierarchyChanged) | (1UL << 12);

    /// <summary>
    /// 按当前的设备层级把按设备存的掩码合成主 / 从两个,存回去(全空就摘掉)。具体设备那一份只取这类设备产生得了的事件 ——
    /// 给主指针选的 KeyPress 不会让主键盘的按键也报过来。已经删掉的设备那一份丢掉。
    /// </summary>
    private void UpdateXiSelection(XWindow window, XClient client, XiSelection selection)
    {
        ulong master = 0, slave = 0;
        foreach ((ushort device, ulong mask) in selection.ByDevice.ToArray())
        {
            switch (device)
            {
                case 0:   // XIAllDevices
                    master |= mask;
                    slave |= mask;
                    break;
                case 1:   // XIAllMasterDevices
                    master |= mask;
                    break;
                default:
                    if (!_xiDevices.TryGetValue(device, out XiDevice? d))
                    {
                        selection.ByDevice.Remove(device);
                        break;
                    }
                    ulong own = mask & (d.Pointer ? ~XiKeyboardEvents : XiKeyboardEvents | XiAnyDeviceEvents);
                    if (d.Master)
                    {
                        master |= own;
                    }
                    else
                    {
                        slave |= own;
                    }
                    break;
            }
        }
        (selection.Master, selection.Slave) = (master, slave);
        if (selection.ByDevice.Count == 0)
        {
            window.Xi2Selections.Remove(client);
        }
        else
        {
            window.Xi2Selections[client] = selection;
        }
    }

    /// <summary>设备层级变了(加删主设备、挂上 / 摘下从设备):各窗口上的 XI2 选择按新的层级重算。</summary>
    private void RecomputeXiSelections()
    {
        foreach (XWindow window in _resources.Values.OfType<XWindow>().Append(Root).Distinct())
        {
            foreach ((XClient client, XiSelection selection) in window.Xi2Selections.ToArray())
            {
                UpdateXiSelection(window, client, selection);
            }
        }
    }

    private void XiGrabDevice(XClient c, XRequestReader r)
    {
        XWindow window = Window(r.U32());
        uint time = r.U32();
        uint cursorId = r.U32();
        ushort id = r.U16();
        byte grabMode = r.U8(), pairedMode = r.U8();   // XIGrabModeSync 0、XIGrabModeAsync 1
        bool ownerEvents = r.Bool();
        r.Skip(1);
        ushort units = r.U16();
        ulong mask = ReadXiMask(r, units);
        if (!IsKnownDevice(id))
        {
            throw BadDevice(id);
        }
        bool pointer = IsPointerDevice(id);
        ActiveGrab? existing = pointer ? PointerGrab : KeyboardGrab;
        // 失败的次序照 XI 2.2「XIGrabDevice」列的:AlreadyGrabbed、NotViewable、InvalidTime、Frozen。
        // 非受信客户端在键盘本来不归它时抓不了键盘(SECURITY「Keyboard Security」,同 GrabKeyboard)。
        byte status = (existing is not null && !ReferenceEquals(existing.Client, c)) || (!pointer && c.Untrusted && !KeyboardReachesUntrusted())
            ? GrabAlreadyGrabbed
            : !window.IsViewable ? GrabNotViewable
            : !TimeAcceptable(ref time, pointer ? _lastPointerGrabTime : _lastKeyboardGrabTime) ? GrabInvalidTime
            : FrozenByOther(pointer, c) ? GrabFrozen
            : GrabSuccess;
        if (status == GrabSuccess)
        {
            ActiveGrab grab = new()
            {
                Client = c,
                Window = window,
                OwnerEvents = ownerEvents,
                Xi2 = true,
                Xi2Mask = mask,
                Cursor = cursorId == 0 ? null : Use<XCursorResource>(cursorId),
                Time = time,
            };
            if (pointer)
            {
                PointerGrab = grab;
                UpdateCursor();
            }
            else
            {
                KeyboardGrab = grab;
            }
            // grab_mode 管被抓的这个设备,paired_device_mode 管与它配对的另一个。
            bool deviceSync = grabMode == 0, pairedSync = pairedMode == 0;
            ApplyGrabModes(grab, pointer ? deviceSync : pairedSync, pointer ? pairedSync : deviceSync);
        }
        c.Reply(51, w => w.U8(status).Zero(23));
    }

    private void XiPassiveGrab(XClient c, XRequestReader r, bool grab)
    {
        if (grab)
        {
            r.Skip(4);   // time
        }
        XWindow window = Window(r.U32());
        uint cursorId = grab ? r.U32() : 0;
        uint detail = r.U32();
        r.Skip(2);   // deviceid:只有一对主设备
        ushort modifierCount = r.U16();
        ushort units = grab ? r.U16() : (ushort)0;
        byte grabType = r.U8();   // 0 Button、1 Keycode、2 Enter、3 FocusIn、4 TouchBegin、5 / 6 手势(XI 2.4)
        if (grabType > 6)
        {
            throw new XProtocolError(XErrorCode.Value, grabType);
        }
        if (grabType >= 2 && detail != 0)
        {
            throw new XProtocolError(XErrorCode.Value, detail);   // Enter / FocusIn / Touch / 手势的 detail 必须是 0
        }
        if (grab)
        {
            byte grabMode = r.U8(), pairedMode = r.U8();   // Sync 0、Async 1、Touch 2
            if (grabType == 4 ? grabMode != 2 : grabMode > 1)
            {
                throw new XProtocolError(XErrorCode.Value, grabMode);   // TouchBegin 必须是 Touch,别的只能是 Sync / Async
            }
            if (pairedMode > 1)
            {
                throw new XProtocolError(XErrorCode.Value, pairedMode);
            }
            bool ownerEvents = r.Bool();
            r.Skip(2);
            ulong mask = ReadXiMask(r, units);
            List<(uint Raw, ushort Core)> modifiers = ReadXiGrabModifiers(r, modifierCount);
            List<uint> failed = [];
            if (grabType is 0 or 1)
            {
                CheckXiGrabDetail(grabType, detail);
                PassiveGrabTable list = grabType == 0 ? window.ButtonGrabs : window.KeyGrabs;
                // 与别的客户端的被动抓取有共同组合的不登记,回报给客户端(XI 2.2「XIPassiveGrabDevice」:AlreadyGrabbed);
                // Any 也算(AnyModifier 等于对所有组合各登记一次)。
                List<PassiveGrab> others = [.. list.Overlapping((int)detail).Where(g => !ReferenceEquals(g.Client, c) && !g.Client.Closed)];
                int mine = list.Count(g => ReferenceEquals(g.Client, c));
                bool deviceSync = grabMode == 0, pairedSync = pairedMode == 0;
                XCursorResource? cursor = cursorId == 0 ? null : Use<XCursorResource>(cursorId);
                foreach ((uint raw, ushort core) in modifiers)
                {
                    if (others.Any(g => g.Overlaps((int)detail, core)))
                    {
                        failed.Add(raw);
                        continue;
                    }
                    // 这个客户端自己在这些组合上的旧 XI2 抓取被取代:整个盖住的删掉,只盖住一部分的减掉那一部分。
                    int before = list.Count;
                    SubtractPassiveGrabs(list, c, xi2: true, (int)detail, core);
                    mine -= before - list.Count;
                    if (++mine > MaxPassiveGrabsPerWindow)
                    {
                        throw new XProtocolError(XErrorCode.Alloc);   // 一个客户端在一个窗口上登记这么多被动抓取,不会是真实程序
                    }
                    // 按钮抓取:grab_mode 管指针、paired 管键盘;按键抓取反过来。
                    list.Add(new PassiveGrab(c, (int)detail, core, ownerEvents, 0, null, cursor, Xi2: true, Xi2Mask: mask,
                        PointerSync: grabType == 0 ? deviceSync : pairedSync, KeyboardSync: grabType == 0 ? pairedSync : deviceSync));
                }
            }
            else
            {
                // Enter / FocusIn / Touch 类被动抓取不支持:按规范以「这些组合都没抓成」回应。
                failed.AddRange(modifiers.Select(m => m.Raw));
            }
            c.Reply(54, w =>
            {
                w.U16((ushort)failed.Count).Zero(22);
                foreach (uint mods in failed)
                {
                    w.U32(mods).U8(1).Zero(3);   // GRABMODIFIERINFO:status AlreadyGrabbed
                }
            });
        }
        else
        {
            r.Skip(3);
            List<(uint Raw, ushort Core)> modifiers = ReadXiGrabModifiers(r, modifierCount);
            if (grabType is 0 or 1)
            {
                // AnyModifier / AnyButton 的抓取只减掉这几个组合,其余照样有效(原先不拆分)。
                PassiveGrabTable list = grabType == 0 ? window.ButtonGrabs : window.KeyGrabs;
                foreach ((_, ushort core) in modifiers)
                {
                    SubtractPassiveGrabs(list, c, xi2: true, (int)detail, core);
                }
            }
        }
    }

    /// <summary>一个客户端在一个窗口上最多登记这么多个(按钮或按键)被动抓取。</summary>
    internal const int MaxPassiveGrabsPerWindow = 4096;

    /// <summary>
    /// 被动抓取的修饰组合:XIAnyModifier(0x80000000)换成核心的 AnyModifier(0x8000);其余只认 8 个核心修饰位,
    /// 带了别的位回 BadValue —— 原先直接截成低 8 位,0x100 会变成「不带修饰」。
    /// </summary>
    private static List<(uint Raw, ushort Core)> ReadXiGrabModifiers(XRequestReader r, int count)
    {
        if (count * 4L > r.Remaining)
        {
            throw new XProtocolError(XErrorCode.Length);
        }
        List<(uint, ushort)> modifiers = [with(count)];
        for (int i = 0; i < count; i++)
        {
            uint mods = r.U32();
            if (mods != 0x80000000 && (mods & ~0xFFu) != 0)
            {
                throw new XProtocolError(XErrorCode.Value, mods);
            }
            modifiers.Add((mods, mods == 0x80000000 ? (ushort)0x8000 : (ushort)mods));
        }
        return modifiers;
    }

    /// <summary>被动抓取的 detail:按钮 0(XIAnyButton)–255,键码 0(XIAnyKeycode)或 8–255;其余 BadValue。</summary>
    private static void CheckXiGrabDetail(byte grabType, uint detail)
    {
        if (detail > 255 || (grabType == 1 && detail is not 0 and < Keymap.MinKeycode))
        {
            throw new XProtocolError(XErrorCode.Value, detail);
        }
    }

    // ------------------------------------------------------------------ 事件编码

    private void WriteXiModifiers(XWriter w)
    {
        w.U32(_baseMods).U32(_latchedMods).U32(_lockedMods).U32((uint)(_modifiers & 0xFF));
        w.U8(0).U8(0).U8(0).U8(0);   // 组:只有一组
    }

    private void WriteXiButtons(XWriter w, int units) => WriteButtonMask(w, units);

    private void WriteButtonMask(XWriter w, int units)
    {
        for (int i = 0; i < units * 4; i++)
        {
            w.U8(i < _buttonsDown.Length ? _buttonsDown[i] : (byte)0);
        }
    }

    /// <summary>事件窗口里、位于通往源窗口路径上的那个子窗口(源就是事件窗口时为 None)。</summary>
    private static uint ChildOnPath(XWindow eventWindow, XWindow source)
    {
        for (XWindow? w = source; w is not null && !ReferenceEquals(w, eventWindow); w = w.Parent)
        {
            if (ReferenceEquals(w.Parent, eventWindow))
            {
                return w.Id;
            }
        }
        return 0;
    }

    private uint ChildTowardPointer(XWindow window) =>
        ReferenceEquals(_pointerWindow, window) || !_pointerWindow.IsDescendantOf(window) ? 0 : ChildOnPath(window, _pointerWindow);

    /// <summary>XI2 的 DeviceEvent(KeyPress / KeyRelease / ButtonPress / ButtonRelease / Motion)。</summary>
    private void SendXi2DeviceEvent(XClient client, int evtype, byte detail, XWindow eventWindow, XWindow source, bool slave, uint flags = 0)
    {
        bool key = evtype is XEventCode.KeyPress or XEventCode.KeyRelease;
        ushort sourceId = key ? XiSlaveKeyboard : XiSlavePointer;
        ushort device = slave || IsFloating(!key) ? sourceId : MasterOf(!key);
        (int ex, int ey) = eventWindow.AbsoluteInner();
        uint child = ChildOnPath(eventWindow, source);
        int px = _pointerX, py = _pointerY;
        uint time = Now;
        if (!key)
        {
            flags |= _xi2PointerFlags;
        }
        // 滚动的 Motion 还带滚动轴:轴 2(水平)、3(垂直),值是累计的滚动量(XI 2.1「Smooth scrolling」)。
        (bool scrollX, bool scrollY) = evtype == XEventCode.MotionNotify ? _xi2ScrollAxes : default;
        (double valueX, double valueY) = _scrollValue;
        client.GenericEvent(XInputMajor, (ushort)evtype, w =>
        {
            w.U16(device).U32(time).U32(detail).U32(Root.Id).U32(eventWindow.Id).U32(child)
                .I32(Fp1616(px)).I32(Fp1616(py)).I32(Fp1616(px - ex)).I32(Fp1616(py - ey))
                .U16(1).U16(key ? (ushort)0 : (ushort)1).U16(sourceId).Zero(2).U32(flags);
            WriteXiModifiers(w);
            WriteButtonMask(w, 1);
            if (!key)
            {
                w.U32(0x3u | (scrollX ? 0x4u : 0) | (scrollY ? 0x8u : 0));   // 轴 0、1(还有滚动轴)
                w.I32(px).U32(0).I32(py).U32(0);                             // FP3232 值,按轴号从小到大
                if (scrollX)
                {
                    WriteFp3232(w, valueX);
                }
                if (scrollY)
                {
                    WriteFp3232(w, valueY);
                }
            }
        });
    }

    /// <summary>
    /// XI2 的 Enter / Leave / FocusIn / FocusOut(只发给在该窗口上选了它的客户端,不传播)。<paramref name="only" /> 给了就只发给它;
    /// <paramref name="force" /> 时它没在这个窗口上选也发(抓取的事件掩码里有)。
    /// </summary>
    private void SendXi2Crossing(int evtype, XWindow window, byte detail, byte mode = 0, uint child = 0, XClient? only = null, bool force = false)
    {
        if (!force && !window.AnyXi2Selects(evtype))
        {
            return;
        }
        bool focusEvent = evtype is XiFocusIn or XiFocusOut;
        ushort sourceId = focusEvent ? XiSlaveKeyboard : XiSlavePointer;
        ushort attached = MasterOf(!focusEvent);
        ushort device = attached != 0 ? attached : sourceId;
        (int ex, int ey) = window.AbsoluteInner();
        int px = _pointerX, py = _pointerY;
        bool focus = _focus is { } f && (ReferenceEquals(f, window) || window.IsDescendantOf(f));
        uint time = Now;
        // 指针每跨一个窗口都走这里:直接遍历选择表,不为每个窗口建 LINQ 管道再拷成数组(发事件不改这张表)。
        if (force && only is not null)
        {
            Send(only);
            return;
        }
        ulong bit = 1UL << evtype;
        foreach ((XClient client, XiSelection selection) in window.Xi2Selections)
        {
            if (((selection.Master | selection.Slave) & bit) != 0 && (only is null || ReferenceEquals(client, only)))
            {
                Send(client);
            }
        }

        void Send(XClient client)
        {
            if (client.Closed)
            {
                return;
            }
            client.GenericEvent(XInputMajor, (ushort)evtype, w =>
            {
                w.U16(device).U32(time).U16(sourceId).U8(mode).U8(detail).U32(Root.Id).U32(window.Id).U32(child)
                    .I32(Fp1616(px)).I32(Fp1616(py)).I32(Fp1616(px - ex)).I32(Fp1616(py - ey))
                    .Bool(true).Bool(focus).U16(1);
                WriteXiModifiers(w);
                WriteButtonMask(w, 1);
            });
        }
    }

    /// <summary>
    /// XI2 的原始事件:发给在根窗口上选了它的客户端,不受焦点与抓取影响。移动事件的轴 0、1 是设备的位置 (<paramref name="x" />, <paramref name="y" />)
    /// —— 与 XIQueryDevice 的声明(Abs X / Abs Y、Absolute)一致;没有加速,处理后的值与原始值相同。
    /// </summary>
    private void SendRawEvent(int evtype, uint detail, int x, int y, uint flags = 0)
    {
        if (!Root.AnyXi2Selects(evtype))
        {
            return;
        }
        bool key = evtype is XiRawKeyPress or XiRawKeyRelease;
        bool motion = evtype == XiRawMotion;
        ushort sourceId = key ? XiSlaveKeyboard : XiSlavePointer;
        uint time = Now;
        ulong bit = 1UL << evtype;
        foreach ((XClient client, (ulong master, ulong slave)) in Root.Xi2Selections)
        {
            if (client.Closed || ((master | slave) & bit) == 0 || (key && IsRestricted(client)))
            {
                continue;
            }
            // 从设备与主设备各一份(选 XIAllDevices 的两份都收);设备浮动时主设备不产生,只选了主设备的照旧收从设备那份。
            bool floating = IsFloating(!key);
            if ((slave & bit) != 0 || floating)
            {
                Send(client, sourceId);
            }
            if ((master & bit) != 0 && !floating)
            {
                Send(client, MasterOf(!key));
            }
        }

        void Send(XClient client, ushort device) => client.GenericEvent(XInputMajor, (ushort)evtype, w =>
        {
            w.U16(device).U32(time).U32(detail).U16(sourceId).U16(motion ? (ushort)1 : (ushort)0).U32(flags).Zero(4);
            if (motion)
            {
                w.U32(0x3);
                w.I32(x).U32(0).I32(y).U32(0);   // 处理后的值
                w.I32(x).U32(0).I32(y).U32(0);   // 原始值
            }
        });
    }

    /// <summary>
    /// 滚动的原始事件:RawMotion,只带滚动轴(2 水平、3 垂直),值是这一次的增量(原始事件报设备给的数据,相对轴就是增量);
    /// 处理后的值与原始值相同。<paramref name="emulated" /> 时是从按钮 4–7 模拟出来的(带 PointerEmulated)。
    /// </summary>
    private void SendRawScroll(double dx, double dy, bool emulated)
    {
        if (!Root.AnyXi2Selects(XiRawMotion))
        {
            return;
        }
        bool horizontal = dx != 0, vertical = dy != 0;
        uint mask = (horizontal ? 0x4u : 0) | (vertical ? 0x8u : 0);
        uint flags = emulated ? XiPointerEmulatedFlag : 0;
        uint time = Now;
        ulong bit = 1UL << XiRawMotion;
        foreach ((XClient client, (ulong master, ulong slave)) in Root.Xi2Selections)
        {
            if (client.Closed || ((master | slave) & bit) == 0)
            {
                continue;
            }
            bool floating = IsFloating(pointer: true);
            if ((slave & bit) != 0 || floating)
            {
                Send(client, XiSlavePointer);
            }
            if ((master & bit) != 0 && !floating)
            {
                Send(client, MasterOf(pointer: true));
            }
        }

        void Send(XClient client, ushort device) => client.GenericEvent(XInputMajor, XiRawMotion, w =>
        {
            w.U16(device).U32(time).U32(0).U16(XiSlavePointer).U16(1).U32(flags).Zero(4);
            w.U32(mask);
            for (int pass = 0; pass < 2; pass++)   // 处理后的值,然后原始值
            {
                if (horizontal)
                {
                    WriteFp3232(w, dx);
                }
                if (vertical)
                {
                    WriteFp3232(w, dy);
                }
            }
        });
    }

    /// <summary>客户端断开:摘掉它在各窗口上的 XI2 事件选择。</summary>
    private void CleanupXInput(XClient client)
    {
        foreach (XWindow window in _resources.Values.OfType<XWindow>())
        {
            window.Xi2Selections.Remove(client);
        }
        Root.Xi2Selections.Remove(client);
    }
}
