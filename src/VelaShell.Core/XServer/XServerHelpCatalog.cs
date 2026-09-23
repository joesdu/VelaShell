namespace VelaShell.Core.XServer;

/// <summary>帮助表里的一条参数。</summary>
/// <param name="Syntax">参数写法(原样,不翻译 —— 那是要照抄进命令行的东西)。</param>
/// <param name="DescriptionKey">说明文字的资源键。</param>
/// <param name="Example">可选的示例(原样,等宽显示);没有为 <see langword="null" />。</param>
public sealed record XServerHelpEntry(string Syntax, string DescriptionKey, string? Example = null);

/// <summary>帮助表里的一组参数。</summary>
/// <param name="TitleKey">分组标题的资源键。</param>
/// <param name="Entries">组内参数,按 VcXsrv <c>-help</c> 的原顺序。</param>
public sealed record XServerHelpGroup(string TitleKey, IReadOnlyList<XServerHelpEntry> Entries);

/// <summary>
/// 设置 → X Server →「附加参数」旁「帮助」对话框的内容:VcXsrv 的全部命令行参数。
/// </summary>
/// <remarks>
/// <para>
/// 内容整理自 VcXsrv 的 <c>-help</c> 输出。整理时做了三件事:按「通用 / XDMCP / VcXsrv 专有」分组;
/// 把原文里重复出现的两条(<c>-[no]compositewm</c>、<c>-noprimary</c> 与 <c>-[no]primary</c>)合并;
/// 把 <c>-screen</c> 的例子拆成单独的等宽示例。
/// </para>
/// <para>
/// 资源键在这里以字面量出现,<c>UnusedLocalizedKeyTests</c> 据此判定它们有人引用;
/// 加条目时五份 resx 要一起加。
/// </para>
/// </remarks>
public static class XServerHelpCatalog
{
    /// <summary>可以在运行时用 <c>+extension</c> / <c>-extension</c> 开关的扩展名(原样,不翻译)。</summary>
    public static IReadOnlyList<string> ToggleableExtensions { get; } =
    [
        "Generic Event Extension", "XTEST", "SECURITY", "XINERAMA", "XFIXES", "XFree86-Bigfont",
        "RENDER", "RANDR", "COMPOSITE", "DAMAGE", "MIT-SCREEN-SAVER", "DOUBLE-BUFFER",
        "RECORD", "DPMS", "X-Resource", "GLX"
    ];

    /// <summary>全部分组,按对话框里的显示顺序。</summary>
    public static IReadOnlyList<XServerHelpGroup> Groups { get; } =
    [
        new("XHelpGroup_General",
        [
            new("-silent-dup-error", "XHelp_SilentDupError"),
            new("-a #", "XHelp_A"),
            new("-ac", "XHelp_Ac"),
            new("-audit int", "XHelp_Audit"),
            new("-auth file", "XHelp_Auth"),
            new("-br", "XHelp_Br"),
            new("+bs", "XHelp_BsOn"),
            new("-bs", "XHelp_BsOff"),
            new("-cc int", "XHelp_Cc"),
            new("-nocursor", "XHelp_NoCursor"),
            new("-core", "XHelp_Core"),
            new("-displayfd fd", "XHelp_DisplayFd"),
            new("-dpi [auto|int]", "XHelp_Dpi"),
            new("-dpms", "XHelp_Dpms"),
            new("-deferglyphs [none|all|16]", "XHelp_DeferGlyphs"),
            new("-f #", "XHelp_F"),
            new("-fakescreenfps #", "XHelp_FakeScreenFps"),
            new("-fp string", "XHelp_Fp"),
            new("-help", "XHelp_Help"),
            new("+iglx", "XHelp_IglxOn"),
            new("-iglx", "XHelp_IglxOff"),
            new("-I", "XHelp_IgnoreRest"),
            new("-maxclients n", "XHelp_MaxClients"),
            new("-nolisten string", "XHelp_NoListen"),
            new("-listen string", "XHelp_Listen"),
            new("-noreset", "XHelp_NoReset"),
            new("-background [none]", "XHelp_Background"),
            new("-reset", "XHelp_Reset"),
            new("-pn", "XHelp_Pn"),
            new("-nopn", "XHelp_NoPn"),
            new("-r", "XHelp_RepeatOff"),
            new("r", "XHelp_RepeatOn"),
            new("-render [default|mono|gray|color]", "XHelp_Render"),
            new("-retro", "XHelp_Retro"),
            new("-seat string", "XHelp_Seat"),
            new("-t #", "XHelp_T"),
            new("-terminate [delay]", "XHelp_Terminate"),
            new("-tst", "XHelp_Tst"),
            new("-wr", "XHelp_Wr"),
            new("+xinerama", "XHelp_XineramaOn"),
            new("-xinerama", "XHelp_XineramaOff"),
            new("-dumbSched", "XHelp_DumbSched"),
            new("-schedInterval int", "XHelp_SchedInterval"),
            new("[+-]accessx [ timeout [ timeout_mask [ feedback [ options_mask ] ] ] ]", "XHelp_AccessX"),
            new("-vmid GUID", "XHelp_VmId"),
            new("-vsockport port", "XHelp_VsockPort"),
        ]),
        new("XHelpGroup_Extensions",
        [
            new("+extension name", "XHelp_ExtensionOn", "+extension RANDR"),
            new("-extension name", "XHelp_ExtensionOff", "-extension GLX"),
        ]),
        new("XHelpGroup_Xdmcp",
        [
            new("-query host-name", "XHelp_Query", "-query 192.168.1.10"),
            new("-broadcast", "XHelp_Broadcast"),
            new("-multicast [addr [hops]]", "XHelp_Multicast"),
            new("-indirect host-name", "XHelp_Indirect"),
            new("-port port-num", "XHelp_Port"),
            new("-from local-address", "XHelp_From"),
            new("-once", "XHelp_Once"),
            new("-class display-class", "XHelp_Class"),
            new("-cookie xdm-auth-bits", "XHelp_Cookie"),
            new("-displayID display-id", "XHelp_DisplayId"),
        ]),
        new("XHelpGroup_VcXsrv",
        [
            new("-[no]clipboard", "XHelp_Clipboard"),
            new("-[no]primary", "XHelp_Primary"),
            new("-clipupdates num_boxes", "XHelp_ClipUpdates"),
            new("-[no]compositewm", "XHelp_CompositeWm"),
            new("-[no]compositealpha", "XHelp_CompositeAlpha"),
            new("-depth bits_per_pixel", "XHelp_Depth"),
            new("-[no]emulate3buttons [timeout]", "XHelp_Emulate3Buttons"),
            new("-engine engine_type_id", "XHelp_Engine"),
            new("-fullscreen", "XHelp_Fullscreen"),
            new("-[no]hostintitle", "XHelp_HostInTitle"),
            new("-icon icon_specifier", "XHelp_Icon"),
            new("-ignoreinput", "XHelp_IgnoreInput"),
            new("-[no]keyhook", "XHelp_KeyHook"),
            new("-lesspointer", "XHelp_LessPointer"),
            new("-logfile filename", "XHelp_LogFile"),
            new("-logverbose verbosity", "XHelp_LogVerbose"),
            new("-[no]multimonitors, -[no]multiplemonitors", "XHelp_MultiMonitors"),
            new("-multiwindow", "XHelp_MultiWindow"),
            new("-nodecoration", "XHelp_NoDecoration"),
            new("-refresh rate_in_Hz", "XHelp_Refresh"),
            new("-resize=none|scrollbars|randr", "XHelp_Resize"),
            new("-rootless", "XHelp_Rootless"),
            new("-screen scr_num [width height [x y] | [[WxH[+X+Y]][@m]] ]", "XHelp_Screen",
                "-screen 0 800x600+100+100@2\n-screen 0 1024x768@3\n-screen 0 @1"),
            new("-swcursor", "XHelp_SwCursor"),
            new("-[no]trayicon", "XHelp_TrayIcon"),
            new("-[no]unixkill", "XHelp_UnixKill"),
            new("-[no]wgl", "XHelp_Wgl"),
            new("-swrastwgl", "XHelp_SwrastWgl"),
            new("-[no]winkill", "XHelp_WinKill"),
            new("-xkblayout XKBLayout", "XHelp_XkbLayout", "-xkblayout de"),
            new("-xkbmodel XKBModel", "XHelp_XkbModel"),
            new("-xkboptions XKBOptions", "XHelp_XkbOptions"),
            new("-xkbrules XKBRules", "XHelp_XkbRules"),
            new("-xkbvariant XKBVariant", "XHelp_XkbVariant", "-xkbvariant nodeadkeys"),
        ]),
    ];
}
