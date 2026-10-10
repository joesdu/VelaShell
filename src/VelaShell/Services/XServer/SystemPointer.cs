using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;

namespace VelaShell.Services.XServer;

/// <summary>
/// 挪系统光标、把它关在一块矩形里(X 程序抓着指针时的 WarpPointer 与 confine-to,见 <see cref="AvaloniaXServerHost.PointerWarped" />)。
/// 坐标一律是物理像素的屏幕坐标。Windows:SetCursorPos / ClipCursor;Linux 的 X11 桌面:另开一条到桌面 X 服务端的连接发 XWarpPointer
/// (Wayland 不许程序挪指针,XWayland 会忽略;关光标不做 —— 要抓指针,会和 Avalonia 自己的输入打架)。macOS 先不做:
/// CGWarpMouseCursorPosition 的坐标按点、原点在主显示器左上,与这里的物理像素怎么换要在实机上核对。做不到时返回 false。
/// </summary>
internal static partial class SystemPointer
{
    public static bool Warp(int x, int y)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return SetCursorPos(x, y);
            }
            if (OperatingSystem.IsLinux())
            {
                return LinuxWarp(x, y);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return false;
    }

    /// <summary>把光标关在 <paramref name="area" /> 里;null = 放开。只有 Windows 做得到。</summary>
    public static bool Confine(PixelRect? area)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }
        try
        {
            if (area is not { } a)
            {
                return ClipCursor(0);
            }
            Rect32 rect = new() { Left = a.X, Top = a.Y, Right = a.Right, Bottom = a.Bottom };
            return ClipCursor(ref rect);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left, Top, Right, Bottom;
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCursorPos(int x, int y);

    [SupportedOSPlatform("windows")]
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClipCursor(ref Rect32 rect);

    [SupportedOSPlatform("windows")]
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClipCursor(nint none);

    // ------------------------------------------------------------------ Linux(X11 桌面)

    private static nint s_display;
    private static bool s_displayTried;

    private static bool LinuxWarp(int x, int y)
    {
        if (!s_displayTried)
        {
            s_displayTried = true;
            s_display = Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 } ? XOpenDisplay(0) : 0;
        }
        if (s_display == 0)
        {
            return false;
        }
        XWarpPointer(s_display, 0, XDefaultRootWindow(s_display), 0, 0, 0, 0, x, y);
        XFlush(s_display);
        return true;
    }

    [LibraryImport("libX11.so.6")]
    private static partial nint XOpenDisplay(nint name);

    [LibraryImport("libX11.so.6")]
    private static partial nint XDefaultRootWindow(nint display);

    [LibraryImport("libX11.so.6")]
    private static partial int XWarpPointer(nint display, nint srcWindow, nint dstWindow, int srcX, int srcY, uint srcWidth, uint srcHeight, int dstX, int dstY);

    [LibraryImport("libX11.so.6")]
    private static partial int XFlush(nint display);
}
