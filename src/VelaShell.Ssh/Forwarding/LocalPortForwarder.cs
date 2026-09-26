// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §7.2  direct-tcpip
//   RFC 1928       SOCKS5（动态转发）
//   行为规格:      velashell-docs/zh/ssh/spec/07-forwarding.md §二、§三、§五、§六、§八

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Session;

namespace VelaShell.Ssh.Forwarding;

/// <summary>本地 / 动态转发的参数。</summary>
public sealed record LocalPortForwardOptions
{
    /// <summary>
    /// 监听地址。
    /// </summary>
    /// <remarks>
    /// 〔决策 velashell-docs/zh/ssh/spec/07 §2.3〕<b>默认绑环回。</b>
    /// 一条隧道的另一端往往是内网数据库或管理接口；默认绑 <c>0.0.0.0</c>
    /// 等于把它暴露给同网段的所有人。要对外开放，使用者得<b>显式</b>写出来。
    /// </remarks>
    public IPAddress BindAddress { get; init; } = IPAddress.Loopback;

    /// <summary>监听端口。<c>0</c> 表示由系统分配，结果看 <see cref="LocalPortForwarder.BoundEndPoint"/>。</summary>
    public int BindPort { get; init; }

    /// <summary>并发连接数上限。</summary>
    public int MaxConnections { get; init; } = 1024;

    /// <summary>动态转发里，SOCKS 握手要在多久之内完成。</summary>
    /// <remarks>
    /// 连上来一句不说的客户端每个都占一个并发名额；没有时限的话，
    /// 占满 <see cref="MaxConnections"/> 之后正经的连接一条也进不来。
    /// 浏览器与 curl 连上就发握手，30 秒绰绰有余。
    /// </remarks>
    public TimeSpan SocksHandshakeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>每条隧道通道的参数。</summary>
    public SshChannelOptions Channel { get; init; } = SshChannelOptions.Default with
    {
        // 隧道上 stderr 不会有东西。
        StderrMode = SshStderrMode.Discard,
    };

    /// <summary>默认参数。</summary>
    public static LocalPortForwardOptions Default { get; } = new();
}

/// <summary>在本机监听、把连接送进 SSH 隧道的转发器（<c>-L</c> / <c>-D</c>）。</summary>
/// <remarks>
/// 本地转发与动态转发共用这一个类型 ——
/// 它们的差别只有一处：<b>目标是配置死的，还是客户端在 SOCKS 握手里给的</b>。
/// 搬运、计量、半关闭、错误收尾完全一样。
/// </remarks>
public sealed class LocalPortForwarder : PortForwarder
{
    private readonly SshConnection _connection;
    private readonly LocalPortForwardOptions _options;
    private readonly Socket _listener;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _connectionSlots;

    private readonly string? _targetHost;
    private readonly int _targetPort;

    /// <summary>还在处理中的连接。释放要等它们收完尾，之后才能放掉槽位信号量。</summary>
    private readonly ConcurrentDictionary<long, Task> _connections = new();

    /// <summary>连接断开时停下监听（见 <see cref="OnConnectionLost"/>）。</summary>
    private CancellationTokenRegistration _disconnectedRegistration;

    private Task? _acceptLoop;
    private bool _disposed;

    private LocalPortForwarder(
        SshConnection connection,
        LocalPortForwardOptions options,
        ForwardKind kind,
        Socket listener,
        string? targetHost,
        int targetPort)
        : base(kind)
    {
        _connection = connection;
        _options = options;
        _listener = listener;
        _targetHost = targetHost;
        _targetPort = targetPort;
        BoundEndPoint = listener.LocalEndPoint;
        _connectionSlots = new SemaphoreSlim(options.MaxConnections, options.MaxConnections);
    }

    /// <summary>实际监听的端点。<b>端口给 0 时，实际端口在这里。</b></summary>
    public EndPoint? BoundEndPoint { get; }

    /// <inheritdoc />
    /// <remarks>SSH 连接断了之后是 <see langword="false"/>：那时监听已经关掉，端口也放出来了。</remarks>
    public override bool IsActive => !_disposed && !_lifetime.IsCancellationRequested;

    // ------------------------------------------------------------ 建立

    /// <summary>起一个本地转发（<c>-L</c>）。</summary>
    /// <param name="connection">会话。</param>
    /// <param name="targetHost">远端目标主机（<b>从服务端视角解析</b>）。</param>
    /// <param name="targetPort">远端目标端口。</param>
    /// <param name="options">参数。</param>
    /// <exception cref="SshForwardException">本地端口起不来。</exception>
    public static LocalPortForwarder Start(
        SshConnection connection,
        string targetHost,
        int targetPort,
        LocalPortForwardOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrEmpty(targetHost);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetPort, 65535);

        LocalPortForwardOptions effective = options ?? LocalPortForwardOptions.Default;
        Socket listener = Bind(effective);

        LocalPortForwarder forwarder = new(
            connection, effective, ForwardKind.Local, listener, targetHost, targetPort);
        forwarder.Run();
        return forwarder;
    }

    /// <summary>起一个动态转发（<c>-D</c>，SOCKS5）。</summary>
    /// <param name="connection">会话。</param>
    /// <param name="options">参数。</param>
    /// <exception cref="SshForwardException">本地端口起不来。</exception>
    public static LocalPortForwarder StartDynamic(SshConnection connection, LocalPortForwardOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        LocalPortForwardOptions effective = options ?? LocalPortForwardOptions.Default;
        Socket listener = Bind(effective);

        LocalPortForwarder forwarder = new(connection, effective, ForwardKind.Dynamic, listener, null, 0);
        forwarder.Run();
        return forwarder;
    }

    private static Socket Bind(LocalPortForwardOptions options)
    {
        Socket listener = new(
            options.BindAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

        try
        {
            listener.Bind(new IPEndPoint(options.BindAddress, options.BindPort));
            listener.Listen(backlog: 128);
            return listener;
        }
        catch (SocketException ex)
        {
            listener.Dispose();

            // 不留半挂的监听。起不来就是起不来，别让调用方以为转发生效了。
            throw new SshForwardException(SshFailureReason.ForwardBindFailed,
                $"在 {options.BindAddress}:{options.BindPort} 上起监听失败：{ex.SocketErrorCode}。" +
                (options.BindPort != 0 ? "端口可能已被占用。" : ""),
                ex);
        }
    }

    private void Run()
    {
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_lifetime.Token));

        // 连接断了就不再监听：留着的话端口一直被占，重连之后同一个转发起不来（端口已被占用），
        // 而这里每接一条都只会换来一次「隧道打不开」。回调在线程池上跑（见 SshConnection.Disconnected）。
        _disconnectedRegistration = _connection.Disconnected.Register(
            static state => ((LocalPortForwarder)state!).OnConnectionLost(), this);
    }

    private void OnConnectionLost()
    {
        // 先关监听再标记不在跑：看到 IsActive 为假的人，可以确信端口已经放出来了。
        _listener.Dispose();
        Lifecycle.CancelInBackground(_lifetime);
    }

    // ------------------------------------------------------------ 主循环

    /// <summary>接受失败之后最长退避多久。</summary>
    private static readonly TimeSpan MaxAcceptBackoff = TimeSpan.FromSeconds(1);

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        TimeSpan backoff = TimeSpan.Zero;

        while (!cancellationToken.IsCancellationRequested)
        {
            Socket inbound;
            try
            {
                inbound = await _listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                backoff = TimeSpan.Zero;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                Report(ForwardErrorReason.Accept, "接受入站连接失败。", ex);

                // 要退避。文件句柄耗尽（EMFILE）这类失败会立刻、反复地再来一次：
                // 不等一下就是一个满核空转、每秒上万条错误事件的循环，而它恰恰发生在
                // 机器已经吃紧的时候。
                backoff = backoff == TimeSpan.Zero
                    ? TimeSpan.FromMilliseconds(50)
                    : TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxAcceptBackoff.Ticks));
                try
                {
                    await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }

            // 并发上限撞满：拒掉这一条，已有连接不受影响。
            if (!_connectionSlots.Wait(0, CancellationToken.None))
            {
                Report(ForwardErrorReason.ConnectionLimit,
                    $"并发连接数已达上限 {_options.MaxConnections}，这一条被拒绝。", null);
                inbound.Dispose();
                continue;
            }

            long connectionId = NextConnectionId();
            Task handling = Task.Run(
                () => HandleConnectionAsync(connectionId, inbound, cancellationToken), CancellationToken.None);
            _connections[connectionId] = handling;

            // 先登记、后挂摘除：任务要是已经跑完了，续体也排在登记之后执行。
            _ = handling.ContinueWith(
                (_, state) => ((LocalPortForwarder)state!)._connections.TryRemove(connectionId, out Task? _),
                this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task HandleConnectionAsync(long connectionId, Socket inbound, CancellationToken cancellationToken)
    {
        EndPoint? source = SafeRemoteEndPoint(inbound);

        NetworkStream stream = new(inbound, ownsSocket: true);
        StreamRelayEndpoint local = new(
            stream, () => StreamRelayEndpoint.ShutdownSend(inbound), abort: () => StreamRelayEndpoint.Reset(inbound));

        SshChannel? channel = null;
        string target = "?";

        try
        {
            string host;
            int port;
            byte socksAddressType = 0x01;

            if (Kind == ForwardKind.Dynamic)
            {
                // 握手有时限：连上来一句不说的客户端，每个都白占一个并发名额，
                // 占满 MaxConnections 之后正经的连接一条也进不来。
                SocksTarget? socks;
                using (CancellationTokenSource handshake =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    handshake.CancelAfter(_options.SocksHandshakeTimeout);
                    try
                    {
                        socks = await SocksHandshake
                            .ReadRequestAsync(local.Input, local.Output, handshake.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        Report(ForwardErrorReason.SocksHandshake,
                            $"SOCKS 握手在 {_options.SocksHandshakeTimeout.TotalSeconds:0.#} 秒内没有完成，这一条被关掉。", null);
                        return;
                    }
                }

                if (socks is not { } parsed)
                {
                    Report(ForwardErrorReason.SocksHandshake, "SOCKS 握手非法或命令不支持，这一条被关掉。", null);
                    return;
                }

                host = parsed.Host;
                port = parsed.Port;
                socksAddressType = parsed.AddressType;
            }
            else
            {
                host = _targetHost!;
                port = _targetPort;
            }

            target = $"{host}:{port}";

            try
            {
                channel = await _connection.OpenTcpTunnelAsync(
                    host, port,
                    originatorHost: (source as IPEndPoint)?.Address.ToString() ?? "127.0.0.1",
                    originatorPort: (source as IPEndPoint)?.Port ?? 0,
                    _options.Channel,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (SshChannelException ex)
            {
                if (Kind == ForwardKind.Dynamic)
                {
                    // 应答码对不对是有实际后果的：curl 与浏览器会据此决定要不要重试、
                    // 以及报给用户哪句话。一律回 0x01 等于把信息丢了。
                    await SocksHandshake.WriteReplyAsync(
                        local.Output, SocksHandshake.MapFailure(ex.OpenFailureReason),
                        socksAddressType, cancellationToken).ConfigureAwait(false);
                }

                Report(ForwardErrorReason.ChannelOpen, $"到 {target} 的隧道打不开：{ex.Message}", ex);
                return;
            }

            if (Kind == ForwardKind.Dynamic)
            {
                await SocksHandshake.WriteReplyAsync(
                    local.Output, SocksReply.Succeeded, socksAddressType, cancellationToken)
                    .ConfigureAwait(false);
            }

            await RelayAsync(connectionId, source, target, local, channel, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 转发器在收工。
        }
        catch (Exception ex)
        {
            // 单条连接的失败绝不影响转发器本身。一条隧道要能跑几天，
            // 期间必然有连不上的目标、被重置的连接。
            Report(ForwardErrorReason.Relay, $"到 {target} 的连接出错：{ex.Message}", ex);
        }
        finally
        {
            if (channel is not null)
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }
            await local.DisposeAsync().ConfigureAwait(false);
            _connectionSlots.Release();
        }
    }

    private static EndPoint? SafeRemoteEndPoint(Socket socket)
    {
        try
        {
            return socket.RemoteEndPoint;
        }
        catch (Exception)
        {
            return null;   // 连接可能已经没了
        }
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await _disconnectedRegistration.DisposeAsync().ConfigureAwait(false);

        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 释放路径不抛。
        }

        _listener.Dispose();

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 同上。
            }
        }

        // 等每条连接收完尾，之后才能释放槽位信号量与令牌源：
        // 曾经是直接释放 —— 还在收尾的连接随后 Release 一个已释放的信号量，
        // 异常落在没人观察的任务里；释放返回时连接也还开着。
        // 取消之后搬运会立刻中止，通道释放本身有时限，所以这里等得到头。
        try
        {
            await Task.WhenAll(_connections.Values).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 同上（HandleConnectionAsync 自己不抛）。
        }

        _connectionSlots.Dispose();
        _lifetime.Dispose();
    }
}
