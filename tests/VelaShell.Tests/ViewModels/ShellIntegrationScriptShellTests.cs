using System.Diagnostics;
using System.Runtime.InteropServices;
using VelaShell.Core.Ssh;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 把 <see cref="ShellIntegrationScript" /> 里 bash 之外的三段(zsh / fish / POSIX sh)
/// 交给<b>真正的 shell</b> 跑一遍。
/// </summary>
/// <remarks>
/// <para>
/// 理由与 <see cref="PromptHookShellTests" />(bash 那段)逐字相同:这些片段是 shell 语义,
/// C# 单测只断言得了"字符串里有没有某几个词",拦不住"跑起来才炸" —— 而这个功能已经
/// 因为这类问题炸过两次(pyenv 的 <c>;;</c>、fish 的解析期报错)。三件事必须真跑才知道:
/// </para>
/// <list type="number">
/// <item><b>装得上</b> —— 语法正确,stderr 一个字都不吐(注入时 stderr 被吞,
/// 真出了问题只会表现为"功能莫名其妙不工作")。</item>
/// <item><b>幂等</b> —— 重连、多开标签都会把同一段再注入一次,装三遍的结果必须和装一遍一样。</item>
/// <item><b>真的会发 OSC 7</b> —— 字节对得上,而不只是函数定义在那儿。</item>
/// </list>
/// <para>
/// 找不到对应的 shell 就报 Inconclusive 而不是假装通过:zsh / fish 在 Windows 开发机上基本没有,
/// 而在装了它们的机器(以及 CI 的 Linux runner)上,这几条就是真机验证。
/// </para>
/// </remarks>
[TestClass]
public sealed class ShellIntegrationScriptShellTests
{
    /// <summary>四段脚本共用的函数名。</summary>
    private const string Hook = ShellIntegrationScript.FunctionName;

    /// <summary>OSC 7 的开头;新片段用 BEL(<c>\a</c>)收尾。</summary>
    private const string Osc7Prefix = "\e]7;file://";

    /// <summary>
    /// 在 PATH 里找一个可执行文件(Windows 上补 <c>.exe</c>)。
    /// </summary>
    /// <remarks>
    /// 和 <see cref="BashProbe.Find" /> 一样跳过 <c>System32</c> 与 <c>WindowsApps</c> ——
    /// 那两处的 <c>bash.exe</c> 是 WSL 入口,拿它跑临时脚本会掉进另一套文件系统视图。
    /// 这里找的是 zsh/fish/dash,道理一致:宁可找不到(报 Inconclusive),也不要找到个假的。
    /// </remarks>
    private static string? FindOnPath(string name)
    {
        bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        string fileName = windows ? name + ".exe" : name;
        string system = windows ? Environment.GetFolderPath(Environment.SpecialFolder.Windows) : "\0";
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0
                || (windows && dir.StartsWith(system, StringComparison.OrdinalIgnoreCase))
                || (windows && dir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            string candidate;
            try
            {
                candidate = Path.Combine(dir, fileName);
            }
            catch (ArgumentException)
            {
                continue; // PATH 里混进了带非法字符的项
            }
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>把脚本写进临时文件交给 <paramref name="shell" /> 跑,回收 stdout。</summary>
    /// <remarks>
    /// 只认 LF:Windows 上写出 CRLF,shell 会得到 <c>$'\r': command not found</c>。
    /// stderr 必须是空的 —— 片段吐到 stderr 的任何东西在真机上都会被注入时的
    /// <c>2&gt;/dev/null</c> 吞掉,于是变成一个查不出原因的"功能不工作"。
    /// </remarks>
    private static string Run(string shell, string script, string extension)
    {
        string path = Path.Combine(Path.GetTempPath(), $"vela-shell-integration-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, script.ReplaceLineEndings("\n"));
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = shell,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(BashProbe.ToBashPath(path));

            using Process process = Process.Start(psi)!;
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            Assert.IsTrue(process.WaitForExit(30_000), $"{shell} 没有在 30 秒内退出");
            Assert.IsEmpty(stderr.Trim(), $"片段向 stderr 写了东西:{stderr}");
            return stdout;
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>取出 <c>标记&lt;内容&gt;</c> 里的内容。</summary>
    private static string Extract(string output, string tag)
    {
        int start = output.IndexOf(tag + "<", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(
            0,
            start,
            $"输出里没有 {tag}<…>,脚本多半中途退出了。实际输出:{output}");
        start += tag.Length + 1;
        int end = output.IndexOf('>', start);
        Assert.IsGreaterThanOrEqualTo(0, end, $"输出里的 {tag}<…> 没有闭合:{output}");
        return output[start..end];
    }

    /// <summary>断言这串字节确实是一条指向 <paramref name="pwd" /> 的 OSC 7。</summary>
    private static void AssertReportsOsc7(string emitted, string pwd)
    {
        Assert.StartsWith(Osc7Prefix, emitted, $"发出来的不是 OSC 7:{Escape(emitted)}");
        Assert.EndsWith(pwd + "\a", emitted, $"OSC 7 的路径不是当前目录:{Escape(emitted)}");
    }

    /// <summary>把控制字符写成可读形式,断言失败时才看得懂。</summary>
    private static string Escape(string value) =>
        value.Replace("\e", "<ESC>", StringComparison.Ordinal).Replace("\a", "<BEL>", StringComparison.Ordinal);

    // ---- POSIX sh(dash / ash / ksh …):唯一动 PS1 的一段,也是"认不出种类时"的兜底 ----

    /// <summary>
    /// POSIX sh 那段:装三遍之后 <c>PS1</c> 只能多出**一个**前缀,且函数真的会发 OSC 7。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 优先找真的 <c>dash</c>(Debian/Ubuntu 的 <c>/bin/sh</c> 就是它,也是这段的头号用户);
    /// 找不到就退到 <c>sh</c>,再退到 bash —— bash 同样会对 <c>PS1</c> 做命令替换展开,
    /// 语法与幂等这两条照样验得了,只是少了"在真 dash 上跑过"这一层。
    /// </para>
    /// <para>
    /// 整个装载块重定向到 <c>/dev/null</c>:片段末尾会立刻调用一次 <c>vela_shell_osc7</c>
    /// (让文件浏览器一连上就有 cwd),那串 OSC 混进 stdout 就没法比对了。
    /// </para>
    /// </remarks>
    [TestMethod]
    public void PosixSh_InstallsOnceAndReportsCwd()
    {
        string? sh = FindOnPath("dash") ?? FindOnPath("sh") ?? BashProbe.Find();
        if (sh is null)
        {
            Assert.Inconclusive("本机没有 dash/sh/bash,跳过 POSIX sh 片段的 shell 语义验证。");
            return;
        }
        string snippet = ShellIntegrationScript.PosixSh;
        string script = string.Join(
            '\n',
            "set -u",
            "PS1='$ '",
            "{",
            snippet,
            snippet,
            snippet,
            "} >/dev/null 2>&1",
            "printf 'PS1<%s>\\n' \"$PS1\"",
            "printf 'PWD<%s>\\n' \"$PWD\"",
            "printf 'OSC<%s>\\n' \"$(" + Hook + ")\"");

        string stdout = Run(sh, script, ".sh");

        // 幂等:装三遍,PS1 前面也只能有一个 $(vela_shell_osc7)。
        Assert.AreEqual($"$({Hook})$ ", Extract(stdout, "PS1"));
        AssertReportsOsc7(Extract(stdout, "OSC"), Extract(stdout, "PWD"));
    }

    // ---- zsh:macOS 的默认 shell,旧版把它整个漏掉了 ----

    /// <summary>
    /// zsh 那段:钩子必须**恰好一次**出现在 <c>precmd_functions</c> 里,且真的会发 OSC 7。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这一条盯的是旧版最大的那个洞:守卫 <c>test -n "$BASH_VERSION"</c> 在 zsh 上为假,
    /// 于是 zsh 用户既不报错、也永远拿不到 cwd。
    /// </para>
    /// <para>
    /// 顺带验的是"不用 <c>add-zsh-hook</c>"这个决定:直接往数组里塞,连
    /// <c>fpath</c> 不含标准函数目录的机器(NixOS)也照样成立。
    /// </para>
    /// </remarks>
    [TestMethod]
    public void Zsh_RegistersPrecmdHookOnceAndReportsCwd()
    {
        string? zsh = FindOnPath("zsh");
        if (zsh is null)
        {
            Assert.Inconclusive("本机没有 zsh,跳过 zsh 片段的 shell 语义验证。");
            return;
        }
        string snippet = ShellIntegrationScript.Zsh;
        string script = string.Join(
            '\n',
            "typeset -ga precmd_functions",
            "precmd_functions=()",
            "{",
            snippet,
            snippet,
            snippet,
            "} >/dev/null 2>&1",
            "printf 'HOOKS<%s>\\n' \"${precmd_functions[*]}\"",
            "printf 'PWD<%s>\\n' \"$PWD\"",
            "printf 'OSC<%s>\\n' \"$(" + Hook + ")\"");

        string stdout = Run(zsh, script, ".zsh");

        Assert.AreEqual(Hook, Extract(stdout, "HOOKS"), "装三遍之后钩子只能在 precmd_functions 里出现一次");
        AssertReportsOsc7(Extract(stdout, "OSC"), Extract(stdout, "PWD"));
    }

    /// <summary>
    /// zsh 片段不许把用户原有的 <c>precmd_functions</c> 挤掉 —— 那会连累 oh-my-zsh、
    /// powerlevel10k 这些全靠 precmd 工作的东西,是比"没有目录跟随"严重得多的破坏。
    /// </summary>
    [TestMethod]
    public void Zsh_KeepsExistingPrecmdFunctions()
    {
        string? zsh = FindOnPath("zsh");
        if (zsh is null)
        {
            Assert.Inconclusive("本机没有 zsh,跳过 zsh 片段的 shell 语义验证。");
            return;
        }
        string script = string.Join(
            '\n',
            "typeset -ga precmd_functions",
            "_user_precmd() { : }",
            "precmd_functions=(_user_precmd)",
            "{",
            ShellIntegrationScript.Zsh,
            ShellIntegrationScript.Zsh,
            "} >/dev/null 2>&1",
            "printf 'HOOKS<%s>\\n' \"${precmd_functions[*]}\"");

        Assert.AreEqual($"_user_precmd {Hook}", Extract(Run(zsh, script, ".zsh"), "HOOKS"));
    }

    // ---- fish:唯一一段无法靠 eval '…' 当护身符的,全靠探测分派 + 注入窗口兜底 ----

    /// <summary>
    /// fish 那段:装三遍之后函数还在(<c>--on-variable PWD</c> 挂着),且真的会发 OSC 7。
    /// </summary>
    /// <remarks>
    /// fish 的 <c>functions -e</c> 删一个不存在的函数会往 stderr 写一行抱怨,
    /// 所以片段里先 <c>functions -q</c> 问一句 —— <see cref="Run" /> 对 stderr 的断言盯的就是它。
    /// </remarks>
    [TestMethod]
    public void Fish_InstallsPwdWatcherAndReportsCwd()
    {
        string? fish = FindOnPath("fish");
        if (fish is null)
        {
            Assert.Inconclusive("本机没有 fish,跳过 fish 片段的 shell 语义验证。");
            return;
        }
        string snippet = ShellIntegrationScript.Fish;
        string script = string.Join(
            '\n',
            "begin",
            snippet,
            snippet,
            snippet,
            "end >/dev/null 2>&1",
            $"functions -q {Hook}; and printf 'FOUND<yes>\\n'",
            "printf 'PWD<%s>\\n' \"$PWD\"",
            "printf 'OSC<%s>\\n' (" + Hook + ")");

        string stdout = Run(fish, script, ".fish");

        Assert.AreEqual("yes", Extract(stdout, "FOUND"));
        AssertReportsOsc7(Extract(stdout, "OSC"), Extract(stdout, "PWD"));
    }

    // ---- 跨 shell 的共同约定 ----

    /// <summary>
    /// <b>fish 那段里不许出现 <c>${…}</c>。</b>fish 先把整行解析完再执行,<c>${var}</c>
    /// 在它那里是解析期语法错误(<c>Expected a variable name after this $</c>)——
    /// 整行连同后面真正要跑的命令一起死掉,而注入窗口还会把报错藏起来,
    /// 表现是"功能莫名其妙不工作"。同样的道理,发往 fish 的整行也不许接那段摘历史前缀。
    /// </summary>
    [TestMethod]
    public void FishScript_AndItsInjectedLine_ContainNoBraceExpansion()
    {
        Assert.DoesNotContain("${", ShellIntegrationScript.Fish, "fish 解析不了 ${…}");
        Assert.IsFalse(
            ShellHistoryScrub.SupportedBy(RemoteShellKind.Fish),
            "摘历史前缀里有 ${BASH_VERSION:-},接到 fish 上会让整行解析失败");
    }

    /// <summary>
    /// 反向守卫:另外三种 shell 照样要接摘历史前缀 —— 那是"注入不留痕"的唯一手段,
    /// 别为了修 fish 把所有人的都关掉。
    /// </summary>
    [TestMethod]
    [DataRow(RemoteShellKind.Bash)]
    [DataRow(RemoteShellKind.Zsh)]
    [DataRow(RemoteShellKind.PosixSh)]
    [DataRow(RemoteShellKind.Unknown)]
    public void HistoryScrub_StaysOnForEveryShellThatCanParseIt(RemoteShellKind kind) =>
        Assert.IsTrue(ShellHistoryScrub.SupportedBy(kind));

    /// <summary>
    /// 四段都得发同一种东西:OSC 7 + 同一个函数名。函数名是 bash 那段的去重锚点,
    /// 改掉它,被旧版注入过的会话就认不出自己、那条自愈路径会失效。
    /// </summary>
    [TestMethod]
    [DataRow(RemoteShellKind.Bash)]
    [DataRow(RemoteShellKind.Zsh)]
    [DataRow(RemoteShellKind.Fish)]
    [DataRow(RemoteShellKind.PosixSh)]
    public void EveryScript_EmitsOsc7ThroughTheSharedFunctionName(RemoteShellKind kind)
    {
        string script = ShellIntegrationScript.For(kind);

        Assert.Contains(Hook, script);
        Assert.Contains("]7;file://%s%s", script);
    }
}
