// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 依据:The X Keyboard Extension: Protocol Specification —— §2「Key names」(每个键码一个 4 字符的名字);
// 名字取 evdev 键码约定里的物理位置名(<AE01> 是字母区第 E 行第 1 个键,诸如此类),
// 这是键盘描述的数据约定,不是服务端代码。没有约定名字的键码叫「I」加键码(I120)。

namespace VelaShell.XServer.Input;

/// <summary>evdev 键码(扫描码 + 8)→ XKB 键名。</summary>
internal static class XkbKeyNames
{
    private static readonly string[] Names = Build();

    public static string Of(byte keycode) => Names[keycode];

    private static string[] Build()
    {
        string[] names = new string[256];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = $"I{i}";
        }
        void Row(int first, string prefix, int count)
        {
            for (int i = 0; i < count; i++)
            {
                names[first + i] = $"{prefix}{i + 1:D2}";
            }
        }
        Row(10, "AE", 12);   // 1 2 3 … =
        Row(24, "AD", 12);   // Q W E … ]
        Row(38, "AC", 11);   // A S D … '
        Row(52, "AB", 10);   // Z X C … /
        Row(67, "FK", 10);   // F1–F10
        (int Code, string Name)[] fixedNames =
        [
            (9, "ESC"), (22, "BKSP"), (23, "TAB"), (36, "RTRN"), (37, "LCTL"), (49, "TLDE"), (50, "LFSH"), (51, "BKSL"),
            (62, "RTSH"), (63, "KPMU"), (64, "LALT"), (65, "SPCE"), (66, "CAPS"), (77, "NMLK"), (78, "SCLK"),
            (79, "KP7"), (80, "KP8"), (81, "KP9"), (82, "KPSU"), (83, "KP4"), (84, "KP5"), (85, "KP6"), (86, "KPAD"),
            (87, "KP1"), (88, "KP2"), (89, "KP3"), (90, "KP0"), (91, "KPDL"), (92, "LVL3"), (94, "LSGT"),
            (95, "FK11"), (96, "FK12"), (97, "AB11"), (98, "KATA"), (99, "HIRA"), (100, "HENK"), (101, "HKTG"),
            (102, "MUHE"), (103, "JPCM"), (104, "KPEN"), (105, "RCTL"), (106, "KPDV"), (107, "PRSC"), (108, "RALT"),
            (109, "LNFD"), (110, "HOME"), (111, "UP"), (112, "PGUP"), (113, "LEFT"), (114, "RGHT"), (115, "END"),
            (116, "DOWN"), (117, "PGDN"), (118, "INS"), (119, "DELE"), (121, "MUTE"), (122, "VOL-"), (123, "VOL+"),
            (124, "POWR"), (125, "KPEQ"), (127, "PAUS"), (130, "HNGL"), (131, "HJCV"), (132, "AE13"), (133, "LWIN"),
            (134, "RWIN"), (135, "COMP"), (136, "STOP"), (137, "AGAI"), (138, "PROP"), (139, "UNDO"), (140, "FRNT"),
            (141, "COPY"), (142, "OPEN"), (143, "PAST"), (144, "FIND"), (145, "CUT"), (146, "HELP"),
        ];
        foreach ((int code, string name) in fixedNames)
        {
            names[code] = name;
        }
        return names;
    }
}
