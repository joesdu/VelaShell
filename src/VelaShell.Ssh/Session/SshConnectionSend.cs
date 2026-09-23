// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §6    报文逐个封装,序号逐个推进 —— 所以出站必须单写者
//   RFC 4253 §7.1  发出 KEXINIT 之后到 NEWKEYS 之前只许发传输层消息
//   RFC 4253 §7.3  发出 NEWKEYS 之后的下一个报文就用新密钥
//   行为规格:      velashell-docs/zh/ssh/spec/03-key-exchange.md §8.2;velashell-docs/zh/ssh/design/architecture.md §5.4

using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Threading.Tasks.Sources;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Session;

public sealed partial class SshConnection
{
    /// <summary>
    /// 排在发送泵前面、还没封装上线的字节上限。超过它，数据面的发送方就要等。
    /// </summary>
    /// <remarks>
    /// 重协商期间被闸门暂存的帧也算在里面 —— 对端迟迟不完成重协商时，
    /// 我们宁可让发送方等着，也不能无界地攒（原则 2）。
    /// </remarks>
    internal const long MaxPendingSendBytes = SendGate<ReadOnlyMemory<byte>>.DefaultMaxStashBytes;

    /// <summary>一轮最多合并多少字节再刷出。</summary>
    private const int BatchBytes = 64 * 1024;

    /// <summary>一轮最多合并多少个出站项再刷出。</summary>
    private const int BatchItems = 32;

    private readonly Channel<OutboundItem> _outbound = Channel.CreateUnbounded<OutboundItem>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

    /// <summary>
    /// 闸门本身不再限量：限量挪到了入队这一步（<see cref="_pendingSendBytes"/>），
    /// 因为只有在那里才分得清「数据面的发送方」与「接收循环」—— 后者绝不能等。
    /// </summary>
    private readonly SendGate<ReadOnlyMemory<byte>> _sendGate = new(long.MaxValue);

    private readonly Channels.AsyncGate _sendCapacityChanged = new();

    /// <summary>让「登记账本 + 入队」成为一个动作。</summary>
    private readonly Lock _enqueueLock = new();
    private long _pendingSendBytes;
    private readonly Task _sendPump;

    /// <summary>出站项的种类。</summary>
    private enum OutboundKind : byte
    {
        /// <summary>一个报文。</summary>
        Frame,

        /// <summary>关闸：进入重协商。</summary>
        CloseGate,

        /// <summary>发 NEWKEYS 并换上新的发送侧状态。</summary>
        NewKeys,

        /// <summary>开闸并按原顺序排空暂存区。</summary>
        OpenGate,
    }

    /// <summary>排队等发送泵处理的一项。</summary>
    /// <param name="Kind">种类。</param>
    /// <param name="Packet">报文（<see cref="OutboundKind.Frame"/> 时）。</param>
    /// <param name="AccountedBytes">计入 <see cref="_pendingSendBytes"/> 的字节数；控制帧为 0。</param>
    /// <param name="Completion">上线之后要通知的人；<see langword="null"/> 表示没人等。</param>
    /// <param name="Suite">新的发送密码套件（<see cref="OutboundKind.NewKeys"/> 时）。</param>
    /// <param name="Compressor">新的发送压缩器；<see langword="null"/> 表示不动压缩。</param>
    /// <param name="StrictKex">是否启用了严格 KEX。</param>
    private readonly record struct OutboundItem(
        OutboundKind Kind,
        ReadOnlyMemory<byte> Packet,
        int AccountedBytes,
        SendCompletion? Completion,
        ISshCipherSuite? Suite = null,
        ISshCompressor? Compressor = null,
        bool StrictKex = false);

    // ------------------------------------------------------------ 入队

    /// <summary>发一个报文，等它真正刷上线。</summary>
    /// <remarks>
    /// <para>
    /// <b>报文的内存交给发送泵</b>，直到本方法返回之前都不能改动它 ——
    /// 重协商期间它可能被暂存到开闸之后才发出。
    /// </para>
    /// <para>
    /// 取消只在入队之前（含背压等待）生效。入队之后这一帧一定会按顺序发出去，
    /// 那时再取消只会让调用方误以为它没发。
    /// </para>
    /// </remarks>
    private ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken) =>
        EnqueueAsync(packet, applyBackpressure: true, onEnqueued: null, cancellationToken);

    /// <summary>发一个报文；<paramref name="onEnqueued"/> 与入队<b>原子地</b>执行。</summary>
    /// <remarks>
    /// 给「应答靠 FIFO 对齐」的请求用：登记账本与入队必须是同一个动作。
    /// 分开做的话，两个并发的请求可能登记顺序与上线顺序相反，
    /// 应答就会被安到对方头上 —— 而那是静默的错误答案；
    /// 登记之后在背压上被取消，账本里又会留下一个永远等不到应答的空位。
    /// </remarks>
    private ValueTask SendAsync(
        ReadOnlyMemory<byte> packet, Action onEnqueued, CancellationToken cancellationToken) =>
        EnqueueAsync(packet, applyBackpressure: true, onEnqueued, cancellationToken);

    /// <summary>发一个不受背压限制的报文（接收循环与密钥交换用）。</summary>
    /// <remarks>
    /// ⚠️ <b>接收循环绝不能在背压上等。</b>背压要靠接收循环读到
    /// <c>WINDOW_ADJUST</c>、读到对端的 <c>NEWKEYS</c> 才能解除 ——
    /// 让它等背压，就是让它等它自己。
    /// </remarks>
    private ValueTask SendControlAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken) =>
        EnqueueAsync(packet, applyBackpressure: false, onEnqueued: null, cancellationToken);

    private async ValueTask EnqueueAsync(
        ReadOnlyMemory<byte> packet,
        bool applyBackpressure,
        Action? onEnqueued,
        CancellationToken cancellationToken)
    {
        // 空包不该走到这里 —— 消息编号是第一个字节。
        if (packet.IsEmpty)
        {
            throw new ArgumentException("待发的报文是空的。", nameof(packet));
        }

        ThrowIfFaulted();
        cancellationToken.ThrowIfCancellationRequested();

        int accounted = 0;
        if (applyBackpressure)
        {
            // 先取票、再查条件、最后等票 —— 顺序反过来会丢唤醒。
            while (true)
            {
                Task changed = _sendCapacityChanged.NextChange();
                ThrowIfFaulted();
                if (Volatile.Read(ref _pendingSendBytes) < MaxPendingSendBytes)
                {
                    break;
                }
                await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            accounted = packet.Length;
            Interlocked.Add(ref _pendingSendBytes, accounted);
        }

        SendCompletion completion = SendCompletion.Rent();
        ValueTask done = completion.AsValueTask();

        bool enqueued;
        if (onEnqueued is null)
        {
            enqueued = _outbound.Writer.TryWrite(
                new OutboundItem(OutboundKind.Frame, packet, accounted, completion));
        }
        else
        {
            lock (_enqueueLock)
            {
                enqueued = _outbound.Writer.TryWrite(
                    new OutboundItem(OutboundKind.Frame, packet, accounted, completion));
                if (enqueued)
                {
                    onEnqueued();
                }
            }
        }

        if (!enqueued)
        {
            ReleasePendingBytes(accounted);
            completion.SetException(ClosedException());
        }

        await done.ConfigureAwait(false);
    }

    /// <summary>投递一个报文，不等它上线（接收循环用）。</summary>
    /// <remarks>
    /// 接收循环里要回的那些小报文（<c>CHANNEL_CLOSE</c>、请求应答、
    /// <c>OPEN_CONFIRMATION</c>…）走这里：发送泵保证它们按投递顺序上线，
    /// 发送失败则由发送泵把整条会话判死 —— 接收循环不需要、也不应该等。
    /// </remarks>
    private void Post(ReadOnlyMemory<byte> packet)
    {
        if (Volatile.Read(ref _fault) is not null)
        {
            return;
        }

        _outbound.Writer.TryWrite(new OutboundItem(OutboundKind.Frame, packet, 0, null));
    }

    /// <summary>投递一个控制项（关闸 / 开闸），不等它生效。</summary>
    /// <remarks>
    /// 控制项与报文在同一条队列里，所以「关闸之前入队的帧照常发出、之后的被暂存」
    /// 是由队列顺序保证的，不需要任何锁。
    /// </remarks>
    private void PostControl(OutboundKind kind) =>
        _outbound.Writer.TryWrite(new OutboundItem(kind, default, 0, null));

    /// <summary>发 NEWKEYS 并换上新的发送侧状态，等它刷上线。</summary>
    private async ValueTask SendNewKeysAsync(
        ISshCipherSuite suite, ISshCompressor? compressor, bool strictKex, CancellationToken cancellationToken)
    {
        ThrowIfFaulted();
        cancellationToken.ThrowIfCancellationRequested();

        SendCompletion completion = SendCompletion.Rent();
        ValueTask done = completion.AsValueTask();

        if (!_outbound.Writer.TryWrite(
                new OutboundItem(OutboundKind.NewKeys, default, 0, completion, suite, compressor, strictKex)))
        {
            completion.SetException(ClosedException());
        }

        await done.ConfigureAwait(false);
    }

    private void ReleasePendingBytes(int bytes)
    {
        if (bytes > 0)
        {
            Interlocked.Add(ref _pendingSendBytes, -bytes);
        }
    }

    private Exception ClosedException() =>
        Volatile.Read(ref _fault) ?? new SshConnectionClosedException(
            SshFailureReason.Aborted, SshPhase.Open, "连接已经关闭，报文没有发出去。");

    // ------------------------------------------------------------ 发送泵

    /// <summary>
    /// 唯一的写者：把出站项逐个封装进传输，一轮合并若干项再刷出一次。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 单写者是 RFC 4253 §6 逼出来的：序号逐包推进、密钥在 NEWKEYS 处切换，
    /// 这两件事都要求封装严格按一个顺序进行。与其让 N 个写者抢一把锁、
    /// 每人各刷一次，不如只有一个写者，一轮把攒下的帧一起刷出去。
    /// </para>
    /// <para>
    /// 重协商的闸门只有这里会碰 —— 关闸、开闸、换密钥都是队列里的一项，
    /// 它们与报文之间的先后就是入队的先后，不需要另外的同步。
    /// </para>
    /// </remarks>
    private async Task SendPumpAsync(CancellationToken cancellationToken)
    {
        ChannelReader<OutboundItem> reader = _outbound.Reader;
        List<SendCompletion> flushed = [];
        Exception? failure = null;

        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                int bytes = 0;
                int items = 0;

                while (bytes < BatchBytes && items < BatchItems && reader.TryRead(out OutboundItem item))
                {
                    items++;
                    bytes += Process(item, flushed);
                }

                await _transport.FlushAsync(cancellationToken).ConfigureAwait(false);

                foreach (SendCompletion completion in flushed)
                {
                    completion.SetResult();
                }
                flushed.Clear();

                _sendCapacityChanged.Signal();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 我们自己要收工。
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (failure is not null)
        {
            Fault(failure);
        }

        // 收尾：还在等的人一律拿到同一个原因，而不是永远挂着。
        _outbound.Writer.TryComplete();
        Exception reason = ClosedException();

        foreach (SendCompletion completion in flushed)
        {
            completion.SetException(reason);
        }

        while (reader.TryRead(out OutboundItem item))
        {
            ReleasePendingBytes(item.AccountedBytes);
            item.Completion?.SetException(reason);
            item.Suite?.Dispose();
            item.Compressor?.Dispose();
        }

        _sendGate.DrainForAbort();
        Interlocked.Exchange(ref _pendingSendBytes, 0);
        _sendCapacityChanged.Signal();
    }

    /// <summary>处理一个出站项。</summary>
    /// <returns>这一项写进传输的字节数。</returns>
    private int Process(in OutboundItem item, List<SendCompletion> flushed)
    {
        switch (item.Kind)
        {
            case OutboundKind.Frame:
                {
                    SshMessageNumber number = (SshMessageNumber)item.Packet.Span[0];

                    // 暂存时登记的是「计入背压的字节数」，排空时照原数归还。
                    switch (_sendGate.Admit(item.Packet, number, item.AccountedBytes))
                    {
                        case SendGateAdmission.Send:
                            _transport.WritePacket(item.Packet.Span);
                            ReleasePendingBytes(item.AccountedBytes);
                            if (item.Completion is not null)
                            {
                                flushed.Add(item.Completion);
                            }
                            return item.Packet.Length;

                        default:
                            // 已暂存：开闸后按原顺序流出。发送方不必再等 ——
                            // 它等的是「收下了」，不是「上线了」，而暂存的字节仍然计在背压里。
                            item.Completion?.SetResult();
                            return 0;
                    }
                }

            case OutboundKind.CloseGate:
                _sendGate.Close();
                if (item.Completion is not null)
                {
                    flushed.Add(item.Completion);
                }
                return 0;

            case OutboundKind.NewKeys:
                // ⚠️ NEWKEYS 与换发送侧状态之间不能插进任何一帧 ——
                // 单写者 + 同一项里做完，这件事是结构上保证的。
                _transport.WritePacket([(byte)SshMessageNumber.NewKeys]);
                _transport.SetSendCipherSuite(item.Suite!, item.StrictKex);
                if (item.Compressor is not null)
                {
                    _transport.SetSendCompressor(item.Compressor);
                }
                if (item.Completion is not null)
                {
                    flushed.Add(item.Completion);
                }
                return 1;

            case OutboundKind.OpenGate:
                {
                    _sendGate.Open();
                    int written = 0;
                    while (_sendGate.TryTakeStashed(out ReadOnlyMemory<byte> frame, out int accounted))
                    {
                        _transport.WritePacket(frame.Span);
                        ReleasePendingBytes(accounted);
                        written += frame.Length;
                    }
                    if (item.Completion is not null)
                    {
                        flushed.Add(item.Completion);
                    }
                    return written;
                }

            default:
                throw new InvalidOperationException($"未知的出站项 {item.Kind}。");
        }
    }

    /// <summary>
    /// 一次发送的完成通知。池化，免得每个报文都分配一个 <see cref="TaskCompletionSource"/>
    /// （原则 1：热路径用 <see cref="ValueTask"/> + 池化的 <see cref="IValueTaskSource"/>）。
    /// </summary>
    private sealed class SendCompletion : IValueTaskSource
    {
        private const int MaxPooled = 256;
        private static readonly ConcurrentQueue<SendCompletion> Pool = new();

        // 续体一律异步执行：完成它的是发送泵，不能让等待方的代码跑在泵的线程上。
        private ManualResetValueTaskSourceCore<bool> _core = new() { RunContinuationsAsynchronously = true };

        public static SendCompletion Rent() =>
            Pool.TryDequeue(out SendCompletion? pooled) ? pooled : new SendCompletion();

        public ValueTask AsValueTask() => new(this, _core.Version);

        public void SetResult() => _core.SetResult(true);

        public void SetException(Exception exception) => _core.SetException(exception);

        void IValueTaskSource.GetResult(short token)
        {
            try
            {
                _core.GetResult(token);
            }
            finally
            {
                _core.Reset();
                if (Pool.Count < MaxPooled)
                {
                    Pool.Enqueue(this);
                }
            }
        }

        ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _core.GetStatus(token);

        void IValueTaskSource.OnCompleted(
            Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            _core.OnCompleted(continuation, state, token, flags);
    }
}
