namespace VelaShell.Core.Ssh;

// 库中立的 SSH/SFTP 异常层级:Infrastructure 的包装器(当前 TmdsSshInterop)把具体库
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

/// <summary>身份认证失败(用户名/密码/密钥错误)。</summary>
public class VelaSshAuthenticationException(string message, Exception? innerException = null) : VelaSshClientException(message, innerException);

/// <summary>操作在配置的超时时间内未完成。</summary>
public class VelaSshOperationTimeoutException(string message, Exception? innerException = null) : VelaSshClientException(message, innerException);

/// <summary>SFTP 子系统操作失败的基类。</summary>
public class VelaSftpOperationException(string message, Exception? innerException = null) : VelaSshClientException(message, innerException);

/// <summary>SFTP 服务器返回权限拒绝(SSH_FX_PERMISSION_DENIED)。</summary>
public class VelaSftpPermissionDeniedException(string message, Exception? innerException = null) : VelaSftpOperationException(message, innerException);

/// <summary>SFTP 路径不存在(SSH_FX_NO_SUCH_FILE)。</summary>
public class VelaSftpPathNotFoundException(string message, Exception? innerException = null) : VelaSftpOperationException(message, innerException);

/// <summary>
/// 断点续传的前置校验失败:已有的那半截文件并不是本次要传的文件的前缀
/// (同名不同内容,或续传探测之后目标被改写)。此时继续追加只会产出损坏的文件,
/// 因此中止并交由用户决定是否整份重传。
/// </summary>
public class VelaSftpResumeMismatchException(string message, Exception? innerException = null) : VelaSftpOperationException(message, innerException);
