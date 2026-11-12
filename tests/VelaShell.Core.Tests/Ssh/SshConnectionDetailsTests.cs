using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Tests.TestKit;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 连接信息面板的数据(<see cref="ISshClientWrapper.GetConnectionDetails" />):库已经交出来的只读属性一路接到 Core 的快照上,
/// 成功建连时经过的跳板也记下来(库的拨号器只在失败时留逐跳结果)。
/// </summary>
/// <remarks>走宿主真实的装配路径,对端与跳板都是环回上的测试服务端;跳板的隧道里再跑一台目标服务端。</remarks>
[TestClass]
[TestCategory("SshConnection")]
public sealed class SshConnectionDetailsTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task Direct_ReportsPeerAlgorithmsAuthTimingTrafficAndTheOpenShellChannel()
    {
        await using LoopbackSshHost host = new(options: new TestSshServerOptions { Identification = "SSH-2.0-TargetServer_9.9" });
        ConnectionInfo info = Info(host.Port);
        await using Connected connected = await ConnectAsync(info);
        await using IShellStreamWrapper shell = await connected.Wrapper.CreateShellStreamAsync("xterm", 80, 24, 0, 0, 4096);

        SshConnectionDetails details = connected.Wrapper.GetConnectionDetails()!;

        Assert.IsNotNull(details);
        Assert.AreEqual($"joe@127.0.0.1:{host.Port}", details.Target);
        Assert.IsTrue(details.IsAlive);
        Assert.AreEqual("SSH-2.0-TargetServer_9.9", details.PeerVersion);
        Assert.AreEqual("ssh-ed25519", details.HostKey.Type);
        Assert.StartsWith("SHA256:", details.HostKey.Fingerprint);
        Assert.IsFalse(details.HostKey.IsCertificate);
        Assert.AreEqual(64, details.SessionId.Length, "会话 ID 是 SHA-256 交换哈希的十六进制。");
        Assert.AreEqual("password", details.AuthenticationMethod);
        Assert.IsFalse(string.IsNullOrEmpty(details.Algorithms.KeyExchange));
        Assert.AreEqual("ssh-ed25519", details.Algorithms.HostKey);
        Assert.IsFalse(string.IsNullOrEmpty(details.Algorithms.EncryptionClientToServer));
        Assert.AreEqual("none", details.Algorithms.CompressionClientToServer);
        Assert.IsGreaterThan(TimeSpan.Zero, details.Timings.Total);
        Assert.IsGreaterThan(0L, details.BytesSent);
        Assert.IsGreaterThan(0L, details.PacketsReceived);
        Assert.AreEqual(0, details.RekeyCount);
        Assert.IsNull(details.LastRekey);
        SshChannelDetails channel = details.Channels.Single();
        Assert.AreEqual("session", channel.Type);
        Assert.AreEqual(SshChannelPhase.Open, channel.Phase);
        Assert.IsNotNull(channel.OpenedAt);
        Assert.IsEmpty(details.Hops, "直连没有跳板。");
        Assert.IsNull(details.Proxy, "没配代理就是直连。");
    }

    [TestMethod]
    public async Task ViaJumpHost_ListsTheHopWithItsOwnVersionAndAlgorithms()
    {
        await using LoopbackSshHost jump = new(options: new TestSshServerOptions { Identification = "SSH-2.0-JumpServer_1.0" })
        {
            JumpTarget = (new TestChannelScript { CloseAfterScript = false }, new TestSshServerOptions { Identification = "SSH-2.0-TargetServer_9.9" }),
        };
        ConnectionInfo info = new()
        {
            Host = "target.internal",
            Port = 22,
            Username = "joe",
            AuthMethod = AuthMethod.Password,
            Password = LoopbackSshHost.Password,
            JumpHost = Info(jump.Port, user: "jumper"),
        };
        await using Connected connected = await ConnectAsync(info);

        SshConnectionDetails details = connected.Wrapper.GetConnectionDetails()!;

        Assert.AreEqual("SSH-2.0-TargetServer_9.9", details.PeerVersion, "面板的主体是终点那一跳。");
        SshHopDetails hop = details.Hops.Single();
        Assert.AreEqual($"jumper@127.0.0.1:{jump.Port}", hop.Target);
        Assert.AreEqual("SSH-2.0-JumpServer_1.0", hop.PeerVersion);
        Assert.IsFalse(string.IsNullOrEmpty(hop.KeyExchange));
        Assert.AreEqual("ssh-ed25519", hop.HostKey);
        Assert.IsFalse(string.IsNullOrEmpty(hop.Encryption));
        Assert.AreEqual("password", hop.AuthenticationMethod);
        Assert.IsTrue(hop.IsAlive);
        Assert.AreSequenceEqual(["target.internal:22"], jump.TunnelTargets, "目标名交给跳板解析。");
    }

    /// <summary>同一份装配连第二次(重连):跳板从头记,不在上一次的记录后面接着加。</summary>
    [TestMethod]
    public async Task Reconnecting_RecordsTheRouteAfresh()
    {
        await using LoopbackSshHost jump = new()
        {
            JumpTarget = (new TestChannelScript { CloseAfterScript = false }, null),
        };
        ConnectionInfo info = new()
        {
            Host = "target.internal",
            Port = 22,
            Username = "joe",
            AuthMethod = AuthMethod.Password,
            Password = LoopbackSshHost.Password,
            JumpHost = Info(jump.Port),
        };
        SshConnectionAssembler.Assembled assembled = SshConnectionAssembler.Create(
            info, hostKey: null, settings: null, prompt: null, alerts: null, proxyResolver: null);
        using CancellationTokenSource cts = new(Deadline);

        await using (await assembled.Connect(cts.Token))
        {
        }
        await using VelaShell.Ssh.Session.SshConnection second = await assembled.Connect(cts.Token);

        Assert.HasCount(1, assembled.Route.Hops);
        Assert.IsTrue(assembled.Route.Hops[0].Connection.IsAlive, "记的是这一次连上的跳板,不是上一次已经断开的那条。");
    }

    [TestMethod]
    public async Task NotConnectedOrDisposed_GivesNothingAndDoesNotThrow()
    {
        await using LoopbackSshHost host = new();
        ConnectionInfo info = Info(host.Port);
        SshConnectionAssembler.Assembled assembled = SshConnectionAssembler.Create(
            info, hostKey: null, settings: null, prompt: null, alerts: null, proxyResolver: null);
        VelaSshClientWrapper wrapper = new(assembled.Connect, assembled.ConnectTimeout, info.Ssh) { Route = assembled.Route };

        Assert.IsNull(wrapper.GetConnectionDetails(), "还没连上。");
        using CancellationTokenSource cts = new(Deadline);
        await wrapper.ConnectAsync(cts.Token);
        Assert.IsNotNull(wrapper.GetConnectionDetails());
        await wrapper.DisposeAsync();
        Assert.IsNull(wrapper.GetConnectionDetails(), "释放之后。");
    }

    private static ConnectionInfo Info(int port, string user = "joe") => new()
    {
        Host = "127.0.0.1",
        Port = port,
        Username = user,
        AuthMethod = AuthMethod.Password,
        Password = LoopbackSshHost.Password,
    };

    private static async Task<Connected> ConnectAsync(ConnectionInfo info)
    {
        SshConnectionAssembler.Assembled assembled = SshConnectionAssembler.Create(
            info, hostKey: null, settings: null, prompt: null, alerts: null, proxyResolver: null);
        VelaSshClientWrapper wrapper = new(assembled.Connect, assembled.ConnectTimeout, info.Ssh,
            target: Core.Net.HostAddress.FormatTarget(info.Username, info.Host, info.Port), banners: assembled.Banners)
        {
            Route = assembled.Route,
        };
        using CancellationTokenSource cts = new(Deadline);
        try
        {
            await wrapper.ConnectAsync(cts.Token);
        }
        catch
        {
            await wrapper.DisposeAsync();
            throw;
        }
        return new(wrapper);
    }

    private sealed record Connected(VelaSshClientWrapper Wrapper) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Wrapper.DisposeAsync();
    }
}
