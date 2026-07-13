namespace VelaShell.Core.Models;

/// <summary>
/// 服务器分组:将若干会话归入同一逻辑组以便侧栏组织与排序。
/// </summary>
public class ServerGroup
{
    /// <summary>分组的唯一标识符。</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>分组的显示名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>分组的可选图标标识。</summary>
    public string? Icon { get; set; }

    /// <summary>分组在列表中的排序序号,值越小越靠前。</summary>
    public int SortOrder { get; set; }

    /// <summary>归属于本分组的会话 Id 集合。</summary>
    public List<Guid> Sessions { get; set; } = [];
}
