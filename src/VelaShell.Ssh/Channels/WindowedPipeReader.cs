// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/05-connection.md §3.2、§4.3

using System.Buffers;
using System.IO.Pipelines;

namespace VelaShell.Ssh.Channels;

/// <summary>一条会把「消费了多少」报上去的 <see cref="PipeReader"/>。</summary>
/// <remarks>
/// <para>
/// 这是「背压是结构性的」落到代码上的那一处：
/// <b>SSH 窗口在 <see cref="AdvanceTo(SequencePosition)"/> 之后才回补，
/// 不是在报文到达时。</b>
/// </para>
/// <para>
/// 消费者不读，窗口就不补，对端自然停下来 —— 不需要额外的限流器，
/// 也不会出现「内部队列无限涨」。换句话说，
/// <b>调用方读取的速度就是对端发送的速度</b>，中间没有可以无限膨胀的缓冲。
/// </para>
/// <para>
/// 回调是同步的、必须很快 —— 它在调用方的 <c>AdvanceTo</c> 里跑。
/// 实现只记账并唤醒一个泵，真正的发送在别处。
/// </para>
/// <para>
/// <paramref name="deliveryGate"/> 是写入方往管道里写时持有的那把锁：读的一方收尾时在它里面标记
/// <see cref="IsAbandoned"/>，写入方在同一把锁里看它（见 <see cref="ReleaseUnread"/>）。
/// </para>
/// </remarks>
internal sealed class WindowedPipeReader(PipeReader inner, Action<long> onConsumed, Lock deliveryGate) : PipeReader
{
    private ReadOnlySequence<byte> _currentBuffer;
    private bool _hasBuffer;

    /// <summary>读的一方收尾了：写入方不再往管道里写，到的数据丢弃并自己回补窗口。在 <c>deliveryGate</c> 里读写。</summary>
    internal bool IsAbandoned { get; private set; }

    /// <inheritdoc />
    public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        ReadResult result = await inner.ReadAsync(cancellationToken).ConfigureAwait(false);
        _currentBuffer = result.Buffer;
        _hasBuffer = true;
        return result;
    }

    /// <inheritdoc />
    public override bool TryRead(out ReadResult result)
    {
        if (!inner.TryRead(out result))
        {
            return false;
        }
        _currentBuffer = result.Buffer;
        _hasBuffer = true;
        return true;
    }

    /// <inheritdoc />
    public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);

    /// <inheritdoc />
    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        long consumedBytes = 0;
        if (_hasBuffer)
        {
            // Slice(start, position) 给出的就是「从头到这个位置」的那一段。
            consumedBytes = _currentBuffer.Slice(_currentBuffer.Start, consumed).Length;
            _hasBuffer = false;
            _currentBuffer = default;
        }

        // 先把 AdvanceTo 交给内层 —— 回调里出任何岔子都不该让管道处于半推进状态。
        inner.AdvanceTo(consumed, examined);

        if (consumedBytes > 0)
        {
            onConsumed(consumedBytes);
        }
    }

    /// <inheritdoc />
    public override void CancelPendingRead() => inner.CancelPendingRead();

    /// <inheritdoc />
    public override void Complete(Exception? exception = null)
    {
        ReleaseUnread();
        inner.Complete(exception);
    }

    /// <inheritdoc />
    public override ValueTask CompleteAsync(Exception? exception = null)
    {
        ReleaseUnread();
        return inner.CompleteAsync(exception);
    }

    /// <summary>消费者不读了：把管道里还没读的字节当作「已消费」报上去。</summary>
    /// <remarks>
    /// <para>
    /// 消费者提前收尾（只关心开头几行，读完就 <c>Complete</c>）时，
    /// 管道里剩下的字节永远不会被 <see cref="AdvanceTo(SequencePosition, SequencePosition)"/> 消费 ——
    /// 不把它们报上去，那部分窗口就永远不会回补，对端停在一个再也不会涨的窗口上，
    /// 而通道的关闭流程（对端的 EOF / exit-status）也就一起卡住了。
    /// </para>
    /// <para>
    /// ⚠️ <b>先叫写入方停手，再清点。</b>曾经直接清点：清点完到完成内层读端之间，接收循环照样往管道里写，
    /// 那几包随读端完成一起丢掉、没人回补。调度慢的机器上这段空隙里能进来半个窗口以上，
    /// 攒着的回补就再也够不到阈值，对端停在零窗口上（CI 上偶发 30 秒超时）。
    /// 在写入方的锁里标记之后，后到的数据走写入方「丢弃并回补」那条路，这里清点到的就是全部。
    /// </para>
    /// </remarks>
    private void ReleaseUnread()
    {
        lock (deliveryGate)
        {
            IsAbandoned = true;
        }

        long unread = 0;
        try
        {
            if (_hasBuffer)
            {
                inner.AdvanceTo(_currentBuffer.Start);
                _hasBuffer = false;
                _currentBuffer = default;
            }

            if (inner.TryRead(out ReadResult rest))
            {
                unread = rest.Buffer.Length;
                inner.AdvanceTo(rest.Buffer.End);
            }
        }
        catch (Exception)
        {
            // 管道已经收尾或正被并发读 —— 没有什么可以放的了。
        }

        if (unread > 0)
        {
            onConsumed(unread);
        }
    }
}
