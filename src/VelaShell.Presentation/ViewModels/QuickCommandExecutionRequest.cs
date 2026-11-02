namespace VelaShell.Presentation.ViewModels;

/// <summary>一次快捷命令执行请求;目标标识已在触发时完成快照。</summary>
public sealed class QuickCommandExecutionRequest(
    string commandText,
    IReadOnlyList<Guid> targetIds,
    string? commandName = null,
    Guid? commandId = null,
    bool executeAfterSend = false)
    : EventArgs
{
    /// <summary>需要发送到终端的命令正文(可能含 <c>{{变量}}</c> 占位,由宿主在发送前询问并替换)。</summary>
    public string CommandText { get; } = commandText;

    /// <summary>本次发送正文后是否按回车。</summary>
    public bool ExecuteAfterSend { get; } = executeAfterSend;

    /// <summary>触发运行时确定的终端标签标识快照。</summary>
    public IReadOnlyList<Guid> TargetIds { get; } = targetIds;

    /// <summary>快捷命令的显示名称;询问变量时作弹窗标题。</summary>
    public string? CommandName { get; } = commandName;

    /// <summary>快捷命令的 id;询问框按它记住上一次填的值(没有时按命令正文记)。</summary>
    public Guid? CommandId { get; } = commandId;
}
