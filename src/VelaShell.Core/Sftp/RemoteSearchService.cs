using System.Text;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;

namespace VelaShell.Core.Sftp;

/// <summary>一次远端搜索是怎么收场的。</summary>
public enum RemoteSearchStatus
{
    /// <summary>跑完了,结果是全的。</summary>
    Completed,

    /// <summary>到了条数上限就停了,结果不全。</summary>
    Truncated,

    /// <summary>到了时间上限就停了,结果不全。</summary>
    TimedOut,

    /// <summary>用户停的。</summary>
    Cancelled,

    /// <summary>会话已经不在(或不是 SSH 会话)。</summary>
    NoSession,

    /// <summary>远端登录 shell 不认 sh 语法(Windows 的 cmd.exe / PowerShell、csh 一类):一条命令都没发。</summary>
    NotPosix,

    /// <summary>判定不了远端的 shell(exec 被禁、只开了 SFTP 的账号、超时):一条命令都没发。</summary>
    ShellUnknown,

    /// <summary>远端没有 <c>find</c>(或按内容找时没有 <c>grep</c>)。</summary>
    ToolMissing,

    /// <summary>出错了,原因在 <see cref="RemoteSearchOutcome.Detail" />。</summary>
    Failed,
}

/// <summary>一次远端搜索的结论。</summary>
/// <param name="Status">怎么收场的。</param>
/// <param name="Count">交出去了几条。</param>
/// <param name="Detail">出错时的原因(原文,给界面直接显示)。</param>
public sealed record RemoteSearchOutcome(RemoteSearchStatus Status, int Count, string? Detail = null);

/// <summary>远端搜索(<c>find</c> / <c>grep</c>):文件面板的「搜索」窗口用它。</summary>
public interface IRemoteSearchService
{
    /// <summary>这个会话能不能搜 —— 要有 SSH 连接(FTP 与插件协议没有 exec 通道)。不发任何东西。</summary>
    /// <param name="sessionId">会话标识。</param>
    bool IsAvailable(Guid sessionId);

    /// <summary>搜一次;每搜到一条回调一次(在 I/O 线程上,回调完成前不读下一行)。</summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="query">查询。</param>
    /// <param name="onHit">逐条回调。</param>
    /// <param name="cancellationToken">取消即停(远端进程一并收掉)。</param>
    Task<RemoteSearchOutcome> SearchAsync(
        Guid sessionId,
        RemoteSearchQuery query,
        Func<RemoteSearchHit, ValueTask> onHit,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 经会话现有 SSH 连接的独立 exec 通道跑一条 <c>find</c>(按内容时 <c>find … -exec grep</c>),不自己遍历目录。
/// </summary>
/// <remarks>
/// <para>
/// <b>先判 POSIX,判否就一条命令都不发。</b>与资源监控、任务管理器同一条教训(<c>plan.md</c> §18-B/E):
/// Windows OpenSSH 的 cmd.exe 会把整行命令原样回显,解析出来就是一张「什么都没找到」的假结果表。
/// 结论由 <see cref="RemoteShellProbe" /> 按主机缓存;fish 也收(命令行按 fish 的引号规则写,脚本本体交给 <c>sh</c>)。
/// </para>
/// <para>
/// <b>时间上限</b>是 <see cref="DefaultTimeLimit" />:到点就停、交出已经找到的那些,并如实说「结果不完整」。
/// 不做成设置项 —— 一次搜索要找多久是这次的事,真要更久就把起点目录缩小。
/// </para>
/// </remarks>
/// <param name="connections">SSH 连接服务。</param>
/// <param name="timeLimit">时间上限;null = <see cref="DefaultTimeLimit" />(测试里调短)。</param>
public sealed class RemoteSearchService(ISshConnectionService connections, TimeSpan? timeLimit = null) : IRemoteSearchService
{
    /// <summary>一次搜索最多跑多久。</summary>
    public static readonly TimeSpan DefaultTimeLimit = TimeSpan.FromMinutes(2);

    private readonly ISshConnectionService _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    private readonly TimeSpan _timeLimit = timeLimit ?? DefaultTimeLimit;

    /// <inheritdoc />
    public bool IsAvailable(Guid sessionId) => _connections.GetClient(sessionId) is not null;

    /// <inheritdoc />
    public async Task<RemoteSearchOutcome> SearchAsync(
        Guid sessionId,
        RemoteSearchQuery query,
        Func<RemoteSearchHit, ValueTask> onHit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(onHit);
        if (_connections.GetClient(sessionId) is not { IsConnected: true } client)
        {
            return new(RemoteSearchStatus.NoSession, 0);
        }
        ConnectionInfo? info = _connections.GetSession(sessionId)?.ConnectionInfo;
        RemoteShellKind shell = await RemoteShellProbe
            .DetectAsync(client, RemoteShellProbe.CacheKey(info?.Host, info?.Port ?? 22, info?.Username), cancellationToken)
            .ConfigureAwait(false);
        switch (shell)
        {
            case RemoteShellKind.NonPosix:
                return new(RemoteSearchStatus.NotPosix, 0);
            case RemoteShellKind.Unknown:
                // 探针被取消也落在这里:那是用户停的,不是「判定不了」。
                return new(cancellationToken.IsCancellationRequested ? RemoteSearchStatus.Cancelled : RemoteSearchStatus.ShellUnknown, 0);
        }

        // 文件名按会话编码(与 SFTP 文件名同一个来源):GBK 服务器上的中文名才显示得对、点了选得中那一行。UTF-8 会话照旧。
        Encoding? fileNameEncoding = RemoteFileNameEncoding.Resolve(info?.FileNameEncoding);
        string command = RemoteSearch.BuildCommand(query, shell, fileNameEncoding);
        string root = RemoteSearch.NormalizeRoot(query.Root);
        using var timeout = new CancellationTokenSource(_timeLimit);
        // 收满就停:远端的 head 收满会退出,可 find 是经 -exec 的批处理 sh 往外写的 —— 批处理 sh 死于 SIGPIPE,find 本身照样把整棵树走完、
        // 按内容找时 grep 照样把剩下的文件读完,主 sh 在 wait 里等着它们,通道一直不到 EOF(复查 file-panes F1)。
        // 所以第「上限 + 1」条一到就从这一侧取消,与用户点「停止」同一条路:库先给远端发 TERM,脚本的 trap 把整个进程组收掉。
        using var full = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token, full.Token);
        int count = 0;
        bool truncated = false;
        bool toolMissing = false;
        try
        {
            // 按原始字节、只以换行分记录:按行解码会按 UTF-8 解、还把名字里的 \r 当成行尾(见 RemoteSearch 的类型说明)。
            RemoteCommandStreamResult result = await client.StreamCommandRecordsAsync(
                command,
                (byte)'\n',
                async bytes =>
                {
                    if (truncated)
                    {
                        // 已经喊停了,缓冲区里剩下的几条不再看。
                        return;
                    }
                    string line = RemoteSearch.DecodeRecord(bytes.Span, fileNameEncoding);
                    if (RemoteSearch.IsToolMissingMarker(line))
                    {
                        toolMissing = true;
                        return;
                    }
                    if (!RemoteSearch.TryParseHit(line, root, out RemoteSearchHit hit))
                    {
                        return;
                    }
                    // 脚本多要了一条:收到第「上限 + 1」条只说明还有,不交出去。
                    if (count >= query.MaxResults)
                    {
                        truncated = true;
                        await full.CancelAsync().ConfigureAwait(false);
                        return;
                    }
                    count++;
                    await onHit(hit).ConfigureAwait(false);
                },
                linked.Token).ConfigureAwait(false);
            if (toolMissing)
            {
                return new(RemoteSearchStatus.ToolMissing, count);
            }
            // 连 sh 都没有(127):同样是缺工具,而不是「什么都没找到」。
            if (result.ExitCode == 127 && count == 0)
            {
                return new(RemoteSearchStatus.ToolMissing, 0);
            }
            return new(truncated ? RemoteSearchStatus.Truncated : RemoteSearchStatus.Completed, count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(RemoteSearchStatus.Cancelled, count);
        }
        catch (OperationCanceledException) when (truncated)
        {
            // 是收满了自己停的:如实说「到了上限」,而不是等两分钟后报「超时」。
            return new(RemoteSearchStatus.Truncated, count);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return new(RemoteSearchStatus.TimedOut, count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(RemoteSearchStatus.Failed, count, ex.Message);
        }
    }
}
