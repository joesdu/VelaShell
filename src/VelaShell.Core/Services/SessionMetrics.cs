using System.Globalization;

namespace VelaShell.Core.Services;

/// <summary>一块已挂载的真实文件系统(non-tmpfs 等虚拟盘)的用量(资源面板逐盘显示)。</summary>
/// <param name="Source">文件系统的设备/来源(如 /dev/sda1)。</param>
/// <param name="MountPoint">挂载点路径(可能含空格)。</param>
/// <param name="TotalBytes">该文件系统的总容量(字节)。</param>
/// <param name="UsedBytes">该文件系统的已用空间(字节)。</param>
// ReSharper disable once NotAccessedPositionalProperty.Global
public sealed record DiskUsage(string Source, string MountPoint, long TotalBytes, long UsedBytes)
{
    /// <summary>已用空间占总容量的百分比(0-100);总容量为 0 时返回 0。</summary>
    public double Percent => TotalBytes > 0 ? (UsedBytes * 100.0) / TotalBytes : 0;
}

/// <summary>单个 CPU 核心的累计 jiffies 计数(/proc/stat cpuN 行),由采集器做两次采样差分。</summary>
/// <param name="Name">核心名(如 cpu0)。</param>
/// <param name="TotalJiffies">该核心累计的总 jiffies。</param>
/// <param name="IdleJiffies">该核心累计的空闲 jiffies(含 iowait)。</param>
public sealed record CpuCoreCounter(string Name, long TotalJiffies, long IdleJiffies);

/// <summary>单个网卡的累计收发字节计数(/proc/net/dev),由采集器做两次采样差分。</summary>
/// <param name="Name">网卡接口名。</param>
/// <param name="RxBytes">累计接收字节数。</param>
/// <param name="TxBytes">累计发送字节数。</param>
public sealed record NetInterfaceCounter(string Name, long RxBytes, long TxBytes);

/// <summary>单个网卡的瞬时速率(字节/秒),由采集器从上一采样计算。</summary>
/// <param name="Name">网卡接口名。</param>
/// <param name="RxBytesPerSec">接收速率(字节/秒)。</param>
/// <param name="TxBytesPerSec">发送速率(字节/秒)。</param>
public sealed record NetInterfaceRate(string Name, double RxBytesPerSec, double TxBytesPerSec);

/// <summary>远端会话主机在某一时刻的资源快照(设计面板 EP3Gd)。</summary>
public sealed class SessionMetrics
{
    /// <summary>
    /// 一次性探测:每段以分隔符隔开,使得任一探针的部分失败不会拖垮其余部分。面向 Linux(规范目标为 Ubuntu/CentOS)。
    /// __S__/__N__ 导出原始累计计数;采集器将连续两次采样转换为瞬时 CPU% 与网络速率。
    /// </summary>
    public const string MetricsCommand =
        "echo __P__; nproc 2>/dev/null; " +
        "echo __L__; cat /proc/loadavg 2>/dev/null; " +
        // htop 口径的 used = total − free − buffers − cached − 可回收 slab。`free` 自带的
        // "used" 列在 procps 4.x 改了含义(total − available),读出来比用户比对的对象
        // (htop 99M 对比我们的 19%)高出约 2 倍。
        """echo __M__; awk '/^MemTotal:/{t=$2} /^MemFree:/{f=$2} /^Buffers:/{b=$2} /^Cached:/{c=$2} /^SReclaimable:/{s=$2} /^SwapTotal:/{st=$2} /^SwapFree:/{sf=$2} END{if(t>0){u=t-f-b-c-s; if(u<0)u=0; print t*1024" "u*1024" "st*1024" "(st-sf)*1024}}' /proc/meminfo 2>/dev/null; """ +
        "echo __D__; df -B1 --output=size,used / 2>/dev/null | tail -1; " +
        """echo __O__; . /etc/os-release 2>/dev/null && echo "$PRETTY_NAME"; """ +
        "echo __K__; uname -r 2>/dev/null; " +
        "echo __S__; grep -m1 '^cpu ' /proc/stat 2>/dev/null; " +
        """echo __N__; awk -F: 'NR>2 {gsub(/^ +/,"",$1); if ($1!="lo") {split($2,f," "); rx+=f[1]; tx+=f[9]}} END {print rx+0" "tx+0}' /proc/net/dev 2>/dev/null; """ +
        // __DL__:多磁盘面板/提示所用的全部真实文件系统(排除 tmpfs 等虚拟盘;
        // 需 GNU df,BusyBox 上该段为空,UI 退回 __D__ 的根分区聚合值)。
        "echo __DL__; df -B1 -x tmpfs -x devtmpfs -x squashfs -x overlay --output=source,size,used,target 2>/dev/null | tail -n +2; " +
        // __C__:状态栏 CPU 提示所用的逐核心 /proc/stat 行。
        "echo __C__; grep '^cpu[0-9]' /proc/stat 2>/dev/null; " +
        // __NI__:状态栏网络提示所用的逐网卡累计 rx/tx。
        // 只取物理网卡(/sys/class/net/*/device 存在):Docker/K8s 主机会有成百个 veth/
        // 网桥虚拟接口,全列出来没法看。__N__ 的合计口径保持不变。
        """echo __NI__; for i in /sys/class/net/*; do if [ -e "$i/device" ]; then echo "${i##*/} $(cat "$i/statistics/rx_bytes" 2>/dev/null) $(cat "$i/statistics/tx_bytes" 2>/dev/null)"; fi; done 2>/dev/null""";

    /// <summary>逻辑 CPU 核心数(nproc);至少为 1。</summary>
    public int CpuCores { get; private init; }

    /// <summary>
    /// 0-100。解析时作为 1 分钟负载平均的近似值;当采集器持有上一次 /proc/stat 采样时,
    /// 会用两次采样 jiffies 差分得到的真实瞬时繁忙率覆盖此值(更新鲜——loadavg 按设计就是滞后的,
    /// 在资源面板里表现为"刷新慢")。
    /// </summary>
    public double CpuPercent { get; set; }

    /// <summary>物理内存总量(字节)。</summary>
    public long MemTotalBytes { get; private init; }

    /// <summary>已用物理内存(字节),htop 口径:total − free − buffers − cached − 可回收 slab。</summary>
    public long MemUsedBytes { get; private init; }

    /// <summary>交换分区总量(字节)。</summary>
    public long SwapTotalBytes { get; private init; }

    /// <summary>已用交换分区(字节)。</summary>
    public long SwapUsedBytes { get; private init; }

    /// <summary>根分区总容量(字节)。</summary>
    public long DiskTotalBytes { get; private init; }

    /// <summary>根分区已用容量(字节)。</summary>
    public long DiskUsedBytes { get; private init; }

    /// <summary>全部已挂载的真实文件系统(__DL__);空 = 探针不支持,退回根分区聚合值。</summary>
    public IReadOnlyList<DiskUsage> Disks { get; private init; } = [];

    /// <summary>操作系统发行版描述(/etc/os-release 的 PRETTY_NAME)。</summary>
    public string OsVersion { get; private init; } = "";

    /// <summary>内核版本(uname -r)。</summary>
    public string Kernel { get; private init; } = "";

    // 有状态采集器用来计算差分的原始累计计数。
    /// <summary>本次采样是否成功取得 CPU jiffies 累计计数(可供采集器差分)。</summary>
    public bool HasCpuCounters { get; private init; }

    /// <summary>聚合 CPU 的累计总 jiffies(/proc/stat cpu 行各列之和)。</summary>
    public long CpuTotalJiffies { get; private init; }

    /// <summary>聚合 CPU 的累计空闲 jiffies(idle + iowait)。</summary>
    public long CpuIdleJiffies { get; private init; }

    /// <summary>本次采样是否成功取得网络收发累计字节计数(可供采集器差分)。</summary>
    public bool HasNetCounters { get; private init; }

    /// <summary>所有非回环网卡累计接收字节数之和。</summary>
    public long NetRxTotalBytes { get; private init; }

    /// <summary>所有非回环网卡累计发送字节数之和。</summary>
    public long NetTxTotalBytes { get; private init; }

    /// <summary>
    /// 瞬时网络速率(字节/秒),由采集器根据上一次采样填入;在取得第二次采样前为 false。
    /// </summary>
    public bool HasNetRates { get; set; }

    /// <summary>瞬时网络接收速率(字节/秒),由采集器从上一采样计算。</summary>
    public double NetRxBytesPerSec { get; set; }

    /// <summary>瞬时网络发送速率(字节/秒),由采集器从上一采样计算。</summary>
    public double NetTxBytesPerSec { get; set; }

    /// <summary>
    /// 逐核心累计计数(__C__),原始值;百分比由采集器差分后写入
    /// <see cref="CorePercents" />(与本列表同序,首个采样为 null)。
    /// </summary>
    public IReadOnlyList<CpuCoreCounter> CoreCounters { get; private init; } = [];

    /// <summary>各核心的瞬时占用率(0-100),与 <see cref="CoreCounters" /> 同序;首个采样为 null。</summary>
    public IReadOnlyList<double>? CorePercents { get; set; }

    /// <summary>
    /// 逐网卡累计计数(__NI__),原始值;速率由采集器差分后写入
    /// <see cref="NicRates" />(首个采样为 null)。
    /// </summary>
    public IReadOnlyList<NetInterfaceCounter> NicCounters { get; private init; } = [];

    /// <summary>各网卡的瞬时收发速率,与 <see cref="NicCounters" /> 同序;首个采样为 null。</summary>
    public IReadOnlyList<NetInterfaceRate>? NicRates { get; set; }

    /// <summary>内存使用率(0-100);总量为 0 时返回 0。</summary>
    public double MemPercent => MemTotalBytes > 0 ? (MemUsedBytes * 100.0) / MemTotalBytes : 0;

    /// <summary>交换分区使用率(0-100);总量为 0 时返回 0。</summary>
    public double SwapPercent => SwapTotalBytes > 0 ? (SwapUsedBytes * 100.0) / SwapTotalBytes : 0;

    /// <summary>根分区使用率(0-100);总量为 0 时返回 0。</summary>
    public double DiskPercent => DiskTotalBytes > 0 ? (DiskUsedBytes * 100.0) / DiskTotalBytes : 0;

    /// <summary>
    /// 解析指标探针命令的分隔输出(见 MetricsCommand)。当输出不可用(如非 Linux 主机)时返回 null。
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
            _ = double.TryParse(loadParts[0], CultureInfo.InvariantCulture, out load1);
        }
        long memTotal = 0, memUsed = 0, swapTotal = 0, swapUsed = 0;
        string[] memParts = Section("__M__").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (memParts.Length >= 2)
        {
            _ = long.TryParse(memParts[0], out memTotal);
            _ = long.TryParse(memParts[1], out memUsed);
        }
        if (memParts.Length >= 4)
        {
            _ = long.TryParse(memParts[2], out swapTotal);
            _ = long.TryParse(memParts[3], out swapUsed);
        }
        long diskTotal = 0, diskUsed = 0;
        string[] diskParts = Section("__D__").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (diskParts.Length >= 2)
        {
            _ = long.TryParse(diskParts[0], out diskTotal);
            _ = long.TryParse(diskParts[1], out diskUsed);
        }
        string os = Section("__O__");
        string kernel = Section("__K__");

        // __S__:/proc/stat 的聚合行 "cpu  user nice system idle iowait irq ..."。
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
            _ = long.TryParse(statParts[4], out long idle); // 空闲
            long iowait = 0;
            if (statParts.Length > 5)
            {
                _ = long.TryParse(statParts[5], out iowait); // iowait 计入空闲
            }
            cpuIdle = idle + iowait;
            hasCpuCounters = cpuTotal > 0;
        }

        // __N__:所有非回环网卡累计 rx/tx 字节之和。
        long netRx = 0, netTx = 0;
        bool hasNetCounters = false;
        string[] netParts = Section("__N__").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (netParts.Length >= 2 &&
            long.TryParse(netParts[0], out netRx) &&
            long.TryParse(netParts[1], out netTx))
        {
            hasNetCounters = true;
        }

        // __DL__:每行一个真实文件系统 "source size used mountpoint"(挂载点可能含空格)。
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

        // __C__:/proc/stat 的逐核心行 "cpuN user nice system idle iowait ...",
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
            _ = long.TryParse(parts[4], out long coreIdle);
            long coreIowait = 0;
            if (parts.Length > 5)
            {
                _ = long.TryParse(parts[5], out coreIowait);
            }
            if (total > 0)
            {
                coreCounters.Add(new(parts[0], total, coreIdle + coreIowait));
            }
        }

        // __NI__:每行一个非回环网卡 "name rx tx"。
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
