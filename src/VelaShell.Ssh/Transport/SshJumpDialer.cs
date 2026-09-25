// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §7.2  direct-tcpip:经跳板主机到达目标
//   行为规格:      velashell-docs/zh/ssh/spec/09-dialing.md §5

using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Session;

namespace VelaShell.Ssh.Transport;

/// <summary>经另一台 SSH 主机（跳板，<c>ProxyJump</c> / <c>ssh -J</c>）拨号。</summary>
/// <param name="JumpHost">跳板的连接参数：它自己的凭据、主机密钥策略，以及<b>它自己的拨号器</b>。</param>
/// <remarks>
/// <para>
/// 跳板本身是一条完整的 SSH 连接；到目标的字节流是它上面的一条 <c>direct-tcpip</c> 通道。
/// 跳板的 <see cref="SshConnectionOptions.Dialer"/> 可以又是一个代理或跳板 ——
/// 多级跳板就是这么嵌套出来的，不需要另外的机制。
/// </para>
/// <para>
/// <b>返回的流拥有这条跳板连接</b>：流释放时先关通道、再断开跳板。
/// 跳板连接不在多次拨号之间共享 —— 共享会让一条连接的生命周期取决于另一条。
/// </para>
/// <para>
/// 目标地址<b>从跳板的视角</b>解析：内网名字、<c>localhost</c> 都指跳板那边。
/// </para>
/// </remarks>
public sealed record SshJumpDialer(SshConnectionOptions JumpHost) : ISshTransportDialer
{
    /// <inheritdoc />
    public SshDialKind Kind => SshDialKind.SshJump;

    /// <inheritdoc />
    public async ValueTask<Stream> DialAsync(SshDialTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        SshEndPoint jump = new(JumpHost.Host, JumpHost.Port);
        long startedAt = Environment.TickCount64;

        // 跳板这一跳的整个建连都发生在外层的拨号阶段里 ——
        // 把外层的计时器交给它，它在等用户裁决主机密钥时外层也停表（velashell-docs/zh/ssh/spec/09 §2.4）。
        SshConnectionOptions options = JumpHost with { OuterDeadline = target.Deadline };

        SshConnection connection;
        try
        {
            connection = await SshConnectionFactory.ConnectAsync(options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (target.Deadline is { IsExpired: true })
        {
            // 外层的计时器在跳板这一跳建连时到点了。原样当取消往外传的话，外层只知道自己在「拨号」，
            // 报出来的是「建立 TCP 连接超时」—— 可卡住的是跳板的握手。说清是哪一跳。
            throw OuterTimeout(jump, $"经跳板 {JumpHost.UserName}@{jump} 建连时超时", startedAt, ex);
        }
        catch (SshException ex)
        {
            IReadOnlyList<SshHopInfo> hops = ex is SshConnectException { Hops.Count: > 0 } connect
                ? [.. connect.Hops, DialHops.Hop(SshDialKind.SshJump, jump, succeeded: false, startedAt, ex.Message)]
                : [DialHops.Hop(SshDialKind.SshJump, jump, succeeded: false, startedAt, ex.Message)];

            throw DialHops.Rewrap(ex, $"连不上跳板 {JumpHost.UserName}@{jump}：{ex.Message}", hops);
        }

        SshHopInfo reachedJump = DialHops.Hop(SshDialKind.SshJump, jump, succeeded: true, startedAt);
        long tunnelStartedAt = Environment.TickCount64;

        try
        {
            // originator 填 127.0.0.1:0：我们没有一个真实的来源套接字，
            // 编造一个看起来真实的地址只会误导跳板的日志（velashell-docs/zh/ssh/spec/09 §5）。
            SshChannel channel = await connection
                .OpenTcpTunnelAsync(target.EndPoint.Host, target.EndPoint.Port, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return new SshChannelStream(channel, ownsChannel: true, owner: connection);
        }
        catch (Exception ex)
        {
            await connection.DisposeAsync().ConfigureAwait(false);

            if (ex is OperationCanceledException canceled && target.Deadline is { IsExpired: true })
            {
                SshConnectException timeout = OuterTimeout(
                    target.EndPoint, $"跳板 {jump} 转发到 {target.EndPoint} 时超时", tunnelStartedAt, canceled);
                throw new SshConnectException(timeout.Reason, timeout.Phase, timeout.Message, ex)
                {
                    Hops = [reachedJump, .. timeout.Hops],
                };
            }

            if (ex is not SshException)
            {
                throw;
            }

            string detail = ex.Message;
            throw new SshConnectException(
                SshFailureReason.ProxyRefused, SshPhase.Dialing,
                $"跳板 {jump} 不肯转发到 {target.EndPoint}：{detail}", ex)
            {
                Hops = [reachedJump, DialHops.Hop(SshDialKind.SshJump, target.EndPoint, succeeded: false, tunnelStartedAt, detail)],
            };
        }
    }

    private static SshConnectException OuterTimeout(
        SshEndPoint where, string message, long startedAt, OperationCanceledException inner) =>
        new(SshFailureReason.Timeout, SshPhase.Dialing,
            $"{message}（外层连接的计时器到点了；等人输入的时间不算在内）。", inner)
        {
            Hops = [DialHops.Hop(SshDialKind.SshJump, where, succeeded: false, startedAt, "超时")],
        };
}
