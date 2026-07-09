using NSubstitute;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;

namespace VelaShell.Infrastructure.Tests;

[TestClass]
[TestCategory("Metrics")]
public class SessionMetricsServiceTests
{
    private static string Probe(long cpuBusy, long cpuIdle, long rx, long tx) =>
        "__P__\n4\n" +
        "__L__\n0.5 0.4 0.3 1/100 200\n" +
        "__M__\n1000 500\n" +
        "__D__\n2000 1000\n" +
        "__O__\nDebian\n" +
        "__K__\n6.1\n" +
        $"__S__\ncpu  {cpuBusy} 0 0 {cpuIdle} 0 0 0\n" +
        $"__N__\n{rx} {tx}\n";

    [TestMethod]
    public async Task SecondSample_ComputesInstantCpuAndNetRates()
    {
        var sessionId = Guid.NewGuid();
        var client = Substitute.For<ISshClientWrapper>();
        client.IsConnected.Returns(true);
        var connections = Substitute.For<ISshConnectionService>();
        connections.GetClient(sessionId).Returns(client);
        var service = new SessionMetricsService(connections);

        // Sample 1: busy=100 idle=900; rx=1_000_000 tx=500_000.
        client.RunCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Probe(100, 900, 1_000_000, 500_000));
        var first = await service.GetMetricsAsync(sessionId);

        Assert.IsNotNull(first);
        Assert.IsFalse(first.HasNetRates);           // no previous sample yet
        Assert.AreEqual(12.5, first.CpuPercent, 0.1); // loadavg fallback: 0.5 / 4 cores

        // Sample 2 (+~0.5s wall time): busy +300, idle +700 → 30% instantaneous;
        // rx +2 MB, tx +0.5 MB over the elapsed interval.
        await Task.Delay(500);
        client.RunCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Probe(400, 1600, 3_097_152, 1_024_288));
        var second = await service.GetMetricsAsync(sessionId);

        Assert.IsNotNull(second);
        Assert.AreEqual(30.0, second.CpuPercent, 0.1); // (dTotal-dIdle)/dTotal = 300/1000
        Assert.IsTrue(second.HasNetRates);
        Assert.IsTrue(second.NetRxBytesPerSec > 0, "rx rate should be positive");
        Assert.IsTrue(second.NetTxBytesPerSec > 0, "tx rate should be positive");
        Assert.IsTrue(second.NetRxBytesPerSec > second.NetTxBytesPerSec,
            "rx moved 4x the bytes of tx, so its rate must dominate");
    }

    [TestMethod]
    public async Task CounterReset_ClampsRatesToZeroInsteadOfNegative()
    {
        var sessionId = Guid.NewGuid();
        var client = Substitute.For<ISshClientWrapper>();
        client.IsConnected.Returns(true);
        var connections = Substitute.For<ISshConnectionService>();
        connections.GetClient(sessionId).Returns(client);
        var service = new SessionMetricsService(connections);

        client.RunCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Probe(100, 900, 5_000_000, 5_000_000));
        await service.GetMetricsAsync(sessionId);

        await Task.Delay(300);
        // Interface bounced: counters restarted from near zero.
        client.RunCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Probe(200, 1800, 1_000, 1_000));
        var second = await service.GetMetricsAsync(sessionId);

        Assert.IsNotNull(second);
        Assert.AreEqual(0, second.NetRxBytesPerSec);
        Assert.AreEqual(0, second.NetTxBytesPerSec);
    }

    [TestMethod]
    public async Task DisconnectedSession_ReturnsNullAndForgetsHistory()
    {
        var sessionId = Guid.NewGuid();
        var client = Substitute.For<ISshClientWrapper>();
        client.IsConnected.Returns(true);
        var connections = Substitute.For<ISshConnectionService>();
        connections.GetClient(sessionId).Returns(client);
        var service = new SessionMetricsService(connections);

        client.RunCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Probe(100, 900, 1000, 1000));
        await service.GetMetricsAsync(sessionId);

        client.IsConnected.Returns(false);
        Assert.IsNull(await service.GetMetricsAsync(sessionId));

        // Reconnected: history was dropped, so the next sample is a fresh "first" one.
        client.IsConnected.Returns(true);
        client.RunCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Probe(500, 1500, 9000, 9000));
        var afterReconnect = await service.GetMetricsAsync(sessionId);

        Assert.IsNotNull(afterReconnect);
        Assert.IsFalse(afterReconnect.HasNetRates);
    }
}
