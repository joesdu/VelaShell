using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using ReactiveUI;
using VelaShell.Core.Resources;

namespace VelaShell.Presentation.ViewModels;

public sealed class StatusBarViewModel(IScheduler scheduler) : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = [];

    private DateTimeOffset _uptimeStart;

    private IDisposable? _uptimeSubscription;

    public StatusBarViewModel()
        : this(DefaultScheduler.Instance) { }

    // The metric segments are always visible; before the first sample (or without a
    // connected session) they show idle placeholders (用户要求).

    public string StatusText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = Strings.Ready;

    public string ConnectionInfo
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    public string Status
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            IsConnected = value == Strings.Connected;
        }
    } = Strings.Disconnected;

    public string Latency
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    public string TerminalType
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "xterm-256color";

    public string WindowSize
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "80×24";

    public string Encoding
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "UTF-8";

    public string Uptime
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    public bool IsConnected
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string CpuUsage
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "--";

    public string MemUsage
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "--";

    /// <summary>悬停提示:CPU 总占用 + 每核心占用(用户反馈,由主 VM 按采样填充)。</summary>
    public string CpuTooltip
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "CPU";

    /// <summary>悬停提示:内存/交换分区的已用与总量。</summary>
    public string MemTooltip
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "内存";

    /// <summary>悬停提示:每个磁盘(挂载点)的用量。</summary>
    public string DiskTooltip
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "磁盘";

    /// <summary>悬停提示:每个网卡的上下行速率。</summary>
    public string NetTooltip
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "网速";

    /// <summary>Swap usage percent; "--" when the host has no swap or no session is live.</summary>
    public string SwapUsage
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "--";

    /// <summary>Root filesystem usage percent; "--" without a live session.</summary>
    public string DiskUsage
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "--";

    /// <summary>The dominant direction's rate, e.g. "4.2 MB/s" (Android-style readout).</summary>
    public string NetSpeed
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "0 B/s";

    /// <summary>
    /// True while the server is actually uploading — lights the ↑ half of the
    /// arrow-up-down glyph in the accent color.
    /// </summary>
    public bool IsNetUpActive
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>True while the server is actually downloading — lights the ↓ half.</summary>
    public bool IsNetDownActive
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public void Dispose()
    {
        _disposables.Dispose();
        _uptimeSubscription = null;
    }

    /// <summary>
    /// Feeds one network sample (bytes/second per direction). Each arrow half lights
    /// up for its own direction; the readout shows the dominant direction's rate.
    /// </summary>
    public void UpdateNetwork(double rxBytesPerSec, double txBytesPerSec, bool hasRates)
    {
        if (!hasRates)
        {
            NetSpeed = "0 B/s";
            IsNetUpActive = false;
            IsNetDownActive = false;
            return;
        }

        // Keepalives/echo traffic hovers under this; don't light the arrows for noise.
        const double activeThreshold = 512;
        IsNetUpActive = txBytesPerSec >= activeThreshold;
        IsNetDownActive = rxBytesPerSec >= activeThreshold;
        NetSpeed = FormatRate(Math.Max(rxBytesPerSec, txBytesPerSec));
    }

    /// <summary>
    /// Resets the metric segments to their idle placeholders (no connected session).
    /// The segments stay visible so the bar layout never jumps.
    /// </summary>
    public void ClearSessionMetrics()
    {
        CpuUsage = "--";
        MemUsage = "--";
        SwapUsage = "--";
        DiskUsage = "--";
        NetSpeed = "0 B/s";
        IsNetUpActive = false;
        IsNetDownActive = false;
        CpuTooltip = "CPU";
        MemTooltip = "内存";
        DiskTooltip = "磁盘";
        NetTooltip = "网速";
    }

    public static string FormatRate(double bytesPerSec)
    {
        const double kb = 1024, mb = kb * 1024, gb = mb * 1024;
        return bytesPerSec switch
        {
            >= gb => $"{bytesPerSec / gb:F1} GB/s",
            >= mb => $"{bytesPerSec / mb:F1} MB/s",
            >= kb => $"{bytesPerSec / kb:F1} KB/s",
            _ => $"{bytesPerSec:F0} B/s"
        };
    }

    public void StartUptimeTimer()
    {
        StopUptimeTimer();
        _uptimeStart = scheduler.Now;
        _uptimeSubscription = Observable
                              .Interval(TimeSpan.FromSeconds(1), scheduler)
                              .Subscribe(_ =>
                              {
                                  TimeSpan elapsed = scheduler.Now - _uptimeStart;
                                  Uptime = elapsed.ToString(@"hh\:mm\:ss");
                              });
        _disposables.Add(_uptimeSubscription);
    }

    public void StopUptimeTimer()
    {
        _uptimeSubscription?.Dispose();
        _uptimeSubscription = null;
    }

    public void ResetUptime()
    {
        StopUptimeTimer();
        Uptime = string.Empty;
        StartUptimeTimer();
    }
}
