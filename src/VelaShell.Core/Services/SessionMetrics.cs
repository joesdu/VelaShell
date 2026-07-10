using System.Globalization;

namespace VelaShell.Core.Services;

/// <summary>One mounted real filesystem(non-tmpfs 等虚拟盘)的用量(资源面板逐盘显示)。</summary>
// ReSharper disable once NotAccessedPositionalProperty.Global
public sealed record DiskUsage(string Source, string MountPoint, long TotalBytes, long UsedBytes)
{
    public double Percent => TotalBytes > 0 ? (UsedBytes * 100.0) / TotalBytes : 0;
}

/// <summary>单个 CPU 核心的累计 jiffies 计数(/proc/stat cpuN 行),由采集器做两次采样差分。</summary>
public sealed record CpuCoreCounter(string Name, long TotalJiffies, long IdleJiffies);

/// <summary>单个网卡的累计收发字节计数(/proc/net/dev),由采集器做两次采样差分。</summary>
public sealed record NetInterfaceCounter(string Name, long RxBytes, long TxBytes);

/// <summary>单个网卡的瞬时速率(字节/秒),由采集器从上一采样计算。</summary>
public sealed record NetInterfaceRate(string Name, double RxBytesPerSec, double TxBytesPerSec);

/// <summary>A point-in-time resource snapshot of a remote session's host (design panel EP3Gd).</summary>
public sealed class SessionMetrics
{
    /// <summary>
    /// One-shot probe: each section is delimited so a partial failure of any single
    /// probe doesn't break the rest. Linux-oriented (spec targets Ubuntu/CentOS). __S__/__N__
    /// export raw cumulative counters; the collector turns consecutive samples into
    /// instantaneous CPU% and network rates.
    /// </summary>
    public const string MetricsCommand =
        "echo __P__; nproc 2>/dev/null; " +
        "echo __L__; cat /proc/loadavg 2>/dev/null; " +
        // htop-style used = total − free − buffers − cached − reclaimable slab. `free`'s own
        // "used" column changed meaning in procps 4.x (total − available), which reads ~2x
        // higher than what users compare against (用户反馈: htop 99M vs our 19%).
        """echo __M__; awk '/^MemTotal:/{t=$2} /^MemFree:/{f=$2} /^Buffers:/{b=$2} /^Cached:/{c=$2} /^SReclaimable:/{s=$2} /^SwapTotal:/{st=$2} /^SwapFree:/{sf=$2} END{if(t>0){u=t-f-b-c-s; if(u<0)u=0; print t*1024" "u*1024" "st*1024" "(st-sf)*1024}}' /proc/meminfo 2>/dev/null; """ +
        "echo __D__; df -B1 --output=size,used / 2>/dev/null | tail -1; " +
        """echo __O__; . /etc/os-release 2>/dev/null && echo "$PRETTY_NAME"; """ +
        "echo __K__; uname -r 2>/dev/null; " +
        "echo __S__; grep -m1 '^cpu ' /proc/stat 2>/dev/null; " +
        """echo __N__; awk -F: 'NR>2 {gsub(/^ +/,"",$1); if ($1!="lo") {split($2,f," "); rx+=f[1]; tx+=f[9]}} END {print rx+0" "tx+0}' /proc/net/dev 2>/dev/null; """ +
        // __DL__: all real filesystems for the multi-disk panel/tooltips(排除 tmpfs 等虚拟盘;
        // 需 GNU df,BusyBox 上该段为空,UI 退回 __D__ 的根分区聚合值)。
        "echo __DL__; df -B1 -x tmpfs -x devtmpfs -x squashfs -x overlay --output=source,size,used,target 2>/dev/null | tail -n +2; " +
        // __C__: per-core /proc/stat lines for the status-bar CPU tooltip.
        "echo __C__; grep '^cpu[0-9]' /proc/stat 2>/dev/null; " +
        // __NI__: per-interface cumulative rx/tx for the status-bar network tooltip。
        // 只取物理网卡(/sys/class/net/*/device 存在):Docker/K8s 主机会有成百个 veth/
        // 网桥虚拟接口,全列出来没法看(用户反馈)。__N__ 的合计口径保持不变。
        """echo __NI__; for i in /sys/class/net/*; do if [ -e "$i/device" ]; then echo "${i##*/} $(cat "$i/statistics/rx_bytes" 2>/dev/null) $(cat "$i/statistics/tx_bytes" 2>/dev/null)"; fi; done 2>/dev/null""";

    public int CpuCores { get; private init; }

    /// <summary>
    /// 0-100. Parsed as the 1-minute load-average approximation; when the collector
    /// holds a previous /proc/stat sample it overwrites this with the real instantaneous
    /// busy percentage from the two-sample jiffies delta (much fresher — loadavg lags by
    /// design, which read as "刷新慢" in the resource panel).
    /// </summary>
    public double CpuPercent { get; set; }

    public long MemTotalBytes { get; private init; }

    public long MemUsedBytes { get; private init; }

    public long SwapTotalBytes { get; private init; }

    public long SwapUsedBytes { get; private init; }

    public long DiskTotalBytes { get; private init; }

    public long DiskUsedBytes { get; private init; }

    /// <summary>All mounted real filesystems(__DL__);空 = 探针不支持,退回根分区聚合值。</summary>
    public IReadOnlyList<DiskUsage> Disks { get; private init; } = [];

    public string OsVersion { get; private init; } = "";

    public string Kernel { get; private init; } = "";

    // Raw cumulative counters used by the stateful collector to compute deltas.
    public bool HasCpuCounters { get; private init; }

    public long CpuTotalJiffies { get; private init; }

    public long CpuIdleJiffies { get; private init; }

    public bool HasNetCounters { get; private init; }

    public long NetRxTotalBytes { get; private init; }

    public long NetTxTotalBytes { get; private init; }

    /// <summary>
    /// Instantaneous network rates (bytes/s), filled by the collector from the
    /// previous sample; false until a second sample exists.
    /// </summary>
    public bool HasNetRates { get; set; }

    public double NetRxBytesPerSec { get; set; }

    public double NetTxBytesPerSec { get; set; }

    /// <summary>
    /// Per-core cumulative counters(__C__),原始值;百分比由采集器差分后写入
    /// <see cref="CorePercents" />(与本列表同序,首个采样为 null)。
    /// </summary>
    public IReadOnlyList<CpuCoreCounter> CoreCounters { get; private init; } = [];

    public IReadOnlyList<double>? CorePercents { get; set; }

    /// <summary>
    /// Per-NIC cumulative counters(__NI__),原始值;速率由采集器差分后写入
    /// <see cref="NicRates" />(首个采样为 null)。
    /// </summary>
    public IReadOnlyList<NetInterfaceCounter> NicCounters { get; private init; } = [];

    public IReadOnlyList<NetInterfaceRate>? NicRates { get; set; }

    public double MemPercent => MemTotalBytes > 0 ? (MemUsedBytes * 100.0) / MemTotalBytes : 0;

    public double SwapPercent => SwapTotalBytes > 0 ? (SwapUsedBytes * 100.0) / SwapTotalBytes : 0;

    public double DiskPercent => DiskTotalBytes > 0 ? (DiskUsedBytes * 100.0) / DiskTotalBytes : 0;

    /// <summary>
    /// Parses the delimited output of the metrics probe command (see MetricsCommand).
    /// Returns null when the output is unusable (e.g. non-Linux host).
    /// </summary>
    public static SessionMetrics? Parse(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }
        int cores = int.TryParse(Section("__P__"), out int p) ? Math.Max(1, p) : 1;
        double load1 = 0;
        string[] loadParts = Section("__L__").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (loadParts.Length > 0)
        {
            double.TryParse(loadParts[0], CultureInfo.InvariantCulture, out load1);
        }
        long memTotal = 0, memUsed = 0, swapTotal = 0, swapUsed = 0;
        string[] memParts = Section("__M__").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (memParts.Length >= 2)
        {
            long.TryParse(memParts[0], out memTotal);
            long.TryParse(memParts[1], out memUsed);
        }
        if (memParts.Length >= 4)
        {
            long.TryParse(memParts[2], out swapTotal);
            long.TryParse(memParts[3], out swapUsed);
        }
        long diskTotal = 0, diskUsed = 0;
        string[] diskParts = Section("__D__").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (diskParts.Length >= 2)
        {
            long.TryParse(diskParts[0], out diskTotal);
            long.TryParse(diskParts[1], out diskUsed);
        }
        string os = Section("__O__");
        string kernel = Section("__K__");

        // __S__: the aggregate "cpu  user nice system idle iowait irq ..." line of /proc/stat.
        long cpuTotal = 0, cpuIdle = 0;
        bool hasCpuCounters = false;
        string[] statParts = Section("__S__").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (statParts is ["cpu", _, _, _, _, ..])
        {
            for (int i = 1; i < statParts.Length; i++)
            {
                if (long.TryParse(statParts[i], out long v))
                {
                    cpuTotal += v;
                }
            }
            long.TryParse(statParts[4], out long idle); // idle
            long iowait = 0;
            if (statParts.Length > 5)
            {
                long.TryParse(statParts[5], out iowait); // iowait counts as idle
            }
            cpuIdle = idle + iowait;
            hasCpuCounters = cpuTotal > 0;
        }

        // __N__: cumulative rx/tx bytes summed over all non-loopback interfaces.
        long netRx = 0, netTx = 0;
        bool hasNetCounters = false;
        string[] netParts = Section("__N__").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (netParts.Length >= 2 &&
            long.TryParse(netParts[0], out netRx) &&
            long.TryParse(netParts[1], out netTx))
        {
            hasNetCounters = true;
        }

        // __DL__: one real filesystem per line "source size used mountpoint"(挂载点可能含空格)。
        // 同一设备的多个挂载(bind mount / btrfs 子卷)只记第一处,避免容量重复计入。
        var disks = new List<DiskUsage>();
        var seenSources = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in Lines(Section("__DL__")))
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4 ||
                !long.TryParse(parts[1], out long dTotal) ||
                !long.TryParse(parts[2], out long dUsed) ||
                dTotal <= 0)
            {
                continue;
            }
            if (!seenSources.Add(parts[0]))
            {
                continue;
            }
            disks.Add(new(parts[0], string.Join(' ', parts[3..]), dTotal, dUsed));
        }

        // __C__: per-core "cpuN user nice system idle iowait ..." lines of /proc/stat,
        // 与聚合行同一套 jiffies 口径(iowait 计入空闲)。
        var coreCounters = new List<CpuCoreCounter>();
        foreach (string line in Lines(Section("__C__")))
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || !parts[0].StartsWith("cpu", StringComparison.Ordinal))
            {
                continue;
            }
            long total = 0;
            for (int i = 1; i < parts.Length; i++)
            {
                if (long.TryParse(parts[i], out long v))
                {
                    total += v;
                }
            }
            long.TryParse(parts[4], out long coreIdle);
            long coreIowait = 0;
            if (parts.Length > 5)
            {
                long.TryParse(parts[5], out coreIowait);
            }
            if (total > 0)
            {
                coreCounters.Add(new(parts[0], total, coreIdle + coreIowait));
            }
        }

        // __NI__: one non-loopback interface per line "name rx tx".
        var nicCounters = new List<NetInterfaceCounter>();
        foreach (string line in Lines(Section("__NI__")))
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 &&
                long.TryParse(parts[1], out long rx) &&
                long.TryParse(parts[2], out long tx))
            {
                nicCounters.Add(new(parts[0], rx, tx));
            }
        }
        if (cores == 1 && memTotal == 0 && diskTotal == 0 && os.Length == 0 && kernel.Length == 0 && !hasCpuCounters && !hasNetCounters)
        {
            return null;
        }
        return new()
        {
            CpuCores = cores,
            CpuPercent = Math.Clamp((load1 / cores) * 100.0, 0, 100),
            MemTotalBytes = memTotal,
            MemUsedBytes = memUsed,
            SwapTotalBytes = swapTotal,
            SwapUsedBytes = swapUsed,
            DiskTotalBytes = diskTotal,
            DiskUsedBytes = diskUsed,
            Disks = disks,
            OsVersion = os,
            Kernel = kernel,
            HasCpuCounters = hasCpuCounters,
            CpuTotalJiffies = cpuTotal,
            CpuIdleJiffies = cpuIdle,
            HasNetCounters = hasNetCounters,
            NetRxTotalBytes = netRx,
            NetTxTotalBytes = netTx,
            CoreCounters = coreCounters,
            NicCounters = nicCounters
        };

        string Section(string marker)
        {
            int start = output.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                return "";
            }
            start += marker.Length;
            int end = output.IndexOf("__", start, StringComparison.Ordinal);
            return (end < 0 ? output[start..] : output[start..end]).Trim();
        }

        static string[] Lines(string section) => section.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
