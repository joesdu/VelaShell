namespace VelaShell.Core.Models;

/// <summary>
/// 表示一个 SFTP 文件传输任务及其当前状态与进度。
/// </summary>
public class TransferTask
{
    /// <summary>
    /// 传输任务的唯一标识。
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// 传输方向(上传或下载)。
    /// </summary>
    public required TransferType Type { get; init; }

    /// <summary>
    /// 本地文件路径。
    /// </summary>
    public required string LocalPath { get; init; }

    /// <summary>
    /// 远程文件路径。
    /// </summary>
    public required string RemotePath { get; init; }

    /// <summary>
    /// 传输任务当前的状态。
    /// </summary>
    public required TransferStatus Status { get; set; }

    /// <summary>
    /// 传输的实时进度信息,尚未开始时为 <c>null</c>。
    /// </summary>
    public TransferProgress? Progress { get; set; }
}
