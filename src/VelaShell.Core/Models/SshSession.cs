using ReactiveUI;

namespace VelaShell.Core.Models;

/// <summary>
/// 表示一个活动的 SSH 会话
/// </summary>
public class SshSession : ReactiveObject
{
/// <summary>
/// 获取会话的唯一标识符
/// </summary>
    public Guid SessionId { get; init; } = Guid.NewGuid();

/// <summary>
/// 获取连接信息
/// </summary>
    public required ConnectionInfo ConnectionInfo { get; init; }

/// <summary>
/// 获取或设置会话状态
/// </summary>
    public SessionStatus Status
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

/// <summary>
/// 获取或设置错误消息(当 Status 为 Error 时)
/// </summary>
    public string? ErrorMessage
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

/// <summary>
/// 获取会话创建时的时间戳
/// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

/// <summary>
/// 获取会话最近一次连接时的时间戳
/// </summary>
    public DateTime? ConnectedAt { get; set; }
}
