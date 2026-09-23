// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/08-failures.md §1(原则)、§2(层级)、§5(带上下文的三个异常)

namespace VelaShell.Ssh.Diagnostics;

/// <summary>
/// 本库全部异常的基类。
/// </summary>
/// <remarks>
/// <para>
/// <b>异常里装数据，不装拼好的句子。</b><c>Message</c> 是给人看的，<b>不是 API</b>。
/// 要程序化判断就用 <see cref="Reason"/> 与 <see cref="Phase"/>，以及各派生类型的结构化字段。
/// </para>
/// <para>
/// 这一条不是洁癖：让调用方为了区分「认证失败 / 超时 / 协商失败」去切消息字符串，
/// 会在库改动一个字的措辞时静默失效 —— 而那种代码没有任何办法察觉自己已经坏了。
/// </para>
/// </remarks>
public abstract class SshException : Exception
{
    /// <summary>用给定原因、阶段与消息创建异常。</summary>
    protected SshException(SshFailureReason reason, SshPhase phase, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
        Phase = phase;
    }

    /// <summary>失败的分类。</summary>
    public SshFailureReason Reason { get; }

    /// <summary>在哪一步失败的。</summary>
    public SshPhase Phase { get; }

    /// <summary>
    /// 这个失败是否**可能**因为重试而消失。
    /// </summary>
    /// <remarks>
    /// 这是库给的**建议，不是承诺**：它表达「重试有没有意义」，
    /// 不表达「应该重试」—— 重试策略是使用者的事，只有他们知道用户在等还是在跑批。
    /// </remarks>
    public virtual bool IsRetryable => Reason is
        SshFailureReason.DnsFailure or
        SshFailureReason.TcpRefused or
        SshFailureReason.TcpTimeout or
        SshFailureReason.TcpUnreachable or
        SshFailureReason.ProxyRefused or
        SshFailureReason.Timeout or
        SshFailureReason.KeepAliveTimeout or
        SshFailureReason.ClosedByPeer or
        SshFailureReason.AuthenticationFailed;
}

/// <summary>建连阶段的失败（拨号、版本交换、协商、主机密钥）。</summary>
public class SshConnectException : SshException
{
    /// <summary>用给定原因、阶段与消息创建异常。</summary>
    public SshConnectException(SshFailureReason reason, SshPhase phase, string message, Exception? innerException = null)
        : base(reason, phase, message, innerException)
    {
    }

    /// <summary>
    /// 链路上每一跳的结果（代理、跳板、最终目标）。
    /// </summary>
    /// <remarks>
    /// 失败在哪一跳**必须能看出来**：「连不上跳板机 jump.example.com」与
    /// 「连不上 10.0.0.9」对用户是两个完全不同的问题，而没有这张表它们长得一模一样。
    /// </remarks>
    public IReadOnlyList<SshHopInfo> Hops { get; init; } = [];
}

/// <summary>链路上一跳的结果。</summary>
/// <param name="Kind">这一跳是怎么走的。</param>
/// <param name="Target">这一跳的目标。</param>
/// <param name="Succeeded">是否成功。</param>
/// <param name="Elapsed">耗时。</param>
/// <param name="Detail">失败时的补充说明。</param>
public readonly record struct SshHopInfo(
    Transport.SshDialKind Kind,
    string Target,
    bool Succeeded,
    TimeSpan Elapsed,
    string? Detail = null);

/// <summary>算法协商失败：某一类算法双方没有交集。</summary>
/// <remarks>
/// <para>
/// <b>异常自带双方的完整名单</b> —— 不需要再开一条连接去探对端支持什么。
/// 那份名单我们在握手时本来就收到了，只是常见的库没有交出来。
/// </para>
/// <para>
/// 展示时建议过滤掉 <c>*-cert-v01@openssh.com</c> 变体：它们只在对端出示证书时才用得上，
/// 列出来会把几个真正可用的算法淹没在十几行里。过滤在使用者一侧做，库照常给全量。
/// </para>
/// </remarks>
public sealed class SshNegotiationException : SshConnectException
{
    /// <summary>创建一个协商失败异常。</summary>
    public SshNegotiationException(
        SshNegotiationCategory category,
        IReadOnlyList<string> offeredByPeer,
        IReadOnlyList<string> offeredByUs,
        string peerVersion)
        : base(SshFailureReason.NegotiationFailed, SshPhase.KeyExchange, BuildMessage(category, offeredByPeer, offeredByUs))
    {
        Category = category;
        OfferedByPeer = offeredByPeer;
        OfferedByUs = offeredByUs;
        PeerVersion = peerVersion;
    }

    /// <summary>哪一类算法没有交集。</summary>
    public SshNegotiationCategory Category { get; }

    /// <summary>对端在 KEXINIT 里给出的该类名单，原样。</summary>
    public IReadOnlyList<string> OfferedByPeer { get; }

    /// <summary>我们发出去的该类名单，原样。</summary>
    public IReadOnlyList<string> OfferedByUs { get; }

    /// <summary>对端的版本标识串，例如 <c>SSH-2.0-OpenSSH_9.6p1</c>。</summary>
    public string PeerVersion { get; }

    private static string BuildMessage(
        SshNegotiationCategory category, IReadOnlyList<string> peer, IReadOnlyList<string> ours) =>
        $"{Describe(category)}协商失败：双方没有共同支持的算法。" +
        $"对端提供：{(peer.Count == 0 ? "(空)" : string.Join(", ", peer))}；" +
        $"本端支持：{(ours.Count == 0 ? "(空)" : string.Join(", ", ours))}。";

    private static string Describe(SshNegotiationCategory category) => category switch
    {
        SshNegotiationCategory.KeyExchange => "密钥交换算法",
        SshNegotiationCategory.HostKey => "主机密钥算法",
        SshNegotiationCategory.EncryptionClientToServer => "加密算法（客户端→服务端）",
        SshNegotiationCategory.EncryptionServerToClient => "加密算法（服务端→客户端）",
        SshNegotiationCategory.MacClientToServer => "完整性算法（客户端→服务端）",
        SshNegotiationCategory.MacServerToClient => "完整性算法（服务端→客户端）",
        SshNegotiationCategory.CompressionClientToServer => "压缩算法（客户端→服务端）",
        SshNegotiationCategory.CompressionServerToClient => "压缩算法（服务端→客户端）",
        _ => "算法",
    };
}

/// <summary>协商失败发生在哪一类算法上。</summary>
public enum SshNegotiationCategory
{
    /// <summary>密钥交换算法。</summary>
    KeyExchange,

    /// <summary>主机密钥算法。</summary>
    HostKey,

    /// <summary>加密算法（客户端 → 服务端）。</summary>
    EncryptionClientToServer,

    /// <summary>加密算法（服务端 → 客户端）。</summary>
    EncryptionServerToClient,

    /// <summary>完整性算法（客户端 → 服务端）。</summary>
    MacClientToServer,

    /// <summary>完整性算法（服务端 → 客户端）。</summary>
    MacServerToClient,

    /// <summary>压缩算法（客户端 → 服务端）。</summary>
    CompressionClientToServer,

    /// <summary>压缩算法（服务端 → 客户端）。</summary>
    CompressionServerToClient,
}

/// <summary>对端违反了协议。</summary>
/// <remarks>收到它必须断开连接，不得重试或继续读。</remarks>
public sealed class SshProtocolException : SshException
{
    /// <summary>创建一个协议错误异常。</summary>
    public SshProtocolException(SshPhase phase, string message, Exception? innerException = null)
        : base(SshFailureReason.ProtocolError, phase, message, innerException)
    {
    }
}

/// <summary>连接已断开。</summary>
public sealed class SshConnectionClosedException : SshException
{
    /// <summary>创建一个连接已断异常。</summary>
    public SshConnectionClosedException(SshFailureReason reason, SshPhase phase, string message, Exception? innerException = null)
        : base(reason, phase, message, innerException)
    {
    }

    /// <summary>收到 <c>SSH_MSG_DISCONNECT</c> 时对端给出的原因码。</summary>
    public SshDisconnectReason? DisconnectReason { get; init; }

    /// <summary>
    /// 对端给出的描述文本，原样保留。
    /// </summary>
    /// <remarks>
    /// 它常常是唯一有用的信息（<c>Too many authentication failures</c>、
    /// <c>No supported authentication methods available</c>）。
    /// <b>同时它是不可信文本</b>，展示时按不可信内容处理：不解释控制字符、不当作富文本。
    /// </remarks>
    public string? PeerDescription { get; init; }
}
