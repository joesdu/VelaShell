namespace VelaShell.Core.Models;

/// <summary>描述远端文件或目录的元数据信息。</summary>
public class RemoteFileInfo
{
    /// <summary>获取文件或目录的名称(不含路径)。</summary>
    public required string Name { get; init; }

    /// <summary>获取文件或目录的完整远端路径。</summary>
    public required string FullPath { get; init; }

    /// <summary>获取文件大小(字节)。</summary>
    public required long Size { get; init; }

    /// <summary>获取权限字符串(如 rwxr-xr-x)。</summary>
    public required string Permissions { get; init; }

    /// <summary>获取一个值,指示该条目是否为目录。</summary>
    public required bool IsDirectory { get; init; }

    /// <summary>获取最后修改时间。</summary>
    public required DateTime LastModified { get; init; }

    /// <summary>获取文件或目录的属主。</summary>
    public required string Owner { get; init; }

    /// <summary>获取文件或目录所属的用户组。</summary>
    public required string Group { get; init; }
}
