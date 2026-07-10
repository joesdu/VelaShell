using System.Runtime.InteropServices;

namespace VelaShell.Services;

/// <summary>
/// 系统提示音(设置 → 常规 → 声音提示):Windows 上用 user32 MessageBeep,
/// 无外部依赖;其他平台静默。
/// </summary>
public static class SystemSound
{
    public static void Alert()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        try
        {
            MessageBeep(0x30 /* MB_ICONWARNING */);
        }
        catch
        {
            // 提示音失败无关紧要。
        }
    }

    [DllImport("user32.dll")]
    private static extern bool MessageBeep(uint type);
}
