namespace VelaShell.Core.Models;

/// <summary>
/// SSH 会话连接状态
/// </summary>
public enum SessionStatus
{
    /// <summary>
    /// 会话已处于活动连接状态
    /// </summary>
    Connected,

    /// <summary>
    /// 会话正在连接过程中
    /// </summary>
    Connecting,

    /// <summary>
    /// 会话已断开连接
    /// </summary>
    Disconnected,

    /// <summary>
    /// 会话发生错误
    /// </summary>
    Error
}
