namespace VelaShell.Core.Models;

/// <summary>
/// 文件传输(上传/下载)任务的状态。
/// </summary>
public enum TransferStatus
{
    /// <summary>已加入队列,等待开始。</summary>
    Queued,

    /// <summary>正在传输中。</summary>
    InProgress,

    /// <summary>已成功完成。</summary>
    Completed,

    /// <summary>传输失败。</summary>
    Failed,

    /// <summary>已被取消。</summary>
    Cancelled
}
