// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §7    TCP/IP 端口转发
//   行为规格:      velashell-docs/zh/ssh/spec/07-forwarding.md §五、§八

using System.Net;
using VelaShell.Ssh.Channels;

namespace VelaShell.Ssh.Forwarding;

/// <summary>一条正在跑的端口转发：本地（<c>-L</c>）、动态（<c>-D</c>）或远程（<c>-R</c>）。</summary>
/// <remarks>
/// <para>
/// 三种转发的计量、事件、释放完全一样，差别只在谁监听、目标从哪来 ——
/// 所以面板只认这一个类型就够了，不必按形态各写一遍分支。
/// 起一条转发用 <see cref="LocalPortForwarder"/> 或 <see cref="RemotePortForwarder"/> 上的静态方法。
/// </para>
/// <para>
/// <b>字节数一律从本机的视角说</b>：<see cref="BytesSent"/> 是本机这头送进隧道的，
/// <see cref="BytesReceived"/> 是从隧道那头收回来的 —— 不论连接是谁发起的。
/// </para>
/// </remarks>
public abstract class PortForwarder : IAsyncDisposable
{
    private long _nextConnectionId;
    private long _activeConnections;
    private long _totalConnections;
    private long _bytesSent;
    private long _bytesReceived;

    /// <summary>只给本库的两个派生类型用。</summary>
    private protected PortForwarder(ForwardKind kind) => Kind = kind;

    /// <summary>转发的形态。</summary>
    public ForwardKind Kind { get; }

    /// <summary>转发器还在跑吗。</summary>
    /// <remarks>释放之后、或者 SSH 连接断了之后是 <see langword="false"/>。</remarks>
    public abstract bool IsActive { get; }

    /// <summary>当前活跃的连接数。</summary>
    public int ActiveConnections => (int)Volatile.Read(ref _activeConnections);

    /// <summary>累计的连接数。</summary>
    public long TotalConnections => Volatile.Read(ref _totalConnections);

    /// <summary>本机这头送进隧道的应用字节数。</summary>
    public long BytesSent => Volatile.Read(ref _bytesSent);

    /// <summary>从隧道那头收回本机的应用字节数。</summary>
    public long BytesReceived => Volatile.Read(ref _bytesReceived);

    /// <summary>一条连接建立了。</summary>
    public event EventHandler<ForwardConnectionEventArgs>? ConnectionOpened;

    /// <summary>一条连接结束了（参数里带着它的字节数与时长）。</summary>
    public event EventHandler<ForwardConnectionEventArgs>? ConnectionClosed;

    /// <summary>单条连接出错了 —— <b>转发器仍在跑</b>。</summary>
    public event EventHandler<ForwardErrorEventArgs>? Error;

    /// <inheritdoc />
    public abstract ValueTask DisposeAsync();

    /// <summary>这一条连接在本转发器内的序号。</summary>
    private protected long NextConnectionId() => Interlocked.Increment(ref _nextConnectionId);

    /// <summary>把本机一头与隧道通道对接起来搬运，计量、事件、错误都在这里。</summary>
    /// <param name="connectionId">连接序号。</param>
    /// <param name="source">来源端点（远程转发没有）。</param>
    /// <param name="target">目标的名字（进事件与错误消息）。</param>
    /// <param name="local">本机那一头。</param>
    /// <param name="channel">隧道通道。</param>
    /// <param name="cancellationToken">转发器的生命周期。</param>
    private protected async Task RelayAsync(
        long connectionId,
        EndPoint? source,
        string target,
        IRelayEndpoint local,
        SshChannel channel,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _activeConnections);
        Interlocked.Increment(ref _totalConnections);
        ForwardMetrics.ActiveConnections.Add(1, ForwardEvents.KindTag(Kind));
        ForwardMetrics.TotalConnections.Add(1, ForwardEvents.KindTag(Kind));

        try
        {
            ForwardEvents.Raise(ConnectionOpened, this, new ForwardConnectionEventArgs(connectionId, source, target));

            ChannelRelayEndpoint remote = new(channel);

            RelayResult result = await DuplexRelay.RunAsync(
                local, remote,
                onBytesFromLeft: bytes =>
                {
                    Interlocked.Add(ref _bytesSent, bytes);
                    ForwardMetrics.Bytes.Add(bytes, ForwardEvents.KindTag(Kind), ForwardEvents.DirectionSent);
                },
                onBytesFromRight: bytes =>
                {
                    Interlocked.Add(ref _bytesReceived, bytes);
                    ForwardMetrics.Bytes.Add(bytes, ForwardEvents.KindTag(Kind), ForwardEvents.DirectionReceived);
                },
                cancellationToken).ConfigureAwait(false);

            ForwardEvents.Raise(ConnectionClosed, this, new ForwardConnectionEventArgs(
                connectionId, source, target, result.BytesFromLeft, result.BytesFromRight, result.Duration));

            // 转发器自己在收工（释放、连接断了）时的取消不是这条连接的错。
            if (result.Error is { } error
                && !(error is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                Report(ForwardErrorReason.Relay, $"到 {target} 的搬运中断：{error.Message}", error);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeConnections);
            ForwardMetrics.ActiveConnections.Add(-1, ForwardEvents.KindTag(Kind));
        }
    }

    /// <summary>一条连接失败了：记一笔错误计数，发 <see cref="Error"/> 事件。</summary>
    private protected void Report(ForwardErrorReason reason, string message, Exception? exception)
    {
        ForwardEvents.RecordError(Kind, reason);
        ForwardEvents.Raise(Error, this, new ForwardErrorEventArgs(reason, message, exception));
    }
}
