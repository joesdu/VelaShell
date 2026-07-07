namespace PulseTerm.Core.Services;

/// <summary>A point-in-time resource snapshot of a remote session's host (design panel EP3Gd).</summary>
public sealed class SessionMetrics
{
    public int CpuCores { get; init; }
    /// <summary>0-100. Derived from 1-minute load average over core count (portable one-shot
    /// approximation; a two-sample /proc/stat delta would need a stateful collector).</summary>
    public double CpuPercent { get; init; }
    public long MemTotalBytes { get; init; }
    public long MemUsedBytes { get; init; }
    public long DiskTotalBytes { get; init; }
    public long DiskUsedBytes { get; init; }
    public string OsVersion { get; init; } = "";
    public string Kernel { get; init; } = "";

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

        if (cores == 1 && memTotal == 0 && diskTotal == 0 && os.Length == 0 && kernel.Length == 0)
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
        };
    }

    /// <summary>One-shot probe: each section is delimited so a partial failure of any single
    /// probe doesn't break the rest. Linux-oriented (spec targets Ubuntu/CentOS).</summary>
    public const string MetricsCommand =
        "echo __P__; nproc 2>/dev/null; " +
        "echo __L__; cat /proc/loadavg 2>/dev/null; " +
        "echo __M__; free -b 2>/dev/null | awk 'NR==2{print $2\" \"$3}'; " +
        "echo __D__; df -B1 --output=size,used / 2>/dev/null | tail -1; " +
        "echo __O__; . /etc/os-release 2>/dev/null && echo \"$PRETTY_NAME\"; " +
        "echo __K__; uname -r 2>/dev/null";
}
