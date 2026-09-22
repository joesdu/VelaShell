using System.Net.Sockets;
using VelaShell.Core.Net;
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

        ProxyRoute route = proxyResolver?.Resolve(host, port) ?? ProxyRoute.Direct;
        _lastRoute = route;

        if (route.Kind == ProxyKind.None)
        {
            return await ConnectDirectAsync(host, port, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await ProxyStreamConnector
                .ConnectAsync(route, host, port, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SshConnectException(
                SshFailureReason.ProxyRefused, SshPhase.Dialing, DescribeProxyFailure(route, host, port, ex), ex);
        }
    }

    /// <summary>
    /// 直连。<b>主机名交给 <see cref="Socket" /> 去解析,不在这里先解</b> ——
    /// 内网域名常常只有远端解析得了。
    /// </summary>
    private static async ValueTask<Stream> ConnectDirectAsync(
        string host, int port, CancellationToken cancellationToken)
    {
        Socket socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new SshConnectException(ReasonFor(ex), SshPhase.Dialing, $"连不上 {host}:{port}:{ex.Message}", ex);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        return new NetworkStream(socket, ownsSocket: true);
    }

    /// <summary>把 socket 错误码翻成库的强类型原因,让上层能分「解析不了」和「拒绝连接」。</summary>
    private static SshFailureReason ReasonFor(SocketException ex) => ex.SocketErrorCode switch
    {
        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => SshFailureReason.DnsFailure,
        SocketError.ConnectionRefused => SshFailureReason.TcpRefused,
        SocketError.TimedOut => SshFailureReason.TcpTimeout,
        SocketError.NetworkUnreachable or SocketError.HostUnreachable => SshFailureReason.TcpUnreachable,
        _ => SshFailureReason.Unknown,
    };

    /// <summary>
    /// 代理失败的错误补全(#464):说清走了哪个代理、去往哪个目标。
    /// </summary>
    /// <remarks>
    /// SSH 的 22 端口经 HTTP CONNECT 常被代理软件限制(只放行 80/443),
    /// 这时直指「换 SOCKS5」,免得用户对着通用报错干瞪眼。
    /// 后缀是纯技术信息,不新增本地化键。
    /// </remarks>
    private static string DescribeProxyFailure(ProxyRoute route, string host, int port, Exception error)
    {
        string via = $" (via {(route.Kind == ProxyKind.Socks5 ? "socks5" : "http")} {route.Host}:{route.Port} → {host}:{port})";
        string hint = route.Kind == ProxyKind.Http && port == 22
            ? " If the proxy refuses CONNECT to port 22, switch Proxy to socks5 (e.g. 127.0.0.1:10808) or none for TUN."
            : "";
        return error.Message + via + hint;
    }
}
