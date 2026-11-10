namespace VelaShell.Plugin.Ai.Configuration;

/// <summary>供本地 Agent 使用的一个授权工作区。</summary>
public sealed class LocalWorkspace
{
    /// <summary>稳定 id,由设置页生成。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>显示名称。</summary>
    public string Name { get; set; } = "Workspace";

    /// <summary>工作区根目录,必须是绝对路径。</summary>
    public string RootPath { get; set; } = "";

    /// <summary>
    /// 旧版兼容字段。工作区一旦被选中即可读取；新界面不再提供单独开关。
    /// </summary>
    public bool AllowRead { get; set; } = true;

    /// <summary>
    /// 旧版兼容字段。Agent 模式下工作区默认允许写入，实际修改仍受审批模式控制。
    /// </summary>
    public bool AllowWrite { get; set; } = true;
}
