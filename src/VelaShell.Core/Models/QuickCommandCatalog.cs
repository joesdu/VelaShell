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
        new() { Name = "netstat -tlnp", Category = "Network", CommandText = "netstat -tlnp", Description = "Show listening ports", IsBuiltIn = true },
        new() { Name = "systemctl status", Category = "System", CommandText = "systemctl status", Description = "Show systemd service status", IsBuiltIn = true },
        new() { Name = "journalctl -f", Category = "System", CommandText = "journalctl -f", Description = "Follow system journal", IsBuiltIn = true },
        new() { Name = "Enabled Services", Category = "System", CommandText = "sudo systemctl list-unit-files --type service | grep enabled", Description = "List enabled systemd services", IsBuiltIn = true },
        new() { Name = "Linux Kernel Packages", Category = "System", CommandText = "dpkg --list | grep linux-image", Description = "List installed Linux kernel packages", IsBuiltIn = true },
        new() { Name = "docker ps", Category = "Docker", CommandText = "sudo docker ps -a", Description = "List running containers", IsBuiltIn = true },
        new() { Name = "docker stats", Category = "Docker", CommandText = "sudo docker stats", Description = "Container resource usage", IsBuiltIn = true },
        new() { Name = "docker system prune", Category = "Docker", CommandText = "sudo docker system prune -f", Description = "Remove unused Docker data", IsBuiltIn = true }
    ];
}
