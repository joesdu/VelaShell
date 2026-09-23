// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4344 §4   aes128-ctr / aes192-ctr / aes256-ctr
//   RFC 4253 §6.4 MAC 的输入是 序号 ‖ 未加密的整帧(MtE)
//   OpenSSH PROTOCOL 的 *-etm@openssh.com —— MAC 覆盖密文,长度字段明文且不参与对齐
//   NIST SP 800-38A §6.5  CTR 工作模式
//   行为规格:     velashell-docs/zh/ssh/spec/01-transport-framing.md §1.2、§2.1 ③

using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace VelaShell.Ssh.Crypto;

/// <summary>MAC 算法的选择。</summary>
public enum SshMacAlgorithm
{
    /// <summary>HMAC-SHA-1，20 字节。仅为老设备保留。</summary>
    HmacSha1,

    /// <summary>HMAC-SHA-256，32 字节。</summary>
    HmacSha256,

    /// <summary>HMAC-SHA-512，64 字节。</summary>
    HmacSha512,
}

/// <summary>
/// AES-CTR 加密 + 独立 HMAC 完整性，支持 Encrypt-then-MAC 与 MAC-then-Encrypt 两种顺序。
/// </summary>
/// <remarks>
/// <para>
/// <b>两种顺序的差别是安全属性，不是风格</b>：
/// </para>
/// <list type="table">
///   <item>
///     <term>EtM（<c>*-etm@openssh.com</c>）</term>
///     <description>MAC 覆盖<b>密文</b>，长度字段明文。可以**先验证再解密**。</description>
///   </item>
///   <item>
///     <term>MtE（RFC 4253 原始顺序）</term>
///     <description>MAC 覆盖<b>明文</b>，长度字段也被加密。
///     必须先解密才能验证 —— 本质上给对端提供了一个解密预言机。
///     只为兼容老服务端而实现，排在 EtM 之后。</description>
///   </item>
/// </list>
/// <para>
/// CTR 的计数器是**跨帧连续的一条流**。实现上关键的一点是：
/// <see cref="TryOpen"/> 在返回 <see cref="SshOpenStatus.NeedMoreData"/> 时
/// <b>绝不推进计数器</b> —— 每次尝试都从当前计数器重新算一遍密钥流。
/// 重复解那一两个块的代价可以忽略，而换来的是「重试不会污染状态」这个性质
/// （见 <c>CipherSuiteConformanceTests.数据不足时返回NeedMoreData且不改动状态</c>）。
/// </para>
/// </remarks>
public sealed class AesCtrHmacCipherSuite : ISshCipherSuite
{
    private const int AesBlockBytes = 16;

    private readonly Aes _aes;
    private readonly byte[] _counter = new byte[AesBlockBytes];
    private readonly byte[] _macKey;
    private readonly HashAlgorithmName _macName;
    private readonly int _macBytes;
    private readonly bool _encryptThenMac;
    private bool _disposed;

    /// <summary>构造一个 AES-CTR + HMAC 套件。</summary>
    /// <param name="key">16 / 24 / 32 字节（AES-128 / 192 / 256）。</param>
    /// <param name="iv">16 字节，CTR 的初始计数器块。</param>
    /// <param name="mac">MAC 算法。</param>
    /// <param name="macKey">MAC 密钥，长度应当等于该 MAC 的输出长度。</param>
    /// <param name="encryptThenMac">是否用 Encrypt-then-MAC。</param>
    public AesCtrHmacCipherSuite(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> iv,
        SshMacAlgorithm mac,
        ReadOnlySpan<byte> macKey,
        bool encryptThenMac)
    {
        if (key.Length is not (16 or 24 or 32))
        {
            throw new ArgumentException("AES 密钥必须是 16、24 或 32 字节。", nameof(key));
        }
        if (iv.Length != AesBlockBytes)
        {
            throw new ArgumentException($"CTR 的初始计数器必须是 {AesBlockBytes} 字节。", nameof(iv));
        }

        (_macName, _macBytes) = mac switch
        {
            SshMacAlgorithm.HmacSha1 => (HashAlgorithmName.SHA1, 20),
            SshMacAlgorithm.HmacSha256 => (HashAlgorithmName.SHA256, 32),
            SshMacAlgorithm.HmacSha512 => (HashAlgorithmName.SHA512, 64),
            _ => throw new ArgumentOutOfRangeException(nameof(mac)),
        };

        _macKey = macKey.ToArray();
        _encryptThenMac = encryptThenMac;

        // 只用 AES 的 ECB **单块加密**来产生计数器块 —— 这是 CTR 模式的定义
        // （NIST SP 800-38A §6.5），不是「用 ECB 加密数据」。
        // 走 BCL 而不是软件实现，是为了吃到 AES-NI / ARM Crypto 扩展的硬件加速。
        _aes = Aes.Create();
        _aes.Key = key.ToArray();
        _aes.Mode = CipherMode.ECB;
        _aes.Padding = PaddingMode.None;

        iv.CopyTo(_counter);

        Shape = new CipherSuiteShape
        {
            // EtM 下长度字段是明文；MtE 下它和其余部分一起被加密。
            LengthIsEncrypted = !encryptThenMac,
            AadBytes = 0,
            TagBytes = _macBytes,
            BlockBytes = AesBlockBytes,
            // 见 velashell-docs/zh/ssh/spec/01 §1.2：EtM 与 AEAD 一样，长度字段不进对齐计算。
            LengthInAlignment = !encryptThenMac,
            EncryptThenMac = encryptThenMac,
            IsEncrypted = true,
            // MtE 下长度被加密，要解一整个 AES 块才读得到它。
            LengthProbeBytes = encryptThenMac ? SshPacketFormat.LengthFieldBytes : AesBlockBytes,
        };
    }

    /// <inheritdoc />
    public CipherSuiteShape Shape { get; }

    /// <inheritdoc />
    public void Seal(ReadOnlySpan<byte> payload, uint sequenceNumber, IBufferWriter<byte> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(output);

        CipherSuiteShape shape = Shape;
        int padding = SshPacketFormat.ChoosePaddingLength(payload.Length, shape);
        int packetLength = SshPacketFormat.PaddingLengthFieldBytes + payload.Length + padding;
        int total = SshPacketFormat.LengthFieldBytes + packetLength + _macBytes;

        Span<byte> frame = output.GetSpan(total)[..total];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)packetLength);
        frame[SshPacketFormat.LengthFieldBytes] = (byte)padding;
        payload.CopyTo(frame[(SshPacketFormat.LengthFieldBytes + SshPacketFormat.PaddingLengthFieldBytes)..]);
        RandomNumberGenerator.Fill(frame.Slice(SshPacketFormat.LengthFieldBytes + packetLength - padding, padding));

        Span<byte> mac = frame[^_macBytes..];

        if (_encryptThenMac)
        {
            // 加密区不含长度字段。
            Span<byte> region = frame.Slice(SshPacketFormat.LengthFieldBytes, packetLength);
            ApplyKeystream(region, _counter);
            AdvanceCounter(packetLength);
            // MAC 覆盖：序号 ‖ 明文长度字段 ‖ 密文
            ComputeMac(sequenceNumber, frame[..(SshPacketFormat.LengthFieldBytes + packetLength)], mac);
        }
        else
        {
            // MAC 先算，覆盖的是**明文**整帧（含长度字段）。
            Span<byte> region = frame[..(SshPacketFormat.LengthFieldBytes + packetLength)];
            ComputeMac(sequenceNumber, region, mac);
            ApplyKeystream(region, _counter);
            AdvanceCounter(region.Length);
        }

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

        return _encryptThenMac
            ? OpenEncryptThenMac(input, sequenceNumber, maxPacketLength, payload, ref consumed)
            : OpenMacThenEncrypt(input, sequenceNumber, maxPacketLength, payload, ref consumed);
    }

    private SshOpenStatus OpenEncryptThenMac(
        ReadOnlySequence<byte> input, uint sequenceNumber, int maxPacketLength,
        IBufferWriter<byte> payload, ref long consumed)
    {
        if (input.Length < SshPacketFormat.LengthFieldBytes)
        {
            return SshOpenStatus.NeedMoreData;
        }

        Span<byte> lengthField = stackalloc byte[SshPacketFormat.LengthFieldBytes];
        input.Slice(0, SshPacketFormat.LengthFieldBytes).CopyTo(lengthField);
        uint packetLength = BinaryPrimitives.ReadUInt32BigEndian(lengthField);

        // 长度是明文但**还没被 MAC 认证**。先做范围检查再据它等数据 ——
        // 否则等于让一个未经认证的数字决定我们要等多少字节。
        ValidateEncryptedRegion(packetLength, maxPacketLength, "EtM");

        long total = SshPacketFormat.LengthFieldBytes + packetLength + _macBytes;
        if (input.Length < total)
        {
            return SshOpenStatus.NeedMoreData;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent((int)total);
        try
        {
            Span<byte> frame = rented.AsSpan(0, (int)total);
            input.Slice(0, total).CopyTo(frame);

            // ① 先验证（EtM 的全部价值就在这一步能先做）。
            Span<byte> expected = stackalloc byte[64];
            expected = expected[.._macBytes];
            ComputeMac(sequenceNumber, frame[..(SshPacketFormat.LengthFieldBytes + (int)packetLength)], expected);
            if (!CryptographicOperations.FixedTimeEquals(expected, frame[^_macBytes..]))
            {
                throw SshFrameFormatException.IntegrityCheckFailed();
            }

            // ② 再解密。
            Span<byte> region = frame.Slice(SshPacketFormat.LengthFieldBytes, (int)packetLength);
            ApplyKeystream(region, _counter);
            AdvanceCounter((int)packetLength);

            EmitPayload(region, (int)packetLength, payload);
            CryptographicOperations.ZeroMemory(frame);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        consumed = total;
        return SshOpenStatus.Opened;
    }

    private SshOpenStatus OpenMacThenEncrypt(
        ReadOnlySequence<byte> input, uint sequenceNumber, int maxPacketLength,
        IBufferWriter<byte> payload, ref long consumed)
    {
        // 长度被加密：要先解一整个 AES 块才读得到它。
        if (input.Length < AesBlockBytes)
        {
            return SshOpenStatus.NeedMoreData;
        }

        Span<byte> firstBlock = stackalloc byte[AesBlockBytes];
        input.Slice(0, AesBlockBytes).CopyTo(firstBlock);
        // 用**当前**计数器解，且不提交 —— 数据不够时下一次会从同一个计数器重来。
        ApplyKeystream(firstBlock, _counter);
        uint packetLength = BinaryPrimitives.ReadUInt32BigEndian(firstBlock);

        // MtE 下长度字段也在加密区里，所以整段(4 + packetLength)才是块的整数倍。
        if (packetLength > (uint)maxPacketLength
            || packetLength < SshPacketFormat.PaddingLengthFieldBytes + SshPacketFormat.MinimumPadding
            || (packetLength + SshPacketFormat.LengthFieldBytes) % AesBlockBytes != 0)
        {
            throw new SshFrameFormatException(
                $"MtE 帧头非法：packet_length={packetLength}（上限 {maxPacketLength}）。");
        }

        int encryptedBytes = SshPacketFormat.LengthFieldBytes + (int)packetLength;
        long total = encryptedBytes + _macBytes;
        if (input.Length < total)
        {
            return SshOpenStatus.NeedMoreData;   // 计数器未动
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent((int)total);
        try
        {
            Span<byte> frame = rented.AsSpan(0, (int)total);
            input.Slice(0, total).CopyTo(frame);

            // ① 解密整个加密区（含长度字段），从**同一个**起始计数器开始。
            Span<byte> region = frame[..encryptedBytes];
            ApplyKeystream(region, _counter);

            // ② 再验证 —— MtE 只能这个顺序，这正是它不如 EtM 的地方。
            Span<byte> expected = stackalloc byte[64];
            expected = expected[.._macBytes];
            ComputeMac(sequenceNumber, region, expected);
            if (!CryptographicOperations.FixedTimeEquals(expected, frame[^_macBytes..]))
            {
                throw SshFrameFormatException.IntegrityCheckFailed();
            }

            AdvanceCounter(encryptedBytes);
            EmitPayload(region[SshPacketFormat.LengthFieldBytes..], (int)packetLength, payload);
            CryptographicOperations.ZeroMemory(frame);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        consumed = total;
        return SshOpenStatus.Opened;
    }

    private static void ValidateEncryptedRegion(uint packetLength, int maxPacketLength, string label)
    {
        if (packetLength > (uint)maxPacketLength
            || packetLength < SshPacketFormat.PaddingLengthFieldBytes + SshPacketFormat.MinimumPadding
            || packetLength % AesBlockBytes != 0)
        {
            throw new SshFrameFormatException(
                $"{label} 帧头非法：packet_length={packetLength}（上限 {maxPacketLength}，须为 {AesBlockBytes} 的倍数）。");
        }
    }

    /// <summary>从已解密的 <c>padding_length ‖ payload ‖ padding</c> 区里取出载荷。</summary>
    private static void EmitPayload(ReadOnlySpan<byte> region, int packetLength, IBufferWriter<byte> payload)
    {
        byte paddingLength = region[0];
        int payloadLength = packetLength - SshPacketFormat.PaddingLengthFieldBytes - paddingLength;
        if (paddingLength < SshPacketFormat.MinimumPadding || payloadLength < 0)
        {
            throw new SshFrameFormatException($"padding_length {paddingLength} 非法。");
        }

        if (payloadLength > 0)
        {
            region.Slice(SshPacketFormat.PaddingLengthFieldBytes, payloadLength)
                  .CopyTo(payload.GetSpan(payloadLength));
            payload.Advance(payloadLength);
        }
    }

    /// <summary>
    /// 用从 <paramref name="startCounter"/> 起的 CTR 密钥流对 <paramref name="data"/> 做异或。
    /// </summary>
    /// <remarks>
    /// <b>不改变 <see cref="_counter"/>。</b>提交由调用方在确认整帧无误之后用
    /// <see cref="AdvanceCounter"/> 显式完成 —— 这个分工是「NeedMoreData 不污染状态」的全部实现。
    /// <para>
    /// 计数器块批量生成后一次 <c>EncryptEcb</c>，让 AES-NI 能在多个块上流水，
    /// 比逐块调用快得多。
    /// </para>
    /// </remarks>
    private void ApplyKeystream(Span<byte> data, ReadOnlySpan<byte> startCounter)
    {
        if (data.IsEmpty)
        {
            return;
        }

        int blocks = (data.Length + AesBlockBytes - 1) / AesBlockBytes;
        int streamBytes = blocks * AesBlockBytes;

        byte[] rented = ArrayPool<byte>.Shared.Rent(streamBytes * 2);
        try
        {
            Span<byte> counters = rented.AsSpan(0, streamBytes);
            Span<byte> keyStream = rented.AsSpan(streamBytes, streamBytes);

            Span<byte> current = stackalloc byte[AesBlockBytes];
            startCounter.CopyTo(current);
            for (int i = 0; i < blocks; i++)
            {
                current.CopyTo(counters[(i * AesBlockBytes)..]);
                IncrementCounter(current);
            }

            _aes.EncryptEcb(counters, keyStream, PaddingMode.None);

            for (int i = 0; i < data.Length; i++)
            {
                data[i] ^= keyStream[i];
            }

            CryptographicOperations.ZeroMemory(keyStream);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>把持久计数器推进 <paramref name="byteCount"/> 字节对应的块数。</summary>
    private void AdvanceCounter(int byteCount)
    {
        int blocks = (byteCount + AesBlockBytes - 1) / AesBlockBytes;
        Span<byte> counter = _counter;
        for (int i = 0; i < blocks; i++)
        {
            IncrementCounter(counter);
        }
    }

    /// <summary>128 位大端计数器加 1（RFC 4344 §4 规定的 SDCTR 行为）。</summary>
    /// <remarks>
    /// 把 16 字节当成「高 64 位 ‖ 低 64 位」两个大端整数来加，而不是逐字节带进位。
    /// 绝大多数情况下只动低 64 位，两次内存访问就够 —— 逐字节版本在最好的情况下
    /// 也要读写一个字节并做一次分支判断，而这段代码在每一个 AES 块上都会跑一次。
    /// </remarks>
    private static void IncrementCounter(Span<byte> counter)
    {
        Span<byte> low = counter[8..];
        ulong lowValue = BinaryPrimitives.ReadUInt64BigEndian(low);
        BinaryPrimitives.WriteUInt64BigEndian(low, unchecked(lowValue + 1));

        // 低 64 位回绕时才需要动高 64 位。2^64 个块 = 2^68 字节，现实中到不了，
        // 但计数器语义必须完整 —— 缺了这一步就是一个「跑够久才出现」的 bug。
        if (lowValue == ulong.MaxValue)
        {
            Span<byte> high = counter[..8];
            ulong highValue = BinaryPrimitives.ReadUInt64BigEndian(high);
            BinaryPrimitives.WriteUInt64BigEndian(high, unchecked(highValue + 1));
        }
    }

    private void ComputeMac(uint sequenceNumber, ReadOnlySpan<byte> data, Span<byte> mac)
    {
        Span<byte> seq = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(seq, sequenceNumber);

        using var hmac = IncrementalHash.CreateHMAC(_macName, _macKey);
        hmac.AppendData(seq);
        hmac.AppendData(data);
        hmac.GetHashAndReset(mac);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        CryptographicOperations.ZeroMemory(_counter);
        CryptographicOperations.ZeroMemory(_macKey);
        _aes.Dispose();
    }
}
