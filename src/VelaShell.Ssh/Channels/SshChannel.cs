// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §5.1  CHANNEL_OPEN / CONFIRMATION / FAILURE
//   RFC 4254 §5.2  CHANNEL_WINDOW_ADJUST / DATA / EXTENDED_DATA
//   RFC 4254 §5.3  CHANNEL_EOF / CHANNEL_CLOSE
//   RFC 4254 §5.4  CHANNEL_REQUEST / SUCCESS / FAILURE
//   行为规格:      velashell-docs/zh/ssh/spec/05-connection.md §1、§3、§4、§5

using System.Buffers;
using System.IO.Pipelines;
using System.Threading.Channels;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Channels;

/// <summary>通道往会话那边发包的出口。</summary>
internal interface ISshChannelHost
{
    /// <summary>把一个已经拼好的报文发出去。<b>实现必须是线程安全的</b>（多条通道并发发）。</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken);

    /// <summary>
    /// 发一个报文，并在它入队的<b>同一时刻</b>执行 <paramref name="onEnqueued"/>。
    /// </summary>
    /// <remarks>给「应答靠 FIFO 对齐」的请求登记账本用 —— 登记顺序必须等于上线顺序。</remarks>
    ValueTask SendAsync(ReadOnlyMemory<byte> packet, Action onEnqueued, CancellationToken cancellationToken);

    /// <summary>通道已经彻底关了，可以把号收回去。</summary>
    void OnChannelClosed(uint localId, int windowBytes);
}

/// <summary>通道的状态。</summary>
public enum SshChannelState
{
    /// <summary>已发 <c>CHANNEL_OPEN</c>，还没收到应答。</summary>
    Opening,

    /// <summary>双向都能收发。</summary>
    Open,

    /// <summary>我们发过 <c>CHANNEL_EOF</c>，不再发数据；<b>仍然可以收</b>。</summary>
    LocalEof,

    /// <summary>对端发过 <c>CHANNEL_EOF</c>；<b>我们仍然可以发</b>。</summary>
    RemoteEof,

    /// <summary>双向都发过 EOF，但通道还没关。</summary>
    BothEof,

    /// <summary><c>CHANNEL_CLOSE</c> 已收或已发，等另一半。</summary>
    Closing,

    /// <summary>双向 <c>CHANNEL_CLOSE</c> 都走完了。</summary>
    Closed,
}

/// <summary>一条 SSH 通道。</summary>
/// <remarks>
/// <para>
/// 数据面是三条管子：<see cref="StandardOutput"/>、<see cref="StandardError"/>、
/// <see cref="StandardInput"/>；其余一切（退出状态、半关闭、关闭）走
/// <see cref="ReadEventAsync"/> 这条有序事件流。
/// </para>
/// <para>
/// 〔决策 velashell-docs/zh/ssh/spec/05 §4.3〕<b>stdout 与 stderr 是两条独立的 <c>PipeReader</c></b>，
/// 不是一个带标志位的读取接口。理由的硬的那一条是：
/// 两条流必须能被独立消费而不互相饿死。只有一个读接口的话，
/// 调用方轮流读两边，一边读空时另一边可能正在被对端写满 —— 那就是经典的双管道死锁。
/// </para>
/// </remarks>
public sealed class SshChannel : IAsyncDisposable
{
    private const int MaxFieldBytes = 64 * 1024;

    /// <summary>扩展数据里代表 stderr 的类型码（RFC 4254 §5.2）。</summary>
    private const uint ExtendedDataStderr = 1;

    private readonly ISshChannelHost _host;
    private readonly Lock _stateLock = new();

    private readonly Pipe _stdoutPipe;
    private readonly Pipe? _stderrPipe;
    private readonly Pipe _stdinPipe;

    private readonly SshWindow _receiveWindow;
    private readonly SshWindow _sendWindow;
    private readonly AsyncGate _sendWindowGate = new();

    private readonly Channel<SshChannelEvent> _events =
        Channel.CreateUnbounded<SshChannelEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>
    /// 等待应答的通道请求。
    /// </summary>
    /// <remarks>
    /// <b>通道请求的应答没有 id，靠 FIFO 顺序对齐</b>（velashell-docs/zh/ssh/spec/05 §5.1）——
    /// 所以这里是队列而不是字典。用字典就需要一个 id，而协议根本没给。
    /// </remarks>
    private readonly Session.FifoRequestLedger<bool> _pendingRequests = new();

    private readonly CancellationTokenSource _lifetime = new();
    private Task? _stdinPump;
    private Task? _windowAdjustPump;

    /// <summary>消费者已消费、但还没回补给对端的字节数。</summary>
    private long _consumedPendingAdjust;
    private readonly AsyncGate _consumedGate = new();

    /// <summary>接收窗口的伸缩策略。</summary>
    private readonly SshWindowPolicy _windowPolicy;

    /// <summary>连续几轮没有把窗口用尽。</summary>
    private int _idleRounds;

    /// <summary>上一轮回补以来，接收窗口有没有被对端吃到见底。</summary>
    /// <remarks>
    /// <b>这才是「窗口是瓶颈」的真信号。</b>
    /// 用「两次回补的间隔很短」当信号是不对的：局域网上间隔本来就短，
    /// 那会让窗口一路涨到上限，白占几十 MiB 内存 —— 而那条链路上
    /// 窗口根本不是瓶颈。
    /// </remarks>
    private volatile bool _windowWasExhausted;

    private SshChannelState _state = SshChannelState.Opening;
    private bool _closeSent;
    private bool _closeReceived;
    private bool _disposed;

    internal SshChannel(
        ISshChannelHost host,
        uint localId,
        string channelType,
        SshChannelOptions options)
    {
        _host = host;
        LocalId = localId;
        ChannelType = channelType;

        _windowPolicy = options.WindowPolicy;
        int window = options.WindowPolicy.InitialBytes;
        _receiveWindow = new SshWindow(window);
        _sendWindow = new SshWindow(0);   // 真正的值要等 OPEN_CONFIRMATION

        // 管道的暂停水位定得比窗口高：窗口本身就是背压机制，
        // 对端**不可能**发来超过窗口的未消费数据，所以写这一侧永远不该阻塞。
        //
        // 让它阻塞是有害的：接收循环是所有通道共用的，卡在一条通道上
        // 会把其它通道一起饿死（队头阻塞）。而且 `WriteToPipe` 正是靠
        // 「flush 必定同步完成」才敢不等它 —— 一旦真的顶到水位，
        // 那条路会在**上一次 flush 还没完成时继续写同一个 PipeWriter**，
        // 那是对 Pipe 的误用，症状是随机的
        // 「Reading is not allowed after reader was completed」。
        //
        // ⚠️ **水位要按 `MaximumBytes` 算，不是 `InitialBytes`。**
        // 自适应策略下窗口会一路长到上限；按起步值算水位的话，
        // 窗口一旦长过 2×起步，上面那个「不可能」就不成立了 ——
        // 这不是理论风险，它在自适应用例里被复现了出来（约八轮一次）。
        // 固定策略下 Maximum == Initial，这个改动对它没有任何影响。
        PipeOptions pipeOptions = new(
            pauseWriterThreshold: _windowPolicy.MaximumBytes * 2L,
            resumeWriterThreshold: _windowPolicy.MaximumBytes,
            useSynchronizationContext: false);

        _stdoutPipe = new Pipe(pipeOptions);
        _stderrPipe = options.StderrPolicy == SshStderrPolicy.Buffer ? new Pipe(pipeOptions) : null;
        _stdinPipe = new Pipe(new PipeOptions(useSynchronizationContext: false));

        StandardOutput = new WindowedPipeReader(_stdoutPipe.Reader, NoteConsumed);
        StandardError = _stderrPipe is null
            ? new EmptyPipeReader()
            : new WindowedPipeReader(_stderrPipe.Reader, NoteConsumed);
        StandardInput = _stdinPipe.Writer;
    }

    /// <summary>我们给这条通道的编号。</summary>
    public uint LocalId { get; }

    /// <summary>对端给这条通道的编号。<c>CHANNEL_OPEN_CONFIRMATION</c> 之后才有效。</summary>
    public uint RemoteId { get; private set; }

    /// <summary>通道类型（<c>"session"</c> / <c>"direct-tcpip"</c> / …）。</summary>
    public string ChannelType { get; }

    /// <summary>当前状态。</summary>
    public SshChannelState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    /// <summary>对端宣告的单个数据段上限。<b>发送时必须遵守</b>。</summary>
    public int RemoteMaxPacketBytes { get; private set; }

    /// <summary>远端的标准输出。</summary>
    public PipeReader StandardOutput { get; }

    /// <summary>
    /// 远端的标准错误。
    /// </summary>
    /// <remarks>
    /// <see cref="SshStderrPolicy.Discard"/> 时这是一条**立刻结束的空流** ——
    /// 不是一条永远不返回的流，那会让调用方挂死。
    /// </remarks>
    public PipeReader StandardError { get; }

    /// <summary>写进去的内容变成 <c>CHANNEL_DATA</c>。</summary>
    public PipeWriter StandardInput { get; }

    /// <summary>当前的接收窗口剩余（诊断用）。</summary>
    public uint ReceiveWindowRemaining => _receiveWindow.Remaining;

    /// <summary>
    /// 接收窗口当前的**额定大小**。自适应策略下它会随链路情况变。
    /// </summary>
    public int ReceiveWindowSize => _receiveWindow.Size;

    /// <summary>当前的发送窗口剩余（诊断用）。</summary>
    public uint SendWindowRemaining => _sendWindow.Remaining;

    // ------------------------------------------------------------ 事件

    /// <summary>读下一件事。</summary>
    /// <remarks>
    /// 通道关闭之后这个方法会抛 <see cref="ChannelClosedException"/> —— 在那之前
    /// 一定会先读到一条 <see cref="SshChannelEvent.Closed"/>。
    /// </remarks>
    public ValueTask<SshChannelEvent> ReadEventAsync(CancellationToken cancellationToken = default) =>
        _events.Reader.ReadAsync(cancellationToken);

    /// <summary>把这条通道当成一条双向字节流（读 stdout、写 stdin）。</summary>
    /// <param name="ownsChannel">释放流时是否一并关掉通道。</param>
    /// <remarks>
    /// <c>direct-tcpip</c> / <c>direct-streamlocal</c> 这类隧道通道最常这么用 ——
    /// 交给只认 <see cref="Stream"/> 的 API（<c>SslStream</c>、HTTP 客户端的连接回调、拨号器）。
    /// </remarks>
    public SshChannelStream AsStream(bool ownsChannel = true) => new(this, ownsChannel);

    /// <summary>还有没有事件可读（不阻塞）。</summary>
    public bool TryReadEvent(out SshChannelEvent? channelEvent) =>
        _events.Reader.TryRead(out channelEvent);

    // ------------------------------------------------------------ 发送

    /// <summary>发一个通道请求。</summary>
    /// <param name="requestType">请求类型。</param>
    /// <param name="payload">类型相关的数据。</param>
    /// <param name="wantReply">要不要等应答。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns><paramref name="wantReply"/> 为假时恒为 <see langword="true"/>。</returns>
    public async ValueTask<bool> SendRequestAsync(
        string requestType,
        ReadOnlyMemory<byte> payload = default,
        bool wantReply = true,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);


        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteMessageNumber(SshMessageNumber.ChannelRequest);
        writer.WriteUInt32(RemoteId);
        writer.WriteUtf8String(requestType);
        writer.WriteBoolean(wantReply);
        writer.WriteRaw(payload.Span);

        if (!wantReply)
        {
            await _host.SendAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
            return true;
        }

        // 登记与入队是**同一个动作**：应答靠 FIFO 对齐。
        // 先发后登记，一个快到的应答会发现账本是空的；先登记后发（中间隔着背压等待），
        // 并发的两个请求可能登记顺序与上线顺序相反，而那会把应答安到对方头上。
        Task<bool>? reply = null;
        await _host.SendAsync(
            buffer.WrittenMemory, () => reply = _pendingRequests.Register(), cancellationToken)
            .ConfigureAwait(false);

        return await reply!.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>发 <c>CHANNEL_EOF</c>：我们不再发数据了。</summary>
    /// <remarks>
    /// <b>这不是关闭通道。</b>发完之后仍然可以继续收对端的数据。
    /// </remarks>
    public async ValueTask SendEofAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (_state is SshChannelState.LocalEof or SshChannelState.BothEof
                or SshChannelState.Closing or SshChannelState.Closed)
            {
                return;
            }
            _state = _state == SshChannelState.RemoteEof
                ? SshChannelState.BothEof
                : SshChannelState.LocalEof;
        }

        // 先把 stdin 里还没发出去的内容冲干净，再发 EOF ——
        // 反过来会让最后一段数据排在 EOF 之后，对端多半已经不读了。
        await _stdinPipe.Writer.CompleteAsync().ConfigureAwait(false);
        if (_stdinPump is not null)
        {
            try
            {
                await _stdinPump.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // 泵自己出的错已经反映在通道状态上了，这里不重复报。
            }
        }

        await SendSimpleAsync(SshMessageNumber.ChannelEof, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>关闭通道。</summary>
    /// <remarks>
    /// 发出 <c>CHANNEL_CLOSE</c> 并等对端也发一个 —— <b><c>CLOSE</c> 必须双向</b>，
    /// 只有双方都发过之后通道号才可以回收。
    /// </remarks>
    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        bool needSend;
        lock (_stateLock)
        {
            if (_state == SshChannelState.Closed)
            {
                return;
            }
            needSend = !_closeSent;
            _closeSent = true;
            if (_state != SshChannelState.Closed)
            {
                _state = SshChannelState.Closing;
            }
        }

        if (needSend)
        {
            try
            {
                await SendSimpleAsync(SshMessageNumber.ChannelClose, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // 会话可能已经没了。那样的话通道当然也关了，不必再报一次。
                FinishClose(SshChannelCloseReason.SessionClosed);
                return;
            }
        }

        MaybeFinishClose(SshChannelCloseReason.ClosedLocally);
    }

    private async ValueTask SendSimpleAsync(SshMessageNumber number, CancellationToken cancellationToken)
    {
        byte[] packet = new byte[5];
        packet[0] = (byte)number;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(1), RemoteId);
        await _host.SendAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 会话回调

    internal void OnOpenConfirmed(uint remoteId, uint initialWindow, uint maxPacket)
    {
        RemoteId = remoteId;
        RemoteMaxPacketBytes = (int)Math.Min(maxPacket, int.MaxValue);
        _sendWindow.Add(initialWindow);

        lock (_stateLock)
        {
            _state = SshChannelState.Open;
        }

        _sendWindowGate.Signal();
        _stdinPump = Task.Run(() => PumpStandardInputAsync(_lifetime.Token));
        _windowAdjustPump = Task.Run(() => PumpWindowAdjustAsync(_lifetime.Token));
    }

    internal void OnOpenFailed() => FinishClose(SshChannelCloseReason.ClosedByPeer);

    /// <summary>收到 <c>CHANNEL_DATA</c>。</summary>
    /// <returns>窗口够不够。<see langword="false"/> 是对端的协议违规。</returns>
    internal bool OnData(ReadOnlySequence<byte> data)
    {
        int length = (int)data.Length;
        if (!_receiveWindow.TryConsume(length))
        {
            return false;
        }

        NoteWindowPressure();

        if (!TryDeliver(_stdoutPipe.Writer, data))
        {
            // 没交出去（通道在关、对端已 EOF、消费者不读了）—— 丢弃，
            // 且**立刻回补窗口**：不然「丢弃」就变成了让对端停住的死锁。
            NoteConsumed(length);
        }
        return true;
    }

    /// <summary>收到 <c>CHANNEL_EXTENDED_DATA</c>。</summary>
    internal bool OnExtendedData(uint dataTypeCode, ReadOnlySequence<byte> data)
    {
        int length = (int)data.Length;

        // 扩展数据**同样计入窗口**。忘了这一点的症状是
        // stderr 大量输出时窗口被吃空，整条通道停住。
        if (!_receiveWindow.TryConsume(length))
        {
            return false;
        }

        NoteWindowPressure();

        // 〔决策 velashell-docs/zh/ssh/spec/05 §4.2〕非 stderr 的类型码：丢弃、计入窗口、不报错。
        // 保留值的语义未来可能被定义，为它断开会让我们无法与新实现共处。
        if (dataTypeCode != ExtendedDataStderr || _stderrPipe is null || !TryDeliver(_stderrPipe.Writer, data))
        {
            // 丢弃也要立刻回补窗口 —— 不然「丢弃」就变成了死锁。
            NoteConsumed(length);
        }
        return true;
    }

    /// <summary>收到 <c>CHANNEL_WINDOW_ADJUST</c>。</summary>
    /// <returns>加完有没有溢出 <c>uint32</c>。溢出是协议违规。</returns>
    internal bool OnWindowAdjust(uint bytes)
    {
        if (!_sendWindow.Add(bytes))
        {
            return false;
        }
        _sendWindowGate.Signal();
        return true;
    }

    /// <summary>收到 <c>CHANNEL_EOF</c>。</summary>
    internal void OnEof()
    {
        lock (_stateLock)
        {
            if (_state is SshChannelState.Closing or SshChannelState.Closed)
            {
                return;
            }
            _state = _state == SshChannelState.LocalEof
                ? SshChannelState.BothEof
                : SshChannelState.RemoteEof;
        }

        // 对端不再发数据 —— 把读这一侧收尾，消费者读到 IsCompleted。
        // 状态已经在锁里改掉了，TryDeliver 不会再往这两条管道里写（见它的说明）。
        CompleteReceivePipes();
        _events.Writer.TryWrite(new SshChannelEvent.Eof());
    }

    /// <summary>收尾两条接收管道。调用前状态必须已经在锁里改成不再接收的值。</summary>
    private void CompleteReceivePipes()
    {
        _stdoutPipe.Writer.Complete();
        _stderrPipe?.Writer.Complete();
    }

    /// <summary>收到 <c>CHANNEL_CLOSE</c>。</summary>
    /// <returns>要不要回一个 <c>CHANNEL_CLOSE</c>。</returns>
    internal bool OnClose()
    {
        bool mustReply;
        lock (_stateLock)
        {
            _closeReceived = true;
            mustReply = !_closeSent;
            _closeSent = true;
        }

        return mustReply;
    }

    /// <summary>双向 CLOSE 都走完了。</summary>
    internal void OnCloseCompleted(SshChannelCloseReason reason) => FinishClose(reason);

    /// <summary>收到通道请求的应答。</summary>
    /// <returns>队列里有没有人在等。<see langword="false"/> 说明 FIFO 失步了。</returns>
    internal bool OnRequestReply(bool success) => _pendingRequests.TryComplete(success);

    /// <summary>收到对端发来的通道请求。</summary>
    /// <returns>我们是否「认得」它。不认得且对端要应答时，调用方要回 <c>CHANNEL_FAILURE</c>。</returns>
    internal bool OnPeerRequest(string requestType, ReadOnlyMemory<byte> payload)
    {
        if (requestType == "exit-status")
        {
            SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
            _events.Writer.TryWrite(new SshChannelEvent.ExitStatus((int)reader.ReadUInt32()));
            return true;
        }

        if (requestType == "exit-signal")
        {
            SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
            string signalName = reader.ReadUtf8String(MaxFieldBytes);
            bool coreDumped = reader.ReadBoolean();
            string message = reader.ReadUtf8String(MaxFieldBytes);
            _events.Writer.TryWrite(new SshChannelEvent.ExitSignal(signalName, coreDumped, message));
            return true;
        }

        // ⚠️ **必须复制。**载荷背后是传输的接收缓冲，下一次读包就会被覆盖，
        //    而事件的消费者是在那之后才去读它的。
        _events.Writer.TryWrite(new SshChannelEvent.PeerRequest(requestType, payload.ToArray()));
        return false;
    }

    /// <summary>会话没了。</summary>
    internal void OnSessionClosed() => FinishClose(SshChannelCloseReason.SessionClosed);

    // ------------------------------------------------------------ 内部

    private bool IsClosedOrClosing()
    {
        lock (_stateLock)
        {
            return _state is SshChannelState.Closing or SshChannelState.Closed;
        }
    }

    /// <summary>把收到的数据交给消费者。</summary>
    /// <returns>
    /// 交出去了为 <see langword="true"/>；因为通道在关、对端已经 EOF、
    /// 或者消费者已经不读了而丢弃为 <see langword="false"/>。
    /// </returns>
    /// <remarks>
    /// ⚠️ <b>「看状态」与「写管道」必须在同一把锁里。</b>
    /// 分开的话，接收循环刚看完「还开着」，用户线程就 Dispose 了通道、
    /// 把管道收了尾 —— 接着那一次写就撞上一个已完成的 <see cref="PipeWriter"/>，
    /// 异常冒到接收循环里，<b>整条会话</b>因为一条通道的关闭而被判死。
    /// 收尾一侧（<see cref="FinishClose"/>、<see cref="OnEof"/>）总是先在锁里改状态、
    /// 再完成管道，所以锁里看到「还开着」时，管道一定还没被完成。
    /// </remarks>
    private bool TryDeliver(PipeWriter writer, ReadOnlySequence<byte> data)
    {
        lock (_stateLock)
        {
            // CLOSE 之后收到的是在途数据 —— 丢弃，且**不报错**（velashell-docs/zh/ssh/spec/05 §1 规则 4）。
            // EOF 之后再来数据是对端的错，但为它断开整条会话不值得 —— 丢弃即可。
            if (_state is SshChannelState.Closing or SshChannelState.Closed
                or SshChannelState.RemoteEof or SshChannelState.BothEof)
            {
                return false;
            }

            return WriteToPipe(writer, data);
        }
    }

    /// <summary>把收到的数据写进管道。</summary>
    /// <returns>消费者已经不读了（读端完成）时为 <see langword="false"/>。</returns>
    /// <exception cref="InvalidOperationException">
    /// 管道顶到了暂停水位 —— 说明窗口记账坏了，见方法体里的说明。
    /// </exception>
    private static bool WriteToPipe(PipeWriter writer, ReadOnlySequence<byte> data)
    {
        foreach (ReadOnlyMemory<byte> segment in data)
        {
            writer.Write(segment.Span);
        }

        // 不等 FlushAsync：接收循环是所有通道共用的，等在一条通道上
        // 会把其它通道一起饿死。管道的水位定得比窗口高，这里不该真的阻塞。
        ValueTask<FlushResult> flush = writer.FlushAsync();
        if (flush.IsCompletedSuccessfully)
        {
            // 读端完成了：消费者不要这条流了。这批字节永远不会被「消费」，
            // 调用方要替它回补窗口，不然对端会停在一个再也不会涨的窗口上。
            return !flush.Result.IsCompleted;
        }

        // 走到这里说明水位被顶住了 —— **按上面的水位设置，这不该发生**：
        // 未消费的数据受接收窗口限制，而水位是窗口上限的两倍。
        //
        // 真走到了，说明我们自己的窗口记账坏了。此时**绝不能接着写** ——
        // 上一次 flush 还没完成就再写同一个 PipeWriter 是对 Pipe 的误用，
        // 它不会当场报错，只会让管道在之后的某个时刻抛出一句
        // 看不出所以然的「Reading is not allowed after reader was completed」。
        //
        // 所以这里把它变成一条**指着真正原因**的错误，而不是继续糊下去。
        _ = ObserveFlushAsync(flush);
        throw new InvalidOperationException(
            $"通道接收管道顶到了暂停水位 —— 这意味着未消费数据超过了接收窗口上限，" +
            $"是窗口记账的 bug。本次写入 {data.Length} 字节。请带着这条消息提 issue。");
    }

    private static async Task ObserveFlushAsync(ValueTask<FlushResult> flush)
    {
        try
        {
            _ = await flush.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 管道的读那一侧已经收尾了。写不进去不是错误，是消费者不要了。
        }
    }

    /// <summary>消费者消费掉了一些字节 —— 该给对端补窗口了。</summary>
    /// <remarks>
    /// 〔决策 velashell-docs/zh/ssh/spec/05 §3.2〕<b>窗口挂在消费上，不挂在接收上。</b>
    /// 消费者不读，窗口就不补，对端自然停下来 —— 背压是结构性的，
    /// 不需要额外的限流器，也不会出现「内部队列无限涨」。
    /// </remarks>
    private void NoteConsumed(long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        Interlocked.Add(ref _consumedPendingAdjust, bytes);
        _consumedGate.Signal();
    }

    private async Task PumpWindowAdjustAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 已消费、但还没告诉对端的字节数。
            //
            // ⚠️ 它必须**跨轮累加**。攒不够阈值就把这一轮的 consumed 丢掉的话，
            //    对端视角的窗口会一点一点缩小，最后归零 —— 症状是传了一阵子之后
            //    通道永久停住，而本地账面上看窗口明明是满的。
            //    消费者每次只读几十字节（逐行读）时最容易撞上。
            long unreported = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                Task ticket = _consumedGate.NextChange();

                unreported += Interlocked.Exchange(ref _consumedPendingAdjust, 0);

                // 攒够半个窗口再发 —— 每消费几十字节就发一个 WINDOW_ADJUST
                // 是在拿控制报文淹没链路。
                //
                // 攒着不发也不会卡住对端：本地未消费的数据接近 0 时，
                // 对端手上至少还剩半个窗口。
                if (unreported >= _receiveWindow.Size / 2)
                {
                    // 扩窗要**当场把多出来的额度授予对端**。
                    //
                    // 只改本地的 Size 而不多授一点，结果是：阈值（Size/2）涨了，
                    // 而对端手上的窗口没涨 —— 它发不出更多数据，我们也就攒不够
                    // 下一次回补的量。两边一起停住，谁都不动。
                    long extra = ResizeWindowIfNeeded();

                    long grant = unreported + extra;
                    if (grant > 0)
                    {
                        uint delta = (uint)Math.Min(grant, uint.MaxValue);
                        _receiveWindow.Add(delta);
                        await SendWindowAdjustAsync(delta, cancellationToken).ConfigureAwait(false);
                    }

                    unreported = 0;
                    continue;
                }

                await ticket.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 通道关了，正常收场。
        }
        catch (Exception)
        {
            // 会话没了。窗口回补没有补救动作可做 —— 通道本来也用不下去了。
        }
    }

    /// <summary>对端把窗口吃到见底了 —— 记下来，给伸缩用。</summary>
    private void NoteWindowPressure()
    {
        // 「见底」不要求恰好为 0：剩下不到 1/8 的时候，对端已经在拿
        // 「还能发多少」当限制了。
        if (_receiveWindow.Remaining <= (uint)(_receiveWindow.Size / 8))
        {
            _windowWasExhausted = true;
        }
    }

    /// <summary>按链路情况调整窗口。</summary>
    /// <returns>因为扩窗而<b>额外要授予对端</b>的字节数。</returns>
    /// <remarks>
    /// <para>
    /// 固定窗口的问题在于它<b>同时决定了吞吐上限</b>（<c>窗口 / RTT</c>）：
    /// 2 MiB / 200 ms ≈ 10 MB/s，跨洋链路上怎么也跑不过这个数。
    /// 所以窗口要能长到带宽时延积那么大。
    /// </para>
    /// <para>
    /// <b>判据是「窗口有没有被吃到见底」</b>，不是「两次回补隔了多久」。
    /// 后者在局域网上永远成立，会让窗口一路涨到上限白占内存 ——
    /// 而那条链路上窗口根本不是瓶颈。
    /// </para>
    /// <para>
    /// 反向也要有：连续几轮都没吃紧，说明窗口开大了，缩回去。
    /// 缩窗的做法是<b>少授一点</b> —— 已经给出去的额度收不回来，
    /// 只能靠「这一轮少给」让它慢慢回落。
    /// </para>
    /// </remarks>
    private long ResizeWindowIfNeeded()
    {
        if (!_windowPolicy.IsAdaptive)
        {
            return 0;
        }

        int current = _receiveWindow.Size;

        if (_windowWasExhausted)
        {
            _windowWasExhausted = false;
            _idleRounds = 0;

            int grown = (int)Math.Min((long)current * 2, _windowPolicy.MaximumBytes);
            if (grown <= current)
            {
                return 0;
            }

            _receiveWindow.Resize(grown);
            return grown - current;
        }

        if (++_idleRounds < 3)
        {
            return 0;
        }

        _idleRounds = 0;
        int shrunk = (int)Math.Max((long)(current * 0.75), _windowPolicy.MinimumBytes);

        if (shrunk >= current)
        {
            return 0;
        }

        _receiveWindow.Resize(shrunk);

        // 负数：这一轮少授这么多，窗口就此回落。
        return shrunk - current;
    }

    private async ValueTask SendWindowAdjustAsync(uint bytes, CancellationToken cancellationToken)
    {
        if (bytes == 0 || IsClosedOrClosing())
        {
            return;
        }

        byte[] packet = new byte[9];
        packet[0] = (byte)SshMessageNumber.ChannelWindowAdjust;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(1), RemoteId);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(5), bytes);
        await _host.SendAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    private async Task PumpStandardInputAsync(CancellationToken cancellationToken)
    {
        PipeReader reader = _stdinPipe.Reader;

        try
        {
            while (true)
            {
                ReadResult read = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = read.Buffer;

                while (!buffer.IsEmpty)
                {
                    // 单个数据段**必须**不超过对端宣告的上限 —— 超了对端会断连。
                    int chunk = (int)Math.Min(buffer.Length, RemoteMaxPacketBytes);

                    // 窗口不够就等回补。这不是错误，是背压。
                    chunk = await WaitForSendWindowAsync(chunk, cancellationToken).ConfigureAwait(false);
                    if (chunk == 0)
                    {
                        return;   // 通道关了
                    }

                    await SendDataAsync(buffer.Slice(0, chunk), cancellationToken).ConfigureAwait(false);
                    buffer = buffer.Slice(chunk);
                }

                reader.AdvanceTo(read.Buffer.End);

                if (read.IsCompleted)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 通道关了。
        }
        catch (Exception ex)
        {
            await reader.CompleteAsync(ex).ConfigureAwait(false);
        }
    }

    /// <summary>等到发送窗口能放下至少一个字节，返回这次能发多少。</summary>
    /// <returns>可发字节数；通道关闭时返回 0。</returns>
    private async ValueTask<int> WaitForSendWindowAsync(int wanted, CancellationToken cancellationToken)
    {
        while (true)
        {
            // 先取票、再查条件、最后等票 —— 顺序反过来会丢通知，症状是挂死。
            Task ticket = _sendWindowGate.NextChange();

            if (IsClosedOrClosing())
            {
                return 0;
            }

            uint available = _sendWindow.Remaining;
            if (available > 0)
            {
                int take = (int)Math.Min((uint)wanted, available);
                if (_sendWindow.TryConsume(take))
                {
                    return take;
                }
            }

            await ticket.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask SendDataAsync(ReadOnlySequence<byte> data, CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> buffer = new((int)data.Length + 16);
        SshDataWriter writer = new(buffer);
        writer.WriteMessageNumber(SshMessageNumber.ChannelData);
        writer.WriteUInt32(RemoteId);
        writer.WriteUInt32((uint)data.Length);
        foreach (ReadOnlyMemory<byte> segment in data)
        {
            writer.WriteRaw(segment.Span);
        }

        await _host.SendAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }

    private void MaybeFinishClose(SshChannelCloseReason reason)
    {
        lock (_stateLock)
        {
            if (!_closeSent || !_closeReceived)
            {
                return;
            }
        }
        FinishClose(reason);
    }

    private void FinishClose(SshChannelCloseReason reason)
    {
        lock (_stateLock)
        {
            if (_state == SshChannelState.Closed)
            {
                return;
            }
            _state = SshChannelState.Closed;
        }

        _events.Writer.TryWrite(new SshChannelEvent.Closed(reason));
        _events.Writer.TryComplete();

        // 还在等应答的请求不会再有应答了。让它们返回 false 而不是永远挂着 ——
        // 挂死没有堆栈也没有日志，只有一个再也不返回的 await。
        _pendingRequests.Close(false);

        CompleteReceivePipes();
        _stdinPipe.Writer.Complete();
        _stdinPipe.Reader.Complete();

        _sendWindowGate.Signal();
        _consumedGate.Signal();

        // ⚠️ **不能用 Cancel()。**这里常常跑在接收循环上（收到 CHANNEL_CLOSE、会话断开），
        //    而 Cancel() 会在**当前线程上同步**执行回调：泵被唤醒、结束，等着泵的
        //    DisposeAsync 恢复，再往上是使用者的 await —— 一整串同步完成的续体
        //    就在接收循环的线程上跑了起来。使用者接着同步阻塞一下（轮询、Wait），
        //    接收循环就被整个挂住，别的通道的数据躺在套接字里没人读。
        //    CancelAsync 把回调放到线程池上执行（velashell-docs/zh/ssh/spec/05 §8：接收循环上不跑使用者的代码）。
        Session.Lifecycle.CancelInBackground(_lifetime);

        _host.OnChannelClosed(LocalId, _receiveWindow.Size);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        // ⚠️ **释放有时限。**半死的链路上 CLOSE 可能永远刷不出去（发送缓冲满着、
        //    对端不读），泵里在途的那一帧也一样。等不到就不等了 —— 通道在本端照样收尾，
        //    剩下的由会话自己的判死（保活、TCP）去收场。
        using CancellationTokenSource deadline = new(DisposeTimeout);

        try
        {
            await CloseAsync(deadline.Token).AsTask().WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 释放路径不抛 —— 否则每条 await using 的错误路径都会被次要异常盖住。
        }

        FinishClose(SshChannelCloseReason.ClosedLocally);

        foreach (Task? pump in new[] { _stdinPump, _windowAdjustPump })
        {
            if (pump is null)
            {
                continue;
            }
            try
            {
                await pump.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 同上。
            }
        }

        // 泵可能还没停（卡在一次永远刷不出去的发送上）—— 这时释放令牌源会让它
        // 在之后读 Token 时抛 ObjectDisposedException，所以只有泵都停了才释放。
        if (_stdinPump?.IsCompleted != false && _windowAdjustPump?.IsCompleted != false)
        {
            _lifetime.Dispose();
        }
    }

    /// <summary>释放一条通道最多等多久（发 CLOSE、等泵收尾）。</summary>
    internal static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(5);
}
