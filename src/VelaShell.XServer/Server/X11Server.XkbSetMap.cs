// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   The X Keyboard Extension: Protocol Specification, Version 1.0 —— §16「SetMap」(present 掩码:KeyTypes 1、KeySyms 2、
//   ModifierMap 4、ExplicitComponents 8、KeyActions 16、KeyBehaviors 32、VirtualMods 64、VirtualModMap 128;
//   各段依次是 SETKEYTYPE、KEYSYMMAP、动作数 + 动作、SETBEHAVIOR、虚拟修饰的真修饰、SETEXPLICIT、KEYMODMAP、KEYVMODMAP,
//   变长段补齐到 4 字节)、§8「Key Symbol Map」(KEYSYMMAP:ktIndex[4]、groupInfo 的低 4 位是组数、width 是每组的级数,
//   键值按组排列)、§17「Interactions Between XKB and the Core Protocol」(XKB 键位表换成核心的列序:组 1 第 1、2 级,
//   组 2 第 1、2 级,组 1 第 3、4 级)。
//
//   XKB 描述始终由核心键位表推出(X11Server.Xkb.cs),所以 SetMap 把上传的键值与修饰键映射换成核心键位表写回,
//   再照常推出 XKB —— xkbcomp keymap $DISPLAY、setxkbmap -print | xkbcomp - $DISPLAY 都走这条路。
//   类型、动作、行为、显式成分、虚拟修饰映射按规范读完、不另存:这些由服务端按键值与修饰键表推出。

using VelaShell.XServer.Input;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Server;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    private void XkbSetMap(XRequestReader r)
    {
        ushort present = r.U16();
        r.U16();   // flags
        r.U8();    // minKeyCode
        r.U8();    // maxKeyCode
        byte firstType = r.U8(), nTypes = r.U8();
        byte firstKeySym = r.U8(), nKeySyms = r.U8();
        r.U16();   // totalSyms
        r.U8();    // firstKeyAction
        byte nKeyActions = r.U8();
        ushort totalActions = r.U16();
        r.U8();    // firstKeyBehavior
        r.U8();    // nKeyBehaviors
        byte totalBehaviors = r.U8();
        r.U8();    // firstKeyExplicit
        r.U8();    // nKeyExplicit
        byte totalExplicit = r.U8();
        byte firstModMapKey = r.U8(), nModMapKeys = r.U8(), totalModMapKeys = r.U8();
        r.U8();    // firstVModMapKey
        r.U8();    // nVModMapKeys
        byte totalVModMapKeys = r.U8();
        ushort virtualMods = r.U16();
        _ = firstType;

        if ((present & 1) != 0)   // KeyTypes:SETKEYTYPE × nTypes
        {
            for (int t = 0; t < nTypes; t++)
            {
                r.Skip(4);   // mask、realMods、virtualMods
                r.U8();      // numLevels
                byte entries = r.U8();
                bool preserve = r.Bool();
                r.Skip(1);
                r.Skip(entries * 4 * (preserve ? 2 : 1));
            }
        }

        List<(byte Keycode, uint[] Columns)> keys = [];
        if ((present & 2) != 0)   // KeySyms:KEYSYMMAP × nKeySyms
        {
            CheckKeyRange(firstKeySym, nKeySyms);
            for (int k = 0; k < nKeySyms; k++)
            {
                r.Skip(4);   // ktIndex[4]:类型由服务端按键值推
                int groups = r.U8() & 0x0F;
                int width = r.U8();
                ushort count = r.U16();
                uint[] syms = new uint[count];
                for (int i = 0; i < count; i++)
                {
                    syms[i] = r.U32();
                }
                keys.Add(((byte)(firstKeySym + k), ToCoreColumns(syms, groups, width)));
            }
        }
        if ((present & 16) != 0)   // KeyActions:每键的动作数(补齐)+ 动作
        {
            r.Skip(XWire.Pad(nKeyActions) + (totalActions * 8));
        }
        if ((present & 32) != 0)   // KeyBehaviors
        {
            r.Skip(totalBehaviors * 4);
        }
        if ((present & 64) != 0)   // VirtualMods:每个置位的虚拟修饰一个字节(补齐)
        {
            int bits = System.Numerics.BitOperations.PopCount(virtualMods);
            r.Skip(XWire.Pad(bits));
        }
        if ((present & 8) != 0)   // ExplicitComponents
        {
            r.Skip(XWire.Pad(totalExplicit * 2));
        }
        List<(byte Keycode, byte Mods)> modmap = [];
        if ((present & 4) != 0)   // ModifierMap:KEYMODMAP × totalModMapKeys(补齐)
        {
            CheckKeyRange(firstModMapKey, nModMapKeys);
            for (int i = 0; i < totalModMapKeys; i++)
            {
                modmap.Add((r.U8(), r.U8()));
            }
            r.Skip(XWire.Pad(totalModMapKeys * 2) - (totalModMapKeys * 2));
        }
        if ((present & 128) != 0)   // VirtualModMap
        {
            r.Skip(totalVModMapKeys * 4);
        }

        if (keys.Count > 0)
        {
            int per = keys.Any(k => k.Columns.Length > 2) ? 6 : 2;
            uint[] table = new uint[keys.Count * per];
            for (int k = 0; k < keys.Count; k++)
            {
                keys[k].Columns.CopyTo(table.AsSpan(k * per));
            }
            _keymap.Change(firstKeySym, per, table);
            byte count = (byte)keys.Count;
            foreach (XClient client in _clients.Values)
            {
                client.Event(XEventCode.MappingNotify, 0, w => w.U8(1).U8(firstKeySym).U8(count));
            }
        }
        if ((present & 4) != 0)
        {
            _keymap.SetModifierMap(MergeModifierMap(firstModMapKey, nModMapKeys, modmap));
            foreach (XClient client in _clients.Values)
            {
                client.Event(XEventCode.MappingNotify, 0, w => w.U8(0).U8(0).U8(0));
            }
        }
        if (keys.Count > 0 || (present & 4) != 0)
        {
            NotifyXkbMapChanged();
        }
    }

    private static void CheckKeyRange(byte first, byte count)
    {
        if (count > 0 && (first < Keymap.MinKeycode || first + count - 1 > Keymap.MaxKeycode))
        {
            throw new XProtocolError(XErrorCode.Value, first);
        }
    }

    /// <summary>
    /// 一个键的 XKB 键值(按组排列,每组 <paramref name="width" /> 级)换成核心列:只有两级、一组时 2 列,
    /// 否则 6 列 —— 组 1 第 1、2 级,组 2 第 1、2 级(没有组 2 时照抄组 1),组 1 第 3、4 级。
    /// </summary>
    private static uint[] ToCoreColumns(uint[] syms, int groups, int width)
    {
        uint At(int group, int level) =>
            group < groups && level < width && (group * width) + level < syms.Length ? syms[(group * width) + level] : 0;

        uint l1 = At(0, 0), l2 = At(0, 1), l3 = At(0, 2), l4 = At(0, 3);
        if (groups <= 1 && width <= 2)
        {
            return [l1, l2];
        }
        uint g2l1 = groups > 1 ? At(1, 0) : l1, g2l2 = groups > 1 ? At(1, 1) : l2;
        return [l1, l2, g2l1, g2l2, l3, l4];
    }

    /// <summary>
    /// 把 SetMap 里一段键码的修饰位并进核心修饰键表:这段键码先从表里摘掉,再按上传的修饰位放回去;表宽不够时加宽。
    /// </summary>
    private byte[] MergeModifierMap(byte first, byte count, List<(byte Keycode, byte Mods)> entries)
    {
        int per = _keymap.KeycodesPerModifier;
        byte[] current = _keymap.ModifierMap;
        var rows = new List<byte>[8];
        for (int m = 0; m < 8; m++)
        {
            rows[m] = [];
            for (int i = 0; i < per; i++)
            {
                byte code = current[(m * per) + i];
                if (code != 0 && (code < first || code >= first + count))
                {
                    rows[m].Add(code);
                }
            }
        }
        foreach ((byte keycode, byte mods) in entries)
        {
            for (int m = 0; m < 8; m++)
            {
                if ((mods & (1 << m)) != 0 && !rows[m].Contains(keycode))
                {
                    rows[m].Add(keycode);
                }
            }
        }
        int width = Math.Max(1, rows.Max(row => row.Count));
        byte[] map = new byte[8 * width];
        for (int m = 0; m < 8; m++)
        {
            rows[m].CopyTo(map, m * width);
        }
        return map;
    }
}
