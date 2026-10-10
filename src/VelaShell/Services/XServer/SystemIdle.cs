using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VelaShell.Services.XServer;

/// <summary>
/// 本机的屏保与关显示器(X 程序挂起 / 重置屏保时用,见 <see cref="AvaloniaXServerHost.ScreenSaverSuspensionChanged" />)。
/// Windows 用 SetThreadExecutionState(状态跟着调用的线程走,一律在 UI 线程上调);macOS(IOPMAssertion)与 Linux
/// (org.freedesktop.ScreenSaver 的 D-Bus 接口)先不做 —— 前者要实机核对,后者宿主还没有 D-Bus 客户端。做不到时返回 false。
/// </summary>
internal static partial class SystemIdle
{
    private const uint EsSystemRequired = 0x1, EsDisplayRequired = 0x2, EsContinuous = 0x80000000;

    /// <summary>一直不进屏保、不关显示器(<paramref name="inhibit" /> 为真),或恢复正常。</summary>
    public static bool Inhibit(bool inhibit) =>
        OperatingSystem.IsWindows() && Call(inhibit ? EsContinuous | EsDisplayRequired | EsSystemRequired : EsContinuous);

    /// <summary>重置一次空闲计时(相当于用户动了一下)。</summary>
    public static bool Reset() => OperatingSystem.IsWindows() && Call(EsDisplayRequired | EsSystemRequired);

    [SupportedOSPlatform("windows")]
    private static bool Call(uint flags)
    {
        try
        {
            return SetThreadExecutionState(flags) != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll")]
    private static partial uint SetThreadExecutionState(uint flags);
}
