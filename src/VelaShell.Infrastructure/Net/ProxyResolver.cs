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
public sealed class ProxyResolver(ISettingsService settings) : IProxyResolver
{
    /// <summary>
    /// 「系统代理」的数据源。默认取进程启动时的 <see cref="HttpClient.DefaultProxy" />;
    /// <see cref="VelaWebProxy.Install" /> 覆盖 DefaultProxy 前会先把原值存进来,
    /// 避免 system 类型解析到我们自己而无限递归。
    /// </summary>
    internal static IWebProxy? SystemProxySource { get; set; }

    /// <inheritdoc />
    public ProxyRoute Resolve(string targetHost, int targetPort)
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
            "system" => FromSystem(targetHost, targetPort, o),
            _ => ProxyRoute.Direct,
        };
    }

    private ProxyOptions ReadOptions()
    {
        try { return settings.GetSnapshotBlocking().Proxy; }
        catch { return new(); }
    }

    private static ProxyRoute Explicit(ProxyKind kind, ProxyOptions o) =>
        string.IsNullOrWhiteSpace(o.Host) || o.Port is < 1 or > 65535
            ? throw new InvalidOperationException(Strings.Get("Msg_ProxyMisconfigured"))
            : new(kind, o.Host.Trim(), o.Port, o.Username, o.Password, o.ProxyDns);

    private static ProxyRoute FromSystem(string targetHost, int targetPort, ProxyOptions o)
    {
        IWebProxy sys = SystemProxySource ?? HttpClient.DefaultProxy;
        if (sys is VelaWebProxy)
        {
            // 未经 Install 捕获且 DefaultProxy 已被替换:无法取到真实系统代理,按直连处理。
            return ProxyRoute.Direct;
        }
        try
        {
            // SSH 是裸 TCP,不是 HTTPS:只用 https 探针会撞上按 scheme 分流的 PAC/绕过规则。
            // http 与 https 各问一次,任一说走代理就走代理(取先说走代理的那一档)。
            // 端口同样参与 bypass 判断,故带上真实目标端口(#464)。
            Uri? probe = SelectSystemProbe(sys, targetHost, targetPort);
            if (probe is null)
            {
                return ProxyRoute.Direct;
            }
            Uri? p = SafeGetProxy(sys, probe);
            if (p is null || IsSameTarget(p, probe))
            {
                return ProxyRoute.Direct;
            }
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
            // 系统代理探询失败(非法主机名等)不阻断连接:system 档语义是"跟随系统",系统无代理即直连。
            return ProxyRoute.Direct;
        }
    }

    /// <summary>
    /// 系统代理探针选择:http/https 各问一次,任一命中代理即返回该探针;都绕过则返回 null。
    /// SSH 走裸 TCP,与 https 没有绑定关系,单用 https 探针是 #464 的误路由根源之一。
    /// </summary>
    private static Uri? SelectSystemProbe(IWebProxy sys, string targetHost, int targetPort)
    {
        string formatted = FormatHost(targetHost.Trim());
        Uri httpProbe;
        Uri httpsProbe;
        try
        {
            httpProbe = new($"http://{formatted}:{targetPort}/");
            httpsProbe = new($"https://{formatted}:{targetPort}/");
        }
        catch
        {
            return null;
        }
        bool httpBypassed = SafeIsBypassed(sys, httpProbe);
        bool httpsBypassed = SafeIsBypassed(sys, httpsProbe);
        if (!httpBypassed)
        {
            return httpProbe;
        }
        if (!httpsBypassed)
        {
            return httpsProbe;
        }
        return null;
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
    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host, out IPAddress? ip) && IPAddress.IsLoopback(ip));

    /// <summary>IPv6 字面量拼进 URL 需要方括号。</summary>
    internal static string FormatHost(string host) =>
        IPAddress.TryParse(host, out IPAddress? ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{host}]"
            : host;
}
