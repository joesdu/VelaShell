// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §5.3   CHANNEL_EOF / CHANNEL_CLOSE
//   RFC 4254 §6.8   xon-xoff
//   RFC 4254 §6.10  exit-status / exit-signal
//   行为规格:       velashell-docs/zh/ssh/spec/05-connection.md §一、§5.4

using System.Buffers;
using System.Threading.Channels;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Channels;

/// <summary>一条通道的有序事件流（<see cref="SshChannel.ReadEventAsync"/>），以及缓存在通道上的结局：退出状态、关闭原因、最近一次 <c>xon-xoff</c>。</summary>
/// <remarks>
/// <para>
/// 从 <see cref="SshChannel"/> 拆出来的协作者。对端的通道请求在接收循环上解析进来（<see cref="OnPeerRequest"/>），
/// 不等待任何东西；要不要回 <c>SUCCESS</c> / <c>FAILURE</c> 由接收循环按返回值决定。
/// </para>
/// <para>
/// 事件流是单读者的；退出状态与关闭原因另外缓存，谁来问、问几次都一样（velashell-docs/zh/ssh/spec/05 §一、§5.4）。
/// </para>
/// </remarks>
internal sealed class ChannelEventQueue
{
    private const int MaxFieldBytes = 64 * 1024;

    private readonly Channel<SshChannelEvent> _events =
        Channel.CreateUnbounded<SshChannelEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>事件流的终结事件；关了之后 <see cref="ReadAsync"/> 一直交回它。</summary>
    private SshChannelEvent.Closed? _closedEvent;

    /// <summary>通道关了时完成。<see cref="WaitForExitAsync"/> 等它，<b>不去读事件流</b>。</summary>
    private readonly TaskCompletionSource _closedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>对端报来的那一份退出状态 / 退出信号（只收第一份）。在进事件流之前写下。</summary>
    private SshChannelEvent? _exitEvent;

    /// <summary>已经收到过退出状态或退出信号了。</summary>
    private int _exitReported;

    /// <summary>最近一次 <c>xon-xoff</c> 的值：-1 没收到过，0 / 1。</summary>
    private int _clientFlowControl = -1;

    /// <summary>事件流里还没被读走的 <see cref="SshChannelEvent.PeerRequest"/> 条数。</summary>
    private int _queuedPeerRequests;

    /// <summary>通道为什么关闭；还没关时是 <see cref="SshChannelCloseReason.Unknown"/>（见 <see cref="SshChannel.CloseReason"/>）。</summary>
    public SshChannelCloseReason CloseReason => Volatile.Read(ref _closedEvent)?.Reason ?? SshChannelCloseReason.Unknown;

    /// <summary>服务端最近一次说的「客户端能不能在本地做 ^S / ^Q 流控」；没说过时为 <see langword="null"/>。</summary>
    public bool? ClientMayDoFlowControl => Volatile.Read(ref _clientFlowControl) switch
    {
        0 => false,
        1 => true,
        _ => null,
    };

    /// <summary>读下一件事；关了之后永远得到那同一条 <see cref="SshChannelEvent.Closed"/>（见 <see cref="SshChannel.ReadEventAsync"/>）。</summary>
    public async ValueTask<SshChannelEvent> ReadAsync(CancellationToken cancellationToken)
    {
        while (await _events.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_events.Reader.TryRead(out SshChannelEvent? channelEvent))
            {
                NoteEventRead(channelEvent);
                return channelEvent;
            }
        }

        return Volatile.Read(ref _closedEvent)
            ?? throw new InvalidOperationException("事件流结束了却没有 Closed 事件 —— 这是库的 bug。");
    }

    /// <summary>读走一条对端请求带来的事件，积压计数减一（见 <see cref="OnPeerRequest"/>）。</summary>
    private void NoteEventRead(SshChannelEvent? channelEvent)
    {
        if (channelEvent is SshChannelEvent.PeerRequest or SshChannelEvent.FlowControl)
        {
            Interlocked.Decrement(ref _queuedPeerRequests);
        }
    }

    /// <summary>等通道关闭，交回退出状态（见 <see cref="SshChannel.WaitForExitAsync"/>）。</summary>
    public async ValueTask<SshExitStatus> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _closedSignal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        // 收到 CLOSE 时可能还没有退出状态 —— 对端实现不规范，或者连接断了。
        // 那时 ExitCode 是 null，那不是 bug 而是事实：进程到底怎么结束的，我们不知道。
        return Volatile.Read(ref _exitEvent) switch
        {
            SshChannelEvent.ExitStatus status => new SshExitStatus(status.Code, null, false, null),
            SshChannelEvent.ExitSignal signal => new SshExitStatus(null, signal.SignalName, signal.CoreDumped, signal.ErrorMessage),
            _ => new SshExitStatus(null, null, false, null),
        };
    }

    /// <summary>对端发了 <c>CHANNEL_EOF</c>（通道已经保证每条只报一次）。</summary>
    public void OnEof() => _events.Writer.TryWrite(new SshChannelEvent.Eof());

    /// <summary>通道收尾：记下原因、写进终结事件、关上事件流，再放出关闭信号。只在通道第一次收尾时调用。</summary>
    public void Close(SshChannelCloseReason reason)
    {
        SshChannelEvent.Closed closed = new(reason);
        Volatile.Write(ref _closedEvent, closed);
        _events.Writer.TryWrite(closed);
        _events.Writer.TryComplete();
        _closedSignal.TrySetResult();
    }

    /// <summary>收到对端发来的通道请求。</summary>
    /// <returns>我们是否「认得」它。不认得且对端要应答时，调用方要回 <c>CHANNEL_FAILURE</c>。</returns>
    public bool OnPeerRequest(string requestType, ReadOnlyMemory<byte> payload)
    {
        // 退出状态 / 退出信号每条通道只有一次（RFC 4254 §6.10）：只收第一份，之后的照样「认得」但丢掉。
        // 曾经每来一份都进事件流，而它们不受下面那个积压上限约束 —— 对端不停地发 exit-signal
        // （每条带两个最长 64 KiB 的字符串），没人读事件流时内存无界增长，完全绕过窗口流控。
        if (requestType is SshProtocolNames.RequestExitStatus or SshProtocolNames.RequestExitSignal
            && Interlocked.Exchange(ref _exitReported, 1) != 0)
        {
            return true;
        }

        if (requestType == SshProtocolNames.RequestExitStatus)
        {
            SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
            SshChannelEvent.ExitStatus status = new((int)reader.ReadUInt32());
            Volatile.Write(ref _exitEvent, status);
            _events.Writer.TryWrite(status);
            return true;
        }

        if (requestType == SshProtocolNames.RequestExitSignal)
        {
            SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
            string signalName = reader.ReadUtf8String(MaxFieldBytes);
            bool coreDumped = reader.ReadBoolean();
            string message = reader.ReadUtf8String(MaxFieldBytes);
            SshChannelEvent.ExitSignal signal = new(signalName, coreDumped, message);
            Volatile.Write(ref _exitEvent, signal);
            _events.Writer.TryWrite(signal);
            return true;
        }

        if (requestType == SshProtocolNames.RequestXonXoff)
        {
            // 〔velashell-docs/zh/ssh/spec/05 §5.4〕类型化成一条事件，最近一次的值留在属性上。
            // 事件与未知请求一起受下面那个积压上限约束：对端可以来回翻个不停。
            SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
            bool clientCanDo = reader.ReadBoolean();
            Volatile.Write(ref _clientFlowControl, clientCanDo ? 1 : 0);
            if (Volatile.Read(ref _queuedPeerRequests) < SshChannel.MaxQueuedEvents
                && _events.Writer.TryWrite(new SshChannelEvent.FlowControl(clientCanDo)))
            {
                Interlocked.Increment(ref _queuedPeerRequests);
            }
            return true;
        }

        // 不认识的请求攒成事件等使用者去读。**有上限**：没人读事件流的话（大多数使用者只读 stdout），
        // 对端每发一条就白占一份内存（载荷最长 256 KiB）—— 它绕过了窗口流控，
        // 是一条不花对端任何代价的内存放大。超出上限的直接丢掉，照样回 FAILURE（调用方会回）。
        // 自己计数：单读者的无界 Channel 不支持 Count（CanCount 为假）。
        if (Volatile.Read(ref _queuedPeerRequests) >= SshChannel.MaxQueuedEvents)
        {
            return false;
        }

        // ⚠️ **必须复制。**载荷背后是传输的接收缓冲，下一次读包就会被覆盖，
        //    而事件的消费者是在那之后才去读它的。
        if (_events.Writer.TryWrite(new SshChannelEvent.PeerRequest(requestType, payload.ToArray())))
        {
            Interlocked.Increment(ref _queuedPeerRequests);
        }
        return false;
    }
}
