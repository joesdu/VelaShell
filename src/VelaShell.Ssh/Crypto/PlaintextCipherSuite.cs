// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §6    Binary Packet Protocol
//   RFC 4253 §7.3  首个 SSH_MSG_NEWKEYS 之前使用 "none" 加密与 "none" MAC
//   行为规格:      velashell-docs/zh/ssh/spec/01-transport-framing.md §2.2

using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace VelaShell.Ssh.Crypto;

/// <summary>
/// 不加密、不认证的套件。版本交换之后、首次 <c>SSH_MSG_NEWKEYS</c> 之前使用。
/// </summary>
/// <remarks>
/// <para>
/// 它不是一个「特例分支」，就是 <see cref="CipherSuiteShape"/> 的一组取值 ——
/// 帧层对它与真正的密码套件一视同仁。这样握手期的分帧代码与稳态完全是同一条路径，
/// 不会出现「只在握手期才走的那段代码没人测过」的情况。
/// </para>
/// <para>
/// <b>填充仍然是密码学随机的。</b>握手期虽然不保密，但这些字节最终会进入
/// 交换哈希的输入之一（对端的 KEXINIT 载荷），可预测的填充没有任何好处。
/// </para>
/// </remarks>
internal sealed class PlaintextCipherSuite : ISshCipherSuite
{
    /// <inheritdoc />
    public CipherSuiteShape Shape => CipherSuiteShape.Plaintext;

    /// <inheritdoc />
    public void Seal(ReadOnlySpan<byte> payload, uint sequenceNumber, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);

        CipherSuiteShape shape = Shape;
        int padding = SshPacketFormat.ChoosePaddingLength(payload.Length, shape);
        int packetLength = SshPacketFormat.PaddingLengthFieldBytes + payload.Length + padding;
        int total = SshPacketFormat.LengthFieldBytes + packetLength;

        Span<byte> frame = output.GetSpan(total)[..total];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)packetLength);
        frame[SshPacketFormat.LengthFieldBytes] = (byte)padding;
        payload.CopyTo(frame[(SshPacketFormat.LengthFieldBytes + SshPacketFormat.PaddingLengthFieldBytes)..]);
        RandomNumberGenerator.Fill(frame[^padding..]);

        output.Advance(total);
    }

    /// <inheritdoc />
    public SshOpenStatus TryOpen(
        ReadOnlySequence<byte> input,
        uint sequenceNumber,
        int maxPacketLength,
        IBufferWriter<byte> payload,
        out long consumed)
    {
        ArgumentNullException.ThrowIfNull(payload);
        consumed = 0;

        if (input.Length < SshPacketFormat.LengthFieldBytes + 1)
        {
            return SshOpenStatus.NeedMoreData;
        }

        Span<byte> header = stackalloc byte[SshPacketFormat.LengthFieldBytes + 1];
        input.Slice(0, header.Length).CopyTo(header);

        uint packetLength = BinaryPrimitives.ReadUInt32BigEndian(header);
        byte paddingLength = header[SshPacketFormat.LengthFieldBytes];

        // 先校验再使用:越界的长度说明我们已经在错误的位置解析。
        SshPacketFormat.ValidateHeader(packetLength, paddingLength, maxPacketLength, Shape);

        long total = SshPacketFormat.LengthFieldBytes + packetLength;
        if (input.Length < total)
        {
            return SshOpenStatus.NeedMoreData;
        }

        int payloadLength = (int)packetLength - SshPacketFormat.PaddingLengthFieldBytes - paddingLength;
        if (payloadLength < 0)
        {
            throw new SshFrameFormatException("填充长度超过报文长度。");
        }

        if (payloadLength > 0)
        {
            ReadOnlySequence<byte> slice = input.Slice(
                SshPacketFormat.LengthFieldBytes + SshPacketFormat.PaddingLengthFieldBytes,
                payloadLength);
            Span<byte> destination = payload.GetSpan(payloadLength)[..payloadLength];
            slice.CopyTo(destination);
            payload.Advance(payloadLength);
        }

        consumed = total;
        return SshOpenStatus.Opened;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // 没有密钥材料要清。
    }
}
