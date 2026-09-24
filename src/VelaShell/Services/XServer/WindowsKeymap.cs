using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using VelaShell.XServer.Host;

namespace VelaShell.Services.XServer;

/// <summary>
/// 按 Windows 当前的键盘布局算出内置 X 服务端的键位表(无修饰、Shift,布局有 AltGr 字符时再加 AltGr、Shift+AltGr):
/// 德语、法语等布局上 X 程序打出的字符与键帽一致。只动打字符的那些键(主键区的数字行到斜杠、102 键的那一个),
/// 其余键(方向、功能、修饰)不变。
/// </summary>
/// <remarks>
/// <para>
/// X 的键码是物理位置(evdev + 8),而主键区 evdev 1–88 的编号与 PC 扫描码(set 1)一致 —— 所以对每个键码,
/// 用 <c>MapVirtualKeyEx</c> 把扫描码换成当前布局下的虚拟键,再用 <c>ToUnicodeEx</c> 取四种修饰状态下打出的字符。
/// </para>
/// <para>
/// 死键(´ ^ ¨ ~ `)给出 X 的 dead_* 键值,组合交给客户端(Xlib 的 Compose / xkbcommon)。有 AltGr 层时,宿主把右 Alt
/// 设成 ISO_Level3_Shift 并放进 Mod5,服务端据此推出四级键类型(德语的 @、€,法语的 #、{ 由此打得出)。
/// </para>
/// </remarks>
internal static partial class WindowsKeymap
{
    /// <summary>第一段:数字行 1 到斜杠(X 键码 10–61,evdev 2–53)。</summary>
    public const byte FirstKeycode = XKeycodes.D1;

    private const byte LastKeycode = XKeycodes.Slash;

    /// <summary>当前线程的键盘布局句柄(同一个布局前后两次取值相同,变了才需要重算)。</summary>
    [SupportedOSPlatform("windows")]
    public static nint CurrentLayout() => GetKeyboardLayout(0);

    /// <summary>
    /// 按 <paramref name="layout" /> 算出键码 <see cref="FirstKeycode" /> 起主键区的键值,以及 102 键(<see cref="XKeycodes.IntlBackslash" />)的。
    /// </summary>
    /// <returns>
    /// 每键码的列数(布局没有 AltGr 字符时 2 列:无修饰、Shift;有时 6 列,按 XKB 规范 §17 的核心列序:
    /// 组 1 第 1、2 级,组 2 第 1、2 级(照抄组 1),组 1 第 3、4 级 —— 第 3、4 级就是 AltGr、Shift+AltGr),以及两段键值。
    /// </returns>
    [SupportedOSPlatform("windows")]
    public static (int PerKeycode, uint[] Main, uint[] IntlBackslash) Build(nint layout)
    {
        const int keys = LastKeycode - FirstKeycode + 1;
        var levels = new (uint L1, uint L2, uint L3, uint L4)[keys + 1];
        bool altGr = false;
        for (int i = 0; i <= keys; i++)
        {
            byte keycode = i < keys ? (byte)(FirstKeycode + i) : XKeycodes.IntlBackslash;
            levels[i] = Fixed(keycode) is { } fixedSyms ? (fixedSyms.Item1, fixedSyms.Item2, 0, 0) : Translate(keycode, layout);
            altGr |= levels[i].L3 != 0 || levels[i].L4 != 0;
        }
        int per = altGr ? 6 : 2;
        uint[] Row((uint L1, uint L2, uint L3, uint L4) k) => altGr ? [k.L1, k.L2, k.L1, k.L2, k.L3, k.L4] : [k.L1, k.L2];
        uint[] main = [.. levels.Take(keys).SelectMany(Row)];
        return (per, main, Row(levels[keys]));
    }

    /// <summary>这一段里不打字符的键:与布局无关,照 X 的标准键值。</summary>
    private static (uint, uint)? Fixed(byte keycode) => keycode switch
    {
        XKeycodes.BackSpace => (0xff08, 0xff08),
        XKeycodes.Tab => (0xff09, 0xfe20),          // Tab / ISO_Left_Tab
        XKeycodes.Return => (0xff0d, 0xff0d),
        XKeycodes.ControlLeft => (0xffe3, 0xffe3),
        XKeycodes.ShiftLeft => (0xffe1, 0xffe1),
        _ => null,
    };

    [SupportedOSPlatform("windows")]
    private static (uint L1, uint L2, uint L3, uint L4) Translate(byte keycode, nint layout)
    {
        uint scancode = (uint)(keycode - 8);
        uint vk = MapVirtualKeyExW(scancode, MapvkVscToVkEx, layout);
        if (vk == 0)
        {
            return default;
        }
        uint lower = KeysymFor(vk, scancode, shift: false, altGr: false, layout);
        uint upper = KeysymFor(vk, scancode, shift: true, altGr: false, layout);
        uint third = KeysymFor(vk, scancode, shift: false, altGr: true, layout);
        uint fourth = KeysymFor(vk, scancode, shift: true, altGr: true, layout);
        return (lower, upper == 0 ? lower : upper, third, fourth == 0 ? third : fourth);
    }

    /// <summary>
    /// 一种修饰状态下这个键打出的键值。AltGr 在 Windows 上就是 Ctrl+Alt;那种状态下打出控制字符(没有 AltGr 字符的键)
    /// 按「没有」算。
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static unsafe uint KeysymFor(uint vk, uint scancode, bool shift, bool altGr, nint layout)
    {
        byte* state = stackalloc byte[256];
        new Span<byte>(state, 256).Clear();
        if (shift)
        {
            state[VkShift] = 0x80;
        }
        if (altGr)
        {
            state[VkControl] = 0x80;
            state[VkMenu] = 0x80;
            state[VkLControl] = 0x80;
            state[VkRMenu] = 0x80;
        }
        char* buffer = stackalloc char[8];
        // 标志位 4:不改动系统的键盘状态(否则死键会把「等下一个键」的状态留在这个线程上)。
        int count = ToUnicodeEx(vk, scancode, state, buffer, 8, 4, layout);
        if (count == 0)
        {
            return 0;
        }
        char c = buffer[0];
        if (altGr && c < ' ')
        {
            return 0;
        }
        return count < 0 ? DeadKeysym(c) : Keysym(c);
    }
    /// <summary>字符 → X 键值:Latin-1 可打印字符就是它本身,其余用 Unicode 键值(0x01000000 + 码位)。</summary>
    internal static uint Keysym(char c) =>
        c is >= (char)0x20 and <= (char)0x7e or >= (char)0xa0 and <= (char)0xff ? c : 0x01000000u | c;

    /// <summary>死键 → X 的 dead_* 键值;不认识的死键按普通字符给(至少能打出那个符号)。</summary>
    internal static uint DeadKeysym(char c) => c switch
    {
        '`' => 0xfe50,           // dead_grave
        '´' or '\'' => 0xfe51,   // dead_acute
        '^' => 0xfe52,           // dead_circumflex
        '~' => 0xfe53,           // dead_tilde
        '¯' => 0xfe54,      // dead_macron
        '˘' => 0xfe55,      // dead_breve
        '˙' => 0xfe56,      // dead_abovedot
        '¨' or '"' => 0xfe57,    // dead_diaeresis
        '˚' or '°' => 0xfe58,   // dead_abovering
        '˝' => 0xfe59,      // dead_doubleacute
        'ˇ' => 0xfe5a,      // dead_caron
        '¸' => 0xfe5b,      // dead_cedilla
        '˛' => 0xfe5c,      // dead_ogonek
        _ => Keysym(c),
    };

    private const uint MapvkVscToVkEx = 3;
    private const int VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkLControl = 0xA2, VkRMenu = 0xA5;

    [LibraryImport("user32.dll")]
    private static partial nint GetKeyboardLayout(uint threadId);

    [LibraryImport("user32.dll", EntryPoint = "MapVirtualKeyExW")]
    private static partial uint MapVirtualKeyExW(uint code, uint mapType, nint layout);

    [LibraryImport("user32.dll")]
    private static unsafe partial int ToUnicodeEx(uint vk, uint scancode, byte* keyState, char* buffer, int bufferSize, uint flags, nint layout);
}
