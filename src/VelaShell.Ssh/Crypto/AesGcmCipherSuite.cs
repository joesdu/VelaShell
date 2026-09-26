// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 5647 §5   AES-GCM 在 SSH 中的报文格式(长度字段是明文 AAD)
//   RFC 5647 §7.1 nonce = 4 字节固定 IV ‖ 8 字节 invocation counter,每帧加 1
//   RFC 5647 §7.2 对齐不含长度字段
//   OpenSSH PROTOCOL  aes128-gcm@openssh.com / aes256-gcm@openssh.com
//   行为规格:     velashell-docs/zh/ssh/spec/01-transport-framing.md §2.1 ②

using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace VelaShell.Ssh.Crypto;

/// <summary>
/// <c>aes128-gcm@openssh.com</c> / <c>aes256-gcm@openssh.com</c>。
/// </summary>
/// <remarks>
/// <para>
/// 形状：长度字段**明文**且作为 AAD，tag 16 字节，对齐按 16 且**不含长度字段**。
/// </para>
/// <para>
/// ⚠️ <b>nonce 用的是独立递增的 invocation counter，不是报文序号。</b>
/// 两者在首次密钥交换之后数值恰好相同，所以用序号代替能跑通握手与初期流量 ——
/// <b>但重协商之后就会错开</b>，症状是「连接跑了一阵忽然开始解密失败」。
/// 这是 GCM 实现最隐蔽的一个坑，本类用 <see cref="_nonce"/> 自己维护计数器。
/// </para>
/// </remarks>
internal sealed class AesGcmCipherSuite : ISshCipherSuite
{
    private const int NonceBytes = 12;
    private const int FixedIvBytes = 4;
    private const int CounterBytes = NonceBytes - FixedIvBytes;
    private const int TagBytes = 16;
    private const int BlockBytes = 16;

    private readonly AesGcm _aes;
    private readonly byte[] _nonce = new byte[NonceBytes];
    private bool _disposed;

    /// <summary>用给定密钥与初始 IV 构造。</summary>
    /// <param name="key">16 或 32 字节（AES-128 / AES-256）。</param>
    /// <param name="iv">12 字节。前 4 字节固定，后 8 字节是 invocation counter 的初值。</param>
    public AesGcmCipherSuite(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        if (key.Length is not (16 or 32))
        {
            throw new ArgumentException("AES-GCM 密钥必须是 16 或 32 字节。", nameof(key));
        }
        if (iv.Length != NonceBytes)
        {
            throw new ArgumentException($"AES-GCM 的 IV 必须是 {NonceBytes} 字节。", nameof(iv));
        }

        _aes = new AesGcm(key, TagBytes);
        iv.CopyTo(_nonce);
    }

    /// <inheritdoc />
    public CipherSuiteShape Shape { get; } = new()
    {
        LengthIsEncrypted = false,
        AadBytes = SshPacketFormat.LengthFieldBytes,
        TagBytes = TagBytes,
        BlockBytes = BlockBytes,
        // RFC 5647 §7.2：对齐的是 padding_length + payload + padding，**不含**长度字段。
        LengthInAlignment = false,
        EncryptThenMac = true,
        IsEncrypted = true,
        LengthProbeBytes = SshPacketFormat.LengthFieldBytes,
    };

    /// <inheritdoc />
    public void Seal(ReadOnlySpan<byte> payload, uint sequenceNumber, IBufferWriter<byte> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(output);

        CipherSuiteShape shape = Shape;
        int padding = SshPacketFormat.ChoosePaddingLength(payload.Length, shape);
        int packetLength = SshPacketFormat.PaddingLengthFieldBytes + payload.Length + padding;
        int total = SshPacketFormat.LengthFieldBytes + packetLength + TagBytes;

        Span<byte> frame = output.GetSpan(total)[..total];
        Span<byte> lengthField = frame[..SshPacketFormat.LengthFieldBytes];
        BinaryPrimitives.WriteUInt32BigEndian(lengthField, (uint)packetLength);

        // 明文区：padding_length ‖ payload ‖ padding
        byte[] rented = ArrayPool<byte>.Shared.Rent(packetLength);
        try
        {
            Span<byte> plaintext = rented.AsSpan(0, packetLength);
            plaintext[0] = (byte)padding;
            payload.CopyTo(plaintext[SshPacketFormat.PaddingLengthFieldBytes..]);
            RandomNumberGenerator.Fill(plaintext[^padding..]);

            Span<byte> ciphertext = frame.Slice(SshPacketFormat.LengthFieldBytes, packetLength);
            Span<byte> tag = frame[^TagBytes..];

            // 长度字段是 AAD：它明文传输，但被 tag 保护，改不了。
            _aes.Encrypt(_nonce, plaintext, ciphertext, tag, lengthField);
            CryptographicOperations.ZeroMemory(plaintext);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        AdvanceNonce();
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(payload);
        consumed = 0;

        if (input.Length < SshPacketFormat.LengthFieldBytes)
        {
            return SshOpenStatus.NeedMoreData;
        }

        Span<byte> lengthField = stackalloc byte[SshPacketFormat.LengthFieldBytes];
        input.Slice(0, SshPacketFormat.LengthFieldBytes).CopyTo(lengthField);
        uint packetLength = BinaryPrimitives.ReadUInt32BigEndian(lengthField);

        // 长度是明文，但它被 tag 保护 —— 不过 tag 要等整帧收齐才能验。
        // 所以这里先做**范围检查**再按它去等数据：不检查就等于让对端指定我们等多少字节。
        if (packetLength > (uint)maxPacketLength
            || packetLength < SshPacketFormat.PaddingLengthFieldBytes + SshPacketFormat.MinimumPadding
            || packetLength % BlockBytes != 0)
        {
            throw new SshFrameFormatException(
                $"AES-GCM 帧头非法：packet_length={packetLength}（上限 {maxPacketLength}，须为 {BlockBytes} 的倍数）。");
        }

        long total = SshPacketFormat.LengthFieldBytes + packetLength + TagBytes;
        if (input.Length < total)
        {
            return SshOpenStatus.NeedMoreData;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent((int)packetLength * 2);
        try
        {
            Span<byte> ciphertext = rented.AsSpan(0, (int)packetLength);
            Span<byte> plaintext = rented.AsSpan((int)packetLength, (int)packetLength);
            input.Slice(SshPacketFormat.LengthFieldBytes, packetLength).CopyTo(ciphertext);

            Span<byte> tag = stackalloc byte[TagBytes];
            input.Slice(SshPacketFormat.LengthFieldBytes + packetLength, TagBytes).CopyTo(tag);

            try
            {
                _aes.Decrypt(_nonce, ciphertext, tag, plaintext, lengthField);
            }
            catch (AuthenticationTagMismatchException ex)
            {
                throw new SshFrameFormatException("报文完整性校验失败。", ex);
            }

            byte paddingLength = plaintext[0];
            int payloadLength = (int)packetLength - SshPacketFormat.PaddingLengthFieldBytes - paddingLength;
            if (paddingLength < SshPacketFormat.MinimumPadding || payloadLength < 0)
            {
                throw new SshFrameFormatException($"padding_length {paddingLength} 非法。");
            }

            if (payloadLength > 0)
            {
                plaintext.Slice(SshPacketFormat.PaddingLengthFieldBytes, payloadLength)
                         .CopyTo(payload.GetSpan(payloadLength));
                payload.Advance(payloadLength);
            }

            CryptographicOperations.ZeroMemory(plaintext);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        // 只有成功解出一帧才推进计数器 —— NeedMoreData 的路径上不得改动任何状态。
        AdvanceNonce();
        consumed = total;
        return SshOpenStatus.Opened;
    }

    /// <summary>
    /// invocation counter 加 1（nonce 的后 8 字节，大端，按 64 位无符号回绕）。
    /// </summary>
    /// <remarks>
    /// 计数器回绕会重用 nonce，对 GCM 是灾难性的（可恢复认证密钥）。
    /// 2⁶⁴ 个报文在现实中到不了，但会话级的重协商阈值仍然会在接近时强制换密钥
    /// （velashell-docs/zh/ssh/spec/03-key-exchange.md §8.1）。
    /// </remarks>
    private void AdvanceNonce()
    {
        Span<byte> counter = _nonce.AsSpan(FixedIvBytes, CounterBytes);
        ulong value = BinaryPrimitives.ReadUInt64BigEndian(counter);
        BinaryPrimitives.WriteUInt64BigEndian(counter, unchecked(value + 1));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        CryptographicOperations.ZeroMemory(_nonce);
        _aes.Dispose();
    }
}
