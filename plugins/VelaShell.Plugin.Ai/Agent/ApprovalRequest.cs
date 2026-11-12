namespace VelaShell.Plugin.Ai.Agent;

/// <summary>一次需要用户点头的操作。</summary>
/// <param name="Kind">工具名(<c>run_command</c> / <c>write_remote_file</c> / <c>mcp:服务器名</c> 等)。</param>
/// <param name="Detail">给用户看的详情(命令原文、文件路径+片段、MCP 参数)。</param>
/// <param name="RepeatKey">
/// "本次会话内总是允许"的记忆键;为 null 表示<b>不提供</b>这个选项。
/// 只有可重复、且重复起来语义稳定的操作才给键 —— 比如同一个命令名的只读排查命令;
/// 写文件、往终端里敲字这类每次目标都不同的,必须一次一问。
/// </param>
/// <param name="Origin">
/// 发起这次操作的子 Agent(<c>id · 角色</c>);主 Agent 自己发起的为 null。
/// 几个子任务并行时,审批卡靠它说清"是谁要动这个文件"。
/// </param>
public readonly record struct ApprovalRequest(string Kind, string Detail, string? RepeatKey = null, string? Origin = null)
{
    /// <summary>审批卡上显示的那段文字。</summary>
    public string Summary => Detail.Length > 0 ? $"{Kind}: {Detail}" : Kind;
}
