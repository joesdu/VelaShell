using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using PulseTerm.Core.Resources;
using ReactiveUI;

namespace PulseTerm.Presentation.ViewModels;

public sealed class StatusBarViewModel : ReactiveObject, IDisposable
{
    private readonly IScheduler _scheduler;
    private readonly CompositeDisposable _disposables = new();

    private string _statusText;
    private string _connectionInfo;
    private string _status;
    private string _latency;
    private string _terminalType;
    private string _windowSize;
    private string _encoding;
    private string _uptime;
    private bool _isConnected;
    private string _cpuUsage;
    private string _memUsage;
    private string _netUsage;

    private IDisposable? _uptimeSubscription;
    private DateTimeOffset _uptimeStart;

    public StatusBarViewModel()
        : this(DefaultScheduler.Instance)
    {
    }

    public StatusBarViewModel(IScheduler scheduler)
    {
        _scheduler = scheduler;
        _statusText = Strings.Ready;
        _connectionInfo = string.Empty;
        _status = Strings.Disconnected;
        _latency = string.Empty;
        _terminalType = "xterm-256color";
        _windowSize = "80×24";
        _encoding = "UTF-8";
        _uptime = string.Empty;
        // The metric segments are always visible; before the first sample (or without a
        // connected session) they show idle placeholders (用户要求).
        _cpuUsage = "--";
        _memUsage = "--";
        _netUsage = string.Empty;
    }

    public string StatusText
    {
        get => _statusText;
        set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    public string ConnectionInfo
    {
        get => _connectionInfo;
        set => this.RaiseAndSetIfChanged(ref _connectionInfo, value);
    }

    public string Status
    {
        get => _status;
        set
        {
            this.RaiseAndSetIfChanged(ref _status, value);
            IsConnected = value == Strings.Connected;
        }
    }

    public string Latency
    {
        get => _latency;
        set => this.RaiseAndSetIfChanged(ref _latency, value);
    }

    public string TerminalType
    {
        get => _terminalType;
        set => this.RaiseAndSetIfChanged(ref _terminalType, value);
    }

    public string WindowSize
    {
        get => _windowSize;
        set => this.RaiseAndSetIfChanged(ref _windowSize, value);
    }

    public string Encoding
    {
        get => _encoding;
        set => this.RaiseAndSetIfChanged(ref _encoding, value);
    }

    public string Uptime
    {
        get => _uptime;
        set => this.RaiseAndSetIfChanged(ref _uptime, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set => this.RaiseAndSetIfChanged(ref _isConnected, value);
    }

    public string CpuUsage
    {
        get => _cpuUsage;
        set => this.RaiseAndSetIfChanged(ref _cpuUsage, value);
    }

    public string MemUsage
    {
        get => _memUsage;
        set => this.RaiseAndSetIfChanged(ref _memUsage, value);
    }

    public string NetUsage
    {
        get => _netUsage;
        set => this.RaiseAndSetIfChanged(ref _netUsage, value);
    }

    private string _swapUsage = "--";
    private string _diskUsage = "--";

    /// <summary>Swap usage percent; "--" when the host has no swap or no session is live.</summary>
    public string SwapUsage
    {
        get => _swapUsage;
        set => this.RaiseAndSetIfChanged(ref _swapUsage, value);
    }

    /// <summary>Root filesystem usage percent; "--" without a live session.</summary>
    public string DiskUsage
    {
        get => _diskUsage;
        set => this.RaiseAndSetIfChanged(ref _diskUsage, value);
    }

    private string _netSpeed = "0 B/s";
    private bool _isNetUpActive;
    private bool _isNetDownActive;

    /// <summary>The dominant direction's rate, e.g. "4.2 MB/s" (Android-style readout).</summary>
    public string NetSpeed
    {
        get => _netSpeed;
        private set => this.RaiseAndSetIfChanged(ref _netSpeed, value);
    }

    /// <summary>True while the server is actually uploading — lights the ↑ half of the
    /// arrow-up-down glyph in the accent color.</summary>
    public bool IsNetUpActive
    {
        get => _isNetUpActive;
        private set => this.RaiseAndSetIfChanged(ref _isNetUpActive, value);
    }

    /// <summary>True while the server is actually downloading — lights the ↓ half.</summary>
    public bool IsNetDownActive
    {
        get => _isNetDownActive;
        private set => this.RaiseAndSetIfChanged(ref _isNetDownActive, value);
    }

    /// <summary>Feeds one network sample (bytes/second per direction). Each arrow half lights
    /// up for its own direction; the readout shows the dominant direction's rate.</summary>
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

    /// <summary>Resets the metric segments to their idle placeholders (no connected session).
    /// The segments stay visible so the bar layout never jumps.</summary>
    public void ClearSessionMetrics()
    {
        CpuUsage = "--";
        MemUsage = "--";
        SwapUsage = "--";
        DiskUsage = "--";
        NetSpeed = "0 B/s";
        IsNetUpActive = false;
        IsNetDownActive = false;
    }

    public static string FormatRate(double bytesPerSec)
    {
        const double kb = 1024, mb = kb * 1024, gb = mb * 1024;
        return bytesPerSec switch
        {
            >= gb => $"{bytesPerSec / gb:F1} GB/s",
            >= mb => $"{bytesPerSec / mb:F1} MB/s",
            >= kb => $"{bytesPerSec / kb:F1} KB/s",
            _ => $"{bytesPerSec:F0} B/s",
        };
    }

    public void StartUptimeTimer()
    {
        StopUptimeTimer();
        _uptimeStart = _scheduler.Now;

        _uptimeSubscription = Observable
            .Interval(TimeSpan.FromSeconds(1), _scheduler)
            .Subscribe(_ =>
            {
                var elapsed = _scheduler.Now - _uptimeStart;
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

    public void Dispose()
    {
        _disposables.Dispose();
        _uptimeSubscription = null;
    }
}
