using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using VelaShell.Core.Net;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Net;
using VelaShell.Infrastructure.Ssh;
using VelaShell.PluginSdk.Network;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Transport;

namespace VelaShell.Infrastructure.Plugins.Capabilities;

/// <summary>
/// <see cref="INetworkApi" /> 的宿主实现:按宿主的代理设置替插件建立出站 TCP。
/// </summary>
/// <remarks>
/// <para>
/// 与 SSH 的 <see cref="ProxyTransportDialer" /> 用同一个 <see cref="IProxyResolver" /> 选路、同一组
/// SSH 库拨号器(<see cref="DialerChain" />)握手 —— 代理在宿主里只有这一份实现,插件不另写一套,
/// 也看不到代理地址与凭据。路由**每次连接现解析**,用户改了代理设置,下一条连接就生效。
/// </para>
/// <para>
/// 失败按 SDK 契约改写:选了代理时,凡与代理有关的失败(配置不成立、连不上代理、拒绝、认证失败、握手超时)
/// 一律是 <see cref="NetworkProxyException" />,消息与 SSH 的代理报错同一套界面语言 —— 让用户去看代理设置,
/// 而不是对着一个操作系统的「连接被拒绝」查目标;宿主**绝不**因此改为直连。直连时交回操作系统的
/// <see cref="SocketException" />(拒绝、不可达、解析不了)或 <see cref="TimeoutException" />。
/// </para>
/// </remarks>
/// <param name="proxyResolver">代理解析器;为 <see langword="null" /> 时一律直连(无设置服务的测试宿主)。</param>
/// <param name="direct">直连拨号器;默认 <see cref="DialerChain.Tcp" />,测试可替换。</param>
internal sealed class NetworkCapability(IProxyResolver? proxyResolver, ISshTransportDialer? direct = null) : INetworkApi
{
    private static readonly TimeSpan MinConnectTimeout = TimeSpan.FromSeconds(1);

    private readonly ISshTransportDialer _direct = direct ?? DialerChain.Tcp;

    /// <inheritdoc />
    public async Task<HostTcpConnection> ConnectTcpAsync(string host, int port, TcpConnectOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        options ??= new TcpConnectOptions();
        host = host.Trim();

        ProxyRoute route;
        try
        {
            route = proxyResolver?.Resolve(host, port) ?? ProxyRoute.Direct;
        }
        catch (InvalidOperationException ex)
        {
            // ProxyMisconfiguredException:用户显式要求走代理,但代理配置本身不成立。消息已是界面语言。
            throw new NetworkProxyException(ex.Message, ex);
        }

        TimeSpan timeout = ClampTimeout(options.ConnectTimeout);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            return route.Kind == ProxyKind.None
                ? await ConnectDirectAsync(host, port, options, linked.Token).ConfigureAwait(false)
                : await ConnectViaProxyAsync(route, host, port, options, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            string message = $"{HostAddress.FormatEndPoint(host, port)}: {Strings.Get("SshErr_TcpTimeout")}";
            throw route.Kind == ProxyKind.None
                ? new TimeoutException(message, ex)
                : new NetworkProxyException(Strings.Format("Msg_ProxyConnectFailed", message) + Via(route, host, port), ex);
        }
    }

    private async Task<HostTcpConnection> ConnectDirectAsync(string host, int port, TcpConnectOptions options,
        CancellationToken cancellationToken)
    {
        Stream stream;
        try
        {
            stream = await _direct.DialAsync(SshDialTarget.Direct(host, port), cancellationToken).ConfigureAwait(false);
        }
        catch (SshException ex)
        {
            throw TranslateDirectFailure(ex, host, port);
        }

        IPEndPoint? remote = Configure(stream, options);
        return new HostTcpConnection(stream, viaProxy: false, remote);
    }

    private async Task<HostTcpConnection> ConnectViaProxyAsync(ProxyRoute route, string host, int port,
        TcpConnectOptions options, CancellationToken cancellationToken)
    {
        // 「不用代理做 DNS」时先在本机解析成 IP 再交给代理(与 SSH 一致)。目标名在本机解析不了与代理无关。
        string targetHost = host;
        if (!route.ProxyDns)
        {
            targetHost = await LocalDnsResolver.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
        }

        ISshTransportDialer dialer = ProxyTransportDialer.ProxyDialer(route, _direct);

        Stream stream;
        try
        {
            stream = await dialer.DialAsync(SshDialTarget.Direct(targetHost, port), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SshException or IOException or SocketException)
        {
            throw new NetworkProxyException(ProxyTransportDialer.DescribeProxyFailure(route, host, port, ex), ex);
        }

        // 经代理时本机 socket 的对端是代理:选项照设(作用于到代理的一段),但不把它的地址当成目标交出去。
        Configure(stream, options);
        return new HostTcpConnection(stream, viaProxy: true, remoteEndPoint: null);
    }

    /// <summary>
    /// 把插件要的 socket 选项设到实际连出的 socket 上,并取对端地址。拨号器交回的流不是
    /// <see cref="NetworkStream" />(例如 HTTP CONNECT 应答后多读了字节、包了一层)时取不到 socket,
    /// 选项按契约静默忽略 —— SSH 库的 TCP 拨号器本来就开着 <c>TCP_NODELAY</c> 与系统默认的 keep-alive。
    /// </summary>
    internal static IPEndPoint? Configure(Stream stream, TcpConnectOptions options)
    {
        if (stream is not NetworkStream { Socket: { } socket })
        {
            return null;
        }

        TrySet(() => socket.NoDelay = options.NoDelay);
        if (options.KeepAliveIdle > TimeSpan.Zero)
        {
            int seconds = (int)Math.Clamp(Math.Ceiling(options.KeepAliveIdle.TotalSeconds), 1, int.MaxValue);
            TrySet(() => socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true));
            TrySet(() => socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, seconds));
        }

        try
        {
            return socket.RemoteEndPoint as IPEndPoint;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return null;
        }

        static void TrySet(Action set)
        {
            try { set(); }
            catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException or ObjectDisposedException) { }
        }
    }

    internal static TimeSpan ClampTimeout(TimeSpan requested) =>
        requested <= TimeSpan.Zero
            ? new TcpConnectOptions().ConnectTimeout
            : requested < MinConnectTimeout
                ? MinConnectTimeout
                : requested > TcpConnectOptions.MaxConnectTimeout ? TcpConnectOptions.MaxConnectTimeout : requested;

    /// <summary>
    /// 直连失败改写成操作系统的 <see cref="SocketException" />(原样交回库包着的那一个,保留系统消息),
    /// 超时成 <see cref="TimeoutException" />,其余成带界面语言消息的 <see cref="IOException" />。
    /// </summary>
    private static Exception TranslateDirectFailure(SshException ex, string host, int port)
    {
        if (ex.InnerException is SocketException socket)
        {
            ExceptionDispatchInfo.Capture(socket).Throw();
        }

        string message = $"{HostAddress.FormatEndPoint(host, port)}: {SshInterop.Localize(ex)}";
        return ex.Reason switch
        {
            SshFailureReason.TcpTimeout or SshFailureReason.Timeout => new TimeoutException(message, ex),
            SshFailureReason.DnsFailure => new SocketException((int)SocketError.HostNotFound, message),
            SshFailureReason.TcpRefused => new SocketException((int)SocketError.ConnectionRefused, message),
            SshFailureReason.TcpUnreachable => new SocketException((int)SocketError.HostUnreachable, message),
            _ => new IOException(message, ex),
        };
    }

    private static string Via(ProxyRoute route, string host, int port) =>
        $" (via {route.Scheme} {HostAddress.FormatEndPoint(route.Host, route.Port)} → {HostAddress.FormatEndPoint(host, port)})";
}
