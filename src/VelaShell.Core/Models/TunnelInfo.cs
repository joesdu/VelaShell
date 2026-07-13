namespace VelaShell.Core.Models;

/// <summary>
/// 描述一条已创建的端口转发隧道的运行时信息。
/// </summary>
public sealed class TunnelInfo
{
    /// <summary>
    /// 隧道的唯一标识。
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// 隧道的配置(转发类型、监听地址、目标地址等)。
    /// </summary>
    public required TunnelConfig Config { get; init; }

    /// <summary>
    /// 隧道当前的运行状态。
    /// </summary>
    public required TunnelStatus Status { get; set; }

    /// <summary>
    /// 该隧道所属的 SSH 会话标识。
    /// </summary>
    public required Guid SessionId { get; init; }

    /// <summary>
    /// 隧道的创建时间。
    /// </summary>
    public required DateTime CreatedAt { get; init; }

    /// <summary>
    /// 通过该隧道累计转发的字节数。
    /// </summary>
    public long BytesTransferred { get; set; }

    /// <summary>
    /// 最近一次转发通道错误(如目标拒绝连接)。隧道监听正常但每个连接都失败时,
    /// 界面靠它把问题暴露出来,而不是一直显示"运行中"。
    /// </summary>
    public string? LastError { get; set; }
}
