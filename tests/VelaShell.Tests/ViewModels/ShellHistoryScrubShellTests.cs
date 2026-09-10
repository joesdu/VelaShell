using System.Diagnostics;
using System.Text.RegularExpressions;
using VelaShell.Core.Ssh;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 把静默注入的整行交给<b>真正的、交互式的</b> bash 跑一遍,验它不留在命令历史里。
/// </summary>
/// <remarks>
/// <para>
/// 用户反馈:开了「上报终端工作目录」之后,登进服务器按一下方向键,迎面就是一整行
/// <c>test -n "${BASH_VERSION:-}" &amp;&amp; eval '…'</c> —— 屏幕上明明什么都没有,历史里却整行现形,
/// 第一反应是「谁往我服务器上注了东西」。
/// </para>
/// <para>
/// <b>必须是交互式 bash。</b>脚本模式下 history 根本是关的,<c>bash script.sh</c> 跑得再绿也
/// 什么都没验到 —— 这条 bug 只活在交互式会话里。所以这里用管道喂 <c>bash --norc -i</c>,
/// 与用户真实的那条路一致。<c>HISTFILE</c> 一律指到临时文件:测试绝不能碰开发者自己的
/// <c>~/.bash_history</c>。
/// </para>
/// <para>
/// 找不到 bash 就报 Inconclusive(Windows 上它来自 Git for Windows,不是人人都装);
/// Linux/macOS 上一定在,CI 与真机验证绕不过去。
/// </para>
/// </remarks>
[TestClass]
public sealed class ShellHistoryScrubShellTests
{
    private static string? _bash;

    [ClassInitialize]
    public static void Init(TestContext _) => _bash = BashProbe.Find();

    /// <summary>宿主真正会注入的那一行:目录上报钩子 + 摘历史前缀,与 SendSilentCommand 一字不差。</summary>
    private static string InjectedLine =>
        ShellHistoryScrub.Prepend(MainWindowViewModel.WorkingDirectoryReportHook);

    [TestMethod]
    public void TheInjectedLine_DoesNotStayInHistory()
    {
        string[] history = RunInteractive(
            null,
            "echo user-1",
            " " + InjectedLine, // 前导空格是 SendSilentCommand 发的那一个,一并带上。
            "echo user-2");

        AssertNotInHistory(history);
        CollectionAssert.Contains(history, "echo user-1", "用户自己的命令不能跟着一起被摘掉。");
        CollectionAssert.Contains(history, "echo user-2");
    }

    /// <summary>
    /// 摘历史那段接在**前面**,所以这一行的退出码仍旧由后半段(用户自己的命令)决定。
    /// </summary>
    /// <remarks>
    /// 缀在末尾的话,收尾动作会把 <c>$?</c> 抹成 0 —— 而 starship / powerlevel10k 这类提示符
    /// 会把上一条命令的退出码画出来,一条失败的「认证后执行命令」就此变得毫无痕迹。
    /// </remarks>
    [TestMethod]
    public void TheRealCommandsExitStatus_Survives()
    {
        string[] output = RunInteractiveRaw(
            null,
            " " + ShellHistoryScrub.Prepend("sh -c 'exit 3'"),
            "printf 'STATUS<%s>\n' \"$?\"");

        Assert.Contains("STATUS<3>", string.Join('\n', output), "退出码被收尾动作吃掉了。");
    }

    /// <summary>
    /// 用户真配了 <c>HISTCONTROL=ignorespace</c> 时,注入行<b>根本没进历史</b> ——
    /// 这时候「删掉最后一条」删的就是人家上一条真命令。删错用户的历史比留一行噪音严重得多。
    /// </summary>
    [TestMethod]
    public void WithIgnorespace_TheUsersOwnLastCommandIsNotDeleted()
    {
        string[] history = RunInteractive(
            "ignorespace",
            "echo keep-me",
            " " + InjectedLine);

        AssertNotInHistory(history);
        CollectionAssert.Contains(history, "echo keep-me", "注入行没进历史,却把用户上一条删掉了。");
    }

    /// <summary>
    /// 用户设了 <c>HISTTIMEFORMAT</c> 时 <c>history 1</c> 会多打一列时间戳,
    /// 而取序号是按「开头的数字」来的 —— 摘历史那段自带 <c>HISTTIMEFORMAT=</c> 前缀正为此。
    /// </summary>
    [TestMethod]
    public void WithAHistoryTimestampFormat_ItStillScrubs()
    {
        string[] history = RunInteractive(
            null,
            "HISTTIMEFORMAT='%F %T '",
            "echo user-1",
            " " + InjectedLine);

        AssertNotInHistory(history);
        CollectionAssert.Contains(history, "echo user-1");
    }

    private static void AssertNotInHistory(string[] history)
    {
        string joined = string.Join('\n', history);
        Assert.DoesNotContain("vela_shell_osc7", joined, $"目录上报钩子留在历史里了:\n{joined}");
        Assert.DoesNotContain(ShellHistoryScrub.Marker, joined, $"摘历史那段自己留在历史里了:\n{joined}");
    }

    /// <summary>喂几行给交互式 bash,回来的是**历史里的命令**(已剥掉序号与时间戳列)。</summary>
    private static string[] RunInteractive(string? histControl, params string[] lines)
    {
        // HISTTIMEFORMAT= 只对这一次调用生效:用户设过它的话,history 会多打一列时间戳。
        string[] output = RunInteractiveRaw(histControl, [.. lines, "HISTTIMEFORMAT= history"]);
        return [.. output
            .Select(StripEscapes)
            .Select(line => Regex.Match(line, @"^\s*\d+\s+(?<cmd>.*)$"))
            .Where(m => m.Success)
            .Select(m => m.Groups["cmd"].Value.TrimEnd())];
    }

    /// <summary>把行首残留的转义序列剥掉再解析。</summary>
    /// <remarks>
    /// 钩子装好之后<b>每次提示符</b>都真的会发一串 OSC 7,而没有 tty 的 bash 把它和随后的输出
    /// 挤在同一行上 —— <c>history</c> 的第一行于是长成「一串 OSC 7 + <c>    1  echo user-1</c>」,
    /// 行首锚定的正则一条也匹配不上。这不是被测代码的问题,是解析的问题:先剥干净再比对。
    /// 剥法取最后一个 ESC 之后的部分,再去掉 ST(<c>ESC \</c>)剩下的那个反斜杠 ——
    /// 历史里是普通命令,不会自带 ESC。
    /// </remarks>
    private static string StripEscapes(string line)
    {
        int lastEscape = line.LastIndexOf((char)27);
        string tail = lastEscape < 0 ? line : line[(lastEscape + 1)..];
        return tail.StartsWith((char)92) ? tail[1..] : tail; // 92 = 反斜杠,即 ST 的后半个字节
    }

    private static string[] RunInteractiveRaw(string? histControl, params string[] lines)
    {
        if (_bash is null)
        {
            Assert.Inconclusive("本机没有 bash(Windows 上需要 Git for Windows),跳过 shell 语义验证。");
        }

        string histFile = Path.Combine(Path.GetTempPath(), $"vela-hist-{Guid.NewGuid():N}");
        ProcessStartInfo psi = new()
        {
            FileName = _bash,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("--norc"); // 别让开发者的 ~/.bashrc 掺和进来。
        psi.ArgumentList.Add("-i");     // 交互式才记历史 —— 这正是被测的那条路。
        psi.Environment["HISTFILE"] = BashProbe.ToBashPath(histFile);
        psi.Environment["HISTSIZE"] = "500";
        psi.Environment["HISTCONTROL"] = histControl ?? string.Empty;
        psi.Environment["PS1"] = string.Empty;
        psi.Environment["TERM"] = "dumb";

        try
        {
            using Process process = Process.Start(psi)!;
            foreach (string line in lines)
            {
                process.StandardInput.Write(line + "\n");
            }
            process.StandardInput.Write("exit\n");
            process.StandardInput.Flush();
            string stdout = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd(); // 没有 tty 的交互式 bash 会念叨作业控制,与本用例无关。
            Assert.IsTrue(process.WaitForExit(30_000), "bash 没有在 30 秒内退出");
            return stdout.ReplaceLineEndings("\n").Split('\n');
        }
        finally
        {
            File.Delete(histFile);
        }
    }
}
