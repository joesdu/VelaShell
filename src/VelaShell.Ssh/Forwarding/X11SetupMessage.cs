// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X11 核心协议  连接建立报文（byte_order + 两段变长授权字段）
//   行为规格:     velashell-docs/zh/ssh/spec/07-forwarding.md §7.5.5

using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace VelaShell.Ssh.Forwarding;

/// <summary>解析 X11 客户端的连接建立报文，并把假 cookie 换成真的。</summary>
/// <remarks>
/// <para>
/// 这是 X11 转发安全性的**全部落点**。远端的 X 客户端拿着我们发出去的
/// <b>假</b> cookie 连过来；我们在这里核对它，核对通过才把报文里的 cookie
/// 换成本机真实的那个再转给 X server。
/// </para>
/// <para>
/// 报文结构（<c>velashell-docs/zh/ssh/spec/07</c> §7.5.5）：12 字节定长头 + 两段补齐到
/// 4 字节边界的变长数据。
/// </para>
/// </remarks>
public static class X11SetupMessage
{
    /// <summary>定长头的长度。</summary>
    public const int HeaderLength = 12;

    /// <summary>两段变长字段各自的上限。</summary>
    /// <remarks>
    /// 〔<c>velashell-docs/zh/ssh/spec/07</c> §7.5.5〕长度是远端给的，而这些字节要在核对 cookie 之前攒着。
    /// 协议允许各到 64 KiB —— 曾经就按那个上限等，等于让一个还没证明身份的对端决定我们攒多少、等多久。
    /// 能通过核对的只有 18 字节的 <c>MIT-MAGIC-COOKIE-1</c> 加 16 字节的假 cookie，
    /// 真实的 X11 授权协议名与数据都远小于 256 字节；读到 12 字节的头就能判。
    /// </remarks>
    internal const int MaxFieldLength = 256;

    /// <summary>解析结果。</summary>
    /// <param name="TotalLength">整个建立报文的字节数（含补齐）。</param>
    /// <param name="BigEndian">长度字段是不是大端。</param>
    /// <param name="ProtocolName">授权协议名。</param>
    /// <param name="ProtocolData">授权数据（cookie）。</param>
    public readonly record struct Parsed(
        int TotalLength, bool BigEndian, string ProtocolName, byte[] ProtocolData);

    /// <summary>试着解析一个完整的建立报文。</summary>
    /// <param name="buffer">已经收到的字节。</param>
    /// <param name="parsed">解析结果。</param>
    /// <returns>
    /// <see langword="true"/> 表示解出来了；<see langword="false"/> 表示
    /// <b>还不够，要再收</b>（不是错误）。
    /// </returns>
    /// <exception cref="FormatException">
    /// 字节序标记不合法，或字段长度超过上限（各 256 字节）—— 只要头到了就判，不等后面的数据。
    /// </exception>
    /// <remarks>
    /// ⚠️ <b>报文可能分几次到达。</b> 先攒够 12 字节的头，再按头里的长度
    /// 攒够两段数据 —— 一上来就假设「第一次读就是完整报文」在小 MTU
    /// 或慢链路上会随机失败。
    /// </remarks>
    public static bool TryParse(ReadOnlySequence<byte> buffer, out Parsed parsed)
    {
        parsed = default;

        if (buffer.Length < HeaderLength)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[HeaderLength];
        buffer.Slice(0, HeaderLength).CopyTo(header);

        // ⚠️ **两种字节序都要认。** 第一个字节说了算，只按一种解析的症状是
        //    「某些客户端能连，某些连不上」—— 而那看上去完全像随机故障。
        bool bigEndian = header[0] switch
        {
            (byte)'B' => true,
            (byte)'l' => false,
            _ => throw new FormatException(
                $"X11 连接建立报文的字节序标记非法：0x{header[0]:X2}（只认 'B' 与 'l'）。"),
        };

        int nameLength = ReadUInt16(header[6..], bigEndian);
        int dataLength = ReadUInt16(header[8..], bigEndian);

        if (nameLength > MaxFieldLength || dataLength > MaxFieldLength)
        {
            throw new FormatException(
                $"X11 连接建立报文的字段过长（名字 {nameLength}，数据 {dataLength}）。");
        }

        // 两段都补齐到 4 的倍数。
        int paddedName = Pad4(nameLength);
        int paddedData = Pad4(dataLength);
        int total = HeaderLength + paddedName + paddedData;

        if (buffer.Length < total)
        {
            return false;   // 还没收全 —— 继续等
        }

        byte[] name = new byte[nameLength];
        buffer.Slice(HeaderLength, nameLength).CopyTo(name);

        byte[] data = new byte[dataLength];
        buffer.Slice(HeaderLength + paddedName, dataLength).CopyTo(data);

        parsed = new Parsed(total, bigEndian, Encoding.ASCII.GetString(name), data);
        return true;
    }

    /// <summary>核对假 cookie，并造一个把它换成真 cookie 的新报文。</summary>
    /// <param name="original">原始报文（长度必须是 <see cref="Parsed.TotalLength"/>）。</param>
    /// <param name="parsed">解析结果。</param>
    /// <param name="expectedFakeCookie">我们在 <c>x11-req</c> 里发出去的假 cookie。</param>
    /// <param name="realCookie">本机显示真正的 cookie。</param>
    /// <param name="rewritten">换好的报文。</param>
    /// <returns>核对通过返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>比较必须是常数时间的。</b> 逐字节短路比较会泄漏「前几个字节对了几个」，
    /// 而攻击者可以一条条开通道慢慢试出整个 cookie。
    /// </para>
    /// <para>
    /// 只支持 <c>MIT-MAGIC-COOKIE-1</c>；别的授权协议一律不通过
    /// （与 OpenSSH 一致，<c>velashell-docs/zh/ssh/spec/07</c> §7.5.5）。
    /// </para>
    /// <para>
    /// 真假 cookie <b>长度相同</b>时才能就地替换；长度不同就要重建报文
    /// （补齐也会变）。这里统一走重建，省掉一个「长度恰好相等」的隐含假设。
    /// </para>
    /// </remarks>
    public static bool TryRewriteCookie(
        ReadOnlySpan<byte> original,
        in Parsed parsed,
        ReadOnlySpan<byte> expectedFakeCookie,
        ReadOnlySpan<byte> realCookie,
        out byte[] rewritten)
    {
        rewritten = [];

        if (!string.Equals(parsed.ProtocolName, XAuthority.MitMagicCookie1, StringComparison.Ordinal))
        {
            return false;
        }

        // 常数时间比较。长度不等时 FixedTimeEquals 直接返回 false，
        // 那一步本身不泄漏内容。
        if (!CryptographicOperations.FixedTimeEquals(parsed.ProtocolData, expectedFakeCookie))
        {
            return false;
        }

        int paddedName = Pad4(parsed.ProtocolName.Length);
        int paddedData = Pad4(realCookie.Length);
        rewritten = new byte[HeaderLength + paddedName + paddedData];

        // 头照抄，只改数据长度那一项。
        original[..HeaderLength].CopyTo(rewritten);
        WriteUInt16(rewritten.AsSpan(8), (ushort)realCookie.Length, parsed.BigEndian);

        Encoding.ASCII.GetBytes(parsed.ProtocolName).CopyTo(rewritten.AsSpan(HeaderLength));
        realCookie.CopyTo(rewritten.AsSpan(HeaderLength + paddedName));

        return true;
    }

    /// <summary>生成一个随机的假 cookie。</summary>
    /// <remarks>
    /// 16 字节 —— 与 <c>MIT-MAGIC-COOKIE-1</c> 的惯例一致。
    /// <b>绝不能拿本机真实的 cookie 去发给服务端</b>，这个假的就是为此存在的。
    /// </remarks>
    public static byte[] CreateFakeCookie() => RandomNumberGenerator.GetBytes(16);

    /// <summary>把 cookie 写成 <c>x11-req</c> 要的十六进制文本。</summary>
    /// <remarks>
    /// ⚠️ <c>x11-req</c> 的 cookie 字段是<b>十六进制字符串</b>，不是原始字节。
    /// 发原始字节的症状是远端 <c>xauth</c> 存进去的和我们校验的对不上，
    /// 而错误信息只会说「连接被拒绝」。
    /// </remarks>
    public static string ToHex(ReadOnlySpan<byte> cookie) => Convert.ToHexStringLower(cookie);

    private static int Pad4(int length) => (length + 3) & ~3;

    private static int ReadUInt16(ReadOnlySpan<byte> span, bool bigEndian) =>
        bigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(span)
            : BinaryPrimitives.ReadUInt16LittleEndian(span);

    private static void WriteUInt16(Span<byte> span, ushort value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt16BigEndian(span, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(span, value);
        }
    }
}
