// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-ietf-secsh-filexfer-02 §6、§7
//   OpenSSH PROTOCOL              SFTP 扩展章节
//   行为规格:                     velashell-docs/zh/ssh/spec/06-sftp.md 全部

using System.Buffers;

namespace VelaShell.Ssh.Sftp;

public sealed partial class SftpFileSystem
{
    // ------------------------------------------------------------ 文件

    /// <summary>打开一个文件用于读。</summary>
    public ValueTask<SftpFileStream> OpenReadAsync(string path, CancellationToken cancellationToken = default) =>
        OpenAsync(path, SftpOpenModes.Read, cancellationToken: cancellationToken);

    /// <summary>打开一个文件用于写（不存在则创建，存在则截断）。</summary>
    public ValueTask<SftpFileStream> OpenWriteAsync(
        string path,
        uint permissions = SftpProtocol.DefaultFilePermissions,
        SftpWriteMode writeMode = SftpWriteMode.Pipelined,
        CancellationToken cancellationToken = default) =>
        OpenAsync(path,
            SftpOpenModes.Write | SftpOpenModes.Create | SftpOpenModes.Truncate,
            SftpFileAttributes.WithPermissions(permissions),
            writeMode, cancellationToken);

    /// <summary>打开一个文件用于续写（从给定偏移继续）。</summary>
    /// <remarks>
    /// <para>
    /// 配合 <see cref="SftpFileStream.DurableLength"/> 或
    /// <see cref="SftpTransferInterruptedException.DurableLength"/> 用，
    /// 就是精确的断点续传。
    /// </para>
    /// <para>
    /// 返回的流把 <c>[0, offset)</c> 算作已确认（服务端的文件比 <paramref name="offset"/> 短时只算到文件末尾）——
    /// 那一段是上一次传输确认过的。不这样算的话，续传途中再断一次，
    /// <see cref="SftpFileStream.DurableLength"/> 报的是 0，下一次续传就从头来过。
    /// </para>
    /// </remarks>
    public async ValueTask<SftpFileStream> OpenAppendAsync(
        string path,
        long offset,
        uint permissions = SftpProtocol.DefaultFilePermissions,
        CancellationToken cancellationToken = default)
    {
        // 先查参数再开：开了之后才发现偏移不对，那个句柄就没人关了。
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        SftpFileStream stream = await OpenAsync(
            path,
            SftpOpenModes.Write | SftpOpenModes.Create,
            SftpFileAttributes.WithPermissions(permissions),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        stream.Position = offset;
        stream.AssumeDurablePrefix(stream.LengthKnown ? Math.Min(offset, stream.Length) : offset);
        return stream;
    }

    /// <summary>用任意方式打开文件。</summary>
    /// <param name="path">路径。</param>
    /// <param name="flags">打开方式。流能不能读、能不能写就由它决定（<see cref="SftpOpenModes.Read"/> / <see cref="SftpOpenModes.Write"/>）。</param>
    /// <param name="attributes">
    /// 创建文件时的属性；带 <see cref="SftpOpenModes.Create"/> 而没给权限时补上 <c>0644</c>
    /// （<see cref="SftpProtocol.DefaultFilePermissions"/>）。
    /// </param>
    /// <param name="writeMode">写入方式。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="ArgumentException">
    /// 打开方式自相矛盾：既不读也不写；<see cref="SftpOpenModes.Truncate"/> / <see cref="SftpOpenModes.Exclusive"/>
    /// 没有配 <see cref="SftpOpenModes.Create"/>（draft-02 §6.3 要求两者一起用）。
    /// </exception>
    public async ValueTask<SftpFileStream> OpenAsync(
        string path,
        SftpOpenModes flags,
        SftpFileAttributes attributes = default,
        SftpWriteMode writeMode = SftpWriteMode.Pipelined,
        CancellationToken cancellationToken = default)
    {
        ValidatePath(path);

        bool canRead = (flags & SftpOpenModes.Read) != 0;
        bool canWrite = (flags & (SftpOpenModes.Write | SftpOpenModes.Append)) != 0;

        // 〔velashell-docs/zh/ssh/spec/06 §4.1〕不需要服务端就能查的，在发 OPEN 之前查。
        if (!canRead && !canWrite)
        {
            throw new ArgumentException("打开方式里既没有读也没有写。", nameof(flags));
        }
        if ((flags & (SftpOpenModes.Truncate | SftpOpenModes.Exclusive)) != 0 && (flags & SftpOpenModes.Create) == 0)
        {
            throw new ArgumentException(
                "TRUNC / EXCL 必须与 CREAT 一起用（draft-02 §6.3）。只想截短一个已有的文件：以写方式打开，再 SetLengthAsync(0)。",
                nameof(flags));
        }

        // 创建时传明确的权限：不传的话服务端用它自己的默认值（受 umask 影响），结果不可预测。
        // 曾经只有 OpenWriteAsync / OpenAppendAsync 传，宿主的 Create / CreateNew / OpenOrCreate 走的这条都没传。
        if ((flags & SftpOpenModes.Create) != 0 && !attributes.HasPermissions)
        {
            attributes = attributes with
            {
                Flags = attributes.Flags | SftpAttributeFields.Permissions,
                Permissions = SftpProtocol.DefaultFilePermissions,
            };
        }

        await AcquireHandleSlotAsync(cancellationToken).ConfigureAwait(false);

        byte[] handle;
        try
        {
            using SftpResponse response = await _pipeline.SendAsync(
                (output, id) => SftpWire.WriteOpen(output, id, path, flags, attributes, _names),
                onLateResponse: CloseLateHandle,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            response.ThrowIfError(path, SftpOperation.Open, SftpMessageType.Handle);
            handle = SftpWire.ReadHandle(response.Payload);
        }
        catch (Exception)
        {
            // 没开成（或取消之后迟到的句柄由 CloseLateHandle 关）：额度现在就还。
            ReleaseHandleSlot();
            throw;
        }

        // 截断打开的，长度就是 0；别的（读、续写、不截断的写）要问一次 ——
        // 曾经只有读才问，续写打开的流 Length 一直报 0，Seek(0, End) 回到了文件开头。
        long length = 0;
        bool lengthKnown = (flags & SftpOpenModes.Truncate) != 0;
        bool isRegularFile = false;
        if (!lengthKnown)
        {
            try
            {
                using SftpResponse stat = await _pipeline.SendAsync(
                    (output, id) => SftpWire.WriteHandleRequest(output, SftpMessageType.FStat, id, handle),
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                stat.ThrowIfError(path, SftpOperation.GetAttributes, SftpMessageType.Attrs);
                SftpFileAttributes current = SftpWire.ReadAttrs(stat.Payload);
                isRegularFile = current.IsRegularFile;
                if (current.HasSize)
                {
                    length = (long)current.Size;
                    lengthKnown = true;
                }
            }
            catch (SftpException)
            {
                // 拿不到长度不影响读写 —— 只是 Length 与 Seek(SeekOrigin.End) 会不准。
            }
            catch (Exception)
            {
                // ⚠️ 取消、流水线断了、应答不合格式：句柄已经开在服务端了，
                //    还没交给流，这里不关就没人关 —— 每失败一次漏一个，直到 max-open-handles 用光。
                await CloseHandleQuietlyAsync(handle).ConfigureAwait(false);
                ReleaseHandleSlot();
                throw;
            }
        }

        return new SftpFileStream(
            _pipeline, handle, path, canRead, canWrite, length, BlockSize, writeMode,
            maxInFlightWrites: StreamWindow, maxReadAhead: StreamWindow, lengthKnown, isRegularFile)
        {
            OnHandleClosed = ReleaseHandleSlot,
            CloseTimeout = _options.CloseTimeout,
        };
    }

    /// <summary>单个流自己的在途上限。</summary>
    /// <remarks>
    /// <para>
    /// 开着自适应时取深度的<b>上限</b>，不取起始值：让管线的在途额度成为唯一的限流点。
    /// 「深度是不是瓶颈」的信号只在管线额度被用光时才记得到 ——
    /// 流自带一个与管线起始深度一样大的上限的话，它总是先卡住，管线额度永远空着，深度永远不长。
    /// </para>
    /// <para>
    /// 曾经就是这样：单文件上传、下载（最常见的用法）的窗口钉死在 64 × 块大小，
    /// 服务端没宣告 limits 时是 2 MiB，200 ms RTT 下约 10 MB/s —— 正是自适应深度想消灭的那个上限。
    /// 只有多个流并发时深度才会长。
    /// </para>
    /// </remarks>
    private int StreamWindow => _options.IsPipelineDepthAdaptive ? _options.MaxPipelineDepth : _options.MaxInFlight;

    /// <summary>流水线的深度被调大过几次（诊断与测试用）。</summary>
    internal int PipelineDepthIncreases => _pipeline.DepthIncreases;

    /// <summary>列目录时丢掉了几个名字不合法的项（空名字、含 <c>/</c> 或 NUL；诊断与测试用）。</summary>
    internal int MalformedEntriesSkipped => Volatile.Read(ref _malformedEntriesSkipped);

    /// <summary>单个写入流最多有多少字节「已经发出、还没被服务端确认」。</summary>
    /// <remarks>
    /// <para>
    /// 流水线写的完成顺序不保证与偏移顺序一致：断线时远端文件的长度只是「已确认的最高偏移」，
    /// 它之前最多这么多字节可能还是空洞。只凭远端长度续传的话，要从长度往回退这么多再比对。
    /// </para>
    /// <para>
    /// 开着自适应深度时它按深度的<b>上限</b>算 —— 断线那一刻深度长到了多少，事后无从得知。
    /// 能拿到断线那条流的 <see cref="SftpFileStream.DurableLength"/>（已连续确认的偏移）时用它，不必回退。
    /// </para>
    /// </remarks>
    public long MaxUnconfirmedWriteBytes => (long)StreamWindow * BlockSize;

    /// <summary>把整个文件读成字节。</summary>
    public async ValueTask<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        await using SftpFileStream stream = await OpenReadAsync(path, cancellationToken).ConfigureAwait(false);

        // 初始容量按服务端报的长度估，但**封顶** —— 那是对端给的数：一个谎报 2 GiB 的小文件
        // 不该让我们先分配 2 GiB。真实数据多了，缓冲自己会长。
        const int maxInitialCapacity = 1024 * 1024;
        ArrayBufferWriter<byte> output = new((int)Math.Clamp(stream.Length, 1, maxInitialCapacity));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BlockSize);

        try
        {
            // 走顺序读：它带预读（spec/06 §5.5）。
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, BlockSize), cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                output.Write(buffer.AsSpan(0, read));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return output.WrittenSpan.ToArray();
    }

    /// <summary>把字节写成一个文件（覆盖）。</summary>
    public async ValueTask WriteAllBytesAsync(
        string path,
        ReadOnlyMemory<byte> content,
        uint permissions = SftpProtocol.DefaultFilePermissions,
        CancellationToken cancellationToken = default)
    {
        await using SftpFileStream stream =
            await OpenWriteAsync(path, permissions, SftpWriteMode.Pipelined, cancellationToken).ConfigureAwait(false);

        await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
