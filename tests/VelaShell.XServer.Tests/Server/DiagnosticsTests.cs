using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>可观测性(xs_plan F27):计量仪表与看门狗。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class DiagnosticsTests
{
    /// <summary>订阅 <see cref="XServerMetrics.MeterName" />,把每次记录收下来:(仪表名, 值, 第一个标签的值)。</summary>
    private static MeterListener Listen(ConcurrentQueue<(string Name, double Value, string? Tag)> seen)
    {
        MeterListener listener = new()
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == XServerMetrics.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            seen.Enqueue((instrument.Name, value, tags.Length > 0 ? tags[0].Value?.ToString() : null)));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            seen.Enqueue((instrument.Name, value, tags.Length > 0 ? tags[0].Value?.ToString() : null)));
        listener.Start();
        return listener;
    }

    [TestMethod]
    public async Task 计量仪表记下被拒的连接_连着的客户端_协议错误_每项工作的耗时()
    {
        ConcurrentQueue<(string Name, double Value, string? Tag)> seen = new();
        using MeterListener listener = Listen(seen);
        byte[] cookie = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
        await using X11Server server = new(new X11ServerOptions { AuthorizationCookie = cookie });

        await using (XTestClient refused = await XTestClient.ConnectAsync(server, isLocal: false))
        {
            Assert.AreEqual(0, refused.SetupReply[0], "没带 cookie:被拒");
        }
        await using XTestClient c = await XTestClient.ConnectAsync(server, authName: "MIT-MAGIC-COOKIE-1", authData: cookie);
        await c.SendAsync(4, 0, b => b.U32(0x12345));   // DestroyWindow 一个不存在的窗口:BadWindow
        Assert.AreEqual(3, (await c.NextAsync(m => m.IsError)).Detail);
        await c.SyncAsync();

        Assert.IsTrue(seen.Contains(("velashell.xserver.connections.refused", 1.0, "authorization")), "被拒的连接");
        Assert.IsTrue(seen.Contains(("velashell.xserver.clients.active", 1.0, null)), "连着的客户端");
        Assert.IsTrue(seen.Contains(("velashell.xserver.protocol.errors", 1.0, "Window")), "协议错误");
        Assert.IsTrue(seen.Any(m => m.Name == "velashell.xserver.work.duration" && m.Value >= 0), "每项工作的耗时");
    }

    /// <summary>
    /// 看门狗:一项工作卡住了(这里是排进执行线程的一项内部工作睡 2.5 秒),不等它做完就记一行点名,同一项只记一次,并计入仪表。
    /// 原先只有做完之后才记「慢」,真卡死时宿主日志里什么也没有。
    /// </summary>
    [TestMethod]
    public async Task 看门狗不等卡住的工作做完就点名_同一项只记一次()
    {
        ConcurrentQueue<(string Name, double Value, string? Tag)> seen = new();
        using MeterListener listener = Listen(seen);
        ConcurrentQueue<string> log = new();
        await using X11Server server = new(new X11ServerOptions { Log = log.Enqueue });
        server.WatchdogThreshold = TimeSpan.FromMilliseconds(300);
        await using XTestClient c = await XTestClient.ConnectAsync(server);

        server.Post(null, () => Thread.Sleep(2500));
        DateTime deadline = DateTime.UtcNow.AddSeconds(2.2);
        while (!log.Any(line => line.StartsWith("watchdog:", StringComparison.Ordinal)) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
        Assert.IsTrue(log.Any(line => line.StartsWith("watchdog: host or timer", StringComparison.Ordinal)), "还在卡着的时候就记了:" + string.Join(" | ", log));
        await c.SyncAsync();   // 等它做完
        Assert.AreEqual(1, log.Count(line => line.StartsWith("watchdog:", StringComparison.Ordinal)), "同一项只记一次");
        Assert.IsTrue(seen.Contains(("velashell.xserver.work.stalled", 1.0, null)), "计入仪表");
    }
}
