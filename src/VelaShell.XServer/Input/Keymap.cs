// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 第 5 节「Keyboards」(键码 8–255、每键码若干键值、
//   第一 / 第二列与 Shift / Lock 的关系)、「GetKeyboardMapping」「GetModifierMapping」
//   X Window System Protocol 附录 A「KEYSYM Encoding」(键值编号)
//   键码编号:Linux 输入子系统的 evdev 扫描码 + 8(与现代 Linux 上的 X.Org 一致,见架构 §7)

namespace VelaShell.XServer.Input;

/// <summary>
/// 键码 ↔ 键值表(US 布局起步)与修饰键映射。可被 ChangeKeyboardMapping / SetModifierMapping 改写。
/// </summary>
internal sealed class Keymap
{
    public const byte MinKeycode = 8;
    public const byte MaxKeycode = 255;

    /// <summary>每个键码几个键值:第 1 列无修饰,第 2 列 Shift。</summary>
    public int KeysymsPerKeycode { get; private set; } = 2;

    private uint[] _keysyms = new uint[(MaxKeycode - MinKeycode + 1) * 2];

    /// <summary>修饰键表:8 个修饰位 × 每位 <see cref="KeycodesPerModifier" /> 个键码(0 = 空位)。</summary>
    public byte[] ModifierMap { get; private set; } =
    [
        50, 62,     // Shift:Shift_L、Shift_R
        66, 0,      // Lock:Caps_Lock
        37, 105,    // Control:Control_L、Control_R
        64, 108,    // Mod1:Alt_L、Alt_R
        77, 0,      // Mod2:Num_Lock
        0, 0,       // Mod3
        133, 134,   // Mod4:Super_L、Super_R
        0, 0,       // Mod5
    ];

    public int KeycodesPerModifier => ModifierMap.Length / 8;

    public Keymap()
    {
        foreach ((byte code, uint lower, uint upper) in UsLayout)
        {
            _keysyms[(code - MinKeycode) * 2] = lower;
            _keysyms[((code - MinKeycode) * 2) + 1] = upper;
        }
    }

    public uint Keysym(byte keycode, int column) =>
        keycode < MinKeycode || column >= KeysymsPerKeycode ? 0 : _keysyms[((keycode - MinKeycode) * KeysymsPerKeycode) + column];

    /// <summary>
    /// ChangeKeyboardMapping:改写一段键码的键值。表的列数取原列数与请求列数中大的那个 —— 请求更宽时整张表按新列数重排,
    /// 更窄时别的键不受影响;请求里的这几个键,超出请求列数的列清成 NoSymbol。
    /// </summary>
    /// <remarks>
    /// 只放宽不收窄:<c>xmodmap -e "keycode 108 = ISO_Level3_Shift"</c> 发的是每键码 1 列,要是整张表随之收成 1 列,
    /// 所有键的 Shift 列都没了。
    /// </remarks>
    public void Change(byte firstKeycode, int keysymsPerKeycode, ReadOnlySpan<uint> keysyms)
    {
        if (keysymsPerKeycode > KeysymsPerKeycode)
        {
            uint[] next = new uint[(MaxKeycode - MinKeycode + 1) * keysymsPerKeycode];
            for (int k = 0; k <= MaxKeycode - MinKeycode; k++)
            {
                _keysyms.AsSpan(k * KeysymsPerKeycode, KeysymsPerKeycode).CopyTo(next.AsSpan(k * keysymsPerKeycode));
            }
            _keysyms = next;
            KeysymsPerKeycode = keysymsPerKeycode;
        }
        int per = KeysymsPerKeycode;
        int count = keysyms.Length / keysymsPerKeycode;
        for (int k = 0; k < count; k++)
        {
            Span<uint> row = _keysyms.AsSpan((firstKeycode - MinKeycode + k) * per, per);
            row.Clear();
            keysyms.Slice(k * keysymsPerKeycode, keysymsPerKeycode).CopyTo(row);
        }
    }

    public void SetModifierMap(byte[] map) => ModifierMap = map;

    /// <summary>这个键码是哪个修饰位(没有返回 0)。</summary>
    public ushort ModifierBitOf(byte keycode)
    {
        int per = KeycodesPerModifier;
        for (int m = 0; m < 8; m++)
        {
            for (int i = 0; i < per; i++)
            {
                if (ModifierMap[(m * per) + i] == keycode && keycode != 0)
                {
                    return (ushort)(1 << m);
                }
            }
        }
        return 0;
    }

    /// <summary>Lock 类修饰(Caps_Lock、Num_Lock):按下翻转,而不是按住生效。</summary>
    public bool IsLockingKey(byte keycode)
    {
        uint sym = Keysym(keycode, 0);
        return sym is 0xffe5 or 0xff7f;
    }

    /// <summary>键码表:(键码, 无修饰键值, Shift 键值)。</summary>
    private static readonly (byte Code, uint Lower, uint Upper)[] UsLayout =
    [
        (9, 0xff1b, 0xff1b),                                  // Escape
        (10, '1', '!'), (11, '2', '@'), (12, '3', '#'), (13, '4', '$'), (14, '5', '%'),
        (15, '6', '^'), (16, '7', '&'), (17, '8', '*'), (18, '9', '('), (19, '0', ')'),
        (20, '-', '_'), (21, '=', '+'),
        (22, 0xff08, 0xff08),                                 // BackSpace
        (23, 0xff09, 0xfe20),                                 // Tab / ISO_Left_Tab
        (24, 'q', 'Q'), (25, 'w', 'W'), (26, 'e', 'E'), (27, 'r', 'R'), (28, 't', 'T'),
        (29, 'y', 'Y'), (30, 'u', 'U'), (31, 'i', 'I'), (32, 'o', 'O'), (33, 'p', 'P'),
        (34, '[', '{'), (35, ']', '}'),
        (36, 0xff0d, 0xff0d),                                 // Return
        (37, 0xffe3, 0xffe3),                                 // Control_L
        (38, 'a', 'A'), (39, 's', 'S'), (40, 'd', 'D'), (41, 'f', 'F'), (42, 'g', 'G'),
        (43, 'h', 'H'), (44, 'j', 'J'), (45, 'k', 'K'), (46, 'l', 'L'),
        (47, ';', ':'), (48, '\'', '"'), (49, '`', '~'),
        (50, 0xffe1, 0xffe1),                                 // Shift_L
        (51, '\\', '|'),
        (52, 'z', 'Z'), (53, 'x', 'X'), (54, 'c', 'C'), (55, 'v', 'V'), (56, 'b', 'B'),
        (57, 'n', 'N'), (58, 'm', 'M'),
        (59, ',', '<'), (60, '.', '>'), (61, '/', '?'),
        (62, 0xffe2, 0xffe2),                                 // Shift_R
        (63, 0xffaa, 0xffaa),                                 // KP_Multiply
        (64, 0xffe9, 0xffe7),                                 // Alt_L / Meta_L
        (65, ' ', ' '),
        (66, 0xffe5, 0xffe5),                                 // Caps_Lock
        (67, 0xffbe, 0xffbe), (68, 0xffbf, 0xffbf), (69, 0xffc0, 0xffc0), (70, 0xffc1, 0xffc1),   // F1–F4
        (71, 0xffc2, 0xffc2), (72, 0xffc3, 0xffc3), (73, 0xffc4, 0xffc4), (74, 0xffc5, 0xffc5),   // F5–F8
        (75, 0xffc6, 0xffc6), (76, 0xffc7, 0xffc7),                                             // F9–F10
        (77, 0xff7f, 0xff7f),                                 // Num_Lock
        (78, 0xff14, 0xff14),                                 // Scroll_Lock
        (79, 0xff95, 0xffb7), (80, 0xff97, 0xffb8), (81, 0xff9a, 0xffb9),                         // KP_7 8 9
        (82, 0xffad, 0xffad),                                 // KP_Subtract
        (83, 0xff96, 0xffb4), (84, 0xff9d, 0xffb5), (85, 0xff98, 0xffb6),                         // KP_4 5 6
        (86, 0xffab, 0xffab),                                 // KP_Add
        (87, 0xff9c, 0xffb1), (88, 0xff99, 0xffb2), (89, 0xff9b, 0xffb3),                         // KP_1 2 3
        (90, 0xff9e, 0xffb0), (91, 0xff9f, 0xffae),                                             // KP_0、KP_Decimal
        (94, '<', '>'),                                       // 102 键盘上 Z 左边那个键
        (95, 0xffc8, 0xffc8), (96, 0xffc9, 0xffc9),           // F11、F12
        (104, 0xff8d, 0xff8d),                                // KP_Enter
        (105, 0xffe4, 0xffe4),                                // Control_R
        (106, 0xffaf, 0xffaf),                                // KP_Divide
        (107, 0xff61, 0xff61),                                // Print
        (108, 0xffea, 0xffea),                                // Alt_R
        (110, 0xff50, 0xff50), (111, 0xff52, 0xff52), (112, 0xff55, 0xff55),                     // Home Up Prior
        (113, 0xff51, 0xff51), (114, 0xff53, 0xff53),                                           // Left Right
        (115, 0xff57, 0xff57), (116, 0xff54, 0xff54), (117, 0xff56, 0xff56),                     // End Down Next
        (118, 0xff63, 0xff63), (119, 0xffff, 0xffff),                                           // Insert Delete
        (127, 0xff13, 0xff13),                                // Pause
        (133, 0xffeb, 0xffeb), (134, 0xffec, 0xffec),         // Super_L、Super_R
        (135, 0xff67, 0xff67),                                // Menu
    ];
}
