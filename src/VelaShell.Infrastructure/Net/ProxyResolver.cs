using System.Net;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Net;
using VelaShell.Core.Resources;

namespace VelaShell.Infrastructure.Net;

/// <summary>
/// <see cref="IProxyResolver" /> 实现:每次解析都读当前设置(设置服务有进程内 JSON 缓存,
/// 读取廉价),保存代理设置后新建的连接立即生效,无需订阅保存事件。
/// </summary>
/// <remarks>
/// <b>system 是解析器,不是协议</b>:读 OS 当前代理并折成 http / socks5,作用于**全部出站 TCP**
/// (SSH / SFTP / FTP 控制与数据 / 全部 HTTP 请求)。数据源是**实时**的
/// <see cref="HttpClient.DefaultProxy" />(Windows 上会给 Internet Settings 注册变更通知,
/// Clash 开关系统代理后不需要重启应用)。
/// <b>none 必须真正直连</b>:不回看系统代理,也不读 <c>HTTP_PROXY</c> / <c>ALL_PROXY</c>。
/// </remarks>
public sealed class ProxyResolver(ISettingsService settings) : IProxyResolver
{
    /// <summary>
    /// 「系统代理」的数据源。默认取进程启动时的 <see cref="HttpClient.DefaultProxy" />;
    /// <see cref="VelaWebProxy.Install" /> 覆盖 DefaultProxy 前会先把原值存进来,
    /// 避免 system 类型解析到我们自己而无限递归。
    /// </summary>
    internal static IWebProxy? SystemProxySource { get; set; }

    /// <inheritdoc />
    public ProxyRoute Resolve(string targetHost, int targetPort, string? schemeHint = null)
    {
        if (IsLoopback(targetHost))
        {
            return ProxyRoute.Direct;
        }
        ProxyOptions o = ReadOptions();
        return o.Type switch
        {
            "http" => Explicit(ProxyKind.Http, o),
            "socks5" => Explicit(ProxyKind.Socks5, o),
            // system 是**解析器**,不是协议:读 OS 当前代理,折成 http / socks5 再交给调用方,
            // 作用于**全部出站 TCP**(SSH / SFTP / FTP 控制与数据 / HTTP)。
            "system" => FromSystem(targetHost, targetPort, o, schemeHint),
            // none = 强制直连。到这里就**不再回看系统代理、也不读 HTTP_PROXY / ALL_PROXY**
            // (进程级 HttpClient.DefaultProxy 已被 VelaWebProxy 接管,不会有环境变量兜底)。
            _ => ProxyRoute.Direct,
        };
    }

    private ProxyOptions ReadOptions()
    {
        try { return settings.GetSnapshotBlocking().Proxy; }
        catch { return new(); }
    }

    private static ProxyRoute Explicit(ProxyKind kind, ProxyOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.Host) || o.Port is < 1 or > 65535)
        {
            throw new ProxyMisconfiguredException(Strings.Get("Msg_ProxyMisconfigured"));
        }
        if (kind == ProxyKind.Socks5 && (Utf8ByteCount(o.Username) > 255 || Utf8ByteCount(o.Password) > 255))
        {
            // SOCKS5 用户名密码子协商(RFC 1929)长度字段各 1 字节:握手必失败,
            // 在这里前置报错,别等到隧道握手时才抛连接失败。
            // 与上面同属「代理配置本身不成立」,类型必须一致 —— 各通道按类型决定不再翻译这条错误。
            throw new ProxyMisconfiguredException(
                Strings.Format("Msg_ProxyConnectFailed", "SOCKS5 username/password exceeds 255 bytes"));
        }
        return new(kind, o.Host.Trim(), o.Port, o.Username, o.Password, o.ProxyDns);
    }

    private static int Utf8ByteCount(string s) => System.Text.Encoding.UTF8.GetByteCount(s ?? "");

    private static ProxyRoute FromSystem(string targetHost, int targetPort, ProxyOptions o, string? schemeHint)
    {
        IWebProxy sys = SystemProxySource ?? HttpClient.DefaultProxy;
        if (sys is VelaWebProxy)
        {
            // 未经 Install 捕获且 DefaultProxy 已被替换:无法取到真实系统代理,按直连处理。
            return ProxyRoute.Direct;
        }
        try
        {
            // 探针 URL 见 BuildSystemProbe:HTTP 通道按自身协议问,裸 TCP 按 http 问。
            Uri? probe = BuildSystemProbe(targetHost, targetPort, schemeHint);
            if (probe is null || SafeIsBypassed(sys, probe))
            {
                return ProxyRoute.Direct;
            }
            Uri? p = SafeGetProxy(sys, probe);
            if (p is null || IsSameTarget(p, probe))
            {
                return ProxyRoute.Direct;
            }
            // 系统配的是 SOCKS 就按 SOCKS5 走,绝不硬套 HTTP CONNECT。
            ProxyKind kind = MapProxyScheme(p.Scheme);
            int port = p.Port is < 1 or > 65535 ? DefaultProxyPort(kind) : p.Port;
            if (string.IsNullOrWhiteSpace(p.Host))
            {
                return ProxyRoute.Direct;
            }
            (string user, string pass) = ExtractProxyCredentials(sys, p);
            return new(kind, p.Host, port, user, pass, o.ProxyDns);
        }
        catch
        {
            // 系统代理探询失败(非法主机名、PAC 求值抛错等):按直连。
            // 系统代理的语义是"跟随系统",取不到就当作系统没配代理 —— 明确回退,不静默乱连。
            return ProxyRoute.Direct;
        }
    }

    /// <summary>
    /// 系统代理探针 URL。
    /// </summary>
    /// <remarks>
    /// HTTP 通道按自己的协议问(http / https)—— 按 URL 分流的 PAC 只有问对协议才拿得准。
    /// SSH / SFTP / FTP 是裸 TCP,协议无名可问,统一按 <c>http</c> 探:系统代理多是手工配置的
    /// host:port,与 scheme 无关,http 是最通用的一档。遇到按 URL 分流的 PAC 时它可能直接回
    /// DIRECT —— 那是**合法答案**,照它直连,不当失败(#464)。
    /// <para>
    /// ⚠️ 裸 TCP 的探针 scheme 在 #464 里从 <c>https</c> 改成了 <c>http</c>,这是**行为变更**
    /// 而非等价改写:Windows 上分协议配置(<c>http=A;https=B</c>)的用户,SSH / FTP 会从
    /// 走 B 变成走 A。单端点配置(Clash 这类把全部流量指到同一个 host:port 的)无差别。
    /// 两者都是启发式 —— 裸 TCP 本就没有"正确"的 scheme 可报;选 http 是因为手工配置的
    /// 系统代理里它是最常被填、也最常被当作通配的一档。
    /// </para>
    /// </remarks>
    private static Uri? BuildSystemProbe(string targetHost, int targetPort, string? schemeHint)
    {
        string formatted = FormatHost(targetHost.Trim());
        string scheme = string.Equals(schemeHint, "https", StringComparison.OrdinalIgnoreCase) ? "https" : "http";
        try
        {
            return new($"{scheme}://{formatted}:{targetPort}/");
        }
        catch
        {
            return null;
        }
    }

    private static bool SafeIsBypassed(IWebProxy sys, Uri target)
    {
        try { return sys.IsBypassed(target); }
        catch { return true; }
    }

    private static Uri? SafeGetProxy(IWebProxy sys, Uri target)
    {
        try { return sys.GetProxy(target); }
        catch { return null; }
    }

    private static bool IsSameTarget(Uri proxy, Uri probe) =>
        Uri.Compare(proxy, probe, UriComponents.HostAndPort | UriComponents.Scheme, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    /// <summary>代理协议映射:socks 系一律按 SOCKS5 处理(含 socks5h),http/https 按 HTTP CONNECT 处理。</summary>
    internal static ProxyKind MapProxyScheme(string scheme) =>
        scheme.StartsWith("socks", StringComparison.OrdinalIgnoreCase)
            ? ProxyKind.Socks5
            : ProxyKind.Http;

    private static int DefaultProxyPort(ProxyKind kind) => kind == ProxyKind.Socks5 ? 1080 : 80;

    /// <summary>
    /// 系统代理凭据提取(#464):优先解析代理 URI 自带的 userinfo,
    /// 其次取 <see cref="IWebProxy.Credentials" />(WebProxy 上用户配过的账号)。
    /// 以往此处直接返回空,带认证的系统代理必吃 407。
    /// </summary>
    internal static (string Username, string Password) ExtractProxyCredentials(IWebProxy sys, Uri proxyUri)
    {
        if (!string.IsNullOrEmpty(proxyUri.UserInfo))
        {
            string userInfo = Uri.UnescapeDataString(proxyUri.UserInfo);
            int sep = userInfo.IndexOf(':');
            if (sep >= 0)
            {
                return (userInfo[..sep], userInfo[(sep + 1)..]);
            }
            return (userInfo, "");
        }
        try
        {
            NetworkCredential? cred = sys.Credentials?.GetCredential(proxyUri, "Basic")
                ?? sys.Credentials?.GetCredential(proxyUri, proxyUri.Scheme);
            if (cred is not null && !string.IsNullOrEmpty(cred.UserName))
            {
                return (cred.UserName, cred.Password ?? "");
            }
        }
        catch
        {
            // 凭据探询失败按无凭据处理,由后续握手的 407 报错,而不是在这里阻断。
        }
        return ("", "");
    }

    /// <summary>环回目标永不走代理:代理自身的环回中继、本机实验环境都依赖这一点。</summary>
    /// <remarks>
    /// 地址层面交给 <see cref="IPAddress.IsLoopback" /> 就够 —— 它比对的是首字节,
    /// 127/8 整段(127.0.0.2…)与 IPv4 映射的 IPv6(::ffff:127.x)本来就在内;
    /// <c>IPAddress.TryParse</c> 也接受 <c>[::1]</c> 这种带方括号的
    /// <see cref="Uri.Host" /> 形式。这里只补它管不到的字符串层面两件事:
    /// 两端空白,以及 FQDN 尾点(<c>localhost.</c> / <c>127.0.0.1.</c>)。
    /// </remarks>
    private static bool IsLoopback(string host)
    {
        string h = host.Trim().TrimEnd('.');
        return h.Equals("localhost", StringComparison.OrdinalIgnoreCase)
               || (IPAddress.TryParse(h, out IPAddress? ip) && IPAddress.IsLoopback(ip));
    }

    /// <summary>IPv6 字面量拼进 URL 需要方括号。</summary>
    /// <remarks>
    /// 入参可能**已经**是带方括号的 <see cref="Uri.Host" />(<c>VelaWebProxy</c> 传的就是它,
    /// 系统代理折出来的 <c>p.Host</c> 同理)。再套一层会得到 <c>[[::1]]</c>,
    /// 后面的 <see cref="Uri" /> 构造直接抛 —— 必须先认出来。
    /// </remarks>
    internal static string FormatHost(string host)
    {
        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            return host;
        }
        return IPAddress.TryParse(host, out IPAddress? ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{host}]"
            : host;
    }
}
