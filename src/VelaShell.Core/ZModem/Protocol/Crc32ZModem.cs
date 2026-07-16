namespace VelaShell.Core.ZModem.Protocol;

/// <summary>
/// ZMODEM 使用的 CRC-32(多项式 0xEDB88320 反转形式,初值 0xFFFFFFFF,输出取反),
/// 与 zlib / PKZIP / ISO-HDLC 的 CRC-32 一致。用于 ZBIN32 帧头与 CRC32 数据子包。
/// </summary>
/// <remarks>
/// 规范里 CRC32 在链路上以「运行值取反」的形式随数据一起传输,校验时用同一算法把
/// 收到的 4 个 CRC 字节一并喂入,若最终得到魔数 <see cref="ResidualMagic" /> 即校验通过。
/// </remarks>
public static class Crc32ZModem
{
    private const uint Polynomial = 0xEDB88320;
    private const uint InitialValue = 0xFFFFFFFF;

    /// <summary>把接收到的数据 + 其后 4 个 CRC 字节一起累加后的期望残值(校验通过的判据)。</summary>
    public const uint ResidualMagic = 0xDEBB20E3;

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0u; i < 256; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0
                    ? (crc >> 1) ^ Polynomial
                    : crc >> 1;
            }
            table[i] = crc;
        }
        return table;
    }

    /// <summary>计算整段数据的 ZMODEM CRC-32(含初值与最终取反),得到可直接写入帧的值。</summary>
    /// <param name="data">参与校验的字节序列。</param>
    /// <returns>32 位校验值(已做最终取反)。</returns>
    public static uint Compute(ReadOnlySpan<byte> data) => UpdateRunning(InitialValue, data) ^ 0xFFFFFFFF;

    /// <summary>
    /// 在「运行值」(未做最终取反)上累加一段数据。首次调用请传
    /// <c>0xFFFFFFFF</c> 作为初值;取最终 CRC 时对返回值做一次 <c>^ 0xFFFFFFFF</c>。
    /// </summary>
    /// <param name="crc">上一次的运行 CRC 值。</param>
    /// <param name="data">追加参与校验的字节序列。</param>
    /// <returns>更新后的运行 CRC 值(未取反)。</returns>
    public static uint UpdateRunning(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
        }
        return crc;
    }

    /// <summary>在「运行值」上累加单个字节(不做初值/取反处理)。</summary>
    /// <param name="crc">上一次的运行 CRC 值。</param>
    /// <param name="b">追加参与校验的字节。</param>
    /// <returns>更新后的运行 CRC 值(未取反)。</returns>
    public static uint UpdateRunning(uint crc, byte b) => (crc >> 8) ^ Table[(crc ^ b) & 0xFF];

    /// <summary>ZMODEM CRC-32 的运行初值 <c>0xFFFFFFFF</c>,供分块累加起始使用。</summary>
    public static uint Initial => InitialValue;
}
