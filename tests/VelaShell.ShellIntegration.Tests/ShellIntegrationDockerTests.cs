using System.Diagnostics.CodeAnalysis;
using VelaShell.Core.Ssh;

namespace VelaShell.ShellIntegration.Tests;

/// <summary>
/// 「文件浏览器跟随终端目录」的端到端验证:真 sshd、真登录 shell、真 PTY。
/// </summary>
/// <remarks>
/// <para>
/// 五种登录 shell(bash / zsh / fish / dash / ash)各跑一遍同一组断言,外加两个
/// 「用户已经动过手脚」的账号。靶子见 <c>tests/fixtures/ssh-shells/Dockerfile</c>,
/// 起法:<c>docker compose -f docker-compose.test.yml up -d ssh-shells</c>。
/// </para>
/// <para>
/// 这一套要钉死的是四件替身永远测不出来的事:
/// </para>
/// <list type="number">
/// <item><b>注进去真的能用</b> —— 对端 shell 解析得了这一行,钩子真的装上了,cwd 真的报了出来。</item>
/// <item><b>屏幕上真的干净</b> —— 注入行的回显、以及它引发的任何输出,一个字节都不上屏。</item>
/// <item><b>两边互不打扰</b> —— 用户紧跟着的命令照常显示;用户自己的 rc(pyenv 式的
/// <c>PROMPT_COMMAND</c>、starship 式的 PS1 重写)既不被我们弄坏,也弄不坏我们。</item>
/// <item><b>不留痕</b> —— 注入行不进命令历史,且不篡改用户提示符看到的上条退出码。</item>
/// </list>
/// </remarks>
[SuppressMessage("Usage", "MSTEST0045:Use cooperative cancellation with [Timeout]",
    Justification = "被等待的 docker/SSH 操作不接受测试取消令牌,协作取消无法中断它们。")]
[TestClass]
[TestCategory("DockerIntegration")]
public sealed class ShellIntegrationDockerTests
{
    /// <summary>容器里五个账号 → 期望探出来的种类 → 家目录。</summary>
    private static IEnumerable<object[]> Shells =>
    [
        ["vela-bash", RemoteShellKind.Bash],
        ["vela-zsh", RemoteShellKind.Zsh],
        ["vela-fish", RemoteShellKind.Fish],
        ["vela-dash", RemoteShellKind.PosixSh],
        ["vela-ash", RemoteShellKind.PosixSh]
    ];

    private static string HomeOf(string user) => "/home/" + user;

    [TestInitialize]
    public void Setup()
    {
        ShellIntegrationHarness.RequireContainer();

        // 探针按主机+用户缓存;每个用例都从零开始探,免得前一个用例的结论串味。
        RemoteShellProbe.ClearCache();
    }

    /// <summary>
    /// 探针得认得出每一种登录 shell —— 认错了后面全盘皆错(发错片段、fish 还会被摘历史前缀带死)。
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(Shells))]
    [Timeout(60_000)]
    public async Task Probe_IdentifiesTheLoginShell(string user, RemoteShellKind expected)
    {
        using ShellIntegrationHarness harness = await ShellIntegrationHarness.ConnectAsync(user);

        RemoteShellKind kind = await harness.DetectShellKindAsync();

        Assert.AreEqual(expected, kind, $"{user} 的登录 shell 认错了");
    }

    /// <summary>
    /// 注入之后:cwd 立刻报得出来(不必等用户先敲回车),而屏幕上一个字节的痕迹都没有。
    /// </summary>
    /// <remarks>
    /// "立刻"这一条对 fish 尤其要紧 —— 它那段挂在 <c>PWD</c> 变化上,不主动调一次就得等到
    /// 用户第一次 <c>cd</c>,文件浏览器在那之前完全是瞎的。
    /// </remarks>
    [TestMethod]
    [DynamicData(nameof(Shells))]
    [Timeout(60_000)]
    public async Task Injection_ReportsCwdImmediately_AndLeavesNoTrace(string user, RemoteShellKind expected)
    {
        using ShellIntegrationHarness harness = await ShellIntegrationHarness.ConnectAsync(user);
        RemoteShellKind kind = await harness.DetectShellKindAsync();
        Assert.AreEqual(expected, kind);

        harness.WaitForOutputIdle();
        await harness.InjectShellIntegrationAsync(kind);

        harness.WaitForWorkingDirectory(HomeOf(user));
        AssertNoTrace(harness);
    }

    /// <summary>终端里 <c>cd</c> 到别处,必须再报一次 —— 这才是"跟随"两个字的意思。</summary>
    [TestMethod]
    [DynamicData(nameof(Shells))]
    [Timeout(60_000)]
    public async Task Injection_FollowsSubsequentCd(string user, RemoteShellKind expected)
    {
        using ShellIntegrationHarness harness = await ShellIntegrationHarness.ConnectAsync(user);
        RemoteShellKind kind = await harness.DetectShellKindAsync();
        Assert.AreEqual(expected, kind);
        harness.WaitForOutputIdle();
        await harness.InjectShellIntegrationAsync(kind);
        harness.WaitForWorkingDirectory(HomeOf(user));

        await harness.TypeAsync("cd /tmp");

        harness.WaitForWorkingDirectory("/tmp");
    }

    /// <summary>
    /// <b>两边互不打扰(其一):用户紧跟着的那条命令,输出必须原样显示。</b>
    /// </summary>
    /// <remarks>
    /// 这正是注入窗口用"自己那条带 nonce 的哨兵"而不是"任意一条集成序列"收口的理由:
    /// 窗口在哨兵处就闭合了,用户的命令排在哨兵之后,一个字节都不会被吞。
    /// </remarks>
    [TestMethod]
    [DynamicData(nameof(Shells))]
    [Timeout(60_000)]
    public async Task UserCommandRightAfterTheInjection_IsFullyVisible(string user, RemoteShellKind expected)
    {
        const string marker = "VELA-USER-OUTPUT-1234";
        using ShellIntegrationHarness harness = await ShellIntegrationHarness.ConnectAsync(user);
        RemoteShellKind kind = await harness.DetectShellKindAsync();
        Assert.AreEqual(expected, kind);

        // 与宿主的 SendSessionInjections 同序:等对端安静 → 钩子(带窗口)→ 用户的启动命令(不带窗口)。
        harness.WaitForOutputIdle();
        await harness.InjectShellIntegrationAsync(kind);
        await harness.InjectUserCommandAsync(kind, "echo " + marker);

        harness.WaitFor(() => harness.Visible.Contains(marker, StringComparison.Ordinal), "用户命令的输出");
        AssertNoTrace(harness);
    }

    /// <summary>
    /// <b>两边互不打扰(其二):用户的 <c>PROMPT_COMMAND</c> 长什么样都不该把我们弄坏。</b>
    /// </summary>
    /// <remarks>
    /// <c>vela-pyenv</c> 的 rc 把 <c>PROMPT_COMMAND</c> 设成 <c>_pyenv_virtualenv_hook;</c> ——
    /// 结尾自带分号,正是 pyenv-virtualenv 的真实形状。旧版钩子直接追加会拼出
    /// <c>…;;vela_shell_osc7</c>,<c>;;</c> 出了 case 就是语法错误,用户<b>每敲一次回车</b>
    /// 都看一行报错。这条用例把那一幕摆在真机上:既要报得出 cwd,也不许出现 syntax error,
    /// 而且用户原来那个钩子必须还在。
    /// </remarks>
    [TestMethod]
    [Timeout(60_000)]
    public async Task UsersOwnPromptCommand_IsNeitherBrokenNorLost()
    {
        const string user = "vela-pyenv";
        using ShellIntegrationHarness harness = await ShellIntegrationHarness.ConnectAsync(user);
        RemoteShellKind kind = await harness.DetectShellKindAsync();
        harness.WaitForOutputIdle();
        await harness.InjectShellIntegrationAsync(kind);
        harness.WaitForWorkingDirectory(HomeOf(user));

        await harness.TypeAsync("echo \"PC<$PROMPT_COMMAND>\"");

        harness.WaitFor(() => harness.Visible.Contains("PC<", StringComparison.Ordinal), "PROMPT_COMMAND 回显");
        string visible = harness.Visible;
        Assert.Contains("_pyenv_virtualenv_hook;vela_shell_osc7", visible, "用户原有的钩子必须还在,且不许拼出 ;;");
        Assert.DoesNotContain(";;", visible, "拼出了 ;; —— 用户每敲一次回车就会看到一行语法错误");
        Assert.DoesNotContain("syntax error", visible, ShellIntegrationHarness.Escape(harness.Raw));
    }

    /// <summary>
    /// <b>两边互不打扰(其三):提示符被 starship / powerlevel10k 那类工具全权接管也照样工作。</b>
    /// </summary>
    /// <remarks>
    /// <c>vela-starship</c> 的 rc 每次提示符都重写 <c>PS1</c>。bash / zsh 两段刻意走提示符<b>钩子</b>
    /// 而不碰 <c>PS1</c>,正是为了这个场景 —— 只有没有钩子可用的 dash/ash 才不得不动 <c>PS1</c>。
    /// </remarks>
    [TestMethod]
    [Timeout(60_000)]
    public async Task PromptRewrittenEveryTime_DoesNotBreakReporting()
    {
        const string user = "vela-starship";
        using ShellIntegrationHarness harness = await ShellIntegrationHarness.ConnectAsync(user);
        RemoteShellKind kind = await harness.DetectShellKindAsync();
        harness.WaitForOutputIdle();
        await harness.InjectShellIntegrationAsync(kind);
        harness.WaitForWorkingDirectory(HomeOf(user));

        await harness.TypeAsync("cd /tmp");

        harness.WaitForWorkingDirectory("/tmp");
        Assert.Contains("[fake-starship]", harness.Visible, "用户的提示符本身也得好好的");
    }

    /// <summary>
    /// 注入行不许留在命令历史里 —— 屏幕上隐形、方向键一按却整行冒出来,
    /// 用户的第一反应是「谁往我服务器上注了东西」。
    /// </summary>
    /// <remarks>
    /// 断言走<b>原始</b>字节流而不是可见流:抑制针认的是"整行 + 换行",而历史输出里那一行
    /// 前面带着序号,针照样能咬住后半截并剥掉 —— 拿可见流去断言会假阳性地全绿。
    /// </remarks>
    [TestMethod]
    [Timeout(60_000)]
    public async Task TheInjectedLine_DoesNotStayInHistory()
    {
        const string user = "vela-bash";
        using ShellIntegrationHarness harness = await ShellIntegrationHarness.ConnectAsync(user);
        RemoteShellKind kind = await harness.DetectShellKindAsync();
        harness.WaitForOutputIdle();
        await harness.InjectShellIntegrationAsync(kind);
        harness.WaitForWorkingDirectory(HomeOf(user));

        await harness.TypeAsync("echo HIST-BEGIN; history; echo HIST-END");

        harness.WaitFor(() => harness.Raw.Contains("HIST-END", StringComparison.Ordinal), "history 输出");
        string history = Between(harness.Raw, "HIST-BEGIN", "HIST-END");
        Assert.DoesNotContain(
            ShellIntegrationScript.FunctionName,
            history,
            $"注入行留在历史里了:\n{ShellIntegrationHarness.Escape(history)}");
        Assert.DoesNotContain(ShellHistoryScrub.Marker, history, "摘历史那段自己留在历史里了");
    }

    /// <summary>
    /// 钩子对 <c>$?</c> 必须是<b>透明</b>的:提示符看到的仍是用户上条命令的退出码。
    /// </summary>
    /// <remarks>
    /// bash 跑完<b>全部</b> <c>PROMPT_COMMAND</c> 之后才展开 <c>PS1</c>,那时 <c>$?</c> 是最后一个
    /// 钩子的状态 —— 而我们这个钩子正排在最后。不把进门时的 <c>$?</c> 还回去,用户提示符里的
    /// "上条命令退出码"就被永久钉死成 0(<c>printf</c> 总是成功),一条失败的命令看上去和成功的一样。
    /// </remarks>
    [TestMethod]
    [Timeout(60_000)]
    public async Task TheHook_IsTransparentToTheExitStatus()
    {
        const string user = "vela-bash";
        using ShellIntegrationHarness harness = await ShellIntegrationHarness.ConnectAsync(user);
        RemoteShellKind kind = await harness.DetectShellKindAsync();
        harness.WaitForOutputIdle();
        await harness.InjectShellIntegrationAsync(kind);
        harness.WaitForWorkingDirectory(HomeOf(user));

        // 提示符自己把上条退出码画出来 —— starship / p10k 干的正是这件事,只是花哨得多。
        await harness.TypeAsync("PS1='[rc=$?]# '");
        await harness.TypeAsync("false");

        harness.WaitFor(() => harness.Visible.Contains("[rc=1]#", StringComparison.Ordinal), "提示符画出 rc=1");
    }

    /// <summary>
    /// 在 tmux 里要<b>额外</b>再发一份包进 DCS 的拷贝,否则 tmux 自己把 OSC 7 吃掉、不转发给外层终端。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这条盯的是全篇最容易写错的一处转义:DCS 只能由 ST(<c>ESC \</c>)收尾、载荷里每个 ESC 还得加倍,
    /// 而"printf 的格式串里要的是字面量 <c>\033\\</c>"这件事在三种引号下要写成不同的字数 ——
    /// bash / zsh 的双引号要四个反斜杠,POSIX sh 的单引号要两个,fish 的单引号又是四个(成因还不同)。
    /// 数错一个,发出去的就是一串垃圾字符,而且只有 tmux 用户看得见。
    /// </para>
    /// <para>
    /// 断言走<b>原始</b>字节流:DCS 透传那一份是发给外层终端的,我们自己的仿真器会把它当未知 DCS 消费掉,
    /// 可见流里本来就不该留下它。这里验的是"发出来的字节对不对",不是"屏幕上有没有"。
    /// </para>
    /// <para>
    /// 用环境变量假扮 tmux 而不是真起一个:真 tmux 的透传要落到<b>它的客户端</b>终端上,
    /// 这里根本没有那个终端;而要验的那件事(格式串的转义)与 tmux 在不在场无关。
    /// </para>
    /// </remarks>
    [TestMethod]
    [DataRow("vela-bash", "export TMUX=fake-tmux", "bash / zsh:双引号格式串")]
    [DataRow("vela-ash", "export TMUX=fake-tmux", "POSIX sh:单引号格式串")]
    [DataRow("vela-fish", "set -x TMUX fake-tmux", "fish:单引号但折叠规则不同")]
    public async Task InsideTmux_AlsoEmitsThePassthroughCopy(string user, string setTmux, string because)
    {
        using ShellIntegrationHarness harness = await ShellIntegrationHarness.ConnectAsync(user);
        RemoteShellKind kind = await harness.DetectShellKindAsync();
        harness.WaitForOutputIdle();
        await harness.InjectShellIntegrationAsync(kind);
        harness.WaitForWorkingDirectory(HomeOf(user));

        await harness.TypeAsync(setTmux);
        await harness.TypeAsync("cd /tmp");

        harness.WaitForWorkingDirectory("/tmp");
        harness.WaitFor(
            () => harness.Raw.Contains("\ePtmux;\e\e]7;file://", StringComparison.Ordinal),
            $"DCS 透传的 OSC 7({because})");
        Assert.Contains("\ePtmux;\e\e]633;P;Cwd=/tmp\a\e\\", harness.Raw, $"透传那份的收尾必须是 ST({because})");
        Assert.Contains("\e]7;file://", harness.Raw, "裸的那份要留给 tmux 自己记 pane 的 cwd");
    }

    /// <summary>屏幕上不许有注入的任何痕迹:脚本正文、摘历史前缀、哨兵,一个都不许露。</summary>
    private static void AssertNoTrace(ShellIntegrationHarness harness)
    {
        string visible = harness.Visible;
        string dump = ShellIntegrationHarness.Escape(visible);
        Assert.DoesNotContain(ShellIntegrationScript.FunctionName, visible, $"注入行的正文露在屏幕上:\n{dump}");
        Assert.DoesNotContain("BASH_VERSION", visible, $"注入行的守卫露在屏幕上:\n{dump}");
        Assert.DoesNotContain(ShellHistoryScrub.Marker, visible, $"摘历史前缀露在屏幕上:\n{dump}");
        Assert.DoesNotContain(ShellIntegrationScript.SentinelKey, visible, $"窗口哨兵露在屏幕上:\n{dump}");
        Assert.DoesNotContain("not found", visible, $"注入引发的报错露在屏幕上:\n{dump}");
        Assert.DoesNotContain("syntax error", visible, $"注入引发的报错露在屏幕上:\n{dump}");
    }

    /// <summary>取两个标记之间那一段(含头尾标记之外的正文)。</summary>
    private static string Between(string text, string begin, string end)
    {
        int from = text.IndexOf(begin, StringComparison.Ordinal);
        int to = text.LastIndexOf(end, StringComparison.Ordinal);
        return from < 0 || to <= from ? text : text[(from + begin.Length)..to];
    }
}
