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
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Channels;

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
/// <see cref="SshStderrMode.Discard"/>。
/// </para>
/// <para>
/// 实现上通道本身只管生命周期（状态机、双向 CLOSE、还号、收尾的顺序）与请求账本；
/// 接收窗口（<see cref="ChannelReceiveWindow"/>）、数据投递（<see cref="ChannelDataReceiver"/>）、
/// 发送窗口与 stdin 泵（<see cref="StandardInputPump"/>）、事件流（<see cref="ChannelEventQueue"/>）是它的协作者。
/// </para>
/// </remarks>
public sealed class SshChannel : IAsyncDisposable
{
    private readonly ISshChannelHost _host;
    private readonly Lock _stateLock = new();

    private readonly ChannelReceiveWindow _receiveWindow;
    private readonly ChannelDataReceiver _data;
    private readonly StandardInputPump _input;
    private readonly ChannelEventQueue _events = new();

    private readonly TaskCompletionSource _wireClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>等待双向 CLOSE（或连接终止）以及 stdin 发送泵收尾，供敏感诊断租约确定安全释放边界。</summary>
    /// <remarks>velashell-docs/zh/ssh/spec/08-failures.md §9.1；本端 DisposeAsync 不等待对端 CLOSE。</remarks>
    public async ValueTask WaitForSensitivePayloadDrainAsync(CancellationToken cancellationToken = default)
    {
        await _wireClosed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        Task? input;
        Task? window;
        lock (_stateLock)
        {
            input = _stdinPump;
            window = _windowAdjustPump;
        }
        if (input is not null)
        {
            await input.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        if (window is not null)
        {
            await window.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>本端发出、等应答的通道请求（应答没有 id，靠 FIFO 顺序对齐）。</summary>
    private readonly ChannelRequestLedger _requests;

    /// <summary>通道的生命周期；也就是公开出去的 <see cref="Closed"/>。</summary>
    /// <remarks>
    /// <b>不释放它。</b>它的令牌交给了使用者，释放之后再读 <c>Token</c> 会抛；
    /// 而一个没有定时器、没有链接的令牌源本来就没有要还的资源。
    /// </remarks>
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _stdinPump;
    private Task? _windowAdjustPump;

    /// <summary>回补泵被叫醒过几次（测试用：离阈值还远时不该叫醒它）。</summary>
    internal int WindowAdjustPumpWakeups => _receiveWindow.PumpWakeups;

    private SshChannelState _state = SshChannelState.Opening;

    /// <summary><c>CHANNEL_CLOSE</c> 已经<b>入队</b>（不是「打算发」）。之后这条通道上不许再有任何报文。</summary>
    private bool _closeSent;
    private bool _closeReceived;

    /// <summary>对端的 <c>CHANNEL_CLOSE</c> 到的时候我们还没发 —— 是对端先关的。</summary>
    private bool _peerClosedFirst;

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

        ReceiveMaxPacketBytes = options.ReceiveMaxPacketBytes;
        IsInteractive = options.IsInteractive;
        _receiveWindow = new ChannelReceiveWindow(this, options.WindowPolicy);
        _budgetCharged = options.WindowPolicy.InitialBytes;   // 会话开通道时按它计的
        _input = new StandardInputPump(this);
        _requests = new ChannelRequestLedger(this);

        // 接收管道的水位、stderr 的去留见 ChannelDataReceiver；它在 _stateLock 里看状态、写管道。
        _data = new ChannelDataReceiver(this, _receiveWindow, options, _stateLock);
        StandardOutput = _data.StandardOutput;
        StandardError = _data.StandardError;
        StandardInput = new StandardInputWriter(_input.Writer, AcceptsInput);
    }

    /// <summary>往会话那边发包的出口（协作者发帧用）。</summary>
    internal ISshChannelHost Host => _host;

    /// <summary>我们给这条通道的编号。</summary>
    public uint LocalId { get; }

    /// <summary>对端给这条通道的编号。<c>CHANNEL_OPEN_CONFIRMATION</c> 之后才有效。</summary>
    public uint RemoteId { get; private set; }

    /// <summary>通道类型（<c>"session"</c> / <c>"direct-tcpip"</c> / …）。</summary>
    public string ChannelType { get; }

    /// <summary>对端确认（或者我们确认对端开过来）的时刻；还在打开、或者没开成时为 <see langword="null"/>。</summary>
    /// <remarks>连接信息里列「这条隧道开了多久」用它；曾经没有，每通道只有字节数。</remarks>
    public DateTimeOffset? OpenedAt => Volatile.Read(ref _openedAtTicks) is var ticks and > 0
        ? new DateTimeOffset(ticks, TimeSpan.Zero)
        : null;

    private long _openedAtTicks;

    /// <summary>通道<b>整个</b>结束（状态进入 <see cref="SshChannelState.Closed"/>）时被取消。</summary>
    /// <remarks>
    /// 回调在线程池上执行，不在接收循环上。
    /// 与 <see cref="SshChannelState.RemoteEof"/> 不同：EOF 只是对端不再发，往它写仍然有意义；
    /// 到了这里，两个方向都没有了。
    /// </remarks>
    public CancellationToken Closed => _lifetime.Token;

    /// <summary>通道为什么关闭；还没关时是 <see cref="SshChannelCloseReason.Unknown"/>。</summary>
    /// <remarks>
    /// <para>
    /// 在 stdout / stderr 读到头之前就已经记下：读端看到结尾时来看它，就分得出「对端关的（远端进程退了）」「会话没了」「本端关的」。
    /// 只收到 EOF、还没收到 CLOSE 时读端也会读到头，那时它还是 <see cref="SshChannelCloseReason.Unknown"/>。
    /// </para>
    /// <para>
    /// 〔velashell-docs/zh/ssh/spec/05 §一〕事件流里也有（<see cref="SshChannelEvent.Closed"/>），但事件流是单读者的；这里谁来问、问几次都一样。
    /// 曾经只在事件流里，宿主只好从读管道的结束方式反推「远端退出 / 断线 / 本端拆除」。
    /// </para>
    /// </remarks>
    public SshChannelCloseReason CloseReason => _events.CloseReason;

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

    /// <summary>一个 <c>CHANNEL_DATA</c> 的数据段最多多大：本端报文长度上限减去报文头、通道头与最大填充。</summary>
    private const int MaxSendDataBytes = Crypto.SshPacketFormat.DefaultMaxPacketLength - 512;

    /// <summary>我们宣告的单个数据段上限（<c>maximum packet size</c>）：对端发来的每个 DATA / EXTENDED_DATA 都不许超过它。</summary>
    internal int ReceiveMaxPacketBytes { get; }

    /// <summary>这条通道发的报文走交互道（<see cref="SshChannelOptions.IsInteractive"/>）；开通道时定下，之后不变 —— 中途换道会让它自己的报文乱序。</summary>
    internal bool IsInteractive { get; }

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
    /// <see cref="SshStderrMode.Discard"/> 时这是一条**立刻结束的空流** ——
    /// 不是一条永远不返回的流，那会让调用方挂死。
    /// 连接中途断了的时候读会抛，与 <see cref="StandardOutput"/> 一样。
    /// </remarks>
    public PipeReader StandardError { get; }

    /// <summary>写进去的内容变成 <c>CHANNEL_DATA</c>。</summary>
    /// <remarks>
    /// <para>
    /// 完成这个 writer（<c>Complete</c> / <c>CompleteAsync</c>）等同于 <see cref="SendEofAsync"/>：
    /// 已写入的内容冲干净之后发 <c>CHANNEL_EOF</c>。要等 EOF 真正入队再往下走，用 <see cref="SendEofAsync"/>。
    /// </para>
    /// <para>
    /// 通道关了之后（本端开始关闭、任一方向的 CLOSE 已经入队、或者连接断了），<c>FlushAsync</c> / <c>WriteAsync</c>
    /// <b>当场</b>返回 <see cref="FlushResult.IsCompleted"/> 为真（<see cref="PipeWriter"/> 表达「读的一方不要了」的惯用法），
    /// 写进来的不再发出，不抛异常。这个 writer 归调用方：库不替它完成。
    /// </para>
    /// </remarks>
    public PipeWriter StandardInput { get; }

    /// <summary>经这条通道发出的应用数据字节数（<c>CHANNEL_DATA</c> 的数据段，不含协议开销）。</summary>
    /// <remarks>传输面板、隧道统计里每条通道的数字。线上的总流量看 <c>SshConnection.BytesSent</c>。</remarks>
    public long BytesSent => _input.BytesSent;

    /// <summary>经这条通道收到的应用数据字节数（<c>CHANNEL_DATA</c> 与 <c>CHANNEL_EXTENDED_DATA</c> 的数据段，读没读走都算）。</summary>
    public long BytesReceived => _data.BytesReceived;

    /// <summary>开着的通道数记没记上 +1（收尾时据此 -1）；在 <see cref="_stateLock"/> 里读写。</summary>
    private bool _activeMetered;

    /// <summary>度量的 <c>host</c> 标签（来自会话）；<see langword="null"/> 时不记。</summary>
    internal string? MetricsHost => _host.MetricsHost;

    /// <summary>当前的接收窗口剩余（诊断用）。</summary>
    public uint ReceiveWindowRemaining => _receiveWindow.Remaining;

    /// <summary>
    /// 接收窗口当前的**额定大小**。自适应策略下它会随链路情况变。
    /// </summary>
    public int ReceiveWindowSize => _receiveWindow.Size;

    /// <summary>当前的发送窗口剩余（诊断用）。</summary>
    public uint SendWindowRemaining => _input.SendWindowRemaining;

    // ------------------------------------------------------------ 事件

    /// <summary>读下一件事。</summary>
    /// <remarks>
    /// 事件流以一条 <see cref="SshChannelEvent.Closed"/> 结尾。通道关了之后再读，
    /// <b>永远得到那同一条 <see cref="SshChannelEvent.Closed"/></b> —— 不抛异常，
    /// 等退出状态的几个调用方（<c>WaitAsync</c> 被调了两次之类）不必各自去接一个 BCL 的异常类型。
    /// </remarks>
    public ValueTask<SshChannelEvent> ReadEventAsync(CancellationToken cancellationToken = default) =>
        _events.ReadAsync(cancellationToken);

    /// <summary>把这条通道当成一条双向字节流（读 stdout、写 stdin）。</summary>
    /// <param name="ownsChannel">释放流时是否一并关掉通道。</param>
    /// <remarks>
    /// <c>direct-tcpip</c> / <c>direct-streamlocal</c> 这类隧道通道最常这么用 ——
    /// 交给只认 <see cref="Stream"/> 的 API（<c>SslStream</c>、HTTP 客户端的连接回调、拨号器）。
    /// </remarks>
    public SshChannelStream AsStream(bool ownsChannel = true) => new(this, ownsChannel);

    /// <summary>等通道关闭，交回退出状态（<see cref="SshCommand.WaitAsync"/> 与 <see cref="SshShell.WaitAsync"/> 共用）。</summary>
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/05 §5.4〕退出状态缓存在通道上（<see cref="ChannelEventQueue"/>），不只活在事件流里 ——
    /// 事件流是单读者的，读过一次就没了：曾经 <c>ReadToEndAsync</c> 之后再 <c>WaitAsync</c>、或者调用方自己读过事件，
    /// 再问就是 null。现在调多少次、谁先读过事件，答案都一样。
    /// <para>
    /// 等的是关闭信号，<b>不读事件流</b>：事件流归调用方（<see cref="ReadEventAsync"/>）。曾经这里循环读事件直到 <c>Closed</c>，
    /// 与调用方自己的读者抢同一条单读者流 —— 并发时谁拿到退出状态说不准，先等完再读的话事件已经被它读光了。
    /// </para>
    /// </remarks>
    internal ValueTask<SshExitStatus> WaitForExitAsync(CancellationToken cancellationToken) =>
        _events.WaitForExitAsync(cancellationToken);

    /// <summary>给远端进程发信号（<see cref="SshCommand.SendSignalAsync"/> 与 <see cref="SshShell.SendSignalAsync"/> 共用）。</summary>
    internal ValueTask SendSignalAsync(string signalName, CancellationToken cancellationToken) =>
        _requests.SendSignalAsync(signalName, cancellationToken);

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

        // 关了之后不发、要应答的登记与入队同一时刻做 —— 见 ChannelRequestLedger.SendAsync。
        return await _requests.SendAsync(requestType, payload, wantReply, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>这条通道上还能不能发报文：<c>CHANNEL_CLOSE</c> 入队之后就不能了。</summary>
    /// <remarks>在会话的入队锁里被调用（见 <see cref="ISshChannelHost.SendIfAsync"/>）。</remarks>
    internal bool MayStillSend()
    {
        lock (_stateLock)
        {
            return !_closeSent;
        }
    }

    /// <summary>
    /// stdin 还收不收：本端开始关闭、任一方向的 CLOSE 已经入队、或者通道已经收尾（含连接断了）之后就不收了。
    /// </summary>
    /// <remarks>
    /// 交给调用方的 <see cref="StandardInput"/> 写之前问它（见 <see cref="StandardInputWriter"/>）。
    /// 只看 <c>_closeSent</c> 不够：连接断了时通道直接收尾，一个 CLOSE 也没发。
    /// </remarks>
    private bool AcceptsInput()
    {
        lock (_stateLock)
        {
            return !_closeSent && _state is not (SshChannelState.Closing or SshChannelState.Closed);
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

    /// <summary>
    /// 这边不再要对端的标准输出了：本端把 <see cref="StandardOutput"/> 收尾、之后到的数据丢弃，并告诉对端（<c>eow@openssh.com</c>）。
    /// </summary>
    /// <returns>
    /// 告诉对端的请求有没有发出去：对端不是 OpenSSH、或者通道已经在关时为 <see langword="false"/> —— 那时只在本端丢弃，
    /// 远端进程照常跑完。
    /// </returns>
    /// <remarks>
    /// <para>
    /// 〔velashell-docs/zh/ssh/spec/05 §5.2〕场景是只要输出的前几行（<c>head</c>、<c>grep -m 1</c> 那样）：读够了就不读了。
    /// OpenSSH 的服务端收到之后关掉远端进程的输出端，进程再写就收到 <c>SIGPIPE</c> 提前结束，不再白跑完、白占带宽；
    /// 通道仍然开着，退出状态照常回来，标准输入也还能写。与 <c>CHANNEL_EOF</c> 方向相反、同样不是关闭通道。
    /// </para>
    /// <para>
    /// <b>只发给 OpenSSH</b>（按服务端的版本标识串）：这是 OpenSSH 的扩展，有的实现收到不认识的通道请求会直接断开连接。
    /// 不要应答、不占窗口。本端丢弃的数据照常回补窗口，对端不会因此停住。标准错误不受影响。
    /// </para>
    /// </remarks>
    public async ValueTask<bool> StopStandardOutputAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 读端完成之后，到达的数据在 ChannelDataReceiver.TryDeliver 里就被判为「消费者不读了」：丢弃并立刻回补窗口。
        await StandardOutput.CompleteAsync().ConfigureAwait(false);

        return _host.PeerAcceptsEndOfWrite
            && await SendRequestAsync(SshProtocolNames.RequestEndOfWrite, default, wantReply: false, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>发 <c>CHANNEL_EOF</c>：我们不再发数据了。</summary>
    /// <remarks>
    /// <b>这不是关闭通道。</b>发完之后仍然可以继续收对端的数据。
    /// </remarks>
    public async ValueTask SendEofAsync(CancellationToken cancellationToken = default)
    {
        if (!TryMarkLocalEof())
        {
            return;
        }

        // 先把 stdin 里还没发出去的内容冲干净，再发 EOF ——
        // 反过来会让最后一段数据排在 EOF 之后，对端多半已经不读了。
        // 〔velashell-docs/zh/ssh/spec/05 §4.3〕EOF 由 stdin 泵在冲干净之后发（用通道自己的生命周期，不用这里的令牌）：
        // 这里被取消只是不再等，EOF 照样会发出去。曾经由这里发：等泵的时候被取消，EOF 就永远不会再发，
        // 远端的 cat / sort 一直等输入。
        await _input.CompleteWriterAsync().ConfigureAwait(false);
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

        // 泵正常收尾时已经发了（_eofOwed 已是 0）。泵从没起来（通道还没确认）、或者是出错退出的：在这里补发。
        // 对端可能已经 CLOSE 了 —— 那时不发，入队时一并判定。
        await SendOwedEofAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>本端已经推进到 EOF、而 <c>CHANNEL_EOF</c> 还没发：1；否则 0。谁把它换成 0 谁发（只发一次）。</summary>
    private int _eofOwed;

    /// <summary>欠着的 <c>CHANNEL_EOF</c> 由这里发：谁先把 <see cref="_eofOwed"/> 换成 0 谁发（<see cref="SendEofAsync"/> 与 stdin 泵共用）。</summary>
    internal async ValueTask SendOwedEofAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _eofOwed, 0) == 1)
        {
            await _host.SendIfAsync(SimplePacket(SshMessageNumber.ChannelEof), _mayStillSend, IsInteractive, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>发一帧，入队那一刻 CLOSE 已经入队了就不发（<see cref="MayStillSend"/>），走这条通道自己的道。</summary>
    internal ValueTask SendIfStillOpenAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken) =>
        _host.SendIfAsync(packet, _mayStillSend, IsInteractive, cancellationToken);

    /// <summary>把状态推进到「本端已 EOF」；已经 EOF 过或者通道在关，返回 <see langword="false"/>。</summary>
    /// <remarks>
    /// EOF 只发一次：<see cref="SendEofAsync"/> 与 stdin 泵（调用方直接完成了 <see cref="StandardInput"/>）
    /// 都从这里过，谁先推进状态谁发。
    /// </remarks>
    internal bool TryMarkLocalEof()
    {
        lock (_stateLock)
        {
            if (_state is SshChannelState.LocalEof or SshChannelState.BothEof
                or SshChannelState.Closing or SshChannelState.Closed)
            {
                return false;
            }
            _state = _state == SshChannelState.RemoteEof
                ? SshChannelState.BothEof
                : SshChannelState.LocalEof;
            Volatile.Write(ref _eofOwed, 1);
            return true;
        }
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
            Task ticket = _input.NextProgress();

            long sent = _input.SentBytes;
            if (sent >= totalBytes)
            {
                return;
            }

            // 看标记而不是看泵的 Task：泵在 finally 里响铃时，它的 Task 还没完成。
            if (_stdinPump is null || _input.HasExited)
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
            await _host.SendIfAsync(SimplePacket(SshMessageNumber.ChannelClose), TryCommitClose, IsInteractive, cancellationToken)
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

    /// <summary>我们开的通道被对端确认了。</summary>
    internal void OnOpenConfirmed(uint remoteId, uint initialWindow, uint maxPacket)
    {
        OnOpenAccepted(remoteId, initialWindow, maxPacket);
        StartPumps();
    }

    /// <summary>记下对端的通道号、初始窗口与包上限，通道进入 Open（泵还没起）。</summary>
    /// <remarks>
    /// <para>
    /// <b>对端开过来的通道，确认发出去之前就要做完这一步。</b>曾经是先发确认、后设对端的号：
    /// 对端收到确认立刻 EOF + CLOSE（端口扫描、健康检查），而决定开通道的后台任务恰好在两步之间被抢占，
    /// 接收循环先处理了那个 CLOSE —— 我们的 CLOSE 带着还是 0 的对端号发了出去，关掉的是对端的 0 号通道
    /// （往往是用户的第一条 shell），真正那条通道永远收不到 CLOSE。
    /// </para>
    /// <para>
    /// 已经收尾了的（会话断开与确认撞在一起）不改回 Open —— 曾经无条件改，关掉的通道又被「复活」、泵照样起来。
    /// </para>
    /// </remarks>
    internal void OnOpenAccepted(uint remoteId, uint initialWindow, uint maxPacket)
    {
        RemoteId = remoteId;

        // 对端宣告的 max packet 按本端的上限截断：照单全收的话，宣告 4 GiB 的对端会让 stdin 泵把管道里攒着的
        // 几 MiB 拼成一个报文发出去 —— 超过任何实现愿意收的报文长度（包括我们自己收的上限）。
        RemoteMaxPacketBytes = (int)Math.Min(maxPacket, MaxSendDataBytes);
        _input.OpenWindow(initialWindow);

        bool opened = false;
        lock (_stateLock)
        {
            if (_state == SshChannelState.Opening)
            {
                _state = SshChannelState.Open;
                Volatile.Write(ref _openedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
                opened = _activeMetered = MetricsHost is not null;
            }
        }

        if (opened && MetricsHost is { } host)
        {
            KeyValuePair<string, object?> type = Diagnostics.SshMetrics.ChannelTypeTag(ChannelType);
            Diagnostics.SshMetrics.ChannelsActive.Add(1, Diagnostics.SshMetrics.HostTag(host), type);
            Diagnostics.SshMetrics.ChannelWindow.Record(_receiveWindow.Size, Diagnostics.SshMetrics.HostTag(host), type);
        }
    }

    /// <summary>起 stdin 泵与回补泵。已经收尾了就不起。</summary>
    /// <remarks>
    /// 与 <see cref="FinishClose"/> 在同一把锁里对「泵起没起」达成一致：收尾时泵没起，由收尾替它完成 stdin 的 reader；
    /// 起了就归泵自己完成 —— 两边各自判断的话，收尾刚完成 reader、泵就起来去读它。
    /// </remarks>
    internal void StartPumps()
    {
        lock (_stateLock)
        {
            if (_state == SshChannelState.Closed)
            {
                return;
            }

            _stdinPump = Task.Run(() => _input.PumpAsync(_lifetime.Token));
            _windowAdjustPump = Task.Run(() => _receiveWindow.PumpAsync(_lifetime.Token));
        }

        _input.Wake();
    }

    internal void OnOpenFailed()
    {
        FinishClose(SshChannelCloseReason.ClosedByPeer);

        // 对端从没建起这条通道，不会再有发往这个号的报文。
        ReleaseId(force: true);
    }

    /// <summary>收到 <c>CHANNEL_DATA</c>。</summary>
    /// <returns>窗口够不够。<see langword="false"/> 是对端的协议违规。</returns>
    internal bool OnData(ReadOnlySequence<byte> data) => _data.OnData(data);

    /// <summary>收到 <c>CHANNEL_EXTENDED_DATA</c>。</summary>
    internal bool OnExtendedData(uint dataTypeCode, ReadOnlySequence<byte> data) => _data.OnExtendedData(dataTypeCode, data);

    /// <summary>丢弃的 stderr 的末尾，按 UTF-8 解、照对端文本清洗过；没留或没有时为 <see langword="null"/>。</summary>
    internal string? DiscardedStderrTail => _data.DiscardedStderrTail;

    /// <summary>收到 <c>CHANNEL_WINDOW_ADJUST</c>。</summary>
    /// <returns>加完有没有溢出 <c>uint32</c>。溢出是协议违规。</returns>
    internal bool OnWindowAdjust(uint bytes) => _input.OnWindowAdjust(bytes);

    /// <summary>收到 <c>CHANNEL_EOF</c>。</summary>
    internal void OnEof()
    {
        lock (_stateLock)
        {
            // 重复的 EOF 什么也不做：曾经它把 BothEof 退回 RemoteEof（本端随后可能再发一次 EOF），
            // 每来一份还往事件流里塞一条 Eof —— 同样不受积压上限约束。
            if (_state is SshChannelState.Closing or SshChannelState.Closed
                or SshChannelState.RemoteEof or SshChannelState.BothEof)
            {
                return;
            }
            _state = _state == SshChannelState.LocalEof
                ? SshChannelState.BothEof
                : SshChannelState.RemoteEof;
        }

        // 对端不再发数据 —— 把读这一侧收尾，消费者读到 IsCompleted。
        // 状态已经在锁里改掉了，ChannelDataReceiver 不会再往这两条管道里写（见 AcceptsDataWhileLocked）。
        _data.Complete();
        _events.OnEof();
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
            _peerClosedFirst = mustReply;
            _closeSent = true;
        }

        return mustReply;
    }

    /// <summary>双向 CLOSE 都走完了（收到对端的那一个之后调用）。</summary>
    internal void OnCloseCompleted()
    {
        // 原因看谁先发的 CLOSE。曾经一律记成 ClosedByPeer：本端 CloseAsync 先发、对端照规矩回一个，
        // 事件流里也说是「对端关的」。
        bool peerClosedFirst;
        lock (_stateLock)
        {
            peerClosedFirst = _peerClosedFirst;
        }

        // 本端先收尾过（释放了通道）的话，状态早就是 Closed，FinishClose 什么都不做 ——
        // 但号一直扣着在等的正是这一个 CLOSE。
        FinishClose(peerClosedFirst ? SshChannelCloseReason.ClosedByPeer : SshChannelCloseReason.ClosedLocally);
        ReleaseId(force: false);
    }

    /// <summary>收到通道请求的应答。</summary>
    /// <returns><see langword="false"/> 说明 FIFO 失步了：账本还开着，却没有人在等（关账之后的迟到应答不算，见 <see cref="ChannelRequestLedger.OnReply"/>）。</returns>
    internal bool OnRequestReply(bool success) => _requests.OnReply(success);

    /// <summary>收到对端发来的通道请求。</summary>
    /// <returns>我们是否「认得」它。不认得且对端要应答时，调用方要回 <c>CHANNEL_FAILURE</c>。</returns>
    internal bool OnPeerRequest(string requestType, ReadOnlyMemory<byte> payload) =>
        _events.OnPeerRequest(requestType, payload);

    /// <summary>事件流里最多积压多少条没读的对端未知请求，超过就不再收。</summary>
    /// <remarks>
    /// 退出状态、EOF、关闭这几件不受它限制 —— 它们每条通道只进事件流一次（重复的照样丢掉，
    /// 见 <see cref="OnPeerRequest"/> 与 <see cref="OnEof"/>）。
    /// </remarks>
    internal const int MaxQueuedEvents = 64;

    /// <summary>
    /// 服务端最近一次说的「客户端能不能在本地做 ^S / ^Q 流控」（<c>xon-xoff</c>，RFC 4254 §6.8）；没说过时为 <see langword="null"/>。
    /// </summary>
    /// <remarks>每次变化同时以 <see cref="SshChannelEvent.FlowControl"/> 进事件流。</remarks>
    public bool? ClientMayDoFlowControl => _events.ClientMayDoFlowControl;

    /// <summary>会话没了。</summary>
    /// <param name="reason">连接的故障；本端释放连接时是 <see cref="ObjectDisposedException"/>。</param>
    internal void OnSessionClosed(Exception reason)
    {
        FinishClose(SshChannelCloseReason.SessionClosed, reason);
        ReleaseId(force: true);
    }

    // ------------------------------------------------------------ 内部

    internal bool IsClosedOrClosing()
    {
        lock (_stateLock)
        {
            return _state is SshChannelState.Closing or SshChannelState.Closed;
        }
    }

    /// <summary>还收不收对端的数据：CLOSE 之后的在途数据、EOF 之后的数据都不收（丢弃，不报错）。</summary>
    /// <remarks>
    /// ⚠️ <b>调用方必须持有 <see cref="_stateLock"/></b>，并在同一把锁里写管道（见 <see cref="ChannelDataReceiver"/>）：
    /// 收尾一侧（<see cref="FinishClose"/>、<see cref="OnEof"/>）总是先在锁里改状态、再完成管道。
    /// </remarks>
    internal bool AcceptsDataWhileLocked =>
        _state is not (SshChannelState.Closing or SshChannelState.Closed or SshChannelState.RemoteEof or SshChannelState.BothEof);

    /// <summary>向会话追加预算；号已经还回去（通道收尾了）时不再追加。</summary>
    internal bool TryChargeBudget(int bytes)
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
    internal void RefundBudget(int bytes)
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
        _wireClosed.TrySetResult();
    }

    private void FinishClose(SshChannelCloseReason reason, Exception? failure = null)
    {
        bool pumpStarted;
        bool wasMetered;
        lock (_stateLock)
        {
            if (_state == SshChannelState.Closed)
            {
                return;
            }
            _state = SshChannelState.Closed;

            // 与 StartPumps 在同一把锁里看：这之后泵不会再起来。
            pumpStarted = _stdinPump is not null;
            wasMetered = _activeMetered;
            _activeMetered = false;
        }

        if (wasMetered && MetricsHost is { } host)
        {
            Diagnostics.SshMetrics.ChannelsActive.Add(
                -1, Diagnostics.SshMetrics.HostTag(host), Diagnostics.SshMetrics.ChannelTypeTag(ChannelType));
        }

        // 事件流的终结事件、关闭原因与关闭信号（WaitForExitAsync 等的那个），按这个顺序放出。
        _events.Close(reason);
        lock (_stateLock)
        {
            if (_closeSent && _closeReceived)
            {
                _wireClosed.TrySetResult();
            }
        }

        // 还在等应答的请求不会再有应答了。让它们返回 false 而不是永远挂着 ——
        // 挂死没有堆栈也没有日志，只有一个再也不返回的 await。
        _requests.Close();

        _data.Complete(failure);

        // 〔velashell-docs/zh/ssh/spec/05 §4.4〕stdin 的 writer 归调用方，这里**不替它完成**。
        // 曾经在这里完成它：调用方随后再写，拿到的是 BCL 的 InvalidOperationException（「writer 已完成」）——
        // 不是 SshException、不带断开原因；而这里常常跑在接收循环上，调用方可能正在别的线程写，
        // 本身就是跨线程动了别人的 PipeWriter。改为让 reader 一侧收尾：reader 完成之后，
        // 写入方的 FlushAsync 返回 IsCompleted（PipeWriter 表达「读的一方不要了」的惯用法）。
        //
        // stdin 的 reader 在泵起来之后**归泵所有**，这里不能替它完成：
        // 泵可能正读到一半，或者刚 AdvanceTo 完要回头再读 —— 从外面完成 reader
        // 会让它撞上「reader 完成后不许再读」。取消 _lifetime（下面）、放开发送窗口的闸之后，
        // 泵的每条路径都会退出，并在退出时自己完成 reader。
        // 泵从没起来（通道没开成，或者确认之前就收尾了）时才由这里完成。
        if (!pumpStarted)
        {
            _input.CompleteReaderForUnstartedPump();
        }

        _input.Wake();
        _receiveWindow.Wake();

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
