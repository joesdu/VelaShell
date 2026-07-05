using System.Reactive.Linq;
using NSubstitute;
using PulseTerm.App.ViewModels;
using PulseTerm.Core.Models;
using PulseTerm.Core.Tunnels;

namespace PulseTerm.App.Tests.ViewModels;

[TestClass]
public class TunnelPanelViewModelTests
{
    private readonly ITunnelService _tunnelService;
    private readonly Guid _sessionId;
    private readonly TunnelPanelViewModel _vm;

    public TunnelPanelViewModelTests()
    {
        _tunnelService = Substitute.For<ITunnelService>();
        _sessionId = Guid.NewGuid();
        _vm = new TunnelPanelViewModel(_tunnelService, _sessionId);
    }

    private static TunnelInfo CreateTunnelInfo(
        TunnelType type = TunnelType.LocalForward,
        TunnelStatus status = TunnelStatus.Active,
        string name = "test-tunnel",
        string localHost = "localhost",
        uint localPort = 3306,
        string remoteHost = "db-server",
        uint remotePort = 3306,
        long bytesTransferred = 0)
    {
        return new TunnelInfo
        {
            Id = Guid.NewGuid(),
            Config = new TunnelConfig
            {
                Type = type,
                Name = name,
                LocalHost = localHost,
                LocalPort = localPort,
                RemoteHost = remoteHost,
                RemotePort = remotePort
            },
            Status = status,
            SessionId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            BytesTransferred = bytesTransferred
        };
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task CreateTunnel_WithValidForm_AddsTunnelToList()
    {
        var tunnelInfo = CreateTunnelInfo();
        _tunnelService.CreateLocalForwardAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(tunnelInfo));

        _vm.NewTunnelName = "test-tunnel";
        _vm.NewLocalHost = "localhost";
        _vm.NewLocalPort = 3306;
        _vm.NewRemoteHost = "db-server";
        _vm.NewRemotePort = 3306;
        _vm.NewTunnelType = TunnelType.LocalForward;

        await _vm.CreateTunnelCommand.Execute().FirstAsync();

        Assert.AreEqual(1, _vm.Tunnels.Count());
        Assert.AreEqual("test-tunnel", _vm.Tunnels[0].Name);
        Assert.AreEqual(3306u, _vm.Tunnels[0].LocalPort);
        Assert.AreEqual("db-server", _vm.Tunnels[0].RemoteHost);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    [DataRow(0, 3306, false)]
    [DataRow(3306, 0, false)]
    [DataRow(-1, 3306, false)]
    [DataRow(3306, -1, false)]
    [DataRow(65536, 3306, false)]
    [DataRow(3306, 65536, false)]
    [DataRow(3306, 3306, true)]
    public void CreateTunnel_ValidatesPortRange(int localPort, int remotePort, bool expectedValid)
    {
        _vm.NewTunnelName = "test";
        _vm.NewLocalHost = "localhost";
        _vm.NewLocalPort = localPort;
        _vm.NewRemoteHost = "remote";
        _vm.NewRemotePort = remotePort;

        Assert.AreEqual(expectedValid, _vm.IsFormValid);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task StopTunnel_ChangesTunnelStatusToStopped()
    {
        var tunnelInfo = CreateTunnelInfo(status: TunnelStatus.Active);
        _tunnelService.CreateLocalForwardAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(tunnelInfo));

        _vm.NewTunnelName = "test";
        _vm.NewLocalHost = "localhost";
        _vm.NewLocalPort = 3306;
        _vm.NewRemoteHost = "db-server";
        _vm.NewRemotePort = 3306;

        await _vm.CreateTunnelCommand.Execute().FirstAsync();

        Assert.AreEqual(TunnelStatus.Active, _vm.Tunnels[0].Status);

        await _vm.StopTunnelCommand.Execute(tunnelInfo.Id).FirstAsync();

        Assert.AreEqual(TunnelStatus.Stopped, _vm.Tunnels[0].Status);
        await _tunnelService.Received(1).StopTunnelAsync(tunnelInfo.Id, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task DeleteTunnel_RemovesTunnelFromList()
    {
        var tunnelInfo = CreateTunnelInfo(status: TunnelStatus.Active);
        _tunnelService.CreateLocalForwardAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(tunnelInfo));

        _vm.NewTunnelName = "test";
        _vm.NewLocalHost = "localhost";
        _vm.NewLocalPort = 3306;
        _vm.NewRemoteHost = "db-server";
        _vm.NewRemotePort = 3306;

        await _vm.CreateTunnelCommand.Execute().FirstAsync();
        Assert.AreEqual(1, _vm.Tunnels.Count());

        await _vm.DeleteTunnelCommand.Execute(tunnelInfo.Id).FirstAsync();

        Assert.AreEqual(0, _vm.Tunnels.Count());
        await _tunnelService.Received(1).StopTunnelAsync(tunnelInfo.Id, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    [DataRow("", "localhost", 3306, "remote", 3306, false)]
    [DataRow("test", "", 3306, "remote", 3306, false)]
    [DataRow("test", "localhost", 0, "remote", 3306, false)]
    [DataRow("test", "localhost", 3306, "", 3306, false)]
    [DataRow("test", "localhost", 3306, "remote", 0, false)]
    [DataRow("test", "localhost", 3306, "remote", 3306, true)]
    public void PortValidation_RequiredFieldsMustBeNonEmptyNonZero(
        string name, string localHost, int localPort, string remoteHost, int remotePort, bool expectedValid)
    {
        _vm.NewTunnelName = name;
        _vm.NewLocalHost = localHost;
        _vm.NewLocalPort = localPort;
        _vm.NewRemoteHost = remoteHost;
        _vm.NewRemotePort = remotePort;

        Assert.AreEqual(expectedValid, _vm.IsFormValid);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task CreateTunnel_RemoteForward_UsesCorrectServiceMethod()
    {
        var tunnelInfo = CreateTunnelInfo(type: TunnelType.RemoteForward);
        _tunnelService.CreateRemoteForwardAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(tunnelInfo));

        _vm.NewTunnelName = "remote-tunnel";
        _vm.NewLocalHost = "localhost";
        _vm.NewLocalPort = 8080;
        _vm.NewRemoteHost = "web-server";
        _vm.NewRemotePort = 80;
        _vm.NewTunnelType = TunnelType.RemoteForward;

        await _vm.CreateTunnelCommand.Execute().FirstAsync();

        await _tunnelService.Received(1).CreateRemoteForwardAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>());
        await _tunnelService.DidNotReceive().CreateLocalForwardAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>());
        Assert.AreEqual(1, _vm.Tunnels.Count());
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public void TunnelItemViewModel_DisplayFormat_IsCorrect()
    {
        var tunnelInfo = CreateTunnelInfo(
            localHost: "localhost",
            localPort: 3306,
            remoteHost: "db-server",
            remotePort: 3306);

        var itemVm = new TunnelItemViewModel(tunnelInfo);

        Assert.AreEqual("localhost:3306 → db-server:3306", itemVm.DisplayRoute);
        Assert.AreEqual("L", itemVm.TypeBadge);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    [DataRow(0, "0 B")]
    [DataRow(1024, "1.0 KB")]
    [DataRow(1048576, "1.0 MB")]
    [DataRow(1073741824, "1.0 GB")]
    public void TunnelItemViewModel_BytesTransferred_FormatsCorrectly(long bytes, string expected)
    {
        Assert.AreEqual(expected, TunnelItemViewModel.FormatBytes(bytes));
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task CreateTunnel_ResetsFormAfterSuccess()
    {
        var tunnelInfo = CreateTunnelInfo();
        _tunnelService.CreateLocalForwardAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(tunnelInfo));

        _vm.NewTunnelName = "test";
        _vm.NewLocalHost = "127.0.0.1";
        _vm.NewLocalPort = 8080;
        _vm.NewRemoteHost = "server";
        _vm.NewRemotePort = 80;

        await _vm.CreateTunnelCommand.Execute().FirstAsync();

        Assert.AreEqual(string.Empty, _vm.NewTunnelName);
        Assert.AreEqual("localhost", _vm.NewLocalHost);
        Assert.AreEqual(0, _vm.NewLocalPort);
        Assert.AreEqual(string.Empty, _vm.NewRemoteHost);
        Assert.AreEqual(0, _vm.NewRemotePort);
        Assert.AreEqual(TunnelType.LocalForward, _vm.NewTunnelType);
    }
}
