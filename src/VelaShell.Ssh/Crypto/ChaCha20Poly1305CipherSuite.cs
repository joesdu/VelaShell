// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH PROTOCOL.chacha20poly1305 —— 两把密钥、长度字段单独加密、
//                                        nonce 为 8 字节大端的报文序号
//   RFC 8439 §2.5                     —— Poly1305
//   D. J. Bernstein, ChaCha           —— 64 位 nonce 的原始布局(不是 RFC 8439 的 96 位)
//   行为规格:                         velashell-docs/zh/ssh/spec/01-transport-framing.md §2.1 ①
//
// 原语走 BouncyCastle(架构原则 6):BCL 只有完整的 RFC 8439 AEAD,
// 而这里要的是「raw ChaCha20 块函数 + 独立 Poly1305」这种另一种组合方式。

using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Parameters;

namespace VelaShell.Ssh.Crypto;

/// <summary>
/// <c>chacha20-poly1305@openssh.com</c>。
/// </summary>
/// <remarks>
/// <para>
/// 这不是 RFC 8439 的那个 AEAD，是 OpenSSH 的另一种组合方式，三处不同：
/// </para>
/// <list type="number">
///   <item><b>两把独立密钥。</b>64 字节密钥材料切成两半：
///   前 32 字节加密载荷并产出 Poly1305 密钥，后 32 字节**只**加密那 4 字节长度。</item>
///   <item><b>长度字段被单独加密。</b>因此收包时要先用第二把钥解出长度，
///   才知道这一帧有多长 —— 这就是 <see cref="CipherSuiteShape.LengthIsEncrypted"/>。</item>
///   <item><b>nonce 是 8 字节大端的报文序号</b>，配原始 ChaCha 的 64 位 nonce 布局，
///   不是 RFC 8439 的 96 位。</item>
/// </list>
/// <para>
/// <b>收包顺序是安全属性</b>：先验 tag，再解密载荷。反过来等于给对端提供一个解密预言机。
/// </para>
/// </remarks>
internal sealed class ChaCha20Poly1305CipherSuite : ISshCipherSuite
{
    /// <summary>密钥材料字节数（两把 32 字节的钥）。</summary>
    public const int KeyMaterialBytes = 64;

    private const int TagBytes = 16;
    private const int PolyKeyBytes = 32;
    private const int ChaChaBlockBytes = 64;
    private const int Rounds = 20;

    // 两个引擎与一个 Poly1305 常驻，每个报文只换 nonce（ParametersWithIV 的密钥给 null = 沿用已装的密钥）。
    // 曾经每个报文新建三个引擎、每次都把密钥重新克隆装入 —— 一个 32 KiB 的报文约 1.4 KB 的分配。
    // 一个套件只服务一个方向、只在一个线程上用，所以复用是安全的。
    private readonly ChaChaEngine _payloadEngine = new(Rounds);   // K_2：载荷 + Poly1305 密钥
    private readonly ChaChaEngine _lengthEngine = new(Rounds);    // K_1：只加密长度字段
    private readonly Poly1305 _poly = new();
    private readonly byte[] _nonce = new byte[8];
    private bool _disposed;

    /// <summary>用 64 字节密钥材料构造。</summary>
    /// <param name="keyMaterial">
    /// 64 字节。前 32 字节是载荷密钥，后 32 字节是长度密钥 —— 这个次序由
    /// OpenSSH 规定，颠倒过来会与对端完全对不上。
    /// </param>
    public ChaCha20Poly1305CipherSuite(ReadOnlySpan<byte> keyMaterial)
    {
        if (keyMaterial.Length != KeyMaterialBytes)
        {
            throw new ArgumentException(
                $"chacha20-poly1305 需要 {KeyMaterialBytes} 字节密钥材料。", nameof(keyMaterial));
        }

        byte[] payloadKey = keyMaterial[..32].ToArray();
        byte[] lengthKey = keyMaterial[32..].ToArray();
        try
        {
            _payloadEngine.Init(forEncryption: true, new ParametersWithIV(new KeyParameter(payloadKey), _nonce));
            _lengthEngine.Init(forEncryption: true, new ParametersWithIV(new KeyParameter(lengthKey), _nonce));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payloadKey);
            CryptographicOperations.ZeroMemory(lengthKey);
        }
    }

    /// <inheritdoc />
    public CipherSuiteShape Shape { get; } = new()
    {
        LengthIsEncrypted = true,
        AadBytes = 0,
        TagBytes = TagBytes,
        BlockBytes = 8,
        // 对齐不含长度字段 —— 与 AES-GCM 同理。
        LengthInAlignment = false,
        EncryptThenMac = true,
        IsEncrypted = true,
        // 长度只有 4 字节，且 ChaCha20 是流密码，读 4 字节就能解出来。
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

        // ① 长度字段：用 K_1，counter 从 0 开始。
        Span<byte> lengthField = frame[..SshPacketFormat.LengthFieldBytes];
        BinaryPrimitives.WriteUInt32BigEndian(lengthField, (uint)packetLength);
        EncryptLengthField(lengthField, sequenceNumber);

        // ② 载荷区：先取 Poly1305 密钥（K_2 counter 0），引擎随即停在 counter 1。
        ChaChaEngine engine = Rewind(_payloadEngine, sequenceNumber);
        Span<byte> polyKey = stackalloc byte[PolyKeyBytes];
        try
        {
            DerivePolyKey(engine, polyKey);

            byte[] rented = ArrayPool<byte>.Shared.Rent(packetLength);
            try
            {
                Span<byte> plaintext = rented.AsSpan(0, packetLength);
                plaintext[0] = (byte)padding;
                payload.CopyTo(plaintext[SshPacketFormat.PaddingLengthFieldBytes..]);
                RandomNumberGenerator.Fill(plaintext[^padding..]);

                Span<byte> ciphertext = frame.Slice(SshPacketFormat.LengthFieldBytes, packetLength);
                engine.ProcessBytes(plaintext, ciphertext);
                CryptographicOperations.ZeroMemory(plaintext);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }

            // ③ tag 覆盖**整段密文，含那 4 字节已加密的长度**。
            ComputeTag(polyKey, frame[..(SshPacketFormat.LengthFieldBytes + packetLength)], frame[^TagBytes..]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(polyKey);
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

        if (input.Length < SshPacketFormat.LengthFieldBytes)
        {
            return SshOpenStatus.NeedMoreData;
        }

        // ① 解出长度。ChaCha20 是流密码，这一步不改变任何持久状态 ——
        //    每一帧的密钥流由它自己的序号决定，所以 NeedMoreData 时重来一次结果相同。
        Span<byte> lengthField = stackalloc byte[SshPacketFormat.LengthFieldBytes];
        input.Slice(0, SshPacketFormat.LengthFieldBytes).CopyTo(lengthField);
        Span<byte> plainLength = stackalloc byte[SshPacketFormat.LengthFieldBytes];
        DecryptLengthField(lengthField, sequenceNumber, plainLength);
        uint packetLength = BinaryPrimitives.ReadUInt32BigEndian(plainLength);

        // 长度此刻还**没有被认证**（tag 要等整帧收齐才能验）。所以必须先做范围检查 ——
        // 不检查就等于让一个还没被验证过的数字决定我们要等多少字节、分配多少内存。
        if (packetLength > (uint)maxPacketLength
            || packetLength < SshPacketFormat.PaddingLengthFieldBytes + SshPacketFormat.MinimumPadding
            || packetLength % 8 != 0)
        {
            throw new SshFrameFormatException(
                $"chacha20-poly1305 帧头非法：packet_length={packetLength}（上限 {maxPacketLength}，须为 8 的倍数）。");
        }

        long total = SshPacketFormat.LengthFieldBytes + packetLength + TagBytes;
        if (input.Length < total)
        {
            return SshOpenStatus.NeedMoreData;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent((int)total);
        Span<byte> polyKey = stackalloc byte[PolyKeyBytes];
        ChaChaEngine engine = Rewind(_payloadEngine, sequenceNumber);
        try
        {
            Span<byte> frame = rented.AsSpan(0, (int)total);
            input.Slice(0, total).CopyTo(frame);

            DerivePolyKey(engine, polyKey);

            // ② **先验 tag，再解密。** 顺序反过来就是一个解密预言机。
            Span<byte> expectedTag = stackalloc byte[TagBytes];
            ComputeTag(polyKey, frame[..(SshPacketFormat.LengthFieldBytes + (int)packetLength)], expectedTag);
            if (!CryptographicOperations.FixedTimeEquals(expectedTag, frame[^TagBytes..]))
            {
                throw SshFrameFormatException.IntegrityCheckFailed();
            }

            // ③ 解密载荷区（引擎已在 counter 1）。
            Span<byte> ciphertext = frame.Slice(SshPacketFormat.LengthFieldBytes, (int)packetLength);
            engine.ProcessBytes(ciphertext, ciphertext);

            byte paddingLength = ciphertext[0];
            int payloadLength = (int)packetLength - SshPacketFormat.PaddingLengthFieldBytes - paddingLength;
            if (paddingLength < SshPacketFormat.MinimumPadding || payloadLength < 0)
            {
                throw new SshFrameFormatException($"padding_length {paddingLength} 非法。");
            }

            if (payloadLength > 0)
            {
                ciphertext.Slice(SshPacketFormat.PaddingLengthFieldBytes, payloadLength)
                          .CopyTo(payload.GetSpan(payloadLength));
                payload.Advance(payloadLength);
            }

            CryptographicOperations.ZeroMemory(frame);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(polyKey);
            ArrayPool<byte>.Shared.Return(rented);
        }

        consumed = total;
        return SshOpenStatus.Opened;
    }

    /// <summary>
    /// 把引擎拨回 counter 0、换上这个报文的 nonce（8 字节大端的报文序号），密钥不动。
    /// </summary>
    /// <remarks>
    /// 序号是 32 位，但 nonce 字段是 64 位 —— 高 32 位恒为零
    /// （OpenSSH PROTOCOL.chacha20poly1305：nonce 就是 64 位的报文序号）。
    /// </remarks>
    private ChaChaEngine Rewind(ChaChaEngine engine, uint sequenceNumber)
    {
        BinaryPrimitives.WriteUInt64BigEndian(_nonce, sequenceNumber);
        engine.Init(forEncryption: true, new ParametersWithIV(null, _nonce));
        return engine;
    }

    /// <summary>
    /// 取 Poly1305 的一次性密钥，并把引擎推进到 counter 1。
    /// </summary>
    /// <remarks>
    /// 处理满 64 字节（一整个 ChaCha 块）之后引擎自然停在 counter 1，
    /// 正是载荷该用的计数器 —— 不需要显式设置计数器，也就不会设错。
    /// 密钥只取前 32 字节，后 32 字节按 RFC 8439 §2.6 丢弃。
    /// </remarks>
    private static void DerivePolyKey(ChaChaEngine engine, Span<byte> polyKey)
    {
        Span<byte> block = stackalloc byte[ChaChaBlockBytes];
        block.Clear();
        engine.ProcessBytes(block, block);
        block[..PolyKeyBytes].CopyTo(polyKey);
        CryptographicOperations.ZeroMemory(block);
    }

    private void EncryptLengthField(Span<byte> lengthField, uint sequenceNumber)
    {
        Rewind(_lengthEngine, sequenceNumber).ProcessBytes(lengthField, lengthField);
    }

    private void DecryptLengthField(ReadOnlySpan<byte> encrypted, uint sequenceNumber, Span<byte> plain)
    {
        Rewind(_lengthEngine, sequenceNumber).ProcessBytes(encrypted, plain);
    }

    private void ComputeTag(ReadOnlySpan<byte> polyKey, ReadOnlySpan<byte> data, Span<byte> tag)
    {
        _poly.Init(new KeyParameter(polyKey));
        _poly.BlockUpdate(data);
        _poly.DoFinal(tag);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        // 引擎里留着密钥展开后的状态：装一把全零的钥把它覆盖掉。
        byte[] zeroKey = new byte[32];
        _payloadEngine.Init(forEncryption: true, new ParametersWithIV(new KeyParameter(zeroKey), _nonce));
        _lengthEngine.Init(forEncryption: true, new ParametersWithIV(new KeyParameter(zeroKey), _nonce));
    }
}
