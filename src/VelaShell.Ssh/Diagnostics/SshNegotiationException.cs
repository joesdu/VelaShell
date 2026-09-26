// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/08-failures.md §1(原则)、§2(层级)、§5(带上下文的三个异常)

namespace VelaShell.Ssh.Diagnostics;

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
