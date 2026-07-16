using VelaShell.Core.ZModem.Protocol;

namespace VelaShell.Terminal.ZModem;

/// <summary>检测到的 ZMODEM 自启动类型(决定本地扮演哪一侧)。</summary>
public enum ZModemTrigger
{
    /// <summary>未命中任何引导。</summary>
    None,

    /// <summary>命中 ZRQINIT:远端跑了 <c>sz</c> 要发文件,本地应「接收」(下载)。</summary>
    Receive,

    /// <summary>命中 ZRINIT:远端跑了 <c>rz</c> 要收文件,本地应「发送」(上传)。</summary>
    Send
}

/// <summary>一次检测扫描的结果:应喂给终端的字节,以及是否命中 ZMODEM 启动。</summary>
/// <param name="TerminalBytes">应正常喂入 VT 终端的字节(命中时为引导前的部分)。</param>
/// <param name="Trigger">命中的引导类型;<see cref="ZModemTrigger.None" /> 表示未命中。</param>
/// <param name="ProtocolBytes">命中时,从引导序列起、应交给 ZMODEM 引擎的字节。</param>
public readonly record struct ZModemDetectResult(
    byte[] TerminalBytes,
    ZModemTrigger Trigger,
    byte[] ProtocolBytes)
{
    /// <summary>是否命中了 ZMODEM 引导。</summary>
    public bool Detected => Trigger != ZModemTrigger.None;
}

/// <summary>
/// 在终端输出字节流中检测 ZMODEM 自动启动:远端 <c>sz</c> 注入的 <c>ZRQINIT</c> 引导
/// (<c>** ZDLE 'B' '0' '0'</c>,本地转接收),或远端 <c>rz</c> 注入的 <c>ZRINIT</c> 引导
/// (<c>** ZDLE 'B' '0' '1'</c>,本地转发送)。采用滚动缓冲:当分片尾部可能是被切断的引导前缀时,
/// 扣留最多 <c>SignatureLength-1</c> 字节不喂终端,等下一分片续上,避免把协议引导错误渲染到屏幕。
/// </summary>
public sealed class ZModemDetector
{
    private static readonly byte[] ReceiveSignature = ZModemConstants.ReceiveInitSignature.ToArray();
    private static readonly byte[] SendSignature = ZModemConstants.SendInitSignature.ToArray();

    // 两个引导等长(都是 ZPAD ZPAD ZDLE ZHEX + 两位十六进制类型),扣留长度按其一即可。
    private static readonly int SignatureLength = ReceiveSignature.Length;

    // 扣留的、可能属于被切断引导前缀的尾部字节。
    private readonly List<byte> _held = [];

    /// <summary>处理一段新到达的输出字节,判断是否命中 ZMODEM 引导。</summary>
    /// <param name="incoming">本次到达的原始输出字节。</param>
    /// <returns>检测结果:待喂终端字节、命中的引导类型、以及命中后交给引擎的协议字节。</returns>
    public ZModemDetectResult Process(ReadOnlySpan<byte> incoming)
    {
        // 把先前扣留的尾部与新数据拼成一个连续窗口做匹配。
        byte[] window = new byte[_held.Count + incoming.Length];
        _held.CopyTo(window);
        incoming.CopyTo(window.AsSpan(_held.Count));
        _held.Clear();

        int recvIdx = IndexOf(window, ReceiveSignature);
        int sendIdx = IndexOf(window, SendSignature);

        // 两个引导都可能出现时,取更靠前的那个。
        int idx;
        ZModemTrigger trigger;
        if (recvIdx >= 0 && (sendIdx < 0 || recvIdx <= sendIdx))
        {
            idx = recvIdx;
            trigger = ZModemTrigger.Receive;
        }
        else if (sendIdx >= 0)
        {
            idx = sendIdx;
            trigger = ZModemTrigger.Send;
        }
        else
        {
            // 未命中:扣留可能是被切断引导前缀的尾部,其余喂终端。
            int holdLen = LongestSignaturePrefixSuffix(window);
            int feedLen = window.Length - holdLen;
            byte[] feed = window[..feedLen];
            if (holdLen > 0)
            {
                _held.AddRange(window.AsSpan(feedLen).ToArray());
            }
            return new(feed, ZModemTrigger.None, []);
        }

        // 命中:引导之前的部分照常进终端,引导及其后全部交给引擎。
        return new(window[..idx], trigger, window[idx..]);
    }

    /// <summary>会话结束或路由复位时,取回并清空当前扣留的字节(交回终端,避免吞字节)。</summary>
    /// <returns>此前被扣留、尚未喂终端的字节。</returns>
    public byte[] Flush()
    {
        if (_held.Count == 0)
        {
            return [];
        }
        byte[] result = [.. _held];
        _held.Clear();
        return result;
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return -1;
        }
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// 求 <paramref name="window" /> 的末尾与任一引导前缀的最长重叠长度(0..SignatureLength-1),
    /// 即需要扣留、等后续分片续接的尾部长度。两个引导只在最后一字节不同,故按公共前缀判断即可。
    /// </summary>
    private static int LongestSignaturePrefixSuffix(ReadOnlySpan<byte> window)
    {
        int max = Math.Min(SignatureLength - 1, window.Length);
        for (int len = max; len > 0; len--)
        {
            ReadOnlySpan<byte> tail = window[^len..];
            if (tail.SequenceEqual(ReceiveSignature.AsSpan(0, len)) || tail.SequenceEqual(SendSignature.AsSpan(0, len)))
            {
                return len;
            }
        }
        return 0;
    }
}
