using System.Diagnostics;
using VelaShell.Core.Models;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 把 <see cref="ShellIntegration.Bash" /> 原样交给<b>真正的 bash</b> 跑一遍,
/// 断言它最终会往终端里打出哪些字节。
/// </summary>
/// <remarks>
/// <para>
/// 这段是要<b>发给用户照抄进自己 rc 文件</b>的,炸了就是炸在别人的机器上,所以不能只断言
/// "字符串里有 133;C"。这里用 bash 自己的提示符展开(<c>${VAR@P}</c>)把 <c>PS0</c> /
/// <c>PS1</c> 展成<b>实际会打印的字节</b>再比对 —— 等价于在真 TTY 上画一次提示符,
/// 却不需要 PTY。
/// </para>
/// <para>
/// 尤其守着两条真机踩过的坑:
/// </para>
/// <list type="bullet">
/// <item>
/// 用户另有 <c>PROMPT_COMMAND</c> 钩子时,<b>退出码必须还是用户命令的</b>。<c>PS1</c> 在全部
/// 钩子跑完之后才展开,那时 <c>$?</c> 已经是最后一个钩子的状态 —— 所以抓取函数必须排在最前面。
/// </item>
/// <item>
/// 反复 source(重连 / 多开标签)不许叠加:三段各自带 <c>case</c> 守卫,跑三遍与跑一遍同值。
/// </item>
/// </list>
/// <para>
/// 找不到 bash 就报 Inconclusive:Windows 开发机上 bash 来自 Git for Windows,不是人人都装。
/// Linux/macOS 上它一定在,CI 与真机验证不会被绕过去。
/// </para>
/// </remarks>
[TestClass]
public sealed class ShellIntegrationShellTests
{
    private static string? _bash;

    [ClassInitialize]
    public static void Init(TestContext _) => _bash = BashProbe.Find();

    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// 摆好初始状态 → 把片段跑 <paramref name="times" /> 遍 → 用 bash 的提示符展开打印结果。
    /// </summary>
    /// <remarks>
    /// <c>__vela_osc133_status</c> 先塞一个非零值:退出码是经由 <c>PS1</c> 里的参数展开发出去的,
    /// 塞 0 的话"根本没展开"和"展开成 0"看起来一模一样,这条断言就白写了。
    /// </remarks>
    private static string BuildScript(string? initialPromptCommand, string? initialPs1, int times)
    {
        var lines = new List<string>
        {
            "set -u",
            "myhook() { :; }",
            initialPs1 is null ? "unset PS1" : "PS1=" + Quote(initialPs1),
            initialPromptCommand is null ? "unset PROMPT_COMMAND" : "PROMPT_COMMAND=" + Quote(initialPromptCommand),
            "unset PS0"
        };
        for (int i = 0; i < times; i++)
        {
            lines.Add(ShellIntegration.Bash);
        }
        lines.Add("__vela_osc133_status=7");
        lines.Add("printf 'PS0<%s>\\n' \"${PS0@P}\"");
        lines.Add("printf 'PS1<%s>\\n' \"${PS1@P}\"");
        lines.Add("printf 'PC<%s>\\n' \"$PROMPT_COMMAND\"");
        return string.Join('\n', lines);
    }

    /// <summary>包成 shell 单引号字面量(内部的单引号按 <c>'\''</c> 拆开重接)。</summary>
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private (string Ps0, string Ps1, string PromptCommand) Run(
        string? initialPromptCommand = null,
        string? initialPs1 = "P> ",
        int times = 1)
    {
        string script = Path.Combine(Path.GetTempPath(), $"vela-osc133-{Guid.NewGuid():N}.sh");
        // bash 只认 LF;Windows 上写出 CRLF 会得到 "$'\r': command not found"。
        File.WriteAllText(script, BuildScript(initialPromptCommand, initialPs1, times).ReplaceLineEndings("\n"));
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = _bash!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--norc");
            psi.ArgumentList.Add("--noprofile");
            psi.ArgumentList.Add(BashProbe.ToBashPath(script));

            using Process process = Process.Start(psi)!;
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            Assert.IsTrue(process.WaitForExit(30_000), "bash 没有在 30 秒内退出");
            TestContext.WriteLine($"stderr: {stderr}");
            return (Extract(stdout, "PS0"), Extract(stdout, "PS1"), Extract(stdout, "PC"));
        }
        finally
        {
            File.Delete(script);
        }
    }

    /// <summary>取出 <c>标签&lt;值&gt;</c> 里的值。</summary>
    /// <remarks>
    /// <b>按行尾定界,不能按第一个 <c>&gt;</c> 定界。</b>提示符文本里出现 <c>&gt;</c> 再正常不过
    /// (<c>P&gt; </c>、<c>❯</c> 的 ASCII 退化写法…),按第一个 <c>&gt;</c> 截断会把值切掉半截,
    /// 然后报一个看着像"片段坏了"其实是测试自己坏了的失败 —— 本用例最初就是这么红的。
    /// </remarks>
    private static string Extract(string output, string tag)
    {
        int start = output.IndexOf(tag + "<", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(
            0,
            start,
            $"bash 输出里没有 {tag}<…>,脚本多半中途退出了(常见原因:set -u 撞上未定义的变量)。实际输出:{output}");
        start += tag.Length + 1;
        int lineEnd = output.IndexOf('\n', start);
        string line = (lineEnd < 0 ? output[start..] : output[start..lineEnd]).TrimEnd('\r');
        Assert.EndsWith(">", line, $"{tag} 那一行没有以 '>' 收尾:{line}");
        return line[..^1];
    }

    private static void RequireBash()
    {
        if (_bash is null)
        {
            Assert.Inconclusive("没找到 bash(Windows 上来自 Git for Windows),跳过。");
        }
    }

    [TestMethod]
    public void Ps0_EmitsCommandStart()
    {
        // PS0 在"读完命令、执行之前"打印 —— 语义正好就是 OSC 133 的 C,
        // 一个 DEBUG trap 都不用装(那个 trap 全局只有一个,会和 bash-preexec / direnv 抢)。
        RequireBash();
        Assert.AreEqual("\e]133;C\e\\", Run().Ps0);
    }

    [TestMethod]
    public void Ps1_WrapsTheUserPromptWithFinishedPromptStartAndInputStart()
    {
        RequireBash();
        (_, string ps1, _) = Run();

        Assert.AreEqual("\e]133;D;7\e\\\e]133;A\e\\P> \e]133;B\e\\", ps1);
    }

    [TestMethod]
    public void ExitCodeSurvivesAUserPromptCommandHook()
    {
        // 这是最容易写错的一条:PS1 在全部 PROMPT_COMMAND 钩子跑完之后才展开,
        // 那时 $? 已经是最后一个钩子的状态。抓取函数必须排在最前面。
        RequireBash();
        (_, string ps1, string pc) = Run(initialPromptCommand: "myhook");

        Assert.AreEqual("__vela_osc133_status_capture;myhook", pc, "抓取函数必须排在用户钩子之前。");
        Assert.Contains("\e]133;D;7\e\\", ps1, "退出码没有被发出去。");
    }

    [TestMethod]
    public void UserPromptCommandIsPreservedVerbatim()
    {
        // 用户 PROMPT_COMMAND 里合法的 `;;`(case 分支)一个字符都不许动。
        RequireBash();
        const string user = "case $TERM in xterm*) :;; *) :;; esac";
        (_, _, string pc) = Run(initialPromptCommand: user);

        Assert.AreEqual($"__vela_osc133_status_capture;{user}", pc);
    }

    [TestMethod]
    public void UserPromptTextIsPreserved()
    {
        RequireBash();
        (_, string ps1, _) = Run(initialPs1: @"\u@\h:\w\$ ");

        Assert.Contains("\e]133;A\e\\", ps1);
        Assert.Contains("\e]133;B\e\\", ps1);
        Assert.DoesNotContain(@"\u", ps1, "用户的提示符转义应当照常被 bash 展开,而不是被我们包成字面量。");
    }

    [TestMethod]
    public void SourcingRepeatedly_IsIdempotent()
    {
        // 重连、多开标签都会把同一段再跑一次。
        RequireBash();
        (string ps0Once, string ps1Once, string pcOnce) = Run(initialPromptCommand: "myhook");
        (string ps0Thrice, string ps1Thrice, string pcThrice) = Run(initialPromptCommand: "myhook", times: 3);

        Assert.AreEqual(ps0Once, ps0Thrice);
        Assert.AreEqual(ps1Once, ps1Thrice);
        Assert.AreEqual(pcOnce, pcThrice);
    }

    [TestMethod]
    public void RunsUnderSetU_WithEverythingUnset()
    {
        // 谨慎用户的 rc 里 set -u 很常见;PS0 / PS1 / PROMPT_COMMAND 三个都可能压根没定义过。
        RequireBash();
        (string ps0, string ps1, string pc) = Run(initialPromptCommand: null, initialPs1: null);

        Assert.AreEqual("\e]133;C\e\\", ps0);
        Assert.AreEqual("\e]133;D;7\e\\\e]133;A\e\\\e]133;B\e\\", ps1);
        Assert.AreEqual("__vela_osc133_status_capture", pc);
    }
}
