using VelaShell.Core.Ssh;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 静默注入整行的组装:摘历史前缀在最前、哨兵插在中间、<b>哨兵的位置决定藏什么</b>。
/// </summary>
/// <remarks>
/// 这一条是「我们的注入不打扰用户、用户的命令也不打扰我们」的落脚点:
/// 排在哨兵<b>之前</b>的东西连输出带报错一起藏掉(我们自己注的脚本),
/// 排在<b>之后</b>的原样显示(用户配的命令)。位置搞反了,要么用户的
/// <c>neofetch</c> 被吞掉,要么我们的报错糊在屏幕上。
/// </remarks>
[TestClass]
[TestCategory("Ssh")]
public class SilentCommandTests
{
    /// <summary>被注入的那一行里,哨兵长这样(整条序列在 <see cref="ShellIntegrationInjection" /> 里)。</summary>
    private const string MarkerText = "VelaShell=";

    /// <summary>只有 hidden:哨兵排在它<b>后面</b> —— 它跑完才报到,输出因此一起被藏。</summary>
    [TestMethod]
    public void Build_HiddenOnly_PutsTheSentinelAfterIt()
    {
        ShellIntegrationInjection injection = SilentCommand.Build(RemoteShellKind.Bash, "install_me", null);

        int hidden = injection.CommandLine.IndexOf("install_me", StringComparison.Ordinal);
        int marker = injection.CommandLine.IndexOf(MarkerText, StringComparison.Ordinal);
        Assert.IsGreaterThan(-1, hidden);
        Assert.IsGreaterThan(hidden, marker, "要藏输出的那段必须排在哨兵之前");
    }

    /// <summary>只有 visible:哨兵排在它<b>前面</b> —— 先报到再执行,只藏回显。</summary>
    [TestMethod]
    public void Build_VisibleOnly_PutsTheSentinelBeforeIt()
    {
        ShellIntegrationInjection injection = SilentCommand.Build(RemoteShellKind.Bash, null, "neofetch");

        int marker = injection.CommandLine.IndexOf(MarkerText, StringComparison.Ordinal);
        int visible = injection.CommandLine.IndexOf("neofetch", StringComparison.Ordinal);
        Assert.IsGreaterThan(-1, marker);
        Assert.IsGreaterThan(marker, visible, "要显示输出的那段必须排在哨兵之后");
    }

    /// <summary>两段都有:哨兵夹在中间 —— 目录上报脚本走的就是这一种。</summary>
    [TestMethod]
    public void Build_BothParts_SandwichTheSentinel()
    {
        ShellIntegrationInjection injection =
            SilentCommand.Build(RemoteShellKind.Zsh, "install_me", "report_now");

        int hidden = injection.CommandLine.IndexOf("install_me", StringComparison.Ordinal);
        int marker = injection.CommandLine.IndexOf(MarkerText, StringComparison.Ordinal);
        int visible = injection.CommandLine.IndexOf("report_now", StringComparison.Ordinal);
        Assert.IsGreaterThan(hidden, marker);
        Assert.IsGreaterThan(marker, visible);
    }

    /// <summary>
    /// 哨兵是<b>整条</b>转义序列,而不是只有 <c>VelaShell=…</c> 那一截。
    /// </summary>
    /// <remarks>
    /// 窗口是"从哨兵结束处恢复放行"的;只认中间那一截就会把开头的 <c>ESC ] 633 ; P ;</c> 吞掉、
    /// 却把结尾的 BEL 放出去 —— 解析器没在 OSC 状态里,那个 BEL 就是一声真响的铃。
    /// </remarks>
    [TestMethod]
    public void Build_SentinelIsTheWholeEscapeSequence()
    {
        ShellIntegrationInjection injection = SilentCommand.Build(RemoteShellKind.Bash, null, "echo hi");

        Assert.StartsWith("\e]633;P;VelaShell=", injection.Sentinel);
        Assert.EndsWith("\a", injection.Sentinel);
        Assert.Contains(injection.Sentinel[3..^1], injection.CommandLine, "哨兵的文本必须真的出现在这一行里");
    }

    /// <summary>
    /// 每次一枚新 nonce:固定串会让前一条注入的哨兵把后一条的窗口提前关掉,
    /// 而握手时本来就是连着注入好几条的。
    /// </summary>
    [TestMethod]
    public void Build_UsesAFreshNoncePerInjection() =>
        Assert.AreNotEqual(
            SilentCommand.Build(RemoteShellKind.Bash, null, "echo hi").Sentinel,
            SilentCommand.Build(RemoteShellKind.Bash, null, "echo hi").Sentinel);

    /// <summary>
    /// 探不出种类 / 对端是 cmd.exe:<b>不发哨兵</b>(那上面 <c>printf</c> 未必存在,哨兵回不来,
    /// 窗口只能白等到超时),整行照发,由调用方退回按回显匹配的抑制针。
    /// </summary>
    [TestMethod]
    [DataRow(RemoteShellKind.Unknown)]
    [DataRow(RemoteShellKind.NonPosix)]
    public void Build_WithoutSentinelSupport_StillSendsTheCommand(RemoteShellKind kind)
    {
        ShellIntegrationInjection injection = SilentCommand.Build(kind, null, "tmux attach");

        Assert.IsEmpty(injection.Sentinel);
        Assert.Contains("tmux attach", injection.CommandLine);
        Assert.DoesNotContain(MarkerText, injection.CommandLine);
    }

    /// <summary>
    /// 摘历史前缀按 shell 放行:bash / zsh / POSIX sh 接,<b>fish 与非 POSIX 不接</b>。
    /// </summary>
    /// <remarks>
    /// 前缀里的 <c>${BASH_VERSION:-}</c> 在 fish 里是解析期语法错误 —— fish 先把整行解析完再执行,
    /// 于是<b>整行连同后面真正要跑的命令一起死掉</b>,而注入窗口还会把报错藏起来,
    /// 表现就是"功能莫名其妙不工作"。
    /// </remarks>
    [TestMethod]
    [DataRow(RemoteShellKind.Bash, true)]
    [DataRow(RemoteShellKind.Zsh, true)]
    [DataRow(RemoteShellKind.PosixSh, true)]
    [DataRow(RemoteShellKind.Fish, false)]
    [DataRow(RemoteShellKind.NonPosix, false)]
    public void Build_PrependsHistoryScrub_OnlyWhereItParses(RemoteShellKind kind, bool expected)
    {
        ShellIntegrationInjection injection = SilentCommand.Build(kind, null, "echo hi");

        Assert.AreEqual(expected, injection.CommandLine.Contains(ShellHistoryScrub.Marker, StringComparison.Ordinal));

        // 接了前缀的,整行必须从前缀开头 —— 它得赶在 PROMPT_COMMAND(有人在那儿挂 history -a)
        // 之前把这一行摘掉,排到后面就晚了。
        Assert.AreEqual(expected, injection.CommandLine.TrimStart().StartsWith("test -n", StringComparison.Ordinal));
    }

    /// <summary>两段都空 = 什么都不发(空串在 SendSilentCommand 里被直接丢弃,连回车都不会多出来)。</summary>
    [TestMethod]
    public void Build_WithNothingToRun_IsNone()
    {
        Assert.IsTrue(SilentCommand.Build(RemoteShellKind.Bash, null, null).IsEmpty);
        Assert.IsTrue(SilentCommand.Build(RemoteShellKind.Bash, "   ", "\t").IsEmpty);
    }
}
