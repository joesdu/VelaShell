using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using VelaShell.XServer.Host;

namespace VelaShell.Services.XServer;

/// <summary>
/// 按 Linux 桌面当前的键盘布局算出内置 X 服务端的键位表:主键区与 102 键,四层是 XKB 的第 1–4 级
/// (无修饰、Shift、AltGr、Shift+AltGr)。键码范围、固定键与列序见 <see cref="HostKeymap" />。
/// </summary>
/// <remarks>
/// <para>
/// 桌面的布局从桌面自己的 X 显示(<c>$DISPLAY</c>)读:经 libxkbcommon-x11 取核心键盘设备的完整键位表与当前布局组。
/// Wayland 桌面上 <c>$DISPLAY</c> 是 XWayland,它的键位表由合成器同步,同样是当前布局。桌面的 X 与内置服务端都用
/// evdev + 8 的键码,所以键值按键码原样取,不需要换算;用户在桌面上做过的改键(xmodmap、setxkbmap 的选项)也一并带过来。
/// </para>
/// <para>
/// libxkbcommon / libxkbcommon-x11 / libxcb 是 GTK、Qt 桌面的标配,运行时按名字加载;取不到(没有这些库、没有 <c>$DISPLAY</c>、
/// <c>$DISPLAY</c> 指向的就是内置服务端自己)时返回 null,沿用服务端的 US 键位表。
/// </para>
/// </remarks>
internal static partial class LinuxKeymap
{
    private const string Xcb = "libxcb.so.1";
    private const string XkbCommon = "libxkbcommon.so.0";
    private const string XkbCommonX11 = "libxkbcommon-x11.so.0";

    private const uint StateLayoutEffective = 1 << 7;   // XKB_STATE_LAYOUT_EFFECTIVE
    private const uint IsoLevel3Shift = 0xfe03, IsoLevel3Latch = 0xfe04, IsoLevel3Lock = 0xfe05;

    /// <summary>
    /// 按桌面当前的布局算出键位表。<paramref name="ownDisplay" /> 是内置服务端自己的显示号:<c>$DISPLAY</c> 指向它时不读(那是自己的表)。
    /// </summary>
    [SupportedOSPlatform("linux")]
    public static HostKeymapResult? Build(int ownDisplay)
    {
        string? display = Environment.GetEnvironmentVariable("DISPLAY");
        if (string.IsNullOrEmpty(display) || DisplayNumber(display) == ownDisplay)
        {
            return null;
        }
        try
        {
            return Read();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary><c>$DISPLAY</c> 里的显示号(<c>:1</c>、<c>host:1.0</c>、<c>unix:1</c>);解析不出为 -1。</summary>
    internal static int DisplayNumber(string display)
    {
        int colon = display.LastIndexOf(':');
        if (colon < 0)
        {
            return -1;
        }
        string rest = display[(colon + 1)..];
        int dot = rest.IndexOf('.', StringComparison.Ordinal);
        return int.TryParse(dot < 0 ? rest : rest[..dot], System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out int number) ? number : -1;
    }

    [SupportedOSPlatform("linux")]
    private static unsafe HostKeymapResult? Read()
    {
        nint connection = xcb_connect(null, null);
        if (connection == 0)
        {
            return null;
        }
        nint context = 0, keymap = 0, state = 0;
        try
        {
            if (xcb_connection_has_error(connection) != 0
                || xkb_x11_setup_xkb_extension(connection, 1, 0, 0, null, null, null, null) != 1)
            {
                return null;
            }
            int device = xkb_x11_get_core_keyboard_device_id(connection);
            context = xkb_context_new(0);
            if (device < 0 || context == 0)
            {
                return null;
            }
            keymap = xkb_x11_keymap_new_from_device(context, connection, device, 0);
            state = keymap == 0 ? 0 : xkb_x11_state_new_from_device(keymap, connection, device);
            if (state == 0)
            {
                return null;
            }
            uint group = xkb_state_serialize_layout(state, StateLayoutEffective);
            // 第三、四层只有右 Alt 是 Level3 键时才保留:内置服务端只把右 Alt 当 AltGr。美式 pc105 的 102 键也写着 | ¦,
            // 键位表里还有一个虚拟的 <LVL3> 键固定是 ISO_Level3_Shift,但右 Alt 是 Alt_R,那两层打不出。
            bool level3 = Level(keymap, XKeycodes.AltRight, group, 0) is IsoLevel3Shift or IsoLevel3Latch or IsoLevel3Lock;
            List<(uint, uint, uint, uint)> levels = [];
            foreach (byte keycode in HostKeymap.Keycodes())
            {
                levels.Add(HostKeymap.Fixed(keycode) is { } fixedSyms
                    ? (fixedSyms.Item1, fixedSyms.Item2, 0u, 0u)
                    : (Level(keymap, keycode, group, 0), Level(keymap, keycode, group, 1),
                        level3 ? Level(keymap, keycode, group, 2) : 0, level3 ? Level(keymap, keycode, group, 3) : 0));
            }
            return HostKeymap.Assemble(levels);
        }
        finally
        {
            if (state != 0)
            {
                xkb_state_unref(state);
            }
            if (keymap != 0)
            {
                xkb_keymap_unref(keymap);
            }
            if (context != 0)
            {
                xkb_context_unref(context);
            }
            xcb_disconnect(connection);
        }
    }

    /// <summary>键码在布局组 <paramref name="group" /> 第 <paramref name="level" /> 级(从 0 数)的第一个键值;没有为 0。</summary>
    [SupportedOSPlatform("linux")]
    private static unsafe uint Level(nint keymap, uint keycode, uint group, uint level)
    {
        uint* syms = null;
        return xkb_keymap_key_get_syms_by_level(keymap, keycode, group, level, &syms) > 0 && syms != null ? syms[0] : 0;
    }

    [LibraryImport(Xcb, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial nint xcb_connect(string? displayName, int* screen);

    [LibraryImport(Xcb)]
    private static partial int xcb_connection_has_error(nint connection);

    [LibraryImport(Xcb)]
    private static partial void xcb_disconnect(nint connection);

    [LibraryImport(XkbCommonX11)]
    private static unsafe partial int xkb_x11_setup_xkb_extension(nint connection, ushort major, ushort minor, int flags,
        ushort* majorOut, ushort* minorOut, byte* baseEvent, byte* baseError);

    [LibraryImport(XkbCommonX11)]
    private static partial int xkb_x11_get_core_keyboard_device_id(nint connection);

    [LibraryImport(XkbCommonX11)]
    private static partial nint xkb_x11_keymap_new_from_device(nint context, nint connection, int device, int flags);

    [LibraryImport(XkbCommonX11)]
    private static partial nint xkb_x11_state_new_from_device(nint keymap, nint connection, int device);

    [LibraryImport(XkbCommon)]
    private static partial nint xkb_context_new(int flags);

    [LibraryImport(XkbCommon)]
    private static partial void xkb_context_unref(nint context);

    [LibraryImport(XkbCommon)]
    private static partial void xkb_keymap_unref(nint keymap);

    [LibraryImport(XkbCommon)]
    private static partial void xkb_state_unref(nint state);

    [LibraryImport(XkbCommon)]
    private static partial uint xkb_state_serialize_layout(nint state, uint components);

    [LibraryImport(XkbCommon)]
    private static unsafe partial int xkb_keymap_key_get_syms_by_level(nint keymap, uint key, uint layout, uint level, uint** symsOut);
}
