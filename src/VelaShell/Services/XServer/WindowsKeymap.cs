using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using VelaShell.XServer.Host;

namespace VelaShell.Services.XServer;

/// <summary>
/// 按 Windows 当前的键盘布局算出内置 X 服务端的键位表(无修饰、Shift 两列):德语、法语等布局上 X 程序打出的字符与
/// 键帽一致。只动打字符的那些键(主键区的数字行到斜杠、102 键的那一个),其余键(方向、功能、修饰)不变。
/// </summary>
/// <remarks>
/// <para>
/// X 的键码是物理位置(evdev + 8),而主键区 evdev 1–88 的编号与 PC 扫描码(set 1)一致 —— 所以对每个键码,
/// 用 <c>MapVirtualKeyEx</c> 把扫描码换成当前布局下的虚拟键,再用 <c>ToUnicodeEx</c> 取无修饰与按住 Shift 时打出的字符。
/// </para>
/// <para>
/// 死键(´ ^ ¨ ~ `)给出 X 的 dead_* 键值,组合交给客户端(Xlib 的 Compose / xkbcommon)。AltGr 层(德语的 @、€ 一类)
/// 暂不生成:服务端的 XKB 描述目前只推两层。
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
    /// 按 <paramref name="layout" /> 算出键码 <see cref="FirstKeycode" /> 起的两列键值,以及 102 键(<see cref="XKeycodes.IntlBackslash" />)的两列。
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static (uint[] Main, uint[] IntlBackslash) Build(nint layout)
    {
        uint[] main = new uint[(LastKeycode - FirstKeycode + 1) * 2];
        for (byte keycode = FirstKeycode; keycode <= LastKeycode; keycode++)
        {
            (uint lower, uint upper) = Fixed(keycode) ?? Translate(keycode, layout);
            int at = (keycode - FirstKeycode) * 2;
            main[at] = lower;
            main[at + 1] = upper;
        }
        (uint l, uint u) = Translate(XKeycodes.IntlBackslash, layout);
        return (main, [l, u]);
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
    private static (uint Lower, uint Upper) Translate(byte keycode, nint layout)
    {
        uint scancode = (uint)(keycode - 8);
        uint vk = MapVirtualKeyExW(scancode, MapvkVscToVkEx, layout);
        if (vk == 0)
        {
            return (0, 0);
        }
        uint lower = KeysymFor(vk, scancode, shift: false, layout);
        uint upper = KeysymFor(vk, scancode, shift: true, layout);
        return (lower, upper == 0 ? lower : upper);
    }

    [SupportedOSPlatform("windows")]
    private static unsafe uint KeysymFor(uint vk, uint scancode, bool shift, nint layout)
    {
        byte* state = stackalloc byte[256];
        new Span<byte>(state, 256).Clear();
        if (shift)
        {
            state[VkShift] = 0x80;
        }
        char* buffer = stackalloc char[8];
        // 标志位 4:不改动系统的键盘状态(否则死键会把「等下一个键」的状态留在这个线程上)。
        int count = ToUnicodeEx(vk, scancode, state, buffer, 8, 4, layout);
        if (count == 0)
        {
            return 0;
        }
        char c = buffer[0];
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
    private const int VkShift = 0x10;

    [LibraryImport("user32.dll")]
    private static partial nint GetKeyboardLayout(uint threadId);

    [LibraryImport("user32.dll", EntryPoint = "MapVirtualKeyExW")]
    private static partial uint MapVirtualKeyExW(uint code, uint mapType, nint layout);

    [LibraryImport("user32.dll")]
    private static unsafe partial int ToUnicodeEx(uint vk, uint scancode, byte* keyState, char* buffer, int bufferSize, uint flags, nint layout);
}
