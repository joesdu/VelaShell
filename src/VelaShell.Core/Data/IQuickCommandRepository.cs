using VelaShell.Core.Models;

namespace VelaShell.Core.Data;

/// <summary>快捷命令 v2 文档的读取、保存、迁移与同步边界。</summary>
public interface IQuickCommandRepository
{
    /// <summary>本地快捷命令文档发生用户可见变化。</summary>
    event EventHandler? Changed;

    /// <summary>加载并在必要时迁移本地文档。</summary>
    Task<QuickCommandLoadResult> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>保存完整 v2 快照。</summary>
    Task SaveAsync(QuickCommandData data, CancellationToken cancellationToken = default);

    /// <summary>导出包含旧客户端分类兼容字段的同步快照。</summary>
    Task<QuickCommandSyncData> ExportSyncAsync(CancellationToken cancellationToken = default);

    /// <summary>校验并应用来自云端的 v1 或 v2 快照。</summary>
    Task ApplySyncAsync(QuickCommandSyncData data, CancellationToken cancellationToken = default);
}

/// <summary>快捷命令加载结果。</summary>
public sealed record QuickCommandLoadResult(
    QuickCommandData Data,
    bool Migrated = false,
    string? Error = null
);

/// <summary>Gist 中的快捷命令兼容载荷。</summary>
public sealed class QuickCommandSyncData
{
    /// <summary>快捷命令载荷版本;缺失视为 v1。</summary>
    public int SchemaVersion { get; set; }

    /// <summary>v2 分组列表。</summary>
    public List<QuickCommandGroup> Groups { get; set; } = [];

    /// <summary>命令列表;Category 仅用于旧客户端兼容。</summary>
    public List<QuickCommandSyncItem> Commands { get; set; } = [];
}

/// <summary>快捷命令同步条目。</summary>
public sealed class QuickCommandSyncItem
{
    /// <summary>稳定命令标识。</summary>
    public Guid Id { get; set; }

    /// <summary>v2 所属分组标识。</summary>
    public Guid GroupId { get; set; }

    /// <summary>命令显示名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>供旧客户端读取的兼容分类名。</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>发送到终端的命令正文。</summary>
    public string CommandText { get; set; } = string.Empty;

    /// <summary>命令说明。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>命令在分组内的排序值。</summary>
    public int SortOrder { get; set; }

    /// <summary>旧载荷中的内置标记;迁移时用于过滤误存项。</summary>
    public bool IsBuiltIn { get; set; }
}
