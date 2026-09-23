namespace VelaShell.Core.XServer;

/// <summary>
/// <see cref="Models.XServerOptions.WindowMode" /> 的合法取值。顺序即设置页下拉的条目顺序。
/// </summary>
public static class XServerWindowModes
{
    /// <summary>每个顶层 X 窗口一个 Windows 原生窗口(<c>-multiwindow</c>)。默认。</summary>
    public const string MultiWindow = "multiwindow";

    /// <summary>整个 X 屏幕在一个带标题栏的大窗口里(VcXsrv 不带模式参数时的行为)。</summary>
    public const string Windowed = "windowed";

    /// <summary>一个没有标题栏与边框的大窗口(<c>-nodecoration</c>)。</summary>
    public const string NoDecoration = "nodecoration";

    /// <summary>全屏(<c>-fullscreen</c>)。</summary>
    public const string Fullscreen = "fullscreen";

    /// <summary>透明根窗口,窗口装饰交给远端的窗口管理器(<c>-rootless</c>)。</summary>
    public const string Rootless = "rootless";

    /// <summary>全部取值,按下拉顺序。</summary>
    public static IReadOnlyList<string> All { get; } =
        [MultiWindow, Windowed, NoDecoration, Fullscreen, Rootless];
}
