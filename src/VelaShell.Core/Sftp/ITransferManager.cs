using VelaShell.Core.Models;

namespace VelaShell.Core.Sftp;

/// <summary>
/// 执行文件传输的委托。由调用方(如 SftpService)注入,以执行实际的上传/下载工作。
/// </summary>
public delegate Task TransferExecutor(
    TransferTask task,
    IProgress<TransferProgress> progress,
    CancellationToken cancellationToken);

/// <summary>
/// 文件传输管理器接口,负责排队、并发调度与取消上传/下载任务。
/// </summary>
public interface ITransferManager : IDisposable
{
    /// <summary>
    /// 允许同时进行的最大传输任务数,超出的任务进入排队等待。
    /// </summary>
    int MaxConcurrentTransfers { get; set; }

    /// <summary>
    /// 当前正在执行的传输任务列表。
    /// </summary>
    IReadOnlyList<TransferTask> ActiveTransfers { get; }

    /// <summary>
    /// 当前处于排队等待中的传输任务列表。
    /// </summary>
    IReadOnlyList<TransferTask> QueuedTransfers { get; }

    /// <summary>
    /// 将一个传输任务加入队列,并在有空闲并发槽位时开始执行。
    /// </summary>
    Task QueueTransferAsync(TransferTask task, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按标识取消指定的传输任务(无论其处于排队还是执行中)。
    /// </summary>
    Task CancelTransferAsync(Guid transferId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按标识获取传输任务,不存在时返回 <c>null</c>。
    /// </summary>
    TransferTask? GetTransfer(Guid transferId);
}
