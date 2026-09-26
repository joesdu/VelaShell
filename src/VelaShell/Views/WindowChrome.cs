using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;

namespace VelaShell.Views;

/// <summary>窗口在外框上的角色,决定各平台用哪种外框(见 <see cref="WindowChrome" />)。</summary>
internal enum WindowChromeKind
{
    /// <summary>主窗口:只有 macOS 改用系统标题栏按钮(红绿灯),其余平台沿用自绘无边框。</summary>
    Main,

    /// <summary>模态对话框(<c>ShowDialog</c>):卡片外观,macOS 上不显示红绿灯。</summary>
    Dialog,

    /// <summary>非模态窗口(<c>Show</c>):卡片外观,macOS 上显示红绿灯;最大化 / 全屏时卡片铺满。</summary>
    Tool
}

/// <summary>窗口外框按哪个平台的机制实现。</summary>
internal enum ChromePlatform
{
    /// <summary>Windows:透明窗口 + 16px 边距 + 自绘阴影的卡片(DWM 对无边框透明窗口什么都不加)。</summary>
    Windows,

    /// <summary>
    /// macOS:不透明窗口,圆角、阴影与描边由系统画。必须不透明:透明窗口在 macOS 上每帧走全表面 alpha 合成,
    /// 滚动明显掉帧。
    /// </summary>
    MacOS,

    /// <summary>Linux 原生 Wayland:阴影与描边由 Avalonia 的装饰层画,并经 <c>set_window_geometry</c> 告诉合成器真正的窗口范围。</summary>
    LinuxWayland,

    /// <summary>Linux X11(含从 Wayland 回退):不透明直角矩形。</summary>
    LinuxX11
}

/// <summary>
/// 自绘窗体外框的统一入口:按平台设置窗口的装饰模式、透明度与背景,并给窗口挂样式类,
/// 卡片的边距、圆角、描边与阴影由 <c>Themes/WindowChrome.axaml</c> 按类切换。
/// </summary>
/// <remarks>
/// <para>
/// 以前各窗口一律是 <c>WindowDecorations="None"</c> + 透明窗口 + 卡片 <c>Margin="16"</c> + 在这 16px 里画
/// <c>VelaShadowWindow</c>。这只在 Windows 上成立:DWM 对无边框透明窗口什么都不加。Linux 的合成器只知道
/// 整个窗口矩形,会沿它描边、切圆角、做背景模糊,透明边距就成了卡片外面的一圈「空白」;没有合成器、
/// 不支持透明的 X11(WSLg 即是)则把那圈边距画成实色。macOS 上 <c>None</c> 没有系统阴影和圆角,
/// 为了滚动流畅又只能不透明,结果是没有阴影的直角矩形。所以每个平台改用自己的原生机制,
/// 透明边距的写法只留在 Windows。
/// </para>
/// <para>
/// 挂到窗口上的样式类:
/// <list type="bullet">
///   <item><c>chrome-windows</c> / <c>chrome-macos</c> / <c>chrome-wayland</c> / <c>chrome-x11</c>:平台,四选一;</item>
///   <item><c>chrome-traffic-lights</c>:macOS 上显示系统红绿灯(主窗口与非模态窗口),自绘的窗口按钮
///   (<c>Button.window-caption</c>)隐去,标题栏左侧的让位块(<c>Panel.traffic-light-spacer</c>)显出来;</item>
///   <item><c>window-card-flat</c>:卡片是直角、铺满窗口 —— macOS / X11 恒是,其余平台在最大化 / 全屏时是;
///   贴着卡片四角的子元素(<c>Border.window-card-top</c> / <c>-bottom</c> / <c>-left</c> / <c>-right</c> / <c>-bottom-left</c>)
///   跟着变直角;</item>
///   <item><c>window-fullscreen</c>:全屏,红绿灯随系统标题栏收起,让位块也收起。</item>
/// </list>
/// </para>
/// <para>
/// 依据(Avalonia 12.1.3 源码):macOS 的 <c>BorderOnly</c> 是 <c>Titled | FullSizeContentView</c> 且有系统阴影,
/// 红绿灯只在 <c>Full</c> 时显示;Wayland 的 <c>BorderOnly</c> 会画阴影、描边与缩放抓取区而不画标题栏,
/// 并按阴影宽度调用 <c>xdg_surface.set_window_geometry</c>;X11 后端没有实现阴影范围,也不写
/// <c>_GTK_FRAME_EXTENTS</c>,合成器拿到的仍是整个矩形,所以 X11 只能走不透明矩形。
/// </para>
/// <para>
/// Windows 上不启用 <c>ExtendClientArea</c> / <c>BorderOnly</c>:2026-07 在 Win32 上试过,托管装饰重复画标题与按钮、
/// 按钮点不动,已撤回(plan.md 第 6 节「窗口壳」)。Windows 分支与改造前逐像素一致。
/// </para>
/// </remarks>
internal static class WindowChrome
{
    /// <summary>Windows 上卡片外的透明边距;Wayland 装饰层的阴影宽度取同一个值,阴影的伸展范围才一致。</summary>
    internal const double CardMargin = 16;

    /// <summary>卡片描边宽度。Wayland 上描边由装饰层画在内容区外面。</summary>
    internal const double CardFrame = 1;

    /// <summary>
    /// 全部窗口标题栏(<c>Border.window-titlebar</c>)的高度,与 <c>Themes/WindowChrome.axaml</c> 的样式一致
    /// (WindowChromeTests 钉住)。取 28 与 macOS 的系统标题栏同高;窗口按钮是 27×27 的方块(边长 = 标题栏 − 1px 底边)。
    /// </summary>
    internal const double TitleBarHeight = 28;

    private const string TitleBarClass = "window-titlebar";

    /// <summary>Wayland 装饰主题的资源键(定义在 <c>Themes/WindowChrome.axaml</c>)。</summary>
    internal const string WaylandDecorationsThemeKey = "VelaWaylandWindowDecorations";

    /// <summary>不透明窗口的底色令牌:卡片铺满窗口,只在布局来不及跟上时露出来。</summary>
    private const string OpaqueBackgroundKey = "VelaBgSurface";

    private const string TrafficLightsClass = "chrome-traffic-lights",
        CardFlatClass = "window-card-flat",
        FullScreenClass = "window-fullscreen";

    private static readonly string[] PlatformClasses = ["chrome-windows", "chrome-macos", "chrome-wayland", "chrome-x11"];

    private static readonly ConditionalWeakTable<Window, ChromeState> States = [];

    /// <summary>按当前平台给窗口装上外框。在窗口构造函数的 <c>InitializeComponent()</c> 之后调用。</summary>
    /// <param name="window">目标窗口。XAML 里不要再写 <c>WindowDecorations</c>、<c>TransparencyLevelHint</c>、
    /// <c>Background</c>,以及卡片的 <c>Margin</c> / <c>CornerRadius</c> / <c>BorderThickness</c> / <c>BoxShadow</c>:
    /// 本地值优先级高于样式,留着的话按平台切换就不生效。</param>
    /// <param name="kind">窗口在外框上的角色。</param>
    /// <param name="resizeGrips">
    /// 自绘的缩放抓取区(可选)。由这里统一决定显隐:只在普通态、且系统不提供边缘缩放时显示
    /// (macOS 的系统外框、Wayland 的装饰层都自带)。传入的是按 <c>Tag</c> 标边的 <see cref="Panel" /> 时,
    /// 厚度也由这里按卡片形态调整(见 <see cref="ResizeGripLayout" />)。
    /// </param>
    public static void Apply(Window window, WindowChromeKind kind, Control? resizeGrips = null) =>
        Apply(window, kind, Detect(window), resizeGrips);

    /// <summary>按指定平台装外框。显式传平台只为无头测试覆盖四个分支;可以对同一窗口重复调用。</summary>
    internal static void Apply(Window window, WindowChromeKind kind, ChromePlatform platform, Control? resizeGrips = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ChromeState state = States.GetOrCreateValue(window);

        foreach (string platformClass in PlatformClasses)
        {
            _ = window.Classes.Remove(platformClass);
        }
        window.Classes.Add(ClassOf(platform));
        window.Classes.Set(TrafficLightsClass, platform == ChromePlatform.MacOS && kind != WindowChromeKind.Dialog);

        if (kind == WindowChromeKind.Main)
        {
            // 主窗口只有 macOS 换外框:原生红绿灯、系统圆角与阴影;自绘的三个窗口按钮由 TitleBarView 按类隐去。
            // 其余平台与 XAML 里的自绘无边框一致(Windows 的 DWM 框架语义由 Win32WindowChrome 补回)。
            bool native = platform == ChromePlatform.MacOS;
            window.WindowDecorations = native ? WindowDecorations.Full : WindowDecorations.None;
            window.ExtendClientAreaToDecorationsHint = native;
            window.WindowDecorationsTheme = null;
        }
        else
        {
            (WindowDecorations decorations, bool extend, bool transparent) = platform switch
            {
                ChromePlatform.Windows => (WindowDecorations.None, false, true),
                // 对话框:系统圆角与阴影、不显示红绿灯;非模态窗口:显示红绿灯。扩展客户区后内容铺满整个窗口,
                // 起于标题栏区域的鼠标事件也会转回 Avalonia。
                ChromePlatform.MacOS => (kind == WindowChromeKind.Dialog ? WindowDecorations.BorderOnly : WindowDecorations.Full, true, false),
                // 不扩展客户区(Avalonia 的「强制模式」):装饰层画在内容外面,Width/Height 只算内容。
                ChromePlatform.LinuxWayland => (WindowDecorations.BorderOnly, false, true),
                _ => (WindowDecorations.None, false, false)
            };
            window.WindowDecorations = decorations;
            window.ExtendClientAreaToDecorationsHint = extend;
            window.TransparencyLevelHint = transparent ? [WindowTransparencyLevel.Transparent] : [WindowTransparencyLevel.None];
            if (transparent)
            {
                window.Background = Brushes.Transparent;
            }
            else
            {
                // 不透明窗口要有不透明底色,否则未被卡片盖住的地方露黑。走资源绑定,换主题时跟着变。
                _ = window.Bind(TemplatedControl.BackgroundProperty, window.GetResourceObservable(OpaqueBackgroundKey));
            }
            window.WindowDecorationsTheme = platform == ChromePlatform.LinuxWayland
                && window.TryFindResource(WaylandDecorationsThemeKey, out object? theme)
                    ? theme as ControlTheme
                    : null;
        }

        // XAML 里的窗口尺寸是 Windows 的:含卡片外 16px 的透明边距。其它平台没有这圈边距,
        // 宽高各扣掉,让卡片的可见尺寸与 Windows 一致。重复调用时先把上一次扣掉的加回来。
        double reduction = kind == WindowChromeKind.Main ? 0 : SizeReduction(platform);
        Shrink(window, reduction - state.SizeReduction);
        state.SizeReduction = reduction;
        state.Platform = platform;
        state.Kind = kind;
        state.ResizeGrips = resizeGrips ?? state.ResizeGrips;

        if (!state.Observing)
        {
            state.Observing = true;
            window.PropertyChanged += (_, e) =>
            {
                if (e.Property == Window.WindowStateProperty)
                {
                    ApplyWindowState(window, state);
                }
                else if (e.Property == Window.WindowDecorationMarginProperty)
                {
                    ApplyTitleBarHeight(window, state);
                }
            };
        }
        ApplyWindowState(window, state);
        ApplyTitleBarHeight(window, state);
    }

    /// <summary>
    /// 标题栏(<c>Border.window-titlebar</c>)该有多高。
    /// </summary>
    /// <remarks>
    /// 全部窗口一个高度 <see cref="TitleBarHeight" />,由 <c>Themes/WindowChrome.axaml</c> 的样式给出。
    /// 例外是 macOS 上显示红绿灯的窗口:红绿灯由系统按它自己的标题栏垂直居中,位置改不了 —— Avalonia 12.1.3 没有接口,
    /// <c>ExtendClientAreaTitleBarHeightHint</c> 只改标题栏背景材质的高度;拿 Objective-C 运行时硬挪,系统在缩放、全屏时又会排回去。
    /// 所以反过来让标题栏跟系统标题栏同高,两者的中线才对齐。系统标题栏的实际高度就是
    /// <see cref="Window.WindowDecorationMargin" /> 的 Top(Full + 扩展客户区时由 Avalonia 从 <c>NSTitlebarContainerView</c> 量出来),
    /// 不写死:哪天 macOS 改了标题栏高度,这里跟着走。全屏时它是 0(系统标题栏收起),保持原高度,免得进出全屏时内容上下跳。
    /// </remarks>
    /// <param name="trafficLights">窗口是否显示系统红绿灯。</param>
    /// <param name="systemTitleBarHeight">系统标题栏的实际高度;没有系统标题栏时为 0。</param>
    /// <param name="current">标题栏当前高度;NaN 表示还没设过(用样式给的 <see cref="TitleBarHeight" />)。</param>
    /// <returns>要设的高度;null 表示不设本地值,用样式。</returns>
    internal static double? TitleBarHeightFor(bool trafficLights, double systemTitleBarHeight, double current) =>
        !trafficLights ? null
        : systemTitleBarHeight > 0 ? systemTitleBarHeight
        : double.IsNaN(current) ? null : current;

    private static void ApplyTitleBarHeight(Window window, ChromeState state)
    {
        bool trafficLights = state.Platform == ChromePlatform.MacOS && state.Kind != WindowChromeKind.Dialog;
        foreach (Border bar in window.GetLogicalDescendants().OfType<Border>().Where(border => border.Classes.Contains(TitleBarClass)))
        {
            // 本地值优先于样式:要跟系统标题栏走时设本地值,否则清掉,回到样式的 28。
            double current = bar.IsSet(Layoutable.HeightProperty) ? bar.Height : double.NaN;
            if (TitleBarHeightFor(trafficLights, window.WindowDecorationMargin.Top, current) is { } height)
            {
                bar.Height = height;
            }
            else
            {
                bar.ClearValue(Layoutable.HeightProperty);
            }
        }
    }

    /// <summary>判断窗口所在的平台。Linux 上按窗口句柄区分 X11 与 Wayland。</summary>
    /// <remarks>
    /// X11 后端在窗口构造时就建好了句柄,描述符是 <c>"XID"</c>;Wayland 后端不提供句柄(<c>Handle => null</c>)。
    /// 没有句柄时再看 <c>WAYLAND_DISPLAY</c>:无头测试之类既不是 X11 也不是 Wayland 的场合归到 X11,
    /// 那是最保守的外观(不透明直角矩形,不依赖合成器做任何事)。
    /// </remarks>
    internal static ChromePlatform Detect(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (OperatingSystem.IsWindows())
        {
            return ChromePlatform.Windows;
        }
        if (OperatingSystem.IsMacOS())
        {
            return ChromePlatform.MacOS;
        }
        if (window.TryGetPlatformHandle()?.HandleDescriptor == "XID")
        {
            return ChromePlatform.LinuxX11;
        }
        return string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
            ? ChromePlatform.LinuxX11
            : ChromePlatform.LinuxWayland;
    }

    /// <summary>窗口装外框时用的平台;没经过 <see cref="Apply(Window, WindowChromeKind, Control?)" /> 的窗口返回 null。</summary>
    internal static ChromePlatform? PlatformOf(Window window) =>
        States.TryGetValue(window, out ChromeState? state) ? state.Platform : null;

    /// <summary>
    /// 这扇窗的尺寸比 XAML 里按 Windows 写的少了多少(宽高各):macOS / X11 为 32,Wayland 为 34,其余为 0。
    /// 代码里另有按 Windows 口径写死的尺寸(如高度上限)时,减去它才与卡片的可见尺寸对得上。
    /// </summary>
    internal static double SizeReductionOf(Window window) =>
        States.TryGetValue(window, out ChromeState? state) ? state.SizeReduction : 0;

    /// <summary>
    /// 平台在 <c>Width</c> / <c>Height</c> 之外另加的外框尺寸(两边合计)。只有 Wayland 上卡片窗口的装饰层
    /// (阴影 + 描边)画在内容外面;主窗口与其余平台,窗口尺寸就是全部。算窗口能不能放进工作区时要把它加上。
    /// </summary>
    internal static double OuterFrameSize(Window window) =>
        States.TryGetValue(window, out ChromeState? state)
        && state.Platform == ChromePlatform.LinuxWayland
        && state.Kind != WindowChromeKind.Main
            ? 2 * (CardMargin + CardFrame)
            : 0;

    /// <summary>
    /// 窗口状态变了:最大化 / 全屏时卡片铺满(圆角与阴影留白只在普通态成立,铺满后四周会透出桌面、
    /// 圆角也会在屏幕边缘切出缺口),抓取区让位;全屏时红绿灯收起,让位块跟着收起。
    /// </summary>
    private static void ApplyWindowState(Window window, ChromeState state)
    {
        WindowState windowState = window.WindowState;
        bool expanded = windowState is WindowState.Maximized or WindowState.FullScreen;
        bool flat = state.Kind != WindowChromeKind.Main
            && (state.Platform is ChromePlatform.MacOS or ChromePlatform.LinuxX11 || expanded);
        window.Classes.Set(CardFlatClass, flat);
        window.Classes.Set(FullScreenClass, windowState == WindowState.FullScreen);

        if (state.ResizeGrips is not { } grips)
        {
            return;
        }
        // macOS 的系统外框自带边缘缩放;Wayland 卡片窗口的装饰层在阴影区里放了缩放抓取区(主窗口在 Wayland 上
        // 是 None,没有装饰层,仍用自绘的)。最大化时窗口边缘就是屏幕边缘,抓取区留着只会挡住内容。
        bool systemResizes = state.Platform == ChromePlatform.MacOS
            || (state.Platform == ChromePlatform.LinuxWayland && state.Kind != WindowChromeKind.Main);
        grips.IsVisible = windowState == WindowState.Normal && !systemResizes;
        if (grips is Panel panel)
        {
            ResizeGripLayout.Apply(panel, rounded: state.Kind != WindowChromeKind.Main && !flat);
        }
    }

    private static string ClassOf(ChromePlatform platform) => platform switch
    {
        ChromePlatform.MacOS => "chrome-macos",
        ChromePlatform.LinuxWayland => "chrome-wayland",
        ChromePlatform.LinuxX11 => "chrome-x11",
        _ => "chrome-windows"
    };

    /// <summary>
    /// 卡片窗口的宽高要比 XAML 写的少多少。macOS / X11 没有边距:少两道边距。Wayland 的 <c>Width</c> 只算内容,
    /// 描边画在外面:再少两道描边,可见卡片(含描边)才与 Windows 一样大,整个窗口表面也与 Windows 一样大。
    /// </summary>
    private static double SizeReduction(ChromePlatform platform) => platform switch
    {
        ChromePlatform.Windows => 0,
        ChromePlatform.LinuxWayland => 2 * (CardMargin + CardFrame),
        _ => 2 * CardMargin
    };

    private static void Shrink(Window window, double delta)
    {
        if (delta == 0)
        {
            return;
        }
        // SizeToContent 的那一边是 NaN,由内容决定,卡片边距没了它自然就小了。
        if (!double.IsNaN(window.Width))
        {
            window.Width -= delta;
        }
        if (!double.IsNaN(window.Height))
        {
            window.Height -= delta;
        }
        if (window.MinWidth > 0)
        {
            window.MinWidth = Math.Max(0, window.MinWidth - delta);
        }
        if (window.MinHeight > 0)
        {
            window.MinHeight = Math.Max(0, window.MinHeight - delta);
        }
        if (double.IsFinite(window.MaxWidth))
        {
            window.MaxWidth -= delta;
        }
        if (double.IsFinite(window.MaxHeight))
        {
            window.MaxHeight -= delta;
        }
    }

    private sealed class ChromeState
    {
        public ChromePlatform Platform { get; set; }

        public WindowChromeKind Kind { get; set; }

        /// <summary>已从 XAML 尺寸里扣掉的宽高(各)。</summary>
        public double SizeReduction { get; set; }

        public Control? ResizeGrips { get; set; }

        /// <summary>是否已经在跟踪窗口状态(同一窗口只挂一次)。</summary>
        public bool Observing { get; set; }
    }
}
