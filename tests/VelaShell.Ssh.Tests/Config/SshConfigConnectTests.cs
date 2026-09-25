// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/09-dialing.md §7 —— ssh_config 要能直接变成连接参数，否则解析只是摆设。

using VelaShell.Ssh.Config;
using VelaShell.Ssh.Channels;
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
        Assert.AreEqual(SshAlgorithmNames.ZlibOpenSsh, options.Algorithms.CompressionClientToServer[0], "Compression yes 没生效");
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
            blocks, "target", new SshConfigConnectSettings { DefaultUserName = "me" });

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

        SshConnectionOptions viaCommand = await SshConfigFile.CreateConnectionOptionsAsync(blocks, "viacmd");
        var command = (ProxyCommandDialer)viaCommand.Dialer;
        Assert.AreEqual("nc -x proxy:1080 %h %p", command.CommandTemplate);
        Assert.AreEqual("joe", command.UserName);

        SshConnectionOptions both = await SshConfigFile.CreateConnectionOptionsAsync(blocks, "both");
        Assert.IsInstanceOfType<SshJumpDialer>(both.Dialer);

        SshConnectionOptions none = await SshConfigFile.CreateConnectionOptionsAsync(blocks, "none");
        Assert.IsInstanceOfType<TcpTransportDialer>(none.Dialer);
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
            blocks, "other", new SshConfigConnectSettings { HostKeyPolicy = given });
        Assert.AreSame(given, plain.HostKeyPolicy);
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

        using var signer = VelaShell.Ssh.Auth.InMemorySshSigner.GenerateEd25519();
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
        SshConfigConnectSettings settings = new() { HostKeyPolicy = given };

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

        using var agentKey = VelaShell.Ssh.Auth.InMemorySshSigner.GenerateEd25519();
        SshConfigConnectSettings settings = new()
        {
            Credentials = [new VelaShell.Ssh.Auth.PasswordCredential("目标的口令"), new VelaShell.Ssh.Auth.PublicKeyCredential(agentKey)],
        };

        SshConnectionOptions options = await SshConfigFile.CreateConnectionOptionsAsync(blocks, "target", settings);
        SshConnectionOptions jump = ((SshJumpDialer)options.Dialer).JumpHost;

        Assert.IsTrue(options.Credentials.Any(c => c is VelaShell.Ssh.Auth.PasswordCredential), "目标照常拿到口令");
        Assert.IsFalse(jump.Credentials.Any(c => c is VelaShell.Ssh.Auth.PasswordCredential), "跳板拿不到目标的口令");
        Assert.IsTrue(jump.Credentials.Any(c => c is VelaShell.Ssh.Auth.PublicKeyCredential), "公钥凭据照常给跳板");
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
                blocks, "k", new SshConfigConnectSettings { IdentityFileSkipped = (path, _) => skipped.Add(path) });

            Assert.HasCount(1, options.Credentials, "坏的那把跳过，好的那把照常用");
            Assert.AreSequenceEqual(new[] { bad }, skipped, "跳过要有个说法");
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
        SshConfigConnectSettings settings = new()
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
        var targetKey = (VelaShell.Ssh.Auth.PublicKeyCredential)options.Credentials.Single();
        var jumpKey = (VelaShell.Ssh.Auth.PublicKeyCredential)jump.Credentials.Single();
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
        Assert.IsNotNull(shell.X11);
        Assert.IsTrue(shell.X11.Trusted);

        // §7.5.8：连接级开关打开的 X11 是尽力而为的 —— 失败不该让 shell 起不来。
        Assert.IsTrue(shell.X11.BestEffort);
    }

    [TestMethod]
    public void 模板里调用方给的X11选项保持严格()
    {
        IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse("""
            Host gui
                ForwardX11 yes
            """);

        X11ForwardOptions explicitX11 = new() { Trusted = true };
        SshShellOptions shell = SshConfigFile.Resolve(blocks, "gui")
            .ApplyToShell(new SshShellOptions { X11 = explicitX11 });

        Assert.AreSame(explicitX11, shell.X11, "模板里显式设了的不会被覆盖");
        Assert.IsFalse(shell.X11!.BestEffort);
    }

    [TestMethod]
    public void 跳板规格的各种写法()
    {
        Assert.AreEqual((null, "host", null), SshConfigFile.ParseJumpSpec("host"));
        Assert.AreEqual(("u", "host", 2222), SshConfigFile.ParseJumpSpec("u@host:2222"));
        Assert.AreEqual(("u", "::1", 22), SshConfigFile.ParseJumpSpec("u@[::1]:22"));
        Assert.AreEqual(("u", "host", 2222), SshConfigFile.ParseJumpSpec("ssh://u@host:2222"));
    }
}
