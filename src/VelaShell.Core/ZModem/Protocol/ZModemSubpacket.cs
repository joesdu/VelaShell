namespace VelaShell.Core.ZModem.Protocol;

/// <summary>数据子包结束后对端应采取的动作(由帧结束符 ZCRCE/G/Q/W 决定)。</summary>
public enum ZModemSubpacketEnd
{
    /// <summary>ZCRCE:此帧结束,发送方不再续发,无需应答。</summary>
    EndNoAck,

    /// <summary>ZCRCG:帧继续(还有后续子包),无需应答。</summary>
    MoreNoAck,

    /// <summary>ZCRCQ:帧继续,需要接收方回 ZACK。</summary>
    MoreAck,

    /// <summary>ZCRCW:此帧结束,需要接收方回 ZACK。</summary>
    EndAck
}

/// <summary>数据子包读取结果的分类。</summary>
public enum ZModemSubpacketStatus
{
    /// <summary>成功读到一个校验通过的数据子包。</summary>
    Ok,

    /// <summary>子包 CRC 校验失败。</summary>
    CrcError,

    /// <summary>读取途中检测到取消序列。</summary>
    Cancelled,

    /// <summary>底层通道结束(EOF)。</summary>
    EndOfStream,

    /// <summary>等待子包字节期间超时。</summary>
    Timeout
}

/// <summary>一次数据子包读取的结果。</summary>
/// <param name="Status">读取状态。</param>
/// <param name="Data">子包负载(已反转义),仅在 <see cref="Status" /> 为 <see cref="ZModemSubpacketStatus.Ok" /> 时有效。</param>
/// <param name="End">帧结束语义,决定是否续读子包 / 是否需要应答。</param>
public readonly record struct ZModemSubpacketResult(
    ZModemSubpacketStatus Status,
    byte[] Data,
    ZModemSubpacketEnd End = ZModemSubpacketEnd.EndNoAck);

/// <summary>
/// ZMODEM 数据子包的编解码。子包格式为:[转义后的数据字节…] ZDLE 帧结束符(ZCRCE/G/Q/W) CRC。
/// CRC 覆盖「原始数据字节 + 帧结束符字节」,随后 CRC 字节本身也参与 ZDLE 转义。
/// CRC16 走 <see cref="Crc16Xmodem" />(大端上链),CRC32 走 <see cref="Crc32ZModem" />(小端上链)。
/// </summary>
public static class ZModemSubpacket
{
    private static byte FrameEndByte(ZModemSubpacketEnd end) =>
        end switch
        {
            ZModemSubpacketEnd.EndNoAck => ZModemConstants.ZCRCE,
            ZModemSubpacketEnd.MoreNoAck => ZModemConstants.ZCRCG,
            ZModemSubpacketEnd.MoreAck => ZModemConstants.ZCRCQ,
            ZModemSubpacketEnd.EndAck => ZModemConstants.ZCRCW,
            _ => throw new ArgumentOutOfRangeException(nameof(end))
        };

    private static ZModemSubpacketEnd EndFromToken(ZdleTokenKind kind) =>
        kind switch
        {
            ZdleTokenKind.SubpacketEndNoAck => ZModemSubpacketEnd.EndNoAck,
            ZdleTokenKind.SubpacketMoreNoAck => ZModemSubpacketEnd.MoreNoAck,
            ZdleTokenKind.SubpacketMoreAck => ZModemSubpacketEnd.MoreAck,
            ZdleTokenKind.SubpacketEndAck => ZModemSubpacketEnd.EndAck,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    /// <summary>把一段数据编码为链路上的数据子包(含 ZDLE 转义与 CRC)。</summary>
    /// <param name="data">子包负载(原始未转义字节)。</param>
    /// <param name="end">帧结束语义(决定帧结束符)。</param>
    /// <param name="useCrc32">true 用 CRC32,false 用 CRC16。</param>
    /// <param name="escapeAllControl">是否转义全部控制字符(<c>Zctlesc</c>)。</param>
    /// <returns>可直接写入传输的子包字节。</returns>
    public static byte[] Write(
        ReadOnlySpan<byte> data,
        ZModemSubpacketEnd end,
        bool useCrc32,
        bool escapeAllControl = false)
    {
        byte frameEnd = FrameEndByte(end);
        var output = new List<byte>(data.Length + 8);

        // 1) 转义后的负载。
        ZdleCodec.Escape(data, output, escapeAllControl);

        // 2) ZDLE + 帧结束符(不转义;帧结束符本身即为转义序列的一部分)。
        output.Add(ZModemConstants.ZDLE);
        output.Add(frameEnd);

        // 3) CRC 覆盖 (原始数据 + 帧结束符字节),CRC 字节再做 ZDLE 转义。
        if (useCrc32)
        {
            uint running = Crc32ZModem.Initial;
            running = Crc32ZModem.UpdateRunning(running, data);
            running = Crc32ZModem.UpdateRunning(running, frameEnd);
            uint crc = running ^ 0xFFFFFFFF;
            ZdleCodec.EscapeByte((byte)(crc & 0xFF), output, escapeAllControl);
            ZdleCodec.EscapeByte((byte)((crc >> 8) & 0xFF), output, escapeAllControl);
            ZdleCodec.EscapeByte((byte)((crc >> 16) & 0xFF), output, escapeAllControl);
            ZdleCodec.EscapeByte((byte)((crc >> 24) & 0xFF), output, escapeAllControl);
        }
        else
        {
            ushort crc = 0;
            crc = Crc16Xmodem.Update(crc, data);
            crc = Crc16Xmodem.Update(crc, frameEnd);
            ZdleCodec.EscapeByte((byte)(crc >> 8), output, escapeAllControl);
            ZdleCodec.EscapeByte((byte)(crc & 0xFF), output, escapeAllControl);
        }
        return [.. output];
    }

    /// <summary>
    /// 从帧读取器增量读取一个数据子包:逐字节反转义直到遇到帧结束符,再读入并校验 CRC。
    /// </summary>
    /// <param name="reader">已定位在数据子包起点的帧读取器。</param>
    /// <param name="useCrc32">当前帧是否使用 CRC32(由 ZDATA 帧头形态决定)。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>子包读取结果。</returns>
    public static async ValueTask<ZModemSubpacketResult> ReadAsync(
        ZModemFrameReader reader,
        bool useCrc32,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var data = new List<byte>(1024);
        ZModemSubpacketEnd end;

        // 子包 CRC 覆盖「原始数据 + 帧结束符字节」,故必须捕获终止符原始字节并并入 CRC ——
        // 传 0 会让读侧算出 CRC(data+0) 而写侧是 CRC(data+frameEnd),每个子包都判 CRC 错、
        // 触发无休止重传(表现为文件传输永久卡死)。
        byte frameEnd;
        // 步骤 1:累积负载,直到遇到子包终止符。
        while (true)
        {
            (ZdleToken token, bool eof) = await reader.ReadEscapedByteAsync(ct).ConfigureAwait(false);
            if (eof)
            {
                return new(ZModemSubpacketStatus.EndOfStream, []);
            }
            switch (token.Kind)
            {
                case ZdleTokenKind.DataByte:
                    data.Add(token.Value);
                    continue;
                case ZdleTokenKind.SubpacketEndNoAck:
                case ZdleTokenKind.SubpacketMoreNoAck:
                case ZdleTokenKind.SubpacketMoreAck:
                case ZdleTokenKind.SubpacketEndAck:
                    frameEnd = token.Value; // 终止符字节本身参与 CRC。
                    end = EndFromToken(token.Kind);
                    break;
                case ZdleTokenKind.Cancel:
                    return new(ZModemSubpacketStatus.Cancelled, []);
                case ZdleTokenKind.Rub0:
                case ZdleTokenKind.Rub1:
                    // ZRUB0/1 在数据子包语境下作为普通数据字节处理。
                    data.Add(token.Value);
                    continue;
                default:
                    return new(ZModemSubpacketStatus.CrcError, []);
            }
            break;
        }

        // 步骤 2:读入并校验 CRC(CRC 字节亦经 ZDLE 转义)。
        return useCrc32
            ? await VerifyCrc32Async(reader, data, frameEnd, end, ct).ConfigureAwait(false)
            : await VerifyCrc16Async(reader, data, frameEnd, end, ct).ConfigureAwait(false);
    }

    private static async ValueTask<byte[]?> ReadCrcBytesAsync(ZModemFrameReader reader, int count, CancellationToken ct)
    {
        byte[] bytes = new byte[count];
        for (int i = 0; i < count; i++)
        {
            (ZdleToken token, bool eof) = await reader.ReadEscapedByteAsync(ct).ConfigureAwait(false);
            if (eof || token.Kind != ZdleTokenKind.DataByte)
            {
                return null;
            }
            bytes[i] = token.Value;
        }
        return bytes;
    }

    private static async ValueTask<ZModemSubpacketResult> VerifyCrc16Async(
        ZModemFrameReader reader,
        List<byte> data,
        byte frameEnd,
        ZModemSubpacketEnd end,
        CancellationToken ct)
    {
        byte[]? crcBytes = await ReadCrcBytesAsync(reader, 2, ct).ConfigureAwait(false);
        if (crcBytes is null)
        {
            return new(ZModemSubpacketStatus.EndOfStream, []);
        }
        ushort expected = (ushort)((crcBytes[0] << 8) | crcBytes[1]);
        ushort actual = 0;
        actual = Crc16Xmodem.Update(actual, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(data));
        actual = Crc16Xmodem.Update(actual, frameEnd);
        return expected == actual
            ? new(ZModemSubpacketStatus.Ok, [.. data], end)
            : new(ZModemSubpacketStatus.CrcError, []);
    }

    private static async ValueTask<ZModemSubpacketResult> VerifyCrc32Async(
        ZModemFrameReader reader,
        List<byte> data,
        byte frameEnd,
        ZModemSubpacketEnd end,
        CancellationToken ct)
    {
        byte[]? crcBytes = await ReadCrcBytesAsync(reader, 4, ct).ConfigureAwait(false);
        if (crcBytes is null)
        {
            return new(ZModemSubpacketStatus.EndOfStream, []);
        }
        uint expected = (uint)(crcBytes[0] | (crcBytes[1] << 8) | (crcBytes[2] << 16) | (crcBytes[3] << 24));
        uint running = Crc32ZModem.Initial;
        running = Crc32ZModem.UpdateRunning(running, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(data));
        running = Crc32ZModem.UpdateRunning(running, frameEnd);
        uint actual = running ^ 0xFFFFFFFF;
        return expected == actual
            ? new(ZModemSubpacketStatus.Ok, [.. data], end)
            : new(ZModemSubpacketStatus.CrcError, []);
    }
}
