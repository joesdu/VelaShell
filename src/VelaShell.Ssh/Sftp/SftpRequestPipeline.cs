// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-ietf-secsh-filexfer-02 §3、§4
//   行为规格:                    velashell-docs/zh/ssh/spec/06-sftp.md §5、§九

using System.Buffers;
using System.IO.Pipelines;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Ssh.Sftp;

/// <summary>SFTP 请求的流水线。</summary>
/// <remarks>
/// <para>
/// <b>一条通道上可以有大量在途请求，应答顺序不保证</b> —— 这正是 SFTP 能跑出吞吐的原因，
/// 也是必须用 <c>request-id</c> 而不是队列来对齐应答的原因
/// （与通道请求的 FIFO 语义正好相反，见 <c>velashell-docs/zh/ssh/spec/05-connection.md</c> §5.1）。
/// </para>
/// <para>
/// 在途请求数 × 块大小就是 SFTP 层的「窗口」。和通道窗口一样，固定值在高 RTT 链路上
/// 直接封死吞吐：64 × 32 KiB = 2 MiB，200 ms RTT 下同样是 10 MB/s 封顶。
/// </para>
/// </remarks>
internal sealed class SftpRequestPipeline : IAsyncDisposable
{
    private readonly SshChannel _channel;
    private readonly Lock _stateLock = new();
    private readonly Dictionary<uint, PendingRequest> _pending = [];
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _inFlight;
    private readonly CancellationTokenSource _lifetime = new();

    private Task? _receiveLoop;
    private uint _nextRequestId = 1;   // 0 保留不用
    private readonly int _initialDepth;
    private readonly int _ceiling;

    /// <summary>最近这一窗里，有多少次请求是**等着**才拿到额度的。</summary>
    private int _saturationHits;

    /// <summary>最近这一窗里一共发了多少请求。</summary>
    private int _windowRequests;
    private Exception? _fault;
    private bool _disposed;

    /// <summary>流水线坏掉（或释放）的那一刻完成，结果是原因。不以异常完成 —— 没人等它时也不会变成未观察的任务异常。</summary>
    private readonly TaskCompletionSource<Exception> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>在一条已经起好 sftp 子系统的通道上建立流水线。</summary>
    /// <param name="channel">通道。</param>
    /// <param name="maxInFlight">在途请求数的起始值。</param>
    /// <param name="adaptive">
    /// 是否按「深度有没有成为瓶颈」自动伸缩。
    /// </param>
    /// <param name="ceiling">自适应时深度的上限。</param>
    public SftpRequestPipeline(
        SshChannel channel, int maxInFlight = 64, bool adaptive = true, int ceiling = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxInFlight, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(ceiling, maxInFlight);

        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        MaxInFlight = maxInFlight;
        _initialDepth = maxInFlight;
        _ceiling = ceiling;
        IsAdaptive = adaptive;

        // maxCount 开到上限：SemaphoreSlim 不能改大小，但可以在
        // 初始额度之外继续 Release —— 这就是「长大」的做法。
        _inFlight = new SemaphoreSlim(maxInFlight, ceiling);
    }

    /// <summary>
    /// 当前允许的在途请求数。
    /// </summary>
    /// <remarks>
    /// <b>在途请求数 × 块大小就是 SFTP 层的「窗口」。</b>
    /// 和通道窗口一样，固定值在高 RTT 链路上直接封死吞吐：
    /// 64 × 32 KiB = 2 MiB，200 ms RTT 下同样是 10 MB/s 封顶。
    /// </remarks>
    public int MaxInFlight { get; private set; }

    /// <summary>深度会不会自己伸缩。</summary>
    public bool IsAdaptive { get; }

    /// <summary>流水线已经坏了（通道断了、收到了畸形报文）：之后的请求都会失败。</summary>
    internal bool IsFaulted => Volatile.Read(ref _fault) is not null;

    /// <summary>流水线坏掉或释放时完成，结果是原因（见 <see cref="SftpFileSystem.Closed"/>）。</summary>
    internal Task<Exception> Closed => _closed.Task;

    /// <summary>深度被调大过几次（诊断用）。</summary>
    public int DepthIncreases { get; private set; }

    /// <summary>当前在途的请求数。</summary>
    public int InFlightCount
    {
        get
        {
            lock (_stateLock)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>开始收应答。</summary>
    public void Start() => _receiveLoop ??= Task.Run(() => ReceiveLoopAsync(_lifetime.Token));

    /// <summary>发一个请求并等它的应答。</summary>
    /// <param name="write">往缓冲里写报文的回调，参数是分配好的 request-id。</param>
    /// <param name="onLateResponse">
    /// 请求被取消之后，应答仍然到达时执行的善后。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// <para>
    /// <b>取消一个在途请求不会让服务端停下来</b> —— SFTP 没有取消报文。
    /// 我们只是不再等它的应答。
    /// </para>
    /// <para>
    /// 所以取消之后<b>仍然要消费那个 id 的应答</b>，而且必须处理
    /// 「应答里带着一个需要关闭的句柄」的情况：<c>OPEN</c> 被取消但服务端已经打开了文件，
    /// 那个句柄不关就<b>泄漏在服务端</b>。<paramref name="onLateResponse"/> 就是为此存在的。
    /// </para>
    /// </remarks>
    public async ValueTask<SftpResponse> SendAsync(
        Action<IBufferWriter<byte>, uint> write,
        Action<SftpResponse>? onLateResponse = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfFaulted();

        // 在途请求数是背压：满了就等，不报错。
        //
        // 先试一把不等的 —— **「有没有等过」就是「深度是不是瓶颈」的信号**，
        // 和通道窗口那边「有没有被吃到见底」是同一个判据。
        if (!_inFlight.Wait(0, CancellationToken.None))
        {
            Interlocked.Increment(ref _saturationHits);
            await WaitOrStopAsync(_inFlight, cancellationToken).ConfigureAwait(false);
        }

        MaybeAdjustDepth();

        uint requestId;
        PendingRequest pending;
        int inFlight;
        lock (_stateLock)
        {
            ThrowIfFaulted();
            requestId = AllocateRequestId();
            pending = new PendingRequest(onLateResponse);
            _pending[requestId] = pending;
            inFlight = _pending.Count;
        }

        // 〔velashell-docs/zh/ssh/spec/08 §7〕管线深度的实际取值（velashell.ssh.sftp.inflight）。
        if (_channel.MetricsHost is { } metricsHost && Diagnostics.SshMetrics.SftpInFlight.Enabled)
        {
            Diagnostics.SshMetrics.SftpInFlight.Record(inFlight, Diagnostics.SshMetrics.HostTag(metricsHost));
        }

        bool sent = false;
        try
        {
            if (!_sendLock.Wait(0, CancellationToken.None))
            {
                await WaitOrStopAsync(_sendLock, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                // ⚠️ **直接写进通道的管道，不经中转缓冲。**曾经先拼进一个 ArrayBufferWriter 再整帧复制过去：
                //    它从 256 字节起翻倍长，一个 256 KiB 的 WRITE 要重新分配十来次、把数据多搬一遍。
                //    写报文的回调（SftpWire.Write*）要么在碰到管道之前就失败（参数不对），要么整帧写完 ——
                //    所以回调返回之后才算「发出去了」；它先失败的话这个 id 照常撤回。
                PipeWriter writer = _channel.StandardInput;
                write(writer, requestId);
                sent = true;
                await FlushFrameAsync(writer, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }

            return await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (sent)
        {
            // 已经发出去了，取消或失败：**请求仍然留在账本里**。
            // 摘掉它的话，迟到的应答会被当成「未知 id」丢弃，
            // 而它可能带着一个需要关闭的句柄。应答若恰好已经交付，Abandon 接手善后。
            pending.Abandon();
            throw;
        }
        catch (Exception)
        {
            // **没发出去**（排队等发送时被取消、拼报文的回调抛了）：这个 id 永远等不到应答。
            // 留在账本里的话，它占着的在途额度只有应答才还得回来 —— 也就是永远还不回来。
            // 曾经就是这样：取消一次上传漏掉几十个额度，漏满之后所有 SFTP 操作一起挂住。
            Withdraw(requestId);

            // 排在发送锁上时流水线收工了：Fault 已经把它从账本里摘走、把故障设在了 Completion 上，而那个任务没人会再看 ——
            // 断线时几个排队的写一起变成未观察的任务异常（velashell-docs/zh/ssh/spec/06 §5.4）。放弃它：故障在那里看一眼。
            pending.Abandon();
            throw;
        }
    }

    /// <summary>把一个没发出去的请求从账本里摘掉，还回它的在途额度。</summary>
    /// <remarks>已经被 <see cref="Fault"/> 结算过的就不用管了 —— 流水线已经废了。</remarks>
    private void Withdraw(uint requestId)
    {
        bool removed;
        lock (_stateLock)
        {
            removed = _pending.Remove(requestId);
        }

        if (removed)
        {
            _inFlight.Release();
        }
    }

    /// <summary>在信号量上等；流水线收工（故障或释放）时立刻放出来，而不是陪着一起挂。</summary>
    /// <remarks>
    /// 在途额度只有应答才还得回来，而故障之后不会再有应答 —— 不连上 <see cref="_lifetime"/>，
    /// 排在额度上的调用方（比如列目录时并发解析的那一批符号链接）就永远等下去。
    /// </remarks>
    private async ValueTask WaitOrStopAsync(SemaphoreSlim semaphore, CancellationToken cancellationToken)
    {
        using var linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            await semaphore.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ThrowIfFaulted();
            throw new ObjectDisposedException(nameof(SftpRequestPipeline));
        }
    }

    /// <summary>把一帧交给通道。<b>字节一定会整帧提交</b>，取消只打断背压等待。</summary>
    /// <remarks>
    /// <para>
    /// 不能把调用方的令牌直接交给 <see cref="PipeWriter.WriteAsync"/>：令牌进门时就已取消的话，
    /// 它<b>一个字节都不写</b>；等背压时才取消的话，字节<b>已经提交</b>、照样会发出去。
    /// 抛出来的都是同一个取消异常，调用方分不清这个请求到底上没上线 ——
    /// 而「上没上线」决定了它的在途额度该不该当场还回去。
    /// </para>
    /// <para>
    /// 所以写的时候不带令牌，取消改用 <see cref="PipeWriter.CancelPendingFlush"/> 打断背压等待：
    /// 进了这个方法，请求就算发出去了。
    /// </para>
    /// </remarks>
    private async ValueTask WriteFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        PipeWriter writer = _channel.StandardInput;
        writer.Write(frame.Span);
        await FlushFrameAsync(writer, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>把已经写进 <paramref name="writer"/> 的整帧刷出去（见 <see cref="WriteFrameAsync"/> 的说明）。</summary>
    private async ValueTask FlushFrameAsync(PipeWriter writer, CancellationToken cancellationToken)
    {
        FlushResult result;
        using (cancellationToken.Register(CancelPendingFlush, writer))
        using (_lifetime.Token.Register(CancelPendingFlush, writer))
        {
            result = await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }

        if (result.IsCanceled)
        {
            // 调用方取消的，照实抛。流水线收工打断的，接着去等应答 —— Fault 已经把它结算成了故障。
            // 两者都不是的话，是上一个写者登记的回调在它收尾的边缘触发、取消到了这一次
            // （没有挂起的 flush 时，CancelPendingFlush 作用于下一次）：字节已经提交，少等一回背压无妨。
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static readonly Action<object?> CancelPendingFlush =
        static state => ((PipeWriter)state!).CancelPendingFlush();

    /// <summary>每攒够一窗请求，看一次要不要调深度。</summary>
    /// <remarks>
    /// <para>
    /// 判据是<b>「这一窗里有多少次请求是等着才拿到额度的」</b>。
    /// 等得多说明在途额度用光了、链路还没喂饱 —— 深度就是瓶颈，调大。
    /// 几乎不等说明够用 —— 往回收一点，别白占内存与服务端的句柄。
    /// </para>
    /// <para>
    /// 不按 RTT 直接算 BDP：那要先估出带宽，而带宽估计本身在一条
    /// 有别的流量的链路上很不稳。「有没有撞到上限」是个更直接、
    /// 也更难估错的信号。
    /// </para>
    /// </remarks>
    private void MaybeAdjustDepth()
    {
        if (!IsAdaptive)
        {
            return;
        }

        if (Interlocked.Increment(ref _windowRequests) < AdjustWindow)
        {
            return;
        }

        int hits = Interlocked.Exchange(ref _saturationHits, 0);
        Interlocked.Exchange(ref _windowRequests, 0);

        lock (_stateLock)
        {
            // 一窗里超过一半都在等 → 深度是瓶颈。
            if (hits * 2 > AdjustWindow && MaxInFlight < _ceiling)
            {
                int grown = Math.Min(MaxInFlight * 2, _ceiling);
                int extra = grown - MaxInFlight;

                MaxInFlight = grown;
                DepthIncreases++;
                _inFlight.Release(extra);
                return;
            }

            // 几乎没等过 → 往回收，但不低于起始值。
            if (hits == 0 && MaxInFlight > _initialDepth)
            {
                int shrunk = Math.Max(MaxInFlight * 3 / 4, _initialDepth);
                int give = MaxInFlight - shrunk;

                // 收回额度要**不阻塞地**收：收不回来说明它们正在用，
                // 那就下一窗再说 —— 在这里等会把调用方一起卡住。
                for (int i = 0; i < give && _inFlight.Wait(0, CancellationToken.None); i++)
                {
                    MaxInFlight--;
                }
            }
        }
    }

    /// <summary>每多少个请求评估一次深度。</summary>
    private const int AdjustWindow = 32;

    // ------------------------------------------------------------ 接收

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        PipeReader reader = _channel.StandardOutput;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ReadResult read = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = read.Buffer;
                SequencePosition consumed = buffer.Start;

                while (SftpWire.TryReadFrame(ref buffer, out SftpFrame frame))
                {
                    Dispatch(frame);
                    consumed = buffer.Start;
                }

                reader.AdvanceTo(consumed, buffer.End);

                if (read.IsCompleted)
                {
                    Fault(new SftpTransferInterruptedException(
                        0, "SFTP 通道在还有在途请求时就关闭了。"));
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 本端在收工（关闭文件系统、连接释放）：那是中止，不是「服务端不支持 SFTP」。
            Fault(new SftpUnavailableException(SshFailureReason.Aborted, "SFTP 流水线已收工。"));
        }
        catch (Exception ex)
        {
            Fault(ex);
        }
    }

    private void Dispatch(SftpFrame frame)
    {
        // INIT / VERSION 没有 request-id，走单独的一条路。
        if (frame.Type == SftpMessageType.Version)
        {
            CompleteVersion(frame);
            return;
        }

        if (frame.Payload.Length < 4)
        {
            throw new SshProtocolException(
                SshPhase.Open, $"SFTP 应答 {frame.Type} 连 request-id 都放不下。");
        }

        uint requestId = SftpWire.ReadRequestId(frame.Payload);
        ReadOnlySequence<byte> payload = frame.Payload.Slice(4);

        PendingRequest? pending;
        lock (_stateLock)
        {
            if (!_pending.Remove(requestId, out pending))
            {
                // 未知 request-id：忽略，不断开。
                // 多半是已取消请求的迟到应答（velashell-docs/zh/ssh/spec/06 §九）。
                return;
            }
        }

        _inFlight.Release();

        // 交给等的人；请求已经被取消了的话就地善后（见 PendingRequest）。
        pending.Deliver(Materialize(frame.Type, requestId, payload));
    }

    private static SftpResponse Materialize(SftpMessageType type, uint requestId, ReadOnlySequence<byte> payload)
    {
        int length = (int)payload.Length;
        byte[] rented = ArrayPool<byte>.Shared.Rent(Math.Max(length, 1));
        payload.CopyTo(rented);
        return new SftpResponse(type, requestId, rented, length);
    }

    private TaskCompletionSource<SftpResponse>? _versionCompletion;

    /// <summary>等 <c>SSH_FXP_VERSION</c>。<b>它没有 request-id</b>，所以要单独等。</summary>
    /// <remarks>
    /// 流水线已经收工时当场以收工的原因结算。sftp-server 一起来就退出时，通道里早就排着 stderr、退出状态与 CLOSE，
    /// 收包循环一开跑就收工 —— 可能抢在握手走到这里之前。<see cref="Fault"/> 只结算已经有人在等的 VERSION，
    /// 曾经晚来的这一个要等满握手时限（30 秒），报成「sftp-server 没有回应」，退出码与 stderr 都丢了。
    /// </remarks>
    internal Task<SftpResponse> WaitForVersionAsync()
    {
        lock (_stateLock)
        {
            _versionCompletion ??= new TaskCompletionSource<SftpResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (_fault is { } fault)
            {
                // VERSION 已经到了的话 TrySetException 什么也不做，照样交出它。
                _versionCompletion.TrySetException(fault);
            }
            return _versionCompletion.Task;
        }
    }

    private void CompleteVersion(SftpFrame frame)
    {
        SftpResponse response = Materialize(SftpMessageType.Version, 0, frame.Payload);

        TaskCompletionSource<SftpResponse> completion;
        lock (_stateLock)
        {
            _versionCompletion ??= new TaskCompletionSource<SftpResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            completion = _versionCompletion;
        }

        if (!completion.TrySetResult(response))
        {
            response.Dispose();
        }
    }

    /// <summary>直接发一段已经拼好的报文（握手用，它没有 request-id）。</summary>
    internal async ValueTask SendRawAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        if (!_sendLock.Wait(0, CancellationToken.None))
        {
            await WaitOrStopAsync(_sendLock, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await WriteFrameAsync(packet, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // ------------------------------------------------------------ 内部

    /// <summary>分配一个 request-id。调用时必须持有锁。</summary>
    /// <remarks><c>0</c> 保留不用；回绕时跳过已占用的号。</remarks>
    private uint AllocateRequestId()
    {
        for (int attempt = 0; attempt <= ushort.MaxValue; attempt++)
        {
            uint candidate = _nextRequestId++;
            if (_nextRequestId == 0)
            {
                _nextRequestId = 1;   // 回绕，跳过 0
            }

            if (candidate != 0 && !_pending.ContainsKey(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("找不到空闲的 SFTP request-id —— 在途请求太多了。");
    }

    private void ThrowIfFaulted()
    {
        Exception? fault = _fault;
        if (fault is not null)
        {
            throw fault;
        }
    }

    private void Fault(Exception exception)
    {
        PendingRequest[] pending;
        Exception reason;
        lock (_stateLock)
        {
            _fault ??= exception;
            reason = _fault;
            pending = [.. _pending.Values];
            _pending.Clear();
        }
        _closed.TrySetResult(reason);

        // **一次性**把所有在途请求以同一个异常收尾。
        // 不这么做的话，通道断开时每个在途请求都会各自挂到取消或超时上 ——
        // 而挂死没有堆栈也没有日志。
        foreach (PendingRequest item in pending)
        {
            item.Fail(exception);
        }

        lock (_stateLock)
        {
            if (_versionCompletion is { } version && version.TrySetException(exception))
            {
                // 等 VERSION 的人可能已经走了：握手超时（WaitAsync 到点）之后流水线才收工，故障落在一个没人再看的任务上，
                // GC 时成了未观察的任务异常。这里看一眼；还在等的人照样拿到它。
                _ = version.Task.Exception;
            }
        }

        // 还排在在途额度或发送锁上的调用方也要放出来（见 WaitOrStopAsync）。
        // 回调放到线程池上：Fault 常常跑在收应答的循环上。
        Session.Lifecycle.CancelInBackground(_lifetime);
    }

    /// <summary>一个在途请求：等应答的人与收应答的循环之间，谁拿到应答由一个原子状态说了算。</summary>
    /// <remarks>
    /// <para>
    /// 曾经是一个普通的布尔「已放弃」：收包循环先看它、再把应答交给 <see cref="Completion"/>；
    /// 等应答的一方 <c>WaitAsync</c> 被取消之后才把它设上。取消与应答同时到达时，收包循环看到的还是「没放弃」，
    /// 应答（带着服务端已经打开的句柄）就留在一个再也没人读的任务里 —— 句柄泄漏在服务端，租来的缓冲也不还池。
    /// </para>
    /// <para>
    /// 现在两边用比较并交换抢同一个状态：收包循环抢到「已交付」就交给等的人；
    /// 放弃的一方抢到「已放弃」，收包循环就去善后；放弃的一方晚了一步（应答已经交付），由它接手善后。
    /// </para>
    /// </remarks>
    internal sealed class PendingRequest(Action<SftpResponse>? onLateResponse)
    {
        private const int Waiting = 0;
        private const int Delivered = 1;
        private const int Abandoned = 2;

        private int _state;

        public TaskCompletionSource<SftpResponse> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>已经被放弃（等的人走了，应答到了要善后）。</summary>
        public bool IsAbandoned => Volatile.Read(ref _state) == Abandoned;

        /// <summary>收包循环：把应答交给等的人。等的人已经走了就由这里善后。</summary>
        public void Deliver(SftpResponse response)
        {
            if (Interlocked.CompareExchange(ref _state, Delivered, Waiting) == Waiting)
            {
                if (!Completion.TrySetResult(response))
                {
                    response.Dispose();
                }
                return;
            }

            HandleLate(response);
        }

        /// <summary>流水线收工：把故障交给还在等的人。</summary>
        /// <remarks>
        /// 〔velashell-docs/zh/ssh/spec/06 §5.4〕<b>已经放弃的不设异常。</b>等它的人已经走了，没人会看这个任务 ——
        /// 设了就是一个未观察的任务异常，GC 时触发 <c>UnobservedTaskException</c>，宿主据此写进崩溃日志：
        /// 一次断线让几十个被取消过的请求同时「崩溃」，崩溃日志里全是其实不是崩溃的记录。
        /// </remarks>
        public void Fail(Exception exception)
        {
            if (Interlocked.CompareExchange(ref _state, Delivered, Waiting) == Waiting)
            {
                Completion.TrySetException(exception);
            }
        }

        /// <summary>等的人不等了（取消或失败）。应答要是已经交付了，就在这里接手善后。</summary>
        public void Abandon()
        {
            if (Interlocked.CompareExchange(ref _state, Abandoned, Waiting) == Waiting)
            {
                return;   // 应答还没到，收包循环会善后
            }

            // 应答（或收工时的故障）已经、或马上就会落在 Completion 上，而等它的人已经走了：
            // 应答由这里善后；故障在这里看一眼，免得变成未观察的任务异常。
            _ = Completion.Task.ContinueWith(
                static (task, state) =>
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        ((PendingRequest)state!).HandleLate(task.Result);
                    }
                    else
                    {
                        _ = task.Exception;
                    }
                },
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        /// <summary>请求已经被取消了，但应答还是来了。<b>必须善后</b>：OPEN 的句柄不关就泄漏在服务端。</summary>
        private void HandleLate(SftpResponse response)
        {
            try
            {
                onLateResponse?.Invoke(response);
            }
            catch (Exception)
            {
                // 善后失败没有进一步的补救动作可做。
            }
            finally
            {
                response.Dispose();
            }
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

        Fault(new SftpUnavailableException("SFTP 流水线已释放。"));

        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 同上。
            }
        }

        // 两个信号量**不释放**：还在路上的调用方收尾时要 Release 它们（发送锁的 finally、
        // 没发出去的请求还额度），释放了就是一个盖住真实原因的 ObjectDisposedException。
        // 它们没有用到等待句柄，不释放也不漏任何非托管资源。
        _lifetime.Dispose();
    }
}
