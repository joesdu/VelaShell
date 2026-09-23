// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/08-failures.md §拨号失败的分类

using System.Net.Sockets;
using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Ssh.Transport;

/// <summary>最普通的一种拨号：TCP。</summary>
/// <remarks>
/// 它<b>把 socket 的失败翻译成带原因码的异常</b> ——
/// 「连不上」三个字对用户没有任何帮助，而「DNS 查不到这个名字」
/// 与「对方端口没开」要做的事完全不同。
/// </remarks>
public sealed class TcpTransportDialer : ISshTransportDialer
{
    /// <summary>一个可以共用的实例。</summary>
    public static TcpTransportDialer Shared { get; } = new();

    /// <inheritdoc />
    public SshDialKind Kind => SshDialKind.Tcp;

    /// <summary>TCP 连接超时。</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public async ValueTask<Stream> DialAsync(
        SshDialTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        using var timeout = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);

        Socket socket = new(SocketType.Stream, ProtocolType.Tcp);
        long startedAt = Environment.TickCount64;

        try
        {
            if (target.TcpKeepAlive)
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            }

            // Nagle 会把小报文攒起来等 40 ms。交互式 shell 上那 40 ms
            // 就是每一次按键的可感延迟。
            socket.NoDelay = true;

            await socket.ConnectAsync(target.EndPoint.Host, target.EndPoint.Port, timeout.Token)
                .ConfigureAwait(false);

            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            string message = $"连 {target.EndPoint} 超时（{ConnectTimeout.TotalSeconds:0.#} 秒）。";
            throw new SshConnectException(SshFailureReason.TcpTimeout, SshPhase.Dialing, message)
            {
                Hops = [DialHops.Hop(Kind, target.EndPoint, succeeded: false, startedAt, message)],
            };
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            SshConnectException translated = Translate(ex, target.EndPoint);
            throw new SshConnectException(translated.Reason, translated.Phase, translated.Message, ex)
            {
                Hops = [DialHops.Hop(Kind, target.EndPoint, succeeded: false, startedAt, translated.Message)],
            };
        }
        catch (Exception)
        {
            socket.Dispose();
            throw;
        }
    }

    private static SshConnectException Translate(SocketException ex, SshEndPoint endPoint) =>
        ex.SocketErrorCode switch
        {
            SocketError.HostNotFound or SocketError.NoData =>
                new SshConnectException(
                    SshFailureReason.DnsFailure, SshPhase.Dialing,
                    $"DNS 查不到 {endPoint.Host}。", ex),

            SocketError.ConnectionRefused =>
                new SshConnectException(
                    SshFailureReason.TcpRefused, SshPhase.Dialing,
                    $"{endPoint} 拒绝连接 —— 那个端口上没有在听的服务。", ex),

            SocketError.TimedOut =>
                new SshConnectException(
                    SshFailureReason.TcpTimeout, SshPhase.Dialing,
                    $"连 {endPoint} 超时。对方可能被防火墙挡着（丢包而不是拒绝）。", ex),

            SocketError.NetworkUnreachable or SocketError.HostUnreachable =>
                new SshConnectException(
                    SshFailureReason.TcpUnreachable, SshPhase.Dialing,
                    $"到 {endPoint} 的路由不通。", ex),

            _ => new SshConnectException(
                SshFailureReason.TcpRefused, SshPhase.Dialing,
                $"连 {endPoint} 失败：{ex.SocketErrorCode}。", ex),
        };
}
