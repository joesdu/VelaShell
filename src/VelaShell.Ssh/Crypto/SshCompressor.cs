// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §6.2    压缩:对**载荷**压缩,每个方向一个独立的、跨报文保留的 zlib 流
//   RFC 1950/1951    zlib 与 deflate 的容器与块格式(flush 语义出自这里)
//   OpenSSH PROTOCOL zlib@openssh.com —— 认证成功之后才开始压缩
//   行为规格:        velashell-docs/zh/ssh/spec/01-transport-framing.md §压缩

using System.Buffers;
using System.IO.Compression;
using VelaShell.Ssh.Crypto.Kex;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Crypto;

/// <summary>一个方向上的压缩。</summary>
/// <remarks>
/// <para>
/// <b>压缩的是载荷，不是整个报文</b> —— 长度字段、填充、MAC 都在压缩之外。
/// </para>
/// <para>
/// <b>每个方向一个独立的流，而且跨报文保留状态。</b>
/// 这是压缩率的全部来源：SSH 报文都很小（一次按键才几十字节），
/// 每个报文各压各的几乎压不动；共用一本字典才有意义。
/// 代价是<b>丢一个报文就全乱了</b> —— 但 SSH 跑在 TCP 上，不会丢。
/// </para>
/// </remarks>
public interface ISshCompressor : IDisposable
{
    /// <summary>这个方向现在到底压不压。</summary>
    bool IsActive { get; }

    /// <summary>压一个载荷。</summary>
    void Compress(ReadOnlySpan<byte> payload, IBufferWriter<byte> output);

    /// <summary>解一个载荷。</summary>
    void Decompress(ReadOnlySequence<byte> payload, IBufferWriter<byte> output, int maxOutputLength);
}

/// <summary>不压缩。</summary>
public sealed class NoCompression : ISshCompressor
{
    /// <summary>一个可以共用的实例。</summary>
    public static NoCompression Instance { get; } = new();

    /// <inheritdoc />
    public bool IsActive => false;

    /// <inheritdoc />
    public void Compress(ReadOnlySpan<byte> payload, IBufferWriter<byte> output) => output.Write(payload);

    /// <inheritdoc />
    public void Decompress(ReadOnlySequence<byte> payload, IBufferWriter<byte> output, int maxOutputLength)
    {
        foreach (ReadOnlyMemory<byte> segment in payload)
        {
            output.Write(segment.Span);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}

/// <summary>zlib 压缩（RFC 4253 §6.2）。</summary>
/// <remarks>
/// <para>
/// 每个报文之后做一次 flush：把当前报文的数据全部吐出来，但<b>保留字典</b>。
/// 不 flush 的话数据会留在压缩器里出不来，对端收不到完整报文；
/// 而重置字典（<c>Z_FULL_FLUSH</c>）会把压缩率一起扔掉。
/// </para>
/// <para>
/// <b>用的是 BCL 的 <see cref="ZLibStream"/>，也就是运行时自带的原生 zlib。</b>
/// 这里有一个容易劝退的细节：BCL 不暴露 zlib 的 flush 模式，
/// 而 OpenSSH 用的是 <c>Z_PARTIAL_FLUSH</c>。看上去只能自己接一份 zlib ——
/// 但 <see cref="Stream.Flush"/> 做的 <c>Z_SYNC_FLUSH</c>
/// <b>同样不重置字典</b>，两者的差别只是每个报文多 4 个字节
/// （<c>00 00 FF FF</c> 那个空存储块），而<b>两边都能解</b>。
/// 于是可以直接用原生 zlib：不引第三方的 zlib 实现，也快得多。
/// </para>
/// <para>
/// ⚠️ <b>压缩器的生命周期绑在密钥上，中途不能 Dispose。</b>
/// Dispose 会给 zlib 流收尾（写出结束标记），而那个标记不是给对端的报文数据。
/// 重新协商密钥时要整个换掉（见 <c>velashell-docs/zh/ssh/spec/01-transport-framing.md</c> §六）。
/// </para>
/// </remarks>
public sealed class ZlibCompressor : ISshCompressor
{
    /// <summary>一次解压的中间缓冲大小。</summary>
    private const int ChunkSize = 16 * 1024;

    /// <summary>
    /// 应用有没有打开 <c>System.IO.Compression.UseStrictValidation</c>。默认是关的。
    /// </summary>
    /// <remarks>
    /// 打开之后，「读到没有更多数据」会被当成流被截断而抛 <see cref="InvalidDataException"/>。
    /// 而 SSH 的 zlib 流是<b>一直 flush、永不结束</b>的，所以每个报文解完都会撞上它。
    /// 见 <see cref="Decompress"/> 里的处理。
    /// </remarks>
    private static readonly bool StrictValidation =
        AppContext.TryGetSwitch("System.IO.Compression.UseStrictValidation", out bool strict) && strict;

    private readonly BufferWriterStream _deflateSink = new();
    private readonly SequenceStream _inflateSource = new();
    private readonly ZLibStream _deflater;
    private readonly ZLibStream _inflater;
    private bool _disposed;

    /// <summary>建一个 zlib 压缩器。</summary>
    /// <param name="level">压缩级别，1–9。OpenSSH 用 6。</param>
    public ZlibCompressor(int level = 6)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 9);

        _deflater = new ZLibStream(
            _deflateSink,
            new ZLibCompressionOptions { CompressionLevel = level },
            leaveOpen: true);

        _inflater = new ZLibStream(_inflateSource, CompressionMode.Decompress, leaveOpen: true);
    }

    /// <inheritdoc />
    public bool IsActive => true;

    /// <inheritdoc />
    public void Compress(ReadOnlySpan<byte> payload, IBufferWriter<byte> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(output);

        _deflateSink.Output = output;
        try
        {
            _deflater.Write(payload);

            // 这一句是全部的关键：sync flush —— 吐干净，但不动字典。
            _deflater.Flush();
        }
        finally
        {
            _deflateSink.Output = null;
        }
    }

    /// <inheritdoc />
    public void Decompress(ReadOnlySequence<byte> payload, IBufferWriter<byte> output, int maxOutputLength)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(output);

        _inflateSource.Data = payload;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        long total = 0;

        try
        {
            while (true)
            {
                int read;
                try
                {
                    read = _inflater.Read(buffer, 0, buffer.Length);
                }
                catch (InvalidDataException) when (StrictValidation && total > 0)
                {
                    // 对端是 flush 了流，不是结束了流 —— 所以「读到没有更多数据」
                    // 在严格校验下被当成截断。这一次读本身没有产出任何数据
                    // （载荷已经由前面几次读取回来了），所以这就是载荷的结尾。
                    //
                    // total == 0 时**不豁免**：解出来什么都没有的载荷不是我们 flush 过的东西，
                    // 那仍然是错误 —— 否则非法的 zlib 数据会被静静放过去。
                    break;
                }
                catch (InvalidDataException ex)
                {
                    throw new SshProtocolException(SshPhase.Open, $"解压失败：{ex.Message}", ex);
                }

                if (read == 0)
                {
                    break;
                }

                total += read;

                // ⚠️ **压缩炸弹**：几百字节能解出几百 MiB。
                //    上限必须在**写出之前**检查，不能等写完再看。
                if (total > maxOutputLength)
                {
                    throw new SshProtocolException(
                        SshPhase.Open,
                        $"解压后的载荷超过上限 {maxOutputLength} 字节 —— " +
                        "对端可能在用压缩炸弹撑爆我们的内存。");
                }

                output.Write(buffer.AsSpan(0, read));
            }
        }
        finally
        {
            _inflateSource.Data = default;
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _deflater.Dispose();
        _inflater.Dispose();
        _deflateSink.Dispose();
        _inflateSource.Dispose();
    }

    /// <summary>
    /// 两个把 <see cref="Stream"/> 接到我们自己缓冲类型上的适配器共用的拒绝理由。
    /// </summary>
    /// <remarks>
    /// 这些成员是 <see cref="Stream"/> 的形状逼出来的，对一个「只往一个方向搬字节」
    /// 的适配器全都没有意义。<b>带上一句话比裸抛有用得多</b> ——
    /// 真有人踩到时，异常里直接写着为什么这里不该被调用（架构原则 3）。
    /// </remarks>
    private static NotSupportedException NotAStream(string member) =>
        new($"{member} 对压缩适配器没有意义 —— 它只是把字节从 zlib 搬进/搬出我们自己的缓冲。");

    /// <summary>把写进来的字节交给一个 <see cref="IBufferWriter{T}"/>。</summary>
    /// <remarks>
    /// <see cref="ZLibStream"/> 只认 <see cref="Stream"/>，而我们整条链路用的是
    /// <see cref="IBufferWriter{T}"/>。这个适配器是两者之间唯一的胶水 ——
    /// <b>没有中间缓冲，压缩器吐多少就直接写进调用方的 writer</b>。
    /// </remarks>
    private sealed class BufferWriterStream : Stream
    {
        /// <summary>这一轮写到哪里去。<see langword="null"/> 表示丢弃。</summary>
        public IBufferWriter<byte>? Output { get; set; }

        /// <summary>唯一真正做事的成员。</summary>
        public override void Write(ReadOnlySpan<byte> buffer) => Output?.Write(buffer);

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        /// <summary>zlib 的 flush 由 <see cref="ZlibCompressor.Compress"/> 驱动，这里没事可做。</summary>
        public override void Flush()
        {
        }

        /// <inheritdoc />
        public override bool CanWrite => true;

        /// <inheritdoc />
        public override bool CanRead => false;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => throw NotAStream("读");

        /// <inheritdoc />
        public override long Length => throw NotAStream("长度");

        /// <inheritdoc />
        public override long Position
        {
            get => throw NotAStream("位置");
            set => throw NotAStream("位置");
        }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw NotAStream("定位");

        /// <inheritdoc />
        public override void SetLength(long value) => throw NotAStream("设长度");
    }

    /// <summary>把一段 <see cref="ReadOnlySequence{T}"/> 喂给解压器。</summary>
    /// <remarks>
    /// 直接按段读，<b>不把载荷先拷成一个数组</b> —— 载荷本来就是分段到达的。
    /// </remarks>
    private sealed class SequenceStream : Stream
    {
        /// <summary>这一轮要解的数据。读完之后 <see cref="Read(Span{byte})"/> 返回 0。</summary>
        public ReadOnlySequence<byte> Data { get; set; }

        /// <summary>唯一真正做事的成员：切一段出去，把游标往前挪。</summary>
        public override int Read(Span<byte> buffer)
        {
            int take = (int)Math.Min(buffer.Length, Data.Length);
            if (take == 0)
            {
                return 0;
            }

            Data.Slice(0, take).CopyTo(buffer);
            Data = Data.Slice(take);
            return take;
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        /// <summary>读这一侧没有缓冲，无事可做。</summary>
        public override void Flush()
        {
        }

        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw NotAStream("写");

        /// <inheritdoc />
        public override long Length => throw NotAStream("长度");

        /// <inheritdoc />
        public override long Position
        {
            get => throw NotAStream("位置");
            set => throw NotAStream("位置");
        }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw NotAStream("定位");

        /// <inheritdoc />
        public override void SetLength(long value) => throw NotAStream("设长度");
    }
}

/// <summary>按协商出的算法名造压缩器。</summary>
public static class SshCompressorFactory
{
    /// <summary>这个算法名是不是「认证之后才开始压缩」的那一种。</summary>
    /// <remarks>
    /// <para>
    /// <c>zlib@openssh.com</c> 把压缩推迟到认证成功之后。
    /// </para>
    /// <para>
    /// 推迟是有理由的：认证之前的报文里有密码与公钥，而压缩会让
    /// <b>密文长度泄漏明文的可压缩性</b> —— 对着一个长度可观测的口令做
    /// 压缩旁路攻击（CRIME 那一类）是现实的。推迟之后，攻击者要先过认证
    /// 才能让我们压缩他挑的内容，而那时他已经在里面了。
    /// </para>
    /// </remarks>
    public static bool IsDelayed(string algorithm) =>
        string.Equals(algorithm, SshAlgorithmNames.ZlibOpenSsh, StringComparison.Ordinal);

    /// <summary>协商出的压缩算法必须是本库实现的；否则抛出。</summary>
    /// <remarks>
    /// 只认 <c>none</c> 与 <c>zlib@openssh.com</c>。别的名字（包括裸 <c>zlib</c>）只可能是
    /// 使用者往 <see cref="SshAlgorithmSet"/> 里塞了本库不实现的算法 —— 这时要在协商当场响亮地失败，
    /// 不能悄悄按不压缩处理：对端会按谈成的算法去压，两端在下一个报文上就错位，
    /// 而那时报出来的只是一个看不出缘由的解密或解压错误。
    /// </remarks>
    /// <exception cref="SshKeyExchangeException">算法名不是本库实现的压缩算法。</exception>
    public static void EnsureSupported(string algorithm)
    {
        if (algorithm is not (SshAlgorithmNames.None or SshAlgorithmNames.ZlibOpenSsh))
        {
            throw new SshKeyExchangeException($"尚未实现的压缩算法：{algorithm}。");
        }
    }

    /// <summary>造一个压缩器；<c>none</c> 返回直通的那个。</summary>
    /// <exception cref="SshKeyExchangeException">算法名不是本库实现的压缩算法。</exception>
    public static ISshCompressor Create(string algorithm, int level = 6)
    {
        EnsureSupported(algorithm);
        return IsDelayed(algorithm) ? new ZlibCompressor(level) : NoCompression.Instance;
    }
}
