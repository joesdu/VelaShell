using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Core.Tunnels;
using VelaShell.Infrastructure.Tunnels;

namespace VelaShell.Core.Tests.Tunnels;

[TestClass]
[TestCategory("Tunnel")]
public class TunnelServiceTests
{
    private readonly ISshClientWrapper _mockClientWrapper;
    private readonly ISshConnectionService _mockConnectionService;
    private readonly Guid _sessionId;

    public TunnelServiceTests()
    {
        _mockConnectionService = Substitute.For<ISshConnectionService>();
        _mockClientWrapper = Substitute.For<ISshClientWrapper>();
        _sessionId = Guid.NewGuid();
        var mockSession = new SshSession
        {
            SessionId = _sessionId,
            Status = SessionStatus.Connected,
            ConnectionInfo = new()
            {
                Host = "localhost",
                Port = 22,
                Username = "test",
                AuthMethod = AuthMethod.Password
            }
        };
        _mockConnectionService.GetSession(_sessionId).Returns(mockSession);
        _mockClientWrapper.IsConnected.Returns(true);
    }

    /// <summary>
    /// 构造被测服务。端口占用探测默认注入"全都空闲",否则用例里那些 5432 / 3306
    /// 就要看运行机器上恰好有没有数据库在监听 —— 测试的结论必须由代码决定,而不是由机器决定。
    /// </summary>
    private TunnelService CreateService(Func<string, uint, bool>? isLocalPortInUse = null) =>
        new(_mockConnectionService, _ => _mockClientWrapper, null, isLocalPortInUse ?? ((_, _) => false));

    [TestMethod]
    public async Task CreateLocalForwardAsync_CreatesActiveTunnel()
    {
        var config = new TunnelConfig
        {
            Type = TunnelType.LocalForward,
            Name = "DB Tunnel",
            LocalHost = "127.0.0.1",
            LocalPort = 5432,
            RemoteHost = "db.example.com",
            RemotePort = 5432
        };
        TunnelService service = CreateService();
        TunnelInfo tunnel = await service.CreateLocalForwardAsync(_sessionId, config);
        Assert.IsNotNull(tunnel);
        Assert.AreNotEqual(Guid.Empty, tunnel.Id);
        Assert.AreEqual(config, tunnel.Config);
        Assert.AreEqual(TunnelStatus.Active, tunnel.Status);
        Assert.AreEqual(_sessionId, tunnel.SessionId);
        Assert.IsLessThanOrEqualTo(TimeSpan.FromSeconds(1), (DateTime.UtcNow - tunnel.CreatedAt).Duration());
    }

    [TestMethod]
    public async Task CreateRemoteForwardAsync_CreatesActiveTunnel()
    {
        var config = new TunnelConfig
        {
            Type = TunnelType.RemoteForward,
            Name = "Web Server",
            LocalHost = "127.0.0.1",
            LocalPort = 8080,
            RemoteHost = "localhost",
            RemotePort = 8080
        };
        TunnelService service = CreateService();
        TunnelInfo tunnel = await service.CreateRemoteForwardAsync(_sessionId, config);
        Assert.IsNotNull(tunnel);
        Assert.AreNotEqual(Guid.Empty, tunnel.Id);
        Assert.AreEqual(config, tunnel.Config);
        Assert.AreEqual(TunnelStatus.Active, tunnel.Status);
        Assert.AreEqual(_sessionId, tunnel.SessionId);
    }

    [TestMethod]
    public async Task StopTunnelAsync_ChangesTunnelStatusToStopped()
    {
        var config = new TunnelConfig
        {
            Type = TunnelType.LocalForward,
            Name = "Test Tunnel",
            LocalHost = "127.0.0.1",
            LocalPort = 3306,
            RemoteHost = "mysql.example.com",
            RemotePort = 3306
        };
        TunnelService service = CreateService();
        TunnelInfo tunnel = await service.CreateLocalForwardAsync(_sessionId, config);
        await service.StopTunnelAsync(tunnel.Id);
        IReadOnlyList<TunnelInfo> activeTunnels = service.GetActiveTunnels(_sessionId);
        TunnelInfo? stoppedTunnel = activeTunnels.FirstOrDefault(t => t.Id == tunnel.Id);
        Assert.AreEqual(TunnelStatus.Stopped, stoppedTunnel?.Status);
    }

    [TestMethod]
    public async Task GetActiveTunnels_ReturnsOnlyActiveTunnels()
    {
        var config1 = new TunnelConfig
        {
            Type = TunnelType.LocalForward,
            Name = "Tunnel 1",
            LocalHost = "127.0.0.1",
            LocalPort = 5432,
            RemoteHost = "db1.example.com",
            RemotePort = 5432
        };
        var config2 = new TunnelConfig
        {
            Type = TunnelType.LocalForward,
            Name = "Tunnel 2",
            LocalHost = "127.0.0.1",
            LocalPort = 3306,
            RemoteHost = "db2.example.com",
            RemotePort = 3306
        };
        TunnelService service = CreateService();
        TunnelInfo tunnel1 = await service.CreateLocalForwardAsync(_sessionId, config1);
        TunnelInfo tunnel2 = await service.CreateLocalForwardAsync(_sessionId, config2);
        IReadOnlyList<TunnelInfo> activeTunnels = service.GetActiveTunnels(_sessionId);
        Assert.HasCount(2, activeTunnels);
        Assert.Contains(t => t.Id == tunnel1.Id, activeTunnels);
        Assert.Contains(t => t.Id == tunnel2.Id, activeTunnels);
    }

    [TestMethod]
    public async Task IndividualTunnelFailure_DoesNotAffectOtherTunnels()
    {
        var config1 = new TunnelConfig
        {
            Type = TunnelType.LocalForward,
            Name = "Tunnel 1",
            LocalHost = "127.0.0.1",
            LocalPort = 5432,
            RemoteHost = "db1.example.com",
            RemotePort = 5432
        };
        var config2 = new TunnelConfig
        {
            Type = TunnelType.LocalForward,
            Name = "Tunnel 2",
            LocalHost = "127.0.0.1",
            LocalPort = 3306,
            RemoteHost = "db2.example.com",
            RemotePort = 3306
        };
        TunnelService service = CreateService();
        TunnelInfo tunnel1 = await service.CreateLocalForwardAsync(_sessionId, config1);
        TunnelInfo tunnel2 = await service.CreateLocalForwardAsync(_sessionId, config2);
        await service.StopTunnelAsync(tunnel1.Id);
        IReadOnlyList<TunnelInfo> activeTunnels = service.GetActiveTunnels(_sessionId);
        TunnelInfo? stoppedTunnel = activeTunnels.FirstOrDefault(t => t.Id == tunnel1.Id);
        TunnelInfo? activeTunnel = activeTunnels.FirstOrDefault(t => t.Id == tunnel2.Id);
        Assert.AreEqual(TunnelStatus.Stopped, stoppedTunnel?.Status);
        Assert.AreEqual(TunnelStatus.Active, activeTunnel?.Status);
    }

    [TestMethod]
    public async Task TunnelConfig_StoredForReconnectRecreation()
    {
        var config = new TunnelConfig
        {
            Type = TunnelType.LocalForward,
            Name = "DB Tunnel",
            LocalHost = "127.0.0.1",
            LocalPort = 5432,
            RemoteHost = "db.example.com",
            RemotePort = 5432
        };
        TunnelService service = CreateService();
        TunnelInfo tunnel = await service.CreateLocalForwardAsync(_sessionId, config);
        Assert.IsNotNull(tunnel.Config);
        Assert.AreEqual(TunnelType.LocalForward, tunnel.Config.Type);
        Assert.AreEqual("DB Tunnel", tunnel.Config.Name);
        Assert.AreEqual("127.0.0.1", tunnel.Config.LocalHost);
        Assert.AreEqual(5432u, tunnel.Config.LocalPort);
        Assert.AreEqual("db.example.com", tunnel.Config.RemoteHost);
        Assert.AreEqual(5432u, tunnel.Config.RemotePort);
    }

    // ———— 端口冲突预检 ————

    /// <summary>本地端口已被占用时,在建立转发之前就报清楚,而不是让底层套接字抛错误码。</summary>
    [TestMethod]
    public async Task CreateLocalForwardAsync_ThrowsPortInUse_BeforeTouchingTheClient()
    {
        var config = new TunnelConfig
        {
            Type = TunnelType.LocalForward,
            Name = "Occupied",
            LocalHost = "127.0.0.1",
            LocalPort = 27017,
            RemoteHost = "127.0.0.1",
            RemotePort = 27017
        };
        TunnelService service = CreateService((host, port) => host == "127.0.0.1" && port == 27017);

        TunnelPortInUseException error = await Assert.ThrowsExactlyAsync<TunnelPortInUseException>(
            async () => await service.CreateLocalForwardAsync(_sessionId, config));

        Assert.AreEqual(27017u, error.Port);
        await _mockClientWrapper.DidNotReceive().StartPortForwardAsync(Arg.Any<PortForwardRequest>(), Arg.Any<CancellationToken>());
        Assert.IsEmpty(service.GetActiveTunnels(_sessionId), "预检拦下的隧道不该在列表里留下残骸。");
    }

    /// <summary>动态转发同样监听在本机,同样要预检。</summary>
    [TestMethod]
    public async Task CreateDynamicForwardAsync_ThrowsPortInUse()
    {
        var config = new TunnelConfig
        {
            Type = TunnelType.DynamicForward,
            Name = "SOCKS",
            LocalHost = "127.0.0.1",
            LocalPort = 1080
        };
        TunnelService service = CreateService((_, port) => port == 1080);

        await Assert.ThrowsExactlyAsync<TunnelPortInUseException>(
            async () => await service.CreateDynamicForwardAsync(_sessionId, config));
    }

    /// <summary>远程转发的监听在服务器上,本机端口占用与它无关,不该被预检拦下。</summary>
    [TestMethod]
    public async Task CreateRemoteForwardAsync_IgnoresLocalPortOccupancy()
    {
        var config = new TunnelConfig
        {
            Type = TunnelType.RemoteForward,
            Name = "Expose",
            LocalHost = "127.0.0.1",
            LocalPort = 8080,
            RemoteHost = "127.0.0.1",
            RemotePort = 8080
        };
        TunnelService service = CreateService((_, _) => true);

        TunnelInfo tunnel = await service.CreateRemoteForwardAsync(_sessionId, config);

        Assert.AreEqual(TunnelStatus.Active, tunnel.Status);
    }

    // ———— 流量统计 ————

    /// <summary>活动隧道的连接数与流量从底层句柄同步到 TunnelInfo,供界面读取。</summary>
    [TestMethod]
    public async Task RefreshStatistics_CopiesCountersFromHandle()
    {
        IPortForwardHandle handle = Substitute.For<IPortForwardHandle>();
        handle.BytesTransferred.Returns(4096L);
        handle.TotalConnections.Returns(3);
        handle.ActiveConnections.Returns(1);
        _mockClientWrapper.StartPortForwardAsync(Arg.Any<PortForwardRequest>(), Arg.Any<CancellationToken>()).Returns(handle);
        TunnelService service = CreateService();
        TunnelInfo tunnel = await service.CreateLocalForwardAsync(_sessionId, LocalConfig(5432));

        service.RefreshStatistics();

        Assert.AreEqual(4096L, tunnel.BytesTransferred);
        Assert.AreEqual(3, tunnel.TotalConnections);
        Assert.AreEqual(1, tunnel.ActiveConnections);
    }

    /// <summary>
    /// 停止隧道要先把最后一次读数取下来:句柄一释放就再也问不到,而界面在
    /// "已停止"状态下仍要显示这条隧道跑过的总量。
    /// </summary>
    [TestMethod]
    public async Task StopTunnelAsync_KeepsFinalCounters_AndClearsActive()
    {
        IPortForwardHandle handle = Substitute.For<IPortForwardHandle>();
        handle.BytesTransferred.Returns(8192L);
        handle.TotalConnections.Returns(5);
        handle.ActiveConnections.Returns(2);
        _mockClientWrapper.StartPortForwardAsync(Arg.Any<PortForwardRequest>(), Arg.Any<CancellationToken>()).Returns(handle);
        TunnelService service = CreateService();
        TunnelInfo tunnel = await service.CreateLocalForwardAsync(_sessionId, LocalConfig(5432));

        await service.StopTunnelAsync(tunnel.Id);

        Assert.AreEqual(8192L, tunnel.BytesTransferred);
        Assert.AreEqual(5, tunnel.TotalConnections);
        Assert.AreEqual(0, tunnel.ActiveConnections, "停掉之后不该还有在传的连接。");
    }

    /// <summary>实时速率与连接快照同计数一起从句柄同步过来;停掉之后清零 —— 停着的隧道没有速率,也没有在搬的连接。</summary>
    [TestMethod]
    public async Task RefreshStatistics_CopiesThroughputAndConnections_StopClearsThem()
    {
        IPortForwardHandle handle = Substitute.For<IPortForwardHandle>();
        handle.SentBytesPerSecond.Returns(1500d);
        handle.ReceivedBytesPerSecond.Returns(250_000d);
        TunnelConnectionInfo live = new(7, "127.0.0.1:53122", "db:5432", DateTime.UtcNow.AddSeconds(-42), 1024, 65536);
        handle.Connections.Returns([live]);
        _mockClientWrapper.StartPortForwardAsync(Arg.Any<PortForwardRequest>(), Arg.Any<CancellationToken>()).Returns(handle);
        TunnelService service = CreateService();
        TunnelInfo tunnel = await service.CreateLocalForwardAsync(_sessionId, LocalConfig(5432));

        service.RefreshStatistics();

        Assert.AreEqual(1500d, tunnel.SentBytesPerSecond);
        Assert.AreEqual(250_000d, tunnel.ReceivedBytesPerSecond);
        Assert.AreSequenceEqual([live], [.. tunnel.Connections]);

        await service.StopTunnelAsync(tunnel.Id);

        Assert.AreEqual(0d, tunnel.SentBytesPerSecond);
        Assert.AreEqual(0d, tunnel.ReceivedBytesPerSecond);
        Assert.IsEmpty(tunnel.Connections);
    }

    /// <summary>隧道配置里的限速一路带进转发请求(三种类型都带);不设就是不限。</summary>
    [TestMethod]
    [DataRow(TunnelType.LocalForward)]
    [DataRow(TunnelType.RemoteForward)]
    [DataRow(TunnelType.DynamicForward)]
    public async Task CreateForward_CarriesTheRateLimitIntoTheRequest(TunnelType type)
    {
        List<PortForwardRequest> requests = [];
        _mockClientWrapper.StartPortForwardAsync(Arg.Do<PortForwardRequest>(requests.Add), Arg.Any<CancellationToken>())
                          .Returns(Substitute.For<IPortForwardHandle>());
        TunnelService service = CreateService();
        TunnelConfig limited = new()
        {
            Type = type,
            Name = "limited",
            LocalHost = "127.0.0.1",
            LocalPort = 5432,
            RemoteHost = "127.0.0.1",
            RemotePort = 5432,
            MaxBytesPerSecond = 512 * 1024
        };
        TunnelConfig unlimited = new()
        {
            Type = type,
            Name = "unlimited",
            LocalHost = "127.0.0.1",
            LocalPort = 5433,
            RemoteHost = "127.0.0.1",
            RemotePort = 5433
        };

        foreach (TunnelConfig config in new[] { limited, unlimited })
        {
            _ = type switch
            {
                TunnelType.RemoteForward => await service.CreateRemoteForwardAsync(_sessionId, config),
                TunnelType.DynamicForward => await service.CreateDynamicForwardAsync(_sessionId, config),
                _ => await service.CreateLocalForwardAsync(_sessionId, config)
            };
        }

        Assert.HasCount(2, requests);
        Assert.AreEqual(512 * 1024L, requests[0].MaxBytesPerSecond);
        Assert.IsNull(requests[1].MaxBytesPerSecond);
    }

    /// <summary>
    /// 远程动态转发:服务器上的监听地址与端口取 RemoteHost / RemotePort,放行名单与限速一路带进请求;
    /// 不做本机端口预检(监听在服务器上)。
    /// </summary>
    [TestMethod]
    public async Task CreateRemoteDynamicForwardAsync_BuildsARemoteDynamicRequest()
    {
        PortForwardRequest? request = null;
        _mockClientWrapper.StartPortForwardAsync(Arg.Do<PortForwardRequest>(r => request = r), Arg.Any<CancellationToken>())
                          .Returns(Substitute.For<IPortForwardHandle>());
        TunnelService service = CreateService((_, _) => true);

        TunnelInfo tunnel = await service.CreateRemoteDynamicForwardAsync(_sessionId, new TunnelConfig
        {
            Type = TunnelType.RemoteDynamicForward,
            Name = "socks-back",
            LocalHost = "127.0.0.1",
            LocalPort = 0,
            RemoteHost = "127.0.0.1",
            RemotePort = 1080,
            PermitRemoteOpen = ["localhost:8080", "*.corp.example:443"],
            MaxBytesPerSecond = 4096
        });

        Assert.AreEqual(TunnelStatus.Active, tunnel.Status);
        Assert.IsNotNull(request);
        Assert.AreEqual(PortForwardKind.RemoteDynamic, request.Kind);
        Assert.AreEqual("127.0.0.1", request.BoundHost);
        Assert.AreEqual(1080u, request.BoundPort);
        Assert.IsNull(request.TargetHost);
        Assert.AreSequenceEqual(["localhost:8080", "*.corp.example:443"], [.. request.PermitRemoteOpen!]);
        Assert.AreEqual(4096L, request.MaxBytesPerSecond);
    }

    /// <summary>放行名单留空、或有写错的规则:不建,报一句说得清的话 —— 不替用户当成「全放」或「全拒」。</summary>
    [TestMethod]
    public async Task CreateRemoteDynamicForwardAsync_RejectsAMissingOrBadAllowList()
    {
        TunnelService service = CreateService();
        static TunnelConfig Config(IReadOnlyList<string>? permit) => new()
        {
            Type = TunnelType.RemoteDynamicForward,
            Name = "socks-back",
            LocalHost = "127.0.0.1",
            LocalPort = 0,
            RemoteHost = "127.0.0.1",
            RemotePort = 1080,
            PermitRemoteOpen = permit
        };

        ArgumentException missing = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => service.CreateRemoteDynamicForwardAsync(_sessionId, Config(null)));
        Assert.AreEqual(Strings.Get("TunnelSvc_PermitRemoteOpenRequired"), missing.Message);
        ArgumentException bad = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => service.CreateRemoteDynamicForwardAsync(_sessionId, Config(["localhost"])));
        Assert.AreEqual(Strings.Format("Tunnel_PermitRemoteOpenInvalid", "localhost"), bad.Message);
        await _mockClientWrapper.DidNotReceive().StartPortForwardAsync(Arg.Any<PortForwardRequest>(), Arg.Any<CancellationToken>());
    }

    // ———— Unix 域套接字 ————

    /// <summary>
    /// 本地转发两头各自可以是套接字:本机这头的路径进监听端、服务器那头的路径进目的端;
    /// 本机在套接字上监听时不做端口预检(端口那栏可能是用户没再管的旧值)。
    /// </summary>
    [TestMethod]
    public async Task CreateLocalForwardAsync_UnixSockets_GoIntoTheRequestAndSkipThePortCheck()
    {
        List<PortForwardRequest> requests = [];
        _mockClientWrapper.StartPortForwardAsync(Arg.Do<PortForwardRequest>(requests.Add), Arg.Any<CancellationToken>())
                          .Returns(Substitute.For<IPortForwardHandle>());
        TunnelService service = CreateService((_, _) => true);
        string listen = Path.Combine(Path.GetTempPath(), $"vela-test-{Guid.NewGuid():N}.sock");

        await service.CreateLocalForwardAsync(_sessionId, new TunnelConfig
        {
            Type = TunnelType.LocalForward,
            Name = "docker",
            LocalHost = "127.0.0.1",
            LocalPort = 2375,
            RemoteHost = "127.0.0.1",
            RemotePort = 2375,
            LocalSocketPath = listen,
            RemoteSocketPath = "/var/run/docker.sock"
        });

        PortForwardRequest request = requests.Single();
        Assert.AreEqual(PortForwardKind.Local, request.Kind);
        Assert.AreEqual(listen, request.BoundSocketPath);
        Assert.AreEqual("/var/run/docker.sock", request.TargetSocketPath);
    }

    /// <summary>本机套接字路径上已经有文件:不建、不删,报一句说清楚是哪个路径。</summary>
    [TestMethod]
    public async Task CreateLocalForwardAsync_ExistingSocketFile_IsReportedNotReplaced()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vela-test-{Guid.NewGuid():N}.sock");
        await File.WriteAllTextAsync(path, "someone else's");
        try
        {
            TunnelService service = CreateService();

            TunnelSocketPathInUseException ex = await Assert.ThrowsExactlyAsync<TunnelSocketPathInUseException>(
                () => service.CreateLocalForwardAsync(_sessionId, new TunnelConfig
                {
                    Type = TunnelType.LocalForward,
                    Name = "docker",
                    LocalHost = "127.0.0.1",
                    LocalPort = 2375,
                    RemoteHost = "127.0.0.1",
                    RemotePort = 2375,
                    LocalSocketPath = path
                }));

            Assert.AreEqual(Strings.Format("TunnelSvc_LocalSocketExists", path), ex.Message);
            Assert.AreEqual("someone else's", await File.ReadAllTextAsync(path), "不替用户删。");
            await _mockClientWrapper.DidNotReceive().StartPortForwardAsync(Arg.Any<PortForwardRequest>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// 远程转发的套接字两头各自可以给(SSH 库四种组合都有):服务器那头进监听端、本机那头进目的端,没给的那头照旧用主机与端口。
    /// 曾经只给一头的不建(TunnelSvc_RemoteSocketNeedsBothEnds)。
    /// </summary>
    [TestMethod]
    public async Task CreateRemoteForwardAsync_UnixSocketsOnEitherEnd()
    {
        List<PortForwardRequest> requests = [];
        _mockClientWrapper.StartPortForwardAsync(Arg.Do<PortForwardRequest>(requests.Add), Arg.Any<CancellationToken>())
                          .Returns(Substitute.For<IPortForwardHandle>());
        TunnelService service = CreateService();
        static TunnelConfig Config(string? local, string? remote) => new()
        {
            Type = TunnelType.RemoteForward,
            Name = "agent",
            LocalHost = "127.0.0.1",
            LocalPort = 8080,
            RemoteHost = "127.0.0.1",
            RemotePort = 8080,
            LocalSocketPath = local,
            RemoteSocketPath = remote
        };

        await service.CreateRemoteForwardAsync(_sessionId, Config(@"C:\Users\me\app.sock", "/tmp/app.sock"));
        await service.CreateRemoteForwardAsync(_sessionId, Config("/home/me/app.sock", null));
        await service.CreateRemoteForwardAsync(_sessionId, Config(null, "/tmp/app.sock"));

        Assert.HasCount(3, requests);
        Assert.AreEqual(("/tmp/app.sock", @"C:\Users\me\app.sock"), (requests[0].BoundSocketPath, requests[0].TargetSocketPath));

        // 服务器的端口回连到本机的套接字:监听端是服务器上的地址与端口。
        Assert.AreEqual((PortForwardKind.Remote, null, "/home/me/app.sock"), (requests[1].Kind, requests[1].BoundSocketPath, requests[1].TargetSocketPath));
        Assert.AreEqual(("127.0.0.1", 8080u), (requests[1].BoundHost, requests[1].BoundPort));

        // 服务器上的套接字回连到本机的端口:目的端是本机的主机与端口。
        Assert.AreEqual((PortForwardKind.Remote, "/tmp/app.sock", null), (requests[2].Kind, requests[2].BoundSocketPath, requests[2].TargetSocketPath));
        Assert.AreEqual(("127.0.0.1", 8080u), (requests[2].TargetHost, requests[2].TargetPort));
    }

    private static TunnelConfig LocalConfig(uint port) =>
        new()
        {
            Type = TunnelType.LocalForward,
            Name = "Metered",
            LocalHost = "127.0.0.1",
            LocalPort = port,
            RemoteHost = "127.0.0.1",
            RemotePort = port
        };
}
