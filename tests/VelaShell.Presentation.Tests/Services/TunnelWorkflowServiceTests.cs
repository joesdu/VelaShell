using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Tunnels;
using VelaShell.Presentation.Services;

namespace VelaShell.Presentation.Tests.Services;

[TestClass]
public sealed class TunnelWorkflowServiceTests
{
    [TestMethod]
    public async Task CreateTunnelAsync_LocalForward_UsesLocalForwardPath()
    {
        ITunnelService tunnelService = Substitute.For<ITunnelService>();
        var workflow = new TunnelWorkflowService(tunnelService);
        var sessionId = Guid.NewGuid();
        TunnelConfig config = CreateConfig(TunnelType.LocalForward);
        TunnelInfo result = CreateInfo(sessionId, config);

        tunnelService.CreateLocalForwardAsync(sessionId, config, Arg.Any<CancellationToken>())
            .Returns(result);

        TunnelInfo created = await workflow.CreateTunnelAsync(sessionId, config);

        Assert.AreSame(result, created);
        await tunnelService.Received(1).CreateLocalForwardAsync(sessionId, config, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task StopTunnelAsync_ForwardsCall()
    {
        ITunnelService tunnelService = Substitute.For<ITunnelService>();
        var workflow = new TunnelWorkflowService(tunnelService);
        var tunnelId = Guid.NewGuid();

        await workflow.StopTunnelAsync(tunnelId);

        await tunnelService.Received(1).StopTunnelAsync(tunnelId, Arg.Any<CancellationToken>());
    }

    private static TunnelConfig CreateConfig(TunnelType tunnelType) => new()
    {
        Type = tunnelType,
        Name = "db",
        LocalHost = "127.0.0.1",
        LocalPort = 5432,
        RemoteHost = "db.internal",
        RemotePort = 5432
    };

    private static TunnelInfo CreateInfo(Guid sessionId, TunnelConfig config) => new()
    {
        Id = Guid.NewGuid(),
        Config = config,
        Status = TunnelStatus.Active,
        SessionId = sessionId,
        CreatedAt = DateTime.UtcNow,
        BytesTransferred = 0
    };
}
