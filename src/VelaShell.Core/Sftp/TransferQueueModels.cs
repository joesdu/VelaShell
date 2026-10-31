using VelaShell.Core.Models;

namespace VelaShell.Core.Sftp;

/// <summary>一批传输是从哪条连接发起的:恢复时据此找回(或重新打开)那条连接。</summary>
/// <param name="ProfileId">连接配置的 Id。</param>
/// <param name="ServerName">面板上显示的服务器名(配置显示名,没有就是主机)。</param>
public sealed record TransferQueueOrigin(Guid ProfileId, string ServerName);

/// <summary>队列里的一项:方向、本地路径、远端路径。</summary>
/// <param name="Type">只会是 <see cref="TransferType.Upload" /> 或 <see cref="TransferType.Download" />。</param>
/// <param name="LocalPath">本地文件。</param>
/// <param name="RemotePath">远端文件。</param>
public readonly record struct TransferQueueItem(TransferType Type, string LocalPath, string RemotePath);

/// <summary>一批停下来的传输:还没落定的那些项,等用户点「继续」或「放弃」。</summary>
/// <param name="BatchId">队列里的批次 Id。</param>
/// <param name="Origin">发起它的连接。</param>
/// <param name="CreatedUtc">这一批最初开始的时刻。</param>
/// <param name="Items">还没落定的项,按原来的顺序。</param>
public sealed record PausedTransferBatch(
    Guid BatchId,
    TransferQueueOrigin Origin,
    DateTime CreatedUtc,
    IReadOnlyList<TransferQueueItem> Items);

/// <summary>
/// 传输队列快照的一份文档(SonnetDB 文档集合 <see cref="TransferQueueJournal.Collection" />):
/// 一批传输里还没落定的一段,最多 <see cref="TransferQueueJournal.ChunkSize" /> 项。
/// </summary>
/// <remarks>
/// 分段存,是因为一批可能有几万个文件:整批一份文档的话,每传完一个文件就要把几 MB 的 JSON 整份重写一遍;
/// 分段之后只重写正在传的那一两段。落定的项从段里拿掉;整段都落定了留一份空段占位,整批落定或作废时各段一起删掉 ——
/// 分段写不是一次原子写,读回时靠「每一段都在」认出写到一半(或删到一半)的批次(见 <see cref="ChunkCount" />)。
/// </remarks>
public sealed class TransferQueueChunk
{
    /// <summary>所属批次。</summary>
    public Guid BatchId { get; set; }

    /// <summary>第几段(从 0 起)。</summary>
    public int Chunk { get; set; }

    /// <summary>
    /// 这一批一共几段(登记时定下,每一段都带着)。读回时少了哪一段,就是这一批没写全或删到一半,整批不还原。
    /// 0 = 旧版本写的文档(没有这一项),缺的段照旧按「已经落定」算。
    /// </summary>
    public int ChunkCount { get; set; }

    /// <summary>发起这一批的连接配置。</summary>
    public Guid ProfileId { get; set; }

    /// <summary>服务器显示名(配置被删之后面板上仍能说清这是哪台机器的)。</summary>
    public string ServerName { get; set; } = string.Empty;

    /// <summary>这一批最初开始的时刻。</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>这一段里还没落定的项。</summary>
    public List<TransferQueueChunkEntry> Items { get; set; } = [];
}

/// <summary><see cref="TransferQueueChunk" /> 里的一项。</summary>
public sealed class TransferQueueChunkEntry
{
    /// <summary>在批次里的序号(决定恢复时的先后)。</summary>
    public int Index { get; set; }

    /// <summary>
    /// <c>upload</c> 或 <c>download</c>。刻意存字符串而不是枚举的数值:枚举哪天调了顺序,
    /// 数值读回来就会把上传读成下载 —— 那是拿远端文件去覆盖本地文件。认不出的项读取时丢弃。
    /// </summary>
    public string Direction { get; set; } = string.Empty;

    /// <summary>本地路径。</summary>
    public string LocalPath { get; set; } = string.Empty;

    /// <summary>远端路径。</summary>
    public string RemotePath { get; set; } = string.Empty;
}
