using Tmds.Ssh;
using VelaShell.Core.Ssh;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// Tmds.Ssh 与 Core 中立类型之间的异常翻译。库类型只在 Infrastructure 内流动;
/// 更换底层库时替换本文件的对应映射。
/// </summary>
internal static class TmdsSshInterop
{
    // Tmds.Ssh 的内部 ConnectFailedException.Message 格式:
    // "The connection could not be established - {ConnectFailedReason} - {description}"
    private const string ConnectFailedPrefix = "The connection could not be established - ";

    /// <summary>
    /// Tmds.Ssh 异常 → Core 中立异常;不认识的类型返回 null(调用方原样上抛)。
    /// 若异常是取消且 <paramref name="callerToken" /> 已请求取消,同样返回 null:
    /// 调用方主动取消必须保留 OperationCanceledException 语义,
    /// 只有库内部超时(调用方未取消)才翻译为 SshOperationTimeoutException。
    /// </summary>
    public static Exception? Translate(Exception ex, CancellationToken callerToken = default)
    {
        if (ex is OperationCanceledException && callerToken.IsCancellationRequested) return null;
        return ex switch
        {
            // Subtypes must be before base types to avoid unreachable pattern warnings
            SshChannelClosedException => new VelaSshClientException(ex.Message, ex),
            SshConnectionClosedException => new VelaSshConnectionException(ex.Message, ex),
            SshChannelException => new VelaSshClientException(ex.Message, ex),
            SshConnectionException sshEx => TranslateSshConnection(sshEx),
            SshOperationException => new VelaSshClientException(ex.Message, ex),
            SftpException => new VelaSftpOperationException(ex.Message, ex),
            OperationCanceledException => new VelaSshOperationTimeoutException(ex.Message, ex),
            SshException => new VelaSshClientException(ex.Message, ex),
            _ => null
        };
    }

    /// <summary>
    /// Tmds.Ssh 的内部 ConnectFailedException 包装了认证失败、密钥协商失败、超时等不同原因。
    /// 按 ConnectFailedReason 分别映射到对应的 Core 异常类型,
    /// 使主窗口的认证重试与错误提示能正常工作。
    /// </summary>
    private static VelaSshClientException TranslateSshConnection(SshConnectionException ex)
    {
        // ConnectFailedException 是 internal class,只能通过消息内容判断原因。
        // 消息格式: "The connection could not be established - {reason} - {description}"
        string msg = ex.Message;
        if (!msg.StartsWith(ConnectFailedPrefix, StringComparison.Ordinal))
        {
            return new VelaSshConnectionException(msg, ex);
        }

        string? reason = ExtractConnectFailedReason(msg);
        return reason switch
        {
            "AuthenticationFailed" => new VelaSshAuthenticationException(msg, ex),
            "Timeout" => new VelaSshOperationTimeoutException(msg, ex),
            _ => new VelaSshConnectionException(msg, ex)
        };
    }

    /// <summary>
    /// 从 ConnectFailedException 的消息中提取 ConnectFailedReason 标识。
    /// 格式: "The connection could not be established - {reason} - {description}"
    /// </summary>
    internal static string? ExtractConnectFailedReason(string message)
    {
        int prefixLen = ConnectFailedPrefix.Length;
        if (message.Length <= prefixLen) return null;
        int endOfReason = message.IndexOf(" - ", prefixLen, StringComparison.Ordinal);
        return endOfReason > prefixLen
            ? message[prefixLen..endOfReason]
            : null;
    }

    /// <summary>
    /// 从异常的消息或 InnerException 链中提取人类可读的连接失败原因。
    /// 首先尝试解析 ConnectFailedException 格式,
    /// 否则返回 InnerException.Message(存在时),最后退回到完整异常链的循环描述。
    /// </summary>
    public static string GetFailureDiagnostic(Exception ex)
    {
        string? reason = ExtractConnectFailedReason(ex.Message);
        if (reason is not null) return reason;

        // 遍历 InnerException 链,收集有意义的描述。
        var parts = new List<string>();
        Exception? current = ex;
        while (current is not null)
        {
            string? innerReason = ExtractConnectFailedReason(current.Message);
            if (innerReason is not null && !parts.Contains(innerReason))
            {
                parts.Add(innerReason);
            }
            else if (current != ex && current.Message is { Length: > 0 } msg && !parts.Contains(msg))
            {
                parts.Add(msg);
            }
            current = current.InnerException;
        }
        return parts.Count > 0 ? string.Join(" → ", parts) : ex.Message;
    }
}
