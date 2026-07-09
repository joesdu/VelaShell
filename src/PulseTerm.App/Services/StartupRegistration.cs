using System;
using System.Diagnostics;

namespace PulseTerm.App.Services;

/// <summary>
/// 开机自启动(设置 → 常规 → 启动):Windows 上写 HKCU\...\Run 键,当前用户生效、无需管理员;
/// 其他平台暂不支持(静默忽略)。应用启动与设置保存时各同步一次,保证键值与设置一致。
/// </summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PulseTerm";

    public static void Apply(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
                return;

            if (enabled)
            {
                var exePath = Environment.ProcessPath
                    ?? Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                    key.SetValue(ValueName, $"\"{exePath}\"");
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // 注册表不可写(策略限制等)时静默失败,不影响其余设置生效。
        }
    }
}
