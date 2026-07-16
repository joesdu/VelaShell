namespace VelaShell.Core.ZModem.Protocol;

/// <summary>
/// ZMODEM 帧类型(帧头首字节)。取值与 Chuck Forsberg 规范一致,用于与 lrzsz 互操作。
/// </summary>
public enum ZModemFrameType : byte
{
    /// <summary>请求接收方初始化(接收方据此回 <see cref="ZRINIT" />)。</summary>
    ZRQINIT = 0,

    /// <summary>接收方就绪,携带接收能力标志(缓冲区大小、是否支持 CRC32 等)。</summary>
    ZRINIT = 1,

    /// <summary>发送方初始化,携带发送选项(转义策略等)。</summary>
    ZSINIT = 2,

    /// <summary>确认帧(对 CRCQ/CRCW 子包或 ZSINIT 的应答)。</summary>
    ZACK = 3,

    /// <summary>文件信息帧,数据子包携带文件名与元数据(大小/时间/模式)。</summary>
    ZFILE = 4,

    /// <summary>跳过当前文件(接收方拒收或文件已存在)。</summary>
    ZSKIP = 5,

    /// <summary>否定应答:子包 CRC 校验失败,要求重传。</summary>
    ZNAK = 6,

    /// <summary>中止会话(致命错误)。</summary>
    ZABORT = 7,

    /// <summary>会话结束握手:双方交换 ZFIN 后发送方追加 <c>"OO"</c>。</summary>
    ZFIN = 8,

    /// <summary>要求发送方从指定字节偏移(帧头位置字段)重新发送数据。</summary>
    ZRPOS = 9,

    /// <summary>数据帧头,其后跟随若干数据子包。</summary>
    ZDATA = 10,

    /// <summary>文件数据结束,位置字段为文件总字节数。</summary>
    ZEOF = 11,

    /// <summary>文件读写错误(不可恢复)。</summary>
    ZFERR = 12,

    /// <summary>请求对文件某段做 CRC 校验(崩溃恢复用)。</summary>
    ZCRC = 13,

    /// <summary>安全挑战:接收方回送发送方给定的随机数。</summary>
    ZCHALLENGE = 14,

    /// <summary>批量传输全部完成。</summary>
    ZCOMPL = 15,

    /// <summary>其它非致命错误后的取消。</summary>
    ZCAN = 16,

    /// <summary>请求剩余可用磁盘空间。</summary>
    ZFREECNT = 17,

    /// <summary>要求接收方执行命令(数据子包携带命令行)。</summary>
    ZCOMMAND = 18,

    /// <summary>把数据子包内容输出到接收方 stderr。</summary>
    ZSTDERR = 19
}
