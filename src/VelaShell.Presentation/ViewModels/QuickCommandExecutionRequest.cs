namespace VelaShell.Presentation.ViewModels;

/// <summary>一次快捷命令执行请求;目标标识已在触发时完成快照。</summary>
public sealed class QuickCommandExecutionRequest(string commandText, IReadOnlyList<Guid> targetIds)
    : EventArgs
{
    /// <summary>需要发送到终端的命令正文。</summary>
    public string CommandText { get; } = commandText;

    /// <summary>触发运行时确定的终端标签标识快照。</summary>
    public IReadOnlyList<Guid> TargetIds { get; } = targetIds;
}
