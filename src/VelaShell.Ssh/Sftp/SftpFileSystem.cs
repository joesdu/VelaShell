// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-ietf-secsh-filexfer-02 §6、§7
//   OpenSSH PROTOCOL              SFTP 扩展章节
//   行为规格:                     velashell-docs/zh/ssh/spec/06-sftp.md 全部

using System.Buffers;
using System.Runtime.CompilerServices;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Session;

namespace VelaShell.Ssh.Sftp;

/// <summary>建立 SFTP 时的参数。</summary>
public sealed record SftpOptions
{
    /// <summary>底层通道的参数。</summary>
    public SshChannelOptions Channel { get; init; } = SshChannelOptions.Default with
    {
        // SFTP 是吞吐型负载，窗口给大一些；stderr 上只会来服务端的诊断噪音。
        WindowPolicy = SshWindowPolicy.Adaptive(2 * 1024 * 1024, 64 * 1024 * 1024),
        StderrMode = SshStderrMode.Discard,
    };

    /// <summary>在途请求数上限。</summary>
    /// <remarks>
    /// 在途请求数 × 块大小就是 SFTP 层的「窗口」。
    /// 和通道窗口一样，太小会在高 RTT 链路上直接封死吞吐。
    /// </remarks>
    public int MaxInFlight { get; init; } = 64;

    /// <summary>
    /// 块大小；<c>0</c> 表示按服务端宣告的 <c>limits@openssh.com</c> 定。
    /// </summary>
    public int BlockSize { get; init; }

    /// <summary>在途请求数是否按「深度有没有成为瓶颈」自动伸缩。</summary>
    /// <remarks>
    /// 关掉它可以得到确定性的内存占用（在途数 × 块大小），
    /// 代价是高 RTT 链路上吞吐被 <c>深度 × 块大小 / RTT</c> 封死。
    /// </remarks>
    public bool AdaptivePipelineDepth { get; init; } = true;

    /// <summary>自适应时在途请求数的上限。</summary>
    public int MaxPipelineDepth { get; init; } = 256;

    /// <summary>列目录时过滤掉 <c>.</c> 与 <c>..</c>。</summary>
    /// <remarks>它们**会**出现在服务端返回的结果里。</remarks>
    public bool FilterDotEntries { get; init; } = true;

    /// <summary>默认参数。</summary>
    public static SftpOptions Default { get; } = new();
}

/// <summary>面向使用者的 SFTP 客户端。</summary>
public sealed class SftpFileSystem : IAsyncDisposable
{
    private readonly SshChannel _channel;
    private readonly SftpRequestPipeline _pipeline;
    private readonly SftpOptions _options;
    private bool _disposed;

    private SftpFileSystem(
        SshChannel channel, SftpRequestPipeline pipeline, SftpOptions options, SftpCapabilities capabilities)
    {
        _channel = channel;
        _pipeline = pipeline;
        _options = options;
        Capabilities = capabilities;
        WorkingDirectory = ".";
    }

    /// <summary>这台服务端支持什么。</summary>
    public SftpCapabilities Capabilities { get; }

    /// <summary>连上时的工作目录（通常就是家目录）。</summary>
    public string WorkingDirectory { get; private set; }

    /// <summary>实际使用的块大小。</summary>
    public int BlockSize { get; private set; }

    /// <summary>在一条会话上起 SFTP。</summary>
    /// <exception cref="SftpUnavailableException">服务端没有 sftp 子系统，或版本太低。</exception>
    public static async ValueTask<SftpFileSystem> ConnectAsync(
        SshConnection connection,
        SftpOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        SftpOptions effective = options ?? SftpOptions.Default;

        SshChannel channel;
        try
        {
            channel = await connection
                .OpenSubsystemAsync(SshProtocolNames.SubsystemSftp, effective.Channel, cancellationToken).ConfigureAwait(false);
        }
        catch (SshChannelException ex)
        {
            // 〔决策 velashell-docs/zh/ssh/spec/06 §一〕**不自动回退到 `exec sftp-server`。**
            // 回退等于在管理员明确禁用 subsystem 的情况下绕过他的配置。
            throw new SftpUnavailableException(
                "服务端没有提供 sftp 子系统。常见原因是 sshd_config 里缺少或注释掉了 " +
                "`Subsystem sftp ...` 那一行。（本库不会自动改用 `exec sftp-server` 绕开它 —— " +
                "那等于绕过管理员的配置。）", ex);
        }

        SftpRequestPipeline pipeline = new(
            channel, effective.MaxInFlight, effective.AdaptivePipelineDepth, effective.MaxPipelineDepth);
        pipeline.Start();

        try
        {
            SftpCapabilities capabilities = await HandshakeAsync(pipeline, cancellationToken).ConfigureAwait(false);
            SftpFileSystem fileSystem = new(channel, pipeline, effective, capabilities);

            await fileSystem.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return fileSystem;
        }
        catch (Exception)
        {
            await pipeline.DisposeAsync().ConfigureAwait(false);
            await channel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask<SftpCapabilities> HandshakeAsync(
        SftpRequestPipeline pipeline, CancellationToken cancellationToken)
    {
        Task<SftpResponse> versionTask = pipeline.WaitForVersionAsync();

        ArrayBufferWriter<byte> init = new();
        SftpWire.WriteInit(init);
        await pipeline.SendRawAsync(init.WrittenMemory, cancellationToken).ConfigureAwait(false);

        using SftpResponse response = await versionTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        (uint version, IReadOnlyDictionary<string, byte[]> extensions) = SftpWire.ReadVersion(response.Payload);

        if (version < SftpProtocol.Version)
        {
            // v0–v2 与 v3 的差异过大（没有 ATTRS 的部分字段、没有 handle 语义保证）。
            throw new SftpUnavailableException(
                $"服务端只支持 SFTP v{version}，本库要求至少 v{SftpProtocol.Version}。");
        }

        // 版本更高就降到 3 —— 我们按 v3 工作，这是 OpenSSH 的实际口径。
        return new SftpCapabilities(version, extensions);
    }

    private async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        // 服务端宣告了上限就按它来，而不是写死。
        if (Capabilities.HasLimits)
        {
            try
            {
                Capabilities.Limits = await QueryLimitsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SshException)
            {
                // 宣告了却查不出来 —— 用保守默认继续，不因此连不上。
            }
        }

        BlockSize = ChooseBlockSize(_options.BlockSize, Capabilities.Limits);

        // 〔决策 velashell-docs/zh/ssh/spec/06 §4.6〕连上就对 "." 做一次 REALPATH。
        // 这是唯一可靠的「用户家目录在哪」的答案 —— 比拼 /home/{user} 靠谱得多。
        try
        {
            WorkingDirectory = await GetRealPathAsync(".", cancellationToken).ConfigureAwait(false);
        }
        catch (SftpException)
        {
            // 少数服务端会拒绝对 "." 做 REALPATH。那不该让整个连接失败。
            WorkingDirectory = ".";
        }
    }

    /// <summary>
    /// 块大小 = min(想要的, 服务端的读写上限, 服务端的报文上限减去请求头, 本端肯收的最大数据块)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>服务端给 0 是「没说」，不是「一个字节」。</b>曾经按字面取，块大小就成了 1 ——
    /// 一个 1 MB 的文件要一百万个请求。
    /// </para>
    /// <para>
    /// 读上限也要算进来：比它长的 <c>READ</c> 会被截短，而顺序读把短读当成「中间有洞」，
    /// 每一块都从头再来，预读永远建不起来。使用者指定的块大小同样要服从这些上限 ——
    /// 超长的 <c>WRITE</c> 会被拒，OpenSSH 甚至直接断开 SFTP 会话。
    /// </para>
    /// </remarks>
    internal static int ChooseBlockSize(int requested, SftpLimits limits)
    {
        long serverCap = long.MaxValue;
        if (limits.MaxWriteLength > 0)
        {
            serverCap = Math.Min(serverCap, (long)Math.Min(limits.MaxWriteLength, int.MaxValue));
        }
        if (limits.MaxReadLength > 0)
        {
            serverCap = Math.Min(serverCap, (long)Math.Min(limits.MaxReadLength, int.MaxValue));
        }
        if (limits.MaxPacketLength > SftpProtocol.RequestHeaderAllowance)
        {
            serverCap = Math.Min(
                serverCap, (long)Math.Min(limits.MaxPacketLength, int.MaxValue) - SftpProtocol.RequestHeaderAllowance);
        }

        long size = requested > 0
            ? requested
            : serverCap == long.MaxValue ? SftpProtocol.DefaultBlockSize : serverCap;

        return (int)Math.Clamp(Math.Min(size, serverCap), 1, SftpProtocol.MaxBlockSize);
    }

    private async ValueTask<SftpLimits> QueryLimitsAsync(CancellationToken cancellationToken)
    {
        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WriteExtended(output, id, SftpExtensionNames.Limits, []),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(path: null, SftpOperation.QueryLimits);
        return SftpWire.ReadLimits(response.Payload);
    }

    // ------------------------------------------------------------ 路径

    /// <summary>把路径规范化成绝对路径。</summary>
    public async ValueTask<string> GetRealPathAsync(string path, CancellationToken cancellationToken = default)
    {
        ValidatePath(path);

        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WritePathRequest(output, SftpMessageType.RealPath, id, path),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(path, SftpOperation.RealPath);

        IReadOnlyList<SftpNameEntry> entries = SftpWire.ReadName(response.Payload);
        if (entries.Count != 1)
        {
            throw new SshProtocolException(
                SshPhase.Open, $"REALPATH 应当返回恰好 1 项，实际返回了 {entries.Count} 项。");
        }

        return entries[0].Name;
    }

    // ------------------------------------------------------------ 属性

    /// <summary>取属性，<b>跟随</b>符号链接。</summary>
    public ValueTask<SftpFileAttributes> GetAttributesAsync(
        string path, CancellationToken cancellationToken = default) =>
        StatAsync(path, SftpMessageType.Stat, cancellationToken);

    /// <summary>取属性，<b>不跟随</b>符号链接 —— 描述链接本身。</summary>
    public ValueTask<SftpFileAttributes> GetLinkAttributesAsync(
        string path, CancellationToken cancellationToken = default) =>
        StatAsync(path, SftpMessageType.LStat, cancellationToken);

    private async ValueTask<SftpFileAttributes> StatAsync(
        string path, SftpMessageType type, CancellationToken cancellationToken)
    {
        ValidatePath(path);

        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WritePathRequest(output, type, id, path),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(path, SftpOperation.GetAttributes);
        return SftpWire.ReadAttrs(response.Payload);
    }

    /// <summary>文件或目录存在吗。</summary>
    public async ValueTask<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await GetLinkAttributesAsync(path, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SftpException ex) when (ex.IsNotFound)
        {
            return false;
        }
    }

    /// <summary>设属性。</summary>
    public async ValueTask SetAttributesAsync(
        string path, SftpFileAttributes attributes, CancellationToken cancellationToken = default)
    {
        ValidatePath(path);

        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WriteSetStat(output, id, path, attributes),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(path, SftpOperation.SetAttributes);
    }

    /// <summary>改权限。</summary>
    public ValueTask SetPermissionsAsync(
        string path, uint permissions, CancellationToken cancellationToken = default) =>
        SetAttributesAsync(path, SftpFileAttributes.WithPermissions(permissions), cancellationToken);

    /// <summary>改最后修改时间。</summary>
    /// <remarks>
    /// <c>atime</c> 与 <c>mtime</c> <b>共用一个标志位</b>，只给一个会把另一个抹成 1970 年。
    /// 所以这里<b>先把当前的 atime 取回来</b>再一并写回。
    /// </remarks>
    public async ValueTask SetLastWriteTimeAsync(
        string path, DateTimeOffset modifyTime, CancellationToken cancellationToken = default)
    {
        SftpFileAttributes current = await GetAttributesAsync(path, cancellationToken).ConfigureAwait(false);

        DateTimeOffset accessTime = current.HasTimes
            ? current.LastAccessTime
            : modifyTime;

        await SetAttributesAsync(
            path, SftpFileAttributes.WithTimes(accessTime, modifyTime), cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 目录

    /// <summary>建目录。</summary>
    public async ValueTask CreateDirectoryAsync(
        string path,
        uint permissions = SftpProtocol.DefaultDirectoryPermissions,
        CancellationToken cancellationToken = default)
    {
        ValidatePath(path);

        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WriteMkDir(output, id, path, SftpFileAttributes.WithPermissions(permissions)),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(path, SftpOperation.CreateDirectory);
    }

    /// <summary>删空目录。</summary>
    public async ValueTask DeleteDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        ValidatePath(path);

        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WritePathRequest(output, SftpMessageType.RmDir, id, path),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(path, SftpOperation.RemoveDirectory);
    }

    /// <summary>删文件。</summary>
    public async ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ValidatePath(path);

        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WritePathRequest(output, SftpMessageType.Remove, id, path),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(path, SftpOperation.Remove);
    }

    /// <summary>列目录。</summary>
    /// <remarks>
    /// <para>
    /// 〔决策 velashell-docs/zh/ssh/spec/06 §八〕<b>用 <c>LSTAT</c> 的口径列，链接项再补一次跟随的
    /// <c>STAT</c> 与 <c>READLINK</c>。</b>
    /// </para>
    /// <para>
    /// 直接用跟随的 <c>STAT</c> 会让「这是个链接」这个事实彻底消失 ——
    /// 于是删除一个指向目录的链接，会变成递归删除目标目录里的东西。那是数据事故。
    /// </para>
    /// <para>
    /// 链接项的补充请求是<b>并发</b>发出的：<c>/usr/lib</c> 那种几百个 <c>.so</c> 链接的目录，
    /// 串行补就是几百轮往返，并发补只是一轮。
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<SftpDirectoryEntry> EnumerateDirectoryAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidatePath(path);

        byte[] handle = await OpenDirectoryHandleAsync(path, cancellationToken).ConfigureAwait(false);

        try
        {
            while (true)
            {
                IReadOnlyList<SftpNameEntry>? batch =
                    await ReadDirectoryBatchAsync(handle, path, cancellationToken).ConfigureAwait(false);

                if (batch is null)
                {
                    yield break;   // STATUS = EOF：目录读完了
                }

                SftpDirectoryEntry[] resolved =
                    await ResolveBatchAsync(path, batch, cancellationToken).ConfigureAwait(false);

                foreach (SftpDirectoryEntry entry in resolved)
                {
                    yield return entry;
                }
            }
        }
        finally
        {
            await CloseHandleQuietlyAsync(handle).ConfigureAwait(false);
        }
    }

    private async ValueTask<byte[]> OpenDirectoryHandleAsync(string path, CancellationToken cancellationToken)
    {
        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WritePathRequest(output, SftpMessageType.OpenDir, id, path),
            onLateResponse: CloseLateHandle,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(path, SftpOperation.OpenDirectory);
        return SftpWire.ReadHandle(response.Payload);
    }

    /// <returns>这一批；<see langword="null"/> 表示目录读完了。</returns>
    private async ValueTask<IReadOnlyList<SftpNameEntry>?> ReadDirectoryBatchAsync(
        byte[] handle, string path, CancellationToken cancellationToken)
    {
        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WriteHandleRequest(output, SftpMessageType.ReadDir, id, handle),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (response.TryGetStatus(out SftpStatusCode code, out string message))
        {
            // EOF 是「读完了」，不是错误。
            if (code == SftpStatusCode.EndOfFile)
            {
                return null;
            }
            throw new SftpException(code, message, path, SftpOperation.ReadDirectory);
        }

        return SftpWire.ReadName(response.Payload);
    }

    private async ValueTask<SftpDirectoryEntry[]> ResolveBatchAsync(
        string directory, IReadOnlyList<SftpNameEntry> batch, CancellationToken cancellationToken)
    {
        List<SftpNameEntry> kept = [];
        foreach (SftpNameEntry entry in batch)
        {
            // `.` 与 `..` **会**出现在服务端的结果里。
            if (_options.FilterDotEntries && entry.Name is "." or "..")
            {
                continue;
            }
            kept.Add(entry);
        }

        // 链接项的补充请求并发发出 —— 它们在同一条通道上流水线，
        // 串行补的话几百个链接就是几百轮往返。
        Task<SftpDirectoryEntry>[] tasks =
        [
            .. kept.Select(entry => ResolveEntryAsync(directory, entry, cancellationToken).AsTask()),
        ];

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async ValueTask<SftpDirectoryEntry> ResolveEntryAsync(
        string directory, SftpNameEntry entry, CancellationToken cancellationToken)
    {
        string fullPath = CombinePath(directory, entry.Name);

        if (!entry.Attributes.IsSymbolicLink)
        {
            return new SftpDirectoryEntry(
                entry.Name, fullPath, entry.Attributes,
                IsSymbolicLink: false, LinkTarget: null, IsBrokenLink: false, entry.LongName);
        }

        // 链接：目标与跟随后的属性一起要。
        Task<string?> targetTask = ReadLinkQuietlyAsync(fullPath, cancellationToken);
        Task<SftpFileAttributes?> followedTask = StatQuietlyAsync(fullPath, cancellationToken);

        await Task.WhenAll(targetTask, followedTask).ConfigureAwait(false);

        string? target = await targetTask.ConfigureAwait(false);
        SftpFileAttributes? followed = await followedTask.ConfigureAwait(false);

        // 断链：**保留链接自身的属性**，IsDirectory 为 false。
        // 返回 null 是不对的 —— 链接本身是存在的，删除它不能先报「找不到」。
        return new SftpDirectoryEntry(
            entry.Name,
            fullPath,
            followed ?? entry.Attributes,
            IsSymbolicLink: true,
            LinkTarget: target,
            IsBrokenLink: followed is null,
            entry.LongName);
    }

    // 下面两个「悄悄」版本吞的是<b>这一条应答</b>的问题：服务端拒了（SftpException），
    // 或者这一条应答长得不对（SshProtocolException，比如 READLINK 回了两项）——
    // 一个怪链接不该让整个目录列不出来。流水线本身坏了就不吞：那不是这一项的事。

    private async Task<string?> ReadLinkQuietlyAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadSymbolicLinkAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (SftpException)
        {
            return null;
        }
        catch (SshProtocolException) when (!_pipeline.IsFaulted)
        {
            return null;
        }
    }

    private async Task<SftpFileAttributes?> StatQuietlyAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await GetAttributesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (SftpException)
        {
            return null;
        }
        catch (SshProtocolException) when (!_pipeline.IsFaulted)
        {
            return null;
        }
    }

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
    /// <param name="attributes">创建文件时的属性；<c>default</c> 表示不带。</param>
    /// <param name="writeMode">写入方式。</param>
    /// <param name="cancellationToken">取消令牌。</param>
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

        byte[] handle;
        using (SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WriteOpen(output, id, path, flags, attributes),
            onLateResponse: CloseLateHandle,
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            response.ThrowIfError(path, SftpOperation.Open);
            handle = SftpWire.ReadHandle(response.Payload);
        }

        // 截断打开的，长度就是 0；别的（读、续写、不截断的写）要问一次 ——
        // 曾经只有读才问，续写打开的流 Length 一直报 0，Seek(0, End) 回到了文件开头。
        long length = 0;
        bool lengthKnown = (flags & SftpOpenModes.Truncate) != 0;
        if (!lengthKnown)
        {
            try
            {
                using SftpResponse stat = await _pipeline.SendAsync(
                    (output, id) => SftpWire.WriteHandleRequest(output, SftpMessageType.FStat, id, handle),
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                stat.ThrowIfError(path, SftpOperation.GetAttributes);
                SftpFileAttributes current = SftpWire.ReadAttrs(stat.Payload);
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
                throw;
            }
        }

        return new SftpFileStream(
            _pipeline, handle, path, canRead, canWrite, length, BlockSize, writeMode,
            maxInFlightWrites: _options.MaxInFlight, maxReadAhead: _options.MaxInFlight)
        {
            LengthKnown = lengthKnown,
        };
    }

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

    // ------------------------------------------------------------ 移动与链接

    /// <summary>重命名或移动。</summary>
    /// <param name="sourcePath">源。</param>
    /// <param name="destinationPath">目标。</param>
    /// <param name="overwrite">
    /// 目标已存在时是否覆盖。<b>需要服务端支持 <c>posix-rename@openssh.com</c></b>。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="SftpException">
    /// 要求覆盖但服务端不支持原子重命名。
    /// </exception>
    /// <remarks>
    /// 〔决策 velashell-docs/zh/ssh/spec/06 §7.2〕<b>不静默降级。</b>
    /// <c>posix-rename</c> 原子覆盖，普通 <c>RENAME</c> 在目标存在时失败 ——
    /// 语义不同。悄悄换一个，上层就无从知道自己拿到的是哪一种。
    /// 用 <see cref="Capabilities"/> 事先问清楚。
    /// </remarks>
    public async ValueTask RenameAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        ValidatePath(sourcePath);
        ValidatePath(destinationPath);

        if (overwrite && !Capabilities.HasPosixRename)
        {
            // 服务端没说话 —— 原话留空，本库的说明放进消息（ServerMessage 只装服务端的原话）。
            throw new SftpException(
                SftpStatusCode.OperationUnsupported,
                serverMessage: "",
                sourcePath,
                SftpOperation.PosixRename,
                detail: "这台服务端没有 posix-rename@openssh.com，做不到原子覆盖式重命名。" +
                        "可以先删除目标再重命名，但那不是原子的 —— 中途失败会两个都没有");
        }

        if (overwrite)
        {
            using SftpResponse response = await _pipeline.SendAsync(
                (output, id) =>
                {
                    ArrayBufferWriter<byte> inner = new();
                    SshDataWriter writer = new(inner);
                    writer.WriteUtf8String(sourcePath);
                    writer.WriteUtf8String(destinationPath);
                    SftpWire.WriteExtended(output, id, SftpExtensionNames.PosixRename, inner.WrittenSpan);
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            response.ThrowIfError(sourcePath, SftpOperation.PosixRename);
            return;
        }

        using SftpResponse plain = await _pipeline.SendAsync(
            (output, id) => SftpWire.WriteRename(output, id, sourcePath, destinationPath),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        plain.ThrowIfError(sourcePath, SftpOperation.Rename);
    }

    /// <summary>读符号链接指向哪里。</summary>
    public async ValueTask<string> ReadSymbolicLinkAsync(
        string path, CancellationToken cancellationToken = default)
    {
        ValidatePath(path);

        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WritePathRequest(output, SftpMessageType.ReadLink, id, path),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(path, SftpOperation.ReadLink);

        IReadOnlyList<SftpNameEntry> entries = SftpWire.ReadName(response.Payload);
        if (entries.Count != 1)
        {
            throw new SshProtocolException(
                SshPhase.Open, $"READLINK 应当返回恰好 1 项，实际返回了 {entries.Count} 项。");
        }

        return entries[0].Name;
    }

    /// <summary>建符号链接。</summary>
    /// <param name="linkPath">在哪里<b>创建</b>链接。</param>
    /// <param name="targetPath">链接<b>指向</b>哪里。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 参数顺序上的坑在 <see cref="SftpWire.WriteSymLink"/> 里说明了 ——
    /// 这里按人话的顺序（先在哪建、再指向哪），发出去时按 OpenSSH 的顺序。
    /// </remarks>
    public async ValueTask CreateSymbolicLinkAsync(
        string linkPath, string targetPath, CancellationToken cancellationToken = default)
    {
        ValidatePath(linkPath);
        ValidatePath(targetPath);

        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WriteSymLink(output, id, targetPath, linkPath),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(linkPath, SftpOperation.CreateSymbolicLink);
    }

    /// <summary>建硬链接（需要 <c>hardlink@openssh.com</c>）。</summary>
    public async ValueTask CreateHardLinkAsync(
        string linkPath, string targetPath, CancellationToken cancellationToken = default)
    {
        ValidatePath(linkPath);
        ValidatePath(targetPath);

        if (!Capabilities.HasHardLink)
        {
            throw new SftpException(
                SftpStatusCode.OperationUnsupported,
                serverMessage: "",
                linkPath,
                SftpOperation.CreateHardLink,
                detail: "这台服务端没有 hardlink@openssh.com");
        }

        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) =>
            {
                ArrayBufferWriter<byte> inner = new();
                SshDataWriter writer = new(inner);
                writer.WriteUtf8String(targetPath);
                writer.WriteUtf8String(linkPath);
                SftpWire.WriteExtended(output, id, SftpExtensionNames.HardLink, inner.WrittenSpan);
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(linkPath, SftpOperation.CreateHardLink);
    }

    // ------------------------------------------------------------ 内部

    /// <summary>取消之后迟到的 <c>HANDLE</c> 应答 —— 句柄不关就泄漏在服务端。</summary>
    private void CloseLateHandle(SftpResponse response)
    {
        if (response.Type != SftpMessageType.Handle)
        {
            return;
        }

        byte[] handle;
        try
        {
            handle = SftpWire.ReadHandle(response.Payload);
        }
        catch (Exception)
        {
            return;
        }

        // 不等应答：这是善后，不是业务路径。
        _ = CloseHandleQuietlyAsync(handle).AsTask();
    }

    private async ValueTask CloseHandleQuietlyAsync(byte[] handle)
    {
        try
        {
            using SftpResponse response = await _pipeline.SendAsync(
                (output, id) => SftpWire.WriteHandleRequest(output, SftpMessageType.Close, id, handle))
                .ConfigureAwait(false);
            _ = response;
        }
        catch (Exception)
        {
            // 通道已经没了的话服务端会自己回收句柄。
        }
    }

    /// <summary>拼路径。<b>SFTP 的路径分隔符永远是 <c>/</c></b>，与本机平台无关。</summary>
    internal static string CombinePath(string directory, string name)
    {
        if (string.IsNullOrEmpty(directory) || directory == ".")
        {
            return name;
        }
        return directory.EndsWith('/') ? directory + name : $"{directory}/{name}";
    }

    private static void ValidatePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        // 含 NUL 的路径**在本地就拒掉**，不发给服务端：
        // 它在不同服务端上的行为从「截断」到「拒绝」都有，全是意外。
        if (path.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("路径里不能含有 NUL 字符。", nameof(path));
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

        await _pipeline.DisposeAsync().ConfigureAwait(false);
        await _channel.DisposeAsync().ConfigureAwait(false);
    }
}
