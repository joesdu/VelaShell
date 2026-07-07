using System;
using System.Threading.Tasks;
using PulseTerm.Core.Services;
using ReactiveUI;

namespace PulseTerm.App.ViewModels;

/// <summary>
/// Backs the tab-hover resource panel (design EP3Gd, spec §11): a live snapshot of the
/// session host's CPU / RAM / Disk / system info, polled while the panel is visible.
/// </summary>
public class ResourceMonitorViewModel : ReactiveObject
{
    private readonly ISessionMetricsService _metricsService;
    private SessionMetrics? _metrics;
    private bool _isAvailable;

    public ResourceMonitorViewModel(ISessionMetricsService metricsService, Guid sessionId, string hostName)
    {
        _metricsService = metricsService ?? throw new ArgumentNullException(nameof(metricsService));
        SessionId = sessionId;
        HostName = hostName;
    }

    public Guid SessionId { get; }
    public string HostName { get; }

    /// <summary>False shows the "数据不可用" placeholder (disconnected / non-Linux host).</summary>
    public bool IsAvailable
    {
        get => _isAvailable;
        private set => this.RaiseAndSetIfChanged(ref _isAvailable, value);
    }

    public string CpuLabel => _metrics is { } m ? $"CPU ({m.CpuCores} cores)" : "CPU";
    public string CpuText => _metrics is { } m ? $"{m.CpuPercent:F0}%" : "--";
    public double CpuPercent => _metrics?.CpuPercent ?? 0;

    public string RamText => _metrics is { } m
        ? $"{FormatGb(m.MemUsedBytes)} / {FormatGb(m.MemTotalBytes)} GB" : "--";
    public double RamPercent => _metrics?.MemPercent ?? 0;

    public string DiskText => _metrics is { } m
        ? $"{FormatGb(m.DiskUsedBytes)} / {FormatGb(m.DiskTotalBytes)} GB ({m.DiskPercent:F0}%)" : "--";
    public double DiskPercent => _metrics?.DiskPercent ?? 0;

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
        var metrics = await _metricsService.GetMetricsAsync(SessionId);
        _metrics = metrics;
        IsAvailable = metrics is not null;
        this.RaisePropertyChanged(string.Empty); // refresh all computed properties
    }

    private static string FormatGb(long bytes) => (bytes / 1024.0 / 1024.0 / 1024.0).ToString("F1");
}
