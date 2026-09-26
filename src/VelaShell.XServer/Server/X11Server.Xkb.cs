// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   The X Keyboard Extension: Protocol Specification, Version 1.0 ——
//   §2「Keyboard State」(base / latched / locked 修饰与组、有效状态、兼容状态);
//   §7「Key Types」(四个规范类型 ONE_LEVEL、TWO_LEVEL、ALPHABETIC、KEYPAD 占下标 0–3,KTMAPENTRY;
//   再加两个四级类型 FOUR_LEVEL、FOUR_LEVEL_ALPHABETIC 放 AltGr 层,第三级由 Mod5(ISO_Level3_Shift 所在的修饰位)选);
//   §8「Key Symbol Map」(KEYSYMMAP:每组的类型下标、组数、宽度、键值)、§9「Key Actions」(SetMods / LockMods);
//   §12「Symbolic Names」、§13「Indicators」、§10「Keyboard Controls」;
//   §17「Interactions Between XKB and the Core Protocol」(由核心键位表推出 XKB 键位表的规则);
//   §16 协议请求:UseExtension 0、SelectEvents 1、Bell 3、GetState 4、LatchLockState 5、GetControls 6、SetControls 7、
//   GetMap 8、SetMap 9、GetCompatMap 10、SetCompatMap 11、GetIndicatorState 12、GetIndicatorMap 13、SetIndicatorMap 14、
//   GetNamedIndicator 15、SetNamedIndicator 16、GetNames 17、SetNames 18、GetGeometry 19、SetGeometry 20、
//   PerClientFlags 21、ListComponents 22、GetKbdByName 23、GetDeviceInfo 24、SetDeviceInfo 25、SetDebuggingFlags 101;
//   附录 C 事件编码(StateNotify、MapNotify、IndicatorStateNotify —— 所有 XKB 事件共用一个事件码,第 2 字节是子类型)
//
//   XKB 键位表始终由核心键位表推出(每个键一组、按键值挑规范类型),所以 xmodmap 之类改核心表的做法与 XKB 客户端看到的
//   永远一致;SetMap 上传的键值与修饰键映射换成核心键位表写回(X11Server.XkbSetMap.cs),类型、动作等由服务端照旧推出;
//   GetKbdByName 要服务端自己按规则编译键位表(需要 xkeyboard-config 的数据),回「没装载」—— 客户端改用
//   setxkbmap -print | xkbcomp - $DISPLAY,在客户端编译好再经 SetMap 上传。
//   Xlib 的 XLookupString、xkbcommon-x11(Qt、GTK4、xdotool 的 libxdo)都从这里取键位表,
//   所以回复里各段的计数、宽度、类型下标必须彼此自洽。

using VelaShell.XServer.Input;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    private const byte XkbDeviceId = 3;       // 与 XInput 的主键盘同号
    private const ushort XkbUseCoreKbd = 0x100;

    private const byte XkbMapNotify = 1, XkbStateNotify = 2, XkbIndicatorStateNotify = 4;

    // 四个规范类型 + 两个四级类型:(名字, 修饰掩码, 级数, 映射表 (修饰, 级), 级名)。
    // 四级类型的第三 / 四级由 Mod5 选 —— 宿主把 AltGr(右 Alt)设成 ISO_Level3_Shift 并放进 Mod5(见 SetModifierMapping)。
    private static readonly (string Name, byte Mask, byte Levels, (byte Mods, byte Level)[] Map, string[] LevelNames)[] XkbTypes =
    [
        ("ONE_LEVEL", 0, 1, [], ["Any"]),
        ("TWO_LEVEL", 0x01, 2, [(0x01, 1)], ["Base", "Shift"]),
        ("ALPHABETIC", 0x03, 2, [(0x01, 1), (0x02, 1)], ["Base", "Caps"]),
        ("KEYPAD", 0x11, 2, [(0x01, 1), (0x10, 1)], ["Base", "Number"]),
        ("FOUR_LEVEL", 0x81, 4, [(0x01, 1), (0x80, 2), (0x81, 3)], ["Base", "Shift", "Alt Base", "Shift Alt"]),
        ("FOUR_LEVEL_ALPHABETIC", 0x83, 4, [(0x01, 1), (0x02, 1), (0x80, 2), (0x81, 3), (0x82, 3)], ["Base", "Caps", "Alt Base", "Shift Alt"]),
    ];

    private const byte XkbFourLevel = 4, XkbFourLevelAlphabetic = 5;

    // 虚拟修饰:(名字, 绑到的真修饰)
    private static readonly (string Name, byte RealMods)[] XkbVirtualMods =
    [
        ("NumLock", 0x10), ("Alt", 0x08), ("LevelThree", 0x80), ("Super", 0x40), ("Meta", 0x08),
    ];

    // SymInterpret:(键值 0 = 任意, 修饰, 匹配方式 AnyOfOrNone 1 / AnyOf 2, 虚拟修饰 0xFF = 无, 动作类型, 动作 flags, 动作修饰)
    private static readonly (uint Sym, byte Mods, byte Match, byte VMod, byte ActionType, byte ActionFlags, byte ActionMods)[] XkbSymInterprets =
    [
        (0xffe5, 0x02, 1, 0xFF, 3, 0, 0x02),   // Caps_Lock + AnyOfOrNone(Lock) → LockMods(Lock)
        (0xff7f, 0xFF, 2, 0, 3, 4, 0),         // Num_Lock + AnyOf(all),虚拟修饰 NumLock → LockMods(modMapMods)
        (0, 0xFF, 2, 0xFF, 1, 5, 0),           // Any + AnyOf(all) → SetMods(modMapMods, clearLocks)
    ];

    private static readonly string[] XkbIndicatorNames = ["Caps Lock", "Num Lock", "Scroll Lock"];

    /// <summary>每个客户端经 SelectEvents 选的:事件类型 → 细节掩码。</summary>
    private readonly Dictionary<XClient, uint[]> _xkbSelections = [];

    /// <summary>选了 DetectableAutoRepeat 的客户端(PerClientFlags)。</summary>
    private readonly HashSet<XClient> _xkbDetectableRepeat = [];

    private void Xkb(XClient c, XRequestReader r)
    {
        if (r.Data is not 0 and not 101)
        {
            CheckXkbDevice(r);   // 除 UseExtension 与 SetDebuggingFlags 外,第一个字段都是 deviceSpec
        }
        switch (r.Data)
        {
            case 0:   // UseExtension
                c.Reply(1, w => w.U16(1).U16(0).Zero(20));
                break;
            case 1:   // SelectEvents
                XkbSelectEvents(c, r);
                break;
            case 3:   // Bell
                {
                    r.Skip(4);   // bellClass、bellID
                    sbyte percent = r.I8();
                    r.Skip(1);   // forceSound
                    bool eventOnly = r.Bool();
                    if (!eventOnly)
                    {
                        RingBell(percent);
                    }
                    break;
                }
            case 4:   // GetState
                {
                    byte mods = (byte)_modifiers, baseMods = _baseMods, latched = _latchedMods, locked = _lockedMods;
                    ushort buttons = _buttons;
                    c.Reply(XkbDeviceId, w => w
                        .U8(mods).U8(baseMods).U8(latched).U8(locked).U8(0).U8(0).I16(0).I16(0)
                        .U8(mods).U8(mods).U8(mods).U8(mods).U8(mods).Zero(1).U16(buttons).Zero(6));
                    break;
                }
            case 5:   // LatchLockState:numlockx 之类用它开 Num Lock
                {
                    byte affectLocks = r.U8(), locks = r.U8();
                    r.Skip(2);   // lockGroup、groupLock:只有一组
                    byte affectLatches = r.U8(), latches = r.U8();
                    _lockedMods = (byte)((_lockedMods & ~affectLocks) | (locks & affectLocks));
                    _latchedMods = (byte)((_latchedMods & ~affectLatches) | (latches & affectLatches));
                    UpdateModifierState(0, 0, XkbMajor, 5);
                    break;
                }
            case 6:   // GetControls
                XkbGetControls(c);
                break;
            case 8:   // GetMap
                XkbGetMap(c, r);
                break;
            case 10:  // GetCompatMap:三条规范的 SymInterpret(与 GetMap 里的键动作一致);要求的组各回一个空的 MODDEF
                {
                    byte groups = (byte)(r.U8() & 0x0F);
                    bool all = r.Bool();
                    ushort firstSI = r.U16(), nSI = r.U16();
                    int total = XkbSymInterprets.Length;
                    (int first, int count) = all ? (0, total) : (firstSI, nSI);
                    if (first + count > total)
                    {
                        throw new XProtocolError(XErrorCode.Value, firstSI);
                    }
                    c.Reply(XkbDeviceId, w =>
                    {
                        w.U8(groups).Zero(1).U16((ushort)first).U16((ushort)count).U16((ushort)total).Zero(16);
                        for (int i = first; i < first + count; i++)
                        {
                            (uint sym, byte mods, byte match, byte vmod, byte actionType, byte actionFlags, byte actionMods) = XkbSymInterprets[i];
                            w.U32(sym).U8(mods).U8(match).U8(vmod).U8(0)
                                .U8(actionType).U8(actionFlags).U8(actionMods).U8(actionMods).Zero(4);
                        }
                        for (int g = 0; g < 4; g++)
                        {
                            if ((groups & (1 << g)) != 0)
                            {
                                w.U8(0).U8(0).U16(0);
                            }
                        }
                    });
                    break;
                }
            case 12:  // GetIndicatorState
                {
                    uint state = IndicatorState();
                    c.Reply(XkbDeviceId, w => w.U32(state).Zero(20));
                    break;
                }
            case 13:  // GetIndicatorMap
                {
                    r.Skip(2);
                    uint which = r.U32();
                    c.Reply(XkbDeviceId, w =>
                    {
                        w.U32(which).U32(0x7).U8((byte)XkbIndicatorNames.Length).Zero(15);
                        for (int i = 0; i < 32; i++)
                        {
                            if ((which & (1u << i)) != 0)
                            {
                                WriteIndicatorMap(w, i);
                            }
                        }
                    });
                    break;
                }
            case 15:  // GetNamedIndicator
                {
                    r.Skip(6);   // ledClass、ledID、pad
                    uint atom = r.U32();
                    string? name = AtomName(atom);
                    int index = name is null ? -1 : Array.IndexOf(XkbIndicatorNames, name);
                    bool on = index >= 0 && (IndicatorState() & (1u << index)) != 0;
                    c.Reply(XkbDeviceId, w =>
                    {
                        w.U32(atom).Bool(index >= 0).Bool(on).Bool(index >= 0).U8((byte)Math.Max(0, index));
                        WriteIndicatorMap(w, index);
                        w.Bool(true).Zero(3);
                    });
                    break;
                }
            case 17:  // GetNames
                r.Skip(2);
                XkbGetNames(c, r.U32());
                break;
            case 19:  // GetGeometry:没有几何描述 —— found = False 时只有定长部分(Xlib 此时不读 labelFont,多给一个字节都会让它断言失败)
                {
                    r.Skip(2);
                    uint name = r.U32();
                    c.Reply(XkbDeviceId, w => w.U32(name).Bool(false).Zero(19));
                    break;
                }
            case 21:  // PerClientFlags:支持 DetectableAutoRepeat(自动重复时不插 KeyRelease)
                {
                    r.Skip(2);
                    uint change = r.U32(), value = r.U32();
                    const uint detectable = 1;
                    if ((change & detectable) != 0)
                    {
                        if ((value & detectable) != 0)
                        {
                            _xkbDetectableRepeat.Add(c);
                        }
                        else
                        {
                            _xkbDetectableRepeat.Remove(c);
                        }
                    }
                    uint current = _xkbDetectableRepeat.Contains(c) ? detectable : 0;
                    c.Reply(XkbDeviceId, w => w.U32(detectable).U32(current).U32(0).U32(0).Zero(8));
                    break;
                }
            case 22:  // ListComponents:没有组件库
                c.Reply(XkbDeviceId, w => w.Zero(12).U16(0).Zero(10));
                break;
            case 23:  // GetKbdByName:不能装载别的键盘描述
                c.Reply(XkbDeviceId, w => w.U8(Keymap.MinKeycode).U8(Keymap.MaxKeycode).Bool(false).Bool(false).U16(0).U16(0).Zero(16));
                break;
            case 24:  // GetDeviceInfo
                {
                    byte[] name = "Virtual core keyboard"u8.ToArray();
                    uint devType = Intern("KEYBOARD");
                    c.Reply(XkbDeviceId, w => w
                        .U16(0).U16(0).U16(0).U16(0)            // present、supported、unsupported、nDeviceLedFBs
                        .U8(0).U8(0).U8(0).U8(0).U8(0).Bool(true)
                        .U16(0x100).U16(0x100).Zero(2)          // dfltKbdFB、dfltLedFB
                        .U32(devType).U16((ushort)name.Length).Bytes(name).Pad4());
                    break;
                }
            case 9:   // SetMap:上传的键值与修饰键映射写回核心键位表(X11Server.XkbSetMap.cs)
                XkbSetMap(r);
                break;
            case 7:   // SetControls
            case 11:  // SetCompatMap
            case 14:  // SetIndicatorMap
            case 16:  // SetNamedIndicator
            case 18:  // SetNames
            case 20:  // SetGeometry
            case 25:  // SetDeviceInfo
                break;   // 键盘描述由核心键位表决定,这些改写接受但不生效(文件头)
            case 101: // SetDebuggingFlags
                c.Reply(0, w => w.U32(0).U32(0).U32(0).U32(0).Zero(8));
                break;
            default:
                throw new XProtocolError(XErrorCode.Request);
        }
    }

    private static void CheckXkbDevice(XRequestReader r)
    {
        ushort device = r.U16();
        if (device is not XkbUseCoreKbd and not XkbDeviceId)
        {
            throw new XProtocolError((XErrorCode)XkbErrorBase, device);
        }
    }

    // ------------------------------------------------------------------ 由核心键位表推出的描述

    /// <summary>一个键的 XKB 形态:类型下标、宽度(级数)、键值(没有键值时宽度为 0)。</summary>
    /// <remarks>
    /// 核心键位表的列与 XKB 的对应(XKB 规范 §17「Interactions Between XKB and the Core Protocol」):
    /// 第 1、2 列是组 1 的第 1、2 级,第 3、4 列是组 2 的第 1、2 级,第 5、6 列是组 1 的第 3、4 级。
    /// 只有一组,所以第 3、4 列不看;第 5、6 列有键值时这个键是四级的(AltGr 层)。
    /// </remarks>
    private (byte Type, byte Width, uint[] Syms) XkbKey(byte keycode)
    {
        uint s0 = _keymap.Keysym(keycode, 0), s1 = _keymap.KeysymsPerKeycode > 1 ? _keymap.Keysym(keycode, 1) : 0;
        uint s4 = _keymap.Keysym(keycode, 4), s5 = _keymap.Keysym(keycode, 5);
        if (s4 != 0 || s5 != 0)
        {
            uint l1 = s0, l2 = s1 == 0 ? s0 : s1, l3 = s4 == 0 ? s5 : s4, l4 = s5 == 0 ? l3 : s5;
            bool alphabetic = IsLowerLetter(l1) && l2 == UpperOf(l1);
            return (alphabetic ? XkbFourLevelAlphabetic : XkbFourLevel, 4, [l1, l2, l3, l4]);
        }
        if (s0 == 0 && s1 == 0)
        {
            return (0, 0, []);
        }
        if (s1 == 0 || s1 == s0)
        {
            return (0, 1, [s0]);
        }
        if (IsKeypadKeysym(s0) || IsKeypadKeysym(s1))
        {
            return (3, 2, [s0, s1]);
        }
        if (IsLowerLetter(s0) && s1 == UpperOf(s0))
        {
            return (2, 2, [s0, s1]);
        }
        return (1, 2, [s0, s1]);
    }

    private static bool IsKeypadKeysym(uint sym) => sym is >= 0xff80 and <= 0xffbd;

    private static bool IsLowerLetter(uint sym) => sym is >= 'a' and <= 'z' or >= 0xe0 and <= 0xfe and not 0xf7;

    private static uint UpperOf(uint sym) => sym is >= 'a' and <= 'z' ? sym - 32 : sym is >= 0xe0 and <= 0xfe ? sym - 32 : sym;

    /// <summary>修饰键的动作:Lock 类是 LockMods,其余是 SetMods(都用修饰键表里的修饰位)。</summary>
    private bool XkbActionOf(byte keycode, out byte type)
    {
        type = 0;
        if (_keymap.ModifierBitOf(keycode) == 0)
        {
            return false;
        }
        type = _keymap.IsLockingKey(keycode) ? (byte)3 : (byte)1;
        return true;
    }

    private ushort XkbVirtualModsOf(byte keycode)
    {
        uint sym = _keymap.Keysym(keycode, 0);
        return sym switch
        {
            0xff7f => 1 << 0,                     // Num_Lock → NumLock
            0xffe9 or 0xffea => (1 << 1) | (1 << 4),   // Alt_L / Alt_R → Alt、Meta
            0xfe03 => 1 << 2,                     // ISO_Level3_Shift → LevelThree
            0xffeb or 0xffec => 1 << 3,           // Super_L / Super_R → Super
            _ => 0,
        };
    }

    private uint IndicatorState()
    {
        uint state = 0;
        if ((_lockedMods & 0x02) != 0)
        {
            state |= 1;   // Caps Lock
        }
        if ((_lockedMods & 0x10) != 0)
        {
            state |= 2;   // Num Lock
        }
        return state;
    }

    private static void WriteIndicatorMap(XWriter w, int index)
    {
        // flags、whichGroups、groups、whichMods(UseLocked = 4)、mods、realMods、vmods、ctrls
        (byte which, byte mods) = index switch
        {
            0 => ((byte)4, (byte)0x02),
            1 => ((byte)4, (byte)0x10),
            _ => ((byte)0, (byte)0),
        };
        w.U8(0).U8(0).U8(0).U8(which).U8(mods).U8(mods).U16(0).U32(0);
    }

    // ------------------------------------------------------------------ GetMap

    private void XkbGetMap(XClient c, XRequestReader r)
    {
        ushort full = r.U16(), partial = r.U16();
        byte firstType = r.U8(), nTypes = r.U8();
        byte firstKeySym = r.U8(), nKeySyms = r.U8();
        byte firstKeyAction = r.U8(), nKeyActions = r.U8();
        byte firstKeyBehavior = r.U8(), nKeyBehaviors = r.U8();
        ushort vmodsWanted = r.U16();
        byte firstKeyExplicit = r.U8(), nKeyExplicit = r.U8();
        byte firstModMapKey = r.U8(), nModMapKeys = r.U8();
        byte firstVModMapKey = r.U8(), nVModMapKeys = r.U8();

        const byte min = Keymap.MinKeycode;
        const int keyCount = Keymap.MaxKeycode - Keymap.MinKeycode + 1;
        ushort present = (ushort)((full | partial) & 0xFF);

        (byte First, int Count) Range(int bit, byte first, byte count)
        {
            if ((full & bit) != 0)
            {
                return (min, keyCount);
            }
            if ((partial & bit) == 0 || count == 0)
            {
                return (0, 0);
            }
            if (first < min || first + count - 1 > Keymap.MaxKeycode)
            {
                throw new XProtocolError(XErrorCode.Value, first);
            }
            return (first, count);
        }

        (byte tFirst, int tCount) = (full & 1) != 0 ? ((byte)0, XkbTypes.Length)
            : (partial & 1) != 0 ? (firstType, nTypes) : ((byte)0, 0);
        if (tFirst + tCount > XkbTypes.Length)
        {
            throw new XProtocolError(XErrorCode.Value, firstType);
        }
        (byte symsFirst, int symsCount) = Range(2, firstKeySym, nKeySyms);
        (byte actsFirst, int actsCount) = Range(16, firstKeyAction, nKeyActions);
        (byte behavFirst, int behavCount) = Range(32, firstKeyBehavior, nKeyBehaviors);
        (byte explFirst, int explCount) = Range(8, firstKeyExplicit, nKeyExplicit);
        (byte modmapFirst, int modmapCount) = Range(4, firstModMapKey, nModMapKeys);
        (byte vmodmapFirst, int vmodmapCount) = Range(128, firstVModMapKey, nVModMapKeys);
        ushort vmods = (full & 64) != 0 ? (ushort)((1 << XkbVirtualMods.Length) - 1)
            : (partial & 64) != 0 ? (ushort)(vmodsWanted & ((1 << XkbVirtualMods.Length) - 1)) : (ushort)0;

        // 先把各段算出来,头部的计数要用。
        List<(byte Type, byte Width, uint[] Syms)> keys = [];
        int totalSyms = 0;
        for (int k = 0; k < symsCount; k++)
        {
            (byte Type, byte Width, uint[] Syms) key = XkbKey((byte)(symsFirst + k));
            keys.Add(key);
            totalSyms += key.Syms.Length;
        }
        List<byte> actionCounts = [];
        List<(byte Type, int Count)> actionLists = [];
        int totalActions = 0;
        for (int k = 0; k < actsCount; k++)
        {
            byte code = (byte)(actsFirst + k);
            byte width = XkbKey(code).Width;
            if (width > 0 && XkbActionOf(code, out byte type))
            {
                actionCounts.Add(width);
                actionLists.Add((type, width));
                totalActions += width;
            }
            else
            {
                actionCounts.Add(0);
            }
        }
        List<(byte Code, byte Mods)> modEntries = [];
        for (int k = 0; k < modmapCount; k++)
        {
            byte code = (byte)(modmapFirst + k);
            byte mods = (byte)_keymap.ModifierBitOf(code);
            if (mods != 0)
            {
                modEntries.Add((code, mods));
            }
        }
        List<(byte Code, ushort VMods)> vmodEntries = [];
        for (int k = 0; k < vmodmapCount; k++)
        {
            byte code = (byte)(vmodmapFirst + k);
            ushort v = XkbVirtualModsOf(code);
            if (v != 0)
            {
                vmodEntries.Add((code, v));
            }
        }

        c.Reply(XkbDeviceId, w =>
        {
            w.Zero(2).U8(min).U8(Keymap.MaxKeycode).U16(present)
                .U8(tFirst).U8((byte)tCount).U8((byte)XkbTypes.Length)
                .U8(symsFirst).U16((ushort)totalSyms).U8((byte)symsCount)
                .U8(actsFirst).U16((ushort)totalActions).U8((byte)actsCount)
                .U8(behavFirst).U8((byte)behavCount).U8(0)
                .U8(explFirst).U8((byte)explCount).U8(0)
                .U8(modmapFirst).U8((byte)modmapCount).U8((byte)modEntries.Count)
                .U8(vmodmapFirst).U8((byte)vmodmapCount).U8((byte)vmodEntries.Count)
                .Zero(1).U16(vmods);

            // KeyTypes
            for (int t = tFirst; t < tFirst + tCount; t++)
            {
                (_, byte mask, byte levels, (byte Mods, byte Level)[] map, _) = XkbTypes[t];
                w.U8(mask).U8(mask).U16(0).U8(levels).U8((byte)map.Length).Bool(false).Zero(1);
                foreach ((byte mods, byte level) in map)
                {
                    w.Bool(true).U8(mods).U8(level).U8(mods).U16(0).Zero(2);
                }
            }
            // KeySyms
            foreach ((byte type, byte width, uint[] keySyms) in keys)
            {
                w.U8(type).U8(0).U8(0).U8(0).U8((byte)(keySyms.Length == 0 ? 0 : 1)).U8(width).U16((ushort)keySyms.Length);
                foreach (uint sym in keySyms)
                {
                    w.U32(sym);
                }
            }
            // KeyActions:先是每个键的动作数(补齐到 4),再是动作本身
            if (actsCount > 0)
            {
                foreach (byte count in actionCounts)
                {
                    w.U8(count);
                }
                w.Zero(XWire.Pad(actionCounts.Count) - actionCounts.Count);
                foreach ((byte type, int count) in actionLists)
                {
                    for (int i = 0; i < count; i++)
                    {
                        // SetMods / LockMods:flags(UseModMapMods = 4;SetMods 再加 ClearLocks = 1)、mask、realMods、vmods
                        w.U8(type).U8(type == 1 ? (byte)5 : (byte)4).U8(0).U8(0).U8(0).U8(0).Zero(2);
                    }
                }
            }
            // KeyBehaviors、Explicit:没有条目
            // VirtualMods:每个要求的虚拟修饰一个字节(绑到的真修饰)
            if (vmods != 0)
            {
                int n = 0;
                for (int i = 0; i < XkbVirtualMods.Length; i++)
                {
                    if ((vmods & (1 << i)) != 0)
                    {
                        w.U8(XkbVirtualMods[i].RealMods);
                        n++;
                    }
                }
                w.Zero(XWire.Pad(n) - n);
            }
            // ModifierMap
            foreach ((byte code, byte mods) in modEntries)
            {
                w.U8(code).U8(mods);
            }
            w.Zero(XWire.Pad(modEntries.Count * 2) - (modEntries.Count * 2));
            // VirtualModMap
            foreach ((byte code, ushort v) in vmodEntries)
            {
                w.U8(code).Zero(1).U16(v);
            }
        });
    }

    // ------------------------------------------------------------------ GetNames

    private void XkbGetNames(XClient c, uint which)
    {
        which &= 0x3FFF;   // 键别名(第 10 位)与无线电组(第 13 位)计数为 0,照样回显
        uint keycodes = Intern("evdev"), geometry = Intern("pc(pc105)"), symbols = Intern($"pc+{KeyboardLayout}"), types = Intern("complete"),
            compat = Intern("complete");
        uint[] typeNames = [.. XkbTypes.Select(t => Intern(t.Name))];
        uint[] levelNames = [.. XkbTypes.SelectMany(t => t.LevelNames).Select(Intern)];
        uint[] indicatorNames = [.. XkbIndicatorNames.Select(Intern)];
        uint[] vmodNames = [.. XkbVirtualMods.Select(v => Intern(v.Name))];
        uint groupName = Intern(KeyboardLayout == "us" ? "English (US)" : KeyboardLayout);
        const byte keyCount = Keymap.MaxKeycode - Keymap.MinKeycode + 1;

        c.Reply(XkbDeviceId, w =>
        {
            w.U32(which).U8(Keymap.MinKeycode).U8(Keymap.MaxKeycode).U8((byte)XkbTypes.Length).U8(1)
                .U16((ushort)((1 << XkbVirtualMods.Length) - 1)).U8(Keymap.MinKeycode).U8(keyCount)
                .U32((1u << XkbIndicatorNames.Length) - 1).U8(0).U8(0).U16((ushort)levelNames.Length).Zero(4);
            uint[] fixedNames = [keycodes, geometry, symbols, symbols, types, compat];   // Keycodes … Compat,各一个原子
            for (int bit = 0; bit < fixedNames.Length; bit++)
            {
                if ((which & (1u << bit)) != 0)
                {
                    w.U32(fixedNames[bit]);
                }
            }
            if ((which & (1 << 6)) != 0)
            {
                foreach (uint atom in typeNames)
                {
                    w.U32(atom);
                }
            }
            if ((which & (1 << 7)) != 0)
            {
                foreach ((string Name, byte Mask, byte Levels, (byte Mods, byte Level)[] Map, string[] LevelNames) type in XkbTypes)
                {
                    w.U8(type.Levels);
                }
                w.Zero(XWire.Pad(XkbTypes.Length) - XkbTypes.Length);
                foreach (uint atom in levelNames)
                {
                    w.U32(atom);
                }
            }
            if ((which & (1 << 8)) != 0)
            {
                foreach (uint atom in indicatorNames)
                {
                    w.U32(atom);
                }
            }
            if ((which & (1 << 11)) != 0)
            {
                foreach (uint atom in vmodNames)
                {
                    w.U32(atom);
                }
            }
            if ((which & (1 << 12)) != 0)
            {
                w.U32(groupName);
            }
            if ((which & (1 << 9)) != 0)
            {
                Span<byte> bytes = stackalloc byte[4];
                for (int k = 0; k < keyCount; k++)
                {
                    string name = XkbKeyNames.Of((byte)(Keymap.MinKeycode + k));
                    bytes.Clear();
                    XWire.Latin1.GetBytes(name.AsSpan(0, Math.Min(4, name.Length)), bytes);
                    w.Bytes(bytes);
                }
            }
            // KeyAliases(第 10 位)与 RGNames(第 13 位):计数都是 0,没有内容。
        });
    }

    // ------------------------------------------------------------------ GetControls

    private static void XkbGetControls(XClient c) => c.Reply(XkbDeviceId, w =>
    {
        w.U8(0).U8(1).U8(0).U8(0).U8(0).U8(0).U8(0).Zero(1)   // mouseKeysDfltBtn、numGroups = 1、groupsWrap、内部 / 忽略锁定修饰
            .U16(0).U16(0)
            .U16(660).U16(40)                                  // repeatDelay、repeatInterval(毫秒)
            .U16(300).U16(300).U16(160).U16(40).U16(30).U16(10).I16(0)
            .U16(0).U16(120).U16(0).U16(0).Zero(2)
            .U32(0).U32(0)
            .U32(1);                                           // enabledControls:RepeatKeys
        for (int i = 0; i < 32; i++)
        {
            w.U8(0xFF);                                        // 每个键都自动重复
        }
    });

    // ------------------------------------------------------------------ 事件

    private void XkbSelectEvents(XClient c, XRequestReader r)
    {
        ushort affectWhich = r.U16(), clear = r.U16(), selectAll = r.U16();
        ushort affectMap = r.U16(), map = r.U16();
        if (!_xkbSelections.TryGetValue(c, out uint[]? details))
        {
            _xkbSelections[c] = details = new uint[12];
        }
        // 每种事件的细节字段宽度(字节),顺序即线上顺序;MapNotify(第 1 位)用头部的 affectMap / map。
        int[] sizes = [2, 0, 2, 4, 4, 4, 2, 1, 1, 1, 2, 2];
        for (int bit = 0; bit < 12; bit++)
        {
            if ((affectWhich & (1 << bit)) == 0)
            {
                continue;
            }
            if ((clear & (1 << bit)) != 0)
            {
                details[bit] = 0;
                continue;
            }
            if ((selectAll & (1 << bit)) != 0)
            {
                details[bit] = uint.MaxValue;
                continue;
            }
            if (bit == 1)
            {
                details[1] = (details[1] & ~(uint)affectMap) | ((uint)map & affectMap);
                continue;
            }
            uint affect = ReadSized(r, sizes[bit]), value = ReadSized(r, sizes[bit]);
            details[bit] = (details[bit] & ~affect) | (value & affect);
        }
        if (details.All(d => d == 0))
        {
            _xkbSelections.Remove(c);
        }

        static uint ReadSized(XRequestReader r, int size) => size switch
        {
            1 => r.U8(),
            2 => r.U16(),
            _ => r.U32(),
        };
    }

    /// <summary>修饰状态变了:给选了 StateNotify(且细节对得上)的客户端发;锁定位变了再发 IndicatorStateNotify。</summary>
    private void NotifyXkbState(ushort changed, byte keycode, byte eventType, byte requestMajor, byte requestMinor)
    {
        if (_xkbSelections.Count == 0)
        {
            return;
        }
        uint time = Now;
        byte mods = (byte)_modifiers, baseMods = _baseMods, latched = _latchedMods, locked = _lockedMods;
        ushort buttons = _buttons;
        uint indicators = IndicatorState();
        foreach ((XClient client, uint[] details) in _xkbSelections)
        {
            if (client.Closed)
            {
                continue;
            }
            if ((details[XkbStateNotify] & changed) != 0)
            {
                client.Event(XkbEventBase, XkbStateNotify, w => w
                    .U32(time).U8(XkbDeviceId).U8(mods).U8(baseMods).U8(latched).U8(locked).U8(0).I16(0).I16(0).U8(0)
                    .U8(mods).U8(mods).U8(mods).U8(mods).U8(mods).U16(buttons).U16(changed)
                    .U8(keycode).U8(eventType).U8(requestMajor).U8(requestMinor));
            }
            if ((changed & 8) != 0 && details[XkbIndicatorStateNotify] != 0)
            {
                client.Event(XkbEventBase, XkbIndicatorStateNotify, w => w
                    .U32(time).U8(XkbDeviceId).Zero(3).U32(indicators).U32(0x3).Zero(12));
            }
        }
    }

    /// <summary>核心键位表 / 修饰键表变了:XKB 键位表随之改变,发 MapNotify。</summary>
    private void NotifyXkbMapChanged()
    {
        if (_xkbSelections.Count == 0)
        {
            return;
        }
        uint time = Now;
        const byte min = Keymap.MinKeycode;
        const byte count = Keymap.MaxKeycode - Keymap.MinKeycode + 1;
        foreach ((XClient client, uint[] details) in _xkbSelections)
        {
            if (client.Closed || details[XkbMapNotify] == 0)
            {
                continue;
            }
            client.Event(XkbEventBase, XkbMapNotify, w => w
                .U32(time).U8(XkbDeviceId).U8(0).U16(0xFF).U8(min).U8(Keymap.MaxKeycode)
                .U8(0).U8((byte)XkbTypes.Length).U8(min).U8(count).U8(min).U8(count).U8(min).U8(count)
                .U8(min).U8(count).U8(min).U8(count).U8(min).U8(count).U16((ushort)((1 << XkbVirtualMods.Length) - 1)));
        }
    }

    /// <summary>根窗口的 _XKB_RULES_NAMES:setxkbmap 等工具从这里读当前的 rules / model / layout / variant / options。</summary>
    private string? _keyboardLayout;

    private string KeyboardLayout => _keyboardLayout ?? _options.KeyboardLayout;

    private void PublishXkbRulesNames()
    {
        uint property = Intern("_XKB_RULES_NAMES");
        byte[] value = XWire.Latin1.GetBytes($"evdev\0pc105\0{KeyboardLayout}\0\0\0");
        Root.Properties[property] = new XProperty(XAtom.String, 8, value);
        SendPropertyNotify(Root, property, deleted: false);
    }

    private void CleanupXkb(XClient client)
    {
        _xkbSelections.Remove(client);
        _xkbDetectableRepeat.Remove(client);
    }
}
