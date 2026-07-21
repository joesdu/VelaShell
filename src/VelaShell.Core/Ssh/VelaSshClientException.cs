namespace VelaShell.Core.Ssh;

// 库中立的 SSH/SFTP 异常层级:Infrastructure 的包装器把具体库(当前为 SSH.NET)的
// 异常翻译成这些类型,Core/App 只依赖它们。类型的**简单名**刻意与 SSH.NET 保持一致
// (SshAuthenticationException 等)——上层 MainWindowViewModel 按类型名匹配生成用户
// 提示,翻译层替换后无需改动。更换底层库时只需在新包装器里做同样的翻译。

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
