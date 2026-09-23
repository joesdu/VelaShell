// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/design/architecture.md §10.2

using System.Buffers;
using System.IO.Pipelines;

namespace VelaShell.Ssh.Transport;

/// <summary>内存传输的参数。</summary>
public sealed record InMemoryTransportOptions
{
    /// <summary>
    /// 单向缓冲写入方被暂停的水位（字节）。默认 64 KiB。
    /// </summary>
    /// <remarks>
    /// 它模拟的是「对端还没读，我方发不动了」。把它调小可以在测试里**制造背压**，
    /// 验证上层在写不动时的行为 —— 那条路径在真实网络上很难稳定复现。
    /// </remarks>
    public long PauseWriterThreshold { get; init; } = 64 * 1024;

    /// <summary>写入方恢复的水位（字节）。默认 32 KiB。</summary>
    public long ResumeWriterThreshold { get; init; } = 32 * 1024;
}

/// <summary>
/// 一对在内存里互联的双工流：一端写出的字节，另一端读得到。
/// </summary>
/// <remarks>
/// <para>
/// <b>它是「协议测试不需要网络、不需要容器」这条路的地基</b>
/// （<see href="https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/ssh/design/architecture.md">velashell-docs/zh/ssh/design/architecture.md</see> §10.2）。
/// 有了它，分帧、状态机、通道窗口、SFTP 管线的绝大部分用例都能毫秒级跑完，
/// 因而可以进每次提交的门禁 —— 而不是被排除在门禁之外、等到某次手动跑集成测试才发现问题。
/// </para>
/// <para>
/// 它也是一个可用的生产特性：把 <see cref="ISshTransportDialer"/> 指到别处
/// （进程内的另一个组件、一条已有的隧道）时，这对流就是那条路的载体。
/// </para>
/// <para>
/// ⏳ <b>尚未做的：链路特征模拟</b>（单向时延、带宽上限、丢包）。
/// 自适应通道窗口（velashell-docs/zh/ssh/spec/05-connection.md §3.3）与 SFTP 管线深度
/// （velashell-docs/zh/ssh/spec/06-sftp.md §5.2）都需要它才能被真正测到。
/// 正确的做法是在两条管道之间插一个按时间放行的中继任务，而不是在写入侧 <c>Task.Delay</c>
/// ——后者会把写入方一起挡住，模拟出来的行为与真实链路不是一回事，
/// 照着它调出来的窗口算法在真网络上会是错的。
/// </para>
/// </remarks>
public static class InMemoryTransport
{
    /// <summary>创建一对互联的双工流。</summary>
    /// <param name="options">缓冲参数；<see langword="null"/> 时用默认值。</param>
    /// <returns>两端。第一端写出的字节，第二端读得到，反之亦然。</returns>
    public static (InMemoryDuplexStream First, InMemoryDuplexStream Second) CreatePair(
        InMemoryTransportOptions? options = null)
    {
        options ??= new InMemoryTransportOptions();

        PipeOptions pipeOptions = new(
            pauseWriterThreshold: options.PauseWriterThreshold,
            resumeWriterThreshold: options.ResumeWriterThreshold,
            useSynchronizationContext: false);

        Pipe firstToSecond = new(pipeOptions);
        Pipe secondToFirst = new(pipeOptions);

        InMemoryDuplexStream first = new(secondToFirst.Reader, firstToSecond.Writer);
        InMemoryDuplexStream second = new(firstToSecond.Reader, secondToFirst.Writer);
        return (first, second);
    }

    /// <summary>
    /// 一个把 <see cref="CreatePair"/> 的一端交出去的拨号器，另一端留给测试的服务端桩。
    /// </summary>
    /// <param name="onAccepted">
    /// 每次拨号成功时以「服务端那一端」回调。测试在这里挂上报文脚本或服务端桩。
    /// </param>
    /// <param name="options">缓冲参数。</param>
    public static ISshTransportDialer CreateDialer(
        Func<InMemoryDuplexStream, SshDialTarget, CancellationToken, ValueTask> onAccepted,
        InMemoryTransportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(onAccepted);
        return new InMemoryDialer(onAccepted, options);
    }

    private sealed class InMemoryDialer(
        Func<InMemoryDuplexStream, SshDialTarget, CancellationToken, ValueTask> onAccepted,
        InMemoryTransportOptions? options) : ISshTransportDialer
    {
        public SshDialKind Kind => SshDialKind.InMemory;

        public async ValueTask<Stream> DialAsync(SshDialTarget target, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(target);
            (InMemoryDuplexStream client, InMemoryDuplexStream server) = CreatePair(options);
            await onAccepted(server, target, cancellationToken).ConfigureAwait(false);
            return client;
        }
    }
}

/// <summary>
/// <see cref="InMemoryTransport.CreatePair"/> 的一端。
/// </summary>
/// <remarks>
/// 除了 <see cref="Stream"/> 的常规读写，它还提供 <see cref="CompleteWrites"/> ——
/// 也就是 TCP 的半关闭。<see cref="Stream"/> 本身没有这个概念，
/// 而<b>半关闭是转发路径的正确性要害</b>（velashell-docs/zh/ssh/spec/07-forwarding.md §2.2）：
/// 把 EOF 当成「连接结束」会截断数据，且症状出现在对端。
/// </remarks>
public sealed class InMemoryDuplexStream : Stream
{
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private bool _writesCompleted;
    private bool _disposed;

    internal InMemoryDuplexStream(PipeReader reader, PipeWriter writer)
    {
        _reader = reader;
        _writer = writer;
    }

    /// <inheritdoc />
    public override bool CanRead => !_disposed;

    /// <inheritdoc />
    public override bool CanWrite => !_disposed && !_writesCompleted;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// 本端不再发送数据，但**仍然可以接收** —— 即 TCP 的 <c>shutdown(SEND)</c>。
    /// </summary>
    /// <remarks>
    /// 对端的读取会在读完已发出的数据后返回 0（EOF）。
    /// 重复调用是空操作。
    /// </remarks>
    public void CompleteWrites()
    {
        if (_writesCompleted)
        {
            return;
        }
        _writesCompleted = true;
        _writer.Complete();
    }

    // RS0027「带可选参数的公开 API 应当是参数最多的那个重载」——
    // 这两个是 Stream 的 override，签名由基类定死（Memory<byte> + 可选 ct），
    // 我们没有选择。该规则针对的是**我们自己设计**的 API，而它分不清这两者。
    // 局部关闭而不是全局关掉：对我们自己的 API 它仍然该生效。
#pragma warning disable RS0027

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (true)
        {
            ReadResult result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> available = result.Buffer;

            if (!available.IsEmpty)
            {
                int count = (int)Math.Min(available.Length, buffer.Length);
                available.Slice(0, count).CopyTo(buffer.Span);
                _reader.AdvanceTo(available.GetPosition(count));
                return count;
            }

            if (result.IsCompleted)
            {
                // 对端不再发送且已读空 —— 这是干净的 EOF，不是错误。
                _reader.AdvanceTo(available.End);
                return 0;
            }

            // 还没有数据：告诉 PipeReader「这些我都看过了」，下一次 ReadAsync 才会等新数据。
            // consumed 与 examined 必须分开给 —— 两个都给 End 会丢数据，
            // 两个都给 Start 会让 ReadAsync 立刻返回同样的空缓冲，变成忙等。
            _reader.AdvanceTo(available.Start, available.End);
        }
    }

    /// <inheritdoc />
    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_writesCompleted)
        {
            throw new InvalidOperationException("写入端已关闭（CompleteWrites 已被调用）。");
        }
        if (buffer.IsEmpty)
        {
            return;
        }

        FlushResult result = await _writer.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (result.IsCompleted)
        {
            throw new IOException("对端已关闭读取端。");
        }
    }

#pragma warning restore RS0027

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_writesCompleted)
        {
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>不支持同步读取。</summary>
    /// <remarks>
    /// 本库全程异步（架构原则 1）。提供一个会阻塞线程池线程的同步壳，
    /// 只会让使用者在不该同步的地方同步。
    /// </remarks>
    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("本流只支持异步读取，请用 ReadAsync。");

    /// <summary>不支持同步写入。理由同 <see cref="Read"/>。</summary>
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("本流只支持异步写入，请用 WriteAsync。");

    /// <inheritdoc />
    public override void Flush()
    {
        // 空操作：PipeWriter.WriteAsync 本身就会刷出。
        // 抛 NotSupportedException 会让一堆只是「顺手 Flush 一下」的通用代码炸掉，
        // 而它们并没有做错什么。
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (disposing)
        {
            if (!_writesCompleted)
            {
                _writesCompleted = true;
                _writer.Complete();
            }
            _reader.Complete();
        }
        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (!_writesCompleted)
            {
                _writesCompleted = true;
                await _writer.CompleteAsync().ConfigureAwait(false);
            }
            await _reader.CompleteAsync().ConfigureAwait(false);
        }

        // base.DisposeAsync() 会转去调 Dispose(true) 并 SuppressFinalize。
        // 我们的 Dispose(bool) 有 _disposed 守卫，此时已是空操作 —— 但**必须**调，
        // 否则 Stream 自己的清理(以及将来版本新增的清理)会被跳过。
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
