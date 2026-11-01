using System.Net.Sockets;
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
/// <param name="recorder">记下这次走了哪个代理(连接信息面板要列);<see langword="null" /> = 不记。</param>
internal sealed class ProxyTransportDialer(IProxyResolver? proxyResolver, SshRouteRecorder? recorder = null) : ISshTransportDialer
{
    // 曾经还要声明拨号器的「种类」(库的接口要求),而这里每次拨号现选路,只好用一个 volatile 字段记「上一次」走的路由。
    // 库的接口已经不要它了 —— 每一跳的种类由库的拨号器(DialerChain)自己记进跳信息。

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
            // 代理配置本身不合法(地址写错、类型不认识):要落到 SSH 异常体系里,宿主那边才翻得成连接失败
            // 而不是一个裸异常。原因码是配置错误 —— 曾经记成可重试的 ProxyRefused,自动重连只会一遍遍再失败。
            throw new SshConnectException(SshFailureReason.InvalidConfiguration, SshPhase.Dialing, ex.Message, ex);
        }
        recorder?.SetProxy(route);

        // 直连与两种代理的握手都用 SSH 库的拨号器(DialerChain),宿主只管「这次走哪条路」。
        // 直连把主机名交给库去解析(Happy Eyeballs),不在这里先解 —— 内网域名常常只有远端解析得了。
        if (route.Kind == ProxyKind.None)
        {
            return await DialerChain.Tcp.DialAsync(target, cancellationToken).ConfigureAwait(false);
        }

        // 库的代理拨号器把目标名交给代理解析;「不用代理做 DNS」时先在本机解析成 IP 再交出去。
        // 用 with 改端点而不是新建目标:目标上挂着连接的计时器,跳板里等主机密钥裁决时要靠它停表。
        SshDialTarget viaProxy = target;
        if (!route.ProxyDns)
        {
            string address;
            try
            {
                address = await LocalDnsResolver.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or IOException or ArgumentException)
            {
                // 目标名在本机解析不了 —— 与代理无关,还没去连代理。曾经落进下面那个 catch,记成 ProxyRefused。
                throw new SshConnectException(
                    SshFailureReason.DnsFailure, SshPhase.Dialing, $"{Strings.Get("SshErr_DnsFailure")} ({host}: {ex.Message})", ex);
            }
            viaProxy = target with { EndPoint = new SshEndPoint(address, port) };
        }

        try
        {
            return await ProxyDialer(route).DialAsync(viaProxy, cancellationToken).ConfigureAwait(false);
        }
        catch (SshConnectException ex)
        {
            // 「连不上代理 / 没配凭据 / 凭据被拒 / 配置不成立」原样留着原因,其余一律记成代理拒绝。
            // 连不上代理本身由库报 ProxyUnreachable(具体是拒绝还是超时在内层异常里);曾经库给的是 TcpRefused 之类,
            // 宿主会把它翻成「目标端口没开」,只好一律改写成 ProxyRefused,又丢了「连不上」与「拒绝转发」的区别(W6)。
            SshFailureReason reason = ex.Reason is SshFailureReason.ProxyUnreachable
                or SshFailureReason.ProxyAuthRequired
                or SshFailureReason.ProxyAuthFailed
                or SshFailureReason.InvalidConfiguration
                ? ex.Reason
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
    /// 代理失败的错误补全(#464):界面语言的标题 + 按库给的种类说的细节 + 走了哪个代理、去往哪个目标。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 认证失败分开说:没配是「代理要认证」(<see cref="SshFailureReason.ProxyAuthRequired" />),
    /// 配了被拒是「用户名或口令不对」(<see cref="SshFailureReason.ProxyAuthFailed" />)。
    /// </para>
    /// <para>
    /// SSH 的 22 端口经 HTTP CONNECT 常被代理软件限制(只放行 80/443),
    /// 这时直指「换 SOCKS5」,免得用户对着通用报错干瞪眼。这句提示用界面语言说(<c>Msg_ProxyHttpSshPortHint</c>):
    /// 要不要追加只看两样宿主自己手里的事实 —— 走的是 HTTP 代理、目标端口是 22 —— 不解析库的句子;
    /// 库按种类交出的应答(<see cref="SshConnectFailureKind.HttpConnectRefused" /> 与状态码)由前半句说。
    /// 库消息里 403 / 405 / 501 的「只放行 80/443」建议是给不经宿主的调用方看的默认文本,这里不再重复。
    /// 曾经这句提示是写死的英文,中 / 日 / 韩界面上整句英文挂在本地化的报错后面。
    /// </para>
    /// <para>后缀 <c>(via …)</c> 是纯技术信息(代理与目标的地址),不本地化。</para>
    /// </remarks>
    private static string DescribeProxyFailure(ProxyRoute route, string host, int port, Exception error)
    {
        string via = $" (via {(route.Kind == ProxyKind.Socks5 ? "socks5" : "http")} {HostAddress.FormatEndPoint(route.Host, route.Port)} → {HostAddress.FormatEndPoint(host, port)})";
        if (error is SshConnectException { Reason: SshFailureReason.ProxyAuthRequired or SshFailureReason.ProxyAuthFailed } auth)
        {
            return Strings.Get(auth.Reason == SshFailureReason.ProxyAuthFailed ? "Msg_ProxyAuthFailed" : "SshErr_ProxyAuthRequired") + via;
        }
        if (error is SshConnectException { Reason: SshFailureReason.ProxyUnreachable } unreachable)
        {
            // 具体原因(拒绝、超时、解析不了)在内层异常里,按原因码翻成界面语言。
            string cause = unreachable.InnerException is SshException inner ? SshInterop.Localize(inner) : unreachable.Message;
            return Strings.Format("Msg_ProxyUnreachable", cause) + via;
        }
        string hint = route.Kind == ProxyKind.Http && port == 22
            ? " " + Strings.Get("Msg_ProxyHttpSshPortHint")
            : "";
        // 后半句:库按种类交出了代理回的是什么(SOCKS5 应答码、HTTP 状态行、握手超时 / 中途断开),用界面语言说;
        // 没归类的(非本库的异常)才用它自己的消息。曾经一律拼库的中文原文。
        string detail = error is SshConnectException { FailureKind: not SshConnectFailureKind.Unknown } structured
            ? SshInterop.DescribeConnectFailure(structured)
            : error.Message;
        return Strings.Format("Msg_ProxyConnectFailed", detail) + via + hint;
    }
}
