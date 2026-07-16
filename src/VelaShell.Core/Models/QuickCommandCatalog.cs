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
