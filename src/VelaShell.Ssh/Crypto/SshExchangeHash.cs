// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §8    交换哈希 H 的输入与顺序
//   RFC 4253 §7.2  密钥派生
//   RFC 5656 §4    ECDH 下 H 的输入
//   RFC 8731 §3    curve25519 下 H 的输入
//   行为规格:      velashell-docs/zh/ssh/spec/03-key-exchange.md §4、§7

using System.Buffers;
using System.Security.Cryptography;
using VelaShell.Ssh.Crypto.Kex;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Crypto;

/// <summary>交换哈希 <c>H</c> 的输入。</summary>
/// <remarks>
/// 字段顺序**就是**它们进哈希的顺序（RFC 4253 §8）。
/// 每一项按其 SSH 类型编码，而公开值与共享密钥的类型**随方法而变** ——
/// 见 <see cref="SshKexValueEncoding"/>。
/// </remarks>
public readonly ref struct SshExchangeHashInput
{
    /// <summary>客户端标识串（去行尾的原文字节）。</summary>
    public required ReadOnlySpan<byte> ClientVersion { get; init; }

    /// <summary>服务端标识串（去行尾的原文字节）。</summary>
    public required ReadOnlySpan<byte> ServerVersion { get; init; }

    /// <summary>客户端 <c>SSH_MSG_KEXINIT</c> 的载荷（含消息编号字节）。</summary>
    public required ReadOnlySpan<byte> ClientKexInit { get; init; }

    /// <summary>服务端 <c>SSH_MSG_KEXINIT</c> 的载荷。</summary>
    public required ReadOnlySpan<byte> ServerKexInit { get; init; }

    /// <summary>服务端主机公钥的 blob。</summary>
    public required ReadOnlySpan<byte> HostKeyBlob { get; init; }

    /// <summary>
    /// 群协商的额外输入（仅 <c>diffie-hellman-group-exchange-*</c>）。
    /// </summary>
    /// <remarks>
    /// RFC 4419 在主机密钥与公开值之间插入
    /// <c>uint32 min ‖ uint32 n ‖ uint32 max ‖ mpint p ‖ mpint g</c>。
    /// 其它方法为 <see langword="default"/>。
    /// </remarks>
    public ReadOnlySpan<byte> GroupExchangeExtra { get; init; }

    /// <summary>客户端公开值。</summary>
    public required ReadOnlySpan<byte> ClientPublicValue { get; init; }

    /// <summary>服务端公开值。</summary>
    public required ReadOnlySpan<byte> ServerPublicValue { get; init; }

    /// <summary>共享密钥。</summary>
    public required ReadOnlySpan<byte> SharedSecret { get; init; }

    /// <summary>公开值的编码方式。</summary>
    public required SshKexValueEncoding PublicValueEncoding { get; init; }

    /// <summary>共享密钥的编码方式。</summary>
    public required SshKexValueEncoding SharedSecretEncoding { get; init; }
}

/// <summary>交换哈希与密钥派生。</summary>
public static class SshExchangeHash
{
    /// <summary>
    /// 计算交换哈希 <c>H</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这是整个握手里最不容出错的一段。</b>任何一处顺序或编码错误，
    /// 表现都是同一句「签名验证失败」，而排查起来极其昂贵 ——
    /// 因为错的地方与报错的地方隔了整整一层。
    /// </para>
    /// <para>
    /// 因此 <see cref="SshExchangeHashInput"/> 把「哪一项、什么类型」做成了类型与字段，
    /// 而不是让调用方自己按顺序拼 —— 拼错的可能性被结构消掉了。
    /// </para>
    /// </remarks>
    public static byte[] Compute(HashAlgorithmName hashAlgorithm, in SshExchangeHashInput input)
    {
        ArrayBufferWriter<byte> buffer = new(1024);
        SshDataWriter writer = new(buffer);

        writer.WriteString(input.ClientVersion);
        writer.WriteString(input.ServerVersion);
        writer.WriteString(input.ClientKexInit);
        writer.WriteString(input.ServerKexInit);
        writer.WriteString(input.HostKeyBlob);

        // 群协商的五个字段（若有）在主机密钥与公开值之间。已由调用方编码好，原样写入。
        if (!input.GroupExchangeExtra.IsEmpty)
        {
            writer.WriteRaw(input.GroupExchangeExtra);
        }

        WriteValue(ref writer, input.ClientPublicValue, input.PublicValueEncoding);
        WriteValue(ref writer, input.ServerPublicValue, input.PublicValueEncoding);
        WriteValue(ref writer, input.SharedSecret, input.SharedSecretEncoding);

        byte[] hash = ComputeHash(hashAlgorithm, buffer.WrittenSpan);

        // 缓冲里有共享密钥，用完就抹掉。
        CryptographicOperations.ZeroMemory(
            System.Runtime.InteropServices.MemoryMarshal.AsMemory(buffer.WrittenMemory).Span);
        return hash;
    }

    /// <summary>
    /// 派生一把会话密钥（RFC 4253 §7.2）。
    /// </summary>
    /// <param name="hashAlgorithm">与 KEX 方法配套的哈希。</param>
    /// <param name="sharedSecret">共享密钥 <c>K</c> 的原始字节。</param>
    /// <param name="secretEncoding"><c>K</c> 的编码方式 —— 与算 <c>H</c> 时必须一致。</param>
    /// <param name="exchangeHash">本次交换的 <c>H</c>。</param>
    /// <param name="sessionId">会话标识（**首次** KEX 的 <c>H</c>，此后永不改变）。</param>
    /// <param name="letter">用途字母 <c>A</c>–<c>F</c>。</param>
    /// <param name="length">需要的字节数。</param>
    /// <remarks>
    /// <para>公式：<c>K_x = HASH(K ‖ H ‖ "X" ‖ session_id)</c>，不够长时按下式扩展：</para>
    /// <code>
    /// K1 = HASH(K ‖ H ‖ "X" ‖ session_id)
    /// K2 = HASH(K ‖ H ‖ K1)
    /// K3 = HASH(K ‖ H ‖ K1 ‖ K2)
    /// </code>
    /// <para>
    /// 两个容易错的点：
    /// </para>
    /// <list type="number">
    ///   <item><b>字母是一个裸字节，不是 <c>string</c></b>（没有长度前缀）。</item>
    ///   <item><b>扩展轮不含字母与 <c>session_id</c></b>，只有 <c>K ‖ H ‖ 已生成的全部</c>。</item>
    /// </list>
    /// <para>
    /// 另外：首次 KEX 时 <paramref name="sessionId"/> 与 <paramref name="exchangeHash"/> 相同，
    /// 重协商时 <c>H</c> 变而 <c>session_id</c> 不变。把它们混成一个字段，
    /// 症状是「重协商之后再开新通道做公钥认证会失败」—— 一条极罕见的路径，
    /// 很可能上线很久才被发现。
    /// </para>
    /// </remarks>
    public static byte[] DeriveKey(
        HashAlgorithmName hashAlgorithm,
        ReadOnlySpan<byte> sharedSecret,
        SshKexValueEncoding secretEncoding,
        ReadOnlySpan<byte> exchangeHash,
        ReadOnlySpan<byte> sessionId,
        char letter,
        int length)
    {
        if (letter is < 'A' or > 'F')
        {
            throw new ArgumentOutOfRangeException(nameof(letter), letter, "用途字母必须是 A–F。");
        }
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        if (length == 0)
        {
            return [];
        }

        // K ‖ H 这一段在每一轮里都相同，只算一次。
        //
        // ⚠️ **只有 K 带长度前缀，H 与 session_id 是裸字节。**
        //
        // RFC 4253 §7.2 写的是 `HASH(K || H || X || session_id)`，
        // 那个 `||` 是**直接拼接**。K 之所以带前缀，是因为它本身就以 mpint 编码，
        // 前缀是 mpint 的一部分；H 与 session_id 都是哈希输出的裸字节，
        // 给它们套一层 SSH string 的 4 字节长度前缀就会派生出**完全不同的密钥**。
        //
        // 这个错误极难自查：H 的计算是另一条代码路径（那里 string 前缀是对的），
        // 所以签名照样验得过 —— 双方对 K 和 H 的看法完全一致，
        // **只是从它们派生出来的密钥不一样**。
        // 于是握手全程顺利，第一个加密报文解不开。
        // 而自己写的测试服务端用的是同一个函数，两边一样错，测起来一路绿灯。
        // 是接上真实 OpenSSH 之后才暴露的（velashell-docs/zh/ssh/design/architecture.md §11.2.13）。
        ArrayBufferWriter<byte> prefix = new(sharedSecret.Length + exchangeHash.Length + 16);
        SshDataWriter prefixWriter = new(prefix);
        WriteValue(ref prefixWriter, sharedSecret, secretEncoding);
        prefixWriter.WriteRaw(exchangeHash);

        byte[] result = new byte[length];
        int produced = 0;

        // 第一轮：K ‖ H ‖ "X" ‖ session_id
        ArrayBufferWriter<byte> round = new(prefix.WrittenCount + 1 + sessionId.Length + 8);
        round.Write(prefix.WrittenSpan);
        SshDataWriter roundWriter = new(round);
        roundWriter.WriteByte((byte)letter);      // 裸字节，不是 string
        roundWriter.WriteRaw(sessionId);          // 同样是裸字节

        byte[] block = ComputeHash(hashAlgorithm, round.WrittenSpan);
        int take = Math.Min(block.Length, length);
        block.AsSpan(0, take).CopyTo(result);
        produced += take;

        // 扩展轮：K ‖ H ‖ (已生成的全部)
        while (produced < length)
        {
            ArrayBufferWriter<byte> next = new(prefix.WrittenCount + produced);
            next.Write(prefix.WrittenSpan);
            next.Write(result.AsSpan(0, produced));

            byte[] more = ComputeHash(hashAlgorithm, next.WrittenSpan);
            take = Math.Min(more.Length, length - produced);
            more.AsSpan(0, take).CopyTo(result.AsSpan(produced));
            produced += take;

            CryptographicOperations.ZeroMemory(more);
            CryptographicOperations.ZeroMemory(
                System.Runtime.InteropServices.MemoryMarshal.AsMemory(next.WrittenMemory).Span);
        }

        CryptographicOperations.ZeroMemory(block);
        CryptographicOperations.ZeroMemory(
            System.Runtime.InteropServices.MemoryMarshal.AsMemory(round.WrittenMemory).Span);
        CryptographicOperations.ZeroMemory(
            System.Runtime.InteropServices.MemoryMarshal.AsMemory(prefix.WrittenMemory).Span);
        return result;
    }

    private static void WriteValue(ref SshDataWriter writer, ReadOnlySpan<byte> value, SshKexValueEncoding encoding)
    {
        if (encoding == SshKexValueEncoding.Mpint)
        {
            writer.WriteMpint(value);
        }
        else
        {
            writer.WriteString(value);
        }
    }

    private static byte[] ComputeHash(HashAlgorithmName algorithm, ReadOnlySpan<byte> data)
    {
        if (algorithm == HashAlgorithmName.SHA256)
        {
            return SHA256.HashData(data);
        }
        if (algorithm == HashAlgorithmName.SHA384)
        {
            return SHA384.HashData(data);
        }
        if (algorithm == HashAlgorithmName.SHA512)
        {
            return SHA512.HashData(data);
        }
        if (algorithm == HashAlgorithmName.SHA1)
        {
            // CA5350「使用了弱加密算法 SHA-1」—— **这是刻意的，不是疏忽。**
            //
            // 它只服务于 diffie-hellman-group14-sha1 这一条路，而那条路：
            //   · 不在 SshAlgorithmSet.Default 里；
            //   · 只有使用者显式调用 WithLegacyInterop() 才会被启用；
            //   · 存在的唯一理由是「那台交换机只会这个」——
            //     而让人连不上设备，并不会让他更安全，只会让他去找一个更差的工具。
            //
            // 把整条规则全局关掉会连带放过真正的疏忽，所以只在这一行抑制。
#pragma warning disable CA5350 // Do Not Use Weak Cryptographic Algorithms
            return SHA1.HashData(data);
#pragma warning restore CA5350
        }

        throw new NotSupportedException($"交换哈希不支持 {algorithm.Name}。");
    }
}
