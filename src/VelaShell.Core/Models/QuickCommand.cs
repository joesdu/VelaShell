namespace VelaShell.Core.Models;

/// <summary>快捷命令定义:一条可在终端快速插入/执行的预设命令及其分类与描述。</summary>
public class QuickCommand
{
    /// <summary>快捷命令的唯一标识符(创建时自动生成)。</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>快捷命令的显示名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>所属分类,用于在列表中分组归类。</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>实际要发送到终端执行的命令文本。</summary>
    public string CommandText { get; set; } = string.Empty;

    /// <summary>命令的说明文字,便于用户理解其用途。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>是否为内置命令(内置项通常不可删除或编辑)。</summary>
    public bool IsBuiltIn { get; set; }
}
