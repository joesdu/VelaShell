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
    private sealed record Sample(long CpuTotal, long CpuIdle, long NetRx, long NetTx, DateTime At);

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
            if (metrics.HasCpuCounters && metrics.CpuTotalJiffies > prev.CpuTotal)
            {
                long deltaTotal = metrics.CpuTotalJiffies - prev.CpuTotal;
                long deltaIdle = metrics.CpuIdleJiffies - prev.CpuIdle;
                metrics.CpuPercent = Math.Clamp((deltaTotal - deltaIdle) * 100.0 / deltaTotal, 0, 100);
            }

            if (metrics.HasNetCounters)
            {
                var seconds = (now - prev.At).TotalSeconds;
                if (seconds > 0.2)
                {
                    // Counters can reset (interface bounce, reboot); clamp negatives to 0.
                    metrics.NetRxBytesPerSec = Math.Max(0, (metrics.NetRxTotalBytes - prev.NetRx) / seconds);
                    metrics.NetTxBytesPerSec = Math.Max(0, (metrics.NetTxTotalBytes - prev.NetTx) / seconds);
                    metrics.HasNetRates = true;
                }
            }
        }

        if (metrics.HasCpuCounters || metrics.HasNetCounters)
        {
            _lastSamples[sessionId] = new Sample(
                metrics.CpuTotalJiffies, metrics.CpuIdleJiffies,
                metrics.NetRxTotalBytes, metrics.NetTxTotalBytes, now);
        }
    }
}
