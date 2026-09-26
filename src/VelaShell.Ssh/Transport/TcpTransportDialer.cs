// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 8305  Happy Eyeballs v2:地址族交替排序、错开发起、先通者胜
//   行为规格: velashell-docs/zh/ssh/spec/08-failures.md §拨号失败的分类;velashell-docs/zh/ssh/spec/09-dialing.md §2.4、§2.5

using System.Net;
using System.Net.Sockets;
using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Ssh.Transport;

/// <summary>最普通的一种拨号：TCP。</summary>
/// <remarks>
/// <para>
/// 它<b>把 socket 的失败翻译成带原因码的异常</b> ——
/// 「连不上」三个字对用户没有任何帮助，而「DNS 查不到这个名字」
/// 与「对方端口没开」要做的事完全不同。
/// </para>
/// <para>
/// <b>名字解析出多个地址时，按 Happy Eyeballs（RFC 8305）错开并发地试</b>：IPv6 与 IPv4 交替排好，
/// 先发第一个，每过 <see cref="AttemptDelay"/> 还没连上就再发下一个（前一个失败了就立刻发），
/// 先连上的那条用，其余的关掉。曾经是逐个顺序试 —— 在「通告了 IPv6、但 IPv6 不通」的网络上
/// （并不少见），第一个 IPv6 地址要等系统的 SYN 超时（Windows 上约 21 秒）才轮到 IPv4，
/// 连接超时早就用得差不多了。
/// </para>
/// </remarks>
internal sealed class TcpTransportDialer : ISshTransportDialer
{
    /// <summary>一个可以共用的实例。</summary>
    public static TcpTransportDialer Shared { get; } = new();

    /// <summary>两次发起之间最多等多久（RFC 8305 §5 推荐 250 ms）。</summary>
    public static TimeSpan AttemptDelay { get; } = TimeSpan.FromMilliseconds(250);

    /// <inheritdoc />
    public SshDialKind Kind => SshDialKind.Tcp;

    /// <summary>
    /// 这个拨号器自己的 TCP 连接上限；默认 <see cref="Timeout.InfiniteTimeSpan"/>：跟连接的 <c>ConnectTimeout</c> 走。
    /// </summary>
    /// <remarks>
    /// 曾经默认 30 秒 —— 使用者给连接设了更长的超时（卫星链路、跨洋的跳板），
    /// TCP 这一步照样在 30 秒被掐断，而那个 30 秒在任何配置里都看不到。
    /// </remarks>
    public TimeSpan ConnectTimeout { get; init; } = Timeout.InfiniteTimeSpan;

    /// <inheritdoc />
    public async ValueTask<Stream> DialAsync(
        SshDialTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        using var timeout = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        if (ConnectTimeout != Timeout.InfiniteTimeSpan)
        {
            timeout.CancelAfter(ConnectTimeout);
        }

        long startedAt = Environment.TickCount64;

        try
        {
            IPAddress[] addresses = IPAddress.TryParse(target.EndPoint.Host, out IPAddress? literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(target.EndPoint.Host, timeout.Token).ConfigureAwait(false);

            if (addresses.Length == 0)
            {
                throw new SocketException((int)SocketError.HostNotFound);
            }

            // 操作系统那一层的 TCP keepalive 一律打开：与 SSH 的保活互不替代，开着没有代价。
            const bool keepAlive = true;
            Socket socket = await RaceAsync(
                    Interleave(addresses),
                    (address, token) => AttemptAsync(address, target.EndPoint.Port, keepAlive, token),
                    AttemptDelay,
                    timeout.Token)
                .ConfigureAwait(false);

            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            string message = $"连 {target.EndPoint} 超时（{ConnectTimeout.TotalSeconds:0.#} 秒）。";
            throw new SshConnectException(SshFailureReason.TcpTimeout, SshPhase.Dialing, message)
            {
                Hops = [DialHops.Hop(Kind, target.EndPoint, succeeded: false, startedAt, message)],
            };
        }
        catch (SocketException ex)
        {
            SshConnectException translated = Translate(ex, target.EndPoint);
            throw new SshConnectException(translated.Reason, translated.Phase, translated.Message, ex)
            {
                Hops = [DialHops.Hop(Kind, target.EndPoint, succeeded: false, startedAt, translated.Message)],
            };
        }
    }

    /// <summary>把地址按族交替排好，从解析结果里的第一个族开始（RFC 8305 §4）。</summary>
    internal static IPAddress[] Interleave(IReadOnlyList<IPAddress> addresses)
    {
        if (addresses.Count == 0)
        {
            return [];
        }

        AddressFamily first = addresses[0].AddressFamily;
        Queue<IPAddress> preferred = new(addresses.Where(a => a.AddressFamily == first));
        Queue<IPAddress> other = new(addresses.Where(a => a.AddressFamily != first));

        List<IPAddress> ordered = [with(addresses.Count)];
        while (preferred.Count > 0 || other.Count > 0)
        {
            if (preferred.TryDequeue(out IPAddress? a))
            {
                ordered.Add(a);
            }
            if (other.TryDequeue(out IPAddress? b))
            {
                ordered.Add(b);
            }
        }
        return [.. ordered];
    }

    /// <summary>错开发起、先通者胜。</summary>
    /// <remarks>
    /// <para>
    /// 每次发起之后等「有一个结束」或「到了 <paramref name="attemptDelay"/>」，先到哪个算哪个：
    /// 到点就发下一个；有一个失败了也立刻发下一个（不必干等剩下的时间）。
    /// </para>
    /// <para>
    /// ⚠️ <b>输了的那几条要收拾干净。</b>取消之后它们多半以取消收场，但也可能恰好在取消之前连上了 ——
    /// 那条连接没人要，不关就一直挂着。全都失败时报最后一个错误。
    /// </para>
    /// </remarks>
    internal static async Task<Socket> RaceAsync(
        IReadOnlyList<IPAddress> addresses,
        Func<IPAddress, CancellationToken, Task<Socket>> attempt,
        TimeSpan attemptDelay,
        CancellationToken cancellationToken)
    {
        using var race = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        List<Task<Socket>> running = [];
        Exception? lastError = null;
        int next = 0;

        // 地址都发出去之后不再有「到点」—— 只剩等某一条结束，或者调用方取消。只建一个，免得每轮都挂一个取消登记。
        var never = Task.Delay(Timeout.InfiniteTimeSpan, race.Token);

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (next < addresses.Count)
                {
                    running.Add(attempt(addresses[next++], race.Token));
                }

                if (running.Count == 0)
                {
                    ExceptionDispatchInfoThrow(lastError ?? new SocketException((int)SocketError.HostNotFound));
                }

                Task delay = next < addresses.Count
                    ? Task.Delay(attemptDelay, cancellationToken)
                    : never;

                Task finished = await Task.WhenAny([.. running, delay]).ConfigureAwait(false);
                if (finished == delay)
                {
                    continue;   // 到点了（或者调用方取消了 —— 循环顶上会抛）
                }

                var done = (Task<Socket>)finished;
                running.Remove(done);

                if (done.IsCompletedSuccessfully)
                {
                    return await done.ConfigureAwait(false);
                }

                // 这一条失败了：记下原因，立刻发下一条（循环顶上）。
                lastError = done.Exception?.InnerException ?? lastError;
            }
        }
        finally
        {
            await race.CancelAsync().ConfigureAwait(false);
            _ = never.ContinueWith(
                static t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            foreach (Task<Socket> loser in running)
            {
                _ = loser.ContinueWith(
                    static t =>
                    {
                        if (t.IsCompletedSuccessfully)
                        {
                            t.Result.Dispose();
                        }
                        else
                        {
                            _ = t.Exception;
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
    }

    private static void ExceptionDispatchInfoThrow(Exception exception) =>
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(exception);

    private static async Task<Socket> AttemptAsync(
        IPAddress address, int port, bool keepAlive, CancellationToken cancellationToken)
    {
        Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (keepAlive)
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            }

            // Nagle 会把小报文攒起来等 40 ms。交互式 shell 上那 40 ms
            // 就是每一次按键的可感延迟。
            socket.NoDelay = true;

            await socket.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
            return socket;
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
