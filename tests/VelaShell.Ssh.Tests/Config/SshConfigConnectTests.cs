// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/09-dialing.md §7 —— ssh_config 要能直接变成连接参数，否则解析只是摆设。

using VelaShell.Ssh.Config;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
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
        SshJumpDialer last = (SshJumpDialer)options.Dialer;
        Assert.AreEqual("10.1.0.1", last.JumpHost.Host);
        Assert.AreEqual("jumper", last.JumpHost.UserName);

        SshJumpDialer first = (SshJumpDialer)last.JumpHost.Dialer;
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

        SshJumpDialer jump = (SshJumpDialer)options.Dialer;
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
        StringAssert.Contains(ex.Message, "环");
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
        ProxyCommandDialer command = (ProxyCommandDialer)viaCommand.Dialer;
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
