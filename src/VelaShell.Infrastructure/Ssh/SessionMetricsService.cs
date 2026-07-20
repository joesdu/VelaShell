using System.Collections.Concurrent;
using VelaShell.Core.Services;
using VelaShell.Core.Ssh;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 在会话现有的 SSH 连接上运行指标探测(§11),并将连续采样转换为瞬时读数:CPU% 取自
/// /proc/stat 的 jiffies 增量(一次性的 loadavg 近似会滞后约一分钟),网速取自
/// /proc/net/dev 的字节计数器增量。
/// </summary>
public sealed class SessionMetricsService(ISshConnectionService connectionService) : ISessionMetricsService
{
    private readonly ISshConnectionService _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
    private readonly ConcurrentDictionary<Guid, Sample> _lastSamples = new();

    /// <summary>
    /// 采集指定会话的一次实时指标:在其现有 SSH 连接上跑探测命令,解析后与上一采样做差分
    /// 得到瞬时 CPU%/网速。连接不存在或已断开、以及探测失败(超时、非 Linux 主机)时返回 <c>null</c>。
    /// </summary>
    public async Task<SessionMetrics?> GetMetricsAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        ISshClientWrapper? client = _connectionService.GetClient(sessionId);
        if (client is null || !client.IsConnected)
        {
            _lastSamples.TryRemove(sessionId, out _);
            return null;
        }
        try
        {
            string output = await client.RunCommandAsync(SessionMetrics.MetricsCommand, cancellationToken).ConfigureAwait(false);
            var metrics = SessionMetrics.Parse(output);
            if (metrics is not null)
            {
                ApplyDeltas(sessionId, metrics);
            }
            return metrics;
        }
        catch
        {
            // 探测失败(超时、非 Linux 主机、会话断开)即为"数据不可用"。
            return null;
        }
    }

    /// <summary>
    /// 针对同一会话的上一采样计算瞬时 CPU%/网速,然后保存本次采样。首个采样保留 loadavg 的
    /// CPU 兜底值,且不报告网速。
    /// </summary>
    private void ApplyDeltas(Guid sessionId, SessionMetrics metrics)
    {
        DateTime now = DateTime.UtcNow;
        _lastSamples.TryGetValue(sessionId, out Sample? prev);
        if (prev is not null)
        {
            double seconds = (now - prev.At).TotalSeconds;
            if (metrics.HasCpuCounters && metrics.CpuTotalJiffies > prev.CpuTotal)
            {
                long deltaTotal = metrics.CpuTotalJiffies - prev.CpuTotal;
                long deltaIdle = metrics.CpuIdleJiffies - prev.CpuIdle;
                metrics.CpuPercent = Math.Clamp(((deltaTotal - deltaIdle) * 100.0) / deltaTotal, 0, 100);
            }

            // 每核心占用:与上一采样按核名对齐做差分(状态栏 CPU 提示逐核显示)。
            if (metrics.CoreCounters.Count > 0 && prev.Cores.Count > 0)
            {
                var prevCores = prev.Cores.ToDictionary(c => c.Name);
                var percents = new List<double>(metrics.CoreCounters.Count);
                foreach (CpuCoreCounter core in metrics.CoreCounters)
                {
                    double percent = 0;
                    if (prevCores.TryGetValue(core.Name, out CpuCoreCounter? p) && core.TotalJiffies > p.TotalJiffies)
                    {
                        long dt = core.TotalJiffies - p.TotalJiffies;
                        long di = core.IdleJiffies - p.IdleJiffies;
                        percent = Math.Clamp(((dt - di) * 100.0) / dt, 0, 100);
                    }
                    percents.Add(percent);
                }
                metrics.CorePercents = percents;
            }
            if (metrics.HasNetCounters && seconds > 0.2)
            {
                // 计数器可能复位(网卡抖动、重启);将负值钳制为 0。
                metrics.NetRxBytesPerSec = Math.Max(0, (metrics.NetRxTotalBytes - prev.NetRx) / seconds);
                metrics.NetTxBytesPerSec = Math.Max(0, (metrics.NetTxTotalBytes - prev.NetTx) / seconds);
                metrics.HasNetRates = true;
            }

            // 每网卡速率:按接口名对齐做差分(状态栏网速提示逐网卡显示)。
            if (metrics.NicCounters.Count > 0 && prev.Nics.Count > 0 && seconds > 0.2)
            {
                var prevNics = prev.Nics.ToDictionary(n => n.Name);
                var rates = new List<NetInterfaceRate>(metrics.NicCounters.Count);
                foreach (NetInterfaceCounter nic in metrics.NicCounters)
                {
                    if (prevNics.TryGetValue(nic.Name, out NetInterfaceCounter? p))
                    {
                        rates.Add(new(nic.Name,
                            Math.Max(0, (nic.RxBytes - p.RxBytes) / seconds),
                            Math.Max(0, (nic.TxBytes - p.TxBytes) / seconds)));
                    }
                }
                metrics.NicRates = rates;
            }
        }
        if (metrics.HasCpuCounters || metrics.HasNetCounters)
        {
            _lastSamples[sessionId] = new(metrics.CpuTotalJiffies, metrics.CpuIdleJiffies,
                metrics.NetRxTotalBytes, metrics.NetTxTotalBytes, now,
                metrics.CoreCounters, metrics.NicCounters);
        }
    }

    private sealed record Sample(
        long CpuTotal,
        long CpuIdle,
        long NetRx,
        long NetTx,
        DateTime At,
        IReadOnlyList<CpuCoreCounter> Cores,
        IReadOnlyList<NetInterfaceCounter> Nics);
}
