using VelaShell.XServer;

namespace VelaShell.Services.XServer;

/// <summary>
/// 按宿主系统当前的键盘布局推出的一份键位表:主键区(数字行到斜杠)与 102 键,每键四层 ——
/// 无修饰、Shift、AltGr(macOS 上是 Option)、Shift+AltGr。
/// </summary>
/// <param name="PerKeycode">每键码的列数:没有第三、四层时 2,有时 6(按 XKB 规范 §17 的核心列序)。</param>
/// <param name="Main">键码 <see cref="HostKeymap.FirstKeycode" /> 起的主键区键值。</param>
/// <param name="IntlBackslash">102 键(<see cref="XKeycodes.IntlBackslash" />)的键值。</param>
/// <param name="Layout">
/// XKB 布局名(发布给 setxkbmap 之类的工具看):手选的布局就是它;跟随系统时取随程序带的表里第一、二层最像的那个(见 <see cref="HostKeymap.ClosestLayout" />)。
/// </param>
internal sealed record HostKeymapResult(int PerKeycode, uint[] Main, uint[] IntlBackslash, string Layout)
{
    /// <summary>有 AltGr 层:右 Alt(Option)要设成 ISO_Level3_Shift 并挪进 Mod5。</summary>
    public bool HasAltGr => PerKeycode > 2;

    /// <summary>两份键位表是不是同一个结果(布局没变时不必重推给服务端)。</summary>
    public bool SameAs(HostKeymapResult? other) =>
        other is not null && other.PerKeycode == PerKeycode && other.Main.AsSpan().SequenceEqual(Main)
        && other.IntlBackslash.AsSpan().SequenceEqual(IntlBackslash);

    /// <summary>交给服务端的键位表(<see cref="X11Server.SetKeymap" />):主键区一段加 102 键,有 AltGr 层时右 Alt 当 AltGr。</summary>
    public XKeymap ToXKeymap() =>
        new XKeymap(Layout, PerKeycode) { AltGr = HasAltGr }
            .MapRange(HostKeymap.FirstKeycode, Main)
            .Map(XKeycodes.IntlBackslash, IntlBackslash);
}

/// <summary>
/// 三个平台的键位表推导(<see cref="WindowsKeymap" /> / <see cref="MacKeymap" /> / <see cref="LinuxKeymap" />)共用的部分:
/// 覆盖哪些键码、哪些键与布局无关、字符与死键怎么换成 X 键值、四层怎么排成核心列。
/// </summary>
internal static class HostKeymap
{
    /// <summary>第一段:数字行 1 到斜杠(X 键码 10–61,evdev 2–53)。</summary>
    public const byte FirstKeycode = XKeycodes.D1;

    public const byte LastKeycode = XKeycodes.Slash;

    /// <summary>要按布局推导的键码:主键区一段,再加 102 键。</summary>
    public static IEnumerable<byte> Keycodes()
    {
        for (int keycode = FirstKeycode; keycode <= LastKeycode; keycode++)
        {
            yield return (byte)keycode;
        }
        yield return XKeycodes.IntlBackslash;
    }

    /// <summary>这一段里不打字符的键:与布局无关,照 X 的标准键值。</summary>
    public static (uint, uint)? Fixed(byte keycode) => keycode switch
    {
        XKeycodes.BackSpace => (0xff08, 0xff08),
        XKeycodes.Tab => (0xff09, 0xfe20),          // Tab / ISO_Left_Tab
        XKeycodes.Return => (0xff0d, 0xff0d),
        XKeycodes.ControlLeft => (0xffe3, 0xffe3),
        XKeycodes.ShiftLeft => (0xffe1, 0xffe1),
        _ => null,
    };

    /// <summary>
    /// 按 <see cref="Keycodes" /> 的次序给出每键四层的键值(0 = 没有),排成核心列:没有第三、四层时每键 2 列;
    /// 有时 6 列 —— 组 1 第 1、2 级,组 2 第 1、2 级(照抄组 1),组 1 第 3、4 级。第二、四层缺的照抄第一、三层。
    /// </summary>
    /// <param name="levels">各键四层的键值。</param>
    /// <param name="layout">布局名;null = 按 <see cref="ClosestLayout" /> 推测。</param>
    public static HostKeymapResult Assemble(IReadOnlyList<(uint L1, uint L2, uint L3, uint L4)> levels, string? layout = null)
    {
        bool altGr = levels.Any(k => k.L3 != 0 || k.L4 != 0);
        uint[] Row((uint L1, uint L2, uint L3, uint L4) k)
        {
            uint l2 = k.L2 == 0 ? k.L1 : k.L2, l4 = k.L4 == 0 ? k.L3 : k.L4;
            return altGr ? [k.L1, l2, k.L1, l2, k.L3, l4] : [k.L1, l2];
        }
        int keys = LastKeycode - FirstKeycode + 1;
        return new HostKeymapResult(altGr ? 6 : 2, [.. levels.Take(keys).SelectMany(Row)], Row(levels[keys]), layout ?? ClosestLayout(levels));
    }

    /// <summary>
    /// 设置里手选的布局:按随程序带的键位表(<see cref="BundledKeymaps" />)推出;表里没有这个布局名时返回 null。
    /// 与布局无关的固定键照 <see cref="Fixed" />,不取表里的值。
    /// </summary>
    public static HostKeymapResult? FromBundled(string layout)
    {
        if (!BundledKeymaps.Layouts.TryGetValue(layout, out uint[]? table))
        {
            return null;
        }
        byte[] keycodes = [.. Keycodes()];
        List<(uint, uint, uint, uint)> levels = [];
        for (int i = 0; i < keycodes.Length; i++)
        {
            levels.Add(Fixed(keycodes[i]) is { } fixedSyms
                ? (fixedSyms.Item1, fixedSyms.Item2, 0u, 0u)
                : (table[i * 4], table[(i * 4) + 1], table[(i * 4) + 2], table[(i * 4) + 3]));
        }
        return Assemble(levels, layout);
    }

    /// <summary>
    /// 跟随系统时的布局名:随程序带的表里,主键区第一、二层(无修饰与 Shift)与 <paramref name="levels" /> 一样的键最多的那个。
    /// 一样的键不到九成时认不出来,按 <c>us</c> 报(服务端起步的布局)。
    /// </summary>
    public static string ClosestLayout(IReadOnlyList<(uint L1, uint L2, uint L3, uint L4)> levels)
    {
        byte[] keycodes = [.. Keycodes()];
        (string Name, int Score) best = ("us", 0);
        foreach ((string name, uint[] table) in BundledKeymaps.Layouts)
        {
            int score = 0;
            for (int i = 0; i < keycodes.Length && i < levels.Count; i++)
            {
                if (table[i * 4] == levels[i].L1 && table[(i * 4) + 1] == (levels[i].L2 == 0 ? levels[i].L1 : levels[i].L2))
                {
                    score++;
                }
            }
            if (score > best.Score)
            {
                best = (name, score);
            }
        }
        return best.Score * 10 >= keycodes.Length * 9 ? best.Name : "us";
    }

    /// <summary>字符 → X 键值:Latin-1 可打印字符就是它本身,其余用 Unicode 键值(0x01000000 + 码位);控制字符为 0。</summary>
    public static uint Keysym(char c) =>
        c is < ' ' or ((char)0x7f) ? 0
        : c is <= (char)0x7e or >= (char)0xa0 and <= (char)0xff ? c : 0x01000000u | c;

    /// <summary>死键打出的附加符号 → X 的 dead_* 键值;不认识的死键按普通字符给(至少能打出那个符号)。</summary>
    public static uint DeadKeysym(char c) => c switch
    {
        '`' => 0xfe50,                      // dead_grave
        '´' or '\'' => 0xfe51,              // dead_acute
        '^' or 'ˆ' => 0xfe52,               // dead_circumflex(macOS 给的是 U+02C6)
        '~' or '˜' => 0xfe53,               // dead_tilde(macOS 给的是 U+02DC)
        '¯' => 0xfe54,                      // dead_macron
        '˘' => 0xfe55,                      // dead_breve
        '˙' => 0xfe56,                      // dead_abovedot
        '¨' or '"' => 0xfe57,               // dead_diaeresis
        '˚' or '°' => 0xfe58,               // dead_abovering
        '˝' => 0xfe59,                      // dead_doubleacute
        'ˇ' => 0xfe5a,                      // dead_caron
        '¸' => 0xfe5b,                      // dead_cedilla
        '˛' => 0xfe5c,                      // dead_ogonek
        _ => Keysym(c),
    };
}
