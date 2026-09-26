// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §4.2  版本标识串交换(行式 IO)
//   RFC 4253 §6    二进制报文协议(帧式 IO)
//   RFC 4253 §7.3  SSH_MSG_NEWKEYS 之后切换密钥;两个方向**各自独立**切换
//   OpenSSH PROTOCOL 的 kex-strict-*-v00@openssh.com —— 切换后序号归零
//   行为规格:      velashell-docs/zh/ssh/spec/01-transport-framing.md §3、§4;velashell-docs/zh/ssh/spec/02-version-exchange.md

using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Transport;

/// <summary>
/// 一条 SSH 传输：在字节流之上提供**行式**（版本交换）与**帧式**（二进制报文）两种读写。
/// </summary>
/// <remarks>
/// <para>
/// 它把 <see cref="Stream"/> 包成 <see cref="PipeReader"/> / <see cref="PipeWriter"/>，
/// 于是分帧是零拷贝的切片，而不是「先读进自己的缓冲再搬一次」。
/// 版本交换之后残留在缓冲里的字节自动归帧层 —— 这正是用 Pipelines 而不是
/// <c>Stream.ReadAsync</c> 的直接好处：不需要自己搬缓冲，也就不会搬错。
/// </para>
/// <para>
/// <b>收发是两条独立的路径</b>，各有各的密码套件与序号。
/// <c>SSH_MSG_NEWKEYS</c> 的两个方向互不等待（RFC 4253 §7.3）——
/// 把它们合并成一个「切换时刻」是一个常见错误，症状是快速链路上偶发的解密失败。
/// </para>
/// <para>
/// <b>不是线程安全的。</b>读由收包泵独占，写由发包泵独占，两者可以并发。
/// </para>
/// </remarks>
internal sealed class SshPacketTransport : IAsyncDisposable
{
    /// <summary>版本标识串单行的最大字节数（含 CR LF）。RFC 4253 §4.2。</summary>
    public const int MaxIdentificationLineBytes = 255;

    private readonly Stream _stream;
    private readonly CountingStream _counting;
    private readonly bool _ownsStream;
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private readonly ArrayBufferWriter<byte> _payloadBuffer = new(4096);

    private ISshCipherSuite _receiveSuite = new PlaintextCipherSuite();
    private ISshCipherSuite _sendSuite = new PlaintextCipherSuite();

    private ISshCompressor _receiveCompressor = NoCompression.Instance;
    private ISshCompressor _sendCompressor = NoCompression.Instance;

    /// <summary>压缩用的中转缓冲（压缩开着时才会用到）。</summary>
    /// <remarks>
    /// ⚠️ <b>两个方向必须各用一个，绝不能共用。</b>
    ///
    /// 收与发是**并发**的：接收循环在读，而 N 条通道的泵在写。共用一个
    /// <see cref="ArrayBufferWriter{T}"/> 的后果是：解压刚把载荷写进去、
    /// 还没等调用方用完，一个并发的 <see cref="WritePacket"/> 就
    /// <c>ResetWrittenCount()</c> 把它清了 —— 调用方拿到的是一段长度为 0
    /// 或者半截的载荷。
    ///
    /// 症状极具迷惑性：小载荷时撞不上（一发一收之间没有交错的机会），
    /// 数据量一上来就开始「收到 0 字节」或者「报文载荷为空，没有消息编号」，
    /// 而那两句话都指不到压缩这里。实测 1.9 KB 的载荷次次都过，
    /// 8 KB 的载荷次次都挂。
    /// </remarks>
    private readonly ArrayBufferWriter<byte> _sendCompressionBuffer = new(4096);

    /// <summary>解压用的中转缓冲。见 <see cref="_sendCompressionBuffer"/> 上的说明。</summary>
    private readonly ArrayBufferWriter<byte> _receiveCompressionBuffer = new(4096);
    private bool _disposed;

    /// <summary>在给定的双工字节流上建立传输。</summary>
    /// <param name="stream">底层流，通常来自 <see cref="ISshTransportDialer"/>。</param>
    /// <param name="ownsStream">释放本对象时是否一并释放 <paramref name="stream"/>。</param>
    public SshPacketTransport(Stream stream, bool ownsStream = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _ownsStream = ownsStream;

        // 套一层计数：**线上字节数与应用字节数不是一回事** ——
        // 前者含协议头、填充与 MAC，压缩打开之后两者还会差出好几倍。
        // 终端产品要在界面上显示「这条连接用了多少流量」，说的是前者。
        _counting = new CountingStream(stream);

        // leaveOpen: 由我们自己决定什么时候释放底层流。
        _reader = PipeReader.Create(_counting, new StreamPipeReaderOptions(
            bufferSize: 64 * 1024, minimumReadSize: 4096, leaveOpen: true));
        _writer = PipeWriter.Create(_counting, new StreamPipeWriterOptions(leaveOpen: true));
    }

    /// <summary>线上收到的字节数（含协议头、填充与 MAC）。</summary>
    public long BytesReceived => _counting.BytesRead;

    /// <summary>线上发出的字节数（含协议头、填充与 MAC）。</summary>
    public long BytesSent => _counting.BytesWritten;

    /// <summary>收到的报文数。</summary>
    public long PacketsReceived { get; private set; }

    /// <summary>发出的报文数。</summary>
    public long PacketsSent { get; private set; }

    /// <summary>当前允许的最大 <c>packet_length</c>。认证成功后由会话放宽。</summary>
    public int MaxPacketLength { get; set; } = SshPacketFormat.PreAuthMaxPacketLength;

    /// <summary>接收方向的当前序号（下一个要收的报文用它）。</summary>
    public uint ReceiveSequenceNumber { get; private set; }

    /// <summary>发送方向的当前序号（下一个要发的报文用它）。</summary>
    public uint SendSequenceNumber { get; private set; }

    // ------------------------------------------------------------ 行式（握手）

    /// <summary>
    /// 读一行以 <c>\n</c> 结尾的文本（版本交换阶段用）。
    /// </summary>
    /// <returns>去掉行尾的那一行；对端干净关闭时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// 〔互操作，velashell-docs/zh/ssh/spec/02 §5〕行尾按 <c>\n</c> 找，然后**顺带去掉前面的 <c>\r</c>**。
    /// RFC 要求 <c>\r\n</c>，但部分嵌入式 SSH 实现（含若干型号网络设备的管理口）只发 <c>\n</c>。
    /// 严格要求 <c>\r\n</c> 会让这些设备完全连不上，而接受它没有任何安全代价 ——
    /// 标识串不参与任何信任判定，且进交换哈希的是「去掉行尾之后」的内容，两种写法结果相同。
    /// </para>
    /// <para>
    /// 多读的字节留在 <see cref="PipeReader"/> 里，自动归后续的帧式读取。
    /// </para>
    /// </remarks>
    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (true)
        {
            ReadResult result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            SequencePosition? newline = buffer.PositionOf((byte)'\n');
            if (newline is not null)
            {
                ReadOnlySequence<byte> line = buffer.Slice(0, newline.Value);
                if (line.Length > MaxIdentificationLineBytes)
                {
                    _reader.AdvanceTo(buffer.Start, buffer.End);
                    throw new SshFrameFormatException(
                        $"标识串行超过 {MaxIdentificationLineBytes} 字节（收到 {line.Length}）。");
                }

                string text = DecodeLine(line);
                _reader.AdvanceTo(buffer.GetPosition(1, newline.Value));
                return text;
            }

            if (buffer.Length > MaxIdentificationLineBytes)
            {
                _reader.AdvanceTo(buffer.Start, buffer.End);
                throw new SshFrameFormatException(
                    $"标识串行超过 {MaxIdentificationLineBytes} 字节仍未见换行。");
            }

            if (result.IsCompleted)
            {
                _reader.AdvanceTo(buffer.Start, buffer.End);
                return null;
            }

            _reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    /// <summary>写一行文本并附加 <c>\r\n</c>，随后刷出。</summary>
    /// <remarks>标识串必须是可打印 US-ASCII（RFC 4253 §4.2）。</remarks>
    public async ValueTask WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(line);

        int byteCount = Encoding.ASCII.GetByteCount(line) + 2;
        if (byteCount > MaxIdentificationLineBytes)
        {
            throw new ArgumentException(
                $"标识串含行尾不得超过 {MaxIdentificationLineBytes} 字节。", nameof(line));
        }

        Span<byte> span = _writer.GetSpan(byteCount)[..byteCount];
        int written = Encoding.ASCII.GetBytes(line, span);
        span[written] = (byte)'\r';
        span[written + 1] = (byte)'\n';
        _writer.Advance(byteCount);

        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string DecodeLine(ReadOnlySequence<byte> line)
    {
        int length = (int)line.Length;
        byte[] rented = ArrayPool<byte>.Shared.Rent(Math.Max(length, 1));
        try
        {
            line.CopyTo(rented);
            // 顺带去掉 CR —— 见 <remarks> 的互操作说明。
            if (length > 0 && rented[length - 1] == (byte)'\r')
            {
                length--;
            }
            // 标识串按 RFC 是 US-ASCII，但前导行（banner）可能是任意 UTF-8。
            // 用宽容的 UTF-8 解码：这些文本只用于展示，不参与任何判定。
            return Encoding.UTF8.GetString(rented.AsSpan(0, length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    // ------------------------------------------------------------ 帧式（稳态）

    /// <summary>
    /// 读一个完整报文。
    /// </summary>
    /// <returns>
    /// 报文的载荷。<see cref="SshInboundPacket.IsEndOfStream"/> 为真表示对端干净关闭。
    /// </returns>
    /// <remarks>
    /// <b>返回的内存只在下一次 <see cref="ReadPacketAsync"/> 之前有效。</b>
    /// 要留着就自己复制 —— 这是 Pipelines 的一贯契约，也是零拷贝的代价。
    /// </remarks>
    public async ValueTask<SshInboundPacket> ReadPacketAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (true)
        {
            ReadResult result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (!buffer.IsEmpty)
            {
                _payloadBuffer.ResetWrittenCount();
                SshOpenStatus status;
                try
                {
                    status = _receiveSuite.TryOpen(
                        buffer, ReceiveSequenceNumber, MaxPacketLength, _payloadBuffer, out long consumed);

                    if (status == SshOpenStatus.Opened)
                    {
                        _reader.AdvanceTo(buffer.GetPosition(consumed));
                        // 序号在**成功取出一帧之后**才推进，与密码套件的约定一致。
                        ReceiveSequenceNumber = unchecked(ReceiveSequenceNumber + 1);
                        PacketsReceived++;
                    }
                }
                catch
                {
                    // 解析失败时也要把 reader 交还，否则 Dispose 会挂住。
                    _reader.AdvanceTo(buffer.Start, buffer.End);
                    throw;
                }

                if (status == SshOpenStatus.Opened)
                {
                    // 解密在前，解压在后 —— 顺序反了什么都对不上（发送侧是先压后加密）。
                    //
                    // ⚠️ 解压放在上面那个 try **外面**：reader 在那里已经 AdvanceTo 过了。
                    //    曾经放在里面，解压一失败（压缩炸弹、坏的 zlib 流），catch 就再 AdvanceTo 一次，
                    //    真正的原因被「PipeReader 已经越过这个位置」的 InvalidOperationException 盖掉。
                    return new SshInboundPacket(
                        _receiveCompressor.IsActive
                            ? DecompressPayload(_payloadBuffer.WrittenMemory)
                            : _payloadBuffer.WrittenMemory);
                }
            }

            if (result.IsCompleted)
            {
                _reader.AdvanceTo(buffer.Start, buffer.End);
                if (buffer.IsEmpty)
                {
                    return SshInboundPacket.EndOfStream;   // 帧边界上的干净关闭
                }
                throw new SshFrameFormatException("对端在报文中途关闭了连接。") { PeerClosedMidPacket = true };
            }

            // 数据不足：consumed = start（什么都没消费）、examined = end（已经看到这里）。
            // 两个参数给错会让 PipeReader 要么丢数据，要么不再等新数据（表现为卡死）。
            _reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    /// <summary>
    /// 把一个载荷封装成报文写进发送缓冲，<b>不刷出</b>。
    /// </summary>
    /// <remarks>
    /// 不刷出是刻意的：调用方连写若干帧再 <see cref="FlushAsync"/> 一次，
    /// 把「每帧一次系统调用」压成「一轮一次」。SFTP 满管线时这是 64 → 1 的差别
    /// （velashell-docs/zh/ssh/spec/01 §4）。
    /// </remarks>
    public void WritePacket(ReadOnlySpan<byte> payload)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // **先压缩，再加密。**反过来的话压缩器面对的是密文 ——
        // 密文没有可压缩性，压出来只会更长，而且会泄漏明文的统计特征。
        if (_sendCompressor.IsActive)
        {
            _sendCompressionBuffer.ResetWrittenCount();
            _sendCompressor.Compress(payload, _sendCompressionBuffer);
            _sendSuite.Seal(_sendCompressionBuffer.WrittenSpan, SendSequenceNumber, _writer);
        }
        else
        {
            _sendSuite.Seal(payload, SendSequenceNumber, _writer);
        }

        SendSequenceNumber = unchecked(SendSequenceNumber + 1);
        PacketsSent++;
    }

    /// <summary>把刚解密出来的载荷解压。</summary>
    private ReadOnlyMemory<byte> DecompressPayload(ReadOnlyMemory<byte> compressed)
    {
        _receiveCompressionBuffer.ResetWrittenCount();
        _receiveCompressor.Decompress(
            new ReadOnlySequence<byte>(compressed), _receiveCompressionBuffer, MaxPacketLength);
        return _receiveCompressionBuffer.WrittenMemory;
    }

    /// <summary>换掉发送方向的压缩器。</summary>
    /// <remarks>
    /// <c>zlib@openssh.com</c> 要等认证成功之后才调用它 ——
    /// 认证之前的报文里有密码与公钥，而压缩会让密文长度泄漏明文的可压缩性。
    /// </remarks>
    public void SetSendCompressor(ISshCompressor compressor)
    {
        ArgumentNullException.ThrowIfNull(compressor);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!ReferenceEquals(_sendCompressor, NoCompression.Instance))
        {
            _sendCompressor.Dispose();
        }
        _sendCompressor = compressor;
    }

    /// <summary>换掉接收方向的压缩器。</summary>
    public void SetReceiveCompressor(ISshCompressor compressor)
    {
        ArgumentNullException.ThrowIfNull(compressor);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!ReferenceEquals(_receiveCompressor, NoCompression.Instance))
        {
            _receiveCompressor.Dispose();
        }
        _receiveCompressor = compressor;
    }

    /// <summary>把已写入的报文刷到底层流。</summary>
    public async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 密钥切换

    /// <summary>
    /// 切换**接收**方向的密码套件（收到对端的 <c>SSH_MSG_NEWKEYS</c> 之后）。
    /// </summary>
    /// <param name="suite">新套件。本对象接管它的生命周期。</param>
    /// <param name="resetSequenceNumber">
    /// 启用严格 KEX 时为 <see langword="true"/> —— 此时序号归零
    /// （OpenSSH <c>kex-strict-*-v00@openssh.com</c>，Terrapin 缓解）。
    /// </param>
    public void SetReceiveCipherSuite(ISshCipherSuite suite, bool resetSequenceNumber)
    {
        ArgumentNullException.ThrowIfNull(suite);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _receiveSuite.Dispose();
        _receiveSuite = suite;
        if (resetSequenceNumber)
        {
            ReceiveSequenceNumber = 0;
        }
    }

    /// <summary>
    /// 切换**发送**方向的密码套件（发出 <c>SSH_MSG_NEWKEYS</c> 之后）。
    /// </summary>
    /// <remarks>
    /// 与接收方向**互不等待**：我们发出 NEWKEYS 之后发的下一个报文就用新密钥，
    /// 而收的方向要等对端的 NEWKEYS 到达（RFC 4253 §7.3）。
    /// </remarks>
    public void SetSendCipherSuite(ISshCipherSuite suite, bool resetSequenceNumber)
    {
        ArgumentNullException.ThrowIfNull(suite);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _sendSuite.Dispose();
        _sendSuite = suite;
        if (resetSequenceNumber)
        {
            SendSequenceNumber = 0;
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

        _receiveSuite.Dispose();
        _sendSuite.Dispose();

        if (!ReferenceEquals(_receiveCompressor, NoCompression.Instance))
        {
            _receiveCompressor.Dispose();
        }
        if (!ReferenceEquals(_sendCompressor, NoCompression.Instance))
        {
            _sendCompressor.Dispose();
        }

        // 释放路径**不抛异常**。
        //
        // PipeWriter.CompleteAsync 会尝试把待发数据刷出去，而这时对端往往已经走了
        // （连接失败、对方先断、测试收尾）—— 刷不出去是常态，不是异常情况。
        // 让它抛出去的后果是：每一条 `await using` 的错误路径都会被一个次要的
        // IOException 盖住真正的失败原因。
        //
        // 对端已经没了，待发数据本来也无处可去，这里没有任何补救动作可做。
        try
        {
            await _reader.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 同上。
        }

        try
        {
            // ⚠️ **带着异常完成，不再往流上写。**不带异常的 CompleteAsync 会把缓冲里剩下的字节刷出去 ——
            //    而剩下的只可能是一次被取消的 flush 丢下的（正常路径上每一帧都显式 flush 过）。
            //    那次 flush 被取消，多半正是因为对端不读了（TCP 零窗口、半开的链路）：
            //    这里再去写，就是在释放路径上等一个永远不来的对端，直到 TCP 自己放弃（十几分钟）。
            await _writer.CompleteAsync(new ObjectDisposedException(nameof(SshPacketTransport))).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 同上。
        }

        if (_ownsStream)
        {
            try
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 同上。
            }
        }
    }
}

/// <summary>数一数流上过了多少字节。</summary>
/// <remarks>
/// 它<b>不改变任何语义</b>，只是在读写路径上各加一次 <c>Interlocked.Add</c>。
/// 放在这一层而不是更上面：只有这里看得到真正上线的字节。
/// </remarks>
internal sealed class CountingStream(Stream inner) : Stream
{
    private long _bytesRead;
    private long _bytesWritten;

    public long BytesRead => Interlocked.Read(ref _bytesRead);

    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

    public override bool CanRead => inner.CanRead;

    public override bool CanWrite => inner.CanWrite;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref _bytesRead, read);
        return read;
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref _bytesWritten, buffer.Length);
    }

    public override Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task WriteAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        inner.FlushAsync(cancellationToken);

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = inner.Read(buffer, offset, count);
        Interlocked.Add(ref _bytesRead, read);
        return read;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        Interlocked.Add(ref _bytesWritten, count);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // **不释放内层流** —— 谁拥有它由 SshPacketTransport 决定。
        base.Dispose(disposing);
    }
}

/// <summary>一个收到的报文。</summary>
/// <remarks>
/// <b><see cref="Payload"/> 只在下一次读取之前有效。</b>
/// 要留着就自己复制 —— 零拷贝的代价就在这里。
/// </remarks>
internal readonly struct SshInboundPacket
{
    internal SshInboundPacket(ReadOnlyMemory<byte> payload)
    {
        Payload = payload;
        IsEndOfStream = false;
    }

    private SshInboundPacket(bool endOfStream)
    {
        Payload = default;
        IsEndOfStream = endOfStream;
    }

    /// <summary>对端干净关闭时返回的哨兵。</summary>
    public static SshInboundPacket EndOfStream { get; } = new(endOfStream: true);

    /// <summary>报文载荷，第一个字节是消息编号。</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>是否表示对端已在帧边界上干净关闭。</summary>
    public bool IsEndOfStream { get; }

    /// <summary>
    /// 消息编号（载荷的第一个字节）。
    /// </summary>
    /// <exception cref="SshFrameFormatException">载荷为空。</exception>
    /// <remarks>
    /// 载荷为空的报文在协议里不存在 —— 每个报文至少有一个消息编号字节
    /// （velashell-docs/zh/ssh/spec/01 §5）。
    /// </remarks>
    public SshMessageNumber MessageNumber => Payload.Length > 0
        ? (SshMessageNumber)Payload.Span[0]
        : throw new SshFrameFormatException("报文载荷为空，没有消息编号。");
}
