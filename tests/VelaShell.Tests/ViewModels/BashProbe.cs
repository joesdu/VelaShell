using System.Diagnostics;
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

    /// <summary>
    /// 找一个 <b>4.4 及以上</b>的 bash;没有则返回 null。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>macOS 的 <c>/bin/bash</c> 是 3.2</b> —— Apple 为了躲 GPLv3 把它冻在那儿十几年了。
    /// 3.2 既没有 <c>PS0</c>(4.4 引入),也没有 <c>${VAR@P}</c> 提示符展开(同样 4.4),
    /// 而 <see cref="ShellIntegrationShellTests" /> 两样都要用:前者是被测片段本身的基础,
    /// 后者是"不开 PTY 也能看到提示符实际字节"的手段。
    /// </para>
    /// <para>
    /// <b>所以这条与 <see cref="Find" /> 分开,而不是把 <see cref="Find" /> 收紧。</b>
    /// OSC 7 那段钩子(<see cref="PromptHookShellTests" />)只用到 <c>[[ ]]</c> 与
    /// <c>${var//a/b}</c>,3.2 跑得好好的 —— 收紧 <see cref="Find" /> 会让它在 macOS 上
    /// 从"真跑过"退化成"跳过",白丢一份覆盖。
    /// </para>
    /// <para>
    /// macOS 上装了 Homebrew 的 bash 就在 <c>/opt/homebrew/bin</c>(Apple Silicon)或
    /// <c>/usr/local/bin</c>(Intel),优先于 <c>/bin/bash</c> 试。
    /// </para>
    /// </remarks>
    public static string? FindSupportingPs0()
    {
        foreach (string candidate in Ps0Candidates())
        {
            if (File.Exists(candidate) && IsAtLeast44(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static IEnumerable<string> Ps0Candidates()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows 上就是 Git for Windows 那份(当前是 5.x),没有别的候选。
            if (Find() is { } git)
            {
                yield return git;
            }
            yield break;
        }
        yield return "/opt/homebrew/bin/bash"; // Homebrew, Apple Silicon
        yield return "/usr/local/bin/bash";    // Homebrew, Intel
        yield return "/bin/bash";              // Linux 上就是它;macOS 上是 3.2,会被版本闸挡下
        yield return "/usr/bin/bash";
    }

    /// <summary>问一句候选 bash 的版本够不够 4.4。</summary>
    /// <remarks>
    /// 用 <c>$BASH_VERSINFO</c> 而不是解析 <c>--version</c> 的那行散文:前者是数组、
    /// 各家发行版都一样,后者的措辞会变。
    /// </remarks>
    private static bool IsAtLeast44(string bash)
    {
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = bash,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("printf '%s %s' \"${BASH_VERSINFO[0]}\" \"${BASH_VERSINFO[1]}\"");

            using Process process = Process.Start(psi)!;
            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(15_000))
            {
                return false;
            }
            string[] parts = output.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 2
                   && int.TryParse(parts[0], out int major)
                   && int.TryParse(parts[1], out int minor)
                   && (major > 4 || (major == 4 && minor >= 4));
        }
        catch (Exception)
        {
            // 候选压根不是个能跑的 bash(权限、架构不符…),当它不存在。
            return false;
        }
    }

    /// <summary>Windows 的 bash 不认盘符路径,转成 <c>/c/...</c> 形式。</summary>
    public static string ToBashPath(string path) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/')
            : path;
}
