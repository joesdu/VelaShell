using VelaShell.Core.Resources;

namespace VelaShell.Core.Models;

/// <summary>quick_commands/commands 的 v2 聚合文档。</summary>
public sealed class QuickCommandData
{
    /// <summary>当前支持的快捷命令文档版本。</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>文档结构版本。</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>默认、内置和用户分组。</summary>
    public List<QuickCommandGroup> Groups { get; set; } = [];

    /// <summary>自定义快捷命令列表。</summary>
    public List<QuickCommand> Commands { get; set; } = [];
}

/// <summary>
/// 内置快捷命令目录:设置 → 快捷命令 的预置项,同时作为命令补全建议
/// (plan.md #16)的数据源之一。自定义命令另存于 quick_commands 集合。
/// 描述文案本地化;命令文本本身(shell 命令)不翻译。首次访问时按当前语言取值
/// (启动流程先应用语言设置再构建 UI,顺序有保证)。
/// </summary>
public static class QuickCommandCatalog
{
    /// <summary>内置快捷命令列表(描述按当前语言本地化)。</summary>
    public static IReadOnlyList<QuickCommand> BuiltIns { get; } =
    [
        BuiltIn("netstat -tlnp", "Network", "netstat -tlnp", "QuickCmd_ShowListeningPorts", 0),
        BuiltIn("systemctl status", "System", "systemctl status", "QuickCmd_SystemdStatus", 0),
        BuiltIn("journalctl -f", "System", "journalctl -f", "QuickCmd_FollowJournal", 1),
        BuiltIn(
            "Enabled Services",
            "System",
            "sudo systemctl list-unit-files --type service | grep enabled",
            "QuickCmd_EnabledServices",
            2
        ),
        BuiltIn(
            "Linux Kernel Packages",
            "System",
            "dpkg --list | grep linux-image",
            "QuickCmd_KernelPackages",
            3
        ),
        BuiltIn("docker ps", "Docker", "sudo docker ps -a", "QuickCmd_DockerPs", 0),
        BuiltIn("docker stats", "Docker", "sudo docker stats", "QuickCmd_DockerStats", 1),
        BuiltIn(
            "docker system prune",
            "Docker",
            "sudo docker system prune -f",
            "QuickCmd_DockerPrune",
            2
        ),
        BuiltIn("ss listening ports", "Network", "ss -tlnp", "QuickCmd_SsListeningPorts", 1),
        BuiltIn("ip addresses", "Network", "ip -brief addr", "QuickCmd_IpAddresses", 2),
        BuiltIn("Public IP", "Network", "curl -s ifconfig.me && echo", "QuickCmd_PublicIp", 3),
        BuiltIn("Connection summary", "Network", "ss -s", "QuickCmd_ConnectionSummary", 4),
        BuiltIn(
            "Failed services",
            "System",
            "systemctl list-units --failed",
            "QuickCmd_FailedServices",
            4
        ),
        BuiltIn("OS release", "System", "cat /etc/os-release", "QuickCmd_OsRelease", 5),
        BuiltIn("Kernel info", "System", "uname -a", "QuickCmd_KernelInfo", 6),
        BuiltIn("Recent logins", "System", "last -n 20", "QuickCmd_RecentLogins", 7),
        BuiltIn("Uptime & load", "Monitor", "uptime", "QuickCmd_Uptime", 0),
        BuiltIn("Memory usage", "Monitor", "free -h", "QuickCmd_MemoryUsage", 1),
        BuiltIn(
            "Top CPU processes",
            "Monitor",
            "ps aux --sort=-%cpu | head -15",
            "QuickCmd_TopCpuProcesses",
            2
        ),
        BuiltIn(
            "Top memory processes",
            "Monitor",
            "ps aux --sort=-%mem | head -15",
            "QuickCmd_TopMemProcesses",
            3
        ),
        BuiltIn("vmstat", "Monitor", "vmstat 1 5", "QuickCmd_VmStat", 4),
        BuiltIn("Disk usage", "Files", "df -h", "QuickCmd_DiskUsage", 0),
        BuiltIn(
            "Largest directories",
            "Files",
            "du -xh --max-depth=1 . 2>/dev/null | sort -hr | head -15",
            "QuickCmd_LargestDirs",
            1
        ),
        BuiltIn(
            "Large files (>100MB)",
            "Files",
            "find . -xdev -type f -size +100M -exec ls -lh {} + 2>/dev/null | head -15",
            "QuickCmd_LargeFiles",
            2
        ),
        BuiltIn(
            "Recently modified",
            "Files",
            "find . -type f -mmin -60 -not -path '*/.*' 2>/dev/null | head -20",
            "QuickCmd_RecentlyModified",
            3
        ),
        BuiltIn("docker compose up", "Docker", "docker compose up -d", "QuickCmd_ComposeUp", 3),
        BuiltIn(
            "docker compose logs",
            "Docker",
            "docker compose logs -f --tail=100",
            "QuickCmd_ComposeLogs",
            4
        ),
        BuiltIn("docker disk usage", "Docker", "docker system df", "QuickCmd_DockerDf", 5),
    ];

    private static QuickCommand BuiltIn(
        string name,
        string group,
        string commandText,
        string descriptionKey,
        int sortOrder
    ) =>
        new()
        {
            Id = QuickCommandGroupCatalog.IdForName($"builtin-command:{name}"),
            GroupId = QuickCommandGroupCatalog.IdForName(group),
            Name = name,
            CommandText = commandText,
            Description = Strings.Get(descriptionKey),
            SortOrder = sortOrder,
            IsBuiltIn = true,
        };
}
