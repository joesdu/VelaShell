// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   .Xauthority 文件格式（X11 发行版的 Xau 库）
//   行为规格:   velashell-docs/zh/ssh/spec/07-forwarding.md §7.5.7

using System.Buffers.Binary;
using System.Text;

namespace VelaShell.Ssh.Forwarding;

/// <summary><c>.Xauthority</c> 里的一条记录。</summary>
/// <param name="Family">地址族（见 <see cref="XAuthority"/> 上的常量）。</param>
/// <param name="Address">地址：本机族里是主机名，网络族里是原始地址字节。</param>
/// <param name="DisplayNumber">显示号，ASCII 文本（<c>"0"</c>）。</param>
/// <param name="Name">授权协议名，通常是 <c>MIT-MAGIC-COOKIE-1</c>。</param>
/// <param name="Data">授权数据（cookie 本身）。</param>
public sealed record XAuthorityEntry(
    int Family,
    byte[] Address,
    string DisplayNumber,
    string Name,
    byte[] Data);

/// <summary>读 <c>.Xauthority</c>。</summary>
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
/// <b>这个文件里装的是能打开你本机显示的钥匙</b>，所以这里只读不写，
/// 解析失败一律当作「没有匹配项」而不是抛异常 —— 一个半截的
/// <c>.Xauthority</c>（写到一半、被别的程序锁着）不该让整条连接失败。
/// </para>
/// </remarks>
public static class XAuthority
{
    /// <summary>本机族：地址字段里是主机名。</summary>
    public const int FamilyLocal = 256;

    /// <summary>通配族：匹配任何显示。</summary>
    public const int FamilyWild = 65535;

    /// <summary>IPv4。</summary>
    public const int FamilyInternet = 0;

    /// <summary>IPv6。</summary>
    public const int FamilyInternet6 = 6;

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

    /// <summary>解析一份 <c>.Xauthority</c> 的内容。</summary>
    /// <param name="content">文件字节。</param>
    /// <returns>解出来的记录；遇到截断就到此为止，已解出的照常返回。</returns>
    public static IReadOnlyList<XAuthorityEntry> Parse(ReadOnlySpan<byte> content)
    {
        List<XAuthorityEntry> entries = [];
        int offset = 0;

        while (offset + 2 <= content.Length)
        {
            int family = BinaryPrimitives.ReadUInt16BigEndian(content[offset..]);
            offset += 2;

            if (!TryReadBlock(content, ref offset, out byte[]? address)
                || !TryReadBlock(content, ref offset, out byte[]? number)
                || !TryReadBlock(content, ref offset, out byte[]? name)
                || !TryReadBlock(content, ref offset, out byte[]? data))
            {
                // 截断了。**已经解出来的仍然有效** —— 文件可能正被写入。
                break;
            }

            entries.Add(new XAuthorityEntry(
                family,
                address,
                Encoding.ASCII.GetString(number),
                Encoding.ASCII.GetString(name),
                data));
        }

        return entries;
    }

    /// <summary>读文件并解析；读不到就返回空。</summary>
    public static async ValueTask<IReadOnlyList<XAuthorityEntry>> LoadAsync(
        string? path = null, CancellationToken cancellationToken = default)
    {
        string? actual = path ?? DefaultPath;
        if (actual is null || !File.Exists(actual))
        {
            return [];
        }

        try
        {
            byte[] content = await File.ReadAllBytesAsync(actual, cancellationToken).ConfigureAwait(false);
            return Parse(content);
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
    /// <param name="hostName">本机主机名；<see langword="null"/> 取当前机器名。</param>
    /// <returns>找到返回 cookie，否则 <see langword="null"/>。</returns>
    /// <remarks>
    /// 匹配规则：协议名必须是 <c>MIT-MAGIC-COOKIE-1</c>；显示号要对上
    /// （按十进制<b>数值</b>比，<c>"05"</c> 与显示 5 算对上；记录里的显示号是空串时当通配；
    /// 带符号、空白或其它字符而解析不了的不匹配）；地址族是
    /// <see cref="FamilyWild"/> 时不看地址，是 <see cref="FamilyLocal"/>
    /// 时按主机名比（<b>不区分大小写</b> —— 主机名本来就不区分）。
    /// </remarks>
    public static byte[]? FindCookie(
        IReadOnlyList<XAuthorityEntry> entries, X11Display display, string? hostName = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(display);

        string host = hostName ?? SafeHostName();

        foreach (XAuthorityEntry entry in entries)
        {
            // 〔velashell-docs/zh/ssh/spec/07 §7.5.7〕只认 MIT-MAGIC-COOKIE-1，
            // XDM-AUTHORIZATION-1 一律跳过（与 OpenSSH 一致）。
            if (!string.Equals(entry.Name, MitMagicCookie1, StringComparison.Ordinal))
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
                FamilyLocal => string.Equals(
                    Encoding.ASCII.GetString(entry.Address), host, StringComparison.OrdinalIgnoreCase),

                // 网络族：本机显示时不该拿它来开本机的门，但远程显示时它就是对的。
                // 这里只在显示本身是远程时才比。
                _ => !display.IsLocal,
            };

            if (addressMatches)
            {
                return entry.Data;
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

    private static string SafeHostName()
    {
        try
        {
            return Environment.MachineName;
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
