// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §7.1  SSH_MSG_KEXINIT 的字段与协商规则
//   RFC 8308 §2.1  ext-info-c / ext-info-s 藏在 kex_algorithms 列表里
//   OpenSSH PROTOCOL 的 kex-strict-*-v00@openssh.com —— 同上
//   行为规格:      velashell-docs/zh/ssh/spec/03-key-exchange.md §2

using System.Buffers;
using System.Security.Cryptography;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Crypto;

/// <summary>
/// <c>SSH_MSG_KEXINIT</c>（20）：双方各发一次，宣告自己支持的算法。
/// </summary>
/// <remarks>
/// <para>
/// <b>两个容易被忽略的事实：</b>
/// </para>
/// <list type="number">
///   <item>
///     它是**双向同时发**的，不是请求-应答。谁先到都合法。
///   </item>
///   <item>
///     <b>整个报文的载荷必须原样保存</b> —— 它就是交换哈希里的 <c>I_C</c> / <c>I_S</c>
///     （velashell-docs/zh/ssh/spec/03 §4.1）。任何重新编码都可能产生不同的字节，
///     而那会让签名验证以一种完全看不出原因的方式失败。
///     为此 <see cref="Payload"/> 保存的是解析时看到的原始字节。
///   </item>
/// </list>
/// </remarks>
internal sealed class SshKexInitMessage
{
    /// <summary>cookie 的字节数。</summary>
    public const int CookieBytes = 16;

    /// <summary>name-list 的解析上限（单个列表）。</summary>
    private const int MaxNameListBytes = 64 * 1024;

    /// <summary>密钥交换算法。可能含 <c>ext-info-*</c> 与 <c>kex-strict-*</c> 指示符。</summary>
    public required IReadOnlyList<string> KeyExchangeAlgorithms { get; init; }

    /// <summary>主机密钥算法。</summary>
    public required IReadOnlyList<string> ServerHostKeyAlgorithms { get; init; }

    /// <summary>加密算法（客户端 → 服务端）。</summary>
    public required IReadOnlyList<string> EncryptionClientToServer { get; init; }

    /// <summary>加密算法（服务端 → 客户端）。</summary>
    public required IReadOnlyList<string> EncryptionServerToClient { get; init; }

    /// <summary>MAC 算法（客户端 → 服务端）。</summary>
    public required IReadOnlyList<string> MacClientToServer { get; init; }

    /// <summary>MAC 算法（服务端 → 客户端）。</summary>
    public required IReadOnlyList<string> MacServerToClient { get; init; }

    /// <summary>压缩算法（客户端 → 服务端）。</summary>
    public required IReadOnlyList<string> CompressionClientToServer { get; init; }

    /// <summary>压缩算法（服务端 → 客户端）。</summary>
    public required IReadOnlyList<string> CompressionServerToClient { get; init; }

    /// <summary>
    /// 对端是否紧接着发了一个猜测的 KEX 报文。
    /// </summary>
    /// <remarks>
    /// 猜错时那个报文**必须被丢弃**（RFC 4253 §7.1）。
    /// 我们自己发送时恒为 <see langword="false"/>（velashell-docs/zh/ssh/spec/03 §2.4）。
    /// </remarks>
    public required bool FirstKexPacketFollows { get; init; }

    /// <summary>
    /// 报文的原始载荷（含消息编号字节）。
    /// </summary>
    /// <remarks>
    /// <b>这是交换哈希的输入之一</b>，必须原样保存。见类型说明。
    /// </remarks>
    public required ReadOnlyMemory<byte> Payload { get; init; }

    /// <summary>对端是否宣告支持 RFC 8308 的扩展协商。</summary>
    public bool SupportsExtensionInfo =>
        KeyExchangeAlgorithms.Contains(SshAlgorithmNames.ExtInfoServer, StringComparer.Ordinal);

    /// <summary>对端是否宣告支持严格 KEX（Terrapin 缓解）。</summary>
    public bool SupportsStrictKeyExchange =>
        KeyExchangeAlgorithms.Contains(SshAlgorithmNames.StrictKexServer, StringComparer.Ordinal);

    /// <summary>
    /// 把本端的算法清单编码成一个 <c>SSH_MSG_KEXINIT</c> 载荷。
    /// </summary>
    /// <param name="algorithms">本端支持的算法。</param>
    /// <param name="includeIndicators">
    /// 是否附上 <c>ext-info-c</c> 与 <c>kex-strict-c-v00@openssh.com</c>。
    /// <b>只在首次 KEXINIT 时为真</b> —— RFC 8308 §2.2 明确要求 <c>ext-info-c</c>
    /// 只出现在第一次，重协商时再发是协议违规，某些服务端会直接断连。
    /// </param>
    /// <param name="output">载荷写到这里。</param>
    public static void Encode(SshAlgorithmSet algorithms, bool includeIndicators, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(algorithms);
        ArgumentNullException.ThrowIfNull(output);

        SshDataWriter writer = new(output);
        writer.WriteMessageNumber(SshMessageNumber.KexInit);

        // cookie 必须是密码学随机数：它让任何一方都无法单独决定交换哈希的取值。
        Span<byte> cookie = stackalloc byte[CookieBytes];
        RandomNumberGenerator.Fill(cookie);
        writer.WriteRaw(cookie);

        string[] kex = includeIndicators
            ? [.. algorithms.KeyExchange, SshAlgorithmNames.ExtInfoClient, SshAlgorithmNames.StrictKexClient]
            : [.. algorithms.KeyExchange];

        // 指示符放在列表末尾：它们按名字根本选不中，但放末尾能让人一眼看出
        // 它们不是候选项（velashell-docs/zh/ssh/spec/03 §2.3）。
        writer.WriteNameList(kex);
        writer.WriteNameList([.. algorithms.HostKey]);
        writer.WriteNameList([.. algorithms.EncryptionClientToServer]);
        writer.WriteNameList([.. algorithms.EncryptionServerToClient]);
        writer.WriteNameList([.. algorithms.MacClientToServer]);
        writer.WriteNameList([.. algorithms.MacServerToClient]);
        writer.WriteNameList([.. algorithms.CompressionClientToServer]);
        writer.WriteNameList([.. algorithms.CompressionServerToClient]);
        writer.WriteNameList([]);   // languages c2s —— 一律发空
        writer.WriteNameList([]);   // languages s2c
        writer.WriteBoolean(false); // first_kex_packet_follows，见 velashell-docs/zh/ssh/spec/03 §2.4
        writer.WriteUInt32(0);      // 保留
    }

    /// <summary>解析一个 <c>SSH_MSG_KEXINIT</c> 载荷。</summary>
    /// <param name="payload">整个载荷，含消息编号字节。</param>
    public static SshKexInitMessage Decode(ReadOnlyMemory<byte> payload)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.KexInit);
        reader.Skip(CookieBytes);

        string[] kex = reader.ReadNameList(MaxNameListBytes);
        string[] hostKey = reader.ReadNameList(MaxNameListBytes);
        string[] encC2S = reader.ReadNameList(MaxNameListBytes);
        string[] encS2C = reader.ReadNameList(MaxNameListBytes);
        string[] macC2S = reader.ReadNameList(MaxNameListBytes);
        string[] macS2C = reader.ReadNameList(MaxNameListBytes);
        string[] cmpC2S = reader.ReadNameList(MaxNameListBytes);
        string[] cmpS2C = reader.ReadNameList(MaxNameListBytes);
        _ = reader.ReadNameList(MaxNameListBytes);   // languages c2s，忽略
        _ = reader.ReadNameList(MaxNameListBytes);   // languages s2c，忽略
        bool follows = reader.ReadBoolean();
        _ = reader.ReadUInt32();                     // 保留字段：收到非 0 也必须忽略，不得报错

        // 刻意**不**调用 ExpectEnd：RFC 允许将来在末尾扩展字段，
        // 而我们保存的整段 Payload 仍然是交换哈希的正确输入。

        return new SshKexInitMessage
        {
            KeyExchangeAlgorithms = kex,
            ServerHostKeyAlgorithms = hostKey,
            EncryptionClientToServer = encC2S,
            EncryptionServerToClient = encS2C,
            MacClientToServer = macC2S,
            MacServerToClient = macS2C,
            CompressionClientToServer = cmpC2S,
            CompressionServerToClient = cmpS2C,
            FirstKexPacketFollows = follows,
            Payload = payload,
        };
    }
}
