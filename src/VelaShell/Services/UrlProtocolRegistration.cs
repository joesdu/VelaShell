using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace VelaShell.Services;

/// <summary>
/// URL 协议关联(设置 → 安全与审计 → 外部登录):把 <c>ssh://</c> 与 <c>sftp://</c> 交给本应用,
/// 于是堡垒机/SSO 网页上那颗「用终端打开」按钮点下去唤起的就是 VelaShell。
/// <para>
/// Windows 只写 <c>HKCU\Software\Classes</c>(当前用户生效、不需要管理员);关闭时**只删指向本应用的键**,
/// 绝不连带清掉 Xshell、MobaXterm 之类别人写的关联 —— 用户在我们这儿关个开关,不该把另一个软件的
/// 关联一起弄没。Linux 走每用户的 <c>.desktop</c> + <c>xdg-mime</c>;macOS 的 scheme 只能由
/// app bundle 的 Info.plist 声明(打包期的事),运行时改不了,静默跳过。
/// </para>
/// </summary>
public static class UrlProtocolRegistration
{
    /// <summary>关联的 scheme 列表。telnet/ftp 不在其中:前者本应用不支持,后者会抢走系统既有关联。</summary>
    private static readonly string[] Schemes = ["ssh", "sftp"];

    /// <summary>标记键:用来认出「这条关联是我们写的」,从而只删自己的。</summary>
    private const string OwnerValueName = "VelaShellManaged";

    /// <summary>按开关同步协议关联;失败静默忽略(权限受限、桌面环境异常都不该影响其它设置生效)。</summary>
    public static async Task ApplyAsync(bool enabled)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                ApplyWindows(enabled);
            }
            else if (OperatingSystem.IsLinux())
            {
                // 设置保存可能发生在上一次注册仍在运行时。串行提交以免后写入的设置被覆盖。
                await LinuxRegistrationGate.WaitAsync().ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                try
                {
                    await ApplyLinuxAsync(enabled).ConfigureAwait(false);
                }
                finally
                {
                    LinuxRegistrationGate.Release();
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[VelaShell] URL protocol registration failed: {ex.Message}");
        }
    }

    /// <summary>当前可执行文件路径;取不到时为空串(调用方据此放弃注册)。</summary>
    private static string ExecutablePath() =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;

    [SupportedOSPlatform("windows")]
    private static void ApplyWindows(bool enabled)
    {
        string exePath = ExecutablePath();
        foreach (string scheme in Schemes)
        {
            string path = $@"Software\Classes\{scheme}";
            if (enabled)
            {
                if (exePath.Length == 0)
                {
                    return;
                }
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(path);
                key.SetValue(null, $"URL:{scheme.ToUpperInvariant()} Protocol");
                key.SetValue("URL Protocol", string.Empty);
                key.SetValue(OwnerValueName, 1, RegistryValueKind.DWord);
                using (RegistryKey icon = key.CreateSubKey("DefaultIcon"))
                {
                    icon.SetValue(null, $"\"{exePath}\",0");
                }
                using RegistryKey command = key.CreateSubKey(@"shell\open\command");
                // 与 Xshell 同形的调用约定:调用方只会替换 %1,不会替我们加引号,所以引号写死在这。
                command.SetValue(null, $"\"{exePath}\" -url \"%1\"");
            }
            else if (IsOurs(path))
            {
                Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            }
        }
    }

    /// <summary>这条 HKCU 关联是不是本应用写的(有我们的标记键即算)。</summary>
    [SupportedOSPlatform("windows")]
    private static bool IsOurs(string path)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(path);
        return key?.GetValue(OwnerValueName) is int marker && marker == 1;
    }

    private static readonly SemaphoreSlim LinuxRegistrationGate = new(1, 1);

    [SupportedOSPlatform("linux")]
    private static async Task ApplyLinuxAsync(bool enabled)
    {
        string applications = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "applications");
        string desktopFile = Path.Combine(applications, "velashell-url-handler.desktop");
        if (!enabled)
        {
            if (File.Exists(desktopFile))
            {
                File.Delete(desktopFile);
                await RunQuietlyAsync("update-desktop-database", [applications]).ConfigureAwait(false);
            }
            return;
        }
        string exePath = ExecutablePath();
        if (exePath.Length == 0)
        {
            return;
        }
        Directory.CreateDirectory(applications);
        string mimeTypes = string.Join(string.Empty, Schemes.Select(static s => $"x-scheme-handler/{s};"));
        File.WriteAllText(desktopFile, $"""
            [Desktop Entry]
            Name=VelaShell (URL handler)
            Comment=Open ssh:// and sftp:// links in VelaShell
            Exec="{exePath}" -url %u
            Icon=velashell
            Type=Application
            Terminal=false
            NoDisplay=true
            StartupWMClass=VelaShell.App
            MimeType={mimeTypes}

            """);
        await RunQuietlyAsync("update-desktop-database", [applications]).ConfigureAwait(false);
        foreach (string scheme in Schemes)
        {
            await RunQuietlyAsync("xdg-mime", ["default", "velashell-url-handler.desktop", $"x-scheme-handler/{scheme}"]).ConfigureAwait(false);
        }
    }

    /// <summary>跑一条桌面环境的辅助命令;装没装、成不成都不影响功能本身(关联文件已经写好了)。</summary>
    private static async Task RunQuietlyAsync(string fileName, string[] arguments)
    {
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
            await Process.RunAsync(fileName, arguments, silent: true, cancellationToken: timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            // 命令不存在(精简发行版)—— 关联文件已落盘,多数桌面重启后仍会认。
        }
    }
}
