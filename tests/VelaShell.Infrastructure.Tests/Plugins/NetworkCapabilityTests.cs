using System.Net;
using System.Net.Sockets;
using System.Text;
using NSubstitute;
using VelaShell.Core.Net;
using VelaShell.Infrastructure.Plugins.Capabilities;
using VelaShell.PluginSdk.Network;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>
/// 出站网络能力:插件的 TCP 按宿主代理设置拨号。直连交回对端地址;经代理不交;
/// 代理相关的失败一律是 <see cref="NetworkProxyException" />,绝不退回直连。
/// </summary>
[TestClass]
[TestCategory("Proxy")]
public sealed class NetworkCapabilityTests
{
    private static CancellationTokenSource Deadline() => new(TimeSpan.FromSeconds(15));

    private static IProxyResolver Resolver(ProxyRoute route)
    {
        IProxyResolver resolver = Substitute.For<IProxyResolver>();
        resolver.Resolve(Arg.Any<string>(), Arg.Any<int>()).Returns(route);
        return resolver;
    }

    [TestMethod]
    public async Task Direct_ReturnsPeerEndPointAndCarriesBytes()
    {
        using CancellationTokenSource cts = Deadline();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(cts.Token);
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("hi"), cts.Token);
        });

        await using HostTcpConnection connection = await new NetworkCapability(Resolver(ProxyRoute.Direct))
            .ConnectTcpAsync("127.0.0.1", port, new TcpConnectOptions { KeepAliveIdle = TimeSpan.FromSeconds(30) }, cts.Token);

        Assert.IsFalse(connection.ViaProxy);
        Assert.AreEqual(port, connection.RemoteEndPoint?.Port);
        byte[] buffer = new byte[2];
        await connection.Stream.ReadExactlyAsync(buffer, cts.Token);
        Assert.AreEqual("hi", Encoding.ASCII.GetString(buffer));
        await server;
    }

    [TestMethod]
    public async Task Socks5_HidesEndPointAndSendsTargetName()
    {
        using CancellationTokenSource cts = Deadline();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        int proxyPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var request = new TaskCompletionSource<byte[]>();
        var server = Task.Run(async () =>
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(cts.Token);
            NetworkStream s = client.GetStream();
            await s.ReadExactlyAsync(new byte[3], cts.Token);
            await s.WriteAsync(new byte[] { 0x05, 0x00 }, cts.Token);
            byte[] req = new byte[4 + 1 + 10 + 2]; // "rdp.target"
            await s.ReadExactlyAsync(req, cts.Token);
            request.SetResult(req);
            await s.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, cts.Token);
            await s.WriteAsync(Encoding.ASCII.GetBytes("ok"), cts.Token);
        });

        await using HostTcpConnection connection = await new NetworkCapability(
                Resolver(new ProxyRoute(ProxyKind.Socks5, "127.0.0.1", proxyPort)))
            .ConnectTcpAsync("rdp.target", 3389, cancellationToken: cts.Token);

        Assert.IsTrue(connection.ViaProxy);
        Assert.IsNull(connection.RemoteEndPoint, "经代理时对端是代理,不能交给插件当目标");
        byte[] req = await request.Task;
        Assert.AreEqual("rdp.target", Encoding.ASCII.GetString(req, 5, 10));
        Assert.AreEqual(3389, (req[15] << 8) | req[16]);
        byte[] buffer = new byte[2];
        await connection.Stream.ReadExactlyAsync(buffer, cts.Token);
        Assert.AreEqual("ok", Encoding.ASCII.GetString(buffer));
        await server;
    }

    [TestMethod]
    public async Task MisconfiguredProxy_IsProxyException_NotDirect()
    {
        IProxyResolver resolver = Substitute.For<IProxyResolver>();
        resolver.Resolve(Arg.Any<string>(), Arg.Any<int>()).Returns(_ => throw new ProxyMisconfiguredException("bad proxy"));

        NetworkProxyException error = await Assert.ThrowsExactlyAsync<NetworkProxyException>(
            () => new NetworkCapability(resolver).ConnectTcpAsync("example.com", 23));
        Assert.AreEqual("bad proxy", error.Message);
    }

    [TestMethod]
    public async Task UnreachableProxy_IsProxyException()
    {
        int closedPort;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        NetworkProxyException error = await Assert.ThrowsExactlyAsync<NetworkProxyException>(
            () => new NetworkCapability(Resolver(new ProxyRoute(ProxyKind.Socks5, "127.0.0.1", closedPort)))
                .ConnectTcpAsync("target.example", 23));
        Assert.Contains($"via socks5 127.0.0.1:{closedPort}", error.Message);
    }

    [TestMethod]
    public async Task Arguments_AreValidated()
    {
        var capability = new NetworkCapability(null);
        await Assert.ThrowsAsync<ArgumentException>(() => capability.ConnectTcpAsync(" ", 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => capability.ConnectTcpAsync("h", 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => capability.ConnectTcpAsync("h", 65536));
    }

    [TestMethod]
    public void Timeout_IsClamped()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(15), NetworkCapability.ClampTimeout(TimeSpan.Zero));
        Assert.AreEqual(TimeSpan.FromSeconds(1), NetworkCapability.ClampTimeout(TimeSpan.FromMilliseconds(10)));
        Assert.AreEqual(TcpConnectOptions.MaxConnectTimeout, NetworkCapability.ClampTimeout(TimeSpan.FromHours(1)));
    }
}
