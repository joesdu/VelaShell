// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   .Xauthority 文件格式（X11 发行版的 Xau 库）
//   行为规格:   velashell-docs/zh/ssh/spec/07-forwarding.md §7.5.7

using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VelaShell.Ssh.Forwarding;

/// <summary><c>.Xauthority</c> 里的一条记录。</summary>
/// <remarks>
/// 各字段在文件里都是「2 字节长度 + 内容」:超过 65535 字节、或者显示号 / 协议名不是 ASCII 时构造就抛
/// <see cref="ArgumentException"/>(编码不出来的记录不该存在)。
/// </remarks>
public sealed record XAuthorityEntry
{
    /// <summary>构造一条记录。</summary>
    /// <param name="family">地址族(见 <see cref="XAuthority"/> 上的常量)。</param>
    /// <param name="address">地址:本机族里是主机名,网络族里是原始地址字节。</param>
    /// <param name="displayNumber">显示号,ASCII 文本(<c>"0"</c>)。</param>
    /// <param name="name">授权协议名,通常是 <c>MIT-MAGIC-COOKIE-1</c>。</param>
    /// <param name="data">授权数据(cookie 本身)。</param>
    /// <exception cref="ArgumentException">某个字段编码不进文件。</exception>
    public XAuthorityEntry(ushort family, ReadOnlyMemory<byte> address, string displayNumber, string name, ReadOnlyMemory<byte> data)
    {
        ArgumentNullException.ThrowIfNull(displayNumber);
        ArgumentNullException.ThrowIfNull(name);
        CheckField(address.Length, nameof(address));
        CheckField(data.Length, nameof(data));
        CheckText(displayNumber, nameof(displayNumber));
        CheckText(name, nameof(name));
        Family = family;
        Address = address;
        DisplayNumber = displayNumber;
        Name = name;
        Data = data;

        static void CheckText(string text, string parameter)
        {
            CheckField(text.Length, parameter);
            if (!Ascii.IsValid(text))
            {
                throw new ArgumentException("只能是 ASCII。", parameter);
            }
        }

        static void CheckField(int length, string parameter)
        {
            if (length > ushort.MaxValue)
            {
                throw new ArgumentException($"超过 {ushort.MaxValue} 字节,写不进 .Xauthority。", parameter);
            }
        }
    }

    /// <summary>地址族(见 <see cref="XAuthority"/> 上的常量)。</summary>
    public ushort Family { get; }

    /// <summary>地址:本机族里是主机名,网络族里是原始地址字节。</summary>
    public ReadOnlyMemory<byte> Address { get; }

    /// <summary>显示号,ASCII 文本(<c>"0"</c>)。</summary>
    public string DisplayNumber { get; }

    /// <summary>授权协议名,通常是 <c>MIT-MAGIC-COOKIE-1</c>。</summary>
    public string Name { get; }

    /// <summary>授权数据(cookie 本身)。</summary>
    public ReadOnlyMemory<byte> Data { get; }
}

/// <summary>读写 <c>.Xauthority</c> 的格式,并按显示找 cookie。</summary>
/// <remarks>
/// <para>
/// 文件是一串定长前缀的二进制记录，<b>全部是大端</b>：
/// </para>
/// <code>
/// uint16 family
/// uint16 address_length   ‖ address
/// uint16 number_length    ‖ display_number(ASCII)
/// uint16 name_length      ‖ name
/// uint16 data_length      ‖ data
/// </code>
/// <para>
/// <b>这个文件里装的是能打开你本机显示的钥匙</b>。转发找 cookie 时只读不写,
/// 解析失败一律当作「没有匹配项」而不是抛异常 —— 一个半截的
/// <c>.Xauthority</c>(写到一半、被别的程序锁着)不该让整条连接失败。
/// 要改写这个文件的调用方(宿主把内置 X 服务端的 cookie 登记进去)用 <see cref="TryDecode"/> 与 <see cref="Encode"/>:
/// 认不全的文件不改写,否则会把用户别的钥匙弄丢;上锁与原子替换由调用方负责。
/// 宿主与本库原先各有一份解析,规则不同(一份截断就整个不认,一份截断前的照常用),合成了这一份。
/// </para>
/// </remarks>
public static class XAuthority
{
    /// <summary>授权文件可导入的最大字节数。</summary>
    public const int MaxFileBytes = BoundedFileReader.MaxBytes;

    /// <summary>本机族：地址字段里是主机名。</summary>
    public const ushort FamilyLocal = 256;

    /// <summary>通配族：匹配任何显示。</summary>
    public const ushort FamilyWild = 65535;

    /// <summary>IPv4。</summary>
    public const ushort FamilyInternet = 0;

    /// <summary>IPv6。</summary>
    public const ushort FamilyInternet6 = 6;

    /// <summary>我们唯一支持的授权协议。</summary>
    public const string MitMagicCookie1 = "MIT-MAGIC-COOKIE-1";

    /// <summary>单条记录的字段上限 —— 防一个坏文件把内存吃光。</summary>
    private const int MaxFieldBytes = 64 * 1024;

    /// <summary><c>.Xauthority</c> 的默认位置。</summary>
    /// <remarks><c>XAUTHORITY</c> 优先，否则 <c>~/.Xauthority</c>。</remarks>
    public static string? DefaultPath
    {
        get
        {
            string? fromEnvironment = Environment.GetEnvironmentVariable("XAUTHORITY");
            if (!string.IsNullOrEmpty(fromEnvironment))
            {
                return fromEnvironment;
            }

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return home.Length == 0 ? null : Path.Combine(home, ".Xauthority");
        }
    }

    /// <summary>解析一份 <c>.Xauthority</c> 的内容(找 cookie 用的宽容解法)。</summary>
    /// <param name="content">文件字节。</param>
    /// <returns>解出来的记录；遇到截断就到此为止，已解出的照常返回 —— 文件可能正被写入。</returns>
    internal static IReadOnlyList<XAuthorityEntry> Decode(ReadOnlySpan<byte> content)
    {
        List<XAuthorityEntry> entries = [];
        _ = DecodeInto(content, entries, strict: false);
        return entries;
    }

    /// <summary>
    /// 严格地解析一份 <c>.Xauthority</c> 的内容:每一条都完整才算成功(空内容是零条记录)。要改写这个文件时用它 ——
    /// 有任何一条认不全(截断的、别的格式的)就返回 false,调用方不该改写一个自己读不全的文件。
    /// </summary>
    /// <param name="content">文件字节。</param>
    /// <param name="entries">成功时是全部记录。</param>
    /// <returns>每一条都解出来了。</returns>
    public static bool TryDecode(ReadOnlySpan<byte> content, [NotNullWhen(true)] out IReadOnlyList<XAuthorityEntry>? entries)
    {
        List<XAuthorityEntry> decoded = [];
        if (DecodeInto(content, decoded, strict: true))
        {
            entries = decoded;
            return true;
        }
        entries = null;
        return false;
    }

    /// <summary>按文件格式编码一串记录(<see cref="TryDecode"/> 的反过程)。</summary>
    /// <param name="entries">记录。</param>
    /// <returns>文件字节。</returns>
    public static byte[] Encode(IEnumerable<XAuthorityEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        using MemoryStream output = new();
        Span<byte> u16 = stackalloc byte[2];
        foreach (XAuthorityEntry entry in entries)
        {
            BinaryPrimitives.WriteUInt16BigEndian(u16, entry.Family);
            output.Write(u16);
            WriteBlock(output, entry.Address.Span);
            WriteBlock(output, Encoding.ASCII.GetBytes(entry.DisplayNumber));
            WriteBlock(output, Encoding.ASCII.GetBytes(entry.Name));
            WriteBlock(output, entry.Data.Span);
        }
        return output.ToArray();

        static void WriteBlock(MemoryStream output, ReadOnlySpan<byte> block)
        {
            Span<byte> length = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)block.Length);   // 构造记录时已经查过放得下
            output.Write(length);
            output.Write(block);
        }
    }

    /// <summary>
    /// 逐条解进 <paramref name="entries"/>;全部解完返回 true,遇到截断就停在那里返回 false。
    /// 显示号或协议名不是 ASCII 的记录:<paramref name="strict"/> 时当作认不全(返回 false —— 改写回去会把那几个字节换成 <c>?</c>),
    /// 否则跳过它接着解(反正匹配不上任何显示)。
    /// </summary>
    private static bool DecodeInto(ReadOnlySpan<byte> content, List<XAuthorityEntry> entries, bool strict)
    {
        if (content.Length > MaxFileBytes)
        {
            return false;
        }
        int offset = 0;
        while (offset < content.Length)
        {
            if (offset + 2 > content.Length)
            {
                return false;
            }
            ushort family = BinaryPrimitives.ReadUInt16BigEndian(content[offset..]);
            offset += 2;

            if (!TryReadBlock(content, ref offset, out byte[]? address)
                || !TryReadBlock(content, ref offset, out byte[]? number)
                || !TryReadBlock(content, ref offset, out byte[]? name)
                || !TryReadBlock(content, ref offset, out byte[]? data))
            {
                return false;   // 截断了:已经解出来的仍然有效
            }

            if (!Ascii.IsValid(number) || !Ascii.IsValid(name))
            {
                if (strict)
                {
                    return false;
                }
                continue;
            }

            entries.Add(new XAuthorityEntry(
                family,
                address,
                Encoding.ASCII.GetString(number),
                Encoding.ASCII.GetString(name),
                data));
        }
        return true;
    }

    /// <summary>读文件并解析；读不到就返回空。</summary>
    internal static async ValueTask<IReadOnlyList<XAuthorityEntry>> LoadAsync(
        string? path = null, CancellationToken cancellationToken = default)
    {
        string? actual = path ?? DefaultPath;
        if (actual is null || !File.Exists(actual))
        {
            return [];
        }

        try
        {
            return await BoundedFileReader.ReadAsync(actual,
                static (bytes, count) => Decode(bytes.AsSpan(0, count)), cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];   // 被锁着、权限不够 —— 当作没有
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>找出某个显示的 <c>MIT-MAGIC-COOKIE-1</c>。</summary>
    /// <param name="entries">记录。</param>
    /// <param name="display">要找的显示。</param>
    /// <param name="hostName">本机主机名；<see langword="null"/> 取 <see cref="Dns.GetHostName"/>。</param>
    /// <param name="hostAddresses">
    /// 显示主机解析出来的地址；<see langword="null"/> 时只认主机本身就是 IP 字面量的情况。
    /// 主机是域名时由调用方先解析好（见 <see cref="ResolveHostAddressesAsync"/>）。
    /// </param>
    /// <returns>找到返回 cookie，否则 <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// 匹配规则：协议名必须是 <c>MIT-MAGIC-COOKIE-1</c>，cookie 不能为空；显示号要对上
    /// （按十进制<b>数值</b>比，<c>"05"</c> 与显示 5 算对上；记录里的显示号是空串时当通配；
    /// 带符号、空白或其它字符而解析不了的不匹配）；地址族是
    /// <see cref="FamilyWild"/> 时不看地址。
    /// </para>
    /// <para>
    /// <see cref="FamilyLocal"/> 按主机名比（<b>不区分大小写</b>），但<b>只在显示指向本机时</b>
    /// （本机显示、回环地址、或主机名就是本机名 —— 与 Xlib 一致）。
    /// 网络族（<see cref="FamilyInternet"/> / <see cref="FamilyInternet6"/>）<b>必须地址逐字节相等</b>。
    /// </para>
    /// <para>
    /// ⚠️ 两条都是为了<b>不把一台 X server 的 cookie 交给另一台</b>：真 cookie 会被写进
    /// 发往 <paramref name="display"/> 的建立报文里。<c>DISPLAY=otherhost:0</c> 时要是拿了
    /// 本机 <c>:0</c> 的那条，等于把本机显示的钥匙送给了 otherhost。
    /// </para>
    /// </remarks>
    internal static byte[]? FindCookie(
        IReadOnlyList<XAuthorityEntry> entries,
        X11Display display,
        string? hostName = null,
        IReadOnlyList<IPAddress>? hostAddresses = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(display);

        string host = hostName ?? SafeHostName();
        IReadOnlyList<IPAddress> addresses = hostAddresses
            ?? (IPAddress.TryParse(display.Host, out IPAddress? literal) ? [literal] : []);

        // Xlib 连本机（包括走回环 TCP）时用本机主机名找授权。
        bool refersToLocalHost =
            display.IsLocal
            || addresses.Any(IPAddress.IsLoopback)
            || (host.Length != 0 && string.Equals(display.Host, host, StringComparison.OrdinalIgnoreCase));

        foreach (XAuthorityEntry entry in entries)
        {
            // 〔velashell-docs/zh/ssh/spec/07 §7.5.7〕只认 MIT-MAGIC-COOKIE-1，
            // XDM-AUTHORIZATION-1 一律跳过（与 OpenSSH 一致）。
            // 〔velashell-docs/zh/ssh/spec/07 §7.5.7〕cookie 为空的记录跳过、接着往下找：空 cookie 开不了任何门，
            // 拿它当真 cookie 换进建立报文，后面那条有效的就被它挡住了。
            if (!string.Equals(entry.Name, MitMagicCookie1, StringComparison.Ordinal) || entry.Data.Length == 0)
            {
                continue;
            }

            if (!DisplayNumberMatches(entry.DisplayNumber, display.Number))
            {
                continue;
            }

            bool addressMatches = entry.Family switch
            {
                FamilyWild => true,
                FamilyLocal => refersToLocalHost && string.Equals(
                    Encoding.ASCII.GetString(entry.Address.Span), host, StringComparison.OrdinalIgnoreCase),
                FamilyInternet => AddressMatches(entry.Address.Span, addresses, AddressFamily.InterNetwork),
                FamilyInternet6 => AddressMatches(entry.Address.Span, addresses, AddressFamily.InterNetworkV6),
                _ => false,
            };

            if (addressMatches)
            {
                return entry.Data.ToArray();
            }
        }

        return null;
    }

    /// <summary>记录里的显示号字段是否指向 <paramref name="wanted"/>。</summary>
    /// <remarks>
    /// 按<b>数值</b>比，不按文本比：字段是 ASCII 十进制数，按文本比的话 <c>"05"</c>
    /// 与显示 5 对不上。只认纯十进制数字 —— 不带符号、不带空白、与区域设置无关；
    /// 空串是通配；非空而解析不了（或超出范围）的字段不匹配任何显示，
    /// 而不是被当成通配 —— 读不懂的条目不该拿来开门。
    /// </remarks>
    private static bool DisplayNumberMatches(string field, int wanted)
    {
        if (field.Length == 0)
        {
            return true;
        }

        return int.TryParse(
                field,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out int number)
            && number == wanted;
    }

    /// <summary>解析显示主机的地址，给 <see cref="FindCookie"/> 比网络族用。</summary>
    /// <remarks>本机显示与 IP 字面量不查 DNS；解析失败返回空 —— 于是只剩通配条目能匹配。</remarks>
    internal static async ValueTask<IReadOnlyList<IPAddress>> ResolveHostAddressesAsync(
        X11Display display, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(display);

        if (IPAddress.TryParse(display.Host, out IPAddress? literal))
        {
            return [literal];
        }

        if (display.IsLocal)
        {
            return [];
        }

        try
        {
            return await Dns.GetHostAddressesAsync(display.Host, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return [];
        }
    }

    private static bool AddressMatches(ReadOnlySpan<byte> entryAddress, IReadOnlyList<IPAddress> addresses, AddressFamily family)
    {
        foreach (IPAddress candidate in addresses)
        {
            IPAddress address = candidate.IsIPv4MappedToIPv6 ? candidate.MapToIPv4() : candidate;
            if (address.AddressFamily == family && address.GetAddressBytes().AsSpan().SequenceEqual(entryAddress))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>本机主机名。</summary>
    /// <remarks>
    /// ⚠️ 用 <see cref="Dns.GetHostName"/> 而不是 <see cref="Environment.MachineName"/>：
    /// 后者在类 Unix 上会在第一个 <c>.</c> 处截断，而 <c>.Xauthority</c> 里存的是
    /// <c>gethostname()</c> 的完整值 —— 主机名带域名的机器上就永远对不上。
    /// </remarks>
    private static string SafeHostName()
    {
        try
        {
            return Dns.GetHostName();
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static bool TryReadBlock(ReadOnlySpan<byte> content, ref int offset, out byte[] block)
    {
        block = [];

        if (offset + 2 > content.Length)
        {
            return false;
        }

        int length = BinaryPrimitives.ReadUInt16BigEndian(content[offset..]);
        offset += 2;

        if (length > MaxFieldBytes || offset + length > content.Length)
        {
            return false;
        }

        block = content.Slice(offset, length).ToArray();
        offset += length;
        return true;
    }
}
