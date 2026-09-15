using System.Text;

namespace VelaShell.Terminal.Tests;

/// <summary>连接初始化命令回显抑制器:整块剥除、跨块切分、双次命中、超窗放行、巧合前缀不误扣。</summary>
[TestClass]
public class EchoSuppressorTests
{
    private const string Payload = "prompt_nl() { local c; ((c>1)) && echo; }; PROMPT_COMMAND=prompt_nl";

    private static byte[] Needle => Encoding.UTF8.GetBytes(Payload + "\r\n");

    private static string Run(EchoSuppressor s, params string[] chunks)
    {
        var sb = new StringBuilder();
        foreach (string chunk in chunks)
        {
            sb.Append(Encoding.UTF8.GetString(s.Process(Encoding.UTF8.GetBytes(chunk))));
        }
        return sb.ToString();
    }

    [TestMethod]
    public void WholeChunk_EchoRemoved_SurroundingsKept()
    {
        var s = new EchoSuppressor(Needle, 2, TimeSpan.FromSeconds(10));
        string result = Run(s, "banner\r\n " + Payload + "\r\npi@host:~$ ");
        Assert.AreEqual("banner\r\n pi@host:~$ ", result);
    }

    [TestMethod]
    public void TwoOccurrences_BothRemoved()
    {
        var s = new EchoSuppressor(Needle, 2, TimeSpan.FromSeconds(10));
        string result = Run(s, " " + Payload + "\r\npi@host:~$  " + Payload + "\r\npi@host:~$ ");
        Assert.AreEqual(" pi@host:~$  pi@host:~$ ", result);
    }

    [TestMethod]
    public void SplitAcrossChunks_AtEveryBoundary_EchoStillRemoved()
    {
        string whole = "MOTD\r\n " + Payload + "\r\npi@host:~$ ";
        // 从 needle 中段任意切开(前 4 字节内的切分允许漏抑制,见 MinHold 注释)。
        byte[] bytes = Encoding.UTF8.GetBytes(whole);
        int needleStart = whole.IndexOf(Payload, StringComparison.Ordinal);
        for (int split = needleStart + 4; split < bytes.Length - 1; split++)
        {
            var s = new EchoSuppressor(Needle, 2, TimeSpan.FromSeconds(10));
            string part1 = Encoding.UTF8.GetString(s.Process(bytes.AsSpan(0, split).ToArray()));
            string part2 = Encoding.UTF8.GetString(s.Process(bytes.AsSpan(split).ToArray()));
            Assert.AreEqual("MOTD\r\n pi@host:~$ ", part1 + part2, $"split={split}");
        }
    }

    [TestMethod]
    public void HitsExhausted_FurtherIdenticalTextPassesThrough()
    {
        var s = new EchoSuppressor(Needle, 1, TimeSpan.FromSeconds(10));
        string result = Run(s, Payload + "\r\n", Payload + "\r\n");
        Assert.AreEqual(Payload + "\r\n", result);
    }

    [TestMethod]
    public void ExpiredWindow_HeldPrefixReleased()
    {
        var s = new EchoSuppressor(Needle, 2, TimeSpan.FromMilliseconds(1));
        // 先喂一个会被扣住的前缀,等窗口过期后,后续数据应携带被扣字节原样放行。
        string prefix = Payload[..10];
        byte[] first = s.Process(Encoding.UTF8.GetBytes(prefix));
        Thread.Sleep(30);
        byte[] second = s.Process(Encoding.UTF8.GetBytes("XYZ"));
        Assert.AreEqual(prefix + "XYZ",
            Encoding.UTF8.GetString(first) + Encoding.UTF8.GetString(second));
    }

    [TestMethod]
    public void ShortCoincidentalPrefixAtChunkTail_NotHeldBack()
    {
        var s = new EchoSuppressor(Needle, 2, TimeSpan.FromSeconds(10));
        // 块尾是 needle 的前 2 字节("pr"),低于 MinHold,应立即放行不扣。
        byte[] result = s.Process(Encoding.UTF8.GetBytes("pi@host:~$ pr"));
        Assert.AreEqual("pi@host:~$ pr", Encoding.UTF8.GetString(result));
    }

    /// <summary>
    /// 宿主一旦发现抑制器失效就会弃用实例(<c>SshTerminalBridge</c>),此时块尾扣住的部分命中
    /// 必须取得回来——否则再没有下一次 Process 放行它们,那几个字节被永久吞掉。
    /// </summary>
    [TestMethod]
    public void ExpiredWithHeldTail_TakeHeldReturnsBytesInsteadOfLosingThem()
    {
        var s = new EchoSuppressor(Needle, 2, TimeSpan.FromMilliseconds(1));
        string prefix = Payload[..10];
        byte[] first = s.Process(Encoding.UTF8.GetBytes(prefix));

        // 扣住了:本次一个字节都没放出来。
        Assert.IsEmpty(first);
        Thread.Sleep(30);
        Assert.IsTrue(s.Expired);

        // 宿主弃用实例前把扣住的尾巴取回来交还终端;取一次即清空。
        Assert.AreEqual(prefix, Encoding.UTF8.GetString(s.TakeHeld()));
        Assert.IsEmpty(s.TakeHeld());
    }

    /// <summary>
    /// 针太短就拒收 —— 短针在流里到处都是,扣住它会把正常输出剪坏。
    /// </summary>
    /// <remarks>
    /// 宿主据此判断"这条命令短到不值得剥回显"(一条 <c>w</c> 的整行才三个字节),
    /// 而不是等着接异常:那一下会发生在握手里,甩出来就是连接失败。
    /// </remarks>
    [TestMethod]
    public void TooShortNeedle_IsRejectedLoudly()
    {
        byte[] tooShort = Encoding.UTF8.GetBytes("w\r\n");

        Assert.IsLessThan(EchoSuppressor.MinNeedleBytes, tooShort.Length, "一条 w 的整行连四个字节都不到");
        Assert.ThrowsExactly<ArgumentException>(
            () => _ = new EchoSuppressor(tooShort, 2, TimeSpan.FromSeconds(10)));
    }

    // ---- 一个实例多根针:握手时是连着发好几条的 ----

    /// <summary>第二条注入的回显。</summary>
    private const string Payload2 = "cd '/srv/app'";

    private static byte[] Needle2 => Encoding.UTF8.GetBytes(Payload2 + "\r\n");

    /// <summary>
    /// 握手时连着发三条(目录上报脚本 → 初始目录 <c>cd</c> → 认证后命令),而它们在
    /// <b>任何数据到达之前</b>就都发出去了。早先每条各建一个抑制器、后者把前者顶掉,
    /// 结果只有最后一条的回显被剥,前面几条明晃晃留在屏幕上 —— 这条用例盯的就是那个。
    /// </summary>
    [TestMethod]
    public void AddNeedle_TwoInjectionsBackToBack_BothEchoesStripped()
    {
        var s = new EchoSuppressor(Needle, 2, TimeSpan.FromSeconds(10));
        s.AddNeedle(Needle2);

        string result = Run(s, "MOTD\r\n " + Payload + "\r\npi@host:~$  " + Payload2 + "\r\npi@host:~$ ");

        Assert.AreEqual("MOTD\r\n pi@host:~$  pi@host:~$ ", result);
    }

    /// <summary>加针不分先后:后加的那根被切在块边界上照样剥得掉(跨块续判对每根针都成立)。</summary>
    [TestMethod]
    public void AddNeedle_SecondNeedleSplitAcrossChunks_StillStripped()
    {
        var s = new EchoSuppressor(Needle, 2, TimeSpan.FromSeconds(10));
        s.AddNeedle(Needle2);
        string echo = Payload2 + "\r\n";

        string result = Run(s, "x" + echo[..7], echo[7..] + "pi@host:~$ ");

        Assert.AreEqual("xpi@host:~$ ", result);
    }

    // ---- 注入窗口(gateSentinel):把注入**引发的**输出也藏掉 ----

    /// <summary>
    /// 窗口的闭合哨兵 —— <b>被注入的那一行自己打印出来的</b>那条,带本次注入的随机 nonce。
    /// 形状与 <c>ShellIntegrationScript.Build</c> 产出的一致。
    /// </summary>
    private const string Sentinel = "\e]633;P;VelaShell=deadbeef\a";

    /// <summary>
    /// 开一个注入窗口(从这一刻起整段扣住,直到哨兵出现);吞噬窗口给足,让用例自己控制何时闭合。
    /// </summary>
    /// <remarks>
    /// 窗口模式<b>不装抑制针</b> —— 整段都扣着,回显长什么样无所谓,这正是它存在的理由:
    /// 长注入的回显会被各家 shell 折行重绘,逐字节匹配咬不住(真机实测)。
    /// 用例里那条 <see cref="Payload" /> 因此只是"窗口期间对端吐的东西",不是抑制针。
    /// </remarks>
    private static EchoSuppressor Gated(TimeSpan? swallowWindow = null) =>
        EchoSuppressor.OpenWindow(
            Encoding.UTF8.GetBytes(Sentinel),
            TimeSpan.FromSeconds(10),
            swallowWindow ?? TimeSpan.FromSeconds(10));

    /// <summary>
    /// 从武装那一刻起到哨兵为止,<b>一个字节都不上屏</b>:回显、注入引发的报错,统统扣住;
    /// 从哨兵<b>结束处</b>恢复放行 —— 哨兵本身也不上屏,它是我们自己的记号。
    /// </summary>
    [TestMethod]
    public void Gated_SwallowsEverythingUntilTheSentinel()
    {
        EchoSuppressor s = Gated();

        string result = Run(
            s,
            " " + Payload + "\r\n",
            "bash: line 1: syntax error near unexpected token\r\n",
            Sentinel + "pi@host:~$ ");

        Assert.AreEqual("pi@host:~$ ", result);
    }

    /// <summary>
    /// <b>回显被折行重绘成什么样都无所谓</b> —— 这正是窗口取代抑制针的理由。
    /// </summary>
    /// <remarks>
    /// 样本取自真机(<c>ShellIntegrationDockerTests</c> 第一版失败时抓下来的):
    /// zsh 在行尾用 <c>CR</c> + <c>ESC[K</c> 重绘,并把断点处的字符<b>重复一遍</b>;
    /// ash 插一对 <c>CR LF</c>;fish 干脆按列重排。逐字节匹配在这三种下全部失效,
    /// 而"整段扣住"对它们一视同仁。
    /// </remarks>
    [TestMethod]
    [DataRow(" test -n \"${BASH_VER\r\r\nSION:-}\" && eval 'x'\r\n", "ash:行尾插 CR LF")]
    [DataRow(" test -n \"${BASH_VER \r\e[KS\rSION:-}\" && eval 'x'\r\n", "zsh:CR + ESC[K 重绘并重复字符")]
    [DataRow(" test\r\e[35C-n \e[A\r\r\n\e[5C\"${BASH_VERSION:-}\"\r\n", "fish:按列重排")]
    public void Gated_SwallowsHoweverTheEchoWasRedrawn(string mangledEcho, string because)
    {
        EchoSuppressor s = Gated();

        string result = Run(s, mangledEcho, Sentinel + "pi@host:~$ ");

        Assert.AreEqual("pi@host:~$ ", result, because);
    }

    /// <summary>
    /// <b>别家的集成序列关不掉我们的窗口。</b>用户的 rc 里可能本来就装着 VS Code / iTerm2 /
    /// ConEmu 的集成,连上就开始发 —— 认"任意一条集成序列"(electerm 的做法)会被它们提前
    /// 关掉窗口,于是注入的报错照样漏上屏。只认自己那条带 nonce 的哨兵才关得准。
    /// </summary>
    [TestMethod]
    [DataRow("\e]7;file:///x\a", "OSC 7")]
    [DataRow("\e]133;A\a", "OSC 133 命令块标记")]
    [DataRow("\e]633;P;Cwd=/x\a", "OSC 633(VS Code)—— 连同族的都不算")]
    [DataRow("\e]1337;CurrentDir=/x\a", "OSC 1337(iTerm2)")]
    [DataRow("\e]633;P;VelaShell=99999999\a", "别次注入的哨兵(nonce 对不上)")]
    public void Gated_IsNotClosedByAnyoneElsesSequence(string foreign, string because)
    {
        EchoSuppressor s = Gated();

        Assert.IsEmpty(Run(s, Payload + "\r\nnoise" + foreign + "more"), because);
    }

    /// <summary>
    /// <b>横幅 / MOTD 只能靠「等对端安静再武装」来保住,窗口本身不给任何豁免。</b>
    /// </summary>
    /// <remarks>
    /// 这一条是把代价写下来:窗口是"从武装那一刻起整段扣住",武装之前的字节它一个都碰不到,
    /// 武装之后的它一个都不放过 —— 包括此刻恰好还没送完的横幅。所以宿主必须先等对端安静
    /// (<c>SshTerminalBridge.RunWhenOutputIdle</c>)再武装:那不是可选的优化,是这套机制的前提。
    /// </remarks>
    [TestMethod]
    public void Gated_GivesNoAmnestyToWhateverArrivesAfterArming()
    {
        EchoSuppressor s = Gated();

        string result = Run(s, "Last login: Mon Sep 15\r\n" + Payload + "\r\nnoise" + Sentinel + "$ ");

        Assert.AreEqual("$ ", result, "武装之后到达的一切都该被扣住,横幅也不例外");
    }

    /// <summary>
    /// 窗口到期必须**放行**扣住的字节,而不是丢弃 —— 注入没成功时提示符也在那堆字节里,
    /// 丢掉就等于把用户扔在一块空屏前面(和 electerm 的取舍不同,理由见 EchoSuppressor 类注释)。
    /// </summary>
    [TestMethod]
    public void Gated_WhenWindowExpires_ReleasesInsteadOfDiscarding()
    {
        EchoSuppressor s = Gated(TimeSpan.FromMilliseconds(1));
        Run(s, " " + Payload + "\r\nbash: bad substitution\r\n");
        Thread.Sleep(30);

        string released = Run(s, "pi@host:~$ ");

        Assert.Contains("bash: bad substitution", released);
        Assert.EndsWith("pi@host:~$ ", released);
    }

    /// <summary>
    /// 吞噬阶段靠"下一块输出到来"推进,而注入失败时远端<b>恰恰不会再有输出</b>。
    /// 宿主的兜底定时器因此调用 <see cref="EchoSuppressor.ForceFlush" /> 把扣住的字节交出来。
    /// </summary>
    [TestMethod]
    public void Gated_ForceFlush_HandsBackEverythingHeld()
    {
        const string held = " " + Payload + "\r\nbash: command not found\r\npi@host:~$ ";
        EchoSuppressor s = Gated();

        // 窗口还开着:回显、报错、提示符全都扣在手里,一个字节都没放出来。
        Assert.IsEmpty(Run(s, held));

        Assert.AreEqual(held, Encoding.UTF8.GetString(s.ForceFlush()));
    }

    /// <summary>
    /// <b>窗口闭合不等于收工。</b>闭合后要退回继续剥针 —— 后面还排着第二条注入的回显没剥。
    /// 把"吞噬结束"直接当成"放行一切",就会让「先钩子(带窗口)、后用户命令」这条路上的
    /// 第二行漏到屏幕上,而那正是注入窗口当初要解决的问题。
    /// </summary>
    [TestMethod]
    public void Gated_AfterWindowCloses_StillStripsLaterNeedles()
    {
        EchoSuppressor s = Gated();
        Assert.IsEmpty(Run(s, Payload + "\r\n"), "第一条回显吃掉,随即转入吞噬");
        s.AddNeedle(Needle2);

        // 吐回来的字节要继续按回显扫:第二条的回显就贴在哨兵后面。
        string result = Run(s, "noise" + Sentinel + Payload2 + "\r\npi@host:~$ ");

        Assert.AreEqual("pi@host:~$ ", result);
    }

    /// <summary>
    /// <see cref="EchoSuppressor.ForceFlush" /> 同样只结束吞噬,不是收工:
    /// 兜底定时器开火之后,第二条注入的回显照样得剥掉。
    /// </summary>
    [TestMethod]
    public void Gated_ForceFlush_ReturnsToStrippingRatherThanGivingUp()
    {
        EchoSuppressor s = Gated();
        Run(s, "bash: oops\r\n");
        s.AddNeedle(Needle2);

        Assert.AreEqual("bash: oops\r\n", Encoding.UTF8.GetString(s.ForceFlush()));
        Assert.IsFalse(s.Expired, "针还没用完,实例不能被丢掉");
        Assert.AreEqual("pi@host:~$ ", Run(s, Payload2 + "\r\npi@host:~$ "));
    }

    /// <summary>
    /// 吞噬阶段<b>不算失效</b>,哪怕命中数已经用尽:那时手里还扣着字节,宿主一旦按
    /// <see cref="EchoSuppressor.Expired" /> 把实例丢掉,就再没人把它们放出来了。
    /// </summary>
    [TestMethod]
    public void Gated_WhileSwallowing_IsNotExpired()
    {
        EchoSuppressor s = Gated();
        Run(s, " " + Payload + "\r\n" + Payload + "\r\n");

        Assert.IsFalse(s.Expired);
    }

    /// <summary>不开注入窗口时行为必须逐字与从前一致 —— 回显剥掉,其余照常。</summary>
    [TestMethod]
    public void Ungated_LeavesFollowingOutputAlone()
    {
        var s = new EchoSuppressor(Needle, 2, TimeSpan.FromSeconds(10));

        Assert.AreEqual(
            " bash: oops\r\n",
            Run(s, " " + Payload + "\r\nbash: oops\r\n"));
    }

    /// <summary>
    /// <b>用户紧跟着的那条命令,一个字节都不许被吞。</b>窗口在哨兵处就闭合了,而用户的命令
    /// 排在哨兵之后 —— 它的回显由自己那根针剥掉,它的输出(哪怕是报错)原样上屏。
    /// 这条用例把"两边互不打扰"这句话钉死:我们的注入藏干净,用户的命令照常显示。
    /// </summary>
    [TestMethod]
    public void Gated_DoesNotSwallowTheUserCommandThatFollows()
    {
        const string userCommand = "neofetch";
        EchoSuppressor s = Gated();
        s.AddNeedle(Encoding.UTF8.GetBytes(userCommand + "\r\n"));

        string result = Run(
            s,
            Payload + "\r\n",                                   // 我们这行的回显
            "bash: something went wrong\r\n",                   // 我们这行的副作用 —— 该藏
            Sentinel,                                           // 我们这行跑完了 —— 窗口闭合
            "pi@host:~$ " + userCommand + "\r\n",               // 用户那行的回显 —— 该剥
            "OS: Alpine Linux\r\nHost: docker\r\npi@host:~$ "); // 用户那行的输出 —— 必须显示

        Assert.DoesNotContain("something went wrong", result, "我们这行的报错该藏");
        Assert.DoesNotContain(userCommand, result, "用户那行的回显该剥");
        Assert.Contains("OS: Alpine Linux", result, "用户命令的输出必须原样显示");
        Assert.Contains("Host: docker", result);
    }
}
