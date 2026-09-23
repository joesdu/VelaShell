using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Auth;
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
        List<ShellStreamNotice> notices = [];
        Assert.IsNull(SshForwardingOptions.Agent(null, notices));
        Assert.IsNull(SshForwardingOptions.Agent(new SshSessionOptions { X11Forwarding = true }, notices));
        Assert.IsNotNull(SshForwardingOptions.Agent(new SshSessionOptions { AgentForwarding = true }, notices));
        Assert.IsEmpty(notices);
    }

    /// <summary>不限定、不确认:与 <c>ssh -A</c> 一致。</summary>
    [TestMethod]
    public void Agent_Default_ExposesWholeAgentWithoutConfirmation()
    {
        AgentForwardPolicy policy = SshForwardingOptions.Agent(new SshSessionOptions { AgentForwarding = true }, [])!;

        Assert.IsEmpty(policy.AllowedKeys);
        Assert.IsNull(policy.ConfirmEachSignature);
    }

    [TestMethod]
    public void Agent_Restricted_ForwardsOnlyTheListedKeys_AndSkipsBrokenLines()
    {
        using var a = InMemorySshSigner.GenerateEd25519();
        using var b = InMemorySshSigner.GenerateEd25519();
        List<ShellStreamNotice> notices = [];

        AgentForwardPolicy policy = SshForwardingOptions.Agent(
            new SshSessionOptions
            {
                AgentForwarding = true,
                AgentForwardKeys = [Line(a, "C:/keys/a"), "ssh-ed25519 !!!不是base64", "垃圾", Line(b)],
            },
            notices)!;

        CollectionAssert.AreEquivalent(
            new[] { a.PublicKey.Sha256Fingerprint, b.PublicKey.Sha256Fingerprint },
            policy.AllowedKeys.Select(k => k.Sha256Fingerprint).ToArray());
        Assert.IsEmpty(notices);
    }

    /// <summary>
    /// 限定了却一把都解析不出来:不转发,并写一行黄字。
    /// 交给库一个空的 AllowedKeys 会被解释成「整个 agent 都可见」—— 把最严的设置翻成了最宽的那一档。
    /// </summary>
    [TestMethod]
    public void Agent_RestrictedButNothingUsable_DoesNotForwardAtAll()
    {
        List<ShellStreamNotice> notices = [];

        AgentForwardPolicy? policy = SshForwardingOptions.Agent(
            new SshSessionOptions { AgentForwarding = true, AgentForwardKeys = ["垃圾"] }, notices);

        Assert.IsNull(policy);
        Assert.HasCount(1, notices);
        Assert.IsTrue(notices[0].IsWarning);
    }

    [TestMethod]
    public async Task Agent_Confirm_AllowOnceAsksEveryTime_AllowForSessionAsksOncePerKey()
    {
        using var a = InMemorySshSigner.GenerateEd25519();
        using var b = InMemorySshSigner.GenerateEd25519();
        FakeSignPrompt prompt = new(AgentSignDecision.AllowForSession);
        var confirm = ConfirmOf(prompt);

        Assert.IsTrue(await confirm(Request(a), CancellationToken.None));
        Assert.IsTrue(await confirm(Request(a), CancellationToken.None));
        Assert.AreEqual(1, prompt.Calls, "本次会话内允许过的钥不该再问");

        // 另一把钥照样要问
        prompt.Next = AgentSignDecision.AllowOnce;
        Assert.IsTrue(await confirm(Request(b), CancellationToken.None));
        Assert.IsTrue(await confirm(Request(b), CancellationToken.None));
        Assert.AreEqual(3, prompt.Calls, "「允许一次」之后下一次还要问");

        AgentSignRequest shown = prompt.LastRequest!;
        Assert.AreEqual("joe@10.0.0.1:22", shown.Target);
        Assert.AreEqual(b.PublicKey.Sha256Fingerprint, shown.Fingerprint);
        Assert.AreEqual("C:/keys/id", shown.Comment);
    }

    [TestMethod]
    public async Task Agent_Confirm_DenyAndMissingPrompt_Refuse()
    {
        using var key = InMemorySshSigner.GenerateEd25519();

        Assert.IsFalse(await ConfirmOf(new FakeSignPrompt(AgentSignDecision.Deny))(Request(key), CancellationToken.None));
        // 没有弹窗可用(无界面宿主):开了确认就一律拒签,而不是悄悄放行
        Assert.IsFalse(await ConfirmOf(null)(Request(key), CancellationToken.None));
    }

    /// <summary>没人应答:到期按拒绝,而不是无限期挂着远端的 ssh。</summary>
    [TestMethod]
    public async Task Agent_Confirm_NoAnswerBeforeDeadline_Refuses()
    {
        using var key = InMemorySshSigner.GenerateEd25519();
        FakeSignPrompt waitsForever = new(AgentSignDecision.AllowOnce) { WaitForCancellation = true };

        bool approved = await ConfirmOf(waitsForever, TimeSpan.FromMilliseconds(100))(Request(key), CancellationToken.None);

        Assert.IsFalse(approved);
    }

    /// <summary>实现方没理会取消、过了期限才交回「允许」:照样拒。</summary>
    [TestMethod]
    public async Task Agent_Confirm_ApprovalAfterDeadline_StillRefused()
    {
        using var key = InMemorySshSigner.GenerateEd25519();
        FakeSignPrompt late = new(AgentSignDecision.AllowForSession) { IgnoreCancellationDelay = TimeSpan.FromMilliseconds(300) };
        var confirm = ConfirmOf(late, TimeSpan.FromMilliseconds(50));

        Assert.IsFalse(await confirm(Request(key), CancellationToken.None));
        // 迟到的「本次会话内允许」也不能记下来
        late.Next = AgentSignDecision.Deny;
        late.IgnoreCancellationDelay = TimeSpan.Zero;
        Assert.IsFalse(await confirm(Request(key), CancellationToken.None));
        Assert.AreEqual(2, late.Calls);
    }

    [TestMethod]
    public void Agent_Describe_MentionsRestrictionAndConfirmation()
    {
        using var key = InMemorySshSigner.GenerateEd25519();
        SshSessionOptions features = new() { AgentForwarding = true, AgentForwardKeys = [Line(key)], AgentForwardConfirm = true };

        string text = SshForwardingOptions.DescribeAgent(features, SshForwardingOptions.Agent(features, [])!);

        Assert.StartsWith(Strings.Get("Ssh_AgentForwardOn"), text);
        Assert.Contains(Strings.Format("Ssh_AgentForwardOnlyKeys", 1), text);
        Assert.Contains(Strings.Get("Ssh_AgentForwardConfirmEach"), text);
    }

    private static string Line(InMemorySshSigner key, string? comment = null) =>
        $"{key.PublicKey.KeyType} {Convert.ToBase64String(key.PublicKey.Blob.Span)}" + (comment is null ? "" : " " + comment);

    private static AgentSignatureRequest Request(InMemorySshSigner key) => new(key.PublicKey, "C:/keys/id");

    private static Func<AgentSignatureRequest, CancellationToken, ValueTask<bool>> ConfirmOf(
        IAgentSignPrompt? prompt, TimeSpan? timeout = null) =>
        SshForwardingOptions.Agent(
            new SshSessionOptions { AgentForwarding = true, AgentForwardConfirm = true },
            [], prompt, "joe@10.0.0.1:22", timeout)!.ConfirmEachSignature!;

    private sealed class FakeSignPrompt(AgentSignDecision decision) : IAgentSignPrompt
    {
        public AgentSignDecision Next { get; set; } = decision;

        public int Calls { get; private set; }

        public AgentSignRequest? LastRequest { get; private set; }

        public bool WaitForCancellation { get; init; }

        public TimeSpan IgnoreCancellationDelay { get; set; }

        public async Task<AgentSignDecision> ConfirmAsync(AgentSignRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            if (WaitForCancellation)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            if (IgnoreCancellationDelay > TimeSpan.Zero)
            {
                await Task.Delay(IgnoreCancellationDelay, CancellationToken.None);
            }
            return Next;
        }
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
