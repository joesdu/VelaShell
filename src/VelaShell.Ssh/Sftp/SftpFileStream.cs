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

    internal SftpFileStream(
        SftpRequestPipeline pipeline,
        byte[] handle,
        string path,
        bool canRead,
        bool canWrite,
        long initialLength,
        int blockSize,
        SftpWriteMode writeMode,
        int maxInFlightWrites)
    {
        _pipeline = pipeline;
        _handle = handle;
        Path = path;
        _blockSize = blockSize;
        _knownLength = initialLength;
        CanRead = canRead;
        CanWrite = canWrite;
        WriteMode = writeMode;

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

    /// <inheritdoc />
    public override bool CanRead { get; }

    /// <inheritdoc />
    public override bool CanWrite { get; }

    /// <inheritdoc />
    public override bool CanSeek => true;

    /// <inheritdoc />
    public override long Length => _knownLength;

    /// <inheritdoc />
    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    // ------------------------------------------------------------ 读

    /// <inheritdoc />
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

        int wanted = Math.Min(buffer.Length, _blockSize);
        int read = await ReadAtAsync(_position, buffer[..wanted], cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    /// <summary>从指定偏移读，不动当前位置。</summary>
    /// <returns>实际读到的字节数；<c>0</c> 表示到达文件末尾。</returns>
    /// <remarks>
    /// <b>返回的字节数可能少于请求的</b>，那不是错误 ——
    /// 调用方要循环读直到拿够或返回 0。
    /// </remarks>
    public async ValueTask<int> ReadAtAsync(
        long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WriteRead(output, id, _handle, (ulong)offset, (uint)buffer.Length),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (response.TryGetStatus(out SftpStatusCode code, out string message))
        {
            // EOF 不是错误 —— 它就是「读完了」。
            if (code == SftpStatusCode.EndOfFile)
            {
                return 0;
            }
            throw new SftpException(code, message, Path, "读取");
        }

        if (response.Type != SftpMessageType.Data)
        {
            throw new SshProtocolException(
                SshPhase.Open, $"读取时期望 SSH_FXP_DATA，收到 {response.Type}。");
        }

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

    private NotSupportedException SyncNotSupported(string asyncAlternative) =>
        new($"{nameof(SftpFileStream)}（{Path}）只支持异步操作，请改用 {asyncAlternative}。" +
            "同步调用只能靠阻塞线程等网络往返来实现，本库不提供这种形态。");

    // ------------------------------------------------------------ 写

    /// <inheritdoc />
    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (!CanWrite)
        {
            throw new NotSupportedException("这个流是以只读方式打开的。");
        }

        int offset = 0;
        while (offset < buffer.Length)
        {
            int chunk = Math.Min(buffer.Length - offset, _blockSize);
            await WriteAtAsync(_position, buffer.Slice(offset, chunk), cancellationToken).ConfigureAwait(false);
            _position += chunk;
            offset += chunk;
        }
    }

    /// <summary>往指定偏移写，不动当前位置。</summary>
    /// <remarks>
    /// 流水线模式下这个方法<b>在应答回来之前就返回</b> ——
    /// 真正确认落盘要看 <see cref="DurableLength"/>，或者 <see cref="FlushAsync"/> 之后。
    /// </remarks>
    public async ValueTask WriteAtAsync(
        long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ThrowIfWriteFaulted();

        if (data.IsEmpty)
        {
            return;
        }

        // 在途写的数量就是背压。满了就等，不报错。
        await _writeSlots.WaitAsync(cancellationToken).ConfigureAwait(false);

        // 调用方的缓冲在我们返回之后就可能被复用 —— 必须先复制。
        byte[] rented = ArrayPool<byte>.Shared.Rent(data.Length);
        data.CopyTo(rented);

        Task write = SendWriteAsync(offset, rented, data.Length, cancellationToken);

        lock (_pendingLock)
        {
            _pendingWrites.RemoveAll(static t => t.IsCompleted);
            _pendingWrites.Add(write);
        }

        _knownLength = Math.Max(_knownLength, offset + data.Length);

        if (WriteMode == SftpWriteMode.Sequential)
        {
            // 顺序模式的承诺是「任何时刻文件都是一个完整前缀」——
            // 那就必须等这一块确认了才返回。
            await write.ConfigureAwait(false);
        }
    }

    private async Task SendWriteAsync(long offset, byte[] rented, int length, CancellationToken cancellationToken)
    {
        try
        {
            using SftpResponse response = await _pipeline.SendAsync(
                (output, id) => SftpWire.WriteWrite(output, id, _handle, (ulong)offset, rented.AsSpan(0, length)),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            response.ThrowIfError(Path, "写入", treatEndOfFileAsError: true);

            // 确认了才并入 —— DurableLength 的可信度全靠这一行的位置。
            _acked.Add(offset, length);
        }
        catch (Exception ex)
        {
            // 记下第一个错误。后续的 WriteAtAsync 会立刻抛，
            // 而不是继续往一条已经坏掉的流上堆请求。
            Interlocked.CompareExchange(ref _writeFault, ex, null);
            throw;
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

    /// <summary>等所有在途写入都确认。</summary>
    /// <exception cref="SftpTransferInterruptedException">
    /// 有写入失败。异常里带着<b>精确</b>的 <c>DurableLength</c>。
    /// </exception>
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        Task[] pending;
        lock (_pendingLock)
        {
            pending = [.. _pendingWrites];
        }

        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 把「已经确切落盘了多少」一并交出去，让上层不必再 stat 一次、
            // 更不必盲退一个在途窗口。
            throw new SftpTransferInterruptedException(
                DurableLength,
                $"写入 {Path} 时中断。已连续确认 {DurableLength} 字节，从这里续传即可。",
                ex);
        }

        ThrowIfWriteFaulted();
    }

    /// <summary>
    /// 本端没有缓冲，所以没有东西要「冲」—— 这是一个不阻塞的空操作。
    /// <b>要确认数据已经落盘，用 <see cref="FlushAsync(CancellationToken)"/></b>。
    /// </summary>
    /// <exception cref="SftpTransferInterruptedException">此前已经有写入失败。</exception>
    /// <remarks>
    /// <para>
    /// 每次 <c>WriteAsync</c> 返回时，数据已经作为 <c>WRITE</c> 请求发了出去；
    /// <see cref="FlushAsync(CancellationToken)"/> 做的是「等服务端逐个确认」——
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
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _knownLength + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        ArgumentOutOfRangeException.ThrowIfNegative(target, nameof(offset));
        _position = target;
        return target;
    }

    /// <summary>不支持 —— 用 <see cref="SetLengthAsync"/>。</summary>
    /// <exception cref="NotSupportedException">总是。理由见 <see cref="Read(byte[], int, int)"/>。</exception>
    public override void SetLength(long value) => throw SyncNotSupported(nameof(SetLengthAsync));

    /// <summary>截断或扩展文件。</summary>
    public async ValueTask SetLengthAsync(long value, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);

        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WriteFSetStat(
                output, id, _handle, SftpFileAttributes.WithSize((ulong)value)),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(Path, "设置文件长度", treatEndOfFileAsError: true);
        _knownLength = value;
    }

    /// <summary>取当前属性。</summary>
    public async ValueTask<SftpFileAttributes> GetAttributesAsync(CancellationToken cancellationToken = default)
    {
        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WriteHandleRequest(output, SftpMessageType.FStat, id, _handle),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(Path, "取属性", treatEndOfFileAsError: true);
        return SftpWire.ReadAttrs(response.Payload);
    }

    /// <summary>强制落盘（需要 <c>fsync@openssh.com</c>）。</summary>
    public async ValueTask FsyncAsync(CancellationToken cancellationToken = default)
    {
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

        response.ThrowIfError(Path, "fsync", treatEndOfFileAsError: true);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (_closed)
        {
            return;
        }
        _closed = true;

        try
        {
            await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                using SftpResponse response = await _pipeline.SendAsync(
                    (output, id) => SftpWire.WriteHandleRequest(output, SftpMessageType.Close, id, _handle))
                    .ConfigureAwait(false);
                _ = response;
            }
            catch (Exception)
            {
                // 关不上多半是通道已经没了 —— 那样服务端也会自己回收句柄。
                // 在释放路径上为此抛异常，只会盖住真正的失败原因。
            }
        }

        _writeSlots.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
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
