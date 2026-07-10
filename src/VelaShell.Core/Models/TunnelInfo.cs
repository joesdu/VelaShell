namespace VelaShell.Core.Models;

public sealed class TunnelInfo
{
    public required Guid Id { get; init; }

    public required TunnelConfig Config { get; init; }

    public required TunnelStatus Status { get; set; }

    public required Guid SessionId { get; init; }

    public required DateTime CreatedAt { get; init; }

    public long BytesTransferred { get; set; }

    /// <summary>
    /// 最近一次转发通道错误(如目标拒绝连接)。隧道监听正常但每个连接都失败时,
    /// 界面靠它把问题暴露出来,而不是一直显示"运行中"。
    /// </summary>
    public string? LastError { get; set; }
}
