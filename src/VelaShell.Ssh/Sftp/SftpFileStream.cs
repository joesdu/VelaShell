// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-ietf-secsh-filexfer-02 §6.4(READ)、§6.5(WRITE)
//   行为规格:                    velashell-docs/zh/ssh/spec/06-sftp.md §4.3、§六

using System.Buffers;
using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Ssh.Sftp;

/// <summary>写入的并发方式。</summary>
public enum SftpWriteMode
{
    /// <summary>
    /// 流水线：同时有多个在途 <c>WRITE</c>，吞吐高。
    /// </summary>
    /// <remarks>
    /// 代价是中途断开时文件里可能留有空洞 —— 用
    /// <see cref="SftpFileStream.DurableLength"/> 拿到精确的续传点。
    /// </remarks>
    Pipelined,

    /// <summary>
    /// 一次只有一个在途 <c>WRITE</c>。
    /// </summary>
    /// <remarks>
    /// 牺牲吞吐换「文件长度就是可信长度」—— 任何时刻文件都是一个完整前缀。
    /// 写配置文件这类场景用它。
    /// </remarks>
    Sequential,
}

/// <summary>一个远端文件的读写流。</summary>
/// <remarks>
/// SFTP 的读写都带<b>绝对偏移</b>，服务端不维护文件位置 ——
/// 所以并发乱序读写是天然可行的，这也正是 <see cref="DurableLength"/> 存在的原因。
/// </remarks>
public sealed class SftpFileStream : Stream
{
    private readonly SftpRequestPipeline _pipeline;
    private readonly byte[] _handle;
    private readonly int _blockSize;
    private readonly SemaphoreSlim _writeSlots;
    private readonly AckedRangeSet _acked = new();
    private readonly List<Task> _pendingWrites = [];
    private readonly Lock _pendingLock = new();

    private long _position;
    private long _knownLength;
    private Exception? _writeFault;
    private bool _closed;

    // ---- 流水线写的攒块：不足一块的尾巴留在本端，凑满一块再发（velashell-docs/zh/ssh/spec/06 §5.2）----

    /// <summary>攒块缓冲（租来的，至少一块大）；还没攒过时为 <see langword="null"/>。</summary>
    private byte[]? _coalesce;

    /// <summary>攒块缓冲里有多少字节。</summary>
    private int _coalesceLength;

    /// <summary>攒块缓冲第一个字节在文件里的偏移。</summary>
    private long _coalesceOffset;

    // ---- 顺序读的预读（velashell-docs/zh/ssh/spec/06 §5.5）----

    /// <summary>一个已经发出、按偏移排队的预读请求。</summary>
    private sealed record ReadAheadBlock(long Offset, int Length, Task<SftpResponse> Response);

    private readonly Queue<ReadAheadBlock> _readAhead = new();
    private ReadAheadBlock? _gapRead;
    private readonly int _maxReadAhead;
    private readonly bool _readAheadPastLength;

    // 长度未知时，每个流同时限制请求数（含 EOF 探测）与请求的总字节数。
    private const int UnknownLengthMaxRequests = 8;
    private const int UnknownLengthMaxBytes = 1024 * 1024;

    /// <summary>当前的预读窗口（慢启动：从 1 起，每交出一整块翻一倍）。</summary>
    private int _readAheadDepth = 1;

    /// <summary>下一个预读请求的偏移。</summary>
    private long _readAheadNext;

    /// <summary>
    /// 每个预读请求要多长：起初是块大小；服务端在已知长度之内也读不满一块时，降到它实际给的长度（不低于 <see cref="MinReadAheadLength"/>）。
    /// </summary>
    private int _readAheadLength;

    /// <summary>学到的请求长度的下限：偶尔一次短得离谱的短读，不该把之后的每个请求都压成几个字节。</summary>
    private const int MinReadAheadLength = 4096;

    /// <summary>取了一半的块：调用方的缓冲比块小时，剩下的留到下一次读。</summary>
    private SftpResponse? _partial;
    private ReadOnlySequence<byte> _partialData;

    /// <summary><see cref="_partialData"/> 第一个字节在文件里的偏移。</summary>
    private long _partialOffset;

    internal SftpFileStream(
        SftpRequestPipeline pipeline,
        byte[] handle,
        string path,
        bool canRead,
        bool canWrite,
        long initialLength,
        int blockSize,
        SftpWriteMode writeMode,
        int maxInFlightWrites,
        int maxReadAhead,
        bool lengthKnown,
        bool isRegularFile)
    {
        _pipeline = pipeline;
        _handle = handle;
        Path = path;
        _blockSize = blockSize;
        _readAheadLength = blockSize;
        _knownLength = initialLength;
        _readable = canRead;
        _writable = canWrite;
        WriteMode = writeMode;
        LengthKnown = lengthKnown;
        _readAheadPastLength = !lengthKnown && isRegularFile;
        _maxReadAhead = lengthKnown ? maxReadAhead : 1;
        if (_readAheadPastLength)
        {
            _maxReadAhead = Math.Min(maxReadAhead, Math.Min(UnknownLengthMaxRequests, UnknownLengthMaxBytes / blockSize));
        }

        int slots = writeMode == SftpWriteMode.Sequential ? 1 : Math.Max(1, maxInFlightWrites);
        _writeSlots = new SemaphoreSlim(slots, slots);
    }

    /// <summary>远端路径。</summary>
    public override string ToString() => Path;

    /// <summary>远端路径。</summary>
    public string Path { get; }

    /// <summary>写入的并发方式。</summary>
    public SftpWriteMode WriteMode { get; }

    /// <summary>
    /// 从 0 开始<b>连续</b>已确认落盘的字节数。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这不是文件长度。</b>流水线写入时应答顺序不保证，
    /// 服务端报告的文件长度只是「已确认的<b>最高</b>偏移」，它前面可能有空洞。
    /// </para>
    /// <para>
    /// 断点续传从这个数续，<b>精确，不用猜</b> ——
    /// 不必像常见做法那样从文件长度盲退一个完整的在途窗口（比如 2 MiB）。
    /// </para>
    /// </remarks>
    public long DurableLength => _acked.DurableLength;

    /// <summary>当前有几段不连续的已确认区间（诊断用；顺序写时恒为 1）。</summary>
    public int AckedRangeCount => _acked.RangeCount;

    private readonly bool _readable;
    private readonly bool _writable;

    /// <inheritdoc />
    /// <remarks>关闭之后为 <see langword="false"/>（<see cref="Stream"/> 的约定）。</remarks>
    public override bool CanRead => _readable && !_closed;

    /// <inheritdoc />
    /// <remarks>关闭之后为 <see langword="false"/>（<see cref="Stream"/> 的约定）。</remarks>
    public override bool CanWrite => _writable && !_closed;

    /// <inheritdoc />
    public override bool CanSeek => true;

    /// <inheritdoc />
    /// <remarks>
    /// 打开时向服务端问过的长度，之后随读写推进。打开时没问到（服务端拒了 FSTAT）就从 0 起算，
    /// 那时 <see cref="LengthKnown"/> 为假。
    /// </remarks>
    public override long Length => _knownLength;

    /// <summary>打开时拿到了文件的真实长度（或者是截断打开的，长度就是 0）。</summary>
    internal bool LengthKnown { get; }

    /// <summary>句柄关掉之后（不论 CLOSE 成没成）调一次：还文件系统的句柄额度。</summary>
    internal Action? OnHandleClosed { get; init; }

    /// <summary>关闭时等在途写入确认、等 <c>CLOSE</c> 应答各最多多久（见 <see cref="SftpOptions.CloseTimeout"/>）。</summary>
    internal TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            if (value != _position)
            {
                ResetReadAhead();
                _position = value;
            }
        }
    }

    // ------------------------------------------------------------ 读

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>顺序读带预读</b>（velashell-docs/zh/ssh/spec/06 §5.5）：在途的 <c>READ</c> 按偏移排成一队，
    /// 逐块交给调用方。曾经一次只发一个、等它回来再发下一个 —— 吞吐被钉死在「块大小 ÷ RTT」，
    /// 100 ms 的链路上下载只有几百 KB/s，而写入那一侧早就是流水线了。
    /// </para>
    /// <para>
    /// 取消只取消这一次等待：预读请求属于流，下一次读接着用。
    /// </para>
    /// </remarks>
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (!CanRead)
        {
            throw new NotSupportedException("这个流是以只写方式打开的。");
        }

        if (buffer.IsEmpty)
        {
            return 0;
        }

        cancellationToken.ThrowIfCancellationRequested();
        // 读写同一个句柄：攒着没发的写要先发，读到的才是写过之后的内容。
        await FlushCoalescedAsync(cancellationToken).ConfigureAwait(false);

        // ① 上一块还没交完。
        if (_partial is not null)
        {
            if (_partialOffset == _position)
            {
                return TakeFromPartial(buffer.Span);
            }
            DropPartial();
        }

        ReadAheadBlock head;
        if (_readAhead.TryPeek(out ReadAheadBlock? next) && next.Offset > _position)
        {
            // 短读留下的缺口。一次只补一段，抵消已交出的那一块，即使学到的块很小也不超出窗口。
            head = _gapRead ??= SendRead(_position, (int)Math.Min(next.Offset - _position, _readAheadLength));
        }
        else
        {
            if (_readAhead.Count == 0)
            {
                _readAheadNext = _position;
            }
            FillReadAhead();
            head = _readAhead.Peek();
        }

        SftpResponse response = await head.Response.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_gapRead is null)
        {
            _readAhead.Dequeue();
        }
        _gapRead = null;

        ReadOnlySequence<byte> data;
        try
        {
            if (response.TryGetStatus(out SftpStatusCode code, out string message))
            {
                // EOF 不是错误 —— 它就是「读完了」。
                if (code == SftpStatusCode.EndOfFile)
                {
                    response.Dispose();
                    DiscardReadAhead();
                    return 0;
                }
                throw new SftpException(code, message, Path, SftpOperation.Read);
            }

            if (response.Type != SftpMessageType.Data)
            {
                throw new SshProtocolException(
                    SshPhase.Open, $"读取时期望 SSH_FXP_DATA，收到 {response.Type}。");
            }

            response.ExpectType(Path, SftpOperation.Read, SftpMessageType.Data);
            data = SftpWire.ReadData(response.Payload, head.Length);
            if (data.Length > long.MaxValue - head.Offset)
            {
                throw new IOException("远端数据的结束偏移超出流能表示的范围。");
            }
        }
        catch (Exception)
        {
            response.Dispose();
            DiscardReadAhead();
            throw;
        }

        if (data.IsEmpty)
        {
            response.Dispose();
            DiscardReadAhead();
            return 0;
        }

        _knownLength = Math.Max(_knownLength, head.Offset + data.Length);

        if (data.Length < head.Length)
        {
            // 服务端可能有未宣告的读上限。普通文件长度未知时也按实际返回的长度试读，是否 EOF 由下一次 READ 决定。
            if (_readAheadPastLength || head.Length <= _knownLength - head.Offset)
            {
                _readAheadLength = Math.Min(_readAheadLength, Math.Max(MinReadAheadLength, (int)data.Length));
            }
        }
        else
        {
            _readAheadDepth = Math.Min(_readAheadDepth * 2, _maxReadAhead);
        }

        _partial = response;
        _partialData = data;
        _partialOffset = head.Offset;
        return TakeFromPartial(buffer.Span);
    }

    /// <summary>把预读队伍补到当前窗口。</summary>
    /// <remarks>
    /// 有长度时仅在界内预发。没有长度时，只对 FSTAT 明确识别的普通文件预发；路径的 STAT 可能已经是另一个文件。
    /// </remarks>
    private void FillReadAhead()
    {
        while (_readAheadNext >= 0 && _readAhead.Count < _readAheadDepth
               && (_readAhead.Count == 0 || _readAheadPastLength || _readAheadNext < _knownLength))
        {
            ReadAheadBlock block = SendRead(_readAheadNext, _readAheadLength);
            _readAhead.Enqueue(block);
            _readAheadNext = _readAheadNext <= long.MaxValue - block.Length ? _readAheadNext + block.Length : -1;
        }
    }

    /// <summary>发一个预读的 <c>READ</c>。不带调用方的令牌：预读请求属于流，不属于这一次读。</summary>
    private ReadAheadBlock SendRead(long offset, int length)
    {
        Task<SftpResponse> response = _pipeline.SendAsync(
            (output, id) => SftpWire.WriteRead(output, id, _handle, (ulong)offset, (uint)length),
            cancellationToken: CancellationToken.None).AsTask();
        return new ReadAheadBlock(offset, length, response);
    }

    private int TakeFromPartial(Span<byte> destination)
    {
        int take = (int)Math.Min(destination.Length, _partialData.Length);
        _partialData.Slice(0, take).CopyTo(destination);
        _partialData = _partialData.Slice(take);
        _partialOffset += take;
        _position += take;

        if (_partialData.IsEmpty)
        {
            DropPartial();
        }
        return take;
    }

    private void DropPartial()
    {
        _partial?.Dispose();
        _partial = null;
        _partialData = default;
    }

    /// <summary>预读整队作废，窗口回到 1。</summary>
    /// <remarks>
    /// 作废的请求不能丢着不管：应答照样会到，到了就释放（载荷是从池里租的）；
    /// 失败的那些把异常看掉，免得变成未观察的任务异常。
    /// </remarks>
    private void DiscardReadAhead()
    {
        if (_gapRead is not null)
        {
            _readAhead.Enqueue(_gapRead);
            _gapRead = null;
        }
        while (_readAhead.TryDequeue(out ReadAheadBlock? block))
        {
            _ = block.Response.ContinueWith(
                static completed =>
                {
                    if (completed.IsCompletedSuccessfully)
                    {
                        completed.Result.Dispose();
                    }
                    else
                    {
                        _ = completed.Exception;
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        _readAheadDepth = 1;
    }

    /// <summary>写入或改长度之后，预读到的内容可能已经过时。</summary>
    private void ResetReadAhead()
    {
        DropPartial();
        DiscardReadAhead();
    }

    /// <summary>从指定偏移读，不动当前位置。</summary>
    /// <returns>实际读到的字节数；<c>0</c> 表示到达文件末尾。</returns>
    /// <remarks>
    /// <b>返回的字节数可能少于请求的</b>，那不是错误 ——
    /// 调用方要循环读直到拿够或返回 0。
    /// </remarks>
    /// <exception cref="NotSupportedException">这个流是以只写方式打开的。</exception>
    public async ValueTask<int> ReadAtAsync(
        long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (!_readable)
        {
            throw new NotSupportedException("这个流是以只写方式打开的。");
        }

        if (buffer.IsEmpty)
        {
            return 0;
        }

        // 攒着没发的写先发（理由同 ReadAsync）。
        await FlushCoalescedAsync(cancellationToken).ConfigureAwait(false);

        // 一次至多读一块：更长的 READ 要么被服务端截短，要么应答超出我们肯收的报文长度。
        // 反正调用方要循环读（见上），这里少给一些不改变语义。
        if (buffer.Length > _blockSize)
        {
            buffer = buffer[.._blockSize];
        }

        int length = buffer.Length;
        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WriteRead(output, id, _handle, (ulong)offset, (uint)length),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (response.TryGetStatus(out SftpStatusCode code, out string message))
        {
            // EOF 不是错误 —— 它就是「读完了」。
            if (code == SftpStatusCode.EndOfFile)
            {
                return 0;
            }
            throw new SftpException(code, message, Path, SftpOperation.Read);
        }

        if (response.Type != SftpMessageType.Data)
        {
            throw new SshProtocolException(
                SshPhase.Open, $"读取时期望 SSH_FXP_DATA，收到 {response.Type}。");
        }

        response.ExpectType(Path, SftpOperation.Read, SftpMessageType.Data);
        return SftpWire.ReadDataInto(response.Payload, buffer.Span);
    }

    /// <summary>不支持 —— 用 <see cref="ReadAsync(Memory{byte}, CancellationToken)"/>。</summary>
    /// <exception cref="NotSupportedException">总是。</exception>
    /// <remarks>
    /// 〔架构原则 1〕<b>异步是唯一形态。</b>同步读要么阻塞一个线程等一个网络往返
    /// （UI 线程上就是界面卡住一个 RTT，线程池上并发一多就把池子饿死），
    /// 要么是「同步包异步」—— 那两种都是假装同步。宁可当场说清楚该用哪个。
    /// </remarks>
    public override int Read(byte[] buffer, int offset, int count) => throw SyncNotSupported(nameof(ReadAsync));

    /// <inheritdoc cref="Read(byte[], int, int)" />
    public override int Read(Span<byte> buffer) => throw SyncNotSupported(nameof(ReadAsync));

    /// <inheritdoc cref="Read(byte[], int, int)" />
    public override int ReadByte() => throw SyncNotSupported(nameof(ReadAsync));

    /// <inheritdoc />
    /// <remarks>
    /// 数组版本<b>必须</b>重写：<see cref="Stream"/> 的默认实现绕到同步的 <c>Read</c> 上，
    /// 而本类的同步读直接抛 —— 调用方写的明明是 <c>await ReadAsync(buffer, 0, n)</c>，却拿到「只支持异步」。
    /// </remarks>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    /// <remarks>
    /// 老式的 Begin/End 也接到异步读上。基类的实现绕到同步 <see cref="Read(byte[], int, int)"/>，而那个直接抛。
    /// </remarks>
    public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) =>
        TaskToAsyncResult.Begin(ReadAsync(buffer, offset, count, CancellationToken.None), callback, state);

    /// <inheritdoc />
    public override int EndRead(IAsyncResult asyncResult) => TaskToAsyncResult.End<int>(asyncResult);

    private NotSupportedException SyncNotSupported(string asyncAlternative) =>
        new($"{nameof(SftpFileStream)}（{Path}）只支持异步操作，请改用 {asyncAlternative}。" +
            "同步调用只能靠阻塞线程等网络往返来实现，本库不提供这种形态。");

    // ------------------------------------------------------------ 写

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>流水线模式下按整块发</b>：不足一块的尾巴先留在本端，与后面的写凑满一块再发；
    /// <see cref="FlushAsync(CancellationToken)"/>、释放、读、改长度、按偏移写之前都会先把它发出去。
    /// </para>
    /// <para>
    /// 在途写入按<b>请求个数</b>限流，每个 <c>WRITE</c> 不论大小都占一个名额。曾经每次调用各自按块切：
    /// 调用方每次写 256 KiB、而服务端的块是 255 KiB 时，每次都切成「一大一小」两个请求，
    /// 在途的字节数因此少了一半，高 RTT 链路上的上传吞吐跟着掉一半。
    /// </para>
    /// <para>顺序模式不攒：它的承诺是每次写返回时数据已经确认落盘。</para>
    /// </remarks>
    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (!CanWrite)
        {
            throw new NotSupportedException("这个流是以只读方式打开的。");
        }

        if (WriteMode == SftpWriteMode.Sequential)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int chunk = Math.Min(buffer.Length - offset, _blockSize);
                await WriteBlockAtAsync(_position, buffer.Slice(offset, chunk), cancellationToken).ConfigureAwait(false);
                _position += chunk;
                offset += chunk;
            }
            return;
        }

        ThrowIfWriteFaulted();

        while (!buffer.IsEmpty)
        {
            // 攒着的尾巴接不上当前位置（中间 Seek 过）：先把它发出去。
            if (_coalesceLength > 0 && _coalesceOffset + _coalesceLength != _position)
            {
                await FlushCoalescedAsync(cancellationToken).ConfigureAwait(false);
            }

            // 没有攒着的、手上又有一整块：直接发，不经攒块缓冲。
            if (_coalesceLength == 0 && buffer.Length >= _blockSize)
            {
                await WriteBlockAtAsync(_position, buffer[.._blockSize], cancellationToken).ConfigureAwait(false);
                _position += _blockSize;
                buffer = buffer[_blockSize..];
                continue;
            }

            _coalesce ??= ArrayPool<byte>.Shared.Rent(_blockSize);
            if (_coalesceLength == 0)
            {
                _coalesceOffset = _position;
            }

            int take = Math.Min(_blockSize - _coalesceLength, buffer.Length);
            buffer[..take].CopyTo(_coalesce.AsMemory(_coalesceLength));
            _coalesceLength += take;
            _position += take;
            buffer = buffer[take..];
            _knownLength = Math.Max(_knownLength, _coalesceOffset + _coalesceLength);

            if (_coalesceLength == _blockSize)
            {
                await FlushCoalescedAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>把攒着的尾巴作为一个 <c>WRITE</c> 发出去（不等确认）。</summary>
    /// <remarks>交出去之后才清空：没交出去（等名额时被取消）的话数据还在，下一次照样发。</remarks>
    private async ValueTask FlushCoalescedAsync(CancellationToken cancellationToken)
    {
        if (_coalesceLength == 0)
        {
            return;
        }

        await WriteBlockAtAsync(_coalesceOffset, _coalesce.AsMemory(0, _coalesceLength), cancellationToken)
            .ConfigureAwait(false);
        _coalesceLength = 0;
    }

    /// <summary>往指定偏移写，不动当前位置。</summary>
    /// <remarks>
    /// 流水线模式下这个方法<b>在应答回来之前就返回</b> ——
    /// 真正确认落盘要看 <see cref="DurableLength"/>，或者 <see cref="FlushAsync"/> 之后。
    /// </remarks>
    /// <exception cref="NotSupportedException">这个流是以只读方式打开的。</exception>
    public async ValueTask WriteAtAsync(
        long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        // 在本地就拒掉：曾经发出去让服务端拒，那个失败记成写入故障 —— 之后关闭这个只读流会抛「传输中断」。
        if (!_writable)
        {
            throw new NotSupportedException("这个流是以只读方式打开的。");
        }

        // 攒着的尾巴先发：它与这次写可能重叠，服务端按到达顺序处理，先写的要先到。
        await FlushCoalescedAsync(cancellationToken).ConfigureAwait(false);

        // 按块切开：一个比服务端 max-write-length 还长的 WRITE 会被拒 ——
        // OpenSSH 收到超长报文直接断开 SFTP 会话，连同别的在途请求一起。
        while (data.Length > _blockSize)
        {
            await WriteBlockAtAsync(offset, data[.._blockSize], cancellationToken).ConfigureAwait(false);
            offset += _blockSize;
            data = data[_blockSize..];
        }

        await WriteBlockAtAsync(offset, data, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteBlockAtAsync(
        long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ThrowIfWriteFaulted();

        if (data.IsEmpty)
        {
            return;
        }

        // 读写同一个句柄时，预读到的内容可能正好被这一次写盖掉。
        ResetReadAhead();

        // 在途写的数量就是背压。满了就等，不报错。调用方的令牌只管「等写槽」这一段。
        await _writeSlots.WaitAsync(cancellationToken).ConfigureAwait(false);

        // 等写槽的时候，前面的写可能已经失败了：别再往一条坏掉的流上堆请求。
        if (Volatile.Read(ref _writeFault) is not null)
        {
            _writeSlots.Release();
            ThrowIfWriteFaulted();
        }

        // 调用方的缓冲在我们返回之后就可能被复用 —— 必须先复制。
        byte[] rented = ArrayPool<byte>.Shared.Rent(data.Length);
        data.CopyTo(rented);

        // 〔velashell-docs/zh/ssh/spec/06 §6.4〕
        // ⚠️ 入队的 WRITE 不带这一次 WriteAsync 的令牌：WriteAsync 返回时它还在路上，
        //    令牌之后被取消的话，已经发出的 WRITE 照样落盘，本端却不再记账（DurableLength 偏小），
        //    流还被标成写入故障，之后 Flush / 关闭抛「传输中断，从 N 续传」而不是取消。
        //    要打断等待，用 FlushAsync(ct)。它只会以应答或流水线收工（通道断了会把在途请求一次性收尾）结束。
        Task write = SendWriteAsync(offset, rented, data.Length);

        lock (_pendingLock)
        {
            _pendingWrites.RemoveAll(static t => t.IsCompleted);
            _pendingWrites.Add(write);
        }

        _knownLength = Math.Max(_knownLength, offset + data.Length);

        if (WriteMode == SftpWriteMode.Sequential)
        {
            // 顺序模式的承诺是「任何时刻文件都是一个完整前缀」——
            // 那就必须等这一块确认了才返回。取消只是不再等，这一块照样会被确认、记账。
            await write.WaitAsync(cancellationToken).ConfigureAwait(false);
            ThrowIfWriteFaulted();
        }
    }

    private async Task SendWriteAsync(long offset, byte[] rented, int length)
    {
        try
        {
            using SftpResponse response = await _pipeline.SendAsync(
                (output, id) => SftpWire.WriteWrite(output, id, _handle, (ulong)offset, rented.AsSpan(0, length)),
                cancellationToken: CancellationToken.None).ConfigureAwait(false);

            response.ThrowIfError(Path, SftpOperation.Write, SftpMessageType.Status);

            // 确认了才并入 —— DurableLength 的可信度全靠这一行的位置。
            _acked.Add(offset, length);
        }
        catch (Exception ex)
        {
            // 记下第一个错误。后续的 WriteAtAsync 会立刻抛，
            // 而不是继续往一条已经坏掉的流上堆请求；FlushAsync / 关闭由它报出带精确续传点的中断。
            //
            // ⚠️ 不再往外抛：这个任务没人逐个 await（在途写的清单只在 FlushAsync 里一起等，中途还会摘掉做完的），
            //    让它以异常结束就是一个未观察的任务异常 —— 断线时几十个写同时失败，宿主的崩溃日志里全是它们。
            //    错误已经在 _writeFault 上了，这里交出去的只是「这一块结束了」。
            Interlocked.CompareExchange(ref _writeFault, ex, null);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
            _writeSlots.Release();
        }
    }

    /// <summary>不支持 —— 用 <see cref="WriteAsync(ReadOnlyMemory{byte}, CancellationToken)"/>。</summary>
    /// <exception cref="NotSupportedException">总是。理由见 <see cref="Read(byte[], int, int)"/>。</exception>
    public override void Write(byte[] buffer, int offset, int count) => throw SyncNotSupported(nameof(WriteAsync));

    /// <inheritdoc cref="Write(byte[], int, int)" />
    public override void Write(ReadOnlySpan<byte> buffer) => throw SyncNotSupported(nameof(WriteAsync));

    /// <inheritdoc cref="Write(byte[], int, int)" />
    public override void WriteByte(byte value) => throw SyncNotSupported(nameof(WriteAsync));

    /// <inheritdoc />
    /// <remarks>理由同数组版本的 <see cref="ReadAsync(byte[], int, int, CancellationToken)"/>。</remarks>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    /// <remarks>同 <see cref="BeginRead"/>：接到异步写上。</remarks>
    public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) =>
        TaskToAsyncResult.Begin(WriteAsync(buffer, offset, count, CancellationToken.None), callback, state);

    /// <inheritdoc />
    public override void EndWrite(IAsyncResult asyncResult) => TaskToAsyncResult.End(asyncResult);

    /// <summary>等所有在途写入都确认。</summary>
    /// <exception cref="SftpTransferInterruptedException">
    /// 有写入失败。异常里带着<b>精确</b>的 <c>DurableLength</c>。
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> 取消了：只是不再等，在途的写入照样会被确认、记账。
    /// </exception>
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        // 不足一块的尾巴还在本端：先发出去，再一起等确认。
        await FlushCoalescedAsync(cancellationToken).ConfigureAwait(false);

        Task[] pending;
        lock (_pendingLock)
        {
            pending = [.. _pendingWrites];
        }

        // 在途的写不会以异常结束（失败记在 _writeFault 上，见 SendWriteAsync）；取消只是不再等。
        await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);

        // 把「已经确切落盘了多少」一并交出去，让上层不必再 stat 一次、更不必盲退一个在途窗口。
        ThrowIfWriteFaulted();
    }

    /// <summary>
    /// 不阻塞的空操作：不发攒着的尾巴，也不等确认。
    /// <b>要把数据发完并确认落盘，用 <see cref="FlushAsync(CancellationToken)"/></b>（释放时也会做）。
    /// </summary>
    /// <exception cref="SftpTransferInterruptedException">此前已经有写入失败。</exception>
    /// <remarks>
    /// <para>
    /// 流水线模式下，不足一块的尾巴留在本端等凑满（见 <see cref="WriteAsync(ReadOnlyMemory{byte}, CancellationToken)"/>），
    /// 其余的在 <c>WriteAsync</c> 返回时已经作为 <c>WRITE</c> 请求发了出去；
    /// <see cref="FlushAsync(CancellationToken)"/> 做的是「发出尾巴、等服务端逐个确认」——
    /// 那要等网络往返，同步版本只能阻塞线程，本库不提供（架构原则 1）。
    /// </para>
    /// <para>
    /// 仍然保留一个不抛的同步 <c>Flush</c>，是因为包装流（<c>StreamWriter</c> 之类）
    /// 在各自的收尾里会同步调用它；让它抛会把一个无害的调用变成失败。
    /// 已知的写入错误照样在这里报出来 —— 那不需要等任何东西。
    /// </para>
    /// </remarks>
    public override void Flush() => ThrowIfWriteFaulted();

    private void ThrowIfWriteFaulted()
    {
        Exception? fault = _writeFault;
        if (fault is not null)
        {
            throw new SftpTransferInterruptedException(
                DurableLength,
                $"写入 {Path} 时中断。已连续确认 {DurableLength} 字节，从这里续传即可。",
                fault);
        }
    }

    // ------------------------------------------------------------ 其它

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _knownLength + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        ArgumentOutOfRangeException.ThrowIfNegative(target, nameof(offset));
        Position = target;
        return target;
    }

    /// <summary>不支持 —— 用 <see cref="SetLengthAsync"/>。</summary>
    /// <exception cref="NotSupportedException">总是。理由见 <see cref="Read(byte[], int, int)"/>。</exception>
    public override void SetLength(long value) => throw SyncNotSupported(nameof(SetLengthAsync));

    /// <summary>截断或扩展文件。</summary>
    public async ValueTask SetLengthAsync(long value, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        ResetReadAhead();

        // 攒着的尾巴与在途的写都先落地：截断之后才到的写会把文件又撑长（中间留一个空洞）。
        await FlushAsync(cancellationToken).ConfigureAwait(false);

        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WriteFSetStat(
                output, id, _handle, SftpFileAttributes.WithSize((ulong)value)),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(Path, SftpOperation.SetLength, SftpMessageType.Status);
        _knownLength = value;

        // 〔velashell-docs/zh/ssh/spec/06 §6.2〕截掉的那部分不再算「已确认落盘」：曾经 DurableLength 不回退，
        // 之后再断开，报出的续传点会跨过已经被截掉的数据，续传出一个中间是空洞的文件。
        // 扩长不动它：服务端补的零不是我们写的数据。
        _acked.TruncateTo(value);
    }

    /// <summary>服务端给的句柄（<c>copy-data</c> 这类要两个句柄的扩展用）。</summary>
    internal ReadOnlySpan<byte> Handle => _handle;

    /// <summary>按句柄设属性（<c>FSETSTAT</c>）。</summary>
    /// <remarks>先等攒着的与在途的写都落地：之后才到的写会把修改时间又改成「现在」。</remarks>
    public async ValueTask SetAttributesAsync(SftpFileAttributes attributes, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        await FlushAsync(cancellationToken).ConfigureAwait(false);

        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WriteFSetStat(output, id, _handle, attributes),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(Path, SftpOperation.SetAttributes, SftpMessageType.Status);
    }

    /// <summary>按句柄设访问时间与修改时间（两者共用一个标志位，要一起给）。</summary>
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/06 §7.1〕上传完、关闭之前用同一个句柄设修改时间：一次往返，
    /// 而 <see cref="SftpFileSystem.SetLastWriteTimeAsync"/> 要先 <c>STAT</c> 取回访问时间再 <c>SETSTAT</c>，两次往返 ——
    /// 批量上传小文件时差别明显。
    /// </remarks>
    public ValueTask SetTimesAsync(
        DateTimeOffset lastAccessTime, DateTimeOffset lastWriteTime, CancellationToken cancellationToken = default) =>
        SetAttributesAsync(SftpFileAttributes.WithTimes(lastAccessTime, lastWriteTime), cancellationToken);

    /// <summary>取当前属性。</summary>
    public async ValueTask<SftpFileAttributes> GetAttributesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_closed, this);

        // 攒着的尾巴先发，服务端报的大小才算上它。
        await FlushCoalescedAsync(cancellationToken).ConfigureAwait(false);

        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WriteHandleRequest(output, SftpMessageType.FStat, id, _handle),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(Path, SftpOperation.GetAttributes, SftpMessageType.Attrs);
        return SftpWire.ReadAttrs(response.Payload);
    }

    /// <summary>强制落盘（需要 <c>fsync@openssh.com</c>）。</summary>
    public async ValueTask FsyncAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        await FlushAsync(cancellationToken).ConfigureAwait(false);

        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) =>
            {
                ArrayBufferWriter<byte> inner = new();
                Protocol.SshDataWriter writer = new(inner);
                writer.WriteString(_handle);
                SftpWire.WriteExtended(output, id, SftpExtensionNames.Fsync, inner.WrittenSpan);
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(Path, SftpOperation.Fsync, SftpMessageType.Status);
    }

    /// <inheritdoc />
    /// <exception cref="SftpTransferInterruptedException">有写入没能确认。</exception>
    /// <exception cref="SftpException">
    /// 可写的流上，服务端对 <c>CLOSE</c> 回了失败。
    /// </exception>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>释放句柄之后，凡是要用到句柄的操作都抛 <see cref="ObjectDisposedException"/></b>
    /// （读写、<c>Seek</c>、改长度、取属性、fsync），<see cref="CanRead"/> / <see cref="CanWrite"/> 变成假。
    /// OpenSSH 的句柄是表里的下标，关掉之后会分给下一个打开的文件 —— 拿旧句柄再写一次，
    /// 写进去的是别人的文件。
    /// <see cref="Position"/>、<see cref="Length"/>、<see cref="DurableLength"/> 仍然可读：
    /// 关闭报错之后，调用方正要靠它们决定从哪里续传。
    /// </para>
    /// <para>
    /// <b>可写的流要看 <c>CLOSE</c> 的应答。</b>有的服务端（NFS、配额）直到关闭时才报出写入失败；
    /// 吞掉它，调用方就以为文件完整地写好了。只读的流关不上无关紧要，不报。
    /// 通道已经没了（发不出、收不到应答）也不报 —— 那时服务端自己会回收句柄，
    /// 而真正的原因已经由别的路径报过了。
    /// </para>
    /// </remarks>
    public override async ValueTask DisposeAsync()
    {
        if (_closed)
        {
            return;
        }
        _closed = true;

        // 还在路上的预读：应答到了就释放。CLOSE 排在它们后面发，服务端按顺序处理。
        ResetReadAhead();

        // 〔velashell-docs/zh/ssh/spec/06 §6.5〕两段都有时限：服务端不再应答时，释放曾经一直等下去。
        Exception? failure = null;
        using (CancellationTokenSource flushDeadline = new(CloseTimeout))
        {
            try
            {
                await FlushAsync(flushDeadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (flushDeadline.IsCancellationRequested)
            {
                failure = new SftpTransferInterruptedException(
                    SshFailureReason.Timeout,
                    DurableLength,
                    $"关闭 {Path} 时等了 {CloseTimeout.TotalSeconds:0} 秒，服务端还没确认完在途的写入。" +
                    $"已连续确认 {DurableLength} 字节，从这里续传即可。");
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }

        if (!_writable)
        {
            // 〔velashell-docs/zh/ssh/spec/06 §6.5〕只读的流关不上无关紧要（不报），那就不必等 CLOSE 的应答：
            // 小文件下载省掉一整轮往返。CLOSE 照发，句柄额度等应答回来（或发不出去）之后再还。
            _ = CloseReadOnlyInBackgroundAsync();
        }
        else
        {
            failure = await CloseAsync(failure).ConfigureAwait(false);
        }

        // 写槽不释放：关闭超时的话还有写在路上，它们收尾时要 Release —— 对一个已释放的信号量那是个异常，
        // 落在一个没人看的任务上。它没有用到等待句柄，不释放也不漏任何非托管资源。
        if (_coalesce is not null)
        {
            ArrayPool<byte>.Shared.Return(_coalesce);
            _coalesce = null;
            _coalesceLength = 0;
        }
        await base.DisposeAsync().ConfigureAwait(false);

        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        }
    }

    /// <summary>只读的流：在后台发 CLOSE、等应答、还句柄额度；什么错都不报。</summary>
    private async Task CloseReadOnlyInBackgroundAsync()
    {
        try
        {
            await CloseAsync(failure: null).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // CloseAsync 自己不抛；这里只是不让后台任务留下任何未观察的异常。
        }
    }

    /// <summary>发 CLOSE、等应答（有时限），之后还句柄额度。</summary>
    /// <returns>可写的流上 CLOSE 回了错误状态、而此前没有更早的失败时，交回那个错误；否则交回 <paramref name="failure"/>。</returns>
    private async Task<Exception?> CloseAsync(Exception? failure)
    {
        try
        {
            using CancellationTokenSource closeDeadline = new(CloseTimeout);
            using SftpResponse response = await _pipeline.SendAsync(
                (output, id) => SftpWire.WriteHandleRequest(output, SftpMessageType.Close, id, _handle),
                cancellationToken: closeDeadline.Token)
                .ConfigureAwait(false);

            // 看的是打开方式，不是 CanWrite —— 那个在关闭之后已经是假了。
            if (failure is null
                && _writable
                && response.TryGetStatus(out SftpStatusCode code, out string message)
                && code != SftpStatusCode.Ok)
            {
                failure = new SftpException(code, message, Path, SftpOperation.Close);
            }
        }
        catch (Exception)
        {
            // 关不上多半是通道已经没了 —— 那样服务端也会自己回收句柄。
            // 在释放路径上为此抛异常，只会盖住真正的失败原因。
        }
        OnHandleClosed?.Invoke();
        return failure;
    }

    /// <summary>续传时，偏移之前的部分由上一次传输确认过了（见 <c>SftpFileSystem.OpenAppendAsync</c>）。</summary>
    internal void AssumeDurablePrefix(long length)
    {
        if (length > 0)
        {
            _acked.Add(0, length);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>同步释放不阻塞</b>：它把收尾（等在途写入确认、关句柄）交给后台去做，立刻返回。
    /// 以前的做法是「同步包异步」地等完 —— 在 UI 线程上那是界面卡住一两个网络往返，
    /// 在线程池上并发一多就是饿死。
    /// </para>
    /// <para>
    /// 代价是同步释放<b>看不到收尾的错误</b>（写入中断、关不上句柄）。
    /// 要看到它们，用 <c>await using</c> / <see cref="DisposeAsync"/>。
    /// </para>
    /// </remarks>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_closed)
        {
            _ = CloseInBackgroundAsync();
        }
        base.Dispose(disposing);
    }

    private async Task CloseInBackgroundAsync()
    {
        try
        {
            await DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 同步释放的调用方已经走了，没有人能接这个异常 —— 见 Dispose 的说明。
        }
    }
}
