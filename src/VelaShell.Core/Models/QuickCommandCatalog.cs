using VelaShell.Core.Resources;

namespace VelaShell.Core.Models;

/// <summary>quick_commands 集合的存储形状(设置页与命令补全建议共用)。</summary>
public sealed class QuickCommandData
{
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
        new() { Name = "netstat -tlnp", Category = "Network", CommandText = "netstat -tlnp", Description = Strings.Get("QuickCmd_ShowListeningPorts"), IsBuiltIn = true },
        new() { Name = "systemctl status", Category = "System", CommandText = "systemctl status", Description = Strings.Get("QuickCmd_SystemdStatus"), IsBuiltIn = true },
        new() { Name = "journalctl -f", Category = "System", CommandText = "journalctl -f", Description = Strings.Get("QuickCmd_FollowJournal"), IsBuiltIn = true },
        new() { Name = "Enabled Services", Category = "System", CommandText = "sudo systemctl list-unit-files --type service | grep enabled", Description = Strings.Get("QuickCmd_EnabledServices"), IsBuiltIn = true },
        new() { Name = "Linux Kernel Packages", Category = "System", CommandText = "dpkg --list | grep linux-image", Description = Strings.Get("QuickCmd_KernelPackages"), IsBuiltIn = true },
        new() { Name = "docker ps", Category = "Docker", CommandText = "sudo docker ps -a", Description = Strings.Get("QuickCmd_DockerPs"), IsBuiltIn = true },
        new() { Name = "docker stats", Category = "Docker", CommandText = "sudo docker stats", Description = Strings.Get("QuickCmd_DockerStats"), IsBuiltIn = true },
        new() { Name = "docker system prune", Category = "Docker", CommandText = "sudo docker system prune -f", Description = Strings.Get("QuickCmd_DockerPrune"), IsBuiltIn = true }
    ];
}
