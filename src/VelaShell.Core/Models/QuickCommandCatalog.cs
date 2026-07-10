namespace VelaShell.Core.Models;

/// <summary>quick_commands 集合的存储形状(设置页与命令补全建议共用)。</summary>
public sealed class QuickCommandData
{
    public List<QuickCommand> Commands { get; set; } = [];
}

/// <summary>
/// 内置快捷命令目录:设置 → 快捷命令 的预置项,同时作为命令补全建议
/// (plan.md #16)的数据源之一。自定义命令另存于 quick_commands 集合。
/// </summary>
public static class QuickCommandCatalog
{
    public static IReadOnlyList<QuickCommand> BuiltIns { get; } =
    [
        new() { Name = "htop", Category = "System Monitor", CommandText = "htop", Description = "Interactive process viewer", IsBuiltIn = true },
        new() { Name = "top", Category = "System Monitor", CommandText = "top", Description = "Display running processes", IsBuiltIn = true },
        new() { Name = "df -h", Category = "System Monitor", CommandText = "df -h", Description = "Disk space usage (human-readable)", IsBuiltIn = true },
        new() { Name = "free -m", Category = "System Monitor", CommandText = "free -m", Description = "Memory usage in MB", IsBuiltIn = true },
        new() { Name = "netstat -tlnp", Category = "Network", CommandText = "netstat -tlnp", Description = "Show listening ports", IsBuiltIn = true },
        new() { Name = "ss -tlnp", Category = "Network", CommandText = "ss -tlnp", Description = "Socket statistics", IsBuiltIn = true },
        new() { Name = "docker ps", Category = "Docker", CommandText = "docker ps", Description = "List running containers", IsBuiltIn = true },
        new() { Name = "docker stats", Category = "Docker", CommandText = "docker stats", Description = "Container resource usage", IsBuiltIn = true },
        new() { Name = "systemctl status", Category = "System", CommandText = "systemctl status", Description = "Show systemd service status", IsBuiltIn = true },
        new() { Name = "journalctl -f", Category = "System", CommandText = "journalctl -f", Description = "Follow system journal", IsBuiltIn = true }
    ];
}
