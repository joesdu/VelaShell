using System.Net.Sockets;
using VelaShell.Core.Ftp;
using VelaShell.Core.Protocols;
using VelaShell.Core.Ssh;

namespace VelaShell.Core.Sftp;

/// <summary>一次传输失败属于哪一类,决定要不要重试、要不要留到以后接着传。</summary>
public enum TransferFailureKind
{
    /// <summary>
    /// 这个文件本身的问题,换几次、换连接都一样:权限拒绝、路径不存在、磁盘满、服务端拒写、
    /// 认证 / 指纹不过、本地文件读不了。直接失败。
    /// </summary>
    Permanent,

    /// <summary>
    /// 链路上的临时问题:断线、超时、通道被关、FTP 数据连接传到一半断了(控制连接还在)。等一会儿在原会话上再试往往就过去了。
    /// </summary>
    Transient,

    /// <summary>
    /// 会话已经不在了(断开、关掉)。在原会话上再试没有意义 —— 会话 id 不会复活 —— 但换一条连接就能接着传,
    /// 所以也不算这个文件失败。
    /// </summary>
    SessionLost,
}

/// <summary>
/// 传输失败重试(设置 → 文件传输 → 失败自动重试)的口径:哪些失败值得重试、重试前等多久、次数上限。
/// </summary>
/// <remarks>
/// <para>
/// <b>按类型认,不按消息认。</b>各协议的异常在 Infrastructure 里已经翻成 Core 的中立异常族
/// (<c>SshInterop</c>、FTP 与插件协议各自的翻译层),这里只看那几个类型;认不出的沿内层异常链往下找,
/// 到底还认不出就当<b>不可重试</b> —— 把一个永久性错误重试几轮,用户要多等几十秒才看到真正的原因,
/// 而漏重试一个临时错误,最坏也就是像改动前一样直接失败、面板上点一下「重试」。
/// </para>
/// <para>
/// 续传下载与双栏中转是调用方自己在循环里读远端文件流:那条流上的失败同样在 Infrastructure 里翻成中立类型
/// (<c>VelaSftpClientWrapper.OpenAsync</c> 交出的流包了一层翻译),这里一样认得出断线。
/// </para>
/// </remarks>
public static class TransferRetryPolicy
{
    /// <summary>重试次数的上限(设置页 <c>NumericUpDown</c> 与 <c>AppSettings.Normalize</c> 同一口径)。</summary>
    public const int MaxRetries = 10;

    /// <summary>两次尝试之间最长等多久。</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    // Windows 的 ERROR_HANDLE_DISK_FULL / ERROR_DISK_FULL / ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION
    // (HRESULT 形式),Unix 上 .NET 把 errno 原样放进 HResult(ENOSPC = 28,ENOSPC 在 Linux 与 macOS 上同值)。
    private const int HResultHandleDiskFull = unchecked((int)0x80070027);
    private const int HResultDiskFull = unchecked((int)0x80070070);
    private const int HResultSharingViolation = unchecked((int)0x80070020);
    private const int HResultLockViolation = unchecked((int)0x80070021);
    private const int ErrnoNoSpace = 28;

    /// <summary>把设置里的次数收进有效区间(0 = 不重试)。</summary>
    public static int Clamp(int configured) => Math.Clamp(configured, 0, MaxRetries);

    /// <summary>
    /// 第 <paramref name="retry" /> 次重试之前等多久:1 s、2 s、4 s……翻倍,封顶 <see cref="MaxDelay" />。
    /// </summary>
    /// <remarks>
    /// 断线之后立刻重连多半还是连不上(路由在收敛、对端在重启),紧着重试只会把几次机会在一秒内用光;
    /// 等太久又让一次短暂的抖动拖慢整批。翻倍退避是两头的折中,与 SSH 自动重连同一个思路。
    /// </remarks>
    public static TimeSpan DelayBeforeRetry(int retry)
    {
        if (retry < 1)
        {
            return TimeSpan.Zero;
        }
        double seconds = Math.Pow(2, Math.Min(retry - 1, 16));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxDelay.TotalSeconds));
    }

    /// <summary>判断一次传输失败属于哪一类(见 <see cref="TransferFailureKind" />)。</summary>
    public static TransferFailureKind Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (ClassifyOne(current) is { } kind)
            {
                return kind;
            }
        }
        return TransferFailureKind.Permanent;
    }

    /// <summary>认得出就给结论,认不出返回 null(由调用方继续看内层异常)。</summary>
    private static TransferFailureKind? ClassifyOne(Exception ex) => ex switch
    {
        // 用户取消不该走到这里;万一走到了,绝不能把取消当成可以重来的失败。
        OperationCanceledException or RemoteRelayCommitException => TransferFailureKind.Permanent,

        AggregateException aggregate => ClassifyAggregate(aggregate),

        // 会话没了:原会话上不必再试,但这个文件可以换一条连接接着传。
        // ObjectDisposedException:会话关闭时 SFTP 客户端与单飞闸被释放,在途的操作以「已释放」收场。
        RemoteSessionUnavailableException or ObjectDisposedException => TransferFailureKind.SessionLost,

        // —— SSH / SFTP(派生类型放在基类型前面)。
        VelaSshHostKeyRejectedException => TransferFailureKind.Permanent,
        VelaSshAuthenticationException or VelaSshAuthenticationCancelledException => TransferFailureKind.Permanent,
        VelaSftpTransferInterruptedException interrupted =>
            interrupted.RefusedByServer ? TransferFailureKind.Permanent : TransferFailureKind.Transient,
        // 其余 SFTP 状态码(权限、不存在、续传核实对不上、Failure 里的「磁盘满」「配额」)都是服务端的答复。
        VelaSftpOperationException => TransferFailureKind.Permanent,
        VelaSshConnectionException or VelaSshOperationTimeoutException => TransferFailureKind.Transient,
        // 基类本身 = 传输 / 协议 / 通道层的错误(通道被对端关掉、SFTP 子系统中途退出)。
        VelaSshClientException => TransferFailureKind.Transient,

        // —— FTP。证书没被信任是连接异常的子类,但要人来点「信任」才过得去。
        VelaFtpCertificateException or VelaFtpAuthenticationException => TransferFailureKind.Permanent,
        VelaFtpConnectionException => TransferFailureKind.Transient,
        // 传输中途数据连接断了、控制连接还应答:会话没掉线,这一个文件值得再试(续传与否照常由续传探测与起点核实定)。
        VelaFtpTransferInterruptedException => TransferFailureKind.Transient,
        VelaFtpClientException => TransferFailureKind.Permanent,

        // —— 插件的文件协议(S3 之类)。
        PluginProtocolConnectionException => TransferFailureKind.Transient,
        PluginProtocolAuthenticationException or PluginProtocolCertificateException or PluginProtocolUnavailableException =>
            TransferFailureKind.Permanent,

        // —— 传输开始之后才发现源与元数据对不上:重试几次还是同一份数据,别再拿几次机会去撞同一堵墙。
        // 以前落进下面的裸 IOException 兜底被当成「临时」,于是同一份文件连传四次、每次都在同一个字节停下
        // (传输日志里成片的 source-length),用户看到的就是「大量失败 + 大量重试」。
        RemoteTransferLengthException => TransferFailureKind.Permanent,

        // —— 网络层。
        TimeoutException or SocketException => TransferFailureKind.Transient,

        // —— 本地中转缓存错误不是链路故障,即使内层看似可重试也不能重复下载撞磁盘。
        RelayCacheException => TransferFailureKind.Permanent,

        // —— 本地文件系统。
        UnauthorizedAccessException => TransferFailureKind.Permanent,
        FileNotFoundException or DirectoryNotFoundException or PathTooLongException or DriveNotFoundException =>
            TransferFailureKind.Permanent,
        IOException io when IsDiskFullOrLocked(io) => TransferFailureKind.Permanent,
        // 有内层的 IOException(典型是包着 SocketException 的「无法从传输连接读取数据」)交给内层判断。
        IOException { InnerException: not null } => null,
        // 光秃秃的 IOException 多半来自流:读到一半断掉、UNC 共享暂时不可达。具体的本地文件错误上面都有专门的类型。
        IOException => TransferFailureKind.Transient,

        _ => null,
    };

    private static TransferFailureKind? ClassifyAggregate(AggregateException aggregate)
    {
        // 并发的几路里只要有一路是永久性错误,重试就没有意义。
        TransferFailureKind? verdict = null;
        foreach (Exception inner in aggregate.Flatten().InnerExceptions)
        {
            TransferFailureKind kind = Classify(inner);
            if (kind == TransferFailureKind.Permanent)
            {
                return kind;
            }
            verdict ??= kind;
        }
        return verdict;
    }

    // 28 不分平台判:Windows 上的 IOException 带的是 0x8007xxxx 形式的 HRESULT,不会撞上这个小正数。
    private static bool IsDiskFullOrLocked(IOException io) =>
        io.HResult is HResultDiskFull or HResultHandleDiskFull or HResultSharingViolation or HResultLockViolation or ErrnoNoSpace;
}
