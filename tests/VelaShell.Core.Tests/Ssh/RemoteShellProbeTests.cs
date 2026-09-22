using NSubstitute;
using VelaShell.Core.Ssh;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 注入目录上报钩子(OSC 7)之前的 POSIX shell 探测。
/// </summary>
/// <remarks>
/// 用户报的现象:连 Windows 的 OpenSSH,一登录就在 cmd.exe 里看到
/// <c>'test' 不是内部或外部命令</c> —— 那串给 bash 准备的钩子被 cmd 当命令执行了(#305)。
/// 这里的断言全部围绕"什么样的回答才算 POSIX",样例取自各 shell 对探针命令的真实反应。
/// </remarks>
[TestClass]
[TestCategory("Ssh")]
public class RemoteShellProbeTests
{
    [TestInitialize]
    public void ResetCache() => RemoteShellProbe.ClearCache();

    /// <summary>
    /// 探针必须三样一起考:printf、<c>$((...))</c> 算术展开、<c>${var:-默认值}</c> 默认值展开。
    /// 少一样就会被 cmd.exe(有 printf.exe 时)或 PowerShell(它自己会算 <c>$(...)</c>)蒙混过去。
    /// </summary>
    [TestMethod]
    public void ProbeCommand_ExercisesPrintfAndBothExpansions()
    {
        Assert.Contains("printf", RemoteShellProbe.PosixProbeCommand);
        Assert.Contains("$((6*7))", RemoteShellProbe.PosixProbeCommand);
        Assert.Contains("${vela_probe_ok:-ok}", RemoteShellProbe.PosixProbeCommand);
    }

    /// <summary>bash/zsh/dash/sh:两种展开都做对,退出码 0。</summary>
    [TestMethod]
    public void IsPosixShell_WithExpandedMarker_IsTrue() =>
        Assert.IsTrue(RemoteShellProbe.IsPosixShell(new("vela-posix-42ok\n", "", 0)));

    /// <summary>cmd.exe:没有 printf 这个命令。</summary>
    [TestMethod]
    public void IsPosixShell_WhenCommandNotFound_IsFalse() =>
        Assert.IsFalse(RemoteShellProbe.IsPosixShell(
            new("", "'printf' 不是内部或外部命令,也不是可运行的程序\n", 1)));

    /// <summary>
    /// cmd.exe 而 PATH 上恰好有 MSYS/Git 的 printf.exe:命令跑通了(退出码 0),
    /// 但 cmd 两种展开都不做,打出来的是字面量 —— 只看退出码就会误判成 POSIX。
    /// </summary>
    [TestMethod]
    public void IsPosixShell_WhenNothingExpanded_IsFalse() =>
        Assert.IsFalse(RemoteShellProbe.IsPosixShell(
            new("vela-posix-$((6*7))${vela_probe_ok:-ok}\n", "", 0)));

    /// <summary>
    /// PowerShell 作默认 shell 且 PATH 上有 printf.exe:它自己会把 <c>$((6*7))</c> 算成 42
    /// (实测 <c>"vela-$((6*7))-${vela_probe:-ok}"</c> → <c>vela-42--</c>),但认不得
    /// <c>${var:-默认值}</c>,展开成空。只考算术就会在这里翻车 —— 后半截 ok 就是为它准备的。
    /// </summary>
    [TestMethod]
    public async Task IsPosixShell_WhenOnlyArithmeticExpanded_IsFalse() =>
        Assert.IsFalse(RemoteShellProbe.IsPosixShell(new("vela-posix-42\n", "", 0)));

    /// <summary>ForceCommand 之类:退出码 0,但回来的是别的东西。</summary>
    [TestMethod]
    public async Task IsPosixShell_WhenOutputIsUnrelated_IsFalse() =>
        Assert.IsFalse(RemoteShellProbe.IsPosixShell(new("Welcome to the gateway\n", "", 0)));

    /// <summary>底层根本没给结果(通道开不出来的替身默认值)。</summary>
    [TestMethod]
    public async Task IsPosixShell_WithNullResult_IsFalse() => Assert.IsFalse(RemoteShellProbe.IsPosixShell(null));

    /// <summary>同一台主机只探一次:重连、开新标签都吃缓存,不该反复占对端的 exec 通道。</summary>
    [TestMethod]
    public async Task IsPosixShellAsync_CachesResultPerHost()
    {
        ISshClientWrapper client = Substitute.For<ISshClientWrapper>();
        client.RunCommandDetailedAsync(RemoteShellProbe.PosixProbeCommand, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RemoteCommandResult("vela-posix-42ok\n", "", 0)));
        string key = RemoteShellProbe.CacheKey("cache.example", 22, "root");

        Assert.IsTrue(await RemoteShellProbe.IsPosixShellAsync(client, key, TestContext.CancellationToken));
        Assert.IsTrue(await RemoteShellProbe.IsPosixShellAsync(client, key, TestContext.CancellationToken));

        await client.Received(1).RunCommandDetailedAsync(
            RemoteShellProbe.PosixProbeCommand, Arg.Any<CancellationToken>());
    }

    /// <summary>非 POSIX 的结论同样进缓存:Windows 主机不该每次连接都再问一遍。</summary>
    [TestMethod]
    public async Task IsPosixShellAsync_CachesNegativeResult()
    {
        ISshClientWrapper client = Substitute.For<ISshClientWrapper>();
        client.RunCommandDetailedAsync(RemoteShellProbe.PosixProbeCommand, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RemoteCommandResult("", "'printf' 不是内部或外部命令", 1)));
        string key = RemoteShellProbe.CacheKey("windows.example", 22, "slime");

        Assert.IsFalse(await RemoteShellProbe.IsPosixShellAsync(client, key, TestContext.CancellationToken));
        Assert.IsFalse(await RemoteShellProbe.IsPosixShellAsync(client, key, TestContext.CancellationToken));

        await client.Received(1).RunCommandDetailedAsync(
            RemoteShellProbe.PosixProbeCommand, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 探测失败(exec 被禁、通道异常)返回 false,但**不进缓存**:那是环境噪声不是结论,
    /// 下次连接还要再问 —— 否则一次网络抖动就永久关掉了这台主机的目录跟随。
    /// </summary>
    [TestMethod]
    public async Task IsPosixShellAsync_WhenProbeThrows_IsFalseAndNotCached()
    {
        ISshClientWrapper client = Substitute.For<ISshClientWrapper>();
        client.RunCommandDetailedAsync(RemoteShellProbe.PosixProbeCommand, Arg.Any<CancellationToken>())
            .Returns<Task<RemoteCommandResult>>(_ => throw new InvalidOperationException("exec disabled"));
        string key = RemoteShellProbe.CacheKey("flaky.example", 22, "root");

        Assert.IsFalse(await RemoteShellProbe.IsPosixShellAsync(client, key, TestContext.CancellationToken));
        Assert.IsFalse(await RemoteShellProbe.IsPosixShellAsync(client, key, TestContext.CancellationToken));

        await client.Received(2).RunCommandDetailedAsync(
            RemoteShellProbe.PosixProbeCommand, Arg.Any<CancellationToken>());
    }

    /// <summary>缓存键要认用户:同一台机器上换个用户就可能换了默认 shell。</summary>
    [TestMethod]
    public void CacheKey_DistinguishesUserHostAndPort()
    {
        Assert.AreNotEqual(
            RemoteShellProbe.CacheKey("host", 22, "root"),
            RemoteShellProbe.CacheKey("host", 22, "slime"));
        Assert.AreNotEqual(
            RemoteShellProbe.CacheKey("host", 22, "root"),
            RemoteShellProbe.CacheKey("host", 2222, "root"));
    }

    /// <summary>拿不到主机名时返回空键 = 不缓存,免得几个连接共用一格互相顶掉结论。</summary>
    [TestMethod]
    public async Task CacheKey_WhenHostMissing_IsEmptyAndDisablesCaching()
    {
        Assert.IsEmpty(RemoteShellProbe.CacheKey(null, 22, "root"));
        Assert.IsEmpty(RemoteShellProbe.CacheKey("   ", 22, "root"));

        ISshClientWrapper client = Substitute.For<ISshClientWrapper>();
        client.RunCommandDetailedAsync(RemoteShellProbe.PosixProbeCommand, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RemoteCommandResult("vela-posix-42ok\n", "", 0)));

        await RemoteShellProbe.IsPosixShellAsync(client, string.Empty, TestContext.CancellationToken);
        await RemoteShellProbe.IsPosixShellAsync(client, string.Empty, TestContext.CancellationToken);

        await client.Received(2).RunCommandDetailedAsync(
            RemoteShellProbe.PosixProbeCommand, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 第一道探针除了"是不是 POSIX",还要带回**是哪一种** —— 注入哪一段脚本全看它。
    /// 样例取自各 shell 对 <c>$BASH_VERSION</c> / <c>$ZSH_VERSION</c> 的真实回答。
    /// </summary>
    [TestMethod]
    [DataRow("vela-posix-42ok:5.2.21(1)-release:\n", RemoteShellKind.Bash, "bash")]
    [DataRow("vela-posix-42ok::5.9\n", RemoteShellKind.Zsh, "zsh")]
    [DataRow("vela-posix-42ok::\n", RemoteShellKind.PosixSh, "dash/ash/ksh:两个版本变量都空")]
    public void ClassifyPosixProbe_ReadsShellKindFromVersionFields(
        string stdout, RemoteShellKind expected, string because) =>
        Assert.AreEqual(expected, RemoteShellProbe.ClassifyPosixProbe(new(stdout, "", 0)), because);

    /// <summary>
    /// 标记对不上时必须是 <see cref="RemoteShellKind.Unknown" /> 而不是
    /// <see cref="RemoteShellKind.NonPosix" />:fish 也倒在这一道上(它对 <c>${…}</c>
    /// 解析期就报错),还得再问一句才能分清"是 fish"还是"是 cmd.exe"。
    /// </summary>
    [TestMethod]
    public void ClassifyPosixProbe_WithoutMarker_IsUnknownNotNonPosix() =>
        Assert.AreEqual(
            RemoteShellKind.Unknown,
            RemoteShellProbe.ClassifyPosixProbe(new("fish: Expected a variable name after this $.", "", 127)));

    /// <summary>fish 认出来的唯一凭据:标记后面跟着一个既非空、又不含 <c>$</c> 的版本号。</summary>
    [TestMethod]
    public void IsFishShell_WithRealVersion_IsTrue() =>
        Assert.IsTrue(RemoteShellProbe.IsFishShell(new("vela-fish-3.7.1\n", "", 0)));

    /// <summary>cmd.exe 不做变量展开,原样回显 —— 含 <c>$</c>,判否。</summary>
    [TestMethod]
    public void IsFishShell_WhenEchoedLiterally_IsFalse() =>
        Assert.IsFalse(RemoteShellProbe.IsFishShell(new("vela-fish-$FISH_VERSION\n", "", 0)));

    /// <summary>PowerShell 把未知变量展开成空 —— 版本为空,判否(否则 #305 换个 shell 重演)。</summary>
    [TestMethod]
    public void IsFishShell_WhenVersionEmpty_IsFalse() =>
        Assert.IsFalse(RemoteShellProbe.IsFishShell(new("vela-fish-\n", "", 0)));

    /// <summary>
    /// fish 要两道探针才认得出来:第一道倒在 <c>${…}</c> 上,第二道才开口。
    /// 顺带验短路 —— 第一道成功的机器绝不该多付第二次往返。
    /// </summary>
    [TestMethod]
    public async Task DetectAsync_FallsBackToFishProbe_OnlyWhenPosixProbeFails()
    {
        ISshClientWrapper client = Substitute.For<ISshClientWrapper>();
        client.RunCommandDetailedAsync(RemoteShellProbe.PosixProbeCommand, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RemoteCommandResult("", "fish: Expected a variable name", 127)));
        client.RunCommandDetailedAsync(RemoteShellProbe.FishProbeCommand, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RemoteCommandResult("vela-fish-3.7.1\n", "", 0)));

        RemoteShellKind kind = await RemoteShellProbe.DetectAsync(
            client, RemoteShellProbe.CacheKey("fish.example", 22, "root"), TestContext.CancellationToken);

        Assert.AreEqual(RemoteShellKind.Fish, kind);
    }

    /// <summary>bash 机器不该被第二道探针连累:第一道就出结论,fish 那句一次都不发。</summary>
    [TestMethod]
    public async Task DetectAsync_WhenPosixProbeSucceeds_SkipsFishProbe()
    {
        ISshClientWrapper client = Substitute.For<ISshClientWrapper>();
        client.RunCommandDetailedAsync(RemoteShellProbe.PosixProbeCommand, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RemoteCommandResult("vela-posix-42ok:5.2.21(1)-release:\n", "", 0)));

        RemoteShellKind kind = await RemoteShellProbe.DetectAsync(
            client, RemoteShellProbe.CacheKey("bash.example", 22, "root"), TestContext.CancellationToken);

        Assert.AreEqual(RemoteShellKind.Bash, kind);
        await client.DidNotReceive().RunCommandDetailedAsync(
            RemoteShellProbe.FishProbeCommand, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 两道都不认 = cmd.exe / PowerShell(#305)。这是**结论**,该进缓存 ——
    /// 与"探测失败"必须分开,后者是噪声、下次还要再问。
    /// </summary>
    [TestMethod]
    public async Task DetectAsync_WhenNeitherProbeMatches_IsNonPosixAndCached()
    {
        ISshClientWrapper client = Substitute.For<ISshClientWrapper>();
        client.RunCommandDetailedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RemoteCommandResult("", "'printf' 不是内部或外部命令", 1)));
        string key = RemoteShellProbe.CacheKey("windows.example", 22, "slime");

        Assert.AreEqual(RemoteShellKind.NonPosix,
            await RemoteShellProbe.DetectAsync(client, key, TestContext.CancellationToken));
        Assert.AreEqual(RemoteShellKind.NonPosix,
            await RemoteShellProbe.DetectAsync(client, key, TestContext.CancellationToken));

        await client.Received(1).RunCommandDetailedAsync(
            RemoteShellProbe.PosixProbeCommand, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// **两道探针连结果都没拿到 = 没人回答**,该按 <see cref="RemoteShellKind.Unknown" /> 处理
    /// 并且<b>不进缓存</b> —— 那是噪声(通道开不出来、包装层返回 null),不是
    /// "这台机器就是 cmd.exe" 的结论。
    /// </summary>
    /// <remarks>
    /// 混为一谈的代价不只是少一次重试:<see cref="RemoteShellKind.NonPosix" /> 还会连带
    /// 关掉摘历史前缀(<see cref="ShellHistoryScrub.SupportedBy" />),于是一次网络抖动
    /// 就把这台机器的注入行永久留在了用户的命令历史里。
    /// </remarks>
    [TestMethod]
    public async Task DetectAsync_WhenProbesReturnNothing_IsUnknownAndNotCached()
    {
        ISshClientWrapper client = Substitute.For<ISshClientWrapper>();
        client.RunCommandDetailedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RemoteCommandResult>(null!));
        string key = RemoteShellProbe.CacheKey("silent.example", 22, "root");

        Assert.AreEqual(RemoteShellKind.Unknown,
            await RemoteShellProbe.DetectAsync(client, key, TestContext.CancellationToken));
        Assert.AreEqual(RemoteShellKind.Unknown,
            await RemoteShellProbe.DetectAsync(client, key, TestContext.CancellationToken));

        await client.Received(2).RunCommandDetailedAsync(
            RemoteShellProbe.PosixProbeCommand, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// fish 在 <see cref="RemoteShellProbe.IsPosixShellAsync" /> 那条线上必须仍是 false ——
    /// 远端进程 / 资源采集的命令通篇是 fish 不认的展开,放它过去就是换个 shell 重演 #305。
    /// </summary>
    [TestMethod]
    public async Task IsPosixShellAsync_TreatsFishAsNonPosix()
    {
        ISshClientWrapper client = Substitute.For<ISshClientWrapper>();
        client.RunCommandDetailedAsync(RemoteShellProbe.PosixProbeCommand, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RemoteCommandResult("", "fish: Expected a variable name", 127)));
        client.RunCommandDetailedAsync(RemoteShellProbe.FishProbeCommand, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RemoteCommandResult("vela-fish-3.7.1\n", "", 0)));
        string key = RemoteShellProbe.CacheKey("fish2.example", 22, "root");

        Assert.IsFalse(await RemoteShellProbe.IsPosixShellAsync(client, key, TestContext.CancellationToken));
        Assert.AreEqual(RemoteShellKind.Fish,
            await RemoteShellProbe.DetectAsync(client, key, TestContext.CancellationToken));
    }

    /// <summary>MSTest 注入的测试上下文(取消令牌)。</summary>
    public TestContext TestContext { get; set; } = null!;
}
