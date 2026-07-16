namespace VelaShell.Core.ZModem.Protocol;

/// <summary>
/// ZMODEM 协议的字节级常量:帧引导字符、头部编码类型、数据子包结束符与控制字符。
/// 取值遵循 Chuck Forsberg 的 ZMODEM 规范(zmodem.txt / lrzsz 实现),用于与 Linux
/// <c>sz</c>/<c>rz</c> 互操作。全部为原始字节,不做任何字符编码转换。
/// </summary>
public static class ZModemConstants
{
    /// <summary>帧填充字符 <c>'*'</c>(0x2A):所有帧头以一或两个 ZPAD 引导。</summary>
    public const byte ZPAD = 0x2A;

    /// <summary>ZMODEM 转义引导字符(0x18,即 CAN):其后一字节做 <c>^0x40</c> 反转义。</summary>
    public const byte ZDLE = 0x18;

    /// <summary>被转义后的 ZDLE 自身(<c>ZDLE ^ 0x40</c> = 0x58,<c>'X'</c>)。</summary>
    public const byte ZDLEE = 0x58;

    /// <summary>二进制帧头标识 <c>'A'</c>(0x41):头部为二进制 + CRC16。</summary>
    public const byte ZBIN = 0x41;

    /// <summary>十六进制帧头标识 <c>'B'</c>(0x42):头部以 ASCII 十六进制编码 + CRC16。</summary>
    public const byte ZHEX = 0x42;

    /// <summary>二进制帧头标识 <c>'C'</c>(0x43):头部为二进制 + CRC32。</summary>
    public const byte ZBIN32 = 0x43;

    /// <summary>数据子包结束符 <c>'h'</c>(0x68):帧结束,不需要对端应答(CRCE)。</summary>
    public const byte ZCRCE = 0x68;

    /// <summary>数据子包续帧符 <c>'i'</c>(0x69):帧继续,不需要应答(CRCG)。</summary>
    public const byte ZCRCG = 0x69;

    /// <summary>数据子包续帧符 <c>'j'</c>(0x6A):帧继续,需要对端 ZACK 应答(CRCQ)。</summary>
    public const byte ZCRCQ = 0x6A;

    /// <summary>数据子包结束符 <c>'k'</c>(0x6B):帧结束,需要对端 ZACK 应答(CRCW)。</summary>
    public const byte ZCRCW = 0x6B;

    /// <summary>取消字符(0x18,与 ZDLE 同值):连续 5 个及以上表示中止传输。</summary>
    public const byte CAN = 0x18;

    /// <summary>退格字符(0x08):中止序列中跟在 CAN 之后,用于清理对端行缓冲。</summary>
    public const byte BS = 0x08;

    /// <summary>软件流控 XON(0x11):数据流中需被 ZDLE 转义,避免被链路吞掉。</summary>
    public const byte XON = 0x11;

    /// <summary>软件流控 XOFF(0x13):数据流中需被 ZDLE 转义。</summary>
    public const byte XOFF = 0x13;

    /// <summary>会话结束后发送方追加的 <c>"OO"</c>(Over and Out)标记的首字节 <c>'O'</c>。</summary>
    public const byte OverAndOut = 0x4F;

    /// <summary>
    /// 标准中止序列:8 个 CAN + 若干 BS。发送它可要求对端立即中止 ZMODEM 会话
    /// 并把光标行清理干净。规范建议至少 5 个 CAN,这里用 8 个更稳妥。
    /// </summary>
    public static ReadOnlySpan<byte> CancelSequence =>
    [
        CAN, CAN, CAN, CAN, CAN, CAN, CAN, CAN,
        BS, BS, BS, BS, BS, BS, BS, BS
    ];

    /// <summary>
    /// 远端运行 <c>sz</c> 时注入到终端输出流的 ZRQINIT 十六进制帧引导:
    /// <c>ZPAD ZPAD ZDLE ZHEX '0' '0'</c>(2A 2A 18 42 30 30)。命中表示对端要发文件,本地应「接收」。
    /// </summary>
    public static ReadOnlySpan<byte> ReceiveInitSignature =>
    [
        ZPAD, ZPAD, ZDLE, ZHEX, 0x30, 0x30
    ];

    /// <summary>
    /// 远端运行 <c>rz</c> 时注入到终端输出流的 ZRINIT 十六进制帧引导:
    /// <c>ZPAD ZPAD ZDLE ZHEX '0' '1'</c>(2A 2A 18 42 30 31)。命中表示对端要收文件,本地应「发送」。
    /// </summary>
    public static ReadOnlySpan<byte> SendInitSignature =>
    [
        ZPAD, ZPAD, ZDLE, ZHEX, 0x30, 0x31
    ];
}

/// <summary>
/// ZRINIT 帧 ZF0 字节的接收方能力位。取值为 lrzsz <c>zmodem.h</c> 中的八进制常量
/// (<c>CANFDX 001</c> … <c>ESC8 0200</c>)。ZF0 是帧头 4 个参数字节里的最后一个(<c>P3</c>)。
/// </summary>
public static class ZModemCapabilities
{
    /// <summary>接收方支持全双工收发(lrzsz <c>CANFDX 001</c>)。未通告时发送方会关闭窗口机制。</summary>
    public const byte CANFDX = 0x01;

    /// <summary>接收方可在磁盘 IO 期间继续收数据,即支持流式(lrzsz <c>CANOVIO 002</c>)。</summary>
    public const byte CANOVIO = 0x02;

    /// <summary>接收方可发送 BREAK 信号(lrzsz <c>CANBRK 004</c>)。</summary>
    public const byte CANBRK = 0x04;

    /// <summary>接收方可解密(lrzsz <c>CANCRY 010</c>);已废弃,不使用。</summary>
    public const byte CANCRY = 0x08;

    /// <summary>接收方可解压(lrzsz <c>CANLZW 020</c>);已废弃,不使用。</summary>
    public const byte CANLZW = 0x10;

    /// <summary>接收方可使用 32 位 CRC 数据子包(lrzsz <c>CANFC32 040</c>)。</summary>
    public const byte CANFC32 = 0x20;

    /// <summary>
    /// 接收方要求发送方转义全部控制字符(lrzsz <c>ESCCTL 0100</c>)。
    /// 注意:该位曾被误当作 CANOVIO 使用,会让 lrzsz 进入 <c>Zctlesc</c> 模式并关闭窗口机制。
    /// </summary>
    public const byte ESCCTL = 0x40;

    /// <summary>接收方要求发送方转义第 8 位(lrzsz <c>ESC8 0200</c>)。8 位干净链路上不应通告。</summary>
    public const byte ESC8 = 0x80;
}
