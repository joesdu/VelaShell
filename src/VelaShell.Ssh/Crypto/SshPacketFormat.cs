// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §6    Binary Packet Protocol —— 填充规则与最小填充长度
//   RFC 4253 §6.1  最大包长
//   行为规格:      velashell-docs/zh/ssh/spec/01-transport-framing.md §1

using System.Security.Cryptography;

namespace VelaShell.Ssh.Crypto;

/// <summary>
/// 二进制报文的通用格式常量与填充计算。
/// </summary>
/// <remarks>各密码套件共用这些规则；套件之间的差异只在 <see cref="CipherSuiteShape"/>。</remarks>
public static class SshPacketFormat
{
    /// <summary><c>packet_length</c> 字段的字节数。</summary>
    public const int LengthFieldBytes = 4;

    /// <summary><c>padding_length</c> 字段的字节数。</summary>
    public const int PaddingLengthFieldBytes = 1;

    /// <summary>填充的最小字节数（RFC 4253 §6）。</summary>
    public const int MinimumPadding = 4;

    /// <summary>填充的最大字节数（<c>padding_length</c> 是一个字节）。</summary>
    public const int MaximumPadding = 255;

    /// <summary>
    /// 认证完成前允许的最大 <c>packet_length</c>。
    /// </summary>
    /// <remarks>RFC 4253 §6.1 要求实现至少支持 35000；握手期不需要更大。</remarks>
    public const int PreAuthMaxPacketLength = 35000;

    /// <summary>
    /// 认证完成后允许的最大 <c>packet_length</c> 的默认值（256 KiB）。
    /// </summary>
    /// <remarks>
    /// RFC 只规定了下限。SFTP 在高延迟链路上要靠大报文降低往返次数，
    /// 把上限永久锁在 35000 会直接封死吞吐优化；但也不能不设上限 ——
    /// 它是对端能让我们分配的最大单块内存，是一个明确的拒绝服务面。
    /// 256 KiB 是「足够大到不限制吞吐、足够小到不值得被用来打内存」的折中。
    /// </remarks>
    public const int DefaultMaxPacketLength = 256 * 1024;

    /// <summary>
    /// 按套件形状计算一帧需要的填充字节数。
    /// </summary>
    /// <param name="payloadLength">载荷字节数。</param>
    /// <param name="shape">套件形状。</param>
    /// <returns>填充字节数。恒落在 [4, 4 + 块大小) 内，因此必然 ≤ 255。</returns>
    /// <remarks>
    /// 对齐的是 <c>padding_length + payload + padding</c>，
    /// <b>当 <see cref="CipherSuiteShape.LengthInAlignment"/> 为真时再加上 4 字节长度字段</b>。
    /// AEAD 套件不含长度字段 —— 这是 RFC 5647 与 OpenSSH chacha20-poly1305 共同的规定，
    /// 也是 AEAD 实现最常见的错位来源。
    /// </remarks>
    public static int ComputePaddingLength(int payloadLength, in CipherSuiteShape shape)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadLength);

        int block = Math.Max(shape.BlockBytes, 8);
        int unaligned = PaddingLengthFieldBytes + payloadLength
                        + (shape.LengthInAlignment ? LengthFieldBytes : 0);

        // 补到块边界。余数为 0 时补满一个块（而不是 0）—— 填充至少要 4 字节。
        int padding = block - (unaligned % block);
        if (padding < MinimumPadding)
        {
            padding += block;
        }

        // padding ∈ [1, block] 加上最多一次 +block，因此 ≤ 2×block ≤ 32，
        // 永远不会逼近 255。这个断言是给将来可能出现的大块算法留的引信。
        System.Diagnostics.Debug.Assert(padding is >= MinimumPadding and <= MaximumPadding);
        return padding;
    }

    /// <summary>
    /// 选定一帧的填充长度，在满足对齐的前提下**以一半概率多补一个块**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 等长报文会泄漏特征：交互式 shell 的每一次按键都是一个固定大小的帧，
    /// 帧长序列本身就是一条侧信道（击键时序 + 长度）。随机长度填充把它糊掉。
    /// </para>
    /// <para>
    /// 代价是平均每帧多几十字节。对交互式流量可忽略；对批量传输，
    /// 载荷本来就接近最大包长，多补一个块的余量往往不存在，自然退化为不补。
    /// </para>
    /// </remarks>
    public static int ChoosePaddingLength(int payloadLength, in CipherSuiteShape shape)
    {
        int baseline = ComputePaddingLength(payloadLength, shape);
        int block = Math.Max(shape.BlockBytes, 8);

        // 只有在加了一个块之后仍不超过 255 时才考虑加。
        if (baseline + block > MaximumPadding)
        {
            return baseline;
        }

        return RandomNumberGenerator.GetInt32(2) == 1 ? baseline + block : baseline;
    }

    /// <summary>
    /// 校验收到的 <c>packet_length</c> 与 <c>padding_length</c> 是否自洽。
    /// </summary>
    /// <exception cref="SshFrameFormatException">任一项非法。</exception>
    /// <remarks>
    /// 这几条检查必须在使用这两个值**之前**做。
    /// 越界的长度字段说明我们已经在错误的位置解析，后续字节全部不可信。
    /// </remarks>
    public static void ValidateHeader(uint packetLength, byte paddingLength, int maxPacketLength, in CipherSuiteShape shape)
    {
        if (packetLength > (uint)maxPacketLength)
        {
            throw new SshFrameFormatException($"packet_length {packetLength} 超过上限 {maxPacketLength}。");
        }

        // 至少要容下 padding_length 字节本身 + 最小填充。
        if (packetLength < PaddingLengthFieldBytes + MinimumPadding)
        {
            throw new SshFrameFormatException($"packet_length {packetLength} 过小。");
        }

        if (paddingLength < MinimumPadding)
        {
            throw new SshFrameFormatException($"padding_length {paddingLength} 小于最小值 {MinimumPadding}。");
        }

        if (paddingLength > packetLength - PaddingLengthFieldBytes)
        {
            throw new SshFrameFormatException(
                $"padding_length {paddingLength} 超过 packet_length {packetLength} 所能容纳的范围。");
        }

        int block = Math.Max(shape.BlockBytes, 8);
        long aligned = packetLength + (shape.LengthInAlignment ? LengthFieldBytes : 0);
        if (aligned % block != 0)
        {
            throw new SshFrameFormatException(
                $"帧长 {aligned} 不是块大小 {block} 的整数倍。");
        }
    }
}
