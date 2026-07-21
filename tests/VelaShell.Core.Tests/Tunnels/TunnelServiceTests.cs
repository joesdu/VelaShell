using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
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
        var service = new TunnelService(_mockConnectionService, sessionId => _mockClientWrapper);
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
        var service = new TunnelService(_mockConnectionService, sessionId => _mockClientWrapper);
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
        var service = new TunnelService(_mockConnectionService, sessionId => _mockClientWrapper);
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
        var service = new TunnelService(_mockConnectionService, sessionId => _mockClientWrapper);
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
        var service = new TunnelService(_mockConnectionService, sessionId => _mockClientWrapper);
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
        var service = new TunnelService(_mockConnectionService, sessionId => _mockClientWrapper);
        TunnelInfo tunnel = await service.CreateLocalForwardAsync(_sessionId, config);
        Assert.IsNotNull(tunnel.Config);
        Assert.AreEqual(TunnelType.LocalForward, tunnel.Config.Type);
        Assert.AreEqual("DB Tunnel", tunnel.Config.Name);
        Assert.AreEqual("127.0.0.1", tunnel.Config.LocalHost);
        Assert.AreEqual(5432u, tunnel.Config.LocalPort);
        Assert.AreEqual("db.example.com", tunnel.Config.RemoteHost);
        Assert.AreEqual(5432u, tunnel.Config.RemotePort);
    }
}
