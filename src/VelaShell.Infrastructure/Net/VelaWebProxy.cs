using System.Net;
using System.Runtime.CompilerServices;
using VelaShell.Core.Net;

namespace VelaShell.Infrastructure.Net;

/// <summary>
/// 面向 <see cref="HttpClient" /> 家族的代理适配:安装为进程级
/// <see cref="HttpClient.DefaultProxy" /> 后,应用内所有未显式配置 Proxy 的
/// HttpClient(更新检查、Gist 同步、告警 Webhook、头像、插件)每次请求都会
/// 经由 <see cref="IProxyResolver" /> 取当前路由 —— 保存设置后无需重启即生效。
/// SocketsHttpHandler 按代理 URI 的协议建立连接,包括 HTTPS 代理的 TLS 握手。
/// </summary>
public sealed class VelaWebProxy(IProxyResolver resolver) : IWebProxy, ICredentials
{
    // SocketsHttpHandler 在认证时传回 GetProxy 返回的 URI。只随它保留 PAC 的查询参数,不缓存密码。
    private readonly ConditionalWeakTable<Uri, ProxyQuery> _queries = new();
    private sealed record ProxyQuery(string Host, int Port, string Scheme);

    /// <summary>
    /// 安装为进程级默认代理(幂等)。先把当前 <see cref="HttpClient.DefaultProxy" />
    /// 存为「系统代理」数据源,再行覆盖 —— 否则 system 档会解析到我们自己。
    /// </summary>
    public static void Install(IProxyResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        if (HttpClient.DefaultProxy is VelaWebProxy)
        {
            return;
        }
        ProxyResolver.SystemProxySource = HttpClient.DefaultProxy;
        HttpClient.DefaultProxy = new VelaWebProxy(resolver);
    }

    /// <summary>代理认证凭据;按当前设置动态给出,setter 为满足接口而存在(忽略写入)。</summary>
    public ICredentials? Credentials
    {
        get => this;
        set { }
    }

    NetworkCredential? ICredentials.GetCredential(Uri uri, string authType)
    {
        // PAC 可能按目标选不同的代理。HTTPS 代理的凭据绝不能交给明文代理,即使主机端口一样。
        ProxyRoute? route = _queries.TryGetValue(uri, out ProxyQuery? query)
            ? SafeRoute(query.Host, query.Port, query.Scheme)
            : SafeRoute("proxy-credential-probe.invalid", 443, "https");
        return route is { HasCredentials: true } && ProxyUri(route) == uri
            ? route.ToCredential() : null;
    }

    /// <inheritdoc />
    public Uri? GetProxy(Uri? destination)
    {
        if (destination is null)
        {
            return null;
        }
        // 解析失败(代理已启用但配置不完整)必须让请求失败,绝不静默直连泄漏流量。
        // scheme 必须透传:系统 PAC 可按协议分流,不带 scheme 会被另一协议的规则误判。
        ProxyRoute route = resolver.Resolve(destination.Host, destination.Port, destination.Scheme);
        Uri? proxy = ProxyUri(route);
        if (proxy is not null)
        {
            _queries.Add(proxy, new(destination.Host, destination.Port, destination.Scheme));
        }
        return proxy;
    }

    private static Uri? ProxyUri(ProxyRoute route) => route.Scheme is { } scheme
        ? new Uri($"{scheme}://{ProxyResolver.FormatHost(route.Host)}:{route.Port}") : null;

    /// <inheritdoc />
    public bool IsBypassed(Uri? host) =>
        host is null || SafeRoute(host.Host, host.Port, host.Scheme) is { Kind: ProxyKind.None };

    /// <summary>IsBypassed/Credentials 语境下的容错解析:配置不完整时按“不绕过”处理,让 GetProxy 抛出明确错误。</summary>
    private ProxyRoute? SafeRoute(string host, int port, string? schemeHint = null)
    {
        try { return resolver.Resolve(host, port, schemeHint); }
        catch { return null; }
    }
}
