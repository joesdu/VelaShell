using System.Net;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
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
/// 发送 / 接收分开计数是库的口径,而宿主的契约要的是**总量**,所以这里相加。
/// 两者都保留会更有信息量,但那要改 <see cref="IPortForwardHandle" /> 以及
/// 隧道面板的绑定 —— 不在这次迁移的范围里,记在 feature-plan 里。
/// </para>
/// </remarks>
internal sealed class LibraryPortForwardHandle : IPortForwardHandle
{
    private readonly PortForwarder _forwarder;
    private readonly CancellationTokenRegistration _disconnected;
    private bool _stopped;

    private LibraryPortForwardHandle(SshConnection connection, PortForwarder forwarder)
    {
        _forwarder = forwarder;
        _forwarder.Error += OnError;

        // 连接断了,转发也就没了 —— 但转发器自己不会为此发 Error(它只报单条连接的失败)。
        // 上一版的计量句柄在这里会上报一条通道错误,隧道面板靠它把「运行中」换成带原因的状态;
        // 不补上的话,远程转发在掉线之后会一直显示得好好的。
        _disconnected = connection.Disconnected.Register(static state =>
        {
            var self = (LibraryPortForwardHandle)state!;
            if (!self._stopped)
            {
                self.ChannelError?.Invoke(new VelaSshConnectionException(Strings.Get("SshErr_ClosedByPeer")));
            }
        }, this);
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
    public event Action<Exception>? ChannelError;

    /// <summary>
    /// 建立并启动一条转发。监听绑定失败(端口被占用等)时抛出且不留下半挂的监听。
    /// </summary>
    public static async Task<LibraryPortForwardHandle> CreateAsync(
        SshConnection connection, PortForwardRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request);

        PortForwarder forwarder = request.Kind switch
        {
            PortForwardKind.Local => LocalPortForwarder.Start(
                connection, request.TargetHost!, (int)request.TargetPort!, LocalOptions(request)),

            PortForwardKind.Dynamic => LocalPortForwarder.StartDynamic(connection, LocalOptions(request)),

            PortForwardKind.Remote => await RemotePortForwarder.StartAsync(
                connection, ResolveOutboundHost(request.TargetHost!), (int)request.TargetPort!,
                new RemotePortForwardOptions { BindAddress = request.BoundHost, BindPort = (int)request.BoundPort },
                cancellationToken).ConfigureAwait(false),

            _ => throw new ArgumentOutOfRangeException(
                nameof(request), request.Kind, @"Unknown port forward kind."),
        };

        return new(connection, forwarder);
    }

    private static LocalPortForwardOptions LocalOptions(PortForwardRequest request) => new()
    {
        BindAddress = ParseBindAddress(request.BoundHost),
        BindPort = (int)request.BoundPort,
    };

    /// <summary>把配置里的监听主机翻译成绑定地址(<c>0.0.0.0</c> / <c>*</c> 表示所有接口)。</summary>
    /// <remarks>
    /// 只有本地转发要这一步:它绑的是本机套接字。远程转发的地址原样交给服务端 ——
    /// <c>""</c>、<c>"*"</c>、<c>"localhost"</c> 在服务端是不同的语义。
    /// </remarks>
    internal static IPAddress ParseBindAddress(string host) =>
        host is "0.0.0.0" or "*" ? IPAddress.Any :
        host == "::" ? IPAddress.IPv6Any :
        host is "localhost" or "127.0.0.1" ? IPAddress.Loopback :
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
        ChannelError?.Invoke(e.Exception ?? new VelaSshClientException($"{e.Reason}:{e.Message}"));
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

        await _disconnected.DisposeAsync().ConfigureAwait(false);
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
