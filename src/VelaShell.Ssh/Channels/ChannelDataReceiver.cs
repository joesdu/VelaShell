// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §5.2  CHANNEL_DATA / CHANNEL_EXTENDED_DATA(扩展数据同样计入窗口)
//   行为规格:      velashell-docs/zh/ssh/spec/05-connection.md §1、§3.2、§4

using System.Buffers;
using System.IO.Pipelines;

namespace VelaShell.Ssh.Channels;

/// <summary>一条通道的数据接收侧：扣接收窗口，把数据投进 stdout / stderr 两条管道，交不出去的丢弃并立刻回补。</summary>
/// <remarks>
/// <para>
/// 从 <see cref="SshChannel"/> 拆出来的协作者。投递跑在接收循环上（所有通道共用的那一条），所以这里<b>绝不等待</b>：
/// 管道的水位定得比窗口高，写进去不会阻塞（见构造函数里的说明）。
/// </para>
/// <para>
/// ⚠️ 「看通道状态」与「写管道」在<b>通道的状态锁</b>里一起做（<c>deliveryGate</c>，与交给 <see cref="WindowedPipeReader"/> 的是同一把）：
/// 收尾一侧总是先在那把锁里改状态、再完成管道（<c>SshChannel.FinishClose</c> / <c>SshChannel.OnEof</c>）。
/// </para>
/// </remarks>
internal sealed class ChannelDataReceiver
{
    /// <summary>扩展数据里代表 stderr 的类型码（RFC 4254 §5.2）。</summary>
    private const uint ExtendedDataStderr = 1;

    private readonly SshChannel _channel;
    private readonly ChannelReceiveWindow _window;
    private readonly Lock _deliveryGate;

    private readonly Pipe _stdoutPipe;
    private readonly Pipe? _stderrPipe;
    private readonly WindowedPipeReader _stdoutReader;
    private readonly WindowedPipeReader? _stderrReader;

    private long _bytesReceived;

    /// <param name="channel">所属通道。</param>
    /// <param name="window">它的接收窗口。</param>
    /// <param name="options">通道参数（stderr 怎么处理、丢弃时留多少末尾）。</param>
    /// <param name="deliveryGate">通道的状态锁。</param>
    internal ChannelDataReceiver(SshChannel channel, ChannelReceiveWindow window, SshChannelOptions options, Lock deliveryGate)
    {
        _channel = channel;
        _window = window;
        _deliveryGate = deliveryGate;

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
        SshWindowPolicy policy = options.WindowPolicy;
        PipeOptions pipeOptions = new(
            pauseWriterThreshold: policy.MaximumBytes * 2L,
            resumeWriterThreshold: policy.MaximumBytes,
            useSynchronizationContext: false);

        _stdoutPipe = new Pipe(pipeOptions);
        _stderrPipe = options.StderrMode == SshStderrMode.Buffer ? new Pipe(pipeOptions) : null;
        _stderrTailLimit = _stderrPipe is null ? options.DiscardedStderrTailBytes : 0;

        // 收尾的读端在通道的状态锁里标记，TryDeliver 在同一把锁里看（见 WindowedPipeReader.ReleaseUnread）。
        StandardOutput = _stdoutReader = new WindowedPipeReader(_stdoutPipe.Reader, window.NoteReaderConsumed, deliveryGate);
        _stderrReader = _stderrPipe is null ? null : new WindowedPipeReader(_stderrPipe.Reader, window.NoteReaderConsumed, deliveryGate);
        StandardError = _stderrReader ?? (PipeReader)new EmptyPipeReader();
    }

    /// <summary>交给使用者的 stdout 读端（见 <see cref="SshChannel.StandardOutput"/>）。</summary>
    public PipeReader StandardOutput { get; }

    /// <summary>交给使用者的 stderr 读端；丢弃 stderr 时是一条立刻结束的空流（见 <see cref="SshChannel.StandardError"/>）。</summary>
    public PipeReader StandardError { get; }

    /// <summary>收到的应用数据字节数（读没读走都算）。</summary>
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    /// <summary>收到 <c>CHANNEL_DATA</c>。</summary>
    /// <returns>窗口够不够。<see langword="false"/> 是对端的协议违规。</returns>
    public bool OnData(ReadOnlySequence<byte> data)
    {
        int length = (int)data.Length;
        if (!_window.TryConsume(length))
        {
            return false;
        }
        Interlocked.Add(ref _bytesReceived, length);

        _window.NoteArrival();

        if (!TryDeliver(_stdoutPipe.Writer, _stdoutReader, data))
        {
            // 没交出去（通道在关、对端已 EOF、消费者不读了）—— 丢弃，
            // 且**立刻回补窗口**：不然「丢弃」就变成了让对端停住的死锁。
            _window.NoteConsumed(length);
        }
        else
        {
            _window.NoteDelivered(length);
        }
        return true;
    }

    /// <summary>收到 <c>CHANNEL_EXTENDED_DATA</c>。</summary>
    public bool OnExtendedData(uint dataTypeCode, ReadOnlySequence<byte> data)
    {
        int length = (int)data.Length;

        // 扩展数据**同样计入窗口**。忘了这一点的症状是
        // stderr 大量输出时窗口被吃空，整条通道停住。
        if (!_window.TryConsume(length))
        {
            return false;
        }
        Interlocked.Add(ref _bytesReceived, length);

        _window.NoteArrival();

        // 〔决策 velashell-docs/zh/ssh/spec/05 §4.2〕非 stderr 的类型码：丢弃、计入窗口、不报错。
        // 保留值的语义未来可能被定义，为它断开会让我们无法与新实现共处。
        if (dataTypeCode != ExtendedDataStderr || _stderrPipe is null || _stderrReader is null
            || !TryDeliver(_stderrPipe.Writer, _stderrReader, data))
        {
            if (dataTypeCode == ExtendedDataStderr && _stderrTailLimit > 0)
            {
                KeepStderrTail(data);
            }

            // 丢弃也要立刻回补窗口 —— 不然「丢弃」就变成了死锁。
            _window.NoteConsumed(length);
        }
        else
        {
            _window.NoteDelivered(length);
        }
        return true;
    }

    /// <summary>收尾两条接收管道。调用前通道状态必须已经在锁里改成不再接收的值。</summary>
    /// <param name="failure">
    /// 不是对端收的尾（连接断了、被释放了）时的原因：读的一方拿到它，而不是一个像 EOF 的「读完」。
    /// 已经因为对端的 EOF 收过尾的管道不受影响 —— 第二次完成是空操作。
    /// </param>
    public void Complete(Exception? failure = null)
    {
        _stdoutPipe.Writer.Complete(failure);
        _stderrPipe?.Writer.Complete(failure);
    }

    /// <summary>丢弃 stderr 时留住的末尾上限（<see cref="SshChannelOptions.DiscardedStderrTailBytes"/>）；0 不留。</summary>
    private readonly int _stderrTailLimit;

    private readonly Lock _stderrTailLock = new();
    private byte[]? _stderrTail;
    private int _stderrTailLength;

    /// <summary>把丢弃的 stderr 记进末尾缓冲：只留最后 <see cref="_stderrTailLimit"/> 字节。</summary>
    private void KeepStderrTail(ReadOnlySequence<byte> data)
    {
        lock (_stderrTailLock)
        {
            _stderrTail ??= new byte[_stderrTailLimit];
            ReadOnlySequence<byte> incoming = data.Length > _stderrTailLimit ? data.Slice(data.Length - _stderrTailLimit) : data;
            int keep = Math.Min(_stderrTailLength, _stderrTailLimit - (int)incoming.Length);
            _stderrTail.AsSpan(_stderrTailLength - keep, keep).CopyTo(_stderrTail);
            incoming.CopyTo(_stderrTail.AsSpan(keep));
            _stderrTailLength = keep + (int)incoming.Length;
        }
    }

    /// <summary>丢弃的 stderr 的末尾，按 UTF-8 解、照对端文本清洗过；没留或没有时为 <see langword="null"/>。</summary>
    public string? DiscardedStderrTail
    {
        get
        {
            lock (_stderrTailLock)
            {
                if (_stderrTail is null || _stderrTailLength == 0)
                {
                    return null;
                }
                string text = System.Text.Encoding.UTF8.GetString(_stderrTail, 0, _stderrTailLength);
                return Diagnostics.PeerText.SanitizeTail(text) is { Length: > 0 } clean ? clean : null;
            }
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
    /// 收尾一侧（<c>SshChannel.FinishClose</c>、<c>SshChannel.OnEof</c>）总是先在锁里改状态、
    /// 再完成管道，所以锁里看到「还开着」时，管道一定还没被完成。
    /// </remarks>
    private bool TryDeliver(PipeWriter writer, WindowedPipeReader reader, ReadOnlySequence<byte> data)
    {
        lock (_deliveryGate)
        {
            // CLOSE 之后收到的是在途数据 —— 丢弃，且**不报错**（velashell-docs/zh/ssh/spec/05 §1 规则 4）。
            // EOF 之后再来数据是对端的错，但为它断开整条会话不值得 —— 丢弃即可。
            if (!_channel.AcceptsDataWhileLocked)
            {
                return false;
            }

            // 读的一方在收尾：它清点完就完成读端，这时再写进去的会随读端一起丢掉、没人回补。
            if (reader.IsAbandoned)
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
}
