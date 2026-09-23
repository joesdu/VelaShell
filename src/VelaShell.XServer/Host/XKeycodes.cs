// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

namespace VelaShell.XServer.Host;

/// <summary>
/// 宿主注入按键时用的 X 键码(evdev 扫描码 + 8,与 X.Org 在现代 Linux 上的编号一致)。
/// 宿主负责把自己的物理键翻成这里的号;键码 → 字符由服务端的键值表决定(US 布局起步)。
/// </summary>
public static class XKeycodes
{
#pragma warning disable CS1591 // 键名即文档
    public const byte Escape = 9;
    public const byte D1 = 10, D2 = 11, D3 = 12, D4 = 13, D5 = 14, D6 = 15, D7 = 16, D8 = 17, D9 = 18, D0 = 19;
    public const byte Minus = 20, Equal = 21, BackSpace = 22, Tab = 23;
    public const byte Q = 24, W = 25, E = 26, R = 27, T = 28, Y = 29, U = 30, I = 31, O = 32, P = 33;
    public const byte BracketLeft = 34, BracketRight = 35, Return = 36, ControlLeft = 37;
    public const byte A = 38, S = 39, D = 40, F = 41, G = 42, H = 43, J = 44, K = 45, L = 46;
    public const byte Semicolon = 47, Apostrophe = 48, Grave = 49, ShiftLeft = 50, Backslash = 51;
    public const byte Z = 52, X = 53, C = 54, V = 55, B = 56, N = 57, M = 58;
    public const byte Comma = 59, Period = 60, Slash = 61, ShiftRight = 62, KeypadMultiply = 63;
    public const byte AltLeft = 64, Space = 65, CapsLock = 66;
    public const byte F1 = 67, F2 = 68, F3 = 69, F4 = 70, F5 = 71, F6 = 72, F7 = 73, F8 = 74, F9 = 75, F10 = 76;
    public const byte NumLock = 77, ScrollLock = 78;
    public const byte Keypad7 = 79, Keypad8 = 80, Keypad9 = 81, KeypadSubtract = 82;
    public const byte Keypad4 = 83, Keypad5 = 84, Keypad6 = 85, KeypadAdd = 86;
    public const byte Keypad1 = 87, Keypad2 = 88, Keypad3 = 89, Keypad0 = 90, KeypadDecimal = 91;
    public const byte IntlBackslash = 94, F11 = 95, F12 = 96;
    public const byte KeypadEnter = 104, ControlRight = 105, KeypadDivide = 106, PrintScreen = 107, AltRight = 108;
    public const byte Home = 110, Up = 111, PageUp = 112, Left = 113, Right = 114;
    public const byte End = 115, Down = 116, PageDown = 117, Insert = 118, Delete = 119;
    public const byte Pause = 127, SuperLeft = 133, SuperRight = 134, Menu = 135;
#pragma warning restore CS1591
}
