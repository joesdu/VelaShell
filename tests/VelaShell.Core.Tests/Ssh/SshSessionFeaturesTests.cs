using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Forwarding;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 一条配置的 SSH 可选能力(<see cref="SshSessionOptions" />)怎么落到库的选项上:
/// 压缩进算法集,两个转发进 shell 选项。
/// </summary>
[TestClass]
[TestCategory("Ssh")]
public class SshSessionFeaturesTests
{
    private static ConnectionInfo Info(SshSessionOptions? ssh) => new()
    {
        Host = "host",
        Username = "user",
        AuthMethod = AuthMethod.Password,
        Password = "pw",
        Ssh = ssh,
    };

    /// <summary>默认不压缩(与 OpenSSH 一致):只报 <c>none</c>。</summary>
    [TestMethod]
    public void Compression_OffByDefault_OffersOnlyNone()
    {
        SshAlgorithmSet algorithms = SshConnectionAssembler.Algorithms(Info(null));

        Assert.AreSequenceEqual(new[] { "none" }, [.. algorithms.CompressionClientToServer]);
        Assert.AreSequenceEqual(new[] { "none" }, [.. algorithms.CompressionServerToClient]);
    }

    /// <summary>
    /// 开了压缩:<c>zlib@openssh.com</c> 排在 <c>none</c> 前面 ——
    /// 服务端不支持时协商落回 <c>none</c>,不会因此连不上。
    /// </summary>
    /// <remarks>
    /// 不能出现老式的 <c>zlib</c>:它在认证之前就开始压缩,是 CRIME 一类攻击的入口。
    /// </remarks>
    [TestMethod]
    public void Compression_On_PrefersDelayedZlibAndKeepsNoneAsFallback()
    {
        SshAlgorithmSet algorithms = SshConnectionAssembler.Algorithms(Info(new SshSessionOptions { Compression = true }));

        Assert.AreSequenceEqual(
            new[] { "zlib@openssh.com", "none" }, [.. algorithms.CompressionClientToServer]);
        Assert.AreSequenceEqual(
            new[] { "zlib@openssh.com", "none" }, [.. algorithms.CompressionServerToClient]);
    }

    /// <summary>只开压缩不影响其余算法 —— 那一套是安全默认值,不该被顺手改掉。</summary>
    [TestMethod]
    public void Compression_On_LeavesOtherAlgorithmsAlone()
    {
        SshAlgorithmSet on = SshConnectionAssembler.Algorithms(Info(new SshSessionOptions { Compression = true }));

        Assert.AreSequenceEqual([.. SshAlgorithmSet.Default.KeyExchange], [.. on.KeyExchange]);
        Assert.AreSequenceEqual(
            [.. SshAlgorithmSet.Default.EncryptionClientToServer], [.. on.EncryptionClientToServer]);
    }

    [TestMethod]
    public void X11_Off_RequestsNothing()
    {
        List<ShellStreamNotice> notices = [];

        Assert.IsNull(SshForwardingOptions.X11(null, notices));
        Assert.IsNull(SshForwardingOptions.X11(new SshSessionOptions { Compression = true }, notices));
        Assert.IsEmpty(notices);
    }

    /// <summary>
    /// 配置里填了显示地址就用它;有效期设成不过期 —— 交互式会话里
    /// 「开了半小时之后新窗口打不开了」只会让人以为转发坏了。
    /// </summary>
    [TestMethod]
    public void X11_UsesConfiguredDisplay_AndNeverExpires()
    {
        List<ShellStreamNotice> notices = [];

        X11ForwardOptions? options = SshForwardingOptions.X11(
            new SshSessionOptions { X11Forwarding = true, X11Display = "127.0.0.1:1.0", X11Trusted = false },
            notices);

        Assert.IsNotNull(options);
        Assert.AreEqual("127.0.0.1", options.Display!.Host);
        Assert.AreEqual(1, options.Display.Number);
        Assert.IsFalse(options.Trusted);
        Assert.AreEqual(TimeSpan.Zero, options.Timeout);
        Assert.IsEmpty(notices);
    }

    /// <summary>
    /// 配置里的 X11 开关是连接级的,必须尽力而为:本机没 X 服务器、服务端 <c>X11Forwarding no</c>
    /// 时 shell 照开,原因走 <c>SshShell.X11SetupFailure</c> —— 否则开 shell 会被它整个拦下。
    /// </summary>
    [TestMethod]
    public void X11_IsBestEffort()
    {
        List<ShellStreamNotice> notices = [];

        X11ForwardOptions? options = SshForwardingOptions.X11(
            new SshSessionOptions { X11Forwarding = true, X11Display = "127.0.0.1:1.0" }, notices);

        Assert.IsNotNull(options);
        Assert.IsTrue(options.BestEffort);
    }

    /// <summary>
    /// 写错的显示地址不能让 shell 开不起来:跳过 X11,并留一条提示说清楚是哪个值认不出来。
    /// </summary>
    [TestMethod]
    public void X11_UnparsableDisplay_IsSkippedWithAWarning()
    {
        List<ShellStreamNotice> notices = [];

        X11ForwardOptions? options = SshForwardingOptions.X11(
            new SshSessionOptions { X11Forwarding = true, X11Display = "not-a-display" }, notices);

        Assert.IsNull(options);
        Assert.HasCount(1, notices);
        Assert.IsTrue(notices[0].IsWarning);
        Assert.Contains("not-a-display", notices[0].Text);
    }

    /// <summary>
    /// 配置里没写显示地址时,VelaShell 管理的本机 X Server 的显示优先于 <c>DISPLAY</c> 与默认值 ——
    /// 它在运行就说明用户此刻要的是它,而它的显示号不一定是 0。
    /// </summary>
    [TestMethod]
    public void X11_LocalServerDisplay_WinsWhenProfileLeavesItBlank()
    {
        List<ShellStreamNotice> notices = [];

        X11ForwardOptions? options = SshForwardingOptions.X11(
            new SshSessionOptions { X11Forwarding = true }, notices, localServerDisplay: "localhost:3.0");

        Assert.IsNotNull(options);
        Assert.AreEqual(3, options.Display!.Number);
        Assert.IsEmpty(notices);
    }

    /// <summary>配置里明确写了显示地址:那是用户指定的 X 服务端,本机 X Server 不插手。</summary>
    [TestMethod]
    public void X11_ConfiguredDisplay_BeatsLocalServer()
    {
        List<ShellStreamNotice> notices = [];

        X11ForwardOptions? options = SshForwardingOptions.X11(
            new SshSessionOptions { X11Forwarding = true, X11Display = "127.0.0.1:1.0" },
            notices, localServerDisplay: "localhost:3.0");

        Assert.AreEqual(1, options!.Display!.Number);
    }

    [TestMethod]
    public void X11_Describe_FormatsLikeDisplayVariable()
    {
        List<ShellStreamNotice> notices = [];
        X11ForwardOptions options = SshForwardingOptions.X11(
            new SshSessionOptions { X11Forwarding = true, X11Display = "localhost:0.0" }, notices)!;

        Assert.AreEqual("localhost:0.0", SshForwardingOptions.Describe(options.Display!));
    }

    [TestMethod]
    public void Agent_OnlyWhenEnabled()
    {
        Assert.IsNull(SshForwardingOptions.Agent(null));
        Assert.IsNull(SshForwardingOptions.Agent(new SshSessionOptions { X11Forwarding = true }));
        Assert.IsNotNull(SshForwardingOptions.Agent(new SshSessionOptions { AgentForwarding = true }));
    }

    /// <summary>三项都关就是「没有」:存成 null,老配置零迁移。</summary>
    [TestMethod]
    public void Options_IsEmpty_OnlyWhenAllThreeAreOff()
    {
        Assert.IsTrue(new SshSessionOptions().IsEmpty);
        Assert.IsTrue(new SshSessionOptions { X11Display = "localhost:0", X11Trusted = false }.IsEmpty);
        Assert.IsFalse(new SshSessionOptions { Compression = true }.IsEmpty);
        Assert.IsFalse(new SshSessionOptions { AgentForwarding = true }.IsEmpty);
        Assert.IsFalse(new SshSessionOptions { X11Forwarding = true }.IsEmpty);
    }
}
