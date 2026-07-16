namespace VelaShell.Core.ZModem.Protocol;

/// <summary>帧头在链路上的三种编码形态。</summary>
public enum ZModemHeaderFormat
{
    /// <summary>十六进制帧头(ASCII hex,CRC16),用于启动 / 控制帧。</summary>
    Hex,

    /// <summary>二进制帧头 + CRC16。</summary>
    Binary16,

    /// <summary>二进制帧头 + CRC32。</summary>
    Binary32
}

/// <summary>
/// 一个 ZMODEM 帧头:帧类型 + 4 字节参数(ZP0..ZP3)。参数按帧类型解释为位置偏移
/// (ZDATA/ZEOF/ZRPOS)、能力标志(ZRINIT/ZSINIT)等,采用小端序(ZP0 为最低字节)。
/// </summary>
public readonly record struct ZModemHeader
{
    /// <summary>用给定帧类型与 4 字节参数构造帧头。</summary>
    /// <param name="type">帧类型。</param>
    /// <param name="p0">参数字节 0(最低有效字节)。</param>
    /// <param name="p1">参数字节 1。</param>
    /// <param name="p2">参数字节 2。</param>
    /// <param name="p3">参数字节 3(最高有效字节)。</param>
    public ZModemHeader(ZModemFrameType type, byte p0, byte p1, byte p2, byte p3)
    {
        Type = type;
        P0 = p0;
        P1 = p1;
        P2 = p2;
        P3 = p3;
    }

    /// <summary>帧类型(帧头首字节)。</summary>
    public ZModemFrameType Type { get; }

    /// <summary>参数字节 0(最低有效字节)。</summary>
    public byte P0 { get; }

    /// <summary>参数字节 1。</summary>
    public byte P1 { get; }

    /// <summary>参数字节 2。</summary>
    public byte P2 { get; }

    /// <summary>参数字节 3(最高有效字节)。</summary>
    public byte P3 { get; }

    /// <summary>把 4 个参数字节按小端序组合成 32 位无符号整数(位置 / 计数类字段用)。</summary>
    public uint Position => (uint)(P0 | (P1 << 8) | (P2 << 16) | (P3 << 24));

    /// <summary>用一个 32 位位置 / 计数值(小端拆分)构造帧头。</summary>
    /// <param name="type">帧类型。</param>
    /// <param name="position">位置或计数值。</param>
    /// <returns>参数字节由 <paramref name="position" /> 小端拆分得到的帧头。</returns>
    public static ZModemHeader WithPosition(ZModemFrameType type, uint position) =>
        new(type,
            (byte)(position & 0xFF),
            (byte)((position >> 8) & 0xFF),
            (byte)((position >> 16) & 0xFF),
            (byte)((position >> 24) & 0xFF));

    /// <summary>构造一个 4 参数全 0 的帧头(ZRQINIT/ZFIN/ZSKIP 等无参帧用)。</summary>
    /// <param name="type">帧类型。</param>
    /// <returns>参数全 0 的帧头。</returns>
    public static ZModemHeader Empty(ZModemFrameType type) => new(type, 0, 0, 0, 0);
}
