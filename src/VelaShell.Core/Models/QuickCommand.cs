namespace VelaShell.Core.Models;

/// <summary>快捷命令定义:一条可在终端快速插入/执行的预设命令及其分组与描述。</summary>
public class QuickCommand
{
    /// <summary>快捷命令的稳定唯一标识符。</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>快捷命令的显示名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>所属分组标识。</summary>
    public Guid GroupId { get; set; } = QuickCommandGroupCatalog.DefaultGroupId;

    /// <summary>实际要发送到终端执行的命令文本。</summary>
    public string CommandText { get; set; } = string.Empty;

    /// <summary>命令的说明文字,便于用户理解其用途。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>在所属分组内的显示顺序。</summary>
    public int SortOrder { get; set; }

    /// <summary>是否为内置命令(内置项通常不可删除或编辑)。</summary>
    public bool IsBuiltIn { get; set; }
}
