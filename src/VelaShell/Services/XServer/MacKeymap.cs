using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using VelaShell.XServer;

namespace VelaShell.Services.XServer;

/// <summary>
/// 按 macOS 当前的键盘布局算出内置 X 服务端的键位表:主键区与 102 键,四层是无修饰、Shift、Option、Shift+Option
/// (Option 在 macOS 上就是打第三、四层字符的那个键,对应 X 的 AltGr)。键码范围、固定键与列序见 <see cref="HostKeymap" />。
/// </summary>
/// <remarks>
/// <para>
/// 取当前键盘布局(<c>TISCopyCurrentKeyboardLayoutInputSource</c> —— 当前输入源是拼音这类输入法时,给出的是它底下的键盘布局)
/// 的 <c>uchr</c> 数据,对每个 X 键码的物理位置用 <c>UCKeyTranslate</c> 按四种修饰状态翻译。X 键码按物理位置编号,
/// macOS 的虚拟键码(Carbon 的 <c>kVK_*</c>)同样是物理位置,两者之间是一张固定的表。
/// </para>
/// <para>
/// 死键:翻译得到空串而死键状态非零时,在那个状态下再翻译一次空格键,得到附加符号本身(´ ˆ ¨ ˜ `),换成 X 的 dead_* 键值。
/// 美式布局在 Option 层也有字符(å ∫ ç …),所以 macOS 上通常都有第三、四层:右 Option 成为 ISO_Level3_Shift,左 Option 仍是 Alt。
/// </para>
/// </remarks>
internal static partial class MacKeymap
{
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    /// <summary>UCKeyTranslate 的修饰状态是 Carbon 事件修饰位右移 8 位:shiftKey(1 &lt;&lt; 9)→ 2,optionKey(1 &lt;&lt; 11)→ 8。</summary>
    private const uint ShiftState = 0x02, OptionState = 0x08;

    private const ushort KeyActionDown = 0;
    private const ushort VirtualKeySpace = 0x31;

    /// <summary>X 键码 → macOS 虚拟键码(Carbon <c>Events.h</c> 的 kVK_ANSI_* / kVK_ISO_Section);不打字符或没有对应的为 -1。</summary>
    internal static int VirtualKeyFor(byte keycode) => keycode switch
    {
        XKeycodes.D1 => 0x12,
        XKeycodes.D2 => 0x13,
        XKeycodes.D3 => 0x14,
        XKeycodes.D4 => 0x15,
        XKeycodes.D5 => 0x17,
        XKeycodes.D6 => 0x16,
        XKeycodes.D7 => 0x1A,
        XKeycodes.D8 => 0x1C,
        XKeycodes.D9 => 0x19,
        XKeycodes.D0 => 0x1D,
        XKeycodes.Minus => 0x1B,
        XKeycodes.Equal => 0x18,
        XKeycodes.Q => 0x0C,
        XKeycodes.W => 0x0D,
        XKeycodes.E => 0x0E,
        XKeycodes.R => 0x0F,
        XKeycodes.T => 0x11,
        XKeycodes.Y => 0x10,
        XKeycodes.U => 0x20,
        XKeycodes.I => 0x22,
        XKeycodes.O => 0x1F,
        XKeycodes.P => 0x23,
        XKeycodes.BracketLeft => 0x21,
        XKeycodes.BracketRight => 0x1E,
        XKeycodes.A => 0x00,
        XKeycodes.S => 0x01,
        XKeycodes.D => 0x02,
        XKeycodes.F => 0x03,
        XKeycodes.G => 0x05,
        XKeycodes.H => 0x04,
        XKeycodes.J => 0x26,
        XKeycodes.K => 0x28,
        XKeycodes.L => 0x25,
        XKeycodes.Semicolon => 0x29,
        XKeycodes.Apostrophe => 0x27,
        XKeycodes.Grave => 0x32,
        XKeycodes.Backslash => 0x2A,
        XKeycodes.Z => 0x06,
        XKeycodes.X => 0x07,
        XKeycodes.C => 0x08,
        XKeycodes.V => 0x09,
        XKeycodes.B => 0x0B,
        XKeycodes.N => 0x2D,
        XKeycodes.M => 0x2E,
        XKeycodes.Comma => 0x2B,
        XKeycodes.Period => 0x2F,
        XKeycodes.Slash => 0x2C,
        XKeycodes.IntlBackslash => 0x0A,   // kVK_ISO_Section:ISO 键盘左 Shift 旁边那个键
        _ => -1,
    };

    /// <summary>按当前键盘布局算出键位表;取不到布局数据时返回 null(沿用服务端的 US 键位表)。</summary>
    [SupportedOSPlatform("macos")]
    public static unsafe HostKeymapResult? Build()
    {
        nint source = TISCopyCurrentKeyboardLayoutInputSource();
        if (source == 0)
        {
            return null;
        }
        try
        {
            nint data = TISGetInputSourceProperty(source, PropertyKey("kTISPropertyUnicodeKeyLayoutData"));
            byte* layout = data == 0 ? null : CFDataGetBytePtr(data);
            if (layout == null)
            {
                return null;
            }
            uint keyboardType = LMGetKbdType();
            return HostKeymap.Assemble([.. HostKeymap.Keycodes().Select(keycode =>
            {
                if (HostKeymap.Fixed(keycode) is { } fixedSyms)
                {
                    return (fixedSyms.Item1, fixedSyms.Item2, 0u, 0u);
                }
                int vk = VirtualKeyFor(keycode);
                return vk < 0 ? default : (Translate(layout, (ushort)vk, 0, keyboardType), Translate(layout, (ushort)vk, ShiftState, keyboardType),
                    Translate(layout, (ushort)vk, OptionState, keyboardType), Translate(layout, (ushort)vk, ShiftState | OptionState, keyboardType));
            })]);
        }
        finally
        {
            CFRelease(source);
        }
    }

    /// <summary>一种修饰状态下这个键打出的键值;死键给出 dead_* 键值,打不出字符为 0。</summary>
    [SupportedOSPlatform("macos")]
    private static unsafe uint Translate(byte* layout, ushort vk, uint modifiers, uint keyboardType)
    {
        char* buffer = stackalloc char[8];
        uint dead = 0;
        nuint length = 0;
        if (UCKeyTranslate(layout, vk, KeyActionDown, modifiers, keyboardType, 0, &dead, 8, &length, buffer) != 0)
        {
            return 0;
        }
        if (length == 0 && dead != 0)
        {
            // 死键:在这个死键状态下再按一次空格,得到附加符号本身。
            if (UCKeyTranslate(layout, VirtualKeySpace, KeyActionDown, 0, keyboardType, 0, &dead, 8, &length, buffer) != 0 || length == 0)
            {
                return 0;
            }
            return HostKeymap.DeadKeysym(buffer[0]);
        }
        return length == 0 ? 0 : HostKeymap.Keysym(buffer[0]);
    }

    /// <summary>
    /// TIS 的属性键是 Carbon 导出的全局 CFStringRef 变量:取变量地址再读出它的值。
    /// </summary>
    [SupportedOSPlatform("macos")]
    private static nint PropertyKey(string name)
    {
        nint carbon = NativeLibrary.Load(Carbon);
        return Marshal.ReadIntPtr(NativeLibrary.GetExport(carbon, name));
    }

    [LibraryImport(Carbon)]
    private static partial nint TISCopyCurrentKeyboardLayoutInputSource();

    [LibraryImport(Carbon)]
    private static partial nint TISGetInputSourceProperty(nint source, nint key);

    [LibraryImport(Carbon)]
    private static partial byte LMGetKbdType();

    [LibraryImport(Carbon)]
    private static partial int UCKeyTranslate(byte* layout, ushort virtualKeyCode, ushort keyAction, uint modifierKeyState,
        uint keyboardType, uint keyTranslateOptions, uint* deadKeyState, nuint maxStringLength, nuint* actualStringLength, char* unicodeString);

    [LibraryImport(CoreFoundation)]
    private static partial byte* CFDataGetBytePtr(nint data);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint obj);
}
