// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/07-forwarding.md §4.6
// 主机名的写法: RFC 1035 §2.3.4（总长、每段长度）、RFC 1123 §2.1（最后一段不会全是数字）

using System.Buffers;
using System.Net;
using System.Net.Sockets;

namespace VelaShell.Ssh.Forwarding;

/// <summary>
/// 远程动态转发（<c>-R [bind:]port</c> 不给目标）的放行名单：远端经它能让本机连哪些 <c>主机:端口</c>
/// —— 即 ssh_config(5) 的 <c>PermitRemoteOpen</c>。
/// </summary>
/// <remarks>
/// <para>
/// 〔决策 velashell-docs/zh/ssh/spec/07 §4.6〕<b>没有默认值。</b>远程动态转发等于把本机变成远端的 SOCKS 代理，
/// 本机能到的内网它就能到 —— 放哪些出去必须由调用方明说。要全放，显式给 <see cref="Any"/>。
/// </para>
/// <para>
/// 每条规则是 <c>主机:端口</c>：主机可带 <c>*</c> / <c>?</c> 通配（不分大小写），IPv6 字面量写在方括号里；
/// 端口是数字或 <c>*</c>。<b>按远端在 SOCKS 握手里给的名字比，不先解析</b>：规则写 IP，远端给域名就对不上（反之亦然）——
/// 宁可错拒，不让一个解析到内网地址的域名绕过名单。
/// </para>
/// <para>
/// 「写 IP 还是写名字」按规则的主机部分认，各配各的：
/// </para>
/// <list type="bullet">
/// <item>只有 <c>*</c>：任何主机，域名与 IP 字面量（含 <c>10.1</c> 这类简写）都算。</item>
/// <item>只含数字、<c>.</c>、<c>*</c>、<c>?</c>（至少一个数字），如 <c>10.0.0.*</c>：IPv4 规则，<b>只配规范写法的点分四段 IPv4</b>
/// （<c>10.0.0.7</c>）。<c>10.0.0.7.attacker.example</c> 是域名、配不上 —— 曾经通配是纯文字比，<c>*</c> 一路吃过点，
/// 这个名字就进了名单，而它解析到哪由对方的 DNS 说了算。<c>2130706433</c>、<c>0177.0.0.1</c>、<c>10.1</c>
/// 这些写法 .NET 照样当地址连，但文字上和规则对不齐，一律不配。</item>
/// <item>含 <c>:</c>（方括号里的）：IPv6 规则，只配 IPv6 字面量；两边都换成规范写法（压缩零段、小写）再比，
/// 带通配的拿规范写法去配。</item>
/// <item>其余：名字规则，<b>只配域名，不配 IP 字面量</b>。IP 目标只由写成 IP 的规则（或单独的 <c>*</c>）放行：
/// 名字规则在文字上本来就几乎配不上地址，偶尔配上（<c>fe80*</c> 对 <c>fe80::1</c>）也是碰巧，不是写规则的人的意思。</item>
/// </list>
/// <para>
/// 远端给的主机本身先过一道写法检查，<b>连 <see cref="Any"/> 也不例外</b>：只认合法的主机名（字母、数字、<c>-</c>、<c>_</c>，
/// 点分段，总长 ≤ 253、每段 ≤ 63，最后一段不是数字）或 IP 字面量。带 NUL、控制字符、空白、<c>%</c>、<c>/</c>、通配符的一律不放 ——
/// 名单是按文字比的，<c>*.corp.example</c> 在文字上配得上 <c>127.0.0.1\0.corp.example</c>；而系统解析器拿到的是 C 字符串，
/// NUL 之后的部分可能被截掉，解析的就成了 <c>127.0.0.1</c>。
/// 最后一段全是数字却不是地址的（<c>10.0.0.256</c>、<c>1.2.3.4.5</c>、<c>10.0.0.1.</c>）同样不放：它不是主机名，
/// 各平台的解析器又可能把它读成数字地址。
/// </para>
/// </remarks>
public sealed class RemoteOpenPolicy
{
    // RFC 1035 §2.3.4：线上最多 255 个八位组，去掉长度字节与根，文字最多 253 个字符；每段最多 63。
    private const int MaxHostNameLength = 253;
    private const int MaxLabelLength = 63;

    private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789abcdefABCDEF");

    private readonly Rule[] _rules;
    private readonly bool _any;

    private RemoteOpenPolicy(bool any, Rule[] rules)
    {
        _any = any;
        _rules = rules;
    }

    /// <summary>规则的主机写成了哪一种 —— 决定它能配哪一种目标。</summary>
    private enum RuleHostKind
    {
        /// <summary>名字：只配域名。</summary>
        Name,

        /// <summary>只有 <c>*</c>：任何主机。</summary>
        AnyHost,

        /// <summary>数字、点与通配：只配规范写法的点分四段 IPv4。</summary>
        IPv4,

        /// <summary>带冒号：只配 IPv6 字面量，按规范写法比。</summary>
        IPv6,
    }

    /// <summary>远端给的主机是哪一种。</summary>
    private enum TargetKind
    {
        /// <summary>写法不对（控制字符、空白、通配符、数字结尾的非地址……）：什么规则都不放。</summary>
        Invalid,

        /// <summary>合法的主机名。</summary>
        Name,

        /// <summary>规范写法的点分四段 IPv4。</summary>
        IPv4,

        /// <summary>IPv6 字面量。</summary>
        IPv6,

        /// <summary>.NET 当地址连、但不是规范写法的 IPv4（<c>10.1</c>、<c>2130706433</c>、<c>0177.0.0.1</c>、<c>0x7f.1</c>）：只有单独的 <c>*</c> 放。</summary>
        OtherAddress,
    }

    /// <param name="Host">规则里写的主机（原样，给 <see cref="ToString"/>）。</param>
    /// <param name="Port">端口；<see langword="null"/> 是 <c>*</c>。</param>
    /// <param name="Kind">主机写成了哪一种。</param>
    /// <param name="Canonical">不带通配的 IPv6 规则的规范写法；其余为 <see langword="null"/>。</param>
    private readonly record struct Rule(string Host, int? Port, RuleHostKind Kind, string? Canonical);

    /// <summary>什么都放（OpenSSH 的 <c>PermitRemoteOpen any</c>）。本机能到的地方远端都能到。</summary>
    public static RemoteOpenPolicy Any { get; } = new(any: true, []);

    /// <summary>什么都不放（<c>PermitRemoteOpen none</c>）。</summary>
    public static RemoteOpenPolicy None { get; } = new(any: false, []);

    /// <summary>只放名单上的。</summary>
    /// <param name="rules">规则，形如 <c>localhost:8080</c>、<c>*.corp.example:443</c>、<c>10.0.0.?:*</c>、<c>[::1]:22</c>。</param>
    /// <exception cref="ArgumentException">规则写法不对（没有端口、端口不在 1–65535、主机为空）。</exception>
    public static RemoteOpenPolicy Allow(params IEnumerable<string> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return new RemoteOpenPolicy(any: false, [.. rules.Select(ParseRule)]);
    }

    /// <summary>放不放行这个目标。</summary>
    /// <param name="host">远端给的主机（域名或 IP 字面量，原样）。调用方随后要拨的必须就是这个字符串，中间不再改写。</param>
    /// <param name="port">远端给的端口。</param>
    /// <remarks>写法不对的主机（见类型说明）在 <see cref="Any"/> 下同样不放。</remarks>
    public bool Permits(string host, int port)
    {
        ArgumentNullException.ThrowIfNull(host);
        TargetKind kind = Classify(host, out string canonical);
        if (kind == TargetKind.Invalid)
        {
            return false;
        }
        return _any || _rules.Any(rule => (rule.Port is null || rule.Port == port) && HostMatches(rule, kind, host, canonical));
    }

    /// <summary>给人看的写法（与 ssh_config 的一致）。</summary>
    public override string ToString() =>
        _any ? "any" : _rules.Length == 0 ? "none"
        : string.Join(' ', _rules.Select(r => $"{(r.Host.Contains(':') ? $"[{r.Host}]" : r.Host)}:{(r.Port?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "*")}"));

    private static Rule ParseRule(string rule)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rule);
        string text = rule.Trim();

        string host;
        string port;
        if (text.StartsWith('['))
        {
            int close = text.IndexOf(']', StringComparison.Ordinal);
            if (close < 0 || close + 1 >= text.Length || text[close + 1] != ':')
            {
                throw new ArgumentException($"放行规则「{rule}」写法不对：IPv6 要写成 [地址]:端口。", nameof(rule));
            }
            host = text[1..close];
            port = text[(close + 2)..];
        }
        else
        {
            int colon = text.LastIndexOf(':');
            if (colon <= 0)
            {
                throw new ArgumentException($"放行规则「{rule}」写法不对：要写成 主机:端口（端口可以是 *）。", nameof(rule));
            }
            host = text[..colon];
            port = text[(colon + 1)..];
        }

        if (host.Length == 0)
        {
            throw new ArgumentException($"放行规则「{rule}」没有主机。", nameof(rule));
        }

        int? number = null;
        if (port != "*")
        {
            number = int.TryParse(port, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int value)
                && value is >= 1 and <= 65535
                ? value
                : throw new ArgumentException($"放行规则「{rule}」的端口要是 1–65535 或 *。", nameof(rule));
        }

        (RuleHostKind kind, string? canonical) = ClassifyRuleHost(host);
        return new Rule(host, number, kind, canonical);
    }

    /// <summary>规则的主机按写法归类。方括号只是把端口隔开的语法：<c>[10.0.0.1]</c> 照样是 IPv4 规则。</summary>
    private static (RuleHostKind Kind, string? Canonical) ClassifyRuleHost(string host)
    {
        if (host.All(c => c == '*'))
        {
            return (RuleHostKind.AnyHost, null);
        }
        if (host.Contains(':'))
        {
            // 不带通配的先换成规范写法：[0:0:0:0:0:0:0:1] 与 [::1] 是同一条。认不出地址的留着文字，拿去和目标的规范写法比（多半永远配不上 —— 宁可错拒）。
            bool wildcard = host.AsSpan().ContainsAny('*', '?');
            string? canonical = !wildcard && IPAddress.TryParse(host, out IPAddress? address) && address.AddressFamily == AddressFamily.InterNetworkV6
                ? address.ToString()
                : null;
            return (RuleHostKind.IPv6, canonical);
        }
        return host.All(c => char.IsAsciiDigit(c) || c is '.' or '*' or '?') && host.Any(char.IsAsciiDigit)
            ? (RuleHostKind.IPv4, null)
            : (RuleHostKind.Name, null);
    }

    /// <summary>远端给的主机归类；IPv6 字面量顺带给出规范写法。</summary>
    private static TargetKind Classify(string host, out string canonical)
    {
        canonical = host;
        if (host.Length == 0)
        {
            return TargetKind.Invalid;
        }

        // 字符白名单在先：NUL、控制字符、空白、% / 通配符一个都进不了后面的解析。
        foreach (char c in host)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':'))
            {
                return TargetKind.Invalid;
            }
        }

        // 带冒号的只能是 IPv6 字面量（白名单已经挡掉了方括号与 %scope）。
        if (host.Contains(':'))
        {
            if (!IPAddress.TryParse(host, out IPAddress? v6) || v6.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return TargetKind.Invalid;
            }
            canonical = v6.ToString();
            return TargetKind.IPv6;
        }

        // .NET 认得的 IPv4 写法比点分四段宽得多（10.1、2130706433、0177.0.0.1、0x7f.1），拨号时也照样当地址连。
        // 只有和自己的规范写法一字不差的才算 IPv4 —— 规则是按文字配的，简写对不齐。
        if (IPAddress.TryParse(host, out IPAddress? v4))
        {
            return v4.ToString() == host ? TargetKind.IPv4 : TargetKind.OtherAddress;
        }

        // 主机名：末尾可以有一个表示绝对名字的点，其余不许有空段。
        ReadOnlySpan<char> name = host.EndsWith('.') ? host.AsSpan(0, host.Length - 1) : host.AsSpan();
        if (name.Length is 0 or > MaxHostNameLength)
        {
            return TargetKind.Invalid;
        }
        ReadOnlySpan<char> last = default;
        foreach (Range range in name.Split('.'))
        {
            last = name[range];
            if (last.Length is 0 or > MaxLabelLength)
            {
                return TargetKind.Invalid;
            }
        }

        // RFC 1123 §2.1：最后一段不会全是数字。这样的串不是主机名，解析器却可能把它读成数字地址（含 0x 十六进制）。
        return LooksNumeric(last) ? TargetKind.Invalid : TargetKind.Name;
    }

    /// <summary>全是十进制数字，或 <c>0x</c> 开头后面全是十六进制数字。</summary>
    private static bool LooksNumeric(ReadOnlySpan<char> label) =>
        !label.ContainsAnyExceptInRange('0', '9')
        || (label.Length >= 2 && label[0] == '0' && label[1] is ('x' or 'X') && label[2..].IndexOfAnyExcept(HexDigits) < 0);

    private static bool HostMatches(Rule rule, TargetKind kind, string host, string canonical) => rule.Kind switch
    {
        RuleHostKind.AnyHost => true,
        RuleHostKind.IPv4 => kind == TargetKind.IPv4 && Matches(rule.Host, host),
        RuleHostKind.IPv6 => kind == TargetKind.IPv6
            && (rule.Canonical is { } exact ? string.Equals(exact, canonical, StringComparison.OrdinalIgnoreCase) : Matches(rule.Host, canonical)),
        _ => kind == TargetKind.Name && Matches(rule.Host, host),
    };

    /// <summary><c>*</c> 配任意多个字符、<c>?</c> 配一个，不分大小写。</summary>
    /// <remarks>纯文字比；<c>*</c> 会吃过点 —— 所以先由 <see cref="HostMatches"/> 按种类挡住，IPv4 规则只拿点分四段来配。</remarks>
    private static bool Matches(string pattern, string value)
    {
        int p = 0, v = 0, star = -1, mark = 0;
        while (v < value.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(value[v])))
            {
                p++;
                v++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = v;
            }
            else if (star >= 0)
            {
                p = star + 1;
                v = ++mark;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }
        return p == pattern.Length;
    }
}
