using System.Globalization;
using System.Text;
using VelaShell.Core.Models;

namespace VelaShell.Core.XServer;

/// <summary>
/// 把 <see cref="XServerOptions" /> 翻成 VcXsrv 的命令行参数。
/// </summary>
/// <remarks>
/// <para>
/// <b>每个开关都显式写出两态</b>(<c>-clipboard</c> / <c>-noclipboard</c>),不依赖 VcXsrv 的默认值:
/// 默认值随版本变过(<c>-wgl</c> 就是),显式写出来,设置页上看到的就是实际生效的。
/// </para>
/// <para>
/// <b>附加参数排在最后</b>。VcXsrv 对同一开关是后写的覆盖先写的,所以用户在「附加参数」里写
/// <c>-noclipboard</c> 能盖过页面上勾着的「启用剪贴板」—— 那是有意留的逃生口。
/// </para>
/// </remarks>
public static class XServerCommandLine
{
    /// <summary>组出完整参数表(不含可执行文件本身)。</summary>
    /// <param name="options">X Server 设置。</param>
    /// <param name="displayNumber">已经选定的显示号(自动模式在调用前就要解析成具体的数)。</param>
    /// <param name="logFile">VcXsrv 日志写到哪;<see langword="null" /> = 不指定(VcXsrv 写进自己的临时目录)。</param>
    public static IReadOnlyList<string> Build(XServerOptions options, int displayNumber, string? logFile = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(displayNumber);

        List<string> args = [":" + displayNumber.ToString(CultureInfo.InvariantCulture)];

        switch (options.WindowMode)
        {
            case XServerWindowModes.Windowed:
                break; // 不带模式参数就是一个带标题栏的大窗口
            case XServerWindowModes.NoDecoration:
                args.Add("-nodecoration");
                break;
            case XServerWindowModes.Fullscreen:
                args.Add("-fullscreen");
                break;
            case XServerWindowModes.Rootless:
                args.Add("-rootless");
                break;
            default:
                args.Add("-multiwindow");
                break;
        }

        args.Add(options.Clipboard ? "-clipboard" : "-noclipboard");
        // PRIMARY 只在开了剪贴板时有意义;关着时不写,免得命令行里出现一个看着像生效了的开关。
        if (options.Clipboard)
        {
            args.Add(options.CopyOnSelection ? "-primary" : "-noprimary");
        }

        args.Add(options.KeyHook ? "-keyhook" : "-nokeyhook");
        args.Add(options.NativeOpenGl ? "-wgl" : "-nowgl");
        args.Add(options.ShowTrayIcon ? "-trayicon" : "-notrayicon");

        if (options.DisableAccessControl)
        {
            args.Add("-ac");
        }

        if (!string.IsNullOrWhiteSpace(options.KeyboardLayout))
        {
            args.Add("-xkblayout");
            args.Add(options.KeyboardLayout.Trim());
        }

        if (!string.IsNullOrWhiteSpace(options.KeyboardModel))
        {
            args.Add("-xkbmodel");
            args.Add(options.KeyboardModel.Trim());
        }

        // 显示号被占用时由我们自己报错(启动前就查过端口),不要 VcXsrv 再弹一个模态框。
        args.Add("-silent-dup-error");

        if (!string.IsNullOrEmpty(logFile))
        {
            args.Add("-logfile");
            args.Add(logFile);
        }

        args.AddRange(SplitArguments(options.ExtraArguments));
        return args;
    }

    /// <summary>
    /// 把「附加参数」一栏的文本切成参数:按空白切,双引号内的空白保留,引号本身去掉。
    /// </summary>
    /// <remarks>
    /// 不做反斜杠转义 —— 那一栏里最常见的是 Windows 路径(<c>-logfile "C:\x y\a.log"</c>),
    /// 把 <c>\</c> 当转义符只会把路径切坏。
    /// </remarks>
    public static IReadOnlyList<string> SplitArguments(string? text)
    {
        List<string> result = [];
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        StringBuilder current = new();
        bool inQuotes = false;
        bool hasToken = false;
        foreach (char c in text)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true; // "" 也是一个(空)参数
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
                continue;
            }

            current.Append(c);
            hasToken = true;
        }

        if (hasToken)
        {
            result.Add(current.ToString());
        }
        return result;
    }

    /// <summary>显示号 → 给 X 客户端用的显示地址。</summary>
    public static string DisplayAddress(int displayNumber) =>
        "localhost:" + displayNumber.ToString(CultureInfo.InvariantCulture) + ".0";

    /// <summary>X11 的 TCP 端口:显示 N 在 6000+N。</summary>
    public static int TcpPort(int displayNumber) => 6000 + displayNumber;
}
