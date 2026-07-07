namespace PulseTerm.Core.Services;

/// <summary>A point-in-time resource snapshot of a remote session's host (design panel EP3Gd).</summary>
public sealed class SessionMetrics
{
    public int CpuCores { get; init; }

    /// <summary>0-100. Parsed as the 1-minute load-average approximation; when the collector
    /// holds a previous /proc/stat sample it overwrites this with the real instantaneous
    /// busy percentage from the two-sample jiffies delta (much fresher — loadavg lags by
    /// design, which read as "刷新慢" in the resource panel).</summary>
    public double CpuPercent { get; set; }
    public long MemTotalBytes { get; init; }
    public long MemUsedBytes { get; init; }
    public long DiskTotalBytes { get; init; }
    public long DiskUsedBytes { get; init; }
    public string OsVersion { get; init; } = "";
    public string Kernel { get; init; } = "";

    // Raw cumulative counters used by the stateful collector to compute deltas.
    public bool HasCpuCounters { get; init; }
    public long CpuTotalJiffies { get; init; }
    public long CpuIdleJiffies { get; init; }
    public bool HasNetCounters { get; init; }
    public long NetRxTotalBytes { get; init; }
    public long NetTxTotalBytes { get; init; }

    /// <summary>Instantaneous network rates (bytes/s), filled by the collector from the
    /// previous sample; false until a second sample exists.</summary>
    public bool HasNetRates { get; set; }
    public double NetRxBytesPerSec { get; set; }
    public double NetTxBytesPerSec { get; set; }

    public double MemPercent => MemTotalBytes > 0 ? MemUsedBytes * 100.0 / MemTotalBytes : 0;
    public double DiskPercent => DiskTotalBytes > 0 ? DiskUsedBytes * 100.0 / DiskTotalBytes : 0;

    /// <summary>
    /// Parses the delimited output of the metrics probe command (see MetricsCommand).
    /// Returns null when the output is unusable (e.g. non-Linux host).
    /// </summary>
    public static SessionMetrics? Parse(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        string Section(string marker)
        {
            var start = output.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return "";
            start += marker.Length;
            var end = output.IndexOf("__", start, StringComparison.Ordinal);
            return (end < 0 ? output[start..] : output[start..end]).Trim();
        }

        int cores = int.TryParse(Section("__P__"), out var p) ? Math.Max(1, p) : 1;

        double load1 = 0;
        var loadParts = Section("__L__").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (loadParts.Length > 0)
            double.TryParse(loadParts[0], System.Globalization.CultureInfo.InvariantCulture, out load1);

        long memTotal = 0, memUsed = 0;
        var memParts = Section("__M__").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (memParts.Length >= 2)
        {
            long.TryParse(memParts[0], out memTotal);
            long.TryParse(memParts[1], out memUsed);
        }

        long diskTotal = 0, diskUsed = 0;
        var diskParts = Section("__D__").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (diskParts.Length >= 2)
        {
            long.TryParse(diskParts[0], out diskTotal);
            long.TryParse(diskParts[1], out diskUsed);
        }

        var os = Section("__O__");
        var kernel = Section("__K__");

        // __S__: the aggregate "cpu  user nice system idle iowait irq ..." line of /proc/stat.
        long cpuTotal = 0, cpuIdle = 0;
        bool hasCpuCounters = false;
        var statParts = Section("__S__").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (statParts.Length >= 5 && statParts[0] == "cpu")
        {
            for (int i = 1; i < statParts.Length; i++)
            {
                if (long.TryParse(statParts[i], out var v))
                    cpuTotal += v;
            }

            long.TryParse(statParts[4], out var idle);              // idle
            long iowait = 0;
            if (statParts.Length > 5)
                long.TryParse(statParts[5], out iowait);            // iowait counts as idle
            cpuIdle = idle + iowait;
            hasCpuCounters = cpuTotal > 0;
        }

        // __N__: cumulative rx/tx bytes summed over all non-loopback interfaces.
        long netRx = 0, netTx = 0;
        bool hasNetCounters = false;
        var netParts = Section("__N__").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (netParts.Length >= 2 &&
            long.TryParse(netParts[0], out netRx) &&
            long.TryParse(netParts[1], out netTx))
        {
            hasNetCounters = true;
        }

        if (cores == 1 && memTotal == 0 && diskTotal == 0 && os.Length == 0 && kernel.Length == 0
            && !hasCpuCounters && !hasNetCounters)
            return null;

        return new SessionMetrics
        {
            CpuCores = cores,
            CpuPercent = Math.Clamp(load1 / cores * 100.0, 0, 100),
            MemTotalBytes = memTotal,
            MemUsedBytes = memUsed,
            DiskTotalBytes = diskTotal,
            DiskUsedBytes = diskUsed,
            OsVersion = os,
            Kernel = kernel,
            HasCpuCounters = hasCpuCounters,
            CpuTotalJiffies = cpuTotal,
            CpuIdleJiffies = cpuIdle,
            HasNetCounters = hasNetCounters,
            NetRxTotalBytes = netRx,
            NetTxTotalBytes = netTx,
        };
    }

    /// <summary>One-shot probe: each section is delimited so a partial failure of any single
    /// probe doesn't break the rest. Linux-oriented (spec targets Ubuntu/CentOS). __S__/__N__
    /// export raw cumulative counters; the collector turns consecutive samples into
    /// instantaneous CPU% and network rates.</summary>
    public const string MetricsCommand =
        "echo __P__; nproc 2>/dev/null; " +
        "echo __L__; cat /proc/loadavg 2>/dev/null; " +
        "echo __M__; free -b 2>/dev/null | awk 'NR==2{print $2\" \"$3}'; " +
        "echo __D__; df -B1 --output=size,used / 2>/dev/null | tail -1; " +
        "echo __O__; . /etc/os-release 2>/dev/null && echo \"$PRETTY_NAME\"; " +
        "echo __K__; uname -r 2>/dev/null; " +
        "echo __S__; grep -m1 '^cpu ' /proc/stat 2>/dev/null; " +
        "echo __N__; awk -F: 'NR>2 {gsub(/^ +/,\"\",$1); if ($1!=\"lo\") {split($2,f,\" \"); rx+=f[1]; tx+=f[9]}} END {print rx+0\" \"tx+0}' /proc/net/dev 2>/dev/null";
}
