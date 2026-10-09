namespace VelaShell.Presentation.ViewModels;

/// <summary>资源管理器发起的一次导出(#571):导出哪些连接、是不是整个分组。</summary>
/// <param name="ProfileIds">用户选中的连接(跳板机由导出服务自动带上)。</param>
/// <param name="GroupName">从分组菜单导出时的分组名(拼进建议的文件名里);导出全部 / 多选时为 null。</param>
public sealed record SessionExportRequest(IReadOnlyList<Guid> ProfileIds, string? GroupName);
