namespace VelaShell.Plugin.Ai.Agent;

/// <summary>文件改动的种类。</summary>
public enum FileChangeKind
{
    /// <summary>新建(改动前文件不存在)。</summary>
    Created,

    /// <summary>修改已有文件的内容。</summary>
    Modified,

    /// <summary>删除。</summary>
    Deleted,

    /// <summary>重命名或移动;<see cref="FileChange.OldPath" /> 是原路径。</summary>
    Renamed
}

/// <summary>
/// Agent 工具成功改动一个文件之后发出的一条记录,供聊天面板汇总「本轮改了哪些文件」并画差异。
/// </summary>
/// <param name="ToolName">发起改动的工具名(<c>patch_local_file</c> 等)。</param>
/// <param name="Location">
/// 文件所在的位置:本地为 <c>local:&lt;workspaceId&gt;</c>,远端为 <c>remote:&lt;sessionId&gt;</c>。
/// 与 <see cref="Path" /> 一起唯一确定一个文件。
/// </param>
/// <param name="Path">给人看的路径:本地是相对工作区的路径,远端是绝对路径。</param>
/// <param name="Kind">改动种类。</param>
/// <param name="Before">改动前的全文;新建、二进制或超出大小上限时为 <see langword="null" />。</param>
/// <param name="After">改动后的全文;删除、二进制或超出大小上限时为 <see langword="null" />。</param>
/// <param name="OldPath">重命名前的路径;只有 <see cref="FileChangeKind.Renamed" /> 才有。</param>
public sealed record FileChange(
    string ToolName,
    string Location,
    string Path,
    FileChangeKind Kind,
    string? Before,
    string? After,
    string? OldPath = null);
