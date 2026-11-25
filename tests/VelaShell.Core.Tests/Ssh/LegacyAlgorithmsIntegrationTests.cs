using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 「允许老算法」与自定义算法清单在<b>真实的「老设备」</b>上的端到端行为。
/// </summary>
/// <remarks>
/// 靶子是 <c>docker-compose.test.yml</c> 的 <c>ssh-legacy</c>(端口 2225,见 <c>tests/fixtures/ssh-legacy/Dockerfile</c>):
/// 一台收窄成只剩 <c>diffie-hellman-group14-sha1</c> / <c>ssh-rsa</c> / <c>hmac-sha1</c> 的 OpenSSH。
/// 连接走宿主真正的装配路径 —— 要验的是「配置里的开关有没有一路接到线上」,以及谈不成时诊断有没有指对路。
/// </remarks>
[SuppressMessage("Usage", "MSTEST0045:Use cooperative cancellation with [Timeout]",
    Justification = "被等待的 SSH 操作经宿主装配路径,取消令牌由测试上下文给出。")]
[TestClass]
[TestCategory("DockerIntegration")]
[DoNotParallelize]
public class LegacyAlgorithmsIntegrationTests
{
    private const string TestHost = "127.0.0.1";
    private const int TestPort = 2225;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(60_000)]
    public async Task Defaults_CannotNegotiate_AndTheErrorPointsAtTheSetting()
    {
        RequireContainer();

        VelaSshConnectionException ex = await Assert.ThrowsExactlyAsync<VelaSshConnectionException>(
            () => ConnectAsync(null));

        Assert.Contains(Strings.Get("Ssh_AlgoKindKex"), ex.Message);
        Assert.Contains(Strings.Format("Ssh_AlgoMismatchEnable", "diffie-hellman-group14-sha1"), ex.Message,
            "对端提供、本版实现了、只是没放开的算法要点名,并指到连接配置里去");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task LegacySwitch_Connects_AndTheLegacyAlgorithmsAreWhatGetNegotiated()
    {
        RequireContainer();

        await using VelaSshClientWrapper ssh = await ConnectAsync(new SshSessionOptions { LegacyAlgorithms = true });

        Assert.AreEqual("vela-legacy", (await ssh.RunCommandAsync("whoami")).Trim());
        Assert.AreEqual("diffie-hellman-group14-sha1", ssh.InnerConnection!.Algorithms.KeyExchange);
        Assert.AreEqual("ssh-rsa", ssh.InnerConnection.Algorithms.HostKey);
        Assert.AreEqual("hmac-sha1", ssh.InnerConnection.Algorithms.MacClientToServer);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task CustomLists_InOpenSshSyntax_Connect()
    {
        RequireContainer();

        // 照 ~/.ssh/config 里那几行抄过来
        await using VelaSshClientWrapper ssh = await ConnectAsync(new SshSessionOptions
        {
            KexAlgorithms = "+diffie-hellman-group14-sha1",
            HostKeyAlgorithms = "+ssh-rsa",
            Macs = "+hmac-sha1",
        });

        Assert.AreEqual("vela-legacy", (await ssh.RunCommandAsync("whoami")).Trim());
    }

    private async Task<VelaSshClientWrapper> ConnectAsync(SshSessionOptions? features)
    {
        ConnectionInfo info = new()
        {
            Host = TestHost,
            Port = TestPort,
            Username = "vela-legacy",
            AuthMethod = AuthMethod.Password,
            Password = "velapass",
            Ssh = features,
        };
        SshConnectionAssembler.Assembled assembled = SshConnectionAssembler.Create(
            info, hostKey: FixtureHostKeys.Create(), settings: null, prompt: null, alerts: null, proxyResolver: null);
        VelaSshClientWrapper wrapper = new(assembled.Connect, assembled.ConnectTimeout, info.Ssh);
        try
        {
            await wrapper.ConnectAsync(TestContext.CancellationToken);
            return wrapper;
        }
        catch
        {
            await wrapper.DisposeAsync();
            throw;
        }
    }

    private static void RequireContainer()
    {
        try
        {
            using TcpClient tcp = new();
            if (!tcp.ConnectAsync(TestHost, TestPort).Wait(TimeSpan.FromSeconds(3)))
            {
                Assert.Inconclusive($"[SKIP] ssh-legacy 靶机 {TestHost}:{TestPort} 不可达。" +
                                    "运行 'docker compose -f docker-compose.test.yml up -d --build ssh-legacy'。");
            }
        }
        catch (Exception ex) when (ex is SocketException or AggregateException)
        {
            Assert.Inconclusive($"[SKIP] ssh-legacy 靶机 {TestHost}:{TestPort} 不可达:{ex.Message}");
        }
    }
}
