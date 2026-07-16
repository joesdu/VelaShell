using VelaShell.Core.ZModem.Abstractions;

namespace VelaShell.Core.ZModem.Protocol;

/// <summary>帧读取结果的分类。</summary>
public enum ZModemReadStatus
{
    /// <summary>成功读到一个校验通过的帧头。</summary>
    Header,

    /// <summary>检测到取消序列(连续 CAN),应中止会话。</summary>
    Cancelled,

    /// <summary>底层通道结束(EOF),无更多数据。</summary>
    EndOfStream,

    /// <summary>帧头 CRC 校验失败(调用方通常回 ZNAK 或忽略)。</summary>
    CrcError,

    /// <summary>等待帧头期间超时。</summary>
    Timeout
}

/// <summary>一次帧头读取的结果。</summary>
/// <param name="Status">读取状态。</param>
/// <param name="Header">当 <see cref="Status" /> 为 <see cref="ZModemReadStatus.Header" /> 时有效。</param>
/// <param name="Format">读到的帧头编码形态(决定后续数据子包用 CRC16 还是 CRC32)。</param>
public readonly record struct ZModemHeaderResult(
    ZModemReadStatus Status,
    ZModemHeader Header = default,
    ZModemHeaderFormat Format = ZModemHeaderFormat.Binary16);

/// <summary>
/// 增量式 ZMODEM 帧读取器:从 <see cref="IByteDuplex" /> 拉取字节并缓冲,
/// 逐字节按需从通道补充,因此对「帧头被切分到多个网络分片」天然免疫。
/// 提供 ZDLE 感知的读原语,既服务帧头解析(本类),也服务数据子包解析(P0-4)。
/// </summary>
public sealed class ZModemFrameReader(IByteDuplex duplex)
{
    private readonly IByteDuplex _duplex = duplex ?? throw new ArgumentNullException(nameof(duplex));
    private byte[] _buffer = [];
    private int _pos;
    private bool _eof;

    /// <summary>把一段初始字节(如检测阶段截获的引导字节)预置到缓冲区最前。</summary>
    /// <param name="seed">要预置的字节。</param>
    public void Seed(ReadOnlySpan<byte> seed)
    {
        if (seed.IsEmpty)
        {
            return;
        }
        int remaining = _buffer.Length - _pos;
        byte[] merged = new byte[remaining + seed.Length];
        seed.CopyTo(merged);
        Array.Copy(_buffer, _pos, merged, seed.Length, remaining);
        _buffer = merged;
        _pos = 0;
    }

    /// <summary>拉取下一个原始字节;缓冲耗尽时从通道补充。EOF 返回 <c>-1</c>。</summary>
    private async ValueTask<int> NextRawByteAsync(CancellationToken ct)
    {
        while (_pos >= _buffer.Length)
        {
            if (_eof)
            {
                return -1;
            }
            ReadOnlyMemory<byte> chunk = await _duplex.ReadAsync(ct).ConfigureAwait(false);
            if (chunk.IsEmpty)
            {
                _eof = true;
                return -1;
            }
            _buffer = chunk.ToArray();
            _pos = 0;
        }
        return _buffer[_pos++];
    }

    /// <summary>
    /// 读取一个 ZDLE 反转义后的数据字节,并给出其语义(数据 / 子包终止 / 取消等)。
    /// 数据子包解析器(P0-4)基于此逐字节推进。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>反转义后的字节及其语义标记;通道 EOF 时 Kind 为 <see cref="ZdleTokenKind.Invalid" /> 且值为 0xFF 哨兵。</returns>
    public async ValueTask<(ZdleToken Token, bool EndOfStream)> ReadEscapedByteAsync(CancellationToken ct)
    {
        int raw = await NextRawByteAsync(ct).ConfigureAwait(false);
        if (raw < 0)
        {
            return (new ZdleToken(ZdleTokenKind.Invalid, 0), true);
        }
        if (raw != ZModemConstants.ZDLE)
        {
            return (new ZdleToken(ZdleTokenKind.DataByte, (byte)raw), false);
        }
        int following = await NextRawByteAsync(ct).ConfigureAwait(false);
        if (following < 0)
        {
            return (new ZdleToken(ZdleTokenKind.Invalid, 0), true);
        }
        return (ZdleCodec.ClassifyEscaped((byte)following), false);
    }

    /// <summary>
    /// 扫描并读取下一个帧头。会跳过帧前的噪声(提示符文本、CR/LF、XON 等),
    /// 直到遇到 ZPAD…ZDLE 引导序列,并识别取消序列与通道结束。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>帧头读取结果。</returns>
    public async ValueTask<ZModemHeaderResult> ReadHeaderAsync(CancellationToken ct)
    {
        // 步骤 1:定位帧引导。0x18 同时是 ZDLE 与 CAN,故遇到 0x18 时须窥视其后一字节来消歧:
        // 跟随格式字节(ZHEX/ZBIN/ZBIN32)即为帧头;再来一个 0x18 则计入取消序列(连续 5 个即中止)。
        int can = 0;
        while (true)
        {
            int b = await NextRawByteAsync(ct).ConfigureAwait(false);
            if (b < 0)
            {
                return new(ZModemReadStatus.EndOfStream);
            }
            if (b != ZModemConstants.ZDLE)
            {
                // 噪声 / ZPAD / CR / LF / XON:重置取消计数并继续找引导。
                can = 0;
                continue;
            }

            // 命中 0x18(ZDLE 或 CAN):累计取消计数,再窥视其后一字节决定含义。
            if (++can >= 5)
            {
                return new(ZModemReadStatus.Cancelled);
            }
            int kind = await NextRawByteAsync(ct).ConfigureAwait(false);
            if (kind < 0)
            {
                return new(ZModemReadStatus.EndOfStream);
            }
            switch (kind)
            {
                case ZModemConstants.ZHEX:
                    return await ReadHexHeaderAsync(ct).ConfigureAwait(false);
                case ZModemConstants.ZBIN:
                    return await ReadBinaryHeaderAsync(ZModemHeaderFormat.Binary16, ct).ConfigureAwait(false);
                case ZModemConstants.ZBIN32:
                    return await ReadBinaryHeaderAsync(ZModemHeaderFormat.Binary32, ct).ConfigureAwait(false);
                case ZModemConstants.CAN: // 又一个 0x18:属取消序列,计数后继续。
                    if (++can >= 5)
                    {
                        return new(ZModemReadStatus.Cancelled);
                    }
                    continue;
                default:
                    // 孤立 ZDLE 后跟随未知字节:视作噪声,重置并继续扫描。
                    can = 0;
                    continue;
            }
        }
    }

    private async ValueTask<ZModemHeaderResult> ReadHexHeaderAsync(CancellationToken ct)
    {
        // 5 个头字节 + 2 个 CRC 字节,每个用两位 ASCII 十六进制表示。
        byte[] raw = new byte[7];
        for (int i = 0; i < 7; i++)
        {
            int hi = await NextRawByteAsync(ct).ConfigureAwait(false);
            int lo = await NextRawByteAsync(ct).ConfigureAwait(false);
            if (hi < 0 || lo < 0)
            {
                return new(ZModemReadStatus.EndOfStream);
            }
            if (!ZModemFrameWriter.TryParseHexByte((byte)hi, (byte)lo, out byte value))
            {
                return new(ZModemReadStatus.CrcError);
            }
            raw[i] = value;
        }

        ushort expected = (ushort)((raw[5] << 8) | raw[6]);
        ushort actual = Crc16Xmodem.Compute(raw.AsSpan(0, 5));
        if (expected != actual)
        {
            return new(ZModemReadStatus.CrcError);
        }
        // 尾随的 CR/LF/XON 留给下一次 ReadHeader 的噪声跳过逻辑消化。
        return new(
            ZModemReadStatus.Header,
            new((ZModemFrameType)raw[0], raw[1], raw[2], raw[3], raw[4]),
            ZModemHeaderFormat.Hex);
    }

    private async ValueTask<ZModemHeaderResult> ReadBinaryHeaderAsync(ZModemHeaderFormat format, CancellationToken ct)
    {
        int crcLen = format == ZModemHeaderFormat.Binary32 ? 4 : 2;
        int total = 5 + crcLen;
        byte[] raw = new byte[9];
        for (int i = 0; i < total; i++)
        {
            (ZdleToken token, bool eof) = await ReadEscapedByteAsync(ct).ConfigureAwait(false);
            if (eof)
            {
                return new(ZModemReadStatus.EndOfStream);
            }
            if (token.Kind == ZdleTokenKind.Cancel)
            {
                return new(ZModemReadStatus.Cancelled);
            }
            if (token.Kind != ZdleTokenKind.DataByte)
            {
                return new(ZModemReadStatus.CrcError);
            }
            raw[i] = token.Value;
        }

        ReadOnlySpan<byte> core = raw.AsSpan(0, 5);
        if (format == ZModemHeaderFormat.Binary32)
        {
            uint expected = (uint)(raw[5] | (raw[6] << 8) | (raw[7] << 16) | (raw[8] << 24));
            if (Crc32ZModem.Compute(core) != expected)
            {
                return new(ZModemReadStatus.CrcError);
            }
        }
        else
        {
            ushort expected = (ushort)((raw[5] << 8) | raw[6]);
            if (Crc16Xmodem.Compute(core) != expected)
            {
                return new(ZModemReadStatus.CrcError);
            }
        }
        return new(
            ZModemReadStatus.Header,
            new((ZModemFrameType)raw[0], raw[1], raw[2], raw[3], raw[4]),
            format);
    }
}
