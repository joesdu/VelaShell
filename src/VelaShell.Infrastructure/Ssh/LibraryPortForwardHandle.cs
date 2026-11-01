using System.Net;
using VelaShell.Core.Models;
using VelaShell.Core.Net;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Core.Tunnels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Forwarding;
using VelaShell.Ssh.Session;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// <see cref="IPortForwardHandle" /> 的实现:直接架在库自己的转发器上。
/// </summary>
/// <remarks>
/// <para>
/// <b>这个类型替掉了 <c>MeteredPortForwardHandle</c>(376 行)。</b>
/// 那 376 行的存在理由只有一条:上一版底层库把转发的数据面**整个做在内部**,
/// 不暴露任何计数 —— 而隧道面板要显示「3 连接 · 1.4 MB」。
/// 于是宿主只好自己持有监听端、自己逐连接搬运、自己数字节,
/// 连远程转发都要绕一圈「让库转发到本机一个临时端口,再由自己接力到真正的目标」。
/// </para>
/// <para>
/// 现在计量在库里,而且三种转发共用一个基类 <see cref="PortForwarder" />:
/// 本类型只是把它的计数与事件接到宿主的契约上。
/// </para>
/// <para>
/// 发送 / 接收分开计数是库的口径,而宿主的契约要的累计量是**总量**,所以这里相加;
/// 实时速率(<see cref="PortForwarder.Throughput" />)与连接快照(<see cref="PortForwarder.Connections" />)按方向原样交出去,
/// 隧道面板显示 ↑ / ↓ 两个速率与当前连接列表。
/// </para>
/// </remarks>
internal sealed class LibraryPortForwardHandle : IPortForwardHandle
{
    private readonly PortForwarder _forwarder;
    private bool _stopped;

    private LibraryPortForwardHandle(PortForwarder forwarder)
    {
        _forwarder = forwarder;
        _forwarder.Error += OnError;

        // 转发器停了(连接断了,或者本端释放)—— 它不会为此发 Error(那只报单条连接的失败)。
        // 隧道面板靠这一条把「运行中」换成带原因的状态;不报的话,远程转发在掉线之后会一直显示得好好的。
        // 报的是真实的停止原因(保活超时、服务端 DISCONNECT 带的原话……),按原因码本地化;本端释放(Aborted)不当成错误报。
        // 曾经挂 Disconnected 一律报「对端关闭」,后来改挂连接的 Completion;现在用转发器自己的 Completion(F41)。
        _ = forwarder.Completion.ContinueWith(
            static (ended, state) =>
            {
                var self = (LibraryPortForwardHandle)state!;
                SshException reason = ended.Result;
                if (!self._stopped && reason.Reason != SshFailureReason.Aborted)
                {
                    self.ChannelError?.Invoke(SshInterop.Translate(reason) ?? new VelaSshConnectionException(reason.Message, reason));
                }
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <inheritdoc />
    public bool IsStarted => !_stopped && _forwarder.IsActive;

    /// <inheritdoc />
    public long BytesTransferred => _forwarder.BytesSent + _forwarder.BytesReceived;

    /// <inheritdoc />
    public int TotalConnections => (int)_forwarder.TotalConnections;

    /// <inheritdoc />
    public int ActiveConnections => _forwarder.ActiveConnections;

    /// <inheritdoc />
    public double SentBytesPerSecond => _forwarder.Throughput.SentPerSecond;

    /// <inheritdoc />
    public double ReceivedBytesPerSecond => _forwarder.Throughput.ReceivedPerSecond;

    /// <inheritdoc />
    public IReadOnlyList<TunnelConnectionInfo> Connections =>
        [.. _forwarder.Connections.Select(c => new TunnelConnectionInfo(
            c.Id, DescribeEndPoint(c.Source), c.Target, c.StartedAt.UtcDateTime, c.BytesSent, c.BytesReceived))];

    /// <summary>
    /// 连接来源给人看的写法:IPv6 带方括号(与面板上别处的 <c>host:port</c> 一致);
    /// Unix 套接字上连进来的客户端没有名字(未绑定的一端),给 <see langword="null" />,由界面写「来源未知」。
    /// </summary>
    internal static string? DescribeEndPoint(EndPoint? endPoint) =>
        endPoint switch
        {
            IPEndPoint ip => $"{HostAddress.Bracket(ip.Address.ToString())}:{ip.Port}",
            DnsEndPoint dns => $"{HostAddress.Bracket(dns.Host)}:{dns.Port}",
            System.Net.Sockets.UnixDomainSocketEndPoint unix when unix.ToString() is { Length: > 0 } path => path,
            _ => null,
        };

    /// <inheritdoc />
    public event Action<Exception>? ChannelError;

    /// <summary>
    /// 建立并启动一条转发。监听绑定失败(端口被占用等)时抛出且不留下半挂的监听。
    /// </summary>
    public static async Task<LibraryPortForwardHandle> CreateAsync(
        SshConnection connection, PortForwardRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request);

        // 写成 [::1] 的 IPv6 去掉方括号:目标主机与远程转发的监听地址都原样发给服务端,带着方括号它解析不了(#611)。
        string boundHost = HostAddress.Normalize(request.BoundHost);
        string targetHost = HostAddress.Normalize(request.TargetHost);

        PortForwarder forwarder = request.Kind switch
        {
            // 目的端是服务器上的 Unix 套接字(-L 端口或本机套接字:/var/run/docker.sock)。
            PortForwardKind.Local when request.TargetSocketPath is { } remoteSocket => LocalPortForwarder.StartToUnixSocket(
                connection, remoteSocket, LocalOptions(request, boundHost)),

            PortForwardKind.Local => LocalPortForwarder.Start(
                connection, targetHost, (int)request.TargetPort!, LocalOptions(request, boundHost)),

            PortForwardKind.Dynamic => LocalPortForwarder.StartDynamic(connection, LocalOptions(request, boundHost)),

            // 本机这头是套接字(-R 端口或服务器套接字:/本机/路径);服务器那头是端口还是套接字由参数里的 ListenSocketPath 定。
            PortForwardKind.Remote when request.TargetSocketPath is { } localSocket =>
                await RemotePortForwarder.StartToUnixSocketAsync(
                    connection, localSocket, RemoteOptions(request, boundHost), cancellationToken).ConfigureAwait(false),

            PortForwardKind.Remote => await RemotePortForwarder.StartAsync(
                connection, ResolveOutboundHost(targetHost), (int)request.TargetPort!,
                RemoteOptions(request, boundHost),
                cancellationToken).ConfigureAwait(false),

            PortForwardKind.RemoteDynamic => await RemotePortForwarder.StartDynamicAsync(
                connection, PermitPolicy(request.PermitRemoteOpen), RemoteOptions(request, boundHost),
                cancellationToken).ConfigureAwait(false),

            _ => throw new ArgumentOutOfRangeException(
                nameof(request), request.Kind, @"Unknown port forward kind."),
        };

        return new(forwarder);
    }

    /// <summary>本地 / 动态转发的参数:绑定地址与端口(或本机的套接字文件)、限速。</summary>
    /// <remarks>设了套接字路径时库不再看地址与端口;此时也不解析地址 —— 那一栏可能是用户没再管的旧值。</remarks>
    internal static LocalPortForwardOptions LocalOptions(PortForwardRequest request, string boundHost) => new()
    {
        BindAddress = request.BoundSocketPath is null ? ParseBindAddress(boundHost) : null,
        BindPort = request.BoundSocketPath is null ? (int)request.BoundPort : 0,
        ListenSocketPath = request.BoundSocketPath,
        MaxBytesPerSecond = request.MaxBytesPerSecond,
    };

    /// <summary>远程转发的参数:服务端的绑定地址与端口(原样),或服务端监听的套接字路径;限速。</summary>
    /// <remarks>设了套接字路径时库不再看地址与端口,端口照样带上(那一栏可能是用户没再管的旧值,库不用它)。</remarks>
    internal static RemotePortForwardOptions RemoteOptions(PortForwardRequest request, string boundHost) => new()
    {
        BindAddress = boundHost,
        BindPort = (int)request.BoundPort,
        ListenSocketPath = request.BoundSocketPath,
        MaxBytesPerSecond = request.MaxBytesPerSecond,
    };

    /// <summary>
    /// 远程动态转发的放行名单:单独一条 <c>any</c> 是全放,别的逐条交给库(写法不对时库当场抛,不起这个转发)。
    /// 空名单是全拒 —— 宿主的服务层已经拦着空名单,这里只是不把它悄悄当成全放。
    /// </summary>
    internal static RemoteOpenPolicy PermitPolicy(IReadOnlyList<string>? rules) =>
        rules is not { Count: > 0 } ? RemoteOpenPolicy.None :
        rules.Any(r => string.Equals(r, RemoteOpenRules.Any, StringComparison.OrdinalIgnoreCase)) ? RemoteOpenPolicy.Any :
        RemoteOpenPolicy.Allow(rules);

    /// <summary>把配置里的监听主机翻译成绑定地址(<c>0.0.0.0</c> / <c>*</c> 表示所有接口)。</summary>
    /// <remarks>
    /// 只有本地转发要这一步:它绑的是本机套接字。远程转发的地址原样交给服务端 ——
    /// <c>""</c>、<c>"*"</c>、<c>"localhost"</c> 在服务端是不同的语义。
    /// </remarks>
    /// <remarks><c>localhost</c> 给 <see langword="null"/>:库同时听 <c>127.0.0.1</c> 与 <c>::1</c>(Q5);写 <c>127.0.0.1</c> 的只听 IPv4。</remarks>
    internal static IPAddress? ParseBindAddress(string host) =>
        host is "0.0.0.0" or "*" ? IPAddress.Any :
        host == "::" ? IPAddress.IPv6Any :
        host == "localhost" ? null :
        host == "127.0.0.1" ? IPAddress.Loopback :
        IPAddress.Parse(host);

    /// <summary>
    /// 远程转发的**本机**目标:<c>0.0.0.0</c> 作为目标没有意义
    /// (它只是「监听所有接口」的写法),按用户的本意落到环回。
    /// </summary>
    internal static string ResolveOutboundHost(string host) =>
        host is "0.0.0.0" or "*" ? "127.0.0.1" :
        host == "::" ? "::1" :
        host;

    /// <remarks>
    /// 单条连接失败(目标拒绝、SOCKS 客户端不守协议)不影响监听端口,
    /// 上报给界面,否则用户只看到「运行中」却连不上。
    /// </remarks>
    private void OnError(object? sender, ForwardErrorEventArgs e)
    {
        if (_stopped)
        {
            return;
        }
        // 远端要连的目标不在放行名单里:库的消息是写给开发者的,界面上要一句说得清的本地化提示(放宽名单就好)。
        if (e.Reason == ForwardErrorReason.TargetNotPermitted)
        {
            ChannelError?.Invoke(new VelaSshClientException(Strings.Get("TunnelSvc_TargetNotPermitted")));
            return;
        }
        // 没带异常的那几种(撞并发上限、SOCKS 握手没完成、隧道在时限内没打开)按原因与事件上的参数用界面语言说;
        // 曾经显示成「Reason:库的中文消息」。
        ChannelError?.Invoke(e.Exception ?? new VelaSshClientException(SshInterop.LocalizeForwardError(e)));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => StopAsync();

    private async ValueTask StopAsync()
    {
        if (_stopped)
        {
            return;
        }
        _stopped = true;

        _forwarder.Error -= OnError;

        // 停止路径上的 catch 一律吞掉:要停的东西本来就在停,重复停止与已断连接抛的
        // 都是清理噪声。记它只会在每次关隧道时刷日志。
        try
        {
            await _forwarder.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 同上。
        }
    }
}
