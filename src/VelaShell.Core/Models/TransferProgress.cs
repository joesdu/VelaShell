namespace VelaShell.Core.Models;

/// <summary>
/// 表示文件传输过程中的实时进度快照。
/// </summary>
public class TransferProgress
{
    /// <summary>
    /// 正在传输的文件名。
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>
    /// 已传输的字节数。
    /// </summary>
    public required long BytesTransferred { get; init; }

    /// <summary>
    /// 文件的总字节数。
    /// </summary>
    public required long TotalBytes { get; init; }

    /// <summary>
    /// 传输完成的百分比(0-100)。
    /// </summary>
    public required int Percentage { get; init; }

    /// <summary>
    /// 当前传输速度,单位为字节每秒。
    /// </summary>
    public required double SpeedBytesPerSecond { get; init; }

    /// <summary>
    /// 预计剩余传输时间。
    /// </summary>
    public required TimeSpan EstimatedTimeRemaining { get; init; }
}
