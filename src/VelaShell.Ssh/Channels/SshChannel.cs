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

    /// <summary>
    /// 发一个报文；入队的<b>同一时刻</b>先问 <paramref name="admit"/> 还发不发，它说不发就不发。
    /// </summary>
    /// <remarks>
    /// 通道上的每一帧都走这里，<paramref name="admit"/> 查的是「CLOSE 发过没有」——
    /// 先查后入队的话，中间插进来的 CLOSE 会让这一帧排到 CLOSE 后面（RFC 4254 §5.3 不许）。
    /// <paramref name="admit"/> 在入队锁里执行，不许在里面等任何东西。
    /// </remarks>
    ValueTask SendIfAsync(ReadOnlyMemory<byte> packet, Func<bool> admit, CancellationToken cancellationToken);

    /// <summary>同 <see cref="SendIfAsync"/>，但 <paramref name="packet"/> 是<b>借来的</b>：返回之后调用方就会回收它。</summary>
    /// <remarks>
    /// 给通道数据用 —— 那是按块从池里租的缓冲。返回时这一帧要么已经写进传输（加密时已复制），
    /// 要么被重协商的闸门暂存了 —— 暂存的那一刻发送泵会自己复制一份，不再引用这块内存。
    /// </remarks>
    ValueTask SendBorrowedIfAsync(ReadOnlyMemory<byte> packet, Func<bool> admit, CancellationToken cancellationToken);

    /// <summary>通道已经彻底关了（或者永远不会再有对端的报文），可以把号收回去。</summary>
    /// <param name="localId">通道号。</param>
    /// <param name="windowBytes">这条通道<b>此刻计在会话预算上的</b>字节数（开通道时计的加上扩窗时追加的）。</param>
    void OnChannelClosed(uint localId, int windowBytes);

    /// <summary>自适应扩窗之前先向会话的窗口总预算申请；预算不够就不扩。</summary>
    bool TryReserveWindowBudget(int bytes);

    /// <summary>缩窗时把多出来的预算还回去。</summary>
    void ReleaseWindowBudget(int bytes);
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
/// 两条流要能被并发地各读各的。只有一个读接口的话，
/// 调用方轮流读两边，一边读空时另一边可能正在被对端写满 —— 那就是经典的双管道死锁。
/// </para>
/// <para>
/// ⚠️ <b>但两条流共用一个窗口</b>（RFC 4254 §5.2：扩展数据同样计入窗口）。一边不读，
/// 那边的数据堆满窗口之后<b>另一边也会停住</b>。所以要么两边都读，要么把不关心的 stderr 设成
/// <see cref="SshStderrPolicy.Discard"/>。
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

    /// <summary>通道的生命周期；也就是公开出去的 <see cref="Closed"/>。</summary>
    /// <remarks>
    /// <b>不释放它。</b>它的令牌交给了使用者，释放之后再读 <c>Token</c> 会抛；
    /// 而一个没有定时器、没有链接的令牌源本来就没有要还的资源。
    /// </remarks>
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _stdinPump;
    private Task? _windowAdjustPump;

    /// <summary>消费者已消费、但还没回补给对端的字节数。</summary>
    private long _consumedPendingAdjust;
    private readonly AsyncGate _consumedGate = new();

    /// <summary>stdin 泵已经交给会话发送的字节数（<see cref="WaitStandardInputSentAsync"/> 用）。</summary>
    private long _stdinSentBytes;

    /// <summary>stdin 泵每交出一段、或者泵退出时响一次。</summary>
    private readonly AsyncGate _stdinProgressGate = new();

    /// <summary>stdin 泵已经退出（之后不会再有进展）。</summary>
    private volatile bool _stdinPumpExited;

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

    /// <summary>交给了读的一方、还没被读走的字节数（伸缩判据用，见 <see cref="NoteWindowPressure"/>）。</summary>
    private long _unreadBytes;

    /// <summary>读的一方上一次读空了在等数据，是几次回补之前的事（见 <see cref="NoteWindowPressure"/>）。</summary>
    /// <remarks>接收循环写 0，回补泵每次回补加一。起始值很大：还没收到数据时不算「等过」。</remarks>
    private int _adjustsSinceStarved = int.MaxValue / 2;

    /// <summary>收到过数据了（第一包到来之前读的一方本来就空着，那不算「等」）。</summary>
    private bool _receivedAnyData;


    private SshChannelState _state = SshChannelState.Opening;

    /// <summary><c>CHANNEL_CLOSE</c> 已经<b>入队</b>（不是「打算发」）。之后这条通道上不许再有任何报文。</summary>
    private bool _closeSent;
    private bool _closeReceived;

    /// <summary>通道号已经还给会话。</summary>
    private bool _idReleased;
    private bool _disposed;

    /// <summary><see cref="MayStillSend"/> 的缓存委托 —— 每一帧都要用，别每次分配。</summary>
    private readonly Func<bool> _mayStillSend;

    /// <summary>这条通道此刻计在会话窗口总预算上的字节数。</summary>
    /// <remarks>
    /// 开通道时会话按初始窗口计了一笔；自适应扩窗、缩窗各自追加或退回。关闭时按这个数退 ——
    /// 曾经按「当前窗口大小」退：扩过的窗口从没计过，却按扩后的大小退，预算越退越多，
    /// 会话窗口总上限形同虚设。在 <see cref="_stateLock"/> 里读写。
    /// </remarks>
    private int _budgetCharged;

    internal SshChannel(
        ISshChannelHost host,
        uint localId,
        string channelType,
        SshChannelOptions options)
    {
        _host = host;
        LocalId = localId;
        ChannelType = channelType;
        _mayStillSend = MayStillSend;

        _windowPolicy = options.WindowPolicy;
        int window = options.WindowPolicy.InitialBytes;
        _receiveWindow = new SshWindow(window);
        _budgetCharged = window;   // 会话开通道时按它计的
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

        StandardOutput = new WindowedPipeReader(_stdoutPipe.Reader, NoteReaderConsumed);
        StandardError = _stderrPipe is null
            ? new EmptyPipeReader()
            : new WindowedPipeReader(_stderrPipe.Reader, NoteReaderConsumed);
        StandardInput = _stdinPipe.Writer;
    }

    /// <summary>我们给这条通道的编号。</summary>
    public uint LocalId { get; }

    /// <summary>对端给这条通道的编号。<c>CHANNEL_OPEN_CONFIRMATION</c> 之后才有效。</summary>
    public uint RemoteId { get; private set; }

    /// <summary>通道类型（<c>"session"</c> / <c>"direct-tcpip"</c> / …）。</summary>
    public string ChannelType { get; }

    /// <summary>通道<b>整个</b>结束（状态进入 <see cref="SshChannelState.Closed"/>）时被取消。</summary>
    /// <remarks>
    /// 回调在线程池上执行，不在接收循环上。
    /// 与 <see cref="SshChannelState.RemoteEof"/> 不同：EOF 只是对端不再发，往它写仍然有意义；
    /// 到了这里，两个方向都没有了。
    /// </remarks>
    public CancellationToken Closed => _lifetime.Token;

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
    /// <remarks>
    /// <para>
    /// <b>读完（<c>IsCompleted</c> 且没抛）只说明对端自己收了尾</b>：发了 <c>CHANNEL_EOF</c>
    /// 或 <c>CHANNEL_CLOSE</c>。连接中途断了，读会抛出连接的故障（<see cref="Diagnostics.SshException"/>）；
    /// 本端释放了连接，读会抛 <see cref="ObjectDisposedException"/>。
    /// </para>
    /// <para>
    /// 这样区分是因为「断线」与「对端说完了」在读的一方眼里必须不同：当成读完的话，
    /// 下载到一半的文件、跑到一半的命令输出都会被当成完整结果交出去，
    /// 终端也分不清是用户敲了 <c>exit</c> 还是链路断了（后者才该自动重连）。
    /// 断线之前已经收到、还没读走的那部分随之作废 —— 反正结果已经不完整了。
    /// </para>
    /// </remarks>
    public PipeReader StandardOutput { get; }

    /// <summary>
    /// 远端的标准错误。
    /// </summary>
    /// <remarks>
    /// <see cref="SshStderrPolicy.Discard"/> 时这是一条**立刻结束的空流** ——
    /// 不是一条永远不返回的流，那会让调用方挂死。
    /// 连接中途断了的时候读会抛，与 <see cref="StandardOutput"/> 一样。
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
    public async ValueTask<SshChannelEvent> ReadEventAsync(CancellationToken cancellationToken = default)
    {
        SshChannelEvent channelEvent = await _events.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        NoteEventRead(channelEvent);
        return channelEvent;
    }

    /// <summary>读走一条对端的未知请求，积压计数减一（见 <see cref="OnPeerRequest"/>）。</summary>
    private void NoteEventRead(SshChannelEvent? channelEvent)
    {
        if (channelEvent is SshChannelEvent.PeerRequest)
        {
            Interlocked.Decrement(ref _queuedPeerRequests);
        }
    }

    /// <summary>把这条通道当成一条双向字节流（读 stdout、写 stdin）。</summary>
    /// <param name="ownsChannel">释放流时是否一并关掉通道。</param>
    /// <remarks>
    /// <c>direct-tcpip</c> / <c>direct-streamlocal</c> 这类隧道通道最常这么用 ——
    /// 交给只认 <see cref="Stream"/> 的 API（<c>SslStream</c>、HTTP 客户端的连接回调、拨号器）。
    /// </remarks>
    public SshChannelStream AsStream(bool ownsChannel = true) => new(this, ownsChannel);

    /// <summary>还有没有事件可读（不阻塞）。</summary>
    public bool TryReadEvent(out SshChannelEvent? channelEvent)
    {
        bool read = _events.Reader.TryRead(out channelEvent);
        NoteEventRead(channelEvent);
        return read;
    }

    // ------------------------------------------------------------ 发送

    /// <summary>发一个通道请求。</summary>
    /// <param name="requestType">请求类型。</param>
    /// <param name="payload">类型相关的数据。</param>
    /// <param name="wantReply">要不要等应答。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 对端是否接受；<paramref name="wantReply"/> 为假时，发出去了就是 <see langword="true"/>。
    /// 通道已经关了（<c>CHANNEL_CLOSE</c> 已发）时请求不会上线，返回 <see langword="false"/>。
    /// </returns>
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

        // ⚠️ 关了之后不许再发（RFC 4254 §5.3）。对端双向 CLOSE 走完之后就可以复用它的通道号，
        //    一个迟到的 window-change / signal 会落到**另一个会话**上；要应答的请求还会把
        //    那条新通道的应答队列搅乱。
        if (!wantReply)
        {
            bool sent = false;
            await _host.SendIfAsync(buffer.WrittenMemory, () => sent = MayStillSend(), cancellationToken)
                .ConfigureAwait(false);
            return sent;
        }

        // 登记与入队是**同一个动作**：应答靠 FIFO 对齐。
        // 先发后登记，一个快到的应答会发现账本是空的；先登记后发（中间隔着背压等待），
        // 并发的两个请求可能登记顺序与上线顺序相反，而那会把应答安到对方头上。
        Task<bool>? reply = null;
        await _host.SendIfAsync(
            buffer.WrittenMemory,
            () =>
            {
                if (!MayStillSend())
                {
                    return false;
                }
                reply = _pendingRequests.Register();
                return true;
            },
            cancellationToken).ConfigureAwait(false);

        return reply is not null && await reply.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>这条通道上还能不能发报文：<c>CHANNEL_CLOSE</c> 入队之后就不能了。</summary>
    /// <remarks>在会话的入队锁里被调用（见 <see cref="ISshChannelHost.SendIfAsync"/>）。</remarks>
    private bool MayStillSend()
    {
        lock (_stateLock)
        {
            return !_closeSent;
        }
    }

    /// <summary>CLOSE 是否已经入队。接收循环回复对端请求之前要看它。</summary>
    internal bool CloseSent
    {
        get
        {
            lock (_stateLock)
            {
                return _closeSent;
            }
        }
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

        // 泵收尾期间对端可能已经 CLOSE 了 —— 那时 EOF 不能再发，入队时一并判定。
        await _host.SendIfAsync(SimplePacket(SshMessageNumber.ChannelEof), _mayStillSend, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>等 stdin 泵把累计 <paramref name="totalBytes"/> 字节都交给会话发送。</summary>
    /// <exception cref="IOException">通道先关了，还有字节没发出去。</exception>
    /// <remarks>
    /// 给 <see cref="SshChannelStream.FlushAsync(CancellationToken)"/> 用：写入 stdin 只是进了本地管道，
    /// 窗口不够时它们会在管道里一直等 —— 「冲刷」要等的正是这一段。
    /// </remarks>
    internal async ValueTask WaitStandardInputSentAsync(long totalBytes, CancellationToken cancellationToken)
    {
        while (true)
        {
            // 先取票、再查条件、最后等票（见 AsyncGate）。
            Task ticket = _stdinProgressGate.NextChange();

            long sent = Interlocked.Read(ref _stdinSentBytes);
            if (sent >= totalBytes)
            {
                return;
            }

            // 看标记而不是看泵的 Task：泵在 finally 里响铃时，它的 Task 还没完成。
            if (_stdinPump is null || _stdinPumpExited)
            {
                throw new IOException(
                    $"通道 {LocalId} 已经关闭，还有 {totalBytes - sent} 字节没有发出去。");
            }

            await ticket.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>关闭通道：发出 <c>CHANNEL_CLOSE</c>，不等对端的那一个。</summary>
    /// <remarks>
    /// <para>
    /// <b><c>CLOSE</c> 必须双向</b>，只有双方都发过之后通道号才可以回收 —— 对端的那一个到了，
    /// 通道号才还给会话（见 <see cref="ReleaseId"/>）。
    /// </para>
    /// <para>
    /// 取消只在 CLOSE 入队之前生效；被取消的话它还没发，之后再调用一次（或者释放通道）会重新发。
    /// </para>
    /// </remarks>
    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (_closeSent)
            {
                return;
            }

            // 先把状态改掉：泵看到它就不再取新的数据。
            if (_state != SshChannelState.Closed)
            {
                _state = SshChannelState.Closing;
            }
        }

        try
        {
            // 「已发」这个标记在入队锁里、与入队同一时刻设上（TryCommitClose）。
            // 曾经是先设标记、后发 —— 发送被取消（释放通道有 5 秒时限）的话，标记已经是真的，
            // CLOSE 却永远不会再发：服务端那条通道一直开着，远端进程也一直跑着。
            await _host.SendIfAsync(SimplePacket(SshMessageNumber.ChannelClose), TryCommitClose, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // 会话已经没了。那样的话通道当然也关了，不必再报一次；号也不必再等对端。
            FinishClose(SshChannelCloseReason.SessionClosed, ex);
            ReleaseId(force: true);
            return;
        }

        MaybeFinishClose(SshChannelCloseReason.ClosedLocally);
    }

    /// <summary>在入队锁里把「CLOSE 已发」设上；已经发过就不再发。</summary>
    private bool TryCommitClose()
    {
        lock (_stateLock)
        {
            if (_closeSent)
            {
                return false;
            }
            _closeSent = true;
            return true;
        }
    }

    private byte[] SimplePacket(SshMessageNumber number)
    {
        byte[] packet = new byte[5];
        packet[0] = (byte)number;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(1), RemoteId);
        return packet;
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

    internal void OnOpenFailed()
    {
        FinishClose(SshChannelCloseReason.ClosedByPeer);

        // 对端从没建起这条通道，不会再有发往这个号的报文。
        ReleaseId(force: true);
    }

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
        else
        {
            Interlocked.Add(ref _unreadBytes, length);
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
        else
        {
            Interlocked.Add(ref _unreadBytes, length);
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
    /// <param name="failure">
    /// 不是对端收的尾（连接断了、被释放了）时的原因：读的一方拿到它，而不是一个像 EOF 的「读完」。
    /// 已经因为对端的 EOF 收过尾的管道不受影响 —— 第二次完成是空操作。
    /// </param>
    private void CompleteReceivePipes(Exception? failure = null)
    {
        _stdoutPipe.Writer.Complete(failure);
        _stderrPipe?.Writer.Complete(failure);
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
    internal void OnCloseCompleted(SshChannelCloseReason reason)
    {
        // 本端先收尾过（释放了通道）的话，状态早就是 Closed，FinishClose 什么都不做 ——
        // 但号一直扣着在等的正是这一个 CLOSE。
        FinishClose(reason);
        ReleaseId(force: false);
    }

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

        // 不认识的请求攒成事件等使用者去读。**有上限**：没人读事件流的话（大多数使用者只读 stdout），
        // 对端每发一条就白占一份内存（载荷最长 256 KiB）—— 它绕过了窗口流控，
        // 是一条不花对端任何代价的内存放大。超出上限的直接丢掉，照样回 FAILURE（调用方会回）。
        // 自己计数：单读者的无界 Channel 不支持 Count（CanCount 为假）。
        if (Volatile.Read(ref _queuedPeerRequests) >= MaxQueuedEvents)
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

    /// <summary>事件流里最多积压多少条没读的对端未知请求，超过就不再收。</summary>
    /// <remarks>退出状态、EOF、关闭这几件不受它限制 —— 它们每条通道只有一次。</remarks>
    internal const int MaxQueuedEvents = 64;

    /// <summary>事件流里还没被读走的 <see cref="SshChannelEvent.PeerRequest"/> 条数。</summary>
    private int _queuedPeerRequests;

    /// <summary>会话没了。</summary>
    /// <param name="reason">连接的故障；本端释放连接时是 <see cref="ObjectDisposedException"/>。</param>
    internal void OnSessionClosed(Exception reason)
    {
        FinishClose(SshChannelCloseReason.SessionClosed, reason);
        ReleaseId(force: true);
    }

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
    /// <remarks>
    /// <para>
    /// 「见底」不要求恰好为 0：剩下不到 1/8 的时候，对端已经在拿「还能发多少」当限制了。
    /// </para>
    /// <para>
    /// ⚠️ <b>但要分清是谁让它见底的。</b>读的一方慢，数据堆在管道里没人读，回补就不发，窗口一样见底 ——
    /// 那时窗口不是瓶颈，扩窗换不来吞吐，只是让这条通道多缓着几十 MiB 没读的数据。
    /// 曾经不分：消费者一慢，窗口就一路翻到上限。
    /// </para>
    /// <para>
    /// 分得清的信号是「读的一方有没有读空了在等」：窗口不够（BDP 大于窗口）时，读得快的一方把数据读空，
    /// 要空等一个往返下一轮才到；读得慢时管道里总有没读完的，永远读不空。
    /// 所以额度见了底、而且最近读空过，才算窗口太小（在 <see cref="ResizeWindowIfNeeded"/> 里合起来判）。
    /// 通道的第一包不算读空 —— 那时本来就空着。
    /// 按比例划一条「未读少于几分之几」的线不行：那条线会被时序偶然跨过，窗口就时涨时落。
    /// </para>
    /// </remarks>
    private void NoteWindowPressure()
    {
        if (_receivedAnyData && Interlocked.Read(ref _unreadBytes) <= 0)
        {
            Volatile.Write(ref _adjustsSinceStarved, 0);
        }
        _receivedAnyData = true;

        if (_receiveWindow.Remaining <= (uint)(_receiveWindow.Size / 8))
        {
            _windowWasExhausted = true;
        }
    }

    /// <summary>读的一方读走了这么多（经 <see cref="WindowedPipeReader"/>）。</summary>
    private void NoteReaderConsumed(long bytes)
    {
        Interlocked.Add(ref _unreadBytes, -bytes);
        NoteConsumed(bytes);
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

        // 「最近读空过」看的是这一轮与上一轮：回补常常发生在一轮数据还没收完的时候，
        // 读空（在一轮的开头）与见底（在一轮的末尾）就被那次回补隔在了两边。
        int sinceStarved = Volatile.Read(ref _adjustsSinceStarved);
        Interlocked.CompareExchange(ref _adjustsSinceStarved, Math.Min(sinceStarved + 1, int.MaxValue / 2), sinceStarved);
        bool starvedLately = sinceStarved <= 1;

        if (_windowWasExhausted)
        {
            _windowWasExhausted = false;
            _idleRounds = 0;

            if (!starvedLately)
            {
                return 0;   // 见底是读的一方跟不上造成的：扩窗只会多缓一堆没读的数据
            }


            int grown = (int)Math.Min((long)current * 2, _windowPolicy.MaximumBytes);
            if (grown <= current)
            {
                return 0;
            }

            // 扩出来的每一个字节都是对端可以塞进来、我们得缓着的内存 —— 先向会话的窗口总预算申请。
            // 曾经扩窗从不计预算：开通道时只计初始窗口，之后每条通道都能长到上限（默认 64 MiB），
            // 256 MiB 的会话总上限就只管得住「开通道那一刻」。
            if (!TryChargeBudget(grown - current))
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
        RefundBudget(current - shrunk);

        // 负数：这一轮少授这么多，窗口就此回落。
        return shrunk - current;
    }

    /// <summary>向会话追加预算；号已经还回去（通道收尾了）时不再追加。</summary>
    private bool TryChargeBudget(int bytes)
    {
        if (!_host.TryReserveWindowBudget(bytes))
        {
            return false;
        }

        lock (_stateLock)
        {
            if (!_idReleased)
            {
                _budgetCharged += bytes;
                return true;
            }
        }

        // 申请与收尾撞在一起：号已经按旧数退过了，这一笔当场还回去。
        _host.ReleaseWindowBudget(bytes);
        return false;
    }

    /// <summary>缩窗后把多出来的预算还给会话。</summary>
    private void RefundBudget(int bytes)
    {
        lock (_stateLock)
        {
            if (_idReleased)
            {
                return;   // 收尾时已经整笔退过了
            }
            _budgetCharged -= bytes;
        }

        _host.ReleaseWindowBudget(bytes);
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
        await _host.SendIfAsync(packet, _mayStillSend, cancellationToken).ConfigureAwait(false);
    }

    private async Task PumpStandardInputAsync(CancellationToken cancellationToken)
    {
        PipeReader reader = _stdinPipe.Reader;
        Exception? failure = null;

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

                    Interlocked.Add(ref _stdinSentBytes, chunk);
                    _stdinProgressGate.Signal();
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
            failure = ex;
        }
        finally
        {
            // reader 归泵所有，由泵自己收尾 —— 见 FinishClose 里的说明。
            //
            // ⚠️ **每一条出口都要走到这里**，包括循环中间那个「通道关了」的 return：
            //    那时泵手上还捏着一段没 AdvanceTo 的数据，而写入方的 FlushAsync 正卡在背压上。
            //    FinishClose 完成的是 writer —— **那放不出挂起的 FlushAsync**，只有 reader
            //    前进或完成才行。曾经这条 return 跳过了收尾，写入方就永远等下去。
            await reader.CompleteAsync(failure).ConfigureAwait(false);

            // 等着「发完」的人不会再等到进展了 —— 叫醒他们去看结局。
            _stdinPumpExited = true;
            _stdinProgressGate.Signal();
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
        // 缓冲从池里租：这是上传路径上每一块都要走的一步，曾经每块新分配一个数组（最大一个 max packet），
        // 高速上传时就是每秒几千次分配。发送方的 await 返回时发送泵已经不再引用它
        // （见 ISshChannelHost.SendBorrowedIfAsync），所以 finally 里就可以还回去。
        int length = (int)data.Length;
        byte[] rented = ArrayPool<byte>.Shared.Rent(9 + length);
        try
        {
            rented[0] = (byte)SshMessageNumber.ChannelData;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(rented.AsSpan(1), RemoteId);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(rented.AsSpan(5), (uint)length);
            data.CopyTo(rented.AsSpan(9));

            await _host.SendBorrowedIfAsync(rented.AsMemory(0, 9 + length), _mayStillSend, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
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
        ReleaseId(force: false);
    }

    /// <summary>把通道号还给会话。</summary>
    /// <param name="force">
    /// 不等双向 CLOSE：会话没了、或者对端从没建起这条通道 —— 都不会再有发往这个号的报文。
    /// </param>
    /// <remarks>
    /// ⚠️ <b>双向 CLOSE 走完之前不能还</b>（RFC 4254 §5.3，velashell-docs/zh/ssh/spec/05 §1 第 2 条）。
    /// 曾经释放通道时本端一收尾就还号：对端那条通道还开着，它发来的 DATA / exit-status / CLOSE
    /// 过了回收延迟就落到复用了这个号的新通道上；要回应答的请求还会被我们用一个不相干的号回 FAILURE。
    /// 号一直扣着的这段时间里，通道已经在本端收尾，迟到的数据照常计窗口、丢弃，不会回任何报文。
    /// </remarks>
    private void ReleaseId(bool force)
    {
        int charged;
        lock (_stateLock)
        {
            if (_idReleased || (!force && !(_closeSent && _closeReceived)))
            {
                return;
            }
            _idReleased = true;
            charged = _budgetCharged;
        }

        // 退的是**计过的**数，不是此刻的窗口大小（见 _budgetCharged）。
        _host.OnChannelClosed(LocalId, charged);
    }

    private void FinishClose(SshChannelCloseReason reason, Exception? failure = null)
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

        CompleteReceivePipes(failure);
        _stdinPipe.Writer.Complete();

        // stdin 的 reader 在泵起来之后**归泵所有**，这里不能替它完成：
        // 泵可能正读到一半，或者刚 AdvanceTo 完要回头再读 —— 从外面完成 reader
        // 会让它撞上「reader 完成后不许再读」。完成 writer、取消 _lifetime 之后，
        // 泵的每条路径都会退出，并在退出时自己完成 reader。
        // 泵从没起来（通道没开成）时才由这里完成。
        if (_stdinPump is null)
        {
            _stdinPipe.Reader.Complete();
        }

        _sendWindowGate.Signal();
        _consumedGate.Signal();

        // ⚠️ **不能用 Cancel()。**这里常常跑在接收循环上（收到 CHANNEL_CLOSE、会话断开），
        //    而 Cancel() 会在**当前线程上同步**执行回调：泵被唤醒、结束，等着泵的
        //    DisposeAsync 恢复，再往上是使用者的 await —— 一整串同步完成的续体
        //    就在接收循环的线程上跑了起来。使用者接着同步阻塞一下（轮询、Wait），
        //    接收循环就被整个挂住，别的通道的数据躺在套接字里没人读。
        //    CancelAsync 把回调放到线程池上执行（velashell-docs/zh/ssh/spec/05 §8：接收循环上不跑使用者的代码）。
        Session.Lifecycle.CancelInBackground(_lifetime);

        // 号不在这里还 —— 见 ReleaseId。
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

        // CLOSE 本身**不带时限**地发：等不及的只是释放这一步，CLOSE 留在后台，
        // 背压一松就上线（会话先没了的话它自己收场，不抛）。带着时限发的话，超时就等于
        // 这条通道在服务端永远开着 —— 远端进程接着跑，MaxSessions 名额也一直占着。
        Task closing = CloseAsync(CancellationToken.None).AsTask();
        try
        {
            await closing.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 释放路径不抛 —— 否则每条 await using 的错误路径都会被次要异常盖住。
        }

        // 本端收尾；号要等对端的 CLOSE（见 ReleaseId）。
        FinishClose(SshChannelCloseReason.ClosedLocally);
        ReleaseId(force: false);

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
    }

    /// <summary>释放一条通道最多等多久（发 CLOSE、等泵收尾）。</summary>
    internal static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(5);
}
