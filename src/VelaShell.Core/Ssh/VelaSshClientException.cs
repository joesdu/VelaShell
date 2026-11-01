namespace VelaShell.Core.Ssh;

// 库中立的 SSH/SFTP 异常层级:Infrastructure 的包装器(当前 SshInterop)把具体库
// 的异常翻译成这些类型,Core/App 只依赖它们。更换底层库时只需在新包装器里做同样的翻译。
//
// 上层请**直接按类型匹配**(`ex is VelaSshAuthenticationException`),不要按类型名字符串。
// 历史教训:本文件早期的注释声称这些类型的简单名与 SSH.NET 一致,而实际带着 Vela 前缀,
// 从来就对不上;`MainWindowViewModel` 照着注释写了 `ex.GetType().Name == "SshAuthenticationException"`,
// 于是认证失败重试与分类错误提示全部静默失效,编译器也不会报错。更糟的是两个测试各自定义了
// 一个私有的、名叫 SshAuthenticationException 的假异常来迎合那套字符串匹配,测试因此长期全绿
// ——测的是实现的怪癖,不是真实行为。2026-07-22 已全部改为类型匹配。

/// <summary>SSH 客户端操作失败的基类(传输/协议/通道层错误)。</summary>
public class VelaSshClientException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>无法建立或维持 SSH 会话(网络中断、服务器关闭连接等)。</summary>
public class VelaSshConnectionException(string message, Exception? innerException = null) : VelaSshClientException(message, innerException);

/// <summary>主机指纹没有通过信任判定(用户拒绝、变更被阻断,或自动重连时没法问人)。</summary>
/// <remarks>
/// 单独一类,是因为自动重连要据此停下:这一类失败再试多少次结果都一样,只有用户手动重连、在弹窗里核对过才可能过。
/// 重协商途中换了主机密钥不在此列 —— 那是已经连上的会话被断开,仍是 <see cref="VelaSshConnectionException" />。
/// </remarks>
public class VelaSshHostKeyRejectedException(string message, Exception? innerException = null) : VelaSshConnectionException(message, innerException);

/// <summary>身份认证失败(用户名/密码/密钥错误)。</summary>
public class VelaSshAuthenticationException(string message, Exception? innerException = null) : VelaSshClientException(message, innerException);

/// <summary>Tailscale SSH 授权失败；只携带本地化安全消息，不保留服务端异常链或验证链接。</summary>
/// <param name="message">本地化的安全消息。</param>
/// <param name="requiresUserAction">
/// 是否要人来处理才可能过：出现过验证链接、或服务端明确拒绝。认证跳上的连接中断、没等到链接的超时
/// 与别的认证方式一样只是这一次没连上，后台照常退避重试，不暂停自动重连。
/// </param>
public class VelaSshTailscaleAuthenticationException(string message, bool requiresUserAction = true)
    : VelaSshAuthenticationException(message)
{
    /// <summary>后台重连是否应当暂停、等用户手动处理。</summary>
    public bool RequiresUserAction { get; } = requiresUserAction;
}

/// <summary>
/// 用户在认证交互(keyboard-interactive 的动态码框)里点了取消。
/// </summary>
/// <remarks>
/// 这是「不连了」,不是认证失败:上层要像取消一样安静地收场,而不是报错、再弹一遍凭据框。
/// <b>刻意不派生自 <see cref="OperationCanceledException" /></b> —— 调用方没取消却收到的取消,
/// 库与 <c>SshConnectionService</c> 一律当成内部计时器到点,会把它改判成「连接超时」。
/// </remarks>
public class VelaSshAuthenticationCancelledException(string message, Exception? innerException = null) : VelaSshClientException(message, innerException);

/// <summary>操作在配置的超时时间内未完成。</summary>
public class VelaSshOperationTimeoutException(string message, Exception? innerException = null) : VelaSshClientException(message, innerException);

/// <summary>SFTP 子系统操作失败的基类。</summary>
public class VelaSftpOperationException(string message, Exception? innerException = null) : VelaSshClientException(message, innerException);

/// <summary>SFTP 服务器返回权限拒绝(SSH_FX_PERMISSION_DENIED)。</summary>
public class VelaSftpPermissionDeniedException(string message, Exception? innerException = null) : VelaSftpOperationException(message, innerException);

/// <summary>
/// 写入中断(断线、服务端拒写、本端释放),带着**精确**的续传点:远端文件从开头起连续确认落盘了多少字节。
/// </summary>
/// <remarks>
/// 流水线写入的应答不按偏移顺序回来,中断时远端的文件长度只是「已确认的最高偏移」,它之前可能还有空洞;
/// <see cref="DurableLength" /> 才是从这里续传不会续出坏文件的那个位置。
/// </remarks>
public class VelaSftpTransferInterruptedException(string message, long durableLength, Exception? innerException = null)
    : VelaSftpOperationException(message, innerException)
{
    /// <summary>远端文件从开头起连续确认落盘的字节数。</summary>
    public long DurableLength { get; } = durableLength;

    /// <summary>
    /// 中断是服务端拒写(磁盘满、配额、权限,即 SFTP 状态码),而不是断线或本端释放。
    /// </summary>
    /// <remarks>
    /// 传输失败重试据此分流:断线换一次连接、等一会儿往往就过去了;服务端拒写再试几次结果都一样,
    /// 只是让用户多等几轮退避才看到「磁盘满」。原因只在库的异常链里,翻译时(<c>SshInterop</c>)定下来。
    /// </remarks>
    public bool RefusedByServer { get; init; }
}

/// <summary>
/// 上传主错误之外的目标收尾结果。附在原异常上,不改变源错误的类型、实例与重试分类。
/// </summary>
/// <param name="DurableLength">收尾之后目标从开头起连续收到 WRITE 成功确认的字节数。</param>
/// <param name="CleanupException">目标收尾的中立异常;收尾成功时为 null。不是本次上传的主错误。</param>
public sealed record VelaSftpUploadFailureDetails(long DurableLength, Exception? CleanupException)
{
    /// <summary>把目标结果附到主异常,主异常仍是调用方应处理的原因。</summary>
    public void AttachTo(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        exception.Data[typeof(VelaSftpUploadFailureDetails)] = this;
    }

    /// <summary>读取上传失败的目标收尾结果;不是此类失败时为 null。</summary>
    public static VelaSftpUploadFailureDetails? Get(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.Data[typeof(VelaSftpUploadFailureDetails)] as VelaSftpUploadFailureDetails;
    }
}

/// <summary>SFTP 路径不存在(SSH_FX_NO_SUCH_FILE)。</summary>
public class VelaSftpPathNotFoundException(string message, Exception? innerException = null) : VelaSftpOperationException(message, innerException);

/// <summary>
/// 断点续传的前置校验失败:已有的那半截文件并不是本次要传的文件的前缀
/// (同名不同内容,或续传探测之后目标被改写)。此时继续追加只会产出损坏的文件,
/// 因此中止并交由用户决定是否整份重传。
/// </summary>
public class VelaSftpResumeMismatchException(string message, Exception? innerException = null) : VelaSftpOperationException(message, innerException);

/// <summary>
/// 目标服务器根本续不了:不支持按偏移读写(FTP 服务器对 <c>REST</c> / <c>APPE</c> 回 500 / 502 / 504,命令不认识或没实现)。
/// 目标没有被这次调用碰过。
/// </summary>
/// <remarks>
/// 派生自 <see cref="VelaSftpResumeMismatchException" />:同样核实不了「目标上那半截是不是这个文件的开头」,
/// 只按它接的调用方照旧把它交回同名冲突策略 —— 悄悄整份重传会不经询问覆盖一个可能与此无关的文件。
/// 只有调用方确知那半截是这一次传输自己刚写下的(传输失败重试在动过字节之后续传)时,才可以直接从头整份重传。
/// </remarks>
public class VelaSftpResumeUnsupportedException(string message, Exception? innerException = null)
    : VelaSftpResumeMismatchException(message, innerException);
