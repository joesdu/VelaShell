using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Transport;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 跳板(<c>ProxyJump</c>):先连上跳板机,再在它上面开一条到下一跳的
/// <c>direct-tcpip</c> 通道,把那条通道当作下一跳的传输。
/// </summary>
/// <remarks>
/// <para>
/// 链是**递归**的:最内层跳板用 <see cref="ProxyTransportDialer" /> 真正出站
/// (网络代理只作用于那一跳 —— 其余各跳都跑在 SSH 通道里,不经本机网络栈),
/// 外层每一跳都用本类型包住它的内层。
/// </para>
/// <para>
/// <b>跳板连接的生命周期跟着这个拨号器走,而不是跟着它拨出来的流。</b>
/// 由最外层的包装器在断开时一并释放 —— 见 <see cref="DisposeAsync" />。
/// 反过来(让流去关跳板)会在自动重连时把还要用的跳板一起拆掉。
/// </para>
/// <para>
/// <b>主机名交给跳板机去解析</b>:目标常常是只有跳板那一侧解析得了的内网域名,
/// 本地先解一遍既会失败,也泄漏了访问目标。这一条是库的
/// <see cref="SshEndPoint" /> 明确要求的。
/// </para>
/// </remarks>
/// <param name="connectJump">
/// 建立到跳板机的连接。每次拨号调一次 —— 自动重连时跳板也要重连。
/// </param>
internal sealed class SshJumpDialer(Func<CancellationToken, ValueTask<SshConnection>> connectJump)
    : ISshTransportDialer, IAsyncDisposable
{
    private readonly List<SshConnection> _jumps = [];
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <inheritdoc />
    public SshDialKind Kind => SshDialKind.SshJump;

    /// <inheritdoc />
    public async ValueTask<Stream> DialAsync(
        SshDialTarget target, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        SshConnection jump = await connectJump(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            if (_disposed)
            {
                // 拨号过程中被释放了:别把刚建好的跳板漏在外面。
                _ = DisposeQuietlyAsync(jump);
                throw new ObjectDisposedException(nameof(SshJumpDialer));
            }
            _jumps.Add(jump);
        }

        try
        {
            SshChannel channel = await jump
                .OpenTcpTunnelAsync(target.EndPoint.Host, target.EndPoint.Port, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return new SshChannelStream(channel);
        }
        catch (SshException ex)
        {
            // 跳板连上了、通道开不了 —— 这句话要说清是**哪一跳**开不出通道,
            // 否则多跳链路上的失败根本定位不到。
            throw new SshConnectException(
                SshFailureReason.ChannelOpenFailed, SshPhase.Dialing,
                $"跳板 {jump.Description} 打不开到 {target.EndPoint} 的通道:{ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        SshConnection[] jumps;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            jumps = [.. _jumps];
            _jumps.Clear();
        }

        foreach (SshConnection jump in jumps)
        {
            await DisposeQuietlyAsync(jump).ConfigureAwait(false);
        }
    }

    private static async ValueTask DisposeQuietlyAsync(SshConnection connection)
    {
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 释放路径不抛:跳板可能已经先断了。
        }
    }
}
