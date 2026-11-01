using FluentFTP;
using FluentFTP.Exceptions;
using VelaShell.Core.Ftp;

namespace VelaShell.Infrastructure.Ftp;

/// <summary>
/// FluentFTP 异常 → Core 的 <see cref="VelaFtpClientException" /> 族的一处翻译。
/// 与 SSH 侧的 <c>SshInterop</c> 同样的约定:具体库的异常类型不得越过 Infrastructure 边界
/// (见 docs/架构设计.md 的分层硬规则)。
/// </summary>
internal static class FluentFtpInterop
{
    /// <summary>把 FluentFTP/Socket 的异常翻译成库中立异常;已是中立异常的原样返回。</summary>
    public static Exception Translate(Exception ex, string operation)
    {
        return ex switch
        {
            VelaFtpClientException or OperationCanceledException => ex,
            FtpAuthenticationException auth => new VelaFtpAuthenticationException(
                                $"FTP login failed: {auth.CompletionCode} {auth.Message}", auth),
            FtpCommandException cmd => TranslateCommand(cmd, operation),
            FtpSecurityNotAvailableException tls => new VelaFtpConnectionException(
                                $"The server does not support the requested FTPS mode ({operation}).", tls),
            TimeoutException timeout => new VelaFtpConnectionException($"FTP {operation} timed out.", timeout),
            System.Net.Sockets.SocketException socket => new VelaFtpConnectionException($"FTP {operation} failed: {socket.Message}", socket),
            System.Security.Authentication.AuthenticationException tlsAuth => new VelaFtpConnectionException($"TLS handshake failed: {tlsAuth.Message}", tlsAuth),
            IOException io => new VelaFtpConnectionException($"FTP {operation} failed: {io.Message}", io),
            FtpException ftp => new VelaFtpOperationException($"FTP {operation} failed: {ftp.Message}", ftp),
            // FluentFTP 在**底层套接字已死**时会从内部抛出 NullReference / ObjectDisposed /
            // InvalidOperation —— 它并不总把断线包成 FtpException。不翻译的话,用户双击一个
            // 远端文件只会看到「Object reference not set to an instance of an object」
            // (实机反馈的「null 错误」就是它),既不知道发生了什么,也不知道该重连。
            NullReferenceException or ObjectDisposedException or InvalidOperationException =>
                new VelaFtpConnectionException(
                    $"FTP connection was lost during {operation}; reconnect and try again.", ex),
            _ => ex,
        };
    }

    /// <summary>该异常是否代表「连接已失效」——据此把会话标记为离线并丢弃池中的坏连接。</summary>
    public static bool IsConnectionLost(Exception ex) => ex is VelaFtpConnectionException;

    /// <summary>
    /// 服务器是不是在说「同一时刻只能有一个(连接/传输)」。
    /// <para>
    /// 用于把该会话就地收成单连接后重试一次 —— 用户报的现象是批量上传时
    /// 第一个文件成功、其余全失败(服务端只支持单线程上传)。
    /// </para>
    /// </summary>
    /// <remarks>
    /// 各家服务器的措辞与应答码都不统一,所以两条线一起看:
    /// 应答码 421(服务不可用/用户数超限)、425(开不了数据连接)、450(文件/传输忙),
    /// 以及中英文关键词。判错的代价很小:无非是这一项退化成排队重试一次;
    /// 判漏的代价才大 —— 用户看到的是一批失败的传输。
    /// </remarks>
    public static bool IsConcurrencyRejection(Exception? ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is FtpCommandException { CompletionCode: "421" or "425" or "450" })
            {
                return true;
            }
            if (current.Message is { Length: > 0 } message && MentionsConcurrencyLimit(message))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 数据连接刚建起来就被对方断掉,而且这条流一个字节都没读出来。
    /// </summary>
    /// <remarks>
    /// 不少服务器(默认 <c>max_per_ip</c> 很小的 vsftpd / proftpd、Windows IIS 的
    /// «同时连接的客户端数» 上限)在并发被拒时**不返回 425/450**,而是把数据连接直接复位 ——
    /// 客户端看到的只有一条 <see cref="System.Net.Sockets.SocketException" />(10054 / ConnectionReset)。
    /// 措辞与应答码都看不出「并发」,所以只能靠事实判断:RETR 之后立刻断、并且一个字节都没拿到。
    /// <para>
    /// 这也是「批量传输里成片的 <c>actual=0</c>」的成因:识别不出来的话,它只会被当成临时失败
    /// 反复重试,每次重试又开一条同样会被拒的连接。
    /// </para>
    /// </remarks>
    public static bool LooksLikeRejectedDataConnection(Exception? ex, bool receivedAnyBytes)
    {
        if (receivedAnyBytes)
        {
            return false;
        }
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is System.Net.Sockets.SocketException
                or FtpCommandException { CompletionCode: "425" or "426" or "450" or "451" })
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 一次传输尝试的失败,看上去是不是「数据连接中途断了」:服务器回 <c>426</c>(连接关闭、传输中止),
    /// 或者链上有套接字层的失败 / 超时。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只是候选:套接字失败也可能落在控制连接上。调用方还得问一句控制连接(<c>NOOP</c>)才能下结论 ——
    /// 应答的是数据连接中断,不应答的是会话掉线。
    /// </para>
    /// <para>
    /// 只按类型与应答码认,不看消息。链上若有别的 FTP 应答(<c>425</c> 开不了数据连接、<c>450</c> / <c>451</c> / <c>452</c> /
    /// <c>552</c> 拒写或磁盘满、<c>5xx</c> 不存在 / 没权限)、TLS 或登录失败,一律不算:那是服务器的明确答复,
    /// 不是断线 —— 磁盘满的服务器常常一边回 <c>452</c> 一边复位数据连接,两样都在链上,得按答复算。
    /// 没有套接字异常垫底的裸 <see cref="IOException" /> 也不算:本地文件读写失败就是这个形状。
    /// </para>
    /// <para>
    /// <paramref name="libraryResumeRefused" />:这次调用自己没发 REST,最后一条回复却是服务器拒了 REST
    /// (<see cref="IsResumeCommandRefused" />)—— 那是 FluentFTP 发现数据流断了、想按偏移续读,服务器不支持。
    /// 链上那条 <c>500</c> / <c>502</c> / <c>504</c> 是续读的结局、不是对这个文件的答复,不据此否决;
    /// 库会去续读,本身就说明数据连接中途断了(它只在读到提前的 EOF 或复位时才续),算作迹象。
    /// </para>
    /// </remarks>
    /// <param name="ex">这次尝试的失败。</param>
    /// <param name="libraryResumeRefused">FluentFTP 自己的续读被服务器以「不支持」拒掉(见上)。</param>
    public static bool LooksLikeDataChannelInterruption(Exception? ex, bool libraryResumeRefused = false)
    {
        bool evidence = libraryResumeRefused;
        var pending = new Stack<Exception>();
        if (ex is not null)
        {
            pending.Push(ex);
        }
        // 有界遍历:FluentFTP 的续写失败会把两条链装进一个 AggregateException。
        for (int visited = 0; visited < 32 && pending.TryPop(out Exception? current); visited++)
        {
            switch (current)
            {
                case FtpCommandException { CompletionCode: "426" }:
                    evidence = true;
                    break;
                case FtpCommandException { CompletionCode: "500" or "502" or "504" } when libraryResumeRefused:
                    break;
                case FtpCommandException or FtpAuthenticationException or FtpSecurityNotAvailableException
                    or System.Security.Authentication.AuthenticationException:
                    return false;
                case System.Net.Sockets.SocketException or TimeoutException:
                    evidence = true;
                    break;
            }
            if (current is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is { } nested)
            {
                pending.Push(nested);
            }
        }
        return evidence;
    }

    /// <summary>
    /// 服务器对续传要用的命令回「不认识 / 没实现」(<c>500</c> / <c>502</c> / <c>504</c>):<c>REST</c>(按偏移续读,
    /// 下载与续传起点核实用)或 <c>APPE</c>(追加写,续传上传用)。这台服务器续不了,与这个文件无关。
    /// </summary>
    /// <remarks>按 FluentFTP 给回复记下的命令认(<see cref="FtpReply.Command" />),不看消息。</remarks>
    public static bool IsResumeCommandRefused(FtpReply reply) =>
        reply is { Code: "500" or "502" or "504", Command: { } command }
        && (command.StartsWith("REST ", StringComparison.Ordinal) || command.StartsWith("APPE ", StringComparison.Ordinal));

    private static readonly string[] ConcurrencyKeywords =
    [
        "too many", "maximum number", "max clients", "connection limit", "already connected",
        "only one", "one transfer", "simultaneous", "concurrent", "busy", "try again later",
        "连接数", "同时", "并发", "超过最大", "已达上限",
    ];

    private static bool MentionsConcurrencyLimit(string message)
    {
        foreach (string keyword in ConcurrencyKeywords)
        {
            if (message.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 按 FTP 应答码分类。5xx 里 550 既可能是「不存在」也可能是「没权限」,
    /// 服务器措辞不统一,因此再看一眼文本 —— 分不清时保守地归为一般操作失败。
    /// </summary>
    private static Exception TranslateCommand(FtpCommandException cmd, string operation)
    {
        string message = cmd.Message ?? string.Empty;
        bool denied = message.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
                      message.Contains("access is denied", StringComparison.OrdinalIgnoreCase) ||
                      cmd.CompletionCode is "530" or "532";
        if (denied)
        {
            return new VelaFtpPermissionDeniedException($"FTP {operation} denied: {message}", cmd);
        }
        bool notFound = message.Contains("no such file", StringComparison.OrdinalIgnoreCase) ||
                        message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                        message.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
        return notFound
            ? new VelaFtpPathNotFoundException($"FTP {operation} failed: {message}", cmd)
            : new VelaFtpOperationException($"FTP {operation} failed: {message}", cmd);
    }
}
