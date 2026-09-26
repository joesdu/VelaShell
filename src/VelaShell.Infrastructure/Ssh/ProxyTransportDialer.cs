using VelaShell.Core.Net;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Net;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Transport;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 按 VelaShell 的代理设置拨号:直连、HTTP CONNECT、或 SOCKS5。
/// </summary>
/// <remarks>
/// <para>
/// <b>这个类型替掉了 <c>LoopbackProxyRelay</c>(107 行)以及它在包装器里的那一整套装配。</b>
/// 旧路径是:在 <c>127.0.0.1</c> 上开一个一次性监听 → 把 SSH 库的目标改写成那个环回端口 →
/// 库以为自己在直连 → 中继在背后打通代理隧道并双向拷贝。
/// 之所以要这么绕,是因为上游只认 <c>host:port</c>,没有拨号扩展点。
/// </para>
/// <para>
/// 代价不只是那 107 行:它<b>在本机开了一个监听端口</b>。虽然只服务一条连接、连上就
/// <c>Stop()</c>,但那一瞬间同机任何进程都能抢先连上去。现在这条路径整个不存在了 ——
/// 代理隧道就是一条 <see cref="Stream" />,直接交给库。
/// </para>
/// <para>
/// 代理路由**每次拨号现解析**,不在构造时定死:用户可能在两次连接之间改了代理设置,
/// 而自动重连不会重建这个拨号器。
/// </para>
/// </remarks>
/// <param name="proxyResolver">代理解析器;为 <see langword="null" /> 时一律直连。</param>
internal sealed class ProxyTransportDialer(IProxyResolver? proxyResolver) : ISshTransportDialer
{
    /// <summary>
    /// 上一次拨号实际走的路由,给错误消息补上「经哪个代理去哪」。
    /// </summary>
    /// <remarks>
    /// 写在拨号里、读在连接失败的 catch 里,两边不是同一个线程,所以要 volatile。
    /// </remarks>
    private volatile ProxyRoute? _lastRoute;

    /// <inheritdoc />
    /// <remarks>
    /// 声明成 <see cref="SshDialKind.Tcp" /> 还是代理那两种,只影响诊断文本;
    /// 但它得如实反映**上一次**拨号走的是什么,否则日志会骗人。
    /// </remarks>
    public SshDialKind Kind => _lastRoute?.Kind switch
    {
        ProxyKind.Http => SshDialKind.HttpConnect,
        ProxyKind.Socks5 => SshDialKind.Socks5,
        _ => SshDialKind.Tcp,
    };

    /// <inheritdoc />
    public async ValueTask<Stream> DialAsync(
        SshDialTarget target, CancellationToken cancellationToken = default)
    {
        string host = target.EndPoint.Host;
        int port = target.EndPoint.Port;

        ProxyRoute route;
        try
        {
            route = proxyResolver?.Resolve(host, port) ?? ProxyRoute.Direct;
        }
        catch (InvalidOperationException ex)
        {
            // 代理配置本身不合法(地址写错、类型不认识):仍然是一次「连不上」,
            // 要落到 SSH 异常体系里,宿主那边才翻得成连接失败而不是一个裸异常。
            throw new SshConnectException(SshFailureReason.ProxyRefused, SshPhase.Dialing, ex.Message, ex);
        }
        _lastRoute = route;

        // 直连与两种代理的握手都用 SSH 库的拨号器(DialerChain),宿主只管「这次走哪条路」。
        // 直连把主机名交给库去解析(Happy Eyeballs),不在这里先解 —— 内网域名常常只有远端解析得了。
        if (route.Kind == ProxyKind.None)
        {
            return await DialerChain.Tcp.DialAsync(target, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            // 库的代理拨号器把目标名交给代理解析;「不用代理做 DNS」时先在本机解析成 IP 再交出去。
            // 用 with 改端点而不是新建目标:目标上挂着连接的计时器,跳板里等主机密钥裁决时要靠它停表。
            SshDialTarget viaProxy = route.ProxyDns
                ? target
                : target with
                {
                    EndPoint = new SshEndPoint(
                        await LocalDnsResolver.ResolveAsync(host, cancellationToken).ConfigureAwait(false), port),
                };
            return await ProxyDialer(route).DialAsync(viaProxy, cancellationToken).ConfigureAwait(false);
        }
        catch (SshConnectException ex)
        {
            // 「要认证 / 凭据被拒」单独留着原因,其余一律记成代理拒绝 —— 包括连不上代理本身:
            // 那时库给的是 TcpRefused 之类,宿主会把它翻成「目标端口没开」,而没开的其实是代理。
            SshFailureReason reason = ex.Reason == SshFailureReason.ProxyAuthRequired
                ? SshFailureReason.ProxyAuthRequired
                : SshFailureReason.ProxyRefused;
            throw new SshConnectException(reason, SshPhase.Dialing, DescribeProxyFailure(route, host, port, ex), ex)
            {
                Hops = ex.Hops,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SshConnectException(
                SshFailureReason.ProxyRefused, SshPhase.Dialing, DescribeProxyFailure(route, host, port, ex), ex);
        }
    }

    /// <summary>按路由选库的代理拨号器。</summary>
    private static ISshTransportDialer ProxyDialer(ProxyRoute route)
    {
        SshProxyCredentials? credentials = route.HasCredentials ? new(route.Username, route.Password) : null;
        return route.Kind == ProxyKind.Http
            ? DialerChain.HttpConnect(route.Host, route.Port, credentials)
            : DialerChain.Socks5(route.Host, route.Port, credentials);
    }

    /// <summary>
    /// 代理失败的错误补全(#464):界面语言的标题 + 库给的细节 + 走了哪个代理、去往哪个目标。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 认证失败按有没有配凭据分开说:没配是「代理要认证」,配了是「用户名或口令不对」——
    /// 库对这两种给的是同一个原因码,宿主这里有路由,分得清。
    /// </para>
    /// <para>
    /// SSH 的 22 端口经 HTTP CONNECT 常被代理软件限制(只放行 80/443),
    /// 这时直指「换 SOCKS5」,免得用户对着通用报错干瞪眼。
    /// 后缀是纯技术信息,不新增本地化键。
    /// </para>
    /// </remarks>
    private static string DescribeProxyFailure(ProxyRoute route, string host, int port, Exception error)
    {
        string via = $" (via {(route.Kind == ProxyKind.Socks5 ? "socks5" : "http")} {route.Host}:{route.Port} → {host}:{port})";
        if (error is SshConnectException { Reason: SshFailureReason.ProxyAuthRequired })
        {
            return Strings.Get(route.HasCredentials ? "Msg_ProxyAuthFailed" : "SshErr_ProxyAuthRequired") + via;
        }
        string hint = route.Kind == ProxyKind.Http && port == 22
            ? " If the proxy refuses CONNECT to port 22, switch Proxy to socks5 (e.g. 127.0.0.1:10808) or none for TUN."
            : "";
        return Strings.Format("Msg_ProxyConnectFailed", error.Message) + via + hint;
    }
}
