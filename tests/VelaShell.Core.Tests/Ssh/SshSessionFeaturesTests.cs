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

        Assert.AreSequenceEqual(["none"], [.. algorithms.CompressionClientToServer]);
        Assert.AreSequenceEqual(["none"], [.. algorithms.CompressionServerToClient]);
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
            ["zlib@openssh.com", "none"], [.. algorithms.CompressionClientToServer]);
        Assert.AreSequenceEqual(
            ["zlib@openssh.com", "none"], [.. algorithms.CompressionServerToClient]);
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
        Assert.IsFalse(options.IsTrusted);
        Assert.AreEqual(Timeout.InfiniteTimeSpan, options.Timeout);
        Assert.IsEmpty(notices);
    }

    /// <summary>
    /// 配置里的 X11 开关是连接级的,必须尽力而为:本机没 X 服务器、服务端 <c>X11Forwarding no</c>
    /// 时 shell 照开,原因走 <c>SshShell.X11SetupFailure</c> —— 否则开 shell 会被它整个拦下。
    /// </summary>
    [TestMethod]
    public void X11_ContinuesWithoutItOnFailure()
    {
        List<ShellStreamNotice> notices = [];

        X11ForwardOptions? options = SshForwardingOptions.X11(
            new SshSessionOptions { X11Forwarding = true, X11Display = "127.0.0.1:1.0" }, notices);

        Assert.IsNotNull(options);
        Assert.AreEqual(ForwardFailureMode.Continue, options.FailureMode);
    }

    /// <summary>
    /// 内置 X 服务端给了连接器、且显示正是取自它时,x11 通道经连接器直接接进服务端 —— 受信与非受信都是:非受信模式不跑 xauth,
    /// 连接器带着「非受信」交给服务端(它按 SECURITY 的语义限制这条连接;原先内置引擎没有 SECURITY,非受信不开转发)。
    /// 配置里自己写了显示地址时照旧走套接字。
    /// </summary>
    [TestMethod]
    public void X11_LocalServerConnector_ForItsOwnDisplay_TrustedOrNot()
    {
        List<ShellStreamNotice> notices = [];
        static ValueTask<Stream> connector(CancellationToken _) => ValueTask.FromResult<Stream>(new MemoryStream());

        X11ForwardOptions? trusted = SshForwardingOptions.X11(
            new SshSessionOptions { X11Forwarding = true, X11Trusted = true }, notices, "localhost:10.0", connector);
        X11ForwardOptions? untrusted = SshForwardingOptions.X11(
            new SshSessionOptions { X11Forwarding = true, X11Trusted = false }, notices, "localhost:10.0", connector);
        X11ForwardOptions? explicitDisplay = SshForwardingOptions.X11(
            new SshSessionOptions { X11Forwarding = true, X11Trusted = true, X11Display = "localhost:3" }, notices, "localhost:10.0", connector);

        Assert.AreSame(connector, trusted?.LocalConnector);
        Assert.AreSame(connector, untrusted?.LocalConnector, "非受信同样经连接器");
        Assert.IsFalse(untrusted!.IsTrusted);
        Assert.IsEmpty(notices, "开得起来,不再提示");
        Assert.IsNull(explicitDisplay?.LocalConnector, "用户指定的显示与本机 X Server 无关");
        Assert.AreEqual(3, explicitDisplay?.Display?.Number);
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
        AgentForwardOptions policy = SshForwardingOptions.Agent(new SshSessionOptions { AgentForwarding = true }, [])!;

        Assert.IsNull(policy.AllowedKeys, "没限定时交 null：整个 agent 可见（空列表在库里表示一把都不给）");
        Assert.IsNull(policy.ApproveSignature);
    }

    /// <summary>
    /// 与 X11 同一条理由:本机 agent 没在跑、服务端 <c>AllowAgentForwarding no</c> 都很常见,
    /// shell 照开,原因走 <c>SshShell.AgentSetupFailure</c> —— 曾经是被拒之后去掉 agent 把整个 shell 重开一次。
    /// </summary>
    [TestMethod]
    public void Agent_ContinuesWithoutItOnFailure()
    {
        AgentForwardOptions policy = SshForwardingOptions.Agent(new SshSessionOptions { AgentForwarding = true }, [])!;

        Assert.AreEqual(ForwardFailureMode.Continue, policy.FailureMode);
    }

    [TestMethod]
    public void Agent_Restricted_ForwardsOnlyTheListedKeys_AndSkipsBrokenLines()
    {
        using var a = InMemorySshSigner.GenerateEd25519();
        using var b = InMemorySshSigner.GenerateEd25519();
        List<ShellStreamNotice> notices = [];

        AgentForwardOptions policy = SshForwardingOptions.Agent(
            new SshSessionOptions
            {
                AgentForwarding = true,
                AgentForwardKeys = [Line(a, "C:/keys/a"), "ssh-ed25519 !!!不是base64", "垃圾", Line(b)],
            },
            notices)!;

        Assert.AreSequenceEqual(
            [a.PublicKey.Sha256Fingerprint, b.PublicKey.Sha256Fingerprint], policy.AllowedKeys!.Select(k => k.Sha256Fingerprint).ToArray(), Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
        Assert.IsEmpty(notices);
    }

    /// <summary>
    /// 限定了却一把都解析不出来:不转发,并写一行黄字。
    /// 交给库一个空的 AllowedKeys 等于一把都不给 —— 转发开了也没用,不如明说。
    /// </summary>
    [TestMethod]
    public void Agent_RestrictedButNothingUsable_DoesNotForwardAtAll()
    {
        List<ShellStreamNotice> notices = [];

        AgentForwardOptions? policy = SshForwardingOptions.Agent(
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
        Func<AgentSignatureRequest, CancellationToken, ValueTask<bool>> confirm = ConfirmOf(prompt);

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

    /// <summary>
    /// 确认框要说得出「以谁登录哪台」:目的主机在已知主机里时摆主机名,用户才认得出是不是自己刚敲的 git pull。
    /// </summary>
    [TestMethod]
    public async Task Agent_Confirm_ShowsLoginUserAndKnownDestination()
    {
        using var key = InMemorySshSigner.GenerateEd25519();
        using var github = InMemorySshSigner.GenerateEd25519();
        FakeSignPrompt prompt = new(AgentSignDecision.AllowOnce);
        FakeKnownHosts known = new(
            new KnownHost { Host = "github.com", Port = 22, Fingerprint = github.PublicKey.Sha256Fingerprint },
            new KnownHost { Host = "10.0.0.9", Port = 2222, Fingerprint = github.PublicKey.Sha256Fingerprint },
            new KnownHost { Host = "other", Port = 22, Fingerprint = key.PublicKey.Sha256Fingerprint });

        Assert.IsTrue(await ConfirmOf(prompt, hostKeys: known)(Login(key, "git", github), CancellationToken.None));

        AgentSignRequest shown = prompt.LastRequest!;
        Assert.AreEqual("git", shown.LoginUser);
        Assert.AreEqual(github.PublicKey.Sha256Fingerprint, shown.DestinationFingerprint);
        Assert.AreSequenceEqual(new[] { "github.com", "10.0.0.9:2222" }, shown.DestinationHosts.ToArray());
        Assert.IsNull(shown.SignatureNamespace);
    }

    /// <summary>已知主机读不出来:照样弹窗,只是摆不出主机名 —— 绝不因此挡住确认。</summary>
    [TestMethod]
    public async Task Agent_Confirm_KnownHostsLookupFails_StillAsks()
    {
        using var key = InMemorySshSigner.GenerateEd25519();
        using var host = InMemorySshSigner.GenerateEd25519();
        FakeSignPrompt prompt = new(AgentSignDecision.AllowOnce);

        Assert.IsTrue(await ConfirmOf(prompt, hostKeys: new FakeKnownHosts { Throws = true })(Login(key, "git", host), CancellationToken.None));

        Assert.AreEqual(host.PublicKey.Sha256Fingerprint, prompt.LastRequest!.DestinationFingerprint);
        Assert.IsEmpty(prompt.LastRequest.DestinationHosts);
    }

    /// <summary>
    /// 「本次会话内允许」只管同一件事:批准了以 git 登录 github,不等于也批准了拿这把钥登录别的机器、换个用户,或者签别的东西。
    /// </summary>
    [TestMethod]
    public async Task Agent_Confirm_AllowForSession_OnlyCoversSameUserAndDestination()
    {
        using var key = InMemorySshSigner.GenerateEd25519();
        using var github = InMemorySshSigner.GenerateEd25519();
        using var elsewhere = InMemorySshSigner.GenerateEd25519();
        FakeSignPrompt prompt = new(AgentSignDecision.AllowForSession);
        Func<AgentSignatureRequest, CancellationToken, ValueTask<bool>> confirm = ConfirmOf(prompt);

        Assert.IsTrue(await confirm(Login(key, "git", github), CancellationToken.None));
        Assert.IsTrue(await confirm(Login(key, "git", github), CancellationToken.None));
        Assert.AreEqual(1, prompt.Calls, "同一用户登录同一台:不再问");

        prompt.Next = AgentSignDecision.Deny;
        Assert.IsFalse(await confirm(Login(key, "git", elsewhere), CancellationToken.None), "换了目的主机要重新问");
        Assert.IsFalse(await confirm(Login(key, "root", github), CancellationToken.None), "换了用户要重新问");
        Assert.IsFalse(await confirm(Login(key, "git", destination: null), CancellationToken.None), "核实不了目的主机的也要重新问");
        Assert.IsFalse(await confirm(Request(key) with { SignatureNamespace = "git" }, CancellationToken.None), "签别的东西要重新问");
        Assert.AreEqual(5, prompt.Calls);
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
        Func<AgentSignatureRequest, CancellationToken, ValueTask<bool>> confirm = ConfirmOf(late, TimeSpan.FromMilliseconds(50));

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

    private static AgentSignatureRequest Login(InMemorySshSigner key, string user, InMemorySshSigner? destination) =>
        Request(key) with { UserName = user, Service = "ssh-connection", DestinationHostKey = destination?.PublicKey };

    private static Func<AgentSignatureRequest, CancellationToken, ValueTask<bool>> ConfirmOf(
        IAgentSignPrompt? prompt, TimeSpan? timeout = null, IHostKeyService? hostKeys = null) =>
        SshForwardingOptions.Agent(
            new SshSessionOptions { AgentForwarding = true, AgentForwardConfirm = true },
            [], prompt, "joe@10.0.0.1:22", timeout, hostKeys)!.ApproveSignature!;

    /// <summary>只答「全部已知主机」;别的用不上。</summary>
    private sealed class FakeKnownHosts(params KnownHost[] hosts) : IHostKeyService
    {
        public bool Throws { get; init; }

        public Task<List<KnownHost>> GetKnownHostsAsync(CancellationToken cancellationToken = default) =>
            Throws ? throw new IOException("库文件坏了") : Task.FromResult(hosts.ToList());

        public Task<HostKeyVerification> VerifyHostKeyAsync(string host, int port, string keyType, string fingerprint, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<KnownHost?> FindKnownHostAsync(string host, int port, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<KnownHost>> FindKnownHostKeysAsync(string host, int port, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task TrustHostKeyAsync(string host, int port, string keyType, string fingerprint, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RemoveKnownHostAsync(string host, int port, string? keyType = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

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
