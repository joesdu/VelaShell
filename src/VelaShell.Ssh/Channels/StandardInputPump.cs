// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §5.2  CHANNEL_DATA 受对端窗口与 maximum packet size 约束
//   RFC 4254 §5.3  CHANNEL_EOF
//   行为规格:      velashell-docs/zh/ssh/spec/05-connection.md §3、§4.3、§4.4

using System.Buffers;
using System.IO.Pipelines;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Channels;

/// <summary>一条通道的发送侧：对端给的发送窗口，以及把 stdin 管道里的内容按窗口与包上限切成 <c>CHANNEL_DATA</c> 发出去的泵。</summary>
/// <remarks>
/// <para>
/// 从 <see cref="SshChannel"/> 拆出来的协作者。泵什么时候起、收尾时由谁完成 stdin 的读端，仍由通道在它的状态锁里决定
/// （<c>SshChannel.StartPumps</c> / <c>SshChannel.FinishClose</c>）；泵起来之后读端归泵，退出时由泵自己完成。
/// </para>
/// <para>
/// 每一帧的「还发不发」仍是通道的 <c>MayStillSend</c>，在会话的入队锁里与入队同一时刻判定（见 <see cref="ISshChannelHost.SendIfAsync"/>）。
/// 这里不持有通道的锁去入队。
/// </para>
/// </remarks>
internal sealed class StandardInputPump
{
    private readonly SshChannel _channel;
    private readonly Pipe _pipe = new(new PipeOptions(useSynchronizationContext: false));
    private readonly SshWindow _sendWindow = new(0);   // 真正的值要等 OPEN_CONFIRMATION
    private readonly AsyncGate _sendWindowGate = new();

    /// <summary>泵已经交给会话发送的字节数（<see cref="SshChannel.WaitStandardInputSentAsync"/> 用）。</summary>
    private long _sentBytes;

    /// <summary>泵每交出一段、或者泵退出时响一次。</summary>
    private readonly AsyncGate _progressGate = new();

    /// <summary>泵已经退出（之后不会再有进展）。</summary>
    private volatile bool _exited;

    /// <summary>发数据帧用的放行判定：同通道的 <c>MayStillSend</c>，另把结论记进 <see cref="_dataAdmitted"/>。</summary>
    /// <remarks>数据帧只有这个泵一个发送方、一帧一帧地发，所以一个字段就够记，不必每帧分配一个闭包。</remarks>
    private readonly Func<bool> _admitData;

    /// <summary>上一个数据帧有没有放行（<see cref="_admitData"/> 写，泵在那一帧发完之后读）。</summary>
    private bool _dataAdmitted;

    private long _bytesSent;

    internal StandardInputPump(SshChannel channel)
    {
        _channel = channel;
        _admitData = () => _dataAdmitted = channel.MayStillSend();
    }

    /// <summary>stdin 管道的写端（包在 <see cref="StandardInputWriter"/> 里交给使用者）。</summary>
    public PipeWriter Writer => _pipe.Writer;

    /// <summary>经这条通道发出的应用数据字节数。</summary>
    public long BytesSent => Interlocked.Read(ref _bytesSent);

    /// <summary>当前的发送窗口剩余（诊断用）。</summary>
    public uint SendWindowRemaining => _sendWindow.Remaining;

    /// <summary>已经交给会话发送的累计字节数。</summary>
    public long SentBytes => Interlocked.Read(ref _sentBytes);

    /// <summary>泵已经退出（看标记而不是看泵的 Task：泵在 finally 里响铃时，它的 Task 还没完成）。</summary>
    public bool HasExited => _exited;

    /// <summary>取一张「有进展」的票（先取票、再查条件、最后等票，见 <see cref="AsyncGate"/>）。</summary>
    public Task NextProgress() => _progressGate.NextChange();

    /// <summary>对端确认通道时给的初始窗口。</summary>
    public void OpenWindow(uint initialWindow) => _sendWindow.Add(initialWindow);

    /// <summary>收到 <c>CHANNEL_WINDOW_ADJUST</c>。</summary>
    /// <returns>加完有没有溢出 <c>uint32</c>。溢出是协议违规。</returns>
    public bool OnWindowAdjust(uint bytes)
    {
        if (!_sendWindow.Add(bytes))
        {
            return false;
        }
        _sendWindowGate.Signal();
        return true;
    }

    /// <summary>叫醒等发送窗口的泵：去看窗口，或者去看通道关了没有。</summary>
    public void Wake() => _sendWindowGate.Signal();

    /// <summary>完成写端（<see cref="SshChannel.SendEofAsync"/>：冲干净之后由泵发 EOF）。</summary>
    public ValueTask CompleteWriterAsync() => _pipe.Writer.CompleteAsync();

    /// <summary>泵从没起来时，由通道收尾替它完成读端（见 <c>SshChannel.FinishClose</c>）。</summary>
    public void CompleteReaderForUnstartedPump() => _pipe.Reader.Complete();

    /// <summary>stdin 泵：读管道、等窗口、按包上限切块发出；写端完成之后冲干净再发 EOF。</summary>
    public async Task PumpAsync(CancellationToken cancellationToken)
    {
        PipeReader reader = _pipe.Reader;
        Exception? failure = null;

        try
        {
            while (true)
            {
                ReadResult read = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = read.Buffer;

                while (!buffer.IsEmpty)
                {
                    // 对端宣告的 max packet 是 0：它不收任何数据。曾经算出的块是 0、被当成「通道关了」，泵悄悄退出 ——
                    // stdin 静默失效，之后连 EOF 也不再发。现在照实失败：写入方的 FlushAsync 拿到这个异常。
                    if (_channel.RemoteMaxPacketBytes == 0)
                    {
                        throw new SshChannelException(Diagnostics.SshFailureReason.ProtocolError,
                            $"对端给通道 {_channel.LocalId} 宣告的 max packet 是 0 —— 它不收任何数据，写进 stdin 的内容发不出去。")
                        {
                            FailureKind = SshChannelFailureKind.ZeroMaxPacket,
                        };
                    }

                    // 单个数据段**必须**不超过对端宣告的上限 —— 超了对端会断连。
                    int chunk = (int)Math.Min(buffer.Length, _channel.RemoteMaxPacketBytes);

                    // 窗口不够就等回补。这不是错误，是背压。
                    chunk = await WaitForSendWindowAsync(chunk, cancellationToken).ConfigureAwait(false);
                    if (chunk == 0)
                    {
                        return;   // 通道关了
                    }

                    if (!await SendDataAsync(buffer.Slice(0, chunk), cancellationToken).ConfigureAwait(false))
                    {
                        // CLOSE 已经入队，这一帧没有发出去：通道关了。不能计进「已发」——
                        // 曾经照计，FlushAsync 对被丢掉的数据报了成功。
                        return;
                    }
                    buffer = buffer.Slice(chunk);

                    Interlocked.Add(ref _sentBytes, chunk);
                    _progressGate.Signal();
                }

                reader.AdvanceTo(read.Buffer.End);

                if (read.IsCompleted)
                {
                    // writer 完成了：调用方直接完成了 StandardInput（PipeWriter 表达「写完了」的惯用法），
                    // 或者 SendEofAsync 完成了它。两种都是 EOF —— 数据已经冲干净，现在发。
                    // 不发的话，远端等着读完的程序（cat、sort）会一直挂着。通道在关时状态推进不了，什么也不发。
                    _ = _channel.TryMarkLocalEof();
                    await _channel.SendOwedEofAsync(cancellationToken).ConfigureAwait(false);
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
            // reader 归泵所有，由泵自己收尾 —— 见 SshChannel.FinishClose 里的说明。
            //
            // ⚠️ **每一条出口都要走到这里**，包括循环中间那个「通道关了」的 return：
            //    那时泵手上还捏着一段没 AdvanceTo 的数据，而写入方的 FlushAsync 正卡在背压上。
            //    FinishClose 完成的是 writer —— **那放不出挂起的 FlushAsync**，只有 reader
            //    前进或完成才行。曾经这条 return 跳过了收尾，写入方就永远等下去。
            await reader.CompleteAsync(failure).ConfigureAwait(false);

            // 等着「发完」的人不会再等到进展了 —— 叫醒他们去看结局。
            _exited = true;
            _progressGate.Signal();
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

            if (_channel.IsClosedOrClosing())
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

    /// <summary>发一个 <c>CHANNEL_DATA</c>。</summary>
    /// <returns>发出去了（入队了）为 <see langword="true"/>；CLOSE 已经入队、这一帧不再发时为 <see langword="false"/>。</returns>
    private async ValueTask<bool> SendDataAsync(ReadOnlySequence<byte> data, CancellationToken cancellationToken)
    {
        // 缓冲从池里租：这是上传路径上每一块都要走的一步，曾经每块新分配一个数组（最大一个 max packet），
        // 高速上传时就是每秒几千次分配。发送方的 await 返回时发送泵已经不再引用它
        // （见 ISshChannelHost.SendBorrowedIfAsync），所以 finally 里就可以还回去。
        int length = (int)data.Length;
        byte[] rented = ArrayPool<byte>.Shared.Rent(9 + length);
        try
        {
            rented[0] = (byte)SshMessageNumber.ChannelData;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(rented.AsSpan(1), _channel.RemoteId);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(rented.AsSpan(5), (uint)length);
            data.CopyTo(rented.AsSpan(9));

            _dataAdmitted = false;
            await _channel.Host.SendBorrowedIfAsync(rented.AsMemory(0, 9 + length), _admitData, _channel.IsInteractive, cancellationToken)
                .ConfigureAwait(false);
            if (_dataAdmitted)
            {
                Interlocked.Add(ref _bytesSent, length);
            }
            return _dataAdmitted;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
