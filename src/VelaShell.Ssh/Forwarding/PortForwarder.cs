// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §7.2  direct-tcpip
//   RFC 1928       SOCKS5（动态转发）
//   行为规格:      velashell-docs/zh/ssh/spec/07-forwarding.md §二、§三、§五、§六、§八

using System.Net;
using System.Net.Sockets;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Session;

namespace VelaShell.Ssh.Forwarding;

/// <summary>本地/动态转发的参数。</summary>
public sealed record PortForwardOptions
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

    /// <summary>监听端口。<c>0</c> 表示由系统分配，结果看 <see cref="PortForwarder.BoundEndPoint"/>。</summary>
    public int BindPort { get; init; }

    /// <summary>单个转发器的并发连接数上限。</summary>
    public int MaxConnections { get; init; } = 1024;

    /// <summary>每条隧道通道的参数。</summary>
    public SshChannelOptions Channel { get; init; } = SshChannelOptions.Default with
    {
        // 隧道上 stderr 不会有东西。
        StderrPolicy = SshStderrPolicy.Discard,
    };

    /// <summary>默认参数。</summary>
    public static PortForwardOptions Default { get; } = new();
}

/// <summary>一个在本机监听、把连接送进 SSH 隧道的转发器。</summary>
/// <remarks>
/// 本地转发（<c>-L</c>）与动态转发（<c>-D</c>）共用这一个类型 ——
/// 它们的差别只有一处：<b>目标是配置死的，还是客户端在 SOCKS 握手里给的</b>。
/// 搬运、计量、半关闭、错误收尾完全一样。
/// </remarks>
public sealed class PortForwarder : IAsyncDisposable
{
    private readonly SshConnection _connection;
    private readonly PortForwardOptions _options;
    private readonly Socket _listener;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _connectionSlots;
    private readonly KeyValuePair<string, object?>[] _tags;

    private readonly string? _targetHost;
    private readonly int _targetPort;

    private Task? _acceptLoop;
    private long _nextConnectionId;
    private long _activeConnections;
    private long _totalConnections;
    private long _bytesUp;
    private long _bytesDown;
    private bool _disposed;

    private PortForwarder(
        SshConnection connection,
        PortForwardOptions options,
        ForwardKind kind,
        Socket listener,
        string? targetHost,
        int targetPort)
    {
        _connection = connection;
        _options = options;
        _listener = listener;
        _targetHost = targetHost;
        _targetPort = targetPort;
        Kind = kind;
        BoundEndPoint = listener.LocalEndPoint;
        _connectionSlots = new SemaphoreSlim(options.MaxConnections, options.MaxConnections);

        _tags =
        [
            new KeyValuePair<string, object?>("kind", kind.ToString()),
            new KeyValuePair<string, object?>("bind", BoundEndPoint?.ToString() ?? "?"),
        ];
    }

    /// <summary>转发的形态。</summary>
    public ForwardKind Kind { get; }

    /// <summary>实际监听的端点。<b>端口给 0 时，实际端口在这里。</b></summary>
    public EndPoint? BoundEndPoint { get; }

    /// <summary>转发器还在跑吗。</summary>
    public bool IsActive => !_disposed && !_lifetime.IsCancellationRequested;

    /// <summary>当前活跃的连接数。</summary>
    public int ActiveConnections => (int)Volatile.Read(ref _activeConnections);

    /// <summary>累计的连接数。</summary>
    public long TotalConnections => Volatile.Read(ref _totalConnections);

    /// <summary>本机 → 远端的应用字节数。</summary>
    public long BytesUp => Volatile.Read(ref _bytesUp);

    /// <summary>远端 → 本机的应用字节数。</summary>
    public long BytesDown => Volatile.Read(ref _bytesDown);

    /// <summary>一条连接建立了。</summary>
    public event EventHandler<ForwardConnectionEventArgs>? ConnectionOpened;

    /// <summary>一条连接结束了（参数里带着它的字节数与时长）。</summary>
    public event EventHandler<ForwardConnectionEventArgs>? ConnectionClosed;

    /// <summary>单条连接出错了 —— <b>转发器仍在跑</b>。</summary>
    public event EventHandler<ForwardErrorEventArgs>? Error;

    // ------------------------------------------------------------ 建立

    /// <summary>起一个本地转发（<c>-L</c>）。</summary>
    /// <param name="connection">会话。</param>
    /// <param name="targetHost">远端目标主机（<b>从服务端视角解析</b>）。</param>
    /// <param name="targetPort">远端目标端口。</param>
    /// <param name="options">参数。</param>
    /// <exception cref="SshForwardException">本地端口起不来。</exception>
    public static PortForwarder StartLocal(
        SshConnection connection,
        string targetHost,
        int targetPort,
        PortForwardOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrEmpty(targetHost);

        PortForwardOptions effective = options ?? PortForwardOptions.Default;
        Socket listener = Bind(effective);

        PortForwarder forwarder = new(
            connection, effective, ForwardKind.Local, listener, targetHost, targetPort);
        forwarder.Start();
        return forwarder;
    }

    /// <summary>起一个动态转发（<c>-D</c>，SOCKS5）。</summary>
    public static PortForwarder StartDynamic(SshConnection connection, PortForwardOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        PortForwardOptions effective = options ?? PortForwardOptions.Default;
        Socket listener = Bind(effective);

        PortForwarder forwarder = new(connection, effective, ForwardKind.Dynamic, listener, null, 0);
        forwarder.Start();
        return forwarder;
    }

    private static Socket Bind(PortForwardOptions options)
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

            // **不留半挂的监听。**起不来就是起不来，别让调用方以为转发生效了。
            throw new SshForwardException(
                $"在 {options.BindAddress}:{options.BindPort} 上起监听失败：{ex.SocketErrorCode}。" +
                (options.BindPort != 0 ? "端口可能已被占用。" : ""),
                ex);
        }
    }

    private void Start() => _acceptLoop = Task.Run(() => AcceptLoopAsync(_lifetime.Token));

    // ------------------------------------------------------------ 主循环

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket inbound;
            try
            {
                inbound = await _listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
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
                Report("accept", "接受入站连接失败。", ex);
                continue;
            }

            // 并发上限撞满：拒掉这一条，**已有连接不受影响**。
            if (!_connectionSlots.Wait(0, CancellationToken.None))
            {
                Report("too-many-connections",
                    $"并发连接数已达上限 {_options.MaxConnections}，这一条被拒绝。", null);
                inbound.Dispose();
                continue;
            }

            _ = Task.Run(() => HandleConnectionAsync(inbound, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleConnectionAsync(Socket inbound, CancellationToken cancellationToken)
    {
        long connectionId = Interlocked.Increment(ref _nextConnectionId);
        EndPoint? source = SafeRemoteEndPoint(inbound);

        NetworkStream stream = new(inbound, ownsSocket: true);
        StreamRelayEndpoint local = new(stream, () => SafeShutdownSend(inbound));

        SshChannel? channel = null;
        string target = "?";

        try
        {
            string host;
            int port;
            byte socksAddressType = 0x01;

            if (Kind == ForwardKind.Dynamic)
            {
                SocksTarget? socks = await SocksHandshake
                    .ReadRequestAsync(local.Input, local.Output, cancellationToken).ConfigureAwait(false);

                if (socks is not { } parsed)
                {
                    Report("socks", "SOCKS 握手非法或命令不支持，这一条被关掉。", null);
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

                Report("channel-open", $"到 {target} 的隧道打不开：{ex.Message}", ex);
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
            // **单条连接的失败绝不影响转发器本身。**一条隧道要能跑几天，
            // 期间必然有连不上的目标、被重置的连接。
            Report("relay", $"到 {target} 的连接出错：{ex.Message}", ex);
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

    private async Task RelayAsync(
        long connectionId,
        EndPoint? source,
        string target,
        IRelayEndpoint local,
        SshChannel channel,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _activeConnections);
        Interlocked.Increment(ref _totalConnections);
        ForwardMetrics.ActiveConnections.Add(1, _tags);
        ForwardMetrics.TotalConnections.Add(1, _tags);

        ConnectionOpened?.Invoke(this, new ForwardConnectionEventArgs(connectionId, source, target));

        try
        {
            ChannelRelayEndpoint remote = new(channel);

            RelayResult result = await DuplexRelay.RunAsync(
                local, remote,
                onBytesFromLeft: bytes =>
                {
                    Interlocked.Add(ref _bytesUp, bytes);
                    ForwardMetrics.Bytes.Add(bytes, [.. _tags, new("direction", "up")]);
                },
                onBytesFromRight: bytes =>
                {
                    Interlocked.Add(ref _bytesDown, bytes);
                    ForwardMetrics.Bytes.Add(bytes, [.. _tags, new("direction", "down")]);
                },
                cancellationToken).ConfigureAwait(false);

            ConnectionClosed?.Invoke(this, new ForwardConnectionEventArgs(
                connectionId, source, target, result.BytesFromLeft, result.BytesFromRight, result.Duration));

            if (result.Error is { } error)
            {
                Report("relay", $"到 {target} 的搬运中断：{error.Message}", error);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeConnections);
            ForwardMetrics.ActiveConnections.Add(-1, _tags);
        }
    }

    // ------------------------------------------------------------ 工具

    private void Report(string reason, string message, Exception? exception)
    {
        ForwardMetrics.Errors.Add(1, [.. _tags, new("reason", reason)]);
        Error?.Invoke(this, new ForwardErrorEventArgs(reason, message, exception));
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

    private static void SafeShutdownSend(Socket socket)
    {
        try
        {
            socket.Shutdown(SocketShutdown.Send);
        }
        catch (Exception)
        {
            // 对面已经走了。半关闭没能做成不影响别的。
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

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

        _connectionSlots.Dispose();
        _lifetime.Dispose();
    }
}
