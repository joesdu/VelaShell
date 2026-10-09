using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using VelaShell.Core.Resources;

namespace VelaShell.Services.XServer;

/// <summary>
/// X 窗口系统菜单(Windows 上标题栏右键 / Alt+空格)里加的几项:截图复制到剪贴板、截图另存为(xs_plan F20)。
/// 放在系统菜单里:X 窗口里的按键与鼠标都归 X 程序,宿主在窗口里加快捷键会抢掉程序自己的键;系统菜单是窗口管理器的地盘。
/// macOS 与 Linux 的宿主窗口拿不到系统菜单,那里不加(截图的入口留给 X 程序清单)。
/// </summary>
internal static partial class WindowSystemMenu
{
    // WM_SYSCOMMAND 的命令号:低 4 位归系统用,要小于 0xF000(SC_SIZE 起是系统命令)。
    private const uint CopyScreenshotCommand = 0x1F10, SaveScreenshotCommand = 0x1F20;
    private const uint MfString = 0x0, MfSeparator = 0x800;

    /// <summary>给窗口的系统菜单加上截图两项,选中时调 <paramref name="copy" /> / <paramref name="save" />。做不到时返回 false。</summary>
    public static bool Attach(Window window, Action copy, Action save)
    {
        if (!OperatingSystem.IsWindows() || window.TryGetPlatformHandle() is not { } handle)
        {
            return false;
        }
        try
        {
            return AttachWindows(window, handle.Handle, copy, save);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool AttachWindows(Window window, nint hwnd, Action copy, Action save)
    {
        nint menu = GetSystemMenu(hwnd, false);
        if (menu == 0
            || !AppendMenuW(menu, MfSeparator, 0, null)
            || !AppendMenuW(menu, MfString, CopyScreenshotCommand, Strings.Get("XServer_CopyScreenshot"))
            || !AppendMenuW(menu, MfString, SaveScreenshotCommand, Strings.Get("XServer_SaveScreenshot")))
        {
            return false;
        }
        // 钩子归 Win32WindowChrome 一家(一个窗口只能有一个 WndProc 钩子,见 WndProcHookOwnershipTests)。
        Views.Win32WindowChrome.AddSystemCommandHandler(window, command =>
        {
            if (command is not (CopyScreenshotCommand or SaveScreenshotCommand))
            {
                return false;
            }
            // 不在窗口过程里直接做:另存为要开对话框,放到 UI 线程的下一拍。
            Avalonia.Threading.Dispatcher.UIThread.Post(command == CopyScreenshotCommand ? copy : save);
            return true;
        });
        return true;
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("user32.dll")]
    private static partial nint GetSystemMenu(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool revert);

    [SupportedOSPlatform("windows")]
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenuW(nint menu, uint flags, nuint id, string? text);
}
