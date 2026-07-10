using ReactiveUI;
using VelaShell.Core.Services;

namespace VelaShell.ViewModels;

/// <summary>One disk row of the resource panel(多磁盘主机逐盘显示,用户反馈)。</summary>
public sealed class DiskRowViewModel(string label, string text, double percent)
{
    public string Label { get; } = label;

    public string Text { get; } = text;

    public double Percent { get; } = percent;

    public bool Warn => Percent is > 70 and <= 90;

    public bool Crit => Percent > 90;
}

/// <summary>
/// Backs the tab-hover resource panel (design EP3Gd, spec §11): a live snapshot of the
/// session host's CPU / RAM / Disk / system info, polled while the panel is visible.
/// </summary>
public class ResourceMonitorViewModel(ISessionMetricsService metricsService, Guid sessionId, string hostName) : ReactiveObject
{
    private readonly ISessionMetricsService _metricsService = metricsService ?? throw new ArgumentNullException(nameof(metricsService));
    private SessionMetrics? _metrics;

    public Guid SessionId { get; } = sessionId;

    public string HostName { get; } = hostName;

    /// <summary>False shows the "数据不可用" placeholder (disconnected / non-Linux host).</summary>
    public bool IsAvailable
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string CpuLabel => _metrics is { } m ? $"CPU ({m.CpuCores} cores)" : "CPU";

    public string CpuText => _metrics is { } m ? $"{m.CpuPercent:F0}%" : "--";

    public double CpuPercent => _metrics?.CpuPercent ?? 0;

    public string RamText => _metrics is { } m
                                 ? $"{FormatGb(m.MemUsedBytes)} / {FormatGb(m.MemTotalBytes)} GB"
                                 : "--";

    public double RamPercent => _metrics?.MemPercent ?? 0;

    public string DiskText => _metrics is { } m
                                  ? $"{FormatGb(m.DiskUsedBytes)} / {FormatGb(m.DiskTotalBytes)} GB ({m.DiskPercent:F0}%)"
                                  : "--";

    public double DiskPercent => _metrics?.DiskPercent ?? 0;

    /// <summary>
    /// 逐盘行:探针拿到完整磁盘列表时一盘一行(按挂载点标注);
    /// 老主机/BusyBox 拿不到列表时退回原来的根分区单行。
    /// </summary>
    public IReadOnlyList<DiskRowViewModel> Disks
    {
        get
        {
            if (_metrics is not { } m)
            {
                return [];
            }
            if (m.Disks.Count > 0)
            {
                return m.Disks.Select(d => new DiskRowViewModel($"Disk {d.MountPoint}",
                    $"{FormatGb(d.UsedBytes)} / {FormatGb(d.TotalBytes)} GB ({d.Percent:F0}%)",
                    d.Percent)).ToList();
            }
            return [new("Disk", DiskText, DiskPercent)];
        }
    }

    public string OsVersion => _metrics?.OsVersion is { Length: > 0 } os ? os : "--";

    public string Kernel => _metrics?.Kernel is { Length: > 0 } k ? $"Linux {k}" : "--";

    // Threshold coloring per §11: normal / >70% warning / >90% critical.
    public bool CpuWarn => CpuPercent is > 70 and <= 90;

    public bool CpuCrit => CpuPercent > 90;

    public bool RamWarn => RamPercent is > 70 and <= 90;

    public bool RamCrit => RamPercent > 90;

    public bool DiskWarn => DiskPercent is > 70 and <= 90;

    public bool DiskCrit => DiskPercent > 90;

    public async Task RefreshAsync()
    {
        SessionMetrics? metrics = await _metricsService.GetMetricsAsync(SessionId);
        _metrics = metrics;
        IsAvailable = metrics is not null;
        this.RaisePropertyChanged(string.Empty); // refresh all computed properties
    }

    private static string FormatGb(long bytes) => (bytes / 1024.0 / 1024.0 / 1024.0).ToString("F1");
}
