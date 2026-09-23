// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §7.1  协商规则
//   RFC 5647       AEAD 加密下 MAC 列表的协商结果被忽略
//   行为规格:      velashell-docs/zh/ssh/spec/03-key-exchange.md §2.2

using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Crypto;

/// <summary>一次协商的结果。</summary>
/// <param name="KeyExchange">密钥交换算法。</param>
/// <param name="HostKey">主机密钥算法（同时也是签名算法名）。</param>
/// <param name="EncryptionClientToServer">加密算法（客户端 → 服务端）。</param>
/// <param name="EncryptionServerToClient">加密算法（服务端 → 客户端）。</param>
/// <param name="MacClientToServer">完整性算法（客户端 → 服务端）；AEAD 下为 <see langword="null"/>。</param>
/// <param name="MacServerToClient">完整性算法（服务端 → 客户端）；AEAD 下为 <see langword="null"/>。</param>
/// <param name="CompressionClientToServer">压缩算法（客户端 → 服务端）。</param>
/// <param name="CompressionServerToClient">压缩算法（服务端 → 客户端）。</param>
/// <param name="StrictKeyExchange">双方是否都宣告支持严格 KEX（Terrapin 缓解）。</param>
/// <param name="PeerSupportsExtensionInfo">对端是否支持 RFC 8308 扩展协商。</param>
public readonly record struct SshNegotiatedAlgorithms(
    string KeyExchange,
    string HostKey,
    string EncryptionClientToServer,
    string EncryptionServerToClient,
    string? MacClientToServer,
    string? MacServerToClient,
    string CompressionClientToServer,
    string CompressionServerToClient,
    bool StrictKeyExchange,
    bool PeerSupportsExtensionInfo);

/// <summary>按 RFC 4253 §7.1 的规则做算法协商。</summary>
public static class SshAlgorithmNegotiator
{
    /// <summary>
    /// 以客户端身份协商全部算法。
    /// </summary>
    /// <param name="ours">本端清单（顺序即偏好）。</param>
    /// <param name="peer">对端的 KEXINIT。</param>
    /// <param name="peerVersion">对端版本串，只用于失败时的诊断。</param>
    /// <exception cref="SshNegotiationException">任一类算法没有交集。</exception>
    public static SshNegotiatedAlgorithms Negotiate(
        SshAlgorithmSet ours, SshKexInitMessage peer, string peerVersion)
    {
        ArgumentNullException.ThrowIfNull(ours);
        ArgumentNullException.ThrowIfNull(peer);

        // 指示符不是候选项：它们按名字选不中，但把它们从我们的名单里剔掉之后，
        // 失败时报给用户的「本端支持」才不会混进两个看不懂的东西。
        string[] ourKex = [.. ours.KeyExchange.Where(static n => !IsIndicator(n))];
        string[] peerKex = [.. peer.KeyExchangeAlgorithms.Where(static n => !IsIndicator(n))];

        string kex = Pick(SshNegotiationCategory.KeyExchange, ourKex, peerKex, peerVersion);
        string hostKey = Pick(SshNegotiationCategory.HostKey,
            ours.HostKey, peer.ServerHostKeyAlgorithms, peerVersion);

        string encC2S = Pick(SshNegotiationCategory.EncryptionClientToServer,
            ours.EncryptionClientToServer, peer.EncryptionClientToServer, peerVersion);
        string encS2C = Pick(SshNegotiationCategory.EncryptionServerToClient,
            ours.EncryptionServerToClient, peer.EncryptionServerToClient, peerVersion);

        // AEAD 自带完整性：该方向的 MAC 协商结果被忽略（RFC 5647）。
        // 但我们**仍然发送**非空的 MAC 列表 —— 不发会让只支持非 AEAD 的对端无法与我们协商。
        string? macC2S = IsAead(encC2S)
            ? null
            : Pick(SshNegotiationCategory.MacClientToServer, ours.MacClientToServer, peer.MacClientToServer, peerVersion);
        string? macS2C = IsAead(encS2C)
            ? null
            : Pick(SshNegotiationCategory.MacServerToClient, ours.MacServerToClient, peer.MacServerToClient, peerVersion);

        string cmpC2S = Pick(SshNegotiationCategory.CompressionClientToServer,
            ours.CompressionClientToServer, peer.CompressionClientToServer, peerVersion);
        string cmpS2C = Pick(SshNegotiationCategory.CompressionServerToClient,
            ours.CompressionServerToClient, peer.CompressionServerToClient, peerVersion);

        return new SshNegotiatedAlgorithms(
            kex, hostKey, encC2S, encS2C, macC2S, macS2C, cmpC2S, cmpS2C,
            StrictKeyExchange: peer.SupportsStrictKeyExchange,
            PeerSupportsExtensionInfo: peer.SupportsExtensionInfo);
    }

    /// <summary>该加密算法是否自带完整性（AEAD）。</summary>
    public static bool IsAead(string encryptionAlgorithm) => encryptionAlgorithm is
        SshAlgorithmNames.ChaCha20Poly1305 or
        SshAlgorithmNames.Aes128Gcm or
        SshAlgorithmNames.Aes256Gcm;

    /// <summary>该名字是不是藏在 kex 列表里的指示符而非真正的算法。</summary>
    public static bool IsIndicator(string name) => name is
        SshAlgorithmNames.ExtInfoClient or
        SshAlgorithmNames.ExtInfoServer or
        SshAlgorithmNames.StrictKexClient or
        SshAlgorithmNames.StrictKexServer;

    /// <summary>
    /// 取**我们**列表中第一个、且同时出现在对端列表里的名字。
    /// </summary>
    /// <remarks>
    /// 顺序以客户端为准（RFC 4253 §7.1）——
    /// 所以我们的列表顺序完全决定了结果，服务端的顺序不起作用。
    /// 名字**区分大小写**，按序数比较：任何大小写折叠都会让协商结果与对端的理解产生分歧。
    /// </remarks>
    private static string Pick(
        SshNegotiationCategory category,
        IReadOnlyList<string> ours,
        IReadOnlyList<string> peer,
        string peerVersion)
    {
        foreach (string candidate in ours)
        {
            if (peer.Contains(candidate, StringComparer.Ordinal))
            {
                return candidate;
            }
        }

        throw new SshNegotiationException(category, peer, ours, peerVersion);
    }
}
