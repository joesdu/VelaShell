// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/06-sftp.md §3.3、§6.2、§九

using VelaShell.Ssh.Sftp;

namespace VelaShell.Ssh.Diagnostics;

/// <summary>SFTP 操作失败。</summary>
/// <remarks>
/// <para>
/// <b><see cref="ServerMessage"/> 是这个类型存在的理由。</b>
/// </para>
/// <para>
/// SFTP v3 只有 9 个状态码，而码 <c>4</c>（<c>FAILURE</c>）承载了绝大多数真实错误 ——
/// 「目录非空」「文件已存在」「磁盘满」「配额超限」在 v3 里<b>全是同一个码</b>。
/// 服务端给的那段文本是唯一能区分它们的信息，所以它必须原样留着，
/// 而不是被我们拼进消息里就丢掉。上层要做模式匹配时用这个字段，
/// 不必去解析我们组织过的句子。
/// </para>
/// </remarks>
public sealed class SftpException : SshException
{
    /// <summary>创建一个 SFTP 异常。</summary>
    public SftpException(
        SftpStatusCode statusCode,
        string serverMessage,
        string? path = null,
        string? operation = null,
        Exception? innerException = null)
        : base(MapReason(statusCode), SshPhase.Open, BuildMessage(statusCode, serverMessage, path, operation), innerException)
    {
        StatusCode = statusCode;
        ServerMessage = serverMessage;
        Path = path;
        Operation = operation;
    }

    /// <summary>服务端给的状态码。</summary>
    public SftpStatusCode StatusCode { get; }

    /// <summary>
    /// 服务端给的**原始**文本，未经任何加工。
    /// </summary>
    /// <remarks>
    /// 可能是空串（有些服务端只给码不给文本），
    /// 也是<b>不可信文本</b> —— 展示时按不可信内容处理。
    /// </remarks>
    public string ServerMessage { get; }

    /// <summary>出问题的路径（如果这个操作有路径的话）。</summary>
    public string? Path { get; }

    /// <summary>出问题的操作名。</summary>
    public string? Operation { get; }

    /// <summary>是不是「文件不存在」。</summary>
    public bool IsNotFound => StatusCode == SftpStatusCode.NoSuchFile;

    /// <summary>是不是「权限不足」。</summary>
    public bool IsAccessDenied => StatusCode == SftpStatusCode.PermissionDenied;

    /// <summary>服务端说不支持这个操作。</summary>
    public bool IsUnsupported => StatusCode == SftpStatusCode.OperationUnsupported;

    private static SshFailureReason MapReason(SftpStatusCode code) => code switch
    {
        SftpStatusCode.NoConnection or SftpStatusCode.ConnectionLost => SshFailureReason.ClosedByPeer,
        SftpStatusCode.BadMessage => SshFailureReason.ProtocolError,
        SftpStatusCode.OperationUnsupported => SshFailureReason.Unsupported,
        _ => SshFailureReason.Unknown,
    };

    private static string BuildMessage(
        SftpStatusCode code, string serverMessage, string? path, string? operation)
    {
        string what = code switch
        {
            SftpStatusCode.NoSuchFile => "文件或目录不存在",
            SftpStatusCode.PermissionDenied => "权限不足",
            SftpStatusCode.OperationUnsupported => "服务端不支持这个操作",
            SftpStatusCode.BadMessage => "服务端说我们发的报文格式不对",
            SftpStatusCode.NoConnection or SftpStatusCode.ConnectionLost => "连接已断开",

            // 码 4 在 v3 里什么都可能是。**不要在这里猜**，
            // 把服务端的原话摆出来比我们编一个具体原因有用得多。
            SftpStatusCode.Failure => "操作失败",
            _ => $"SFTP 错误（状态码 {(uint)code}）",
        };

        string prefix = operation is null ? what : $"{operation} 失败：{what}";

        // 服务端的原话与路径（可能来自服务端的目录列表）进消息之前先清一遍 —— 见 PeerText。
        // 原话照样留在 ServerMessage 里。
        string withPath = path is null ? prefix : $"{prefix}（{PeerText.Sanitize(path)}）";

        return string.IsNullOrWhiteSpace(serverMessage)
            ? withPath + "。"
            : $"{withPath}。服务端说：{PeerText.Sanitize(serverMessage)}";
    }
}

/// <summary>传输被中断，但我们知道**确切**已经落盘了多少。</summary>
/// <remarks>
/// <para>
/// 这是 <see cref="DurableLength"/> 存在的全部意义。
/// </para>
/// <para>
/// 流水线满载时有 N 个在途 <c>WRITE</c>，而<b>应答顺序不保证</b>。
/// 中途断开时，服务端报告的文件长度只代表「已确认的<b>最高</b>偏移」，
/// 它之前可能留着读作 0 的空洞。所以不能拿文件长度当续传起点。
/// </para>
/// <para>
/// 常见做法是从文件长度<b>盲退一个完整的在途窗口</b>（比如 2 MiB），
/// 代价是每次续传都要重传那么多，而且那个数字是猜的 ——
/// 换个实现或换个配置就不对了。
/// </para>
/// <para>
/// <see cref="DurableLength"/> 是「从 0 开始连续已确认」的最高偏移，
/// <b>精确，不用猜</b>。从这个数续传即可。
/// </para>
/// </remarks>
public sealed class SftpTransferInterruptedException : SshException
{
    /// <summary>创建一个传输中断异常。</summary>
    public SftpTransferInterruptedException(
        long durableLength, string message, Exception? innerException = null)
        : base(SshFailureReason.ClosedByPeer, SshPhase.Open, message, innerException) =>
        DurableLength = durableLength;

    /// <summary>
    /// 从 0 开始<b>连续</b>已确认的字节数。断点续传从这里续。
    /// </summary>
    public long DurableLength { get; }
}

/// <summary>SFTP 子系统起不来。</summary>
public sealed class SftpUnavailableException : SshException
{
    /// <summary>创建一个 SFTP 不可用异常。</summary>
    public SftpUnavailableException(string message, Exception? innerException = null)
        : base(SshFailureReason.Unsupported, SshPhase.Open, message, innerException)
    {
    }
}
