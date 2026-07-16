using VelaShell.Core.ZModem.Model;

namespace VelaShell.Core.ZModem.Abstractions;

/// <summary>
/// 接收方文件落地目标:引擎每收到一个 ZFILE 就询问处置(接收 / 跳过 / 中止),
/// 随后把数据子包写入,并在文件结束或失败时收尾。实现方负责路径解析、覆盖策略与真正的磁盘 IO。
/// </summary>
public interface IZModemFileSink
{
    /// <summary>
    /// 发送方提供了一个文件(ZFILE)。返回处置决定;若接受,可通过 out 参数给出续传起始偏移
    /// (0 表示从头接收,>0 表示崩溃恢复续传)。
    /// </summary>
    /// <param name="metadata">解析出的文件元数据。</param>
    /// <param name="item">对应的传输项(实现可在此写入解析后的 LocalPath)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>处置决定与续传偏移。</returns>
    ValueTask<(ZModemFileDisposition Disposition, long ResumeOffset)> OnFileOfferedAsync(
        ZModemFileMetadata metadata,
        ZModemTransferItem item,
        CancellationToken cancellationToken);

    /// <summary>把一段已校验的文件数据写入目标。</summary>
    /// <param name="item">当前文件项。</param>
    /// <param name="data">已反转义并通过 CRC 校验的数据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask WriteAsync(ZModemTransferItem item, ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>当前文件全部数据接收完毕(ZEOF),收尾并落盘。</summary>
    /// <param name="item">当前文件项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask CompleteAsync(ZModemTransferItem item, CancellationToken cancellationToken);

    /// <summary>当前文件失败(协议错误 / IO 错误 / 取消),清理半成品。</summary>
    /// <param name="item">当前文件项。</param>
    /// <param name="error">导致失败的异常;取消时可为 <c>null</c>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask FailAsync(ZModemTransferItem item, Exception? error, CancellationToken cancellationToken);
}
