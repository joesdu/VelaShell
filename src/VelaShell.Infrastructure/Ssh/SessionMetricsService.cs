using System.Collections.Concurrent;
using VelaShell.Core.Services;
using VelaShell.Core.Ssh;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>Runs the metrics probe over the session's existing SSH connection (§11) and turns
/// consecutive samples into instantaneous readings: CPU% from the /proc/stat jiffies delta
/// (the one-shot loadavg approximation lags by a minute) and network rates from the
/// /proc/net/dev byte-counter delta.</summary>
public sealed class SessionMetricsService : ISessionMetricsService
{
    private sealed record Sample(long CpuTotal, long CpuIdle, long NetRx, long NetTx, DateTime At,
        IReadOnlyList<CpuCoreCounter> Cores, IReadOnlyList<NetInterfaceCounter> Nics);

    private readonly ISshConnectionService _connectionService;
    private readonly ConcurrentDictionary<Guid, Sample> _lastSamples = new();

    public SessionMetricsService(ISshConnectionService connectionService)
    {
        _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
    }

    public async Task<SessionMetrics?> GetMetricsAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var client = _connectionService.GetClient(sessionId);
        if (client is null || !client.IsConnected)
        {
            _lastSamples.TryRemove(sessionId, out _);
            return null;
        }

        try
        {
            var output = await client.RunCommandAsync(SessionMetrics.MetricsCommand, cancellationToken)
                .ConfigureAwait(false);
            var metrics = SessionMetrics.Parse(output);
            if (metrics is not null)
                ApplyDeltas(sessionId, metrics);
            return metrics;
        }
        catch
        {
            // A failed probe (timeout, non-Linux host, dropped session) is "data unavailable".
            return null;
        }
    }

    /// <summary>Computes instantaneous CPU% / network rates against the previous sample of the
    /// same session, then stores this sample. First samples keep the loadavg CPU fallback and
    /// report no network rate.</summary>
    private void ApplyDeltas(Guid sessionId, SessionMetrics metrics)
    {
        var now = DateTime.UtcNow;
        _lastSamples.TryGetValue(sessionId, out var prev);

        if (prev is not null)
        {
            var seconds = (now - prev.At).TotalSeconds;

            if (metrics.HasCpuCounters && metrics.CpuTotalJiffies > prev.CpuTotal)
            {
                long deltaTotal = metrics.CpuTotalJiffies - prev.CpuTotal;
                long deltaIdle = metrics.CpuIdleJiffies - prev.CpuIdle;
                metrics.CpuPercent = Math.Clamp((deltaTotal - deltaIdle) * 100.0 / deltaTotal, 0, 100);
            }

            // 每核心占用:与上一采样按核名对齐做差分(状态栏 CPU 提示逐核显示)。
            if (metrics.CoreCounters.Count > 0 && prev.Cores.Count > 0)
            {
                var prevCores = prev.Cores.ToDictionary(c => c.Name);
                var percents = new List<double>(metrics.CoreCounters.Count);
                foreach (var core in metrics.CoreCounters)
                {
                    double percent = 0;
                    if (prevCores.TryGetValue(core.Name, out var p) && core.TotalJiffies > p.TotalJiffies)
                    {
                        long dt = core.TotalJiffies - p.TotalJiffies;
                        long di = core.IdleJiffies - p.IdleJiffies;
                        percent = Math.Clamp((dt - di) * 100.0 / dt, 0, 100);
                    }
                    percents.Add(percent);
                }
                metrics.CorePercents = percents;
            }

            if (metrics.HasNetCounters && seconds > 0.2)
            {
                // Counters can reset (interface bounce, reboot); clamp negatives to 0.
                metrics.NetRxBytesPerSec = Math.Max(0, (metrics.NetRxTotalBytes - prev.NetRx) / seconds);
                metrics.NetTxBytesPerSec = Math.Max(0, (metrics.NetTxTotalBytes - prev.NetTx) / seconds);
                metrics.HasNetRates = true;
            }

            // 每网卡速率:按接口名对齐做差分(状态栏网速提示逐网卡显示)。
            if (metrics.NicCounters.Count > 0 && prev.Nics.Count > 0 && seconds > 0.2)
            {
                var prevNics = prev.Nics.ToDictionary(n => n.Name);
                var rates = new List<NetInterfaceRate>(metrics.NicCounters.Count);
                foreach (var nic in metrics.NicCounters)
                {
                    if (prevNics.TryGetValue(nic.Name, out var p))
                        rates.Add(new NetInterfaceRate(nic.Name,
                            Math.Max(0, (nic.RxBytes - p.RxBytes) / seconds),
                            Math.Max(0, (nic.TxBytes - p.TxBytes) / seconds)));
                }
                metrics.NicRates = rates;
            }
        }

        if (metrics.HasCpuCounters || metrics.HasNetCounters)
        {
            _lastSamples[sessionId] = new Sample(
                metrics.CpuTotalJiffies, metrics.CpuIdleJiffies,
                metrics.NetRxTotalBytes, metrics.NetTxTotalBytes, now,
                metrics.CoreCounters, metrics.NicCounters);
        }
    }
}
