// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Input Device Extension Protocol, Version 2.2(XI2)—— §2「Notations」(FP1616、FP3232、SETofEVENTMASK)、
//   §4「Devices」(主 / 从设备、XIAllDevices 0 / XIAllMasterDevices 1、设备类:Key 0、Button 1、Valuator 2)、
//   §6「Requests」(XIQueryPointer 40 … XIGetSelectedEvents 60、XIBarrierReleasePointer 61)、
//   §7「Events」(经 Generic Event Extension 发出:DeviceEvent —— KeyPress 2 / KeyRelease 3 / ButtonPress 4 /
//   ButtonRelease 5 / Motion 6;EnterLeave —— Enter 7 / Leave 8 / FocusIn 9 / FocusOut 10;
//   RawEvent —— RawKeyPress 13 … RawMotion 17,只发给在根窗口上选了它的客户端)
//   X Input Device Extension Protocol, Version 1.5 —— GetExtensionVersion 1、ListInputDevices 2、OpenDevice 3、
//   CloseDevice 4、SelectExtensionEvent 6、GetSelectedExtensionEvents 7、GetDeviceFocus 20、SetDeviceFocus 21、
//   GetDeviceKeyMapping 24、GetDeviceModifierMapping 26、GetDeviceButtonMapping 28、QueryDeviceState 30、
//   DeviceBell 32、ListDeviceProperties 36、GetDeviceProperty 39;错误 BadDevice / BadEvent / BadMode / DeviceBusy / BadClass
//
//   设备:主指针 2、主键盘 3,各挂一个从设备(4、5)。宿主注入的输入都来自这两个从设备。
//   XI2 事件与核心事件走同一条传播路径(同一个窗口上,选了核心的收核心、选了 XI2 的收 XI2);抓取也可以是 XI2 的。
//   XI 1.x 的设备事件(DeviceKeyPress 之类)不产生 —— 现代客户端都走 XI2;XI 1.x 的请求只回答查询。

using VelaShell.XServer.Input;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer.Server;

public sealed partial class X11Server
{
    private const byte XInputMajor = 145;
    private const byte XInputEventBase = 74;   // XI 1.x 的 17 个事件
    private const byte XInputErrorBase = 144;  // BadDevice +0、BadEvent +1、BadMode +2、DeviceBusy +3、BadClass +4

    private const ushort XiMasterPointer = 2, XiMasterKeyboard = 3, XiSlavePointer = 4, XiSlaveKeyboard = 5;
    private const int XiEnter = 7, XiLeave = 8, XiFocusIn = 9, XiFocusOut = 10;
    private const int XiRawKeyPress = 13, XiRawKeyRelease = 14, XiRawButtonPress = 15, XiRawButtonRelease = 16, XiRawMotion = 17;
    private const int XiButtonCount = 9;

    private static readonly string[] XiButtonLabels =
    [
        "Button Left", "Button Middle", "Button Right", "Button Wheel Up", "Button Wheel Down",
        "Button Horiz Wheel Left", "Button Horiz Wheel Right", "Button Side", "Button Extra",
    ];

    /// <summary>设备属性(XIGetProperty / GetDeviceProperty):设备 → 原子 → 值。</summary>
    private readonly Dictionary<ushort, Dictionary<uint, XProperty>> _deviceProperties = [];

    private static bool IsPointerDevice(ushort id) => id is XiMasterPointer or XiSlavePointer;

    private static bool IsKnownDevice(ushort id) => id is >= XiMasterPointer and <= XiSlaveKeyboard;

    private static XProtocolError BadDevice(uint id) => new((XErrorCode)XInputErrorBase, id);

    private static int Fp1616(int value) => value << 16;

    private void XInput(XClient c, XRequestReader r)
    {
        byte minor = r.Data;
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
                    if (id is not ((byte)XiSlavePointer or (byte)XiSlaveKeyboard))
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
                    uint time = Now;
                    c.Reply(minor, w => w.U32(focus).U32(time).U8(_focusRevertTo).Zero(15));
                    break;
                }
            case 21:  // SetDeviceFocus
                {
                    uint focusId = r.U32();
                    r.Skip(4);
                    byte revertTo = r.U8();
                    SetFocus(focusId switch { 0 => null, 1 => Root, _ => Window(focusId) }, revertTo);
                    break;
                }
            case 24:  // GetDeviceKeyMapping
                {
                    r.U8();
                    byte first = r.U8(), count = r.U8();
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
            case 28:  // GetDeviceButtonMapping:恒等映射
                c.Reply(minor, w =>
                {
                    w.U8(XiButtonCount).Zero(23);
                    for (int b = 1; b <= XiButtonCount; b++)
                    {
                        w.U8((byte)b);
                    }
                    w.Pad4();
                });
                break;
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
                            w.U8(2).U8(12).U8(2).U8(1).I32(Math.Max(0, _pointerX)).I32(Math.Max(0, _pointerY));   // ValuatorState
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
                _host.Bell(r.I8() is var p && p < 0 ? 50 : p);
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
                    (int wx, int wy) = window.AbsoluteInner();
                    uint child = ChildTowardPointer(window);
                    int px = Math.Max(0, _pointerX), py = Math.Max(0, _pointerY);
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
                    if (src != 0)
                    {
                        (int sx, int sy) = Window(src).AbsoluteInner();
                        int w = srcW == 0 ? int.MaxValue : srcW, h = srcH == 0 ? int.MaxValue : srcH;
                        if (_pointerX < sx + srcX || _pointerY < sy + srcY || _pointerX >= sx + srcX + w || _pointerY >= sy + srcY + h)
                        {
                            break;   // 指针不在源矩形里:什么也不做(同核心 WarpPointer)
                        }
                    }
                    if (dst != 0)
                    {
                        (int dx, int dy) = Window(dst).AbsoluteInner();
                        MovePointer(dx + dstX, dy + dstY);
                    }
                    else
                    {
                        MovePointer(Math.Max(0, _pointerX) + dstX, Math.Max(0, _pointerY) + dstY);
                    }
                    break;
                }
            case 42:  // XIChangeCursor:同核心的窗口光标
                {
                    XWindow window = Window(r.U32());
                    uint cursor = r.U32();
                    window.Cursor = cursor == 0 ? null : Lookup<XCursor>(cursor) ?? throw new XProtocolError(XErrorCode.Cursor, cursor);
                    UpdateCursor();
                    break;
                }
            case 43:  // XIChangeHierarchy:设备拓扑固定(一对主设备),不支持增删主设备
                throw new XProtocolError(XErrorCode.Implementation);
            case 44:  // XISetClientPointer:只有一个主指针
                break;
            case 45:  // XIGetClientPointer
                c.Reply(minor, w => w.Bool(true).Zero(1).U16(XiMasterPointer).Zero(20));
                break;
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
                        0 => [XiMasterPointer, XiMasterKeyboard, XiSlavePointer, XiSlaveKeyboard],
                        1 => [XiMasterPointer, XiMasterKeyboard],
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
                    uint focusId = r.U32();
                    SetFocus(focusId == 0 ? null : Window(focusId), 1);
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
                    r.Skip(4);
                    ushort id = r.U16();
                    if (IsPointerDevice(id))
                    {
                        if (ReferenceEquals(_pointerGrab?.Client, c))
                        {
                            _pointerGrab = null;
                            UpdateCursor();
                        }
                    }
                    else if (ReferenceEquals(_keyboardGrab?.Client, c))
                    {
                        _keyboardGrab = null;
                    }
                    break;
                }
            case 53:  // XIAllowEvents:只实现异步抓取(同核心 AllowEvents)
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
                    if (format is not (8 or 16 or 32) || mode > 2)
                    {
                        throw new XProtocolError(XErrorCode.Value, format);
                    }
                    byte[] data = r.Bytes((int)Math.Min(count * (format / 8L), r.Remaining));
                    Dictionary<uint, XProperty> props = DeviceProperties(id);
                    if (mode != 0 && props.TryGetValue(property, out XProperty? existing) && existing.Type == type && existing.Format == format)
                    {
                        data = mode == 1 ? [.. data, .. existing.Data] : [.. existing.Data, .. data];
                    }
                    props[property] = new XProperty(type, format, data);
                    break;
                }
            case 58:  // XIDeleteProperty
                {
                    ushort id = r.U16();
                    r.Skip(2);
                    DeviceProperties(id).Remove(r.U32());
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
                    List<(ushort Device, ulong Mask)> masks = [];
                    if (window.Xi2Selections.TryGetValue(c, out var selected))
                    {
                        if (selected.Master != 0)
                        {
                            masks.Add((1, selected.Master));
                        }
                        if (selected.Slave != 0)
                        {
                            masks.Add((0, selected.Slave));
                        }
                    }
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
        (ushort use, ushort attachment, string name) = id switch
        {
            XiMasterPointer => ((ushort)1, XiMasterKeyboard, "Virtual core pointer"),
            XiMasterKeyboard => ((ushort)2, XiMasterPointer, "Virtual core keyboard"),
            XiSlavePointer => ((ushort)3, XiMasterPointer, "VelaShell pointer"),
            _ => ((ushort)4, XiMasterKeyboard, "VelaShell keyboard"),
        };
        byte[] nameBytes = XWire.Latin1.GetBytes(name);
        bool pointer = IsPointerDevice(id);
        ushort source = pointer ? XiSlavePointer : XiSlaveKeyboard;
        w.U16(id).U16(use).U16(attachment).U16(pointer ? (ushort)3 : (ushort)1).U16((ushort)nameBytes.Length).Bool(true).Zero(1)
            .Bytes(nameBytes).Pad4();
        if (pointer)
        {
            // ButtonClass:type 1、len、sourceid、num_buttons、state(1 个 32 位)、labels
            w.U16(1).U16(3 + XiButtonCount).U16(source).U16(XiButtonCount);
            WriteButtonMask(w, 1);
            foreach (string label in XiButtonLabels)
            {
                w.U32(Intern(label));
            }
            WriteValuatorClass(w, source, 0, "Abs X", Root.Width, Math.Max(0, _pointerX));
            WriteValuatorClass(w, source, 1, "Abs Y", Root.Height, Math.Max(0, _pointerY));
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

    private void XiListInputDevices(XClient c)
    {
        // XI 1.x 的视角:核心指针(use 0)、核心键盘(use 1),再加两个从设备作为扩展设备。
        (byte Id, byte Use, string Name, bool Pointer)[] devices =
        [
            ((byte)XiMasterPointer, 0, "Virtual core pointer", true),
            ((byte)XiMasterKeyboard, 1, "Virtual core keyboard", false),
            ((byte)XiSlavePointer, 4, "VelaShell pointer", true),
            ((byte)XiSlaveKeyboard, 3, "VelaShell keyboard", false),
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
        byte[] slice = value.Data.AsSpan((int)start, (int)count).ToArray();
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
        (ulong master, ulong slave) = window.Xi2Selections.GetValueOrDefault(c);
        for (int i = 0; i < count; i++)
        {
            ushort device = r.U16();
            ushort units = r.U16();
            ulong mask = ReadXiMask(r, units);
            switch (device)
            {
                case 0:   // XIAllDevices
                    master = mask;
                    slave = mask;
                    break;
                case 1:   // XIAllMasterDevices
                case XiMasterPointer:
                case XiMasterKeyboard:
                    master = mask;
                    break;
                case XiSlavePointer:
                case XiSlaveKeyboard:
                    slave = mask;
                    break;
                default:
                    throw BadDevice(device);
            }
        }
        if (master == 0 && slave == 0)
        {
            window.Xi2Selections.Remove(c);
        }
        else
        {
            window.Xi2Selections[c] = (master, slave);
        }
    }

    private void XiGrabDevice(XClient c, XRequestReader r)
    {
        XWindow window = Window(r.U32());
        r.Skip(4);   // time
        uint cursorId = r.U32();
        ushort id = r.U16();
        r.Skip(2);   // grab_mode、paired_device_mode:只有异步
        bool ownerEvents = r.Bool();
        r.Skip(1);
        ushort units = r.U16();
        ulong mask = ReadXiMask(r, units);
        if (!IsKnownDevice(id))
        {
            throw BadDevice(id);
        }
        bool pointer = IsPointerDevice(id);
        ActiveGrab? existing = pointer ? _pointerGrab : _keyboardGrab;
        byte status = existing is not null && !ReferenceEquals(existing.Client, c) ? (byte)1   // AlreadyGrabbed
            : !window.IsViewable ? (byte)3                                                      // GrabNotViewable
            : (byte)0;
        if (status == 0)
        {
            ActiveGrab grab = new()
            {
                Client = c,
                Window = window,
                OwnerEvents = ownerEvents,
                Xi2 = true,
                Xi2Mask = mask,
                Cursor = cursorId == 0 ? null : Lookup<XCursor>(cursorId),
            };
            if (pointer)
            {
                _pointerGrab = grab;
                UpdateCursor();
            }
            else
            {
                _keyboardGrab = grab;
            }
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
        byte grabType = r.U8();   // 0 Button、1 Keycode、2 Enter、3 FocusIn、4 TouchBegin
        if (grab)
        {
            r.Skip(2);            // grab_mode、paired_device_mode
            bool ownerEvents = r.Bool();
            r.Skip(2);
            ulong mask = ReadXiMask(r, units);
            List<uint> modifiers = [];
            for (int i = 0; i < modifierCount; i++)
            {
                modifiers.Add(r.U32());
            }
            if (grabType is 0 or 1)
            {
                List<PassiveGrab> list = grabType == 0 ? window.ButtonGrabs : window.KeyGrabs;
                foreach (uint mods in modifiers)
                {
                    ushort core = mods == 0x80000000 ? (ushort)0x8000 : (ushort)(mods & 0xFF);   // XIAnyModifier
                    list.RemoveAll(g => ReferenceEquals(g.Client, c) && g.Detail == (int)detail && g.Modifiers == core);
                    list.Add(new PassiveGrab(c, (int)detail, core, ownerEvents, 0, null,
                        cursorId == 0 ? null : Lookup<XCursor>(cursorId), Xi2: true, Xi2Mask: mask));
                }
            }
            // Enter / FocusIn / Touch 类被动抓取不支持;规范允许以「全部修饰组合都失败」回应 —— 这里回空列表,表示没有冲突。
            c.Reply(54, w => w.U16(0).Zero(22));
        }
        else
        {
            r.Skip(3);
            for (int i = 0; i < modifierCount; i++)
            {
                uint mods = r.U32();
                ushort core = mods == 0x80000000 ? (ushort)0x8000 : (ushort)(mods & 0xFF);
                List<PassiveGrab> list = grabType == 0 ? window.ButtonGrabs : window.KeyGrabs;
                list.RemoveAll(g => ReferenceEquals(g.Client, c) && g.Xi2 && g.Detail == (int)detail && g.Modifiers == core);
            }
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
    private void SendXi2DeviceEvent(XClient client, int evtype, byte detail, XWindow eventWindow, XWindow source, bool slave)
    {
        bool key = evtype is XEventCode.KeyPress or XEventCode.KeyRelease;
        ushort device = slave ? (key ? XiSlaveKeyboard : XiSlavePointer) : (key ? XiMasterKeyboard : XiMasterPointer);
        ushort sourceId = key ? XiSlaveKeyboard : XiSlavePointer;
        (int ex, int ey) = eventWindow.AbsoluteInner();
        uint child = ChildOnPath(eventWindow, source);
        int px = Math.Max(0, _pointerX), py = Math.Max(0, _pointerY);
        uint time = Now;
        client.GenericEvent(XInputMajor, (ushort)evtype, w =>
        {
            w.U16(device).U32(time).U32(detail).U32(Root.Id).U32(eventWindow.Id).U32(child)
                .I32(Fp1616(px)).I32(Fp1616(py)).I32(Fp1616(px - ex)).I32(Fp1616(py - ey))
                .U16(1).U16(key ? (ushort)0 : (ushort)1).U16(sourceId).Zero(2).U32(0);
            WriteXiModifiers(w);
            WriteButtonMask(w, 1);
            if (!key)
            {
                w.U32(0x3);                                    // 轴 0、1
                w.I32(px).U32(0).I32(py).U32(0);               // FP3232 值
            }
        });
    }

    /// <summary>XI2 的 Enter / Leave / FocusIn / FocusOut(只发给在该窗口上选了它的客户端,不传播)。</summary>
    private void SendXi2Crossing(int evtype, XWindow window, byte detail, byte mode = 0, uint child = 0)
    {
        if (!window.AnyXi2Selects(evtype))
        {
            return;
        }
        bool focusEvent = evtype is XiFocusIn or XiFocusOut;
        ushort device = focusEvent ? XiMasterKeyboard : XiMasterPointer;
        ushort sourceId = focusEvent ? XiSlaveKeyboard : XiSlavePointer;
        (int ex, int ey) = window.AbsoluteInner();
        int px = Math.Max(0, _pointerX), py = Math.Max(0, _pointerY);
        bool focus = _focus is { } f && (ReferenceEquals(f, window) || window.IsDescendantOf(f));
        uint time = Now;
        foreach ((XClient client, (ulong master, ulong slave)) in window.Xi2Selections)
        {
            if (client.Closed || ((master | slave) & (1UL << evtype)) == 0)
            {
                continue;
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

    /// <summary>XI2 的原始事件:发给在根窗口上选了它的客户端,不受焦点与抓取影响。</summary>
    private void SendRawEvent(int evtype, uint detail, int dx, int dy)
    {
        if (!Root.AnyXi2Selects(evtype))
        {
            return;
        }
        bool key = evtype is XiRawKeyPress or XiRawKeyRelease;
        bool motion = evtype == XiRawMotion;
        ushort sourceId = key ? XiSlaveKeyboard : XiSlavePointer;
        uint time = Now;
        foreach ((XClient client, (ulong master, ulong slave)) in Root.Xi2Selections)
        {
            if (client.Closed || ((master | slave) & (1UL << evtype)) == 0)
            {
                continue;
            }
            ushort device = (master & (1UL << evtype)) != 0 ? (key ? XiMasterKeyboard : XiMasterPointer) : sourceId;
            client.GenericEvent(XInputMajor, (ushort)evtype, w =>
            {
                w.U16(device).U32(time).U32(detail).U16(sourceId).U16(motion ? (ushort)1 : (ushort)0).U32(0).Zero(4);
                if (motion)
                {
                    w.U32(0x3);
                    w.I32(dx).U32(0).I32(dy).U32(0);   // 处理后的值
                    w.I32(dx).U32(0).I32(dy).U32(0);   // 原始值
                }
            });
        }
    }

    private void CleanupXInput(XClient client)
    {
        foreach (XWindow window in _resources.Values.OfType<XWindow>())
        {
            window.Xi2Selections.Remove(client);
        }
        Root.Xi2Selections.Remove(client);
    }
}
