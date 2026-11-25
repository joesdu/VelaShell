using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Infrastructure.Tunnels;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 隧道在<b>真实 OpenSSH 服务端</b>上的端到端行为:从 <see cref="TunnelConfig" /> 起,经 <see cref="TunnelService" />、
/// 宿主的 SSH 客户端包装与 SSH 库的转发器,一直到线上。
/// </summary>
/// <remarks>
/// <para>
/// 靶子是 <c>docker-compose.test.yml</c> 的 <c>ssh-shells</c>(端口 2223,账号 vela-bash,Alpine 的 OpenSSH,
/// <c>AllowTcpForwarding</c> / <c>AllowStreamLocalForwarding</c> 都是默认的开)。远端那头用 bash 的 <c>/dev/tcp</c>
/// 当客户端,不依赖靶机上装了什么别的工具。
/// </para>
/// <para>
/// 连接走宿主真正的装配路径(<see cref="SshConnectionAssembler" /> + <see cref="VelaSshClientWrapper" />),
/// 隧道走 <see cref="TunnelService" /> —— 要验的正是「面板上填的那几项有没有一路接到线上」。
/// </para>
/// </remarks>
[SuppressMessage("Usage", "MSTEST0045:Use cooperative cancellation with [Timeout]",
    Justification = "被等待的 docker/SSH 操作不接受测试取消令牌,协作取消无法中断它们。")]
[TestClass]
[TestCategory("DockerIntegration")]
[DoNotParallelize]
public class TunnelForwardingIntegrationTests
{
    private const string TestHost = "127.0.0.1";
    private const int TestPort = 2223;
    private const string TestUser = "vela-bash";
    private const string TestPassword = "velapass";

    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// 限速真的生效:远端往一条限速 64 KB/s 的远程转发里灌 256 KB,本机这头收完要三秒上下(一秒的突发额度之后按速率补),
    /// 途中任何一段时间里多收的都不超过令牌桶的上限;速率与连接快照(来源、目标、到目前为止的字节数)也从库一路同步到了 <see cref="TunnelInfo" />。
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task RemoteForward_RateLimitIsEnforcedAndTheConnectionShowsUpWhileItRuns()
    {
        RequireContainer();
        await using VelaSshClientWrapper ssh = await ConnectAsync();
        await using TunnelService service = CreateService(ssh);
        using TcpListener sink = new(IPAddress.Loopback, 0);
        sink.Start();
        int sinkPort = ((IPEndPoint)sink.LocalEndpoint).Port;
        int serverPort = RandomServerPort();
        const int payload = 256 * 1024;

        TunnelInfo tunnel = await service.CreateRemoteForwardAsync(SessionId, new TunnelConfig
        {
            Type = TunnelType.RemoteForward,
            Name = "limited",
            LocalHost = "127.0.0.1",
            LocalPort = (uint)sinkPort,
            RemoteHost = "127.0.0.1",
            RemotePort = (uint)serverPort,
            MaxBytesPerSecond = 64 * 1024
        });

        Task<string> push = ssh.RunCommandAsync(
            $"head -c {payload} /dev/zero > /dev/tcp/127.0.0.1/{serverPort} && echo PUSHED");
        using TcpClient accepted = await sink.AcceptTcpClientAsync(TestContext.CancellationToken);
        var elapsed = Stopwatch.StartNew();
        Task<long> drain = DrainAsync(accepted.GetStream());

        // 搬运途中:连接快照里有这一条,目标是本机的 sink。
        // 本机 sink 先被连上、回连的通道随后才确认并开始搬运,所以等它进快照。
        await WaitForAsync(() =>
        {
            service.RefreshStatistics();
            // 计数与快照是两次读的,刚开始搬的那一刻可能一个已经算上、另一个还没有,所以两个一起等。
            return tunnel.Connections.Count == 1 && tunnel.ActiveConnections == 1;
        }, "搬运途中的连接应当出现在快照里、计进活跃数。");
        Assert.AreEqual($"127.0.0.1:{sinkPort}", tunnel.Connections.Single().Target);

        // 搬完之前每 50 毫秒看一眼快照。库按一秒的额度放行、一段不超过一块缓冲(32 KiB),计数是一段一段往上走的:
        // 任意两次读数之间多出来的,不超过「这段时间按速率补的 + 一秒的突发 + 读数那一刻正在写的一段」——
        // 读数在两次取时刻之间,窗口取前一次的「之前」到后一次的「之后」,机器再忙也只会把窗口拉长、不会冤枉。
        // 曾经按一次读到的整块等、整块写:机器忙时 200 KB 一块到达,先等三秒再一口气写出,计数从 0 跳到 200 KB。
        List<(double Before, double After, long Bytes, double Rate)> samples = [];
        while (!drain.IsCompleted)
        {
            double before = elapsed.Elapsed.TotalSeconds;
            service.RefreshStatistics();
            long bytes = tunnel.Connections.SingleOrDefault()?.BytesReceived ?? 0;
            samples.Add((before, elapsed.Elapsed.TotalSeconds, bytes, tunnel.ReceivedBytesPerSecond));
            await Task.Delay(50, TestContext.CancellationToken);
        }

        long received = await drain;
        elapsed.Stop();
        Assert.Contains("PUSHED", await push);
        Assert.AreEqual(payload, received);
        Assert.IsGreaterThanOrEqualTo(2.9, elapsed.Elapsed.TotalSeconds,
            $"64 KB/s 收 256 KB:一秒的突发之后还有 192 KB,至少三秒,实际 {elapsed.Elapsed.TotalSeconds:0.00} 秒 —— 限速没生效。");
        Assert.Contains(s => s.Bytes is > 0 and < payload, samples,
            "搬运途中应当读得到已经收回、还没收完的字节数:" + string.Join(", ", samples.Select(s => s.Bytes)));
        Assert.Contains(s => s.Bytes < payload && s.Rate > 0, samples, "还没收完时速率就该有读数。");
        const double rate = 64 * 1024;
        const double segment = 32 * 1024;
        // 定时器可能比高精度时钟早醒一个系统时钟周期(Windows 上约 16 ms),窗口按多 20 ms 算。
        const double timerSlack = 0.02;
        double closest = double.MaxValue;
        for (int i = 0; i < samples.Count; i++)
        {
            for (int j = i + 1; j < samples.Count; j++)
            {
                double window = samples[j].After - samples[i].Before + timerSlack;
                double allowed = (rate * window) + rate + segment;
                long gained = samples[j].Bytes - samples[i].Bytes;
                closest = Math.Min(closest, allowed - gained);
                Assert.IsLessThanOrEqualTo(allowed, gained,
                    $"{samples[i].Before:0.00}–{samples[j].After:0.00} 秒之间多收了 {gained} 字节,"
                    + $"超过令牌桶的上限 {allowed:0}:单次突发超过了一秒的额度。");
            }
        }
        TestContext.WriteLine($"{samples.Count} 次读数,离令牌桶上限最近的一次还差 {closest:0} 字节。");

        // 收完了、本机这头还没关:这条连接仍在快照里,字节数与速率都已从库同步过来。
        await WaitForAsync(() =>
        {
            service.RefreshStatistics();
            return tunnel.Connections.SingleOrDefault()?.BytesReceived == payload && tunnel.ReceivedBytesPerSecond > 0;
        }, "连接快照里的字节数与速率应当同步到 TunnelInfo。");

        // 本机这头也关掉,这条连接才算搬完、从快照里摘掉(半关闭:另一个方向还开着时它仍在搬)。
        accepted.Dispose();
        await WaitForAsync(() =>
        {
            service.RefreshStatistics();
            return tunnel.Connections.Count == 0;
        }, "搬完的连接应当从快照里摘掉。");
    }

    /// <summary>
    /// 远程动态转发:远端把服务器上的那个端口当 SOCKS5 代理,名单里的目标由本机替它连上、双向搬运;
    /// 名单外的目标回「规则不允许」(0x02),隧道行上记一句本地化的提示。
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task RemoteDynamicForward_ConnectsAllowedTargetsAndRefusesTheRest()
    {
        RequireContainer();
        await using VelaSshClientWrapper ssh = await ConnectAsync();
        await using TunnelService service = CreateService(ssh);
        using TcpListener echo = new(IPAddress.Loopback, 0);
        echo.Start();
        int echoPort = ((IPEndPoint)echo.LocalEndpoint).Port;
        int serverPort = RandomServerPort();

        TunnelInfo tunnel = await service.CreateRemoteDynamicForwardAsync(SessionId, new TunnelConfig
        {
            Type = TunnelType.RemoteDynamicForward,
            Name = "socks-back",
            LocalHost = "127.0.0.1",
            LocalPort = 0,
            RemoteHost = "127.0.0.1",
            RemotePort = (uint)serverPort,
            PermitRemoteOpen = [$"127.0.0.1:{echoPort}"]
        });

        Task serve = ServeUppercaseEchoOnceAsync(echo);
        string allowed = await ssh.RunCommandAsync(SocksClientScript(serverPort, echoPort, "ping"));
        await serve.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);

        Assert.Contains("M=05 00", allowed, $"SOCKS 方法协商没过。远端输出:\n{allowed}");
        Assert.Contains("R=05 00", allowed, $"名单里的目标应当回成功。远端输出:\n{allowed}");
        Assert.Contains("D=PING", allowed, $"数据没有经本机的目标原样回来。远端输出:\n{allowed}");
        Assert.IsNull(tunnel.LastError);

        // 被拒之后通道随即关掉:不再往里写(写一个关掉的套接字,bash 会吃 SIGPIPE)。
        string refused = await ssh.RunCommandAsync(SocksClientScript(serverPort, echoPort == 65535 ? 1 : echoPort + 1, payload: null));
        Assert.Contains("R=05 02", refused, $"名单外的目标应当回「规则不允许」。远端输出:\n{refused}");
        await WaitForAsync(() => tunnel.LastError is not null, "被拒的那一条应当记到隧道行上。");
        Assert.AreEqual(Strings.Get("TunnelSvc_TargetNotPermitted"), tunnel.LastError);
    }

    /// <summary>
    /// Unix 域套接字的两种转发首尾相接、一次走通:
    /// 远程转发(服务器上的 <c>/tmp/…sock</c> → 本机的套接字,两头都是套接字)接着一条本地转发(本机端口 → 服务器上那个套接字)。
    /// 往本机端口写的数据经 <c>direct-streamlocal</c> 到服务器上的套接字,又经 <c>forwarded-streamlocal</c> 回到本机的套接字服务,大写回来。
    /// 本机这头的 AF_UNIX 在 Windows 上同样能用(Windows 10 1803 起)。
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task UnixSocketForwards_RemoteSocketToLocalSocket_AndLocalPortToRemoteSocket()
    {
        RequireContainer();
        await using VelaSshClientWrapper ssh = await ConnectAsync();
        await using TunnelService service = CreateService(ssh);
        string localSocket = Path.Combine(Path.GetTempPath(), $"vela-tun-{Guid.NewGuid():N}.sock");
        string remoteSocket = $"/tmp/vela-tun-{Guid.NewGuid():N}.sock";
        using Socket echo = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        echo.Bind(new UnixDomainSocketEndPoint(localSocket));
        echo.Listen(4);
        int localPort = FreeLocalPort();
        try
        {
            TunnelInfo back = await service.CreateRemoteForwardAsync(SessionId, new TunnelConfig
            {
                Type = TunnelType.RemoteForward,
                Name = "socket-back",
                LocalHost = "127.0.0.1",
                LocalPort = 1,
                RemoteHost = "127.0.0.1",
                RemotePort = 1,
                LocalSocketPath = localSocket,
                RemoteSocketPath = remoteSocket
            });
            TunnelInfo there = await service.CreateLocalForwardAsync(SessionId, new TunnelConfig
            {
                Type = TunnelType.LocalForward,
                Name = "to-socket",
                LocalHost = "127.0.0.1",
                LocalPort = (uint)localPort,
                RemoteHost = "127.0.0.1",
                RemotePort = 1,
                RemoteSocketPath = remoteSocket
            });

            Task serve = ServeUppercaseEchoOnceAsync(echo);
            using TcpClient client = new();
            await client.ConnectAsync(IPAddress.Loopback, localPort, TestContext.CancellationToken);
            NetworkStream stream = client.GetStream();
            await stream.WriteAsync("hello"u8.ToArray(), TestContext.CancellationToken);
            byte[] reply = new byte[5];
            await stream.ReadExactlyAsync(reply, TestContext.CancellationToken);
            Assert.AreEqual("HELLO", System.Text.Encoding.ASCII.GetString(reply));

            client.Client.Shutdown(SocketShutdown.Send);
            await serve.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);
            Assert.IsNull(back.LastError, back.LastError);
            Assert.IsNull(there.LastError, there.LastError);
            service.RefreshStatistics();
            Assert.AreEqual(1, back.TotalConnections);
            Assert.AreEqual(1, there.TotalConnections);
        }
        finally
        {
            await ssh.RunCommandAsync($"rm -f {remoteSocket}");
            File.Delete(localSocket);
        }
    }

    /// <summary>
    /// 本地转发在本机的套接字文件上监听(<c>-L /路径/x.sock:127.0.0.1:2222</c>):连那个套接字,读到的是服务器上 sshd 的版本行;
    /// 停掉隧道之后套接字文件被删掉。
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task LocalForward_ListensOnALocalSocketFile()
    {
        RequireContainer();
        await using VelaSshClientWrapper ssh = await ConnectAsync();
        await using TunnelService service = CreateService(ssh);
        string localSocket = Path.Combine(Path.GetTempPath(), $"vela-tun-{Guid.NewGuid():N}.sock");
        try
        {
            TunnelInfo tunnel = await service.CreateLocalForwardAsync(SessionId, new TunnelConfig
            {
                Type = TunnelType.LocalForward,
                Name = "sshd-banner",
                LocalHost = "127.0.0.1",
                LocalPort = 1,
                RemoteHost = "127.0.0.1",
                RemotePort = 2222,
                LocalSocketPath = localSocket
            });
            Assert.IsTrue(File.Exists(localSocket));

            using (Socket client = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
            {
                await client.ConnectAsync(new UnixDomainSocketEndPoint(localSocket), TestContext.CancellationToken);
                byte[] banner = new byte[8];
                int read = 0;
                while (read < banner.Length)
                {
                    int n = await client.ReceiveAsync(banner.AsMemory(read), SocketFlags.None, TestContext.CancellationToken);
                    Assert.AreNotEqual(0, n, "没读到版本行就断了。");
                    read += n;
                }
                Assert.AreEqual("SSH-2.0-", System.Text.Encoding.ASCII.GetString(banner));
            }

            await service.StopTunnelAsync(tunnel.Id);
            Assert.IsFalse(File.Exists(localSocket), "停掉隧道之后套接字文件要删掉。");
        }
        finally
        {
            File.Delete(localSocket);
        }
    }

    /// <summary>本机一个空着的 TCP 端口(绑 0 → 读端口 → 放掉)。</summary>
    private static int FreeLocalPort()
    {
        using TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    /// <summary>本机那头的 Unix 套接字服务:接一条连接,把读到的转成大写回送,读到对端关闭为止。</summary>
    private async Task ServeUppercaseEchoOnceAsync(Socket listener)
    {
        using Socket client = await listener.AcceptAsync(TestContext.CancellationToken);
        await using NetworkStream stream = new(client, ownsSocket: false);
        await UppercaseEchoAsync(stream);
    }

    /// <summary>
    /// 远端那头的 SOCKS5 客户端(bash 的 <c>/dev/tcp</c> + 逐字节的 <c>dd</c>,不靠靶机上另装工具):
    /// 协商「不认证」,请求连 <c>127.0.0.1:目标端口</c>,发一段数据,把方法应答、请求应答与回来的数据分别打出来。
    /// <paramref name="payload" /> 为 <see langword="null" /> 时只走到请求应答为止。
    /// </summary>
    private static string SocksClientScript(int proxyPort, int targetPort, string? payload) =>
        $"exec 3<>/dev/tcp/127.0.0.1/{proxyPort}; " +
        "printf '\\x05\\x01\\x00' >&3; " +
        "echo \"M=$(dd bs=1 count=2 <&3 2>/dev/null | od -An -tx1 | tr -s ' ' | sed 's/^ //')\"; " +
        $"printf '\\x05\\x01\\x00\\x01\\x7f\\x00\\x00\\x01\\x{targetPort >> 8:x2}\\x{targetPort & 0xFF:x2}' >&3; " +
        "echo \"R=$(dd bs=1 count=10 <&3 2>/dev/null | od -An -tx1 | tr -s ' ' | sed 's/^ //')\"" +
        (payload is null
            ? string.Empty
            : $"; printf '{payload}' >&3; echo \"D=$(timeout 5 dd bs=1 count={payload.Length} <&3 2>/dev/null)\"");

    /// <summary>本机那头的目标:接一条连接,把读到的转成大写回送,读到对端关闭为止。</summary>
    private async Task ServeUppercaseEchoOnceAsync(TcpListener listener)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(TestContext.CancellationToken);
        await UppercaseEchoAsync(client.GetStream());
    }

    /// <summary>把读到的转成大写回送,读到对端关闭为止。</summary>
    private async Task UppercaseEchoAsync(Stream stream)
    {
        byte[] buffer = new byte[1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, TestContext.CancellationToken)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                buffer[i] = (byte)char.ToUpperInvariant((char)buffer[i]);
            }
            await stream.WriteAsync(buffer.AsMemory(0, read), TestContext.CancellationToken);
        }
    }

    // ------------------------------------------------------------ 装配

    private static readonly Guid SessionId = Guid.NewGuid();

    /// <summary>一个只认这一条会话、把它交给真实客户端包装的隧道服务。</summary>
    private static TunnelService CreateService(VelaSshClientWrapper ssh)
    {
        ISshConnectionService connections = Substitute.For<ISshConnectionService>();
        connections.GetSession(SessionId).Returns(new SshSession
        {
            SessionId = SessionId,
            Status = SessionStatus.Connected,
            ConnectionInfo = new()
            {
                Host = TestHost,
                Port = TestPort,
                Username = TestUser,
                AuthMethod = AuthMethod.Password
            }
        });
        return new TunnelService(connections, _ => ssh, null, (_, _) => false);
    }

    private async Task<VelaSshClientWrapper> ConnectAsync()
    {
        ConnectionInfo info = new()
        {
            Host = TestHost,
            Port = TestPort,
            Username = TestUser,
            AuthMethod = AuthMethod.Password,
            Password = TestPassword,
        };
        SshConnectionAssembler.Assembled assembled = SshConnectionAssembler.Create(
            info, hostKey: FixtureHostKeys.Create(), settings: null, prompt: null, alerts: null, proxyResolver: null);
        VelaSshClientWrapper wrapper = new(assembled.Connect, assembled.ConnectTimeout, info.Ssh);
        await wrapper.ConnectAsync(TestContext.CancellationToken);
        return wrapper;
    }

    /// <summary>服务端上一个大概率空着的高端口(多条用例、多次运行之间不撞)。</summary>
    private static int RandomServerPort() => 20000 + Random.Shared.Next(0, 20000);

    /// <summary>等某个条件成立(转发器在后台收尾),至多 10 秒。</summary>
    private async Task WaitForAsync(Func<bool> condition, string message)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.IsLessThanOrEqualTo(deadline, DateTime.UtcNow, message);
            await Task.Delay(50, TestContext.CancellationToken);
        }
    }

    /// <summary>读到对端关闭为止,返回读到的字节数。</summary>
    private static async Task<long> DrainAsync(Stream stream)
    {
        byte[] buffer = new byte[16 * 1024];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            total += read;
        }
        return total;
    }

    private static void RequireContainer()
    {
        try
        {
            using TcpClient tcp = new();
            if (!tcp.ConnectAsync(TestHost, TestPort).Wait(TimeSpan.FromSeconds(3)))
            {
                Assert.Inconclusive($"[SKIP] ssh-shells 靶机 {TestHost}:{TestPort} 不可达。" +
                                    "运行 'docker compose -f docker-compose.test.yml up -d --build ssh-shells'。");
            }
        }
        catch (Exception ex) when (ex is SocketException or AggregateException)
        {
            Assert.Inconclusive($"[SKIP] ssh-shells 靶机 {TestHost}:{TestPort} 不可达:{ex.Message}");
        }
    }
}
