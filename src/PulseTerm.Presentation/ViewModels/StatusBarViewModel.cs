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
        // Live metrics segments stay hidden until the first real sample arrives.
        _cpuUsage = string.Empty;
        _memUsage = string.Empty;
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

    private string _netArrow = "↓";
    private string _netSpeed = string.Empty;
    private bool _isNetActive;

    /// <summary>"↑" while upload dominates, "↓" while download dominates (Android-style:
    /// one arrow + the dominant direction's rate).</summary>
    public string NetArrow
    {
        get => _netArrow;
        private set => this.RaiseAndSetIfChanged(ref _netArrow, value);
    }

    /// <summary>The dominant direction's rate, e.g. "4.2 MB/s". Empty hides the segment.</summary>
    public string NetSpeed
    {
        get => _netSpeed;
        private set => this.RaiseAndSetIfChanged(ref _netSpeed, value);
    }

    /// <summary>True while real traffic is flowing — the view paints the arrow in the accent
    /// color, falling back to muted when the link is idle.</summary>
    public bool IsNetActive
    {
        get => _isNetActive;
        private set => this.RaiseAndSetIfChanged(ref _isNetActive, value);
    }

    /// <summary>Feeds one network sample (bytes/second per direction). Shows the dominant
    /// direction's arrow and rate; below the activity threshold the arrow stays muted.</summary>
    public void UpdateNetwork(double rxBytesPerSec, double txBytesPerSec, bool hasRates)
    {
        if (!hasRates)
        {
            NetArrow = "↓";
            NetSpeed = "0 B/s";
            IsNetActive = false;
            return;
        }

        // Keepalives/echo traffic hovers under this; don't light the arrow for noise.
        const double activeThreshold = 512;

        bool uploadDominates = txBytesPerSec > rxBytesPerSec;
        double rate = uploadDominates ? txBytesPerSec : rxBytesPerSec;

        NetArrow = uploadDominates ? "↑" : "↓";
        NetSpeed = FormatRate(rate);
        IsNetActive = rate >= activeThreshold;
    }

    /// <summary>Clears the live metrics segments (no connected session).</summary>
    public void ClearSessionMetrics()
    {
        CpuUsage = string.Empty;
        MemUsage = string.Empty;
        NetSpeed = string.Empty;
        IsNetActive = false;
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
