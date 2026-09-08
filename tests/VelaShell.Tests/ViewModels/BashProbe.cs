using System.Runtime.InteropServices;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 「把一段 shell 代码交给<b>真正的 bash</b> 跑一遍」这类测试共用的两件小事:找到 bash、
/// 把 Windows 路径翻成 bash 认的形式。
/// </summary>
/// <remarks>
/// 提取出来是因为现在有两处需要它:<see cref="PromptHookShellTests" />(OSC 7 目录上报钩子)
/// 与 <see cref="ShellIntegrationShellTests" />(OSC 133 集成片段)。两边都在验 shell 语义 ——
/// C# 只断言得了"字符串里有没有某几个词",拦不住"跑起来才炸"。
/// </remarks>
internal static class BashProbe
{
    /// <summary>找一个能用的 bash:Unix 上就是自带的那份,Windows 上从 PATH 里挑 Git 带的那份。</summary>
    /// <remarks>
    /// Windows 上要<b>跳过</b> <c>%SystemRoot%\System32\bash.exe</c> 与 <c>WindowsApps\bash.exe</c> ——
    /// 那两个是 WSL 的入口,不是 Git for Windows 的 bash。用它跑脚本会掉进 WSL 的文件系统视图,
    /// 临时脚本的路径(<c>/c/Users/…</c>)在那边根本不存在(WSL 是 <c>/mnt/c/…</c>),
    /// 表现是一句莫名其妙的 "No such file or directory"。Git 也不一定装在 Program Files
    /// (本机就在 D:\Git),所以按 PATH 找而不是猜几个固定路径。
    /// </remarks>
    public static string? Find()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return File.Exists("/bin/bash") ? "/bin/bash" : null;
        }
        string system = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0
                || dir.StartsWith(system, StringComparison.OrdinalIgnoreCase)
                || dir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string candidate;
            try
            {
                candidate = Path.Combine(dir, "bash.exe");
            }
            catch (ArgumentException)
            {
                // PATH 里混进了带非法字符的项,跳过就是。
                continue;
            }
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>Windows 的 bash 不认盘符路径,转成 <c>/c/...</c> 形式。</summary>
    public static string ToBashPath(string path) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/')
            : path;
}
